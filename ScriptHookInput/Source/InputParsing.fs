namespace ScriptHookInput.Source

open System
open System.Collections.Generic

module private BindingSyntax =
    let private defaultGameControl (control: GameControlId) =
        let name = control.ToString()
        if name.StartsWith("INPUT_FRONTEND_", StringComparison.Ordinal) ||
           name.StartsWith("INPUT_CURSOR_", StringComparison.Ordinal) then
            GameControl.Frontend(control)
        else
            GameControl.Player(control)

    let private parseGameControl (token: string) =
        let mutable control = Unchecked.defaultof<GameControlId>
        if Enum.TryParse<GameControlId>(token, true, &control) &&
           Enum.IsDefined(typeof<GameControlId>, control) then
            GameControlBinding(defaultGameControl control) :> InputBinding
        else
            raise (
                FormatException(
                    $"Unknown GTA game input '{token}'. INPUT_* names must match the SHI game-control catalog."))

    let private tryParseKeyboard (token: string) =
        let normalized = token.Trim()
        let alias =
            match normalized.ToUpperInvariant() with
            | "ESC" -> "Escape"
            | "RETURN" -> "Enter"
            | "CTRL" -> "Control"
            | value when value.Length = 1 && Char.IsDigit(value[0]) -> "D" + value
            | _ -> normalized

        let mutable key = Unchecked.defaultof<KeyboardKey>
        if Enum.TryParse<KeyboardKey>(alias, true, &key) &&
           Enum.IsDefined(typeof<KeyboardKey>, key) then
            ValueSome(DeviceControlBinding(DeviceControl.Keyboard(key)) :> InputBinding)
        else
            ValueNone

    let private tryParseMouse (token: string) =
        match token.ToUpperInvariant() with
        | "MOUSE_LEFT" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.Left)) :> InputBinding)
        | "MOUSE_RIGHT" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.Right)) :> InputBinding)
        | "MOUSE_MIDDLE" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.Middle)) :> InputBinding)
        | "MOUSE_X1" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.X1)) :> InputBinding)
        | "MOUSE_X2" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.X2)) :> InputBinding)
        | "MOUSE_AXIS_X" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseAxis.X)) :> InputBinding)
        | "MOUSE_AXIS_Y" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseAxis.Y)) :> InputBinding)
        | _ -> ValueNone

    let private tryParseController (token: string) =
        match token.ToUpperInvariant() with
        | "PAD_DPAD_UP" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadUp)) :> InputBinding)
        | "PAD_DPAD_DOWN" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadDown)) :> InputBinding)
        | "PAD_DPAD_LEFT" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadLeft)) :> InputBinding)
        | "PAD_DPAD_RIGHT" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadRight)) :> InputBinding)
        | "PAD_MENU_PRIMARY" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.MenuPrimary)) :> InputBinding)
        | "PAD_MENU_SECONDARY" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.MenuSecondary)) :> InputBinding)
        | "PAD_LEFT_STICK" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.LeftStick)) :> InputBinding)
        | "PAD_RIGHT_STICK" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.RightStick)) :> InputBinding)
        | "PAD_LEFT_SHOULDER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.LeftShoulder)) :> InputBinding)
        | "PAD_RIGHT_SHOULDER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.RightShoulder)) :> InputBinding)
        | "PAD_SOUTH" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.South)) :> InputBinding)
        | "PAD_EAST" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.East)) :> InputBinding)
        | "PAD_WEST" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.West)) :> InputBinding)
        | "PAD_NORTH" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.North)) :> InputBinding)
        | "PAD_LEFT_STICK_X" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.LeftStickX)) :> InputBinding)
        | "PAD_LEFT_STICK_Y" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.LeftStickY)) :> InputBinding)
        | "PAD_RIGHT_STICK_X" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.RightStickX)) :> InputBinding)
        | "PAD_RIGHT_STICK_Y" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.RightStickY)) :> InputBinding)
        | "PAD_LEFT_TRIGGER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.LeftTrigger)) :> InputBinding)
        | "PAD_RIGHT_TRIGGER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.RightTrigger)) :> InputBinding)
        | _ -> ValueNone

    let parse (text: string) =
        if String.IsNullOrWhiteSpace text then
            raise (FormatException("An input token cannot be empty."))

        let token = text.Trim()
        if token.StartsWith("INPUT_", StringComparison.OrdinalIgnoreCase) then
            parseGameControl token
        else
            match tryParseKeyboard token with
            | ValueSome binding -> binding
            | ValueNone ->
                match tryParseMouse token with
                | ValueSome binding -> binding
                | ValueNone ->
                    match tryParseController token with
                    | ValueSome binding -> binding
                    | ValueNone ->
                        raise (
                            FormatException(
                                $"Unknown device input '{token}'. Use a symbolic keyboard name, MOUSE_*, PAD_*, or an INPUT_* game control."))

    let format (binding: InputBinding) =
        ArgumentNullException.ThrowIfNull(binding)
        match binding with
        | :? GameControlBinding as game ->
            enum<GameControlId>(game.Control.Index).ToString()
        | :? DeviceControlBinding as device ->
            let control = device.Control
            match control.DeviceKind, control.ControlKind with
            | InputDeviceKind.Keyboard, InputControlKind.Button ->
                let key = enum<KeyboardKey>(control.Code)
                match key with
                | KeyboardKey.D0 -> "0"
                | KeyboardKey.D1 -> "1"
                | KeyboardKey.D2 -> "2"
                | KeyboardKey.D3 -> "3"
                | KeyboardKey.D4 -> "4"
                | KeyboardKey.D5 -> "5"
                | KeyboardKey.D6 -> "6"
                | KeyboardKey.D7 -> "7"
                | KeyboardKey.D8 -> "8"
                | KeyboardKey.D9 -> "9"
                | _ -> key.ToString()
            | InputDeviceKind.Mouse, InputControlKind.Button ->
                match enum<MouseButton>(control.Code) with
                | MouseButton.Left -> "MOUSE_LEFT"
                | MouseButton.Right -> "MOUSE_RIGHT"
                | MouseButton.Middle -> "MOUSE_MIDDLE"
                | MouseButton.X1 -> "MOUSE_X1"
                | MouseButton.X2 -> "MOUSE_X2"
                | value -> raise (ArgumentOutOfRangeException(nameof binding, value, "Unsupported mouse button."))
            | InputDeviceKind.Mouse, InputControlKind.Axis ->
                match enum<MouseAxis>(control.Code) with
                | MouseAxis.X -> "MOUSE_AXIS_X"
                | MouseAxis.Y -> "MOUSE_AXIS_Y"
                | value -> raise (ArgumentOutOfRangeException(nameof binding, value, "Unsupported mouse axis."))
            | InputDeviceKind.Controller, InputControlKind.Button ->
                "PAD_" +
                (match enum<ControllerButton>(control.Code) with
                | ControllerButton.DPadUp -> "DPAD_UP"
                | ControllerButton.DPadDown -> "DPAD_DOWN"
                | ControllerButton.DPadLeft -> "DPAD_LEFT"
                | ControllerButton.DPadRight -> "DPAD_RIGHT"
                | ControllerButton.MenuPrimary -> "MENU_PRIMARY"
                | ControllerButton.MenuSecondary -> "MENU_SECONDARY"
                | ControllerButton.LeftStick -> "LEFT_STICK"
                | ControllerButton.RightStick -> "RIGHT_STICK"
                | ControllerButton.LeftShoulder -> "LEFT_SHOULDER"
                | ControllerButton.RightShoulder -> "RIGHT_SHOULDER"
                | ControllerButton.South -> "SOUTH"
                | ControllerButton.East -> "EAST"
                | ControllerButton.West -> "WEST"
                | ControllerButton.North -> "NORTH"
                | value -> raise (ArgumentOutOfRangeException(nameof binding, value, "Unsupported controller button.")))
            | InputDeviceKind.Controller, InputControlKind.Axis ->
                "PAD_" +
                (match enum<ControllerAxis>(control.Code) with
                | ControllerAxis.LeftStickX -> "LEFT_STICK_X"
                | ControllerAxis.LeftStickY -> "LEFT_STICK_Y"
                | ControllerAxis.RightStickX -> "RIGHT_STICK_X"
                | ControllerAxis.RightStickY -> "RIGHT_STICK_Y"
                | ControllerAxis.LeftTrigger -> "LEFT_TRIGGER"
                | ControllerAxis.RightTrigger -> "RIGHT_TRIGGER"
                | value -> raise (ArgumentOutOfRangeException(nameof binding, value, "Unsupported controller axis.")))
            | _ ->
                raise (ArgumentException("The device binding contains an unsupported control.", nameof binding))
        | _ ->
            raise (ArgumentException("The input binding type is not supported.", nameof binding))

[<AbstractClass; Sealed>]
type InputBindingCodec private () =
    static member Parse(text: string) = BindingSyntax.parse text

    static member ParseMany(text: string) : IReadOnlyList<InputBinding> =
        if String.IsNullOrWhiteSpace text then
            raise (FormatException("An input binding list cannot be empty."))

        let tokens = text.Split(',', StringSplitOptions.TrimEntries)
        if tokens |> Array.exists String.IsNullOrWhiteSpace then
            raise (FormatException("An input binding list cannot contain an empty comma-separated token."))

        let values = tokens |> Array.map BindingSyntax.parse
        System.Array.AsReadOnly(values) :> IReadOnlyList<InputBinding>

    static member Format(binding: InputBinding) = BindingSyntax.format binding

    static member FormatMany(bindings: IEnumerable<InputBinding>) =
        ArgumentNullException.ThrowIfNull(bindings)
        let values = bindings |> Seq.map BindingSyntax.format |> Seq.toArray
        if Array.isEmpty values then
            invalidArg (nameof bindings) "At least one input binding is required."
        String.Join(", ", values)