using System.Globalization;
using System.Text;

namespace CommunityScriptHookVDotNetCore.Source;

internal readonly record struct RuntimeConfiguration(
    ScriptTickMode TickMode,
    int LockedTickRate)
{
    public const int MinimumLockedTickRate = 64;
    public const int MaximumLockedTickRate = 1024;
    public const int DefaultLockedTickRate = 64;
}

internal sealed class RuntimeFiles : IDisposable
{
    private RuntimeFiles(
        string rootDirectory,
        string scriptsDirectory,
        RuntimeConfiguration configuration,
        RuntimeLog log)
    {
        RootDirectory = rootDirectory;
        ScriptsDirectory = scriptsDirectory;
        Configuration = configuration;
        Log = log;
    }

    public string RootDirectory { get; }
    public string ScriptsDirectory { get; }
    public RuntimeConfiguration Configuration { get; }
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
            RuntimeConfiguration configuration = LoadConfiguration(
                Path.Combine(root, "CommunityScriptHookVDotNetCore.ini"),
                log);
            return new(root, scripts, configuration, log);
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    public void Dispose() => Log.Dispose();

    private static RuntimeConfiguration LoadConfiguration(
        string path,
        RuntimeLog log)
    {
        string? modeText = null;
        string? rateText = null;
        bool fileExisted = File.Exists(path);
        bool repaired = !fileExisted;

        if (fileExisted)
        {
            try
            {
                string section = string.Empty;
                foreach (string originalLine in File.ReadLines(path))
                {
                    string line = originalLine.Trim();
                    if (line.Length == 0 ||
                        line.StartsWith(';') ||
                        line.StartsWith('#'))
                    {
                        continue;
                    }

                    if (line.StartsWith('[') && line.EndsWith(']'))
                    {
                        section = line[1..^1].Trim();
                        if (!section.Equals(
                                "Tick",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            repaired = true;
                        }
                        continue;
                    }

                    int separator = line.IndexOf('=');
                    if (separator <= 0 ||
                        !section.Equals(
                            "Tick",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        repaired = true;
                        continue;
                    }

                    string key = line[..separator].Trim();
                    string value = line[(separator + 1)..].Trim();
                    if (key.Equals(
                            "Mode",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (modeText is not null)
                        {
                            repaired = true;
                        }
                        modeText = value;
                    }
                    else if (key.Equals(
                                 "Rate",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        if (rateText is not null)
                        {
                            repaired = true;
                        }
                        rateText = value;
                    }
                    else
                    {
                        repaired = true;
                    }
                }
            }
            catch (Exception exception)
            {
                repaired = true;
                modeText = null;
                rateText = null;
                log.Warning(
                    "CommunityScriptHookVDotNetCore.ini could not be read and was reset: " +
                    exception.Message);
            }
        }

        ScriptTickMode mode;
        if (modeText?.Equals(
                "Locked",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            mode = ScriptTickMode.Locked;
        }
        else if (modeText?.Equals(
                     "Synchronized",
                     StringComparison.OrdinalIgnoreCase) == true)
        {
            mode = ScriptTickMode.Synchronized;
        }
        else
        {
            mode = ScriptTickMode.Synchronized;
            repaired = true;
            if (modeText is not null)
            {
                log.Warning(
                    "Tick Mode was invalid and was repaired to Synchronized.");
            }
        }

        int rate;
        if (!int.TryParse(
                rateText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            rate = RuntimeConfiguration.DefaultLockedTickRate;
            repaired = true;

            bool explicitInvalidValue = rateText is not null;
            bool missingActiveValue =
                fileExisted &&
                mode.UsesLockedRate &&
                rateText is null;
            if (explicitInvalidValue || missingActiveValue)
            {
                log.Warning(
                    "Tick Rate was missing or invalid and was repaired to 64.");
            }
        }
        else
        {
            int clamped = Math.Clamp(
                parsed,
                RuntimeConfiguration.MinimumLockedTickRate,
                RuntimeConfiguration.MaximumLockedTickRate);
            if (clamped != parsed)
            {
                repaired = true;
                log.Warning(
                    $"Tick Rate was constrained to {clamped}. " +
                    "The supported range is 64 through 1024.");
            }
            rate = clamped;
        }

        RuntimeConfiguration result = new(mode, rate);
        if (repaired)
        {
            WriteConfiguration(path, result);
        }
        return result;
    }

    private static void WriteConfiguration(
        string path,
        RuntimeConfiguration configuration)
    {
        const string NewLine = "\r\n";
        StringBuilder content = new();
        content.Append("; Synchronized or Locked. Take your pick.");
        content.Append(NewLine);
        content.Append("; Rate is only use on Locked mode.");
        content.Append(NewLine);
        content.Append("[Tick]");
        content.Append(NewLine);
        content.Append("Mode=");
        content.Append(configuration.TickMode);
        content.Append(NewLine);
        content.Append("Rate=");
        content.Append(
            configuration.LockedTickRate.ToString(
                CultureInfo.InvariantCulture));
        content.Append(NewLine);

        string temporary = path + ".tmp";
        File.WriteAllText(
            temporary,
            content.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }
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