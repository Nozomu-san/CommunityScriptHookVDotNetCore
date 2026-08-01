namespace ScriptHookInput.Source

open System
open System.Collections.Generic
open Alloc8orStandardNatives.Source
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
    member _.References = references
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
        state <- InputState.Unavailable(frameIndex, origin)
        previousDown <- false
        initialized <- false

[<Sealed>]
type internal InputRuntime(nativeServices: IStandardNatives) as this =
    let gate = obj()
    let gameReader = GameInputReader(nativeServices)
    let deviceReader = new DeviceInputReader()
    let observations = Dictionary<ObservationKey, ObservationEntry>()
    let actions = ResizeArray<InputAction>()
    let mutable frame = InputFrame.Empty
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
                created

        entry.AddReference()
        entry

    let release (entry: ObservationEntry) =
        if entry.RemoveReference() = 0 then
            observations.Remove(entry.Key) |> ignore

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

            for action in actions do
                action.Update(context.HostFrameIndex))

    member private _.CreateInputAction(binding: InputActionBinding) =
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
                        acquired.ToArray())

                actions.Add(action)
                action :> IInputAction
            with
            | _ ->
                for entry in acquired do
                    release entry
                reraise())

    interface IScriptHookInput with
        member _.Frame = lock gate (fun () -> frame)
        member _.Parse(text) = InputBindingCodec.Parse(text)
        member _.ParseMany(text) = InputBindingCodec.ParseMany(text)

    interface IInputActions with
        member _.Create(binding) =
            this.CreateInputAction(binding)

        member _.Create(name, inputs) =
            this.CreateInputAction(InputActionBinding(name, inputs))

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    disposed <- true
                    for action in actions do
                        action.Detach()
                    actions.Clear()
                    observations.Clear()
                    frame <- InputFrame.Empty
                    (deviceReader :> IDisposable).Dispose())

and internal InputAction(
    runtime: InputRuntime,
    name: string,
    entries: ObservationEntry array) as this =

    let gate = obj()
    let mutable attached = true
    let mutable initialized = false
    let mutable previousDown = false
    let mutable previousOrigin = InputOrigin.None
    let mutable state = InputState.Unavailable(0UL, InputOrigin.None)

    member _.Entries = entries

    member _.Update(frameIndex: uint64) =
        lock gate (fun () ->
            if attached then
                let mutable availableCount = 0
                let mutable currentDown = false
                let mutable strongestValue = 0.0f
                let mutable strongestOrigin = InputOrigin.None
                let mutable downOrigin = InputOrigin.None
                let mutable hasDownOrigin = false

                for entry in entries do
                    let sample = entry.State
                    if sample.IsAvailable then
                        availableCount <- availableCount + 1
                        if abs sample.Value > abs strongestValue then
                            strongestValue <- sample.Value
                            strongestOrigin <- sample.Origin
                        if sample.IsDown then
                            currentDown <- true
                            if not hasDownOrigin then
                                hasDownOrigin <- true
                                downOrigin <- sample.Origin

                if availableCount = 0 then
                    state <-
                        InputState.Unavailable(
                            frameIndex,
                            InputOrigin.None)
                    initialized <- false
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
                    initialized <- true)

    member _.Detach() =
        lock gate (fun () ->
            attached <- false
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

    interface IDisposable with
        member _.Dispose() =
            runtime.ReleaseAction(this)
