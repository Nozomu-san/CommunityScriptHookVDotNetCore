using System.Reflection;
using CommunityScriptHookVDotNetCore.Source;

[assembly: AssemblyMetadata("CSHVDNC.Role", "RuntimeExtension")]
[assembly: AssemblyMetadata("CSHVDNC.Id", "LocalUserDebug")]
[assembly: AssemblyMetadata(
    "CSHVDNC.EntryType",
    "LocalUserDebug.Source.LocalUserDebugExtension")]
[assembly: AssemblyMetadata("CSHVDNC.Provides", "user.debug;game.notifications")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Requires",
    "runtime.diagnostics;native.standard")]

namespace LocalUserDebug.Source;

internal sealed class LocalUserDebugExtension : IScript4RuntimeExtension
{
    private DebugNotificationRuntime? _runtime;

    public Task InitializeAsync(
        RuntimeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtime is not null)
        {
            throw new InvalidOperationException(
                "LocalUserDebug is already initialized.");
        }

        NotificationOperations notifications = new();
        context.Services.Register<INotificationOperations>(notifications);
        _runtime = new(context.Diagnostics, notifications);
        return Task.CompletedTask;
    }

    public void AdvanceHostFrame(RuntimeExtensionFrameContext context)
    {
        _runtime?.Advance();
    }

    public Task ShutdownAsync()
    {
        _runtime?.Dispose();
        _runtime = null;
        return Task.CompletedTask;
    }
}