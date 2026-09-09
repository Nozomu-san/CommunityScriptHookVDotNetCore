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
    constexpr std::size_t DetailedEventIdCapacity = 16;
    constexpr std::size_t DetailedEventProbeCapacity = 128;
    constexpr std::size_t RecentDamageContextCapacity = 32;

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
                constexpr std::ptrdiff_t EntityOffset = 0x90;
                auto* bytes = static_cast<std::byte*>(group);
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

    void CaptureDetailedProbe(EventRecord& record, void* event) noexcept
    {
        if (event == nullptr ||
            (record.EventId != 17 &&
             record.EventId != 29 &&
             record.EventId != 42 &&
             record.EventId != 124 &&
             record.EventId != 125 &&
             record.EventId != 141))
        {
            return;
        }

        const auto* source = static_cast<const std::byte*>(event) + sizeof(void*);
        std::size_t captured = 0;
        const std::size_t limit = std::min<std::size_t>(
            record.ProbeData.size(),
            DetailedEventProbeCapacity);
        while (captured < limit)
        {
            const std::size_t chunk = std::min<std::size_t>(
                limit - captured,
                sizeof(std::uint64_t));
            bool readable = true;
            __try
            {
                std::memcpy(
                    record.ProbeData.data() + captured,
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

        if (captured != 0)
        {
            record.ProbeSize = static_cast<std::uint32_t>(captured);
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

            record.EventId = static_cast<std::uint32_t>(getId(event));
            CountObserved(record.EventId);
            if (IsDetailedEventId(record.EventId))
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

            if (IsDetailedEventId(record.EventId))
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

            CaptureDetailedProbe(record, event);
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
            std::move(
                g_recentDamageContexts.begin() + 1,
                g_recentDamageContexts.end(),
                g_recentDamageContexts.begin());
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
            for (std::size_t move = index + 1;
                 move < g_recentDamageContextCount;
                 ++move)
            {
                g_recentDamageContexts[move - 1] =
                    g_recentDamageContexts[move];
            }
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
}