#include "EventQueue.hpp"

namespace CEventGenerator
{
    EventQueue& EventQueue::Instance() noexcept
    {
        static EventQueue instance;
        return instance;
    }

    bool EventQueue::TryPush(const EventRecord& record) noexcept
    {
        try
        {
            std::unique_lock lock(_gate, std::try_to_lock);
            if (!lock.owns_lock() || _size == MaximumPendingRecords)
            {
                _dropped.fetch_add(1, std::memory_order_relaxed);
                return false;
            }

            _records[(_head + _size) % MaximumPendingRecords] = record;
            ++_size;
            return true;
        }
        catch (...)
        {
            _dropped.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
    }

    bool EventQueue::TryPop(EventRecord& record) noexcept
    {
        try
        {
            std::scoped_lock lock(_gate);
            if (_size == 0)
            {
                return false;
            }

            record = _records[_head];
            _head = (_head + 1) % MaximumPendingRecords;
            --_size;
            return true;
        }
        catch (...)
        {
            return false;
        }
    }

    std::uint64_t EventQueue::PendingCount() const noexcept
    {
        try
        {
            std::scoped_lock lock(_gate);
            return static_cast<std::uint64_t>(_size);
        }
        catch (...)
        {
            return 0;
        }
    }

    std::uint64_t EventQueue::DroppedCount() const noexcept
    {
        return _dropped.load(std::memory_order_relaxed);
    }
}