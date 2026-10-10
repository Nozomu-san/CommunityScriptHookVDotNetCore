#include "Configuration.hpp"
#include "EventHook.hpp"
#include "EventLocator.hpp"
#include "EventQueue.hpp"
#include "MinHook/src/hde/hde64.h"

#include <Windows.h>
#include <MinHook.h>

#pragma comment(lib, "User32.lib")

#include <algorithm>
#include <array>
#include <bit>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <iterator>
#include <cstdio>
#include <cwchar>
#include <string_view>

namespace
{
    using namespace CEventGenerator;
    using EventDispatch = void* (*)(void*, void*);
    using DamageDispatch = void (*)(void*, void*, std::uint32_t, std::uint32_t, bool);

    struct DamageProcessData final
    {
        void* Culprit{};
        float BaseDamage{};
        std::uint32_t Weapon{};
    };

    static_assert(offsetof(DamageProcessData, BaseDamage) == 8);
    static_assert(offsetof(DamageProcessData, Weapon) == 12);
    static_assert(sizeof(DamageProcessData) == 16);

    using DamageProcessDispatch = bool (*)(
        DamageProcessData*,
        void*,
        std::uintptr_t,
        std::uintptr_t,
        std::uintptr_t,
        std::uintptr_t);
    using GetEventId = std::int32_t(__fastcall*)(void*);
    using GetEventArguments = bool(__fastcall*)(void*, void*, std::size_t);
    using GetEventEntity = void* (__fastcall*)(void*);

    constexpr std::uint32_t EmptyDetailedEventId = 0xFFFFFFFFu;
    constexpr std::size_t DetailedEventIdCapacity = 24;
    constexpr std::size_t CaptureEventIdCapacity = 512;
    constexpr std::uint8_t ReactionStreamMask = 1u << 0;
    constexpr std::uint8_t GroupStreamMask = 1u << 1;
    constexpr std::uint8_t GlobalStreamMask = 1u << 2;
    constexpr std::size_t DetailedEventProbeCapacity = 128;
    constexpr std::size_t AimEventProbeCapacity = 288;
    constexpr std::size_t AimGroupProbeCapacity = 288;
    constexpr std::size_t RecentDamageContextCapacity = 32;

    constexpr std::uint32_t CatalogEventCacheMiss = 0xFFFFFFFFu;
    constexpr std::size_t CatalogVtableCacheCapacity = 256;

    constexpr std::array CatalogEventNames
    {
        std::string_view{"CEventAcquaintancePed"},
        std::string_view{"CEventAcquaintancePedDead"},
        std::string_view{"CEventAcquaintancePedDislike"},
        std::string_view{"CEventAcquaintancePedHate"},
        std::string_view{"CEventAcquaintancePedLike"},
        std::string_view{"CEventAcquaintancePedWanted"},
        std::string_view{"CEventAgitated"},
        std::string_view{"CEventAgitatedAction"},
        std::string_view{"CEventCallForCover"},
        std::string_view{"CEventCarUndriveable"},
        std::string_view{"CEventClimbLadderOnRoute"},
        std::string_view{"CEventClimbNavMeshOnRoute"},
        std::string_view{"CEventCombatTaunt"},
        std::string_view{"CEventCommunicateEvent"},
        std::string_view{"CEventCopCarBeingStolen"},
        std::string_view{"CEventCrimeCryForHelp"},
        std::string_view{"CEventCrimeReported"},
        std::string_view{"CEventDamage"},
        std::string_view{"CEventDataDecisionMaker"},
        std::string_view{"CEventDataFileMounter"},
        std::string_view{"CEventDataResponseAggressiveRubberneck"},
        std::string_view{"CEventDataResponseDeferToScenarioPointFlags"},
        std::string_view{"CEventDataResponseFriendlyAimedAt"},
        std::string_view{"CEventDataResponseFriendlyNearMiss"},
        std::string_view{"CEventDataResponsePlayerDeath"},
        std::string_view{"CEventDataResponsePoliceTaskWanted"},
        std::string_view{"CEventDataResponseSwatTaskWanted"},
        std::string_view{"CEventDataResponseTask"},
        std::string_view{"CEventDataResponseTaskAgitated"},
        std::string_view{"CEventDataResponseTaskCombat"},
        std::string_view{"CEventDataResponseTaskCower"},
        std::string_view{"CEventDataResponseTaskCrouch"},
        std::string_view{"CEventDataResponseTaskDuckAndCover"},
        std::string_view{"CEventDataResponseTaskEscapeBlast"},
        std::string_view{"CEventDataResponseTaskEvasiveStep"},
        std::string_view{"CEventDataResponseTaskExhaustedFlee"},
        std::string_view{"CEventDataResponseTaskExplosion"},
        std::string_view{"CEventDataResponseTaskFlee"},
        std::string_view{"CEventDataResponseTaskFlyAway"},
        std::string_view{"CEventDataResponseTaskGrowlAndFlee"},
        std::string_view{"CEventDataResponseTaskGunAimedAt"},
        std::string_view{"CEventDataResponseTaskHandsUp"},
        std::string_view{"CEventDataResponseTaskHeadTrack"},
        std::string_view{"CEventDataResponseTaskLeaveCarAndFlee"},
        std::string_view{"CEventDataResponseTaskScenarioFlee"},
        std::string_view{"CEventDataResponseTaskSharkAttack"},
        std::string_view{"CEventDataResponseTaskShockingEventBackAway"},
        std::string_view{"CEventDataResponseTaskShockingEventGoto"},
        std::string_view{"CEventDataResponseTaskShockingEventHurryAway"},
        std::string_view{"CEventDataResponseTaskShockingEventReact"},
        std::string_view{"CEventDataResponseTaskShockingEventReactToAircraft"},
        std::string_view{"CEventDataResponseTaskShockingEventStopAndStare"},
        std::string_view{"CEventDataResponseTaskShockingEventThreatResponse"},
        std::string_view{"CEventDataResponseTaskShockingEventWatch"},
        std::string_view{"CEventDataResponseTaskShockingNiceCar"},
        std::string_view{"CEventDataResponseTaskShockingPoliceInvestigate"},
        std::string_view{"CEventDataResponseTaskThreat"},
        std::string_view{"CEventDataResponseTaskTurnToFace"},
        std::string_view{"CEventDataResponseTaskWalkAway"},
        std::string_view{"CEventDataResponseTaskWalkRoundEntity"},
        std::string_view{"CEventDataResponseTaskWalkRoundFire"},
        std::string_view{"CEventDeadPedFound"},
        std::string_view{"CEventDeath"},
        std::string_view{"CEventDecisionMakerResponse"},
        std::string_view{"CEventDisturbance"},
        std::string_view{"CEventDraggedOutCar"},
        std::string_view{"CEventEditableResponse"},
        std::string_view{"CEventEncroachingPed"},
        std::string_view{"CEventEntityDamaged"},
        std::string_view{"CEventEntityDestroyed"},
        std::string_view{"CEventExplosion"},
        std::string_view{"CEventExplosionHeard"},
        std::string_view{"CEventFireNearby"},
        std::string_view{"CEventFootStepHeard"},
        std::string_view{"CEventFriendlyAimedAt"},
        std::string_view{"CEventFriendlyFireNearMiss"},
        std::string_view{"CEventGetOutOfWater"},
        std::string_view{"CEventGivePedTask"},
        std::string_view{"CEventGroupScriptAI"},
        std::string_view{"CEventGroupScriptNetwork"},
        std::string_view{"CEventGunAimedAt"},
        std::string_view{"CEventGunShot"},
        std::string_view{"CEventGunShotBulletImpact"},
        std::string_view{"CEventGunShotWhizzedBy"},
        std::string_view{"CEventHelpAmbientFriend"},
        std::string_view{"CEventHurtTransition"},
        std::string_view{"CEventInAir"},
        std::string_view{"CEventInfo"},
        std::string_view{"CEventInfoBase"},
        std::string_view{"CEventInjuredCryForHelp"},
        std::string_view{"CEventLeaderEnteredCarAsDriver"},
        std::string_view{"CEventLeaderExitedCarAsDriver"},
        std::string_view{"CEventLeaderHolsteredWeapon"},
        std::string_view{"CEventLeaderLeftCover"},
        std::string_view{"CEventLeaderUnholsteredWeapon"},
        std::string_view{"CEventMeleeAction"},
        std::string_view{"CEventMustLeaveBoat"},
        std::string_view{"CEventNetworkAdminInvited"},
        std::string_view{"CEventNetworkAttemptHostMigration"},
        std::string_view{"CEventNetworkBail"},
        std::string_view{"CEventNetworkCashTransactionLog"},
        std::string_view{"CEventNetworkCheatTriggered"},
        std::string_view{"CEventNetworkClanInviteReceived"},
        std::string_view{"CEventNetworkClanJoined"},
        std::string_view{"CEventNetworkClanKicked"},
        std::string_view{"CEventNetworkClanLeft"},
        std::string_view{"CEventNetworkClanRankChanged"},
        std::string_view{"CEventNetworkCloudEvent"},
        std::string_view{"CEventNetworkCloudFileResponse"},
        std::string_view{"CEventNetworkEmailReceivedEvent"},
        std::string_view{"CEventNetworkEndMatch"},
        std::string_view{"CEventNetworkEndSession"},
        std::string_view{"CEventNetworkEntityDamage"},
        std::string_view{"CEventNetworkFindSession"},
        std::string_view{"CEventNetworkFollowInviteReceived"},
        std::string_view{"CEventNetworkHostMigration"},
        std::string_view{"CEventNetworkHostSession"},
        std::string_view{"CEventNetworkIncrementStat"},
        std::string_view{"CEventNetworkInviteAccepted"},
        std::string_view{"CEventNetworkInviteConfirmed"},
        std::string_view{"CEventNetworkInviteRejected"},
        std::string_view{"CEventNetworkJoinSession"},
        std::string_view{"CEventNetworkJoinSessionResponse"},
        std::string_view{"CEventNetworkOnlinePermissionsUpdated"},
        std::string_view{"CEventNetworkPedLeftBehind"},
        std::string_view{"CEventNetworkPickupRespawned"},
        std::string_view{"CEventNetworkPlayerArrest"},
        std::string_view{"CEventNetworkPlayerCollectedAmbientPickup"},
        std::string_view{"CEventNetworkPlayerCollectedPickup"},
        std::string_view{"CEventNetworkPlayerCollectedPortablePickup"},
        std::string_view{"CEventNetworkPlayerDroppedPortablePickup"},
        std::string_view{"CEventNetworkPlayerEnteredVehicle"},
        std::string_view{"CEventNetworkPlayerJoinScript"},
        std::string_view{"CEventNetworkPlayerLeftScript"},
        std::string_view{"CEventNetworkPlayerScript"},
        std::string_view{"CEventNetworkPlayerSession"},
        std::string_view{"CEventNetworkPlayerSpawn"},
        std::string_view{"CEventNetworkPresenceInvite"},
        std::string_view{"CEventNetworkPresenceInviteRemoved"},
        std::string_view{"CEventNetworkPresenceInviteReply"},
        std::string_view{"CEventNetworkPresenceTriggerEvent"},
        std::string_view{"CEventNetworkPresence_StatUpdate"},
        std::string_view{"CEventNetworkPrimaryClanChanged"},
        std::string_view{"CEventNetworkRequestDelay"},
        std::string_view{"CEventNetworkRosChanged"},
        std::string_view{"CEventNetworkScAdminPlayerUpdated"},
        std::string_view{"CEventNetworkScAdminReceivedCash"},
        std::string_view{"CEventNetworkScriptEvent"},
        std::string_view{"CEventNetworkSessionEvent"},
        std::string_view{"CEventNetworkShopTransaction"},
        std::string_view{"CEventNetworkSignInStateChanged"},
        std::string_view{"CEventNetworkSocialClubAccountLinked"},
        std::string_view{"CEventNetworkSpectateLocal"},
        std::string_view{"CEventNetworkStartMatch"},
        std::string_view{"CEventNetworkStartSession"},
        std::string_view{"CEventNetworkStorePlayerLeft"},
        std::string_view{"CEventNetworkSummon"},
        std::string_view{"CEventNetworkSystemServiceEvent"},
        std::string_view{"CEventNetworkTextMessageReceived"},
        std::string_view{"CEventNetworkTimedExplosion"},
        std::string_view{"CEventNetworkTransitionEvent"},
        std::string_view{"CEventNetworkTransitionGamerInstruction"},
        std::string_view{"CEventNetworkTransitionMemberJoined"},
        std::string_view{"CEventNetworkTransitionMemberLeft"},
        std::string_view{"CEventNetworkTransitionParameterChanged"},
        std::string_view{"CEventNetworkTransitionStarted"},
        std::string_view{"CEventNetworkTransitionStringChanged"},
        std::string_view{"CEventNetworkVehicleUndrivable"},
        std::string_view{"CEventNetworkVoiceConnectionRequested"},
        std::string_view{"CEventNetworkVoiceConnectionResponse"},
        std::string_view{"CEventNetworkVoiceConnectionTerminated"},
        std::string_view{"CEventNetworkVoiceSessionEnded"},
        std::string_view{"CEventNetworkVoiceSessionStarted"},
        std::string_view{"CEventNetworkWithData"},
        std::string_view{"CEventNetwork_InboxMsgReceived"},
        std::string_view{"CEventNewTask"},
        std::string_view{"CEventObjectCollision"},
        std::string_view{"CEventOnFire"},
        std::string_view{"CEventOpenDoor"},
        std::string_view{"CEventPedCollisionWithPed"},
        std::string_view{"CEventPedCollisionWithPlayer"},
        std::string_view{"CEventPedEnteredMyVehicle"},
        std::string_view{"CEventPedJackingMyVehicle"},
        std::string_view{"CEventPedOnCarRoof"},
        std::string_view{"CEventPedSeenDeadPed"},
        std::string_view{"CEventPlayerCollisionWithPed"},
        std::string_view{"CEventPlayerDeath"},
        std::string_view{"CEventPlayerUnableToEnterVehicle"},
        std::string_view{"CEventPotentialBeWalkedInto"},
        std::string_view{"CEventPotentialBlast"},
        std::string_view{"CEventPotentialGetRunOver"},
        std::string_view{"CEventPotentialWalkIntoVehicle"},
        std::string_view{"CEventProvidingCover"},
        std::string_view{"CEventRanOverPed"},
        std::string_view{"CEventReactionEnemyPed"},
        std::string_view{"CEventReactionInvestigateDeadPed"},
        std::string_view{"CEventReactionInvestigateThreat"},
        std::string_view{"CEventRequestHelp"},
        std::string_view{"CEventRequestHelpWithConfrontation"},
        std::string_view{"CEventRespondedToThreat"},
        std::string_view{"CEventScanner"},
        std::string_view{"CEventScenarioForceAction"},
        std::string_view{"CEventScriptCommand"},
        std::string_view{"CEventScriptWithData"},
        std::string_view{"CEventShocking"},
        std::string_view{"CEventShockingBicycleCrash"},
        std::string_view{"CEventShockingBicycleOnPavement"},
        std::string_view{"CEventShockingCarAlarm"},
        std::string_view{"CEventShockingCarChase"},
        std::string_view{"CEventShockingCarCrash"},
        std::string_view{"CEventShockingCarOnCar"},
        std::string_view{"CEventShockingCarPileUp"},
        std::string_view{"CEventShockingDangerousAnimal"},
        std::string_view{"CEventShockingDeadBody"},
        std::string_view{"CEventShockingDrivingOnPavement"},
        std::string_view{"CEventShockingEngineRevved"},
        std::string_view{"CEventShockingExplosion"},
        std::string_view{"CEventShockingFire"},
        std::string_view{"CEventShockingGunFight"},
        std::string_view{"CEventShockingGunshotFired"},
        std::string_view{"CEventShockingHelicopterOverhead"},
        std::string_view{"CEventShockingHornSounded"},
        std::string_view{"CEventShockingInDangerousVehicle"},
        std::string_view{"CEventShockingInjuredPed"},
        std::string_view{"CEventShockingMadDriver"},
        std::string_view{"CEventShockingMadDriverBicycle"},
        std::string_view{"CEventShockingMadDriverExtreme"},
        std::string_view{"CEventShockingMugging"},
        std::string_view{"CEventShockingNonViolentWeaponAimedAt"},
        std::string_view{"CEventShockingParachuterOverhead"},
        std::string_view{"CEventShockingPedKnockedIntoByPlayer"},
        std::string_view{"CEventShockingPedRunOver"},
        std::string_view{"CEventShockingPedShot"},
        std::string_view{"CEventShockingPlaneFlyby"},
        std::string_view{"CEventShockingPotentialBlast"},
        std::string_view{"CEventShockingPropertyDamage"},
        std::string_view{"CEventShockingRunningPed"},
        std::string_view{"CEventShockingRunningStampede"},
        std::string_view{"CEventShockingSeenCarStolen"},
        std::string_view{"CEventShockingSeenConfrontation"},
        std::string_view{"CEventShockingSeenGangFight"},
        std::string_view{"CEventShockingSeenInsult"},
        std::string_view{"CEventShockingSeenMeleeAction"},
        std::string_view{"CEventShockingSeenNiceCar"},
        std::string_view{"CEventShockingSeenPedKilled"},
        std::string_view{"CEventShockingSiren"},
        std::string_view{"CEventShockingStudioBomb"},
        std::string_view{"CEventShockingVehicleTowed"},
        std::string_view{"CEventShockingVisibleWeapon"},
        std::string_view{"CEventShockingWeaponThreat"},
        std::string_view{"CEventShockingWeirdPed"},
        std::string_view{"CEventShockingWeirdPedApproaching"},
        std::string_view{"CEventShoutBlockingLos"},
        std::string_view{"CEventShoutTargetPosition"},
        std::string_view{"CEventShovePed"},
        std::string_view{"CEventSoundBase"},
        std::string_view{"CEventStatChangedValue"},
        std::string_view{"CEventStaticCountReachedMax"},
        std::string_view{"CEventStuckInAir"},
        std::string_view{"CEventSuspiciousActivity"},
        std::string_view{"CEventSwitch2NM"},
        std::string_view{"CEventUnidentifiedPed"},
        std::string_view{"CEventVehicleCollision"},
        std::string_view{"CEventVehicleDamage"},
        std::string_view{"CEventVehicleDamageWeapon"},
        std::string_view{"CEventVehicleOnFire"},
        std::string_view{"CEventWrithe"}
    };

    struct CompleteObjectLocator64 final
    {
        std::uint32_t Signature;
        std::uint32_t Offset;
        std::uint32_t ConstructorDisplacementOffset;
        std::int32_t TypeDescriptorRva;
        std::int32_t ClassDescriptorRva;
        std::int32_t SelfRva;
    };


    struct DamageContext final
    {
        void* Victim{};
        void* Culprit{};
        std::uint32_t Weapon{};
        float BaseDamage{};
        std::int64_t CompletedAt{};
    };

    struct DamageMagnitude final
    {
        float Value{};
        DamageMagnitudeSource Source{DamageMagnitudeSource::Unavailable};
    };

    constexpr std::size_t DynamicProbeRecordCapacity = 4096;
    constexpr std::size_t DynamicProbeWalkerHistoryCapacity = 4096;
    constexpr std::size_t DynamicProbePendingThreadCapacity = 64;
    constexpr std::size_t DynamicProbeEventIdentityCapacity = 128;
    constexpr std::size_t DynamicProbeWalkerSiteCapacity = EnhancedDiagnosticCandidateCapacity;
    constexpr std::size_t DynamicProbePageCapacity =
        EnhancedVirtualSlotCallsiteDiagnosticCapacity * 2 +
        DynamicProbeWalkerSiteCapacity;
    constexpr std::uint32_t DynamicProbeEventTypeSlotOffset = 0x48;
    constexpr std::uint32_t DynamicProbeEventObjectFlowRelation = 1u << 8;
    constexpr std::uint32_t DynamicProbeUnknownEventType = 0xFFFFFFFFu;
    constexpr std::uint64_t DynamicProbeCorrelationWindowMs = 500;
    constexpr std::size_t DynamicProbeCorrelationHistoryLimit = 1024;
    constexpr std::size_t DynamicProbeCorrelationMatchCapacity = 64;
    constexpr std::uint8_t DynamicProbeBreakpoint = 0xCC;
    constexpr int DynamicProbeToggleKey = VK_F10;
    constexpr wchar_t DynamicProbeLogName[] = L"CEventGenerator.log";

    enum class DynamicProbeRecordKind : std::uint32_t
    {
        WalkerEntry = 1,
        VirtualCall = 2
    };

    struct DynamicProbeVirtualSite final
    {
        std::byte* Pre{};
        std::byte* Post{};
        std::uint8_t PreOriginal{};
        std::uint8_t PostOriginal{};
        std::uint32_t WalkerRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t SlotOffset{};
        std::atomic<std::uint64_t> PreHits{};
        std::atomic<std::uint64_t> PostHits{};
    };

    struct DynamicProbeWalkerSite final
    {
        std::byte* Pre{};
        std::byte* Post{};
        std::uint8_t PreOriginal{};
        std::uint8_t PostOriginal{};
        std::uint32_t WalkerRva{};
        std::atomic<std::uint64_t> PreHits{};
        std::atomic<std::uint64_t> PostHits{};
    };

    struct DynamicProbePage final
    {
        void* Base{};
        std::size_t Size{};
        DWORD OriginalProtect{};
    };

    struct DynamicProbePendingThread final
    {
        std::atomic<DWORD> ThreadId{};
        std::atomic<std::uint32_t> VirtualSiteIndex{0xFFFFFFFFu};
        std::atomic<std::uint64_t> Event{};
        std::atomic<std::uint64_t> Vtable{};
        std::atomic<std::uint64_t> Target{};
        std::atomic<std::uint64_t> Rcx{};
        std::atomic<std::uint64_t> Rdx{};
        std::atomic<std::uint64_t> R8{};
        std::atomic<std::uint64_t> R9{};
        std::atomic<std::uint64_t> Rsp{};
        std::atomic<std::uint64_t> ReturnAddress{};
    };

    struct DynamicProbeEventIdentity final
    {
        std::atomic<std::uint64_t> Event{};
        std::atomic<std::uint32_t> EventType{DynamicProbeUnknownEventType};
        std::atomic<std::uint32_t> ThreadId{};
        std::atomic<std::uint64_t> Tick{};
    };

    struct DynamicProbeWalkerHistoryEntry final
    {
        std::atomic<std::uint64_t> Sequence{};
        std::atomic<std::uint64_t> Tick{};
        std::atomic<std::uint32_t> ThreadId{};
        std::atomic<std::uint32_t> WalkerRva{};
        std::atomic<std::uint64_t> Rcx{};
        std::atomic<std::uint64_t> Rdx{};
        std::atomic<std::uint64_t> R8{};
        std::atomic<std::uint64_t> R9{};
        std::atomic<std::uint64_t> Rsp{};
        std::atomic<std::uint64_t> ReturnAddress{};
    };

    struct DynamicProbeRecord final
    {
        std::atomic<std::uint64_t> Sequence{};
        std::atomic<std::uint64_t> Tick{};
        std::atomic<std::uint32_t> ThreadId{};
        std::atomic<std::uint32_t> Kind{};
        std::atomic<std::uint32_t> WalkerRva{};
        std::atomic<std::uint32_t> CallsiteRva{};
        std::atomic<std::uint32_t> SlotOffset{};
        std::atomic<std::uint32_t> EventType{DynamicProbeUnknownEventType};
        std::atomic<std::uint32_t> EventArgumentMask{};
        std::atomic<std::uint64_t> Event{};
        std::atomic<std::uint64_t> Vtable{};
        std::atomic<std::uint64_t> Target{};
        std::atomic<std::uint64_t> Rcx{};
        std::atomic<std::uint64_t> Rdx{};
        std::atomic<std::uint64_t> R8{};
        std::atomic<std::uint64_t> R9{};
        std::atomic<std::uint64_t> Rsp{};
        std::atomic<std::uint64_t> ReturnAddress{};
        std::atomic<std::uint64_t> ReturnValue{};
    };

    std::atomic<bool> g_dynamicProbeArmed{};
    std::atomic<bool> g_dynamicProbeActive{};
    std::atomic<std::uint32_t> g_dynamicProbeVirtualSiteCount{};
    std::atomic<std::uint32_t> g_dynamicProbeWalkerSiteCount{};
    std::atomic<std::uint32_t> g_dynamicProbeSessionCount{};
    std::atomic<std::uint64_t> g_dynamicProbeHitCount{};
    std::atomic<std::uint64_t> g_dynamicProbeRecordSequence{};
    std::atomic<std::uint64_t> g_dynamicProbeWalkerHistorySequence{};
    std::atomic<std::uint64_t> g_dynamicProbeSessionStartTick{};
    std::array<
        DynamicProbeVirtualSite,
        EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_dynamicProbeVirtualSites{};
    std::array<
        DynamicProbeWalkerSite,
        DynamicProbeWalkerSiteCapacity> g_dynamicProbeWalkerSites{};
    std::array<DynamicProbePage, DynamicProbePageCapacity> g_dynamicProbePages{};
    std::atomic<std::uint32_t> g_dynamicProbePageCount{};
    std::array<
        DynamicProbePendingThread,
        DynamicProbePendingThreadCapacity> g_dynamicProbePendingThreads{};
    std::array<
        DynamicProbeEventIdentity,
        DynamicProbeEventIdentityCapacity> g_dynamicProbeEventIdentities{};
    std::array<DynamicProbeRecord, DynamicProbeRecordCapacity> g_dynamicProbeRecords{};
    std::array<
        DynamicProbeWalkerHistoryEntry,
        DynamicProbeWalkerHistoryCapacity> g_dynamicProbeWalkerHistory{};
    std::atomic<void*> g_dynamicProbeHandler{};
    std::array<wchar_t, MAX_PATH> g_dynamicProbeLogPath{};
    bool g_dynamicProbeToggleDown{};

    std::atomic<Status> g_status{Status::Uninitialized};
    std::atomic<GameEdition> g_gameEdition{GameEdition::Unknown};
    std::atomic<std::uint32_t> g_gameBuild{};
    std::atomic<LocatorFailure> g_locatorFailure{LocatorFailure::None};
    std::atomic<std::uint32_t> g_pedMatchCount{};
    std::atomic<std::uint32_t> g_globalMatchCount{};
    std::atomic<std::uint32_t> g_damageMatchCount{};
    std::atomic<std::uint32_t> g_cEventCountAnchorCount{};
    std::atomic<std::uint32_t> g_cEventStackAnchorCount{};
    std::atomic<std::uint32_t> g_eventTypeAnchorCount{};
    std::atomic<std::uint32_t> g_cEventCountOffset{};
    std::atomic<std::uint32_t> g_cEventStackOffset{};
    std::atomic<std::uint32_t> g_eventTypeVFuncOffset{};
    std::atomic<std::uint32_t> g_eventIdVFuncOffset{};
    std::atomic<std::uint32_t> g_eventArgumentsVFuncOffset{};
    std::atomic<std::uint32_t> g_eventEntityVFuncOffset{};
    std::atomic<std::uint32_t> g_cEventStackFunctionMatchCount{};
    std::atomic<std::uint32_t> g_cEventStackAnchorRva{};
    std::atomic<std::uint32_t> g_cEventCountFunctionRva{};
    std::atomic<std::uint32_t> g_cEventCountFunctionSize{};
    std::atomic<std::uint32_t> g_eventTypeFunctionRva{};
    std::atomic<std::uint32_t> g_eventTypeFunctionSize{};
    std::atomic<std::uint32_t> g_eventWalkerCandidateCount{};
    std::atomic<std::uint32_t> g_eventWalkerCandidateRva{};
    std::atomic<std::uint32_t> g_eventWalkerCandidateSize{};
    std::atomic<std::uint32_t> g_typedEventWalkerCandidateCount{};
    std::atomic<std::uint32_t> g_typedEventWalkerCandidateRva{};
    std::atomic<std::uint32_t> g_typedEventWalkerCandidateSize{};
    std::atomic<std::uint32_t> g_directCountCallerCount{};
    std::atomic<std::uint32_t> g_directEventTypeCallerCount{};
    std::atomic<std::uint32_t> g_relatedEventWalkerCandidateCount{};
    std::atomic<std::uint32_t> g_guardFlags{};
    std::atomic<std::uint32_t> g_guardCFFunctionCount{};
    std::atomic<std::uint32_t> g_guardLongJumpTargetCount{};
    std::atomic<std::uint32_t> g_guardAddressTakenIatCount{};
    std::atomic<std::uint32_t> g_enhancedCandidateDiagnosticCount{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDiagnosticCandidateCapacity> g_enhancedCandidateRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDiagnosticCandidateCapacity> g_enhancedCandidateSizes{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDiagnosticCandidateCapacity> g_enhancedCandidateRelations{};
    std::atomic<std::uint32_t> g_dispatchCallsiteCandidateCount{};
    std::atomic<std::uint32_t> g_dispatchCallsiteDiagnosticCount{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchCallsiteDiagnosticCapacity> g_dispatchCallsiteWalkerRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchCallsiteDiagnosticCapacity> g_dispatchCallsiteRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchCallsiteDiagnosticCapacity> g_dispatchCallsiteCalleeRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchCallsiteDiagnosticCapacity> g_dispatchCallsiteRelations{};
    std::atomic<std::uint32_t> g_dispatchCalleeCandidateCount{};
    std::atomic<std::uint32_t> g_dispatchCalleeDiagnosticCount{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchCalleeDiagnosticCapacity> g_dispatchCalleeRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchCalleeDiagnosticCapacity> g_dispatchCalleeSizes{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchCalleeDiagnosticCapacity> g_dispatchCalleeRelations{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchCalleeDiagnosticCapacity> g_dispatchCalleeCallsiteCounts{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchCalleeDiagnosticCapacity> g_dispatchCalleeWalkerCounts{};
    std::atomic<std::uint32_t> g_dispatchForwardCallsiteCandidateCount{};
    std::atomic<std::uint32_t> g_dispatchForwardCallsiteDiagnosticCount{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCallsiteDiagnosticCapacity> g_dispatchForwardCallsiteParentRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCallsiteDiagnosticCapacity> g_dispatchForwardCallsiteRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCallsiteDiagnosticCapacity> g_dispatchForwardCallsiteCalleeRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCallsiteDiagnosticCapacity> g_dispatchForwardCallsiteEventArgumentMasks{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCallsiteDiagnosticCapacity> g_dispatchForwardCallsiteGroupArgumentMasks{};
    std::atomic<std::uint32_t> g_dispatchForwardCalleeCandidateCount{};
    std::atomic<std::uint32_t> g_dispatchForwardCalleeDiagnosticCount{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCalleeDiagnosticCapacity> g_dispatchForwardCalleeRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCalleeDiagnosticCapacity> g_dispatchForwardCalleeSizes{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCalleeDiagnosticCapacity> g_dispatchForwardCalleeRelations{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCalleeDiagnosticCapacity> g_dispatchForwardCalleeCallsiteCounts{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCalleeDiagnosticCapacity> g_dispatchForwardCalleeParentCounts{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCalleeDiagnosticCapacity> g_dispatchForwardCalleeEventArgumentMasks{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedDispatchForwardCalleeDiagnosticCapacity> g_dispatchForwardCalleeGroupArgumentMasks{};
    std::atomic<std::uint32_t> g_eventConsumerCallsiteCandidateCount{};
    std::atomic<std::uint32_t> g_eventConsumerCallsiteDiagnosticCount{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerCallsiteDiagnosticCapacity> g_eventConsumerCallsiteWalkerRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerCallsiteDiagnosticCapacity> g_eventConsumerCallsiteRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerCallsiteDiagnosticCapacity> g_eventConsumerCallsiteCalleeRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerCallsiteDiagnosticCapacity> g_eventConsumerCallsiteEventArgumentMasks{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerCallsiteDiagnosticCapacity> g_eventConsumerCallsiteRelations{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerCallsiteDiagnosticCapacity> g_eventConsumerCallsiteInstructionLengths{};
    std::array<std::array<std::atomic<std::uint32_t>, 4>, EnhancedEventConsumerCallsiteDiagnosticCapacity> g_eventConsumerCallsiteEncodings{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerCallsiteDiagnosticCapacity> g_eventConsumerCallsiteOperandInfos{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerCallsiteDiagnosticCapacity> g_eventConsumerCallsiteDisplacements{};
    std::atomic<std::uint32_t> g_eventConsumerCandidateCount{};
    std::atomic<std::uint32_t> g_eventConsumerDiagnosticCount{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerSizes{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerRelations{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerArgumentMasks{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerFirstUseRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerFirstClobberRvas{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerCallsiteCounts{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerWalkerCounts{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerReadCounts{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerMemoryStoreCounts{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerCompareCounts{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerVtableLoadCounts{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerForwardCounts{};
    std::array<
        std::atomic<std::uint32_t>,
        EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailForwardCounts{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailInstructionRvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailInstructionLengths{};
    std::array<std::array<std::atomic<std::uint32_t>, 4>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailEncodings{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailOperandInfos{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailDisplacements{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailBaseRegisters{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailIndexRegisters{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailScales{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailImageSizes{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailSlotRvas{};
    std::array<std::atomic<std::uint64_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailSlotAddresses{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailSlotProtections{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailSlotFlags{};
    std::array<std::atomic<std::uint64_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailTargetAddresses{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailTargetProtections{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailTargetFlags{};
    std::array<std::atomic<std::uint64_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailTargetModuleBases{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailTargetModuleSizes{};
    std::array<std::atomic<std::uint32_t>, EnhancedEventConsumerDiagnosticCapacity> g_eventConsumerTailTargetModuleRvas{};
    std::atomic<std::uint32_t> g_virtualSlotCandidateCount{};
    std::atomic<std::uint32_t> g_virtualSlotDiagnosticCount{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotDiagnosticCapacity> g_virtualSlotOffsets{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotDiagnosticCapacity> g_virtualSlotRelations{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotDiagnosticCapacity> g_virtualSlotCallsiteCounts{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotDiagnosticCapacity> g_virtualSlotWalkerCounts{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotDiagnosticCapacity> g_virtualSlotEventRcxCounts{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotDiagnosticCapacity> g_virtualSlotEventArgumentMasks{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotDiagnosticCapacity> g_virtualSlotReturnRelations{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotDiagnosticCapacity> g_virtualSlotReturnUseCounts{};
    std::atomic<std::uint32_t> g_virtualSlotCallsiteCandidateCount{};
    std::atomic<std::uint32_t> g_virtualSlotCallsiteDiagnosticCount{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteWalkerRvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteRvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteOffsets{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteRelations{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteEventArgumentMasks{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteReturnRelations{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteReturnFirstUseRvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteReturnFirstClobberRvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsitePreparedArgumentMasks{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteMemoryArgumentMasks{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteAddressArgumentMasks{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteImmediateArgumentMasks{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity> g_virtualSlotCallsiteZeroArgumentMasks{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity * 3> g_virtualSlotCallsiteArgumentSourceRvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity * 3> g_virtualSlotCallsiteArgumentSourceKinds{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity * 3> g_virtualSlotCallsiteArgumentSourceEncodings{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity * 3> g_virtualSlotCallsiteArgumentSourceOperandInfos{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity * 3> g_virtualSlotCallsiteArgumentSourceDisplacements{};
    std::array<std::atomic<std::uint32_t>, EnhancedVirtualSlotCallsiteDiagnosticCapacity * 3> g_virtualSlotCallsiteArgumentSourceValues{};
    std::atomic<std::uint32_t> g_rttiEventTypeCandidateCount{};
    std::atomic<std::uint32_t> g_rttiEventTypeDiagnosticCount{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeNameRvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeTypeDescriptorRvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeCompleteObjectLocatorRvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeVftableRvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeValues{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeSlot30Rvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeSlot48Rvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeSlot50Rvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeSlot60Rvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeSlot68Rvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeSlotB0Rvas{};
    std::array<std::atomic<std::uint32_t>, EnhancedRttiEventTypeDiagnosticCapacity> g_rttiEventTypeSlotF8Rvas{};
    std::atomic<std::uint64_t> g_sequence{};
    std::atomic<std::uint64_t> g_performanceFrequency{};
    std::array<std::atomic<std::uint32_t>, DetailedEventIdCapacity> g_detailedEventIds{};
    std::array<std::atomic<std::uint8_t>, CaptureEventIdCapacity> g_captureStreamMasks{};
    std::array<std::atomic<std::uint8_t>, CatalogEventNames.size()> g_catalogCaptureStreamMasks{};
    std::array<std::atomic<std::uintptr_t>, CatalogVtableCacheCapacity> g_catalogVtableKeys{};
    std::array<std::atomic<std::uint32_t>, CatalogVtableCacheCapacity> g_catalogVtableIds{};
    std::atomic<bool> g_catalogCaptureEnabled{};
    std::array<void*, 3> g_targets{};
    std::array<EventDispatch, 3> g_originals{};
    void* g_damageTarget{};
    DamageDispatch g_originalDamage{};
    void* g_damageProcessTarget{};
    DamageProcessDispatch g_originalDamageProcess{};
    thread_local std::array<DamageContext, RecentDamageContextCapacity> g_recentDamageContexts{};
    thread_local std::size_t g_recentDamageContextCount{};
    std::atomic<std::uint64_t> g_damageObserved{};
    std::atomic<std::uint64_t> g_meleeActionObserved{};
    std::atomic<std::uint64_t> g_shockingGunshotObserved{};
    std::atomic<std::uint64_t> g_visibleWeaponObserved{};
    std::atomic<std::uint64_t> g_gunShotObserved{};
    std::atomic<std::uint64_t> g_bulletImpactObserved{};
    std::atomic<std::uint64_t> g_whizzedByObserved{};
    std::atomic<std::uint64_t> g_entityDamagedObserved{};
    std::atomic<std::uint64_t> g_damageMetadataObserved{};

    void InitializeDetailedIds() noexcept
    {
        for (auto& value : g_detailedEventIds)
        {
            value.store(EmptyDetailedEventId, std::memory_order_relaxed);
        }
    }

    [[nodiscard]] bool IsDetailedEventId(std::uint32_t eventId) noexcept
    {
        for (const auto& value : g_detailedEventIds)
        {
            if (value.load(std::memory_order_relaxed) == eventId)
            {
                return true;
            }
        }
        return false;
    }

    void InitializeCaptureStreamMasks() noexcept
    {
        for (auto& value : g_captureStreamMasks)
        {
            value.store(0, std::memory_order_relaxed);
        }
    }

    void InitializeCatalogCaptureStreamMasks() noexcept
    {
        for (auto& value : g_catalogCaptureStreamMasks)
        {
            value.store(0, std::memory_order_relaxed);
        }
        for (auto& value : g_catalogVtableKeys)
        {
            value.store(0, std::memory_order_relaxed);
        }
        for (auto& value : g_catalogVtableIds)
        {
            value.store(0, std::memory_order_relaxed);
        }
        g_catalogCaptureEnabled.store(false, std::memory_order_relaxed);
    }


    [[nodiscard]] bool TryGetCatalogEventIndex(
        std::uint32_t catalogEventId,
        std::size_t& index) noexcept
    {
        if (catalogEventId == 0 || catalogEventId > CatalogEventNames.size())
        {
            return false;
        }

        index = static_cast<std::size_t>(catalogEventId - 1u);
        return true;
    }

    [[nodiscard]] std::uint32_t ResolveCatalogEventId(
        void** vtable) noexcept
    {
#if defined(_M_X64) || defined(__x86_64__)
        if (vtable == nullptr)
        {
            return CatalogEventCacheMiss;
        }

        __try
        {
            const auto* locator =
                reinterpret_cast<const CompleteObjectLocator64*>(vtable[-1]);
            if (locator == nullptr || locator->Signature != 1u ||
                locator->TypeDescriptorRva <= 0 || locator->SelfRva <= 0)
            {
                return CatalogEventCacheMiss;
            }

            const auto* imageBase =
                reinterpret_cast<const std::byte*>(locator) - locator->SelfRva;
            const auto* typeDescriptor =
                imageBase + locator->TypeDescriptorRva;
            const char* typeName = reinterpret_cast<const char*>(
                typeDescriptor + sizeof(void*) * 2u);
            if (typeName == nullptr)
            {
                return CatalogEventCacheMiss;
            }

            const char* eventName = std::strstr(typeName, "CEvent");
            if (eventName == nullptr)
            {
                return CatalogEventCacheMiss;
            }

            const char* end = std::strchr(eventName, '@');
            if (end == nullptr || end == eventName)
            {
                return CatalogEventCacheMiss;
            }

            const std::string_view candidate(
                eventName,
                static_cast<std::size_t>(end - eventName));
            const auto found = std::ranges::lower_bound(
                CatalogEventNames,
                candidate);
            if (found == CatalogEventNames.end() || *found != candidate)
            {
                return CatalogEventCacheMiss;
            }

            return static_cast<std::uint32_t>(
                std::ranges::distance(CatalogEventNames.begin(), found) + 1);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return CatalogEventCacheMiss;
        }
#endif
        return CatalogEventCacheMiss;
    }

    [[nodiscard]] std::uint32_t ResolveCachedCatalogEventId(
        void** vtable) noexcept
    {
        if (vtable == nullptr)
        {
            return CatalogEventCacheMiss;
        }

        const std::uintptr_t key = reinterpret_cast<std::uintptr_t>(vtable);
        const std::size_t slot =
            static_cast<std::size_t>((key >> 4u) & (CatalogVtableCacheCapacity - 1u));
        const std::uintptr_t cachedKey =
            g_catalogVtableKeys[slot].load(std::memory_order_acquire);
        if (cachedKey == key)
        {
            return g_catalogVtableIds[slot].load(std::memory_order_acquire);
        }

        const std::uint32_t resolved = ResolveCatalogEventId(vtable);
        g_catalogVtableIds[slot].store(resolved, std::memory_order_release);
        g_catalogVtableKeys[slot].store(key, std::memory_order_release);
        return resolved;
    }


    [[nodiscard]] constexpr std::uint8_t StreamMask(EventStream stream) noexcept
    {
        switch (stream)
        {
        case EventStream::Reaction:
            return ReactionStreamMask;
        case EventStream::Group:
            return GroupStreamMask;
        case EventStream::Global:
            return GlobalStreamMask;
        default:
            return 0;
        }
    }

    [[nodiscard]] bool ShouldCaptureEvent(
        std::uint32_t eventId,
        EventStream stream) noexcept
    {
        if (eventId >= g_captureStreamMasks.size())
        {
            return false;
        }

        const std::uint8_t mask =
            g_captureStreamMasks[eventId].load(std::memory_order_relaxed);
        return (mask & StreamMask(stream)) != 0;
    }

    [[nodiscard]] bool ShouldCaptureCatalogEvent(
        void** vtable,
        EventStream stream,
        std::uint32_t& catalogEventId) noexcept
    {
        catalogEventId = 0;
        if (!g_catalogCaptureEnabled.load(std::memory_order_relaxed))
        {
            return false;
        }

        const std::uint32_t resolved = ResolveCachedCatalogEventId(vtable);
        if (resolved == CatalogEventCacheMiss)
        {
            return false;
        }

        std::size_t index = 0;
        if (!TryGetCatalogEventIndex(resolved, index))
        {
            return false;
        }

        const std::uint8_t mask =
            g_catalogCaptureStreamMasks[index].load(std::memory_order_relaxed);
        if ((mask & StreamMask(stream)) == 0)
        {
            return false;
        }

        catalogEventId = resolved;
        return true;
    }

    [[nodiscard]] std::atomic<std::uint64_t>* ObservedCounter(
        std::uint32_t eventId) noexcept
    {
        switch (eventId)
        {
        case 17:
            return &g_damageObserved;
        case 42:
            return &g_meleeActionObserved;
        case 91:
            return &g_shockingGunshotObserved;
        case 123:
            return &g_visibleWeaponObserved;
        case 124:
            return &g_gunShotObserved;
        case 125:
            return &g_bulletImpactObserved;
        case 126:
            return &g_whizzedByObserved;
        case 141:
            return &g_entityDamagedObserved;
        case EntityDamageMetadataEventId:
            return &g_damageMetadataObserved;
        default:
            return nullptr;
        }
    }

    void CountObserved(std::uint32_t eventId) noexcept
    {
        if (auto* counter = ObservedCounter(eventId); counter != nullptr)
        {
            counter->fetch_add(1, std::memory_order_relaxed);
        }
    }

    void StampRecord(EventRecord& record, EventStream stream) noexcept
    {
        record.Size = sizeof(EventRecord);
        record.Sequence = g_sequence.fetch_add(1, std::memory_order_relaxed) + 1;
        record.GameBuild = g_gameBuild.load(std::memory_order_relaxed);
        record.Stream = stream;

        LARGE_INTEGER counter{};
        if (QueryPerformanceCounter(&counter) != FALSE)
        {
            record.PerformanceCounter = counter.QuadPart;
        }
    }

    void CaptureDispatchContext(
        EventRecord& record,
        EventStream stream,
        void* group) noexcept
    {
        if (group == nullptr)
        {
            return;
        }

        __try
        {
            if (stream == EventStream::Reaction)
            {
                auto* bytes = static_cast<std::byte*>(group);
                void* entity = nullptr;

                if (record.EventId == 29)
                {
                    constexpr std::ptrdiff_t PrimaryEntityOffset = 0x98;
                    constexpr std::ptrdiff_t MirrorEntityOffset = 0xA0;
                    void* primary =
                        *reinterpret_cast<void**>(bytes + PrimaryEntityOffset);
                    void* mirror =
                        *reinterpret_cast<void**>(bytes + MirrorEntityOffset);
                    const auto primaryAddress =
                        reinterpret_cast<std::uintptr_t>(primary);
                    if (primary != nullptr &&
                        primary == mirror &&
                        primaryAddress >= 0x10000)
                    {
                        entity = primary;
                    }
                }

                if (entity == nullptr)
                {
                    constexpr std::ptrdiff_t EntityOffset = 0x90;
                    void* candidate =
                        *reinterpret_cast<void**>(bytes + EntityOffset);
                    if (reinterpret_cast<std::uintptr_t>(candidate) >= 0x10000)
                    {
                        entity = candidate;
                    }
                }

                if (entity != nullptr)
                {
                    record.DispatchEntityAddress =
                        reinterpret_cast<std::uint64_t>(entity);
                    record.DispatchEntityCount = 1;
                    record.Flags =
                        record.Flags | RecordFlags::DispatchEntityAddress;
                }
                return;
            }

            if (stream == EventStream::Group)
            {
                constexpr std::ptrdiff_t EventCountOffset = 0x100;
                constexpr std::uint32_t MaximumEventCount = 32;
                auto* bytes = static_cast<std::byte*>(group);
                const std::uint32_t count =
                    *reinterpret_cast<std::uint32_t*>(bytes + EventCountOffset);
                if (count <= MaximumEventCount)
                {
                    record.DispatchEntityCount = count;
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            record.DispatchEntityAddress = 0;
            record.DispatchEntityCount = 0;
        }
    }

    [[nodiscard]] std::size_t CaptureProbeBytes(
        std::byte* destination,
        std::size_t destinationCapacity,
        const std::byte* source,
        std::size_t requested) noexcept
    {
        if (destination == nullptr ||
            destinationCapacity == 0 ||
            source == nullptr ||
            requested == 0)
        {
            return 0;
        }

        const std::size_t limit = std::min(destinationCapacity, requested);
        std::size_t captured = 0;
        while (captured < limit)
        {
            const std::size_t chunk = std::min<std::size_t>(
                limit - captured,
                sizeof(std::uint64_t));
            bool readable = true;
            __try
            {
                std::memcpy(
                    destination + captured,
                    source + captured,
                    chunk);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                readable = false;
            }

            if (!readable)
            {
                break;
            }

            captured += chunk;
        }

        return captured;
    }

    void CaptureDetailedProbe(
        EventRecord& record,
        void* group,
        void* event) noexcept
    {
        if (event == nullptr ||
            (record.EventId != 17 &&
             record.EventId != 29 &&
             record.EventId != 42 &&
             record.EventId != 124 &&
             record.EventId != 125 &&
             record.EventId != 141 &&
             record.CatalogEventId == 0))
        {
            return;
        }

        std::size_t extent = 0;

        if (record.EventId == 29 ||
            record.CatalogEventId != 0)
        {
            const auto* eventSource =
                static_cast<const std::byte*>(event) + sizeof(void*);

            const std::size_t eventCaptured = CaptureProbeBytes(
                record.ProbeData.data(),
                record.ProbeData.size(),
                eventSource,
                AimEventProbeCapacity);

            extent = eventCaptured;

            if (group != nullptr &&
                record.ProbeData.size() > AimEventProbeCapacity)
            {
                const std::size_t groupCaptured = CaptureProbeBytes(
                    record.ProbeData.data() + AimEventProbeCapacity,
                    record.ProbeData.size() - AimEventProbeCapacity,
                    static_cast<const std::byte*>(group),
                    AimGroupProbeCapacity);

                if (groupCaptured != 0)
                {
                    extent = AimEventProbeCapacity + groupCaptured;
                }
            }
        }
        else
        {
            const auto* source =
                static_cast<const std::byte*>(event) + sizeof(void*);

            extent = CaptureProbeBytes(
                record.ProbeData.data(),
                record.ProbeData.size(),
                source,
                DetailedEventProbeCapacity);
        }

        if (extent != 0)
        {
            record.ProbeSize = static_cast<std::uint32_t>(extent);
            record.Flags = record.Flags | RecordFlags::ProbeData;
        }
    }

    void CaptureEvent(EventStream stream, void* group, void* event) noexcept
    {
        if (event == nullptr || GetStatus() != Status::Ready)
        {
            return;
        }

        EventRecord record{};
        StampRecord(record, stream);
        record.GroupAddress = reinterpret_cast<std::uint64_t>(group);
        record.EventAddress = reinterpret_cast<std::uint64_t>(event);

        __try
        {
            auto** vtable = *reinterpret_cast<void***>(event);
            if (vtable == nullptr)
            {
                return;
            }

            const std::uint32_t idOffset =
                g_eventIdVFuncOffset.load(std::memory_order_relaxed);
            const std::uint32_t argumentsOffset =
                g_eventArgumentsVFuncOffset.load(std::memory_order_relaxed);
            const std::uint32_t entityOffset =
                g_eventEntityVFuncOffset.load(std::memory_order_relaxed);
            if (idOffset == 0 || argumentsOffset == 0 || entityOffset == 0 ||
                idOffset % sizeof(void*) != 0 ||
                argumentsOffset % sizeof(void*) != 0 ||
                entityOffset % sizeof(void*) != 0)
            {
                return;
            }

            const auto getId = reinterpret_cast<GetEventId>(
                vtable[idOffset / sizeof(void*)]);
            const auto getArguments = reinterpret_cast<GetEventArguments>(
                vtable[argumentsOffset / sizeof(void*)]);
            const auto getEntity = reinterpret_cast<GetEventEntity>(
                vtable[entityOffset / sizeof(void*)]);

            if (getId == nullptr || getArguments == nullptr)
            {
                return;
            }

            const std::uint32_t actualEventId =
                static_cast<std::uint32_t>(getId(event));
            record.EventId = actualEventId;
            CountObserved(actualEventId);

            const bool directCapture = ShouldCaptureEvent(actualEventId, stream);
            std::uint32_t catalogEventId = 0;
            const bool catalogCapture = ShouldCaptureCatalogEvent(
                vtable,
                stream,
                catalogEventId);
            if (!directCapture && !catalogCapture)
            {
                return;
            }

            if (catalogEventId == 0 && directCapture)
            {
                const std::uint32_t resolved = ResolveCachedCatalogEventId(vtable);
                if (resolved != CatalogEventCacheMiss)
                {
                    catalogEventId = resolved;
                }
            }

            record.CatalogEventId = catalogEventId;

            const bool detailedCapture =
                IsDetailedEventId(actualEventId) || catalogCapture;
            if (detailedCapture)
            {
                CaptureDispatchContext(record, stream, group);
            }
            if (getEntity != nullptr)
            {
                if (void* entity = getEntity(event); entity != nullptr)
                {
                    record.RelatedEntityAddress =
                        reinterpret_cast<std::uint64_t>(entity);
                    record.Flags =
                        record.Flags | RecordFlags::RelatedEntityAddress;
                }
            }

            if (detailedCapture)
            {
                for (std::uint32_t count = 0; count <= ArgumentCapacity; ++count)
                {
                    if (!getArguments(
                            event,
                            record.Arguments.data(),
                            static_cast<std::size_t>(count) *
                                sizeof(std::uint64_t)))
                    {
                        continue;
                    }

                    record.ArgumentCount = count;
                    record.Flags = record.Flags | RecordFlags::Arguments;
                    break;
                }
            }

            CaptureDetailedProbe(record, group, event);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return;
        }

        static_cast<void>(EventQueue::Instance().TryPush(record));
    }

    [[nodiscard]] bool TryCaptureDamageContext(
        DamageProcessData* damage,
        void* victim,
        DamageContext& context) noexcept
    {
        if (damage == nullptr || victim == nullptr)
        {
            return false;
        }

        __try
        {
            context = {
                victim,
                damage->Culprit,
                damage->Weapon,
                damage->BaseDamage
            };
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }

        return std::isfinite(context.BaseDamage) && context.BaseDamage >= 0.0f;
    }

    [[nodiscard]] std::int64_t CurrentPerformanceCounter() noexcept
    {
        LARGE_INTEGER counter{};
        return QueryPerformanceCounter(&counter) != FALSE
            ? counter.QuadPart
            : 0;
    }

    void PurgeRecentDamageContexts(std::int64_t now) noexcept
    {
        if (g_recentDamageContextCount == 0)
        {
            return;
        }

        const std::uint64_t frequency =
            g_performanceFrequency.load(std::memory_order_relaxed);
        if (now <= 0 || frequency == 0)
        {
            return;
        }

        const std::uint64_t lifetime = std::max<std::uint64_t>(1, frequency / 10);
        std::size_t write = 0;
        for (std::size_t read = 0; read < g_recentDamageContextCount; ++read)
        {
            const DamageContext& context = g_recentDamageContexts[read];
            const bool expired =
                context.CompletedAt > 0 &&
                now >= context.CompletedAt &&
                static_cast<std::uint64_t>(now - context.CompletedAt) > lifetime;
            if (!expired)
            {
                if (write != read)
                {
                    g_recentDamageContexts[write] = context;
                }
                ++write;
            }
        }

        for (std::size_t index = write;
             index < g_recentDamageContextCount;
             ++index)
        {
            g_recentDamageContexts[index] = {};
        }
        g_recentDamageContextCount = write;
    }

    void RememberRecentDamageContext(DamageContext context) noexcept
    {
        context.CompletedAt = CurrentPerformanceCounter();
        PurgeRecentDamageContexts(context.CompletedAt);

        if (g_recentDamageContextCount == g_recentDamageContexts.size())
        {
            std::ranges::shift_left(
                g_recentDamageContexts,
                1);
            --g_recentDamageContextCount;
        }

        g_recentDamageContexts[g_recentDamageContextCount] = context;
        ++g_recentDamageContextCount;
    }

    [[nodiscard]] DamageMagnitude ResolveDamageMagnitude(
        void* victim,
        void* culprit,
        std::uint32_t weapon) noexcept
    {
        const std::int64_t now = CurrentPerformanceCounter();
        PurgeRecentDamageContexts(now);
        for (std::size_t offset = 0;
             offset < g_recentDamageContextCount;
             ++offset)
        {
            const std::size_t index =
                g_recentDamageContextCount - offset - 1;
            const DamageContext& context = g_recentDamageContexts[index];
            if (context.Victim != victim ||
                context.Culprit != culprit ||
                context.Weapon != weapon)
            {
                continue;
            }

            const DamageMagnitude magnitude{
                context.BaseDamage,
                DamageMagnitudeSource::DamageProcess
            };
            std::ranges::shift_left(
                g_recentDamageContexts.begin() + index,
                g_recentDamageContexts.begin() + g_recentDamageContextCount,
                1);
            --g_recentDamageContextCount;
            g_recentDamageContexts[g_recentDamageContextCount] = {};
            return magnitude;
        }

        return {};
    }

    [[nodiscard]] EventRecord CreateDamageRecord(
        void* victim,
        void* culprit,
        std::uint32_t weapon,
        std::uint32_t time,
        bool state) noexcept
    {
        EventRecord record{};
        StampRecord(record, EventStream::Damage);
        record.EventId = EntityDamageMetadataEventId;
        CountObserved(record.EventId);

        if (victim != nullptr)
        {
            record.RelatedEntityAddress = reinterpret_cast<std::uint64_t>(victim);
            record.Flags = record.Flags | RecordFlags::RelatedEntityAddress;
        }

        const DamageMagnitude magnitude = ResolveDamageMagnitude(
            victim,
            culprit,
            weapon);

        record.ArgumentCount = 6;
        record.Arguments[0] = reinterpret_cast<std::uint64_t>(culprit);
        record.Arguments[1] = weapon;
        record.Arguments[2] = time;
        record.Arguments[3] = state ? 1u : 0u;
        record.Arguments[4] = std::bit_cast<std::uint32_t>(magnitude.Value);
        record.Arguments[5] = static_cast<std::uint32_t>(magnitude.Source);
        record.Flags = record.Flags | RecordFlags::Arguments;
        return record;
    }

    void* HookReaction(void* group, void* event)
    {
        CaptureEvent(EventStream::Reaction, group, event);
        return g_originals[0](group, event);
    }

    void* HookGroup(void* group, void* event)
    {
        CaptureEvent(EventStream::Group, group, event);
        return g_originals[1](group, event);
    }

    void* HookGlobal(void* group, void* event)
    {
        CaptureEvent(EventStream::Global, group, event);
        return g_originals[2](group, event);
    }

    void HookDamage(
        void* victim,
        void* culprit,
        std::uint32_t weapon,
        std::uint32_t time,
        bool state)
    {
        g_originalDamage(victim, culprit, weapon, time, state);
        EventRecord record = CreateDamageRecord(victim, culprit, weapon, time, state);
        if (GetStatus() == Status::Ready)
        {
            static_cast<void>(EventQueue::Instance().TryPush(record));
        }
    }

    bool HookDamageProcess(
        DamageProcessData* damage,
        void* victim,
        std::uintptr_t a3,
        std::uintptr_t a4,
        std::uintptr_t a5,
        std::uintptr_t a6)
    {
        const bool result = g_originalDamageProcess(
            damage,
            victim,
            a3,
            a4,
            a5,
            a6);

        DamageContext context{};
        if (TryCaptureDamageContext(damage, victim, context))
        {
            RememberRecentDamageContext(context);
        }
        return result;
    }


    constexpr std::array<EventDispatch, 3> Hooks{
        &HookReaction,
        &HookGroup,
        &HookGlobal
    };

    void RemoveCreatedHooks() noexcept
    {
        for (void* target : g_targets)
        {
            if (target == nullptr)
            {
                continue;
            }

            static_cast<void>(MH_DisableHook(target));
            static_cast<void>(MH_RemoveHook(target));
        }

        if (g_damageTarget != nullptr)
        {
            static_cast<void>(MH_DisableHook(g_damageTarget));
            static_cast<void>(MH_RemoveHook(g_damageTarget));
        }

        if (g_damageProcessTarget != nullptr)
        {
            static_cast<void>(MH_DisableHook(g_damageProcessTarget));
            static_cast<void>(MH_RemoveHook(g_damageProcessTarget));
        }

    }

    void InstallOptionalDamageProcessHook() noexcept
    {
        if (g_damageProcessTarget == nullptr)
        {
            return;
        }

        if (MH_CreateHook(
                g_damageProcessTarget,
                reinterpret_cast<void*>(&HookDamageProcess),
                reinterpret_cast<void**>(&g_originalDamageProcess)) != MH_OK)
        {
            g_damageProcessTarget = nullptr;
            g_originalDamageProcess = nullptr;
            return;
        }

        if (MH_EnableHook(g_damageProcessTarget) != MH_OK)
        {
            static_cast<void>(MH_RemoveHook(g_damageProcessTarget));
            g_damageProcessTarget = nullptr;
            g_originalDamageProcess = nullptr;
        }
    }

    [[nodiscard]] bool ReadDynamicProbePointer(
        std::uintptr_t address,
        std::uintptr_t& value) noexcept
    {
        SIZE_T read{};
        value = 0;
        return address != 0 &&
            ReadProcessMemory(
                GetCurrentProcess(),
                reinterpret_cast<const void*>(address),
                &value,
                sizeof(value),
                &read) != FALSE &&
            read == sizeof(value);
    }

    void WriteDynamicProbeByte(std::byte* address, std::uint8_t value) noexcept
    {
        if (address == nullptr)
        {
            return;
        }
        *reinterpret_cast<volatile std::uint8_t*>(address) = value;
        static_cast<void>(FlushInstructionCache(GetCurrentProcess(), address, 1));
    }

    [[nodiscard]] DynamicProbePendingThread* FindDynamicProbePendingThread(
        DWORD threadId,
        bool claim) noexcept
    {
        for (auto& pending : g_dynamicProbePendingThreads)
        {
            DWORD current = pending.ThreadId.load(std::memory_order_acquire);
            if (current == threadId)
            {
                return &pending;
            }
            if (claim && current == 0)
            {
                DWORD expected{};
                if (pending.ThreadId.compare_exchange_strong(
                        expected,
                        threadId,
                        std::memory_order_acq_rel))
                {
                    return &pending;
                }
            }
        }
        return nullptr;
    }

    void ResetDynamicProbeSessionData() noexcept
    {
        g_dynamicProbeHitCount.store(0, std::memory_order_release);
        g_dynamicProbeRecordSequence.store(0, std::memory_order_release);
        g_dynamicProbeWalkerHistorySequence.store(0, std::memory_order_release);
        for (auto& site : g_dynamicProbeVirtualSites)
        {
            site.PreHits.store(0, std::memory_order_release);
            site.PostHits.store(0, std::memory_order_release);
        }
        for (auto& site : g_dynamicProbeWalkerSites)
        {
            site.PreHits.store(0, std::memory_order_release);
            site.PostHits.store(0, std::memory_order_release);
        }
        for (auto& pending : g_dynamicProbePendingThreads)
        {
            pending.ThreadId.store(0, std::memory_order_release);
            pending.VirtualSiteIndex.store(0xFFFFFFFFu, std::memory_order_release);
            pending.Event.store(0, std::memory_order_release);
            pending.Vtable.store(0, std::memory_order_release);
            pending.Target.store(0, std::memory_order_release);
            pending.Rcx.store(0, std::memory_order_release);
            pending.Rdx.store(0, std::memory_order_release);
            pending.R8.store(0, std::memory_order_release);
            pending.R9.store(0, std::memory_order_release);
            pending.Rsp.store(0, std::memory_order_release);
            pending.ReturnAddress.store(0, std::memory_order_release);
        }
        for (auto& identity : g_dynamicProbeEventIdentities)
        {
            identity.Event.store(0, std::memory_order_release);
            identity.EventType.store(
                DynamicProbeUnknownEventType,
                std::memory_order_release);
            identity.ThreadId.store(0, std::memory_order_release);
            identity.Tick.store(0, std::memory_order_release);
        }
        for (auto& record : g_dynamicProbeRecords)
        {
            record.Sequence.store(0, std::memory_order_release);
        }
        for (auto& entry : g_dynamicProbeWalkerHistory)
        {
            entry.Sequence.store(0, std::memory_order_release);
        }
        g_dynamicProbeSessionStartTick.store(
            GetTickCount64(),
            std::memory_order_release);
    }

    [[nodiscard]] bool AddDynamicProbeWritablePage(std::byte* address) noexcept
    {
        SYSTEM_INFO info{};
        GetSystemInfo(&info);
        if (address == nullptr || info.dwPageSize == 0)
        {
            return false;
        }
        const auto raw = reinterpret_cast<std::uintptr_t>(address);
        const auto mask = static_cast<std::uintptr_t>(info.dwPageSize - 1);
        void* page = reinterpret_cast<void*>(raw & ~mask);
        const std::uint32_t count =
            g_dynamicProbePageCount.load(std::memory_order_acquire);
        for (std::uint32_t index = 0; index < count; ++index)
        {
            if (g_dynamicProbePages[index].Base == page)
            {
                return true;
            }
        }
        if (count >= g_dynamicProbePages.size())
        {
            return false;
        }
        DWORD original{};
        if (VirtualProtect(
                page,
                info.dwPageSize,
                PAGE_EXECUTE_READWRITE,
                &original) == FALSE)
        {
            return false;
        }
        g_dynamicProbePages[count] = DynamicProbePage{
            page,
            static_cast<std::size_t>(info.dwPageSize),
            original
        };
        g_dynamicProbePageCount.store(count + 1, std::memory_order_release);
        return true;
    }

    void RestoreDynamicProbePages() noexcept
    {
        const std::uint32_t count = g_dynamicProbePageCount.exchange(
            0,
            std::memory_order_acq_rel);
        for (std::uint32_t index = 0; index < count; ++index)
        {
            DWORD ignored{};
            static_cast<void>(VirtualProtect(
                g_dynamicProbePages[index].Base,
                g_dynamicProbePages[index].Size,
                g_dynamicProbePages[index].OriginalProtect,
                &ignored));
            g_dynamicProbePages[index] = {};
        }
    }

    [[nodiscard]] int FindDynamicProbeVirtualPreSite(
        std::byte* address) noexcept
    {
        const std::uint32_t count =
            g_dynamicProbeVirtualSiteCount.load(std::memory_order_acquire);
        for (std::uint32_t index = 0; index < count; ++index)
        {
            if (g_dynamicProbeVirtualSites[index].Pre == address)
            {
                return static_cast<int>(index);
            }
        }
        return -1;
    }

    [[nodiscard]] int FindDynamicProbeVirtualPostSite(
        std::byte* address) noexcept
    {
        const std::uint32_t count =
            g_dynamicProbeVirtualSiteCount.load(std::memory_order_acquire);
        for (std::uint32_t index = 0; index < count; ++index)
        {
            if (g_dynamicProbeVirtualSites[index].Post == address)
            {
                return static_cast<int>(index);
            }
        }
        return -1;
    }

    [[nodiscard]] int FindDynamicProbeWalkerPreSite(
        std::byte* address) noexcept
    {
        const std::uint32_t count =
            g_dynamicProbeWalkerSiteCount.load(std::memory_order_acquire);
        for (std::uint32_t index = 0; index < count; ++index)
        {
            if (g_dynamicProbeWalkerSites[index].Pre == address)
            {
                return static_cast<int>(index);
            }
        }
        return -1;
    }

    [[nodiscard]] int FindDynamicProbeWalkerPostSite(
        std::byte* address) noexcept
    {
        const std::uint32_t count =
            g_dynamicProbeWalkerSiteCount.load(std::memory_order_acquire);
        for (std::uint32_t index = 0; index < count; ++index)
        {
            if (g_dynamicProbeWalkerSites[index].Post == address)
            {
                return static_cast<int>(index);
            }
        }
        return -1;
    }

    [[nodiscard]] bool DynamicProbeEndpointInUse(
        std::byte* address,
        std::uint32_t virtualCount,
        std::uint32_t walkerCount) noexcept
    {
        if (address == nullptr)
        {
            return true;
        }
        for (std::uint32_t index = 0; index < virtualCount; ++index)
        {
            if (g_dynamicProbeVirtualSites[index].Pre == address ||
                g_dynamicProbeVirtualSites[index].Post == address)
            {
                return true;
            }
        }
        for (std::uint32_t index = 0; index < walkerCount; ++index)
        {
            if (g_dynamicProbeWalkerSites[index].Pre == address ||
                g_dynamicProbeWalkerSites[index].Post == address)
            {
                return true;
            }
        }
        return false;
    }

    [[nodiscard]] std::uint32_t DynamicProbeArgumentMask(
        std::uint64_t event,
        std::uint64_t rcx,
        std::uint64_t rdx,
        std::uint64_t r8,
        std::uint64_t r9) noexcept
    {
        if (event == 0)
        {
            return 0;
        }
        std::uint32_t mask{};
        if (rcx == event)
        {
            mask |= 1u << 0;
        }
        if (rdx == event)
        {
            mask |= 1u << 1;
        }
        if (r8 == event)
        {
            mask |= 1u << 2;
        }
        if (r9 == event)
        {
            mask |= 1u << 3;
        }
        return mask;
    }

    void RememberDynamicProbeEvent(
        std::uint64_t event,
        std::uint32_t eventType,
        DWORD threadId,
        std::uint64_t tick) noexcept
    {
        if (event == 0 || eventType == DynamicProbeUnknownEventType)
        {
            return;
        }
        const std::size_t index =
            static_cast<std::size_t>((event >> 4u) %
                DynamicProbeEventIdentityCapacity);
        auto& identity = g_dynamicProbeEventIdentities[index];
        identity.EventType.store(eventType, std::memory_order_relaxed);
        identity.ThreadId.store(threadId, std::memory_order_relaxed);
        identity.Tick.store(tick, std::memory_order_relaxed);
        identity.Event.store(event, std::memory_order_release);
    }

    [[nodiscard]] std::uint32_t ResolveDynamicProbeEventType(
        std::uint64_t event) noexcept
    {
        if (event == 0)
        {
            return DynamicProbeUnknownEventType;
        }
        const std::size_t index =
            static_cast<std::size_t>((event >> 4u) %
                DynamicProbeEventIdentityCapacity);
        const auto& identity = g_dynamicProbeEventIdentities[index];
        if (identity.Event.load(std::memory_order_acquire) != event)
        {
            return DynamicProbeUnknownEventType;
        }
        return identity.EventType.load(std::memory_order_acquire);
    }

    void RememberDynamicProbeWalkerHistory(
        std::uint32_t walkerIndex,
        DWORD threadId,
        const CONTEXT& context,
        std::uint64_t returnAddress) noexcept
    {
        const std::uint64_t sequence =
            g_dynamicProbeWalkerHistorySequence.fetch_add(
                1,
                std::memory_order_acq_rel) + 1;
        auto& entry = g_dynamicProbeWalkerHistory[
            static_cast<std::size_t>(
                (sequence - 1) % DynamicProbeWalkerHistoryCapacity)];
        entry.Sequence.store(0, std::memory_order_release);
        entry.Tick.store(GetTickCount64(), std::memory_order_relaxed);
        entry.ThreadId.store(threadId, std::memory_order_relaxed);
        entry.WalkerRva.store(
            g_dynamicProbeWalkerSites[walkerIndex].WalkerRva,
            std::memory_order_relaxed);
        entry.Rcx.store(context.Rcx, std::memory_order_relaxed);
        entry.Rdx.store(context.Rdx, std::memory_order_relaxed);
        entry.R8.store(context.R8, std::memory_order_relaxed);
        entry.R9.store(context.R9, std::memory_order_relaxed);
        entry.Rsp.store(context.Rsp, std::memory_order_relaxed);
        entry.ReturnAddress.store(returnAddress, std::memory_order_relaxed);
        entry.Sequence.store(sequence, std::memory_order_release);
        g_dynamicProbeHitCount.fetch_add(1, std::memory_order_acq_rel);
    }

    void PublishDynamicProbeCorrelatedWalkerRecord(
        const DynamicProbeWalkerHistoryEntry& entry,
        std::uint64_t event,
        std::uint32_t eventType,
        std::uint32_t eventArgumentMask) noexcept
    {
        const std::uint64_t sequence =
            g_dynamicProbeRecordSequence.fetch_add(
                1,
                std::memory_order_acq_rel) + 1;
        auto& record = g_dynamicProbeRecords[
            static_cast<std::size_t>(
                (sequence - 1) % DynamicProbeRecordCapacity)];
        record.Sequence.store(0, std::memory_order_release);
        record.Tick.store(
            entry.Tick.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
        record.ThreadId.store(
            entry.ThreadId.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
        record.Kind.store(
            static_cast<std::uint32_t>(DynamicProbeRecordKind::WalkerEntry),
            std::memory_order_relaxed);
        record.WalkerRva.store(
            entry.WalkerRva.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
        record.CallsiteRva.store(0, std::memory_order_relaxed);
        record.SlotOffset.store(0, std::memory_order_relaxed);
        record.EventType.store(eventType, std::memory_order_relaxed);
        record.EventArgumentMask.store(
            eventArgumentMask,
            std::memory_order_relaxed);
        record.Event.store(event, std::memory_order_relaxed);
        record.Vtable.store(0, std::memory_order_relaxed);
        record.Target.store(0, std::memory_order_relaxed);
        record.Rcx.store(
            entry.Rcx.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
        record.Rdx.store(
            entry.Rdx.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
        record.R8.store(
            entry.R8.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
        record.R9.store(
            entry.R9.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
        record.Rsp.store(
            entry.Rsp.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
        record.ReturnAddress.store(
            entry.ReturnAddress.load(std::memory_order_relaxed),
            std::memory_order_relaxed);
        record.ReturnValue.store(0, std::memory_order_relaxed);
        record.Sequence.store(sequence, std::memory_order_release);
    }

    void CorrelateDynamicProbeWalkerHistory(
        std::uint64_t event,
        std::uint32_t eventType,
        DWORD threadId,
        std::uint64_t tick) noexcept
    {
        if (event == 0 || eventType == DynamicProbeUnknownEventType)
        {
            return;
        }
        const std::uint64_t total =
            g_dynamicProbeWalkerHistorySequence.load(
                std::memory_order_acquire);
        if (total == 0)
        {
            return;
        }
        const std::uint64_t lower =
            total > DynamicProbeCorrelationHistoryLimit
                ? total - DynamicProbeCorrelationHistoryLimit + 1
                : 1;
        std::array<
            std::uint64_t,
            DynamicProbeCorrelationMatchCapacity> matches{};
        std::size_t matchCount{};
        for (std::uint64_t sequence = total;
             sequence >= lower;
             --sequence)
        {
            const auto& entry = g_dynamicProbeWalkerHistory[
                static_cast<std::size_t>(
                    (sequence - 1) % DynamicProbeWalkerHistoryCapacity)];
            if (entry.Sequence.load(std::memory_order_acquire) != sequence)
            {
                if (sequence == lower)
                {
                    break;
                }
                continue;
            }
            if (entry.ThreadId.load(std::memory_order_relaxed) != threadId)
            {
                if (sequence == lower)
                {
                    break;
                }
                continue;
            }
            const std::uint64_t entryTick =
                entry.Tick.load(std::memory_order_relaxed);
            if (entryTick > tick)
            {
                if (sequence == lower)
                {
                    break;
                }
                continue;
            }
            if (tick - entryTick > DynamicProbeCorrelationWindowMs)
            {
                break;
            }
            const std::uint32_t mask = DynamicProbeArgumentMask(
                event,
                entry.Rcx.load(std::memory_order_relaxed),
                entry.Rdx.load(std::memory_order_relaxed),
                entry.R8.load(std::memory_order_relaxed),
                entry.R9.load(std::memory_order_relaxed));
            if (mask != 0 && matchCount < matches.size())
            {
                matches[matchCount++] = sequence;
            }
            if (sequence == lower)
            {
                break;
            }
        }
        while (matchCount > 0)
        {
            const std::uint64_t sequence = matches[--matchCount];
            const auto& entry = g_dynamicProbeWalkerHistory[
                static_cast<std::size_t>(
                    (sequence - 1) % DynamicProbeWalkerHistoryCapacity)];
            if (entry.Sequence.load(std::memory_order_acquire) != sequence)
            {
                continue;
            }
            const std::uint32_t mask = DynamicProbeArgumentMask(
                event,
                entry.Rcx.load(std::memory_order_relaxed),
                entry.Rdx.load(std::memory_order_relaxed),
                entry.R8.load(std::memory_order_relaxed),
                entry.R9.load(std::memory_order_relaxed));
            if (mask != 0)
            {
                PublishDynamicProbeCorrelatedWalkerRecord(
                    entry,
                    event,
                    eventType,
                    mask);
            }
        }
    }

    void PublishDynamicProbeVirtualRecord(
        std::uint32_t siteIndex,
        DWORD threadId,
        std::uint32_t eventType,
        std::uint64_t returnValue,
        const DynamicProbePendingThread* pending) noexcept
    {
        const std::uint64_t sequence =
            g_dynamicProbeRecordSequence.fetch_add(
                1,
                std::memory_order_acq_rel) + 1;
        auto& record = g_dynamicProbeRecords[
            static_cast<std::size_t>(
                (sequence - 1) % DynamicProbeRecordCapacity)];
        record.Sequence.store(0, std::memory_order_release);
        const std::uint64_t tick = GetTickCount64();
        record.Tick.store(tick, std::memory_order_relaxed);
        record.ThreadId.store(threadId, std::memory_order_relaxed);
        record.Kind.store(
            static_cast<std::uint32_t>(DynamicProbeRecordKind::VirtualCall),
            std::memory_order_relaxed);
        record.WalkerRva.store(
            g_dynamicProbeVirtualSites[siteIndex].WalkerRva,
            std::memory_order_relaxed);
        record.CallsiteRva.store(
            g_dynamicProbeVirtualSites[siteIndex].CallsiteRva,
            std::memory_order_relaxed);
        record.SlotOffset.store(
            g_dynamicProbeVirtualSites[siteIndex].SlotOffset,
            std::memory_order_relaxed);
        record.EventType.store(eventType, std::memory_order_relaxed);
        record.ReturnValue.store(returnValue, std::memory_order_relaxed);
        if (pending != nullptr)
        {
            const std::uint64_t event =
                pending->Event.load(std::memory_order_relaxed);
            record.Event.store(event, std::memory_order_relaxed);
            record.Vtable.store(
                pending->Vtable.load(std::memory_order_relaxed),
                std::memory_order_relaxed);
            record.Target.store(
                pending->Target.load(std::memory_order_relaxed),
                std::memory_order_relaxed);
            record.Rcx.store(
                pending->Rcx.load(std::memory_order_relaxed),
                std::memory_order_relaxed);
            record.Rdx.store(
                pending->Rdx.load(std::memory_order_relaxed),
                std::memory_order_relaxed);
            record.R8.store(
                pending->R8.load(std::memory_order_relaxed),
                std::memory_order_relaxed);
            record.R9.store(
                pending->R9.load(std::memory_order_relaxed),
                std::memory_order_relaxed);
            record.Rsp.store(
                pending->Rsp.load(std::memory_order_relaxed),
                std::memory_order_relaxed);
            record.ReturnAddress.store(
                pending->ReturnAddress.load(std::memory_order_relaxed),
                std::memory_order_relaxed);
            record.EventArgumentMask.store(
                DynamicProbeArgumentMask(
                    event,
                    pending->Rcx.load(std::memory_order_relaxed),
                    pending->Rdx.load(std::memory_order_relaxed),
                    pending->R8.load(std::memory_order_relaxed),
                    pending->R9.load(std::memory_order_relaxed)),
                std::memory_order_relaxed);
        }
        else
        {
            record.Event.store(0, std::memory_order_relaxed);
            record.Vtable.store(0, std::memory_order_relaxed);
            record.Target.store(0, std::memory_order_relaxed);
            record.Rcx.store(0, std::memory_order_relaxed);
            record.Rdx.store(0, std::memory_order_relaxed);
            record.R8.store(0, std::memory_order_relaxed);
            record.R9.store(0, std::memory_order_relaxed);
            record.Rsp.store(0, std::memory_order_relaxed);
            record.ReturnAddress.store(0, std::memory_order_relaxed);
            record.EventArgumentMask.store(0, std::memory_order_relaxed);
        }
        record.Sequence.store(sequence, std::memory_order_release);
        g_dynamicProbeHitCount.fetch_add(1, std::memory_order_acq_rel);
    }

    LONG CALLBACK DynamicProbeExceptionHandler(
        EXCEPTION_POINTERS* exceptionInfo) noexcept
    {
#if defined(_M_X64) || defined(__x86_64__)
        if (exceptionInfo == nullptr ||
            exceptionInfo->ExceptionRecord == nullptr ||
            exceptionInfo->ContextRecord == nullptr ||
            !g_dynamicProbeActive.load(std::memory_order_acquire))
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        CONTEXT* context = exceptionInfo->ContextRecord;
        if (exceptionInfo->ExceptionRecord->ExceptionCode !=
            EXCEPTION_BREAKPOINT)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        auto* address = reinterpret_cast<std::byte*>(
            exceptionInfo->ExceptionRecord->ExceptionAddress);
        const DWORD threadId = GetCurrentThreadId();

        const int virtualPreIndex =
            FindDynamicProbeVirtualPreSite(address);
        if (virtualPreIndex >= 0)
        {
            const auto index =
                static_cast<std::uint32_t>(virtualPreIndex);
            auto& site = g_dynamicProbeVirtualSites[index];
            site.PreHits.fetch_add(1, std::memory_order_acq_rel);
            DynamicProbePendingThread* pending =
                FindDynamicProbePendingThread(threadId, true);
            if (pending != nullptr)
            {
                std::uintptr_t vtable{};
                std::uintptr_t target{};
                std::uintptr_t returnAddress{};
                static_cast<void>(ReadDynamicProbePointer(
                    static_cast<std::uintptr_t>(context->Rcx),
                    vtable));
                if (vtable != 0)
                {
                    static_cast<void>(ReadDynamicProbePointer(
                        vtable + site.SlotOffset,
                        target));
                }
                static_cast<void>(ReadDynamicProbePointer(
                    static_cast<std::uintptr_t>(context->Rsp),
                    returnAddress));
                pending->Event.store(
                    context->Rcx,
                    std::memory_order_relaxed);
                pending->Vtable.store(
                    vtable,
                    std::memory_order_relaxed);
                pending->Target.store(
                    target,
                    std::memory_order_relaxed);
                pending->Rcx.store(
                    context->Rcx,
                    std::memory_order_relaxed);
                pending->Rdx.store(
                    context->Rdx,
                    std::memory_order_relaxed);
                pending->R8.store(
                    context->R8,
                    std::memory_order_relaxed);
                pending->R9.store(
                    context->R9,
                    std::memory_order_relaxed);
                pending->Rsp.store(
                    context->Rsp,
                    std::memory_order_relaxed);
                pending->ReturnAddress.store(
                    returnAddress,
                    std::memory_order_relaxed);
                pending->VirtualSiteIndex.store(
                    index,
                    std::memory_order_release);
            }
            WriteDynamicProbeByte(site.Post, DynamicProbeBreakpoint);
            WriteDynamicProbeByte(site.Pre, site.PreOriginal);
            context->Rip = reinterpret_cast<DWORD64>(site.Pre);
            return EXCEPTION_CONTINUE_EXECUTION;
        }

        const int virtualPostIndex =
            FindDynamicProbeVirtualPostSite(address);
        if (virtualPostIndex >= 0)
        {
            const auto index =
                static_cast<std::uint32_t>(virtualPostIndex);
            auto& site = g_dynamicProbeVirtualSites[index];
            site.PostHits.fetch_add(1, std::memory_order_acq_rel);
            DynamicProbePendingThread* pending =
                FindDynamicProbePendingThread(threadId, true);
            const DynamicProbePendingThread* recordPending =
                pending != nullptr &&
                pending->VirtualSiteIndex.load(std::memory_order_acquire) ==
                    index
                    ? pending
                    : nullptr;
            std::uint32_t eventType = DynamicProbeUnknownEventType;
            bool publish{};
            if (site.SlotOffset == DynamicProbeEventTypeSlotOffset)
            {
                eventType = static_cast<std::uint32_t>(context->Rax);
                publish = recordPending != nullptr;
                if (publish && eventType != DynamicProbeUnknownEventType)
                {
                    const std::uint64_t event =
                        recordPending->Event.load(std::memory_order_relaxed);
                    const std::uint64_t tick = GetTickCount64();
                    RememberDynamicProbeEvent(
                        event,
                        eventType,
                        threadId,
                        tick);
                    CorrelateDynamicProbeWalkerHistory(
                        event,
                        eventType,
                        threadId,
                        tick);
                }
            }
            else if (recordPending != nullptr)
            {
                eventType = ResolveDynamicProbeEventType(
                    recordPending->Event.load(std::memory_order_relaxed));
                publish = eventType != DynamicProbeUnknownEventType;
            }
            if (publish)
            {
                PublishDynamicProbeVirtualRecord(
                    index,
                    threadId,
                    eventType,
                    context->Rax,
                    recordPending);
            }
            else
            {
                g_dynamicProbeHitCount.fetch_add(
                    1,
                    std::memory_order_acq_rel);
            }
            if (pending != nullptr)
            {
                pending->VirtualSiteIndex.store(
                    0xFFFFFFFFu,
                    std::memory_order_release);
            }
            WriteDynamicProbeByte(site.Pre, DynamicProbeBreakpoint);
            WriteDynamicProbeByte(site.Post, site.PostOriginal);
            context->Rip = reinterpret_cast<DWORD64>(site.Post);
            return EXCEPTION_CONTINUE_EXECUTION;
        }

        const int walkerPreIndex =
            FindDynamicProbeWalkerPreSite(address);
        if (walkerPreIndex >= 0)
        {
            const auto index =
                static_cast<std::uint32_t>(walkerPreIndex);
            auto& site = g_dynamicProbeWalkerSites[index];
            site.PreHits.fetch_add(1, std::memory_order_acq_rel);
            std::uintptr_t returnAddress{};
            static_cast<void>(ReadDynamicProbePointer(
                static_cast<std::uintptr_t>(context->Rsp),
                returnAddress));
            RememberDynamicProbeWalkerHistory(
                index,
                threadId,
                *context,
                returnAddress);
            WriteDynamicProbeByte(site.Post, DynamicProbeBreakpoint);
            WriteDynamicProbeByte(site.Pre, site.PreOriginal);
            context->Rip = reinterpret_cast<DWORD64>(site.Pre);
            return EXCEPTION_CONTINUE_EXECUTION;
        }

        const int walkerPostIndex =
            FindDynamicProbeWalkerPostSite(address);
        if (walkerPostIndex >= 0)
        {
            const auto index =
                static_cast<std::uint32_t>(walkerPostIndex);
            auto& site = g_dynamicProbeWalkerSites[index];
            site.PostHits.fetch_add(1, std::memory_order_acq_rel);
            WriteDynamicProbeByte(site.Pre, DynamicProbeBreakpoint);
            WriteDynamicProbeByte(site.Post, site.PostOriginal);
            context->Rip = reinterpret_cast<DWORD64>(site.Post);
            return EXCEPTION_CONTINUE_EXECUTION;
        }
#else
        static_cast<void>(exceptionInfo);
#endif
        return EXCEPTION_CONTINUE_SEARCH;
    }

    void BuildDynamicProbeLogPath() noexcept
    {
        g_dynamicProbeLogPath.fill(L'\0');
        const DWORD length = GetModuleFileNameW(
            nullptr,
            g_dynamicProbeLogPath.data(),
            static_cast<DWORD>(g_dynamicProbeLogPath.size()));
        if (length == 0 || length >= g_dynamicProbeLogPath.size())
        {
            static_cast<void>(wcscpy_s(
                g_dynamicProbeLogPath.data(),
                g_dynamicProbeLogPath.size(),
                DynamicProbeLogName));
            return;
        }
        wchar_t* slash = wcsrchr(
            g_dynamicProbeLogPath.data(),
            L'\\');
        wchar_t* slash2 = wcsrchr(
            g_dynamicProbeLogPath.data(),
            L'/');
        if (slash2 != nullptr &&
            (slash == nullptr || slash2 > slash))
        {
            slash = slash2;
        }
        if (slash == nullptr)
        {
            static_cast<void>(wcscpy_s(
                g_dynamicProbeLogPath.data(),
                g_dynamicProbeLogPath.size(),
                DynamicProbeLogName));
            return;
        }
        const std::size_t prefix = static_cast<std::size_t>(
            slash + 1 - g_dynamicProbeLogPath.data());
        if (prefix + std::size(DynamicProbeLogName) >
            g_dynamicProbeLogPath.size())
        {
            return;
        }
        static_cast<void>(wcscpy_s(
            slash + 1,
            g_dynamicProbeLogPath.size() - prefix,
            DynamicProbeLogName));
    }

    [[nodiscard]] const char* DynamicProbeRecordKindName(
        std::uint32_t value) noexcept
    {
        if (value ==
            static_cast<std::uint32_t>(
                DynamicProbeRecordKind::WalkerEntry))
        {
            return "WalkerEntry";
        }
        if (value ==
            static_cast<std::uint32_t>(
                DynamicProbeRecordKind::VirtualCall))
        {
            return "VirtualCall";
        }
        return "Unknown";
    }

    void AppendDynamicProbePathSummaries(
        std::FILE* file,
        std::uint64_t firstSequence,
        std::uint64_t totalRecords) noexcept
    {
        std::array<std::uint64_t, 64> events{};
        std::size_t eventCount{};
        for (std::uint64_t sequence = firstSequence;
             sequence <= totalRecords;
             ++sequence)
        {
            const auto& record = g_dynamicProbeRecords[
                static_cast<std::size_t>(
                    (sequence - 1) % DynamicProbeRecordCapacity)];
            if (record.Sequence.load(std::memory_order_acquire) != sequence)
            {
                continue;
            }
            const std::uint64_t event =
                record.Event.load(std::memory_order_relaxed);
            if (event == 0)
            {
                continue;
            }
            bool exists{};
            for (std::size_t index = 0; index < eventCount; ++index)
            {
                if (events[index] == event)
                {
                    exists = true;
                    break;
                }
            }
            if (!exists && eventCount < events.size())
            {
                events[eventCount++] = event;
            }
        }

        for (std::size_t eventIndex = 0;
             eventIndex < eventCount;
             ++eventIndex)
        {
            const std::uint64_t event = events[eventIndex];
            std::uint32_t eventType = DynamicProbeUnknownEventType;
            for (std::uint64_t sequence = firstSequence;
                 sequence <= totalRecords;
                 ++sequence)
            {
                const auto& record = g_dynamicProbeRecords[
                    static_cast<std::size_t>(
                        (sequence - 1) % DynamicProbeRecordCapacity)];
                if (record.Sequence.load(std::memory_order_acquire) ==
                        sequence &&
                    record.Event.load(std::memory_order_relaxed) == event)
                {
                    const std::uint32_t candidate =
                        record.EventType.load(std::memory_order_relaxed);
                    if (candidate != DynamicProbeUnknownEventType)
                    {
                        eventType = candidate;
                        break;
                    }
                }
            }
            std::fprintf(
                file,
                "PathEvent=0x%llX;EventType=%u;Steps=",
                static_cast<unsigned long long>(event),
                eventType);
            bool firstStep = true;
            for (std::uint64_t sequence = firstSequence;
                 sequence <= totalRecords;
                 ++sequence)
            {
                const auto& record = g_dynamicProbeRecords[
                    static_cast<std::size_t>(
                        (sequence - 1) % DynamicProbeRecordCapacity)];
                if (record.Sequence.load(std::memory_order_acquire) !=
                        sequence ||
                    record.Event.load(std::memory_order_relaxed) != event)
                {
                    continue;
                }
                if (!firstStep)
                {
                    std::fputc(',', file);
                }
                firstStep = false;
                const std::uint32_t kind =
                    record.Kind.load(std::memory_order_relaxed);
                if (kind ==
                    static_cast<std::uint32_t>(
                        DynamicProbeRecordKind::WalkerEntry))
                {
                    std::fprintf(
                        file,
                        "W:0x%X/Args=0x%X",
                        record.WalkerRva.load(std::memory_order_relaxed),
                        record.EventArgumentMask.load(
                            std::memory_order_relaxed));
                }
                else
                {
                    std::fprintf(
                        file,
                        "V:0x%X/0x%X",
                        record.WalkerRva.load(std::memory_order_relaxed),
                        record.SlotOffset.load(std::memory_order_relaxed));
                }
            }
            std::fputc('\n', file);
        }
    }

    void AppendDynamicProbeSnapshot() noexcept
    {
        if (!CEventGenerator::Configuration::LogEnabled())
        {
            return;
        }
        if (g_dynamicProbeLogPath[0] == L'\0')
        {
            BuildDynamicProbeLogPath();
        }
        std::FILE* file{};
        if (_wfopen_s(
                &file,
                g_dynamicProbeLogPath.data(),
                L"ab") != 0 ||
            file == nullptr)
        {
            return;
        }
        const std::uint32_t session =
            g_dynamicProbeSessionCount.load(std::memory_order_acquire);
        const std::uint32_t virtualSiteCount =
            g_dynamicProbeVirtualSiteCount.load(std::memory_order_acquire);
        const std::uint32_t walkerSiteCount =
            g_dynamicProbeWalkerSiteCount.load(std::memory_order_acquire);
        const std::uint64_t totalRecords =
            g_dynamicProbeRecordSequence.load(std::memory_order_acquire);
        const std::uint64_t walkerHistory =
            g_dynamicProbeWalkerHistorySequence.load(
                std::memory_order_acquire);
        const std::uint64_t startTick =
            g_dynamicProbeSessionStartTick.load(std::memory_order_acquire);
        const std::uint64_t endTick = GetTickCount64();
        std::fprintf(
            file,
            "Session=%u;LocatorRevision=%u;Mode=EventGatedCorrelation;StartTick=%llu;EndTick=%llu;VirtualSites=%u;WalkerSites=%u;Hits=%llu;Records=%llu;WalkerHistory=%llu\n",
            session,
            LocatorRevision,
            static_cast<unsigned long long>(startTick),
            static_cast<unsigned long long>(endTick),
            virtualSiteCount,
            walkerSiteCount,
            static_cast<unsigned long long>(
                g_dynamicProbeHitCount.load(std::memory_order_acquire)),
            static_cast<unsigned long long>(totalRecords),
            static_cast<unsigned long long>(walkerHistory));
        for (std::uint32_t index = 0;
             index < virtualSiteCount;
             ++index)
        {
            const auto& site = g_dynamicProbeVirtualSites[index];
            std::fprintf(
                file,
                "VirtualSite=%u;WalkerRva=0x%X;CallsiteRva=0x%X;Slot=0x%X;PreHits=%llu;PostHits=%llu\n",
                index,
                site.WalkerRva,
                site.CallsiteRva,
                site.SlotOffset,
                static_cast<unsigned long long>(
                    site.PreHits.load(std::memory_order_acquire)),
                static_cast<unsigned long long>(
                    site.PostHits.load(std::memory_order_acquire)));
        }
        for (std::uint32_t index = 0;
             index < walkerSiteCount;
             ++index)
        {
            const auto& site = g_dynamicProbeWalkerSites[index];
            std::fprintf(
                file,
                "WalkerSite=%u;WalkerRva=0x%X;PreHits=%llu;PostHits=%llu\n",
                index,
                site.WalkerRva,
                static_cast<unsigned long long>(
                    site.PreHits.load(std::memory_order_acquire)),
                static_cast<unsigned long long>(
                    site.PostHits.load(std::memory_order_acquire)));
        }
        const auto mainModuleBase = reinterpret_cast<std::uintptr_t>(
            GetModuleHandleW(nullptr));
        const std::uint64_t firstSequence =
            totalRecords > DynamicProbeRecordCapacity
                ? totalRecords - DynamicProbeRecordCapacity + 1
                : 1;
        for (std::uint64_t sequence = firstSequence;
             sequence <= totalRecords;
             ++sequence)
        {
            const auto& record = g_dynamicProbeRecords[
                static_cast<std::size_t>(
                    (sequence - 1) % DynamicProbeRecordCapacity)];
            if (record.Sequence.load(std::memory_order_acquire) != sequence)
            {
                continue;
            }
            const std::uint32_t eventType =
                record.EventType.load(std::memory_order_relaxed);
            std::fprintf(
                file,
                "Record=%llu;Kind=%s;Tick=%llu;Thread=%u;WalkerRva=0x%X;CallsiteRva=0x%X;Slot=0x%X;EventType=%u;EventTypeKnown=%u;Event=0x%llX;EventArgMask=0x%X;Vtable=0x%llX;Target=0x%llX;RCX=0x%llX;RDX=0x%llX;R8=0x%llX;R9=0x%llX;RSP=0x%llX;ReturnAddress=0x%llX;ReturnAddressRva=0x%llX;ReturnValue=0x%llX\n",
                static_cast<unsigned long long>(sequence),
                DynamicProbeRecordKindName(
                    record.Kind.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(
                    record.Tick.load(std::memory_order_relaxed)),
                record.ThreadId.load(std::memory_order_relaxed),
                record.WalkerRva.load(std::memory_order_relaxed),
                record.CallsiteRva.load(std::memory_order_relaxed),
                record.SlotOffset.load(std::memory_order_relaxed),
                eventType,
                eventType != DynamicProbeUnknownEventType ? 1u : 0u,
                static_cast<unsigned long long>(
                    record.Event.load(std::memory_order_relaxed)),
                record.EventArgumentMask.load(std::memory_order_relaxed),
                static_cast<unsigned long long>(
                    record.Vtable.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(
                    record.Target.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(
                    record.Rcx.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(
                    record.Rdx.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(
                    record.R8.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(
                    record.R9.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(
                    record.Rsp.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(
                    record.ReturnAddress.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(
                    mainModuleBase != 0 &&
                        record.ReturnAddress.load(std::memory_order_relaxed) >=
                            mainModuleBase
                        ? record.ReturnAddress.load(std::memory_order_relaxed) -
                            mainModuleBase
                        : 0),
                static_cast<unsigned long long>(
                    record.ReturnValue.load(std::memory_order_relaxed)));
        }
        AppendDynamicProbePathSummaries(
            file,
            firstSequence,
            totalRecords);
        std::fputs("EndSession\n", file);
        std::fclose(file);
    }

    void RestoreDynamicProbeBreakpoints() noexcept
    {
        const std::uint32_t virtualCount =
            g_dynamicProbeVirtualSiteCount.load(std::memory_order_acquire);
        for (std::uint32_t index = 0; index < virtualCount; ++index)
        {
            WriteDynamicProbeByte(
                g_dynamicProbeVirtualSites[index].Pre,
                g_dynamicProbeVirtualSites[index].PreOriginal);
            WriteDynamicProbeByte(
                g_dynamicProbeVirtualSites[index].Post,
                g_dynamicProbeVirtualSites[index].PostOriginal);
        }
        const std::uint32_t walkerCount =
            g_dynamicProbeWalkerSiteCount.load(std::memory_order_acquire);
        for (std::uint32_t index = 0; index < walkerCount; ++index)
        {
            WriteDynamicProbeByte(
                g_dynamicProbeWalkerSites[index].Pre,
                g_dynamicProbeWalkerSites[index].PreOriginal);
            WriteDynamicProbeByte(
                g_dynamicProbeWalkerSites[index].Post,
                g_dynamicProbeWalkerSites[index].PostOriginal);
        }
    }

    void StopDynamicProbe(bool writeSnapshot) noexcept
    {
        if (!g_dynamicProbeActive.load(std::memory_order_acquire))
        {
            return;
        }
        RestoreDynamicProbeBreakpoints();
        void* handler = g_dynamicProbeHandler.exchange(
            nullptr,
            std::memory_order_acq_rel);
        if (handler != nullptr)
        {
            static_cast<void>(RemoveVectoredExceptionHandler(handler));
        }
        RestoreDynamicProbePages();
        g_dynamicProbeActive.store(false, std::memory_order_release);
        if (writeSnapshot)
        {
            AppendDynamicProbeSnapshot();
        }
    }

    [[nodiscard]] bool StartDynamicProbe() noexcept
    {
#if defined(_M_X64) || defined(__x86_64__)
        if (!g_dynamicProbeArmed.load(std::memory_order_acquire) ||
            g_dynamicProbeActive.load(std::memory_order_acquire))
        {
            return false;
        }
        ResetDynamicProbeSessionData();
        g_dynamicProbePageCount.store(0, std::memory_order_release);
        const std::uint32_t virtualCount =
            g_dynamicProbeVirtualSiteCount.load(std::memory_order_acquire);
        const std::uint32_t walkerCount =
            g_dynamicProbeWalkerSiteCount.load(std::memory_order_acquire);
        for (std::uint32_t index = 0; index < virtualCount; ++index)
        {
            if (!AddDynamicProbeWritablePage(
                    g_dynamicProbeVirtualSites[index].Pre) ||
                !AddDynamicProbeWritablePage(
                    g_dynamicProbeVirtualSites[index].Post))
            {
                RestoreDynamicProbePages();
                return false;
            }
        }
        for (std::uint32_t index = 0; index < walkerCount; ++index)
        {
            if (!AddDynamicProbeWritablePage(
                    g_dynamicProbeWalkerSites[index].Pre) ||
                !AddDynamicProbeWritablePage(
                    g_dynamicProbeWalkerSites[index].Post))
            {
                RestoreDynamicProbePages();
                return false;
            }
        }
        void* handler =
            AddVectoredExceptionHandler(
                1,
                DynamicProbeExceptionHandler);
        if (handler == nullptr)
        {
            RestoreDynamicProbePages();
            return false;
        }
        g_dynamicProbeHandler.store(
            handler,
            std::memory_order_release);
        g_dynamicProbeSessionCount.fetch_add(
            1,
            std::memory_order_acq_rel);
        g_dynamicProbeActive.store(true, std::memory_order_release);
        for (std::uint32_t index = 0; index < virtualCount; ++index)
        {
            WriteDynamicProbeByte(
                g_dynamicProbeVirtualSites[index].Pre,
                DynamicProbeBreakpoint);
        }
        for (std::uint32_t index = 0; index < walkerCount; ++index)
        {
            WriteDynamicProbeByte(
                g_dynamicProbeWalkerSites[index].Pre,
                DynamicProbeBreakpoint);
        }
        return true;
#else
        return false;
#endif
    }

    [[nodiscard]] std::uint32_t DynamicProbeVirtualInstructionLength(
        const EnhancedVirtualSlotCallsiteDiagnostic& callsite,
        const std::byte* pre) noexcept
    {
        if (pre == nullptr)
        {
            return 0;
        }
        const auto* bytes =
            reinterpret_cast<const std::uint8_t*>(pre);
        if (bytes[0] != 0xFF)
        {
            return 0;
        }
        if (bytes[1] == 0x50 &&
            callsite.SlotOffset <= 0x7Fu &&
            bytes[2] ==
                static_cast<std::uint8_t>(callsite.SlotOffset))
        {
            return 3;
        }
        if (bytes[1] == 0x90)
        {
            std::uint32_t displacement{};
            std::memcpy(
                &displacement,
                bytes + 2,
                sizeof(displacement));
            if (displacement == callsite.SlotOffset)
            {
                return 6;
            }
        }
        return 0;
    }

    [[nodiscard]] std::uint32_t DynamicProbeWalkerInstructionLength(
        const std::byte* pre) noexcept
    {
        if (pre == nullptr)
        {
            return 0;
        }
        hde64s instruction{};
        const unsigned int length =
            hde64_disasm(pre, &instruction);
        if (length == 0 ||
            length > 15 ||
            (instruction.flags & F_ERROR) != 0)
        {
            return 0;
        }
        const std::uint8_t opcode = instruction.opcode;
        if (opcode == 0xC2 ||
            opcode == 0xC3 ||
            opcode == 0xCA ||
            opcode == 0xCB ||
            opcode == 0xE8 ||
            opcode == 0xE9 ||
            opcode == 0xEA ||
            opcode == 0xEB ||
            (opcode >= 0x70 && opcode <= 0x7F) ||
            (opcode >= 0xE0 && opcode <= 0xE3) ||
            (opcode == 0x0F &&
             instruction.opcode2 >= 0x80 &&
             instruction.opcode2 <= 0x8F))
        {
            return 0;
        }
        if (opcode == 0xFF &&
            (instruction.modrm_reg == 2 ||
             instruction.modrm_reg == 3 ||
             instruction.modrm_reg == 4 ||
             instruction.modrm_reg == 5))
        {
            return 0;
        }
        return length;
    }

    void ArmDynamicProbe(
        const EventLocatorResult& located) noexcept
    {
        if (located.Targets.Edition != GameEdition::Enhanced ||
            located.Diagnostics.Failure !=
                LocatorFailure::EnhancedDispatchUnresolved)
        {
            return;
        }
        auto* imageBase =
            reinterpret_cast<std::byte*>(
                GetModuleHandleW(nullptr));
        if (imageBase == nullptr)
        {
            return;
        }

        std::uint32_t virtualCount{};
        const std::uint32_t virtualDiagnosticCount =
            std::min<std::uint32_t>(
                located.Diagnostics.VirtualSlotCallsiteDiagnosticCount,
                static_cast<std::uint32_t>(
                    located.Diagnostics.VirtualSlotCallsites.size()));
        for (std::uint32_t index = 0;
             index < virtualDiagnosticCount &&
             virtualCount < g_dynamicProbeVirtualSites.size();
             ++index)
        {
            const auto& callsite =
                located.Diagnostics.VirtualSlotCallsites[index];
            if (callsite.CallsiteRva == 0)
            {
                continue;
            }
            auto* pre =
                imageBase + callsite.CallsiteRva;
            const std::uint32_t length =
                DynamicProbeVirtualInstructionLength(
                    callsite,
                    pre);
            if (length == 0)
            {
                continue;
            }
            auto* post = pre + length;
            if (DynamicProbeEndpointInUse(
                    pre,
                    virtualCount,
                    0) ||
                DynamicProbeEndpointInUse(
                    post,
                    virtualCount,
                    0))
            {
                continue;
            }
            auto& site =
                g_dynamicProbeVirtualSites[virtualCount];
            site.Pre = pre;
            site.Post = post;
            site.PreOriginal =
                reinterpret_cast<const std::uint8_t*>(pre)[0];
            site.PostOriginal =
                reinterpret_cast<const std::uint8_t*>(post)[0];
            site.WalkerRva = callsite.WalkerRva;
            site.CallsiteRva = callsite.CallsiteRva;
            site.SlotOffset = callsite.SlotOffset;
            ++virtualCount;
        }

        std::uint32_t walkerCount{};
        const std::uint32_t candidateCount =
            std::min<std::uint32_t>(
                located.Diagnostics.EnhancedCandidateDiagnosticCount,
                static_cast<std::uint32_t>(
                    located.Diagnostics.EnhancedCandidates.size()));
        for (std::uint32_t index = 0;
             index < candidateCount &&
             walkerCount < g_dynamicProbeWalkerSites.size();
             ++index)
        {
            const auto& candidate =
                located.Diagnostics.EnhancedCandidates[index];
            if (candidate.Rva == 0 ||
                (candidate.Relations &
                    DynamicProbeEventObjectFlowRelation) == 0)
            {
                continue;
            }
            auto* pre = imageBase + candidate.Rva;
            const std::uint32_t length =
                DynamicProbeWalkerInstructionLength(pre);
            if (length == 0)
            {
                continue;
            }
            auto* post = pre + length;
            if (DynamicProbeEndpointInUse(
                    pre,
                    virtualCount,
                    walkerCount) ||
                DynamicProbeEndpointInUse(
                    post,
                    virtualCount,
                    walkerCount))
            {
                continue;
            }
            auto& site =
                g_dynamicProbeWalkerSites[walkerCount];
            site.Pre = pre;
            site.Post = post;
            site.PreOriginal =
                reinterpret_cast<const std::uint8_t*>(pre)[0];
            site.PostOriginal =
                reinterpret_cast<const std::uint8_t*>(post)[0];
            site.WalkerRva = candidate.Rva;
            ++walkerCount;
        }

        g_dynamicProbeVirtualSiteCount.store(
            virtualCount,
            std::memory_order_release);
        g_dynamicProbeWalkerSiteCount.store(
            walkerCount,
            std::memory_order_release);
        g_dynamicProbeArmed.store(
            virtualCount != 0,
            std::memory_order_release);
        BuildDynamicProbeLogPath();
        if (CEventGenerator::Configuration::LogEnabled() &&
            g_dynamicProbeLogPath[0] != L'\0')
        {
            static_cast<void>(
                DeleteFileW(
                    g_dynamicProbeLogPath.data()));
        }
    }

    [[nodiscard]] bool QueueEnableRequired() noexcept
    {
        for (void* target : g_targets)
        {
            if (target == nullptr || MH_QueueEnableHook(target) != MH_OK)
            {
                return false;
            }
        }

        return g_damageTarget != nullptr &&
            MH_QueueEnableHook(g_damageTarget) == MH_OK;
    }
}

namespace CEventGenerator
{
    Status InitializeEventHooks() noexcept
    {
        Status expected = Status::Uninitialized;
        if (!g_status.compare_exchange_strong(
                expected,
                Status::Initializing,
                std::memory_order_acq_rel))
        {
            return expected;
        }

        InitializeDetailedIds();
        InitializeCaptureStreamMasks();
        InitializeCatalogCaptureStreamMasks();

        LARGE_INTEGER frequency{};
        if (QueryPerformanceFrequency(&frequency) == FALSE ||
            frequency.QuadPart <= 0)
        {
            g_status.store(
                Status::HookInstallationFailed,
                std::memory_order_release);
            return Status::HookInstallationFailed;
        }

        g_performanceFrequency.store(
            static_cast<std::uint64_t>(frequency.QuadPart),
            std::memory_order_release);

        EventLocatorResult located = LocateEventHooks();
        g_gameEdition.store(located.Targets.Edition, std::memory_order_release);
        g_gameBuild.store(located.Targets.GameBuild, std::memory_order_release);
        g_locatorFailure.store(
            located.Diagnostics.Failure,
            std::memory_order_release);
        g_pedMatchCount.store(
            located.Diagnostics.PedMatchCount,
            std::memory_order_release);
        g_globalMatchCount.store(
            located.Diagnostics.GlobalMatchCount,
            std::memory_order_release);
        g_damageMatchCount.store(
            located.Diagnostics.DamageMatchCount,
            std::memory_order_release);
        g_cEventCountAnchorCount.store(
            located.Diagnostics.CEventCountAnchorCount,
            std::memory_order_release);
        g_cEventStackAnchorCount.store(
            located.Diagnostics.CEventStackAnchorCount,
            std::memory_order_release);
        g_eventTypeAnchorCount.store(
            located.Diagnostics.EventTypeAnchorCount,
            std::memory_order_release);
        g_cEventCountOffset.store(
            located.Diagnostics.CEventCountOffset,
            std::memory_order_release);
        g_cEventStackOffset.store(
            located.Diagnostics.CEventStackOffset,
            std::memory_order_release);
        g_eventTypeVFuncOffset.store(
            located.Diagnostics.EventTypeVFuncOffset,
            std::memory_order_release);
        g_eventIdVFuncOffset.store(
            located.Targets.EventIdVFuncOffset,
            std::memory_order_release);
        g_eventArgumentsVFuncOffset.store(
            located.Targets.EventArgumentsVFuncOffset,
            std::memory_order_release);
        g_eventEntityVFuncOffset.store(
            located.Targets.EventEntityVFuncOffset,
            std::memory_order_release);
        g_cEventStackFunctionMatchCount.store(
            located.Diagnostics.CEventStackFunctionMatchCount,
            std::memory_order_release);
        g_cEventStackAnchorRva.store(
            located.Diagnostics.CEventStackAnchorRva,
            std::memory_order_release);
        g_cEventCountFunctionRva.store(
            located.Diagnostics.CEventCountFunctionRva,
            std::memory_order_release);
        g_cEventCountFunctionSize.store(
            located.Diagnostics.CEventCountFunctionSize,
            std::memory_order_release);
        g_eventTypeFunctionRva.store(
            located.Diagnostics.EventTypeFunctionRva,
            std::memory_order_release);
        g_eventTypeFunctionSize.store(
            located.Diagnostics.EventTypeFunctionSize,
            std::memory_order_release);
        g_eventWalkerCandidateCount.store(
            located.Diagnostics.EventWalkerCandidateCount,
            std::memory_order_release);
        g_eventWalkerCandidateRva.store(
            located.Diagnostics.EventWalkerCandidateRva,
            std::memory_order_release);
        g_eventWalkerCandidateSize.store(
            located.Diagnostics.EventWalkerCandidateSize,
            std::memory_order_release);
        g_typedEventWalkerCandidateCount.store(
            located.Diagnostics.TypedEventWalkerCandidateCount,
            std::memory_order_release);
        g_typedEventWalkerCandidateRva.store(
            located.Diagnostics.TypedEventWalkerCandidateRva,
            std::memory_order_release);
        g_typedEventWalkerCandidateSize.store(
            located.Diagnostics.TypedEventWalkerCandidateSize,
            std::memory_order_release);
        g_directCountCallerCount.store(
            located.Diagnostics.DirectCountCallerCount,
            std::memory_order_release);
        g_directEventTypeCallerCount.store(
            located.Diagnostics.DirectEventTypeCallerCount,
            std::memory_order_release);
        g_relatedEventWalkerCandidateCount.store(
            located.Diagnostics.RelatedEventWalkerCandidateCount,
            std::memory_order_release);
        g_guardFlags.store(located.Diagnostics.GuardFlags, std::memory_order_release);
        g_guardCFFunctionCount.store(located.Diagnostics.GuardCFFunctionCount, std::memory_order_release);
        g_guardLongJumpTargetCount.store(located.Diagnostics.GuardLongJumpTargetCount, std::memory_order_release);
        g_guardAddressTakenIatCount.store(located.Diagnostics.GuardAddressTakenIatCount, std::memory_order_release);
        g_enhancedCandidateDiagnosticCount.store(
            located.Diagnostics.EnhancedCandidateDiagnosticCount,
            std::memory_order_release);
        for (std::size_t index = 0;
             index < EnhancedDiagnosticCandidateCapacity;
             ++index)
        {
            g_enhancedCandidateRvas[index].store(
                located.Diagnostics.EnhancedCandidates[index].Rva,
                std::memory_order_release);
            g_enhancedCandidateSizes[index].store(
                located.Diagnostics.EnhancedCandidates[index].Size,
                std::memory_order_release);
            g_enhancedCandidateRelations[index].store(
                located.Diagnostics.EnhancedCandidates[index].Relations,
                std::memory_order_release);
        }
        g_dispatchCallsiteCandidateCount.store(
            located.Diagnostics.DispatchCallsiteCandidateCount,
            std::memory_order_release);
        g_dispatchCallsiteDiagnosticCount.store(
            located.Diagnostics.DispatchCallsiteDiagnosticCount,
            std::memory_order_release);
        for (std::size_t index = 0;
             index < EnhancedDispatchCallsiteDiagnosticCapacity;
             ++index)
        {
            g_dispatchCallsiteWalkerRvas[index].store(
                located.Diagnostics.DispatchCallsites[index].WalkerRva,
                std::memory_order_release);
            g_dispatchCallsiteRvas[index].store(
                located.Diagnostics.DispatchCallsites[index].CallsiteRva,
                std::memory_order_release);
            g_dispatchCallsiteCalleeRvas[index].store(
                located.Diagnostics.DispatchCallsites[index].CalleeRva,
                std::memory_order_release);
            g_dispatchCallsiteRelations[index].store(
                located.Diagnostics.DispatchCallsites[index].Relations,
                std::memory_order_release);
        }
        g_dispatchCalleeCandidateCount.store(
            located.Diagnostics.DispatchCalleeCandidateCount,
            std::memory_order_release);
        g_dispatchCalleeDiagnosticCount.store(
            located.Diagnostics.DispatchCalleeDiagnosticCount,
            std::memory_order_release);
        for (std::size_t index = 0;
             index < EnhancedDispatchCalleeDiagnosticCapacity;
             ++index)
        {
            g_dispatchCalleeRvas[index].store(
                located.Diagnostics.DispatchCallees[index].Rva,
                std::memory_order_release);
            g_dispatchCalleeSizes[index].store(
                located.Diagnostics.DispatchCallees[index].Size,
                std::memory_order_release);
            g_dispatchCalleeRelations[index].store(
                located.Diagnostics.DispatchCallees[index].Relations,
                std::memory_order_release);
            g_dispatchCalleeCallsiteCounts[index].store(
                located.Diagnostics.DispatchCallees[index].CallsiteCount,
                std::memory_order_release);
            g_dispatchCalleeWalkerCounts[index].store(
                located.Diagnostics.DispatchCallees[index].WalkerCount,
                std::memory_order_release);
        }
        g_dispatchForwardCallsiteCandidateCount.store(
            located.Diagnostics.DispatchForwardCallsiteCandidateCount,
            std::memory_order_release);
        g_dispatchForwardCallsiteDiagnosticCount.store(
            located.Diagnostics.DispatchForwardCallsiteDiagnosticCount,
            std::memory_order_release);
        for (std::size_t index = 0;
             index < EnhancedDispatchForwardCallsiteDiagnosticCapacity;
             ++index)
        {
            g_dispatchForwardCallsiteParentRvas[index].store(
                located.Diagnostics.DispatchForwardCallsites[index].ParentRva,
                std::memory_order_release);
            g_dispatchForwardCallsiteRvas[index].store(
                located.Diagnostics.DispatchForwardCallsites[index].CallsiteRva,
                std::memory_order_release);
            g_dispatchForwardCallsiteCalleeRvas[index].store(
                located.Diagnostics.DispatchForwardCallsites[index].CalleeRva,
                std::memory_order_release);
            g_dispatchForwardCallsiteEventArgumentMasks[index].store(
                located.Diagnostics.DispatchForwardCallsites[index].EventArgumentMask,
                std::memory_order_release);
            g_dispatchForwardCallsiteGroupArgumentMasks[index].store(
                located.Diagnostics.DispatchForwardCallsites[index].GroupArgumentMask,
                std::memory_order_release);
        }
        g_dispatchForwardCalleeCandidateCount.store(
            located.Diagnostics.DispatchForwardCalleeCandidateCount,
            std::memory_order_release);
        g_dispatchForwardCalleeDiagnosticCount.store(
            located.Diagnostics.DispatchForwardCalleeDiagnosticCount,
            std::memory_order_release);
        for (std::size_t index = 0;
             index < EnhancedDispatchForwardCalleeDiagnosticCapacity;
             ++index)
        {
            g_dispatchForwardCalleeRvas[index].store(
                located.Diagnostics.DispatchForwardCallees[index].Rva,
                std::memory_order_release);
            g_dispatchForwardCalleeSizes[index].store(
                located.Diagnostics.DispatchForwardCallees[index].Size,
                std::memory_order_release);
            g_dispatchForwardCalleeRelations[index].store(
                located.Diagnostics.DispatchForwardCallees[index].Relations,
                std::memory_order_release);
            g_dispatchForwardCalleeCallsiteCounts[index].store(
                located.Diagnostics.DispatchForwardCallees[index].CallsiteCount,
                std::memory_order_release);
            g_dispatchForwardCalleeParentCounts[index].store(
                located.Diagnostics.DispatchForwardCallees[index].ParentCount,
                std::memory_order_release);
            g_dispatchForwardCalleeEventArgumentMasks[index].store(
                located.Diagnostics.DispatchForwardCallees[index].EventArgumentMask,
                std::memory_order_release);
            g_dispatchForwardCalleeGroupArgumentMasks[index].store(
                located.Diagnostics.DispatchForwardCallees[index].GroupArgumentMask,
                std::memory_order_release);
        }
        g_eventConsumerCallsiteCandidateCount.store(
            located.Diagnostics.EventConsumerCallsiteCandidateCount,
            std::memory_order_release);
        g_eventConsumerCallsiteDiagnosticCount.store(
            located.Diagnostics.EventConsumerCallsiteDiagnosticCount,
            std::memory_order_release);
        for (std::size_t index = 0;
             index < EnhancedEventConsumerCallsiteDiagnosticCapacity;
             ++index)
        {
            g_eventConsumerCallsiteWalkerRvas[index].store(
                located.Diagnostics.EventConsumerCallsites[index].WalkerRva,
                std::memory_order_release);
            g_eventConsumerCallsiteRvas[index].store(
                located.Diagnostics.EventConsumerCallsites[index].CallsiteRva,
                std::memory_order_release);
            g_eventConsumerCallsiteCalleeRvas[index].store(
                located.Diagnostics.EventConsumerCallsites[index].CalleeRva,
                std::memory_order_release);
            g_eventConsumerCallsiteEventArgumentMasks[index].store(
                located.Diagnostics.EventConsumerCallsites[index].EventArgumentMask,
                std::memory_order_release);
            const auto& callsite = located.Diagnostics.EventConsumerCallsites[index];
            g_eventConsumerCallsiteRelations[index].store(
                callsite.Relations,
                std::memory_order_release);
            g_eventConsumerCallsiteInstructionLengths[index].store(
                callsite.InstructionLength, std::memory_order_release);
            g_eventConsumerCallsiteEncodings[index][0].store(callsite.Encoding0, std::memory_order_release);
            g_eventConsumerCallsiteEncodings[index][1].store(callsite.Encoding1, std::memory_order_release);
            g_eventConsumerCallsiteEncodings[index][2].store(callsite.Encoding2, std::memory_order_release);
            g_eventConsumerCallsiteEncodings[index][3].store(callsite.Encoding3, std::memory_order_release);
            g_eventConsumerCallsiteOperandInfos[index].store(callsite.OperandInfo, std::memory_order_release);
            g_eventConsumerCallsiteDisplacements[index].store(callsite.Displacement, std::memory_order_release);
        }
        g_eventConsumerCandidateCount.store(
            located.Diagnostics.EventConsumerCandidateCount,
            std::memory_order_release);
        g_eventConsumerDiagnosticCount.store(
            located.Diagnostics.EventConsumerDiagnosticCount,
            std::memory_order_release);
        for (std::size_t index = 0;
             index < EnhancedEventConsumerDiagnosticCapacity;
             ++index)
        {
            const auto& consumer = located.Diagnostics.EventConsumers[index];
            g_eventConsumerRvas[index].store(
                consumer.Rva,
                std::memory_order_release);
            g_eventConsumerSizes[index].store(
                consumer.Size,
                std::memory_order_release);
            g_eventConsumerRelations[index].store(
                consumer.Relations,
                std::memory_order_release);
            g_eventConsumerArgumentMasks[index].store(
                consumer.EventArgumentMask,
                std::memory_order_release);
            g_eventConsumerFirstUseRvas[index].store(
                consumer.FirstUseRva,
                std::memory_order_release);
            g_eventConsumerFirstClobberRvas[index].store(
                consumer.FirstClobberRva,
                std::memory_order_release);
            g_eventConsumerCallsiteCounts[index].store(
                consumer.CallsiteCount,
                std::memory_order_release);
            g_eventConsumerWalkerCounts[index].store(
                consumer.WalkerCount,
                std::memory_order_release);
            g_eventConsumerReadCounts[index].store(
                consumer.ReadCount,
                std::memory_order_release);
            g_eventConsumerMemoryStoreCounts[index].store(
                consumer.MemoryStoreCount,
                std::memory_order_release);
            g_eventConsumerCompareCounts[index].store(
                consumer.CompareCount,
                std::memory_order_release);
            g_eventConsumerVtableLoadCounts[index].store(
                consumer.VtableLoadCount,
                std::memory_order_release);
            g_eventConsumerForwardCounts[index].store(
                consumer.ForwardCount,
                std::memory_order_release);
            g_eventConsumerTailForwardCounts[index].store(
                consumer.TailForwardCount,
                std::memory_order_release);
            g_eventConsumerTailInstructionRvas[index].store(consumer.TailInstructionRva, std::memory_order_release);
            g_eventConsumerTailInstructionLengths[index].store(consumer.TailInstructionLength, std::memory_order_release);
            g_eventConsumerTailEncodings[index][0].store(consumer.TailEncoding0, std::memory_order_release);
            g_eventConsumerTailEncodings[index][1].store(consumer.TailEncoding1, std::memory_order_release);
            g_eventConsumerTailEncodings[index][2].store(consumer.TailEncoding2, std::memory_order_release);
            g_eventConsumerTailEncodings[index][3].store(consumer.TailEncoding3, std::memory_order_release);
            g_eventConsumerTailOperandInfos[index].store(consumer.TailOperandInfo, std::memory_order_release);
            g_eventConsumerTailDisplacements[index].store(consumer.TailDisplacement, std::memory_order_release);
            g_eventConsumerTailBaseRegisters[index].store(consumer.TailBaseRegister, std::memory_order_release);
            g_eventConsumerTailIndexRegisters[index].store(consumer.TailIndexRegister, std::memory_order_release);
            g_eventConsumerTailScales[index].store(consumer.TailScale, std::memory_order_release);
            g_eventConsumerTailImageSizes[index].store(consumer.TailImageSize, std::memory_order_release);
            g_eventConsumerTailSlotRvas[index].store(consumer.TailSlotRva, std::memory_order_release);
            g_eventConsumerTailSlotAddresses[index].store(consumer.TailSlotAddress, std::memory_order_release);
            g_eventConsumerTailSlotProtections[index].store(consumer.TailSlotProtection, std::memory_order_release);
            g_eventConsumerTailSlotFlags[index].store(consumer.TailSlotFlags, std::memory_order_release);
            g_eventConsumerTailTargetAddresses[index].store(consumer.TailTargetAddress, std::memory_order_release);
            g_eventConsumerTailTargetProtections[index].store(consumer.TailTargetProtection, std::memory_order_release);
            g_eventConsumerTailTargetFlags[index].store(consumer.TailTargetFlags, std::memory_order_release);
            g_eventConsumerTailTargetModuleBases[index].store(consumer.TailTargetModuleBase, std::memory_order_release);
            g_eventConsumerTailTargetModuleSizes[index].store(consumer.TailTargetModuleSize, std::memory_order_release);
            g_eventConsumerTailTargetModuleRvas[index].store(consumer.TailTargetModuleRva, std::memory_order_release);
        }
        g_virtualSlotCandidateCount.store(
            located.Diagnostics.VirtualSlotCandidateCount,
            std::memory_order_release);
        g_virtualSlotDiagnosticCount.store(
            located.Diagnostics.VirtualSlotDiagnosticCount,
            std::memory_order_release);
        for (std::size_t index = 0;
             index < EnhancedVirtualSlotDiagnosticCapacity;
             ++index)
        {
            const auto& slot = located.Diagnostics.VirtualSlots[index];
            g_virtualSlotOffsets[index].store(slot.SlotOffset, std::memory_order_release);
            g_virtualSlotRelations[index].store(slot.Relations, std::memory_order_release);
            g_virtualSlotCallsiteCounts[index].store(slot.CallsiteCount, std::memory_order_release);
            g_virtualSlotWalkerCounts[index].store(slot.WalkerCount, std::memory_order_release);
            g_virtualSlotEventRcxCounts[index].store(slot.EventRcxCount, std::memory_order_release);
            g_virtualSlotEventArgumentMasks[index].store(slot.EventArgumentMask, std::memory_order_release);
            g_virtualSlotReturnRelations[index].store(slot.ReturnRelations, std::memory_order_release);
            g_virtualSlotReturnUseCounts[index].store(slot.ReturnUseCount, std::memory_order_release);
        }
        g_virtualSlotCallsiteCandidateCount.store(
            located.Diagnostics.VirtualSlotCallsiteCandidateCount,
            std::memory_order_release);
        g_virtualSlotCallsiteDiagnosticCount.store(
            located.Diagnostics.VirtualSlotCallsiteDiagnosticCount,
            std::memory_order_release);
        for (std::size_t index = 0;
             index < EnhancedVirtualSlotCallsiteDiagnosticCapacity;
             ++index)
        {
            const auto& callsite = located.Diagnostics.VirtualSlotCallsites[index];
            g_virtualSlotCallsiteWalkerRvas[index].store(callsite.WalkerRva, std::memory_order_release);
            g_virtualSlotCallsiteRvas[index].store(callsite.CallsiteRva, std::memory_order_release);
            g_virtualSlotCallsiteOffsets[index].store(callsite.SlotOffset, std::memory_order_release);
            g_virtualSlotCallsiteRelations[index].store(callsite.Relations, std::memory_order_release);
            g_virtualSlotCallsiteEventArgumentMasks[index].store(callsite.EventArgumentMask, std::memory_order_release);
            g_virtualSlotCallsiteReturnRelations[index].store(callsite.ReturnRelations, std::memory_order_release);
            g_virtualSlotCallsiteReturnFirstUseRvas[index].store(callsite.ReturnFirstUseRva, std::memory_order_release);
            g_virtualSlotCallsiteReturnFirstClobberRvas[index].store(callsite.ReturnFirstClobberRva, std::memory_order_release);
            g_virtualSlotCallsitePreparedArgumentMasks[index].store(callsite.PreparedArgumentMask, std::memory_order_release);
            g_virtualSlotCallsiteMemoryArgumentMasks[index].store(callsite.MemoryArgumentMask, std::memory_order_release);
            g_virtualSlotCallsiteAddressArgumentMasks[index].store(callsite.AddressArgumentMask, std::memory_order_release);
            g_virtualSlotCallsiteImmediateArgumentMasks[index].store(callsite.ImmediateArgumentMask, std::memory_order_release);
            g_virtualSlotCallsiteZeroArgumentMasks[index].store(callsite.ZeroArgumentMask, std::memory_order_release);
            const std::array<std::array<std::uint32_t, 6>, 3> sources{{
                {
                    callsite.RdxSourceRva,
                    callsite.RdxSourceKind,
                    callsite.RdxSourceEncoding,
                    callsite.RdxSourceOperandInfo,
                    callsite.RdxSourceDisplacement,
                    callsite.RdxSourceValue
                },
                {
                    callsite.R8SourceRva,
                    callsite.R8SourceKind,
                    callsite.R8SourceEncoding,
                    callsite.R8SourceOperandInfo,
                    callsite.R8SourceDisplacement,
                    callsite.R8SourceValue
                },
                {
                    callsite.R9SourceRva,
                    callsite.R9SourceKind,
                    callsite.R9SourceEncoding,
                    callsite.R9SourceOperandInfo,
                    callsite.R9SourceDisplacement,
                    callsite.R9SourceValue
                }
            }};
            for (std::size_t argumentIndex = 0;
                 argumentIndex < sources.size();
                 ++argumentIndex)
            {
                const std::size_t flatIndex = index * 3 + argumentIndex;
                g_virtualSlotCallsiteArgumentSourceRvas[flatIndex].store(
                    sources[argumentIndex][0],
                    std::memory_order_release);
                g_virtualSlotCallsiteArgumentSourceKinds[flatIndex].store(
                    sources[argumentIndex][1],
                    std::memory_order_release);
                g_virtualSlotCallsiteArgumentSourceEncodings[flatIndex].store(
                    sources[argumentIndex][2],
                    std::memory_order_release);
                g_virtualSlotCallsiteArgumentSourceOperandInfos[flatIndex].store(
                    sources[argumentIndex][3],
                    std::memory_order_release);
                g_virtualSlotCallsiteArgumentSourceDisplacements[flatIndex].store(
                    sources[argumentIndex][4],
                    std::memory_order_release);
                g_virtualSlotCallsiteArgumentSourceValues[flatIndex].store(
                    sources[argumentIndex][5],
                    std::memory_order_release);
            }
        }
        g_rttiEventTypeCandidateCount.store(
            located.Diagnostics.RttiEventTypeCandidateCount,
            std::memory_order_release);
        g_rttiEventTypeDiagnosticCount.store(
            located.Diagnostics.RttiEventTypeDiagnosticCount,
            std::memory_order_release);
        for (std::size_t index = 0;
             index < EnhancedRttiEventTypeDiagnosticCapacity;
             ++index)
        {
            const auto& type = located.Diagnostics.RttiEventTypes[index];
            g_rttiEventTypeNameRvas[index].store(
                type.TypeNameRva,
                std::memory_order_release);
            g_rttiEventTypeTypeDescriptorRvas[index].store(
                type.TypeDescriptorRva,
                std::memory_order_release);
            g_rttiEventTypeCompleteObjectLocatorRvas[index].store(
                type.CompleteObjectLocatorRva,
                std::memory_order_release);
            g_rttiEventTypeVftableRvas[index].store(
                type.VftableRva,
                std::memory_order_release);
            g_rttiEventTypeValues[index].store(
                type.EventTypeValue,
                std::memory_order_release);
            g_rttiEventTypeSlot30Rvas[index].store(
                type.Slot30TargetRva,
                std::memory_order_release);
            g_rttiEventTypeSlot48Rvas[index].store(
                type.Slot48TargetRva,
                std::memory_order_release);
            g_rttiEventTypeSlot50Rvas[index].store(
                type.Slot50TargetRva,
                std::memory_order_release);
            g_rttiEventTypeSlot60Rvas[index].store(
                type.Slot60TargetRva,
                std::memory_order_release);
            g_rttiEventTypeSlot68Rvas[index].store(
                type.Slot68TargetRva,
                std::memory_order_release);
            g_rttiEventTypeSlotB0Rvas[index].store(
                type.SlotB0TargetRva,
                std::memory_order_release);
            g_rttiEventTypeSlotF8Rvas[index].store(
                type.SlotF8TargetRva,
                std::memory_order_release);
        }
        if (located.State != Status::Ready)
        {
            ArmDynamicProbe(located);
            g_status.store(located.State, std::memory_order_release);
            return located.State;
        }

        g_targets = located.Targets.Functions;
        g_damageTarget = located.Targets.DamageFunction;
        g_damageProcessTarget = located.Targets.DamageProcessFunction;

        const MH_STATUS initialized = MH_Initialize();
        if (initialized != MH_OK &&
            initialized != MH_ERROR_ALREADY_INITIALIZED)
        {
            g_status.store(
                Status::HookInstallationFailed,
                std::memory_order_release);
            return Status::HookInstallationFailed;
        }

        for (std::size_t index = 0; index < g_targets.size(); ++index)
        {
            const MH_STATUS status = MH_CreateHook(
                g_targets[index],
                reinterpret_cast<void*>(Hooks[index]),
                reinterpret_cast<void**>(&g_originals[index]));
            if (status != MH_OK)
            {
                RemoveCreatedHooks();
                static_cast<void>(MH_Uninitialize());
                g_status.store(
                    Status::HookInstallationFailed,
                    std::memory_order_release);
                return Status::HookInstallationFailed;
            }
        }

        if (MH_CreateHook(
                g_damageTarget,
                reinterpret_cast<void*>(&HookDamage),
                reinterpret_cast<void**>(&g_originalDamage)) != MH_OK)
        {
            RemoveCreatedHooks();
            static_cast<void>(MH_Uninitialize());
            g_status.store(
                Status::HookInstallationFailed,
                std::memory_order_release);
            return Status::HookInstallationFailed;
        }

        if (!QueueEnableRequired() || MH_ApplyQueued() != MH_OK)
        {
            RemoveCreatedHooks();
            static_cast<void>(MH_Uninitialize());
            g_status.store(
                Status::HookInstallationFailed,
                std::memory_order_release);
            return Status::HookInstallationFailed;
        }

        InstallOptionalDamageProcessHook();

        g_status.store(Status::Ready, std::memory_order_release);
        return Status::Ready;
    }

    void ShutdownEventHooks() noexcept
    {
        const Status current = g_status.exchange(
            Status::Stopping,
            std::memory_order_acq_rel);
        if (current == Status::Stopped || current == Status::Uninitialized)
        {
            g_status.store(Status::Stopped, std::memory_order_release);
            return;
        }

        StopDynamicProbe(true);
        RemoveCreatedHooks();
        static_cast<void>(MH_Uninitialize());
        g_status.store(Status::Stopped, std::memory_order_release);
    }

    void AdvanceDynamicProbe() noexcept
    {
        if (!g_dynamicProbeArmed.load(std::memory_order_acquire))
        {
            return;
        }
        const bool down = (GetAsyncKeyState(DynamicProbeToggleKey) & 0x8000) != 0;
        if (down && !g_dynamicProbeToggleDown)
        {
            if (g_dynamicProbeActive.load(std::memory_order_acquire))
            {
                StopDynamicProbe(true);
            }
            else
            {
                static_cast<void>(StartDynamicProbe());
            }
        }
        g_dynamicProbeToggleDown = down;
    }

    bool IsDynamicProbeArmed() noexcept
    {
        return g_dynamicProbeArmed.load(std::memory_order_acquire);
    }

    bool IsDynamicProbeActive() noexcept
    {
        return g_dynamicProbeActive.load(std::memory_order_acquire);
    }

    std::uint32_t GetDynamicProbeSiteCount() noexcept
    {
        return
            g_dynamicProbeVirtualSiteCount.load(std::memory_order_acquire) +
            g_dynamicProbeWalkerSiteCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetDynamicProbeVirtualSiteCount() noexcept
    {
        return g_dynamicProbeVirtualSiteCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetDynamicProbeWalkerSiteCount() noexcept
    {
        return g_dynamicProbeWalkerSiteCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetDynamicProbeSessionCount() noexcept
    {
        return g_dynamicProbeSessionCount.load(std::memory_order_acquire);
    }

    std::uint64_t GetDynamicProbeHitCount() noexcept
    {
        return g_dynamicProbeHitCount.load(std::memory_order_acquire);
    }

    Status GetStatus() noexcept
    {
        return g_status.load(std::memory_order_acquire);
    }

    GameEdition GetGameEdition() noexcept
    {
        return g_gameEdition.load(std::memory_order_acquire);
    }

    std::uint32_t GetGameBuild() noexcept
    {
        return g_gameBuild.load(std::memory_order_acquire);
    }

    LocatorFailure GetLocatorFailure() noexcept
    {
        return g_locatorFailure.load(std::memory_order_acquire);
    }

    std::uint32_t GetPedMatchCount() noexcept
    {
        return g_pedMatchCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetGlobalMatchCount() noexcept
    {
        return g_globalMatchCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetDamageMatchCount() noexcept
    {
        return g_damageMatchCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetCEventCountAnchorCount() noexcept
    {
        return g_cEventCountAnchorCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetCEventStackAnchorCount() noexcept
    {
        return g_cEventStackAnchorCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetEventTypeAnchorCount() noexcept
    {
        return g_eventTypeAnchorCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetCEventCountOffset() noexcept
    {
        return g_cEventCountOffset.load(std::memory_order_acquire);
    }

    std::uint32_t GetCEventStackOffset() noexcept
    {
        return g_cEventStackOffset.load(std::memory_order_acquire);
    }

    std::uint32_t GetEventTypeVFuncOffset() noexcept
    {
        return g_eventTypeVFuncOffset.load(std::memory_order_acquire);
    }

    std::uint32_t GetCEventStackFunctionMatchCount() noexcept
    {
        return g_cEventStackFunctionMatchCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetCEventStackAnchorRva() noexcept
    {
        return g_cEventStackAnchorRva.load(std::memory_order_acquire);
    }

    std::uint32_t GetCEventCountFunctionRva() noexcept
    {
        return g_cEventCountFunctionRva.load(std::memory_order_acquire);
    }

    std::uint32_t GetCEventCountFunctionSize() noexcept
    {
        return g_cEventCountFunctionSize.load(std::memory_order_acquire);
    }

    std::uint32_t GetEventTypeFunctionRva() noexcept
    {
        return g_eventTypeFunctionRva.load(std::memory_order_acquire);
    }

    std::uint32_t GetEventTypeFunctionSize() noexcept
    {
        return g_eventTypeFunctionSize.load(std::memory_order_acquire);
    }

    std::uint32_t GetEventWalkerCandidateCount() noexcept
    {
        return g_eventWalkerCandidateCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetEventWalkerCandidateRva() noexcept
    {
        return g_eventWalkerCandidateRva.load(std::memory_order_acquire);
    }

    std::uint32_t GetEventWalkerCandidateSize() noexcept
    {
        return g_eventWalkerCandidateSize.load(std::memory_order_acquire);
    }

    std::uint32_t GetTypedEventWalkerCandidateCount() noexcept
    {
        return g_typedEventWalkerCandidateCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetTypedEventWalkerCandidateRva() noexcept
    {
        return g_typedEventWalkerCandidateRva.load(std::memory_order_acquire);
    }

    std::uint32_t GetTypedEventWalkerCandidateSize() noexcept
    {
        return g_typedEventWalkerCandidateSize.load(std::memory_order_acquire);
    }

    std::uint32_t GetDirectCountCallerCount() noexcept
    {
        return g_directCountCallerCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetDirectEventTypeCallerCount() noexcept
    {
        return g_directEventTypeCallerCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetRelatedEventWalkerCandidateCount() noexcept
    {
        return g_relatedEventWalkerCandidateCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetGuardFlags() noexcept
    {
        return g_guardFlags.load(std::memory_order_acquire);
    }

    std::uint32_t GetGuardCFFunctionCount() noexcept
    {
        return g_guardCFFunctionCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetGuardLongJumpTargetCount() noexcept
    {
        return g_guardLongJumpTargetCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetGuardAddressTakenIatCount() noexcept
    {
        return g_guardAddressTakenIatCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetEnhancedCandidateDiagnosticCount() noexcept
    {
        return g_enhancedCandidateDiagnosticCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetEnhancedCandidateRva(std::uint32_t index) noexcept
    {
        return index < g_enhancedCandidateRvas.size()
            ? g_enhancedCandidateRvas[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEnhancedCandidateSize(std::uint32_t index) noexcept
    {
        return index < g_enhancedCandidateSizes.size()
            ? g_enhancedCandidateSizes[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEnhancedCandidateRelations(std::uint32_t index) noexcept
    {
        return index < g_enhancedCandidateRelations.size()
            ? g_enhancedCandidateRelations[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchCallsiteCandidateCount() noexcept
    {
        return g_dispatchCallsiteCandidateCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetDispatchCallsiteDiagnosticCount() noexcept
    {
        return g_dispatchCallsiteDiagnosticCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetDispatchCallsiteWalkerRva(std::uint32_t index) noexcept
    {
        return index < g_dispatchCallsiteWalkerRvas.size()
            ? g_dispatchCallsiteWalkerRvas[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchCallsiteRva(std::uint32_t index) noexcept
    {
        return index < g_dispatchCallsiteRvas.size()
            ? g_dispatchCallsiteRvas[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchCallsiteCalleeRva(std::uint32_t index) noexcept
    {
        return index < g_dispatchCallsiteCalleeRvas.size()
            ? g_dispatchCallsiteCalleeRvas[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchCallsiteRelations(std::uint32_t index) noexcept
    {
        return index < g_dispatchCallsiteRelations.size()
            ? g_dispatchCallsiteRelations[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchCalleeCandidateCount() noexcept
    {
        return g_dispatchCalleeCandidateCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetDispatchCalleeDiagnosticCount() noexcept
    {
        return g_dispatchCalleeDiagnosticCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetDispatchCalleeRva(std::uint32_t index) noexcept
    {
        return index < g_dispatchCalleeRvas.size()
            ? g_dispatchCalleeRvas[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchCalleeSize(std::uint32_t index) noexcept
    {
        return index < g_dispatchCalleeSizes.size()
            ? g_dispatchCalleeSizes[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchCalleeRelations(std::uint32_t index) noexcept
    {
        return index < g_dispatchCalleeRelations.size()
            ? g_dispatchCalleeRelations[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchCalleeCallsiteCount(std::uint32_t index) noexcept
    {
        return index < g_dispatchCalleeCallsiteCounts.size()
            ? g_dispatchCalleeCallsiteCounts[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchCalleeWalkerCount(std::uint32_t index) noexcept
    {
        return index < g_dispatchCalleeWalkerCounts.size()
            ? g_dispatchCalleeWalkerCounts[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCallsiteCandidateCount() noexcept
    {
        return g_dispatchForwardCallsiteCandidateCount.load(
            std::memory_order_acquire);
    }

    std::uint32_t GetDispatchForwardCallsiteDiagnosticCount() noexcept
    {
        return g_dispatchForwardCallsiteDiagnosticCount.load(
            std::memory_order_acquire);
    }

    std::uint32_t GetDispatchForwardCallsiteParentRva(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCallsiteParentRvas.size()
            ? g_dispatchForwardCallsiteParentRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCallsiteRva(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCallsiteRvas.size()
            ? g_dispatchForwardCallsiteRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCallsiteCalleeRva(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCallsiteCalleeRvas.size()
            ? g_dispatchForwardCallsiteCalleeRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCallsiteEventArgumentMask(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCallsiteEventArgumentMasks.size()
            ? g_dispatchForwardCallsiteEventArgumentMasks[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCallsiteGroupArgumentMask(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCallsiteGroupArgumentMasks.size()
            ? g_dispatchForwardCallsiteGroupArgumentMasks[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCalleeCandidateCount() noexcept
    {
        return g_dispatchForwardCalleeCandidateCount.load(
            std::memory_order_acquire);
    }

    std::uint32_t GetDispatchForwardCalleeDiagnosticCount() noexcept
    {
        return g_dispatchForwardCalleeDiagnosticCount.load(
            std::memory_order_acquire);
    }

    std::uint32_t GetDispatchForwardCalleeRva(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCalleeRvas.size()
            ? g_dispatchForwardCalleeRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCalleeSize(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCalleeSizes.size()
            ? g_dispatchForwardCalleeSizes[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCalleeRelations(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCalleeRelations.size()
            ? g_dispatchForwardCalleeRelations[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCalleeCallsiteCount(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCalleeCallsiteCounts.size()
            ? g_dispatchForwardCalleeCallsiteCounts[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCalleeParentCount(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCalleeParentCounts.size()
            ? g_dispatchForwardCalleeParentCounts[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCalleeEventArgumentMask(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCalleeEventArgumentMasks.size()
            ? g_dispatchForwardCalleeEventArgumentMasks[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetDispatchForwardCalleeGroupArgumentMask(
        std::uint32_t index) noexcept
    {
        return index < g_dispatchForwardCalleeGroupArgumentMasks.size()
            ? g_dispatchForwardCalleeGroupArgumentMasks[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCallsiteCandidateCount() noexcept
    {
        return g_eventConsumerCallsiteCandidateCount.load(
            std::memory_order_acquire);
    }

    std::uint32_t GetEventConsumerCallsiteDiagnosticCount() noexcept
    {
        return g_eventConsumerCallsiteDiagnosticCount.load(
            std::memory_order_acquire);
    }

    std::uint32_t GetEventConsumerCallsiteWalkerRva(
        std::uint32_t index) noexcept
    {
        return index < g_eventConsumerCallsiteWalkerRvas.size()
            ? g_eventConsumerCallsiteWalkerRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCallsiteRva(
        std::uint32_t index) noexcept
    {
        return index < g_eventConsumerCallsiteRvas.size()
            ? g_eventConsumerCallsiteRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCallsiteCalleeRva(
        std::uint32_t index) noexcept
    {
        return index < g_eventConsumerCallsiteCalleeRvas.size()
            ? g_eventConsumerCallsiteCalleeRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCallsiteEventArgumentMask(
        std::uint32_t index) noexcept
    {
        return index < g_eventConsumerCallsiteEventArgumentMasks.size()
            ? g_eventConsumerCallsiteEventArgumentMasks[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCallsiteRelations(
        std::uint32_t index) noexcept
    {
        return index < g_eventConsumerCallsiteRelations.size()
            ? g_eventConsumerCallsiteRelations[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCallsiteInstructionLength(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerCallsiteInstructionLengths.size()
            ? g_eventConsumerCallsiteInstructionLengths[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCallsiteEncoding(std::uint32_t index, std::uint32_t word) noexcept
    {
        return index < g_eventConsumerCallsiteEncodings.size() && word < 4
            ? g_eventConsumerCallsiteEncodings[index][word].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCallsiteOperandInfo(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerCallsiteOperandInfos.size()
            ? g_eventConsumerCallsiteOperandInfos[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCallsiteDisplacement(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerCallsiteDisplacements.size()
            ? g_eventConsumerCallsiteDisplacements[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCandidateCount() noexcept
    {
        return g_eventConsumerCandidateCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetEventConsumerDiagnosticCount() noexcept
    {
        return g_eventConsumerDiagnosticCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetEventConsumerRva(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerRvas.size()
            ? g_eventConsumerRvas[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerSize(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerSizes.size()
            ? g_eventConsumerSizes[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerRelations(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerRelations.size()
            ? g_eventConsumerRelations[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerArgumentMask(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerArgumentMasks.size()
            ? g_eventConsumerArgumentMasks[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerFirstUseRva(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerFirstUseRvas.size()
            ? g_eventConsumerFirstUseRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerFirstClobberRva(
        std::uint32_t index) noexcept
    {
        return index < g_eventConsumerFirstClobberRvas.size()
            ? g_eventConsumerFirstClobberRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCallsiteCount(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerCallsiteCounts.size()
            ? g_eventConsumerCallsiteCounts[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerWalkerCount(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerWalkerCounts.size()
            ? g_eventConsumerWalkerCounts[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerReadCount(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerReadCounts.size()
            ? g_eventConsumerReadCounts[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerMemoryStoreCount(
        std::uint32_t index) noexcept
    {
        return index < g_eventConsumerMemoryStoreCounts.size()
            ? g_eventConsumerMemoryStoreCounts[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerCompareCount(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerCompareCounts.size()
            ? g_eventConsumerCompareCounts[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerVtableLoadCount(
        std::uint32_t index) noexcept
    {
        return index < g_eventConsumerVtableLoadCounts.size()
            ? g_eventConsumerVtableLoadCounts[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerForwardCount(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerForwardCounts.size()
            ? g_eventConsumerForwardCounts[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerTailForwardCount(
        std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailForwardCounts.size()
            ? g_eventConsumerTailForwardCounts[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetEventConsumerTailInstructionRva(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailInstructionRvas.size() ? g_eventConsumerTailInstructionRvas[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailInstructionLength(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailInstructionLengths.size() ? g_eventConsumerTailInstructionLengths[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailEncoding(std::uint32_t index, std::uint32_t word) noexcept
    {
        return index < g_eventConsumerTailEncodings.size() && word < 4 ? g_eventConsumerTailEncodings[index][word].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailOperandInfo(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailOperandInfos.size() ? g_eventConsumerTailOperandInfos[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailDisplacement(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailDisplacements.size() ? g_eventConsumerTailDisplacements[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailBaseRegister(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailBaseRegisters.size() ? g_eventConsumerTailBaseRegisters[index].load(std::memory_order_acquire) : 0xFFFFFFFFu;
    }

    std::uint32_t GetEventConsumerTailIndexRegister(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailIndexRegisters.size() ? g_eventConsumerTailIndexRegisters[index].load(std::memory_order_acquire) : 0xFFFFFFFFu;
    }

    std::uint32_t GetEventConsumerTailScale(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailScales.size() ? g_eventConsumerTailScales[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailImageSize(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailImageSizes.size() ? g_eventConsumerTailImageSizes[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailSlotRva(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailSlotRvas.size() ? g_eventConsumerTailSlotRvas[index].load(std::memory_order_acquire) : 0xFFFFFFFFu;
    }

    std::uint64_t GetEventConsumerTailSlotAddress(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailSlotAddresses.size() ? g_eventConsumerTailSlotAddresses[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailSlotProtection(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailSlotProtections.size() ? g_eventConsumerTailSlotProtections[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailSlotFlags(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailSlotFlags.size() ? g_eventConsumerTailSlotFlags[index].load(std::memory_order_acquire) : 0;
    }

    std::uint64_t GetEventConsumerTailTargetAddress(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailTargetAddresses.size() ? g_eventConsumerTailTargetAddresses[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailTargetProtection(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailTargetProtections.size() ? g_eventConsumerTailTargetProtections[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailTargetFlags(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailTargetFlags.size() ? g_eventConsumerTailTargetFlags[index].load(std::memory_order_acquire) : 0;
    }

    std::uint64_t GetEventConsumerTailTargetModuleBase(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailTargetModuleBases.size() ? g_eventConsumerTailTargetModuleBases[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailTargetModuleSize(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailTargetModuleSizes.size() ? g_eventConsumerTailTargetModuleSizes[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetEventConsumerTailTargetModuleRva(std::uint32_t index) noexcept
    {
        return index < g_eventConsumerTailTargetModuleRvas.size() ? g_eventConsumerTailTargetModuleRvas[index].load(std::memory_order_acquire) : 0;
    }

    std::uint32_t GetVirtualSlotCandidateCount() noexcept
    {
        return g_virtualSlotCandidateCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetVirtualSlotDiagnosticCount() noexcept
    {
        return g_virtualSlotDiagnosticCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetVirtualSlotOffset(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotOffsets.size()
            ? g_virtualSlotOffsets[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotRelations(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotRelations.size()
            ? g_virtualSlotRelations[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteCount(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteCounts.size()
            ? g_virtualSlotCallsiteCounts[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotWalkerCount(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotWalkerCounts.size()
            ? g_virtualSlotWalkerCounts[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotEventRcxCount(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotEventRcxCounts.size()
            ? g_virtualSlotEventRcxCounts[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotEventArgumentMask(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotEventArgumentMasks.size()
            ? g_virtualSlotEventArgumentMasks[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotReturnRelations(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotReturnRelations.size()
            ? g_virtualSlotReturnRelations[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotReturnUseCount(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotReturnUseCounts.size()
            ? g_virtualSlotReturnUseCounts[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteCandidateCount() noexcept
    {
        return g_virtualSlotCallsiteCandidateCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetVirtualSlotCallsiteDiagnosticCount() noexcept
    {
        return g_virtualSlotCallsiteDiagnosticCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetVirtualSlotCallsiteWalkerRva(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteWalkerRvas.size()
            ? g_virtualSlotCallsiteWalkerRvas[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteRva(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteRvas.size()
            ? g_virtualSlotCallsiteRvas[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteOffset(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteOffsets.size()
            ? g_virtualSlotCallsiteOffsets[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteRelations(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteRelations.size()
            ? g_virtualSlotCallsiteRelations[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteEventArgumentMask(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteEventArgumentMasks.size()
            ? g_virtualSlotCallsiteEventArgumentMasks[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteReturnRelations(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteReturnRelations.size()
            ? g_virtualSlotCallsiteReturnRelations[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteReturnFirstUseRva(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteReturnFirstUseRvas.size()
            ? g_virtualSlotCallsiteReturnFirstUseRvas[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteReturnFirstClobberRva(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteReturnFirstClobberRvas.size()
            ? g_virtualSlotCallsiteReturnFirstClobberRvas[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsitePreparedArgumentMask(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsitePreparedArgumentMasks.size()
            ? g_virtualSlotCallsitePreparedArgumentMasks[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteMemoryArgumentMask(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteMemoryArgumentMasks.size()
            ? g_virtualSlotCallsiteMemoryArgumentMasks[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteAddressArgumentMask(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteAddressArgumentMasks.size()
            ? g_virtualSlotCallsiteAddressArgumentMasks[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteImmediateArgumentMask(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteImmediateArgumentMasks.size()
            ? g_virtualSlotCallsiteImmediateArgumentMasks[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteZeroArgumentMask(std::uint32_t index) noexcept
    {
        return index < g_virtualSlotCallsiteZeroArgumentMasks.size()
            ? g_virtualSlotCallsiteZeroArgumentMasks[index].load(std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetVirtualSlotCallsiteArgumentSourceRva(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept
    {
        if (index >= EnhancedVirtualSlotCallsiteDiagnosticCapacity ||
            argumentIndex >= 3)
        {
            return 0;
        }
        const std::size_t flatIndex =
            static_cast<std::size_t>(index) * 3 + argumentIndex;
        return g_virtualSlotCallsiteArgumentSourceRvas[flatIndex].load(
            std::memory_order_acquire);
    }

    std::uint32_t GetVirtualSlotCallsiteArgumentSourceKind(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept
    {
        if (index >= EnhancedVirtualSlotCallsiteDiagnosticCapacity ||
            argumentIndex >= 3)
        {
            return 0;
        }
        const std::size_t flatIndex =
            static_cast<std::size_t>(index) * 3 + argumentIndex;
        return g_virtualSlotCallsiteArgumentSourceKinds[flatIndex].load(
            std::memory_order_acquire);
    }

    std::uint32_t GetVirtualSlotCallsiteArgumentSourceEncoding(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept
    {
        if (index >= EnhancedVirtualSlotCallsiteDiagnosticCapacity ||
            argumentIndex >= 3)
        {
            return 0;
        }
        const std::size_t flatIndex =
            static_cast<std::size_t>(index) * 3 + argumentIndex;
        return g_virtualSlotCallsiteArgumentSourceEncodings[flatIndex].load(
            std::memory_order_acquire);
    }

    std::uint32_t GetVirtualSlotCallsiteArgumentSourceOperandInfo(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept
    {
        if (index >= EnhancedVirtualSlotCallsiteDiagnosticCapacity ||
            argumentIndex >= 3)
        {
            return 0;
        }
        const std::size_t flatIndex =
            static_cast<std::size_t>(index) * 3 + argumentIndex;
        return g_virtualSlotCallsiteArgumentSourceOperandInfos[flatIndex].load(
            std::memory_order_acquire);
    }

    std::uint32_t GetVirtualSlotCallsiteArgumentSourceDisplacement(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept
    {
        if (index >= EnhancedVirtualSlotCallsiteDiagnosticCapacity ||
            argumentIndex >= 3)
        {
            return 0;
        }
        const std::size_t flatIndex =
            static_cast<std::size_t>(index) * 3 + argumentIndex;
        return g_virtualSlotCallsiteArgumentSourceDisplacements[flatIndex].load(
            std::memory_order_acquire);
    }

    std::uint32_t GetVirtualSlotCallsiteArgumentSourceValue(
        std::uint32_t index,
        std::uint32_t argumentIndex) noexcept
    {
        if (index >= EnhancedVirtualSlotCallsiteDiagnosticCapacity ||
            argumentIndex >= 3)
        {
            return 0;
        }
        const std::size_t flatIndex =
            static_cast<std::size_t>(index) * 3 + argumentIndex;
        return g_virtualSlotCallsiteArgumentSourceValues[flatIndex].load(
            std::memory_order_acquire);
    }

    std::uint32_t GetRttiEventTypeCandidateCount() noexcept
    {
        return g_rttiEventTypeCandidateCount.load(std::memory_order_acquire);
    }

    std::uint32_t GetRttiEventTypeDiagnosticCount() noexcept
    {
        return g_rttiEventTypeDiagnosticCount.load(std::memory_order_acquire);
    }

    std::uint32_t CopyRttiEventTypeName(
        std::uint32_t index,
        char* destination,
        std::uint32_t destinationSize) noexcept
    {
        if (index >= EnhancedRttiEventTypeDiagnosticCapacity)
        {
            return 0;
        }
        const std::uint32_t rva =
            g_rttiEventTypeNameRvas[index].load(std::memory_order_acquire);
        const HMODULE module = GetModuleHandleW(nullptr);
        if (module == nullptr || rva == 0)
        {
            return 0;
        }
        const char* name = reinterpret_cast<const char*>(
            reinterpret_cast<const std::byte*>(module) + rva);
        std::size_t length = 0;
        while (length < 192 && name[length] != '\0')
        {
            ++length;
        }
        if (length == 192)
        {
            return 0;
        }
        const std::uint32_t required =
            static_cast<std::uint32_t>(length + 1);
        if (destination == nullptr || destinationSize < required)
        {
            return required;
        }
        std::memcpy(destination, name, length);
        destination[length] = '\0';
        return required;
    }

    std::uint32_t GetRttiEventTypeTypeDescriptorRva(
        std::uint32_t index) noexcept
    {
        return index < g_rttiEventTypeTypeDescriptorRvas.size()
            ? g_rttiEventTypeTypeDescriptorRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetRttiEventTypeCompleteObjectLocatorRva(
        std::uint32_t index) noexcept
    {
        return index < g_rttiEventTypeCompleteObjectLocatorRvas.size()
            ? g_rttiEventTypeCompleteObjectLocatorRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetRttiEventTypeVftableRva(
        std::uint32_t index) noexcept
    {
        return index < g_rttiEventTypeVftableRvas.size()
            ? g_rttiEventTypeVftableRvas[index].load(
                std::memory_order_acquire)
            : 0;
    }

    std::uint32_t GetRttiEventTypeEventTypeValue(
        std::uint32_t index) noexcept
    {
        return index < g_rttiEventTypeValues.size()
            ? g_rttiEventTypeValues[index].load(
                std::memory_order_acquire)
            : 0xFFFFFFFFu;
    }

    std::uint32_t GetRttiEventTypeSlotTargetRva(
        std::uint32_t index,
        std::uint32_t slotOffset) noexcept
    {
        if (index >= EnhancedRttiEventTypeDiagnosticCapacity)
        {
            return 0xFFFFFFFFu;
        }
        const auto load =
            [index](
                const auto& values)
            {
                return values[index].load(std::memory_order_acquire);
            };
        switch (slotOffset)
        {
            case 0x30u:
                return load(g_rttiEventTypeSlot30Rvas);
            case 0x48u:
                return load(g_rttiEventTypeSlot48Rvas);
            case 0x50u:
                return load(g_rttiEventTypeSlot50Rvas);
            case 0x60u:
                return load(g_rttiEventTypeSlot60Rvas);
            case 0x68u:
                return load(g_rttiEventTypeSlot68Rvas);
            case 0xB0u:
                return load(g_rttiEventTypeSlotB0Rvas);
            case 0xF8u:
                return load(g_rttiEventTypeSlotF8Rvas);
            default:
                return 0xFFFFFFFFu;
        }
    }

    std::uint64_t GetPerformanceFrequency() noexcept
    {
        return g_performanceFrequency.load(std::memory_order_acquire);
    }

    std::uint64_t GetObservedCount(std::uint32_t eventId) noexcept
    {
        if (const auto* counter = ObservedCounter(eventId); counter != nullptr)
        {
            return counter->load(std::memory_order_acquire);
        }
        return 0;
    }

    void ClearDetailedEventIds() noexcept
    {
        for (auto& value : g_detailedEventIds)
        {
            value.store(EmptyDetailedEventId, std::memory_order_release);
        }
    }

    bool AddDetailedEventId(std::uint32_t eventId) noexcept
    {
        for (auto& value : g_detailedEventIds)
        {
            const std::uint32_t current = value.load(std::memory_order_acquire);
            if (current == eventId)
            {
                return true;
            }
            if (current != EmptyDetailedEventId)
            {
                continue;
            }

            std::uint32_t expected = EmptyDetailedEventId;
            if (value.compare_exchange_strong(
                    expected,
                    eventId,
                    std::memory_order_acq_rel))
            {
                return true;
            }
        }
        return false;
    }

    void ClearCaptureStreamMasks() noexcept
    {
        for (auto& value : g_captureStreamMasks)
        {
            value.store(0, std::memory_order_release);
        }
    }

    bool SetCaptureStreamMask(
        std::uint32_t eventId,
        std::uint32_t streamMask) noexcept
    {
        if (eventId >= g_captureStreamMasks.size() ||
            (streamMask & ~static_cast<std::uint32_t>(
                ReactionStreamMask | GroupStreamMask | GlobalStreamMask)) != 0)
        {
            return false;
        }

        g_captureStreamMasks[eventId].store(
            static_cast<std::uint8_t>(streamMask),
            std::memory_order_release);
        return true;
    }

    std::uint32_t GetCatalogEventCount() noexcept
    {
        return static_cast<std::uint32_t>(CatalogEventNames.size());
    }

    std::uint32_t CopyCatalogEventName(
        std::uint32_t catalogEventId,
        char* destination,
        std::uint32_t destinationSize) noexcept
    {
        std::size_t index = 0;
        if (!TryGetCatalogEventIndex(catalogEventId, index))
        {
            return 0;
        }

        const std::string_view name = CatalogEventNames[index];
        const std::uint32_t required =
            static_cast<std::uint32_t>(name.size() + 1u);
        if (destination == nullptr || destinationSize < required)
        {
            return required;
        }

        std::memcpy(destination, name.data(), name.size());
        destination[name.size()] = '\0';
        return required;
    }

    void ClearCatalogCaptureStreamMasks() noexcept
    {
        for (auto& value : g_catalogCaptureStreamMasks)
        {
            value.store(0, std::memory_order_release);
        }
        g_catalogCaptureEnabled.store(false, std::memory_order_release);
    }

    bool SetCatalogCaptureStreamMask(
        std::uint32_t catalogEventId,
        std::uint32_t streamMask) noexcept
    {
        if ((streamMask & ~static_cast<std::uint32_t>(
                ReactionStreamMask | GroupStreamMask | GlobalStreamMask)) != 0)
        {
            return false;
        }

        std::size_t index = 0;
        if (!TryGetCatalogEventIndex(catalogEventId, index))
        {
            return false;
        }

        g_catalogCaptureStreamMasks[index].store(
            static_cast<std::uint8_t>(streamMask),
            std::memory_order_release);
        if (streamMask != 0)
        {
            g_catalogCaptureEnabled.store(true, std::memory_order_release);
        }
        return true;
    }
}