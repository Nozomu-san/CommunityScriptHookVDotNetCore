using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace CommunityScriptHookVDotNetCore.Source;

internal sealed class RuntimeExtensionManager(
    string rootDirectory,
    string scriptsDirectory,
    IRuntimeServiceRegistry services,
    RuntimeLog log) : IDisposable
{
    private static readonly Assembly ContractAssembly =
        typeof(IScript4RuntimeExtension).Assembly;
    private static readonly AssemblyLoadContext ContractLoadContext =
        AssemblyLoadContext.GetLoadContext(ContractAssembly)
        ?? throw new InvalidOperationException(
            "The runtime-extension contract assembly has no AssemblyLoadContext.");

    private readonly RuntimeExtensionContext _context = new(
            rootDirectory,
            scriptsDirectory,
            services);
    private readonly List<ActiveRuntimeExtension> _active = [];
    private RootAssemblyResolver? _resolver;
    private bool _shutdown;

    public void LoadAndInitialize()
    {
        IReadOnlyList<RuntimeExtensionDescriptor> discovered =
            RuntimeExtensionDiscovery.Discover(rootDirectory, log);
        if (discovered.Count == 0)
        {
            throw new InvalidOperationException(
                "The mandatory scripts4 lifecycle extension is unavailable.");
        }

        _resolver = new(
            rootDirectory,
            ContractLoadContext,
            ContractAssembly,
            log);

        HashSet<string> available = new(
            [
                RuntimeCapabilities.RuntimeServices,
                RuntimeCapabilities.HostFrame,
                RuntimeCapabilities.RawNative,
                RuntimeCapabilities.CooperativeShutdown,
                RuntimeCapabilities.PackageLifecycle,
                RuntimeCapabilities.PackageTransitionHost,
                RuntimeCapabilities.ScriptScheduler
            ],
            StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> providers = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (string capability in available)
        {
            providers.Add(capability, "CommunityScriptHookVDotNetCore");
        }
        foreach (RuntimeExtensionDescriptor descriptor in discovered)
        {
            foreach (string capability in descriptor.Provides)
            {
                if (!providers.TryAdd(capability, descriptor.Id))
                {
                    throw new InvalidOperationException(
                        $"Capability '{capability}' is provided by both " +
                        $"'{providers[capability]}' and '{descriptor.Id}'.");
                }
            }
        }

        List<RuntimeExtensionDescriptor> pending =
            [.. discovered.OrderBy(value => value.Id, StringComparer.OrdinalIgnoreCase)];
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
                    $"Runtime extension '{descriptor.Id}' was not activated. " +
                    $"Missing capabilities: {missing}.");
            }

            throw new InvalidOperationException(
                "The root runtime-extension dependency graph could not be " +
                "activated completely.");
        }

        if (!available.Contains(RuntimeCapabilities.Scripts4Lifecycle))
        {
            throw new InvalidOperationException(
                "The mandatory scripts4 lifecycle capability was not provided.");
        }

        log.Information(
            $"Activated {_active.Count} root runtime extension(s).");
    }

    public void AdvanceHostFrame(RuntimeExtensionFrameContext context)
    {
        foreach (ActiveRuntimeExtension active in _active)
        {
            try
            {
                active.Instance.AdvanceHostFrame(context);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Runtime extension '{active.Descriptor.Id}' faulted " +
                    "while advancing a host frame.",
                    exception);
            }
        }
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
            try
            {
                Complete(active.Instance.ShutdownAsync(CancellationToken.None));
                log.Information(
                    $"Runtime extension '{active.Descriptor.Id}' stopped.");
            }
            catch (Exception exception)
            {
                log.Error(
                    $"Runtime extension '{active.Descriptor.Id}' failed " +
                    $"during shutdown: {exception}");
            }
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
            Assembly assembly = ContractLoadContext.LoadFromAssemblyPath(
                descriptor.AssemblyPath);
            Type entryType = assembly.GetType(
                descriptor.EntryType,
                throwOnError: true,
                ignoreCase: false)!;
            object? created = Activator.CreateInstance(
                entryType,
                nonPublic: true);
            if (created is not IScript4RuntimeExtension instance)
            {
                Type expectedContract =
                    typeof(IScript4RuntimeExtension);
                Type? declaredContract = entryType
                    .GetInterfaces()
                    .FirstOrDefault(value =>
                        value.FullName == expectedContract.FullName);
                string declaredDescription = declaredContract is null
                    ? "<not declared>"
                    : DescribeContract(declaredContract);

                throw new InvalidOperationException(
                    $"Entry type '{descriptor.EntryType}' does not implement " +
                    $"{nameof(IScript4RuntimeExtension)} from the active " +
                    $"contract assembly. Expected: " +
                    $"{DescribeContract(expectedContract)}. Declared: " +
                    $"{declaredDescription}.");
            }

            Complete(instance.InitializeAsync(
                _context,
                CancellationToken.None));
            active = new(descriptor, instance);
            log.Information(
                $"Runtime extension '{descriptor.Id}' initialized.");
            return true;
        }
        catch (Exception exception)
        {
            log.Error(
                $"Runtime extension '{descriptor.Id}' could not be initialized: " +
                exception);
            throw new InvalidOperationException(
                $"Required root runtime extension '{descriptor.Id}' failed " +
                "to initialize.",
                exception);
        }
    }

    private static string DescribeContract(Type contract)
    {
        Assembly assembly = contract.Assembly;
        AssemblyLoadContext? context =
            AssemblyLoadContext.GetLoadContext(assembly);
        string contextName = context?.Name ?? "<unnamed>";
        Guid moduleVersionId =
            assembly.ManifestModule.ModuleVersionId;

        return $"{assembly.FullName}; ALC='{contextName}'; " +
            $"MVID={moduleVersionId}";
    }

    private static void Complete(ValueTask operation)
    {
        if (operation.IsCompletedSuccessfully)
        {
            operation.GetAwaiter().GetResult();
            return;
        }
        operation.AsTask().GetAwaiter().GetResult();
    }

    private sealed record ActiveRuntimeExtension(
        RuntimeExtensionDescriptor Descriptor,
        IScript4RuntimeExtension Instance);
}

internal sealed class RootAssemblyResolver : IDisposable
{
    private readonly AssemblyLoadContext _context;
    private readonly Assembly _contractAssembly;
    private readonly string _contractAssemblyName;
    private readonly RuntimeLog _log;
    private readonly Dictionary<string, string> _paths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private bool _disposed;

    public RootAssemblyResolver(
        string rootDirectory,
        AssemblyLoadContext context,
        Assembly contractAssembly,
        RuntimeLog log)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(contractAssembly);
        _context = context;
        _contractAssembly = contractAssembly;
        _contractAssemblyName =
            contractAssembly.GetName().Name
            ?? throw new InvalidOperationException(
                "The runtime-extension contract assembly has no simple name.");
        _log = log;
        foreach (string path in Directory.EnumerateFiles(
                     rootDirectory,
                     "*.dll",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                AssemblyName identity = AssemblyName.GetAssemblyName(path);
                if (string.IsNullOrWhiteSpace(identity.Name))
                {
                    continue;
                }

                if (identity.Name.Equals(
                        _contractAssemblyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(path);
                if (!_paths.TryAdd(identity.Name, fullPath) &&
                    !Path.GetFullPath(_paths[identity.Name]).Equals(
                        fullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"More than one root assembly has the simple name " +
                        $"'{identity.Name}'.");
                }
            }
            catch (BadImageFormatException)
            {
            }
            catch (FileLoadException exception)
            {
                _log.Warning(
                    $"Root assembly '{Path.GetFileName(path)}' could not be " +
                    $"indexed: {exception.Message}");
            }
        }

        _context.Resolving += Resolve;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _context.Resolving -= Resolve;
    }

    private Assembly? Resolve(
        AssemblyLoadContext context,
        AssemblyName requested)
    {
        if (string.IsNullOrWhiteSpace(requested.Name))
        {
            return null;
        }

        if (requested.Name.Equals(
                _contractAssemblyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return _contractAssembly;
        }

        if (!_paths.TryGetValue(requested.Name, out string? path))
        {
            return null;
        }

        lock (_gate)
        {
            Assembly? loaded = context.Assemblies.FirstOrDefault(assembly =>
                assembly.GetName().Name?.Equals(
                    requested.Name,
                    StringComparison.OrdinalIgnoreCase) == true);
            if (loaded is not null)
            {
                return loaded;
            }

            try
            {
                return context.LoadFromAssemblyPath(path);
            }
            catch (Exception exception)
            {
                _log.Error(
                    $"Root dependency '{requested}' could not be loaded from " +
                    $"'{path}': {exception.Message}");
                return null;
            }
        }
    }
}