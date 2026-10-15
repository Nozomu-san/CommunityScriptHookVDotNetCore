using System.Diagnostics.CodeAnalysis;

namespace CommunityScriptHookVDotNetCore.Source;

public enum RuntimeDiagnosticSeverity
{
    Warning,
    Error
}

public enum RuntimeDiagnosticImpact
{
    None,
    LifecycleUnavailable
}

public sealed record RuntimeDiagnosticMessage(
    ulong Sequence,
    DateTimeOffset Timestamp,
    RuntimeDiagnosticPhase Phase,
    RuntimeDiagnosticSeverity Severity,
    string Source,
    string Message,
    string? Origin,
    RuntimeDiagnosticImpact Impact,
    string? Summary);

public interface IRuntimeDiagnosticReader
{
    bool TryRead([MaybeNullWhen(false)] out RuntimeDiagnosticMessage message);
}

public interface IRuntimeDiagnosticEvents
{
    event Action? Available;
}

public interface IRuntimeDiagnosticSink
{
    void Warning(string source, string message, string? origin = null);

    void Error(
        string source,
        string message,
        string? origin = null,
        RuntimeDiagnosticImpact impact = RuntimeDiagnosticImpact.None,
        string? summary = null);
}

public enum RuntimeDiagnosticPhase
{
    Brain,
    Extensions,
    Scripts4
}

internal sealed class RuntimeDiagnosticHub :
    IRuntimeDiagnosticSink,
    IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<RuntimeDiagnosticMessage> _journal = [];
    private readonly List<WeakReference<Reader>> _readers = [];
    private RuntimeDiagnosticPhase _phase = RuntimeDiagnosticPhase.Brain;
    private int _readableCount;
    private int _epochStartIndex;
    private ulong _sequence;
    private bool _epochActive = true;
    private bool _disposed;

    public void Warning(string source, string message, string? origin = null) =>
        Publish(
            RuntimeDiagnosticSeverity.Warning,
            source,
            message,
            origin,
            RuntimeDiagnosticImpact.None,
            null);

    public void Error(
        string source,
        string message,
        string? origin = null,
        RuntimeDiagnosticImpact impact = RuntimeDiagnosticImpact.None,
        string? summary = null) =>
        Publish(
            RuntimeDiagnosticSeverity.Error,
            source,
            message,
            origin,
            impact,
            summary);

    internal IRuntimeDiagnosticReader CreateReader()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int cursor = _epochActive
                ? _epochStartIndex
                : _journal.Count;
            Reader reader = new(this, cursor);
            _readers.Add(new(reader));
            return reader;
        }
    }

    internal void EnterPhase(RuntimeDiagnosticPhase phase)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_epochActive)
            {
                return;
            }
            if (phase < _phase)
            {
                throw new InvalidOperationException(
                    "Runtime diagnostic phases cannot move backward within an epoch.");
            }
            _phase = phase;
        }
    }

    internal void BeginReloadEpoch()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_epochActive)
            {
                throw new InvalidOperationException(
                    "A runtime diagnostic epoch is already active.");
            }

            _phase = RuntimeDiagnosticPhase.Brain;
            _epochStartIndex = _journal.Count;
            _epochActive = true;
        }
    }

    internal void SealEpoch()
    {
        lock (_gate)
        {
            if (_disposed || !_epochActive)
            {
                return;
            }

            _readableCount = _journal.Count;
            _epochActive = false;
        }
        NotifyReaders();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _journal.Clear();
            _readers.Clear();
            _readableCount = 0;
            _epochStartIndex = 0;
        }
    }

    private void Publish(
        RuntimeDiagnosticSeverity severity,
        string source,
        string message,
        string? origin,
        RuntimeDiagnosticImpact impact,
        string? summary)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _journal.Add(new(
                ++_sequence,
                DateTimeOffset.Now,
                _phase,
                severity,
                string.IsNullOrWhiteSpace(source)
                    ? "CommunityScriptHookVDotNetCore"
                    : source.Trim(),
                message.Trim(),
                string.IsNullOrWhiteSpace(origin) ? null : origin.Trim(),
                impact,
                string.IsNullOrWhiteSpace(summary) ? null : summary.Trim()));

            if (!_epochActive)
            {
                _readableCount = _journal.Count;
            }
        }
        if (!_epochActive)
        {
            NotifyReaders();
        }
    }

    private void NotifyReaders()
    {
        Reader[] active;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            List<Reader> recipients = [];
            for (int index = _readers.Count - 1; index >= 0; --index)
            {
                if (_readers[index].TryGetTarget(out Reader? reader))
                {
                    recipients.Add(reader);
                }
                else
                {
                    _readers.RemoveAt(index);
                }
            }
            active = [.. recipients];
        }
        foreach (Reader reader in active)
        {
            reader.NotifyAvailable();
        }
    }

    private bool TryRead(
        ref int cursor,
        [MaybeNullWhen(false)] out RuntimeDiagnosticMessage message)
    {
        lock (_gate)
        {
            if (_disposed || cursor >= _readableCount)
            {
                message = null;
                return false;
            }

            message = _journal[cursor++];
            return true;
        }
    }

    private sealed class Reader(
        RuntimeDiagnosticHub owner,
        int cursor) : IRuntimeDiagnosticReader, IRuntimeDiagnosticEvents
    {
        private int _cursor = cursor;

        public event Action? Available;

        internal void NotifyAvailable()
        {
            Delegate[] listeners = Available?.GetInvocationList() ?? [];
            foreach (Delegate listener in listeners)
            {
                try
                {
                    ((Action)listener)();
                }
                catch (Exception)
                {
                }
            }
        }

        public bool TryRead(
            [MaybeNullWhen(false)] out RuntimeDiagnosticMessage message) =>
            owner.TryRead(ref _cursor, out message);
    }
}