namespace Script4Reload.Source

open System
open System.IO
open System.Text

[<RequireQualifiedAccess>]
type ReloadMode =
    | Manual
    | Automatic

type Script4ReloadConfig =
    {
        Mode: ReloadMode
        ReloadInput: string
    }

module internal ReloadConfiguration =
    [<Literal>]
    let private FileName = "Script4Reload.ini"

    let private utf8 = UTF8Encoding(false)

    let private defaults =
        {
            Mode = ReloadMode.Manual
            ReloadInput = "F11"
        }

    let private formatMode = function
        | ReloadMode.Manual -> "Manual"
        | ReloadMode.Automatic -> "Automatic"

    let private parseMode (value: string) =
        if value.Equals("Manual", StringComparison.OrdinalIgnoreCase) then
            Some(ReloadMode.Manual, false)
        elif value.Equals("Automatic", StringComparison.OrdinalIgnoreCase) then
            Some(ReloadMode.Automatic, false)
        elif value.Equals("Synchronized", StringComparison.OrdinalIgnoreCase) then
            Some(ReloadMode.Automatic, true)
        else
            None

    let private serialize (config: Script4ReloadConfig) =
        String.Join(
            Environment.NewLine,
            [|
                "[Reload]"
                "; Manual waits for ReloadInput. Automatic watches scripts4 and does not use input."
                $"Mode={formatMode config.Mode}"
                ""
                "; Used only in Manual mode and kept dormant in Automatic mode."
                "; Visit docs.fivem.net/docs/game-references/controls for GTA game input."
                "; Every other token than INPUT_* selects device input."
                "; Exactly one symbolic input is accepted for this action."
                $"ReloadInput={config.ReloadInput}"
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
                let mutable reloadSections = 0
                let modeValues = ResizeArray<string>()
                let inputValues = ResizeArray<string>()
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
                        if section.Equals("Reload", StringComparison.OrdinalIgnoreCase) then
                            reloadSections <- reloadSections + 1
                            if reloadSections > 1 then
                                repaired <- true
                        else
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
                                modeValues.Add value
                            elif key.Equals("ReloadInput", StringComparison.OrdinalIgnoreCase) then
                                inputValues.Add value
                            else
                                repaired <- true

                let mode =
                    if modeValues.Count = 0 then
                        repaired <- true
                        defaults.Mode
                    else
                        let parsed = ResizeArray<ReloadMode>()
                        let mutable invalid = false
                        for value in modeValues do
                            match parseMode value with
                            | Some(mode, legacyName) ->
                                parsed.Add mode
                                if legacyName then
                                    repaired <- true
                            | None -> invalid <- true

                        if modeValues.Count > 1 then
                            repaired <- true

                        if invalid || parsed.Count = 0 then
                            repaired <- true
                            defaults.Mode
                        else
                            let first = parsed[0]
                            if parsed |> Seq.exists ((<>) first) then
                                repaired <- true
                                defaults.Mode
                            else
                                first

                let reloadInput =
                    if inputValues.Count = 0 then
                        repaired <- true
                        defaults.ReloadInput
                    else
                        let normalized =
                            inputValues
                            |> Seq.map (fun value -> value.Trim())
                            |> Seq.toArray

                        if inputValues.Count > 1 then
                            repaired <- true

                        if normalized |> Array.exists String.IsNullOrWhiteSpace then
                            repaired <- true
                            defaults.ReloadInput
                        else
                            let first = normalized[0]
                            if normalized
                               |> Array.exists (fun value ->
                                   not (value.Equals(first, StringComparison.OrdinalIgnoreCase))) then
                                repaired <- true
                                defaults.ReloadInput
                            else
                                first

                if reloadSections = 0 then
                    repaired <- true

                let config =
                    {
                        Mode = mode
                        ReloadInput = reloadInput
                    }

                if repaired then
                    writeAtomic path (serialize config)
                    config, Some(FileName + " was normalized.")
                else
                    config, None
            with exceptionValue ->
                defaults,
                Some(
                    FileName +
                    " could not be read. Session defaults are in use and the file was left unchanged: " +
                    exceptionValue.Message)