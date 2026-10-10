using System.Collections.ObjectModel;
using System.Text;

namespace CommunityScriptHookVDotNetCore.Source;

public sealed record RuntimeIniEntry(
    string Key,
    string DefaultValue,
    Func<string, string?> Normalize)
{
    public RuntimeIniEntry(string key, string defaultValue)
        : this(key, defaultValue, static value => value.Trim())
    {
    }

    public RuntimeIniEntry(
        string key,
        string defaultValue,
        Func<string, string?> normalize,
        string comment)
        : this(key, defaultValue, normalize)
    {
        Comment = comment;
    }

    public string? Comment { get; init; }
}

public sealed class RuntimeIniResult
{
    internal RuntimeIniResult(
        IReadOnlyDictionary<string, string> values,
        bool normalized,
        string? diagnostic)
    {
        Values = values;
        Normalized = normalized;
        Diagnostic = diagnostic;
    }

    public IReadOnlyDictionary<string, string> Values { get; }
    public bool Normalized { get; }
    public string? Diagnostic { get; }

    public string Get(string key) =>
        Values.TryGetValue(key, out string? value)
            ? value
            : throw new KeyNotFoundException(key);

    public bool GetBoolean(string key) =>
        bool.Parse(Get(key));
}

public static class RuntimeIni
{
    private readonly record struct TextLine(string Content, string Terminator);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static RuntimeIniResult LoadOrCreate(
        string path,
        IReadOnlyList<RuntimeIniEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException(
                "At least one INI entry is required.",
                nameof(entries));
        }

        string fullPath = Path.GetFullPath(path);
        Dictionary<string, RuntimeIniEntry> schema = [with(
            entries.Count,
            StringComparer.OrdinalIgnoreCase)];
        foreach (RuntimeIniEntry entry in entries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);
            ArgumentNullException.ThrowIfNull(entry.Normalize);
            if (!schema.TryAdd(entry.Key.Trim(), entry))
            {
                throw new ArgumentException(
                    $"Duplicate INI schema key '{entry.Key}'.",
                    nameof(entries));
            }
        }

        if (!File.Exists(fullPath))
        {
            Dictionary<string, string> defaults = BuildDefaults(entries);
            try
            {
                WriteCanonical(fullPath, entries, defaults);
                return new(
                    new ReadOnlyDictionary<string, string>(defaults),
                    normalized: true,
                    $"{Path.GetFileName(fullPath)} was created with defaults.");
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    ArgumentException or
                    NotSupportedException)
            {
                return new(
                    new ReadOnlyDictionary<string, string>(defaults),
                    normalized: false,
                    $"{Path.GetFileName(fullPath)} could not be created. Session defaults are in use: {exception.Message}");
            }
        }

        try
        {
            byte[] source = File.ReadAllBytes(fullPath);
            string text = Utf8.GetString(source);
            if (text.Length != 0 && text[0] == '\uFEFF')
            {
                text = text[1..];
            }
            List<TextLine> lines = SplitLines(text);
            Dictionary<string, string> values = [with(
                entries.Count,
                StringComparer.OrdinalIgnoreCase)];
            HashSet<string> seen = [with(StringComparer.OrdinalIgnoreCase)];
            HashSet<string> repairKeys = [with(StringComparer.OrdinalIgnoreCase)];

            foreach (TextLine raw in lines)
            {
                if (!TryGetSchemaLine(raw.Content, schema, out RuntimeIniEntry entry, out string value))
                {
                    continue;
                }

                if (!seen.Add(entry.Key))
                {
                    repairKeys.Add(entry.Key);
                    values.Remove(entry.Key);
                    continue;
                }

                string? normalized = entry.Normalize(value);
                if (normalized is null)
                {
                    repairKeys.Add(entry.Key);
                    continue;
                }

                values[entry.Key] = normalized;
            }

            foreach (RuntimeIniEntry entry in entries)
            {
                if (repairKeys.Contains(entry.Key) ||
                    !values.ContainsKey(entry.Key))
                {
                    values[entry.Key] = NormalizeDefault(entry);
                    repairKeys.Add(entry.Key);
                }
            }

            bool repair = repairKeys.Count != 0;
            if (repair)
            {
                WriteAtomic(
                    fullPath,
                    Repair(lines, entries, schema, values, repairKeys));
            }

            return new(
                new ReadOnlyDictionary<string, string>(values),
                repair,
                repair
                    ? $"{Path.GetFileName(fullPath)} was normalized."
                    : null);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                DecoderFallbackException or
                ArgumentException or
                NotSupportedException)
        {
            Dictionary<string, string> defaults = BuildDefaults(entries);
            return new(
                new ReadOnlyDictionary<string, string>(defaults),
                normalized: false,
                $"{Path.GetFileName(fullPath)} could not be read. Session defaults are in use: {exception.Message}");
        }
    }

    public static string? NormalizeBoolean(string value) =>
        bool.TryParse(value.Trim(), out bool parsed)
            ? parsed ? "true" : "false"
            : null;

    private static Dictionary<string, string> BuildDefaults(
        IReadOnlyList<RuntimeIniEntry> entries)
    {
        Dictionary<string, string> result = [with(
            entries.Count,
            StringComparer.OrdinalIgnoreCase)];
        foreach (RuntimeIniEntry entry in entries)
        {
            result[entry.Key] = NormalizeDefault(entry);
        }
        return result;
    }

    private static string NormalizeDefault(RuntimeIniEntry entry) =>
        entry.Normalize(entry.DefaultValue)
        ?? throw new InvalidOperationException(
            $"INI default for '{entry.Key}' is invalid.");

    private static bool TryGetSchemaLine(
    string content,
    IReadOnlyDictionary<string, RuntimeIniEntry> schema,
    out RuntimeIniEntry entry,
    out string value)
    {
        string line = content.Trim();
        entry = null!;
        value = string.Empty;

        if (line.Length == 0 ||
            line.StartsWith(';') ||
            line.StartsWith('#') ||
            line.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        int separator = line.IndexOf('=');
        if (separator <= 0)
        {
            if (!schema.TryGetValue(
                    line,
                    out RuntimeIniEntry? candidate) ||
                candidate is null)
            {
                return false;
            }

            entry = candidate;
            return true;
        }

        string key = line[..separator].Trim();
        if (!schema.TryGetValue(
                key,
                out RuntimeIniEntry? candidateEntry) ||
            candidateEntry is null)
        {
            return false;
        }

        entry = candidateEntry;
        value = line[(separator + 1)..].Trim();
        return true;
    }

    private static List<TextLine> SplitLines(string text)
    {
        List<TextLine> result = [];
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                result.Add(new(text[start..], string.Empty));
                break;
            }

            int end = newline;
            string terminator = "\n";
            if (end > start && text[end - 1] == '\r')
            {
                --end;
                terminator = "\r\n";
            }

            result.Add(new(text[start..end], terminator));
            start = newline + 1;
        }
        return result;
    }

    private static byte[] Repair(
        IReadOnlyList<TextLine> lines,
        IReadOnlyList<RuntimeIniEntry> entries,
        IReadOnlyDictionary<string, RuntimeIniEntry> schema,
        Dictionary<string, string> values,
        HashSet<string> repairKeys)
    {
        StringBuilder builder = new();
        HashSet<string> emitted = [with(StringComparer.OrdinalIgnoreCase)];

        foreach (TextLine line in lines)
        {
            if (!TryGetSchemaLine(line.Content, schema, out RuntimeIniEntry entry, out _))
            {
                builder.Append(line.Content);
                builder.Append(line.Terminator);
                continue;
            }

            if (!emitted.Add(entry.Key))
            {
                continue;
            }

            if (repairKeys.Contains(entry.Key))
            {
                builder.Append(entry.Key);
                builder.Append('=');
                builder.Append(values[entry.Key]);
                builder.Append(line.Terminator);
            }
            else
            {
                builder.Append(line.Content);
                builder.Append(line.Terminator);
            }
        }

        foreach (RuntimeIniEntry entry in entries)
        {
            if (emitted.Contains(entry.Key))
            {
                continue;
            }

            if (builder.Length != 0 &&
                builder[^1] is not '\n' and not '\r')
            {
                builder.Append("\r\n");
            }

            builder.Append(entry.Key);
            builder.Append('=');
            builder.Append(values[entry.Key]);
            builder.Append("\r\n");
        }

        return Utf8.GetBytes(builder.ToString());
    }

    private static void WriteCanonical(
        string path,
        IReadOnlyList<RuntimeIniEntry> entries,
        IReadOnlyDictionary<string, string> values) =>
        WriteAtomic(path, Serialize(entries, values));

    private static byte[] Serialize(
        IReadOnlyList<RuntimeIniEntry> entries,
        IReadOnlyDictionary<string, string> values)
    {
        StringBuilder builder = new();
        foreach (RuntimeIniEntry entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Comment))
            {
                builder.Append("; ");
                builder.Append(entry.Comment.Trim());
                builder.Append("\r\n");
            }

            builder.Append(entry.Key);
            builder.Append('=');
            builder.Append(values[entry.Key]);
            builder.Append("\r\n");
        }
        return Utf8.GetBytes(builder.ToString());
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> content)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                "INI path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

internal sealed record RuntimeConfiguration(
    string ExtensionsDirectory,
    string ScriptsDirectory,
    bool LogEnabled,
    string? Diagnostic)
{
    private const string FileName = "CommunityScriptHookVDotNetCore.ini";

    public static RuntimeConfiguration Load(string rootDirectory)
    {
        string root = Path.GetFullPath(rootDirectory);
        RuntimeIniResult loaded = RuntimeIni.LoadOrCreate(
            Path.Combine(root, FileName),
            [
                new(
                    "ExtensionsDirectory",
                    "extensions",
                    value => NormalizeRelativeDirectory(root, value),
                    "If you wish to change directory(ies)."),
                new(
                    "Scripts4Directory",
                    "scripts4",
                    value => NormalizeRelativeDirectory(root, value)),
                new(
                    "LogEnabled",
                    "true",
                    RuntimeIni.NormalizeBoolean,
                    "If you wish to toggle your logs.")
            ]);

        string extensions = ResolveRelativeDirectory(
            root,
            loaded.Get("ExtensionsDirectory"));
        string scripts = ResolveRelativeDirectory(
            root,
            loaded.Get("Scripts4Directory"));
        if (extensions.Equals(scripts, StringComparison.OrdinalIgnoreCase))
        {
            extensions = Path.Combine(root, "extensions");
            scripts = Path.Combine(root, "scripts4");
            return new(
                extensions,
                scripts,
                true,
                $"{FileName} resolved both directories to the same path. Session defaults are in use.");
        }

        return new(
            extensions,
            scripts,
            loaded.GetBoolean("LogEnabled"),
            loaded.Diagnostic);
    }

    private static string? NormalizeRelativeDirectory(
        string root,
        string value)
    {
        string candidate = value.Trim().Trim('"');
        if (candidate.Length == 0 || Path.IsPathRooted(candidate))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(Path.Combine(root, candidate));
            string relative = Path.GetRelativePath(root, full);
            if (relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                return null;
            }
            return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return null;
        }
    }

    private static string ResolveRelativeDirectory(string root, string value) =>
        Path.GetFullPath(Path.Combine(root, value));
}