using System.Diagnostics;
using System.Globalization;

namespace LocalNativeMemories.Source;

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

            candidate:
            for (int offset = 0;
                offset <= lastOffset;
                ++offset)
            {
                for (int index = 0;
                    index < pattern.Bytes.Length;
                    ++index)
                {
                    byte? expected = pattern.Bytes[index];

                    if (expected.HasValue &&
                        segment.Bytes[offset + index] != expected.Value)
                    {
                        continue candidate;
                    }
                }

                if (MemoryAccess.TryAdd(
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
