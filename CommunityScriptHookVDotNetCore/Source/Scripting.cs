using System.Diagnostics.CodeAnalysis;

namespace CommunityScriptHookVDotNetCore.Source;

public enum ScriptTickMode
{
    Synchronized,
    Locked
}

internal static class ScriptTickModeExtensions
{
    extension(ScriptTickMode mode)
    {
        public bool UsesLockedRate => mode is ScriptTickMode.Locked;
    }
}

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
    TimeSpan ElapsedTime,
    ScriptTickMode TickMode,
    int? LockedTickRate);

public readonly record struct ScriptStopContext(
    string PackageName,
    ScriptStopReason Reason,
    ulong LifecycleEpoch,
    CancellationToken CancellationToken);

public abstract class Script4
{
    protected abstract ValueTask OnStartAsync(ScriptStartContext context);

    protected virtual void OnTick(ScriptTickContext context)
    {
    }

    protected virtual ValueTask OnStopAsync(ScriptStopContext context) =>
        ValueTask.CompletedTask;

    internal ValueTask StartAsync(ScriptStartContext context) =>
        OnStartAsync(context);

    internal void Tick(ScriptTickContext context) =>
        OnTick(context);

    internal ValueTask StopAsync(ScriptStopContext context) =>
        OnStopAsync(context);
}