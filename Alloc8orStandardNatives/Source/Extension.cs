using CommunityScriptHookVDotNetCore.Source;
using System.Reflection;

[assembly: AssemblyMetadata("SHVDN4.Role", "RuntimeExtension")]
[assembly: AssemblyMetadata("SHVDN4.Id", "Alloc8orStandardNatives")]
[assembly: AssemblyMetadata(
    "SHVDN4.EntryType",
    "Alloc8orStandardNatives.Source.NativeExtension")]
[assembly: AssemblyMetadata("SHVDN4.ContractMajor", "1")]
[assembly: AssemblyMetadata("SHVDN4.ContractMinor", "0")]
[assembly: AssemblyMetadata(
    "SHVDN4.Provides",
    "native.standard;game.build")]
[assembly: AssemblyMetadata("SHVDN4.Requires", "host.native.raw")]

namespace Alloc8orStandardNatives.Source;

internal sealed class NativeExtension : IScript4RuntimeExtension
{
    private bool _initialized;

    public async ValueTask InitializeAsync(
        RuntimeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_initialized)
        {
            throw new InvalidOperationException(
                "Alloc8orStandardNatives is already initialized.");
        }

        IRawNativeTransport transport =
            context.Services.GetRequired<IRawNativeTransport>();
        GameBuildService gameBuild = GameBuildService.Detect();
        NativeCatalog catalog = await NativeCatalog.LoadAsync(
            cancellationToken).ConfigureAwait(false);
        NativeGateway gateway = new(transport, gameBuild, catalog);
        KnownNativeInvoker known = new(catalog, gateway);
        StandardNativeServices services = new(
            gameBuild,
            catalog,
            catalog,
            known);

        StandardNatives.Bind(catalog, gateway);
        try
        {
            context.Services.Register<IGameBuildService>(gameBuild);
            context.Services.Register<INativeCatalog>(catalog);
            context.Services.Register<INativeDatabaseInfo>(catalog);
            context.Services.Register<IKnownNativeInvoker>(known);
            context.Services.Register<IStandardNatives>(services);
            _initialized = true;
        }
        catch
        {
            StandardNatives.Unbind();
            throw;
        }
    }

    public void AdvanceHostFrame(RuntimeExtensionFrameContext context)
    {
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized)
        {
            StandardNatives.Unbind();
            _initialized = false;
        }

        return ValueTask.CompletedTask;
    }
}