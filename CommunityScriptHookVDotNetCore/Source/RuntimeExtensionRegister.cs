using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CommunityScriptHookVDotNetCore.Source;

internal static class RuntimeExtensionMetadataKeys
{
    public const string Role = "CSHVDNC.Role";
    public const string Id = "CSHVDNC.Id";
    public const string EntryType = "CSHVDNC.EntryType";
    public const string ContractMajor = "CSHVDNC.ContractMajor";
    public const string ContractMinor = "CSHVDNC.ContractMinor";
    public const string Provides = "CSHVDNC.Provides";
    public const string Requires = "CSHVDNC.Requires";
    public const string RuntimeExtensionRole = "RuntimeExtension";
}

public static class RuntimeCapabilities
{
    public const string RuntimeServices = "runtime.services";
    public const string HostFrame = "host.frame";
    public const string RawNative = "host.native.raw";
    public const string NativeAdmissionControl = "host.native.admission";
    public const string CooperativeShutdown = "host.shutdown";
    public const string PackageLifecycle = "package.lifecycle";
    public const string PackageTransitionHost = "package.lifecycle.transition";
    public const string ScriptScheduler = "script.scheduler";
}

public enum RawNativeCallStatus
{
    Pending = -1,
    Success = 0,
    InvalidRequest = 1,
    TooManyArguments = 2,
    TooManyResults = 3,
    NativeReturnedNull = 4,
    SessionStopping = 5,
    AdmissionUnavailable = 6,
    AdmissionRejected = 7
}

public readonly record struct RawNativeCallResult(
    RawNativeCallStatus Status,
    ulong[] Results);

public enum NativeCallAdmissionStatus
{
    Allowed = 0,
    UnknownHash = 1,
    UnsupportedTarget = 2,
    ArgumentCountMismatch = 3,
    ResultCountMismatch = 4,
    ExposureRejected = 5
}

public readonly record struct NativeCallAdmissionDecision(
    NativeCallAdmissionStatus Status)
{
    public bool IsAllowed => Status is NativeCallAdmissionStatus.Allowed;
}

public interface INativeCallAdmissionPolicy
{
    NativeCallAdmissionDecision Evaluate(
        ulong hash,
        int argumentCount,
        int requestedResultCount);
}

public interface INativeCallAdmissionControl
{
    IDisposable Install(INativeCallAdmissionPolicy policy);
}

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

public enum ScriptLifecycleTransitionReason
{
    ManualReload,
    SynchronizedReload
}

public sealed record ScriptLifecycleTransitionPlan(
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
    public static ScriptLifecycleTransitionOperationId Create() => new(Guid.NewGuid());
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
    ScriptLifecycleTransitionOperationId RequestTransition(
        ScriptLifecycleTransitionPlan plan);

    IReadOnlyList<ScriptLifecycleTransitionOperationSnapshot> SnapshotOperations();

    void Acknowledge(ScriptLifecycleTransitionOperationId operationId);
}

internal sealed class RuntimeServiceRegistry : IRuntimeServiceRegistry
{
    private sealed record Registration(
        object Service,
        string Owner,
        bool ScriptVisible);

    private const string CoreOwner = "CommunityScriptHookVDotNetCore";
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, Registration> _services = [];
    private readonly HashSet<string> _revokedOwners =
        new(StringComparer.OrdinalIgnoreCase);

    internal RuntimeServiceRegistry()
    {
        ScriptServices = new ScriptServiceView(this);
    }

    internal IScriptServices ScriptServices { get; }

    internal IRuntimeServiceRegistry CreateOwnerScope(
        string owner,
        bool allowNativeAuthority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            if (_revokedOwners.Contains(owner))
            {
                throw new InvalidOperationException(
                    $"Runtime extension owner '{owner}' is permanently revoked " +
                    "for this GTA process.");
            }
        }
        return new OwnerServiceView(this, owner, allowNativeAuthority);
    }

    internal void RevokeOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            _revokedOwners.Add(owner);
            Type[] contracts = [.. _services
                .Where(pair => pair.Value.Owner.Equals(
                    owner,
                    StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)];
            foreach (Type contract in contracts)
            {
                _services.Remove(contract);
            }
        }
    }

    public void Register<TService>(TService service)
        where TService : class =>
        RegisterCore(service, scriptVisible: true);

    public void RegisterRuntimeOnly<TService>(TService service)
        where TService : class =>
        RegisterCore(service, scriptVisible: false);

    public bool TryGet<TService>(
        [NotNullWhen(true)] out TService? service)
        where TService : class =>
        TryGetCore(scriptOnly: false, out service);

    public TService GetRequired<TService>()
        where TService : class =>
        TryGet(out TService? service)
            ? service
            : throw new InvalidOperationException(
                $"Runtime service '{typeof(TService).FullName}' is unavailable.");

    private void RegisterCore<TService>(TService service, bool scriptVisible)
        where TService : class =>
        RegisterOwned(CoreOwner, service, scriptVisible);

    private void RegisterOwned<TService>(
        string owner,
        TService service,
        bool scriptVisible)
        where TService : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(service);
        lock (_gate)
        {
            if (_revokedOwners.Contains(owner))
            {
                throw new InvalidOperationException(
                    $"Runtime extension owner '{owner}' is permanently revoked " +
                    "for this GTA process.");
            }

            Type contract = typeof(TService);
            if (!_services.TryAdd(
                    contract,
                    new(service, owner, scriptVisible)))
            {
                throw new InvalidOperationException(
                    $"Runtime service '{contract.FullName}' is already registered.");
            }
        }
    }

    private bool TryGetOwned<TService>(
        string owner,
        [NotNullWhen(true)] out TService? service)
        where TService : class
    {
        lock (_gate)
        {
            if (_revokedOwners.Contains(owner))
            {
                service = null;
                return false;
            }
            if (_services.TryGetValue(
                    typeof(TService),
                    out Registration? value))
            {
                service = (TService)value.Service;
                return true;
            }
        }
        service = null;
        return false;
    }

    private bool TryGetCore<TService>(
        bool scriptOnly,
        [NotNullWhen(true)] out TService? service)
        where TService : class
    {
        lock (_gate)
        {
            if (_services.TryGetValue(typeof(TService), out Registration? value) &&
                (!scriptOnly || value.ScriptVisible))
            {
                service = (TService)value.Service;
                return true;
            }
        }
        service = null;
        return false;
    }

    private static bool IsNativeAuthorityContract(Type contract) =>
        contract == typeof(IRawNativeTransport) ||
        contract == typeof(INativeCallAdmissionControl);

    private sealed class OwnerServiceView(
        RuntimeServiceRegistry owner,
        string ownerId,
        bool allowNativeAuthority) : IRuntimeServiceRegistry
    {
        public bool TryGet<TService>(
            [NotNullWhen(true)] out TService? service)
            where TService : class
        {
            if (!allowNativeAuthority && IsNativeAuthorityContract(typeof(TService)))
            {
                service = null;
                return false;
            }
            return owner.TryGetOwned(ownerId, out service);
        }

        public TService GetRequired<TService>()
            where TService : class =>
            TryGet(out TService? service)
                ? service
                : throw new InvalidOperationException(
                    $"Runtime service '{typeof(TService).FullName}' is unavailable " +
                    $"to extension '{ownerId}'.");

        public void Register<TService>(TService service)
            where TService : class =>
            owner.RegisterOwned(ownerId, service, scriptVisible: true);

        public void RegisterRuntimeOnly<TService>(TService service)
            where TService : class =>
            owner.RegisterOwned(ownerId, service, scriptVisible: false);
    }

    private sealed class ScriptServiceView(
        RuntimeServiceRegistry owner) : IScriptServices
    {
        public bool TryGet<TService>(
            [NotNullWhen(true)] out TService? service)
            where TService : class =>
            owner.TryGetCore(scriptOnly: true, out service);

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
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> ReferencedAssemblyNames);

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
        RootAssemblySnapshot snapshot,
        RuntimeLog log)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(log);

        List<RuntimeExtensionDescriptor> result = [];
        foreach (RootAssemblyImage image in snapshot.Images
                     .OrderBy(
                         value => value.Path,
                         StringComparer.OrdinalIgnoreCase))
        {
            RuntimeExtensionDescriptor? descriptor;
            try
            {
                descriptor = Inspect(image);
            }
            catch (Exception exception)
            {
                log.Warning(
                    $"Committed root assembly '{Path.GetFileName(image.Path)}' " +
                    $"could not be inspected: {exception.Message}");
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

    private static RuntimeExtensionDescriptor? Inspect(
        RootAssemblyImage image)
    {
        using MemoryStream stream = new(image.Assembly, writable: false);
        using PEReader pe = new(stream, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata)
        {
            return null;
        }

        MetadataReader metadata = pe.GetMetadataReader();
        IReadOnlyDictionary<string, string> values =
            ManagedAssemblyMetadata.Read(metadata);
        if (!values.TryGetValue(RuntimeExtensionMetadataKeys.Role, out string? role) ||
            !role.Equals(RuntimeExtensionMetadataKeys.RuntimeExtensionRole, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string assemblyName = metadata.GetString(
            metadata.GetAssemblyDefinition().Name);
        string id = Required(values, RuntimeExtensionMetadataKeys.Id);
        string entryType = Required(values, RuntimeExtensionMetadataKeys.EntryType);
        int major = ParseContract(values, RuntimeExtensionMetadataKeys.ContractMajor);
        int minor = ParseContract(values, RuntimeExtensionMetadataKeys.ContractMinor);
        if (major != ContractMajor || minor > ContractMinor)
        {
            throw new BadImageFormatException(
                $"Runtime extension '{id}' uses incompatible contract " +
                $"version {major}.{minor}.");
        }

        HashSet<string> references = new(StringComparer.OrdinalIgnoreCase);
        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
        {
            AssemblyReference reference = metadata.GetAssemblyReference(handle);
            string referencedName = metadata.GetString(reference.Name);
            if (!string.IsNullOrWhiteSpace(referencedName))
            {
                references.Add(referencedName);
            }
        }

        return new(
            id,
            image.Path,
            assemblyName,
            entryType,
            ParseCapabilities(values, RuntimeExtensionMetadataKeys.Provides),
            ParseCapabilities(values, RuntimeExtensionMetadataKeys.Requires),
            Array.AsReadOnly(
                [.. references.OrderBy(
                    value => value,
                    StringComparer.OrdinalIgnoreCase)]));
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