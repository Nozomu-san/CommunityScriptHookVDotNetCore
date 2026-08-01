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
    BinaryReplacement,
    Recovery
}

public enum ScriptStopReason
{
    RuntimeShutdown,
    PackageFault,
    LifecycleRestart,
    BinaryReplacement
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
    CancellationToken LifetimeToken);

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
    ulong LifecycleEpoch);

public abstract class Script4
{
    protected abstract void OnStart(ScriptStartContext context);

    protected virtual void OnTick(ScriptTickContext context)
    {
    }

    protected virtual void OnStop(ScriptStopContext context)
    {
    }

    internal void Start(ScriptStartContext context) =>
        OnStart(context);

    internal void Tick(ScriptTickContext context) =>
        OnTick(context);

    internal void Stop(ScriptStopContext context) =>
        OnStop(context);
}