#pragma once

#include "EventAbi.hpp"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <mutex>

namespace CEventGenerator
{
    class EventQueue final
    {
    public:
        static EventQueue& Instance() noexcept;

        EventQueue(const EventQueue&) = delete;
        EventQueue& operator=(const EventQueue&) = delete;

        [[nodiscard]] bool TryPush(const EventRecord& record) noexcept;
        [[nodiscard]] bool TryPop(EventRecord& record) noexcept;
        [[nodiscard]] std::uint64_t PendingCount() const noexcept;
        [[nodiscard]] std::uint64_t DroppedCount() const noexcept;

    private:
        static constexpr std::size_t MaximumPendingRecords = 8192;

        EventQueue() = default;

        mutable std::mutex _gate;
        std::array<EventRecord, MaximumPendingRecords> _records{};
        std::size_t _head{};
        std::size_t _size{};
        std::atomic<std::uint64_t> _dropped{};
    };
}