#include "CoreCLRHostLoader.hpp"

#include <algorithm>
#include <chrono>
#include <cwchar>
#include <ctime>
#include <cwctype>
#include <exception>
#include <fstream>
#include <mutex>
#include <sstream>
#include <utility>

namespace CoreCLRHostLoader
{
    namespace
    {
        struct ParsedConfiguration final
        {
            HostConfiguration Value;
            bool Repaired = false;
        };

        std::mutex g_logMutex;
        std::ofstream g_log;

        [[nodiscard]]
        std::wstring Trim(std::wstring value)
        {
            const auto isSpace = [](wchar_t character)
            {
                return std::iswspace(character) != 0;
            };

            value.erase(
                value.begin(),
                std::find_if_not(value.begin(), value.end(), isSpace));
            value.erase(
                std::find_if_not(value.rbegin(), value.rend(), isSpace).base(),
                value.end());
            return value;
        }

        [[nodiscard]]
        bool EqualsIgnoreCase(
            std::wstring_view left,
            std::wstring_view right) noexcept
        {
            if (left.size() != right.size())
            {
                return false;
            }

            for (std::size_t index = 0; index < left.size(); ++index)
            {
                if (std::towlower(left[index]) !=
                    std::towlower(right[index]))
                {
                    return false;
                }
            }
            return true;
        }

        [[nodiscard]]
        bool EndsWithIgnoreCase(
            std::wstring_view value,
            std::wstring_view suffix) noexcept
        {
            return value.size() >= suffix.size() &&
                EqualsIgnoreCase(value.substr(value.size() - suffix.size()), suffix);
        }

        [[nodiscard]]
        bool IsSimpleAssemblyName(std::wstring_view value) noexcept
        {
            if (value.empty() || value == L"." || value == L".." ||
                value.size() > 240 || value.back() == L' ' || value.back() == L'.')
            {
                return false;
            }

            constexpr std::wstring_view Invalid = L"<>:\"/\\|?*";
            return std::ranges::none_of(value, [](wchar_t character)
            {
                return character < 0x20;
            }) && value.find_first_of(Invalid) == std::wstring_view::npos;
        }

        [[nodiscard]]
        std::string WideToUtf8(std::wstring_view value)
        {
            if (value.empty())
            {
                return {};
            }

            const int required = WideCharToMultiByte(
                CP_UTF8,
                WC_ERR_INVALID_CHARS,
                value.data(),
                static_cast<int>(value.size()),
                nullptr,
                0,
                nullptr,
                nullptr);
            if (required <= 0)
            {
                return {};
            }

            std::string result(static_cast<std::size_t>(required), '\0');
            if (WideCharToMultiByte(
                    CP_UTF8,
                    WC_ERR_INVALID_CHARS,
                    value.data(),
                    static_cast<int>(value.size()),
                    result.data(),
                    required,
                    nullptr,
                    nullptr) != required)
            {
                return {};
            }
            return result;
        }

        [[nodiscard]]
        std::wstring Utf8ToWide(std::string_view value)
        {
            if (value.empty())
            {
                return {};
            }

            const int required = MultiByteToWideChar(
                CP_UTF8,
                MB_ERR_INVALID_CHARS,
                value.data(),
                static_cast<int>(value.size()),
                nullptr,
                0);
            if (required <= 0)
            {
                return {};
            }

            std::wstring result(static_cast<std::size_t>(required), L'\0');
            if (MultiByteToWideChar(
                    CP_UTF8,
                    MB_ERR_INVALID_CHARS,
                    value.data(),
                    static_cast<int>(value.size()),
                    result.data(),
                    required) != required)
            {
                return {};
            }
            return result;
        }

        [[nodiscard]]
        std::wstring LogLevelName(LogLevel level)
        {
            switch (level)
            {
            case LogLevel::Information:
                return L"Information";
            case LogLevel::Warning:
                return L"Warning";
            case LogLevel::Error:
                return L"Error";
            }
            return L"Unknown";
        }

        [[nodiscard]]
        std::wstring Timestamp()
        {
            using namespace std::chrono;

            const system_clock::time_point now = system_clock::now();
            const auto millisecondsPart =
                duration_cast<milliseconds>(now.time_since_epoch()) % 1000;
            const std::time_t time = system_clock::to_time_t(now);

            std::tm local{};
            localtime_s(&local, &time);

            wchar_t buffer[32]{};
            swprintf_s(
                buffer,
                L"%02d:%02d:%02d:%03lld",
                local.tm_hour,
                local.tm_min,
                local.tm_sec,
                static_cast<long long>(millisecondsPart.count()));
            return buffer;
        }

        [[nodiscard]]
        HostResult<std::filesystem::path> ModulePath(HMODULE module)
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
                    return std::unexpected(
                        L"GetModuleFileNameW failed with error " +
                        std::to_wstring(GetLastError()) + L".");
                }

                if (length < buffer.size() - 1)
                {
                    buffer.resize(length);
                    return std::filesystem::path(std::move(buffer));
                }

                if (buffer.size() >= 32768)
                {
                    return std::unexpected(
                        L"The CoreCLRHostLoader module path is too long.");
                }
                buffer.resize(buffer.size() * 2);
            }
        }

        [[nodiscard]]
        HostResult<void> OpenLog(const std::filesystem::path& path)
        {
            std::scoped_lock lock(g_logMutex);
            g_log.close();
            g_log.clear();
            g_log.open(path, std::ios::binary | std::ios::trunc);
            if (!g_log.is_open())
            {
                return std::unexpected(
                    L"CoreCLRHostLoader.log could not be created.");
            }
            return {};
        }

        [[nodiscard]]
        HostResult<std::string> ReadUtf8File(const std::filesystem::path& path)
        {
            std::ifstream stream(path, std::ios::binary);
            if (!stream.is_open())
            {
                return std::unexpected(L"The file could not be opened.");
            }

            std::ostringstream content;
            content << stream.rdbuf();
            if (!stream.good() && !stream.eof())
            {
                return std::unexpected(L"The file could not be read.");
            }
            return content.str();
        }

        [[nodiscard]]
        HostResult<void> WriteUtf8Atomically(
            const std::filesystem::path& destination,
            std::string_view content)
        {
            std::filesystem::path temporary = destination;
            temporary += L".tmp";
            const std::wstring temporaryText = temporary.wstring();
            const std::wstring destinationText = destination.wstring();
            DeleteFileW(temporaryText.c_str());

            HANDLE file = CreateFileW(
                temporaryText.c_str(),
                GENERIC_WRITE,
                0,
                nullptr,
                CREATE_ALWAYS,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH,
                nullptr);
            if (file == INVALID_HANDLE_VALUE)
            {
                return std::unexpected(
                    L"The temporary file could not be created.");
            }

            bool success = true;
            std::size_t offset = 0;
            while (offset < content.size())
            {
                const std::size_t remaining = content.size() - offset;
                const DWORD requested = static_cast<DWORD>(
                    (std::min<std::size_t>)(remaining, MAXDWORD));
                DWORD written = 0;
                if (WriteFile(
                        file,
                        content.data() + offset,
                        requested,
                        &written,
                        nullptr) == FALSE ||
                    written == 0)
                {
                    success = false;
                    break;
                }
                offset += written;
            }

            if (success)
            {
                success = FlushFileBuffers(file) != FALSE;
            }
            CloseHandle(file);

            if (!success)
            {
                DeleteFileW(temporaryText.c_str());
                return std::unexpected(
                    L"The temporary file could not be written.");
            }

            if (MoveFileExW(
                    temporaryText.c_str(),
                    destinationText.c_str(),
                    MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) == FALSE)
            {
                DeleteFileW(temporaryText.c_str());
                return std::unexpected(
                    L"The destination file could not be replaced.");
            }
            return {};
        }

        [[nodiscard]]
        ParsedConfiguration ParseConfiguration(std::string_view text)
        {
            ParsedConfiguration parsed{};
            std::wstring section;
            bool runtimeValueSeen = false;
            bool brainValueSeen = false;

            std::wstring wide = Utf8ToWide(text);
            if (wide.empty() && !text.empty())
            {
                parsed.Repaired = true;
                return parsed;
            }
            if (!wide.empty() && wide.front() == 0xFEFF)
            {
                wide.erase(wide.begin());
            }

            std::wstringstream lines(wide);
            std::wstring line;
            while (std::getline(lines, line))
            {
                if (!line.empty() && line.back() == L'\r')
                {
                    line.pop_back();
                }
                line = Trim(std::move(line));
                if (line.empty() || line.front() == L';' || line.front() == L'#')
                {
                    continue;
                }

                if (line.size() >= 2 && line.front() == L'[' && line.back() == L']')
                {
                    section = Trim(line.substr(1, line.size() - 2));
                    continue;
                }

                const std::size_t equals = line.find(L'=');
                if (equals == std::wstring::npos)
                {
                    parsed.Repaired = true;
                    continue;
                }

                const std::wstring key = Trim(line.substr(0, equals));
                std::wstring value = Trim(line.substr(equals + 1));

                if (EqualsIgnoreCase(section, L"Runtime") &&
                    EqualsIgnoreCase(key, L"AllowPrereleaseRuntime"))
                {
                    if (runtimeValueSeen)
                    {
                        parsed.Repaired = true;
                        continue;
                    }
                    runtimeValueSeen = true;

                    if (EqualsIgnoreCase(value, L"true"))
                    {
                        parsed.Value.AllowPrereleaseRuntime = true;
                    }
                    else if (EqualsIgnoreCase(value, L"false"))
                    {
                        parsed.Value.AllowPrereleaseRuntime = false;
                    }
                    else
                    {
                        parsed.Repaired = true;
                    }
                }
                else if (EqualsIgnoreCase(section, L"Brain") &&
                         EqualsIgnoreCase(key, L"Assembly"))
                {
                    if (brainValueSeen)
                    {
                        parsed.Repaired = true;
                        continue;
                    }
                    brainValueSeen = true;

                    if (EndsWithIgnoreCase(value, L".dll"))
                    {
                        value.resize(value.size() - 4);
                        parsed.Repaired = true;
                    }

                    if (value.empty() || IsSimpleAssemblyName(value))
                    {
                        parsed.Value.BrainAssembly = std::move(value);
                    }
                    else
                    {
                        parsed.Repaired = true;
                    }
                }
                else
                {
                    parsed.Repaired = true;
                }
            }

            return parsed;
        }

        [[nodiscard]]
        std::string RenderConfiguration(const HostConfiguration& configuration)
        {
            std::ostringstream output;
            output
                << "[Runtime]\r\n"
                << "AllowPrereleaseRuntime="
                << (configuration.AllowPrereleaseRuntime ? "true" : "false")
                << "\r\n"
                << "[Brain]\r\n"
                << "; "
                << WideToUtf8(configuration.BrainAssembly)
                << ".dll.\r\n"
                << "Assembly="
                << WideToUtf8(configuration.BrainAssembly)
                << "\r\n";
            return output.str();
        }

        [[nodiscard]]
        std::string EscapeJson(std::wstring_view value)
        {
            const std::string utf8 = WideToUtf8(value);
            std::ostringstream output;
            constexpr char Hex[] = "0123456789ABCDEF";

            for (const unsigned char character : utf8)
            {
                switch (character)
                {
                case '"':
                    output << "\\\"";
                    break;
                case '\\':
                    output << "\\\\";
                    break;
                case '\b':
                    output << "\\b";
                    break;
                case '\f':
                    output << "\\f";
                    break;
                case '\n':
                    output << "\\n";
                    break;
                case '\r':
                    output << "\\r";
                    break;
                case '\t':
                    output << "\\t";
                    break;
                default:
                    if (character < 0x20)
                    {
                        output << "\\u00"
                               << Hex[(character >> 4) & 0x0F]
                               << Hex[character & 0x0F];
                    }
                    else
                    {
                        output << static_cast<char>(character);
                    }
                    break;
                }
            }
            return output.str();
        }
    }

    void WriteLog(LogLevel level, std::wstring_view message) noexcept
    {
        try
        {
            const std::wstring line =
                L"[" + Timestamp() + L"] [" + LogLevelName(level) +
                L"] " + std::wstring(message) + L"\r\n";
            const std::string utf8 = WideToUtf8(line);

            std::scoped_lock lock(g_logMutex);
            if (g_log.is_open())
            {
                g_log.write(
                    utf8.data(),
                    static_cast<std::streamsize>(utf8.size()));
                g_log.flush();
            }
        }
        catch (...)
        {
        }
    }

    HostResult<void> SaveHostConfiguration(const HostState& state) noexcept
    {
        try
        {
            return WriteUtf8Atomically(
                state.Paths.Configuration,
                RenderConfiguration(state.Configuration));
        }
        catch (...)
        {
            return std::unexpected(
                L"An exception occurred while writing CoreCLRHostLoader.ini.");
        }
    }

    HostResult<std::filesystem::path> WriteRuntimeConfiguration(
        const HostState& state,
        const ManagedBrain& brain) noexcept
    {
        try
        {
            std::filesystem::path path =
                state.Paths.Directory / brain.Assembly.filename();
            path.replace_extension(L".json");

            std::ostringstream json;
            json
                << "{\r\n"
                << "  \"runtimeOptions\": {\r\n"
                << "    \"tfm\": \"" << EscapeJson(brain.RuntimeTfm) << "\",\r\n"
                << "    \"rollForward\": \"LatestMajor\",\r\n"
                << "    \"framework\": {\r\n"
                << "      \"name\": \"" << EscapeJson(brain.RuntimeFramework) << "\",\r\n"
                << "      \"version\": \"" << EscapeJson(brain.RuntimeVersion) << "\"\r\n"
                << "    }\r\n"
                << "  }\r\n"
                << "}\r\n";

            auto written = WriteUtf8Atomically(path, json.str());
            if (!written)
            {
                return std::unexpected(written.error());
            }
            return path;
        }
        catch (...)
        {
            return std::unexpected(
                L"An exception occurred while writing the runtime configuration.");
        }
    }

    HostResult<HostState> InitializeHostState(HMODULE module) noexcept
    {
        try
        {
            auto modulePath = ModulePath(module);
            if (!modulePath)
            {
                return std::unexpected(modulePath.error());
            }

            HostState state{};
            state.Paths.Module = std::move(*modulePath);
            state.Paths.Directory = state.Paths.Module.parent_path();
            state.Paths.Configuration =
                state.Paths.Directory / L"CoreCLRHostLoader.ini";
            state.Paths.Log =
                state.Paths.Directory / L"CoreCLRHostLoader.log";

            auto opened = OpenLog(state.Paths.Log);
            if (!opened)
            {
                return std::unexpected(opened.error());
            }

            WriteLog(
                LogLevel::Information,
                std::wstring(ProductName) + L" initialized.");

            bool repaired = false;
            if (std::filesystem::exists(state.Paths.Configuration))
            {
                auto content = ReadUtf8File(state.Paths.Configuration);
                if (content)
                {
                    ParsedConfiguration parsed = ParseConfiguration(*content);
                    state.Configuration = std::move(parsed.Value);
                    repaired = parsed.Repaired;
                }
                else
                {
                    repaired = true;
                    WriteLog(
                        LogLevel::Warning,
                        L"CoreCLRHostLoader.ini could not be read and was reset.");
                }
            }

            auto saved = SaveHostConfiguration(state);
            if (!saved)
            {
                return std::unexpected(saved.error());
            }

            if (repaired)
            {
                WriteLog(
                    LogLevel::Warning,
                    L"CoreCLRHostLoader.ini contained unsupported data and was repaired.");
            }

            WriteLog(
                LogLevel::Information,
                L"Configuration is ready: " +
                    state.Paths.Configuration.wstring());
            return state;
        }
        catch (const std::exception& exception)
        {
            return std::unexpected(
                L"Host file initialization failed: " +
                Utf8ToWide(exception.what()));
        }
        catch (...)
        {
            return std::unexpected(
                L"Host file initialization failed with an unknown exception.");
        }
    }
}
