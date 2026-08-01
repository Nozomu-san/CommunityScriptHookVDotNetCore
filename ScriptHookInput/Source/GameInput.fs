namespace ScriptHookInput.Source

open Alloc8orStandardNatives.Source

[<Sealed>]
type internal GameInputReader(nativeServices: IStandardNatives) =
    do
        System.ArgumentNullException.ThrowIfNull(nativeServices)
        nativeServices.GameBuild |> ignore

    member _.Read(
        control: GameControl,
        mode: GameControlReadMode) : struct (bool * single) voption =

        let group = control.Group
        let index = control.Index

        match mode with
        | GameControlReadMode.Enabled ->
            let down = StandardNatives.IS_CONTROL_PRESSED(group, index)
            let value = StandardNatives.GET_CONTROL_NORMAL(group, index)
            ValueSome(struct (down, value))

        | GameControlReadMode.DisabledAware ->
            let enabledDown = StandardNatives.IS_CONTROL_PRESSED(group, index)
            let disabledDown = StandardNatives.IS_DISABLED_CONTROL_PRESSED(group, index)
            let enabledValue = StandardNatives.GET_CONTROL_NORMAL(group, index)
            let disabledValue = StandardNatives.GET_DISABLED_CONTROL_NORMAL(group, index)
            let value =
                if abs disabledValue > abs enabledValue then
                    disabledValue
                else
                    enabledValue
            ValueSome(struct (enabledDown || disabledDown, value))

        | _ -> ValueNone

    member _.IsUsingKeyboardAndMouse() =
        StandardNatives.IS_USING_KEYBOARD_AND_MOUSE(
            int GameControlGroup.FRONTEND_CONTROL)
