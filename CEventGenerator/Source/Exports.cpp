#include "EventAbi.hpp"
#include "EventHook.hpp"
#include "EventQueue.hpp"

#include <cstdint>
#include <cstring>

using namespace CEventGenerator;

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetRecordSize() noexcept
{
    return sizeof(EventRecord);
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetStatus() noexcept
{
    return static_cast<std::uint32_t>(GetStatus());
}

extern "C" __declspec(dllexport)
std::uint32_t CEG_GetGameBuild() noexcept
{
    return GetGameBuild();
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