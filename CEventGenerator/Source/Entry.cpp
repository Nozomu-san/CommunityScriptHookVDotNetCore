#include "Configuration.hpp"
#include "EventAbi.hpp"
#include "EventHook.hpp"
#include "EventQueue.hpp"

#include <Windows.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>


namespace CEventGenerator::Configuration
{
    namespace
    {
        std::atomic<bool> g_logEnabled{true};

        std::filesystem::path ConfigurationPath(HMODULE module)
        {
            std::wstring buffer(512, L'\0');
            for (;;)
            {
                const DWORD length = GetModuleFileNameW(
                    module,
                    buffer.data(),
                    static_cast<DWORD>(buffer.size()));
                if (length == 0)
                {
                    return L"CEventGenerator.ini";
                }
                if (length < buffer.size() - 1)
                {
                    buffer.resize(length);
                    return std::filesystem::path(buffer).parent_path() /
                        L"CEventGenerator.ini";
                }
                if (buffer.size() >= 32768)
                {
                    return L"CEventGenerator.ini";
                }
                buffer.resize(buffer.size() * 2);
            }
        }

        std::string Canonical(bool enabled)
        {
            return
                "; If you wish to toggle your logs.\r\n"
                "LogEnabled=" +
                std::string(enabled ? "true" : "false") +
                "\r\n";
        }

        bool Parse(std::string_view text, bool& enabled)
        {
            bool seen{};
            std::size_t start{};
            while (start < text.size())
            {
                const std::size_t newline = text.find('\n', start);
                const std::size_t end = newline == std::string_view::npos
                    ? text.size()
                    : newline;
                std::string_view line = text.substr(start, end - start);
                if (!line.empty() && line.back() == '\r')
                {
                    line.remove_suffix(1);
                }
                if (line.empty() || line.front() == ';')
                {
                    if (newline == std::string_view::npos)
                    {
                        break;
                    }
                    start = newline + 1;
                    continue;
                }
                if (!line.empty())
                {
                    constexpr std::string_view Key = "LogEnabled=";
                    if (seen || !line.starts_with(Key))
                    {
                        return false;
                    }
                    const std::string_view value = line.substr(Key.size());
                    if (value == "true")
                    {
                        enabled = true;
                    }
                    else if (value == "false")
                    {
                        enabled = false;
                    }
                    else
                    {
                        return false;
                    }
                    seen = true;
                }
                if (newline == std::string_view::npos)
                {
                    break;
                }
                start = newline + 1;
            }
            return seen;
        }

        void WriteAtomic(
            const std::filesystem::path& path,
            std::string_view content)
        {
            std::filesystem::path temporary = path;
            temporary += L".tmp";
            std::error_code error;
            std::filesystem::remove(temporary, error);
            {
                std::ofstream output(
                    temporary,
                    std::ios::binary | std::ios::trunc);
                if (!output.is_open())
                {
                    return;
                }
                output.write(
                    content.data(),
                    static_cast<std::streamsize>(content.size()));
                output.flush();
                if (!output.good())
                {
                    output.close();
                    std::filesystem::remove(temporary, error);
                    return;
                }
            }
            const std::wstring from = temporary.wstring();
            const std::wstring to = path.wstring();
            if (MoveFileExW(
                    from.c_str(),
                    to.c_str(),
                    MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) == FALSE)
            {
                std::filesystem::remove(temporary, error);
            }
        }
    }

    void Initialize(HMODULE module) noexcept
    {
        bool enabled = true;
        try
        {
            const std::filesystem::path path = ConfigurationPath(module);
            std::string source;
            bool rewrite = true;
            if (std::ifstream input(path, std::ios::binary); input.is_open())
            {
                std::ostringstream buffer;
                buffer << input.rdbuf();
                source = buffer.str();
                rewrite = !Parse(source, enabled);
                if (rewrite)
                {
                    enabled = true;
                }
            }

            if (rewrite)
            {
                WriteAtomic(path, Canonical(enabled));
            }
        }
        catch (...)
        {
            enabled = true;
        }
        g_logEnabled.store(enabled, std::memory_order_release);
    }

    bool LogEnabled() noexcept
    {
        return g_logEnabled.load(std::memory_order_acquire);
    }
}

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
        CEventGenerator::Configuration::Initialize(
            g_module.load(std::memory_order_acquire));
        static_cast<void>(CEventGenerator::InitializeEventHooks());
        while (!g_shutdown.load(std::memory_order_acquire))
        {
            CEventGenerator::AdvanceDynamicProbe();
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