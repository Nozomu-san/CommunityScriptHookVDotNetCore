#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <Windows.h>

namespace CEventGenerator::Configuration
{
    void Initialize(HMODULE module) noexcept;
    [[nodiscard]] bool LogEnabled() noexcept;
}