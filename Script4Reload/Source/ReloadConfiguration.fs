namespace Script4Reload.Source

open System
open System.IO
open CommunityScriptHookVDotNetCore.Source

[<RequireQualifiedAccess>]
type ReloadMode =
    | Manual
    | Synchronized

type Script4ReloadConfig =
    {
        Mode: ReloadMode
        ReloadInput: string
        LogEnabled: bool
    }

module internal ReloadConfiguration =
    [<Literal>]
    let private FileName = "Script4Reload.ini"

    let private normalizeMode (value: string) =
        if value.Equals("Manual", StringComparison.OrdinalIgnoreCase) then
            "Manual"
        elif value.Equals("Synchronized", StringComparison.OrdinalIgnoreCase) then
            "Synchronized"
        else
            null

    let private normalizeInput (value: string) =
        let normalized = value.Trim()
        if String.IsNullOrWhiteSpace normalized then null else normalized

    let loadOrCreate extensionsDirectory =
        let root = Path.GetFullPath extensionsDirectory
        Directory.CreateDirectory root |> ignore
        let loaded =
            RuntimeIni.LoadOrCreate(
                Path.Combine(root, FileName),
                [|
                    RuntimeIniEntry(
                        "Mode",
                        "Manual",
                        Func<string, string>(fun value -> normalizeMode value),
                        "Manual or Synchronized")
                    RuntimeIniEntry(
                        "ReloadInput",
                        "F11",
                        Func<string, string>(fun value -> normalizeInput value),
                        "Assign your key to reload extensions & mods. Only usable on Manual Mode.")
                    RuntimeIniEntry(
                        "LogEnabled",
                        "true",
                        Func<string, string>(fun value -> RuntimeIni.NormalizeBoolean(value)),
                        "If you wish to toggle your logs.")
                |])

        let mode =
            if loaded.Get("Mode").Equals("Synchronized", StringComparison.OrdinalIgnoreCase) then
                ReloadMode.Synchronized
            else
                ReloadMode.Manual

        {
            Mode = mode
            ReloadInput = loaded.Get("ReloadInput")
            LogEnabled = loaded.GetBoolean("LogEnabled")
        }, Option.ofObj loaded.Diagnostic