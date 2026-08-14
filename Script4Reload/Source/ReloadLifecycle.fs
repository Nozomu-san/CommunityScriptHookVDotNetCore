namespace Script4Reload.Source

open System
open System.Diagnostics
open CommunityScriptHookVDotNetCore.Source
open ScriptHookInput.Source

[<Sealed>]
type Scripts4Lifecycle
    (
        context: RuntimeExtensionContext,
        host: IReloadRuntimeHost,
        config: Script4ReloadConfig,
        log: ReloadLog
    ) =

    let settleWindow = TimeSpan.FromMilliseconds 150.0
    let mutable watcher: Scripts4Watcher option = None
    let mutable reloadAction: IInputAction option = None
    let mutable activeOperation: ScriptLifecycleTransitionOperationId option = None
    let mutable lastOperationState: ScriptLifecycleTransitionOperationState option = None
    let mutable synchronizedDirty = false
    let mutable lastDirtyTimestamp = 0L
    let mutable explicitReloadPending = false
    let mutable shutdown = false

    let formatOperationId (value: ScriptLifecycleTransitionOperationId) =
        value.Value.ToString "D"

    let describe (values: seq<string>) =
        values
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> String.concat ", "

    let acquireManualInput() =
        if config.Mode = ReloadMode.Manual && reloadAction.IsNone && not shutdown then
            let inputActions = context.Services.GetRequired<IInputActions>()
            let action = inputActions.Create("Script4Reload.Manual", config.ReloadInputs)
            reloadAction <- Some action
            log.Information($"Manual reload input is active: {config.ReloadInputs}.")

    let releaseManualInput() =
        reloadAction
        |> Option.iter (fun value ->
            try
                value.Dispose()
            with _ -> ())
        reloadAction <- None

    let request reason =
        if activeOperation.IsSome || shutdown then
            false
        else
            let plan = ScriptLifecycleTransitionPlan(reason)
            let operation = host.RequestTransition plan
            activeOperation <- Some operation
            lastOperationState <- None
            log.Information(
                $"scripts4 reconcile '{formatOperationId operation}' requested; " +
                $"reason={reason}.")
            true

    let writeOperationResult (snapshot: ScriptLifecycleTransitionOperationSnapshot) =
        let result = snapshot.Result
        if not (isNull result) then
            let summary =
                $"Epoch={result.LifecycleEpoch} " +
                "Restarted=[" + describe result.RestartedInPlacePackages +
                "] Replaced=[" + describe result.BinaryReplacedPackages +
                "] Libraries=[" + describe result.RefreshedLibraries +
                "] Added=[" + describe result.AddedPackages +
                "] Removed=[" + describe result.RemovedPackages +
                "] Failed=[" + describe result.FailedPackages + "]."

            if result.Succeeded then
                log.Information summary
            else
                log.Error summary

        if not (String.IsNullOrWhiteSpace snapshot.Diagnostic) then
            match snapshot.State with
            | ScriptLifecycleTransitionOperationState.Failed ->
                log.Error snapshot.Diagnostic
            | ScriptLifecycleTransitionOperationState.Cancelled ->
                log.Information snapshot.Diagnostic
            | _ -> log.Information snapshot.Diagnostic

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
                    $"scripts4 reconcile '{formatOperationId operationId}' " +
                    "disappeared before reaching a terminal state.")
                activeOperation <- None
                lastOperationState <- None
            | Some snapshot ->
                if lastOperationState <> Some snapshot.State then
                    lastOperationState <- Some snapshot.State
                    log.Information(
                        $"scripts4 reconcile '{formatOperationId operationId}' " +
                        $"state -> {snapshot.State}.")

                if snapshot.IsTerminal then
                    writeOperationResult snapshot
                    host.Acknowledge operationId
                    activeOperation <- None
                    lastOperationState <- None

    let advanceWatcher() =
        match watcher with
        | None -> ()
        | Some value ->
            match value.ConsumeError() with
            | Some message ->
                log.Warning(
                    "The scripts4 watcher lost reliable event history: " + message)
                if value.Recover() then
                    synchronizedDirty <- true
                    lastDirtyTimestamp <- Stopwatch.GetTimestamp()
                    log.Information(
                        "The scripts4 watcher was recreated; a full CSHVDNC " +
                        "reconcile will be requested after stabilization.")
                else
                    log.Error("The scripts4 watcher could not be recreated.")
            | None -> ()

            if value.ConsumeSignal() then
                synchronizedDirty <- true
                lastDirtyTimestamp <- Stopwatch.GetTimestamp()

    let advanceSynchronized() =
        if config.Mode = ReloadMode.Synchronized &&
           synchronizedDirty &&
           activeOperation.IsNone &&
           lastDirtyTimestamp <> 0L &&
           Stopwatch.GetElapsedTime(lastDirtyTimestamp) >= settleWindow then
            if request ScriptLifecycleTransitionReason.SynchronizedReload then
                synchronizedDirty <- false

    let advanceManual() =
        if config.Mode = ReloadMode.Manual then
            acquireManualInput()
            match reloadAction with
            | Some action ->
                try
                    if action.State.WasPressed then
                        explicitReloadPending <- true
                with exceptionValue ->
                    log.Warning(
                        "Manual input became unavailable and will be reacquired: " +
                        exceptionValue.Message)
                    releaseManualInput()
            | None -> ()

            if explicitReloadPending && activeOperation.IsNone then
                if request ScriptLifecycleTransitionReason.ManualReload then
                    explicitReloadPending <- false

    member _.Initialize() =
        if shutdown then
            raise (ObjectDisposedException(nameof Scripts4Lifecycle))

        match config.Mode with
        | ReloadMode.Manual ->
            acquireManualInput()
            log.Information(
                $"Script4Reload Manual mode is active with input " +
                $"'{config.ReloadInputs}'. A trigger requests a global lifecycle " +
                "reload even when no DLL binary changed.")
        | ReloadMode.Synchronized ->
            watcher <- Some(new Scripts4Watcher(context.ScriptsDirectory))
            log.Information(
                "Script4Reload Synchronized mode is active. Filesystem events " +
                "only mark disk state dirty; CSHVDNC owns capture, validation, " +
                "RAM staging, diffing, and the global lifecycle barrier.")

    member _.RequestExplicitReload(reason: string) =
        if shutdown then
            false
        else
            explicitReloadPending <- true
            log.Information(
                if String.IsNullOrWhiteSpace reason then
                    "An explicit global reload was requested."
                else
                    $"An explicit global reload was requested: {reason.Trim()}.")
            true

    member _.AdvanceFrame(_: RuntimeExtensionFrameContext) =
        if not shutdown then
            advanceOperation()
            if activeOperation.IsNone then
                match config.Mode with
                | ReloadMode.Manual -> advanceManual()
                | ReloadMode.Synchronized ->
                    advanceWatcher()
                    advanceSynchronized()

    member _.Shutdown() =
        if not shutdown then
            shutdown <- true
            releaseManualInput()
            watcher
            |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            watcher <- None
            synchronizedDirty <- false
            explicitReloadPending <- false