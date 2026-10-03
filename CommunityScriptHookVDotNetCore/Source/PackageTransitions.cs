using System.Collections.ObjectModel;
using System.Diagnostics;

namespace CommunityScriptHookVDotNetCore.Source;

internal sealed partial class PackageManager
{
    public ScriptLifecycleTransitionOperationId RequestTransition(
        ScriptLifecycleTransitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureRuntimeThread();
        ThrowIfStopping();
        PackageDiscovery.EnsureFlatAssemblyLayout(_scriptsDirectory);

        ScriptLifecycleTransitionOperationId id =
            ScriptLifecycleTransitionOperationId.Create();
        LifecycleTransitionOperation operation = new(
            id,
            plan,
            _scriptsDirectory,
            _log);
        _operations.Add(operation);
        _pendingOperations.Enqueue(operation);
        _log.Information(
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
        HashSet<string> closure = [with(seeds, StringComparer.OrdinalIgnoreCase)];
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
            .EnumerateFiles(_scriptsDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)];

    private string[] CreateFullCaptureTargets()
    {
        HashSet<string> names = [with(_catalog.Select(value => value.Name),
            StringComparer.OrdinalIgnoreCase)];
        names.UnionWith(EnumerateDiskPackageNames());
        return [.. names.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, string> Fingerprints(
        StagedReloadImage staged)
    {
        HashSet<string> valid = [with(staged.Catalog.Select(value => value.Name),
            StringComparer.OrdinalIgnoreCase)];
        Dictionary<string, string> result = [with(StringComparer.OrdinalIgnoreCase)];
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
        HashSet<string> names = [with(_activeFingerprints.Keys,
            StringComparer.OrdinalIgnoreCase)];
        names.UnionWith(candidate.Keys);

        HashSet<string> activeExecutables = [with(_packages.Select(package => package.Name),
            StringComparer.OrdinalIgnoreCase)];
        HashSet<string> missingExecutableGeneration = [with(staged.Catalog
                .Where(package =>
                    package.Kind == ScriptPackageKind.Executable &&
                    !activeExecutables.Contains(package.Name))
                .Select(package => package.Name),
            StringComparer.OrdinalIgnoreCase)];

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

    private static string[] OrderPackageNames(
        IReadOnlyList<PackageDescriptor> catalog,
        IEnumerable<string> names,
        bool reverse)
    {
        HashSet<string> selected = [with(names,
            StringComparer.OrdinalIgnoreCase)];
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

        SortedSet<string> ready = [with(indegree
                .Where(value => value.Value == 0)
                .Select(value => value.Key),
            StringComparer.OrdinalIgnoreCase)];
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
            HashSet<string> invalidSeeds = [with(resolvedCatalog
                    .Where(package => package.ReferencedAssemblyNames.Any(reference =>
                        _unavailableRuntimeAssemblies.Contains(reference)))
                    .Select(package => package.Name),
                StringComparer.OrdinalIgnoreCase)];
            HashSet<string> invalid = ExpandDependentClosure(
                invalidSeeds,
                resolvedCatalog);
            if (invalid.Count != 0)
            {
                foreach (string name in invalid.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    _log.Error(
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
        Dictionary<string, StagedPackageImage> stagedPackages = [with(
            StringComparer.OrdinalIgnoreCase)];
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
                _log.Information(
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
            _log.Warning(
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
        bool WarningEmitted,
        WeakReference Context);

    private sealed class LifecycleTransitionOperation(
        ScriptLifecycleTransitionOperationId id,
        ScriptLifecycleTransitionPlan plan,
        string _scriptsDirectory,
        RuntimeLog _log) : IDisposable
    {
        private static readonly TimeSpan CaptureDeadline = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RemovalConfirmationWindow =
            TimeSpan.FromMilliseconds(250);
        private readonly string _scriptsDirectory = _scriptsDirectory;
        private readonly RuntimeLog _log = _log;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<string> _restartedInPlace = [];
        private readonly List<string> _binaryReplaced = [];
        private readonly List<string> _libraries = [];
        private readonly List<string> _added = [];
        private readonly List<string> _removed = [];
        private readonly List<string> _failed = [];
        private HashSet<string> _targetSet = [with(StringComparer.OrdinalIgnoreCase)];
        private string[] _binarySeeds = [];
        private HashSet<string> _originalPackageNames = [with(StringComparer.OrdinalIgnoreCase)];
        private Task<CaptureAttempt>? _captureTask;
        private long _captureStarted;
        private long _removalConfirmationStarted;
        private string? _pendingRemovalManifest;
        private StagedReloadImage? _staged;
        private Queue<string>? _stopQueue;
        private ScriptPackage? _stoppingPackage;
        private Queue<string>? _replaceQueue;
        private Queue<StagedPackageImage>? _prepareQueue;
        private Queue<string>? _startQueue;
        private ScriptPackage? _startingPackage;
        private bool _startingPackageReplaced;
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
                    Cancel(owner, "The lifecycle transition was cancelled.");
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
                Cancel(owner, "The lifecycle transition was cancelled.");
            }
            catch (Exception exception)
            {
                Fail(owner, exception.ToString());
            }
        }

        public void Cancel(PackageManager owner, string diagnostic)
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
            if (_stoppingPackage is not null)
            {
                owner.AdoptPackageRetirement(
                    _stoppingPackage,
                    _targetSet.Contains(_stoppingPackage.Name)
                        ? ScriptStopReason.BinaryReplacement
                        : ScriptStopReason.LifecycleRestart,
                    "lifecycle transition cancellation");
                _stoppingPackage = null;
            }
            _stopQueue = null;
            _replaceQueue = null;
            _prepareQueue = null;
            if (_startingPackage is not null)
            {
                owner.AdoptPackageRetirement(
                    _startingPackage,
                    _startingPackageReplaced
                        ? ScriptStopReason.BinaryReplacement
                        : ScriptStopReason.LifecycleRestart,
                    "lifecycle transition cancellation");
                _startingPackage = null;
                _startingPackageReplaced = false;
            }
            _startQueue = null;
            if (_barrierEntered)
            {
                owner.EndLifecycleTransition();
                _barrierEntered = false;
            }
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
                _lifetime.Cancel();
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
                Cancel(owner, "Generation capture was cancelled.");
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

            if (plan.Reason is ScriptLifecycleTransitionReason.AutomaticReload &&
                owner.CandidateRemovesCommittedPackage(staged))
            {
                string manifest = CandidateManifestKey(staged);
                if (_pendingRemovalManifest is null ||
                    !_pendingRemovalManifest.Equals(manifest, StringComparison.Ordinal))
                {
                    _pendingRemovalManifest = manifest;
                    _removalConfirmationStarted = Stopwatch.GetTimestamp();
                    _diagnostic =
                        "A committed DLL is absent from disk. Automatic " +
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
            _originalPackageNames = [with(owner._catalog.Select(value => value.Name),
                StringComparer.OrdinalIgnoreCase)];
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

            if (_stoppingPackage is not null)
            {
                PackageLifecycleProgress progress =
                    _stoppingPackage.AdvanceStopInstances();
                if (progress is PackageLifecycleProgress.Pending)
                {
                    return;
                }
                _stoppingPackage = null;
            }

            while (_stopQueue.Count != 0)
            {
                string name = _stopQueue.Dequeue();
                ScriptPackage? package = owner.FindActivePackage(name);
                if (package is null)
                {
                    continue;
                }

                package.BeginStopInstances(
                    _targetSet.Contains(name)
                        ? ScriptStopReason.BinaryReplacement
                        : ScriptStopReason.LifecycleRestart);
                _stoppingPackage = package;
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

            HashSet<string> candidateNames = [with(_staged.Catalog.Select(value => value.Name),
                StringComparer.OrdinalIgnoreCase)];
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

            if (_startingPackage is not null)
            {
                string name = _startingPackage.Name;
                PackageLifecycleProgress progress =
                    _startingPackage.AdvanceStartInstances();
                if (progress is PackageLifecycleProgress.Pending)
                {
                    return;
                }

                if (progress is PackageLifecycleProgress.Failed)
                {
                    AddUnique(_failed, name);
                    owner.AdoptPackageRetirement(
                        _startingPackage,
                        ScriptStopReason.PackageFault,
                        "lifecycle restart startup failure");
                }
                else if (_startingPackageReplaced)
                {
                    AddUnique(_binaryReplaced, name);
                }
                else
                {
                    AddUnique(_restartedInPlace, name);
                }

                _startingPackage = null;
                _startingPackageReplaced = false;
            }

            while (_startQueue.Count != 0)
            {
                string name = _startQueue.Dequeue();
                ScriptPackage? package = owner.FindActivePackage(name);
                if (package is null)
                {
                    continue;
                }

                bool replaced = _targetSet.Contains(name);
                package.BeginStartInstances(
                    replaced
                        ? ScriptStartReason.BinaryReplacement
                        : ScriptStartReason.LifecycleRestart,
                    _lifecycleEpoch.Value);
                _startingPackage = package;
                _startingPackageReplaced = replaced;
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
            if (_startingPackage is not null)
            {
                owner.AdoptPackageRetirement(
                    _startingPackage,
                    _startingPackageReplaced
                        ? ScriptStopReason.BinaryReplacement
                        : ScriptStopReason.LifecycleRestart,
                    "lifecycle transition failure");
                _startingPackage = null;
                _startingPackageReplaced = false;
            }
            if (_stoppingPackage is not null)
            {
                owner.AdoptPackageRetirement(
                    _stoppingPackage,
                    _targetSet.Contains(_stoppingPackage.Name)
                        ? ScriptStopReason.BinaryReplacement
                        : ScriptStopReason.LifecycleRestart,
                    "lifecycle transition failure");
                _stoppingPackage = null;
            }
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