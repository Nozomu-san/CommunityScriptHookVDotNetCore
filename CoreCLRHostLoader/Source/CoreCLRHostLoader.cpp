#include "CoreCLRHostLoader.hpp"
#include "ScriptHookV.hpp"

#include <algorithm>
#include <atomic>
#include <process.h>
#include <mutex>
#include <utility>

namespace
{
    using namespace CoreCLRHostLoader;

    constexpr double SessionTimeoutSeconds = 30.0;

    std::atomic<HMODULE> g_module = nullptr;
    std::atomic<bool> g_shutdownRequested = false;
    std::atomic<HANDLE> g_stopEvent = nullptr;

    class HostSession final
    {
    public:
        HostSession() = default;

        ~HostSession()
        {
            StopAndJoin();
            CloseHandles();
        }

        HostSession(const HostSession&) = delete;
        HostSession& operator=(const HostSession&) = delete;
        HostSession(HostSession&&) = delete;
        HostSession& operator=(HostSession&&) = delete;

        [[nodiscard]]
        HostResult<void> Start(
            HostConfiguration configuration,
            DotNetEnvironment environment,
            ManagedBrain brain,
            std::filesystem::path runtimeConfiguration)
        {
            if (m_state.load(std::memory_order_acquire) !=
                SessionState::Created)
            {
                return std::unexpected(
                    L"The managed host session has already been started.");
            }

            auto events = CreateEvents();
            if (!events)
            {
                return events;
            }

            LARGE_INTEGER frequency{};
            if (QueryPerformanceFrequency(&frequency) == FALSE ||
                frequency.QuadPart <= 0)
            {
                return std::unexpected(
                    L"QueryPerformanceFrequency failed for the frame bridge.");
            }

            m_configuration = std::move(configuration);
            m_environment = std::move(environment);
            m_brain = std::move(brain);
            m_runtimeConfiguration = std::move(runtimeConfiguration);
            m_frequency = frequency.QuadPart;
            m_request.ReadyEvent = m_readyEvent;
            m_request.FrameRequestedEvent = m_frameRequestedEvent;
            m_request.FrameCompletedEvent = m_frameCompletedEvent;
            m_request.StopRequestedEvent = m_stopRequestedEvent;
            m_request.NativeRequestedEvent = m_nativeRequestedEvent;
            m_request.NativeCompletedEvent = m_nativeCompletedEvent;
            m_request.Frame = &m_frame;
            m_request.NativeCall = &m_nativeCall;
            m_request.PerformanceFrequency =
                static_cast<std::uint64_t>(m_frequency);

            m_state.store(SessionState::Starting, std::memory_order_release);
            LARGE_INTEGER started{};
            QueryPerformanceCounter(&started);
            m_startCounter = started.QuadPart;

            unsigned threadId = 0;
            const std::uintptr_t rawThread = _beginthreadex(
                nullptr,
                0,
                &HostSession::WorkerEntry,
                this,
                0,
                &threadId);
            if (rawThread == 0)
            {
                m_state.store(SessionState::Faulted, std::memory_order_release);
                return std::unexpected(
                    L"The dedicated managed host thread could not be created.");
            }

            m_thread = reinterpret_cast<HANDLE>(rawThread);
            m_threadId = threadId;
            g_stopEvent.store(m_stopRequestedEvent, std::memory_order_release);
            WriteLog(
                LogLevel::Information,
                L"Dedicated managed host thread created: " +
                    std::to_wstring(m_threadId) + L".");
            return {};
        }

        [[nodiscard]]
        bool AdvanceFrame()
        {
            ObserveWorkerExit();
            SessionState state = m_state.load(std::memory_order_acquire);

            if (state == SessionState::Starting)
            {
                ServiceNativeRequest();
                if (WaitForSingleObject(m_readyEvent, 0) == WAIT_OBJECT_0)
                {
                    state = m_state.load(std::memory_order_acquire);
                    if (state == SessionState::Starting)
                    {
                        m_state.store(
                            SessionState::Running,
                            std::memory_order_release);
                        WriteLog(
                            LogLevel::Information,
                            L"Managed brain session is ready.");
                        state = SessionState::Running;
                    }
                }
                else if (HasTimedOut(m_startCounter))
                {
                    SetFault(
                        L"The managed brain did not become ready within the "
                        L"startup timeout.");
                    RequestStop();
                    return false;
                }
            }

            if (state == SessionState::Running)
            {
                ServiceNativeRequest();

                LARGE_INTEGER counter{};
                if (QueryPerformanceCounter(&counter) == FALSE)
                {
                    SetFault(
                        L"QueryPerformanceCounter failed for the frame bridge.");
                    RequestStop();
                    return false;
                }

                m_frame.FrameIndex = ++m_frameIndex;
                m_frame.PerformanceCounter = counter.QuadPart;
                m_frameStartCounter = counter.QuadPart;
                if (SetEvent(m_frameRequestedEvent) == FALSE)
                {
                    SetFault(
                        L"The managed frame request could not be signaled.");
                    RequestStop();
                    return false;
                }

                m_frameInFlight = true;
                return PumpFrameTransaction();
            }

            if (state == SessionState::Stopping)
            {
                ServiceStoppingNativeRequest();
                return true;
            }

            return state != SessionState::Stopped &&
                state != SessionState::Faulted;
        }

        void RequestStop() noexcept
        {
            SessionState state = m_state.load(std::memory_order_acquire);
            while (state != SessionState::Stopped &&
                   state != SessionState::Faulted &&
                   state != SessionState::Stopping)
            {
                if (m_state.compare_exchange_weak(
                        state,
                        SessionState::Stopping,
                        std::memory_order_acq_rel,
                        std::memory_order_acquire))
                {
                    break;
                }
            }

            if (m_stopRequestedEvent != nullptr)
            {
                SetEvent(m_stopRequestedEvent);
            }
            ServiceStoppingNativeRequest();
        }

        void StopAndJoin() noexcept
        {
            if (m_thread == nullptr)
            {
                return;
            }

            RequestStop();
            WaitForSingleObject(m_thread, INFINITE);
            CloseHandle(m_thread);
            m_thread = nullptr;
            g_stopEvent.store(nullptr, std::memory_order_release);
        }

        [[nodiscard]]
        SessionState State() const noexcept
        {
            return m_state.load(std::memory_order_acquire);
        }

        [[nodiscard]]
        std::wstring Error() const
        {
            std::scoped_lock lock(m_errorMutex);
            return m_error;
        }

    private:
        [[nodiscard]]
        HostResult<void> CreateEvents()
        {
            m_readyEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            m_frameRequestedEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            m_frameCompletedEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            m_stopRequestedEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            m_nativeRequestedEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            m_nativeCompletedEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);

            if (m_readyEvent == nullptr ||
                m_frameRequestedEvent == nullptr ||
                m_frameCompletedEvent == nullptr ||
                m_stopRequestedEvent == nullptr ||
                m_nativeRequestedEvent == nullptr ||
                m_nativeCompletedEvent == nullptr)
            {
                CloseHandles();
                return std::unexpected(
                    L"The managed session synchronization events could not be "
                    L"created.");
            }
            return {};
        }

        void CloseHandles() noexcept
        {
            const auto close = [](HANDLE& handle)
            {
                if (handle != nullptr)
                {
                    CloseHandle(handle);
                    handle = nullptr;
                }
            };

            close(m_nativeCompletedEvent);
            close(m_nativeRequestedEvent);
            close(m_stopRequestedEvent);
            close(m_frameCompletedEvent);
            close(m_frameRequestedEvent);
            close(m_readyEvent);
        }

        [[nodiscard]]
        bool HasTimedOut(std::int64_t started) const noexcept
        {
            if (started <= 0 || m_frequency <= 0)
            {
                return false;
            }

            LARGE_INTEGER now{};
            if (QueryPerformanceCounter(&now) == FALSE)
            {
                return false;
            }

            const double elapsed =
                static_cast<double>(now.QuadPart - started) /
                static_cast<double>(m_frequency);
            return elapsed >= SessionTimeoutSeconds;
        }

        [[nodiscard]]
        bool PumpFrameTransaction()
        {
            const std::array<HANDLE, 4> waits{
                m_frameCompletedEvent,
                m_nativeRequestedEvent,
                m_stopRequestedEvent,
                m_thread
            };

            while (m_frameInFlight)
            {
                const DWORD status = WaitForMultipleObjects(
                    static_cast<DWORD>(waits.size()),
                    waits.data(),
                    FALSE,
                    10);
                if (status == WAIT_OBJECT_0)
                {
                    m_frameInFlight = false;
                    return true;
                }
                if (status == WAIT_OBJECT_0 + 1)
                {
                    ExecuteNativeRequest();
                    continue;
                }
                if (status == WAIT_OBJECT_0 + 2)
                {
                    RequestStop();
                    return false;
                }
                if (status == WAIT_OBJECT_0 + 3)
                {
                    ObserveWorkerExit();
                    return false;
                }
                if (status == WAIT_TIMEOUT)
                {
                    if (!HasTimedOut(m_frameStartCounter))
                    {
                        continue;
                    }

                    SetFault(
                        L"The managed brain did not complete a frame within "
                        L"the frame timeout.");
                    RequestStop();
                    return false;
                }

                SetFault(
                    L"The managed frame transaction wait failed.");
                RequestStop();
                return false;
            }
            return true;
        }

        void ServiceNativeRequest() noexcept
        {
            if (WaitForSingleObject(m_nativeRequestedEvent, 0) ==
                WAIT_OBJECT_0)
            {
                ExecuteNativeRequest();
            }
        }

        void ExecuteNativeRequest() noexcept
        {
            std::ranges::fill(m_nativeCall.Results, 0);
            NativeCallStatus status = NativeCallStatus::Success;
            if (m_nativeCall.Size != sizeof(NativeCallMailbox))
            {
                status = NativeCallStatus::InvalidRequest;
            }
            else if (m_nativeCall.ArgumentCount > MaximumNativeArguments)
            {
                status = NativeCallStatus::TooManyArguments;
            }
            else if (m_nativeCall.RequestedResultCount > MaximumNativeResults)
            {
                status = NativeCallStatus::TooManyResults;
            }
            else
            {
                nativeInit(m_nativeCall.Hash);
                for (std::uint32_t index = 0;
                     index < m_nativeCall.ArgumentCount;
                     ++index)
                {
                    nativePush64(m_nativeCall.Arguments[index]);
                }

                const std::uint64_t* result = nativeCall();
                if (result == nullptr)
                {
                    status = NativeCallStatus::NativeReturnedNull;
                }
                else
                {
                    for (std::uint32_t index = 0;
                         index < m_nativeCall.RequestedResultCount;
                         ++index)
                    {
                        m_nativeCall.Results[index] = result[index];
                    }
                }
            }

            m_nativeCall.Status = std::to_underlying(status);
            SetEvent(m_nativeCompletedEvent);
        }

        void ServiceStoppingNativeRequest() noexcept
        {
            if (m_nativeRequestedEvent == nullptr ||
                m_nativeCompletedEvent == nullptr)
            {
                return;
            }

            if (WaitForSingleObject(m_nativeRequestedEvent, 0) ==
                WAIT_OBJECT_0)
            {
                m_nativeCall.Status =
                    std::to_underlying(NativeCallStatus::SessionStopping);
                SetEvent(m_nativeCompletedEvent);
            }
        }

        void ObserveWorkerExit()
        {
            if (m_thread == nullptr ||
                WaitForSingleObject(m_thread, 0) != WAIT_OBJECT_0)
            {
                return;
            }

            const SessionState state = m_state.load(std::memory_order_acquire);
            if (state == SessionState::Starting ||
                state == SessionState::Running)
            {
                SetFault(
                    L"The managed brain session ended unexpectedly.");
            }
        }

        void SetFault(std::wstring message)
        {
            {
                std::scoped_lock lock(m_errorMutex);
                if (m_error.empty())
                {
                    m_error = std::move(message);
                }
            }
            m_state.store(SessionState::Faulted, std::memory_order_release);
            if (m_readyEvent != nullptr)
            {
                SetEvent(m_readyEvent);
            }
            if (m_frameCompletedEvent != nullptr)
            {
                SetEvent(m_frameCompletedEvent);
            }
            ServiceStoppingNativeRequest();
        }

        static unsigned __stdcall WorkerEntry(void* context) noexcept
        {
            auto& session = *static_cast<HostSession*>(context);
            auto result = RunManagedBrain(
                session.m_configuration,
                session.m_environment,
                session.m_brain,
                session.m_runtimeConfiguration,
                session.m_request);

            if (!result)
            {
                session.SetFault(result.error());
            }
            else
            {
                const SessionState current =
                    session.m_state.load(std::memory_order_acquire);
                const bool stopRequested =
                    WaitForSingleObject(
                        session.m_stopRequestedEvent,
                        0) == WAIT_OBJECT_0;
                if (current == SessionState::Faulted)
                {
                }
                else if (stopRequested || current == SessionState::Stopping)
                {
                    session.m_state.store(
                        SessionState::Stopped,
                        std::memory_order_release);
                }
                else
                {
                    session.SetFault(
                        L"The managed brain returned without a shutdown request.");
                }
            }

            SetEvent(session.m_readyEvent);
            SetEvent(session.m_frameCompletedEvent);
            SetEvent(session.m_nativeCompletedEvent);
            return 0;
        }

        HostConfiguration m_configuration;
        DotNetEnvironment m_environment;
        ManagedBrain m_brain;
        std::filesystem::path m_runtimeConfiguration;
        BrainRunRequest m_request{};
        FrameMailbox m_frame{};
        NativeCallMailbox m_nativeCall{};

        HANDLE m_readyEvent = nullptr;
        HANDLE m_frameRequestedEvent = nullptr;
        HANDLE m_frameCompletedEvent = nullptr;
        HANDLE m_stopRequestedEvent = nullptr;
        HANDLE m_nativeRequestedEvent = nullptr;
        HANDLE m_nativeCompletedEvent = nullptr;
        HANDLE m_thread = nullptr;
        unsigned m_threadId = 0;

        std::atomic<SessionState> m_state = SessionState::Created;
        mutable std::mutex m_errorMutex;
        std::wstring m_error;
        std::uint64_t m_frameIndex = 0;
        std::int64_t m_frequency = 0;
        std::int64_t m_startCounter = 0;
        std::int64_t m_frameStartCounter = 0;
        bool m_frameInFlight = false;
    };

    void IdleUntilShutdown()
    {
        while (!g_shutdownRequested.load(std::memory_order_acquire))
        {
            scriptWait(0);
        }
    }

    void RunHost() noexcept
    {
        const HMODULE module = g_module.load(std::memory_order_acquire);
        if (module == nullptr)
        {
            return;
        }

        auto state = InitializeHostState(module);
        if (!state)
        {
            return;
        }

        auto environment = InspectDotNetEnvironment(state->Configuration);
        if (!environment)
        {
            WriteLog(LogLevel::Error, environment.error());
            IdleUntilShutdown();
            return;
        }

        WriteLog(
            LogLevel::Information,
            L".NET root: " + environment->Root.wstring());
        WriteLog(
            LogLevel::Information,
            L"hostfxr: " + environment->HostFxr.wstring() + L" (" +
                environment->HostFxrVersion + L").");

        if (environment->NewestEligibleRuntime)
        {
            WriteLog(
                LogLevel::Information,
                L"Newest eligible Microsoft.NETCore.App runtime: " +
                    environment->NewestEligibleRuntime->Version + L".");
        }

        WriteLog(
            LogLevel::Information,
            state->Configuration.AllowPrereleaseRuntime
                ? L"Prerelease .NET runtimes are eligible."
                : L"Prerelease .NET runtimes are disabled.");

        auto brain = DiscoverManagedBrain(*state);
        if (!brain)
        {
            WriteLog(LogLevel::Error, brain.error());
            IdleUntilShutdown();
            return;
        }
        if (!*brain)
        {
            WriteLog(
                LogLevel::Warning,
                L"No compatible managed brain was found in the host directory.");
            WriteLog(
                LogLevel::Information,
                L"Managed hosting was skipped for this session.");
            IdleUntilShutdown();
            return;
        }

        const std::wstring discoveredName = (*brain)->AssemblyName;
        if (state->Configuration.BrainAssembly != discoveredName)
        {
            state->Configuration.BrainAssembly = discoveredName;
            auto saved = SaveHostConfiguration(*state);
            if (!saved)
            {
                WriteLog(LogLevel::Error, saved.error());
                IdleUntilShutdown();
                return;
            }
        }

        WriteLog(
            LogLevel::Information,
            L"Managed brain discovered: " +
                (*brain)->Assembly.filename().wstring() + L".");

        auto runtimeConfiguration =
            WriteRuntimeConfiguration(*state, **brain);
        if (!runtimeConfiguration)
        {
            WriteLog(LogLevel::Error, runtimeConfiguration.error());
            IdleUntilShutdown();
            return;
        }

        WriteLog(
            LogLevel::Information,
            L"Runtime configuration is ready: " +
                runtimeConfiguration->wstring());

        HostSession session;
        auto started = session.Start(
            state->Configuration,
            *environment,
            **brain,
            *runtimeConfiguration);
        if (!started)
        {
            WriteLog(LogLevel::Error, started.error());
            IdleUntilShutdown();
            return;
        }

        while (!g_shutdownRequested.load(std::memory_order_acquire))
        {
            if (!session.AdvanceFrame())
            {
                break;
            }
            scriptWait(0);
        }

        session.StopAndJoin();
        if (session.State() == SessionState::Faulted)
        {
            const std::wstring error = session.Error();
            WriteLog(
                LogLevel::Error,
                error.empty()
                    ? L"The managed brain session faulted."
                    : error);
        }
        else
        {
            WriteLog(
                LogLevel::Information,
                L"Managed brain session stopped cooperatively.");
        }
    }

    void ScriptMain()
    {
        RunHost();
    }
}

BOOL WINAPI DllMain(
    HMODULE module,
    DWORD reason,
    LPVOID reserved)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_module.store(module, std::memory_order_release);
        DisableThreadLibraryCalls(module);
        scriptRegister(module, &ScriptMain);
        break;

    case DLL_PROCESS_DETACH:
        g_shutdownRequested.store(true, std::memory_order_release);
        if (const HANDLE stop =
                g_stopEvent.load(std::memory_order_acquire);
            stop != nullptr)
        {
            SetEvent(stop);
        }
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
