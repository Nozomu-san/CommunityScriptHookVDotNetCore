namespace Script4Reload.Source

open System
open System.Threading.Tasks
open CommunityScriptHookVDotNetCore.Source

[<AllowNullLiteral>]
type IScript4ReloadControl =
    abstract RequestReload: reason: string -> bool

[<Sealed>]
type Script4ReloadExtension() =
    let gate = obj()
    let mutable lifecycle: Scripts4Lifecycle option = None
    let mutable reloadLog: ReloadLog option = None
    let mutable shutdown = false

    member private this.InitializeCore(context: RuntimeExtensionContext) =
        lock gate (fun () ->
            ArgumentNullException.ThrowIfNull(context)
            if shutdown then
                raise (ObjectDisposedException(nameof Script4ReloadExtension))
            if lifecycle.IsSome then
                invalidOp "Script4Reload is already initialized."

            let config, diagnostic =
                ReloadConfiguration.loadOrCreate context.RootDirectory
            let log = new ReloadLog(context.RootDirectory)
            reloadLog <- Some log

            diagnostic |> Option.iter (fun message -> log.Warning message)

            let host = context.Services.GetRequired<IReloadRuntimeHost>()
            let instance = Scripts4Lifecycle(context, host, config, log)
            instance.Initialize()
            lifecycle <- Some instance
            context.Services.Register<IScript4ReloadControl>(
                this :> IScript4ReloadControl))

    member private _.AdvanceCore(context: RuntimeExtensionFrameContext) =
        lock gate (fun () ->
            if not shutdown then
                lifecycle
                |> Option.iter (fun value -> value.AdvanceFrame(context)))

    member private _.ShutdownCore() =
        lock gate (fun () ->
            if not shutdown then
                shutdown <- true
                lifecycle |> Option.iter (fun value -> value.Shutdown())
                lifecycle <- None
                reloadLog
                |> Option.iter (fun value ->
                    value.Information(
                        "Script4Reload runtime extension shutdown completed.")
                    (value :> IDisposable).Dispose())
                reloadLog <- None)

    interface IScript4ReloadControl with
        member _.RequestReload(reason) =
            lock gate (fun () ->
                if shutdown then
                    false
                else
                    match lifecycle with
                    | Some value -> value.RequestExplicitReload(reason)
                    | None -> false)

    interface IScript4RuntimeExtension with
        member this.InitializeAsync(context, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()
            this.InitializeCore(context)
            ValueTask.CompletedTask

        member this.AdvanceHostFrame(context) =
            this.AdvanceCore(context)

        member this.ShutdownAsync(cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()
            this.ShutdownCore()
            ValueTask.CompletedTask