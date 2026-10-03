using System.Numerics;
using Alloc8orStandardNatives.Source;
using LocalNativeMemories.Source;

namespace StandardGameOperations.Source;

public readonly record struct VehicleVitals(
    float BodyHealth,
    float EngineHealth,
    float PetrolTankHealth,
    int EntityHealth,
    float? MainRotorHealth,
    float? TailRotorHealth,
    float? TailBoomHealth,
    bool? PlanePropellersIntact)
{
    public bool? PlaneWingsIntact { get; init; }
    public bool? LandingGearIntact { get; init; }

    public IEnumerable<(VehicleHealthChannels Channel, float Value)>
        NumericChannels()
    {
        yield return (VehicleHealthChannels.Entity, EntityHealth);
        yield return (VehicleHealthChannels.Body, BodyHealth);
        yield return (VehicleHealthChannels.Engine, EngineHealth);
        yield return (VehicleHealthChannels.PetrolTank, PetrolTankHealth);

        if (MainRotorHealth is float main)
        {
            yield return (VehicleHealthChannels.MainRotor, main);
        }

        if (TailRotorHealth is float tail)
        {
            yield return (VehicleHealthChannels.TailRotor, tail);
        }

        if (TailBoomHealth is float boom)
        {
            yield return (VehicleHealthChannels.TailBoom, boom);
        }
    }
}

public interface IVehicleOperations
{
    VehicleHealthTopology GetHealthTopology(Vehicle vehicle);
    VehicleRuntimeState ReadRuntimeState(Vehicle vehicle);
    VehicleVitals ReadVitals(Vehicle vehicle);
    void SetHealth(Vehicle vehicle, int value);
    void SetBodyHealth(Vehicle vehicle, float value);
    void SetEngineHealth(Vehicle vehicle, float value);
    void SetPetrolTankHealth(Vehicle vehicle, float value);
    void SetMainRotorHealth(Vehicle vehicle, float value);
    void SetTailRotorHealth(Vehicle vehicle, float value);
    bool TrySetTailBoomHealth(Vehicle vehicle, float value);
    bool TryGetHealthAnchor(
        Vehicle vehicle,
        VehicleHealthChannels channel,
        out Vector3 position);
}

internal sealed class VehicleOperations(
    IEntityOperations entities,
    IVehicleLocalMemory memory) : IVehicleOperations
{
    private readonly IEntityOperations _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));
    private readonly IVehicleLocalMemory _memory =
        memory ?? throw new ArgumentNullException(nameof(memory));
    private readonly VehicleTopologyResolver _topology = new();

    public VehicleHealthTopology GetHealthTopology(Vehicle vehicle)
    {
        if (!_entities.IsValid(vehicle))
        {
            return default;
        }

        uint model = StandardNatives.GET_ENTITY_MODEL(
            vehicle.ToNativeEntity());
        return _topology.Resolve(vehicle, model);
    }

    public bool TryGetHealthAnchor(
        Vehicle vehicle,
        VehicleHealthChannels channel,
        out Vector3 position)
    {
        position = default;
        if (!_entities.IsValid(vehicle) ||
            !GetHealthTopology(vehicle).Has(channel))
        {
            return false;
        }

        if (channel is VehicleHealthChannels.Entity or VehicleHealthChannels.Body)
        {
            position = _entities.GetPosition(vehicle);
            return IsFinite(position);
        }

        string? boneName = VehicleHealthAnchorCatalog.BoneName(channel);
        if (string.IsNullOrWhiteSpace(boneName))
        {
            return false;
        }

        int boneIndex = StandardNatives.GET_ENTITY_BONE_INDEX_BY_NAME(
            vehicle.ToNativeEntity(),
            boneName);
        if (boneIndex < 0)
        {
            return false;
        }

        position = StandardNatives.GET_WORLD_POSITION_OF_ENTITY_BONE(
            vehicle.ToNativeEntity(),
            boneIndex);
        return IsFinite(position);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    public VehicleRuntimeState ReadRuntimeState(Vehicle vehicle)
    {
        if (!_entities.IsValid(vehicle))
        {
            return default;
        }

        VehicleHealthTopology topology = GetHealthTopology(vehicle);
        float? flightNozzlePosition = topology.Has(
                VehicleCapabilities.VerticalFlight)
            ? StandardNatives.GET_VEHICLE_FLIGHT_NOZZLE_POSITION(
                vehicle.ToNative())
            : null;

        return new(flightNozzlePosition);
    }

    public VehicleVitals ReadVitals(Vehicle vehicle)
    {
        if (!_entities.IsValid(vehicle))
        {
            return default;
        }

        VehicleHealthTopology topology = GetHealthTopology(vehicle);
        Alloc8orStandardNatives.Source.Vehicle native = vehicle.ToNative();

        float? main = topology.Has(VehicleHealthChannels.MainRotor)
            ? StandardNatives.GET_HELI_MAIN_ROTOR_HEALTH(native)
            : null;

        float? tail = topology.Has(VehicleHealthChannels.TailRotor)
            ? StandardNatives.GET_HELI_TAIL_ROTOR_HEALTH(native)
            : null;

        float? boom = topology.Has(VehicleHealthChannels.TailBoom)
            ? StandardNatives.GET_HELI_TAIL_BOOM_HEALTH(native)
            : null;

        bool? propellers = topology.Tracks(
                VehicleDamageStateChannels.Propellers)
            ? StandardNatives.ARE_PLANE_PROPELLERS_INTACT(native)
            : null;

        bool? wings = topology.Tracks(VehicleDamageStateChannels.Wings)
            ? StandardNatives.ARE_WINGS_OF_PLANE_INTACT(native)
            : null;

        bool? landingGear = topology.Tracks(
                VehicleDamageStateChannels.LandingGear)
            ? StandardNatives.IS_PLANE_LANDING_GEAR_INTACT(native)
            : null;

        return new(
            StandardNatives.GET_VEHICLE_BODY_HEALTH(native),
            StandardNatives.GET_VEHICLE_ENGINE_HEALTH(native),
            StandardNatives.GET_VEHICLE_PETROL_TANK_HEALTH(native),
            Math.Max(
                0,
                StandardNatives.GET_ENTITY_HEALTH(
                    vehicle.ToNativeEntity())),
            main,
            tail,
            boom,
            propellers)
        {
            PlaneWingsIntact = wings,
            LandingGearIntact = landingGear
        };
    }

    public void SetHealth(Vehicle vehicle, int value)
    {
        if (_entities.IsValid(vehicle))
        {
            StandardNatives.SET_ENTITY_HEALTH(
                vehicle.ToNativeEntity(),
                Math.Max(0, value),
                default,
                0);
        }
    }

    public void SetBodyHealth(Vehicle vehicle, float value)
    {
        if (_entities.IsValid(vehicle) && float.IsFinite(value))
        {
            StandardNatives.SET_VEHICLE_BODY_HEALTH(
                vehicle.ToNative(),
                value);
        }
    }

    public void SetEngineHealth(Vehicle vehicle, float value)
    {
        if (_entities.IsValid(vehicle) && float.IsFinite(value))
        {
            StandardNatives.SET_VEHICLE_ENGINE_HEALTH(
                vehicle.ToNative(),
                value);
        }
    }

    public void SetPetrolTankHealth(Vehicle vehicle, float value)
    {
        if (_entities.IsValid(vehicle) && float.IsFinite(value))
        {
            StandardNatives.SET_VEHICLE_PETROL_TANK_HEALTH(
                vehicle.ToNative(),
                value);
        }
    }

    public void SetMainRotorHealth(Vehicle vehicle, float value)
    {
        if (_entities.IsValid(vehicle) &&
            float.IsFinite(value) &&
            GetHealthTopology(vehicle).Has(VehicleHealthChannels.MainRotor))
        {
            StandardNatives.SET_HELI_MAIN_ROTOR_HEALTH(
                vehicle.ToNative(),
                value);
        }
    }

    public void SetTailRotorHealth(Vehicle vehicle, float value)
    {
        if (_entities.IsValid(vehicle) &&
            float.IsFinite(value) &&
            GetHealthTopology(vehicle).Has(VehicleHealthChannels.TailRotor))
        {
            StandardNatives.SET_HELI_TAIL_ROTOR_HEALTH(
                vehicle.ToNative(),
                value);
        }
    }

    public bool TrySetTailBoomHealth(Vehicle vehicle, float value) =>
        _entities.IsValid(vehicle) &&
        float.IsFinite(value) &&
        GetHealthTopology(vehicle).Has(VehicleHealthChannels.TailBoom) &&
        _memory.SetTailBoomHealth(vehicle.Value, value) is
            LocalDataStatus.Success;
}