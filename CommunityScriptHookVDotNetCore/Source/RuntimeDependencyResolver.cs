using System.Reflection;
using System.Runtime.Loader;

namespace CommunityScriptHookVDotNetCore.Source;

internal static class RuntimeDependencyResolver
{
    private const string FSharpCoreAssemblyName = "FSharp.Core";
    private static readonly Lock Gate = new();
    private static Assembly? _fsharpCore;

    internal static Assembly? TryResolve(AssemblyName requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (!string.Equals(
                requested.Name,
                FSharpCoreAssemblyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        lock (Gate)
        {
            if (_fsharpCore is not null &&
                Matches(requested, _fsharpCore.GetName()))
            {
                return _fsharpCore;
            }

            Assembly? loaded = FindLoaded(requested);
            if (loaded is not null)
            {
                _fsharpCore = loaded;
                return loaded;
            }

            foreach (string path in CandidatePaths)
            {
                AssemblyName candidate;
                try
                {
                    candidate = AssemblyName.GetAssemblyName(path);
                }
                catch (Exception exception) when (
                    exception is IOException or
                        UnauthorizedAccessException or
                        BadImageFormatException or
                        FileLoadException)
                {
                    continue;
                }

                if (!Matches(requested, candidate))
                {
                    continue;
                }

                try
                {
                    loaded = AssemblyLoadContext.Default.LoadFromAssemblyPath(
                        Path.GetFullPath(path));
                }
                catch (FileLoadException)
                {
                    loaded = FindLoaded(requested);
                }
                catch (Exception exception) when (
                    exception is IOException or
                        UnauthorizedAccessException or
                        BadImageFormatException)
                {
                    continue;
                }

                if (loaded is not null &&
                    Matches(requested, loaded.GetName()))
                {
                    _fsharpCore = loaded;
                    return loaded;
                }
            }

            return null;
        }
    }

    private static Assembly? FindLoaded(AssemblyName requested) =>
        AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
            Matches(requested, assembly.GetName()));

    private static IReadOnlyList<string> CandidatePaths
    {
        get
        {
            List<string> result = [];
            string runtimeRoot = Path.GetDirectoryName(typeof(Brain).Assembly.Location)
                ?? AppContext.BaseDirectory;
            string local = Path.Combine(runtimeRoot, FSharpCoreAssemblyName + ".dll");
            if (File.Exists(local))
            {
                result.Add(Path.GetFullPath(local));
            }

            if (!TryGetActiveRuntime(out InstalledVersion runtime, out string dotnetRoot))
            {
                return result;
            }

            string sdkRoot = Path.Combine(dotnetRoot, "sdk");
            if (!Directory.Exists(sdkRoot))
            {
                return result;
            }

            List<(InstalledVersion Version, string Path)> candidates = [];
            try
            {
                foreach (string directory in Directory.EnumerateDirectories(sdkRoot))
                {
                    string name = Path.GetFileName(directory);
                    if (!TryParseVersion(name, out InstalledVersion version) ||
                        version.Numeric.Major != runtime.Numeric.Major ||
                        version.Numeric.Minor != runtime.Numeric.Minor ||
                        (version.Prerelease is null) != (runtime.Prerelease is null))
                    {
                        continue;
                    }

                    string path = Path.Combine(
                        directory,
                        "FSharp",
                        FSharpCoreAssemblyName + ".dll");
                    if (File.Exists(path))
                    {
                        candidates.Add((version, path));
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return result;
            }

            candidates.Sort((left, right) =>
            {
                bool leftExact = string.Equals(
                    left.Version.Prerelease,
                    runtime.Prerelease,
                    StringComparison.OrdinalIgnoreCase);
                bool rightExact = string.Equals(
                    right.Version.Prerelease,
                    runtime.Prerelease,
                    StringComparison.OrdinalIgnoreCase);
                if (leftExact != rightExact)
                {
                    return leftExact ? -1 : 1;
                }
                return -CompareVersion(left.Version, right.Version);
            });

            result.AddRange(candidates.Select(value => Path.GetFullPath(value.Path)));
            return result;
        }
    }

    private static bool TryGetActiveRuntime(
        out InstalledVersion runtime,
        out string dotnetRoot)
    {
        runtime = default;
        dotnetRoot = string.Empty;
        string coreLibrary = typeof(object).Assembly.Location;
        if (string.IsNullOrWhiteSpace(coreLibrary))
        {
            return false;
        }

        DirectoryInfo? versionDirectory =
            new(Path.GetDirectoryName(coreLibrary)!);
        if (!TryParseVersion(versionDirectory.Name, out runtime))
        {
            return false;
        }

        DirectoryInfo? root = versionDirectory.Parent?.Parent?.Parent;
        if (root is null)
        {
            return false;
        }

        dotnetRoot = root.FullName;
        return Directory.Exists(dotnetRoot);
    }

    private static bool Matches(AssemblyName requested, AssemblyName candidate)
    {
        if (!string.Equals(
                requested.Name,
                candidate.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (requested.Version is not null && requested.Version != candidate.Version)
        {
            return false;
        }
        if (!string.IsNullOrEmpty(requested.CultureName) &&
            !requested.CultureName.Equals(
                candidate.CultureName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[]? requestedToken = requested.GetPublicKeyToken();
        byte[]? candidateToken = candidate.GetPublicKeyToken();
        return requestedToken is null ||
            requestedToken.Length == 0 ||
            candidateToken is not null && requestedToken.SequenceEqual(candidateToken);
    }

    private static bool TryParseVersion(
        string text,
        out InstalledVersion version)
    {
        version = default;
        int separator = text.IndexOf('-');
        string numericText = separator < 0 ? text : text[..separator];
        string? prerelease = separator < 0 ? null : text[(separator + 1)..];
        if (!Version.TryParse(numericText, out Version? numeric) ||
            numeric.Build < 0 ||
            prerelease is "")
        {
            return false;
        }

        version = new(numeric, prerelease);
        return true;
    }

    private static int CompareVersion(
        InstalledVersion left,
        InstalledVersion right)
    {
        int numeric = left.Numeric.CompareTo(right.Numeric);
        if (numeric != 0)
        {
            return numeric;
        }
        if (left.Prerelease is null || right.Prerelease is null)
        {
            return left.Prerelease is null
                ? right.Prerelease is null ? 0 : 1
                : -1;
        }

        string[] leftParts = left.Prerelease.Split('.');
        string[] rightParts = right.Prerelease.Split('.');
        int count = Math.Min(leftParts.Length, rightParts.Length);
        for (int index = 0; index < count; ++index)
        {
            int compared = CompareIdentifier(leftParts[index], rightParts[index]);
            if (compared != 0)
            {
                return compared;
            }
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        bool leftNumeric = left.All(char.IsDigit);
        bool rightNumeric = right.All(char.IsDigit);
        if (leftNumeric && rightNumeric)
        {
            string leftNormalized = left.TrimStart('0');
            string rightNormalized = right.TrimStart('0');
            leftNormalized = leftNormalized.Length == 0 ? "0" : leftNormalized;
            rightNormalized = rightNormalized.Length == 0 ? "0" : rightNormalized;
            int length = leftNormalized.Length.CompareTo(rightNormalized.Length);
            return length != 0
                ? length
                : string.CompareOrdinal(leftNormalized, rightNormalized);
        }
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }
        return string.CompareOrdinal(left, right);
    }

    private readonly record struct InstalledVersion(
        Version Numeric,
        string? Prerelease);
}