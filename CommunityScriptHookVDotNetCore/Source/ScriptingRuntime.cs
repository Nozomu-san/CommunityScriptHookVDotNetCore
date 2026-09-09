using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace CommunityScriptHookVDotNetCore.Source;

public enum ScriptStartReason
{
    InitialActivation,
    LifecycleRestart,
    BinaryReplacement
}

public enum ScriptStopReason
{
    RuntimeShutdown,
    PackageFault,
    LifecycleRestart,
    BinaryReplacement,
    DependencyUnavailable
}

public interface IScriptEnvironment
{
    string ScriptsDirectory { get; }
}

public interface IScriptServices
{
    bool TryGet<TService>(
        [NotNullWhen(true)] out TService? service)
        where TService : class;

    TService GetRequired<TService>()
        where TService : class;
}

public readonly record struct ScriptStartContext(
    string PackageName,
    IScriptServices Services,
    ScriptStartReason Reason,
    ulong LifecycleEpoch,
    CancellationToken LifetimeToken,
    CancellationToken CancellationToken);

public readonly record struct ScriptTickContext(
    ulong TickIndex,
    ulong HostFrameIndex,
    TimeSpan DeltaTime,
    TimeSpan ElapsedTime);

public readonly record struct ScriptStopContext(
    string PackageName,
    ScriptStopReason Reason,
    ulong LifecycleEpoch);

public abstract class Script4
{
    protected abstract Task OnStartAsync(ScriptStartContext context);

    protected virtual void OnTick(ScriptTickContext context)
    {
    }

    protected virtual Task OnStopAsync(ScriptStopContext context) =>
        Task.CompletedTask;

    internal Task StartAsync(ScriptStartContext context) =>
        OnStartAsync(context);

    internal void Tick(ScriptTickContext context) =>
        OnTick(context);

    internal Task StopAsync(ScriptStopContext context) =>
        OnStopAsync(context);
}

internal sealed class TickScheduler(RuntimeLog log)
{
    internal const int TickRate = 64;
    internal static readonly TimeSpan TickPeriod =
        TimeSpan.FromSeconds(1d / TickRate);

    private readonly double _periodCounter =
        Stopwatch.Frequency / (double)TickRate;
    private bool _started;
    private double _nextCounter;
    private ulong _latestHostFrameIndex;
    private ulong _tickIndex;
    private TimeSpan _elapsedTime;

    internal bool IsStarted => _started;

    internal void ObserveHostFrame(ulong hostFrameIndex) =>
        _latestHostFrameIndex = hostFrameIndex;

    internal void Start(ulong hostFrameIndex)
    {
        if (_started)
        {
            ObserveHostFrame(hostFrameIndex);
            return;
        }

        _latestHostFrameIndex = hostFrameIndex;
        _nextCounter = Stopwatch.GetTimestamp();
        _started = true;
        log.Information(
            $"Script4 lifecycle clock established at {TickRate} ticks per second. " +
            "Missed periods are coalesced and are never replayed.");
    }

    internal int GetWaitTimeoutMilliseconds()
    {
        if (!_started)
        {
            return Timeout.Infinite;
        }

        double remaining = _nextCounter - Stopwatch.GetTimestamp();
        if (remaining <= 0d)
        {
            return 0;
        }

        double milliseconds = remaining * 1000d / Stopwatch.Frequency;
        return (int)Math.Clamp(Math.Ceiling(milliseconds), 1d, int.MaxValue);
    }

    internal bool DispatchIfDue(Action<ScriptTickContext> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (!_started)
        {
            return false;
        }

        long timestamp = Stopwatch.GetTimestamp();
        if (timestamp < _nextCounter)
        {
            return false;
        }

        double elapsedPeriods =
            Math.Floor((timestamp - _nextCounter) / _periodCounter) + 1d;
        _nextCounter += elapsedPeriods * _periodCounter;
        _elapsedTime += TickPeriod;
        dispatch(new(
            TickIndex: ++_tickIndex,
            HostFrameIndex: _latestHostFrameIndex,
            DeltaTime: TickPeriod,
            ElapsedTime: _elapsedTime));
        return true;
    }
}