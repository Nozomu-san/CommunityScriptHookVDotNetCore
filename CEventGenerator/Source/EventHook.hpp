#pragma once

#include "EventAbi.hpp"

#include <cstdint>

namespace CEventGenerator
{
    [[nodiscard]] Status InitializeEventHooks() noexcept;
    void ShutdownEventHooks() noexcept;
    void AdvanceDynamicProbe() noexcept;
    [[nodiscard]] Status GetStatus() noexcept;
    [[nodiscard]] GameEdition GetGameEdition() noexcept;
    [[nodiscard]] std::uint32_t GetGameBuild() noexcept;
    [[nodiscard]] LocatorFailure GetLocatorFailure() noexcept;
    [[nodiscard]] std::uint32_t GetPedMatchCount() noexcept;
    [[nodiscard]] std::uint32_t GetGlobalMatchCount() noexcept;
    [[nodiscard]] std::uint32_t GetDamageMatchCount() noexcept;
    [[nodiscard]] std::uint32_t GetCEventCountAnchorCount() noexcept;
    [[nodiscard]] std::uint32_t GetCEventStackAnchorCount() noexcept;
    [[nodiscard]] std::uint32_t GetEventTypeAnchorCount() noexcept;
    [[nodiscard]] std::uint32_t GetCEventCountOffset() noexcept;
    [[nodiscard]] std::uint32_t GetCEventStackOffset() noexcept;
    [[nodiscard]] std::uint32_t GetEventTypeVFuncOffset() noexcept;
    [[nodiscard]] std::uint32_t GetCEventStackFunctionMatchCount() noexcept;
    [[nodiscard]] std::uint32_t GetCEventStackAnchorRva() noexcept;
    [[nodiscard]] std::uint32_t GetCEventCountFunctionRva() noexcept;
    [[nodiscard]] std::uint32_t GetCEventCountFunctionSize() noexcept;
    [[nodiscard]] std::uint32_t GetEventTypeFunctionRva() noexcept;
    [[nodiscard]] std::uint32_t GetEventTypeFunctionSize() noexcept;
    [[nodiscard]] std::uint32_t GetEventWalkerCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetEventWalkerCandidateRva() noexcept;
    [[nodiscard]] std::uint32_t GetEventWalkerCandidateSize() noexcept;
    [[nodiscard]] std::uint32_t GetTypedEventWalkerCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetTypedEventWalkerCandidateRva() noexcept;
    [[nodiscard]] std::uint32_t GetTypedEventWalkerCandidateSize() noexcept;
    [[nodiscard]] std::uint32_t GetDirectCountCallerCount() noexcept;
    [[nodiscard]] std::uint32_t GetDirectEventTypeCallerCount() noexcept;
    [[nodiscard]] std::uint32_t GetRelatedEventWalkerCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetGuardFlags() noexcept;
    [[nodiscard]] std::uint32_t GetGuardCFFunctionCount() noexcept;
    [[nodiscard]] std::uint32_t GetGuardLongJumpTargetCount() noexcept;
    [[nodiscard]] std::uint32_t GetGuardAddressTakenIatCount() noexcept;
    [[nodiscard]] std::uint32_t GetEnhancedCandidateDiagnosticCount() noexcept;
    [[nodiscard]] std::uint32_t GetEnhancedCandidateRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEnhancedCandidateSize(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEnhancedCandidateRelations(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCallsiteCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCallsiteDiagnosticCount() noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCallsiteWalkerRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCallsiteRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCallsiteCalleeRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCallsiteRelations(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCalleeCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCalleeDiagnosticCount() noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCalleeRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCalleeSize(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCalleeRelations(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCalleeCallsiteCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchCalleeWalkerCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCallsiteCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCallsiteDiagnosticCount() noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCallsiteParentRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCallsiteRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCallsiteCalleeRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCallsiteEventArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCallsiteGroupArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCalleeCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCalleeDiagnosticCount() noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCalleeRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCalleeSize(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCalleeRelations(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCalleeCallsiteCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCalleeParentCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCalleeEventArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetDispatchForwardCalleeGroupArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteDiagnosticCount() noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteWalkerRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteCalleeRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteEventArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteRelations(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteInstructionLength(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteEncoding(std::uint32_t index, std::uint32_t word) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteOperandInfo(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteDisplacement(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerDiagnosticCount() noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerSize(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerRelations(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerFirstUseRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerFirstClobberRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCallsiteCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerWalkerCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerReadCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerMemoryStoreCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerCompareCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerVtableLoadCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerForwardCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailForwardCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailInstructionRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailInstructionLength(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailEncoding(std::uint32_t index, std::uint32_t word) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailOperandInfo(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailDisplacement(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailBaseRegister(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailIndexRegister(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailScale(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailImageSize(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailSlotRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint64_t GetEventConsumerTailSlotAddress(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailSlotProtection(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailSlotFlags(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint64_t GetEventConsumerTailTargetAddress(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailTargetProtection(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailTargetFlags(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint64_t GetEventConsumerTailTargetModuleBase(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailTargetModuleSize(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetEventConsumerTailTargetModuleRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotDiagnosticCount() noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotOffset(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotRelations(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotWalkerCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotEventRcxCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotEventArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotReturnRelations(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotReturnUseCount(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteDiagnosticCount() noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteWalkerRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteOffset(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteRelations(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteEventArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteReturnRelations(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteReturnFirstUseRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteReturnFirstClobberRva(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsitePreparedArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteMemoryArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteAddressArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteImmediateArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteZeroArgumentMask(std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteArgumentSourceRva(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteArgumentSourceKind(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteArgumentSourceEncoding(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteArgumentSourceOperandInfo(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteArgumentSourceDisplacement(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept;
    [[nodiscard]] std::uint32_t GetVirtualSlotCallsiteArgumentSourceValue(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept;
    [[nodiscard]] std::uint32_t GetRttiEventTypeCandidateCount() noexcept;
    [[nodiscard]] std::uint32_t GetRttiEventTypeDiagnosticCount() noexcept;
    [[nodiscard]] std::uint32_t CopyRttiEventTypeName(
        std::uint32_t index,
        char* destination,
        std::uint32_t destinationSize) noexcept;
    [[nodiscard]] std::uint32_t GetRttiEventTypeTypeDescriptorRva(
        std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetRttiEventTypeCompleteObjectLocatorRva(
        std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetRttiEventTypeVftableRva(
        std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetRttiEventTypeEventTypeValue(
        std::uint32_t index) noexcept;
    [[nodiscard]] std::uint32_t GetRttiEventTypeSlotTargetRva(
        std::uint32_t index,
        std::uint32_t slotOffset) noexcept;
    [[nodiscard]] bool IsDynamicProbeArmed() noexcept;
    [[nodiscard]] bool IsDynamicProbeActive() noexcept;
    [[nodiscard]] std::uint32_t GetDynamicProbeSiteCount() noexcept;
    [[nodiscard]] std::uint32_t GetDynamicProbeVirtualSiteCount() noexcept;
    [[nodiscard]] std::uint32_t GetDynamicProbeWalkerSiteCount() noexcept;
    [[nodiscard]] std::uint32_t GetDynamicProbeSessionCount() noexcept;
    [[nodiscard]] std::uint64_t GetDynamicProbeHitCount() noexcept;
    [[nodiscard]] std::uint64_t GetPerformanceFrequency() noexcept;
    [[nodiscard]] std::uint64_t GetObservedCount(std::uint32_t eventId) noexcept;
    void ClearDetailedEventIds() noexcept;
    [[nodiscard]] bool AddDetailedEventId(std::uint32_t eventId) noexcept;
    void ClearCaptureStreamMasks() noexcept;
    [[nodiscard]] bool SetCaptureStreamMask(
        std::uint32_t eventId,
        std::uint32_t streamMask) noexcept;
    [[nodiscard]] std::uint32_t GetCatalogEventCount() noexcept;
    [[nodiscard]] std::uint32_t CopyCatalogEventName(
        std::uint32_t catalogEventId,
        char* destination,
        std::uint32_t destinationSize) noexcept;
    void ClearCatalogCaptureStreamMasks() noexcept;
    [[nodiscard]] bool SetCatalogCaptureStreamMask(
        std::uint32_t catalogEventId,
        std::uint32_t streamMask) noexcept;
}