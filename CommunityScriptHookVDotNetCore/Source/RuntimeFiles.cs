#pragma warning disable SYSLIB1054

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
        if (!Directory.Exists(configuration.ExtensionsDirectory))
        {
            Directory.CreateDirectory(configuration.ExtensionsDirectory);
        }
        if (!Directory.Exists(configuration.ScriptsDirectory))
        {
            Directory.CreateDirectory(configuration.ScriptsDirectory);
        }
        RuntimeDirectoryIdentity.EnsureDistinct(
            configuration.ExtensionsDirectory,
            configuration.ScriptsDirectory);

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

internal static class RuntimeDirectoryIdentity
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileIdInfo = 18;

    internal static void EnsureDistinct(string extensionsDirectory, string scriptsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsDirectory);

        if (Path.GetFullPath(extensionsDirectory).Equals(
                Path.GetFullPath(scriptsDirectory),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The extensions and scripts4 infrastructures cannot share a directory.");
        }

        if (!OperatingSystem.IsWindows())
        {
            string resolvedExtensions = new DirectoryInfo(extensionsDirectory)
                .ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(extensionsDirectory);
            string resolvedScripts = new DirectoryInfo(scriptsDirectory)
                .ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(scriptsDirectory);
            if (resolvedExtensions.Equals(resolvedScripts, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The extensions and scripts4 infrastructures resolve to one directory.");
            }
            return;
        }

        using SafeFileHandle extensions = OpenDirectory(extensionsDirectory);
        using SafeFileHandle scripts = OpenDirectory(scriptsDirectory);
        DirectoryFileIdentity extensionId = ReadIdentity(extensions, extensionsDirectory);
        DirectoryFileIdentity scriptsId = ReadIdentity(scripts, scriptsDirectory);
        if (extensionId.VolumeSerialNumber == scriptsId.VolumeSerialNumber &&
            extensionId.FileIdLow == scriptsId.FileIdLow &&
            extensionId.FileIdHigh == scriptsId.FileIdHigh)
        {
            throw new InvalidOperationException(
                "The extensions and scripts4 infrastructures resolve to one physical directory.");
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            0,
            FileMode.Open,
            FileFlagBackupSemantics,
            0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                $"The runtime directory identity cannot be opened for '{path}'.",
                new Win32Exception(error));
        }
        return handle;
    }

    private static DirectoryFileIdentity ReadIdentity(SafeFileHandle handle, string path)
    {
        if (GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out DirectoryFileIdentity identity,
                (uint)Marshal.SizeOf<DirectoryFileIdentity>()))
        {
            return identity;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error is 50 or 87 &&
            GetFileInformationByHandle(handle, out LegacyDirectoryFileInfo older))
        {
            return new()
            {
                VolumeSerialNumber = older.VolumeSerialNumber,
                FileIdLow = ((ulong)older.FileIndexHigh << 32) | older.FileIndexLow
            };
        }
        throw new IOException(
            $"The runtime directory identity cannot be verified for '{path}'.",
            new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DirectoryFileIdentity
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyDirectoryFileInfo
    {
        public uint Attributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        uint informationClass,
        out DirectoryFileIdentity information,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle",
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out LegacyDirectoryFileInfo information);
}

internal sealed class RuntimeLog : IDisposable, IRuntimeInformationSink
{
    private const int PendingDiagnosticCapacity = 256;
    public long RuntimeStartedAtTimestamp { get; } = Stopwatch.GetTimestamp();
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

#pragma warning restore SYSLIB1054