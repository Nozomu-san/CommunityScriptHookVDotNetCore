namespace CommunityScriptHookVDotNetCore.Source;

internal sealed class TickScheduler(
    RuntimeConfiguration configuration,
    ulong performanceFrequency,
    RuntimeLog log)
{
    private const int MaximumCatchUpTicksPerHostFrame = 64;

    private readonly long _frequency = checked((long)performanceFrequency);
    private readonly int _lockedTickRate =
        configuration.TickMode.UsesLockedRate
            ? configuration.LockedTickRate
            : 0;

    private bool _clockEstablished;
    private bool _backlogWarningWritten;
    private long _previousCounter;
    private Int128 _lockedAccumulator;
    private ulong _tickIndex;
    private TimeSpan _elapsedTime;

    public void Dispatch(
        HostFrameMailbox frame,
        Action<ScriptTickContext> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        if (!_clockEstablished)
        {
            _clockEstablished = true;
            _previousCounter = frame.PerformanceCounter;
            WriteClockEstablished(frame.FrameIndex);

            if (!configuration.TickMode.UsesLockedRate)
            {
                DispatchTick(
                    frame.FrameIndex,
                    TimeSpan.Zero,
                    dispatch);
            }
            return;
        }

        long counterDelta = frame.PerformanceCounter - _previousCounter;
        _previousCounter = frame.PerformanceCounter;
        if (counterDelta < 0)
        {
            counterDelta = 0;
            _lockedAccumulator = 0;
            log.Warning(
                "The host performance counter moved backwards. " +
                "The script runtime clock was resynchronized.");
        }

        if (!configuration.TickMode.UsesLockedRate)
        {
            DispatchTick(
                frame.FrameIndex,
                CounterToTimeSpan(counterDelta),
                dispatch);
            return;
        }

        DispatchLocked(frame.FrameIndex, counterDelta, dispatch);
    }

    private void DispatchLocked(
        ulong hostFrameIndex,
        long counterDelta,
        Action<ScriptTickContext> dispatch)
    {
        _lockedAccumulator += (Int128)counterDelta * _lockedTickRate;

        Int128 due = _lockedAccumulator / _frequency;
        if (due > MaximumCatchUpTicksPerHostFrame)
        {
            Int128 discarded = due - MaximumCatchUpTicksPerHostFrame;
            _lockedAccumulator -= discarded * _frequency;
            due = MaximumCatchUpTicksPerHostFrame;

            if (!_backlogWarningWritten)
            {
                _backlogWarningWritten = true;
                log.Warning(
                    "Locked tick processing exceeded the per-frame catch-up " +
                    "limit. Excess backlog was discarded.");
            }
        }

        TimeSpan delta = TimeSpan.FromSeconds(1.0 / _lockedTickRate);
        for (int index = 0; index < (int)due; ++index)
        {
            _lockedAccumulator -= _frequency;
            DispatchTick(hostFrameIndex, delta, dispatch);
        }
    }

    private void DispatchTick(
        ulong hostFrameIndex,
        TimeSpan delta,
        Action<ScriptTickContext> dispatch)
    {
        _elapsedTime += delta;
        dispatch(new(
            TickIndex: ++_tickIndex,
            HostFrameIndex: hostFrameIndex,
            DeltaTime: delta,
            ElapsedTime: _elapsedTime,
            TickMode: configuration.TickMode,
            LockedTickRate: configuration.TickMode.UsesLockedRate
                ? _lockedTickRate
                : null));
    }

    private TimeSpan CounterToTimeSpan(long counterDelta) =>
        TimeSpan.FromSeconds((double)counterDelta / _frequency);

    private void WriteClockEstablished(ulong hostFrameIndex)
    {
        string mode = !configuration.TickMode.UsesLockedRate
            ? "Synchronized"
            : $"Locked at {_lockedTickRate} ticks per second";
        log.Information(
            $"Script execution scheduler established at host frame " +
            $"{hostFrameIndex}. Mode: {mode}.");
    }
}