namespace Script4Reload.Source

open System
open System.IO
open System.Text

[<RequireQualifiedAccess>]
type ReloadMode =
    | Manual
    | Synchronized

type Script4ReloadConfig =
    {
        Mode: ReloadMode
        ReloadInputs: string
    }

module internal ReloadConfiguration =
    [<Literal>]
    let private FileName = "Script4Reload.ini"

    let private utf8 = UTF8Encoding(false)

    let private defaults =
        {
            Mode = ReloadMode.Manual
            ReloadInputs = "F11"
        }

    let private formatMode = function
        | ReloadMode.Manual -> "Manual"
        | ReloadMode.Synchronized -> "Synchronized"

    let private parseMode (value: string) =
        if value.Equals("Manual", StringComparison.OrdinalIgnoreCase) then
            Some ReloadMode.Manual
        elif value.Equals("Synchronized", StringComparison.OrdinalIgnoreCase) then
            Some ReloadMode.Synchronized
        else
            None

    let private serialize (config: Script4ReloadConfig) =
        String.Join(
            Environment.NewLine,
            [|
                "[Reload]"
                "; Manual or Synchronized. Take your pick."
                $"Mode={formatMode config.Mode}"
                ""
                "; Usable only on Manual mode."
                "; Visit docs.fivem.net/docs/game-references/controls for GTA game input; every other token than INPUT_* selects device input."
                $"ReloadInputs={config.ReloadInputs}"
            |])

    let private writeAtomic (path: string) (content: string) =
        let destination = Path.GetFullPath path
        let directory =
            match Path.GetDirectoryName destination with
            | null
            | "" -> invalidOp "Script4Reload.ini has no parent directory."
            | value -> value

        Directory.CreateDirectory directory |> ignore
        let temporary =
            Path.Combine(
                directory,
                Path.GetFileName(destination) + "." +
                Guid.NewGuid().ToString("N") + ".tmp")

        try
            do
                use stream =
                    new FileStream(
                        temporary,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.ReadWrite ||| FileShare.Delete,
                        4096,
                        FileOptions.WriteThrough)

                use writer = new StreamWriter(stream, utf8, 4096, true)
                writer.Write(content)
                writer.Flush()
                stream.Flush true

            File.Move(temporary, destination, true)
        finally
            if File.Exists temporary then
                try
                    File.Delete temporary
                with
                | :? IOException
                | :? UnauthorizedAccessException -> ()

    let loadOrCreate runtimeRoot =
        let root = Path.GetFullPath runtimeRoot
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, FileName)

        if not (File.Exists path) then
            writeAtomic path (serialize defaults)
            defaults, Some(FileName + " was created with defaults.")
        else
            try
                let mutable section = String.Empty
                let mutable modeText: string option = None
                let mutable inputsText: string option = None
                let mutable repaired = false

                for originalLine in File.ReadLines path do
                    let line = originalLine.Trim()
                    if line.Length = 0 ||
                       line.StartsWith(";", StringComparison.Ordinal) ||
                       line.StartsWith("#", StringComparison.Ordinal) then
                        ()
                    elif line.StartsWith("[", StringComparison.Ordinal) &&
                         line.EndsWith("]", StringComparison.Ordinal) then
                        section <- line[1 .. line.Length - 2].Trim()
                        if not (section.Equals("Reload", StringComparison.OrdinalIgnoreCase)) then
                            repaired <- true
                    else
                        let separator = line.IndexOf('=')
                        if separator <= 0 ||
                           not (section.Equals("Reload", StringComparison.OrdinalIgnoreCase)) then
                            repaired <- true
                        else
                            let key = line[.. separator - 1].Trim()
                            let value = line[separator + 1 ..].Trim()
                            if key.Equals("Mode", StringComparison.OrdinalIgnoreCase) then
                                if modeText.IsSome then repaired <- true
                                modeText <- Some value
                            elif key.Equals("ReloadInputs", StringComparison.OrdinalIgnoreCase) then
                                if inputsText.IsSome then repaired <- true
                                inputsText <- Some value
                            else
                                repaired <- true

                let mode =
                    match modeText |> Option.bind parseMode with
                    | Some value -> value
                    | None ->
                        repaired <- true
                        defaults.Mode

                let reloadInputs =
                    match inputsText with
                    | Some value when not (String.IsNullOrWhiteSpace value) -> value.Trim()
                    | _ ->
                        repaired <- true
                        defaults.ReloadInputs

                let config =
                    {
                        Mode = mode
                        ReloadInputs = reloadInputs
                    }

                let canonical = serialize config
                if repaired ||
                   not (String.Equals(
                       File.ReadAllText(path),
                       canonical,
                       StringComparison.Ordinal)) then
                    writeAtomic path canonical
                    config, Some(FileName + " was normalized.")
                else
                    config, None
            with exceptionValue ->
                writeAtomic path (serialize defaults)
                defaults,
                Some(
                    FileName +
                    " was invalid and defaults were restored: " +
                    exceptionValue.Message)