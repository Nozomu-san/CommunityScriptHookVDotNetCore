#include "CoreCLRHostLoader.hpp"

#include <algorithm>
#include <cstdint>
#include <cwctype>
#include <exception>
#include <limits>
#include <optional>
#include <sstream>
#include <utility>
#include <vector>

#pragma comment(lib, "Advapi32.lib")

namespace CoreCLRHostLoader
{
    namespace
    {
        using host_char_t = wchar_t;
        using hostfxr_handle = void*;

        struct get_hostfxr_parameters final
        {
            std::size_t size = sizeof(get_hostfxr_parameters);
            const host_char_t* assembly_path = nullptr;
            const host_char_t* dotnet_root = nullptr;
        };

        enum class HostFxrDelegateType : std::int32_t
        {
            LoadAssemblyAndGetFunctionPointer = 5
        };

        using get_hostfxr_path_fn =
            std::int32_t(__stdcall*)(
                host_char_t* buffer,
                std::size_t* bufferSize,
                const get_hostfxr_parameters* parameters);

        using hostfxr_initialize_for_runtime_config_fn =
            std::int32_t(__cdecl*)(
                const host_char_t* runtimeConfigurationPath,
                const void* parameters,
                hostfxr_handle* hostContext);

        using hostfxr_get_runtime_delegate_fn =
            std::int32_t(__cdecl*)(
                hostfxr_handle hostContext,
                HostFxrDelegateType type,
                void** runtimeDelegate);

        using hostfxr_close_fn =
            std::int32_t(__cdecl*)(hostfxr_handle hostContext);

        using hostfxr_error_writer_fn =
            void(__cdecl*)(const host_char_t* message);

        using hostfxr_set_error_writer_fn =
            hostfxr_error_writer_fn(__cdecl*)(hostfxr_error_writer_fn writer);

        using load_assembly_and_get_function_pointer_fn =
            std::int32_t(__stdcall*)(
                const host_char_t* assemblyPath,
                const host_char_t* typeName,
                const host_char_t* methodName,
                const host_char_t* delegateTypeName,
                void* reserved,
                void** functionPointer);

        using brain_run_fn =
            std::int32_t(__cdecl*)(
                const BrainRunRequest* request,
                std::int32_t requestSize);

        HMODULE g_activeHostFxr = nullptr;

        class LoadedLibrary final
        {
        public:
            explicit LoadedLibrary(HMODULE module = nullptr) noexcept
                : m_module(module)
            {
            }

            ~LoadedLibrary()
            {
                if (m_module != nullptr)
                {
                    FreeLibrary(m_module);
                }
            }

            LoadedLibrary(const LoadedLibrary&) = delete;
            LoadedLibrary& operator=(const LoadedLibrary&) = delete;
            LoadedLibrary(LoadedLibrary&&) = delete;
            LoadedLibrary& operator=(LoadedLibrary&&) = delete;

            [[nodiscard]] HMODULE Get() const noexcept { return m_module; }
            [[nodiscard]] HMODULE Release() noexcept
            {
                return std::exchange(m_module, nullptr);
            }

        private:
            HMODULE m_module;
        };

        class HostContext final
        {
        public:
            HostContext(hostfxr_handle context, hostfxr_close_fn close) noexcept
                : m_context(context), m_close(close)
            {
            }

            ~HostContext()
            {
                if (m_context != nullptr && m_close != nullptr)
                {
                    m_close(m_context);
                }
            }

            HostContext(const HostContext&) = delete;
            HostContext& operator=(const HostContext&) = delete;

        private:
            hostfxr_handle m_context;
            hostfxr_close_fn m_close;
        };

        class HostFxrErrorWriter final
        {
        public:
            explicit HostFxrErrorWriter(hostfxr_set_error_writer_fn setWriter) noexcept
                : m_setWriter(setWriter)
            {
                if (m_setWriter != nullptr)
                {
                    m_previous = m_setWriter(&WriteHostFxrError);
                }
            }

            ~HostFxrErrorWriter()
            {
                if (m_setWriter != nullptr)
                {
                    m_setWriter(m_previous);
                }
            }

            HostFxrErrorWriter(const HostFxrErrorWriter&) = delete;
            HostFxrErrorWriter& operator=(const HostFxrErrorWriter&) = delete;

        private:
            static void __cdecl WriteHostFxrError(const host_char_t* message) noexcept
            {
                if (message != nullptr && *message != L'\0')
                {
                    WriteLog(LogLevel::Error, message);
                }
            }

            hostfxr_set_error_writer_fn m_setWriter = nullptr;
            hostfxr_error_writer_fn m_previous = nullptr;
        };

        class EnvironmentVariableOverride final
        {
        public:
            EnvironmentVariableOverride(const wchar_t* name, const wchar_t* value)
                : m_name(name)
            {
                SetLastError(ERROR_SUCCESS);
                const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
                const DWORD readError = GetLastError();
                m_existed = required != 0 || readError != ERROR_ENVVAR_NOT_FOUND;

                if (required > 0)
                {
                    m_original.resize(required);
                    const DWORD written = GetEnvironmentVariableW(
                        name,
                        m_original.data(),
                        required);
                    if (written < required)
                    {
                        m_original.resize(written);
                    }
                    else
                    {
                        m_original.clear();
                        m_existed = false;
                    }
                }

                m_applied = SetEnvironmentVariableW(name, value) != FALSE;
            }

            ~EnvironmentVariableOverride()
            {
                if (m_applied)
                {
                    SetEnvironmentVariableW(
                        m_name.c_str(),
                        m_existed ? m_original.c_str() : nullptr);
                }
            }

            EnvironmentVariableOverride(const EnvironmentVariableOverride&) = delete;
            EnvironmentVariableOverride& operator=(const EnvironmentVariableOverride&) = delete;

            [[nodiscard]] bool Applied() const noexcept { return m_applied; }

        private:
            std::wstring m_name;
            std::wstring m_original;
            bool m_existed = false;
            bool m_applied = false;
        };

        [[nodiscard]] std::wstring FormatStatus(std::int32_t status)
        {
            std::wostringstream output;
            output << L"0x" << std::hex << std::uppercase
                   << static_cast<std::uint32_t>(status);
            return output.str();
        }

        struct HostFxrVersion final
        {
            std::uint64_t Major = 0;
            std::uint64_t Minor = 0;
            std::uint64_t Patch = 0;
            std::vector<std::wstring> Prerelease;
        };

        [[nodiscard]]
        bool ReadVersionNumber(
            std::wstring_view text,
            std::size_t& offset,
            std::uint64_t& value) noexcept
        {
            if (offset >= text.size() || !std::iswdigit(text[offset]))
            {
                return false;
            }

            value = 0;
            while (offset < text.size() && std::iswdigit(text[offset]))
            {
                const std::uint64_t digit =
                    static_cast<std::uint64_t>(text[offset] - L'0');
                if (value > ((std::numeric_limits<std::uint64_t>::max)() - digit) / 10)
                {
                    return false;
                }
                value = value * 10 + digit;
                ++offset;
            }
            return true;
        }

        [[nodiscard]]
        std::optional<HostFxrVersion> ParseHostFxrVersion(
            std::wstring_view text)
        {
            HostFxrVersion version{};
            std::size_t offset = 0;
            if (!ReadVersionNumber(text, offset, version.Major) ||
                offset >= text.size() || text[offset++] != L'.' ||
                !ReadVersionNumber(text, offset, version.Minor) ||
                offset >= text.size() || text[offset++] != L'.' ||
                !ReadVersionNumber(text, offset, version.Patch))
            {
                return std::nullopt;
            }

            if (offset == text.size())
            {
                return version;
            }
            if (text[offset++] != L'-' || offset == text.size())
            {
                return std::nullopt;
            }

            while (offset < text.size())
            {
                const std::size_t next = text.find(L'.', offset);
                const std::size_t end =
                    next == std::wstring_view::npos ? text.size() : next;
                if (end == offset)
                {
                    return std::nullopt;
                }

                std::wstring identifier(text.substr(offset, end - offset));
                if (!std::ranges::all_of(
                        identifier,
                        [](wchar_t character)
                        {
                            return std::iswalnum(character) || character == L'-';
                        }))
                {
                    return std::nullopt;
                }

                version.Prerelease.push_back(std::move(identifier));
                if (next == std::wstring_view::npos)
                {
                    break;
                }
                offset = next + 1;
            }

            return version;
        }

        [[nodiscard]]
        bool IsNumericIdentifier(std::wstring_view value) noexcept
        {
            return !value.empty() &&
                std::ranges::all_of(
                    value,
                    [](wchar_t character)
                    {
                        return std::iswdigit(character);
                    });
        }

        [[nodiscard]]
        int CompareNumericIdentifier(
            std::wstring_view left,
            std::wstring_view right) noexcept
        {
            while (left.size() > 1 && left.front() == L'0')
            {
                left.remove_prefix(1);
            }
            while (right.size() > 1 && right.front() == L'0')
            {
                right.remove_prefix(1);
            }

            if (left.size() != right.size())
            {
                return left.size() < right.size() ? -1 : 1;
            }
            if (left == right)
            {
                return 0;
            }
            return left < right ? -1 : 1;
        }

        [[nodiscard]]
        int CompareHostFxrVersion(
            const HostFxrVersion& left,
            const HostFxrVersion& right) noexcept
        {
            if (left.Major != right.Major)
            {
                return left.Major < right.Major ? -1 : 1;
            }
            if (left.Minor != right.Minor)
            {
                return left.Minor < right.Minor ? -1 : 1;
            }
            if (left.Patch != right.Patch)
            {
                return left.Patch < right.Patch ? -1 : 1;
            }

            const bool leftPrerelease = !left.Prerelease.empty();
            const bool rightPrerelease = !right.Prerelease.empty();
            if (leftPrerelease != rightPrerelease)
            {
                return leftPrerelease ? -1 : 1;
            }
            if (!leftPrerelease)
            {
                return 0;
            }

            const std::size_t count =
                (std::min)(left.Prerelease.size(), right.Prerelease.size());
            for (std::size_t index = 0; index < count; ++index)
            {
                const std::wstring_view leftPart = left.Prerelease[index];
                const std::wstring_view rightPart = right.Prerelease[index];
                const bool leftNumeric = IsNumericIdentifier(leftPart);
                const bool rightNumeric = IsNumericIdentifier(rightPart);

                if (leftNumeric && rightNumeric)
                {
                    const int numeric =
                        CompareNumericIdentifier(leftPart, rightPart);
                    if (numeric != 0)
                    {
                        return numeric;
                    }
                    continue;
                }
                if (leftNumeric != rightNumeric)
                {
                    return leftNumeric ? -1 : 1;
                }
                if (leftPart != rightPart)
                {
                    return leftPart < rightPart ? -1 : 1;
                }
            }

            if (left.Prerelease.size() == right.Prerelease.size())
            {
                return 0;
            }
            return left.Prerelease.size() < right.Prerelease.size() ? -1 : 1;
        }

        struct NetHostLocation final
        {
            std::filesystem::path Library;
            std::filesystem::path DotNetRoot;
            HostFxrVersion Version;
        };

        [[nodiscard]]
        std::optional<std::filesystem::path> EnvironmentPath(
            const wchar_t* name)
        {
            const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
            if (required == 0)
            {
                return std::nullopt;
            }

            std::wstring value(required, L'\0');
            const DWORD written = GetEnvironmentVariableW(
                name,
                value.data(),
                required);
            if (written == 0 || written >= required)
            {
                return std::nullopt;
            }
            value.resize(written);
            return std::filesystem::path(std::move(value));
        }

        [[nodiscard]]
        std::optional<std::filesystem::path> RegisteredDotNetRoot()
        {
            constexpr wchar_t Key[] =
                L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64";
            DWORD bytes = 0;
            const LSTATUS sizeStatus = RegGetValueW(
                HKEY_LOCAL_MACHINE,
                Key,
                L"InstallLocation",
                RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY,
                nullptr,
                nullptr,
                &bytes);
            if (sizeStatus != ERROR_SUCCESS || bytes < sizeof(wchar_t))
            {
                return std::nullopt;
            }

            std::wstring value(bytes / sizeof(wchar_t), L'\0');
            DWORD readBytes = bytes;
            const LSTATUS readStatus = RegGetValueW(
                HKEY_LOCAL_MACHINE,
                Key,
                L"InstallLocation",
                RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY,
                nullptr,
                value.data(),
                &readBytes);
            if (readStatus != ERROR_SUCCESS)
            {
                return std::nullopt;
            }

            while (!value.empty() && value.back() == L'\0')
            {
                value.pop_back();
            }
            return value.empty()
                ? std::nullopt
                : std::optional<std::filesystem::path>(
                    std::filesystem::path(std::move(value)));
        }

        [[nodiscard]]
        std::optional<std::filesystem::path> LocateDotNetRoot()
        {
            std::array<std::optional<std::filesystem::path>, 4> candidates
            {
                EnvironmentPath(L"DOTNET_ROOT_X64"),
                EnvironmentPath(L"DOTNET_ROOT"),
                RegisteredDotNetRoot(),
                std::nullopt
            };

            if (auto programFiles = EnvironmentPath(L"ProgramFiles"))
            {
                candidates.back() = *programFiles / L"dotnet";
            }

            std::error_code error;
            for (const auto& candidate : candidates)
            {
                if (candidate &&
                    std::filesystem::is_directory(*candidate, error))
                {
                    std::filesystem::path normalized =
                        std::filesystem::weakly_canonical(*candidate, error);
                    if (!error)
                    {
                        return normalized;
                    }
                    error.clear();
                    return *candidate;
                }
                error.clear();
            }
            return std::nullopt;
        }

        [[nodiscard]]
        std::optional<NetHostLocation> LocateSdkNetHost(
            RuntimeChannel channel)
        {
            auto dotnetRoot = LocateDotNetRoot();
            if (!dotnetRoot)
            {
                return std::nullopt;
            }

            const std::filesystem::path packRoot =
                *dotnetRoot /
                L"packs" /
                L"Microsoft.NETCore.App.Host.win-x64";
            std::error_code error;
            if (!std::filesystem::is_directory(packRoot, error))
            {
                return std::nullopt;
            }

            std::optional<NetHostLocation> selected;
            for (const auto& entry : std::filesystem::directory_iterator(
                     packRoot,
                     std::filesystem::directory_options::skip_permission_denied,
                     error))
            {
                if (error)
                {
                    return std::nullopt;
                }
                if (!entry.is_directory(error))
                {
                    error.clear();
                    continue;
                }

                auto version =
                    ParseHostFxrVersion(entry.path().filename().wstring());
                if (!version ||
                    (channel == RuntimeChannel::Release &&
                     !version->Prerelease.empty()))
                {
                    continue;
                }

                const std::filesystem::path candidate =
                    entry.path() /
                    L"runtimes" /
                    L"win-x64" /
                    L"native" /
                    L"nethost.dll";
                if (!std::filesystem::is_regular_file(candidate, error))
                {
                    error.clear();
                    continue;
                }

                if (!selected ||
                    CompareHostFxrVersion(
                        *version,
                        selected->Version) > 0)
                {
                    selected = NetHostLocation
                    {
                        candidate,
                        *dotnetRoot,
                        std::move(*version)
                    };
                }
            }
            return selected;
        }

        [[nodiscard]]
        HostResult<std::filesystem::path> LocateDefaultHostFxr(
            const ManagedBrain& brain,
            RuntimeChannel channel)
        {
            HMODULE rawNetHost = LoadLibraryExW(
                L"nethost.dll",
                nullptr,
                LOAD_LIBRARY_SEARCH_APPLICATION_DIR |
                    LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
            std::optional<NetHostLocation> sdkNetHost;
            if (rawNetHost == nullptr)
            {
                sdkNetHost = LocateSdkNetHost(channel);
                if (sdkNetHost)
                {
                    rawNetHost = LoadLibraryExW(
                        sdkNetHost->Library.c_str(),
                        nullptr,
                        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
                            LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
                }
            }
            if (rawNetHost == nullptr)
            {
                return std::unexpected(
                    L"nethost.dll could not be loaded from the game root or "
                    L"the selected installed .NET SDK host pack.");
            }

            LoadedLibrary nethost(rawNetHost);
            if (sdkNetHost)
            {
                WriteLog(
                    LogLevel::Information,
                    L"nethost.dll selected from installed SDK host pack: " +
                        sdkNetHost->Library.wstring());
            }

            const auto getHostFxrPath = reinterpret_cast<get_hostfxr_path_fn>(
                GetProcAddress(nethost.Get(), "get_hostfxr_path"));
            if (getHostFxrPath == nullptr)
            {
                return std::unexpected(
                    L"nethost.dll does not expose get_hostfxr_path.");
            }

            const std::wstring assemblyPath = brain.Assembly.wstring();
            const std::wstring sdkRoot = sdkNetHost
                ? sdkNetHost->DotNetRoot.wstring()
                : std::wstring{};
            get_hostfxr_parameters parameters{};
            parameters.assembly_path = assemblyPath.c_str();
            parameters.dotnet_root = sdkRoot.empty()
                ? nullptr
                : sdkRoot.c_str();

            std::size_t required = 0;
            std::int32_t status = getHostFxrPath(nullptr, &required, &parameters);
            if (required == 0)
            {
                return std::unexpected(
                    L"get_hostfxr_path could not determine a hostfxr path: " +
                    FormatStatus(status) + L".");
            }

            std::vector<wchar_t> buffer(required);
            status = getHostFxrPath(buffer.data(), &required, &parameters);
            if (status != 0 || buffer.empty() || buffer.front() == L'\0')
            {
                return std::unexpected(
                    L"get_hostfxr_path failed: " + FormatStatus(status) + L".");
            }

            return std::filesystem::path(buffer.data());
        }

        [[nodiscard]]
        HostResult<std::filesystem::path> ResolveHostFxr(
            const ManagedBrain& brain,
            RuntimeChannel channel)
        {
            auto located = LocateDefaultHostFxr(brain, channel);
            if (!located)
            {
                return std::unexpected(located.error());
            }

            const std::filesystem::path versionDirectory =
                located->parent_path();
            const std::filesystem::path fxrDirectory =
                versionDirectory.parent_path();
            if (!std::filesystem::is_directory(fxrDirectory))
            {
                return std::unexpected(
                    L"The resolved .NET host/fxr directory is unavailable.");
            }

            std::optional<HostFxrVersion> selectedVersion;
            std::filesystem::path selectedPath;
            std::error_code error;
            for (const auto& entry : std::filesystem::directory_iterator(
                     fxrDirectory,
                     std::filesystem::directory_options::skip_permission_denied,
                     error))
            {
                if (error)
                {
                    break;
                }
                if (!entry.is_directory(error))
                {
                    continue;
                }

                auto version =
                    ParseHostFxrVersion(entry.path().filename().wstring());
                if (!version)
                {
                    continue;
                }
                if (channel == RuntimeChannel::Release &&
                    !version->Prerelease.empty())
                {
                    continue;
                }

                const std::filesystem::path candidate =
                    entry.path() / L"hostfxr.dll";
                if (!std::filesystem::is_regular_file(candidate, error))
                {
                    error.clear();
                    continue;
                }

                if (!selectedVersion ||
                    CompareHostFxrVersion(*version, *selectedVersion) > 0)
                {
                    selectedVersion = std::move(version);
                    selectedPath = candidate;
                }
            }

            if (error)
            {
                return std::unexpected(
                    L"The .NET host/fxr directory could not be enumerated.");
            }
            if (selectedPath.empty())
            {
                return std::unexpected(
                    channel == RuntimeChannel::Release
                        ? L"No release hostfxr installation is available."
                        : L"No hostfxr installation is available.");
            }

            return selectedPath;
        }
    }

    HostResult<void> RunManagedBrain(
        const HostConfiguration& configuration,
        const ManagedBrain& brain,
        const BrainRunRequest& request) noexcept
    {
        try
        {
            if (g_activeHostFxr != nullptr)
            {
                return std::unexpected(
                    L"The managed runtime has already been activated by "
                    L"CoreCLRHostLoader in this GTA process.");
            }

            if (!std::filesystem::is_regular_file(brain.RuntimeConfiguration))
            {
                return std::unexpected(
                    L"The managed brain runtime configuration is missing: " +
                    brain.RuntimeConfiguration.wstring());
            }

            auto hostFxrPath = ResolveHostFxr(brain, configuration.Channel);
            if (!hostFxrPath)
            {
                return std::unexpected(hostFxrPath.error());
            }

            WriteLog(
                LogLevel::Information,
                configuration.Channel == RuntimeChannel::Preview
                    ? L"Preview hostfxr selected: " + hostFxrPath->wstring()
                    : L"Release hostfxr selected: " + hostFxrPath->wstring());

            LoadedLibrary hostFxr(LoadLibraryExW(
                hostFxrPath->c_str(),
                nullptr,
                LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
                    LOAD_LIBRARY_SEARCH_DEFAULT_DIRS));
            if (hostFxr.Get() == nullptr)
            {
                return std::unexpected(L"The resolved hostfxr.dll could not be loaded.");
            }

            const auto initialize =
                reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
                    GetProcAddress(hostFxr.Get(), "hostfxr_initialize_for_runtime_config"));
            const auto getDelegate =
                reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
                    GetProcAddress(hostFxr.Get(), "hostfxr_get_runtime_delegate"));
            const auto close = reinterpret_cast<hostfxr_close_fn>(
                GetProcAddress(hostFxr.Get(), "hostfxr_close"));
            const auto setErrorWriter =
                reinterpret_cast<hostfxr_set_error_writer_fn>(
                    GetProcAddress(hostFxr.Get(), "hostfxr_set_error_writer"));

            if (initialize == nullptr || getDelegate == nullptr || close == nullptr)
            {
                return std::unexpected(
                    L"hostfxr does not expose the required stable hosting APIs.");
            }

            HostFxrErrorWriter errorWriter(setErrorWriter);
            const bool previewRuntime =
                configuration.Channel == RuntimeChannel::Preview;

            EnvironmentVariableOverride prereleasePolicy(
                L"DOTNET_ROLL_FORWARD_TO_PRERELEASE",
                previewRuntime ? L"1" : L"0");
            if (!prereleasePolicy.Applied())
            {
                return std::unexpected(
                    L"The prerelease runtime policy could not be applied.");
            }

            std::optional<EnvironmentVariableOverride> rollForwardPolicy;
            if (previewRuntime)
            {
                rollForwardPolicy.emplace(
                    L"DOTNET_ROLL_FORWARD",
                    L"LatestMajor");
                if (!rollForwardPolicy->Applied())
                {
                    return std::unexpected(
                        L"The Preview runtime roll-forward policy could not be applied.");
                }
            }

            hostfxr_handle context = nullptr;
            const std::wstring runtimeConfig = brain.RuntimeConfiguration.wstring();
            std::int32_t status = initialize(runtimeConfig.c_str(), nullptr, &context);
            if (status < 0 || context == nullptr)
            {
                if (context != nullptr)
                {
                    close(context);
                }
                return std::unexpected(
                    L"hostfxr_initialize_for_runtime_config failed with " +
                    FormatStatus(status) + L".");
            }

            HostContext hostContext(context, close);
            void* rawLoadAssembly = nullptr;
            status = getDelegate(
                context,
                HostFxrDelegateType::LoadAssemblyAndGetFunctionPointer,
                &rawLoadAssembly);
            if (status < 0 || rawLoadAssembly == nullptr)
            {
                return std::unexpected(
                    L"hostfxr_get_runtime_delegate failed with " +
                    FormatStatus(status) + L".");
            }

            g_activeHostFxr = hostFxr.Release();
            const auto loadAssembly =
                reinterpret_cast<load_assembly_and_get_function_pointer_fn>(
                    rawLoadAssembly);

            void* rawRun = nullptr;
            const std::wstring assemblyPath = brain.Assembly.wstring();
            const auto unmanagedCallersOnly = reinterpret_cast<const host_char_t*>(
                static_cast<std::intptr_t>(-1));
            status = loadAssembly(
                assemblyPath.c_str(),
                brain.EntryType.c_str(),
                brain.EntryMethod.c_str(),
                unmanagedCallersOnly,
                nullptr,
                &rawRun);
            if (status < 0 || rawRun == nullptr)
            {
                return std::unexpected(
                    L"The managed brain entry point could not be resolved: " +
                    FormatStatus(status) + L".");
            }

            const auto runBrain = reinterpret_cast<brain_run_fn>(rawRun);
            const std::int32_t result = runBrain(
                &request,
                static_cast<std::int32_t>(sizeof(request)));
            if (result != 0)
            {
                return std::unexpected(
                    L"The managed brain ended with result " +
                    std::to_wstring(result) + L".");
            }
            return {};
        }
        catch (const std::exception& exception)
        {
            const int required = MultiByteToWideChar(
                CP_UTF8, 0, exception.what(), -1, nullptr, 0);
            std::wstring message;
            if (required > 1)
            {
                std::wstring buffer(static_cast<std::size_t>(required), L'\0');
                if (MultiByteToWideChar(
                        CP_UTF8,
                        0,
                        exception.what(),
                        -1,
                        buffer.data(),
                        required) == required)
                {
                    buffer.pop_back();
                    message = std::move(buffer);
                }
            }
            return std::unexpected(L"Managed activation failed: " + message);
        }
        catch (...)
        {
            return std::unexpected(
                L"Managed activation failed with an unknown exception.");
        }
    }
}