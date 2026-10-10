#pragma once

#include "EventAbi.hpp"

#include <array>
#include <cstddef>
#include <cstdint>

namespace CEventGenerator
{
    inline constexpr std::size_t EnhancedDiagnosticCandidateCapacity = 16;
    inline constexpr std::size_t EnhancedDispatchCallsiteDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedDispatchCalleeDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedDispatchForwardCallsiteDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedDispatchForwardCalleeDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedEventConsumerCallsiteDiagnosticCapacity = 32;
    inline constexpr std::size_t EnhancedEventConsumerDiagnosticCapacity = 16;
    inline constexpr std::size_t EnhancedVirtualSlotDiagnosticCapacity = 24;
    inline constexpr std::size_t EnhancedVirtualSlotCallsiteDiagnosticCapacity = 48;
    inline constexpr std::size_t EnhancedRttiEventTypeDiagnosticCapacity = 32;

    struct EnhancedCandidateDiagnostic final
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

    struct EventLocatorDiagnostics final
    {
        LocatorFailure Failure{LocatorFailure::None};
        std::uint32_t PedMatchCount{};
        std::uint32_t GlobalMatchCount{};
        std::uint32_t DamageMatchCount{};
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