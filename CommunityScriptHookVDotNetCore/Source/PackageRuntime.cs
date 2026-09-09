using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace CommunityScriptHookVDotNetCore.Source;

internal sealed partial class PackageManager : IReloadRuntimeHost
{
    private const ulong UnloadWarningFrameThreshold = 600;

    private enum InitialActivationState
    {
        None,
        CapturingImages,
        ResolvingImage,
        PreparingPackages,
        StartingPackages,
        Completed,
        Failed
    }

    private readonly int _runtimeThreadId = Environment.CurrentManagedThreadId;
    private readonly string _scriptsDirectory;
    private readonly IScriptServices _services;
    private readonly RuntimeLog _log;
    private readonly List<ScriptPackage> _packages = [];
    private readonly List<RetiringPackage> _retiringPackages = [];
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
    private InitialActivationState _initialActivationState;
    private CancellationTokenSource? _initialActivationLifetime;
    private Task<CaptureAttempt>? _initialCaptureTask;
    private Task<StagedReloadImage>? _initialResolveTask;
    private StagedReloadImage? _initialStaged;
    private Queue<StagedPackageImage>? _initialPrepareQueue;
    private Queue<string>? _initialStartQueue;
    private ScriptPackage? _initialStartingPackage;
    private bool _initialActivationStarted;
    private bool _initialActivationCompleted;
    private bool _lifecycleDispatchPaused;
    private bool _shutdown;

    internal bool InitialActivationStarted => _initialActivationStarted;
    internal bool InitialActivationCompleted => _initialActivationCompleted;

    public PackageManager(
        string scriptsDirectory,
        IScriptServices services,
        RuntimeLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsDirectory);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(log);

        _scriptsDirectory = scriptsDirectory;
        _services = services;
        _log = log;
    }

    public void BeginInitialActivation()
    {
        EnsureRuntimeThread();
        ThrowIfStopping();
        if (_initialActivationStarted)
        {
            throw new InvalidOperationException(
                "The initial scripts4 package activation was already started.");
        }

        PackageDiscovery.EnsureFlatAssemblyLayout(_scriptsDirectory);
        _initialActivationStarted = true;
        _initialActivationState = InitialActivationState.CapturingImages;
        _initialActivationLifetime = new();
        StartInitialCapture();
        _log.Information(
            "Initial scripts4 activation entered the frame-driven pipeline.");
    }

    public void AdvanceInitialActivation(ulong hostFrameIndex)
    {
        EnsureRuntimeThread();
        _lastHostFrameIndex = hostFrameIndex;
        ObserveRetiringPackages(hostFrameIndex);
        ObserveUnloadProbes(hostFrameIndex);

        if (_shutdown || !_initialActivationStarted || _initialActivationCompleted)
        {
            return;
        }

        try
        {
            switch (_initialActivationState)
            {
                case InitialActivationState.CapturingImages:
                    AdvanceInitialCapture();
                    break;

                case InitialActivationState.ResolvingImage:
                    AdvanceInitialResolve();
                    break;

                case InitialActivationState.PreparingPackages:
                    AdvanceInitialPreparation();
                    break;

                case InitialActivationState.StartingPackages:
                    AdvanceInitialStart();
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            if (!_shutdown)
            {
                FailInitialActivation(
                    "The initial scripts4 activation was cancelled.");
            }
        }
        catch (Exception exception)
        {
            FailInitialActivation(exception.ToString());
        }
    }

    private void StartInitialCapture()
    {
        CancellationToken cancellationToken =
            _initialActivationLifetime?.Token
            ?? throw new InvalidOperationException(
                "The initial scripts4 activation lifetime is unavailable.");
        string[] names = EnumerateDiskPackageNames();
        _initialCaptureTask = Task.Run(
            () => PackageImageCapture.CaptureAsync(
                _scriptsDirectory,
                names,
                _log,
                cancellationToken),
            cancellationToken);
    }

    private void AdvanceInitialCapture()
    {
        Task<CaptureAttempt>? capture = _initialCaptureTask;
        if (capture is null)
        {
            StartInitialCapture();
            return;
        }
        if (!capture.IsCompleted)
        {
            return;
        }

        CaptureAttempt attempt = capture.GetAwaiter().GetResult();
        _initialCaptureTask = null;
        if (attempt.Image is null)
        {
            StartInitialCapture();
            return;
        }

        CapturedReloadImage captured = attempt.Image;
        CancellationToken token =
            _initialActivationLifetime?.Token ?? CancellationToken.None;
        _initialResolveTask = Task.Run(
            () => ResolveCapturedImage(captured),
            token);
        _initialActivationState = InitialActivationState.ResolvingImage;
    }

    private void AdvanceInitialResolve()
    {
        Task<StagedReloadImage>? resolve = _initialResolveTask;
        if (resolve is null || !resolve.IsCompleted)
        {
            return;
        }

        StagedReloadImage staged = resolve.GetAwaiter().GetResult();
        _initialResolveTask = null;
        _initialStaged = staged;
        ApplyStagedCatalog(staged);
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

        _initialPrepareQueue = new(
            OrderPackageNames(
                    _catalog,
                    executable.Keys,
                    reverse: false)
                .Select(name => executable[name]));
        _initialActivationState = InitialActivationState.PreparingPackages;
        _log.Information(
            $"Initial scripts4 RAM image contains {_initialPrepareQueue.Count} executable package(s).");
    }

    private void AdvanceInitialPreparation()
    {
        if (_initialPrepareQueue is null)
        {
            FailInitialActivation(
                "The initial executable preparation queue is unavailable.");
            return;
        }

        if (_initialPrepareQueue.Count != 0)
        {
            StagedPackageImage image = _initialPrepareQueue.Dequeue();
            List<StagedPackageImage> dependencies = [];
            bool valid = true;
            foreach (string dependencyName in image.Descriptor.DependencyPackageNames)
            {
                if (_initialStaged is null ||
                    !_initialStaged.Packages.TryGetValue(
                        dependencyName,
                        out StagedPackageImage? dependency) ||
                    dependency is null)
                {
                    valid = false;
                    _log.Error(
                        $"Initial package '{image.Descriptor.Name}' could not " +
                        $"materialize passive dependency '{dependencyName}'.");
                    break;
                }
                dependencies.Add(dependency);
            }

            ScriptPackage? package = valid
                ? ScriptPackage.TryPrepare(
                    image,
                    dependencies.AsReadOnly(),
                    _services,
                    _log,
                    NextPackageGeneration())
                : null;
            if (package is not null)
            {
                _packages.Add(package);
            }
            return;
        }

        _initialPrepareQueue = null;
        _initialStaged = null;
        _initialStartQueue = new(OrderPackageNames(
            _catalog,
            _packages.Select(value => value.Name),
            reverse: false));
        _initialActivationState = InitialActivationState.StartingPackages;
    }

    private void AdvanceInitialStart()
    {
        if (_initialStartQueue is null)
        {
            FailInitialActivation(
                "The initial package start queue is unavailable.");
            return;
        }

        if (_initialStartingPackage is not null)
        {
            PackageLifecycleProgress progress =
                _initialStartingPackage.AdvanceStartInstances();
            if (progress is PackageLifecycleProgress.Pending)
            {
                return;
            }

            if (progress is PackageLifecycleProgress.Failed)
            {
                ScriptPackage failed = _initialStartingPackage;
                BeginPackageRetirement(
                    failed,
                    ScriptStopReason.PackageFault,
                    "initial package startup failure");
            }
            _initialStartingPackage = null;
        }

        while (_initialStartQueue.Count != 0)
        {
            string name = _initialStartQueue.Dequeue();
            ScriptPackage? package = FindActivePackage(name);
            if (package is null)
            {
                continue;
            }

            package.BeginStartInstances(
                ScriptStartReason.InitialActivation,
                _lifecycleEpoch);
            _initialStartingPackage = package;
            return;
        }

        CompleteInitialActivation();
    }

    private void CompleteInitialActivation()
    {
        _initialActivationCompleted = true;
        _initialActivationState = InitialActivationState.Completed;
        _initialActivationLifetime?.Dispose();
        _initialActivationLifetime = null;
        int scripts = _packages.Sum(package => package.ActiveScriptCount);
        _log.Information(
            $"Activated RAM-resident scripts4 generation {_lifecycleEpoch}: " +
            $"{_packages.Count} executable package(s), {scripts} Script4 instance(s).");
    }

    private void FailInitialActivation(string diagnostic)
    {
        _initialActivationState = InitialActivationState.Failed;
        _initialActivationLifetime?.Cancel();
        _initialStartingPackage = null;
        _initialResolveTask = null;
        _initialStaged = null;
        _initialPrepareQueue = null;
        _initialStartQueue = null;
        _initialCaptureTask = null;

        ScriptPackage[] affected = [.. OrderActivePackages(reverse: true)];
        BeginPackageRetirements(
            affected,
            ScriptStopReason.PackageFault,
            "initial activation failure");

        _initialActivationLifetime?.Dispose();
        _initialActivationLifetime = null;
        _log.Error(
            $"Initial scripts4 activation failed without killing the managed " +
            $"runtime session: {diagnostic}");
        _initialActivationCompleted = true;
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
        if (closure.Count != 0 &&
            _activeOperation is not null &&
            !_activeOperation.Snapshot.IsTerminal)
        {
            _activeOperation.Cancel(
                this,
                "A required runtime-extension dependency became unavailable.");
            _activeOperation = null;
        }

        ScriptPackage[] affected = [.. OrderActivePackages(reverse: true)
            .Where(package => closure.Contains(package.Name))];
        BeginPackageRetirements(
            affected,
            ScriptStopReason.DependencyUnavailable,
            "required runtime-extension dependency became unavailable");

        foreach (ScriptPackage package in affected)
        {
            _log.Error(
                $"Script package '{package.Name}' is permanently unavailable for " +
                "this GTA session because a required root runtime-extension " +
                "reference became unavailable.");
        }
    }

    public void AdvanceTransitionOperations(ulong hostFrameIndex)
    {
        EnsureRuntimeThread();
        _lastHostFrameIndex = hostFrameIndex;
        ObserveRetiringPackages(hostFrameIndex);
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
        ObserveRetiringPackages(context.HostFrameIndex);
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

            BeginPackageRetirement(
                package,
                ScriptStopReason.PackageFault,
                "all Script4 executables in the package became unavailable");
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
        _initialActivationLifetime?.Cancel();
        _initialStartingPackage = null;
        _initialCaptureTask = null;
        _initialResolveTask = null;
        _initialStaged = null;
        _initialPrepareQueue = null;
        _initialStartQueue = null;
        foreach (LifecycleTransitionOperation operation in _operations)
        {
            operation.Cancel(this, "The managed runtime is shutting down.");
        }
        _pendingOperations.Clear();
        _activeOperation = null;
        _log.Information(
            "Package lifecycle transition intake and background capture were stopped.");
    }

    public async Task ShutdownAsync(ScriptStopReason reason)
    {
        EnsureRuntimeThread();
        BeginShutdown();
        ScriptPackage[] affected = [.. OrderActivePackages(reverse: true)];
        BeginPackageRetirements(affected, reason, "runtime shutdown");

        foreach (LifecycleTransitionOperation operation in _operations)
        {
            operation.Dispose();
        }
        _operations.Clear();
        _pendingOperations.Clear();
        _activeOperation = null;

        await DrainRetiringPackagesAsync().ConfigureAwait(false);
        ObserveUnloadProbes(_lastHostFrameIndex);
    }

    private void BeginPackageRetirements(
        IEnumerable<ScriptPackage> packages,
        ScriptStopReason reason,
        string diagnostic)
    {
        ScriptPackage[] affected = [.. packages
            .Where(package => _packages.Contains(package))
            .Distinct()];
        if (affected.Length == 0)
        {
            return;
        }

        foreach (ScriptPackage package in affected)
        {
            _packages.Remove(package);
        }
        foreach (ScriptPackage package in affected)
        {
            package.BeginStopInstances(reason);
            _retiringPackages.Add(new(package, reason, diagnostic));
            _log.Information(
                $"Script package '{package.Name}' entered asynchronous retirement; " +
                $"reason={reason}; source={diagnostic}.");
        }
    }

    private void BeginPackageRetirement(
        ScriptPackage package,
        ScriptStopReason reason,
        string diagnostic)
    {
        if (_retiringPackages.Any(value => ReferenceEquals(value.Package, package)))
        {
            return;
        }
        if (_packages.Remove(package))
        {
            package.BeginStopInstances(reason);
            _retiringPackages.Add(new(package, reason, diagnostic));
            _log.Information(
                $"Script package '{package.Name}' entered asynchronous retirement; " +
                $"reason={reason}; source={diagnostic}.");
        }
    }

    private void AdoptPackageRetirement(
        ScriptPackage package,
        ScriptStopReason reason,
        string diagnostic)
    {
        if (_retiringPackages.Any(value => ReferenceEquals(value.Package, package)))
        {
            return;
        }

        _packages.Remove(package);
        package.BeginStopInstances(reason);
        _retiringPackages.Add(new(package, reason, diagnostic));
        _log.Information(
            $"Script package '{package.Name}' entered asynchronous retirement; " +
            $"reason={reason}; source={diagnostic}.");
    }

    private void ObserveRetiringPackages(ulong hostFrameIndex)
    {
        for (int index = _retiringPackages.Count - 1; index >= 0; --index)
        {
            RetiringPackage retiring = _retiringPackages[index];
            PackageLifecycleProgress progress = retiring.Package.AdvanceStopInstances();
            if (progress is PackageLifecycleProgress.Pending)
            {
                continue;
            }

            _retiringPackages.RemoveAt(index);
            AddUnloadProbe(
                retiring.Package,
                retiring.Package.UnloadStopped(),
                hostFrameIndex);
            _log.Information(
                $"Script package '{retiring.Package.Name}' completed cleanup; " +
                $"reason={retiring.Reason}; source={retiring.Diagnostic}.");
        }
    }

    private async Task DrainRetiringPackagesAsync()
    {
        for (;;)
        {
            ObserveRetiringPackages(_lastHostFrameIndex);
            if (_retiringPackages.Count == 0)
            {
                return;
            }

            Task[] pending = [.. _retiringPackages
                .SelectMany(value => value.Package.PendingRetirementTasks)
                .Where(task => !task.IsCompleted)];
            if (pending.Length == 0)
            {
                await Task.Yield();
                continue;
            }
            await Task.WhenAny(pending).ConfigureAwait(false);
        }
    }

    private sealed record RetiringPackage(
        ScriptPackage Package,
        ScriptStopReason Reason,
        string Diagnostic);

}
internal enum PackageLifecycleProgress
{
    Pending,
    Succeeded,
    Failed
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
    private Queue<Type>? _pendingStartTypes;
    private ScriptInstance? _startingScript;
    private ScriptStartReason _startReason;
    private ScriptStopReason _stopReason;
    private ulong _startEpoch;
    private bool _stopInProgress;

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

    public IEnumerable<Task> PendingRetirementTasks =>
        _scripts
            .Select(script => script.RetirementTask)
            .OfType<Task>();

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

    public void BeginStartInstances(
        ScriptStartReason reason,
        ulong lifecycleEpoch)
    {
        if (_loadContext is null || _entryAssembly is null)
        {
            throw new InvalidOperationException(
                $"Package '{Name}' is not loaded.");
        }
        if (_scripts.Count != 0 || _pendingStartTypes is not null)
        {
            throw new InvalidOperationException(
                $"Package '{Name}' already has lifecycle instances or a start in progress.");
        }

        _startReason = reason;
        _startEpoch = lifecycleEpoch;
        _pendingStartTypes = new(_scriptTypes);
    }

    public PackageLifecycleProgress AdvanceStartInstances()
    {
        if (_pendingStartTypes is null)
        {
            throw new InvalidOperationException(
                $"Package '{Name}' has no start operation in progress.");
        }

        if (_startingScript is not null)
        {
            PackageLifecycleProgress progress = _startingScript.AdvanceStart();
            if (progress is PackageLifecycleProgress.Pending)
            {
                return progress;
            }

            _scripts.Add(_startingScript);
            _startingScript = null;
        }

        while (_pendingStartTypes.Count != 0)
        {
            Type type = _pendingStartTypes.Dequeue();
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
                    _startEpoch);
                instance.BeginStart(_startReason);
                _startingScript = instance;
                return PackageLifecycleProgress.Pending;
            }
            catch (Exception exception)
            {
                _log.Error(
                    $"Script type '{type.FullName}' in package '{Name}' " +
                    $"could not begin startup: {exception}");
            }
        }

        _pendingStartTypes = null;
        if (ActiveScriptCount == 0)
        {
            _log.Error(
                $"Package '{Name}' contains no Script4 executable that could " +
                $"start in lifecycle epoch {_startEpoch}.");
            return PackageLifecycleProgress.Failed;
        }

        _log.Information(
            $"Package '{Name}' entered lifecycle epoch {_startEpoch} with " +
            $"{ActiveScriptCount} script executable(s); reason={_startReason}.");
        return PackageLifecycleProgress.Succeeded;
    }

    public void BeginStopInstances(ScriptStopReason reason)
    {
        if (_stopInProgress)
        {
            return;
        }

        _stopInProgress = true;
        _stopReason = reason;
        _pendingStartTypes = null;
        if (_startingScript is not null)
        {
            _startingScript.BeginStop(reason);
            _scripts.Add(_startingScript);
            _startingScript = null;
        }
        foreach (ScriptInstance instance in _scripts)
        {
            instance.BeginStop(reason);
        }
    }

    public PackageLifecycleProgress AdvanceStopInstances()
    {
        if (!_stopInProgress)
        {
            throw new InvalidOperationException(
                $"Package '{Name}' has no stop operation in progress.");
        }

        bool pending = false;
        for (int index = _scripts.Count - 1; index >= 0; --index)
        {
            ScriptInstance instance = _scripts[index];
            PackageLifecycleProgress progress = instance.AdvanceStop();
            if (progress is PackageLifecycleProgress.Pending)
            {
                pending = true;
                continue;
            }
            _scripts.RemoveAt(index);
        }

        if (pending)
        {
            return PackageLifecycleProgress.Pending;
        }

        _stopInProgress = false;
        return PackageLifecycleProgress.Succeeded;
    }

    public void Tick(ScriptTickContext context)
    {
        ObserveCompletedScriptRetirements();
        ScriptInstance[] frame = [.. _scripts];
        foreach (ScriptInstance script in frame)
        {
            script.Tick(context);
        }
        ObserveCompletedScriptRetirements();
    }

    private void ObserveCompletedScriptRetirements()
    {
        if (_stopInProgress)
        {
            return;
        }

        for (int index = _scripts.Count - 1; index >= 0; --index)
        {
            ScriptInstance instance = _scripts[index];
            if (!instance.IsRetiring)
            {
                continue;
            }
            if (instance.AdvanceStop() is PackageLifecycleProgress.Pending)
            {
                continue;
            }
            _scripts.RemoveAt(index);
        }
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

internal sealed class ScriptExecutionMetrics
{
    internal static readonly TimeSpan SlowTickThreshold =
        TimeSpan.FromMilliseconds(50);

    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private int _nativeCalls;

    internal int NativeCalls => _nativeCalls;

    internal TimeSpan Elapsed =>
        Stopwatch.GetElapsedTime(_startedAt);

    internal bool SlowTick =>
        Elapsed > SlowTickThreshold;

    internal void RecordNativeCall() => ++_nativeCalls;

    internal string Describe() =>
        $"nativeCalls={_nativeCalls}; " +
        $"wallTimeMs={Elapsed.TotalMilliseconds:0.###}; " +
        $"diagnosticThresholdMs={SlowTickThreshold.TotalMilliseconds:0.###}";
}

internal static class ScriptExecutionMetricsContext
{
    private static readonly AsyncLocal<ScriptExecutionMetrics?> Current = new();

    internal static void RecordNativeCall() =>
        Current.Value?.RecordNativeCall();

    internal static IDisposable Enter(ScriptExecutionMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ScriptExecutionMetrics? previous = Current.Value;
        Current.Value = metrics;
        return new Scope(previous);
    }

    private sealed class Scope(
        ScriptExecutionMetrics? previous) : IDisposable
    {
        private ScriptExecutionMetrics? _previous = previous;
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

internal sealed class ScriptInstance(
    Script4 script,
    string packageName,
    IScriptServices services,
    RuntimeLog log,
    ulong lifecycleEpoch)
{
    private static readonly TimeSpan StartupDeadline = TimeSpan.FromSeconds(3);
    private readonly CancellationTokenSource _lifetime = new();
    private ManagedLifecycleOperation? _startup;
    private ManagedRetirementOperation? _shutdown;
    private ManagedLifecycleAuthority? _lifecycleAuthority;
    private ScriptStartReason _startReason;
    private ScriptStopReason _stopReason;
    private bool _started;
    private bool _stopped;
    private bool _stopCompleted;
    private bool _faulted;
    private bool _slowTickWarningActive;

    public bool IsActive => _started && !_stopped && !_faulted;

    public bool IsRetiring => _stopped && !_stopCompleted;

    public Task? RetirementTask => _shutdown?.Completion;

    public void BeginStart(ScriptStartReason reason)
    {
        if (_startup is not null || _started || _stopped)
        {
            throw new InvalidOperationException(
                $"Script executable '{script.GetType().FullName}' cannot begin startup twice.");
        }

        _startReason = reason;
        _startup = ManagedLifecycleOperation.Start(
            token => script.StartAsync(new(
                packageName,
                services,
                reason,
                lifecycleEpoch,
                _lifetime.Token,
                token)),
            StartupDeadline,
            _lifetime.Token);
    }

    public PackageLifecycleProgress AdvanceStart()
    {
        ManagedLifecycleOperation operation = _startup
            ?? throw new InvalidOperationException(
                $"Script executable '{script.GetType().FullName}' has no startup operation.");

        ManagedLifecycleOperationSnapshot snapshot = operation.Poll();
        if (snapshot.Status is ManagedLifecycleOperationStatus.Pending)
        {
            return PackageLifecycleProgress.Pending;
        }

        _startup = null;
        ManagedLifecycleAuthority authority = operation.Authority;
        Task startupCompletion = operation.Completion;
        operation.Dispose();

        switch (snapshot.Status)
        {
            case ManagedLifecycleOperationStatus.Succeeded:
                _lifecycleAuthority = authority;
                _started = true;
                log.Information(
                    $"Script executable '{script.GetType().FullName}' started in " +
                    $"lifecycle epoch {lifecycleEpoch}; reason={_startReason}.");
                return PackageLifecycleProgress.Succeeded;

            case ManagedLifecycleOperationStatus.TimedOut:
                _faulted = true;
                _lifetime.Cancel();
                BeginRetirement(
                    ScriptStopReason.PackageFault,
                    startupCompletion);
                log.Error(
                    $"Script executable '{script.GetType().FullName}' exceeded its " +
                    $"{StartupDeadline.TotalSeconds:0.###} second startup deadline " +
                    "and was made unavailable.");
                return PackageLifecycleProgress.Failed;

            case ManagedLifecycleOperationStatus.Cancelled:
                _faulted = true;
                _lifetime.Cancel();
                BeginRetirement(
                    ScriptStopReason.PackageFault,
                    startupCompletion);
                log.Error(
                    $"Script executable '{script.GetType().FullName}' startup was " +
                    $"cancelled in lifecycle epoch {lifecycleEpoch}.");
                return PackageLifecycleProgress.Failed;

            case ManagedLifecycleOperationStatus.Faulted:
                _faulted = true;
                _lifetime.Cancel();
                BeginRetirement(
                    ScriptStopReason.PackageFault,
                    startupCompletion);
                log.Error(
                    $"Script executable '{script.GetType().FullName}' failed during " +
                    $"startup in lifecycle epoch {lifecycleEpoch}: {snapshot.Exception}");
                return PackageLifecycleProgress.Failed;

            default:
                throw new InvalidOperationException(
                    "The Script4 startup operation reached an invalid state.");
        }
    }

    public void Tick(ScriptTickContext context)
    {
        if (!IsActive)
        {
            return;
        }

        ScriptExecutionMetrics metrics = new();
        try
        {
            ManagedLifecycleAuthority authority = _lifecycleAuthority
                ?? throw new InvalidOperationException(
                    $"Script executable '{script.GetType().FullName}' has no active lifecycle authority.");
            using IDisposable authorityScope =
                ManagedLifecycleAuthorityContext.Enter(authority);
            using IDisposable metricsScope =
                ScriptExecutionMetricsContext.Enter(metrics);

            script.Tick(context);

            if (metrics.SlowTick)
            {
                if (!_slowTickWarningActive)
                {
                    log.Warning(
                        $"Script executable '{script.GetType().FullName}' exceeded " +
                        $"the wall-time diagnostic threshold at tick " +
                        $"{context.TickIndex}; the script remains active and missed " +
                        $"64-tick periods will not be replayed. {metrics.Describe()}");
                    _slowTickWarningActive = true;
                }
            }
            else
            {
                _slowTickWarningActive = false;
            }
        }
        catch (Exception exception)
        {
            _faulted = true;
            string diagnostic = metrics.SlowTick
                ? $" Diagnostics: {metrics.Describe()}."
                : string.Empty;
            log.Error(
                $"Script executable '{script.GetType().FullName}' faulted " +
                $"at tick {context.TickIndex}:{diagnostic} {exception}");
            BeginStop(ScriptStopReason.PackageFault);
        }
    }

    public void BeginStop(ScriptStopReason reason)
    {
        if (_stopped)
        {
            return;
        }

        Task? predecessor = null;
        if (_startup is not null)
        {
            predecessor = _startup.Completion;
            _startup.Cancel();
            _startup.Dispose();
            _startup = null;
        }
        BeginRetirement(reason, predecessor);
    }

    private void BeginRetirement(
        ScriptStopReason reason,
        Task? predecessor)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _stopReason = reason;
        _lifecycleAuthority?.ForceRevoke();
        _lifecycleAuthority = null;
        _lifetime.Cancel();
        _shutdown = ManagedRetirementOperation.Start(
            () => script.StopAsync(new(
                packageName,
                reason,
                lifecycleEpoch)),
            predecessor);
    }

    public PackageLifecycleProgress AdvanceStop()
    {
        if (!_stopped)
        {
            throw new InvalidOperationException(
                $"Script executable '{script.GetType().FullName}' has not begun shutdown.");
        }
        if (_stopCompleted)
        {
            return PackageLifecycleProgress.Succeeded;
        }

        ManagedRetirementOperation? operation = _shutdown;
        if (operation is null)
        {
            _started = false;
            _stopCompleted = true;
            _lifetime.Dispose();
            return PackageLifecycleProgress.Succeeded;
        }

        ManagedRetirementOperationSnapshot snapshot = operation.Poll();
        if (snapshot.Status is ManagedRetirementOperationStatus.Pending)
        {
            return PackageLifecycleProgress.Pending;
        }

        _shutdown = null;
        _started = false;
        _stopCompleted = true;
        _lifetime.Dispose();

        if (snapshot.Status is ManagedRetirementOperationStatus.Succeeded)
        {
            log.Information(
                $"Script executable '{script.GetType().FullName}' completed cleanup " +
                $"from lifecycle epoch {lifecycleEpoch}; reason={_stopReason}.");
        }
        else
        {
            log.Error(
                $"Script executable '{script.GetType().FullName}' cleanup faulted " +
                $"after retirement; reason={_stopReason}: {snapshot.Exception}");
        }

        return PackageLifecycleProgress.Succeeded;
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