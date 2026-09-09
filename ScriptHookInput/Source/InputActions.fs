namespace ScriptHookInput.Source

open System
open System.Collections.Generic

[<Sealed>]
type InputActionBinding(name: string, inputs: IEnumerable<InputBinding>) =
    let normalizedName =
        if String.IsNullOrWhiteSpace name then
            invalidArg (nameof name) "An input action name cannot be empty."
        name.Trim()

    let captured =
        ArgumentNullException.ThrowIfNull(inputs)
        let values = inputs |> Seq.toArray
        if Array.isEmpty values then
            invalidArg (nameof inputs) "An input action requires at least one binding."
        for value in values do
            ArgumentNullException.ThrowIfNull(value, nameof inputs)
        values

    let readOnlyInputs: IReadOnlyList<InputBinding> =
        System.Array.AsReadOnly(captured)

    member _.Name = normalizedName
    member _.Inputs = readOnlyInputs

    new(name: string, inputText: string) =
        InputActionBinding(name, InputBindingCodec.ParseMany(inputText))

    new(name: string, [<ParamArray>] inputs: InputBinding array) =
        InputActionBinding(name, inputs :> IEnumerable<InputBinding>)

type IInputAction =
    inherit IDisposable
    abstract Name: string
    abstract State: InputState

type IInputActions =
    abstract Create: binding: InputActionBinding -> IInputAction
    abstract Create: name: string * inputs: string -> IInputAction
    abstract CreateSingle: name: string * input: string -> IInputAction

type IScriptHookInput =
    inherit IInputActions
    abstract Frame: InputFrame
    abstract Pointer: PointerState
    abstract Parse: text: string -> InputBinding
    abstract ParseMany: text: string -> IReadOnlyList<InputBinding>