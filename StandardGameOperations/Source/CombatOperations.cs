using System.Numerics;
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
    bool TryResolveEngagement(
        Ped shooter,
        out CombatEngagement engagement);
    bool WasDamagedByWeapon(
        Entity victim,
        Ped shooter,
        uint weaponHash);
    bool WasDamagedByWeapon(
        CombatEngagement engagement,
        uint weaponHash);
    void ClearDamageEvidence(Entity entity);
    void ClearDamageEvidence(CombatEngagement engagement);
    bool IsImpactOnTargetPath(
        Ped shooter,
        Ped target,
        Vector3 impact,
        float maximumPerpendicularDistance,
        float minimumPathFraction,
        float maximumPathFraction);
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
            !_known.TryGetCombatTarget(shooter, out Entity entity))
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
            NativeHandles.ToNative(victim),
            NativeHandles.ToNativeEntity(shooter),
            true) &&
        StandardNatives.HAS_ENTITY_BEEN_DAMAGED_BY_WEAPON(
            NativeHandles.ToNative(victim),
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

        StandardNatives.CLEAR_ENTITY_LAST_DAMAGE_ENTITY(
            NativeHandles.ToNative(entity));
        StandardNatives.CLEAR_ENTITY_LAST_WEAPON_DAMAGE(
            NativeHandles.ToNative(entity));
    }

    public void ClearDamageEvidence(CombatEngagement engagement)
    {
        ClearDamageEvidence(_entities.AsEntity(engagement.Victim));
        if (engagement.HasVehicle)
        {
            ClearDamageEvidence(
                _entities.AsEntity(engagement.VictimVehicle));
        }
    }

    public bool IsImpactOnTargetPath(
        Ped shooter,
        Ped target,
        Vector3 impact,
        float maximumPerpendicularDistance,
        float minimumPathFraction,
        float maximumPathFraction)
    {
        if (!_peds.IsLivingHuman(shooter) ||
            !_peds.IsLivingHuman(target) ||
            !IsFinite(impact) ||
            !float.IsFinite(maximumPerpendicularDistance) ||
            maximumPerpendicularDistance < 0f ||
            !float.IsFinite(minimumPathFraction) ||
            !float.IsFinite(maximumPathFraction) ||
            minimumPathFraction > maximumPathFraction)
        {
            return false;
        }

        Vector3 start = _peds.GetChestPosition(shooter);
        Vector3 end = _peds.GetChestPosition(target);
        Vector3 path = end - start;
        float pathLengthSquared = path.LengthSquared();
        if (!float.IsFinite(pathLengthSquared) ||
            pathLengthSquared <= 0.0001f)
        {
            return false;
        }

        float fraction = Vector3.Dot(impact - start, path) /
            pathLengthSquared;
        if (!float.IsFinite(fraction) ||
            fraction < minimumPathFraction ||
            fraction > maximumPathFraction)
        {
            return false;
        }

        Vector3 closest = start + path * Math.Clamp(fraction, 0f, 1f);
        float distanceSquared = Vector3.DistanceSquared(impact, closest);
        float maximumDistanceSquared =
            maximumPerpendicularDistance * maximumPerpendicularDistance;

        return float.IsFinite(distanceSquared) &&
            float.IsFinite(maximumDistanceSquared) &&
            distanceSquared <= maximumDistanceSquared;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}