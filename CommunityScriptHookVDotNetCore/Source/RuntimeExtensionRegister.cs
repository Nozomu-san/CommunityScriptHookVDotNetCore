using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CommunityScriptHookVDotNetCore.Source;

internal static class RuntimeExtensionMetadataKeys
{
    public const string Role = "CSHVDNC.Role";
    public const string Id = "CSHVDNC.Id";
    public const string EntryType = "CSHVDNC.EntryType";
    public const string Provides = "CSHVDNC.Provides";
    public const string Requires = "CSHVDNC.Requires";
    public const string ConditionalRequires = "CSHVDNC.ConditionalRequires";
    public const string RuntimeExtensionRole = "RuntimeExtension";
}

public static class RuntimeCapabilities
{
    public const string RuntimeServices = "runtime.services";
    public const string HostFrame = "host.frame";
    public const string RawNative = "host.native.raw";
    public const string NativeAdmissionControl = "host.native.admission";
    public const string GameThreadFunctions = "host.game-thread.function";
    public const string GuardedGameThreadFunctions =
        "host.game-thread.function.guarded";
    public const string CooperativeShutdown = "host.shutdown";
    public const string PackageLifecycle = "package.lifecycle";
    public const string PackageTransitionHost = "package.lifecycle.transition";
    public const string ScriptScheduler = "script.scheduler";
    public const string RuntimeDiagnostics = "runtime.diagnostics";
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
        IReadOnlyList<ulong> arguments,
        int resultCount);
}

public enum GameThreadFunctionCallStatus
{
    Success = 0,
    InvalidRequest = 1,
    SessionStopping = 5,
    FunctionFault = 6,
    AdmissionRejected = 7,
    GuardRejected = 8
}

public readonly record struct GameThreadInt32CallResult(
    GameThreadFunctionCallStatus Status,
    int Value)
{
    public bool IsSuccess => Status is GameThreadFunctionCallStatus.Success;
}

public readonly record struct GameThreadMemoryGuard(
    nint Address,
    ulong Mask,
    ulong Expected,
    int Width)
{
    public bool IsConfigured =>
        Address != 0 &&
        Width is 1 or 2 or 4 or 8;

    public static GameThreadMemoryGuard None { get; } =
        new(0, 0, 0, 0);
}

public interface IGameThreadFunctionTransport
{
    GameThreadInt32CallResult InvokeInt32(
        nint functionAddress,
        nint pointerArgument);

    GameThreadInt32CallResult InvokeInt32Guarded(
        nint functionAddress,
        nint pointerArgument,
        GameThreadMemoryGuard primaryGuard,
        GameThreadMemoryGuard secondaryGuard);
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

public interface IRuntimeExtensionDependencies
{
    void Require(string capability);
}

public sealed class RuntimeExtensionContext
{
    internal RuntimeExtensionContext(
        string rootDirectory,
        string extensionsDirectory,
        string scriptsDirectory,
        IRuntimeServiceRegistry services,
        IRuntimeExtensionDependencies dependencies,
        IRuntimeDiagnosticReader diagnostics)
    {
        RootDirectory = rootDirectory;
        ExtensionsDirectory = extensionsDirectory;
        ScriptsDirectory = scriptsDirectory;
        Services = services;
        Dependencies = dependencies;
        Diagnostics = diagnostics;
    }

    public string RootDirectory { get; }
    public string ExtensionsDirectory { get; }
    public string ScriptsDirectory { get; }
    public IRuntimeServiceRegistry Services { get; }
    public IRuntimeExtensionDependencies Dependencies { get; }
    public IRuntimeDiagnosticReader Diagnostics { get; }
}

public interface IScript4RuntimeExtension
{
    Task InitializeAsync(
        RuntimeExtensionContext context,
        CancellationToken cancellationToken);

    void AdvanceHostFrame(RuntimeExtensionFrameContext context);

    Task ShutdownAsync();
}

public enum ScriptPackageKind
{
    Executable,
    Library
}

public enum ScriptLifecycleTransitionReason
{
    ManualReload,
    AutomaticReload
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
    ReloadingExtensions,
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
        [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, int> _ownerEpochs =
        [with(StringComparer.OrdinalIgnoreCase)];

    internal RuntimeServiceRegistry()
    {
        ScriptServices = new ScriptServiceView(this);
    }

    internal IScriptServices ScriptServices { get; }

    internal IRuntimeServiceRegistry CreateOwnerScope(
        string owner,
        bool allowNativeAuthority,
        bool allowGameThreadFunctions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        int epoch;
        lock (_gate)
        {
            if (_revokedOwners.Contains(owner))
            {
                throw new InvalidOperationException(
                    $"Runtime extension owner '{owner}' is currently revoked.");
            }
            epoch = _ownerEpochs.GetValueOrDefault(owner);
        }
        return new OwnerServiceView(
            this,
            owner,
            epoch,
            allowNativeAuthority,
            allowGameThreadFunctions);
    }

    internal void RevokeOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            _revokedOwners.Add(owner);
            IncrementOwnerEpoch(owner);
            RemoveOwnerServices(owner);
        }
    }

    internal void ResetOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            _revokedOwners.Remove(owner);
            IncrementOwnerEpoch(owner);
            RemoveOwnerServices(owner);
        }
    }


    private void RemoveOwnerServices(string owner)
    {
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

    private void IncrementOwnerEpoch(string owner)
    {
        _ownerEpochs[owner] = checked(_ownerEpochs.GetValueOrDefault(owner) + 1);
    }

    private bool IsOwnerScopeActive(string owner, int epoch)
    {
        lock (_gate)
        {
            return !_revokedOwners.Contains(owner) &&
                _ownerEpochs.GetValueOrDefault(owner) == epoch;
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
                    $"Runtime extension owner '{owner}' is currently revoked.");
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

    private static bool IsGameThreadFunctionContract(Type contract) =>
        contract == typeof(IGameThreadFunctionTransport);

    private sealed class OwnerServiceView(
        RuntimeServiceRegistry owner,
        string ownerId,
        int ownerEpoch,
        bool allowNativeAuthority,
        bool allowGameThreadFunctions) : IRuntimeServiceRegistry
    {
        public bool TryGet<TService>(
            [NotNullWhen(true)] out TService? service)
            where TService : class
        {
            if (!owner.IsOwnerScopeActive(ownerId, ownerEpoch))
            {
                service = null;
                return false;
            }
            Type contract = typeof(TService);
            if ((!allowNativeAuthority && IsNativeAuthorityContract(contract)) ||
                (!allowGameThreadFunctions &&
                 IsGameThreadFunctionContract(contract)))
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
            where TService : class
        {
            if (!owner.IsOwnerScopeActive(ownerId, ownerEpoch))
            {
                throw new InvalidOperationException(
                    $"Runtime extension owner scope '{ownerId}' is no longer active.");
            }
            owner.RegisterOwned(ownerId, service, scriptVisible: true);
        }

        public void RegisterRuntimeOnly<TService>(TService service)
            where TService : class
        {
            if (!owner.IsOwnerScopeActive(ownerId, ownerEpoch))
            {
                throw new InvalidOperationException(
                    $"Runtime extension owner scope '{ownerId}' is no longer active.");
            }
            owner.RegisterOwned(ownerId, service, scriptVisible: false);
        }
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
    string AssemblyName,
    string EntryType,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> ConditionalRequires,
    IReadOnlyList<string> ReferencedAssemblyNames);

internal static class ManagedAssemblyMetadata
{
    private const string MetadataNamespace = "System.Reflection";
    private const string MetadataType = "AssemblyMetadataAttribute";

    public static IReadOnlyDictionary<string, string> Read(
        MetadataReader metadata)
    {
        Dictionary<string, string> values =
            [with(StringComparer.OrdinalIgnoreCase)];
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

        HashSet<string> ids = [with(StringComparer.OrdinalIgnoreCase)];
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

        HashSet<string> references = [with(StringComparer.OrdinalIgnoreCase)];
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
            assemblyName,
            entryType,
            ParseCapabilities(values, RuntimeExtensionMetadataKeys.Provides),
            ParseCapabilities(values, RuntimeExtensionMetadataKeys.Requires),
            ParseCapabilities(values, RuntimeExtensionMetadataKeys.ConditionalRequires),
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