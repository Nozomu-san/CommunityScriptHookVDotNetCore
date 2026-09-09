using Alloc8orStandardNatives.Source;

namespace StandardGameOperations.Source;

[Flags]
public enum VehicleHealthChannels
{
    None = 0,
    Entity = 1,
    Body = 2,
    Engine = 4,
    PetrolTank = 8,
    MainRotor = 16,
    TailRotor = 32,
    TailBoom = 64
}

[Flags]
public enum VehicleCapabilities
{
    None = 0,
    RotorSystem = 1,
    PropellerSystem = 2,
    LandingGear = 4,
    VerticalFlight = 8,
    SpecialHoverFlight = 16,
    RetractableWheels = 32,
    Amphibious = 64
}

[Flags]
public enum VehicleDamageStateChannels
{
    None = 0,
    Propellers = 1,
    Wings = 2,
    LandingGear = 4
}

public enum VehicleComponentAvailability
{
    NotApplicable,
    Available,
    StateOnly
}

public readonly record struct VehicleRuntimeState(
    float? FlightNozzlePosition);

public readonly record struct VehicleHealthTopology(
    VehicleHealthChannels HealthChannels,
    bool IsPlane,
    bool IsHelicopter,
    VehicleComponentAvailability PlanePropellers)
{
    public uint ModelHash { get; init; }
    public int VehicleClass { get; init; }
    public VehicleCapabilities Capabilities { get; init; }
    public VehicleDamageStateChannels DamageStates { get; init; }

    public bool Has(VehicleHealthChannels channel) =>
        (HealthChannels & channel) == channel;

    public bool Has(VehicleCapabilities capability) =>
        (Capabilities & capability) == capability;

    public bool Tracks(VehicleDamageStateChannels channel) =>
        (DamageStates & channel) == channel;
}


internal static class VehicleHealthAnchorCatalog
{
    internal static string? BoneName(VehicleHealthChannels channel) =>
        channel switch
        {
            VehicleHealthChannels.Engine => "engine",
            VehicleHealthChannels.PetrolTank => "petroltank",
            _ => null
        };
}

internal readonly record struct VehicleModelCapabilities(
    VehicleCapabilities Capabilities);

internal static class SpecialVehicleCapabilityCatalog
{
    private static readonly Dictionary<uint, VehicleModelCapabilities>
        Known = Build();

    internal static VehicleModelCapabilities Get(uint modelHash) =>
        Known.TryGetValue(modelHash, out VehicleModelCapabilities value)
            ? value
            : default;

    private static Dictionary<uint, VehicleModelCapabilities> Build()
    {
        Dictionary<uint, VehicleModelCapabilities> result = [];

        Add(
            result,
            VehicleCapabilities.VerticalFlight,
            "hydra",
            "tula",
            "avenger",
            "raiju");

        Add(
            result,
            VehicleCapabilities.SpecialHoverFlight,
            "deluxo",
            "oppressor2");

        Add(
            result,
            VehicleCapabilities.PropellerSystem,
            "dodo",
            "mammatus",
            "cuban800",
            "duster",
            "tula",
            "avenger");

        return result;
    }

    private static void Add(
        Dictionary<uint, VehicleModelCapabilities> target,
        VehicleCapabilities capability,
        params string[] modelNames)
    {
        foreach (string modelName in modelNames)
        {
            uint hash = Joaat(modelName);
            VehicleCapabilities current =
                target.TryGetValue(hash, out VehicleModelCapabilities value)
                    ? value.Capabilities
                    : VehicleCapabilities.None;

            target[hash] = new(current | capability);
        }
    }

    private static uint Joaat(string value)
    {
        uint hash = 0;
        foreach (char character in value)
        {
            byte current = unchecked((byte)char.ToLowerInvariant(character));
            hash += current;
            hash += hash << 10;
            hash ^= hash >> 6;
        }

        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }
}

internal sealed class VehicleTopologyResolver
{
    private static readonly VehicleHealthChannels CommonChannels =
        VehicleHealthChannels.Entity |
        VehicleHealthChannels.Body |
        VehicleHealthChannels.Engine |
        VehicleHealthChannels.PetrolTank;

    private readonly Lock _gate = new();
    private readonly Dictionary<uint, VehicleHealthTopology> _cache = [];

    internal VehicleHealthTopology Resolve(Vehicle vehicle, uint modelHash)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(
                    modelHash,
                    out VehicleHealthTopology cached))
            {
                return cached;
            }
        }

        Alloc8orStandardNatives.Source.Vehicle native = vehicle.ToNative();
        bool isHelicopter = StandardNatives.IS_THIS_MODEL_A_HELI(modelHash);
        bool isPlane = StandardNatives.IS_THIS_MODEL_A_PLANE(modelHash);
        int vehicleClass = StandardNatives.GET_VEHICLE_CLASS(native);

        VehicleCapabilities capabilities =
            SpecialVehicleCapabilityCatalog.Get(modelHash).Capabilities;
        VehicleHealthChannels healthChannels = CommonChannels;
        VehicleDamageStateChannels damageStates =
            VehicleDamageStateChannels.None;

        if (isHelicopter)
        {
            capabilities |= VehicleCapabilities.RotorSystem;
            healthChannels |=
                VehicleHealthChannels.MainRotor |
                VehicleHealthChannels.TailRotor |
                VehicleHealthChannels.TailBoom;
        }

        if (isPlane)
        {
            damageStates |= VehicleDamageStateChannels.Wings;
        }

        if (StandardNatives.GET_VEHICLE_HAS_LANDING_GEAR(native))
        {
            capabilities |= VehicleCapabilities.LandingGear;
            if (isPlane)
            {
                damageStates |= VehicleDamageStateChannels.LandingGear;
            }
        }

        if (StandardNatives.GET_HAS_RETRACTABLE_WHEELS(native))
        {
            capabilities |= VehicleCapabilities.RetractableWheels;
        }

        if (StandardNatives.IS_THIS_MODEL_AN_AMPHIBIOUS_CAR(modelHash) ||
            StandardNatives.IS_THIS_MODEL_AN_AMPHIBIOUS_QUADBIKE(modelHash))
        {
            capabilities |= VehicleCapabilities.Amphibious;
        }

        if ((capabilities & VehicleCapabilities.PropellerSystem) != 0)
        {
            damageStates |= VehicleDamageStateChannels.Propellers;
        }

        VehicleHealthTopology topology = new(
            healthChannels,
            isPlane,
            isHelicopter,
            (capabilities & VehicleCapabilities.PropellerSystem) != 0
                ? VehicleComponentAvailability.StateOnly
                : VehicleComponentAvailability.NotApplicable)
        {
            ModelHash = modelHash,
            VehicleClass = vehicleClass,
            Capabilities = capabilities,
            DamageStates = damageStates
        };

        lock (_gate)
        {
            _cache[modelHash] = topology;
        }

        return topology;
    }
}