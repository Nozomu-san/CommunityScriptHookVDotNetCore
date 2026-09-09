using System.Numerics;
using Alloc8orStandardNatives.Source;

namespace StandardGameOperations.Source;

public interface IWeaponOperations
{
    uint GetSelectedWeapon(Ped ped);
    bool HasWeapon(Ped ped, uint weaponHash);
    uint GetAmmoType(Ped ped, uint weaponHash);
    bool HasComponent(Ped ped, uint weaponHash, uint componentHash);
    bool IsComponentActive(Ped ped, uint weaponHash, uint componentHash);
    bool TryGetAmmoInClip(Ped ped, uint weaponHash, out int ammo);
    int GetMaxAmmoInClip(Ped ped, uint weaponHash);
    bool TryGetLastImpact(Ped ped, out Vector3 position);
    bool TryGetDamage(
        uint weaponHash,
        out float damage,
        uint componentHash = 0);
}

internal sealed class WeaponOperations(
    IEntityOperations entities,
    NativeBindings known) : IWeaponOperations
{
    private readonly IEntityOperations _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));
    private readonly NativeBindings _known =
        known ?? throw new ArgumentNullException(nameof(known));

    public uint GetSelectedWeapon(Ped ped) =>
        !_entities.IsValid(ped)
            ? 0
            : StandardNatives.GET_SELECTED_PED_WEAPON(ped.ToNative());

    public bool HasWeapon(Ped ped, uint weaponHash) =>
        weaponHash != 0 &&
        _entities.IsValid(ped) &&
        StandardNatives.HAS_PED_GOT_WEAPON(
            ped.ToNative(),
            weaponHash,
            false);

    public uint GetAmmoType(Ped ped, uint weaponHash) =>
        weaponHash == 0 || !_entities.IsValid(ped)
            ? 0
            : StandardNatives.GET_PED_AMMO_TYPE_FROM_WEAPON(
                ped.ToNative(),
                weaponHash);

    public bool HasComponent(
        Ped ped,
        uint weaponHash,
        uint componentHash) =>
        weaponHash != 0 &&
        componentHash != 0 &&
        _entities.IsValid(ped) &&
        StandardNatives.HAS_PED_GOT_WEAPON_COMPONENT(
            ped.ToNative(),
            weaponHash,
            componentHash);

    public bool IsComponentActive(
        Ped ped,
        uint weaponHash,
        uint componentHash) =>
        weaponHash != 0 &&
        componentHash != 0 &&
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_WEAPON_COMPONENT_ACTIVE(
            ped.ToNative(),
            weaponHash,
            componentHash);

    public bool TryGetAmmoInClip(
        Ped ped,
        uint weaponHash,
        out int ammo)
    {
        ammo = 0;
        return weaponHash != 0 &&
            _entities.IsValid(ped) &&
            _known.TryGetAmmoInClip(ped, weaponHash, out ammo);
    }

    public int GetMaxAmmoInClip(Ped ped, uint weaponHash) =>
        weaponHash == 0 || !_entities.IsValid(ped)
            ? 0
            : Math.Max(
                0,
                StandardNatives.GET_MAX_AMMO_IN_CLIP(
                    ped.ToNative(),
                    weaponHash,
                    true));

    public bool TryGetLastImpact(Ped ped, out Vector3 position)
    {
        position = default;
        return _entities.IsValid(ped) &&
            _known.TryGetLastImpact(ped, out position);
    }

    public bool TryGetDamage(
        uint weaponHash,
        out float damage,
        uint componentHash = 0)
    {
        damage = 0f;
        if (weaponHash == 0)
        {
            return false;
        }

        float value = StandardNatives.GET_WEAPON_DAMAGE(
            weaponHash,
            componentHash);

        if (!float.IsFinite(value) || value <= 0f)
        {
            return false;
        }

        damage = value;
        return true;
    }
}