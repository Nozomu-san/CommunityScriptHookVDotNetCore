namespace Script4Reload.Source

open System
open System.Globalization
open System.IO
open System.Text

[<Sealed>]
type ReloadLog(runtimeRoot: string) =
    let gate = obj()
    let path =
        Path.Combine(
            Path.GetFullPath runtimeRoot,
            "Script4Reload.log")

    let stream =
        new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite ||| FileShare.Delete)

    let writer = new StreamWriter(stream, UTF8Encoding(false))
    let mutable disposed = false

    do writer.AutoFlush <- true

    member private _.Write(level: string, message: string) =
        if not (isNull message) then
            lock gate (fun () ->
                if not disposed then
                    try
                        let timestamp =
                            DateTime.Now.ToString(
                                "HH:mm:ss:fff",
                                CultureInfo.InvariantCulture)
                        writer.WriteLine($"[{timestamp}] [{level}] {message}")
                    with _ -> ())

    member this.Information(message: string) =
        this.Write("Information", message)

    member this.Warning(message: string) =
        this.Write("Warning", message)

    member this.Error(message: string) =
        this.Write("Error", message)

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    disposed <- true
                    writer.Dispose())