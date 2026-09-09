using System.Runtime.InteropServices;

namespace LowLevelEvents.Source;

internal static class EventDecoder
{
    internal static RawLowLevelEvent Decode(nint buffer)
    {
        uint size = ReadUInt32(buffer, NativeAbi.SizeOffset);
        if (size != NativeAbi.RecordSize)
        {
            throw new InvalidDataException(
                $"Invalid CEventGenerator record size: {size}.");
        }

        uint flags = ReadUInt32(buffer, NativeAbi.FlagsOffset);
        uint argumentCount = ReadUInt32(buffer, NativeAbi.ArgumentCountOffset);
        if (argumentCount > NativeAbi.ArgumentCapacity)
        {
            throw new InvalidDataException(
                $"CEventGenerator returned {argumentCount} arguments, exceeding the event-record capacity.");
        }

        uint probeSize = ReadUInt32(buffer, NativeAbi.ProbeSizeOffset);
        if (probeSize > NativeAbi.ProbeCapacity)
        {
            throw new InvalidDataException(
                $"CEventGenerator returned {probeSize} probe bytes, exceeding the event-record capacity.");
        }

        ulong[] arguments = ReadArguments(buffer, flags, argumentCount);
        byte[] probeData = ReadProbeData(buffer, flags, probeSize);
        uint gameBuild = ReadUInt32(buffer, NativeAbi.GameBuildOffset);
        uint eventId = ReadUInt32(buffer, NativeAbi.EventIdOffset);

        return new RawLowLevelEvent(
            ReadUInt64(buffer, NativeAbi.SequenceOffset),
            ReadInt64(buffer, NativeAbi.PerformanceCounterOffset),
            gameBuild,
            eventId,
            EventCatalog.ResolveName(gameBuild, eventId),
            (LowLevelEventStream)ReadUInt32(buffer, NativeAbi.StreamOffset),
            ReadUInt64(buffer, NativeAbi.GroupAddressOffset),
            ReadUInt64(buffer, NativeAbi.EventAddressOffset),
            (flags & NativeAbi.RelatedEntityAddressFlag) != 0
                ? ReadUInt64(buffer, NativeAbi.RelatedEntityAddressOffset)
                : 0,
            0,
            (flags & NativeAbi.DispatchEntityAddressFlag) != 0
                ? ReadUInt64(buffer, NativeAbi.DispatchEntityAddressOffset)
                : 0,
            0,
            ReadUInt32(buffer, NativeAbi.DispatchEntityCountOffset),
            arguments,
            probeData);
    }

    private static ulong[] ReadArguments(
        nint buffer,
        uint flags,
        uint argumentCount)
    {
        if ((flags & NativeAbi.ArgumentsFlag) == 0 || argumentCount == 0)
        {
            return [];
        }

        ulong[] arguments = new ulong[argumentCount];
        for (int index = 0; index < arguments.Length; ++index)
        {
            arguments[index] = ReadUInt64(
                buffer,
                NativeAbi.ArgumentsOffset + index * sizeof(ulong));
        }
        return arguments;
    }

    private static byte[] ReadProbeData(
        nint buffer,
        uint flags,
        uint probeSize)
    {
        if ((flags & NativeAbi.ProbeDataFlag) == 0 || probeSize == 0)
        {
            return [];
        }

        byte[] data = new byte[probeSize];
        Marshal.Copy(buffer + NativeAbi.ProbeDataOffset, data, 0, data.Length);
        return data;
    }


    private static uint ReadUInt32(nint address, int offset) =>
        unchecked((uint)Marshal.ReadInt32(address, offset));

    private static ulong ReadUInt64(nint address, int offset) =>
        unchecked((ulong)Marshal.ReadInt64(address, offset));

    private static long ReadInt64(nint address, int offset) =>
        Marshal.ReadInt64(address, offset);
}