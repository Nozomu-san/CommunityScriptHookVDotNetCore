using CommunityScriptHookVDotNetCore.Source;
using System.Reflection;

[assembly: AssemblyMetadata("CSHVDNC.Role", "RuntimeExtension")]
[assembly: AssemblyMetadata("CSHVDNC.Id", "Alloc8orStandardNatives")]
[assembly: AssemblyMetadata(
    "CSHVDNC.EntryType",
    "Alloc8orStandardNatives.Source.NativeExtension")]
[assembly: AssemblyMetadata("CSHVDNC.ContractMajor", "1")]
[assembly: AssemblyMetadata("CSHVDNC.ContractMinor", "0")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Provides",
    "native.call.admission;native.standard;game.build")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Requires",
    "host.native.raw;host.native.admission")]

namespace Alloc8orStandardNatives.Source;

internal sealed class NativeExtension : IScript4RuntimeExtension
{
    private IDisposable? _admissionLease;
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
        INativeCallAdmissionControl admission =
            context.Services.GetRequired<INativeCallAdmissionControl>();
        GameBuildService gameBuild = GameBuildService.Detect();
        NativeCatalog catalog = await NativeCatalog.LoadAsync(
            cancellationToken).ConfigureAwait(false);
        CatalogNativeCallAdmissionPolicy admissionPolicy = new(
            catalog,
            gameBuild);
        NativeGateway gateway = new(transport, gameBuild, catalog);
        KnownNativeInvoker known = new(catalog, gateway);
        StandardNativeServices services = new(
            gameBuild,
            catalog,
            catalog);

        IDisposable? admissionLease = null;
        try
        {
            admissionLease = admission.Install(admissionPolicy);
            StandardNatives.Bind(catalog, gateway);
            context.Services.Register<IGameBuildService>(gameBuild);
            context.Services.Register<INativeCatalog>(catalog);
            context.Services.Register<INativeDatabaseInfo>(catalog);
            context.Services.RegisterRuntimeOnly<IKnownNativeInvoker>(known);
            context.Services.Register<IStandardNatives>(services);
            _admissionLease = admissionLease;
            admissionLease = null;
            _initialized = true;
        }
        catch
        {
            StandardNatives.Unbind();
            admissionLease?.Dispose();
            throw;
        }
    }

    public void AdvanceHostFrame(RuntimeExtensionFrameContext context)
    {
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            _admissionLease?.Dispose();
            _admissionLease = null;
            StandardNatives.Unbind();
            _initialized = false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}