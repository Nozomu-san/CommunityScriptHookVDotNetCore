#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <type_traits>
#include <utility>

namespace CEventGenerator
{
    inline constexpr std::uint32_t BridgeAbiVersion = 2;
    inline constexpr std::uint32_t BridgeCapabilityCatalogIdentity = 1u << 0;
    inline constexpr std::uint32_t BridgeCapabilityRuntimeEventVirtualLayout = 1u << 1;
    inline constexpr std::uint32_t BridgeCapabilityLocatorDiagnostics = 1u << 2;
    inline constexpr std::uint32_t BridgeCapabilityDispatchCalleeDiagnostics = 1u << 3;
    inline constexpr std::uint32_t BridgeCapabilityDispatchTopologyDiagnostics = 1u << 4;
    inline constexpr std::uint32_t BridgeCapabilityDispatchForwardDiagnostics = 1u << 5;
    inline constexpr std::uint32_t BridgeCapabilityEventConsumerDiagnostics = 1u << 6;
    inline constexpr std::uint32_t BridgeCapabilityLeafConsumerRecovery = 1u << 7;
    inline constexpr std::uint32_t BridgeCapabilityEventConsumerTailChain = 1u << 8;
    inline constexpr std::uint32_t BridgeCapabilityIndirectTailResolution = 1u << 9;
    inline constexpr std::uint32_t BridgeCapabilityRegisterTailResolution = 1u << 10;
    inline constexpr std::uint32_t BridgeCapabilityMemoryTailResolution = 1u << 11;
    inline constexpr std::uint32_t BridgeCapabilityFanoutDiagnostics = 1u << 12;
    inline constexpr std::uint32_t BridgeCapabilityVirtualSlotDiagnostics = 1u << 13;
    inline constexpr std::uint32_t BridgeCapabilityTailSlotDiagnostics = 1u << 14;
    inline constexpr std::uint32_t BridgeCapabilityConvergedDiagnostics = 1u << 15;
    inline constexpr std::uint32_t BridgeCapabilityVirtualSemanticDiagnostics = 1u << 16;
    inline constexpr std::uint32_t BridgeCapabilityVirtualInputDiagnostics = 1u << 17;
    inline constexpr std::uint32_t BridgeCapabilityAllInDiagnostics = 1u << 18;
    inline constexpr std::uint32_t BridgeCapabilityDynamicGroundTruth = 1u << 19;
    inline constexpr std::uint32_t BridgeCapabilityDynamicPathAbi = 1u << 20;
    inline constexpr std::uint32_t BridgeCapabilityEventGatedDynamicCorrelation = 1u << 21;
    inline constexpr std::uint32_t LocatorRevision = 2000;
    inline constexpr std::uint32_t BridgeCapabilities =
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

    enum class LocatorFailure : std::uint32_t
    {
        None = 0,
        ImageUnavailable = 1,
        PedMatchCount = 2,
        GlobalMatchCount = 3,
        DamageMatchCount = 4,
        TargetInvalid = 5,
        EnhancedCEventCountAnchor = 6,
        EnhancedCEventStackAnchor = 7,
        EnhancedEventTypeAnchor = 8,
        EnhancedStructureInvalid = 9,
        EnhancedDispatchUnresolved = 10,
        EditionUnsupported = 11
    };

    enum class GameEdition : std::uint32_t
    {
        Unknown = 0,
        Legacy = 1,
        Enhanced = 2
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