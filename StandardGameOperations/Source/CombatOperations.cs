using Alloc8orStandardNatives.Source;

namespace StandardGameOperations.Source;

public readonly record struct CombatEngagement(
    Ped Shooter,
    Ped Victim,
    Vehicle VictimVehicle)
{
    public bool HasVehicle => VictimVehicle.Value != 0;
}

public interface ICombatOperations
{
    bool TryGetTarget(Ped shooter, out Ped target);
    bool IsEngagedWith(Ped shooter, Ped target);
    bool IsFacingTarget(Ped shooter, Ped target, float angle);
    bool HasClearLineOfSight(Ped shooter, Ped target);
    bool TryResolveEngagement(Ped shooter, out CombatEngagement engagement);
    bool WasDamagedByWeapon(Entity victim, Ped shooter, uint weaponHash);
    bool WasDamagedByWeapon(
        CombatEngagement engagement,
        uint weaponHash);
    void ClearDamageEvidence(Entity entity);
    void ClearDamageEvidence(CombatEngagement engagement);
}

internal sealed class CombatOperations(
    IEntityOperations entities,
    IPedOperations peds,
    NativeBindings known) : ICombatOperations
{
    private readonly IEntityOperations _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));
    private readonly IPedOperations _peds =
        peds ?? throw new ArgumentNullException(nameof(peds));
    private readonly NativeBindings _known =
        known ?? throw new ArgumentNullException(nameof(known));

    public bool TryGetTarget(Ped shooter, out Ped target)
    {
        target = default;
        if (!_entities.IsValid(shooter) ||
            !NativeBindings.TryGetCombatTarget(shooter, out Entity entity))
        {
            return false;
        }

        Ped candidate = new(entity.Value);
        if (candidate.Value == shooter.Value ||
            !_peds.IsLivingHuman(candidate))
        {
            return false;
        }

        target = candidate;
        return true;
    }

    public bool IsEngagedWith(Ped shooter, Ped target) =>
        _peds.IsLivingHuman(shooter) &&
        _peds.IsLivingHuman(target) &&
        StandardNatives.IS_PED_IN_COMBAT(
            shooter.ToNative(),
            target.ToNative());

    public bool IsFacingTarget(
        Ped shooter,
        Ped target,
        float angle)
    {
        if (!_peds.IsLivingHuman(shooter) ||
            !_peds.IsLivingHuman(target) ||
            !float.IsFinite(angle) ||
            angle <= 0f)
        {
            return false;
        }

        return StandardNatives.IS_PED_FACING_PED(
            shooter.ToNative(),
            target.ToNative(),
            angle);
    }

    public bool HasClearLineOfSight(Ped shooter, Ped target) =>
        _peds.IsLivingHuman(shooter) &&
        _peds.IsLivingHuman(target) &&
        StandardNatives.HAS_ENTITY_CLEAR_LOS_TO_ENTITY(
            shooter.ToNativeEntity(),
            target.ToNativeEntity(),
            17);

    public bool TryResolveEngagement(
        Ped shooter,
        out CombatEngagement engagement)
    {
        engagement = default;
        if (!TryGetTarget(shooter, out Ped victim))
        {
            return false;
        }

        Vehicle vehicle = _peds.GetCurrentVehicle(victim);
        if (!_entities.IsValid(vehicle))
        {
            vehicle = default;
        }

        engagement = new(shooter, victim, vehicle);
        return true;
    }

    public bool WasDamagedByWeapon(
        Entity victim,
        Ped shooter,
        uint weaponHash) =>
        weaponHash != 0 &&
        _entities.IsValid(victim) &&
        _entities.IsValid(shooter) &&
        StandardNatives.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY(
            victim.ToNative(),
            shooter.ToNativeEntity(),
            true) &&
        StandardNatives.HAS_ENTITY_BEEN_DAMAGED_BY_WEAPON(
            victim.ToNative(),
            weaponHash,
            0);

    public bool WasDamagedByWeapon(
        CombatEngagement engagement,
        uint weaponHash)
    {
        if (WasDamagedByWeapon(
                _entities.AsEntity(engagement.Victim),
                engagement.Shooter,
                weaponHash))
        {
            return true;
        }

        return engagement.HasVehicle &&
            WasDamagedByWeapon(
                _entities.AsEntity(engagement.VictimVehicle),
                engagement.Shooter,
                weaponHash);
    }

    public void ClearDamageEvidence(Entity entity)
    {
        if (!_entities.IsValid(entity))
        {
            return;
        }

        StandardNatives.CLEAR_ENTITY_LAST_DAMAGE_ENTITY(entity.ToNative());
        StandardNatives.CLEAR_ENTITY_LAST_WEAPON_DAMAGE(entity.ToNative());
    }

    public void ClearDamageEvidence(CombatEngagement engagement)
    {
        ClearDamageEvidence(_entities.AsEntity(engagement.Victim));
        if (engagement.HasVehicle)
        {
            ClearDamageEvidence(_entities.AsEntity(engagement.VictimVehicle));
        }
    }
}