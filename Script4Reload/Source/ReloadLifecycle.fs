namespace Script4Reload.Source

open System
open System.Diagnostics
open System.IO
open CommunityScriptHookVDotNetCore.Source

type internal IManualReloadInput =
    inherit IDisposable
    abstract WasPressed: bool

[<Sealed>]
type internal ManualReloadInput(context: RuntimeExtensionContext, inputText: string) =
    let actions =
        context.Services.GetRequired<ScriptHookInput.Source.IInputActions>()
    let action = actions.CreateSingle("Script4Reload.Manual", inputText)

    interface IManualReloadInput with
        member _.WasPressed = action.State.WasPressed
        member _.Dispose() = action.Dispose()

[<Sealed>]
type Scripts4Lifecycle
    (
        context: RuntimeExtensionContext,
        host: IReloadRuntimeHost,
        config: Script4ReloadConfig,
        log: ReloadLog
    ) =

    let settleWindow = TimeSpan.FromMilliseconds 150.0
    let mutable watcher: ReloadWatcher option = None
    let mutable manualInput: IManualReloadInput option = None
    let mutable activeOperation: ScriptLifecycleTransitionOperationId option = None
    let mutable lastOperationState: ScriptLifecycleTransitionOperationState option = None
    let mutable synchronizedDirty = false
    let mutable lastDirtyTimestamp = 0L
    let mutable manualNoticeShown = false
    let mutable pendingChangeCount = 0
    let mutable explicitReloadPending = false
    let mutable explicitRestartPending: string array option = None
    let mutable shutdown = false
    let mutable selfUnloadConfirmationPending = false
    let mutable operationReconciled = false
    let mutable manualInputWaitStarted = 0L
    let mutable manualInputWaitWarningShown = false
    let mutable manualInputRejected = false

    let selfSourceExists() =
        File.Exists(Path.Combine(context.ExtensionsDirectory, "Script4Reload.dll"))

    let reconcileOperation() =
        if not operationReconciled then
            operationReconciled <- true
            host.SnapshotOperations()
            |> Seq.filter (fun snapshot ->
                String.Equals(snapshot.Plan.RequestedBy, "Script4Reload",
                              StringComparison.OrdinalIgnoreCase))
            |> Seq.tryLast
            |> Option.iter (fun snapshot ->
                activeOperation <- Some snapshot.Id
                lastOperationState <- None)

    let formatOperationId (value: ScriptLifecycleTransitionOperationId) =
        value.Value.ToString "D"

    let describe (values: seq<string>) =
        values
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> String.concat ", "

    let acquireManualInput() =
        if config.Mode = ReloadMode.Manual && manualInput.IsNone &&
           not shutdown && not manualInputRejected then
            try
                context.Dependencies.Require("input.actions")
                let input = new ManualReloadInput(context, config.ReloadInput)
                manualInput <- Some(input :> IManualReloadInput)
                manualInputWaitStarted <- 0L
                manualInputWaitWarningShown <- false
                log.Information($"Manual reload input is active: {config.ReloadInput}.")
            with
            | :? InvalidOperationException as failure ->
                if manualInputWaitStarted = 0L then
                    manualInputWaitStarted <- Stopwatch.GetTimestamp()
                    log.Information(
                        "Manual reload is waiting for the input.actions " +
                        "capability; Script4Reload will retry on the next frame.")
                elif not manualInputWaitWarningShown &&
                     Stopwatch.GetElapsedTime(manualInputWaitStarted) >=
                         TimeSpan.FromSeconds 2.0 then
                    manualInputWaitWarningShown <- true
                    log.Warning(
                        "Manual reload input has not become available: " +
                        failure.Message)
            | :? ArgumentException as failure ->
                manualInputRejected <- true
                log.Error(
                    $"Invalid manual reload input '{config.ReloadInput}': " +
                    failure.Message)
            | :? FormatException as failure ->
                manualInputRejected <- true
                log.Error(
                    $"Invalid manual reload input '{config.ReloadInput}': " +
                    failure.Message)

    let releaseManualInput() =
        manualInput
        |> Option.iter (fun value ->
            try
                value.Dispose()
            with _ -> ())
        manualInput <- None

    let requestPlan (plan: ScriptLifecycleTransitionPlan) (changedFiles: int) =
        if activeOperation.IsSome || shutdown then
            false
        else
            let ownedPlan =
                ScriptLifecycleTransitionPlan(plan.Reason, plan.TargetPackages,
                                              "Script4Reload", changedFiles)
            try
                let operation = host.RequestTransition ownedPlan
                activeOperation <- Some operation
                lastOperationState <- None
                log.Information(
                    $"lifecycle transition '{formatOperationId operation}' requested; " +
                    $"reason={plan.Reason}.")
                true
            with :? InvalidOperationException ->
                false

    let request reason changedFiles =
        requestPlan (ScriptLifecycleTransitionPlan(reason)) changedFiles

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
                    $"global reload '{formatOperationId operationId}' " +
                    "disappeared before reaching a terminal state.")
                activeOperation <- None
                lastOperationState <- None
            | Some snapshot ->
                if lastOperationState <> Some snapshot.State then
                    lastOperationState <- Some snapshot.State
                    log.Information(
                        $"global reload '{formatOperationId operationId}' " +
                        $"state -> {snapshot.State}.")

                if snapshot.IsTerminal then
                    writeOperationResult snapshot
                    let result = snapshot.Result
                    if snapshot.State = ScriptLifecycleTransitionOperationState.Completed &&
                       not (isNull result) && result.Succeeded &&
                       snapshot.Plan.Reason = ScriptLifecycleTransitionReason.AutomaticReload &&
                       snapshot.Plan.SourceChangeCount > 0 then
                        let count = snapshot.Plan.SourceChangeCount
                        log.Success($"Changes in {count} DLL file(s) have been synchronized.")
                    if snapshot.State = ScriptLifecycleTransitionOperationState.Completed &&
                       not (isNull result) && result.Succeeded then
                        watcher |> Option.iter (fun value -> value.AcceptObserved())
                        manualNoticeShown <- false
                        pendingChangeCount <- 0
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
                    "The reload watcher lost reliable event history: " + message)
                if value.Recover() then
                    synchronizedDirty <- true
                    lastDirtyTimestamp <- Stopwatch.GetTimestamp()
                    log.Information(
                        "The reload watcher was recreated; a full CSHVDNC " +
                        "reconcile will be requested after stabilization.")
                else
                    log.Error("The reload watcher could not be recreated.")
            | None -> ()

            if value.ConsumeSignal() then
                synchronizedDirty <- true
                lastDirtyTimestamp <- Stopwatch.GetTimestamp()

    let inspectChanges() =
        match watcher with
        | None -> 0
        | Some value -> value.InspectChanges()

    let retryInspection (exceptionValue: exn) =
        synchronizedDirty <- true
        lastDirtyTimestamp <- Stopwatch.GetTimestamp()
        log.Information("A changed DLL is not yet readable: " + exceptionValue.Message)

    let advanceManualDetection() =
        if config.Mode = ReloadMode.Manual &&
           synchronizedDirty && lastDirtyTimestamp <> 0L &&
           Stopwatch.GetElapsedTime(lastDirtyTimestamp) >= settleWindow then
            try
                let count = inspectChanges()
                pendingChangeCount <- count
                if count = 0 then
                    manualNoticeShown <- false
                elif not manualNoticeShown && selfSourceExists() then
                    log.Success(
                        $"Detected changes in {count} DLL file(s). " +
                        $"Press {config.ReloadInput} to reload.")
                    manualNoticeShown <- true
                synchronizedDirty <- false
            with
            | :? IOException as failure -> retryInspection failure
            | :? UnauthorizedAccessException as failure -> retryInspection failure

    let advanceSynchronized() =
        if config.Mode = ReloadMode.Synchronized &&
           synchronizedDirty && activeOperation.IsNone &&
           lastDirtyTimestamp <> 0L &&
           Stopwatch.GetElapsedTime(lastDirtyTimestamp) >= settleWindow then
            try
                let count = inspectChanges()
                if not (selfSourceExists()) then
                    log.Warning(
                        "Script4Reload.dll is missing. Synchronized mode is " +
                        "unloading Script4Reload; reloading will be unavailable " +
                        "for the rest of this GTA V session.")
                    if request ScriptLifecycleTransitionReason.AutomaticReload 0 then
                        synchronizedDirty <- false
                elif count > 0 then
                    if request ScriptLifecycleTransitionReason.AutomaticReload count then
                        synchronizedDirty <- false
                else
                    synchronizedDirty <- false
            with
            | :? IOException as failure -> retryInspection failure
            | :? UnauthorizedAccessException as failure -> retryInspection failure

    let advanceManualInput() =
        if config.Mode = ReloadMode.Manual then
            acquireManualInput()
            match manualInput with
            | Some input ->
                try
                    if input.WasPressed then
                        if selfSourceExists() then
                            selfUnloadConfirmationPending <- false
                            try
                                pendingChangeCount <- inspectChanges()
                            with
                            | :? IOException as failure -> retryInspection failure
                            | :? UnauthorizedAccessException as failure -> retryInspection failure
                            explicitReloadPending <- true
                        elif selfUnloadConfirmationPending then
                            selfUnloadConfirmationPending <- false
                            explicitReloadPending <- true
                            log.Warning(
                                "Script4Reload self-unload confirmed; reloading " +
                                "will be unavailable for the rest of this GTA V session.")
                        else
                            selfUnloadConfirmationPending <- true
                            log.Warning(
                                $"Script4Reload.dll is missing. Press {config.ReloadInput} " +
                                "again to unload Script4Reload and disable " +
                                "reloading for the rest of this GTA V session.")
                with exceptionValue ->
                    log.Warning(
                        "Manual input became unavailable and will be reacquired: " +
                        exceptionValue.Message)
                    releaseManualInput()
            | None -> ()

    let advanceExplicitRestart() =
        match explicitRestartPending with
        | Some targets when activeOperation.IsNone ->
            let packages = if targets.Length = 0 then null else targets
            let plan = ScriptLifecycleTransitionPlan(
                ScriptLifecycleTransitionReason.ManualRestart, packages)
            if requestPlan plan 0 then
                explicitRestartPending <- None
        | _ -> ()

    let advanceExplicitReload() =
        if explicitReloadPending && activeOperation.IsNone &&
           explicitRestartPending.IsNone then
            if request ScriptLifecycleTransitionReason.ManualReload pendingChangeCount then
                explicitReloadPending <- false
                synchronizedDirty <- false

    member _.Initialize() =
        if shutdown then
            raise (ObjectDisposedException(nameof Scripts4Lifecycle))

        watcher <- Some(
            new ReloadWatcher(
                context.RootDirectory,
                context.ExtensionsDirectory,
                context.ScriptsDirectory))
        match config.Mode with
        | ReloadMode.Manual ->
            log.Information(
                $"Script4Reload Manual mode is active with input " +
                $"'{config.ReloadInput}'.")
        | ReloadMode.Synchronized ->
            synchronizedDirty <- true
            lastDirtyTimestamp <- Stopwatch.GetTimestamp()
            log.Information(
                "Script4Reload Synchronized mode is active for root extensions, extensions, and scripts4. " +
                "Filesystem events only mark disk state dirty; CSHVDNC owns capture, " +
                "validation, RAM staging, and dependency-scoped transitions.")

    member _.RequestExplicitReload(reason: string) =
        if shutdown then
            false
        else
            explicitReloadPending <- true
            log.Information(
                if String.IsNullOrWhiteSpace reason then
                    "An explicit reload was requested."
                else
                    $"An explicit reload was requested: {reason.Trim()}.")
            true

    member _.RequestExplicitRestart(targetPackages: string array) =
        if shutdown then
            false
        else
            let targets =
                if isNull targetPackages then [||]
                else targetPackages
                     |> Array.filter (String.IsNullOrWhiteSpace >> not)
                     |> Array.map (fun value -> value.Trim())
                     |> Array.distinct
            explicitRestartPending <- Some targets
            log.Information(
                "A lifecycle restart was requested; disk sources will be " +
                "validated before selecting restart, reload, load-new or unload-only.")
            true

    member _.AdvanceFrame(_: RuntimeExtensionFrameContext) =
        if not shutdown then
            reconcileOperation()
            advanceOperation()
            if activeOperation.IsNone then
                advanceWatcher()
                match config.Mode with
                | ReloadMode.Manual ->
                    advanceManualDetection()
                    advanceManualInput()
                | ReloadMode.Synchronized -> ()

                advanceExplicitRestart()
                advanceExplicitReload()

                if activeOperation.IsNone && config.Mode = ReloadMode.Synchronized then
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
            explicitRestartPending <- None
            selfUnloadConfirmationPending <- false
            manualNoticeShown <- false
            pendingChangeCount <- 0