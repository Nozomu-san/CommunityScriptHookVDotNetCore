namespace ScriptHookInput.Source

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Alloc8orStandardNatives.Source
open CommunityScriptHookVDotNetCore.Source

[<assembly: AssemblyMetadata("SHVDN4.Role", "RuntimeExtension")>]
[<assembly: AssemblyMetadata("SHVDN4.Id", "ScriptHookInput")>]
[<assembly: AssemblyMetadata("SHVDN4.EntryType", "ScriptHookInput.Source.InputExtension")>]
[<assembly: AssemblyMetadata("SHVDN4.ContractMajor", "1")>]
[<assembly: AssemblyMetadata("SHVDN4.ContractMinor", "0")>]
[<assembly: AssemblyMetadata("SHVDN4.Provides", "input.snapshot;input.actions;input.game;input.device")>]
[<assembly: AssemblyMetadata("SHVDN4.Requires", "native.standard;host.frame")>]
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
                let nativeServices =
                    context.Services.GetRequired<IStandardNatives>()
                let instance = new InputRuntime(nativeServices)
                context.Services.Register<IScriptHookInput>(instance)
                context.Services.Register<IInputActions>(instance)
                runtime <- Some instance
                ValueTask.CompletedTask

        member _.AdvanceHostFrame(context: RuntimeExtensionFrameContext) =
            match runtime with
            | Some instance -> instance.AdvanceFrame(context)
            | None -> invalidOp "ScriptHookInput has not been initialized."

        member _.ShutdownAsync(cancellationToken: CancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()
            match runtime with
            | Some instance ->
                (instance :> IDisposable).Dispose()
                runtime <- None
            | None -> ()
            ValueTask.CompletedTask
