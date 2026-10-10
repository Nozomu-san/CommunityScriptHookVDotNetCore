using System.Text;

namespace CommunityScriptHookVDotNetCore.Source;

internal sealed class RuntimeFiles : IDisposable
{
    private RuntimeFiles(
        string rootDirectory,
        string extensionsDirectory,
        string scriptsDirectory,
        RuntimeLog log)
    {
        RootDirectory = rootDirectory;
        ExtensionsDirectory = extensionsDirectory;
        ScriptsDirectory = scriptsDirectory;
        Log = log;
    }

    public string RootDirectory { get; }
    public string ExtensionsDirectory { get; }
    public string ScriptsDirectory { get; }
    public RuntimeLog Log { get; }

    public static RuntimeFiles Open(IRuntimeDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        string root = Path.GetDirectoryName(typeof(Brain).Assembly.Location)
            ?? AppContext.BaseDirectory;
        RuntimeConfiguration configuration = RuntimeConfiguration.Load(root);
        Directory.CreateDirectory(configuration.ExtensionsDirectory);
        Directory.CreateDirectory(configuration.ScriptsDirectory);

        RuntimeLog.ConfigureEmergency(root, configuration.LogEnabled);
        RuntimeLog log = RuntimeLog.Open(
            Path.Combine(root, "CommunityScriptHookVDotNetCore.log"),
            configuration.LogEnabled);
        log.AttachDiagnostics(diagnostics);
        log.Information(
            "CommunityScriptHookVDotNetCore initialized. Host: CoreCLRHostLoader.");
        if (!string.IsNullOrWhiteSpace(configuration.Diagnostic))
        {
            log.Warning(configuration.Diagnostic);
        }
        log.Information(
            $"Runtime directories: extensions='{configuration.ExtensionsDirectory}', " +
            $"scripts4='{configuration.ScriptsDirectory}'.");

        return new(
            root,
            configuration.ExtensionsDirectory,
            configuration.ScriptsDirectory,
            log);
    }

    public void Dispose() => Log.Dispose();
}

internal sealed class RuntimeLog : IDisposable
{
    private const int PendingDiagnosticCapacity = 256;
    private static readonly Lock EmergencyGate = new();
    private static string? s_emergencyPath;
    private static bool s_emergencyEnabled = true;
    private readonly Lock _gate = new();
    private readonly Queue<PendingDiagnostic> _pendingDiagnostics =
        [with(PendingDiagnosticCapacity)];
    private StreamWriter? _writer;
    private IRuntimeDiagnosticSink? _diagnostics;
    private bool _disposed;

    private RuntimeLog(StreamWriter? writer) => _writer = writer;

    public static RuntimeLog Open(string path, bool enabled)
    {
        if (!enabled)
        {
            return new(null);
        }

        FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        StreamWriter writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        return new(writer);
    }

    public static void ConfigureEmergency(string rootDirectory, bool enabled)
    {
        lock (EmergencyGate)
        {
            s_emergencyEnabled = enabled;
            s_emergencyPath = enabled
                ? Path.Combine(
                    Path.GetFullPath(rootDirectory),
                    "CommunityScriptHookVDotNetCore.log")
                : null;
        }
    }

    public static void TryWriteEmergency(Exception exception)
    {
        try
        {
            lock (EmergencyGate)
            {
                if (!s_emergencyEnabled || s_emergencyPath is null)
                {
                    return;
                }
                File.AppendAllText(
                    s_emergencyPath,
                    $"[{DateTime.Now:HH:mm:ss:fff}] [Error] {exception}\r\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
        }
    }

    public void AttachDiagnostics(IRuntimeDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        PendingDiagnostic[] pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _diagnostics = diagnostics;
            pending = [.. _pendingDiagnostics];
            _pendingDiagnostics.Clear();
        }

        foreach (PendingDiagnostic diagnostic in pending)
        {
            Publish(diagnostics, diagnostic);
        }
    }

    public void Information(string message) => TryWrite("Information", message);

    public void Warning(
        string message,
        string? source = null,
        string? origin = null) =>
        Emit(
            RuntimeDiagnosticSeverity.Warning,
            "Warning",
            message,
            source,
            origin);

    public void Error(
        string message,
        string? source = null,
        string? origin = null,
        RuntimeDiagnosticImpact impact = RuntimeDiagnosticImpact.None,
        string? summary = null) =>
        Emit(
            RuntimeDiagnosticSeverity.Error,
            "Error",
            message,
            source,
            origin,
            impact,
            summary);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _diagnostics = null;
            _writer?.Dispose();
        }
    }

    private void Emit(
        RuntimeDiagnosticSeverity severity,
        string level,
        string message,
        string? source,
        string? origin,
        RuntimeDiagnosticImpact impact = RuntimeDiagnosticImpact.None,
        string? summary = null)
    {
        PendingDiagnostic diagnostic = new(
            severity,
            source ?? "CommunityScriptHookVDotNetCore",
            message,
            origin,
            impact,
            summary);
        IRuntimeDiagnosticSink? sink;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            sink = _diagnostics;
            if (sink is null)
            {
                if (_pendingDiagnostics.Count == PendingDiagnosticCapacity)
                {
                    _pendingDiagnostics.Dequeue();
                }
                _pendingDiagnostics.Enqueue(diagnostic);
            }
        }

        if (sink is not null)
        {
            Publish(sink, diagnostic);
        }
        TryWrite(level, message);
    }

    private static void Publish(
        IRuntimeDiagnosticSink diagnostics,
        PendingDiagnostic diagnostic)
    {
        if (diagnostic.Severity is RuntimeDiagnosticSeverity.Error)
        {
            diagnostics.Error(
                diagnostic.Source,
                diagnostic.Message,
                diagnostic.Origin,
                diagnostic.Impact,
                diagnostic.Summary);
        }
        else
        {
            diagnostics.Warning(
                diagnostic.Source,
                diagnostic.Message,
                diagnostic.Origin);
        }
    }

    private void TryWrite(string level, string message)
    {
        lock (_gate)
        {
            if (_disposed || _writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss:fff}] [{level}] {message}");
            }
            catch (Exception exception) when (
                exception is IOException or
                    ObjectDisposedException or
                    InvalidOperationException)
            {
                try
                {
                    _writer.Dispose();
                }
                catch
                {
                }
                _writer = null;
            }
        }
    }

    private readonly record struct PendingDiagnostic(
        RuntimeDiagnosticSeverity Severity,
        string Source,
        string Message,
        string? Origin,
        RuntimeDiagnosticImpact Impact,
        string? Summary);
}