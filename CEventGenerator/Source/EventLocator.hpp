#pragma once

#include "EventAbi.hpp"

#include <array>
#include <cstddef>
#include <cstdint>

namespace CEventGenerator
{
    inline constexpr std::size_t EnhancedDiagnosticCandidateCapacity = 16;
    inline constexpr std::size_t EnhancedProducerDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedDispatchCallsiteDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedDispatchCalleeDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedDispatchForwardCallsiteDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedDispatchForwardCalleeDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedEventConsumerCallsiteDiagnosticCapacity = 32;
    inline constexpr std::size_t EnhancedEventConsumerDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedVirtualSlotDiagnosticCapacity = 24;
    inline constexpr std::size_t EnhancedVirtualSlotCallsiteDiagnosticCapacity = 48;
    inline constexpr std::size_t EnhancedRttiEventTypeDiagnosticCapacity = 32;
    inline constexpr std::size_t EnhancedSelectedPathDiagnosticCapacity = 4;
    inline constexpr std::size_t EnhancedCallEvidenceCapacity = 48;
    inline constexpr std::size_t EnhancedVirtualCallEvidenceCapacity = 48;
    inline constexpr std::size_t EnhancedCodeWindowCapacity = 64;

    struct EnhancedCandidateDiagnostic final
    {
        std::uint32_t Rva{};
        std::uint32_t Size{};
        std::uint32_t Relations{};
    };

    struct EnhancedProducerDiagnostic final
    {
        std::uint32_t Rva{};
        std::uint32_t Size{};
        std::uint32_t Relations{};
    };

    struct EnhancedDispatchCallsiteDiagnostic final
    {
        std::uint32_t WalkerRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t CalleeRva{};
        std::uint32_t Relations{};
    };

    struct EnhancedDispatchCalleeDiagnostic final
    {
        std::uint32_t Rva{};
        std::uint32_t Size{};
        std::uint32_t Relations{};
        std::uint32_t CallsiteCount{};
        std::uint32_t WalkerCount{};
    };

    struct EnhancedDispatchForwardCallsiteDiagnostic final
    {
        std::uint32_t ParentRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t CalleeRva{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t GroupArgumentMask{};
    };

    struct EnhancedDispatchForwardCalleeDiagnostic final
    {
        std::uint32_t Rva{};
        std::uint32_t Size{};
        std::uint32_t Relations{};
        std::uint32_t CallsiteCount{};
        std::uint32_t ParentCount{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t GroupArgumentMask{};
    };

    struct EnhancedEventConsumerCallsiteDiagnostic final
    {
        std::uint32_t WalkerRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t CalleeRva{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t Relations{};
        std::uint32_t InstructionLength{};
        std::uint32_t Encoding0{};
        std::uint32_t Encoding1{};
        std::uint32_t Encoding2{};
        std::uint32_t Encoding3{};
        std::uint32_t OperandInfo{};
        std::uint32_t Displacement{};
    };

    struct EnhancedEventConsumerDiagnostic final
    {
        std::uint32_t Rva{};
        std::uint32_t Size{};
        std::uint32_t Relations{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t FirstUseRva{};
        std::uint32_t FirstClobberRva{};
        std::uint32_t CallsiteCount{};
        std::uint32_t WalkerCount{};
        std::uint32_t ReadCount{};
        std::uint32_t MemoryStoreCount{};
        std::uint32_t CompareCount{};
        std::uint32_t VtableLoadCount{};
        std::uint32_t ForwardCount{};
        std::uint32_t TailForwardCount{};
        std::uint32_t TailInstructionRva{};
        std::uint32_t TailInstructionLength{};
        std::uint32_t TailEncoding0{};
        std::uint32_t TailEncoding1{};
        std::uint32_t TailEncoding2{};
        std::uint32_t TailEncoding3{};
        std::uint32_t TailOperandInfo{};
        std::uint32_t TailDisplacement{};
        std::uint32_t TailBaseRegister{};
        std::uint32_t TailIndexRegister{};
        std::uint32_t TailScale{};
        std::uint32_t TailImageSize{};
        std::uint32_t TailSlotRva{};
        std::uint64_t TailSlotAddress{};
        std::uint32_t TailSlotProtection{};
        std::uint32_t TailSlotFlags{};
        std::uint64_t TailTargetAddress{};
        std::uint32_t TailTargetProtection{};
        std::uint32_t TailTargetFlags{};
        std::uint64_t TailTargetModuleBase{};
        std::uint32_t TailTargetModuleSize{};
        std::uint32_t TailTargetModuleRva{};
    };

    struct EnhancedVirtualSlotDiagnostic final
    {
        std::uint32_t SlotOffset{};
        std::uint32_t Relations{};
        std::uint32_t CallsiteCount{};
        std::uint32_t WalkerCount{};
        std::uint32_t EventRcxCount{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t ReturnRelations{};
        std::uint32_t ReturnUseCount{};
    };

    struct EnhancedVirtualSlotCallsiteDiagnostic final
    {
        std::uint32_t WalkerRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t SlotOffset{};
        std::uint32_t Relations{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t ReturnRelations{};
        std::uint32_t ReturnFirstUseRva{};
        std::uint32_t ReturnFirstClobberRva{};
        std::uint32_t PreparedArgumentMask{};
        std::uint32_t MemoryArgumentMask{};
        std::uint32_t AddressArgumentMask{};
        std::uint32_t ImmediateArgumentMask{};
        std::uint32_t ZeroArgumentMask{};
        std::uint32_t RdxSourceRva{};
        std::uint32_t RdxSourceKind{};
        std::uint32_t RdxSourceEncoding{};
        std::uint32_t RdxSourceOperandInfo{};
        std::uint32_t RdxSourceDisplacement{};
        std::uint32_t RdxSourceValue{};
        std::uint32_t R8SourceRva{};
        std::uint32_t R8SourceKind{};
        std::uint32_t R8SourceEncoding{};
        std::uint32_t R8SourceOperandInfo{};
        std::uint32_t R8SourceDisplacement{};
        std::uint32_t R8SourceValue{};
        std::uint32_t R9SourceRva{};
        std::uint32_t R9SourceKind{};
        std::uint32_t R9SourceEncoding{};
        std::uint32_t R9SourceOperandInfo{};
        std::uint32_t R9SourceDisplacement{};
        std::uint32_t R9SourceValue{};
    };

    struct EnhancedRttiEventTypeDiagnostic final
    {
        std::uint32_t TypeNameRva{};
        std::uint32_t TypeDescriptorRva{};
        std::uint32_t CompleteObjectLocatorRva{};
        std::uint32_t VftableRva{};
        std::uint32_t EventTypeValue{0xFFFFFFFFu};
        std::uint32_t Slot30TargetRva{0xFFFFFFFFu};
        std::uint32_t Slot48TargetRva{0xFFFFFFFFu};
        std::uint32_t Slot50TargetRva{0xFFFFFFFFu};
        std::uint32_t Slot60TargetRva{0xFFFFFFFFu};
        std::uint32_t Slot68TargetRva{0xFFFFFFFFu};
        std::uint32_t SlotB0TargetRva{0xFFFFFFFFu};
        std::uint32_t SlotF8TargetRva{0xFFFFFFFFu};
    };

    struct EnhancedSelectedPathDiagnostic final
    {
        std::uint32_t EntryRva{};
        std::uint32_t ComparisonRva{};
        std::uint32_t TransferRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t CalleeRva{};
        std::uint32_t GroupRegister{};
        std::uint32_t Flags{};
        std::uint32_t ContextBeginRva{};
        std::uint32_t ContextLength{};
        std::array<std::uint8_t, EnhancedCodeWindowCapacity> Context{};
    };

    struct EnhancedCallEvidence final
    {
        std::uint32_t OwnerRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t TargetRva{};
        std::uint32_t OperandOffset{0xFFFFFFFFu};
        std::uint32_t BaseRegister{0xFFFFFFFFu};
        std::uint32_t Kind{};
        std::uint32_t Length{};
        std::array<std::uint8_t, 15> Bytes{};
    };

    struct EnhancedVirtualCallEvidence final
    {
        std::uint32_t OwnerRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t SlotOffset{};
        std::uint32_t BaseRegister{};
        std::uint32_t OwnerKind{};
        std::uint32_t NearbyLoadRva{};
        std::uint32_t NearbyLoadSourceRegister{0xFFFFFFFFu};
        std::int32_t NearbyLoadDisplacement{};
        std::uint32_t ObjectRegister{0xFFFFFFFFu};
        std::uint32_t VtableLoadRva{};
        std::uint32_t ThisMoveRva{};
        std::uint32_t AliasMoveRva{};
        std::uint32_t ProvenanceFlags{};
        std::uint32_t ContextBeginRva{};
        std::uint32_t ContextLength{};
        std::array<std::uint8_t, 48> Context{};
    };

    struct EnhancedStackPathEvidence final
    {
        std::uint32_t StackLoadRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t StackDisplacement{};
        std::uint32_t SourceBaseRegister{0xFFFFFFFFu};
        std::uint32_t SourceIndexRegister{0xFFFFFFFFu};
        std::uint32_t ObjectRegister{0xFFFFFFFFu};
        std::uint32_t SlotOffset{};
        std::uint32_t ConditionalBranches{};
        std::uint32_t FirstBranchRva{};
        std::uint32_t FirstBranchTargetRva{};
        std::uint32_t FirstBranchFallthroughRva{};
        bool FirstBranchLeavesLocalPath{};
        std::uint32_t Flags{};
    };

    struct EventHookTargets final
    {
        std::array<void*, 3> Functions{};
        void* DamageFunction{};
        void* DamageProcessFunction{};
        std::uint32_t EventIdVFuncOffset{};
        std::uint32_t EventArgumentsVFuncOffset{};
        std::uint32_t EventEntityVFuncOffset{};
        GameEdition Edition{GameEdition::Unknown};
        std::uint32_t GameBuild{};
    };

    struct EnhancedControlFlowEdge final
    {
        std::uint32_t SourceRva{};
        std::uint32_t TargetRva{};
        std::uint32_t DestinationFunctionRva{};
        std::uint32_t DestinationFunctionEndRva{};
        std::uint32_t Status{};
        std::uint32_t ProbeInstructions{};
        std::uint32_t ProbeRegionBytes{};
        std::uint32_t JumpLength{};
        std::int64_t JumpDisplacement{};
        std::uint32_t SectionStartRva{};
        std::uint32_t SectionEndRva{};
        std::uint32_t SectionCharacteristics{};
        std::array<char, 9> SectionName{};
        std::uint32_t PageState{};
        std::uint32_t PageProtection{};
        std::uint32_t SourceByteCount{};
        std::array<std::uint8_t, 12> SourceBytes{};
        std::uint32_t DestinationByteCount{};
        std::array<std::uint8_t, 12> DestinationBytes{};
    };

    struct EnhancedControlFlowEvidence final
    {
        std::uint32_t OwnerRva{};
        std::uint32_t LinearStopRva{};
        std::uint32_t FirstDecodeFailureRva{};
        std::uint32_t StopContainingInstructionRva{};
        std::uint32_t StopContainingInstructionLength{};
        std::uint32_t StopContainingOpcode{};
        bool StopInsideInstruction{};
        std::uint32_t BlockStarts{};
        std::uint32_t Instructions{};
        std::uint32_t DirectBranches{};
        std::uint32_t ConditionalBranches{};
        std::uint32_t IndirectExits{};
        std::uint32_t OutOfFunctionEdges{};
        std::uint32_t FollowedCrossFunctionEdges{};
        std::uint32_t UnfollowedCrossFunctionEdges{};
        std::uint32_t AcceptedFunctionRegions{};
        std::uint32_t ProvisionalRegions{};
        std::uint32_t ProvisionalEdges{};
        std::uint32_t ProvisionalInstructions{};
        std::uint32_t CandidateVirtualCallsites{};
        std::uint32_t ReachableVirtualCallsites{};
        std::uint32_t EdgeDiagnosticCount{};
        std::array<EnhancedControlFlowEdge, 12> EdgeDiagnostics{};
        std::uint32_t IndirectCallSites{};
        std::uint32_t DecodeFailures{};
        bool LinearStopReachable{};
        bool BudgetExceeded{};
    };

    struct EventLocatorDiagnostics final
    {
        LocatorFailure Failure{LocatorFailure::None};
        std::uint32_t PedMatchCount{};
        std::uint32_t GlobalMatchCount{};
        std::uint32_t DamageMatchCount{};
        std::uint32_t EnhancedReceiverScanLimit{};
        std::uint32_t EnhancedDirectPedCount{};
        std::uint32_t EnhancedDirectGlobalCount{};
        std::uint32_t EnhancedSelectedPedTruncated{};
        std::uint32_t EnhancedSelectedGlobalTruncated{};
        std::uint32_t EnhancedUsedPointerFallback{};
        std::uint32_t EnhancedCorrelationGroupCount{};
        std::uint32_t EnhancedMixedCalleeCount{};
        std::uint32_t CEventCountAnchorCount{};
        std::uint32_t CEventStackAnchorCount{};
        std::uint32_t EventTypeAnchorCount{};
        std::uint32_t CEventCountOffset{};
        std::uint32_t CEventStackOffset{};
        std::uint32_t EventTypeVFuncOffset{};
        std::uint32_t CEventStackFunctionMatchCount{};
        std::uint32_t CEventStackAnchorRva{};
        std::uint32_t CEventCountFunctionRva{};
        std::uint32_t CEventCountFunctionSize{};
        std::uint32_t EventTypeFunctionRva{};
        std::uint32_t EventTypeFunctionSize{};
        std::uint32_t EventWalkerCandidateCount{};
        std::uint32_t EventWalkerCandidateRva{};
        std::uint32_t EventWalkerCandidateSize{};
        std::uint32_t TypedEventWalkerCandidateCount{};
        std::uint32_t TypedEventWalkerCandidateRva{};
        std::uint32_t TypedEventWalkerCandidateSize{};
        std::uint32_t DirectCountCallerCount{};
        std::uint32_t DirectEventTypeCallerCount{};
        std::uint32_t RelatedEventWalkerCandidateCount{};
        std::uint32_t GuardFlags{};
        std::uint32_t GuardCFFunctionCount{};
        std::uint32_t GuardLongJumpTargetCount{};
        std::uint32_t GuardAddressTakenIatCount{};
        std::uint32_t EnhancedCandidateDiagnosticCount{};
        std::array<
            EnhancedCandidateDiagnostic,
            EnhancedDiagnosticCandidateCapacity> EnhancedCandidates{};
        std::uint32_t ProducerCandidateCount{};
        std::uint32_t ProducerDiagnosticCount{};
        std::array<
            EnhancedProducerDiagnostic,
            EnhancedProducerDiagnosticCapacity> Producers{};
        std::uint32_t DispatchCallsiteCandidateCount{};
        std::uint32_t DispatchCallsiteDiagnosticCount{};
        std::array<
            EnhancedDispatchCallsiteDiagnostic,
            EnhancedDispatchCallsiteDiagnosticCapacity> DispatchCallsites{};
        std::uint32_t DispatchCalleeCandidateCount{};
        std::uint32_t DispatchCalleeDiagnosticCount{};
        std::array<
            EnhancedDispatchCalleeDiagnostic,
            EnhancedDispatchCalleeDiagnosticCapacity> DispatchCallees{};
        std::uint32_t DispatchForwardCallsiteCandidateCount{};
        std::uint32_t DispatchForwardCallsiteDiagnosticCount{};
        std::array<
            EnhancedDispatchForwardCallsiteDiagnostic,
            EnhancedDispatchForwardCallsiteDiagnosticCapacity> DispatchForwardCallsites{};
        std::uint32_t DispatchForwardCalleeCandidateCount{};
        std::uint32_t DispatchForwardCalleeDiagnosticCount{};
        std::array<
            EnhancedDispatchForwardCalleeDiagnostic,
            EnhancedDispatchForwardCalleeDiagnosticCapacity> DispatchForwardCallees{};
        std::uint32_t EventConsumerCallsiteCandidateCount{};
        std::uint32_t EventConsumerCallsiteDiagnosticCount{};
        std::array<
            EnhancedEventConsumerCallsiteDiagnostic,
            EnhancedEventConsumerCallsiteDiagnosticCapacity> EventConsumerCallsites{};
        std::uint32_t EventConsumerCandidateCount{};
        std::uint32_t EventConsumerDiagnosticCount{};
        std::array<
            EnhancedEventConsumerDiagnostic,
            EnhancedEventConsumerDiagnosticCapacity> EventConsumers{};
        std::uint32_t VirtualSlotCandidateCount{};
        std::uint32_t VirtualSlotDiagnosticCount{};
        std::array<
            EnhancedVirtualSlotDiagnostic,
            EnhancedVirtualSlotDiagnosticCapacity> VirtualSlots{};
        std::uint32_t VirtualSlotCallsiteCandidateCount{};
        std::uint32_t VirtualSlotCallsiteDiagnosticCount{};
        std::array<
            EnhancedVirtualSlotCallsiteDiagnostic,
            EnhancedVirtualSlotCallsiteDiagnosticCapacity> VirtualSlotCallsites{};
        std::uint32_t EnhancedSelectedPathDiagnosticCount{};
        std::array<EnhancedSelectedPathDiagnostic,
            EnhancedSelectedPathDiagnosticCapacity> EnhancedSelectedPaths{};
        std::uint32_t EnhancedCallEvidenceCandidateCount{};
        std::uint32_t EnhancedCallEvidenceDiagnosticCount{};
        std::uint32_t EnhancedCallDecoderStopCount{};
        std::array<EnhancedCallEvidence,
            EnhancedCallEvidenceCapacity> EnhancedCallEvidenceSamples{};
        std::uint32_t EnhancedVirtualCallCandidateCount{};
        std::uint32_t EnhancedVirtualCallDiagnosticCount{};
        std::uint32_t EnhancedVirtualCallDecoderStops{};
        std::uint32_t EnhancedDecoderStopOwnerRva{};
        std::uint32_t EnhancedDecoderStopRva{};
        std::uint32_t EnhancedDecoderStopReason{};
        std::uint32_t EnhancedDecoderStopByteCount{};
        std::array<std::uint8_t, 15> EnhancedDecoderStopBytes{};
        std::array<EnhancedVirtualCallEvidence,
            EnhancedVirtualCallEvidenceCapacity> EnhancedVirtualCalls{};
        EnhancedStackPathEvidence EnhancedStackPath{};
        EnhancedControlFlowEvidence EnhancedControlFlow{};
        EnhancedControlFlowEvidence EnhancedCountControlFlow{};
        EnhancedControlFlowEvidence EnhancedTypeControlFlow{};
        std::uint32_t RttiEventTypeCandidateCount{};
        std::uint32_t RttiEventTypeDiagnosticCount{};
        std::array<
            EnhancedRttiEventTypeDiagnostic,
            EnhancedRttiEventTypeDiagnosticCapacity> RttiEventTypes{};
    };

    struct EventLocatorResult final
    {
        Status State{Status::HookNotFound};
        EventHookTargets Targets{};
        EventLocatorDiagnostics Diagnostics{};
    };

    [[nodiscard]] EventLocatorResult LocateEventHooks() noexcept;
}