using System.Numerics;

namespace LowLevelEvents.Source;

public enum LowLevelEventBridgeStatus : uint
{
    Uninitialized = 0,
    Initializing = 1,
    Ready = 2,
    UnsupportedGameBuild = 3,
    HookNotFound = 4,
    HookInstallationFailed = 5,
    Stopping = 6,
    Stopped = 7
}

public enum LowLevelEventStream : uint
{
    Reaction = 0,
    Group = 1,
    Global = 2,
    Damage = 3
}

public enum DamageMagnitudeSource : uint
{
    Unavailable = 0,
    DamageProcess = 1
}

public readonly record struct RawLowLevelEvent(
    ulong Sequence,
    long PerformanceCounter,
    uint GameBuild,
    uint EventId,
    string? EventName,
    LowLevelEventStream Stream,
    ulong GroupAddress,
    ulong EventAddress,
    ulong RelatedEntityAddress,
    int RelatedEntityHandle,
    ulong DispatchEntityAddress,
    int DispatchEntityHandle,
    uint DispatchEntityCount,
    ReadOnlyMemory<ulong> Arguments,
    ReadOnlyMemory<byte> ProbeData);

public readonly record struct GunAimedAtEvent(
    ulong Sequence,
    long PerformanceCounter,
    ulong ShooterAddress,
    int ShooterHandle,
    ulong VictimAddress,
    int VictimHandle);

public readonly record struct GunShotEvent(
    ulong Sequence,
    long PerformanceCounter,
    ulong ShooterAddress,
    int ShooterHandle,
    uint WeaponHash,
    Vector3 SourcePosition);

public readonly record struct BulletImpactEvent(
    ulong Sequence,
    long PerformanceCounter,
    ulong ShooterAddress,
    int ShooterHandle,
    uint WeaponHash,
    Vector3 SourcePosition,
    Vector3 ImpactPosition);

public readonly record struct EntityDamageEvent(
    ulong Sequence,
    long PerformanceCounter,
    ulong VictimAddress,
    int VictimHandle,
    ulong CulpritAddress,
    int CulpritHandle,
    uint WeaponHash,
    uint GameTime,
    bool State,
    float BaseDamage,
    DamageMagnitudeSource MagnitudeSource);

public readonly record struct MeleeActionEvent(
    ulong Sequence,
    long PerformanceCounter,
    ulong RelatedEntityAddress,
    int RelatedEntityHandle,
    LowLevelEventStream Stream,
    ReadOnlyMemory<ulong> Arguments,
    ReadOnlyMemory<byte> ProbeData);

public readonly record struct LowLevelEventDiagnostics(
    ulong ContinuityRevision,
    ulong ContinuityLossCount,
    ulong NativeDropped,
    ulong NativePending,
    ulong ManagedDropped,
    ulong SubscriberDropped,
    ulong DamageRaw,
    ulong DamagePublished,
    ulong DamageDropped,
    ulong DamageIdentityQueued,
    ulong DamageVictimIdentityResolved,
    ulong DamageCulpritIdentityResolved,
    ulong DamageVictimIdentityExpired,
    ulong DamageCulpritIdentityExpired,
    int DamageIdentityPending,
    ulong DamageMagnitudeAvailable,
    ulong DamageMagnitudeUnavailable,
    ulong GunAimedAtRaw,
    ulong GunAimedAtReaction,
    ulong GunAimedAtGroup,
    ulong GunAimedAtGlobal,
    ulong GunAimedAtPublished,
    ulong GunAimedAtMissingSource,
    ulong GunAimedAtMissingTarget,
    ulong MeleeActionPublished,
    ulong MeleeActionDropped,
    ulong GunShotPublished,
    ulong GunShotDropped,
    ulong BulletImpactPublished,
    ulong BulletImpactDropped,
    ulong WeaponMalformed,
    ulong WeaponMissingShooter,
    ulong WeaponIdentityQueued,
    ulong WeaponIdentityResolved,
    ulong WeaponIdentityExpired,
    int WeaponIdentityPending,
    ulong GunShotReaction,
    ulong GunShotGroup,
    ulong GunShotGlobal,
    ulong BulletImpactReaction,
    ulong BulletImpactGroup,
    ulong BulletImpactGlobal,
    int Subscriptions,
    ulong DamageObserved,
    ulong MeleeActionObserved,
    ulong ShockingGunshotObserved,
    ulong VisibleWeaponObserved,
    ulong GunShotObserved,
    ulong BulletImpactObserved,
    ulong WhizzedByObserved,
    ulong EntityDamagedObserved);