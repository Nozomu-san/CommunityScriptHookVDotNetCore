namespace ScriptHookInput.Source

open System
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

[<Struct; IsReadOnly>]
type internal DeviceInputFrameContext =
    val FrameIndex: uint64
    val IsForeground: bool

    new(frameIndex, isForeground) =
        { FrameIndex = frameIndex
          IsForeground = isForeground }

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private NativePoint =
    val mutable X: int
    val mutable Y: int

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private XInputGamepad =
    val mutable Buttons: uint16
    val mutable LeftTrigger: byte
    val mutable RightTrigger: byte
    val mutable ThumbLX: int16
    val mutable ThumbLY: int16
    val mutable ThumbRX: int16
    val mutable ThumbRY: int16

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private XInputState =
    val mutable PacketNumber: uint32
    val mutable Gamepad: XInputGamepad

module private NativeDeviceInput =
    [<Literal>]
    let ErrorSuccess = 0u

    [<DllImport("user32.dll", ExactSpelling = true)>]
    extern int16 GetAsyncKeyState(int virtualKey)

    [<DllImport("user32.dll", ExactSpelling = true)>]
    extern nativeint GetForegroundWindow()

    [<DllImport("user32.dll", ExactSpelling = true)>]
    extern uint32 GetWindowThreadProcessId(nativeint windowHandle, uint32& processId)

    [<DllImport("kernel32.dll", ExactSpelling = true)>]
    extern uint32 GetCurrentProcessId()

    [<DllImport("user32.dll", ExactSpelling = true)>]
    [<return: MarshalAs(UnmanagedType.Bool)>]
    extern bool GetCursorPos(NativePoint& point)

    [<DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", ExactSpelling = true)>]
    extern uint32 XInputGetState14(uint32 userIndex, XInputState& state)

    [<DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState", ExactSpelling = true)>]
    extern uint32 XInputGetState910(uint32 userIndex, XInputState& state)

    let isKeyDown virtualKey =
        (int (GetAsyncKeyState virtualKey) &&& 0x8000) <> 0

    let isCurrentProcessForeground() =
        let window = GetForegroundWindow()
        if window = 0n then
            false
        else
            let mutable processId = 0u
            GetWindowThreadProcessId(window, &processId) |> ignore
            processId = GetCurrentProcessId()

    let tryGetControllerState index =
        let mutable state = XInputState()
        try
            if XInputGetState14(uint32 index, &state) = ErrorSuccess then
                ValueSome state
            else
                ValueNone
        with
        | :? DllNotFoundException
        | :? EntryPointNotFoundException ->
            try
                if XInputGetState910(uint32 index, &state) = ErrorSuccess then
                    ValueSome state
                else
                    ValueNone
            with
            | :? DllNotFoundException
            | :? EntryPointNotFoundException -> ValueNone

[<Sealed>]
type internal DeviceInputReader() =
    let gate = obj()
    let controllerStates = Array.zeroCreate<XInputState> 4
    let controllerAvailable = Array.zeroCreate<bool> 4
    let mutable capturedFrame = UInt64.MaxValue
    let mutable foreground = false
    let mutable cursorBaseline = false
    let mutable cursorX = 0
    let mutable cursorY = 0
    let mutable cursorDeltaX = 0
    let mutable cursorDeltaY = 0
    let mutable disposed = false

    let throwIfDisposed() =
        if disposed then
            raise (ObjectDisposedException(nameof DeviceInputReader))

    let normalizeThumb (value: int16) deadZone =
        let raw = int value
        let magnitude = abs raw
        if magnitude <= deadZone then
            0.0f
        else
            let maximum = if raw < 0 then 32768 else 32767
            let scaled = single (magnitude - deadZone) / single (maximum - deadZone)
            let clamped = Math.Clamp(scaled, 0.0f, 1.0f)
            if raw < 0 then -clamped else clamped

    let normalizeTrigger (value: byte) =
        let raw = int value
        if raw <= 30 then
            0.0f
        else
            single (raw - 30) / 225.0f

    let mouseVirtualKey code =
        match enum<MouseButton> code with
        | MouseButton.Left -> 0x01
        | MouseButton.Right -> 0x02
        | MouseButton.Middle -> 0x04
        | MouseButton.X1 -> 0x05
        | MouseButton.X2 -> 0x06
        | _ -> 0

    let controllerMask code =
        match enum<ControllerButton> code with
        | ControllerButton.DPadUp -> 0x0001us
        | ControllerButton.DPadDown -> 0x0002us
        | ControllerButton.DPadLeft -> 0x0004us
        | ControllerButton.DPadRight -> 0x0008us
        | ControllerButton.MenuPrimary -> 0x0010us
        | ControllerButton.MenuSecondary -> 0x0020us
        | ControllerButton.LeftStick -> 0x0040us
        | ControllerButton.RightStick -> 0x0080us
        | ControllerButton.LeftShoulder -> 0x0100us
        | ControllerButton.RightShoulder -> 0x0200us
        | ControllerButton.South -> 0x1000us
        | ControllerButton.East -> 0x2000us
        | ControllerButton.West -> 0x4000us
        | ControllerButton.North -> 0x8000us
        | _ -> 0us

    let captureFrame (frame: DeviceInputFrameContext) =
        if capturedFrame <> frame.FrameIndex then
            capturedFrame <- frame.FrameIndex
            foreground <- frame.IsForeground
            cursorDeltaX <- 0
            cursorDeltaY <- 0

            if not foreground then
                cursorBaseline <- false
                Array.Clear(controllerAvailable, 0, controllerAvailable.Length)
            else
                let mutable point = NativePoint()
                if NativeDeviceInput.GetCursorPos(&point) then
                    if cursorBaseline then
                        cursorDeltaX <- point.X - cursorX
                        cursorDeltaY <- point.Y - cursorY
                    cursorX <- point.X
                    cursorY <- point.Y
                    cursorBaseline <- true
                else
                    cursorBaseline <- false

                for index = 0 to controllerStates.Length - 1 do
                    match NativeDeviceInput.tryGetControllerState index with
                    | ValueSome state ->
                        controllerStates[index] <- state
                        controllerAvailable[index] <- true
                    | ValueNone ->
                        controllerStates[index] <- XInputState()
                        controllerAvailable[index] <- false

    let readControllerButton (control: DeviceControl) =
        let mask = controllerMask control.Code
        if mask = 0us then
            ValueNone
        else
            let mutable available = false
            let mutable down = false

            for index = 0 to controllerStates.Length - 1 do
                if controllerAvailable[index] then
                    available <- true
                    down <-
                        down ||
                        ((controllerStates[index].Gamepad.Buttons &&& mask) <> 0us)

            if available then
                ValueSome(struct (down, if down then 1.0f else 0.0f))
            else
                ValueNone

    let readControllerAxis (control: DeviceControl) =
        let mutable available = false
        let mutable selected = 0.0f

        for index = 0 to controllerStates.Length - 1 do
            if controllerAvailable[index] then
                available <- true
                let gamepad = controllerStates[index].Gamepad
                let value =
                    match enum<ControllerAxis> control.Code with
                    | ControllerAxis.LeftStickX -> normalizeThumb gamepad.ThumbLX 7849
                    | ControllerAxis.LeftStickY -> normalizeThumb gamepad.ThumbLY 7849
                    | ControllerAxis.RightStickX -> normalizeThumb gamepad.ThumbRX 8689
                    | ControllerAxis.RightStickY -> normalizeThumb gamepad.ThumbRY 8689
                    | ControllerAxis.LeftTrigger -> normalizeTrigger gamepad.LeftTrigger
                    | ControllerAxis.RightTrigger -> normalizeTrigger gamepad.RightTrigger
                    | _ -> 0.0f

                if abs value > abs selected then
                    selected <- value

        if available then
            ValueSome(struct (abs selected >= 0.5f, selected))
        else
            ValueNone

    member _.Read(
        frame: DeviceInputFrameContext,
        control: DeviceControl) : struct (bool * single) voption =

        lock gate (fun () ->
            throwIfDisposed()
            captureFrame frame

            if not foreground then
                ValueNone
            else
                match control.DeviceKind, control.ControlKind with
                | InputDeviceKind.Keyboard, InputControlKind.Button ->
                    let down = NativeDeviceInput.isKeyDown control.Code
                    ValueSome(struct (down, if down then 1.0f else 0.0f))

                | InputDeviceKind.Mouse, InputControlKind.Button ->
                    let virtualKey = mouseVirtualKey control.Code
                    if virtualKey = 0 then
                        ValueNone
                    else
                        let down = NativeDeviceInput.isKeyDown virtualKey
                        ValueSome(struct (down, if down then 1.0f else 0.0f))

                | InputDeviceKind.Mouse, InputControlKind.Axis ->
                    let value =
                        match enum<MouseAxis> control.Code with
                        | MouseAxis.X -> single cursorDeltaX
                        | MouseAxis.Y -> single cursorDeltaY
                        | _ -> 0.0f
                    ValueSome(struct (value <> 0.0f, value))

                | InputDeviceKind.Controller, InputControlKind.Button ->
                    readControllerButton control

                | InputDeviceKind.Controller, InputControlKind.Axis ->
                    readControllerAxis control

                | _ -> ValueNone)

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    disposed <- true
                    Array.Clear(controllerAvailable, 0, controllerAvailable.Length))

    static member IsCurrentProcessForeground() =
        NativeDeviceInput.isCurrentProcessForeground()
