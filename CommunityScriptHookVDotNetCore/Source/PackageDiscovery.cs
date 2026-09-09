using System.Collections.ObjectModel;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace CommunityScriptHookVDotNetCore.Source;

internal sealed record PackageDescriptor(
    string Name,
    ScriptPackageKind Kind,
    string? EntryAssembly,
    IReadOnlyList<string> ScriptTypeNames,
    IReadOnlyDictionary<string, string> AssemblyPathsByName,
    IReadOnlyList<string> ReferencedAssemblyNames,
    IReadOnlyList<string> DependencyPackageNames);

internal sealed record PackageAssemblyImage(
    string Path,
    byte[] Assembly,
    byte[]? Symbols)
{
    public string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(Assembly));
}

internal sealed record CapturedPackageImage(
    PackageDescriptor Descriptor,
    IReadOnlyList<PackageAssemblyImage> Assemblies);

internal sealed record CapturedReloadImage(
    IReadOnlyList<string> TargetPackages,
    IReadOnlyDictionary<string, CapturedPackageImage> Packages);

internal sealed record StagedPackageImage(
    PackageDescriptor Descriptor,
    IReadOnlyList<PackageAssemblyImage> Assemblies);

internal sealed record StagedReloadImage(
    IReadOnlyDictionary<string, StagedPackageImage> Packages,
    IReadOnlyList<PackageDescriptor> Catalog);

internal sealed record CaptureAttempt(
    CapturedReloadImage? Image,
    string? Diagnostic)
{
    public static CaptureAttempt Success(CapturedReloadImage image) =>
        new(image, null);

    public static CaptureAttempt Retry(string diagnostic) =>
        new(null, diagnostic);
}

internal static class PackageImageCapture
{
    private sealed record CaptureFile(
        string Path,
        FileStream Stream);

    public static async Task<CaptureAttempt> CaptureAsync(
        string scriptsDirectory,
        IReadOnlyList<string> packageNames,
        RuntimeLog log,
        CancellationToken cancellationToken)
    {
        List<CaptureFile> opened = [];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = Path.GetFullPath(scriptsDirectory);
            Dictionary<string, (string AssemblyPath, string? SymbolsPath)>
                files = new(StringComparer.OrdinalIgnoreCase);

            foreach (string packageName in packageNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string assemblyPath = GetPackageAssemblyPath(
                    root,
                    packageName);
                if (!File.Exists(assemblyPath))
                {
                    continue;
                }

                string symbolsPath = Path.ChangeExtension(
                    assemblyPath,
                    ".pdb");
                files.Add(
                    packageName,
                    (
                        assemblyPath,
                        File.Exists(symbolsPath)
                            ? Path.GetFullPath(symbolsPath)
                            : null));
            }

            foreach (KeyValuePair<
                         string,
                         (string AssemblyPath, string? SymbolsPath)> pair in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string assemblyPath = pair.Value.AssemblyPath;
                string? symbolsPath = pair.Value.SymbolsPath;
                opened.Add(new(
                    assemblyPath,
                    OpenReadFence(assemblyPath)));

                if (symbolsPath is not null)
                {
                    opened.Add(new(
                        symbolsPath,
                        OpenReadFence(symbolsPath)));
                }
            }

            Dictionary<string, byte[]> bytes =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (CaptureFile file in opened)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes[file.Path] = await ReadAllAsync(
                        file.Stream,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            Dictionary<string, CapturedPackageImage> packages =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<
                         string,
                         (string AssemblyPath, string? SymbolsPath)> pair in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string packageName = pair.Key;
                string assemblyPath = pair.Value.AssemblyPath;
                string? symbolsPath = pair.Value.SymbolsPath;
                PackageAssemblyImage image = new(
                    assemblyPath,
                    bytes[assemblyPath],
                    symbolsPath is not null &&
                        bytes.TryGetValue(
                            symbolsPath,
                            out byte[]? symbols) &&
                        symbols is not null
                            ? symbols
                            : null);
                PackageDescriptor? descriptor =
                    PackageDiscovery.InspectPackageImages(
                        root,
                        Array.AsReadOnly([image]),
                        log) ?? throw new BadImageFormatException(
                        $"Package '{packageName}' does not contain a valid " +
                        "managed replacement generation.");
                if (!descriptor.Name.Equals(
                        packageName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new BadImageFormatException(
                        $"Captured package '{packageName}' declares assembly " +
                        $"identity '{descriptor.Name}'.");
                }

                packages[packageName] = new(
                    descriptor,
                    Array.AsReadOnly([image]));
            }

            return CaptureAttempt.Success(new(
                Array.AsReadOnly(packageNames.ToArray()),
                new ReadOnlyDictionary<string, CapturedPackageImage>(packages)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                BadImageFormatException or
                InvalidDataException or
                ArgumentException)
        {
            return CaptureAttempt.Retry(exception.Message);
        }
        finally
        {
            foreach (CaptureFile file in opened)
            {
                await file.Stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string GetPackageAssemblyPath(
        string scriptsRoot,
        string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName) ||
            packageName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            packageName.Contains(Path.DirectorySeparatorChar) ||
            packageName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException(
                $"Package name '{packageName}' is not a valid flat scripts4 " +
                "assembly identity.");
        }

        string candidate = Path.GetFullPath(
            Path.Combine(scriptsRoot, packageName + ".dll"));
        string? parent = Path.GetDirectoryName(candidate);
        if (parent is null ||
            !parent.Equals(
                scriptsRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileNameWithoutExtension(candidate).Equals(
                packageName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Package name '{packageName}' does not identify a direct " +
                "scripts4 DLL.");
        }
        return candidate;
    }

    private static FileStream OpenReadFence(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Delete,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

    private static async Task<byte[]> ReadAllAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        if (stream.Length > int.MaxValue)
        {
            throw new IOException(
                $"File '{stream.Name}' exceeds the supported in-memory size.");
        }

        byte[] image = GC.AllocateUninitializedArray<byte>(
            checked((int)stream.Length));
        await stream.ReadExactlyAsync(image, cancellationToken)
            .ConfigureAwait(false);
        return image;
    }
}

internal static class PackageDiscovery
{
    private const string RuntimeAssemblyName = "CommunityScriptHookVDotNetCore";
    private const string ScriptNamespace = "CommunityScriptHookVDotNetCore.Source";
    private const string ScriptTypeName = "Script4";
    private const string RawNativeTransportTypeName = "IRawNativeTransport";

    internal static PackageDescriptor? InspectPackageImages(
        string scriptsDirectory,
        IReadOnlyList<PackageAssemblyImage> images,
        RuntimeLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsDirectory);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(log);

        if (images.Count == 0)
        {
            return null;
        }
        if (images.Count != 1)
        {
            throw new BadImageFormatException(
                "A flat scripts4 package generation must contain exactly one " +
                "managed assembly image.");
        }

        string root = Path.GetFullPath(scriptsDirectory);
        PackageAssemblyImage image = images[0];
        ValidateDirectAssemblyPath(root, image.Path);

        AssemblyInspection inspection;
        try
        {
            using MemoryStream stream = new(image.Assembly, writable: false);
            inspection = InspectAssembly(image.Path, stream);
        }
        catch (Exception exception)
        {
            throw new BadImageFormatException(
                $"Assembly '{Path.GetFileName(image.Path)}' could not be " +
                "inspected from its captured image.",
                exception);
        }

        return BuildDescriptor(inspection, log);
    }

    internal static IReadOnlyList<PackageDescriptor> ResolveDependencies(
        IReadOnlyList<PackageDescriptor> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);

        Dictionary<string, PackageDescriptor> byPackageName = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (PackageDescriptor package in packages)
        {
            if (!byPackageName.TryAdd(package.Name, package))
            {
                throw new BadImageFormatException(
                    $"More than one scripts4 assembly declares package " +
                    $"identity '{package.Name}'.");
            }
        }

        Dictionary<string, string> assemblyOwners = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (PackageDescriptor package in packages)
        {
            foreach (string assemblyName in package.AssemblyPathsByName.Keys)
            {
                if (assemblyOwners.TryGetValue(
                        assemblyName,
                        out string? existingOwner) &&
                    !existingOwner.Equals(
                        package.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new BadImageFormatException(
                        $"scripts4 packages '{existingOwner}' and '{package.Name}' " +
                        $"both provide assembly identity '{assemblyName}'.");
                }

                assemblyOwners[assemblyName] = package.Name;
            }
        }

        Dictionary<string, string[]> directDependencies = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (PackageDescriptor package in packages)
        {
            HashSet<string> dependencies = new(
                StringComparer.OrdinalIgnoreCase);
            foreach (string reference in package.ReferencedAssemblyNames)
            {
                if (package.AssemblyPathsByName.ContainsKey(reference) ||
                    !assemblyOwners.TryGetValue(reference, out string? owner) ||
                    owner.Equals(
                        package.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!byPackageName.TryGetValue(
                        owner,
                        out PackageDescriptor? provider))
                {
                    throw new BadImageFormatException(
                        $"Package '{package.Name}' references scripts4 assembly " +
                        $"'{reference}', but its owning package is unavailable.");
                }

                if (provider.Kind != ScriptPackageKind.Library)
                {
                    throw new BadImageFormatException(
                        $"Package '{package.Name}' references executable package " +
                        $"'{provider.Name}'. Script4 executables are independent " +
                        "lifecycle actors and cannot be compile-time dependencies. " +
                        "Move shared code into a passive library package.");
                }

                dependencies.Add(provider.Name);
            }

            directDependencies[package.Name] =
                [.. dependencies.OrderBy(
                    value => value,
                    StringComparer.OrdinalIgnoreCase)];
        }

        Dictionary<string, byte> visitState = new(
            StringComparer.OrdinalIgnoreCase);
        List<string> visitStack = [];

        void VisitDependency(string packageName)
        {
            visitState[packageName] = 1;
            visitStack.Add(packageName);

            foreach (string dependencyName in directDependencies[packageName])
            {
                if (!visitState.TryGetValue(dependencyName, out byte state))
                {
                    VisitDependency(dependencyName);
                    continue;
                }
                if (state != 1)
                {
                    continue;
                }

                int cycleStart = visitStack.FindIndex(value =>
                    value.Equals(
                        dependencyName,
                        StringComparison.OrdinalIgnoreCase));
                IEnumerable<string> cycle = cycleStart >= 0
                    ? visitStack.Skip(cycleStart).Append(dependencyName)
                    : [dependencyName, packageName, dependencyName];
                throw new BadImageFormatException(
                    "scripts4 package dependency cycle detected: " +
                    string.Join(" -> ", cycle) + ".");
            }

            visitStack.RemoveAt(visitStack.Count - 1);
            visitState[packageName] = 2;
        }

        foreach (string packageName in directDependencies.Keys.OrderBy(
                     value => value,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!visitState.ContainsKey(packageName))
            {
                VisitDependency(packageName);
            }
        }

        List<PackageDescriptor> resolved = [];
        foreach (PackageDescriptor package in packages)
        {
            HashSet<string> transitive = new(
                StringComparer.OrdinalIgnoreCase);
            Queue<string> pending = new(directDependencies[package.Name]);
            while (pending.Count != 0)
            {
                string dependencyName = pending.Dequeue();
                if (dependencyName.Equals(
                        package.Name,
                        StringComparison.OrdinalIgnoreCase) ||
                    !transitive.Add(dependencyName))
                {
                    continue;
                }

                if (directDependencies.TryGetValue(
                        dependencyName,
                        out string[]? nested))
                {
                    foreach (string value in nested)
                    {
                        pending.Enqueue(value);
                    }
                }
            }

            resolved.Add(package with
            {
                DependencyPackageNames = Array.AsReadOnly(
                    [.. transitive.OrderBy(
                        value => value,
                        StringComparer.OrdinalIgnoreCase)])
            });
        }

        return Array.AsReadOnly(
            [.. resolved.OrderBy(
                value => value.Name,
                StringComparer.OrdinalIgnoreCase)]);
    }

    private static PackageDescriptor? BuildDescriptor(
        AssemblyInspection inspection,
        RuntimeLog log)
    {
        if (!inspection.IsManaged)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(log);
        string assemblyFileName = Path.GetFileName(inspection.Path);
        string assemblyName = inspection.AssemblyName
            ?? throw new BadImageFormatException(
                $"Managed assembly '{assemblyFileName}' has no identity.");
        string fileStem = Path.GetFileNameWithoutExtension(inspection.Path);
        if (!fileStem.Equals(
                assemblyName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BadImageFormatException(
                $"Flat scripts4 assembly '{assemblyFileName}' must use its " +
                $"managed assembly identity '{assemblyName}' as the file name.");
        }

        if (inspection.IsRuntimeAssembly)
        {
            throw new BadImageFormatException(
                $"Package '{assemblyName}' is a copy of " +
                "CommunityScriptHookVDotNetCore.dll.");
        }
        if (inspection.DeclaresManagedBrain)
        {
            throw new BadImageFormatException(
                $"Package '{assemblyName}' declares a managed brain.");
        }
        if (inspection.DeclaresRuntimeExtension)
        {
            throw new BadImageFormatException(
                $"Package '{assemblyName}' declares a root runtime extension.");
        }
        if (inspection.HasModuleInitializer)
        {
            throw new BadImageFormatException(
                $"Package '{assemblyName}' contains a module initializer.");
        }
        if (inspection.ReferencesRawNativeTransport)
        {
            throw new BadImageFormatException(
                $"Package '{assemblyName}' references the runtime-only " +
                $"{RawNativeTransportTypeName} contract.");
        }
        if (inspection.ForbiddenNativeImport is not null)
        {
            throw new BadImageFormatException(
                $"Package '{assemblyName}' imports the Script Hook V native " +
                $"entry point '{inspection.ForbiddenNativeImport}'.");
        }

        string fullAssemblyPath = Path.GetFullPath(inspection.Path);
        IReadOnlyDictionary<string, string> assemblyPathsByName =
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [assemblyName] = fullAssemblyPath
                });
        IReadOnlyList<string> references = Array.AsReadOnly(
            [.. inspection.ReferencedAssemblyNames.OrderBy(
                value => value,
                StringComparer.OrdinalIgnoreCase)]);
        bool executable = inspection.ScriptTypeNames.Count != 0;
        if (inspection.HasScript4DerivedType && !executable)
        {
            throw new BadImageFormatException(
                $"Package '{assemblyName}' derives from Script4 but contains no " +
                "concrete Script4 executable. Passive libraries must not derive " +
                "from Script4, even through an abstract base type.");
        }

        return new(
            assemblyName,
            executable
                ? ScriptPackageKind.Executable
                : ScriptPackageKind.Library,
            executable ? fullAssemblyPath : null,
            inspection.ScriptTypeNames,
            assemblyPathsByName,
            references,
            DependencyPackageNames: []);
    }

    internal static void EnsureFlatAssemblyLayout(string scriptsRoot)
    {
        foreach (string nestedAssembly in Directory.EnumerateFiles(
                     scriptsRoot,
                     "*.dll",
                     SearchOption.AllDirectories))
        {
            string? parent = Path.GetDirectoryName(
                Path.GetFullPath(nestedAssembly));
            if (parent is not null &&
                !parent.Equals(
                    scriptsRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "scripts4 uses a flat managed assembly layout. Move " +
                    $"'{Path.GetRelativePath(scriptsRoot, nestedAssembly)}' " +
                    "directly into scripts4.");
            }
        }
    }

    private static void ValidateDirectAssemblyPath(
        string scriptsRoot,
        string assemblyPath)
    {
        string root = Path.GetFullPath(scriptsRoot);
        string fullPath = Path.GetFullPath(assemblyPath);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null ||
            !parent.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(fullPath).Equals(
                ".dll",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Assembly '{assemblyPath}' is not a direct scripts4 DLL.");
        }
    }

    private static AssemblyInspection InspectAssembly(
        string path,
        Stream stream)
    {
        using PEReader pe = new(
            stream,
            PEStreamOptions.PrefetchMetadata | PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata)
        {
            return AssemblyInspection.Native(path);
        }

        MetadataReader metadata = pe.GetMetadataReader();
        string assemblyName = metadata.GetString(
            metadata.GetAssemblyDefinition().Name);
        bool isRuntimeAssembly = assemblyName.Equals(
            RuntimeAssemblyName,
            StringComparison.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> assemblyMetadata =
            ManagedAssemblyMetadata.Read(metadata);
        bool managedBrain = assemblyMetadata.TryGetValue(
                "CCHL.Role",
                out string? cchlRole) &&
            cchlRole.Equals("ManagedBrain", StringComparison.OrdinalIgnoreCase);
        bool runtimeExtension = assemblyMetadata.TryGetValue(
                RuntimeExtensionMetadataKeys.Role,
                out string? runtimeRole) &&
            runtimeRole.Equals(
                RuntimeExtensionMetadataKeys.RuntimeExtensionRole,
                StringComparison.OrdinalIgnoreCase);
        bool moduleInitializer = HasModuleInitializer(metadata);
        bool referencesRawNativeTransport =
            ReferencesRawNativeTransport(metadata);
        string? forbiddenNativeImport =
            FindForbiddenNativeImport(metadata);

        List<string> scriptTypes = [];
        bool hasScript4DerivedType = false;
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            TypeAttributes attributes = type.Attributes;
            if ((attributes & TypeAttributes.Interface) != 0 ||
                !DerivesFromScript4(metadata, handle, []))
            {
                continue;
            }

            hasScript4DerivedType = true;
            if ((attributes & TypeAttributes.Abstract) == 0 &&
                type.GetGenericParameters().Count == 0)
            {
                scriptTypes.Add(GetTypeName(metadata, handle));
            }
        }

        HashSet<string> referencedAssemblies = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
        {
            AssemblyReference reference = metadata.GetAssemblyReference(handle);
            string referencedName = metadata.GetString(reference.Name);
            if (!string.IsNullOrWhiteSpace(referencedName))
            {
                referencedAssemblies.Add(referencedName);
            }
        }

        return new(
            path,
            true,
            assemblyName,
            isRuntimeAssembly,
            managedBrain,
            runtimeExtension,
            moduleInitializer,
            referencesRawNativeTransport,
            forbiddenNativeImport,
            hasScript4DerivedType,
            scriptTypes,
            Array.AsReadOnly(
                [.. referencedAssemblies.OrderBy(
                    value => value,
                    StringComparer.OrdinalIgnoreCase)]));
    }

    private static bool DerivesFromScript4(
        MetadataReader metadata,
        TypeDefinitionHandle handle,
        HashSet<TypeDefinitionHandle> visited)
    {
        if (!visited.Add(handle))
        {
            return false;
        }

        EntityHandle baseType = metadata.GetTypeDefinition(handle).BaseType;
        if (baseType.IsNil)
        {
            return false;
        }

        if (baseType.Kind == HandleKind.TypeDefinition)
        {
            return DerivesFromScript4(
                metadata,
                (TypeDefinitionHandle)baseType,
                visited);
        }
        if (baseType.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        TypeReference reference = metadata.GetTypeReference(
            (TypeReferenceHandle)baseType);
        if (!metadata.GetString(reference.Namespace).Equals(
                ScriptNamespace,
                StringComparison.Ordinal) ||
            !metadata.GetString(reference.Name).Equals(
                ScriptTypeName,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
        {
            return false;
        }
        AssemblyReference assembly = metadata.GetAssemblyReference(
            (AssemblyReferenceHandle)reference.ResolutionScope);
        return metadata.GetString(assembly.Name).Equals(
            RuntimeAssemblyName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTypeName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return GetTypeName(metadata, declaring) + "+" + name;
        }

        string typeNamespace = metadata.GetString(type.Namespace);
        return typeNamespace.Length == 0
            ? name
            : typeNamespace + "." + name;
    }

    private static bool ReferencesRawNativeTransport(
        MetadataReader metadata)
    {
        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            TypeReference reference = metadata.GetTypeReference(handle);
            if (!metadata.GetString(reference.Namespace).Equals(
                    ScriptNamespace,
                    StringComparison.Ordinal) ||
                !metadata.GetString(reference.Name).Equals(
                    RawNativeTransportTypeName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
            {
                return true;
            }

            AssemblyReference assembly = metadata.GetAssemblyReference(
                (AssemblyReferenceHandle)reference.ResolutionScope);
            if (metadata.GetString(assembly.Name).Equals(
                    RuntimeAssemblyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindForbiddenNativeImport(
        MetadataReader metadata)
    {
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
            {
                continue;
            }

            MethodImport import = method.GetImport();
            string importName = metadata.GetString(import.Name);
            if (importName.Equals("nativeInit", StringComparison.Ordinal) ||
                importName.Equals("nativePush64", StringComparison.Ordinal) ||
                importName.Equals("nativeCall", StringComparison.Ordinal))
            {
                return importName;
            }
        }

        return null;
    }

    private static bool HasModuleInitializer(MetadataReader metadata)
    {
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            if (!metadata.GetString(type.Name).Equals(
                    "<Module>",
                    StringComparison.Ordinal))
            {
                continue;
            }

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if (metadata.GetString(method.Name).Equals(
                        ".cctor",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private sealed record AssemblyInspection(
        string Path,
        bool IsManaged,
        string? AssemblyName,
        bool IsRuntimeAssembly,
        bool DeclaresManagedBrain,
        bool DeclaresRuntimeExtension,
        bool HasModuleInitializer,
        bool ReferencesRawNativeTransport,
        string? ForbiddenNativeImport,
        bool HasScript4DerivedType,
        IReadOnlyList<string> ScriptTypeNames,
        IReadOnlyList<string> ReferencedAssemblyNames)
    {
        public static AssemblyInspection Native(string path) =>
            new(
                path,
                false,
                null,
                false,
                false,
                false,
                false,
                false,
                null,
                false,
                [],
                []);
    }
}