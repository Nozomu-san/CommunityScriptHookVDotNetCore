#include "EventAbi.hpp"
#include "EventHook.hpp"
#include "EventQueue.hpp"

#include <Windows.h>

#include <atomic>
#include <cstdint>
#include <cstring>

using ScriptHookVMain = void (*)();

__declspec(dllimport) void scriptRegister(HMODULE module, ScriptHookVMain scriptMain);
__declspec(dllimport) void scriptUnregister(HMODULE module);
__declspec(dllimport) void scriptWait(DWORD milliseconds);

namespace
{
    std::atomic<HMODULE> g_module{};
    std::atomic<bool> g_shutdown{};

    void ScriptMain()
    {
        static_cast<void>(CEventGenerator::InitializeEventHooks());
        while (!g_shutdown.load(std::memory_order_acquire))
        {
            scriptWait(0);
        }
        CEventGenerator::ShutdownEventHooks();
    }
}

BOOL WINAPI DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_module.store(module, std::memory_order_release);
        DisableThreadLibraryCalls(module);
        scriptRegister(module, &ScriptMain);
        break;

    case DLL_PROCESS_DETACH:
        g_shutdown.store(true, std::memory_order_release);
        if (reserved == nullptr)
        {
            scriptUnregister(module);
        }
        g_module.store(nullptr, std::memory_order_release);
        break;

    default:
        break;
    }
    return TRUE;
}