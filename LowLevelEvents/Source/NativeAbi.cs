namespace LowLevelEvents.Source;

internal static class NativeAbi
{
    internal const int RecordSize = 1040;
    internal const int ArgumentCapacity = 48;
    internal const int ProbeCapacity = 576;
    internal const uint EntityDamageMetadataEventId = 0xFFFF0001u;

    internal const int SizeOffset = 0;
    internal const int FlagsOffset = 4;
    internal const int SequenceOffset = 8;
    internal const int PerformanceCounterOffset = 16;
    internal const int GameBuildOffset = 24;
    internal const int EventIdOffset = 28;
    internal const int StreamOffset = 32;
    internal const int ArgumentCountOffset = 36;
    internal const int GroupAddressOffset = 40;
    internal const int EventAddressOffset = 48;
    internal const int RelatedEntityAddressOffset = 56;
    internal const int DispatchEntityAddressOffset = 64;
    internal const int DispatchEntityCountOffset = 72;
    internal const int ProbeSizeOffset = 76;
    internal const int ProbeDataOffset = 80;
    internal const int ArgumentsOffset = 656;

    internal const uint RelatedEntityAddressFlag = 1 << 0;
    internal const uint ArgumentsFlag = 1 << 1;
    internal const uint DispatchEntityAddressFlag = 1 << 2;
    internal const uint ProbeDataFlag = 1 << 3;
}