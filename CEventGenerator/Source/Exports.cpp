#include "EventAbi.hpp"
#include "EventHook.hpp"
#include "EventQueue.hpp"

#include <cstdint>
#include <cstring>
#include <utility>

using namespace CEventGenerator;

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetAbiVersion() noexcept
{
    return BridgeAbiVersion;
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCapabilities() noexcept
{
    return BridgeCapabilities;
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetLocatorRevision() noexcept
{
    return LocatorRevision;
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDynamicProbeArmed() noexcept
{
    return IsDynamicProbeArmed() ? 1u : 0u;
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDynamicProbeActive() noexcept
{
    return IsDynamicProbeActive() ? 1u : 0u;
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDynamicProbeSiteCount() noexcept
{
    return GetDynamicProbeSiteCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDynamicProbeVirtualSiteCount() noexcept
{
    return GetDynamicProbeVirtualSiteCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDynamicProbeWalkerSiteCount() noexcept
{
    return GetDynamicProbeWalkerSiteCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDynamicProbeSessionCount() noexcept
{
    return GetDynamicProbeSessionCount();
}

extern "C" __declspec(dllexport)
std::uint64_t CEG_GetDynamicProbeHitCount() noexcept
{
    return GetDynamicProbeHitCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRecordSize() noexcept
{
    return sizeof(EventRecord);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetStatus() noexcept
{
    return std::to_underlying(GetStatus());
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetGameEdition() noexcept
{
    return std::to_underlying(GetGameEdition());
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetGameBuild() noexcept
{
    return GetGameBuild();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetLocatorFailure() noexcept
{
    return std::to_underlying(GetLocatorFailure());
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetPedMatchCount() noexcept
{
    return GetPedMatchCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetGlobalMatchCount() noexcept
{
    return GetGlobalMatchCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDamageMatchCount() noexcept
{
    return GetDamageMatchCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCEventCountAnchorCount() noexcept
{
    return GetCEventCountAnchorCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCEventStackAnchorCount() noexcept
{
    return GetCEventStackAnchorCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventTypeAnchorCount() noexcept
{
    return GetEventTypeAnchorCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCEventCountOffset() noexcept
{
    return GetCEventCountOffset();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCEventStackOffset() noexcept
{
    return GetCEventStackOffset();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventTypeVFuncOffset() noexcept
{
    return GetEventTypeVFuncOffset();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCEventStackFunctionMatchCount() noexcept
{
    return GetCEventStackFunctionMatchCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCEventStackAnchorRva() noexcept
{
    return GetCEventStackAnchorRva();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCEventCountFunctionRva() noexcept
{
    return GetCEventCountFunctionRva();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCEventCountFunctionSize() noexcept
{
    return GetCEventCountFunctionSize();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventTypeFunctionRva() noexcept
{
    return GetEventTypeFunctionRva();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventTypeFunctionSize() noexcept
{
    return GetEventTypeFunctionSize();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventWalkerCandidateCount() noexcept
{
    return GetEventWalkerCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventWalkerCandidateRva() noexcept
{
    return GetEventWalkerCandidateRva();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventWalkerCandidateSize() noexcept
{
    return GetEventWalkerCandidateSize();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetTypedEventWalkerCandidateCount() noexcept
{
    return GetTypedEventWalkerCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetTypedEventWalkerCandidateRva() noexcept
{
    return GetTypedEventWalkerCandidateRva();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetTypedEventWalkerCandidateSize() noexcept
{
    return GetTypedEventWalkerCandidateSize();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDirectCountCallerCount() noexcept
{
    return GetDirectCountCallerCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDirectEventTypeCallerCount() noexcept
{
    return GetDirectEventTypeCallerCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRelatedEventWalkerCandidateCount() noexcept
{
    return GetRelatedEventWalkerCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetGuardFlags() noexcept
{
    return GetGuardFlags();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetGuardCFFunctionCount() noexcept
{
    return GetGuardCFFunctionCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetGuardLongJumpTargetCount() noexcept
{
    return GetGuardLongJumpTargetCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetGuardAddressTakenIatCount() noexcept
{
    return GetGuardAddressTakenIatCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEnhancedCandidateDiagnosticCount() noexcept
{
    return GetEnhancedCandidateDiagnosticCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEnhancedCandidateRva(std::uint32_t index) noexcept
{
    return GetEnhancedCandidateRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEnhancedCandidateSize(std::uint32_t index) noexcept
{
    return GetEnhancedCandidateSize(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEnhancedCandidateRelations(std::uint32_t index) noexcept
{
    return GetEnhancedCandidateRelations(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCallsiteCandidateCount() noexcept
{
    return GetDispatchCallsiteCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCallsiteDiagnosticCount() noexcept
{
    return GetDispatchCallsiteDiagnosticCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCallsiteWalkerRva(std::uint32_t index) noexcept
{
    return GetDispatchCallsiteWalkerRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCallsiteRva(std::uint32_t index) noexcept
{
    return GetDispatchCallsiteRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCallsiteCalleeRva(std::uint32_t index) noexcept
{
    return GetDispatchCallsiteCalleeRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCallsiteRelations(std::uint32_t index) noexcept
{
    return GetDispatchCallsiteRelations(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCalleeCandidateCount() noexcept
{
    return GetDispatchCalleeCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCalleeDiagnosticCount() noexcept
{
    return GetDispatchCalleeDiagnosticCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCalleeRva(std::uint32_t index) noexcept
{
    return GetDispatchCalleeRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCalleeSize(std::uint32_t index) noexcept
{
    return GetDispatchCalleeSize(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCalleeRelations(std::uint32_t index) noexcept
{
    return GetDispatchCalleeRelations(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCalleeCallsiteCount(std::uint32_t index) noexcept
{
    return GetDispatchCalleeCallsiteCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchCalleeWalkerCount(std::uint32_t index) noexcept
{
    return GetDispatchCalleeWalkerCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCallsiteCandidateCount() noexcept
{
    return GetDispatchForwardCallsiteCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCallsiteDiagnosticCount() noexcept
{
    return GetDispatchForwardCallsiteDiagnosticCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCallsiteParentRva(std::uint32_t index) noexcept
{
    return GetDispatchForwardCallsiteParentRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCallsiteRva(std::uint32_t index) noexcept
{
    return GetDispatchForwardCallsiteRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCallsiteCalleeRva(std::uint32_t index) noexcept
{
    return GetDispatchForwardCallsiteCalleeRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCallsiteEventArgumentMask(std::uint32_t index) noexcept
{
    return GetDispatchForwardCallsiteEventArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCallsiteGroupArgumentMask(std::uint32_t index) noexcept
{
    return GetDispatchForwardCallsiteGroupArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCalleeCandidateCount() noexcept
{
    return GetDispatchForwardCalleeCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCalleeDiagnosticCount() noexcept
{
    return GetDispatchForwardCalleeDiagnosticCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCalleeRva(std::uint32_t index) noexcept
{
    return GetDispatchForwardCalleeRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCalleeSize(std::uint32_t index) noexcept
{
    return GetDispatchForwardCalleeSize(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCalleeRelations(std::uint32_t index) noexcept
{
    return GetDispatchForwardCalleeRelations(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCalleeCallsiteCount(std::uint32_t index) noexcept
{
    return GetDispatchForwardCalleeCallsiteCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCalleeParentCount(std::uint32_t index) noexcept
{
    return GetDispatchForwardCalleeParentCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCalleeEventArgumentMask(std::uint32_t index) noexcept
{
    return GetDispatchForwardCalleeEventArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetDispatchForwardCalleeGroupArgumentMask(std::uint32_t index) noexcept
{
    return GetDispatchForwardCalleeGroupArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteCandidateCount() noexcept
{
    return GetEventConsumerCallsiteCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteDiagnosticCount() noexcept
{
    return GetEventConsumerCallsiteDiagnosticCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteWalkerRva(std::uint32_t index) noexcept
{
    return GetEventConsumerCallsiteWalkerRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteRva(std::uint32_t index) noexcept
{
    return GetEventConsumerCallsiteRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteCalleeRva(std::uint32_t index) noexcept
{
    return GetEventConsumerCallsiteCalleeRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteEventArgumentMask(std::uint32_t index) noexcept
{
    return GetEventConsumerCallsiteEventArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteRelations(std::uint32_t index) noexcept
{
    return GetEventConsumerCallsiteRelations(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteInstructionLength(std::uint32_t index) noexcept
{
    return GetEventConsumerCallsiteInstructionLength(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteEncoding(std::uint32_t index, std::uint32_t word) noexcept
{
    return GetEventConsumerCallsiteEncoding(index, word);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteOperandInfo(std::uint32_t index) noexcept
{
    return GetEventConsumerCallsiteOperandInfo(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteDisplacement(std::uint32_t index) noexcept
{
    return GetEventConsumerCallsiteDisplacement(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCandidateCount() noexcept
{
    return GetEventConsumerCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerDiagnosticCount() noexcept
{
    return GetEventConsumerDiagnosticCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerRva(std::uint32_t index) noexcept
{
    return GetEventConsumerRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerSize(std::uint32_t index) noexcept
{
    return GetEventConsumerSize(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerRelations(std::uint32_t index) noexcept
{
    return GetEventConsumerRelations(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerArgumentMask(std::uint32_t index) noexcept
{
    return GetEventConsumerArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerFirstUseRva(std::uint32_t index) noexcept
{
    return GetEventConsumerFirstUseRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerFirstClobberRva(std::uint32_t index) noexcept
{
    return GetEventConsumerFirstClobberRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCallsiteCount(std::uint32_t index) noexcept
{
    return GetEventConsumerCallsiteCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerWalkerCount(std::uint32_t index) noexcept
{
    return GetEventConsumerWalkerCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerReadCount(std::uint32_t index) noexcept
{
    return GetEventConsumerReadCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerMemoryStoreCount(std::uint32_t index) noexcept
{
    return GetEventConsumerMemoryStoreCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerCompareCount(std::uint32_t index) noexcept
{
    return GetEventConsumerCompareCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerVtableLoadCount(std::uint32_t index) noexcept
{
    return GetEventConsumerVtableLoadCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerForwardCount(std::uint32_t index) noexcept
{
    return GetEventConsumerForwardCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailForwardCount(std::uint32_t index) noexcept
{
    return GetEventConsumerTailForwardCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailInstructionRva(std::uint32_t index) noexcept
{
    return GetEventConsumerTailInstructionRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailInstructionLength(std::uint32_t index) noexcept
{
    return GetEventConsumerTailInstructionLength(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailEncoding(std::uint32_t index, std::uint32_t word) noexcept
{
    return GetEventConsumerTailEncoding(index, word);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailOperandInfo(std::uint32_t index) noexcept
{
    return GetEventConsumerTailOperandInfo(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailDisplacement(std::uint32_t index) noexcept
{
    return GetEventConsumerTailDisplacement(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailBaseRegister(std::uint32_t index) noexcept
{
    return GetEventConsumerTailBaseRegister(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailIndexRegister(std::uint32_t index) noexcept
{
    return GetEventConsumerTailIndexRegister(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailScale(std::uint32_t index) noexcept
{
    return GetEventConsumerTailScale(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailImageSize(std::uint32_t index) noexcept
{
    return GetEventConsumerTailImageSize(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailSlotRva(std::uint32_t index) noexcept
{
    return GetEventConsumerTailSlotRva(index);
}

extern "C" __declspec(dllexport)
std::uint64_t CEG_GetEventConsumerTailSlotAddress(std::uint32_t index) noexcept
{
    return GetEventConsumerTailSlotAddress(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailSlotProtection(std::uint32_t index) noexcept
{
    return GetEventConsumerTailSlotProtection(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailSlotFlags(std::uint32_t index) noexcept
{
    return GetEventConsumerTailSlotFlags(index);
}

extern "C" __declspec(dllexport)
std::uint64_t CEG_GetEventConsumerTailTargetAddress(std::uint32_t index) noexcept
{
    return GetEventConsumerTailTargetAddress(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailTargetProtection(std::uint32_t index) noexcept
{
    return GetEventConsumerTailTargetProtection(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailTargetFlags(std::uint32_t index) noexcept
{
    return GetEventConsumerTailTargetFlags(index);
}

extern "C" __declspec(dllexport)
std::uint64_t CEG_GetEventConsumerTailTargetModuleBase(std::uint32_t index) noexcept
{
    return GetEventConsumerTailTargetModuleBase(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailTargetModuleSize(std::uint32_t index) noexcept
{
    return GetEventConsumerTailTargetModuleSize(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetEventConsumerTailTargetModuleRva(std::uint32_t index) noexcept
{
    return GetEventConsumerTailTargetModuleRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCandidateCount() noexcept
{
    return GetVirtualSlotCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotDiagnosticCount() noexcept
{
    return GetVirtualSlotDiagnosticCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotOffset(std::uint32_t index) noexcept
{
    return GetVirtualSlotOffset(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotRelations(std::uint32_t index) noexcept
{
    return GetVirtualSlotRelations(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteCount(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotWalkerCount(std::uint32_t index) noexcept
{
    return GetVirtualSlotWalkerCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotEventRcxCount(std::uint32_t index) noexcept
{
    return GetVirtualSlotEventRcxCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotEventArgumentMask(std::uint32_t index) noexcept
{
    return GetVirtualSlotEventArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotReturnRelations(std::uint32_t index) noexcept
{
    return GetVirtualSlotReturnRelations(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotReturnUseCount(std::uint32_t index) noexcept
{
    return GetVirtualSlotReturnUseCount(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteCandidateCount() noexcept
{
    return GetVirtualSlotCallsiteCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteDiagnosticCount() noexcept
{
    return GetVirtualSlotCallsiteDiagnosticCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteWalkerRva(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteWalkerRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteRva(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteOffset(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteOffset(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteRelations(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteRelations(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteEventArgumentMask(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteEventArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteReturnRelations(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteReturnRelations(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteReturnFirstUseRva(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteReturnFirstUseRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteReturnFirstClobberRva(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteReturnFirstClobberRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsitePreparedArgumentMask(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsitePreparedArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteMemoryArgumentMask(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteMemoryArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteAddressArgumentMask(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteAddressArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteImmediateArgumentMask(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteImmediateArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteZeroArgumentMask(std::uint32_t index) noexcept
{
    return GetVirtualSlotCallsiteZeroArgumentMask(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteArgumentSourceRva(
    std::uint32_t index,
    std::uint32_t argumentIndex) noexcept
{
    return GetVirtualSlotCallsiteArgumentSourceRva(index, argumentIndex);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteArgumentSourceKind(
    std::uint32_t index,
    std::uint32_t argumentIndex) noexcept
{
    return GetVirtualSlotCallsiteArgumentSourceKind(index, argumentIndex);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteArgumentSourceEncoding(
    std::uint32_t index,
    std::uint32_t argumentIndex) noexcept
{
    return GetVirtualSlotCallsiteArgumentSourceEncoding(index, argumentIndex);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteArgumentSourceOperandInfo(
    std::uint32_t index,
    std::uint32_t argumentIndex) noexcept
{
    return GetVirtualSlotCallsiteArgumentSourceOperandInfo(index, argumentIndex);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteArgumentSourceDisplacement(
    std::uint32_t index,
    std::uint32_t argumentIndex) noexcept
{
    return GetVirtualSlotCallsiteArgumentSourceDisplacement(index, argumentIndex);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetVirtualSlotCallsiteArgumentSourceValue(
    std::uint32_t index,
    std::uint32_t argumentIndex) noexcept
{
    return GetVirtualSlotCallsiteArgumentSourceValue(index, argumentIndex);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRttiEventTypeCandidateCount() noexcept
{
    return GetRttiEventTypeCandidateCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRttiEventTypeDiagnosticCount() noexcept
{
    return GetRttiEventTypeDiagnosticCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRttiEventTypeName(
    std::uint32_t index,
    char* destination,
    std::uint32_t destinationSize) noexcept
{
    return CopyRttiEventTypeName(index, destination, destinationSize);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRttiEventTypeTypeDescriptorRva(
    std::uint32_t index) noexcept
{
    return GetRttiEventTypeTypeDescriptorRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRttiEventTypeCompleteObjectLocatorRva(
    std::uint32_t index) noexcept
{
    return GetRttiEventTypeCompleteObjectLocatorRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRttiEventTypeVftableRva(
    std::uint32_t index) noexcept
{
    return GetRttiEventTypeVftableRva(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRttiEventTypeEventTypeValue(
    std::uint32_t index) noexcept
{
    return GetRttiEventTypeEventTypeValue(index);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRttiEventTypeSlotTargetRva(
    std::uint32_t index,
    std::uint32_t slotOffset) noexcept
{
    return GetRttiEventTypeSlotTargetRva(index, slotOffset);
}

extern "C" __declspec(dllexport)
std::uint64_t CEG_GetPerformanceFrequency() noexcept
{
    return GetPerformanceFrequency();
}

extern "C" __declspec(dllexport)
std::uint64_t CEG_GetPendingCount() noexcept
{
    return EventQueue::Instance().PendingCount();
}

extern "C" __declspec(dllexport)
std::uint64_t CEG_GetDroppedCount() noexcept
{
    return EventQueue::Instance().DroppedCount();
}

extern "C" __declspec(dllexport)
std::uint64_t CEG_GetObservedCount(std::uint32_t eventId) noexcept
{
    return GetObservedCount(eventId);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_TryDequeue(
    void* destination,
    std::uint32_t destinationSize) noexcept
{
    if (destination == nullptr || destinationSize < sizeof(EventRecord))
    {
        return 0;
    }

    EventRecord record{};
    if (!EventQueue::Instance().TryPop(record))
    {
        return 0;
    }

    std::memcpy(destination, &record, sizeof(record));
    return 1;
}

extern "C" __declspec(dllexport)
void CEG_ClearDetailedEventIds() noexcept
{
    ClearDetailedEventIds();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_AddDetailedEventId(std::uint32_t eventId) noexcept
{
    return AddDetailedEventId(eventId) ? 1u : 0u;
}

extern "C" __declspec(dllexport)
void CEG_ClearCaptureStreamMasks() noexcept
{
    ClearCaptureStreamMasks();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_SetCaptureStreamMask(
    std::uint32_t eventId,
    std::uint32_t streamMask) noexcept
{
    return SetCaptureStreamMask(eventId, streamMask) ? 1u : 0u;
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCatalogEventCount() noexcept
{
    return GetCatalogEventCount();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetCatalogEventName(
    std::uint32_t catalogEventId,
    char* destination,
    std::uint32_t destinationSize) noexcept
{
    return CopyCatalogEventName(catalogEventId, destination, destinationSize);
}

extern "C" __declspec(dllexport)
void CEG_ClearCatalogCaptureStreamMasks() noexcept
{
    ClearCatalogCaptureStreamMasks();
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_SetCatalogCaptureStreamMask(
    std::uint32_t catalogEventId,
    std::uint32_t streamMask) noexcept
{
    return SetCatalogCaptureStreamMask(catalogEventId, streamMask) ? 1u : 0u;
}