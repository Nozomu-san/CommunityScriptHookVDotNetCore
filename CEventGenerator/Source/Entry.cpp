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
#include <string_view>


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

        enum class ParseStatus
        {
            Valid,
            Missing,
            Invalid
        };

        std::string_view Trim(std::string_view value) noexcept
        {
            constexpr std::string_view Whitespace = " \t\v\f\r\n";
            const std::size_t start = value.find_first_not_of(Whitespace);
            if (start == std::string_view::npos)
            {
                return {};
            }
            const std::size_t end = value.find_last_not_of(Whitespace);
            return value.substr(start, end - start + 1);
        }

        ParseStatus Parse(std::string_view text, bool& enabled)
        {
            if (text.starts_with("\xEF\xBB\xBF"))
            {
                text.remove_prefix(3);
            }

            std::string filtered;
            filtered.reserve(text.size());
            bool lineComment = false;
            bool blockComment = false;
            for (std::size_t index = 0; index < text.size();)
            {
                const char current = text[index];
                const char next = index + 1 < text.size()
                    ? text[index + 1]
                    : '\0';
                if (blockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        blockComment = false;
                        index += 2;
                        continue;
                    }
                    ++index;
                    continue;
                }
                if (lineComment)
                {
                    if (current == '\r' || current == '\n')
                    {
                        lineComment = false;
                        filtered.push_back(current);
                    }
                    ++index;
                    continue;
                }
                if (current == '/' && next == '*')
                {
                    filtered.push_back(' ');
                    blockComment = true;
                    index += 2;
                    continue;
                }
                if (current == ';' || current == '#' ||
                    (current == '/' && next == '/'))
                {
                    lineComment = true;
                    index += current == '/' ? 2 : 1;
                    continue;
                }
                filtered.push_back(current);
                ++index;
            }
            if (blockComment)
            {
                return ParseStatus::Invalid;
            }

            bool seen = false;
            std::size_t start = 0;
            for (;;)
            {
                const std::size_t end = filtered.find_first_of("\r\n", start);
                const std::string_view line = Trim(
                    std::string_view(filtered).substr(start, end - start));
                if (!line.empty())
                {
                    const std::size_t separator = line.find('=');
                    if (seen || separator == std::string_view::npos ||
                        Trim(line.substr(0, separator)) != "LogEnabled")
                    {
                        return ParseStatus::Invalid;
                    }
                    const std::string_view value = Trim(line.substr(separator + 1));
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
                        return ParseStatus::Invalid;
                    }
                    seen = true;
                }
                if (end == std::string_view::npos)
                {
                    break;
                }
                start = end + 1;
                if (filtered[end] == '\r' && start < filtered.size() &&
                    filtered[start] == '\n')
                {
                    ++start;
                }
            }
            return seen ? ParseStatus::Valid : ParseStatus::Missing;
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
            if (std::ifstream input(path, std::ios::binary); input.is_open())
            {
                std::ostringstream buffer;
                buffer << input.rdbuf();
                const std::string source = buffer.str();
                const ParseStatus status = Parse(source, enabled);
                if (status == ParseStatus::Missing)
                {
                    std::string updated = source;
                    if (!updated.empty() && updated.back() != '\r' &&
                        updated.back() != '\n')
                    {
                        updated += "\r\n";
                    }
                    updated += "LogEnabled=true\r\n";
                    WriteAtomic(path, updated);
                }
                else if (status == ParseStatus::Invalid)
                {
                    enabled = true;
                }
            }
            else if (!std::filesystem::exists(path))
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