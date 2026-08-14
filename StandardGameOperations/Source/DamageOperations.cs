using Alloc8orStandardNatives.Source;

namespace StandardGameOperations.Source;

public readonly record struct VehicleVitals(
    float BodyHealth,
    float EngineHealth,
    int EntityHealth);

public readonly record struct PedDamageSnapshot(
    Ped Victim,
    int PreviousArmour,
    int CurrentArmour,
    int PreviousHealth,
    int CurrentHealth)
{
    public int ArmourLoss =>
        Math.Max(0, PreviousArmour - CurrentArmour);

    public int HealthLoss =>
        Math.Max(0, PreviousHealth - CurrentHealth);

    public int TotalLoss => ArmourLoss + HealthLoss;
    public bool HasDamage => TotalLoss > 0;
}

public readonly record struct VehicleDamageSnapshot(
    Vehicle Victim,
    float PreviousBodyHealth,
    float CurrentBodyHealth,
    float PreviousEngineHealth,
    float CurrentEngineHealth,
    int PreviousEntityHealth,
    int CurrentEntityHealth)
{
    public float BodyHealthLoss =>
        Math.Max(0f, PreviousBodyHealth - CurrentBodyHealth);

    public float EngineHealthLoss =>
        Math.Max(0f, PreviousEngineHealth - CurrentEngineHealth);

    public int EntityHealthLoss =>
        Math.Max(0, PreviousEntityHealth - CurrentEntityHealth);

    public float TransferableDirectHealthLoss =>
        BodyHealthLoss > 0f
            ? BodyHealthLoss
            : EngineHealthLoss <= 0f && EntityHealthLoss > 0
                ? EntityHealthLoss
                : 0f;

    public bool HasDamage =>
        BodyHealthLoss > 0f ||
        EngineHealthLoss > 0f ||
        EntityHealthLoss > 0;
}

public readonly record struct EngagementBaseline(
    CombatEngagement Engagement,
    PedVitals PedVitals,
    VehicleVitals VehicleVitals)
{
    public bool HasVehicle => Engagement.HasVehicle;
}

public readonly record struct EngagementDamageSnapshot(
    CombatEngagement Engagement,
    PedDamageSnapshot Ped,
    VehicleDamageSnapshot Vehicle,
    bool HasVehicle)
{
    public bool HasDamage =>
        Ped.HasDamage ||
        (HasVehicle && Vehicle.HasDamage);
}

public interface IDamageOperations
{
    VehicleVitals ReadVehicleVitals(Vehicle vehicle);
    bool TryCaptureBaseline(
        CombatEngagement engagement,
        out EngagementBaseline baseline);
    EngagementDamageSnapshot CaptureEngagementDamage(
        EngagementBaseline baseline);
    PedDamageSnapshot CapturePedDamage(Ped victim, PedVitals previous);
    VehicleDamageSnapshot CaptureVehicleDamage(
        Vehicle victim,
        VehicleVitals previous);
    void ApplyPedHealthDamage(Ped victim, int damageAmount);
    void SetPedArmour(Ped ped, int amount);
    void SetPedHealth(Ped ped, int amount);
    void SetVehicleHealth(Vehicle vehicle, int amount);
    void SetVehicleBodyHealth(Vehicle vehicle, float amount);
    void SetVehicleEngineHealth(Vehicle vehicle, float amount);
}

internal sealed class DamageOperations(
    IEntityOperations entities,
    IPedOperations peds,
    NativeBindings known) : IDamageOperations
{
    private readonly IEntityOperations _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));

    private readonly IPedOperations _peds =
        peds ?? throw new ArgumentNullException(nameof(peds));

    private readonly NativeBindings _known =
        known ?? throw new ArgumentNullException(nameof(known));

    public VehicleVitals ReadVehicleVitals(Vehicle vehicle) =>
        !_entities.IsValid(vehicle)
            ? default
            : new(
                StandardNatives.GET_VEHICLE_BODY_HEALTH(
                    NativeHandles.ToNative(vehicle)),
                StandardNatives.GET_VEHICLE_ENGINE_HEALTH(
                    NativeHandles.ToNative(vehicle)),
                Math.Max(
                    0,
                    StandardNatives.GET_ENTITY_HEALTH(
                        NativeHandles.ToNativeEntity(vehicle))));

    public bool TryCaptureBaseline(
        CombatEngagement engagement,
        out EngagementBaseline baseline)
    {
        baseline = default;
        if (!_entities.IsValid(engagement.Shooter) ||
            !_peds.IsLivingHuman(engagement.Victim))
        {
            return false;
        }

        PedVitals pedVitals = _peds.ReadVitals(engagement.Victim);
        VehicleVitals vehicleVitals = default;
        if (engagement.HasVehicle &&
            _entities.IsValid(engagement.VictimVehicle))
        {
            vehicleVitals = ReadVehicleVitals(engagement.VictimVehicle);
        }

        baseline = new(engagement, pedVitals, vehicleVitals);
        return true;
    }

    public EngagementDamageSnapshot CaptureEngagementDamage(
        EngagementBaseline baseline)
    {
        PedDamageSnapshot ped = CapturePedDamage(
            baseline.Engagement.Victim,
            baseline.PedVitals);

        bool hasVehicle = baseline.HasVehicle &&
            _entities.IsValid(baseline.Engagement.VictimVehicle);
        VehicleDamageSnapshot vehicle = hasVehicle
            ? CaptureVehicleDamage(
                baseline.Engagement.VictimVehicle,
                baseline.VehicleVitals)
            : default;

        return new(
            baseline.Engagement,
            ped,
            vehicle,
            hasVehicle);
    }

    public PedDamageSnapshot CapturePedDamage(
        Ped victim,
        PedVitals previous)
    {
        if (!_entities.IsValid(victim))
        {
            return new(
                victim,
                previous.Armour,
                previous.Armour,
                previous.Health,
                previous.Health);
        }

        PedVitals current = _peds.ReadVitals(victim);
        return new(
            victim,
            previous.Armour,
            current.Armour,
            previous.Health,
            current.Health);
    }

    public VehicleDamageSnapshot CaptureVehicleDamage(
        Vehicle victim,
        VehicleVitals previous)
    {
        if (!_entities.IsValid(victim))
        {
            return new(
                victim,
                previous.BodyHealth,
                previous.BodyHealth,
                previous.EngineHealth,
                previous.EngineHealth,
                previous.EntityHealth,
                previous.EntityHealth);
        }

        VehicleVitals current = ReadVehicleVitals(victim);
        return new(
            victim,
            previous.BodyHealth,
            current.BodyHealth,
            previous.EngineHealth,
            current.EngineHealth,
            previous.EntityHealth,
            current.EntityHealth);
    }

    public void ApplyPedHealthDamage(Ped victim, int damageAmount)
    {
        if (damageAmount <= 0 || !_entities.IsValid(victim))
        {
            return;
        }

        _known.ApplyPedHealthDamage(victim, damageAmount);
    }

    public void SetPedArmour(Ped ped, int amount)
    {
        if (_entities.IsValid(ped))
        {
            StandardNatives.SET_PED_ARMOUR(
                NativeHandles.ToNative(ped),
                Math.Max(0, amount));
        }
    }

    public void SetPedHealth(Ped ped, int amount)
    {
        if (_entities.IsValid(ped))
        {
            SetEntityHealth(_entities.AsEntity(ped), amount);
        }
    }

    public void SetVehicleHealth(Vehicle vehicle, int amount)
    {
        if (_entities.IsValid(vehicle))
        {
            SetEntityHealth(_entities.AsEntity(vehicle), amount);
        }
    }

    public void SetVehicleBodyHealth(Vehicle vehicle, float amount)
    {
        if (_entities.IsValid(vehicle) && float.IsFinite(amount))
        {
            StandardNatives.SET_VEHICLE_BODY_HEALTH(
                NativeHandles.ToNative(vehicle),
                amount);
        }
    }

    public void SetVehicleEngineHealth(Vehicle vehicle, float amount)
    {
        if (_entities.IsValid(vehicle) && float.IsFinite(amount))
        {
            StandardNatives.SET_VEHICLE_ENGINE_HEALTH(
                NativeHandles.ToNative(vehicle),
                amount);
        }
    }

    private void SetEntityHealth(Entity entity, int amount)
    {
        if (_entities.IsValid(entity))
        {
            StandardNatives.SET_ENTITY_HEALTH(
                NativeHandles.ToNative(entity),
                Math.Max(0, amount),
                default,
                0);
        }
    }
}