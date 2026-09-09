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
    [DllImport(
        Kernel32,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        nint process,
        nint baseAddress,
        byte[] buffer,
        nuint size,
        out nuint bytesWritten);

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

    internal static bool TryReadUInt16(
        nint address,
        out ushort value)
    {
        value = 0;
        if (!TryReadBytes(address, sizeof(ushort), out byte[] bytes))
        {
            return false;
        }
        value = BitConverter.ToUInt16(bytes);
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

    internal static bool TryReadSingle(
        nint address,
        out float value)
    {
        value = 0f;
        if (!TryReadBytes(address, sizeof(float), out byte[] bytes))
        {
            return false;
        }
        value = BitConverter.ToSingle(bytes);
        return float.IsFinite(value);
    }

    internal static bool TryWriteSingle(
        nint address,
        float value)
    {
        if (!float.IsFinite(value))
        {
            return false;
        }
        return TryWriteBytes(address, BitConverter.GetBytes(value));
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
        IsRangeAccessible(address, size, AccessRequirement.Read);

    internal static bool IsWritableRange(
        nint address,
        nuint size) =>
        IsRangeAccessible(address, size, AccessRequirement.Write);

    internal static bool IsExecutableRange(
        nint address,
        nuint size) =>
        IsRangeAccessible(address, size, AccessRequirement.Execute);

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

    internal static bool TryReadBuffer(
        nint address,
        int length,
        out byte[] bytes) =>
        TryReadBytes(address, length, out bytes);

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

    private static bool TryWriteBytes(nint address, byte[] bytes)
    {
        if (!OperatingSystem.IsWindows() ||
            address <= 0 ||
            bytes.Length == 0)
        {
            return false;
        }

        nuint size = checked((nuint)bytes.Length);
        if (!IsWritableRange(address, size))
        {
            return false;
        }

        nint process = GetCurrentProcess();
        return process != 0 &&
            WriteProcessMemory(
                process,
                address,
                bytes,
                size,
                out nuint written) &&
            written == size;
    }

    private enum AccessRequirement
    {
        Read,
        Write,
        Execute
    }

    private static bool IsRangeAccessible(
        nint address,
        nuint size,
        AccessRequirement requirement)
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

                bool allowed = requirement switch
                {
                    AccessRequirement.Execute => HasExecuteAccess(protection),
                    AccessRequirement.Write => HasWriteAccess(protection),
                    _ => HasReadAccess(protection)
                };
                if (!allowed)
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

    private static bool HasWriteAccess(uint protection)
    {
        uint basic = protection & 0xFF;
        return basic is
            PageReadWrite or
            PageWriteCopy or
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
internal sealed class LocalMemoryService : ILocalMemory
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ulong Int32ToPointerDelegate(int value);

    public LocalDataResult<nint> FindPattern(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return LocalDataResult<nint>.Failed(LocalDataStatus.InvalidRequest);
        }

        nint match = ModulePattern.Find(expression);
        return match != 0
            ? LocalDataResult<nint>.Succeeded(match)
            : LocalDataResult<nint>.Failed(LocalDataStatus.DataUnavailable);
    }

    public LocalDataResult<nint> ResolveRipRelative(
        nint instruction,
        int displacementOffset,
        int instructionLength)
    {
        if (instruction <= 0 ||
            displacementOffset < 0 ||
            instructionLength <= 0 ||
            displacementOffset >= instructionLength)
        {
            return LocalDataResult<nint>.Failed(LocalDataStatus.InvalidRequest);
        }

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
            return LocalDataResult<nint>.Failed(LocalDataStatus.AccessRejected);
        }

        return result > 0
            ? LocalDataResult<nint>.Succeeded(result)
            : LocalDataResult<nint>.Failed(LocalDataStatus.VerificationFailed);
    }

    public LocalDataResult<nint> Add(nint address, long offset)
    {
        if (address <= 0)
        {
            return LocalDataResult<nint>.Failed(LocalDataStatus.InvalidRequest);
        }

        return MemoryAccess.TryAdd(address, offset, out nint result) &&
               result > 0
            ? LocalDataResult<nint>.Succeeded(result)
            : LocalDataResult<nint>.Failed(LocalDataStatus.AccessRejected);
    }

    public LocalDataResult<nint> InvokeInt32ToPointer(
        nint functionAddress,
        int argument)
    {
        if (!MemoryAccess.TryGetDelegate<Int32ToPointerDelegate>(
                functionAddress,
                out Int32ToPointerDelegate? function) ||
            function is null)
        {
            return LocalDataResult<nint>.Failed(LocalDataStatus.AccessRejected);
        }

        try
        {
            nint result = unchecked((nint)(nuint)function(argument));
            return result > 0 && MemoryAccess.IsReadableRange(result, 1)
                ? LocalDataResult<nint>.Succeeded(result)
                : LocalDataResult<nint>.Failed(LocalDataStatus.VerificationFailed);
        }
        catch
        {
            return LocalDataResult<nint>.Failed(LocalDataStatus.DataUnavailable);
        }
    }

    public LocalDataResult<byte> ReadByte(nint address) =>
        MemoryAccess.TryReadByte(address, out byte value)
            ? LocalDataResult<byte>.Succeeded(value)
            : LocalDataResult<byte>.Failed(LocalDataStatus.AccessRejected);

    public LocalDataResult<ushort> ReadUInt16(nint address) =>
        MemoryAccess.TryReadUInt16(address, out ushort value)
            ? LocalDataResult<ushort>.Succeeded(value)
            : LocalDataResult<ushort>.Failed(LocalDataStatus.AccessRejected);

    public LocalDataResult<uint> ReadUInt32(nint address) =>
        MemoryAccess.TryReadUInt32(address, out uint value)
            ? LocalDataResult<uint>.Succeeded(value)
            : LocalDataResult<uint>.Failed(LocalDataStatus.AccessRejected);

    public LocalDataResult<ulong> ReadUInt64(nint address) =>
        MemoryAccess.TryReadUInt64(address, out ulong value)
            ? LocalDataResult<ulong>.Succeeded(value)
            : LocalDataResult<ulong>.Failed(LocalDataStatus.AccessRejected);

    public LocalDataResult<int> ReadInt32(nint address) =>
        MemoryAccess.TryReadInt32(address, out int value)
            ? LocalDataResult<int>.Succeeded(value)
            : LocalDataResult<int>.Failed(LocalDataStatus.AccessRejected);

    public LocalDataResult<nint> ReadPointer(nint address) =>
        MemoryAccess.TryReadIntPtr(address, out nint value) && value > 0
            ? LocalDataResult<nint>.Succeeded(value)
            : LocalDataResult<nint>.Failed(LocalDataStatus.AccessRejected);

    public LocalDataResult<float> ReadSingle(nint address) =>
        MemoryAccess.TryReadSingle(address, out float value)
            ? LocalDataResult<float>.Succeeded(value)
            : LocalDataResult<float>.Failed(LocalDataStatus.AccessRejected);

    public LocalDataStatus WriteSingle(nint address, float value)
    {
        if (!float.IsFinite(value))
        {
            return LocalDataStatus.InvalidRequest;
        }

        return MemoryAccess.TryWriteSingle(address, value)
            ? LocalDataStatus.Success
            : LocalDataStatus.AccessRejected;
    }

    public bool IsReadableRange(nint address, nuint size) =>
        MemoryAccess.IsReadableRange(address, size);

    public bool IsWritableRange(nint address, nuint size) =>
        MemoryAccess.IsWritableRange(address, size);

    public bool IsExecutableRange(nint address, nuint size) =>
        MemoryAccess.IsExecutableRange(address, size);
}

#pragma warning restore SYSLIB1054