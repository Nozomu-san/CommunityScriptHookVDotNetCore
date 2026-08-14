#include "CoreCLRHostLoader.hpp"

#include <cstdint>
#include <exception>
#include <sstream>
#include <utility>
#include <vector>

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

        [[nodiscard]] HostResult<std::filesystem::path> ResolveHostFxr(
            const ManagedBrain& brain)
        {
            LoadedLibrary nethost(LoadLibraryExW(
                L"nethost.dll",
                nullptr,
                LOAD_LIBRARY_SEARCH_APPLICATION_DIR |
                    LOAD_LIBRARY_SEARCH_DEFAULT_DIRS));
            if (nethost.Get() == nullptr)
            {
                return std::unexpected(
                    L"nethost.dll could not be loaded. Deploy the Microsoft "
                    L"native hosting resolver beside CoreCLRHostLoader.");
            }

            const auto getHostFxrPath = reinterpret_cast<get_hostfxr_path_fn>(
                GetProcAddress(nethost.Get(), "get_hostfxr_path"));
            if (getHostFxrPath == nullptr)
            {
                return std::unexpected(
                    L"nethost.dll does not expose get_hostfxr_path.");
            }

            const std::wstring assemblyPath = brain.Assembly.wstring();
            get_hostfxr_parameters parameters{};
            parameters.assembly_path = assemblyPath.c_str();

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

            auto hostFxrPath = ResolveHostFxr(brain);
            if (!hostFxrPath)
            {
                return std::unexpected(hostFxrPath.error());
            }

            WriteLog(LogLevel::Information, L"hostfxr resolved by nethost: " +
                hostFxrPath->wstring());

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
            EnvironmentVariableOverride prereleasePolicy(
                L"DOTNET_ROLL_FORWARD_TO_PRERELEASE",
                configuration.AllowPrereleaseRuntime ? L"1" : L"0");
            if (!prereleasePolicy.Applied())
            {
                return std::unexpected(
                    L"The prerelease runtime policy could not be applied.");
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