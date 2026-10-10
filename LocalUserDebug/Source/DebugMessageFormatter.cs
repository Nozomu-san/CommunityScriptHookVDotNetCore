using System.Text;
using CommunityScriptHookVDotNetCore.Source;

namespace LocalUserDebug.Source;

internal static class DebugMessageFormatter
{
    private const int MaximumCharacters = 360;
    private const int MaximumSourceCharacters = 64;
    private const int MaximumOriginCharacters = 96;

    internal static string Format(RuntimeDiagnosticMessage diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return diagnostic.Impact is RuntimeDiagnosticImpact.LifecycleUnavailable
            ? FormatFatal(diagnostic)
            : FormatStandard(diagnostic);
    }

    private static string FormatFatal(RuntimeDiagnosticMessage diagnostic)
    {
        string source = Clamp(
            Normalize(diagnostic.Source),
            MaximumSourceCharacters);
        string origin = Clamp(
            Normalize(diagnostic.Origin),
            MaximumOriginCharacters);
        string summary = Normalize(diagnostic.Summary);
        if (summary.Length == 0)
        {
            summary = "A mod lifecycle became unavailable.";
        }

        string header = $"[Fatal] {source}";
        string originLine = origin.Length == 0
            ? string.Empty
            : $"\nOrigin: {origin}";
        int budget = Math.Max(
            0,
            MaximumCharacters - header.Length - originLine.Length - 1);
        return header + "\n" + Clamp(summary, budget) + originLine;
    }

    private static string FormatStandard(RuntimeDiagnosticMessage diagnostic)
    {
        string severity = diagnostic.Severity.ToString();
        string source = Clamp(
            Normalize(diagnostic.Source),
            MaximumSourceCharacters);
        string origin = Clamp(
            Normalize(diagnostic.Origin),
            MaximumOriginCharacters);
        string message = Normalize(diagnostic.Message);

        string header = $"[{severity}] {source}";
        string originLine = origin.Length == 0
            ? string.Empty
            : $"\nOrigin: {origin}";
        int budget = Math.Max(
            0,
            MaximumCharacters - header.Length - originLine.Length - 1);
        string body = Clamp(message, budget);
        return body.Length == 0
            ? header + originLine
            : header + "\n" + body + originLine;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new(value.Length);
        bool pendingSpace = false;
        foreach (char character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length != 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string Clamp(string value, int maximum)
    {
        if (maximum <= 0)
        {
            return string.Empty;
        }
        if (value.Length <= maximum)
        {
            return value;
        }
        if (maximum == 1)
        {
            return "…";
        }
        int length = maximum - 1;
        if (length < value.Length &&
            char.IsHighSurrogate(value[length - 1]) &&
            char.IsLowSurrogate(value[length]))
        {
            --length;
        }
        return value[..length] + "…";
    }
}