#pragma once

#include "EventAbi.hpp"

#include <cstdint>

namespace CEventGenerator
{
    [[nodiscard]] Status InitializeEventHooks() noexcept;
    void ShutdownEventHooks() noexcept;
    [[nodiscard]] Status GetStatus() noexcept;
    [[nodiscard]] std::uint32_t GetGameBuild() noexcept;
    [[nodiscard]] std::uint64_t GetPerformanceFrequency() noexcept;
    [[nodiscard]] std::uint64_t GetObservedCount(std::uint32_t eventId) noexcept;
    void ClearDetailedEventIds() noexcept;
    [[nodiscard]] bool AddDetailedEventId(std::uint32_t eventId) noexcept;
}