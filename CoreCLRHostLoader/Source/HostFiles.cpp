#include "CoreCLRHostLoader.hpp"

#include <algorithm>
#include <chrono>
#include <cwchar>
#include <ctime>
#include <cwctype>
#include <exception>
#include <fstream>
#include <mutex>
#include <optional>
#include <sstream>
#include <utility>

namespace CoreCLRHostLoader
{
    namespace
    {
        struct ParsedConfiguration final
        {
            HostConfiguration Value;
            bool IsValid = false;
        };

        enum class ConfigurationSection
        {
            None,
            Runtime,
            Brain,
            Unknown
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
                std::ranges::find_if_not(value, isSpace));
            value.erase(
                std::ranges::find_if_not(
                    value.rbegin(),
                    value.rend(),
                    isSpace).base(),
                value.end());
            return value;
        }

        [[nodiscard]]
        bool EqualsIgnoreCase(
            std::wstring_view left,
            std::wstring_view right) noexcept
        {
            return std::ranges::equal(
                left,
                right,
                [](wchar_t leftCharacter, wchar_t rightCharacter)
                {
                    return std::towlower(leftCharacter) ==
                        std::towlower(rightCharacter);
                });
        }

        [[nodiscard]]
        bool EndsWithIgnoreCase(
            std::wstring_view value,
            std::wstring_view suffix) noexcept
        {
            return std::ranges::ends_with(
                value,
                suffix,
                [](wchar_t leftCharacter, wchar_t rightCharacter)
                {
                    return std::towlower(leftCharacter) ==
                        std::towlower(rightCharacter);
                });
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
        HostResult<std::string> ReplaceBrainAssemblyInput(
            std::string_view text,
            std::wstring_view assemblyName)
        {
            std::wstring wide = Utf8ToWide(text);
            if (wide.empty() && !text.empty())
            {
                return std::unexpected(
                    L"CoreCLRHostLoader.ini is not valid UTF-8.");
            }

            ConfigurationSection section = ConfigurationSection::None;
            std::wstring updated;
            updated.reserve(wide.size() + assemblyName.size());
            bool replaced = false;

            std::size_t offset = 0;
            while (offset < wide.size())
            {
                const std::size_t newline = wide.find(L'\n', offset);
                const std::size_t lineEnd =
                    newline == std::wstring::npos ? wide.size() : newline;
                const std::wstring_view rawLine(
                    wide.data() + offset,
                    lineEnd - offset);

                std::size_t contentEnd = rawLine.size();
                if (contentEnd != 0 && rawLine[contentEnd - 1] == L'\r')
                {
                    --contentEnd;
                }

                const std::wstring trimmed =
                    Trim(std::wstring(rawLine.substr(0, contentEnd)));

                bool assemblyInput = false;
                std::size_t valueStart = 0;
                std::size_t valueEnd = 0;

                if (!trimmed.empty() &&
                    trimmed.front() != L';' &&
                    trimmed.front() != L'#')
                {
                    if (trimmed.size() >= 2 &&
                        trimmed.front() == L'[' &&
                        trimmed.back() == L']')
                    {
                        const std::wstring name =
                            Trim(trimmed.substr(1, trimmed.size() - 2));
                        if (EqualsIgnoreCase(name, L"Runtime"))
                        {
                            section = ConfigurationSection::Runtime;
                        }
                        else if (EqualsIgnoreCase(name, L"Brain"))
                        {
                            section = ConfigurationSection::Brain;
                        }
                        else
                        {
                            section = ConfigurationSection::Unknown;
                        }
                    }
                    else if (section == ConfigurationSection::Brain)
                    {
                        const std::size_t equals =
                            rawLine.substr(0, contentEnd).find(L'=');
                        if (equals != std::wstring_view::npos)
                        {
                            const std::wstring key = Trim(
                                std::wstring(
                                    rawLine.substr(0, equals)));
                            if (EqualsIgnoreCase(key, L"Assembly"))
                            {
                                assemblyInput = true;
                                valueStart = equals + 1;
                                while (valueStart < contentEnd &&
                                    std::iswspace(rawLine[valueStart]) != 0)
                                {
                                    ++valueStart;
                                }

                                valueEnd = contentEnd;
                                while (valueEnd > valueStart &&
                                    std::iswspace(rawLine[valueEnd - 1]) != 0)
                                {
                                    --valueEnd;
                                }
                            }
                        }
                    }
                }

                if (assemblyInput)
                {
                    updated.append(rawLine.substr(0, valueStart));
                    updated.append(assemblyName.data(), assemblyName.size());
                    updated.append(rawLine.substr(valueEnd));
                    replaced = true;
                }
                else
                {
                    updated.append(rawLine);
                }

                if (newline != std::wstring::npos)
                {
                    updated.push_back(L'\n');
                    offset = newline + 1;
                }
                else
                {
                    offset = wide.size();
                }
            }

            if (!replaced)
            {
                return std::unexpected(
                    L"The [Brain] Assembly input could not be located.");
            }

            const std::string encoded = WideToUtf8(updated);
            if (encoded.empty() && !updated.empty())
            {
                return std::unexpected(
                    L"CoreCLRHostLoader.ini could not be encoded as UTF-8.");
            }
            return encoded;
        }

        [[nodiscard]]
        ParsedConfiguration ParseConfiguration(std::string_view text)
        {
            ParsedConfiguration parsed{};
            ConfigurationSection section = ConfigurationSection::None;
            std::optional<RuntimeChannel> runtimeValue;
            std::optional<std::wstring> brainValue;
            bool runtimeInvalid = false;
            bool brainInvalid = false;
            bool runtimeConflict = false;
            bool brainConflict = false;

            std::wstring wide = Utf8ToWide(text);
            if (wide.empty() && !text.empty())
            {
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
                    const std::wstring name =
                        Trim(line.substr(1, line.size() - 2));
                    if (EqualsIgnoreCase(name, L"Runtime"))
                    {
                        section = ConfigurationSection::Runtime;
                    }
                    else if (EqualsIgnoreCase(name, L"Brain"))
                    {
                        section = ConfigurationSection::Brain;
                    }
                    else
                    {
                        section = ConfigurationSection::Unknown;
                    }
                    continue;
                }

                const std::size_t equals = line.find(L'=');
                if (equals == std::wstring::npos)
                {
                    continue;
                }

                const std::wstring key = Trim(line.substr(0, equals));
                std::wstring value = Trim(line.substr(equals + 1));

                if (section == ConfigurationSection::Runtime &&
                    EqualsIgnoreCase(key, L"Channel"))
                {
                    std::optional<RuntimeChannel> candidate;
                    if (EqualsIgnoreCase(value, L"Release"))
                    {
                        candidate = RuntimeChannel::Release;
                    }
                    else if (EqualsIgnoreCase(value, L"Preview"))
                    {
                        candidate = RuntimeChannel::Preview;
                    }
                    else
                    {
                        runtimeInvalid = true;
                        continue;
                    }

                    if (!runtimeValue)
                    {
                        runtimeValue = *candidate;
                    }
                    else if (*runtimeValue != *candidate)
                    {
                        runtimeConflict = true;
                    }
                    continue;
                }

                if (section == ConfigurationSection::Brain &&
                    EqualsIgnoreCase(key, L"Assembly"))
                {
                    if (EndsWithIgnoreCase(value, L".dll"))
                    {
                        value.resize(value.size() - 4);
                    }

                    if (!value.empty() && !IsSimpleAssemblyName(value))
                    {
                        brainInvalid = true;
                        continue;
                    }

                    if (!brainValue)
                    {
                        brainValue = std::move(value);
                    }
                    else if (!EqualsIgnoreCase(*brainValue, value))
                    {
                        brainConflict = true;
                    }
                }
            }

            if (!runtimeValue || runtimeInvalid || runtimeConflict ||
                !brainValue || brainInvalid || brainConflict)
            {
                return parsed;
            }

            parsed.Value.Channel = *runtimeValue;
            parsed.Value.BrainAssembly = std::move(*brainValue);
            parsed.IsValid = true;
            return parsed;
        }

        [[nodiscard]]
        std::string RenderConfiguration(const HostConfiguration& configuration)
        {
            std::ostringstream output;
            output
                << "; Choose between 'Release' or 'Preview'. For early access Runtimes only.\r\n"
                << "[Runtime]\r\n"
                << "Channel="
                << (configuration.Channel == RuntimeChannel::Preview
                    ? "Preview"
                    : "Release")
                << "\r\n\r\n"
                << "[Brain]\r\n"
                << "Assembly="
                << WideToUtf8(configuration.BrainAssembly)
                << "\r\n";
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

    HostResult<bool> CacheManagedBrainSelection(
        HostState& state,
        std::wstring_view assemblyName) noexcept
    {
        try
        {
            if (assemblyName.empty() ||
                !IsSimpleAssemblyName(std::wstring(assemblyName)))
            {
                return std::unexpected(
                    L"The discovered managed brain assembly name is invalid.");
            }

            if (!state.Configuration.BrainAssembly.empty() &&
                EqualsIgnoreCase(
                    state.Configuration.BrainAssembly,
                    assemblyName))
            {
                return false;
            }

            auto content = ReadUtf8File(state.Paths.Configuration);
            if (!content)
            {
                return std::unexpected(
                    L"CoreCLRHostLoader.ini could not be read while caching "
                    L"the managed brain selection: " + content.error());
            }

            auto updated = ReplaceBrainAssemblyInput(*content, assemblyName);
            if (!updated)
            {
                return std::unexpected(updated.error());
            }

            auto written = WriteUtf8Atomically(
                state.Paths.Configuration,
                *updated);
            if (!written)
            {
                return std::unexpected(
                    L"CoreCLRHostLoader.ini could not cache the managed brain "
                    L"selection: " + written.error());
            }

            state.Configuration.BrainAssembly = std::wstring(assemblyName);
            return true;
        }
        catch (const std::exception& exception)
        {
            return std::unexpected(
                L"Managed brain selection caching failed: " +
                Utf8ToWide(exception.what()));
        }
        catch (...)
        {
            return std::unexpected(
                L"Managed brain selection caching failed with an unknown exception.");
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

            const bool configurationExists =
                std::filesystem::exists(state.Paths.Configuration);
            bool configurationNeedsWrite = !configurationExists;

            if (configurationExists)
            {
                auto content = ReadUtf8File(state.Paths.Configuration);
                if (!content)
                {
                    return std::unexpected(
                        L"CoreCLRHostLoader.ini exists but could not be read: " +
                        content.error());
                }

                ParsedConfiguration parsed = ParseConfiguration(*content);
                if (parsed.IsValid)
                {
                    state.Configuration = std::move(parsed.Value);
                    configurationNeedsWrite = false;
                }
                else
                {
                    configurationNeedsWrite = true;
                }
            }

            if (configurationNeedsWrite)
            {
                auto saved = SaveHostConfiguration(state);
                if (!saved)
                {
                    return std::unexpected(saved.error());
                }
                WriteLog(
                    LogLevel::Warning,
                    configurationExists
                        ? L"CoreCLRHostLoader.ini was replaced with the default "
                          L"configuration because its required inputs were missing, "
                          L"invalid, or ambiguous."
                        : L"CoreCLRHostLoader.ini was created with the default "
                          L"configuration.");
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