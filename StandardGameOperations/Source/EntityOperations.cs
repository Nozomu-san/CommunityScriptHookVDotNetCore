using System.Numerics;
using Alloc8orStandardNatives.Source;
using NativeEntity = Alloc8orStandardNatives.Source.Entity;
using NativeObject = Alloc8orStandardNatives.Source.GameObject;
using NativePed = Alloc8orStandardNatives.Source.Ped;
using NativeVehicle = Alloc8orStandardNatives.Source.Vehicle;

namespace StandardGameOperations.Source;

public readonly record struct Entity(int Value);
public readonly record struct Ped(int Value);
public readonly record struct Vehicle(int Value);
public readonly record struct GameObject(int Value);

public interface IEntityOperations
{
    bool IsValid(Entity entity);
    bool IsValid(Ped ped);
    bool IsValid(Vehicle vehicle);
    bool IsValid(GameObject gameObject);
    Entity AsEntity(Ped ped);
    Entity AsEntity(Vehicle vehicle);
    Entity AsEntity(GameObject gameObject);
    Vector3 GetPosition(Ped ped);
    Vector3 GetPosition(Vehicle vehicle);
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

internal sealed class EntityOperations : IEntityOperations
{
    public bool IsValid(Entity entity) =>
        entity.Value != 0 &&
        StandardNatives.DOES_ENTITY_EXIST(NativeHandles.ToNative(entity));

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

    public Vector3 GetPosition(Ped ped) =>
        !IsValid(ped)
            ? default
            : StandardNatives.GET_ENTITY_COORDS(
                NativeHandles.ToNativeEntity(ped),
                true);

    public Vector3 GetPosition(Vehicle vehicle) =>
        !IsValid(vehicle)
            ? default
            : StandardNatives.GET_ENTITY_COORDS(
                NativeHandles.ToNativeEntity(vehicle),
                true);
}