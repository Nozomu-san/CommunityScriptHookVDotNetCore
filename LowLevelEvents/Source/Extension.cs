using System.Reflection;
using CommunityScriptHookVDotNetCore.Source;
using LocalNativeMemories.Source;

[assembly: AssemblyMetadata("CSHVDNC.Role", "RuntimeExtension")]
[assembly: AssemblyMetadata("CSHVDNC.Id", "LowLevelEvents")]
[assembly: AssemblyMetadata(
    "CSHVDNC.EntryType",
    "LowLevelEvents.Source.LowLevelEventsExtension")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Provides",
    "game.events.raw;game.events.damage;game.events.weapon;game.events.combat")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Requires",
    "memory.entity.identity")]

namespace LowLevelEvents.Source;

internal sealed class LowLevelEventsExtension : IScript4RuntimeExtension
{
    private EventStream? _stream;

    public async Task InitializeAsync(
        RuntimeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (_stream is not null)
        {
            throw new InvalidOperationException("LowLevelEvents is already initialized.");
        }

        ILocalEntityIdentity identity =
            context.Services.GetRequired<ILocalEntityIdentity>();
        NativeBridge bridge = await NativeBridge.OpenAsync(
            context.RootDirectory,
            cancellationToken).ConfigureAwait(false);
        EventStream? stream = null;
        try
        {
            stream = new EventStream(bridge, identity);
            stream.ConfigureDetailedEventIds(EventCatalog.GetDetailedEventIds());
            context.Services.Register<ILowLevelEventStream>(stream);
            context.Services.Register<ILowLevelDamageStream>(stream);
            context.Services.Register<ILowLevelWeaponEventStream>(stream);
            context.Services.Register<ILowLevelCombatEventStream>(stream);
            _stream = stream;
        }
        catch
        {
            stream?.Dispose();
            if (stream is null)
            {
                bridge.Dispose();
            }
            throw;
        }
    }

    public void AdvanceHostFrame(RuntimeExtensionFrameContext context)
    {
        _stream?.Advance(context.HostFrameIndex);
    }

    public Task ShutdownAsync()
    {
        EventStream? stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
        return Task.CompletedTask;
    }
}