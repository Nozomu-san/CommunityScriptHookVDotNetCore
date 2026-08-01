namespace Script4Reload.Source

open System
open System.Collections.Generic
open CommunityScriptHookVDotNetCore.Source

type ReloadPlan =
    {
        BinaryReplacementPackages: IReadOnlyList<string>
        ChangedPackageNames: IReadOnlyList<string>
        RestartAllExecutables: bool
        ExpandedByDependencies: bool
    }

    member this.HasBinaryReplacement =
        this.BinaryReplacementPackages.Count <> 0

module internal ReloadPlanning =
    let build
        (
            inventory: IReadOnlyList<ScriptPackageInfo>,
            baseline: ScriptsDirectorySnapshot,
            current: ScriptsDirectorySnapshot
        ) =
        ArgumentNullException.ThrowIfNull(inventory)
        ArgumentNullException.ThrowIfNull(baseline)
        ArgumentNullException.ThrowIfNull(current)

        let changed =
            current.ChangedPackageNames baseline
            |> Seq.toArray

        let requested =
            HashSet<string>(changed, StringComparer.OrdinalIgnoreCase)

        let mutable changedClosure = true
        while changedClosure do
            changedClosure <- false
            for package in inventory do
                if requested.Contains package.Name then
                    for dependency in package.DependencyPackageNames do
                        if requested.Add dependency then
                            changedClosure <- true
                elif package.DependencyPackageNames
                     |> Seq.exists (fun dependency ->
                         requested.Contains dependency) then
                    if requested.Add package.Name then
                        changedClosure <- true

        {
            BinaryReplacementPackages =
                requested
                |> Seq.sortWith (fun left right ->
                    StringComparer.OrdinalIgnoreCase.Compare(left, right))
                |> Seq.toArray
                |> Array.AsReadOnly
            ChangedPackageNames = Array.AsReadOnly changed
            RestartAllExecutables = true
            ExpandedByDependencies = requested.Count > changed.Length
        }