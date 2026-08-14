using System.Diagnostics;
using System.Globalization;
using Alloc8orStandardNatives.Source;

namespace LocalNativeMemories.Source;

internal readonly record struct LocalMemoryProfile(
    GameProcessFamily ProcessFamily,
    int Build,
    int PedDamageFlagsOffset,
    string ScriptEntityPattern,
    int DisplacementOffset,
    int InstructionLength)
{
    private const int EnhancedFirstKnownBuild = 811;
    private const int PedDamageFlagsOffsetValue = 0x188;

    internal static bool TryCreate(
        GameBuildInfo game,
        out LocalMemoryProfile profile)
    {
        profile = game.Edition switch
        {
            GameEdition.Enhanced when game.Build >= EnhancedFirstKnownBuild =>
                new(
                    GameProcessFamily.Enhanced,
                    game.Build,
                    PedDamageFlagsOffsetValue,
                    "41 8B 4C 1C ? E8",
                    6,
                    10),
            GameEdition.Legacy when game.Build >= 0 =>
                new(
                    GameProcessFamily.Legacy,
                    game.Build,
                    PedDamageFlagsOffsetValue,
                    "85 ED 74 0F 8B CD E8 ? ? ? ? " +
                    "48 8B F8 48 85 C0 74 2E",
                    7,
                    11),
            _ => default
        };

        return profile.ProcessFamily != GameProcessFamily.Unknown;
    }
}

internal readonly record struct ResolvedEntityLocation(
    nint GetScriptEntity,
    int PedDamageFlagsOffset);

internal sealed class EntityLocator(GameBuildInfo game) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly LocalMemoryProfile? _profile =
        LocalMemoryProfile.TryCreate(game, out LocalMemoryProfile profile)
            ? profile
            : null;
    private ResolvedEntityLocation? _location;
    private string? _lastFailure;
    private bool _disposed;

    internal GameProcessFamily ProcessFamily =>
        _profile?.ProcessFamily ?? GameProcessFamily.Unknown;

    internal string? LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _lastFailure;
            }
        }
    }

    internal LocalDataStatus TryResolve(
        out ResolvedEntityLocation location)
    {
        lock (_gate)
        {
            location = default;

            if (_disposed)
            {
                _lastFailure = "The process profile is disposed.";
                return LocalDataStatus.DataUnavailable;
            }

            if (_profile is not { } profile)
            {
                _lastFailure =
                    "The current GTA V process/build has no approved local-memory profile.";
                return LocalDataStatus.UnsupportedProcess;
            }

            if (_location is { } cached)
            {
                location = cached;
                _lastFailure = null;
                return LocalDataStatus.Success;
            }

            nint match = ModulePattern.Find(profile.ScriptEntityPattern);
            if (match == 0)
            {
                _lastFailure =
                    $"The Script GUID entity resolver pattern was not found for " +
                    $"{profile.ProcessFamily} build {profile.Build}.";
                return LocalDataStatus.DataUnavailable;
            }

            nint getScriptEntity = ResolveRip(
                match,
                profile.DisplacementOffset,
                profile.InstructionLength);

            if (getScriptEntity == 0 ||
                !ModulePattern.Contains(getScriptEntity) ||
                !MemoryAccess.IsExecutableRange(getScriptEntity, 1))
            {
                _lastFailure =
                    "The Script GUID entity resolver failed validation.";
                return LocalDataStatus.AccessRejected;
            }

            location = new(
                getScriptEntity,
                profile.PedDamageFlagsOffset);
            _location = location;
            _lastFailure = null;
            return LocalDataStatus.Success;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _location = null;
            _lastFailure = null;
            GC.SuppressFinalize(this);
        }
    }

    private static nint ResolveRip(
        nint instruction,
        int displacementOffset,
        int instructionLength)
    {
        if (!MemoryAccess.TryAdd(
                instruction,
                displacementOffset,
                out nint displacementAddress) ||
            !MemoryAccess.TryReadInt32(
                displacementAddress,
                out int displacement) ||
            !MemoryAccess.TryAdd(
                instruction,
                instructionLength,
                out nint nextInstruction) ||
            !MemoryAccess.TryAdd(
                nextInstruction,
                displacement,
                out nint result))
        {
            return 0;
        }

        return result;
    }
}

internal static class ModulePattern
{
    private static readonly Lock Gate = new();

    private static ModuleImage? s_image;
    private static bool s_captureAttempted;

    internal static nint Find(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return 0;
        }

        Pattern pattern;

        try
        {
            pattern = Pattern.Parse(expression);
        }
        catch (Exception exception) when (
            exception is FormatException or
            OverflowException)
        {
            return 0;
        }

        ModuleImage? image = GetImage();
        if (image is null)
        {
            return 0;
        }

        foreach (MemoryAccess.MemorySegment segment in image.Segments)
        {
            int lastOffset =
                segment.Bytes.Length -
                pattern.Bytes.Length;

            for (int offset = 0;
                offset <= lastOffset;
                ++offset)
            {
                bool matched = true;

                for (int index = 0;
                    index < pattern.Bytes.Length;
                    ++index)
                {
                    byte? expected = pattern.Bytes[index];

                    if (expected.HasValue &&
                        segment.Bytes[offset + index] != expected.Value)
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched &&
                    MemoryAccess.TryAdd(
                        segment.BaseAddress,
                        offset,
                        out nint address))
                {
                    return address;
                }
            }
        }

        return 0;
    }

    internal static bool Contains(
        nint address,
        int size = 1)
    {
        ModuleImage? image = GetImage();
        if (image is null || address <= 0 || size <= 0)
        {
            return false;
        }

        try
        {
            ulong start = unchecked((ulong)(nuint)image.BaseAddress);
            ulong end = checked(start + (uint)image.Size);
            ulong value = unchecked((ulong)(nuint)address);
            ulong valueEnd = checked(value + (uint)size);

            return value >= start && valueEnd <= end;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static ModuleImage? GetImage()
    {
        lock (Gate)
        {
            if (s_captureAttempted)
            {
                return s_image;
            }

            s_captureAttempted = true;

            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                using Process process = Process.GetCurrentProcess();
                ProcessModule? module = process.MainModule;

                if (module is null ||
                    module.BaseAddress == 0 ||
                    module.ModuleMemorySize <= 0 ||
                    !MemoryAccess.TryCaptureExecutableSegments(
                        module.BaseAddress,
                        module.ModuleMemorySize,
                        out MemoryAccess.MemorySegment[] segments))
                {
                    return null;
                }

                s_image = new(
                    module.BaseAddress,
                    module.ModuleMemorySize,
                    segments);

                return s_image;
            }
            catch
            {
                return null;
            }
        }
    }

    private sealed record ModuleImage(
        nint BaseAddress,
        int Size,
        MemoryAccess.MemorySegment[] Segments);

    private readonly record struct Pattern(byte?[] Bytes)
    {
        internal static Pattern Parse(string expression)
        {
            string[] parts = expression.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

            if (parts.Length == 0)
            {
                throw new FormatException("The pattern is empty.");
            }

            byte?[] bytes = new byte?[parts.Length];

            for (int index = 0; index < parts.Length; ++index)
            {
                string part = parts[index];

                bytes[index] = part is "?" or "??"
                    ? null
                    : byte.Parse(
                        part,
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture);
            }

            return new(bytes);
        }
    }
}