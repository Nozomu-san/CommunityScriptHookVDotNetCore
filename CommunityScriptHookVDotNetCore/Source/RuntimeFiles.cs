using System.Text;

namespace CommunityScriptHookVDotNetCore.Source;

internal sealed class RuntimeFiles : IDisposable
{
    private RuntimeFiles(
        string rootDirectory,
        string scriptsDirectory,
        RuntimeLog log)
    {
        RootDirectory = rootDirectory;
        ScriptsDirectory = scriptsDirectory;
        Log = log;
    }

    public string RootDirectory { get; }
    public string ScriptsDirectory { get; }
    public RuntimeLog Log { get; }

    public static RuntimeFiles Open()
    {
        string root = Path.GetDirectoryName(typeof(Brain).Assembly.Location)
            ?? AppContext.BaseDirectory;
        RuntimeLog log = RuntimeLog.Open(
            Path.Combine(root, "CommunityScriptHookVDotNetCore.log"));
        log.Information(
            "CommunityScriptHookVDotNetCore initialized. Host: CoreCLRHostLoader.");

        try
        {
            string scripts = Path.Combine(root, "scripts4");
            Directory.CreateDirectory(scripts);
            return new(root, scripts, log);
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    public void Dispose() => Log.Dispose();
}

internal sealed class RuntimeLog : IDisposable
{
    private static readonly Lock EmergencyGate = new();
    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;

    private RuntimeLog(StreamWriter writer) => _writer = writer;

    public static RuntimeLog Open(string path)
    {
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

    public static void TryWriteEmergency(Exception exception)
    {
        try
        {
            lock (EmergencyGate)
            {
                string root = Path.GetDirectoryName(
                    typeof(Brain).Assembly.Location)
                    ?? AppContext.BaseDirectory;
                File.AppendAllText(
                    Path.Combine(root, "CommunityScriptHookVDotNetCore.log"),
                    $"[{DateTime.Now:HH:mm:ss:fff}] [Error] {exception}\r\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
        }
    }

    public void Information(string message) => Write("Information", message);
    public void Warning(string message) => Write("Warning", message);
    public void Error(string message) => Write("Error", message);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            _writer.WriteLine(
                $"[{DateTime.Now:HH:mm:ss:fff}] [{level}] {message}");
        }
    }
}