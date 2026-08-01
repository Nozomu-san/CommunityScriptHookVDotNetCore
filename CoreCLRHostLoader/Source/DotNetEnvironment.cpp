#include "CoreCLRHostLoader.hpp"

#include <algorithm>
#include <charconv>
#include <cstdint>
#include <exception>
#include <cwctype>
#include <limits>
#include <set>
#include <span>
#include <utility>

namespace CoreCLRHostLoader
{
    namespace
    {
        using host_char_t = wchar_t;

        struct hostfxr_dotnet_environment_sdk_info
        {
            std::size_t size;
            const host_char_t* version;
            const host_char_t* path;
        };

        struct hostfxr_dotnet_environment_framework_info
        {
            std::size_t size;
            const host_char_t* name;
            const host_char_t* version;
            const host_char_t* path;
        };

        struct hostfxr_dotnet_environment_info
        {
            std::size_t size;
            const host_char_t* hostfxr_version;
            const host_char_t* hostfxr_commit_hash;
            std::size_t sdk_count;
            const hostfxr_dotnet_environment_sdk_info* sdks;
            std::size_t framework_count;
            const hostfxr_dotnet_environment_framework_info* frameworks;
        };

        using hostfxr_get_dotnet_environment_info_result_fn =
            void(__cdecl*)(
                const hostfxr_dotnet_environment_info* information,
                void* context);

        using hostfxr_get_dotnet_environment_info_fn =
            std::int32_t(__cdecl*)(
                const host_char_t* dotnetRoot,
                void* reserved,
                hostfxr_get_dotnet_environment_info_result_fn result,
                void* context);

        struct SemanticVersion final
        {
            std::vector<std::uint32_t> Numbers;
            std::vector<std::wstring> Prerelease;
            std::wstring Text;

            [[nodiscard]]
            bool IsPrerelease() const noexcept
            {
                return !Prerelease.empty();
            }
        };

        struct EnvironmentCapture final
        {
            std::wstring HostFxrVersion;
            std::vector<RuntimeDescriptor> Runtimes;
        };

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

            LoadedLibrary(LoadedLibrary&& other) noexcept
                : m_module(std::exchange(other.m_module, nullptr))
            {
            }

            LoadedLibrary& operator=(LoadedLibrary&& other) noexcept
            {
                if (this != &other)
                {
                    if (m_module != nullptr)
                    {
                        FreeLibrary(m_module);
                    }
                    m_module = std::exchange(other.m_module, nullptr);
                }
                return *this;
            }

            [[nodiscard]]
            HMODULE Get() const noexcept
            {
                return m_module;
            }

        private:
            HMODULE m_module;
        };

        [[nodiscard]]
        bool IsDigits(std::wstring_view value) noexcept
        {
            return !value.empty() &&
                std::ranges::all_of(value, [](wchar_t character)
                {
                    return character >= L'0' && character <= L'9';
                });
        }

        [[nodiscard]]
        std::optional<std::uint32_t> ParseNumber(std::wstring_view value)
        {
            if (!IsDigits(value))
            {
                return std::nullopt;
            }

            constexpr std::uint64_t Maximum =
                (std::numeric_limits<std::uint32_t>::max)();

            std::uint64_t result = 0;
            for (const wchar_t character : value)
            {
                const std::uint64_t digit =
                    static_cast<std::uint64_t>(character) -
                    static_cast<std::uint64_t>(L'0');
                if (result > (Maximum - digit) / 10u)
                {
                    return std::nullopt;
                }
                result = result * 10u + digit;
            }
            return static_cast<std::uint32_t>(result);
        }

        [[nodiscard]]
        std::vector<std::wstring> Split(
            std::wstring_view value,
            wchar_t separator)
        {
            std::vector<std::wstring> result;
            std::size_t start = 0;
            while (start <= value.size())
            {
                const std::size_t end = value.find(separator, start);
                if (end == std::wstring_view::npos)
                {
                    result.emplace_back(value.substr(start));
                    break;
                }
                result.emplace_back(value.substr(start, end - start));
                start = end + 1;
            }
            return result;
        }

        [[nodiscard]]
        std::optional<SemanticVersion> ParseSemanticVersion(
            std::wstring_view text)
        {
            if (text.empty())
            {
                return std::nullopt;
            }

            const std::size_t buildSeparator = text.find(L'+');
            if (buildSeparator != std::wstring_view::npos)
            {
                text = text.substr(0, buildSeparator);
            }

            const std::size_t prereleaseSeparator = text.find(L'-');
            const std::wstring_view numeric =
                prereleaseSeparator == std::wstring_view::npos
                    ? text
                    : text.substr(0, prereleaseSeparator);
            const std::wstring_view prerelease =
                prereleaseSeparator == std::wstring_view::npos
                    ? std::wstring_view{}
                    : text.substr(prereleaseSeparator + 1);

            SemanticVersion version{};
            version.Text = std::wstring(text);
            for (const std::wstring& component : Split(numeric, L'.'))
            {
                auto number = ParseNumber(component);
                if (!number)
                {
                    return std::nullopt;
                }
                version.Numbers.push_back(*number);
            }

            if (version.Numbers.empty())
            {
                return std::nullopt;
            }

            if (!prerelease.empty())
            {
                version.Prerelease = Split(prerelease, L'.');
                if (std::ranges::any_of(
                        version.Prerelease,
                        [](const std::wstring& value)
                        {
                            return value.empty();
                        }))
                {
                    return std::nullopt;
                }
            }
            return version;
        }

        [[nodiscard]]
        int CompareIdentifier(
            std::wstring_view left,
            std::wstring_view right)
        {
            const bool leftNumeric = IsDigits(left);
            const bool rightNumeric = IsDigits(right);
            if (leftNumeric && rightNumeric)
            {
                const auto leftNumber = ParseNumber(left).value_or(0);
                const auto rightNumber = ParseNumber(right).value_or(0);
                return leftNumber < rightNumber ? -1 : leftNumber > rightNumber ? 1 : 0;
            }
            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }
            return left < right ? -1 : left > right ? 1 : 0;
        }

        [[nodiscard]]
        int CompareVersions(
            const SemanticVersion& left,
            const SemanticVersion& right)
        {
            const std::size_t numericCount =
                (std::max)(left.Numbers.size(), right.Numbers.size());
            for (std::size_t index = 0; index < numericCount; ++index)
            {
                const std::uint32_t leftValue =
                    index < left.Numbers.size() ? left.Numbers[index] : 0;
                const std::uint32_t rightValue =
                    index < right.Numbers.size() ? right.Numbers[index] : 0;
                if (leftValue != rightValue)
                {
                    return leftValue < rightValue ? -1 : 1;
                }
            }

            if (left.IsPrerelease() != right.IsPrerelease())
            {
                return left.IsPrerelease() ? -1 : 1;
            }

            const std::size_t prereleaseCount =
                (std::min)(left.Prerelease.size(), right.Prerelease.size());
            for (std::size_t index = 0; index < prereleaseCount; ++index)
            {
                const int compared = CompareIdentifier(
                    left.Prerelease[index],
                    right.Prerelease[index]);
                if (compared != 0)
                {
                    return compared;
                }
            }

            return left.Prerelease.size() < right.Prerelease.size()
                ? -1
                : left.Prerelease.size() > right.Prerelease.size() ? 1 : 0;
        }

        [[nodiscard]]
        std::optional<std::wstring> EnvironmentVariable(
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
            return value;
        }

        [[nodiscard]]
        std::optional<std::filesystem::path> RegisteredDotNetRoot()
        {
            constexpr wchar_t RegistryPath[] =
                L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64";

            HKEY key = nullptr;
            if (RegOpenKeyExW(
                    HKEY_LOCAL_MACHINE,
                    RegistryPath,
                    0,
                    KEY_READ | KEY_WOW64_64KEY,
                    &key) != ERROR_SUCCESS)
            {
                return std::nullopt;
            }

            DWORD type = 0;
            DWORD bytes = 0;
            LONG status = RegQueryValueExW(
                key,
                L"InstallLocation",
                nullptr,
                &type,
                nullptr,
                &bytes);
            if (status != ERROR_SUCCESS ||
                (type != REG_SZ && type != REG_EXPAND_SZ) ||
                bytes < sizeof(wchar_t))
            {
                RegCloseKey(key);
                return std::nullopt;
            }

            std::wstring value(bytes / sizeof(wchar_t), L'\0');
            status = RegQueryValueExW(
                key,
                L"InstallLocation",
                nullptr,
                &type,
                reinterpret_cast<BYTE*>(value.data()),
                &bytes);
            RegCloseKey(key);
            if (status != ERROR_SUCCESS)
            {
                return std::nullopt;
            }

            while (!value.empty() && value.back() == L'\0')
            {
                value.pop_back();
            }
            if (value.empty())
            {
                return std::nullopt;
            }

            if (type == REG_EXPAND_SZ)
            {
                const DWORD expandedSize = ExpandEnvironmentStringsW(
                    value.c_str(),
                    nullptr,
                    0);
                if (expandedSize != 0)
                {
                    std::wstring expanded(expandedSize, L'\0');
                    const DWORD written = ExpandEnvironmentStringsW(
                        value.c_str(),
                        expanded.data(),
                        expandedSize);
                    if (written != 0 && written <= expandedSize)
                    {
                        while (!expanded.empty() && expanded.back() == L'\0')
                        {
                            expanded.pop_back();
                        }
                        value = std::move(expanded);
                    }
                }
            }
            return std::filesystem::path(std::move(value));
        }

        [[nodiscard]]
        std::vector<std::filesystem::path> CandidateRoots()
        {
            std::vector<std::filesystem::path> result;
            std::set<std::wstring, std::less<>> seen;

            const auto add = [&](std::optional<std::filesystem::path> path)
            {
                if (!path || path->empty())
                {
                    return;
                }
                std::error_code error;
                std::filesystem::path normalized =
                    std::filesystem::weakly_canonical(*path, error);
                if (error)
                {
                    normalized = path->lexically_normal();
                }
                std::wstring key = normalized.wstring();
                std::ranges::transform(key, key.begin(), [](wchar_t character)
                {
                    return std::towlower(character);
                });
                if (seen.insert(key).second)
                {
                    result.push_back(std::move(normalized));
                }
            };

            if (auto value = EnvironmentVariable(L"DOTNET_ROOT_X64"))
            {
                add(std::filesystem::path(*value));
            }
            if (auto value = EnvironmentVariable(L"DOTNET_ROOT"))
            {
                add(std::filesystem::path(*value));
            }
            add(RegisteredDotNetRoot());
            if (auto programFiles = EnvironmentVariable(L"ProgramFiles"))
            {
                add(std::filesystem::path(*programFiles) / L"dotnet");
            }
            return result;
        }

        [[nodiscard]]
        std::optional<std::filesystem::path> FindHostFxr(
            const std::filesystem::path& root,
            bool allowPrerelease)
        {
            const std::filesystem::path directory = root / L"host" / L"fxr";
            std::error_code error;
            if (!std::filesystem::is_directory(directory, error))
            {
                return std::nullopt;
            }

            std::optional<SemanticVersion> bestVersion;
            std::filesystem::path bestPath;
            for (const auto& entry : std::filesystem::directory_iterator(
                     directory,
                     std::filesystem::directory_options::skip_permission_denied,
                     error))
            {
                if (error || !entry.is_directory(error))
                {
                    continue;
                }

                const std::wstring name = entry.path().filename().wstring();
                auto version = ParseSemanticVersion(name);
                if (!version || (!allowPrerelease && version->IsPrerelease()))
                {
                    continue;
                }

                const std::filesystem::path candidate =
                    entry.path() / L"hostfxr.dll";
                if (!std::filesystem::is_regular_file(candidate, error))
                {
                    continue;
                }

                if (!bestVersion || CompareVersions(*bestVersion, *version) < 0)
                {
                    bestVersion = std::move(version);
                    bestPath = candidate;
                }
            }

            return bestVersion
                ? std::optional<std::filesystem::path>(std::move(bestPath))
                : std::nullopt;
        }

        void __cdecl CaptureEnvironment(
            const hostfxr_dotnet_environment_info* information,
            void* context)
        {
            if (information == nullptr || context == nullptr)
            {
                return;
            }

            auto& capture = *static_cast<EnvironmentCapture*>(context);
            if (information->hostfxr_version != nullptr)
            {
                capture.HostFxrVersion = information->hostfxr_version;
            }

            for (std::size_t index = 0;
                 index < information->framework_count;
                 ++index)
            {
                const auto& framework = information->frameworks[index];
                if (framework.name == nullptr || framework.version == nullptr ||
                    std::wstring_view(framework.name) != L"Microsoft.NETCore.App")
                {
                    continue;
                }

                const std::wstring version = framework.version;
                capture.Runtimes.push_back(RuntimeDescriptor{
                    .Name = framework.name,
                    .Version = version,
                    .Path = framework.path != nullptr
                        ? std::filesystem::path(framework.path)
                        : std::filesystem::path{},
                    .IsPrerelease =
                        version.contains(L'-')
                });
            }
        }

        [[nodiscard]]
        std::optional<RuntimeDescriptor> SelectNewestRuntime(
            std::span<const RuntimeDescriptor> runtimes,
            bool allowPrerelease)
        {
            std::optional<RuntimeDescriptor> best;
            std::optional<SemanticVersion> bestVersion;

            for (const RuntimeDescriptor& runtime : runtimes)
            {
                auto version = ParseSemanticVersion(runtime.Version);
                if (!version || (!allowPrerelease && version->IsPrerelease()))
                {
                    continue;
                }

                if (!bestVersion || CompareVersions(*bestVersion, *version) < 0)
                {
                    best = runtime;
                    bestVersion = std::move(version);
                }
            }
            return best;
        }
    }

    HostResult<DotNetEnvironment> InspectDotNetEnvironment(
        const HostConfiguration& configuration) noexcept
    {
        try
        {
            for (const std::filesystem::path& root : CandidateRoots())
            {
                auto hostFxrPath = FindHostFxr(
                    root,
                    configuration.AllowPrereleaseRuntime);
                if (!hostFxrPath)
                {
                    WriteLog(
                        LogLevel::Warning,
                        L"No eligible hostfxr was found under " +
                            root.wstring() + L".");
                    continue;
                }

                const std::wstring hostFxrPathText = hostFxrPath->wstring();
                LoadedLibrary hostFxr(LoadLibraryExW(
                    hostFxrPathText.c_str(),
                    nullptr,
                    LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
                        LOAD_LIBRARY_SEARCH_DEFAULT_DIRS));
                if (hostFxr.Get() == nullptr)
                {
                    WriteLog(
                        LogLevel::Warning,
                        L"hostfxr.dll could not be loaded from " +
                            hostFxrPath->wstring() + L".");
                    continue;
                }

                const auto getEnvironment =
                    reinterpret_cast<hostfxr_get_dotnet_environment_info_fn>(
                        GetProcAddress(
                            hostFxr.Get(),
                            "hostfxr_get_dotnet_environment_info"));
                if (getEnvironment == nullptr)
                {
                    WriteLog(
                        LogLevel::Warning,
                        L"The selected hostfxr does not expose "
                        L"hostfxr_get_dotnet_environment_info.");
                    continue;
                }

                EnvironmentCapture capture{};
                const std::wstring rootText = root.wstring();
                const std::int32_t status = getEnvironment(
                    rootText.c_str(),
                    nullptr,
                    &CaptureEnvironment,
                    &capture);
                if (status != 0)
                {
                    WriteLog(
                        LogLevel::Warning,
                        L"hostfxr_get_dotnet_environment_info failed with code " +
                            std::to_wstring(status) + L".");
                    continue;
                }

                DotNetEnvironment environment{
                    .Root = root,
                    .HostFxr = *hostFxrPath,
                    .HostFxrVersion = std::move(capture.HostFxrVersion),
                    .Runtimes = std::move(capture.Runtimes),
                    .NewestEligibleRuntime = std::nullopt
                };
                environment.NewestEligibleRuntime = SelectNewestRuntime(
                    environment.Runtimes,
                    configuration.AllowPrereleaseRuntime);

                if (!environment.NewestEligibleRuntime)
                {
                    return std::unexpected(
                        L"No eligible Microsoft.NETCore.App runtime is installed "
                        L"under " + root.wstring() + L".");
                }
                return environment;
            }

            return std::unexpected(
                L"No compatible x64 .NET installation could be located.");
        }
        catch (const std::exception& exception)
        {
            const int required = MultiByteToWideChar(
                CP_UTF8,
                0,
                exception.what(),
                -1,
                nullptr,
                0);
            std::wstring message;
            if (required > 1)
            {
                std::wstring buffer(
                    static_cast<std::size_t>(required),
                    L'\0');
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
            return std::unexpected(
                L".NET environment inspection failed: " + message);
        }
        catch (...)
        {
            return std::unexpected(
                L".NET environment inspection failed with an unknown exception.");
        }
    }
}
