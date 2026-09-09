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

public interface IBallisticOperations
{
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
    private readonly IEntityOperations _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));
    private readonly IPedOperations _peds =
        peds ?? throw new ArgumentNullException(nameof(peds));

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

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}