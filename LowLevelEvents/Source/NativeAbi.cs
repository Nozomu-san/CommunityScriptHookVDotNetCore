namespace LowLevelEvents.Source;

internal static class NativeAbi
{
    internal const uint BridgeAbiVersion = 2;
    internal const uint BridgeCapabilityCatalogIdentity = 1u << 0;
    internal const uint BridgeCapabilityRuntimeEventVirtualLayout = 1u << 1;
    internal const uint BridgeCapabilityLocatorDiagnostics = 1u << 2;
    internal const uint BridgeCapabilityDispatchCalleeDiagnostics = 1u << 3;
    internal const uint BridgeCapabilityDispatchTopologyDiagnostics = 1u << 4;
    internal const uint BridgeCapabilityDispatchForwardDiagnostics = 1u << 5;
    internal const uint BridgeCapabilityEventConsumerDiagnostics = 1u << 6;
    internal const uint BridgeCapabilityLeafConsumerRecovery = 1u << 7;
    internal const uint BridgeCapabilityEventConsumerTailChain = 1u << 8;
    internal const uint BridgeCapabilityIndirectTailResolution = 1u << 9;
    internal const uint BridgeCapabilityRegisterTailResolution = 1u << 10;
    internal const uint BridgeCapabilityMemoryTailResolution = 1u << 11;
    internal const uint BridgeCapabilityFanoutDiagnostics = 1u << 12;
    internal const uint BridgeCapabilityVirtualSlotDiagnostics = 1u << 13;
    internal const uint BridgeCapabilityTailSlotDiagnostics = 1u << 14;
    internal const uint BridgeCapabilityConvergedDiagnostics = 1u << 15;
    internal const uint BridgeCapabilityVirtualSemanticDiagnostics = 1u << 16;
    internal const uint BridgeCapabilityVirtualInputDiagnostics = 1u << 17;
    internal const uint BridgeCapabilityAllInDiagnostics = 1u << 18;
    internal const uint BridgeCapabilityDynamicGroundTruth = 1u << 19;
    internal const uint BridgeCapabilityDynamicPathAbi = 1u << 20;
    internal const uint BridgeCapabilityEventGatedDynamicCorrelation = 1u << 21;
    internal const uint RequiredBridgeCapabilities =
        BridgeCapabilityCatalogIdentity |
        BridgeCapabilityRuntimeEventVirtualLayout |
        BridgeCapabilityLocatorDiagnostics |
        BridgeCapabilityDispatchCalleeDiagnostics |
        BridgeCapabilityDispatchTopologyDiagnostics |
        BridgeCapabilityDispatchForwardDiagnostics |
        BridgeCapabilityEventConsumerDiagnostics |
        BridgeCapabilityLeafConsumerRecovery |
        BridgeCapabilityEventConsumerTailChain |
        BridgeCapabilityIndirectTailResolution |
        BridgeCapabilityRegisterTailResolution |
        BridgeCapabilityMemoryTailResolution |
        BridgeCapabilityFanoutDiagnostics |
        BridgeCapabilityVirtualSlotDiagnostics |
        BridgeCapabilityTailSlotDiagnostics |
        BridgeCapabilityConvergedDiagnostics |
        BridgeCapabilityVirtualSemanticDiagnostics |
        BridgeCapabilityVirtualInputDiagnostics |
        BridgeCapabilityAllInDiagnostics |
        BridgeCapabilityDynamicGroundTruth |
        BridgeCapabilityDynamicPathAbi |
        BridgeCapabilityEventGatedDynamicCorrelation;
    internal const int RecordSize = 1048;
    internal const int ArgumentCapacity = 48;
    internal const int ProbeCapacity = 576;
    internal const uint EntityDamageMetadataEventId = 0xFFFF0001u;

    internal const int SizeOffset = 0;
    internal const int FlagsOffset = 4;
    internal const int SequenceOffset = 8;
    internal const int PerformanceCounterOffset = 16;
    internal const int GameBuildOffset = 24;
    internal const int EventIdOffset = 28;
    internal const int CatalogEventIdOffset = 32;
    internal const int StreamOffset = 36;
    internal const int ArgumentCountOffset = 40;
    internal const int GroupAddressOffset = 48;
    internal const int EventAddressOffset = 56;
    internal const int RelatedEntityAddressOffset = 64;
    internal const int DispatchEntityAddressOffset = 72;
    internal const int DispatchEntityCountOffset = 80;
    internal const int ProbeSizeOffset = 84;
    internal const int ProbeDataOffset = 88;
    internal const int ArgumentsOffset = 664;

    internal const uint RelatedEntityAddressFlag = 1 << 0;
    internal const uint ArgumentsFlag = 1 << 1;
    internal const uint DispatchEntityAddressFlag = 1 << 2;
    internal const uint ProbeDataFlag = 1 << 3;
}