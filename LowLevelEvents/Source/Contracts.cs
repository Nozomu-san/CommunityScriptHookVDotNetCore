using System.Diagnostics.CodeAnalysis;

namespace LowLevelEvents.Source;

public interface ILowLevelSubscription<T> : IDisposable
{
    ulong DroppedCount { get; }
    bool TryRead([MaybeNullWhen(false)] out T value);
}

public interface ILowLevelEventContinuity
{
    ulong ContinuityRevision { get; }
    ulong ContinuityLossCount { get; }
}

public interface ILowLevelEventStream : ILowLevelEventContinuity
{
    uint GameBuild { get; }
    LowLevelEventBridgeStatus BridgeStatus { get; }
    ulong NativeDroppedCount { get; }
    ulong ManagedDroppedCount { get; }
    LowLevelEventDiagnostics Diagnostics { get; }
    ILowLevelSubscription<RawLowLevelEvent> SubscribeRaw(
        IEnumerable<uint> eventIds,
        int capacity = 256);
}

public interface ILowLevelDamageStream : ILowLevelEventContinuity
{
    ILowLevelSubscription<EntityDamageEvent> SubscribeDamage(
        int capacity = 256);
}

public interface ILowLevelWeaponEventStream : ILowLevelEventContinuity
{
    ILowLevelSubscription<GunAimedAtEvent> SubscribeGunAimedAt(
        int capacity = 256);

    ILowLevelSubscription<GunShotEvent> SubscribeGunShots(
        int capacity = 256);

    ILowLevelSubscription<BulletImpactEvent> SubscribeBulletImpacts(
        int capacity = 256);
}

public interface ILowLevelCombatEventStream : ILowLevelEventContinuity
{
    ILowLevelSubscription<MeleeActionEvent> SubscribeMeleeActions(
        int capacity = 256);
}