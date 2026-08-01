using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CommunityScriptHookVDotNetCore.Source;

public static class RuntimeCapabilities
{
    public const string RuntimeServices = "runtime.services";
    public const string HostFrame = "host.frame";
    public const string RawNative = "host.native.raw";
    public const string CooperativeShutdown = "host.shutdown";
    public const string PackageLifecycle = "package.lifecycle";
    public const string PackageTransitionHost = "package.lifecycle.transition";
    public const string ScriptScheduler = "script.scheduler";
    public const string Scripts4Lifecycle = "scripts4.lifecycle";
}

public enum RawNativeCallStatus
{
    Pending = -1,
    Success = 0,
    InvalidRequest = 1,
    TooManyArguments = 2,
    TooManyResults = 3,
    NativeReturnedNull = 4,
    SessionStopping = 5
}

public readonly record struct RawNativeCallResult(
    RawNativeCallStatus Status,
    ulong[] Results);

public interface IRawNativeTransport
{
    RawNativeCallResult Invoke(
        ulong hash,
        ReadOnlySpan<ulong> arguments,
        int resultCount);
}

public interface IRuntimeServiceRegistry
{
    bool TryGet<TService>(
        [NotNullWhen(true)] out TService? service)
        where TService : class;

    TService GetRequired<TService>()
        where TService : class;

    void Register<TService>(TService service)
        where TService : class;

    void RegisterRuntimeOnly<TService>(TService service)
        where TService : class;
}

public readonly record struct RuntimeExtensionFrameContext(
    ulong HostFrameIndex,
    long PerformanceCounter,
    ulong PerformanceFrequency);

public sealed class RuntimeExtensionContext
{
    internal RuntimeExtensionContext(
        string rootDirectory,
        string scriptsDirectory,
        IRuntimeServiceRegistry services)
    {
        RootDirectory = rootDirectory;
        ScriptsDirectory = scriptsDirectory;
        Services = services;
    }

    public string RootDirectory { get; }
    public string ScriptsDirectory { get; }
    public IRuntimeServiceRegistry Services { get; }
}

public interface IScript4RuntimeExtension
{
    ValueTask InitializeAsync(
        RuntimeExtensionContext context,
        CancellationToken cancellationToken);

    void AdvanceHostFrame(RuntimeExtensionFrameContext context);

    ValueTask ShutdownAsync(CancellationToken cancellationToken);
}

public enum ScriptPackageKind
{
    Executable,
    Library
}

public sealed record ScriptPackageInfo(
    string Name,
    string Directory,
    ScriptPackageKind Kind,
    string? EntryAssembly,
    IReadOnlyList<string> AssemblyFiles,
    IReadOnlyList<string> DependencyPackageNames);

public enum ScriptLifecycleTransitionReason
{
    ManualReload,
    SynchronizedReload,
    Recovery
}

public sealed record ScriptLifecycleTransitionPlan(
    IReadOnlyList<string> BinaryReplacementPackages,
    bool RestartAllExecutables,
    ScriptLifecycleTransitionReason Reason);

public sealed record ScriptLifecycleTransitionResult(
    ulong LifecycleEpoch,
    IReadOnlyList<string> RestartedInPlacePackages,
    IReadOnlyList<string> BinaryReplacedPackages,
    IReadOnlyList<string> RefreshedLibraries,
    IReadOnlyList<string> AddedPackages,
    IReadOnlyList<string> RemovedPackages,
    IReadOnlyList<string> FailedPackages)
{
    public bool Succeeded => FailedPackages.Count == 0;
}

public enum ScriptLifecycleTransitionOperationState
{
    Queued,
    CapturingImages,
    ReadyInMemory,
    StoppingLifecycle,
    ReplacingBinaries,
    RecreatingInstances,
    StartingLifecycle,
    Completed,
    Cancelled,
    Failed
}

public readonly record struct ScriptLifecycleTransitionOperationId(Guid Value)
{
    public static ScriptLifecycleTransitionOperationId Create() =>
        new(Guid.NewGuid());
}

public sealed record ScriptLifecycleTransitionOperationSnapshot(
    ScriptLifecycleTransitionOperationId Id,
    ScriptLifecycleTransitionOperationState State,
    ScriptLifecycleTransitionPlan Plan,
    ulong? LifecycleEpoch,
    ScriptLifecycleTransitionResult? Result,
    string? Diagnostic)
{
    public bool IsTerminal =>
        State is ScriptLifecycleTransitionOperationState.Completed or
            ScriptLifecycleTransitionOperationState.Cancelled or
            ScriptLifecycleTransitionOperationState.Failed;
}

public interface IReloadRuntimeHost
{
    IReadOnlyList<ScriptPackageInfo> GetInventory();

    void ActivateInitialPackages();

    ScriptLifecycleTransitionOperationId RequestTransition(
        ScriptLifecycleTransitionPlan plan);

    IReadOnlyList<ScriptLifecycleTransitionOperationSnapshot>
        SnapshotOperations();

    void Acknowledge(ScriptLifecycleTransitionOperationId operationId);
}

internal sealed class RuntimeServiceRegistry : IRuntimeServiceRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, object> _runtimeServices = [];
    private readonly Dictionary<Type, object> _scriptServices = [];

    internal RuntimeServiceRegistry()
    {
        ScriptServices = new ScriptServiceView(this);
    }

    internal IScriptServices ScriptServices { get; }

    public void Register<TService>(TService service)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_gate)
        {
            Type contract = typeof(TService);
            if (_runtimeServices.ContainsKey(contract) ||
                _scriptServices.ContainsKey(contract))
            {
                throw new InvalidOperationException(
                    $"Runtime service '{contract.FullName}' is already registered.");
            }

            _runtimeServices.Add(contract, service);
            _scriptServices.Add(contract, service);
        }
    }

    public void RegisterRuntimeOnly<TService>(TService service)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_gate)
        {
            Type contract = typeof(TService);
            if (_runtimeServices.ContainsKey(contract))
            {
                throw new InvalidOperationException(
                    $"Runtime service '{contract.FullName}' is already registered.");
            }

            _runtimeServices.Add(contract, service);
        }
    }

    public bool TryGet<TService>(
        [NotNullWhen(true)] out TService? service)
        where TService : class
    {
        lock (_gate)
        {
            if (_runtimeServices.TryGetValue(
                    typeof(TService),
                    out object? value))
            {
                service = (TService)value;
                return true;
            }
        }

        service = null;
        return false;
    }

    public TService GetRequired<TService>()
        where TService : class =>
        TryGet(out TService? service)
            ? service
            : throw new InvalidOperationException(
                $"Runtime service '{typeof(TService).FullName}' is unavailable.");

    private bool TryGetForScript<TService>(
        [NotNullWhen(true)] out TService? service)
        where TService : class
    {
        lock (_gate)
        {
            if (_scriptServices.TryGetValue(
                    typeof(TService),
                    out object? value))
            {
                service = (TService)value;
                return true;
            }
        }

        service = null;
        return false;
    }

    private sealed class ScriptServiceView(
        RuntimeServiceRegistry owner) : IScriptServices
    {
        public bool TryGet<TService>(
            [NotNullWhen(true)] out TService? service)
            where TService : class =>
            owner.TryGetForScript(out service);

        public TService GetRequired<TService>()
            where TService : class =>
            TryGet(out TService? service)
                ? service
                : throw new InvalidOperationException(
                    $"Script service '{typeof(TService).FullName}' is unavailable.");
    }
}

internal sealed record RuntimeExtensionDescriptor(
    string Id,
    string AssemblyPath,
    string AssemblyName,
    string EntryType,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> Requires);

internal static class ManagedAssemblyMetadata
{
    private const string MetadataNamespace = "System.Reflection";
    private const string MetadataType = "AssemblyMetadataAttribute";

    public static IReadOnlyDictionary<string, string> Read(
        MetadataReader metadata)
    {
        Dictionary<string, string> values =
            new(StringComparer.OrdinalIgnoreCase);
        AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
        foreach (CustomAttributeHandle handle in assembly.GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            if (!IsAssemblyMetadataAttribute(metadata, attribute.Constructor))
            {
                continue;
            }

            BlobReader reader = metadata.GetBlobReader(attribute.Value);
            if (reader.RemainingBytes < sizeof(ushort) ||
                reader.ReadUInt16() != 1)
            {
                continue;
            }

            string? key = reader.ReadSerializedString();
            string? value = reader.ReadSerializedString();
            if (key is null ||
                value is null ||
                reader.RemainingBytes < sizeof(ushort) ||
                reader.ReadUInt16() != 0 ||
                reader.RemainingBytes != 0)
            {
                continue;
            }

            if (!values.TryAdd(key, value))
            {
                throw new BadImageFormatException(
                    $"Assembly metadata key '{key}' is duplicated.");
            }
        }
        return values;
    }

    public static bool Declares(
        MetadataReader metadata,
        string key,
        string value)
    {
        IReadOnlyDictionary<string, string> values = Read(metadata);
        return values.TryGetValue(key, out string? actual) &&
            actual.Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAssemblyMetadataAttribute(
        MetadataReader metadata,
        EntityHandle constructor)
    {
        if (constructor.Kind != HandleKind.MemberReference)
        {
            return false;
        }

        MemberReference member = metadata.GetMemberReference(
            (MemberReferenceHandle)constructor);
        if (!metadata.GetString(member.Name).Equals(
                ".ctor",
                StringComparison.Ordinal) ||
            member.Parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        TypeReference type = metadata.GetTypeReference(
            (TypeReferenceHandle)member.Parent);
        return metadata.GetString(type.Namespace).Equals(
                   MetadataNamespace,
                   StringComparison.Ordinal) &&
            metadata.GetString(type.Name).Equals(
                MetadataType,
                StringComparison.Ordinal);
    }
}

internal static class RuntimeExtensionDiscovery
{
    private const int ContractMajor = 1;
    private const int ContractMinor = 0;

    public static IReadOnlyList<RuntimeExtensionDescriptor> Discover(
        string rootDirectory,
        RuntimeLog log)
    {
        List<RuntimeExtensionDescriptor> result = [];
        foreach (string path in Directory
                     .EnumerateFiles(rootDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            RuntimeExtensionDescriptor? descriptor;
            try
            {
                descriptor = Inspect(path);
            }
            catch (Exception exception)
            {
                log.Warning(
                    $"Root assembly '{Path.GetFileName(path)}' could not be " +
                    $"inspected: {exception.Message}");
                continue;
            }

            if (descriptor is not null)
            {
                result.Add(descriptor);
            }
        }

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (RuntimeExtensionDescriptor descriptor in result)
        {
            if (!ids.Add(descriptor.Id))
            {
                throw new InvalidOperationException(
                    $"Runtime extension id '{descriptor.Id}' is duplicated.");
            }
        }
        return result;
    }

    private static RuntimeExtensionDescriptor? Inspect(string path)
    {
        using FileStream stream = File.Open(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using PEReader pe = new(stream, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata)
        {
            return null;
        }

        MetadataReader metadata = pe.GetMetadataReader();
        IReadOnlyDictionary<string, string> values =
            ManagedAssemblyMetadata.Read(metadata);
        if (!values.TryGetValue("SHVDN4.Role", out string? role) ||
            !role.Equals("RuntimeExtension", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string assemblyName = metadata.GetString(
            metadata.GetAssemblyDefinition().Name);
        string id = Required(values, "SHVDN4.Id");
        string entryType = Required(values, "SHVDN4.EntryType");
        int major = ParseContract(values, "SHVDN4.ContractMajor");
        int minor = ParseContract(values, "SHVDN4.ContractMinor");
        if (major != ContractMajor || minor > ContractMinor)
        {
            throw new BadImageFormatException(
                $"Runtime extension '{id}' uses incompatible contract " +
                $"version {major}.{minor}.");
        }

        return new(
            id,
            Path.GetFullPath(path),
            assemblyName,
            entryType,
            ParseCapabilities(values, "SHVDN4.Provides"),
            ParseCapabilities(values, "SHVDN4.Requires"));
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        if (!values.TryGetValue(key, out string? value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new BadImageFormatException(
                $"Required runtime-extension metadata '{key}' is missing.");
        }
        return value.Trim();
    }

    private static int ParseContract(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        string text = Required(values, key);
        return int.TryParse(text, out int value) && value >= 0
            ? value
            : throw new BadImageFormatException(
                $"Runtime-extension metadata '{key}' is invalid.");
    }

    private static IReadOnlyList<string> ParseCapabilities(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        if (!values.TryGetValue(key, out string? text) ||
            string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return [.. text
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}