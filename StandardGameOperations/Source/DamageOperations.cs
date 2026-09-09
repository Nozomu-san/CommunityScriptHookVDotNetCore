using System.Numerics;
using Alloc8orStandardNatives.Source;

namespace StandardGameOperations.Source;

public interface IDamageOperations
{
    VehicleVitals ReadVehicleVitals(Vehicle vehicle);
    void ApplyPedHealthDamage(Ped victim, int damageAmount);
    void SetPedArmour(Ped ped, int amount);
    void SetPedHealth(Ped ped, int amount);
    void SetVehicleHealth(Vehicle vehicle, int amount);
    void SetVehicleBodyHealth(Vehicle vehicle, float amount);
    void SetVehicleEngineHealth(Vehicle vehicle, float amount);
    void SetVehiclePetrolTankHealth(Vehicle vehicle, float amount);
    void AddOwnedExplosion(Ped owner, Vector3 position, int explosionType);
}

internal sealed class DamageOperations(
    IEntityOperations entities,
    IPedOperations peds,
    IVehicleOperations vehicles,
    NativeBindings known) : IDamageOperations
{
    private readonly IEntityOperations _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));
    private readonly IPedOperations _peds =
        peds ?? throw new ArgumentNullException(nameof(peds));
    private readonly IVehicleOperations _vehicles =
        vehicles ?? throw new ArgumentNullException(nameof(vehicles));
    private readonly NativeBindings _known =
        known ?? throw new ArgumentNullException(nameof(known));

    public VehicleVitals ReadVehicleVitals(Vehicle vehicle) =>
        _vehicles.ReadVitals(vehicle);

    public void ApplyPedHealthDamage(Ped victim, int damageAmount)
    {
        if (damageAmount > 0 &&
            _peds.IsLivingHuman(victim) &&
            _peds.CanReceiveSyntheticDamage(victim))
        {
            _known.ApplyPedHealthDamage(victim, damageAmount);
        }
    }

    public void SetPedArmour(Ped ped, int amount)
    {
        if (_peds.IsHuman(ped))
        {
            StandardNatives.SET_PED_ARMOUR(
                ped.ToNative(),
                Math.Max(0, amount));
        }
    }

    public void SetPedHealth(Ped ped, int amount)
    {
        if (_peds.IsHuman(ped))
        {
            StandardNatives.SET_ENTITY_HEALTH(
                ped.ToNativeEntity(),
                Math.Max(0, amount),
                default,
                0);
        }
    }

    public void SetVehicleHealth(Vehicle vehicle, int amount) =>
        _vehicles.SetHealth(vehicle, amount);

    public void SetVehicleBodyHealth(Vehicle vehicle, float amount) =>
        _vehicles.SetBodyHealth(vehicle, amount);

    public void SetVehicleEngineHealth(Vehicle vehicle, float amount) =>
        _vehicles.SetEngineHealth(vehicle, amount);

    public void SetVehiclePetrolTankHealth(Vehicle vehicle, float amount) =>
        _vehicles.SetPetrolTankHealth(vehicle, amount);

    public void AddOwnedExplosion(
        Ped owner,
        Vector3 position,
        int explosionType)
    {
        if (!_entities.IsValid(owner) ||
            !float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z))
        {
            return;
        }

        StandardNatives.ADD_OWNED_EXPLOSION(
            owner.ToNative(),
            position.X,
            position.Y,
            position.Z,
            explosionType,
            1f,
            true,
            false,
            0f);
    }
}