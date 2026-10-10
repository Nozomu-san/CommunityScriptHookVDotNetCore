using CommunityScriptHookVDotNetCore.Source;
using StandardGameOperations.Source;

namespace LocalUserDebug.Source;

internal sealed class DebugNotificationRuntime : IDisposable
{
    private readonly INotificationOperations _notifications;
    private readonly IRuntimeDiagnosticReader _diagnostics;
    private RuntimeDiagnosticMessage? _pendingMessage;
    private bool _ready;
    private bool _disposed;

    internal DebugNotificationRuntime(
        IRuntimeDiagnosticReader diagnostics,
        INotificationOperations notifications)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(notifications);
        _diagnostics = diagnostics;
        _notifications = notifications;
    }

    internal void Advance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_ready)
        {
            _ready = true;
            return;
        }

        if (_pendingMessage is null &&
            !_diagnostics.TryRead(out _pendingMessage))
        {
            return;
        }

        RuntimeDiagnosticMessage message = _pendingMessage;
        GameNotificationColor color = message.Severity switch
        {
            RuntimeDiagnosticSeverity.Error => GameNotificationColor.Red,
            _ => GameNotificationColor.Yellow
        };

        int handle = _notifications.Post(
            DebugMessageFormatter.Format(message),
            color,
            blink: message.Severity is RuntimeDiagnosticSeverity.Error,
            showInBrief: true,
            forced: true);

        if (handle >= 0)
        {
            _pendingMessage = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pendingMessage = null;
    }
}