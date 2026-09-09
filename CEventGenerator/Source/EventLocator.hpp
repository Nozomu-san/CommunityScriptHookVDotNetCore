#pragma once

#include "EventAbi.hpp"

#include <array>
#include <cstdint>

namespace CEventGenerator
{
    struct EventHookTargets final
    {
        std::array<void*, 3> Functions{};
        void* DamageFunction{};
        void* DamageProcessFunction{};
        std::uint32_t GameBuild{};
    };

    struct EventLocatorResult final
    {
        Status State{Status::HookNotFound};
        EventHookTargets Targets{};
    };

    [[nodiscard]] EventLocatorResult LocateEventHooks() noexcept;
}