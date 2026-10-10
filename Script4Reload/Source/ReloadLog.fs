namespace Script4Reload.Source

open System
open System.Globalization
open System.IO
open System.Text
open CommunityScriptHookVDotNetCore.Source

[<Sealed>]
type ReloadLog(extensionsDirectory: string, enabled: bool, diagnostics: IRuntimeDiagnosticSink) =
    let gate = obj()
    let mutable disposed = false
    let mutable writer : StreamWriter option = None

    do
        if enabled then
            try
                let path =
                    Path.Combine(
                        Path.GetFullPath extensionsDirectory,
                        "Script4Reload.log")
                let stream =
                    new FileStream(
                        path,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.ReadWrite ||| FileShare.Delete)
                let output = new StreamWriter(stream, UTF8Encoding(false))
                output.AutoFlush <- true
                writer <- Some output
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? ArgumentException
            | :? NotSupportedException ->
                writer <- None

    member private _.Write(level: string, message: string) =
        if not (isNull message) then
            lock gate (fun () ->
                if not disposed then
                    match writer with
                    | Some output ->
                        try
                            let timestamp =
                                DateTime.Now.ToString(
                                    "HH:mm:ss:fff",
                                    CultureInfo.InvariantCulture)
                            output.WriteLine($"[{timestamp}] [{level}] {message}")
                        with _ -> ()
                    | None -> ())

    member this.Information(message: string) =
        this.Write("Information", message)

    member this.Warning(message: string) =
        this.Write("Warning", message)
        diagnostics.Warning("Script4Reload", message)

    member this.Error(message: string) =
        this.Write("Error", message)
        diagnostics.Error("Script4Reload", message)

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    disposed <- true
                    writer |> Option.iter (fun value -> value.Dispose())
                    writer <- None)