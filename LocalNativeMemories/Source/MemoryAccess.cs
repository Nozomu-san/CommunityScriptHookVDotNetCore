#pragma warning disable SYSLIB1054

using System.Runtime.InteropServices;

namespace LocalNativeMemories.Source;

internal static class MemoryAccess
{
    private const uint MemCommit = 0x1000;

    private const uint PageNoAccess = 0x01;
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecute = 0x10;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        internal nint BaseAddress;
        internal nint AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId;
        internal nuint RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
    }

    internal readonly record struct MemorySegment(
        nint BaseAddress,
        byte[] Bytes);

    private const string Kernel32 = "kernel32.dll";

    private static readonly nuint MemoryBasicInformationSize =
        checked((nuint)Marshal.SizeOf<MemoryBasicInformation>());
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Kernel32, ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(
        Kernel32,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        nint process,
        nint baseAddress,
        [Out] byte[] buffer,
        nuint size,
        out nuint bytesRead);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Kernel32, ExactSpelling = true)]
    private static extern nuint VirtualQuery(
        nint address,
        out MemoryBasicInformation information,
        nuint length);

    internal static bool TryAdd(
        nint baseAddress,
        long offset,
        out nint address)
    {
        address = 0;

        if (baseAddress <= 0)
        {
            return false;
        }

        try
        {
            long result = checked(
                baseAddress.ToInt64() + offset);

            if (result <= 0)
            {
                return false;
            }

            address = (nint)result;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static bool TryReadByte(
        nint address,
        out byte value)
    {
        value = 0;

        if (!TryReadBytes(address, 1, out byte[] bytes))
        {
            return false;
        }

        value = bytes[0];
        return true;
    }

    internal static bool TryReadInt32(
        nint address,
        out int value)
    {
        value = 0;

        if (!TryReadBytes(
                address,
                sizeof(int),
                out byte[] bytes))
        {
            return false;
        }

        value = BitConverter.ToInt32(bytes);
        return true;
    }

    internal static bool TryReadUInt32(
        nint address,
        out uint value)
    {
        value = 0;

        if (!TryReadBytes(
                address,
                sizeof(uint),
                out byte[] bytes))
        {
            return false;
        }

        value = BitConverter.ToUInt32(bytes);
        return true;
    }

    internal static bool TryReadUInt64(
        nint address,
        out ulong value)
    {
        value = 0;

        if (!TryReadBytes(
                address,
                sizeof(ulong),
                out byte[] bytes))
        {
            return false;
        }

        value = BitConverter.ToUInt64(bytes);
        return true;
    }

    internal static bool TryReadIntPtr(
        nint address,
        out nint value)
    {
        value = 0;

        if (!TryReadBytes(
                address,
                IntPtr.Size,
                out byte[] bytes))
        {
            return false;
        }

        value = IntPtr.Size == sizeof(long)
            ? (nint)BitConverter.ToInt64(bytes)
            : (nint)BitConverter.ToInt32(bytes);

        return true;
    }

    internal static bool IsReadableRange(
        nint address,
        nuint size) =>
        IsRangeAccessible(
            address,
            size,
            requireExecute: false);

    internal static bool IsExecutableRange(
        nint address,
        nuint size) =>
        IsRangeAccessible(
            address,
            size,
            requireExecute: true);

    internal static bool TryGetDelegate<TDelegate>(
        nint address,
        out TDelegate? function)
        where TDelegate : Delegate
    {
        function = null;

        if (!IsExecutableRange(address, 1))
        {
            return false;
        }

        try
        {
            function = Marshal.GetDelegateForFunctionPointer<TDelegate>(
                address);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            MarshalDirectiveException or
            PlatformNotSupportedException)
        {
            function = null;
            return false;
        }
    }

    internal static bool TryCaptureExecutableSegments(
        nint moduleBase,
        int moduleSize,
        out MemorySegment[] segments)
    {
        segments = [];

        if (!OperatingSystem.IsWindows() ||
            moduleBase <= 0 ||
            moduleSize <= 0)
        {
            return false;
        }

        try
        {
            ulong start = unchecked((ulong)(nuint)moduleBase);
            ulong end = checked(start + (uint)moduleSize);
            ulong cursor = start;

            List<MemorySegment> result = [];

            while (cursor < end)
            {
                nint current = unchecked((nint)(nuint)cursor);

                if (!TryQuery(
                        current,
                        out MemoryBasicInformation information))
                {
                    return false;
                }

                ulong regionBase = unchecked(
                    (ulong)(nuint)information.BaseAddress);
                ulong regionEnd = checked(
                    regionBase +
                    (ulong)information.RegionSize);

                if (regionEnd <= cursor)
                {
                    return false;
                }

                ulong clippedStart = Math.Max(cursor, start);
                ulong clippedEnd = Math.Min(regionEnd, end);

                if (clippedEnd > clippedStart &&
                    IsCommitted(information) &&
                    HasReadAccess(information.Protect) &&
                    HasExecuteAccess(information.Protect))
                {
                    int length = checked(
                        (int)(clippedEnd - clippedStart));

                    nint segmentAddress = unchecked(
                        (nint)(nuint)clippedStart);

                    if (!TryReadBytes(
                            segmentAddress,
                            length,
                            out byte[] bytes))
                    {
                        return false;
                    }

                    result.Add(new(segmentAddress, bytes));
                }

                cursor = clippedEnd > cursor
                    ? clippedEnd
                    : regionEnd;
            }

            if (result.Count == 0)
            {
                return false;
            }

            segments = [.. result];
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadBytes(
        nint address,
        int length,
        out byte[] bytes)
    {
        bytes = [];

        if (!OperatingSystem.IsWindows() ||
            address <= 0 ||
            length <= 0)
        {
            return false;
        }

        nuint size = checked((nuint)length);
        if (!IsReadableRange(address, size))
        {
            return false;
        }

        byte[] buffer = new byte[length];
        nint process = GetCurrentProcess();

        if (process == 0 ||
            !ReadProcessMemory(
                process,
                address,
                buffer,
                size,
                out nuint bytesRead) ||
            bytesRead != size)
        {
            return false;
        }

        bytes = buffer;
        return true;
    }

    private static bool IsRangeAccessible(
        nint address,
        nuint size,
        bool requireExecute)
    {
        if (!OperatingSystem.IsWindows() ||
            address <= 0 ||
            size == 0)
        {
            return false;
        }

        try
        {
            ulong cursor = unchecked((ulong)(nuint)address);
            ulong end = checked(cursor + (ulong)size);

            while (cursor < end)
            {
                nint current = unchecked((nint)(nuint)cursor);

                if (!TryQuery(
                        current,
                        out MemoryBasicInformation information) ||
                    !IsCommitted(information))
                {
                    return false;
                }

                uint protection = information.Protect;

                if (requireExecute)
                {
                    if (!HasExecuteAccess(protection))
                    {
                        return false;
                    }
                }
                else if (!HasReadAccess(protection))
                {
                    return false;
                }

                ulong regionBase = unchecked(
                    (ulong)(nuint)information.BaseAddress);
                ulong regionEnd = checked(
                    regionBase +
                    (ulong)information.RegionSize);

                if (cursor < regionBase ||
                    regionEnd <= cursor)
                {
                    return false;
                }

                cursor = Math.Min(regionEnd, end);
            }

            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryQuery(
        nint address,
        out MemoryBasicInformation information)
    {
        information = default;

        if (!OperatingSystem.IsWindows() ||
            address <= 0)
        {
            return false;
        }

        nuint result = VirtualQuery(
            address,
            out information,
            MemoryBasicInformationSize);

        return result != 0 &&
            information.RegionSize != 0;
    }

    private static bool IsCommitted(
        in MemoryBasicInformation information) =>
        information.State == MemCommit &&
        (information.Protect & PageGuard) == 0 &&
        (information.Protect & PageNoAccess) == 0;

    private static bool HasReadAccess(uint protection)
    {
        uint basic = protection & 0xFF;

        return basic is
            PageReadOnly or
            PageReadWrite or
            PageWriteCopy or
            PageExecuteRead or
            PageExecuteReadWrite or
            PageExecuteWriteCopy;
    }

    private static bool HasExecuteAccess(uint protection)
    {
        uint basic = protection & 0xFF;

        return basic is
            PageExecute or
            PageExecuteRead or
            PageExecuteReadWrite or
            PageExecuteWriteCopy;
    }
}

#pragma warning restore SYSLIB1054