namespace ScriptHookInput.Source

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Alloc8orStandardNatives.Source
open CommunityScriptHookVDotNetCore.Source

[<assembly: AssemblyMetadata("CSHVDNC.Role", "RuntimeExtension")>]
[<assembly: AssemblyMetadata("CSHVDNC.Id", "ScriptHookInput")>]
[<assembly: AssemblyMetadata("CSHVDNC.EntryType", "ScriptHookInput.Source.InputExtension")>]
[<assembly: AssemblyMetadata("CSHVDNC.Provides", "input.snapshot;input.actions;input.game;input.device")>]
[<assembly: AssemblyMetadata("CSHVDNC.Requires", "native.standard;host.frame")>]
do ()

[<Sealed>]
type InputExtension() =
    let mutable runtime: InputRuntime option = None

    interface IScript4RuntimeExtension with
        member _.InitializeAsync(
            context: RuntimeExtensionContext,
            cancellationToken: CancellationToken) =

            ArgumentNullException.ThrowIfNull(context)
            cancellationToken.ThrowIfCancellationRequested()

            match runtime with
            | Some _ ->
                invalidOp "ScriptHookInput is already initialized."
            | None ->
                context.Services.GetRequired<IStandardNatives>() |> ignore
                let instance = new InputRuntime()
                context.Services.Register<IScriptHookInput>(instance)
                context.Services.Register<IInputActions>(instance)
                runtime <- Some instance
                Task.CompletedTask

        member _.AdvanceHostFrame(context: RuntimeExtensionFrameContext) =
            match runtime with
            | Some instance -> instance.AdvanceFrame(context)
            | None -> invalidOp "ScriptHookInput has not been initialized."

        member _.ShutdownAsync() =
            match runtime with
            | Some instance ->
                (instance :> IDisposable).Dispose()
                runtime <- None
            | None -> ()
            Task.CompletedTask