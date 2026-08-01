using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace CommunityScriptHookVDotNetCore.Source;

internal static class Runtime
{
    private static int s_running;

    public static BrainRunResult Run(nint request, int requestSize)
    {
        BrainRunResult validation = HostContract.Validate(
            request,
            requestSize,
            out HostRunRequest host);
        if (validation != BrainRunResult.Success)
        {
            return validation;
        }

        if (Interlocked.CompareExchange(ref s_running, 1, 0) != 0)
        {
            return BrainRunResult.AlreadyRunning;
        }

        try
        {
            using RuntimeFiles files = RuntimeFiles.Open();
            using RuntimeSession session = new(host, files);

            files.Log.Information(
                $"Managed runtime thread: {Environment.CurrentManagedThreadId}.");
            files.Log.Information(
                "Frame, raw-native, and cooperative-shutdown bridges are available.");

            session.Initialize();
            session.SignalReady();
            return session.Run();
        }
        catch (Exception exception)
        {
            RuntimeLog.TryWriteEmergency(exception);
            return BrainRunResult.InternalFailure;
        }
        finally
        {
            Volatile.Write(ref s_running, 0);
        }
    }
}

internal sealed class RuntimeSession : IDisposable
{
    private readonly HostRunRequest _request;
    private readonly RuntimeLog _log;
    private readonly EventWaitHandle _ready;
    private readonly EventWaitHandle _frameRequested;
    private readonly EventWaitHandle _frameCompleted;
    private readonly EventWaitHandle _stopRequested;
    private readonly TickScheduler _scheduler;
    private readonly RuntimeServiceRegistry _services = new();
    private readonly NativeTransport _native;
    private readonly PackageManager _packages;
    private readonly RuntimeExtensionManager _extensions;
    private bool _initialized;
    private bool _shutdown;

    public RuntimeSession(HostRunRequest request, RuntimeFiles files)
    {
        _request = request;
        _log = files.Log;
        _ready = BorrowEvent(request.ReadyEvent, EventResetMode.ManualReset);
        _frameRequested = BorrowEvent(
            request.FrameRequestedEvent,
            EventResetMode.AutoReset);
        _frameCompleted = BorrowEvent(
            request.FrameCompletedEvent,
            EventResetMode.AutoReset);
        _stopRequested = BorrowEvent(
            request.StopRequestedEvent,
            EventResetMode.ManualReset);
        _scheduler = new(
            files.Configuration,
            request.PerformanceFrequency,
            files.Log);
        _native = new(
            request.NativeCall,
            BorrowEvent(request.NativeRequestedEvent, EventResetMode.AutoReset),
            BorrowEvent(request.NativeCompletedEvent, EventResetMode.AutoReset),
            BorrowEvent(request.StopRequestedEvent, EventResetMode.ManualReset));
        _packages = new(
            files.ScriptsDirectory,
            _services.ScriptServices,
            files.Log);
        _extensions = new(
            files.RootDirectory,
            files.ScriptsDirectory,
            _services,
            files.Log);

        _services.RegisterRuntimeOnly<IRawNativeTransport>(_native);
        _services.RegisterRuntimeOnly<IReloadRuntimeHost>(_packages);
    }

    public void Initialize()
    {
        if (_initialized)
        {
            throw new InvalidOperationException(
                "The managed runtime session is already initialized.");
        }

        _packages.RefreshCatalog();
        _initialized = true;
        _extensions.LoadAndInitialize();
    }

    public void SignalReady() => _ready.Set();

    public BrainRunResult Run()
    {
        WaitHandle[] waits = [_stopRequested, _frameRequested];
        try
        {
            for (;;)
            {
                switch (WaitHandle.WaitAny(waits))
                {
                    case 0:
                        _log.Information(
                            "Managed runtime shutdown was requested.");
                        return BrainRunResult.Success;

                    case 1:
                        ProcessFrame();
                        break;

                    default:
                        return BrainRunResult.InternalFailure;
                }
            }
        }
        finally
        {
            Shutdown();
        }
    }

    public void Dispose()
    {
        Shutdown();
        _extensions.Dispose();
        _native.Dispose();
        _stopRequested.Dispose();
        _frameCompleted.Dispose();
        _frameRequested.Dispose();
        _ready.Dispose();
    }

    private void ProcessFrame()
    {
        try
        {
            HostFrameMailbox frame =
                Marshal.PtrToStructure<HostFrameMailbox>(_request.Frame);
            if (frame.Size < HostContract.FrameMailboxSize)
            {
                throw new InvalidOperationException(
                    "The host frame mailbox is incompatible.");
            }

            _extensions.AdvanceHostFrame(new(
                frame.FrameIndex,
                frame.PerformanceCounter,
                _request.PerformanceFrequency));
            _packages.AdvanceTransitionOperations(frame.FrameIndex);
            _scheduler.Dispatch(frame, _packages.Tick);
        }
        finally
        {
            _frameCompleted.Set();
        }
    }

    private void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }
        _shutdown = true;

        if (_initialized)
        {
            _packages.BeginShutdown();
            _packages.StopAll(ScriptStopReason.RuntimeShutdown);
            _extensions.Shutdown();
        }
    }

    private static EventWaitHandle BorrowEvent(
        nint handle,
        EventResetMode resetMode)
    {
        EventWaitHandle value = new(false, resetMode)
        {
            SafeWaitHandle = new(handle, ownsHandle: false)
        };
        return value;
    }
}

internal sealed class NativeTransport(
    nint mailbox,
    EventWaitHandle requested,
    EventWaitHandle completed,
    EventWaitHandle stopRequested) : IRawNativeTransport, IDisposable
{
    private readonly Lock _gate = new();
    private readonly WaitHandle[] _waits = [stopRequested, completed];
    private ulong _requestId;

    public RawNativeCallResult Invoke(
        ulong hash,
        ReadOnlySpan<ulong> arguments,
        int resultCount)
    {
        if (arguments.Length > HostContract.MaximumNativeArguments)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments));
        }
        if ((uint)resultCount > HostContract.MaximumNativeResults)
        {
            throw new ArgumentOutOfRangeException(nameof(resultCount));
        }

        lock (_gate)
        {
            Marshal.WriteInt32(
                mailbox,
                HostContract.NativeSizeOffset,
                HostContract.NativeMailboxSize);
            Marshal.WriteInt32(
                mailbox,
                HostContract.NativeArgumentCountOffset,
                arguments.Length);
            Marshal.WriteInt32(
                mailbox,
                HostContract.NativeRequestedResultCountOffset,
                resultCount);
            Marshal.WriteInt32(
                mailbox,
                HostContract.NativeStatusOffset,
                (int)NativeCallStatus.Pending);
            Marshal.WriteInt64(
                mailbox,
                HostContract.NativeRequestIdOffset,
                unchecked((long)++_requestId));
            Marshal.WriteInt64(
                mailbox,
                HostContract.NativeHashOffset,
                unchecked((long)hash));

            for (int index = 0; index < arguments.Length; ++index)
            {
                Marshal.WriteInt64(
                    mailbox,
                    HostContract.NativeArgumentsOffset + index * sizeof(ulong),
                    unchecked((long)arguments[index]));
            }

            requested.Set();
            if (WaitHandle.WaitAny(_waits) == 0)
            {
                return new(
                    RawNativeCallStatus.SessionStopping,
                    []);
            }

            NativeCallStatus status = (NativeCallStatus)Marshal.ReadInt32(
                mailbox,
                HostContract.NativeStatusOffset);
            ulong[] results = new ulong[resultCount];
            if (status == NativeCallStatus.Success)
            {
                for (int index = 0; index < resultCount; ++index)
                {
                    results[index] = unchecked((ulong)Marshal.ReadInt64(
                        mailbox,
                        HostContract.NativeResultsOffset + index * sizeof(ulong)));
                }
            }
            return new(
                (RawNativeCallStatus)status,
                results);
        }
    }

    public void Dispose()
    {
        stopRequested.Dispose();
        completed.Dispose();
        requested.Dispose();
    }
}