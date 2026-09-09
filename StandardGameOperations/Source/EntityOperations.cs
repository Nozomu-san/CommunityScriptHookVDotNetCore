using System.Numerics;
using Alloc8orStandardNatives.Source;
using LocalNativeMemories.Source;
using NativeEntity = Alloc8orStandardNatives.Source.Entity;
using NativeObject = Alloc8orStandardNatives.Source.GameObject;
using NativePed = Alloc8orStandardNatives.Source.Ped;
using NativeVehicle = Alloc8orStandardNatives.Source.Vehicle;

namespace StandardGameOperations.Source;

public readonly record struct Entity(int Value);
public readonly record struct Ped(int Value);
public readonly record struct Vehicle(int Value);
public readonly record struct GameObject(int Value);

public readonly record struct EntityPopulationCounts(
    bool Available,
    int Peds,
    int Vehicles,
    int Objects)
{
    public int Total => checked(Peds + Vehicles + Objects);
}

public interface IEntityOperations
{
    bool IsValid(Entity entity);
    bool IsValid(Ped ped);
    bool IsValid(Vehicle vehicle);
    bool IsValid(GameObject gameObject);
    Entity AsEntity(Ped ped);
    Entity AsEntity(Vehicle vehicle);
    Entity AsEntity(GameObject gameObject);
    Vector3 GetPosition(Entity entity);
    Vector3 GetPosition(Ped ped);
    Vector3 GetPosition(Vehicle vehicle);
    EntityPopulationCounts GetPopulationCounts();
}

internal static class NativeHandles
{
    extension(Entity value)
    {
        internal NativeEntity ToNative() => new(value.Value);
    }

    extension(Ped value)
    {
        internal NativePed ToNative() => new(value.Value);
        internal NativeEntity ToNativeEntity() => new(value.Value);
    }

    extension(Vehicle value)
    {
        internal NativeVehicle ToNative() => new(value.Value);
        internal NativeEntity ToNativeEntity() => new(value.Value);
    }

    extension(GameObject value)
    {
        internal NativeObject ToNative() => new(value.Value);
        internal NativeEntity ToNativeEntity() => new(value.Value);
    }

    extension(NativeEntity value)
    {
        internal Entity FromNative() => new(value.Value);
    }

    extension(NativePed value)
    {
        internal Ped FromNative() => new(value.Value);
    }

    extension(NativeVehicle value)
    {
        internal Vehicle FromNative() => new(value.Value);
    }

    extension(NativeObject value)
    {
        internal GameObject FromNative() => new(value.Value);
    }
}

internal sealed class EntityOperations(ILocalEntityPools pools) :
    IEntityOperations
{
    private readonly ILocalEntityPools _pools =
        pools ?? throw new ArgumentNullException(nameof(pools));

    public bool IsValid(Entity entity) =>
        entity.Value != 0 &&
        StandardNatives.DOES_ENTITY_EXIST(entity.ToNative());

    public bool IsValid(Ped ped)
    {
        if (ped.Value == 0)
        {
            return false;
        }

        NativeEntity entity = new(ped.Value);
        return StandardNatives.DOES_ENTITY_EXIST(entity) &&
            StandardNatives.IS_ENTITY_A_PED(entity);
    }

    public bool IsValid(Vehicle vehicle)
    {
        if (vehicle.Value == 0)
        {
            return false;
        }

        NativeEntity entity = new(vehicle.Value);
        return StandardNatives.DOES_ENTITY_EXIST(entity) &&
            StandardNatives.IS_ENTITY_A_VEHICLE(entity);
    }

    public bool IsValid(GameObject gameObject)
    {
        if (gameObject.Value == 0)
        {
            return false;
        }

        NativeEntity entity = new(gameObject.Value);
        return StandardNatives.DOES_ENTITY_EXIST(entity) &&
            StandardNatives.IS_ENTITY_AN_OBJECT(entity);
    }

    public Entity AsEntity(Ped ped) => new(ped.Value);
    public Entity AsEntity(Vehicle vehicle) => new(vehicle.Value);
    public Entity AsEntity(GameObject gameObject) => new(gameObject.Value);

    public Vector3 GetPosition(Entity entity) =>
        !IsValid(entity)
            ? default
            : StandardNatives.GET_ENTITY_COORDS(entity.ToNative(), true);

    public Vector3 GetPosition(Ped ped) => GetPosition(AsEntity(ped));
    public Vector3 GetPosition(Vehicle vehicle) => GetPosition(AsEntity(vehicle));

    public EntityPopulationCounts GetPopulationCounts()
    {
        LocalDataResult<LocalPoolStatistics> peds =
            _pools.GetStatistics(LocalPoolKinds.Peds);
        LocalDataResult<LocalPoolStatistics> vehicles =
            _pools.GetStatistics(LocalPoolKinds.Vehicles);
        LocalDataResult<LocalPoolStatistics> objects =
            _pools.GetStatistics(LocalPoolKinds.Objects);

        if (!peds.IsSuccess || !vehicles.IsSuccess || !objects.IsSuccess)
        {
            return default;
        }

        return new(
            true,
            peds.Value.ActiveCount,
            vehicles.Value.ActiveCount,
            objects.Value.ActiveCount);
    }
}