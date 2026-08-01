namespace Script4Reload.Source

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Globalization
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Security.Cryptography
open System.Text
open System.Threading

module private FingerprintHash =
    let compute (stream: Stream) (cancellationToken: CancellationToken) =
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        let buffer = Array.zeroCreate<byte> (64 * 1024)
        let mutable count = stream.Read(buffer, 0, buffer.Length)
        while count <> 0 do
            cancellationToken.ThrowIfCancellationRequested()
            hash.AppendData(buffer, 0, count)
            count <- stream.Read(buffer, 0, buffer.Length)

        cancellationToken.ThrowIfCancellationRequested()
        hash.GetHashAndReset() |> Convert.ToHexString

[<Sealed>]
type AssemblyFingerprint private
    (
        relativePath: string,
        length: int64,
        sha256: string,
        moduleVersionId: Guid
    ) =

    member _.RelativePath = relativePath
    member _.Length = length
    member _.Sha256 = sha256
    member _.ModuleVersionId = moduleVersionId

    static member TryCapture
        (
            scriptsRoot: string,
            path: string,
            cancellationToken: CancellationToken
        ) =
        cancellationToken.ThrowIfCancellationRequested()
        let root = Path.GetFullPath scriptsRoot
        let fullPath = Path.GetFullPath path
        use stream =
            new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite ||| FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan)

        let length = stream.Length
        let hash = FingerprintHash.compute stream cancellationToken
        stream.Position <- 0L
        cancellationToken.ThrowIfCancellationRequested()
        use pe =
            new PEReader(
                stream,
                PEStreamOptions.PrefetchMetadata |||
                PEStreamOptions.LeaveOpen)

        if not pe.HasMetadata then
            None
        else
            let reader = pe.GetMetadataReader()
            let definition = reader.GetModuleDefinition()
            let moduleVersionId = reader.GetGuid definition.Mvid
            Some(
                AssemblyFingerprint(
                    Path.GetRelativePath(root, fullPath),
                    length,
                    hash,
                    moduleVersionId))

[<Sealed>]
type PackageFingerprint private
    (
        packageName: string,
        assembly: AssemblyFingerprint,
        value: string
    ) =

    member _.PackageName = packageName
    member _.Assembly = assembly
    member _.Value = value

    static member TryCapture
        (
            scriptsRoot: string,
            path: string,
            cancellationToken: CancellationToken
        ) =
        let fullPath = Path.GetFullPath path
        match AssemblyFingerprint.TryCapture(
                  scriptsRoot,
                  fullPath,
                  cancellationToken) with
        | None -> None
        | Some assembly ->
            let packageName = Path.GetFileNameWithoutExtension fullPath
            let canonical = StringBuilder()
            canonical.Append(assembly.RelativePath.ToUpperInvariant())
            |> ignore
            canonical.Append('|') |> ignore
            canonical.Append(
                assembly.Length.ToString(CultureInfo.InvariantCulture))
            |> ignore
            canonical.Append('|') |> ignore
            canonical.Append(assembly.Sha256) |> ignore
            canonical.Append('|') |> ignore
            canonical.Append(assembly.ModuleVersionId.ToString("D"))
            |> ignore

            cancellationToken.ThrowIfCancellationRequested()
            let digest =
                canonical.ToString()
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> Convert.ToHexString

            Some(PackageFingerprint(packageName, assembly, digest))

[<Sealed>]
type ScriptsDirectorySnapshot private
    (
        value: string,
        packages: IReadOnlyDictionary<string, PackageFingerprint>
    ) =

    member _.Value = value
    member _.Packages = packages

    member this.ContentEquals(other: ScriptsDirectorySnapshot) =
        not (obj.ReferenceEquals(other, null)) &&
        String.Equals(this.Value, other.Value, StringComparison.Ordinal)

    member this.ChangedPackageNames(other: ScriptsDirectorySnapshot) =
        ArgumentNullException.ThrowIfNull(other)
        let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        for KeyValue(name, package) in this.Packages do
            match other.Packages.TryGetValue name with
            | true, previous when
                String.Equals(
                    package.Value,
                    previous.Value,
                    StringComparison.Ordinal) -> ()
            | _ -> names.Add name |> ignore

        for KeyValue(name, _) in other.Packages do
            if not (this.Packages.ContainsKey name) then
                names.Add name |> ignore

        names
        |> Seq.sortWith (fun left right ->
            StringComparer.OrdinalIgnoreCase.Compare(left, right))
        |> Seq.toArray
        |> Array.AsReadOnly

    static member Capture
        (
            scriptsRoot: string,
            cancellationToken: CancellationToken
        ) =
        try
            cancellationToken.ThrowIfCancellationRequested()
            let root = Path.GetFullPath scriptsRoot
            Directory.CreateDirectory root |> ignore

            let mutable nestedAssembly: string option = None
            for path in Directory.EnumerateFiles(
                            root,
                            "*.dll",
                            SearchOption.AllDirectories) do
                cancellationToken.ThrowIfCancellationRequested()
                if nestedAssembly.IsNone then
                    let parent = Path.GetDirectoryName(Path.GetFullPath path)
                    if not (
                        String.Equals(
                            parent,
                            root,
                            StringComparison.OrdinalIgnoreCase)) then
                        nestedAssembly <- Some path

            match nestedAssembly with
            | Some path ->
                invalidOp(
                    "scripts4 uses a flat managed assembly layout. Move '" +
                    Path.GetRelativePath(root, path) +
                    "' directly into scripts4.")
            | None -> ()

            let packages =
                Dictionary<string, PackageFingerprint>(
                    StringComparer.OrdinalIgnoreCase)

            let packagePaths =
                Directory.EnumerateFiles(
                    root,
                    "*.dll",
                    SearchOption.TopDirectoryOnly)
                |> Seq.sortWith (fun left right ->
                    StringComparer.OrdinalIgnoreCase.Compare(left, right))

            for path in packagePaths do
                cancellationToken.ThrowIfCancellationRequested()
                match PackageFingerprint.TryCapture(
                          root,
                          path,
                          cancellationToken) with
                | Some package ->
                    packages.Add(package.PackageName, package)
                | None -> ()

            let canonical = StringBuilder()
            packages
            |> Seq.sortWith (fun left right ->
                StringComparer.OrdinalIgnoreCase.Compare(
                    left.Key,
                    right.Key))
            |> Seq.iter (fun pair ->
                cancellationToken.ThrowIfCancellationRequested()
                canonical.Append(pair.Key.ToUpperInvariant())
                |> ignore
                canonical.Append('|') |> ignore
                canonical.Append(pair.Value.Value) |> ignore
                canonical.Append('\n') |> ignore)

            cancellationToken.ThrowIfCancellationRequested()
            let digest =
                canonical.ToString()
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> Convert.ToHexString

            Ok(
                ScriptsDirectorySnapshot(
                    digest,
                    ReadOnlyDictionary(packages)))
        with
        | :? OperationCanceledException -> reraise()
        | exceptionValue -> Error exceptionValue.Message