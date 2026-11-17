namespace Script4Reload.Source

open System
open System.IO
open System.Threading
open System.Collections.Generic
open System.Security.Cryptography

[<Sealed>]
type ReloadWatcher(rootDirectory: string, extensionsRoot: string, scriptsRoot: string) =
    let roots =
        [|
            Path.GetFullPath rootDirectory
            Path.GetFullPath extensionsRoot
            Path.GetFullPath scriptsRoot
        |]
        |> Array.distinctBy (fun value -> value.ToUpperInvariant())

    let gate = obj()

    let captureFiles() =
        let result = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        for root in roots do
            for path in Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly) do
                use stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.Read ||| FileShare.Delete)
                result[Path.GetFullPath(path)] <-
                    Convert.ToHexString(SHA256.HashData(stream))
        result

    let mutable committed: Dictionary<string, string> option = None
    let mutable observed: Dictionary<string, string> option = None
    let mutable watchers : FileSystemWatcher array = [||]
    let mutable dirty = 0
    let mutable errorMessage: string option = None

    let markDirty() =
        Interlocked.Exchange(&dirty, 1) |> ignore

    let signal (_: obj) (_: FileSystemEventArgs) =
        markDirty()

    let renamed (_: obj) (_: RenamedEventArgs) =
        markDirty()

    let disableWatchers() =
        for watcher in watchers do
            watcher.EnableRaisingEvents <- false

    let error (_: obj) (args: ErrorEventArgs) =
        lock gate (fun () ->
            errorMessage <- Some(args.GetException().Message)
            disableWatchers())
        markDirty()

    let createWatcher root =
        let value = new FileSystemWatcher(root, "*.dll")
        value.IncludeSubdirectories <- false
        value.NotifyFilter <-
            NotifyFilters.FileName |||
            NotifyFilters.LastWrite |||
            NotifyFilters.Size
        value.Changed.AddHandler(FileSystemEventHandler signal)
        value.Created.AddHandler(FileSystemEventHandler signal)
        value.Deleted.AddHandler(FileSystemEventHandler signal)
        value.Renamed.AddHandler(RenamedEventHandler renamed)
        value.Error.AddHandler(ErrorEventHandler error)
        value.EnableRaisingEvents <- true
        value

    let replaceWatchers() =
        for watcher in watchers do
            watcher.EnableRaisingEvents <- false
            watcher.Dispose()
        watchers <- roots |> Array.map createWatcher

    do
        replaceWatchers()
        try
            committed <- Some(captureFiles())
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ()

    member _.InspectChanges() =
        let current = captureFiles()
        match committed with
        | None ->
            committed <- Some current
            observed <- None
            0
        | Some previous ->
            let paths = HashSet<string>(previous.Keys, StringComparer.OrdinalIgnoreCase)
            paths.UnionWith(current.Keys)
            let changed =
                paths
                |> Seq.sumBy (fun path ->
                    match previous.TryGetValue(path), current.TryGetValue(path) with
                    | (true, before), (true, after) when before = after -> 0
                    | _ -> 1)
            observed <- Some current
            changed

    member _.AcceptObserved() =
        observed |> Option.iter (fun snapshot -> committed <- Some snapshot)
        observed <- None

    member _.ConsumeSignal() =
        Interlocked.Exchange(&dirty, 0) <> 0

    member _.ConsumeError() =
        lock gate (fun () ->
            let value = errorMessage
            errorMessage <- None
            value)

    member _.Recover() =
        lock gate (fun () ->
            try
                replaceWatchers()
                errorMessage <- None
                true
            with exceptionValue ->
                for watcher in watchers do
                    try
                        watcher.EnableRaisingEvents <- false
                        watcher.Dispose()
                    with _ -> ()
                watchers <- [||]
                errorMessage <- Some exceptionValue.Message
                false)

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                for watcher in watchers do
                    watcher.EnableRaisingEvents <- false
                    watcher.Dispose()
                watchers <- [||])