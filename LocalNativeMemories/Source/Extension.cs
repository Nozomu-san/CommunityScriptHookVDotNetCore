using System.Diagnostics;
using System.Reflection;
using CommunityScriptHookVDotNetCore.Source;

[assembly: AssemblyMetadata("CSHVDNC.Role", "RuntimeExtension")]
[assembly: AssemblyMetadata("CSHVDNC.Id", "LocalNativeMemories")]
[assembly: AssemblyMetadata(
    "CSHVDNC.EntryType",
    "LocalNativeMemories.Source.LocalNativeMemoriesExtension")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Provides",
    "memory.local;memory.entity.pools;memory.entity.identity;memory.ped;memory.vehicle")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Requires",
    "host.game-thread.function;host.game-thread.function.guarded")]

namespace LocalNativeMemories.Source;

internal sealed class LocalNativeMemoriesExtension :
    IScript4RuntimeExtension
{
    private LocalMemoryService? _memory;
    private EntityPools? _pools;

    public Task InitializeAsync(
        RuntimeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_memory is not null)
        {
            throw new InvalidOperationException(
                "LocalNativeMemories is already initialized.");
        }

        LocalGameEdition edition = LocalProcess.ResolveEdition();
        GameMemoryProfile profile = GameMemoryProfile.Resolve(edition);
        IGameThreadFunctionTransport gameThread =
            context.Services.GetRequired<IGameThreadFunctionTransport>();
        LocalMemoryService memory = new();
        EntityAddressResolver addressResolver = new(memory, profile);
        PoolBackendResolver poolBackends = new(memory, profile);
        PoolVerification poolVerification = new(memory, profile);
        EntityPools pools = new(
            memory,
            poolBackends,
            poolVerification,
            gameThread);
        PedMemory peds = new(profile, memory, addressResolver);
        VehicleMemory vehicles = new(profile, memory, addressResolver);

        context.Services.RegisterRuntimeOnly<ILocalMemory>(memory);
        context.Services.RegisterRuntimeOnly<ILocalEntityPools>(pools);
        context.Services.RegisterRuntimeOnly<ILocalEntityIdentity>(pools);
        context.Services.RegisterRuntimeOnly<IPedLocalMemory>(peds);
        context.Services.RegisterRuntimeOnly<IVehicleLocalMemory>(vehicles);

        _memory = memory;
        _pools = pools;
        return Task.CompletedTask;
    }

    public void AdvanceHostFrame(RuntimeExtensionFrameContext context)
    {
        _pools?.AdvanceHostFrame();
    }

    public Task ShutdownAsync()
    {
        _pools = null;
        _memory = null;
        return Task.CompletedTask;
    }
}

internal static class LocalProcess
{
    internal static LocalGameEdition ResolveEdition()
    {
        using Process process = Process.GetCurrentProcess();
        string? path = process.MainModule?.FileName;
        string name = Path.GetFileName(path ?? string.Empty) ?? string.Empty;

        if (name.Equals("GTA5.exe", StringComparison.OrdinalIgnoreCase))
        {
            return LocalGameEdition.Legacy;
        }

        if (name.Equals(
                "GTA5_Enhanced.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return LocalGameEdition.Enhanced;
        }

        throw new PlatformNotSupportedException(
            $"Unsupported local process '{name}'.");
    }
}