#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>

#include <array>
#include <cstdint>
#include <expected>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace CoreCLRHostLoader
{
    inline constexpr std::wstring_view ProductName = L"CoreCLRHostLoader";

    inline constexpr std::wstring_view ManagedBrainContractId =
        L"7C8E18B7-2D11-4D1E-9C53-5E3A0A4A63A1";
    inline constexpr std::uint16_t ManagedBrainAbiMajor = 1;
    inline constexpr std::uint16_t ManagedBrainAbiMinor = 0;

    inline constexpr std::uint32_t MaximumNativeArguments = 32;
    inline constexpr std::uint32_t MaximumNativeResults = 4;

    template <typename T>
    using HostResult = std::expected<T, std::wstring>;

    enum class LogLevel
    {
        Information,
        Warning,
        Error
    };

    enum class SessionState : std::uint32_t
    {
        Created,
        Starting,
        Running,
        Stopping,
        Stopped,
        Faulted
    };

    enum class HostCapability : std::uint64_t
    {
        FrameBridge = 1ull << 0,
        NativeBridge = 1ull << 1,
        CooperativeShutdown = 1ull << 2
    };

    [[nodiscard]]
    constexpr HostCapability operator|(
        HostCapability left,
        HostCapability right) noexcept
    {
        return static_cast<HostCapability>(
            std::to_underlying(left) |
            std::to_underlying(right));
    }

    enum class NativeCallStatus : std::int32_t
    {
        Pending = -1,
        Success = 0,
        InvalidRequest = 1,
        TooManyArguments = 2,
        TooManyResults = 3,
        NativeReturnedNull = 4,
        SessionStopping = 5
    };

    struct HostPaths final
    {
        std::filesystem::path Module;
        std::filesystem::path Directory;
        std::filesystem::path Configuration;
        std::filesystem::path Log;
    };

    struct HostConfiguration final
    {
        bool AllowPrereleaseRuntime = false;
        std::wstring BrainAssembly;
    };

    struct HostState final
    {
        HostPaths Paths;
        HostConfiguration Configuration;
    };

    struct RuntimeDescriptor final
    {
        std::wstring Name;
        std::wstring Version;
        std::filesystem::path Path;
        bool IsPrerelease = false;
    };

    struct DotNetEnvironment final
    {
        std::filesystem::path Root;
        std::filesystem::path HostFxr;
        std::wstring HostFxrVersion;
        std::vector<RuntimeDescriptor> Runtimes;
        std::optional<RuntimeDescriptor> NewestEligibleRuntime;
    };

    struct ManagedBrain final
    {
        std::filesystem::path Assembly;
        std::wstring AssemblyName;
        std::wstring EntryType;
        std::wstring EntryMethod;
        std::wstring RuntimeTfm;
        std::wstring RuntimeFramework;
        std::wstring RuntimeVersion;
        std::uint16_t AbiMajor = 0;
        std::uint16_t AbiMinor = 0;
    };

#pragma pack(push, 8)
    struct FrameMailbox final
    {
        std::uint32_t Size = sizeof(FrameMailbox);
        std::uint32_t Reserved = 0;
        std::uint64_t FrameIndex = 0;
        std::int64_t PerformanceCounter = 0;
    };

    struct NativeCallMailbox final
    {
        std::uint32_t Size = sizeof(NativeCallMailbox);
        std::uint32_t ArgumentCount = 0;
        std::uint32_t RequestedResultCount = 0;
        std::int32_t Status =
            std::to_underlying(NativeCallStatus::Pending);
        std::uint64_t RequestId = 0;
        std::uint64_t Hash = 0;
        std::array<std::uint64_t, MaximumNativeArguments> Arguments{};
        std::array<std::uint64_t, MaximumNativeResults> Results{};
    };

    struct BrainRunRequest final
    {
        std::uint32_t Size = sizeof(BrainRunRequest);
        std::uint16_t AbiMajor = ManagedBrainAbiMajor;
        std::uint16_t AbiMinor = ManagedBrainAbiMinor;
        std::uint64_t Capabilities = std::to_underlying(
            HostCapability::FrameBridge |
            HostCapability::NativeBridge |
            HostCapability::CooperativeShutdown);
        HANDLE ReadyEvent = nullptr;
        HANDLE FrameRequestedEvent = nullptr;
        HANDLE FrameCompletedEvent = nullptr;
        HANDLE StopRequestedEvent = nullptr;
        HANDLE NativeRequestedEvent = nullptr;
        HANDLE NativeCompletedEvent = nullptr;
        FrameMailbox* Frame = nullptr;
        NativeCallMailbox* NativeCall = nullptr;
        std::uint64_t PerformanceFrequency = 0;
    };
#pragma pack(pop)

    static_assert(sizeof(FrameMailbox) == 24);
    static_assert(alignof(FrameMailbox) == 8);
    static_assert(sizeof(NativeCallMailbox) == 320);
    static_assert(alignof(NativeCallMailbox) == 8);
    static_assert(sizeof(BrainRunRequest) == 88);
    static_assert(alignof(BrainRunRequest) == 8);

    [[nodiscard]]
    HostResult<HostState> InitializeHostState(HMODULE module) noexcept;

    [[nodiscard]]
    HostResult<void> SaveHostConfiguration(const HostState& state) noexcept;

    [[nodiscard]]
    HostResult<std::filesystem::path> WriteRuntimeConfiguration(
        const HostState& state,
        const ManagedBrain& brain) noexcept;

    void WriteLog(LogLevel level, std::wstring_view message) noexcept;

    [[nodiscard]]
    HostResult<DotNetEnvironment> InspectDotNetEnvironment(
        const HostConfiguration& configuration) noexcept;

    [[nodiscard]]
    HostResult<std::optional<ManagedBrain>> DiscoverManagedBrain(
        const HostState& state) noexcept;

    [[nodiscard]]
    HostResult<void> RunManagedBrain(
        const HostConfiguration& configuration,
        const DotNetEnvironment& environment,
        const ManagedBrain& brain,
        const std::filesystem::path& runtimeConfiguration,
        const BrainRunRequest& request) noexcept;
}
