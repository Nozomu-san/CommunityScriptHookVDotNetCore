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
            using RuntimeDiagnosticHub diagnostics = new();
            using RuntimeFiles files = RuntimeFiles.Open(diagnostics);
            using RuntimeSession session = new(host, files, diagnostics);

            files.Log.Information(
                $"Managed runtime thread: {Environment.CurrentManagedThreadId}.");

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

internal sealed record ScriptEnvironment(string ScriptsDirectory) : IScriptEnvironment;

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
    private readonly RuntimeDiagnosticHub _diagnostics;
    private readonly NativeTransport _native;
    private readonly PackageManager _packages;
    private readonly RuntimeExtensionManager _extensions;
    private bool _rootInitializationCommitted;
    private bool _initialDiagnosticEpochSealed;
    private bool _initialized;
    private bool _shutdown;

    public RuntimeSession(
        HostRunRequest request,
        RuntimeFiles files,
        RuntimeDiagnosticHub diagnostics)
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
        _scheduler = new(files.Log);
        _diagnostics = diagnostics;
        _services.Register<IRuntimeDiagnosticSink>(_diagnostics);
        _native = new(
            request.NativeCall,
            BorrowEvent(request.NativeRequestedEvent, EventResetMode.AutoReset),
            BorrowEvent(request.NativeCompletedEvent, EventResetMode.AutoReset),
            BorrowEvent(request.StopRequestedEvent, EventResetMode.ManualReset));
        _services.Register<IScriptEnvironment>(
            new ScriptEnvironment(files.ScriptsDirectory));

        _packages = new(
            files.ScriptsDirectory,
            _services.ScriptServices,
            files.Log,
            _diagnostics);
        _extensions = new(
            files.RootDirectory,
            files.ExtensionsDirectory,
            files.ScriptsDirectory,
            _services,
            files.Log,
            _diagnostics);
        _packages.AttachExtensionReloadController(_extensions);

        _services.RegisterRuntimeOnly<IRawNativeTransport>(_native);
        _services.RegisterRuntimeOnly<INativeCallAdmissionControl>(_native);
        _services.RegisterRuntimeOnly<IGameThreadFunctionTransport>(_native);
        _services.RegisterRuntimeOnly<IReloadRuntimeHost>(_packages);
    }

    public void Initialize()
    {
        if (_initialized)
        {
            throw new InvalidOperationException(
                "The managed runtime session is already initialized.");
        }

        _diagnostics.EnterPhase(RuntimeDiagnosticPhase.Extensions);
        _extensions.PrepareInitialization();
        _initialized = true;
    }

    public void SignalReady() => _ready.Set();

    public BrainRunResult Run()
    {
        WaitHandle[] waits = [_stopRequested, _frameRequested];
        try
        {
            for (;;)
            {
                int signaled = WaitHandle.WaitAny(
                    waits,
                    _scheduler.GetWaitTimeoutMilliseconds());
                switch (signaled)
                {
                    case 0:
                        _log.Information(
                            "Managed runtime shutdown was requested.");
                        return BrainRunResult.Success;

                    case 1:
                        ProcessFrame();
                        break;

                    case WaitHandle.WaitTimeout:
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

            RuntimeExtensionFrameContext extensionFrame = new(
                frame.FrameIndex,
                frame.PerformanceCounter,
                _request.PerformanceFrequency);

            _extensions.AdvanceInitialization();
            if (!_extensions.InitializationCompleted)
            {
                return;
            }

            if (!_rootInitializationCommitted)
            {
                _packages.SetUnavailableRuntimeAssemblies(
                    _extensions.UnavailableAssemblyNames);
                _ = _extensions.DrainNewUnavailableAssemblyNames();
                _diagnostics.EnterPhase(RuntimeDiagnosticPhase.Scripts4);
                _packages.BeginInitialActivation();
                _rootInitializationCommitted = true;
            }

            _packages.AdvanceInitialActivation(frame.FrameIndex);
            if (!_packages.InitialActivationCompleted)
            {
                return;
            }

            if (!_initialDiagnosticEpochSealed)
            {
                _diagnostics.SealEpoch();
                _initialDiagnosticEpochSealed = true;
                return;
            }

            _extensions.AdvanceHostFrame(extensionFrame);
            IReadOnlyList<string> unavailableRoots =
                _extensions.DrainNewUnavailableAssemblyNames();
            if (unavailableRoots.Count != 0)
            {
                _packages.QuarantineAssemblyReferences(unavailableRoots);
            }
            _packages.AdvanceTransitionOperations(frame.FrameIndex);
            _scheduler.ObserveHostFrame(frame.FrameIndex);
            _scheduler.Start(frame.FrameIndex);
            DispatchScriptTickIfDue();
        }
        finally
        {
            _frameCompleted.Set();
        }
    }


    private void DispatchScriptTickIfDue()
    {
        if (!_rootInitializationCommitted ||
            !_packages.InitialActivationCompleted ||
            !_scheduler.IsStarted)
        {
            return;
        }

        _scheduler.DispatchIfDue(_packages.Tick);
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
            Task packageShutdown = _packages.ShutdownAsync(
                ScriptStopReason.RuntimeShutdown);
            Task extensionShutdown = _extensions.ShutdownAsync();
            Task.WhenAll(packageShutdown, extensionShutdown)
                .GetAwaiter()
                .GetResult();
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

internal sealed class NativeTransport :
    IRawNativeTransport,
    INativeCallAdmissionControl,
    IGameThreadFunctionTransport,
    IDisposable
{
    private readonly Lock _gate = new();
    private readonly nint _mailbox;
    private readonly EventWaitHandle _requested;
    private readonly EventWaitHandle _completed;
    private readonly EventWaitHandle _stopRequested;
    private readonly WaitHandle[] _waits;
    private INativeCallAdmissionPolicy? _admissionPolicy;
    private ulong _admissionGeneration;
    private ulong _requestId;

    public NativeTransport(
        nint mailbox,
        EventWaitHandle requested,
        EventWaitHandle completed,
        EventWaitHandle stopRequested)
    {
        ArgumentOutOfRangeException.ThrowIfZero(mailbox);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(stopRequested);

        _mailbox = mailbox;
        _requested = requested;
        _completed = completed;
        _stopRequested = stopRequested;
        _waits = [_stopRequested, _completed];
    }

    public IDisposable Install(INativeCallAdmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        lock (_gate)
        {
            if (_admissionPolicy is not null)
            {
                throw new InvalidOperationException(
                    "A native-call admission policy is already installed.");
            }

            _admissionPolicy = policy;
            ulong generation = ++_admissionGeneration;
            return new AdmissionLease(this, generation);
        }
    }

    public RawNativeCallResult Invoke(
        ulong hash,
        IReadOnlyList<ulong> arguments,
        int resultCount)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > HostContract.MaximumNativeArguments)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments));
        }
        if ((uint)resultCount > HostContract.MaximumNativeResults)
        {
            throw new ArgumentOutOfRangeException(nameof(resultCount));
        }
        if (!ManagedLifecycleAuthorityContext.AllowsNativeCalls)
        {
            return new(
                RawNativeCallStatus.AdmissionRejected,
                []);
        }
        ScriptExecutionMetricsContext.RecordNativeCall();
        lock (_gate)
        {
            if (!ManagedLifecycleAuthorityContext.AllowsNativeCalls)
            {
                return new(
                    RawNativeCallStatus.AdmissionRejected,
                    []);
            }

            INativeCallAdmissionPolicy? policy = _admissionPolicy;
            if (policy is null)
            {
                return new(
                    RawNativeCallStatus.AdmissionUnavailable,
                    []);
            }

            NativeCallAdmissionDecision admission;
            try
            {
                admission = policy.Evaluate(
                    hash,
                    arguments.Count,
                    resultCount);
            }
            catch
            {
                return new(
                    RawNativeCallStatus.AdmissionRejected,
                    []);
            }

            if (!admission.IsAllowed)
            {
                return new(
                    RawNativeCallStatus.AdmissionRejected,
                    []);
            }

            Marshal.WriteInt32(
                _mailbox,
                HostContract.NativeSizeOffset,
                HostContract.NativeMailboxSize);
            Marshal.WriteInt16(
                _mailbox,
                HostContract.NativeArgumentCountOffset,
                checked((short)arguments.Count));
            Marshal.WriteInt16(
                _mailbox,
                HostContract.NativeOperationOffset,
                (short)HostCallOperation.ScriptNative);
            Marshal.WriteInt32(
                _mailbox,
                HostContract.NativeRequestedResultCountOffset,
                resultCount);
            Marshal.WriteInt32(
                _mailbox,
                HostContract.NativeStatusOffset,
                (int)NativeCallStatus.Pending);
            Marshal.WriteInt64(
                _mailbox,
                HostContract.NativeRequestIdOffset,
                unchecked((long)++_requestId));
            Marshal.WriteInt64(
                _mailbox,
                HostContract.NativeHashOffset,
                unchecked((long)hash));

            for (int index = 0; index < arguments.Count; ++index)
            {
                Marshal.WriteInt64(
                    _mailbox,
                    HostContract.NativeArgumentsOffset + index * sizeof(ulong),
                    unchecked((long)arguments[index]));
            }

            if (!ManagedLifecycleAuthorityContext.AllowsNativeCalls)
            {
                return new(
                    RawNativeCallStatus.AdmissionRejected,
                    []);
            }

            _requested.Set();
            if (WaitForCompletion() == 0)
            {
                return new(
                    RawNativeCallStatus.SessionStopping,
                    []);
            }

            NativeCallStatus status = (NativeCallStatus)Marshal.ReadInt32(
                _mailbox,
                HostContract.NativeStatusOffset);
            ulong[] results = new ulong[resultCount];
            if (status == NativeCallStatus.Success)
            {
                for (int index = 0; index < resultCount; ++index)
                {
                    results[index] = unchecked((ulong)Marshal.ReadInt64(
                        _mailbox,
                        HostContract.NativeResultsOffset + index * sizeof(ulong)));
                }
            }
            return new(
                (RawNativeCallStatus)status,
                results);
        }
    }

    public GameThreadInt32CallResult InvokeInt32(
        nint functionAddress,
        nint pointerArgument)
    {
        if (functionAddress == 0 ||
            pointerArgument == 0 ||
            !ManagedLifecycleAuthorityContext.AllowsNativeCalls)
        {
            return new(
                GameThreadFunctionCallStatus.AdmissionRejected,
                0);
        }

        ScriptExecutionMetricsContext.RecordNativeCall();
        lock (_gate)
        {
            if (!ManagedLifecycleAuthorityContext.AllowsNativeCalls)
            {
                return new(
                    GameThreadFunctionCallStatus.AdmissionRejected,
                    0);
            }

            Marshal.WriteInt32(
                _mailbox,
                HostContract.NativeSizeOffset,
                HostContract.NativeMailboxSize);
            Marshal.WriteInt16(
                _mailbox,
                HostContract.NativeArgumentCountOffset,
                1);
            Marshal.WriteInt16(
                _mailbox,
                HostContract.NativeOperationOffset,
                (short)HostCallOperation.Int32FunctionOnePointer);
            Marshal.WriteInt32(
                _mailbox,
                HostContract.NativeRequestedResultCountOffset,
                1);
            Marshal.WriteInt32(
                _mailbox,
                HostContract.NativeStatusOffset,
                (int)NativeCallStatus.Pending);
            Marshal.WriteInt64(
                _mailbox,
                HostContract.NativeRequestIdOffset,
                unchecked((long)++_requestId));
            Marshal.WriteInt64(
                _mailbox,
                HostContract.NativeHashOffset,
                functionAddress.ToInt64());
            Marshal.WriteInt64(
                _mailbox,
                HostContract.NativeArgumentsOffset,
                pointerArgument.ToInt64());

            _requested.Set();
            if (WaitForCompletion() == 0)
            {
                return new(
                    GameThreadFunctionCallStatus.SessionStopping,
                    0);
            }

            NativeCallStatus status = (NativeCallStatus)Marshal.ReadInt32(
                _mailbox,
                HostContract.NativeStatusOffset);
            int value = status is NativeCallStatus.Success
                ? Marshal.ReadInt32(
                    _mailbox,
                    HostContract.NativeResultsOffset)
                : 0;

            return new(
                status switch
                {
                    NativeCallStatus.Success =>
                        GameThreadFunctionCallStatus.Success,
                    NativeCallStatus.SessionStopping =>
                        GameThreadFunctionCallStatus.SessionStopping,
                    NativeCallStatus.FunctionFault =>
                        GameThreadFunctionCallStatus.FunctionFault,
                    _ => GameThreadFunctionCallStatus.InvalidRequest
                },
                value);
        }
    }

    public GameThreadInt32CallResult InvokeInt32Guarded(
        nint functionAddress,
        nint pointerArgument,
        GameThreadMemoryGuard primaryGuard,
        GameThreadMemoryGuard secondaryGuard)
    {
        if (functionAddress == 0 ||
            pointerArgument == 0 ||
            !primaryGuard.IsConfigured ||
            !IsValidGuard(primaryGuard) ||
            (secondaryGuard.Address != 0 &&
             (!secondaryGuard.IsConfigured ||
              !IsValidGuard(secondaryGuard))) ||
            !ManagedLifecycleAuthorityContext.AllowsNativeCalls)
        {
            return new(
                GameThreadFunctionCallStatus.AdmissionRejected,
                0);
        }

        ScriptExecutionMetricsContext.RecordNativeCall();
        lock (_gate)
        {
            if (!ManagedLifecycleAuthorityContext.AllowsNativeCalls)
            {
                return new(
                    GameThreadFunctionCallStatus.AdmissionRejected,
                    0);
            }

            Marshal.WriteInt32(
                _mailbox,
                HostContract.NativeSizeOffset,
                HostContract.NativeMailboxSize);
            Marshal.WriteInt16(
                _mailbox,
                HostContract.NativeArgumentCountOffset,
                9);
            Marshal.WriteInt16(
                _mailbox,
                HostContract.NativeOperationOffset,
                (short)HostCallOperation.Int32FunctionOneGuardedObjectPointer);
            Marshal.WriteInt32(
                _mailbox,
                HostContract.NativeRequestedResultCountOffset,
                1);
            Marshal.WriteInt32(
                _mailbox,
                HostContract.NativeStatusOffset,
                (int)NativeCallStatus.Pending);
            Marshal.WriteInt64(
                _mailbox,
                HostContract.NativeRequestIdOffset,
                unchecked((long)++_requestId));
            Marshal.WriteInt64(
                _mailbox,
                HostContract.NativeHashOffset,
                functionAddress.ToInt64());

            WriteGuardedArgument(0, unchecked((ulong)(nuint)pointerArgument));
            WriteGuardedArgument(
                1,
                unchecked((ulong)(nuint)primaryGuard.Address));
            WriteGuardedArgument(2, primaryGuard.Mask);
            WriteGuardedArgument(3, primaryGuard.Expected);
            WriteGuardedArgument(4, checked((ulong)primaryGuard.Width));
            WriteGuardedArgument(
                5,
                unchecked((ulong)(nuint)secondaryGuard.Address));
            WriteGuardedArgument(6, secondaryGuard.Mask);
            WriteGuardedArgument(7, secondaryGuard.Expected);
            WriteGuardedArgument(
                8,
                secondaryGuard.Address == 0
                    ? 0
                    : checked((ulong)secondaryGuard.Width));

            _requested.Set();
            if (WaitForCompletion() == 0)
            {
                return new(
                    GameThreadFunctionCallStatus.SessionStopping,
                    0);
            }

            NativeCallStatus status = (NativeCallStatus)Marshal.ReadInt32(
                _mailbox,
                HostContract.NativeStatusOffset);
            int value = status is NativeCallStatus.Success
                ? Marshal.ReadInt32(
                    _mailbox,
                    HostContract.NativeResultsOffset)
                : 0;

            return new(
                status switch
                {
                    NativeCallStatus.Success =>
                        GameThreadFunctionCallStatus.Success,
                    NativeCallStatus.SessionStopping =>
                        GameThreadFunctionCallStatus.SessionStopping,
                    NativeCallStatus.FunctionFault =>
                        GameThreadFunctionCallStatus.FunctionFault,
                    NativeCallStatus.GuardRejected =>
                        GameThreadFunctionCallStatus.GuardRejected,
                    _ => GameThreadFunctionCallStatus.InvalidRequest
                },
                value);
        }
    }

    private int WaitForCompletion() =>
        WaitHandle.WaitAny(_waits);

    private static bool IsValidGuard(GameThreadMemoryGuard guard)
    {
        ulong widthMask = guard.Width switch
        {
            1 => byte.MaxValue,
            2 => ushort.MaxValue,
            4 => uint.MaxValue,
            8 => ulong.MaxValue,
            _ => 0
        };

        return widthMask != 0 &&
               (guard.Mask & ~widthMask) == 0 &&
               (guard.Expected & ~guard.Mask) == 0;
    }

    private void WriteGuardedArgument(int index, ulong value) =>
        Marshal.WriteInt64(
            _mailbox,
            HostContract.NativeArgumentsOffset + index * sizeof(ulong),
            unchecked((long)value));


    private void RemoveAdmissionPolicy(ulong generation)
    {
        lock (_gate)
        {
            if (_admissionPolicy is null ||
                generation != _admissionGeneration)
            {
                return;
            }

            _admissionPolicy = null;
            ++_admissionGeneration;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _admissionPolicy = null;
            ++_admissionGeneration;
        }

        _stopRequested.Dispose();
        _completed.Dispose();
        _requested.Dispose();
    }

    private sealed class AdmissionLease(
        NativeTransport owner,
        ulong generation) : IDisposable
    {
        private NativeTransport? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveAdmissionPolicy(
                generation);
    }
}