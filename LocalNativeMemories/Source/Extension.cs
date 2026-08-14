using System.Reflection;
using Alloc8orStandardNatives.Source;
using CommunityScriptHookVDotNetCore.Source;

[assembly: AssemblyMetadata("CSHVDNC.Role", "RuntimeExtension")]
[assembly: AssemblyMetadata("CSHVDNC.Id", "LocalNativeMemories")]
[assembly: AssemblyMetadata(
    "CSHVDNC.EntryType",
    "LocalNativeMemories.Source.LocalNativeMemoriesExtension")]
[assembly: AssemblyMetadata("CSHVDNC.ContractMajor", "1")]
[assembly: AssemblyMetadata("CSHVDNC.ContractMinor", "0")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Provides",
    "memory.ped.invincibility")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Requires",
    "native.standard")]

namespace LocalNativeMemories.Source;

internal sealed class LocalNativeMemoriesExtension :
    IScript4RuntimeExtension
{
    private TargetedEntityMemory? _memory;

    public ValueTask InitializeAsync(
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

        IStandardNatives natives =
            context.Services.GetRequired<IStandardNatives>();
        TargetedEntityMemory memory = new(natives.GameBuild.Current);
        PedInvincibilityMemoryService invincibility = new(memory);

        context.Services.Register<IPedInvincibilityMemory>(invincibility);
        _memory = memory;
        return ValueTask.CompletedTask;
    }

    public void AdvanceHostFrame(RuntimeExtensionFrameContext context)
    {
        _ = context;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _memory?.Dispose();
        _memory = null;
        return ValueTask.CompletedTask;
    }
}