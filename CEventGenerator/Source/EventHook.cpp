#include "EventHook.hpp"
#include "EventLocator.hpp"
#include "EventQueue.hpp"

#include <Windows.h>
#include <MinHook.h>

#include <algorithm>
#include <array>
#include <bit>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <iterator>
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

    std::atomic<Status> g_status{Status::Uninitialized};
    std::atomic<std::uint32_t> g_gameBuild{};
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

                if (record.EventId == 29 && record.GameBuild == 3889)
                {
                    constexpr std::ptrdiff_t PrimaryEntityOffset = 0x98;
                    constexpr std::ptrdiff_t MirrorEntityOffset = 0xA0;

                    void* primary =
                        *reinterpret_cast<void**>(bytes + PrimaryEntityOffset);
                    void* mirror =
                        *reinterpret_cast<void**>(bytes + MirrorEntityOffset);

                    const auto address =
                        reinterpret_cast<std::uintptr_t>(primary);

                    if (primary != nullptr &&
                        primary == mirror &&
                        address >= 0x10000)
                    {
                        record.DispatchEntityAddress =
                            static_cast<std::uint64_t>(address);
                        record.DispatchEntityCount = 1;
                        record.Flags =
                            record.Flags | RecordFlags::DispatchEntityAddress;
                    }

                    return;
                }

                constexpr std::ptrdiff_t EntityOffset = 0x90;
                void* entity = *reinterpret_cast<void**>(bytes + EntityOffset);
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

            const std::size_t shift = record.GameBuild >= 2802 ? 6 : 0;
            const auto getId = reinterpret_cast<GetEventId>(vtable[3 + shift]);
            const auto getArguments = reinterpret_cast<GetEventArguments>(
                vtable[6 + shift]);
            const auto getEntity = reinterpret_cast<GetEventEntity>(
                vtable[25 + shift]);

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
        g_gameBuild.store(located.Targets.GameBuild, std::memory_order_release);
        if (located.State != Status::Ready)
        {
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

        RemoveCreatedHooks();
        static_cast<void>(MH_Uninitialize());
        g_status.store(Status::Stopped, std::memory_order_release);
    }

    Status GetStatus() noexcept
    {
        return g_status.load(std::memory_order_acquire);
    }

    std::uint32_t GetGameBuild() noexcept
    {
        return g_gameBuild.load(std::memory_order_acquire);
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