using System.Numerics;
using Alloc8orStandardNatives.Source;
using LocalNativeMemories.Source;

namespace StandardGameOperations.Source;

public readonly record struct PedVitals(
    int Armour,
    int Health);

public readonly record struct PedInvincibilityState(
    bool WasRead,
    bool StandardInvincibility,
    bool ReactiveInvincibility,
    bool CanBeDamaged)
{
    public bool IsInvincible =>
        StandardInvincibility || ReactiveInvincibility;

    public bool BlocksSyntheticDamage =>
        !WasRead ||
        !CanBeDamaged ||
        StandardInvincibility ||
        ReactiveInvincibility;
}

public interface IPedOperations
{
    Ped GetPlayerPed();
    Ped[] GetNearbyPeds(Ped origin, int maxAmount = 16);
    bool IsHuman(Ped ped);
    bool IsLivingHuman(Ped ped);
    bool IsDead(Ped ped);
    bool IsShooting(Ped ped);
    bool IsReloading(Ped ped);
    bool IsInCover(Ped ped);
    PedVitals ReadVitals(Ped ped);
    Vector3 GetChestPosition(Ped ped);
    Vehicle GetCurrentVehicle(Ped ped);
    bool CanRagdoll(Ped ped);
    PedInvincibilityState ReadInvincibility(Ped ped);
    bool CanReceiveSyntheticDamage(Ped ped);
    bool IsOnFire(Ped ped);
}

internal sealed class PedOperations(
    IEntityOperations entities,
    IPedInvincibilityMemory invincibility,
    NativeBindings known) : IPedOperations
{
    private readonly IEntityOperations _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));

    private readonly IPedInvincibilityMemory _invincibility =
        invincibility ?? throw new ArgumentNullException(nameof(invincibility));

    private readonly NativeBindings _known =
        known ?? throw new ArgumentNullException(nameof(known));

    public Ped GetPlayerPed() =>
        NativeHandles.FromNative(StandardNatives.PLAYER_PED_ID());

    public Ped[] GetNearbyPeds(Ped origin, int maxAmount = 16) =>
        !_entities.IsValid(origin)
            ? []
            : _known.GetNearbyPeds(origin, maxAmount);

    public bool IsHuman(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_HUMAN(NativeHandles.ToNative(ped));

    public bool IsLivingHuman(Ped ped)
    {
        if (!_entities.IsValid(ped))
        {
            return false;
        }

        return StandardNatives.IS_PED_HUMAN(NativeHandles.ToNative(ped)) &&
            !StandardNatives.IS_ENTITY_DEAD(
                NativeHandles.ToNativeEntity(ped),
                false);
    }

    public bool IsDead(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_ENTITY_DEAD(
            NativeHandles.ToNativeEntity(ped),
            false);

    public bool IsShooting(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_SHOOTING(NativeHandles.ToNative(ped));

    public bool IsReloading(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_RELOADING(NativeHandles.ToNative(ped));

    public bool IsInCover(Ped ped) =>
        IsLivingHuman(ped) &&
        StandardNatives.IS_PED_IN_COVER(NativeHandles.ToNative(ped), false);

    public PedVitals ReadVitals(Ped ped) =>
        !_entities.IsValid(ped)
            ? default
            : new(
                Math.Max(
                    0,
                    StandardNatives.GET_PED_ARMOUR(
                        NativeHandles.ToNative(ped))),
                Math.Max(
                    0,
                    StandardNatives.GET_ENTITY_HEALTH(
                        NativeHandles.ToNativeEntity(ped))));

    public Vector3 GetChestPosition(Ped ped) =>
        !_entities.IsValid(ped)
            ? default
            : StandardNatives.GET_PED_BONE_COORDS(
                NativeHandles.ToNative(ped),
                24818,
                0f,
                0f,
                0f);

    public Vehicle GetCurrentVehicle(Ped ped) =>
        !_entities.IsValid(ped)
            ? default
            : NativeHandles.FromNative(
                StandardNatives.GET_VEHICLE_PED_IS_IN(
                    NativeHandles.ToNative(ped),
                    false));

    public bool CanRagdoll(Ped ped) =>
        IsLivingHuman(ped) &&
        StandardNatives.CAN_PED_RAGDOLL(NativeHandles.ToNative(ped));

    public PedInvincibilityState ReadInvincibility(Ped ped)
    {
        if (!_entities.IsValid(ped))
        {
            return default;
        }

        bool canBeDamaged = StandardNatives.GET_ENTITY_CAN_BE_DAMAGED(
            NativeHandles.ToNativeEntity(ped));

        LocalDataResult<LocalNativeMemories.Source.PedInvincibilityState>
            memoryState = _invincibility.Read(new PedHandle(ped.Value));

        if (!memoryState.IsSuccess)
        {
            return new(
                WasRead: false,
                StandardInvincibility: false,
                ReactiveInvincibility: false,
                CanBeDamaged: canBeDamaged);
        }

        return new(
            WasRead: true,
            StandardInvincibility: memoryState.Value.Standard,
            ReactiveInvincibility: memoryState.Value.Reactive,
            CanBeDamaged: canBeDamaged);
    }

    public bool CanReceiveSyntheticDamage(Ped ped) =>
        !ReadInvincibility(ped).BlocksSyntheticDamage;

    public bool IsOnFire(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_ENTITY_ON_FIRE(
            NativeHandles.ToNativeEntity(ped));
}