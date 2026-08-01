namespace Script4Reload.Source

open System
open System.IO
open System.Threading

[<Sealed>]
type Scripts4Watcher(scriptsRoot: string) =
    let root = Path.GetFullPath scriptsRoot
    let mutable watcher: FileSystemWatcher option = None
    let mutable dirty = 0
    let mutable errorMessage: string option = None
    let gate = obj()

    let markDirty() =
        Interlocked.Exchange(&dirty, 1) |> ignore

    let signal (_: obj) (_: FileSystemEventArgs) =
        markDirty()

    let renamed (_: obj) (_: RenamedEventArgs) =
        markDirty()

    let error (_: obj) (args: ErrorEventArgs) =
        lock gate (fun () ->
            errorMessage <- Some(args.GetException().Message)
            watcher
            |> Option.iter (fun value ->
                value.EnableRaisingEvents <- false))
        markDirty()

    let createWatcher() =
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

    do watcher <- Some(createWatcher())

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
                watcher
                |> Option.iter (fun value ->
                    value.EnableRaisingEvents <- false
                    value.Dispose())
                watcher <- Some(createWatcher())
                errorMessage <- None
                markDirty()
                true
            with exceptionValue ->
                watcher <- None
                errorMessage <- Some exceptionValue.Message
                false)

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                watcher
                |> Option.iter (fun value ->
                    value.EnableRaisingEvents <- false
                    value.Dispose())
                watcher <- None)