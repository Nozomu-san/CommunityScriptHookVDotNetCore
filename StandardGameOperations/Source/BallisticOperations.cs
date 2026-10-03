using System.Numerics;
using Alloc8orStandardNatives.Source;

namespace StandardGameOperations.Source;

public readonly record struct ImpactPathMeasurement(
    float PathFraction,
    float PerpendicularDistance)
{
    public bool IsFinite =>
        float.IsFinite(PathFraction) &&
        float.IsFinite(PerpendicularDistance);
}

public readonly record struct PedBoneFrameMeasurement(
    int BoneIndex,
    Vector3 Origin,
    Vector3 IndexedOrigin,
    Vector3 LocalX,
    Vector3 LocalY,
    Vector3 LocalZ)
{
    public bool IsFinite =>
        BoneIndex >= 0 &&
        IsFiniteVector(Origin) &&
        IsFiniteVector(IndexedOrigin) &&
        IsFiniteVector(LocalX) &&
        IsFiniteVector(LocalY) &&
        IsFiniteVector(LocalZ);

    private static bool IsFiniteVector(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public enum GunTaskRetargetStrategy
{
    PedRelativeCorrection,
    MountedPedHeadAnchor,
    MountedVehicleHeadAnchor
}

public readonly record struct GunTaskRetargetMeasurement(
    GunTaskRetargetStrategy Strategy,
    Vehicle TargetVehicle,
    Vector3 ReferenceOffset,
    Vector3 DesiredOffset,
    Vector3 TargetOffset,
    Vector3 TargetWorldPosition)
{
    public bool IsFinite =>
        IsFiniteVector(ReferenceOffset) &&
        IsFiniteVector(DesiredOffset) &&
        IsFiniteVector(TargetOffset) &&
        IsFiniteVector(TargetWorldPosition);

    private static bool IsFiniteVector(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public interface IBallisticOperations
{
    bool TryGetPedBonePosition(
        Ped target,
        ushort boneTag,
        out Vector3 position);

    bool TryGetPedBoneFrame(
        Ped target,
        ushort boneTag,
        out PedBoneFrameMeasurement measurement);

    bool TryGetPedLocalPosition(
        Ped target,
        Vector3 worldPosition,
        out Vector3 localPosition);

    bool TryGetVehicleLocalPosition(
        Vehicle target,
        Vector3 worldPosition,
        out Vector3 localPosition);

    bool TryRedirectPedShotsToPosition(
        Ped shooter,
        Vector3 position);

    bool TryShootSyntheticBulletThroughPosition(
        Ped shooter,
        Vector3 position,
        uint weaponHash,
        int damage);

    bool TryRetargetActiveGunTask(
        Ped shooter,
        Ped target,
        Vector3 referenceWorldPosition,
        Vector3 desiredWorldPosition,
        out GunTaskRetargetMeasurement measurement);

    bool TryRedirectPedShotsToBone(
        Ped shooter,
        Ped target,
        ushort boneTag);

    bool TryMeasureImpactPath(
        Ped shooter,
        Ped target,
        Vector3 impact,
        out ImpactPathMeasurement measurement);

    bool TryMeasurePointToSegment(
        Vector3 start,
        Vector3 end,
        Vector3 point,
        out ImpactPathMeasurement measurement);

    float LinearPathAttenuation(
        Vector3 start,
        Vector3 end,
        Vector3 point,
        float maximumPerpendicularDistance);

    bool IsWithinRadius(Vector3 first, Vector3 second, float radius);

    bool IsImpactOnTargetPath(
        Ped shooter,
        Ped target,
        Vector3 impact,
        float maximumPerpendicularDistance,
        float minimumPathFraction,
        float maximumPathFraction);

    bool VehicleReceivedRound(
        Vehicle vehicle,
        Ped shooter,
        uint weaponHash);
}

internal sealed class BallisticOperations(
    IEntityOperations entities,
    IPedOperations peds) : IBallisticOperations
{
    private const int VehicleMountedWeaponTaskType = 199;
    private const int MountedWeaponTaskMode = 2;

    private readonly IEntityOperations _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));
    private readonly IPedOperations _peds =
        peds ?? throw new ArgumentNullException(nameof(peds));

    public bool TryGetPedBonePosition(
        Ped target,
        ushort boneTag,
        out Vector3 position)
    {
        position = default;
        if (!_peds.IsLivingHuman(target))
        {
            return false;
        }

        position = StandardNatives.GET_PED_BONE_COORDS(
            target.ToNative(),
            boneTag,
            0f,
            0f,
            0f);
        return IsFinite(position);
    }

    public bool TryGetPedBoneFrame(
        Ped target,
        ushort boneTag,
        out PedBoneFrameMeasurement measurement)
    {
        measurement = default;
        if (!_peds.IsLivingHuman(target))
        {
            return false;
        }

        var nativePed = target.ToNative();
        Vector3 origin = StandardNatives.GET_PED_BONE_COORDS(
            nativePed,
            boneTag,
            0f,
            0f,
            0f);
        Vector3 xPoint = StandardNatives.GET_PED_BONE_COORDS(
            nativePed,
            boneTag,
            1f,
            0f,
            0f);
        Vector3 yPoint = StandardNatives.GET_PED_BONE_COORDS(
            nativePed,
            boneTag,
            0f,
            1f,
            0f);
        Vector3 zPoint = StandardNatives.GET_PED_BONE_COORDS(
            nativePed,
            boneTag,
            0f,
            0f,
            1f);

        if (!IsFinite(origin) ||
            !TryNormalizeAxis(xPoint - origin, out Vector3 localX) ||
            !TryNormalizeAxis(yPoint - origin, out Vector3 localY) ||
            !TryNormalizeAxis(zPoint - origin, out Vector3 localZ))
        {
            return false;
        }

        int boneIndex = StandardNatives.GET_PED_BONE_INDEX(
            nativePed,
            boneTag);
        if (boneIndex < 0)
        {
            return false;
        }

        Vector3 indexedOrigin =
            StandardNatives.GET_WORLD_POSITION_OF_ENTITY_BONE(
                target.ToNativeEntity(),
                boneIndex);

        measurement = new(
            boneIndex,
            origin,
            indexedOrigin,
            localX,
            localY,
            localZ);
        return measurement.IsFinite;
    }

    public bool TryGetPedLocalPosition(
        Ped target,
        Vector3 worldPosition,
        out Vector3 localPosition)
    {
        localPosition = default;
        if (!_peds.IsLivingHuman(target) || !IsFinite(worldPosition))
        {
            return false;
        }

        localPosition =
            StandardNatives.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS(
                target.ToNativeEntity(),
                worldPosition.X,
                worldPosition.Y,
                worldPosition.Z);
        return IsFinite(localPosition);
    }

    public bool TryGetVehicleLocalPosition(
        Vehicle target,
        Vector3 worldPosition,
        out Vector3 localPosition)
    {
        localPosition = default;
        if (!_entities.IsValid(target) || !IsFinite(worldPosition))
        {
            return false;
        }

        localPosition =
            StandardNatives.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS(
                target.ToNativeEntity(),
                worldPosition.X,
                worldPosition.Y,
                worldPosition.Z);
        return IsFinite(localPosition);
    }

    public bool TryRedirectPedShotsToPosition(
        Ped shooter,
        Vector3 position)
    {
        if (!_peds.IsLivingHuman(shooter) || !IsFinite(position))
        {
            return false;
        }

        StandardNatives.SET_PED_SHOOTS_AT_COORD(
            shooter.ToNative(),
            position.X,
            position.Y,
            position.Z,
            false);
        return true;
    }

    public bool TryShootSyntheticBulletThroughPosition(
        Ped shooter,
        Vector3 position,
        uint weaponHash,
        int damage)
    {
        if (!_peds.IsLivingHuman(shooter) ||
            !IsFinite(position) ||
            weaponHash == 0 ||
            damage <= 0)
        {
            return false;
        }

        var weaponEntity =
            StandardNatives.GET_CURRENT_PED_WEAPON_ENTITY_INDEX(
                shooter.ToNative(),
                default);

        Vector3 origin =
            weaponEntity.Value != 0 &&
            StandardNatives.DOES_ENTITY_EXIST(weaponEntity)
                ? StandardNatives.GET_ENTITY_COORDS(weaponEntity, true)
                : _peds.GetChestPosition(shooter);

        if (!IsFinite(origin))
        {
            return false;
        }

        Vector3 path = position - origin;
        float lengthSquared = path.LengthSquared();
        if (!float.IsFinite(lengthSquared) ||
            lengthSquared <= 0.0001f)
        {
            return false;
        }

        Vector3 direction = Vector3.Normalize(path);
        Vector3 entry = position - direction * 0.35f;
        Vector3 exit = position + direction * 0.35f;
        if (!IsFinite(entry) || !IsFinite(exit))
        {
            return false;
        }

        StandardNatives.SHOOT_SINGLE_BULLET_BETWEEN_COORDS(
            entry.X,
            entry.Y,
            entry.Z,
            exit.X,
            exit.Y,
            exit.Z,
            damage,
            true,
            weaponHash,
            shooter.ToNative(),
            true,
            false,
            -1.0f);
        return true;
    }

    public bool TryRetargetActiveGunTask(
        Ped shooter,
        Ped target,
        Vector3 referenceWorldPosition,
        Vector3 desiredWorldPosition,
        out GunTaskRetargetMeasurement measurement)
    {
        measurement = default;
        if (!_peds.IsLivingHuman(shooter) ||
            !_peds.IsLivingHuman(target) ||
            !IsFinite(referenceWorldPosition) ||
            !IsFinite(desiredWorldPosition))
        {
            return false;
        }

        Vehicle targetVehicle = _peds.GetCurrentVehicle(target);
        if (!_entities.IsValid(targetVehicle))
        {
            targetVehicle = default;
        }

        bool mountedWeapon =
            _peds.IsTaskActive(shooter, VehicleMountedWeaponTaskType);

        if (mountedWeapon)
        {
            if (targetVehicle.Value != 0)
            {
                var anchorEntity = targetVehicle.ToNativeEntity();
                Vector3 referenceOffset =
                    StandardNatives.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS(
                        anchorEntity,
                        referenceWorldPosition.X,
                        referenceWorldPosition.Y,
                        referenceWorldPosition.Z);
                Vector3 desiredOffset =
                    StandardNatives.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS(
                        anchorEntity,
                        desiredWorldPosition.X,
                        desiredWorldPosition.Y,
                        desiredWorldPosition.Z);

                measurement = new(
                    GunTaskRetargetStrategy.MountedVehicleHeadAnchor,
                    targetVehicle,
                    referenceOffset,
                    desiredOffset,
                    desiredOffset,
                    desiredWorldPosition);
                if (!measurement.IsFinite)
                {
                    return false;
                }

                StandardNatives.SET_MOUNTED_WEAPON_TARGET(
                    shooter.ToNative(),
                    default,
                    targetVehicle.ToNative(),
                    desiredOffset.X,
                    desiredOffset.Y,
                    desiredOffset.Z,
                    MountedWeaponTaskMode,
                    false);
                return true;
            }

            var targetEntity = target.ToNativeEntity();
            Vector3 referencePedOffset =
                StandardNatives.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS(
                    targetEntity,
                    referenceWorldPosition.X,
                    referenceWorldPosition.Y,
                    referenceWorldPosition.Z);
            Vector3 desiredPedOffset =
                StandardNatives.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS(
                    targetEntity,
                    desiredWorldPosition.X,
                    desiredWorldPosition.Y,
                    desiredWorldPosition.Z);

            measurement = new(
                GunTaskRetargetStrategy.MountedPedHeadAnchor,
                default,
                referencePedOffset,
                desiredPedOffset,
                desiredPedOffset,
                desiredWorldPosition);
            if (!measurement.IsFinite)
            {
                return false;
            }

            StandardNatives.SET_MOUNTED_WEAPON_TARGET(
                shooter.ToNative(),
                target.ToNative(),
                default,
                desiredPedOffset.X,
                desiredPedOffset.Y,
                desiredPedOffset.Z,
                MountedWeaponTaskMode,
                false);
            return true;
        }

        var targetPedEntity = target.ToNativeEntity();
        Vector3 referencePedRelativeOffset =
            StandardNatives.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS(
                targetPedEntity,
                referenceWorldPosition.X,
                referenceWorldPosition.Y,
                referenceWorldPosition.Z);
        Vector3 desiredPedRelativeOffset =
            StandardNatives.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS(
                targetPedEntity,
                desiredWorldPosition.X,
                desiredWorldPosition.Y,
                desiredWorldPosition.Z);
        Vector3 correctionOffset =
            desiredPedRelativeOffset - referencePedRelativeOffset;

        measurement = new(
            GunTaskRetargetStrategy.PedRelativeCorrection,
            targetVehicle,
            referencePedRelativeOffset,
            desiredPedRelativeOffset,
            correctionOffset,
            desiredWorldPosition);
        if (!measurement.IsFinite)
        {
            return false;
        }

        StandardNatives.SET_DRIVEBY_TASK_TARGET(
            shooter.ToNative(),
            target.ToNative(),
            default,
            correctionOffset.X,
            correctionOffset.Y,
            correctionOffset.Z);
        return true;
    }

    public bool TryRedirectPedShotsToBone(
        Ped shooter,
        Ped target,
        ushort boneTag)
    {
        return TryGetPedBonePosition(target, boneTag, out Vector3 position) &&
            TryRedirectPedShotsToPosition(shooter, position);
    }

    public bool VehicleReceivedRound(
        Vehicle vehicle,
        Ped shooter,
        uint weaponHash) =>
        weaponHash != 0 &&
        _entities.IsValid(vehicle) &&
        _entities.IsValid(shooter) &&
        StandardNatives.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY(
            vehicle.ToNativeEntity(),
            shooter.ToNativeEntity(),
            true) &&
        StandardNatives.HAS_ENTITY_BEEN_DAMAGED_BY_WEAPON(
            vehicle.ToNativeEntity(),
            weaponHash,
            0);

    public bool TryMeasureImpactPath(
        Ped shooter,
        Ped target,
        Vector3 impact,
        out ImpactPathMeasurement measurement)
    {
        measurement = default;
        if (!_peds.IsLivingHuman(shooter) ||
            !_peds.IsLivingHuman(target) ||
            !IsFinite(impact))
        {
            return false;
        }

        return TryMeasurePointToSegment(
            _peds.GetChestPosition(shooter),
            _peds.GetChestPosition(target),
            impact,
            out measurement);
    }

    public bool TryMeasurePointToSegment(
        Vector3 start,
        Vector3 end,
        Vector3 point,
        out ImpactPathMeasurement measurement)
    {
        measurement = default;
        if (!IsFinite(start) || !IsFinite(end) || !IsFinite(point))
        {
            return false;
        }

        Vector3 path = end - start;
        float lengthSquared = path.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 0.0001f)
        {
            return false;
        }

        float rawFraction = Vector3.Dot(point - start, path) / lengthSquared;
        if (!float.IsFinite(rawFraction))
        {
            return false;
        }

        float fraction = Math.Clamp(rawFraction, 0f, 1f);
        Vector3 closest = start + path * fraction;
        float distanceSquared = Vector3.DistanceSquared(point, closest);
        if (!float.IsFinite(distanceSquared) || distanceSquared < 0f)
        {
            return false;
        }

        measurement = new(fraction, MathF.Sqrt(distanceSquared));
        return measurement.IsFinite;
    }

    public float LinearPathAttenuation(
        Vector3 start,
        Vector3 end,
        Vector3 point,
        float maximumPerpendicularDistance)
    {
        if (!float.IsFinite(maximumPerpendicularDistance) ||
            maximumPerpendicularDistance <= 0f ||
            !TryMeasurePointToSegment(start, end, point, out ImpactPathMeasurement measurement))
        {
            return 0f;
        }

        return Math.Clamp(
            1f - measurement.PerpendicularDistance / maximumPerpendicularDistance,
            0f,
            1f);
    }

    public bool IsWithinRadius(Vector3 first, Vector3 second, float radius)
    {
        if (!IsFinite(first) || !IsFinite(second) ||
            !float.IsFinite(radius) || radius < 0f)
        {
            return false;
        }

        float distanceSquared = Vector3.DistanceSquared(first, second);
        return float.IsFinite(distanceSquared) &&
            distanceSquared <= radius * radius;
    }

    public bool IsImpactOnTargetPath(
        Ped shooter,
        Ped target,
        Vector3 impact,
        float maximumPerpendicularDistance,
        float minimumPathFraction,
        float maximumPathFraction)
    {
        if (!float.IsFinite(maximumPerpendicularDistance) ||
            maximumPerpendicularDistance < 0f ||
            !float.IsFinite(minimumPathFraction) ||
            !float.IsFinite(maximumPathFraction) ||
            minimumPathFraction > maximumPathFraction ||
            !TryMeasureImpactPath(
                shooter,
                target,
                impact,
                out ImpactPathMeasurement measurement))
        {
            return false;
        }

        return measurement.PathFraction >= minimumPathFraction &&
            measurement.PathFraction <= maximumPathFraction &&
            measurement.PerpendicularDistance <= maximumPerpendicularDistance;
    }

    private static bool TryNormalizeAxis(
        Vector3 value,
        out Vector3 normalized)
    {
        normalized = default;
        float lengthSquared = value.LengthSquared();
        if (!IsFinite(value) ||
            !float.IsFinite(lengthSquared) ||
            lengthSquared <= 0.000001f)
        {
            return false;
        }

        normalized = Vector3.Normalize(value);
        return IsFinite(normalized);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}