using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace CommunityScriptHookVDotNetCore.Source;

internal sealed class PackageManager(
    string scriptsDirectory,
    IScriptServices services,
    RuntimeLog log) : IReloadRuntimeHost
{
    private const ulong UnloadWarningFrameThreshold = 600;

    private readonly int _runtimeThreadId = Environment.CurrentManagedThreadId;
    private readonly IScriptServices _services = services;
    private readonly List<ScriptPackage> _packages = [];
    private readonly Queue<LifecycleTransitionOperation> _pendingOperations = [];
    private readonly List<LifecycleTransitionOperation> _operations = [];
    private readonly List<UnloadProbe> _unloadProbes = [];
    private readonly Dictionary<string, string> _activeFingerprints =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unavailableRuntimeAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PackageDescriptor> _catalog = [];
    private LifecycleTransitionOperation? _activeOperation;
    private ulong _lifecycleEpoch;
    private ulong _nextPackageGeneration;
    private ulong _lastHostFrameIndex;
    private bool _initialActivationCompleted;
    private bool _lifecycleDispatchPaused;
    private bool _shutdown;

    public void ActivateInitialPackages()
    {
        EnsureRuntimeThread();
        ThrowIfStopping();
        if (_initialActivationCompleted)
        {
            throw new InvalidOperationException(
                "The initial scripts4 package generation has already been activated.");
        }

        PackageDiscovery.EnsureFlatAssemblyLayout(scriptsDirectory);
        string[] names = EnumerateDiskPackageNames();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        CaptureAttempt attempt;
        for (;;)
        {
            deadline.Token.ThrowIfCancellationRequested();
            attempt = PackageImageCapture.CaptureAsync(
                    scriptsDirectory,
                    names,
                    log,
                    deadline.Token)
                .GetAwaiter()
                .GetResult();
            if (attempt.Image is not null)
            {
                break;
            }
            Thread.Sleep(25);
            names = EnumerateDiskPackageNames();
        }

        StagedReloadImage staged = ResolveCapturedImage(attempt.Image!);
        ApplyStagedCatalog(staged);
        _initialActivationCompleted = true;
        _lifecycleEpoch = 1;

        Dictionary<string, StagedPackageImage> executable = staged.Packages.Values
            .Where(value =>
                value.Descriptor.Kind == ScriptPackageKind.Executable &&
                _catalog.Any(item => item.Name.Equals(
                    value.Descriptor.Name,
                    StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(
                value => value.Descriptor.Name,
                StringComparer.OrdinalIgnoreCase);

        foreach (string name in OrderPackageNames(
                     _catalog,
                     executable.Keys,
                     reverse: false))
        {
            StagedPackageImage image = executable[name];
            List<StagedPackageImage> dependencies = [];
            bool valid = true;
            foreach (string dependencyName in image.Descriptor.DependencyPackageNames)
            {
                if (!staged.Packages.TryGetValue(
                        dependencyName,
                        out StagedPackageImage? dependency) ||
                    dependency is null)
                {
                    valid = false;
                    log.Error(
                        $"Initial package '{name}' is missing passive dependency " +
                        $"'{dependencyName}'.");
                    break;
                }
                dependencies.Add(dependency);
            }

            ScriptPackage? package = valid
                ? ScriptPackage.TryPrepare(
                    image,
                    dependencies.AsReadOnly(),
                    _services,
                    log,
                    NextPackageGeneration())
                : null;
            if (package is not null)
            {
                _packages.Add(package);
            }
        }

        foreach (ScriptPackage package in OrderActivePackages(reverse: false))
        {
            if (package.StartInstances(
                    ScriptStartReason.InitialActivation,
                    _lifecycleEpoch))
            {
                continue;
            }

            AddUnloadProbe(
                package,
                package.UnloadStopped(),
                _lastHostFrameIndex);
            _packages.Remove(package);
        }

        int scripts = _packages.Sum(package => package.ActiveScriptCount);
        log.Information(
            $"Activated RAM-resident scripts4 generation {_lifecycleEpoch}: " +
            $"{_packages.Count} executable package(s), {scripts} Script4 instance(s). " +
            "The committed generation no longer depends on its source DLLs on disk.");
    }

    internal void SetUnavailableRuntimeAssemblies(IEnumerable<string> assemblyNames)
    {
        EnsureRuntimeThread();
        foreach (string name in assemblyNames.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            _unavailableRuntimeAssemblies.Add(name.Trim());
        }
    }

    internal void QuarantineAssemblyReferences(IEnumerable<string> assemblyNames)
    {
        EnsureRuntimeThread();
        string[] newlyUnavailable = [.. assemblyNames
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (newlyUnavailable.Length == 0)
        {
            return;
        }

        foreach (string name in newlyUnavailable)
        {
            _unavailableRuntimeAssemblies.Add(name);
        }

        HashSet<string> seeds = new(
            _catalog
                .Where(package => package.ReferencedAssemblyNames.Any(reference =>
                    _unavailableRuntimeAssemblies.Contains(reference)))
                .Select(package => package.Name),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> closure = ExpandDependentClosure(seeds, _catalog);
        foreach (ScriptPackage package in OrderActivePackages(reverse: true))
        {
            if (!closure.Contains(package.Name))
            {
                continue;
            }
            package.StopInstances(ScriptStopReason.DependencyUnavailable);
            AddUnloadProbe(package, package.UnloadStopped(), _lastHostFrameIndex);
            _packages.Remove(package);
            log.Error(
                $"Script package '{package.Name}' was stopped because a root " +
                "runtime-extension assembly it references is unavailable for " +
                "the remainder of this GTA session.");
        }
    }

    public void AdvanceTransitionOperations(ulong hostFrameIndex)
    {
        EnsureRuntimeThread();
        _lastHostFrameIndex = hostFrameIndex;
        ObserveUnloadProbes(hostFrameIndex);

        if (_shutdown)
        {
            return;
        }

        if (_activeOperation is null && _pendingOperations.Count != 0)
        {
            _activeOperation = _pendingOperations.Dequeue();
        }

        _activeOperation?.Advance(this);
        if (_activeOperation?.Snapshot.IsTerminal == true)
        {
            _activeOperation = null;
        }
    }

    public void Tick(ScriptTickContext context)
    {
        EnsureRuntimeThread();
        if (_lifecycleDispatchPaused || _shutdown)
        {
            return;
        }

        for (int index = _packages.Count - 1; index >= 0; --index)
        {
            ScriptPackage package = _packages[index];
            int activeBeforeTick = package.ActiveScriptCount;
            package.Tick(context);
            if (activeBeforeTick == 0 || package.ActiveScriptCount != 0)
            {
                continue;
            }

            WeakReference? unloaded = package.StopAndUnload(
                ScriptStopReason.PackageFault);
            _packages.RemoveAt(index);
            AddUnloadProbe(package, unloaded, context.HostFrameIndex);
        }
    }

    public void BeginShutdown()
    {
        EnsureRuntimeThread();
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _lifecycleDispatchPaused = true;
        foreach (LifecycleTransitionOperation operation in _operations)
        {
            operation.Cancel("The managed runtime is shutting down.");
        }
        _pendingOperations.Clear();
        _activeOperation = null;
        log.Information(
            "Package lifecycle transition intake and background capture were stopped.");
    }

    public void StopAll(ScriptStopReason reason)
    {
        EnsureRuntimeThread();
        _lifecycleDispatchPaused = true;
        foreach (ScriptPackage package in OrderActivePackages(reverse: true))
        {
            AddUnloadProbe(
                package,
                package.StopAndUnload(reason),
                _lastHostFrameIndex);
        }
        _packages.Clear();

        foreach (LifecycleTransitionOperation operation in _operations)
        {
            operation.Dispose();
        }
        _operations.Clear();
        _pendingOperations.Clear();
        _activeOperation = null;
        ObserveUnloadProbes(_lastHostFrameIndex);
    }

    public ScriptLifecycleTransitionOperationId RequestTransition(
        ScriptLifecycleTransitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureRuntimeThread();
        ThrowIfStopping();
        PackageDiscovery.EnsureFlatAssemblyLayout(scriptsDirectory);

        ScriptLifecycleTransitionOperationId id =
            ScriptLifecycleTransitionOperationId.Create();
        LifecycleTransitionOperation operation = new(
            id,
            plan,
            scriptsDirectory,
            log);
        _operations.Add(operation);
        _pendingOperations.Enqueue(operation);
        log.Information(
            $"scripts4 reconcile '{id.Value:D}' queued; reason={plan.Reason}.");
        return id;
    }

    public IReadOnlyList<ScriptLifecycleTransitionOperationSnapshot>
        SnapshotOperations()
    {
        EnsureRuntimeThread();
        return Array.AsReadOnly(
            [.. _operations.Select(value => value.Snapshot)]);
    }

    public void Acknowledge(
        ScriptLifecycleTransitionOperationId operationId)
    {
        EnsureRuntimeThread();
        int index = _operations.FindIndex(value => value.Id == operationId);
        if (index >= 0 && _operations[index].Snapshot.IsTerminal)
        {
            _operations[index].Dispose();
            _operations.RemoveAt(index);
        }
    }

    private static HashSet<string> ExpandDependentClosure(
        IEnumerable<string> seeds,
        IReadOnlyList<PackageDescriptor> catalog)
    {
        HashSet<string> closure = new(seeds, StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (PackageDescriptor package in catalog)
            {
                if (package.DependencyPackageNames.Any(closure.Contains))
                {
                    changed |= closure.Add(package.Name);
                }
            }
        }
        while (changed);
        return closure;
    }

    private string[] EnumerateDiskPackageNames() =>
        [.. Directory
            .EnumerateFiles(scriptsDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)];

    private string[] CreateFullCaptureTargets()
    {
        HashSet<string> names = new(
            _catalog.Select(value => value.Name),
            StringComparer.OrdinalIgnoreCase);
        names.UnionWith(EnumerateDiskPackageNames());
        return [.. names.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, string> Fingerprints(
        StagedReloadImage staged)
    {
        HashSet<string> valid = new(
            staged.Catalog.Select(value => value.Name),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, StagedPackageImage package) in staged.Packages)
        {
            if (!valid.Contains(name))
            {
                continue;
            }
            PackageAssemblyImage image = package.Assemblies.Single();
            result[name] = image.Sha256;
        }
        return result;
    }

    private string[] DetectChangedSeeds(StagedReloadImage staged)
    {
        Dictionary<string, string> candidate = Fingerprints(staged);
        HashSet<string> names = new(
            _activeFingerprints.Keys,
            StringComparer.OrdinalIgnoreCase);
        names.UnionWith(candidate.Keys);

        HashSet<string> activeExecutables = new(
            _packages.Select(package => package.Name),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> missingExecutableGeneration = new(
            staged.Catalog
                .Where(package =>
                    package.Kind == ScriptPackageKind.Executable &&
                    !activeExecutables.Contains(package.Name))
                .Select(package => package.Name),
            StringComparer.OrdinalIgnoreCase);

        return [.. names
            .Where(name =>
                missingExecutableGeneration.Contains(name) ||
                !_activeFingerprints.TryGetValue(name, out string? active) ||
                !candidate.TryGetValue(name, out string? next) ||
                !active.Equals(next, StringComparison.Ordinal))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)];
    }

    private bool CandidateRemovesCommittedPackage(StagedReloadImage staged)
    {
        Dictionary<string, string> candidate = Fingerprints(staged);
        return _activeFingerprints.Keys.Any(name => !candidate.ContainsKey(name));
    }

    private static string CandidateManifestKey(StagedReloadImage staged) =>
        string.Join(
            "\n",
            Fingerprints(staged)
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));

    private static ReadOnlyCollection<PackageDescriptor>
        GetDependencyDescriptors(
            PackageDescriptor package,
            IReadOnlyList<PackageDescriptor> catalog)
    {
        Dictionary<string, PackageDescriptor> byName = catalog.ToDictionary(
            value => value.Name,
            StringComparer.OrdinalIgnoreCase);
        List<PackageDescriptor> result = [];
        foreach (string dependencyName in package.DependencyPackageNames)
        {
            if (!byName.TryGetValue(
                    dependencyName,
                    out PackageDescriptor? dependency) ||
                dependency.Kind != ScriptPackageKind.Library)
            {
                throw new BadImageFormatException(
                    $"Package '{package.Name}' requires passive library package " +
                    $"'{dependencyName}', but it is unavailable.");
            }
            result.Add(dependency);
        }
        return result.AsReadOnly();
    }

    private static string[] OrderPackageNames(
        IReadOnlyList<PackageDescriptor> catalog,
        IEnumerable<string> names,
        bool reverse)
    {
        HashSet<string> selected = new(
            names,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PackageDescriptor> descriptors = catalog.ToDictionary(
            value => value.Name,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> indegree = selected.ToDictionary(
            value => value,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> dependents = selected.ToDictionary(
            value => value,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (string name in selected)
        {
            if (!descriptors.TryGetValue(
                    name,
                    out PackageDescriptor? descriptor))
            {
                continue;
            }

            foreach (string dependency in descriptor.DependencyPackageNames)
            {
                if (!selected.Contains(dependency))
                {
                    continue;
                }
                ++indegree[name];
                dependents[dependency].Add(name);
            }
        }

        SortedSet<string> ready = new(
            indegree
                .Where(value => value.Value == 0)
                .Select(value => value.Key),
            StringComparer.OrdinalIgnoreCase);
        List<string> ordered = [];
        while (ready.Count != 0)
        {
            string name = ready.Min!;
            ready.Remove(name);
            ordered.Add(name);
            foreach (string dependent in dependents[name].OrderBy(
                         value => value,
                         StringComparer.OrdinalIgnoreCase))
            {
                if (--indegree[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (ordered.Count != selected.Count)
        {
            throw new InvalidDataException(
                "The scripts4 package dependency graph is cyclic after validation.");
        }
        if (reverse)
        {
            ordered.Reverse();
        }
        return [.. ordered];
    }

    private ScriptPackage[] OrderActivePackages(bool reverse)
    {
        Dictionary<string, ScriptPackage> byName = _packages.ToDictionary(
            value => value.Name,
            StringComparer.OrdinalIgnoreCase);
        return [.. OrderPackageNames(_catalog, byName.Keys, reverse)
            .Where(byName.ContainsKey)
            .Select(name => byName[name])];
    }

    private StagedReloadImage ResolveCapturedImage(
        CapturedReloadImage captured)
    {
        Dictionary<string, PackageDescriptor> merged = _catalog.ToDictionary(
            value => value.Name,
            StringComparer.OrdinalIgnoreCase);
        foreach (string target in captured.TargetPackages)
        {
            merged.Remove(target);
        }
        foreach (CapturedPackageImage package in captured.Packages.Values)
        {
            merged[package.Descriptor.Name] = package.Descriptor;
        }

        IReadOnlyList<PackageDescriptor> resolvedCatalog =
            PackageDiscovery.ResolveDependencies(
                [.. merged.Values.OrderBy(
                    value => value.Name,
                    StringComparer.OrdinalIgnoreCase)]);
        if (_unavailableRuntimeAssemblies.Count != 0)
        {
            HashSet<string> invalidSeeds = new(
                resolvedCatalog
                    .Where(package => package.ReferencedAssemblyNames.Any(reference =>
                        _unavailableRuntimeAssemblies.Contains(reference)))
                    .Select(package => package.Name),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> invalid = ExpandDependentClosure(
                invalidSeeds,
                resolvedCatalog);
            if (invalid.Count != 0)
            {
                foreach (string name in invalid.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    log.Error(
                        $"Package '{name}' is excluded because it depends on a " +
                        "quarantined root extension assembly.");
                }
                resolvedCatalog = Array.AsReadOnly(
                    [.. resolvedCatalog.Where(package => !invalid.Contains(package.Name))]);
            }
        }
        Dictionary<string, PackageDescriptor> resolvedByName =
            resolvedCatalog.ToDictionary(
                value => value.Name,
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, StagedPackageImage> stagedPackages = new(
            StringComparer.OrdinalIgnoreCase);
        foreach ((string name, CapturedPackageImage package) in captured.Packages)
        {
            if (!resolvedByName.TryGetValue(
                    name,
                    out PackageDescriptor? descriptor))
            {
                throw new BadImageFormatException(
                    $"Captured package '{name}' disappeared while its " +
                    "dependency graph was being resolved.");
            }

            stagedPackages.Add(
                name,
                new(descriptor, package.Assemblies));
        }

        return new(
            captured.TargetPackages,
            new ReadOnlyDictionary<string, StagedPackageImage>(stagedPackages),
            resolvedCatalog);
    }

    private void ApplyStagedCatalog(StagedReloadImage staged)
    {
        _catalog = staged.Catalog;
        _activeFingerprints.Clear();
        foreach ((string name, string fingerprint) in Fingerprints(staged))
        {
            _activeFingerprints[name] = fingerprint;
        }
    }

    private ScriptPackage? FindActivePackage(string name) =>
        _packages.FirstOrDefault(package => package.Name.Equals(
            name,
            StringComparison.OrdinalIgnoreCase));

    private void RemoveStoppedPackage(ScriptPackage package)
    {
        AddUnloadProbe(
            package,
            package.UnloadStopped(),
            _lastHostFrameIndex);
        _packages.Remove(package);
    }

    private void AddPreparedPackage(ScriptPackage package) =>
        _packages.Add(package);

    private ulong BeginLifecycleTransition()
    {
        if (_lifecycleDispatchPaused)
        {
            throw new InvalidOperationException(
                "A scripts4 lifecycle barrier is already active.");
        }

        _lifecycleDispatchPaused = true;
        return ++_lifecycleEpoch;
    }

    private void EndLifecycleTransition()
    {
        if (!_shutdown)
        {
            _lifecycleDispatchPaused = false;
        }
    }

    private ulong NextPackageGeneration() =>
        ++_nextPackageGeneration;

    private void AddUnloadProbe(
        ScriptPackage package,
        WeakReference? context,
        ulong hostFrameIndex)
    {
        if (context is null)
        {
            return;
        }

        _unloadProbes.Add(new(
            package.Name,
            package.GenerationId,
            hostFrameIndex,
            DateTimeOffset.UtcNow,
            false,
            context));
    }

    private void ObserveUnloadProbes(ulong hostFrameIndex)
    {
        for (int index = _unloadProbes.Count - 1; index >= 0; --index)
        {
            UnloadProbe probe = _unloadProbes[index];
            if (!probe.Context.IsAlive)
            {
                _unloadProbes.RemoveAt(index);
                log.Information(
                    $"Package '{probe.PackageName}' binary generation " +
                    $"{probe.GenerationId} collectible load context was released.");
                continue;
            }

            ulong elapsed = hostFrameIndex >= probe.RequestedFrame
                ? hostFrameIndex - probe.RequestedFrame
                : 0;
            if (probe.WarningEmitted ||
                elapsed < UnloadWarningFrameThreshold)
            {
                continue;
            }

            _unloadProbes[index] = probe with { WarningEmitted = true };
            log.Warning(
                $"Package '{probe.PackageName}' binary generation " +
                $"{probe.GenerationId} remains alive {elapsed} host frames " +
                "after retirement. The runtime will not block the current " +
                "lifecycle epoch while cooperative unloading completes.");
        }
    }

    private void ThrowIfStopping()
    {
        if (_shutdown)
        {
            throw new InvalidOperationException(
                "The package lifecycle runtime is stopping.");
        }
    }

    private void EnsureRuntimeThread()
    {
        if (Environment.CurrentManagedThreadId != _runtimeThreadId)
        {
            throw new InvalidOperationException(
                "Package lifecycle operations must run on the managed runtime " +
                "frame thread.");
        }
    }

    private sealed record UnloadProbe(
        string PackageName,
        ulong GenerationId,
        ulong RequestedFrame,
        DateTimeOffset RequestedAt,
        bool WarningEmitted,
        WeakReference Context);

    private sealed class LifecycleTransitionOperation(
        ScriptLifecycleTransitionOperationId id,
        ScriptLifecycleTransitionPlan plan,
        string scriptsDirectory,
        RuntimeLog log) : IDisposable
    {
        private static readonly TimeSpan CaptureDeadline = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RemovalConfirmationWindow =
            TimeSpan.FromMilliseconds(250);
        private readonly string _scriptsDirectory = scriptsDirectory;
        private readonly RuntimeLog _log = log;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<string> _restartedInPlace = [];
        private readonly List<string> _binaryReplaced = [];
        private readonly List<string> _libraries = [];
        private readonly List<string> _added = [];
        private readonly List<string> _removed = [];
        private readonly List<string> _failed = [];
        private HashSet<string> _targetSet = new(StringComparer.OrdinalIgnoreCase);
        private string[] _binarySeeds = [];
        private HashSet<string> _originalPackageNames = new(StringComparer.OrdinalIgnoreCase);
        private Task<CaptureAttempt>? _captureTask;
        private long _captureStarted;
        private long _removalConfirmationStarted;
        private string? _pendingRemovalManifest;
        private StagedReloadImage? _staged;
        private Queue<string>? _stopQueue;
        private Queue<string>? _replaceQueue;
        private Queue<StagedPackageImage>? _prepareQueue;
        private Queue<string>? _startQueue;
        private ulong? _lifecycleEpoch;
        private string? _diagnostic;
        private bool _barrierEntered;
        private bool _disposed;

        public ScriptLifecycleTransitionOperationId Id { get; } = id;

        public ScriptLifecycleTransitionOperationState State { get; private set; } =
            ScriptLifecycleTransitionOperationState.Queued;

        public ScriptLifecycleTransitionOperationSnapshot Snapshot => new(
            Id,
            State,
            plan,
            _lifecycleEpoch,
            State is ScriptLifecycleTransitionOperationState.Completed or
                    ScriptLifecycleTransitionOperationState.Failed
                ? BuildResult()
                : null,
            _diagnostic);

        public void Advance(PackageManager owner)
        {
            try
            {
                if (_lifetime.IsCancellationRequested)
                {
                    Cancel("The lifecycle transition was cancelled.");
                    return;
                }

                switch (State)
                {
                    case ScriptLifecycleTransitionOperationState.Queued:
                        StartCapture(owner);
                        break;
                    case ScriptLifecycleTransitionOperationState.CapturingImages:
                        AdvanceCapture(owner);
                        break;
                    case ScriptLifecycleTransitionOperationState.ReadyInMemory:
                        PrepareLifecycleStop(owner);
                        break;
                    case ScriptLifecycleTransitionOperationState.StoppingLifecycle:
                        AdvanceLifecycleStop(owner);
                        break;
                    case ScriptLifecycleTransitionOperationState.ReplacingBinaries:
                        AdvanceBinaryReplacement(owner);
                        break;
                    case ScriptLifecycleTransitionOperationState.RecreatingInstances:
                        AdvanceInstanceRecreation(owner);
                        break;
                    case ScriptLifecycleTransitionOperationState.StartingLifecycle:
                        AdvanceLifecycleStart(owner);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                Cancel("The lifecycle transition was cancelled.");
            }
            catch (Exception exception)
            {
                Fail(owner, exception.ToString());
            }
        }

        public void Cancel(string diagnostic)
        {
            if (Snapshot.IsTerminal)
            {
                return;
            }
            _lifetime.Cancel();
            _diagnostic = diagnostic;
            _staged = null;
            _pendingRemovalManifest = null;
            _removalConfirmationStarted = 0;
            _stopQueue = null;
            _replaceQueue = null;
            _prepareQueue = null;
            _startQueue = null;
            State = ScriptLifecycleTransitionOperationState.Cancelled;
            _log.Information(
                $"scripts4 reconcile '{Id.Value:D}' was cancelled: {diagnostic}");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (!Snapshot.IsTerminal)
            {
                Cancel("The lifecycle transition was disposed.");
            }
            _lifetime.Dispose();
        }

        private void StartCapture(PackageManager owner)
        {
            State = ScriptLifecycleTransitionOperationState.CapturingImages;
            string[] targets = owner.CreateFullCaptureTargets();
            CancellationToken cancellationToken = _lifetime.Token;
            if (_captureStarted == 0)
            {
                _captureStarted = Stopwatch.GetTimestamp();
            }
            _captureTask = Task.Run(
                () => PackageImageCapture.CaptureAsync(
                    _scriptsDirectory,
                    targets,
                    _log,
                    cancellationToken),
                cancellationToken);
        }

        private void AdvanceCapture(PackageManager owner)
        {
            Task<CaptureAttempt>? task = _captureTask;
            if (task is null)
            {
                if (_pendingRemovalManifest is not null &&
                    Stopwatch.GetElapsedTime(_removalConfirmationStarted) <
                        RemovalConfirmationWindow)
                {
                    return;
                }
                StartCapture(owner);
                return;
            }
            if (!task.IsCompleted)
            {
                if (Stopwatch.GetElapsedTime(_captureStarted) >= CaptureDeadline)
                {
                    _lifetime.Cancel();
                    Fail(owner,
                        "The disk candidate did not become a coherent readable " +
                        "generation before the capture deadline. The committed " +
                        "RAM generation was left untouched.");
                }
                return;
            }

            CaptureAttempt attempt;
            try
            {
                attempt = task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Cancel("Generation capture was cancelled.");
                return;
            }
            catch (Exception exception)
            {
                attempt = CaptureAttempt.Retry(exception.Message);
            }

            _captureTask = null;
            if (attempt.Image is null)
            {
                if (Stopwatch.GetElapsedTime(_captureStarted) >= CaptureDeadline)
                {
                    Fail(owner,
                        "The disk candidate remained incomplete: " +
                        attempt.Diagnostic);
                    return;
                }

                _diagnostic = attempt.Diagnostic;
                string[] targets = owner.CreateFullCaptureTargets();
                CancellationToken cancellationToken = _lifetime.Token;
                _captureTask = Task.Run(
                    () => PackageImageCapture.CaptureAsync(
                        _scriptsDirectory,
                        targets,
                        _log,
                        cancellationToken),
                    cancellationToken);
                return;
            }

            StagedReloadImage staged;
            try
            {
                staged = owner.ResolveCapturedImage(attempt.Image);
            }
            catch (Exception exception) when (
                exception is BadImageFormatException or InvalidDataException)
            {
                if (Stopwatch.GetElapsedTime(_captureStarted) >= CaptureDeadline)
                {
                    Fail(owner,
                        "The disk candidate remained incoherent: " + exception.Message);
                    return;
                }
                _diagnostic = exception.Message;
                string[] targets = owner.CreateFullCaptureTargets();
                CancellationToken cancellationToken = _lifetime.Token;
                _captureTask = Task.Run(
                    () => PackageImageCapture.CaptureAsync(
                        _scriptsDirectory,
                        targets,
                        _log,
                        cancellationToken),
                    cancellationToken);
                return;
            }

            if (plan.Reason is ScriptLifecycleTransitionReason.SynchronizedReload &&
                owner.CandidateRemovesCommittedPackage(staged))
            {
                string manifest = CandidateManifestKey(staged);
                if (_pendingRemovalManifest is null ||
                    !_pendingRemovalManifest.Equals(manifest, StringComparison.Ordinal))
                {
                    _pendingRemovalManifest = manifest;
                    _removalConfirmationStarted = Stopwatch.GetTimestamp();
                    _diagnostic =
                        "A committed DLL is absent from disk. Synchronized " +
                        "reload is confirming the same desired-state deletion " +
                        "after a second stable observation before entering the " +
                        "global lifecycle barrier.";
                    _log.Information(_diagnostic);
                    return;
                }

                _pendingRemovalManifest = null;
                _removalConfirmationStarted = 0;
            }
            else
            {
                _pendingRemovalManifest = null;
                _removalConfirmationStarted = 0;
            }

            _binarySeeds = owner.DetectChangedSeeds(staged);
            if (_binarySeeds.Length == 0 &&
                plan.Reason is ScriptLifecycleTransitionReason.SynchronizedReload)
            {
                _lifecycleEpoch = owner._lifecycleEpoch;
                _diagnostic = "The synchronized disk observation matches the committed RAM generation; no reload was performed.";
                State = ScriptLifecycleTransitionOperationState.Completed;
                _log.Information(
                    $"scripts4 reconcile '{Id.Value:D}' observed no binary " +
                    "change and completed without entering the global barrier.");
                return;
            }

            _originalPackageNames = new(
                owner._catalog.Select(value => value.Name),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> closure = ExpandDependentClosure(
                _binarySeeds,
                owner._catalog);
            closure.UnionWith(ExpandDependentClosure(
                _binarySeeds,
                staged.Catalog));
            _targetSet = closure;
            _staged = staged;
            _diagnostic = null;
            State = ScriptLifecycleTransitionOperationState.ReadyInMemory;

            string seeds = _binarySeeds.Length == 0
                ? "none; lifecycle-only"
                : $"[{string.Join(", ", _binarySeeds)}]";
            _log.Information(
                $"scripts4 reconcile '{Id.Value:D}' is fully captured and " +
                $"validated in RAM. Binary seeds={seeds}; replacement closure=" +
                $"[{string.Join(", ", _targetSet.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}].");
        }

        private void PrepareLifecycleStop(PackageManager owner)
        {
            _lifecycleEpoch = owner.BeginLifecycleTransition();
            _barrierEntered = true;
            _stopQueue = new(OrderPackageNames(
                owner._catalog,
                owner._packages.Select(value => value.Name),
                reverse: true));
            State = ScriptLifecycleTransitionOperationState.StoppingLifecycle;
            _log.Information(
                $"scripts4 reconcile '{Id.Value:D}' entered global lifecycle " +
                $"barrier {_lifecycleEpoch.Value}. Every Script4 executable is stopped.");
        }

        private void AdvanceLifecycleStop(PackageManager owner)
        {
            if (_stopQueue is null)
            {
                Fail(owner, "The lifecycle stop queue is unavailable.");
                return;
            }

            if (_stopQueue.Count != 0)
            {
                string name = _stopQueue.Dequeue();
                ScriptPackage? package = owner.FindActivePackage(name);
                package?.StopInstances(
                    _targetSet.Contains(name)
                        ? ScriptStopReason.BinaryReplacement
                        : ScriptStopReason.LifecycleRestart);
                return;
            }

            _replaceQueue = new(
                _targetSet.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            State = ScriptLifecycleTransitionOperationState.ReplacingBinaries;
        }

        private void AdvanceBinaryReplacement(PackageManager owner)
        {
            if (_replaceQueue is null)
            {
                Fail(owner, "The binary replacement queue is unavailable.");
                return;
            }

            if (_replaceQueue.Count != 0)
            {
                string name = _replaceQueue.Dequeue();
                ScriptPackage? active = owner.FindActivePackage(name);
                if (active is not null)
                {
                    owner.RemoveStoppedPackage(active);
                }
                return;
            }

            if (_staged is null)
            {
                Fail(owner, "The RAM candidate disappeared before commit.");
                return;
            }

            if (_binarySeeds.Length != 0)
            {
                owner.ApplyStagedCatalog(_staged);
            }

            HashSet<string> candidateNames = new(
                _staged.Catalog.Select(value => value.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (string name in _originalPackageNames)
            {
                if (!candidateNames.Contains(name))
                {
                    AddUnique(_removed, name);
                }
            }
            foreach (string name in candidateNames)
            {
                if (!_originalPackageNames.Contains(name))
                {
                    AddUnique(_added, name);
                }
            }
            foreach (string name in _targetSet)
            {
                PackageDescriptor? descriptor = _staged.Catalog.FirstOrDefault(
                    value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (descriptor?.Kind == ScriptPackageKind.Library)
                {
                    AddUnique(_libraries, name);
                }
            }

            Dictionary<string, StagedPackageImage> executable =
                _staged.Packages.Values
                    .Where(value =>
                        value.Descriptor.Kind == ScriptPackageKind.Executable &&
                        _targetSet.Contains(value.Descriptor.Name) &&
                        owner._catalog.Any(item => item.Name.Equals(
                            value.Descriptor.Name,
                            StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(
                        value => value.Descriptor.Name,
                        StringComparer.OrdinalIgnoreCase);

            _prepareQueue = new(
                OrderPackageNames(owner._catalog, executable.Keys, reverse: false)
                    .Select(name => executable[name]));
            State = ScriptLifecycleTransitionOperationState.RecreatingInstances;
        }

        private void AdvanceInstanceRecreation(PackageManager owner)
        {
            if (_prepareQueue is null || _staged is null)
            {
                Fail(owner, "The executable recreation queue is unavailable.");
                return;
            }

            if (_prepareQueue.Count != 0)
            {
                StagedPackageImage image = _prepareQueue.Dequeue();
                List<StagedPackageImage> dependencies = [];
                bool dependenciesAvailable = true;
                foreach (string dependencyName in image.Descriptor.DependencyPackageNames)
                {
                    if (!_staged.Packages.TryGetValue(
                            dependencyName,
                            out StagedPackageImage? dependency) ||
                        dependency is null)
                    {
                        dependenciesAvailable = false;
                        _log.Error(
                            $"Package '{image.Descriptor.Name}' cannot be prepared " +
                            $"because passive dependency '{dependencyName}' is absent.");
                        break;
                    }
                    dependencies.Add(dependency);
                }

                ScriptPackage? replacement = dependenciesAvailable
                    ? ScriptPackage.TryPrepare(
                        image,
                        dependencies.AsReadOnly(),
                        owner._services,
                        _log,
                        owner.NextPackageGeneration())
                    : null;
                if (replacement is null)
                {
                    AddUnique(_failed, image.Descriptor.Name);
                }
                else
                {
                    owner.AddPreparedPackage(replacement);
                }
                return;
            }

            _startQueue = new(OrderPackageNames(
                owner._catalog,
                owner._packages.Select(value => value.Name),
                reverse: false));
            State = ScriptLifecycleTransitionOperationState.StartingLifecycle;
        }

        private void AdvanceLifecycleStart(PackageManager owner)
        {
            if (_startQueue is null || _lifecycleEpoch is null)
            {
                Fail(owner, "The lifecycle start queue is unavailable.");
                return;
            }

            if (_startQueue.Count != 0)
            {
                string name = _startQueue.Dequeue();
                ScriptPackage? package = owner.FindActivePackage(name);
                if (package is null)
                {
                    return;
                }

                bool replaced = _targetSet.Contains(name);
                bool started = package.StartInstances(
                    replaced
                        ? ScriptStartReason.BinaryReplacement
                        : ScriptStartReason.LifecycleRestart,
                    _lifecycleEpoch.Value);
                if (!started)
                {
                    AddUnique(_failed, name);
                }
                else if (replaced)
                {
                    AddUnique(_binaryReplaced, name);
                }
                else
                {
                    AddUnique(_restartedInPlace, name);
                }
                return;
            }

            Complete(owner);
        }

        private void Complete(PackageManager owner)
        {
            owner.EndLifecycleTransition();
            _barrierEntered = false;
            ScriptLifecycleTransitionResult result = BuildResult();
            State = result.Succeeded
                ? ScriptLifecycleTransitionOperationState.Completed
                : ScriptLifecycleTransitionOperationState.Failed;
            _diagnostic = result.Succeeded
                ? null
                : "One or more executables failed to enter the committed lifecycle epoch.";
            _staged = null;
            _log.Information(
                $"scripts4 reconcile '{Id.Value:D}' completed at epoch " +
                $"{result.LifecycleEpoch}. Restarted=[{string.Join(", ", result.RestartedInPlacePackages)}] " +
                $"Replaced=[{string.Join(", ", result.BinaryReplacedPackages)}] " +
                $"Libraries=[{string.Join(", ", result.RefreshedLibraries)}] " +
                $"Added=[{string.Join(", ", result.AddedPackages)}] " +
                $"Removed=[{string.Join(", ", result.RemovedPackages)}] " +
                $"Failed=[{string.Join(", ", result.FailedPackages)}].");
        }

        private void Fail(PackageManager owner, string message)
        {
            if (_barrierEntered)
            {
                owner.EndLifecycleTransition();
                _barrierEntered = false;
            }
            _diagnostic = message;
            State = ScriptLifecycleTransitionOperationState.Failed;
            _log.Error(
                $"scripts4 reconcile '{Id.Value:D}' failed: {message}");
        }

        private ScriptLifecycleTransitionResult BuildResult() => new(
            _lifecycleEpoch ?? 0,
            Array.AsReadOnly(_restartedInPlace.ToArray()),
            Array.AsReadOnly(_binaryReplaced.ToArray()),
            Array.AsReadOnly(_libraries.ToArray()),
            Array.AsReadOnly(_added.ToArray()),
            Array.AsReadOnly(_removed.ToArray()),
            Array.AsReadOnly(_failed.ToArray()));

        private static void AddUnique(List<string> values, string value)
        {
            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(value);
            }
        }
    }

}

internal sealed class ScriptPackage
{
    private readonly PackageDescriptor _descriptor;
    private readonly RuntimeLog _log;
    private readonly IScriptServices _services;
    private AssemblyLoadContext? _loadContext;
    private Assembly? _entryAssembly;
    private Type[] _scriptTypes;
    private readonly List<ScriptInstance> _scripts = [];

    private ScriptPackage(
        PackageDescriptor descriptor,
        RuntimeLog log,
        IScriptServices services,
        AssemblyLoadContext loadContext,
        Assembly entryAssembly,
        Type[] scriptTypes,
        ulong generationId)
    {
        _descriptor = descriptor;
        _log = log;
        _services = services;
        _loadContext = loadContext;
        _entryAssembly = entryAssembly;
        _scriptTypes = scriptTypes;
        GenerationId = generationId;
    }

    public string Name => _descriptor.Name;

    public ulong GenerationId { get; }

    public int ActiveScriptCount =>
        _scripts.Count(script => script.IsActive);

    public static ScriptPackage? TryPrepare(
        StagedPackageImage staged,
        IReadOnlyList<StagedPackageImage> dependencies,
        IScriptServices services,
        RuntimeLog log,
        ulong generationId)
    {
        PackageDescriptor descriptor = staged.Descriptor;
        if (descriptor.EntryAssembly is null)
        {
            return null;
        }

        StagedScriptPackageLoadContext? context = null;
        try
        {
            context = new(staged, dependencies);
            Assembly assembly = context.LoadEntryAssembly();
            return Prepare(
                descriptor,
                services,
                log,
                context,
                assembly,
                generationId);
        }
        catch (Exception exception)
        {
            log.Error(
                $"Package '{descriptor.Name}' could not be prepared from its " +
                $"captured image: {exception}");
            context?.Unload();
            return null;
        }
    }

    private static ScriptPackage? Prepare(
        PackageDescriptor descriptor,
        IScriptServices services,
        RuntimeLog log,
        AssemblyLoadContext context,
        Assembly assembly,
        ulong generationId)
    {
        HashSet<string> expected = new(
            descriptor.ScriptTypeNames,
            StringComparer.Ordinal);
        Type[] scriptTypes = [.. GetLoadableTypes(assembly)
            .Where(type =>
                type.FullName is not null &&
                expected.Contains(type.FullName) &&
                !type.IsAbstract &&
                !type.ContainsGenericParameters &&
                typeof(Script4).IsAssignableFrom(type))
            .OrderBy(type => type.FullName!, StringComparer.Ordinal)];

        if (scriptTypes.Length == 0)
        {
            log.Error(
                $"Package '{descriptor.Name}' contains no loadable Script4 " +
                "executable type.");
            context.Unload();
            return null;
        }

        log.Information(
            $"Package '{descriptor.Name}' binary generation {generationId} " +
            $"was prepared with {scriptTypes.Length} Script4 type(s) and " +
            $"{descriptor.DependencyPackageNames.Count} passive library " +
            "package dependency(ies).");
        return new(
            descriptor,
            log,
            services,
            context,
            assembly,
            scriptTypes,
            generationId);
    }

    public bool StartInstances(
        ScriptStartReason reason,
        ulong lifecycleEpoch)
    {
        if (_loadContext is null || _entryAssembly is null)
        {
            return false;
        }
        if (_scripts.Count != 0)
        {
            throw new InvalidOperationException(
                $"Package '{Name}' already has lifecycle instances.");
        }

        foreach (Type type in _scriptTypes)
        {
            try
            {
                if (Activator.CreateInstance(type, nonPublic: true) is not
                    Script4 script)
                {
                    _log.Error(
                        $"Script type '{type.FullName}' in package '{Name}' " +
                        "could not be instantiated.");
                    continue;
                }

                ScriptInstance instance = new(
                    script,
                    Name,
                    _services,
                    _log,
                    lifecycleEpoch);
                if (instance.Start(reason))
                {
                    _scripts.Add(instance);
                }
            }
            catch (Exception exception)
            {
                _log.Error(
                    $"Script type '{type.FullName}' in package '{Name}' " +
                    $"could not be created: {exception}");
            }
        }

        if (_scripts.Count == 0)
        {
            _log.Error(
                $"Package '{Name}' contains no Script4 executable that could " +
                $"start in lifecycle epoch {lifecycleEpoch}.");
            return false;
        }

        _log.Information(
            $"Package '{Name}' entered lifecycle epoch {lifecycleEpoch} with " +
            $"{_scripts.Count} script executable(s); reason={reason}.");
        return true;
    }

    public void StopInstances(ScriptStopReason reason)
    {
        for (int index = _scripts.Count - 1; index >= 0; --index)
        {
            _scripts[index].Stop(reason);
        }
        _scripts.Clear();
    }

    public void Tick(ScriptTickContext context)
    {
        foreach (ScriptInstance script in _scripts)
        {
            script.Tick(context);
        }
    }

    public WeakReference? StopAndUnload(ScriptStopReason reason)
    {
        StopInstances(reason);
        return UnloadStopped();
    }

    public WeakReference? UnloadStopped()
    {
        if (_scripts.Count != 0)
        {
            throw new InvalidOperationException(
                $"Package '{Name}' cannot unload while lifecycle instances remain.");
        }

        _entryAssembly = null;
        _scriptTypes = [];
        AssemblyLoadContext? context = _loadContext;
        _loadContext = null;
        if (context is null)
        {
            return null;
        }

        WeakReference reference = new(context, trackResurrection: false);
        context.Unload();
        _log.Information(
            $"Package '{Name}' binary generation {GenerationId} was retired.");
        return reference;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}

internal sealed class ScriptInstance(
    Script4 script,
    string packageName,
    IScriptServices services,
    RuntimeLog log,
    ulong lifecycleEpoch)
{
    private static readonly TimeSpan LifecycleDeadline = TimeSpan.FromSeconds(3);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _started;
    private bool _stopped;
    private bool _faulted;

    public bool IsActive => _started && !_stopped && !_faulted;

    public bool Start(ScriptStartReason reason)
    {
        try
        {
            ManagedLifecycleExecutor.Run(
                async deadlineToken =>
                {
                    using CancellationTokenSource start =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            _lifetime.Token,
                            deadlineToken);
                    await script.StartAsync(new(
                            packageName,
                            services,
                            reason,
                            lifecycleEpoch,
                            _lifetime.Token,
                            start.Token))
                        .ConfigureAwait(false);
                },
                LifecycleDeadline,
                $"Script executable '{script.GetType().FullName}' exceeded the " +
                $"{LifecycleDeadline.TotalSeconds:0.###} second startup deadline.");

            _started = true;
            log.Information(
                $"Script executable '{script.GetType().FullName}' started in " +
                $"lifecycle epoch {lifecycleEpoch}; reason={reason}.");
            return true;
        }
        catch (ManagedLifecycleTimeoutException)
        {
            _faulted = true;
            _lifetime.Cancel();
            log.Error(
                $"Script executable '{script.GetType().FullName}' exceeded the " +
                $"{LifecycleDeadline.TotalSeconds:0.###} second startup deadline. " +
                "Its lifecycle authority was revoked; no forced thread abort was attempted.");
            return false;
        }
        catch (Exception exception)
        {
            _faulted = true;
            _lifetime.Cancel();
            log.Error(
                $"Script executable '{script.GetType().FullName}' failed during " +
                $"startup in lifecycle epoch {lifecycleEpoch}: {exception}");
            return false;
        }
    }

    public void Tick(ScriptTickContext context)
    {
        if (!IsActive)
        {
            return;
        }

        try
        {
            script.Tick(context);
        }
        catch (Exception exception)
        {
            _faulted = true;
            log.Error(
                $"Script executable '{script.GetType().FullName}' faulted " +
                $"at tick {context.TickIndex}: {exception}");
            Stop(ScriptStopReason.PackageFault);
        }
    }

    public void Stop(ScriptStopReason reason)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _lifetime.Cancel();
        if (_started)
        {
            try
            {
                ManagedLifecycleExecutor.Run(
                    token => script.StopAsync(new(
                        packageName,
                        reason,
                        lifecycleEpoch,
                        token)),
                    LifecycleDeadline,
                    $"Script executable '{script.GetType().FullName}' exceeded the " +
                    $"{LifecycleDeadline.TotalSeconds:0.###} second shutdown deadline.");
                log.Information(
                    $"Script executable '{script.GetType().FullName}' stopped " +
                    $"from lifecycle epoch {lifecycleEpoch}; reason={reason}.");
            }
            catch (ManagedLifecycleTimeoutException)
            {
                log.Error(
                    $"Script executable '{script.GetType().FullName}' exceeded " +
                    "the shutdown deadline. The runtime detached it and will rely " +
                    "on cooperative ALC collection when its outstanding work exits.");
            }
            catch (Exception exception)
            {
                log.Error(
                    $"Script executable '{script.GetType().FullName}' failed " +
                    $"during shutdown: {exception}");
            }
        }
        _lifetime.Dispose();
    }
}

internal abstract class SharedScriptPackageLoadContext(
    string name) : AssemblyLoadContext(name, isCollectible: true)
{
    private static readonly Assembly SharedRuntimeAssembly =
        typeof(Script4).Assembly;
    private static readonly AssemblyLoadContext SharedLoadContext =
        GetLoadContext(SharedRuntimeAssembly)
        ?? throw new InvalidOperationException(
            "The Script4 contract assembly has no AssemblyLoadContext.");
    private static readonly string SharedAssemblyName =
        SharedRuntimeAssembly.GetName().Name!;
    private static readonly HashSet<string> PlatformSharedAssemblyNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FSharp.Core"
        };

    protected static Assembly? FindSharedAssembly(AssemblyName requested)
    {
        if (string.IsNullOrWhiteSpace(requested.Name))
        {
            return null;
        }

        if (requested.Name.Equals(
                SharedAssemblyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return SharedRuntimeAssembly;
        }

        foreach (Assembly assembly in SharedLoadContext.Assemblies)
        {
            AssemblyName identity = assembly.GetName();
            if (identity.Name?.Equals(
                    requested.Name,
                    StringComparison.OrdinalIgnoreCase) == true &&
                IsRootRuntimeExtension(assembly))
            {
                return assembly;
            }
        }

        if (!PlatformSharedAssemblyNames.Contains(requested.Name))
        {
            return null;
        }

        try
        {
            return SharedLoadContext.LoadFromAssemblyName(requested);
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

    protected static Assembly LoadImage(
        AssemblyLoadContext context,
        PackageAssemblyImage image)
    {
        using MemoryStream assemblyStream = new(image.Assembly, writable: false);
        if (image.Symbols is null)
        {
            return context.LoadFromStream(assemblyStream);
        }

        using MemoryStream symbolStream = new(image.Symbols, writable: false);
        return context.LoadFromStream(assemblyStream, symbolStream);
    }

    private static bool IsRootRuntimeExtension(Assembly assembly)
    {
        foreach (AssemblyMetadataAttribute metadata in
                 assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (metadata.Key.Equals(
                    RuntimeExtensionMetadataKeys.Role,
                    StringComparison.OrdinalIgnoreCase) &&
                metadata.Value?.Equals(
                    RuntimeExtensionMetadataKeys.RuntimeExtensionRole,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class StagedScriptPackageLoadContext :
    SharedScriptPackageLoadContext
{
    private readonly Dictionary<string, PackageAssemblyImage> _images =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly PackageAssemblyImage _entry;
    private readonly string _entryPath;

    public StagedScriptPackageLoadContext(
        StagedPackageImage package,
        IReadOnlyList<StagedPackageImage> dependencies) :
        base($"Script4:{package.Descriptor.Name}")
    {
        string entryPath = package.Descriptor.EntryAssembly
            ?? throw new InvalidOperationException(
                "A staged executable package has no entry assembly.");
        _entryPath = entryPath;

        PackageAssemblyImage? entry = null;
        AddImages(package, allowExisting: false, ref entry, entryPath);
        foreach (StagedPackageImage dependency in dependencies)
        {
            AddImages(dependency, allowExisting: false, ref entry, entryPath);
        }

        _entry = entry
            ?? throw new BadImageFormatException(
                "The staged entry assembly image is missing.");
    }

    public Assembly LoadEntryAssembly() => LoadImage(this, _entry);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        Assembly? shared = FindSharedAssembly(assemblyName);
        if (shared is not null)
        {
            return shared;
        }

        return !string.IsNullOrWhiteSpace(assemblyName.Name) &&
            _images.TryGetValue(
                assemblyName.Name,
                out PackageAssemblyImage? image) &&
            image is not null
                ? LoadImage(this, image)
                : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        try
        {
            AssemblyDependencyResolver resolver = new(_entryPath);
            string? path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private void AddImages(
        StagedPackageImage package,
        bool allowExisting,
        ref PackageAssemblyImage? entry,
        string entryPath)
    {
        foreach (PackageAssemblyImage image in package.Assemblies)
        {
            string? simpleName = TryReadAssemblySimpleName(image.Assembly);
            if (simpleName is null)
            {
                continue;
            }

            if (!_images.TryAdd(simpleName, image) && !allowExisting)
            {
                throw new BadImageFormatException(
                    $"The coherent generation contains duplicate assembly " +
                    $"identity '{simpleName}'.");
            }

            if (Path.GetFullPath(image.Path).Equals(
                    Path.GetFullPath(entryPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                entry = image;
            }
        }
    }

    private static string? TryReadAssemblySimpleName(byte[] image)
    {
        using MemoryStream stream = new(image, writable: false);
        using PEReader pe = new(stream, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata)
        {
            return null;
        }

        MetadataReader metadata = pe.GetMetadataReader();
        return metadata.GetString(metadata.GetAssemblyDefinition().Name);
    }
}