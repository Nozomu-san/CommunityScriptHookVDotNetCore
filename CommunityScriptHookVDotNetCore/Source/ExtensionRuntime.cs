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
    private static readonly TimeSpan RootLifecycleTimeout = TimeSpan.FromSeconds(5);
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
        RuntimeCapabilities.CooperativeShutdown,
        RuntimeCapabilities.PackageLifecycle,
        RuntimeCapabilities.PackageTransitionHost,
        RuntimeCapabilities.ScriptScheduler
    ];

    private readonly List<ActiveRuntimeExtension> _active = [];
    private readonly HashSet<string> _unavailableAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _newlyUnavailableAssemblies = [];
    private RootAssemblyResolver? _resolver;
    private bool _shutdown;

    internal IReadOnlyCollection<string> UnavailableAssemblyNames =>
        _unavailableAssemblies;

    public void LoadAndInitialize()
    {
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

        HashSet<string> available = new(
            CoreCapabilities,
            StringComparer.OrdinalIgnoreCase);
        List<RuntimeExtensionDescriptor> pending =
        [.. discovered
            .Where(value => !rejected.Contains(value.Id))
            .OrderBy(value => value.Id, StringComparer.OrdinalIgnoreCase)];

        while (pending.Count != 0)
        {
            bool progressed = false;
            for (int index = 0; index < pending.Count;)
            {
                RuntimeExtensionDescriptor descriptor = pending[index];
                if (descriptor.Requires.Any(requirement =>
                        !available.Contains(requirement)))
                {
                    ++index;
                    continue;
                }

                pending.RemoveAt(index);
                progressed = true;
                if (TryInitialize(descriptor, out ActiveRuntimeExtension? active))
                {
                    _active.Add(active);
                    foreach (string capability in descriptor.Provides)
                    {
                        available.Add(capability);
                    }
                }
                else
                {
                    MarkUnavailable(descriptor);
                }
            }

            if (progressed)
            {
                continue;
            }

            foreach (RuntimeExtensionDescriptor descriptor in pending)
            {
                string missing = string.Join(
                    ", ",
                    descriptor.Requires.Where(requirement =>
                        !available.Contains(requirement)));
                log.Error(
                    $"Runtime extension '{descriptor.Id}' is quarantined for " +
                    $"this GTA session. Unsatisfied capabilities: {missing}.");
                MarkUnavailable(descriptor);
            }
            pending.Clear();
        }

        log.Information(
            $"Activated {_active.Count} root runtime extension(s); " +
            $"quarantined {_unavailableAssemblies.Count} root assembly(ies). " +
            "Root extensions have one lifecycle per GTA process and are never retried.");
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
                        "a capability dependency on that provider. " +
                        $"Provider capabilities: [{capabilities}]. The consumer is " +
                        "quarantined so helper assemblies cannot bypass the " +
                        "Provides/Requires lifecycle graph.");
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
        for (int index = 0; index < _active.Count; ++index)
        {
            ActiveRuntimeExtension active = _active[index];
            if (active.Faulted)
            {
                continue;
            }

            try
            {
                active.Instance.AdvanceHostFrame(context);
            }
            catch (Exception exception)
            {
                log.Error(
                    $"Runtime extension '{active.Descriptor.Id}' faulted while " +
                    $"advancing a host frame and is permanently quarantined: {exception}");
                Quarantine(active.Descriptor.Id, "host-frame fault");
            }
        }
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

    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }
        _shutdown = true;

        for (int index = _active.Count - 1; index >= 0; --index)
        {
            ActiveRuntimeExtension active = _active[index];
            ShutdownOne(active, "runtime shutdown");
            services.RevokeOwner(active.Descriptor.Id);
        }
        _active.Clear();
    }

    public void Dispose()
    {
        Shutdown();
        _resolver?.Dispose();
        _resolver = null;
    }

    private bool TryInitialize(
        RuntimeExtensionDescriptor descriptor,
        [NotNullWhen(true)] out ActiveRuntimeExtension? active)
    {
        active = null;
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
            if (created is not IScript4RuntimeExtension instance)
            {
                throw new InvalidOperationException(
                    $"Entry type '{descriptor.EntryType}' does not implement " +
                    $"{nameof(IScript4RuntimeExtension)} from the active contract assembly.");
            }

            bool nativeAuthority = descriptor.Provides.Contains(
                "native.call.admission",
                StringComparer.OrdinalIgnoreCase);
            IRuntimeServiceRegistry ownerServices = services.CreateOwnerScope(
                descriptor.Id,
                nativeAuthority);
            RuntimeExtensionContext context = new(
                rootDirectory,
                scriptsDirectory,
                ownerServices);

            ManagedLifecycleExecutor.Run(
                token => instance.InitializeAsync(context, token),
                RootLifecycleTimeout,
                $"Runtime extension '{descriptor.Id}' exceeded the initialization deadline.");
            active = new(descriptor, assembly, instance, Faulted: false);
            log.Information(
                $"Runtime extension '{descriptor.Id}' initialized for its one " +
                "root lifecycle in this GTA process.");
            return true;
        }
        catch (ManagedLifecycleTimeoutException)
        {
            services.RevokeOwner(descriptor.Id);
            log.Error(
                $"Runtime extension '{descriptor.Id}' exceeded its initialization " +
                "deadline. Managed-brain startup is aborted because root " +
                "infrastructure may still have cooperative work in flight.");
            throw;
        }
        catch (Exception exception)
        {
            services.RevokeOwner(descriptor.Id);
            log.Error(
                $"Runtime extension '{descriptor.Id}' could not initialize and " +
                $"will not be retried before the game restarts: {exception}");
            return false;
        }
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
                if (active.Faulted)
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

                if (active.Descriptor.Requires.Any(removedCapabilities.Contains) &&
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
        for (int index = _active.Count - 1; index >= 0; --index)
        {
            ActiveRuntimeExtension active = _active[index];
            if (active.Faulted || !extensionIds.Contains(active.Descriptor.Id))
            {
                continue;
            }

            active.Faulted = true;
            ShutdownOne(active, reason);
            services.RevokeOwner(active.Descriptor.Id);
            MarkUnavailable(active.Descriptor);
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

    private void ShutdownOne(ActiveRuntimeExtension active, string reason)
    {
        try
        {
            ManagedLifecycleExecutor.Run(
                token => active.Instance.ShutdownAsync(token),
                RootLifecycleTimeout,
                $"Runtime extension '{active.Descriptor.Id}' exceeded the shutdown deadline.");
            log.Information(
                $"Runtime extension '{active.Descriptor.Id}' stopped; reason={reason}.");
        }
        catch (Exception exception)
        {
            log.Error(
                $"Runtime extension '{active.Descriptor.Id}' failed during " +
                $"best-effort shutdown: {exception}");
        }
    }

    private sealed class ActiveRuntimeExtension(
        RuntimeExtensionDescriptor descriptor,
        Assembly assembly,
        IScript4RuntimeExtension instance,
        bool Faulted)
    {
        public RuntimeExtensionDescriptor Descriptor { get; } = descriptor;
        public Assembly Assembly { get; } = assembly;
        public IScript4RuntimeExtension Instance { get; } = instance;
        public bool Faulted { get; set; } = Faulted;
    }
}

internal sealed class ManagedLifecycleTimeoutException(
    string message) : TimeoutException(message)
{
}

internal static class ManagedLifecycleExecutor
{
    public static void Run(
        Func<CancellationToken, ValueTask> operationFactory,
        TimeSpan timeout,
        string timeoutMessage)
    {
        ArgumentNullException.ThrowIfNull(operationFactory);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "A managed lifecycle deadline must be positive.");
        }

        CancellationTokenSource deadline = new(timeout);
        CancellationToken deadlineToken = deadline.Token;
        bool deadlineRetired = false;
        Task operation = Task.Run(
            async () =>
            {
                await operationFactory(deadlineToken).ConfigureAwait(false);
            },
            CancellationToken.None);

        try
        {
            operation.WaitAsync(deadlineToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested)
        {
            deadlineRetired = true;
            RetireTimedOutOperation(operation, deadline);
            throw new ManagedLifecycleTimeoutException(timeoutMessage);
        }
        finally
        {
            if (!deadlineRetired)
            {
                deadline.Dispose();
            }
        }
    }

    private static void RetireTimedOutOperation(
        Task operation,
        CancellationTokenSource deadline)
    {
        if (operation.IsCompleted)
        {
            _ = operation.Exception;
            deadline.Dispose();
            return;
        }

        _ = operation.ContinueWith(
            static (task, state) =>
            {
                _ = task.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            deadline,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            $"Committed {images.Count} root managed assembly image(s) to one " +
            "immutable RAM snapshot. Extension discovery and execution use the " +
            "same captured bytes.");
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
            $"Root resolver activated from {snapshot.Images.Count} committed RAM " +
            "image(s). No root assembly bytes will be reread from disk.");
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