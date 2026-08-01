namespace Script4Reload.Source

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open ScriptHookInput.Source
open CommunityScriptHookVDotNetCore.Source

[<RequireQualifiedAccess>]
type internal SnapshotWork =
    | ExplicitRequest
    | SynchronizedRequest
    | Reconciliation

[<Sealed>]
type Scripts4Lifecycle
    (
        context: RuntimeExtensionContext,
        host: IReloadRuntimeHost,
        config: Script4ReloadConfig,
        log: ReloadLog
    ) =

    let lifetime = new CancellationTokenSource()
    let mutable baseline: ScriptsDirectorySnapshot option = None
    let mutable watcher: Scripts4Watcher option = None
    let mutable reloadAction: IInputAction option = None
    let mutable explicitReloadPending = false
    let mutable synchronizedReloadPending = false
    let mutable activeOperation:
        ScriptLifecycleTransitionOperationId option = None
    let mutable activeInputSnapshot: ScriptsDirectorySnapshot option = None
    let mutable lastOperationState:
        ScriptLifecycleTransitionOperationState option = None
    let mutable reconciliationPending = false
    let mutable snapshotWork: SnapshotWork option = None
    let mutable snapshotTask:
        Task<Result<ScriptsDirectorySnapshot, string>> option = None
    let mutable lastSnapshotDiagnostic: string option = None
    let mutable shutdown = false

    let formatOperationId
        (value: ScriptLifecycleTransitionOperationId) =
        value.Value.ToString("D")

    let describe (values: IEnumerable<string>) =
        values
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> String.concat ", "

    let captureSnapshot (cancellationToken: CancellationToken) =
        ScriptsDirectorySnapshot.Capture(
            context.ScriptsDirectory,
            cancellationToken)

    let beginTransition
        (snapshot: ScriptsDirectorySnapshot)
        (reason: ScriptLifecycleTransitionReason)
        allowLifecycleOnly =
        match baseline with
        | None -> baseline <- Some snapshot
        | Some previous ->
            let plan =
                ReloadPlanning.build(
                    host.GetInventory(),
                    previous,
                    snapshot)

            if not plan.HasBinaryReplacement && not allowLifecycleOnly then
                baseline <- Some snapshot
                log.Information(
                    "The scripts4 signal did not produce a new binary " +
                    "generation; no lifecycle transition was requested.")
            else
                let request =
                    ScriptLifecycleTransitionPlan(
                        plan.BinaryReplacementPackages,
                        plan.RestartAllExecutables,
                        reason)
                let operation = host.RequestTransition request
                activeOperation <- Some operation
                activeInputSnapshot <- Some snapshot
                lastOperationState <- None
                let expansion =
                    if plan.ExpandedByDependencies then
                        " The coherent dependency closure was included."
                    else
                        String.Empty
                let replacement =
                    if plan.HasBinaryReplacement then
                        "[" +
                        describe plan.BinaryReplacementPackages +
                        "]"
                    else
                        "none; lifecycle-only restart"

                log.Information(
                    $"Lifecycle transition '{formatOperationId operation}' " +
                    $"requested. Binary replacement={replacement}." +
                    expansion)

    let writeSnapshotDiagnostic prefix message =
        if lastSnapshotDiagnostic <> Some message then
            lastSnapshotDiagnostic <- Some message
            log.Warning(prefix + message)

    let writeOperationResult
        (snapshot: ScriptLifecycleTransitionOperationSnapshot) =
        let result = snapshot.Result
        if not (isNull result) then
            let summary =
                $"Epoch={result.LifecycleEpoch} " +
                "Restarted=[" +
                describe result.RestartedInPlacePackages +
                "] Replaced=[" +
                describe result.BinaryReplacedPackages +
                "] Libraries=[" +
                describe result.RefreshedLibraries +
                "] Added=[" +
                describe result.AddedPackages +
                "] Removed=[" +
                describe result.RemovedPackages +
                "] Failed=[" +
                describe result.FailedPackages +
                "]."

            if result.Succeeded then
                log.Information summary
            else
                log.Error summary

        if not (String.IsNullOrWhiteSpace snapshot.Diagnostic) then
            if snapshot.State =
               ScriptLifecycleTransitionOperationState.Failed then
                log.Error snapshot.Diagnostic
            elif snapshot.State =
                 ScriptLifecycleTransitionOperationState.Cancelled then
                log.Information snapshot.Diagnostic
            else
                log.Warning snapshot.Diagnostic

    let advanceOperation() =
        match activeOperation with
        | None -> ()
        | Some operationId ->
            let current =
                host.SnapshotOperations()
                |> Seq.tryFind (fun value -> value.Id = operationId)

            match current with
            | None ->
                log.Error(
                    $"Lifecycle transition " +
                    $"'{formatOperationId operationId}' disappeared before " +
                    "it reached a terminal state.")
                activeOperation <- None
                lastOperationState <- None
                reconciliationPending <- true
            | Some snapshot ->
                if lastOperationState <> Some snapshot.State then
                    lastOperationState <- Some snapshot.State
                    log.Information(
                        $"Lifecycle transition " +
                        $"'{formatOperationId operationId}' state -> " +
                        $"{snapshot.State}.")

                if snapshot.IsTerminal then
                    writeOperationResult snapshot
                    host.Acknowledge operationId
                    activeOperation <- None
                    lastOperationState <- None
                    reconciliationPending <- true

    let processSnapshot work result =
        match result with
        | Error message ->
            let prefix =
                match work with
                | SnapshotWork.Reconciliation ->
                    "Post-transition scripts4 reconciliation is waiting " +
                    "for a readable generation: "
                | _ ->
                    "The scripts4 generation is not readable yet: "
            writeSnapshotDiagnostic prefix message
        | Ok current ->
            lastSnapshotDiagnostic <- None
            snapshotWork <- None
            match work with
            | SnapshotWork.ExplicitRequest ->
                explicitReloadPending <- false
                beginTransition
                    current
                    ScriptLifecycleTransitionReason.ManualReload
                    true
            | SnapshotWork.SynchronizedRequest ->
                synchronizedReloadPending <- false
                beginTransition
                    current
                    ScriptLifecycleTransitionReason.SynchronizedReload
                    false
            | SnapshotWork.Reconciliation ->
                reconciliationPending <- false
                match activeInputSnapshot with
                | Some captured when not (captured.ContentEquals current) ->
                    baseline <- Some captured
                    activeInputSnapshot <- None
                    log.Information(
                        "A newer scripts4 generation appeared during the " +
                        "lifecycle transition; a follow-up operation is " +
                        "being requested.")
                    beginTransition
                        current
                        ScriptLifecycleTransitionReason.SynchronizedReload
                        false
                | _ ->
                    baseline <- Some current
                    activeInputSnapshot <- None

    let advanceSnapshotWork() =
        match snapshotWork, snapshotTask with
        | Some _, None when not shutdown ->
            snapshotTask <-
                Some(
                    Task.Run(
                        (fun () -> captureSnapshot lifetime.Token),
                        lifetime.Token))
        | Some work, Some task when task.IsCompleted ->
            snapshotTask <- None
            if task.IsCanceled || shutdown then
                snapshotWork <- None
            else
                try
                    task.GetAwaiter().GetResult()
                    |> processSnapshot work
                with
                | :? OperationCanceledException ->
                    snapshotWork <- None
                | exceptionValue ->
                    processSnapshot work (Error exceptionValue.Message)
        | _ -> ()

    let selectSnapshotWork() =
        if Option.isNone snapshotWork &&
           Option.isNone snapshotTask &&
           Option.isNone activeOperation then
            if reconciliationPending then
                snapshotWork <- Some SnapshotWork.Reconciliation
            elif explicitReloadPending then
                snapshotWork <- Some SnapshotWork.ExplicitRequest
            elif synchronizedReloadPending then
                snapshotWork <- Some SnapshotWork.SynchronizedRequest

    member _.Initialize() =
        if shutdown then
            raise (ObjectDisposedException(nameof Scripts4Lifecycle))

        match captureSnapshot lifetime.Token with
        | Ok snapshot -> baseline <- Some snapshot
        | Error message ->
            invalidOp(
                "The initial scripts4 snapshot could not be captured: " +
                message)

        match config.Mode with
        | ReloadMode.Manual ->
            let input = context.Services.GetRequired<IScriptHookInput>()
            reloadAction <-
                Some(
                    input.Create(
                        "Script4Reload.Reload",
                        config.ReloadInputs))
        | ReloadMode.Synchronized ->
            watcher <- Some(new Scripts4Watcher(context.ScriptsDirectory))

        host.ActivateInitialPackages()
        log.Information(
            $"Script4Reload initialized in {config.Mode} mode and " +
            "activated the initial scripts4 lifecycle generation.")

    member _.RequestExplicitReload(reason: string) =
        if shutdown then
            false
        else
            explicitReloadPending <- true
            let message =
                if String.IsNullOrWhiteSpace reason then
                    "Explicit scripts4 lifecycle restart requested."
                else
                    reason.Trim()
            log.Information message
            true

    member _.AdvanceFrame(frame: RuntimeExtensionFrameContext) =
        if not shutdown then
            match reloadAction with
            | Some action ->
                let state = action.State
                if state.FrameIndex = frame.HostFrameIndex &&
                   state.WasPressed then
                    explicitReloadPending <- true
                    log.Information(
                        "Manual scripts4 lifecycle restart requested by " +
                        "ScriptHookInput.")
            | None -> ()

            match watcher with
            | Some value ->
                match value.ConsumeError() with
                | Some message ->
                    log.Warning(
                        "The scripts4 watcher lost reliable event history " +
                        "and requested full reconciliation: " +
                        message)
                    if value.Recover() then
                        log.Information(
                            "The scripts4 watcher was recreated successfully.")
                    else
                        log.Warning(
                            "The scripts4 watcher could not be recreated yet.")
                    synchronizedReloadPending <- true
                | None -> ()

                if value.ConsumeSignal() then
                    synchronizedReloadPending <- true
            | None -> ()

            advanceOperation()
            selectSnapshotWork()
            advanceSnapshotWork()

    member _.Shutdown() =
        if not shutdown then
            shutdown <- true
            lifetime.Cancel()
            watcher
            |> Option.iter (fun value ->
                (value :> IDisposable).Dispose())
            watcher <- None
            reloadAction
            |> Option.iter (fun value -> value.Dispose())
            reloadAction <- None
            explicitReloadPending <- false
            synchronizedReloadPending <- false
            reconciliationPending <- false
            activeOperation <- None
            activeInputSnapshot <- None
            snapshotTask <- None
            snapshotWork <- None
            baseline <- None
            log.Information(
                "Script4Reload runtime lifecycle shutdown completed.")