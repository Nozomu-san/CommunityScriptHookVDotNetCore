using CommunityScriptHookVDotNetCore.Source;

namespace LocalUserDebug.Source;

internal sealed class DebugNotificationRuntime : IDisposable
{
    private readonly IRuntimeDiagnosticReader _diagnostics;
    private readonly INotificationOperations _notifications;
    private RuntimeDiagnosticMessage? _pendingMessage;
    private readonly IRuntimeDiagnosticEvents? _events;
    private int _signalRevision = 1;
    private int _seenRevision;
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
        _events = diagnostics as IRuntimeDiagnosticEvents;
        _events?.Available += OnAvailable;
    }

    private void OnAvailable() => Interlocked.Increment(ref _signalRevision);

    internal void Advance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_ready)
        {
            _ready = true;
            return;
        }

        if (_pendingMessage is null)
        {
            if (_events is not null &&
                Volatile.Read(ref _signalRevision) == _seenRevision)
            {
                return;
            }
            int revision = Volatile.Read(ref _signalRevision);
            while (_pendingMessage is null &&
                   _diagnostics.TryRead(out RuntimeDiagnosticMessage? candidate))
            {
                if (candidate.DisplayColor is not null ||
                    candidate.Severity is RuntimeDiagnosticSeverity.Warning or
                        RuntimeDiagnosticSeverity.Error)
                {
                    _pendingMessage = candidate;
                }
            }
            if (_pendingMessage is null)
            {
                _seenRevision = revision;
                return;
            }
        }

        RuntimeDiagnosticMessage message = _pendingMessage;
        GameNotificationColor color = message.DisplayColor switch
        {
            RuntimeUserNotificationColor.Green => GameNotificationColor.Green,
            RuntimeUserNotificationColor.Blue => GameNotificationColor.Blue,
            RuntimeUserNotificationColor.Purple => GameNotificationColor.Purple,
            RuntimeUserNotificationColor.Yellow => GameNotificationColor.Yellow,
            RuntimeUserNotificationColor.Red => GameNotificationColor.Red,
            RuntimeUserNotificationColor.Orange => GameNotificationColor.Orange,
            RuntimeUserNotificationColor.White => GameNotificationColor.White,
            _ => message.Severity is RuntimeDiagnosticSeverity.Error
                ? GameNotificationColor.Red
                : GameNotificationColor.Yellow
        };

        int handle = _notifications.Post(
            DebugMessageFormatter.Format(message),
            color,
            blink: message.Severity is RuntimeDiagnosticSeverity.Error,
            showInBrief: true,
            forced: false);

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
        _events?.Available -= OnAvailable;
        _pendingMessage = null;
    }
}