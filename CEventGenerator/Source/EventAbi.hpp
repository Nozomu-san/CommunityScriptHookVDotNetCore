#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <type_traits>
#include <utility>

namespace CEventGenerator
{
    inline constexpr std::uint32_t ArgumentCapacity = 48;
    inline constexpr std::uint32_t ProbeCapacity = 576;
    inline constexpr std::uint32_t EntityDamageMetadataEventId = 0xFFFF0001u;

    enum class Status : std::uint32_t
    {
        Uninitialized = 0,
        Initializing = 1,
        Ready = 2,
        UnsupportedGameBuild = 3,
        HookNotFound = 4,
        HookInstallationFailed = 5,
        Stopping = 6,
        Stopped = 7
    };

    enum class EventStream : std::uint32_t
    {
        Reaction = 0,
        Group = 1,
        Global = 2,
        Damage = 3
    };

    enum class DamageMagnitudeSource : std::uint32_t
    {
        Unavailable = 0,
        DamageProcess = 1
    };

    enum class RecordFlags : std::uint32_t
    {
        None = 0,
        RelatedEntityAddress = 1 << 0,
        Arguments = 1 << 1,
        DispatchEntityAddress = 1 << 2,
        ProbeData = 1 << 3
    };

    [[nodiscard]]
    constexpr RecordFlags operator|(RecordFlags left, RecordFlags right) noexcept
    {
        return static_cast<RecordFlags>(
            std::to_underlying(left) | std::to_underlying(right));
    }

    struct alignas(8) EventRecord final
    {
        std::uint32_t Size{};
        RecordFlags Flags{};
        std::uint64_t Sequence{};
        std::int64_t PerformanceCounter{};
        std::uint32_t GameBuild{};
        std::uint32_t EventId{};
        std::uint32_t CatalogEventId{};
        EventStream Stream{};
        std::uint32_t ArgumentCount{};
        std::uint64_t GroupAddress{};
        std::uint64_t EventAddress{};
        std::uint64_t RelatedEntityAddress{};
        std::uint64_t DispatchEntityAddress{};
        std::uint32_t DispatchEntityCount{};
        std::uint32_t ProbeSize{};
        std::array<std::byte, ProbeCapacity> ProbeData{};
        std::array<std::uint64_t, ArgumentCapacity> Arguments{};
    };

    static_assert(std::is_trivially_copyable_v<EventRecord>);
    static_assert(sizeof(EventRecord) == 1048);
}