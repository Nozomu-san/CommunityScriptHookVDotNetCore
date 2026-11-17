namespace ScriptHookInput.Source

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open CommunityScriptHookVDotNetCore.Source

[<StructuralEquality; NoComparison>]
type internal ObservationKey =
    | GameKey of group: int * index: int * mode: GameControlReadMode
    | DeviceKey of deviceKind: InputDeviceKind * controlKind: InputControlKind * code: int

[<Sealed>]
type internal ObservationEntry(key: ObservationKey, origin: InputOrigin) =
    let mutable references = 0
    let mutable initialized = false
    let mutable previousDown = false
    let mutable state = InputState.Unavailable(0UL, origin)

    member _.Key = key
    member _.State = state

    member _.AddReference() =
        references <- references + 1

    member _.RemoveReference() =
        references <- references - 1
        references

    member _.PublishAvailable(
        frameIndex: uint64,
        isDown: bool,
        value: single,
        currentOrigin: InputOrigin) =

        let wasPressed = initialized && isDown && not previousDown
        let wasReleased = initialized && not isDown && previousDown
        state <-
            InputState(
                true,
                isDown,
                wasPressed,
                wasReleased,
                value,
                frameIndex,
                currentOrigin)
        previousDown <- isDown
        initialized <- true

    member _.PublishUnavailable(frameIndex: uint64) =
        let wasReleased = initialized && previousDown
        state <-
            InputState(
                false,
                false,
                false,
                wasReleased,
                0.0f,
                frameIndex,
                origin)
        previousDown <- false
        initialized <- true

[<Sealed>]
type internal InputRuntime() as this =
    let gate = obj()
    let gameReader = GameInputReader()
    let deviceReader = new DeviceInputReader()
    let observations = Dictionary<ObservationKey, ObservationEntry>()
    let actions = ResizeArray<InputAction>()
    let mutable controllerObservationCount = 0
    let mutable frame = InputFrame.Empty
    let mutable pointer = PointerState.Unavailable 0UL
    let mutable disposed = false

    let throwIfDisposed() =
        if disposed then
            raise (ObjectDisposedException(nameof InputRuntime))

    let gameKey (control: GameControl) mode =
        GameKey(control.Group, control.Index, mode)

    let deviceKey (control: DeviceControl) =
        DeviceKey(control.DeviceKind, control.ControlKind, control.Code)

    let acquire key origin =
        let entry =
            match observations.TryGetValue key with
            | true, existing -> existing
            | false, _ ->
                let created = ObservationEntry(key, origin)
                observations.Add(key, created)
                match key with
                | DeviceKey(InputDeviceKind.Controller, _, _) ->
                    controllerObservationCount <- controllerObservationCount + 1
                | _ -> ()
                created

        entry.AddReference()
        entry

    let release (entry: ObservationEntry) =
        if entry.RemoveReference() = 0 then
            if observations.Remove(entry.Key) then
                match entry.Key with
                | DeviceKey(InputDeviceKind.Controller, _, _) ->
                    controllerObservationCount <- controllerObservationCount - 1
                | _ -> ()

    let readEntry
        (entry: ObservationEntry)
        (frameContext: DeviceInputFrameContext) =

        match entry.Key with
        | GameKey(group, index, mode) ->
            let control = GameControl(group, index)
            match gameReader.Read(control, mode) with
            | ValueSome(struct (down, value)) ->
                ValueSome(struct (down, value, InputOrigin.Game(control)))
            | ValueNone -> ValueNone

        | DeviceKey(kind, controlKind, code) ->
            let control = DeviceControl(kind, controlKind, code)
            match deviceReader.Read(frameContext, control) with
            | ValueSome(struct (down, value)) ->
                ValueSome(struct (down, value, InputOrigin.Device(control)))
            | ValueNone -> ValueNone

    member internal _.ReleaseAction(action: InputAction) =
        lock gate (fun () ->
            if not disposed && actions.Remove(action) then
                action.Detach()
                for entry in action.Entries do
                    release entry)

    member internal _.AdvanceFrame(context: RuntimeExtensionFrameContext) =
        lock gate (fun () ->
            throwIfDisposed()

            let foreground = DeviceInputReader.IsCurrentProcessForeground()
            let frameContext =
                DeviceInputFrameContext(
                    context.HostFrameIndex,
                    foreground)

            deviceReader.Capture(
                frameContext,
                controllerObservationCount > 0)
            pointer <- deviceReader.Pointer

            let usingKeyboardAndMouse =
                foreground && gameReader.IsUsingKeyboardAndMouse()

            frame <-
                InputFrame(
                    context.HostFrameIndex,
                    context.PerformanceCounter,
                    context.PerformanceFrequency,
                    foreground,
                    usingKeyboardAndMouse)

            for entry in observations.Values do
                if foreground then
                    match readEntry entry frameContext with
                    | ValueSome(struct (down, value, origin)) ->
                        entry.PublishAvailable(
                            context.HostFrameIndex,
                            down,
                            value,
                            origin)
                    | ValueNone ->
                        entry.PublishUnavailable(context.HostFrameIndex)
                else
                    entry.PublishUnavailable(context.HostFrameIndex)

            for action in actions.ToArray() do
                action.Update(context.HostFrameIndex))

    member private _.CreateInputAction(binding: InputActionBinding, requireAll: bool) =
        ArgumentNullException.ThrowIfNull(binding)

        lock gate (fun () ->
            throwIfDisposed()
            let unique = HashSet<ObservationKey>()
            let acquired = ResizeArray<ObservationEntry>()

            try
                for input in binding.Inputs do
                    let key, origin =
                        match input with
                        | :? GameControlBinding as game ->
                            gameKey game.Control game.Mode,
                            InputOrigin.Game(game.Control)
                        | :? DeviceControlBinding as device ->
                            deviceKey device.Control,
                            InputOrigin.Device(device.Control)
                        | _ ->
                            invalidArg
                                (nameof binding)
                                "The action contains an unsupported input binding type."

                    if unique.Add(key) then
                        acquired.Add(acquire key origin)

                let action =
                    new InputAction(
                        this,
                        binding.Name,
                        acquired.ToArray(),
                        requireAll)

                actions.Add(action)
                action :> IInputAction
            with
            | _ ->
                for entry in acquired do
                    release entry
                reraise())

    interface IScriptHookInput with
        member _.Frame = lock gate (fun () -> frame)
        member _.Pointer = lock gate (fun () -> pointer)
        member _.Parse(text) = InputBindingCodec.Parse(text)
        member _.ParseMany(text) = InputBindingCodec.ParseMany(text)

    interface IInputActions with
        member _.Create(binding) =
            this.CreateInputAction(binding, false)

        member _.Create(name, inputs) =
            this.CreateInputAction(InputActionBinding(name, inputs), false)

        member _.CreateSingle(name, input) =
            let binding = InputBindingCodec.Parse(input)
            this.CreateInputAction(InputActionBinding(name, [| binding |]), false)

        member _.CreateChord(binding) =
            this.CreateInputAction(binding, true)

        member _.CreateChord(name, inputs) =
            this.CreateInputAction(InputActionBinding(name, inputs), true)

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    disposed <- true
                    for action in actions do
                        action.Detach()
                    actions.Clear()
                    observations.Clear()
                    controllerObservationCount <- 0
                    frame <- InputFrame.Empty
                    pointer <- PointerState.Unavailable frame.FrameIndex
                    (deviceReader :> IDisposable).Dispose())

and internal InputAction(
    runtime: InputRuntime,
    name: string,
    entries: ObservationEntry array,
    requireAll: bool) as this =

    let gate = obj()
    let mutable attached = true
    let mutable initialized = false
    let mutable previousDown = false
    let mutable previousOrigin = InputOrigin.None
    let mutable state = InputState.Unavailable(0UL, InputOrigin.None)
    let entryStates = Array.init entries.Length (fun index -> entries[index].State)
    let pressWaiters = ResizeArray<TaskCompletionSource<InputState>>()
    let releaseWaiters = ResizeArray<TaskCompletionSource<InputState>>()
    let matchWaiters =
        ResizeArray<Func<IReadOnlyList<InputState>, bool> * TaskCompletionSource<InputState>>()

    let removeMatchWaiter (waiter: TaskCompletionSource<InputState>) =
        for index = matchWaiters.Count - 1 downto 0 do
            let _, candidate = matchWaiters[index]
            if Object.ReferenceEquals(candidate, waiter) then
                matchWaiters.RemoveAt(index)

    member _.Entries = entries

    member _.Update(frameIndex: uint64) =
        lock gate (fun () ->
            if attached then
                let mutable availableCount = 0
                let mutable currentDown = requireAll
                let mutable strongestValue = 0.0f
                let mutable strongestOrigin = InputOrigin.None
                let mutable downOrigin = InputOrigin.None
                let mutable hasDownOrigin = false

                for index = 0 to entries.Length - 1 do
                    let sample = entries[index].State
                    entryStates[index] <- sample
                    if sample.IsAvailable then
                        availableCount <- availableCount + 1
                        if abs sample.Value > abs strongestValue then
                            strongestValue <- sample.Value
                            strongestOrigin <- sample.Origin
                        if requireAll then
                            currentDown <- currentDown && sample.IsDown
                        elif sample.IsDown then
                            currentDown <- true
                        if sample.IsDown then
                            if not hasDownOrigin then
                                hasDownOrigin <- true
                                downOrigin <- sample.Origin

                if availableCount = 0 ||
                   (requireAll && availableCount <> entries.Length) then
                    let wasReleased = initialized && previousDown
                    state <-
                        InputState(
                            false,
                            false,
                            false,
                            wasReleased,
                            0.0f,
                            frameIndex,
                            previousOrigin)
                    initialized <- true
                    previousDown <- false
                    previousOrigin <- InputOrigin.None
                else
                    let origin =
                        if hasDownOrigin then
                            downOrigin
                        elif previousDown then
                            previousOrigin
                        else
                            strongestOrigin

                    let wasPressed =
                        initialized && currentDown && not previousDown

                    let wasReleased =
                        initialized && not currentDown && previousDown

                    state <-
                        InputState(
                            true,
                            currentDown,
                            wasPressed,
                            wasReleased,
                            strongestValue,
                            frameIndex,
                            origin)

                    if currentDown then
                        previousOrigin <- origin

                    previousDown <- currentDown
                    initialized <- true

                if state.WasPressed then
                    for waiter in pressWaiters do
                        waiter.TrySetResult(state) |> ignore
                    pressWaiters.Clear()
                if state.WasReleased then
                    for waiter in releaseWaiters do
                        waiter.TrySetResult(state) |> ignore
                    releaseWaiters.Clear()

                if matchWaiters.Count > 0 then
                    let samples =
                        System.Array.AsReadOnly(Array.copy entryStates)
                        :> IReadOnlyList<InputState>
                    for (predicate, waiter) in matchWaiters.ToArray() do
                        if not waiter.Task.IsCompleted then
                            try
                                if predicate.Invoke(samples) then
                                    removeMatchWaiter waiter
                                    waiter.TrySetResult(state) |> ignore
                            with ex ->
                                removeMatchWaiter waiter
                                waiter.TrySetException(ex) |> ignore)

    member private _.WaitForTransition(
        isPress: bool,
        cancellationToken: CancellationToken) : Task<InputState> =

        let completion =
            TaskCompletionSource<InputState>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        let waiters = if isPress then pressWaiters else releaseWaiters
        let registered =
            lock gate (fun () ->
                if not attached then
                    completion.TrySetException(
                        ObjectDisposedException(nameof InputAction)) |> ignore
                    false
                elif cancellationToken.IsCancellationRequested then
                    completion.TrySetCanceled(cancellationToken) |> ignore
                    false
                else
                    waiters.Add(completion)
                    true)

        if registered && cancellationToken.CanBeCanceled then
            let registration =
                cancellationToken.Register(
                    Action(fun () ->
                        lock gate (fun () ->
                            waiters.Remove(completion) |> ignore)
                        completion.TrySetCanceled(cancellationToken) |> ignore))
            completion.Task.ContinueWith(
                Action<Task<InputState>>(fun _ -> registration.Dispose()),
                CancellationToken.None,
                TaskContinuationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default) |> ignore

        completion.Task

    member private _.WaitForMatch(
        predicate: Func<IReadOnlyList<InputState>, bool>,
        cancellationToken: CancellationToken) : Task<InputState> =

        ArgumentNullException.ThrowIfNull(predicate)
        let completion =
            TaskCompletionSource<InputState>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        let registered =
            lock gate (fun () ->
                if not attached then
                    completion.TrySetException(
                        ObjectDisposedException(nameof InputAction)) |> ignore
                    false
                elif cancellationToken.IsCancellationRequested then
                    completion.TrySetCanceled(cancellationToken) |> ignore
                    false
                else
                    try
                        let samples =
                            System.Array.AsReadOnly(Array.copy entryStates)
                            :> IReadOnlyList<InputState>
                        if state.IsAvailable && predicate.Invoke(samples) then
                            completion.TrySetResult(state) |> ignore
                            false
                        else
                            matchWaiters.Add((predicate, completion))
                            true
                    with ex ->
                        completion.TrySetException(ex) |> ignore
                        false)

        if registered && cancellationToken.CanBeCanceled then
            let registration =
                cancellationToken.Register(
                    Action(fun () ->
                        lock gate (fun () ->
                            removeMatchWaiter completion)
                        completion.TrySetCanceled(cancellationToken) |> ignore))
            completion.Task.ContinueWith(
                Action<Task<InputState>>(fun _ -> registration.Dispose()),
                CancellationToken.None,
                TaskContinuationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default) |> ignore

        completion.Task

    member _.Detach() =
        lock gate (fun () ->
            attached <- false
            for waiter in pressWaiters do
                waiter.TrySetException(
                    ObjectDisposedException(nameof InputAction)) |> ignore
            for waiter in releaseWaiters do
                waiter.TrySetException(
                    ObjectDisposedException(nameof InputAction)) |> ignore
            pressWaiters.Clear()
            releaseWaiters.Clear()
            for (_, waiter) in matchWaiters do
                waiter.TrySetException(
                    ObjectDisposedException(nameof InputAction)) |> ignore
            matchWaiters.Clear()
            for index = 0 to entryStates.Length - 1 do
                entryStates[index] <-
                    InputState.Unavailable(state.FrameIndex, InputOrigin.None)
            initialized <- false
            previousDown <- false
            previousOrigin <- InputOrigin.None
            state <-
                InputState.Unavailable(
                    state.FrameIndex,
                    InputOrigin.None))

    interface IInputAction with
        member _.Name = name
        member _.State = lock gate (fun () -> state)
        member _.States =
            lock gate (fun () ->
                System.Array.AsReadOnly(Array.copy entryStates) :> IReadOnlyList<InputState>)
        member _.WaitForPressAsync(cancellationToken) =
            this.WaitForTransition(true, cancellationToken)
        member _.WaitForReleaseAsync(cancellationToken) =
            this.WaitForTransition(false, cancellationToken)
        member _.WaitForMatchAsync(predicate, cancellationToken) =
            this.WaitForMatch(predicate, cancellationToken)

    interface IDisposable with
        member _.Dispose() =
            runtime.ReleaseAction(this)