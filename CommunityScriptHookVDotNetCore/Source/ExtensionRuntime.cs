using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace CommunityScriptHookVDotNetCore.Source;

internal enum RuntimeExtensionReloadState
{
    Idle,
    Retiring,
    Initializing,
    Completed,
    Failed
}

internal sealed record RuntimeExtensionReloadPlan(
    RootAssemblySnapshot Snapshot,
    IReadOnlyList<RuntimeExtensionDescriptor> NextDescriptors,
    IReadOnlyList<RuntimeExtensionDescriptor> PreviousDescriptors,
    IReadOnlyList<string> TargetIds);

internal readonly record struct RuntimeExtensionReloadSnapshot(
    RuntimeExtensionReloadState State,
    int TargetCount,
    string? Diagnostic)
{
    public bool IsTerminal =>
        State is RuntimeExtensionReloadState.Completed or
            RuntimeExtensionReloadState.Failed;
}

internal interface IRuntimeExtensionReloadController
{
    RuntimeExtensionReloadPlan CaptureReloadPlan();
    void BeginReload(RuntimeExtensionReloadPlan plan);
    void AdvanceReload();
    RuntimeExtensionReloadSnapshot ReloadSnapshot { get; }
    IReadOnlyCollection<string> UnavailableAssemblyNames { get; }
}

internal sealed class RuntimeExtensionManager(
    string rootDirectory,
    string extensionsDirectory,
    string scriptsDirectory,
    RuntimeServiceRegistry services,
    RuntimeLog log,
    RuntimeDiagnosticHub diagnostics) : IDisposable, IRuntimeExtensionReloadController
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

    private static readonly string[] CoreCapabilities =
    [
        RuntimeCapabilities.RuntimeServices,
        RuntimeCapabilities.RuntimeDiagnostics,
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
        [with(CoreCapabilities, StringComparer.OrdinalIgnoreCase)];
    private readonly HashSet<string> _unavailableAssemblies =
        [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Queue<string> _newlyUnavailableAssemblies = [];
    private RootAssemblyResolver? _resolver;
    private RuntimeExtensionReloadPlan? _reloadPlan;
    private RuntimeExtensionReloadState _reloadState;
    private string? _reloadDiagnostic;
    private bool _initializationPrepared;
    private bool _initializationCompleted;
    private bool _shutdown;

    internal IReadOnlyCollection<string> UnavailableAssemblyNames =>
        _unavailableAssemblies;

    internal bool InitializationCompleted => _initializationCompleted;

    RuntimeExtensionReloadSnapshot IRuntimeExtensionReloadController.ReloadSnapshot =>
        new(
            _reloadState,
            _reloadPlan?.TargetIds.Count ?? 0,
            _reloadDiagnostic);

    IReadOnlyCollection<string> IRuntimeExtensionReloadController.UnavailableAssemblyNames =>
        _unavailableAssemblies;

    public void PrepareInitialization()
    {
        if (_initializationPrepared)
        {
            throw new InvalidOperationException(
                "Runtime-extension initialization was already prepared.");
        }

        RuntimeExtensionPlacement.Reconcile(
            rootDirectory,
            extensionsDirectory,
            log);
        RootAssemblySnapshot snapshot = RootAssemblySnapshot.Capture(
            extensionsDirectory,
            ContractAssemblyName,
            log);
        IReadOnlyList<RuntimeExtensionDescriptor> discovered =
            RuntimeExtensionDiscovery.Discover(snapshot, log);
        PrepareGeneration(snapshot, discovered, reviveOwners: false);

        _initializationPrepared = true;
        log.Information(
            $"Prepared {_pendingInitialization.Count} runtime extension(s) " +
            $"from '{extensionsDirectory}' with at most " +
            $"{MaximumConcurrentInitializations} concurrent lifecycle operation(s).");
    }

    RuntimeExtensionReloadPlan IRuntimeExtensionReloadController.CaptureReloadPlan()
    {
        EnsureRuntimeThread();
        if (!_initializationCompleted || _reloadState is not RuntimeExtensionReloadState.Idle and
            not RuntimeExtensionReloadState.Completed and not RuntimeExtensionReloadState.Failed)
        {
            throw new InvalidOperationException(
                "Runtime extensions cannot be captured while another lifecycle operation is active.");
        }

        RuntimeExtensionPlacement.Reconcile(
            rootDirectory,
            extensionsDirectory,
            log);
        RootAssemblySnapshot snapshot = RootAssemblySnapshot.Capture(
            extensionsDirectory,
            ContractAssemblyName,
            log);
        IReadOnlyList<RuntimeExtensionDescriptor> discovered =
            RuntimeExtensionDiscovery.Discover(snapshot, log);
        IReadOnlyList<RuntimeExtensionDescriptor> previous =
            Array.AsReadOnly([.. _active.Select(value => value.Descriptor)]);
        IReadOnlyList<string> targets = Array.AsReadOnly(
            [.. previous.Select(value => value.Id)
                .Concat(discovered.Select(value => value.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)]);
        return new(snapshot, discovered, previous, targets);
    }

    void IRuntimeExtensionReloadController.BeginReload(
        RuntimeExtensionReloadPlan plan)
    {
        EnsureRuntimeThread();
        ArgumentNullException.ThrowIfNull(plan);
        if (!_shutdown)
        {
            if (_reloadState is RuntimeExtensionReloadState.Retiring or
                RuntimeExtensionReloadState.Initializing)
            {
                throw new InvalidOperationException(
                    "A runtime-extension reload is already active.");
            }
            if (_initializing.Count != 0 || _pendingInitialization.Count != 0)
            {
                throw new InvalidOperationException(
                    "Runtime-extension initialization is not quiescent.");
            }

            _reloadPlan = plan;
            _reloadDiagnostic = null;
            _reloadState = RuntimeExtensionReloadState.Retiring;

            Task? predecessor = null;
            ActiveRuntimeExtension[] retiring = [.. _active.AsEnumerable().Reverse()];
            foreach (ActiveRuntimeExtension active in retiring)
            {
                active.Unavailable = true;
                active.Authority.ForceRevoke();
                foreach (string capability in active.Descriptor.Provides)
                {
                    _availableCapabilities.Remove(capability);
                }
                services.RevokeOwner(active.Descriptor.Id);
                predecessor = BeginRetirement(
                    active.Descriptor,
                    active.Instance,
                    "runtime-extension reload",
                    predecessor);
            }
            _active.Clear();
            log.Information(
                $"Runtime-extension reload queued {plan.TargetIds.Count} extension index(es). " +
                "All extension binaries were captured before retirement began.");
        }
        else
        {
            throw new ObjectDisposedException(nameof(RuntimeExtensionManager));
        }
    }

    void IRuntimeExtensionReloadController.AdvanceReload()
    {
        EnsureRuntimeThread();
        try
        {
            AdvanceReloadCore();
        }
        catch (Exception exception)
        {
            _reloadDiagnostic = exception.ToString();
            _reloadState = RuntimeExtensionReloadState.Failed;
            log.Error(
                $"Runtime-extension reload failed: {exception}",
                source: "CommunityScriptHookVDotNetCore",
                origin: nameof(AdvanceReloadCore));
        }
    }

    private void AdvanceReloadCore()
    {
        if (_reloadState is RuntimeExtensionReloadState.Idle or
            RuntimeExtensionReloadState.Completed or
            RuntimeExtensionReloadState.Failed)
        {
            return;
        }

        ObserveRetirements();
        if (_reloadState is RuntimeExtensionReloadState.Retiring)
        {
            if (_retiring.Count != 0)
            {
                return;
            }

            RuntimeExtensionReloadPlan plan = _reloadPlan
                ?? throw new InvalidOperationException(
                    "The runtime-extension reload plan is unavailable.");
            _resolver?.Dispose();
            _resolver = null;

            HashSet<string> nextIds = [with(
                plan.NextDescriptors.Select(value => value.Id),
                StringComparer.OrdinalIgnoreCase)];
            foreach (RuntimeExtensionDescriptor previous in plan.PreviousDescriptors)
            {
                if (!nextIds.Contains(previous.Id))
                {
                    MarkUnavailable(previous);
                }
            }

            PrepareGeneration(
                plan.Snapshot,
                plan.NextDescriptors,
                reviveOwners: true);
            _reloadState = RuntimeExtensionReloadState.Initializing;
        }

        if (_reloadState is not RuntimeExtensionReloadState.Initializing)
        {
            return;
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
        if (_pendingInitialization.Count != 0)
        {
            return;
        }

        _reloadState = RuntimeExtensionReloadState.Completed;
        log.Information(
            $"Runtime-extension reload completed with {_active.Count} active extension(s). ");
    }

    private void PrepareGeneration(
        RootAssemblySnapshot snapshot,
        IReadOnlyList<RuntimeExtensionDescriptor> discovered,
        bool reviveOwners)
    {
        if (reviveOwners)
        {
            foreach (RuntimeExtensionDescriptor descriptor in discovered)
            {
                services.ResetOwner(descriptor.Id);
                _unavailableAssemblies.Remove(descriptor.AssemblyName);
            }
        }

        _resolver = new(snapshot, ContractAssembly, log);

        Dictionary<string, string> declaredProviders = [with(
            StringComparer.OrdinalIgnoreCase)];
        foreach (string capability in CoreCapabilities)
        {
            declaredProviders[capability] = "CommunityScriptHookVDotNetCore";
        }

        HashSet<string> rejected = [with(StringComparer.OrdinalIgnoreCase)];
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
                    $"'{existing}' and '{descriptor.Id}'. Both extensions are quarantined.",
                    impact: RuntimeDiagnosticImpact.LifecycleUnavailable,
                    summary:
                        $"Runtime capability '{capability}' has multiple providers; conflicting extensions were quarantined.");
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
    }

    private void EnsureRuntimeThread()
    {
        if (Environment.CurrentManagedThreadId != _runtimeThreadId)
        {
            throw new InvalidOperationException(
                "Runtime-extension lifecycle operations require the managed runtime frame thread.");
        }
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
                        "initialization deadline and was quarantined independently.",
                        source: pending.Descriptor.Id,
                        origin: pending.Descriptor.EntryType,
                        impact: RuntimeDiagnosticImpact.LifecycleUnavailable,
                        summary:
                            $"Runtime extension '{pending.Descriptor.Id}' timed out during initialization and was quarantined.");
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
                        "was cancelled and the extension was quarantined.",
                        source: pending.Descriptor.Id,
                        origin: pending.Descriptor.EntryType,
                        impact: RuntimeDiagnosticImpact.LifecycleUnavailable,
                        summary:
                            $"Runtime extension '{pending.Descriptor.Id}' could not start and was quarantined.");
                    break;

                case ManagedLifecycleOperationStatus.Faulted:
                    MarkUnavailable(pending.Descriptor);
                    BeginRetirement(
                        pending.Descriptor,
                        pending.Instance,
                        "initialization fault",
                        initializationCompletion);
                    log.Error(
                        $"Runtime extension '{pending.Descriptor.Id}' could not initialize: " +
                        snapshot.Exception,
                        source: pending.Descriptor.Id,
                        origin: pending.Descriptor.EntryType,
                        impact: RuntimeDiagnosticImpact.LifecycleUnavailable,
                        summary:
                            $"Runtime extension '{pending.Descriptor.Id}' faulted during initialization and was quarantined.");
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
                extensionsDirectory,
                scriptsDirectory,
                ownerServices,
                new RuntimeExtensionDependencyScope(this, descriptor.Id),
                diagnostics.CreateReader());

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
                $"Runtime extension '{descriptor.Id}' could not begin initialization: {exception}",
                source: descriptor.Id,
                origin: descriptor.EntryType,
                impact: RuntimeDiagnosticImpact.LifecycleUnavailable,
                summary:
                    $"Runtime extension '{descriptor.Id}' could not begin initialization and was quarantined.");
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
                $"Runtime extension '{descriptor.Id}' is quarantined. " +
                $"Unsatisfied capabilities: {missing}.",
                source: descriptor.Id,
                origin: descriptor.EntryType,
                impact: RuntimeDiagnosticImpact.LifecycleUnavailable,
                summary:
                    $"Runtime extension '{descriptor.Id}' was quarantined because required capabilities are unavailable.");
            MarkUnavailable(descriptor);
        }
        _pendingInitialization.Clear();
    }

    private void RejectUndeclaredRootReferences(
        RootAssemblySnapshot snapshot,
        IReadOnlyList<RuntimeExtensionDescriptor> discovered,
        HashSet<string> rejected)
    {
        Dictionary<string, RuntimeExtensionDescriptor> rootsByAssembly = [with(
            StringComparer.OrdinalIgnoreCase)];
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
            HashSet<string> visited = [with(StringComparer.OrdinalIgnoreCase)];
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
                        "declared dependency lifecycle graph.",
                        source: consumer.Id,
                        origin: consumer.EntryType,
                        impact: RuntimeDiagnosticImpact.LifecycleUnavailable,
                        summary:
                            $"Runtime extension '{consumer.Id}' was quarantined because its dependency declaration is incomplete.");
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
        HashSet<string> references = [with(StringComparer.OrdinalIgnoreCase)];
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
                    $"advancing a host frame: {exception}",
                    source: active.Descriptor.Id,
                    origin: active.Descriptor.EntryType,
                    impact: RuntimeDiagnosticImpact.LifecycleUnavailable,
                    summary:
                        $"Runtime extension '{active.Descriptor.Id}' faulted and its lifecycle was stopped.");
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
                _ = BeginRetirement(
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
                _ = BeginRetirement(
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
        HashSet<string> extensionIds =
        [
            with(StringComparer.OrdinalIgnoreCase),
            extensionId
        ];
        HashSet<string> removedCapabilities = [with(StringComparer.OrdinalIgnoreCase)];

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
            if (active.Descriptor.Id.Equals(
                    extensionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                log.Information(
                    $"Runtime extension '{active.Descriptor.Id}' is unavailable until a " +
                    "later successful extension reload.");
            }
            else
            {
                log.Error(
                    $"Runtime extension '{active.Descriptor.Id}' is unavailable until a " +
                    "later successful extension reload.",
                    source: active.Descriptor.Id,
                    origin: active.Descriptor.EntryType,
                    impact: RuntimeDiagnosticImpact.LifecycleUnavailable,
                    summary:
                        $"Runtime extension '{active.Descriptor.Id}' was stopped because a required extension became unavailable.");
            }
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

    private Task BeginRetirement(
        RuntimeExtensionDescriptor descriptor,
        IScript4RuntimeExtension instance,
        string reason,
        Task? predecessor = null)
    {
        RetiringRuntimeExtension? existing = _retiring.FirstOrDefault(value =>
            value.Descriptor.Id.Equals(
                descriptor.Id,
                StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing.Operation.Completion;
        }

        ManagedRetirementOperation operation = ManagedRetirementOperation.Start(
            instance.ShutdownAsync,
            predecessor);
        _retiring.Add(new(descriptor, reason, operation));
        log.Information(
            $"Runtime extension '{descriptor.Id}' entered asynchronous retirement; " +
            $"reason={reason}.");
        return operation.Completion;
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
                $"retirement; reason={retiring.Reason}: {snapshot.Exception}",
                source: retiring.Descriptor.Id,
                origin: retiring.Descriptor.EntryType);
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
            [with(StringComparer.OrdinalIgnoreCase)];
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

internal static class RuntimeExtensionPlacement
{
    private static readonly string[] SidecarExtensions =
        [".pdb", ".ini", ".log"];

    public static void Reconcile(
        string rootDirectory,
        string extensionsDirectory,
        RuntimeLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionsDirectory);
        ArgumentNullException.ThrowIfNull(log);

        string root = Path.GetFullPath(rootDirectory);
        string extensions = Path.GetFullPath(extensionsDirectory);
        if (root.Equals(extensions, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(extensions);
        string[] candidates =
        [
            .. Directory
                .EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(
                    value => value,
                    StringComparer.OrdinalIgnoreCase)
        ];

        foreach (string source in candidates)
        {
            try
            {
                if (!IsRuntimeExtension(source))
                {
                    continue;
                }

                string destination = Path.Combine(
                    extensions,
                    Path.GetFileName(source));
                ReconcileAssembly(source, destination, log);
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    BadImageFormatException or
                    ArgumentException or
                    NotSupportedException)
            {
                log.Warning(
                    $"Root extension '{Path.GetFileName(source)}' could not be " +
                    $"reconciled into '{extensions}': {exception.Message}");
            }
        }
    }

    private static bool IsRuntimeExtension(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        using PEReader pe = new(
            stream,
            PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata)
        {
            return false;
        }

        MetadataReader metadata = pe.GetMetadataReader();
        return ManagedAssemblyMetadata.Declares(
            metadata,
            RuntimeExtensionMetadataKeys.Role,
            RuntimeExtensionMetadataKeys.RuntimeExtensionRole);
    }

    private static void ReconcileAssembly(
        string source,
        string destination,
        RuntimeLog log)
    {
        bool sourceWins =
            !File.Exists(destination) ||
            CompareCandidates(source, destination) > 0;

        if (sourceWins)
        {
            File.Move(source, destination, overwrite: true);
            log.Information(
                $"Root extension '{Path.GetFileName(source)}' was moved to " +
                $"'{Path.GetDirectoryName(destination)}'.");
        }
        else
        {
            File.Delete(source);
            log.Information(
                $"Root extension '{Path.GetFileName(source)}' was discarded " +
                "because the extensions-directory copy is newer or equivalent.");
        }

        foreach (string extension in SidecarExtensions)
        {
            ReconcileSidecar(source, destination, extension);
        }
    }

    private static void ReconcileSidecar(
        string sourceAssembly,
        string destinationAssembly,
        string extension)
    {
        string source = Path.ChangeExtension(sourceAssembly, extension);
        if (!File.Exists(source))
        {
            return;
        }

        string destination =
            Path.ChangeExtension(destinationAssembly, extension);
        if (!File.Exists(destination) ||
            File.GetLastWriteTimeUtc(source) >
                File.GetLastWriteTimeUtc(destination))
        {
            File.Move(source, destination, overwrite: true);
            return;
        }

        File.Delete(source);
    }

    private static int CompareCandidates(
        string left,
        string right)
    {
        if (TryReadDatedVersion(left, out DatedVersion leftVersion) &&
            TryReadDatedVersion(right, out DatedVersion rightVersion))
        {
            int versionComparison = leftVersion.CompareTo(rightVersion);
            if (versionComparison != 0)
            {
                return versionComparison;
            }
        }

        return File.GetLastWriteTimeUtc(left)
            .CompareTo(File.GetLastWriteTimeUtc(right));
    }

    private static bool TryReadDatedVersion(
        string path,
        out DatedVersion version)
    {
        version = default;
        try
        {
            using (FileStream stream = new(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read | FileShare.Delete))
            using (PEReader pe = new(
                       stream,
                       PEStreamOptions.PrefetchMetadata))
            {
                if (pe.HasMetadata)
                {
                    Version assemblyVersion =
                        pe.GetMetadataReader()
                            .GetAssemblyDefinition()
                            .Version;
                    if (TryConvertVersion(assemblyVersion, out version))
                    {
                        return true;
                    }
                }
            }

            string? fileVersion =
                FileVersionInfo.GetVersionInfo(path).FileVersion;
            return Version.TryParse(fileVersion, out Version? parsed) &&
                TryConvertVersion(parsed, out version);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                BadImageFormatException or
                ArgumentException or
                NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryConvertVersion(
        Version value,
        out DatedVersion version)
    {
        version = default;
        if (value.Major <= 0 ||
            value.Minor <= 0 ||
            value.Build <= 0 ||
            value.Revision < 0)
        {
            return false;
        }

        try
        {
            DateOnly date = new(
                value.Build,
                value.Minor,
                value.Major);
            version = new(date, value.Revision);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private readonly record struct DatedVersion(
        DateOnly Date,
        int Variant) : IComparable<DatedVersion>
    {
        public int CompareTo(DatedVersion other)
        {
            int dateComparison = Date.CompareTo(other.Date);
            return dateComparison != 0
                ? dateComparison
                : Variant.CompareTo(other.Variant);
        }
    }
}

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
            [with(StringComparer.OrdinalIgnoreCase)];
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

internal static class RuntimeExtensionAssemblyRegistry
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Assembly> Assemblies =
        [with(StringComparer.OrdinalIgnoreCase)];

    public static bool TryGet(
        string simpleName,
        [NotNullWhen(true)] out Assembly? assembly)
    {
        lock (Gate)
        {
            return Assemblies.TryGetValue(simpleName, out assembly);
        }
    }

    public static void Register(Assembly assembly)
    {
        string? name = assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        lock (Gate)
        {
            Assemblies[name] = assembly;
        }
    }

    public static void Unregister(IEnumerable<Assembly> assemblies)
    {
        lock (Gate)
        {
            foreach (Assembly assembly in assemblies)
            {
                string? name = assembly.GetName().Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                if (Assemblies.TryGetValue(name, out Assembly? current) &&
                    ReferenceEquals(current, assembly))
                {
                    Assemblies.Remove(name);
                }
            }
        }
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
        Assembly contractAssembly,
        RuntimeLog log)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(contractAssembly);
        ArgumentNullException.ThrowIfNull(log);

        _context = new AssemblyLoadContext(
            $"CSHVDNC.Extensions.{Guid.NewGuid():N}",
            isCollectible: true);
        _contractAssembly = contractAssembly;
        _contractAssemblyName = contractAssembly.GetName().Name
            ?? throw new InvalidOperationException(
                "The runtime-extension contract assembly has no simple name.");
        _log = log;
        _snapshot = snapshot;
        _context.Resolving += Resolve;
        _log.Information(
            $"Extension resolver activated from {snapshot.Images.Count} committed RAM image(s).");
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
                $"Runtime extension assembly '{simpleName}' is absent from the RAM snapshot.");
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
        Assembly[] loaded = [.. _context.Assemblies];
        RuntimeExtensionAssemblyRegistry.Unregister(loaded);
        _snapshot = null;
        _context.Unload();
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
        if (snapshot is not null &&
            snapshot.TryGet(requested.Name, out RootAssemblyImage? image))
        {
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
                        $"Extension dependency '{requested}' could not be loaded from its committed RAM image: {exception.Message}");
                    return null;
                }
            }
        }

        Assembly? runtimeDependency = RuntimeDependencyResolver.TryResolve(requested);
        if (runtimeDependency is not null ||
            requested.Name.Equals(
                "FSharp.Core",
                StringComparison.OrdinalIgnoreCase))
        {
            return runtimeDependency;
        }

        foreach (Assembly shared in AssemblyLoadContext.Default.Assemblies)
        {
            if (shared.GetName().Name?.Equals(
                    requested.Name,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                return shared;
            }
        }

        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(requested);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
    }

    private Assembly LoadImage(RootAssemblyImage image)
    {
        Assembly loaded;
        if (image.Symbols is not null)
        {
            try
            {
                using MemoryStream assembly = new(image.Assembly, writable: false);
                using MemoryStream symbols = new(image.Symbols, writable: false);
                loaded = _context.LoadFromStream(assembly, symbols);
                RuntimeExtensionAssemblyRegistry.Register(loaded);
                return loaded;
            }
            catch (BadImageFormatException)
            {
            }
        }

        using MemoryStream imageStream = new(image.Assembly, writable: false);
        loaded = _context.LoadFromStream(imageStream);
        RuntimeExtensionAssemblyRegistry.Register(loaded);
        return loaded;
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