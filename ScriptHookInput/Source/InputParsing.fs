namespace ScriptHookInput.Source

open System
open System.Collections.Generic
open System.Globalization

module private BindingSyntax =
    let private defaultGameControl (control: GameControlId) =
        let name = control.ToString()
        if name.StartsWith("INPUT_FRONTEND_", StringComparison.Ordinal) ||
           name.StartsWith("INPUT_CURSOR_", StringComparison.Ordinal) then
            GameControl.Frontend(control)
        else
            GameControl.Player(control)

    let private parseGameControl (token: string) =
        let parts = token.Split('@')
        let invalid() =
            raise (
                FormatException(
                    $"Invalid GTA game input '{token}'. Use INPUT_NAME or INPUT_NAME@group:mode."))
        if parts.Length > 2 then invalid()

        let name = parts[0].Trim()
        let mutable named = Unchecked.defaultof<GameControlId>
        let hasName =
            Enum.TryParse<GameControlId>(name, true, &named) &&
            Enum.IsDefined(typeof<GameControlId>, named)
        let index =
            if hasName then
                int named
            elif name.StartsWith("INPUT_", StringComparison.OrdinalIgnoreCase) then
                let mutable number = 0
                if Int32.TryParse(
                    name.Substring("INPUT_".Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    &number) then
                    number
                else
                    invalid()
            else
                invalid()

        let defaultGroup =
            if hasName then (defaultGameControl named).Group
            else int GameControlGroup.PLAYER_CONTROL
        let group, mode =
            if parts.Length = 1 then
                defaultGroup, GameControlReadMode.Enabled
            else
                let options = parts[1].Split(':')
                if options.Length <> 2 then invalid()
                let mutable groupValue = 0
                let mutable modeValue = Unchecked.defaultof<GameControlReadMode>
                if not (Int32.TryParse(
                    options[0].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, &groupValue) &&
                    Enum.TryParse<GameControlReadMode>(
                        options[1].Trim(), true, &modeValue) &&
                    Enum.IsDefined(typeof<GameControlReadMode>, modeValue)) then
                    invalid()
                groupValue, modeValue

        GameControlBinding(GameControl(group, index), mode) :> InputBinding

    let private tryParseVirtualKeyNumber (value: string) =
        let mutable code = 0
        let parsed =
            if value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
               value.Length > 2 then
                Int32.TryParse(
                    value.Substring(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    &code)
            else
                Int32.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    &code)

        if parsed && code >= 1 && code <= 255 then ValueSome code
        else ValueNone

    let private tryParseWin32Symbol (name: string) =
        match name with
        | "BACK" -> ValueSome 0x08
        | "SHIFTKEY" -> ValueSome 0x10
        | "CANCEL" -> ValueSome 0x03
        | "CLEAR" -> ValueSome 0x0C
        | "RETURN" -> ValueSome 0x0D
        | "CTRL" | "CONTROL" | "CONTROLKEY" -> ValueSome 0x11
        | "MENU" -> ValueSome 0x12
        | "CAPITAL" -> ValueSome 0x14
        | "KANA" | "HANGUL" | "HANGUEL" -> ValueSome 0x15
        | "IME_ON" -> ValueSome 0x16
        | "JUNJA" -> ValueSome 0x17
        | "FINAL" -> ValueSome 0x18
        | "HANJA" | "KANJI" -> ValueSome 0x19
        | "IME_OFF" -> ValueSome 0x1A
        | "ESC" -> ValueSome 0x1B
        | "CONVERT" -> ValueSome 0x1C
        | "NONCONVERT" -> ValueSome 0x1D
        | "ACCEPT" -> ValueSome 0x1E
        | "MODECHANGE" -> ValueSome 0x1F
        | "PRIOR" -> ValueSome 0x21
        | "NEXT" -> ValueSome 0x22
        | "SELECT" -> ValueSome 0x29
        | "PRINT" -> ValueSome 0x2A
        | "EXECUTE" -> ValueSome 0x2B
        | "SNAPSHOT" | "PRINTSCREEN" -> ValueSome 0x2C
        | "HELP" -> ValueSome 0x2F
        | "LWIN" -> ValueSome 0x5B
        | "RWIN" -> ValueSome 0x5C
        | "APPS" -> ValueSome 0x5D
        | "SLEEP" -> ValueSome 0x5F
        | "SEPARATOR" -> ValueSome 0x6C
        | "NUMLOCK" -> ValueSome 0x90
        | "SCROLL" -> ValueSome 0x91
        | "LSHIFT" | "LSHIFTKEY" -> ValueSome 0xA0
        | "RSHIFT" | "RSHIFTKEY" -> ValueSome 0xA1
        | "LCONTROL" | "LCONTROLKEY" -> ValueSome 0xA2
        | "RCONTROL" | "RCONTROLKEY" -> ValueSome 0xA3
        | "LMENU" -> ValueSome 0xA4
        | "RMENU" -> ValueSome 0xA5
        | "BROWSER_BACK" -> ValueSome 0xA6
        | "BROWSER_FORWARD" -> ValueSome 0xA7
        | "BROWSER_REFRESH" -> ValueSome 0xA8
        | "BROWSER_STOP" -> ValueSome 0xA9
        | "BROWSER_SEARCH" -> ValueSome 0xAA
        | "BROWSER_FAVORITES" -> ValueSome 0xAB
        | "BROWSER_HOME" -> ValueSome 0xAC
        | "VOLUME_MUTE" -> ValueSome 0xAD
        | "VOLUME_DOWN" -> ValueSome 0xAE
        | "VOLUME_UP" -> ValueSome 0xAF
        | "MEDIA_NEXT_TRACK" -> ValueSome 0xB0
        | "MEDIA_PREV_TRACK" -> ValueSome 0xB1
        | "MEDIA_STOP" -> ValueSome 0xB2
        | "MEDIA_PLAY_PAUSE" -> ValueSome 0xB3
        | "LAUNCH_MAIL" -> ValueSome 0xB4
        | "LAUNCH_MEDIA_SELECT" -> ValueSome 0xB5
        | "LAUNCH_APP1" -> ValueSome 0xB6
        | "LAUNCH_APP2" -> ValueSome 0xB7
        | "OEM_1" | "OEM1" | "OEMSEMICOLON" -> ValueSome 0xBA
        | "OEM_PLUS" | "OEMPLUS" -> ValueSome 0xBB
        | "OEM_COMMA" | "OEMCOMMA" -> ValueSome 0xBC
        | "OEM_MINUS" | "OEMMINUS" -> ValueSome 0xBD
        | "OEM_PERIOD" | "OEMPERIOD" -> ValueSome 0xBE
        | "OEM_2" | "OEM2" | "OEMQUESTION" -> ValueSome 0xBF
        | "OEM_3" | "OEM3" | "OEMTILDE" -> ValueSome 0xC0
        | "OEM_4" | "OEM4" | "OEMOPENBRACKETS" -> ValueSome 0xDB
        | "OEM_5" | "OEM5" | "OEMPIPE" -> ValueSome 0xDC
        | "OEM_6" | "OEM6" | "OEMCLOSEBRACKETS" -> ValueSome 0xDD
        | "OEM_7" | "OEM7" | "OEMQUOTES" -> ValueSome 0xDE
        | "OEM_8" | "OEM8" -> ValueSome 0xDF
        | "OEM_102" | "OEM102" | "OEMBACKSLASH" -> ValueSome 0xE2
        | "PROCESSKEY" -> ValueSome 0xE5
        | "PACKET" -> ValueSome 0xE7
        | "ATTN" -> ValueSome 0xF6
        | "CRSEL" -> ValueSome 0xF7
        | "EXSEL" -> ValueSome 0xF8
        | "EREOF" -> ValueSome 0xF9
        | "PLAY" -> ValueSome 0xFA
        | "ZOOM" -> ValueSome 0xFB
        | "NONAME" -> ValueSome 0xFC
        | "PA1" -> ValueSome 0xFD
        | "OEM_CLEAR" -> ValueSome 0xFE
        | _ -> ValueNone

    let private tryParseKeyboard (token: string) =
        let text = token.Trim()
        let withoutFormsPrefix =
            if text.StartsWith(
                "System.Windows.Forms.Keys.",
                StringComparison.OrdinalIgnoreCase) then
                text.Substring("System.Windows.Forms.Keys.".Length)
            elif text.StartsWith("Keys.", StringComparison.OrdinalIgnoreCase) then
                text.Substring("Keys.".Length)
            else
                text
        let name =
            if withoutFormsPrefix.StartsWith("VK_", StringComparison.OrdinalIgnoreCase) then
                withoutFormsPrefix.Substring(3)
            else
                withoutFormsPrefix
        let alias =
            match name.ToUpperInvariant() with
            | "ESC" -> "Escape"
            | "RETURN" -> "Enter"
            | "CTRL" -> "Control"
            | value when value.Length = 1 && value[0] >= '0' && value[0] <= '9' ->
                "D" + value
            | _ -> name

        let mutable key = Unchecked.defaultof<KeyboardKey>
        let parsed =
            if alias.Contains(',') then
                ValueNone
            else
                match tryParseVirtualKeyNumber alias with
                | ValueSome code -> ValueSome code
                | ValueNone when Enum.TryParse<KeyboardKey>(alias, true, &key) &&
                                 Enum.IsDefined(typeof<KeyboardKey>, key) ->
                    ValueSome(int key)
                | ValueNone -> tryParseWin32Symbol (alias.ToUpperInvariant())

        match parsed with
        | ValueSome code when code >= 1 && code <= 255 ->
            ValueSome(DeviceControlBinding(DeviceControl.Keyboard(code)) :> InputBinding)
        | _ -> ValueNone

    let private tryParseMouse (token: string) =
        match token.ToUpperInvariant() with
        | "MOUSE_LEFT" | "MOUSE1" | "MOUSE_BUTTON1" | "MOUSEBUTTONS.LEFT" | "KEYS.LBUTTON" | "SYSTEM.WINDOWS.FORMS.KEYS.LBUTTON"
        | "LBUTTON" | "VK_LBUTTON" ->
            ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.Left)) :> InputBinding)
        | "MOUSE_RIGHT" | "MOUSE2" | "MOUSE_BUTTON2" | "MOUSEBUTTONS.RIGHT" | "KEYS.RBUTTON" | "SYSTEM.WINDOWS.FORMS.KEYS.RBUTTON"
        | "RBUTTON" | "VK_RBUTTON" ->
            ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.Right)) :> InputBinding)
        | "MOUSE_MIDDLE" | "MOUSE3" | "MOUSE_BUTTON3" | "MOUSEBUTTONS.MIDDLE" | "KEYS.MBUTTON" | "SYSTEM.WINDOWS.FORMS.KEYS.MBUTTON"
        | "MBUTTON" | "VK_MBUTTON" ->
            ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.Middle)) :> InputBinding)
        | "MOUSE_X1" | "MOUSE4" | "MOUSE_BUTTON4" | "MOUSEBUTTONS.XBUTTON1" | "KEYS.XBUTTON1" | "SYSTEM.WINDOWS.FORMS.KEYS.XBUTTON1"
        | "XBUTTON1" | "VK_XBUTTON1" ->
            ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.X1)) :> InputBinding)
        | "MOUSE_X2" | "MOUSE5" | "MOUSE_BUTTON5" | "MOUSEBUTTONS.XBUTTON2" | "KEYS.XBUTTON2" | "SYSTEM.WINDOWS.FORMS.KEYS.XBUTTON2"
        | "XBUTTON2" | "VK_XBUTTON2" ->
            ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseButton.X2)) :> InputBinding)
        | "MOUSE_AXIS_X" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseAxis.X)) :> InputBinding)
        | "MOUSE_AXIS_Y" -> ValueSome(DeviceControlBinding(DeviceControl.Mouse(MouseAxis.Y)) :> InputBinding)
        | _ -> ValueNone

    let private tryParseController (token: string) =
        match token.ToUpperInvariant() with
        | "PAD_DPAD_UP" | "XINPUT_GAMEPAD_DPAD_UP" | "VK_GAMEPAD_DPAD_UP" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadUp)) :> InputBinding)
        | "PAD_DPAD_DOWN" | "XINPUT_GAMEPAD_DPAD_DOWN" | "VK_GAMEPAD_DPAD_DOWN" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadDown)) :> InputBinding)
        | "PAD_DPAD_LEFT" | "XINPUT_GAMEPAD_DPAD_LEFT" | "VK_GAMEPAD_DPAD_LEFT" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadLeft)) :> InputBinding)
        | "PAD_DPAD_RIGHT" | "XINPUT_GAMEPAD_DPAD_RIGHT" | "VK_GAMEPAD_DPAD_RIGHT" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.DPadRight)) :> InputBinding)
        | "PAD_MENU_PRIMARY" | "PAD_START" | "GAMEPAD_START" | "XINPUT_GAMEPAD_START" | "VK_GAMEPAD_MENU" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.MenuPrimary)) :> InputBinding)
        | "PAD_MENU_SECONDARY" | "PAD_BACK" | "PAD_VIEW" | "GAMEPAD_BACK" | "XINPUT_GAMEPAD_BACK" | "VK_GAMEPAD_VIEW" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.MenuSecondary)) :> InputBinding)
        | "PAD_LEFT_STICK" | "PAD_L3" | "XINPUT_GAMEPAD_LEFT_THUMB" | "VK_GAMEPAD_LEFT_THUMBSTICK_BUTTON" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.LeftStick)) :> InputBinding)
        | "PAD_RIGHT_STICK" | "PAD_R3" | "XINPUT_GAMEPAD_RIGHT_THUMB" | "VK_GAMEPAD_RIGHT_THUMBSTICK_BUTTON" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.RightStick)) :> InputBinding)
        | "PAD_LEFT_SHOULDER" | "PAD_LB" | "XINPUT_GAMEPAD_LEFT_SHOULDER" | "VK_GAMEPAD_LEFT_SHOULDER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.LeftShoulder)) :> InputBinding)
        | "PAD_RIGHT_SHOULDER" | "PAD_RB" | "XINPUT_GAMEPAD_RIGHT_SHOULDER" | "VK_GAMEPAD_RIGHT_SHOULDER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.RightShoulder)) :> InputBinding)
        | "PAD_SOUTH" | "PAD_A" | "GAMEPADA" | "GAMEPAD_A" | "XINPUT_GAMEPAD_A" | "VK_GAMEPAD_A" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.South)) :> InputBinding)
        | "PAD_EAST" | "PAD_B" | "GAMEPADB" | "GAMEPAD_B" | "XINPUT_GAMEPAD_B" | "VK_GAMEPAD_B" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.East)) :> InputBinding)
        | "PAD_WEST" | "PAD_X" | "GAMEPADX" | "GAMEPAD_X" | "XINPUT_GAMEPAD_X" | "VK_GAMEPAD_X" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.West)) :> InputBinding)
        | "PAD_NORTH" | "PAD_Y" | "GAMEPADY" | "GAMEPAD_Y" | "XINPUT_GAMEPAD_Y" | "VK_GAMEPAD_Y" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerButton.North)) :> InputBinding)
        | "PAD_LEFT_STICK_X" | "PAD_LX" | "GAMEPAD_LEFT_THUMBSTICK_X" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.LeftStickX)) :> InputBinding)
        | "PAD_LEFT_STICK_Y" | "PAD_LY" | "GAMEPAD_LEFT_THUMBSTICK_Y" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.LeftStickY)) :> InputBinding)
        | "PAD_RIGHT_STICK_X" | "PAD_RX" | "GAMEPAD_RIGHT_THUMBSTICK_X" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.RightStickX)) :> InputBinding)
        | "PAD_RIGHT_STICK_Y" | "PAD_RY" | "GAMEPAD_RIGHT_THUMBSTICK_Y" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.RightStickY)) :> InputBinding)
        | "PAD_LEFT_TRIGGER" | "PAD_LT" | "GAMEPAD_LEFT_TRIGGER" | "XINPUT_GAMEPAD_LEFT_TRIGGER" | "VK_GAMEPAD_LEFT_TRIGGER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.LeftTrigger)) :> InputBinding)
        | "PAD_RIGHT_TRIGGER" | "PAD_RT" | "GAMEPAD_RIGHT_TRIGGER" | "XINPUT_GAMEPAD_RIGHT_TRIGGER" | "VK_GAMEPAD_RIGHT_TRIGGER" -> ValueSome(DeviceControlBinding(DeviceControl.Controller(ControllerAxis.RightTrigger)) :> InputBinding)
        | _ -> ValueNone

    let parse (text: string) =
        if String.IsNullOrWhiteSpace text then
            raise (FormatException("An input token cannot be empty."))

        let token = text.Trim()
        if token.StartsWith("INPUT_", StringComparison.OrdinalIgnoreCase) then
            parseGameControl token
        else
            match tryParseMouse token with
            | ValueSome binding -> binding
            | ValueNone ->
                match tryParseController token with
                | ValueSome binding -> binding
                | ValueNone ->
                    match tryParseKeyboard token with
                    | ValueSome binding -> binding
                    | ValueNone ->
                        raise (
                            FormatException(
                                $"Unknown device input '{token}'. Use a symbolic keyboard name, MOUSE_*, PAD_*, or an INPUT_* game control."))

    let format (binding: InputBinding) =
        ArgumentNullException.ThrowIfNull(binding)
        match binding with
        | :? GameControlBinding as game ->
            let id = enum<GameControlId>(game.Control.Index)
            let name =
                if Enum.IsDefined(typeof<GameControlId>, id) then
                    id.ToString()
                else
                    sprintf "INPUT_%d" game.Control.Index
            let defaultGroup =
                if Enum.IsDefined(typeof<GameControlId>, id) then
                    (defaultGameControl id).Group
                else
                    int GameControlGroup.PLAYER_CONTROL
            if game.Control.Group = defaultGroup &&
               game.Mode = GameControlReadMode.Enabled then
                name
            else
                sprintf "%s@%d:%s" name game.Control.Group (game.Mode.ToString())
        | :? DeviceControlBinding as device ->
            let control = device.Control
            match control.DeviceKind, control.ControlKind with
            | InputDeviceKind.Keyboard, InputControlKind.Button ->
                if control.Code < 1 || control.Code > 255 then
                    raise (ArgumentOutOfRangeException(nameof binding, control.Code, "Unsupported virtual-key code."))
                let key = enum<KeyboardKey>(control.Code)
                if not (Enum.IsDefined(typeof<KeyboardKey>, key)) then
                    sprintf "VK_0x%02X" control.Code
                else
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