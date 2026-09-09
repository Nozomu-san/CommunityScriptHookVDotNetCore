using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace CommunityScriptHookVDotNetCore.Source;

internal sealed class RuntimeExtensionManager(
    string rootDirectory,
    string scriptsDirectory,
    RuntimeServiceRegistry services,
    RuntimeLog log) : IDisposable
{
    private static readonly TimeSpan RootInitializationTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly int MaximumConcurrentInitializations =
        Math.Clamp(Environment.ProcessorCount, 2, 8);
    private static readonly Assembly ContractAssembly =
        typeof(IScript4RuntimeExtension).Assembly;
    private static readonly string ContractAssemblyName =
        ContractAssembly.GetName().Name
        ?? throw new InvalidOperationException(
            "The runtime-extension contract assembly has no simple name.");
    private static readonly AssemblyLoadContext ContractLoadContext =
        AssemblyLoadContext.GetLoadContext(ContractAssembly)
        ?? throw new InvalidOperationException(
            "The runtime-extension contract assembly has no AssemblyLoadContext.");

    private static readonly string[] CoreCapabilities =
    [
        RuntimeCapabilities.RuntimeServices,
        RuntimeCapabilities.HostFrame,
        RuntimeCapabilities.RawNative,
        RuntimeCapabilities.NativeAdmissionControl,
        RuntimeCapabilities.GameThreadFunctions,
        RuntimeCapabilities.GuardedGameThreadFunctions,
        RuntimeCapabilities.CooperativeShutdown,
        RuntimeCapabilities.PackageLifecycle,
        RuntimeCapabilities.PackageTransitionHost,
        RuntimeCapabilities.ScriptScheduler
    ];

    private readonly int _runtimeThreadId = Environment.CurrentManagedThreadId;
    private readonly List<ActiveRuntimeExtension> _active = [];
    private readonly List<RetiringRuntimeExtension> _retiring = [];
    private readonly List<PendingRuntimeExtension> _initializing = [];
    private readonly List<RuntimeExtensionDescriptor> _pendingInitialization = [];
    private readonly HashSet<string> _availableCapabilities =
        new(CoreCapabilities, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unavailableAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _newlyUnavailableAssemblies = [];
    private RootAssemblyResolver? _resolver;
    private bool _initializationPrepared;
    private bool _initializationCompleted;
    private bool _shutdown;

    internal IReadOnlyCollection<string> UnavailableAssemblyNames =>
        _unavailableAssemblies;

    internal bool InitializationCompleted => _initializationCompleted;

    public void PrepareInitialization()
    {
        if (_initializationPrepared)
        {
            throw new InvalidOperationException(
                "Runtime-extension initialization was already prepared.");
        }

        RootAssemblySnapshot snapshot = RootAssemblySnapshot.Capture(
            rootDirectory,
            ContractAssemblyName,
            log);
        IReadOnlyList<RuntimeExtensionDescriptor> discovered =
            RuntimeExtensionDiscovery.Discover(snapshot, log);

        _resolver = new(
            snapshot,
            ContractLoadContext,
            ContractAssembly,
            log);

        Dictionary<string, string> declaredProviders = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (string capability in CoreCapabilities)
        {
            declaredProviders[capability] = "CommunityScriptHookVDotNetCore";
        }

        HashSet<string> rejected = new(StringComparer.OrdinalIgnoreCase);
        foreach (RuntimeExtensionDescriptor descriptor in discovered)
        {
            foreach (string capability in descriptor.Provides)
            {
                if (declaredProviders.TryAdd(capability, descriptor.Id))
                {
                    continue;
                }

                string existing = declaredProviders[capability];
                log.Error(
                    $"Runtime capability '{capability}' has ambiguous providers " +
                    $"'{existing}' and '{descriptor.Id}'. Both extension roots are " +
                    "quarantined for this GTA session.");
                rejected.Add(descriptor.Id);
                if (!existing.Equals(
                        "CommunityScriptHookVDotNetCore",
                        StringComparison.OrdinalIgnoreCase))
                {
                    rejected.Add(existing);
                }
            }
        }

        RejectUndeclaredRootReferences(snapshot, discovered, rejected);

        foreach (RuntimeExtensionDescriptor descriptor in discovered)
        {
            if (rejected.Contains(descriptor.Id))
            {
                MarkUnavailable(descriptor);
            }
        }

        _pendingInitialization.AddRange(
            discovered
                .Where(value => !rejected.Contains(value.Id))
                .OrderBy(value => value.Id, StringComparer.OrdinalIgnoreCase));

        _initializationPrepared = true;
        log.Information(
            $"Prepared {_pendingInitialization.Count} root runtime extension(s) " +
            $"for frame-driven initialization with at most " +
            $"{MaximumConcurrentInitializations} concurrent lifecycle operation(s).");
    }

    public void AdvanceInitialization()
    {
        ObserveRetirements();
        if (_shutdown || _initializationCompleted)
        {
            return;
        }
        if (!_initializationPrepared)
        {
            throw new InvalidOperationException(
                "Runtime-extension initialization has not been prepared.");
        }

        ObserveInitializing();
        StartReadyInitializations();
        ObserveInitializing();

        if (_initializing.Count != 0)
        {
            return;
        }

        if (_pendingInitialization.Count != 0)
        {
            QuarantineUnsatisfiedPending();
        }

        if (_pendingInitialization.Count == 0)
        {
            _initializationCompleted = true;
            log.Information(
                $"Activated {_active.Count} root runtime extension(s); " +
                $"quarantined {_unavailableAssemblies.Count} root assembly(ies).");
        }
    }

    private void ObserveInitializing()
    {
        for (int index = _initializing.Count - 1; index >= 0; --index)
        {
            PendingRuntimeExtension pending = _initializing[index];
            ManagedLifecycleOperationSnapshot snapshot =
                pending.Operation.Poll();
            if (snapshot.Status is ManagedLifecycleOperationStatus.Pending)
            {
                continue;
            }

            _initializing.RemoveAt(index);
            ManagedLifecycleAuthority authority = pending.Operation.Authority;
            Task initializationCompletion = pending.Operation.Completion;
            pending.Operation.Dispose();

            switch (snapshot.Status)
            {
                case ManagedLifecycleOperationStatus.Succeeded:
                    _active.Add(new(
                        pending.Descriptor,
                        pending.Instance,
                        authority));
                    foreach (string capability in pending.Descriptor.Provides)
                    {
                        _availableCapabilities.Add(capability);
                    }
                    log.Information(
                        $"Runtime extension '{pending.Descriptor.Id}' initialized " +
                        "for its one root lifecycle in this GTA process.");
                    break;

                case ManagedLifecycleOperationStatus.TimedOut:
                    MarkUnavailable(pending.Descriptor);
                    BeginRetirement(
                        pending.Descriptor,
                        pending.Instance,
                        "initialization timeout",
                        initializationCompletion);
                    log.Error(
                        $"Runtime extension '{pending.Descriptor.Id}' exceeded its " +
                        $"{RootInitializationTimeout.TotalSeconds:0.###} second " +
                        "initialization deadline and was quarantined independently.");
                    break;

                case ManagedLifecycleOperationStatus.Cancelled:
                    MarkUnavailable(pending.Descriptor);
                    BeginRetirement(
                        pending.Descriptor,
                        pending.Instance,
                        "initialization cancellation",
                        initializationCompletion);
                    log.Error(
                        $"Runtime extension '{pending.Descriptor.Id}' initialization " +
                        "was cancelled and the extension was quarantined.");
                    break;

                case ManagedLifecycleOperationStatus.Faulted:
                    MarkUnavailable(pending.Descriptor);
                    BeginRetirement(
                        pending.Descriptor,
                        pending.Instance,
                        "initialization fault",
                        initializationCompletion);
                    log.Error(
                        $"Runtime extension '{pending.Descriptor.Id}' could not " +
                        "initialize and will not be retried before the game restarts: " +
                        snapshot.Exception);
                    break;
            }
        }
    }

    private void StartReadyInitializations()
    {
        while (_initializing.Count < MaximumConcurrentInitializations)
        {
            int readyIndex = _pendingInitialization.FindIndex(
                descriptor => descriptor.Requires.All(
                    _availableCapabilities.Contains));
            if (readyIndex < 0)
            {
                return;
            }

            RuntimeExtensionDescriptor descriptor =
                _pendingInitialization[readyIndex];
            _pendingInitialization.RemoveAt(readyIndex);

            PendingRuntimeExtension? pending = TryBeginInitialize(descriptor);
            if (pending is null)
            {
                MarkUnavailable(descriptor);
                continue;
            }
            _initializing.Add(pending);
        }
    }

    private PendingRuntimeExtension? TryBeginInitialize(
        RuntimeExtensionDescriptor descriptor)
    {
        IScript4RuntimeExtension? instance = null;
        try
        {
            Assembly assembly = _resolver?.LoadRootAssembly(descriptor.AssemblyName)
                ?? throw new InvalidOperationException(
                    $"Root extension image '{descriptor.AssemblyName}' is unavailable in the committed RAM snapshot.");
            Type entryType = assembly.GetType(
                descriptor.EntryType,
                throwOnError: true,
                ignoreCase: false)!;
            object? created = Activator.CreateInstance(entryType, nonPublic: true);
            if (created is not IScript4RuntimeExtension extension)
            {
                throw new InvalidOperationException(
                    $"Entry type '{descriptor.EntryType}' does not implement " +
                    $"{nameof(IScript4RuntimeExtension)} from the active contract assembly.");
            }
            instance = extension;

            bool nativeAuthority = descriptor.Provides.Contains(
                "native.call.admission",
                StringComparer.OrdinalIgnoreCase);
            bool gameThreadFunctionAuthority = descriptor.Requires.Contains(
                RuntimeCapabilities.GameThreadFunctions,
                StringComparer.OrdinalIgnoreCase);
            IRuntimeServiceRegistry ownerServices = services.CreateOwnerScope(
                descriptor.Id,
                nativeAuthority,
                gameThreadFunctionAuthority);
            RuntimeExtensionContext context = new(
                rootDirectory,
                scriptsDirectory,
                ownerServices,
                new RuntimeExtensionDependencyScope(this, descriptor.Id));

            ManagedLifecycleOperation operation = ManagedLifecycleOperation.Start(
                token => instance.InitializeAsync(context, token),
                RootInitializationTimeout);

            log.Information(
                $"Runtime extension '{descriptor.Id}' initialization started with " +
                $"its own {RootInitializationTimeout.TotalSeconds:0.###} second deadline.");

            return new(descriptor, instance, operation);
        }
        catch (Exception exception)
        {
            MarkUnavailable(descriptor);
            if (instance is not null)
            {
                BeginRetirement(descriptor, instance, "initialization setup fault");
            }
            log.Error(
                $"Runtime extension '{descriptor.Id}' could not begin initialization " +
                $"and will not be retried before the game restarts: {exception}");
            return null;
        }
    }

    private void QuarantineUnsatisfiedPending()
    {
        foreach (RuntimeExtensionDescriptor descriptor in _pendingInitialization)
        {
            string missing = string.Join(
                ", ",
                descriptor.Requires.Where(requirement =>
                    !_availableCapabilities.Contains(requirement)));
            log.Error(
                $"Runtime extension '{descriptor.Id}' is quarantined for " +
                $"this GTA session. Unsatisfied capabilities: {missing}.");
            MarkUnavailable(descriptor);
        }
        _pendingInitialization.Clear();
    }

    private void RejectUndeclaredRootReferences(
        RootAssemblySnapshot snapshot,
        IReadOnlyList<RuntimeExtensionDescriptor> discovered,
        HashSet<string> rejected)
    {
        Dictionary<string, RuntimeExtensionDescriptor> rootsByAssembly = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (RuntimeExtensionDescriptor descriptor in discovered)
        {
            rootsByAssembly.TryAdd(descriptor.AssemblyName, descriptor);
        }

        Dictionary<string, string[]> referencesByAssembly = snapshot.Images
            .ToDictionary(
                image => image.AssemblyName,
                ReadReferencedAssemblyNames,
                StringComparer.OrdinalIgnoreCase);

        foreach (RuntimeExtensionDescriptor consumer in discovered)
        {
            Queue<string> pending = new(consumer.ReferencedAssemblyNames);
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            while (pending.TryDequeue(out string? reference))
            {
                if (!visited.Add(reference) ||
                    reference.Equals(
                        consumer.AssemblyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (rootsByAssembly.TryGetValue(
                        reference,
                        out RuntimeExtensionDescriptor? provider))
                {
                    if (provider.Id.Equals(
                            consumer.Id,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool declared = consumer.Requires.Any(requirement =>
                            provider.Provides.Contains(
                                requirement,
                                StringComparer.OrdinalIgnoreCase)) ||
                        consumer.ConditionalRequires.Any(requirement =>
                            provider.Provides.Contains(
                                requirement,
                                StringComparer.OrdinalIgnoreCase));
                    if (declared)
                    {
                        continue;
                    }

                    rejected.Add(consumer.Id);
                    string capabilities = provider.Provides.Count == 0
                        ? "none"
                        : string.Join(", ", provider.Provides);
                    log.Error(
                        $"Runtime extension '{consumer.Id}' has a compile-time " +
                        $"reference closure that reaches root extension assembly " +
                        $"'{provider.AssemblyName}' ({provider.Id}) without declaring " +
                        "a static or conditional capability dependency on that provider. " +
                        $"Provider capabilities: [{capabilities}]. The consumer is " +
                        "quarantined so helper assemblies cannot bypass the " +
                        "declared dependency lifecycle graph.");
                    break;
                }

                if (!referencesByAssembly.TryGetValue(
                        reference,
                        out string[]? nestedReferences))
                {
                    continue;
                }

                foreach (string nested in nestedReferences)
                {
                    pending.Enqueue(nested);
                }
            }
        }
    }

    private static string[] ReadReferencedAssemblyNames(
        RootAssemblyImage image)
    {
        using MemoryStream stream = new(image.Assembly, writable: false);
        using PEReader pe = new(stream, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata)
        {
            return [];
        }

        MetadataReader metadata = pe.GetMetadataReader();
        HashSet<string> references = new(StringComparer.OrdinalIgnoreCase);
        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
        {
            AssemblyReference reference = metadata.GetAssemblyReference(handle);
            string name = metadata.GetString(reference.Name);
            if (!string.IsNullOrWhiteSpace(name))
            {
                references.Add(name);
            }
        }

        return [.. references.OrderBy(
            value => value,
            StringComparer.OrdinalIgnoreCase)];
    }

    public void AdvanceHostFrame(RuntimeExtensionFrameContext context)
    {
        ObserveRetirements();
        if (!_initializationCompleted)
        {
            return;
        }

        ActiveRuntimeExtension[] frame = [.. _active];
        foreach (ActiveRuntimeExtension active in frame)
        {
            if (active.Unavailable)
            {
                continue;
            }

            try
            {
                using IDisposable scope =
                    ManagedLifecycleAuthorityContext.Enter(active.Authority);
                active.Instance.AdvanceHostFrame(context);
            }
            catch (Exception exception)
            {
                log.Error(
                    $"Runtime extension '{active.Descriptor.Id}' faulted while " +
                    $"advancing a host frame: {exception}");
                Quarantine(active.Descriptor.Id, "host-frame fault");
            }
        }
        ObserveRetirements();
    }

    internal IReadOnlyList<string> DrainNewUnavailableAssemblyNames()
    {
        List<string> result = [];
        while (_newlyUnavailableAssemblies.TryDequeue(out string? value))
        {
            result.Add(value);
        }
        return result.AsReadOnly();
    }

    public async Task ShutdownAsync()
    {
        if (!_shutdown)
        {
            _shutdown = true;

            foreach (PendingRuntimeExtension pending in _initializing)
            {
                Task initializationCompletion = pending.Operation.Completion;
                pending.Operation.Cancel();
                pending.Operation.Dispose();
                MarkUnavailable(pending.Descriptor);
                BeginRetirement(
                    pending.Descriptor,
                    pending.Instance,
                    "runtime shutdown during initialization",
                    initializationCompletion);
            }
            _initializing.Clear();

            foreach (RuntimeExtensionDescriptor descriptor in _pendingInitialization)
            {
                MarkUnavailable(descriptor);
            }
            _pendingInitialization.Clear();

            ActiveRuntimeExtension[] active = [.. _active];
            foreach (ActiveRuntimeExtension extension in active)
            {
                extension.Unavailable = true;
                extension.Authority.ForceRevoke();
                foreach (string capability in extension.Descriptor.Provides)
                {
                    _availableCapabilities.Remove(capability);
                }
                MarkUnavailable(extension.Descriptor);
            }
            _active.Clear();

            foreach (ActiveRuntimeExtension extension in active)
            {
                BeginRetirement(
                    extension.Descriptor,
                    extension.Instance,
                    "runtime shutdown");
            }
        }

        await DrainRetirementsAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        ShutdownAsync().GetAwaiter().GetResult();
        _resolver?.Dispose();
        _resolver = null;
    }


    private void RequireConditionalCapability(string extensionId, string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        if (Environment.CurrentManagedThreadId != _runtimeThreadId)
        {
            throw new InvalidOperationException(
                "Conditional runtime dependencies must be activated on the " +
                "managed runtime frame thread.");
        }

        ActiveRuntimeExtension? active = _active.FirstOrDefault(value =>
            !value.Unavailable &&
            value.Descriptor.Id.Equals(
                extensionId,
                StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(
                $"Runtime extension '{extensionId}' can activate conditional " +
                "dependencies only while its root lifecycle is active.");
        string normalized = capability.Trim();
        if (!active.Descriptor.ConditionalRequires.Contains(
                normalized,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Runtime extension '{extensionId}' did not declare conditional " +
                $"capability dependency '{normalized}'.");
        }
        if (!_availableCapabilities.Contains(normalized))
        {
            throw new InvalidOperationException(
                $"Conditional runtime capability '{normalized}' is unavailable " +
                $"to extension '{extensionId}'.");
        }

        active.RuntimeRequirements.Add(normalized);
    }

    private void Quarantine(string extensionId, string reason)
    {
        HashSet<string> extensionIds = new(
            StringComparer.OrdinalIgnoreCase)
        {
            extensionId
        };
        HashSet<string> removedCapabilities = new(
            StringComparer.OrdinalIgnoreCase);

        bool changed;
        do
        {
            changed = false;
            foreach (ActiveRuntimeExtension active in _active)
            {
                if (active.Unavailable)
                {
                    continue;
                }

                if (extensionIds.Contains(active.Descriptor.Id))
                {
                    foreach (string capability in active.Descriptor.Provides)
                    {
                        changed |= removedCapabilities.Add(capability);
                    }
                    continue;
                }

                if ((active.Descriptor.Requires.Any(removedCapabilities.Contains) ||
                     active.RuntimeRequirements.Any(removedCapabilities.Contains)) &&
                    extensionIds.Add(active.Descriptor.Id))
                {
                    changed = true;
                    foreach (string capability in active.Descriptor.Provides)
                    {
                        removedCapabilities.Add(capability);
                    }
                }
            }
        }
        while (changed);

        ActiveRuntimeExtension[] affected = [.. _active.Where(active =>
            !active.Unavailable && extensionIds.Contains(active.Descriptor.Id))];
        if (affected.Length == 0)
        {
            return;
        }

        foreach (ActiveRuntimeExtension active in affected)
        {
            active.Unavailable = true;
            active.Authority.ForceRevoke();
            foreach (string capability in active.Descriptor.Provides)
            {
                _availableCapabilities.Remove(capability);
            }
            MarkUnavailable(active.Descriptor);
        }
        foreach (ActiveRuntimeExtension active in affected)
        {
            _active.Remove(active);
        }
        foreach (ActiveRuntimeExtension active in affected)
        {
            BeginRetirement(active.Descriptor, active.Instance, reason);
            log.Error(
                $"Runtime extension '{active.Descriptor.Id}' is permanently " +
                "unavailable until GTA restarts.");
        }
    }

    private void MarkUnavailable(RuntimeExtensionDescriptor descriptor)
    {
        if (_unavailableAssemblies.Add(descriptor.AssemblyName))
        {
            _newlyUnavailableAssemblies.Enqueue(descriptor.AssemblyName);
        }
        services.RevokeOwner(descriptor.Id);
    }

    private void BeginRetirement(
        RuntimeExtensionDescriptor descriptor,
        IScript4RuntimeExtension instance,
        string reason,
        Task? predecessor = null)
    {
        if (_retiring.Any(value => value.Descriptor.Id.Equals(
                descriptor.Id,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _retiring.Add(new(
            descriptor,
            reason,
            ManagedRetirementOperation.Start(
                instance.ShutdownAsync,
                predecessor)));
        log.Information(
            $"Runtime extension '{descriptor.Id}' entered asynchronous retirement; " +
            $"reason={reason}.");
    }

    private void ObserveRetirements()
    {
        for (int index = _retiring.Count - 1; index >= 0; --index)
        {
            RetiringRuntimeExtension retiring = _retiring[index];
            ManagedRetirementOperationSnapshot snapshot = retiring.Operation.Poll();
            if (snapshot.Status is ManagedRetirementOperationStatus.Pending)
            {
                continue;
            }

            _retiring.RemoveAt(index);
            if (snapshot.Status is ManagedRetirementOperationStatus.Succeeded)
            {
                log.Information(
                    $"Runtime extension '{retiring.Descriptor.Id}' completed cleanup; " +
                    $"reason={retiring.Reason}.");
                continue;
            }

            log.Error(
                $"Runtime extension '{retiring.Descriptor.Id}' cleanup faulted after " +
                $"retirement; reason={retiring.Reason}: {snapshot.Exception}");
        }
    }

    private async Task DrainRetirementsAsync()
    {
        for (;;)
        {
            ObserveRetirements();
            if (_retiring.Count == 0)
            {
                return;
            }

            Task[] pending = [.. _retiring
                .Select(value => value.Operation.Completion)
                .Where(task => !task.IsCompleted)];
            if (pending.Length == 0)
            {
                continue;
            }
            await Task.WhenAny(pending).ConfigureAwait(false);
        }
    }

    private sealed record PendingRuntimeExtension(
        RuntimeExtensionDescriptor Descriptor,
        IScript4RuntimeExtension Instance,
        ManagedLifecycleOperation Operation);

    private sealed record RetiringRuntimeExtension(
        RuntimeExtensionDescriptor Descriptor,
        string Reason,
        ManagedRetirementOperation Operation);

    private sealed class ActiveRuntimeExtension(
        RuntimeExtensionDescriptor descriptor,
        IScript4RuntimeExtension instance,
        ManagedLifecycleAuthority authority)
    {
        public RuntimeExtensionDescriptor Descriptor { get; } = descriptor;
        public IScript4RuntimeExtension Instance { get; } = instance;
        public ManagedLifecycleAuthority Authority { get; } = authority;
        public HashSet<string> RuntimeRequirements { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool Unavailable { get; set; }
    }

    private sealed class RuntimeExtensionDependencyScope(
        RuntimeExtensionManager owner,
        string extensionId) : IRuntimeExtensionDependencies
    {
        public void Require(string capability) =>
            owner.RequireConditionalCapability(extensionId, capability);
    }
}

internal enum ManagedLifecycleOperationStatus
{
    Pending,
    Succeeded,
    TimedOut,
    Cancelled,
    Faulted
}

internal readonly record struct ManagedLifecycleOperationSnapshot(
    ManagedLifecycleOperationStatus Status,
    Exception? Exception = null);

internal sealed class ManagedLifecycleAuthority
{
    private int _state;

    internal bool AllowsNativeCalls => Volatile.Read(ref _state) != 2;

    internal bool TryCommit() =>
        Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal void RevokeIfActive() =>
        Interlocked.CompareExchange(ref _state, 2, 0);

    internal void ForceRevoke() =>
        Interlocked.Exchange(ref _state, 2);
}

internal static class ManagedLifecycleAuthorityContext
{
    private static readonly AsyncLocal<ManagedLifecycleAuthority?> Current = new();

    internal static bool AllowsNativeCalls =>
        Current.Value?.AllowsNativeCalls ?? true;

    internal static IDisposable Enter(ManagedLifecycleAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ManagedLifecycleAuthority? previous = Current.Value;
        Current.Value = authority;
        return new Scope(previous);
    }

    private sealed class Scope(
        ManagedLifecycleAuthority? previous) : IDisposable
    {
        private ManagedLifecycleAuthority? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Current.Value = _previous;
            _previous = null;
        }
    }
}

internal sealed class ManagedLifecycleOperation : IDisposable
{
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _combined;
    private readonly CancellationToken _externalCancellation;
    private readonly CancellationTokenRegistration _revocationRegistration;
    private readonly ManagedLifecycleAuthority _authority = new();
    private readonly Task _operation;
    private int _disposed;

    private ManagedLifecycleOperation(
        Func<CancellationToken, Task> operationFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationFactory);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "A managed lifecycle deadline must be positive.");
        }

        _externalCancellation = cancellationToken;
        _timeout = new();
        _timeout.CancelAfter(timeout);
        _combined = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(
                _timeout.Token,
                cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token);

        _revocationRegistration = _combined.Token.Register(
            static state => ((ManagedLifecycleAuthority)state!).RevokeIfActive(),
            _authority);

        CancellationToken operationToken = _combined.Token;
        _operation = Task.Run(
            async () =>
            {
                using IDisposable scope =
                    ManagedLifecycleAuthorityContext.Enter(_authority);
                operationToken.ThrowIfCancellationRequested();
                await operationFactory(operationToken).ConfigureAwait(false);
                if (!_authority.TryCommit())
                {
                    operationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(operationToken);
                }
            },
            operationToken);
    }

    internal static ManagedLifecycleOperation Start(
        Func<CancellationToken, Task> operationFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        new(operationFactory, timeout, cancellationToken);

    internal ManagedLifecycleAuthority Authority => _authority;

    internal Task Completion => _operation;

    internal ManagedLifecycleOperationSnapshot Poll()
    {
        if (!_operation.IsCompleted)
        {
            if (_timeout.IsCancellationRequested)
            {
                return new(ManagedLifecycleOperationStatus.TimedOut);
            }
            if (_externalCancellation.IsCancellationRequested)
            {
                return new(ManagedLifecycleOperationStatus.Cancelled);
            }
            return new(ManagedLifecycleOperationStatus.Pending);
        }

        if (_operation.IsCompletedSuccessfully)
        {
            return new(ManagedLifecycleOperationStatus.Succeeded);
        }

        if (_operation.IsCanceled)
        {
            return new(
                _timeout.IsCancellationRequested
                    ? ManagedLifecycleOperationStatus.TimedOut
                    : ManagedLifecycleOperationStatus.Cancelled);
        }

        Exception exception = _operation.Exception switch
        {
            { InnerException: not null } aggregate => aggregate.InnerException,
            { } aggregate => aggregate,
            null => new InvalidOperationException(
                "The managed lifecycle operation faulted without an exception.")
        };
        return new(
            ManagedLifecycleOperationStatus.Faulted,
            exception);
    }

    internal void Cancel()
    {
        _authority.RevokeIfActive();
        try
        {
            _combined.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _authority.RevokeIfActive();
        if (!_operation.IsCompleted)
        {
            Cancel();
            _ = _operation.ContinueWith(
                static (task, state) =>
                {
                    ManagedLifecycleOperation owner =
                        (ManagedLifecycleOperation)state!;
                    _ = task.Exception;
                    owner.DisposeResources();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        _ = _operation.Exception;
        DisposeResources();
    }

    private void DisposeResources()
    {
        _revocationRegistration.Dispose();
        _combined.Dispose();
        _timeout.Dispose();
    }
}

internal enum ManagedRetirementOperationStatus
{
    Pending,
    Succeeded,
    Faulted
}

internal readonly record struct ManagedRetirementOperationSnapshot(
    ManagedRetirementOperationStatus Status,
    Exception? Exception = null);

internal sealed class ManagedRetirementOperation
{
    private readonly Task _completion;

    private ManagedRetirementOperation(
        Func<Task> operationFactory,
        Task? predecessor)
    {
        ArgumentNullException.ThrowIfNull(operationFactory);
        ManagedLifecycleAuthority authority = new();
        authority.ForceRevoke();
        _completion = Task.Run(
            async () =>
            {
                if (predecessor is not null)
                {
                    try
                    {
                        await predecessor.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                using IDisposable scope =
                    ManagedLifecycleAuthorityContext.Enter(authority);
                await operationFactory().ConfigureAwait(false);
            });
    }

    internal static ManagedRetirementOperation Start(
        Func<Task> operationFactory,
        Task? predecessor = null) =>
        new(operationFactory, predecessor);

    internal Task Completion => _completion;

    internal ManagedRetirementOperationSnapshot Poll()
    {
        if (!_completion.IsCompleted)
        {
            return new(ManagedRetirementOperationStatus.Pending);
        }
        if (_completion.IsCompletedSuccessfully)
        {
            return new(ManagedRetirementOperationStatus.Succeeded);
        }

        Exception exception = _completion.Exception switch
        {
            { InnerException: not null } aggregate => aggregate.InnerException,
            { } aggregate => aggregate,
            null => new InvalidOperationException(
                "The managed retirement operation faulted without an exception.")
        };
        return new(
            ManagedRetirementOperationStatus.Faulted,
            exception);
    }
}

internal sealed record RootAssemblyImage(
    string Path,
    string AssemblyName,
    byte[] Assembly,
    byte[]? Symbols);

internal sealed class RootAssemblySnapshot
{
    private readonly Dictionary<string, RootAssemblyImage> _images;

    private RootAssemblySnapshot(
        Dictionary<string, RootAssemblyImage> images)
    {
        _images = images;
    }

    public IReadOnlyCollection<RootAssemblyImage> Images =>
        _images.Values;

    public bool TryGet(
        string simpleName,
        [NotNullWhen(true)] out RootAssemblyImage? image) =>
        _images.TryGetValue(simpleName, out image);

    public static RootAssemblySnapshot Capture(
        string rootDirectory,
        string excludedAssemblyName,
        RuntimeLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(excludedAssemblyName);
        ArgumentNullException.ThrowIfNull(log);

        Dictionary<string, RootAssemblyImage> images =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory
                     .EnumerateFiles(
                         rootDirectory,
                         "*.dll",
                         SearchOption.TopDirectoryOnly)
                     .OrderBy(
                         value => value,
                         StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                byte[] assemblyBytes = ReadStableImage(fullPath);
                string assemblyName = ReadAssemblyName(assemblyBytes);
                if (assemblyName.Equals(
                        excludedAssemblyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string pdbPath = Path.ChangeExtension(fullPath, ".pdb");
                byte[]? symbols = File.Exists(pdbPath)
                    ? TryReadStableImage(pdbPath)
                    : null;
                if (!images.TryAdd(
                        assemblyName,
                        new(
                            fullPath,
                            assemblyName,
                            assemblyBytes,
                            symbols)))
                {
                    throw new InvalidOperationException(
                        $"More than one root assembly has simple name '{assemblyName}'.");
                }
            }
            catch (BadImageFormatException)
            {
            }
            catch (IOException exception)
            {
                log.Warning(
                    $"Root assembly '{Path.GetFileName(path)}' could not be " +
                    $"captured into RAM: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                log.Warning(
                    $"Root assembly '{Path.GetFileName(path)}' could not be " +
                    $"captured into RAM: {exception.Message}");
            }
        }

        log.Information(
            $"Committed {images.Count} root managed assembly image(s) to one immutable RAM snapshot.");
        return new(images);
    }

    private static byte[] ReadStableImage(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        byte[] image = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(image);
        return image;
    }

    private static byte[]? TryReadStableImage(string path)
    {
        try
        {
            return ReadStableImage(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadAssemblyName(byte[] image)
    {
        using MemoryStream stream = new(image, writable: false);
        using System.Reflection.PortableExecutable.PEReader pe = new(
            stream,
            System.Reflection.PortableExecutable.PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata)
        {
            throw new BadImageFormatException(
                "The root image is not a managed assembly.");
        }

        MetadataReader metadata = pe.GetMetadataReader();
        string name = metadata.GetString(
            metadata.GetAssemblyDefinition().Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BadImageFormatException(
                "The root managed assembly has no simple name.");
        }
        return name;
    }
}

internal sealed class RootAssemblyResolver : IDisposable
{
    private readonly AssemblyLoadContext _context;
    private readonly Assembly _contractAssembly;
    private readonly string _contractAssemblyName;
    private readonly RuntimeLog _log;
    private readonly Lock _gate = new();
    private RootAssemblySnapshot? _snapshot;
    private bool _disposed;

    public RootAssemblyResolver(
        RootAssemblySnapshot snapshot,
        AssemblyLoadContext context,
        Assembly contractAssembly,
        RuntimeLog log)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(contractAssembly);
        ArgumentNullException.ThrowIfNull(log);

        _context = context;
        _contractAssembly = contractAssembly;
        _contractAssemblyName = contractAssembly.GetName().Name
            ?? throw new InvalidOperationException(
                "The runtime-extension contract assembly has no simple name.");
        _log = log;
        _snapshot = snapshot;

        _context.Resolving += Resolve;
        _log.Information(
            $"Root resolver activated from {snapshot.Images.Count} committed RAM image(s).");
    }

    public Assembly LoadRootAssembly(string simpleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simpleName);
        ThrowIfDisposed();
        Assembly? loaded = FindLoaded(simpleName);
        if (loaded is not null)
        {
            return loaded;
        }
        RootAssemblySnapshot snapshot = _snapshot
            ?? throw new ObjectDisposedException(nameof(RootAssemblyResolver));
        if (!snapshot.TryGet(simpleName, out RootAssemblyImage? image))
        {
            throw new FileNotFoundException(
                $"Root managed assembly '{simpleName}' is absent from the RAM snapshot.");
        }
        return LoadImage(image);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _context.Resolving -= Resolve;
        _snapshot = null;
    }

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName requested)
    {
        if (_disposed || string.IsNullOrWhiteSpace(requested.Name))
        {
            return null;
        }
        if (requested.Name.Equals(
                _contractAssemblyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return _contractAssembly;
        }

        Assembly? loaded = FindLoaded(requested.Name);
        if (loaded is not null)
        {
            return loaded;
        }
        RootAssemblySnapshot? snapshot = _snapshot;
        if (snapshot is null ||
            !snapshot.TryGet(requested.Name, out RootAssemblyImage? image))
        {
            return null;
        }

        lock (_gate)
        {
            loaded = FindLoaded(requested.Name);
            if (loaded is not null)
            {
                return loaded;
            }
            try
            {
                return LoadImage(image);
            }
            catch (Exception exception)
            {
                _log.Error(
                    $"Root dependency '{requested}' could not be loaded from its " +
                    $"committed RAM image: {exception.Message}");
                return null;
            }
        }
    }

    private Assembly LoadImage(RootAssemblyImage image)
    {
        if (image.Symbols is not null)
        {
            try
            {
                using MemoryStream assembly = new(image.Assembly, writable: false);
                using MemoryStream symbols = new(image.Symbols, writable: false);
                return _context.LoadFromStream(assembly, symbols);
            }
            catch (BadImageFormatException)
            {
            }
        }

        using MemoryStream imageStream = new(image.Assembly, writable: false);
        return _context.LoadFromStream(imageStream);
    }

    private Assembly? FindLoaded(string simpleName) =>
        _context.Assemblies.FirstOrDefault(assembly =>
            assembly.GetName().Name?.Equals(
                simpleName,
                StringComparison.OrdinalIgnoreCase) == true);

    private void ThrowIfDisposed()
    {
        if (!_disposed)
        {
            return;
        }
        throw new ObjectDisposedException(nameof(RootAssemblyResolver));
    }
}