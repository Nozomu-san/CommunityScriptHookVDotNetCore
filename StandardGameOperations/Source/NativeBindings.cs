using System.Numerics;
using Alloc8orStandardNatives.Source;

namespace StandardGameOperations.Source;

internal sealed class NativeBindings
{
    private readonly IKnownNativeInvoker _known;
    private readonly ulong _getLastImpact;
    private readonly ulong _getLastDamageBone;
    private readonly ulong _getAmmoInClip;
    private readonly ulong _getNearbyPeds;

    internal NativeBindings(
        IStandardNatives natives,
        IKnownNativeInvoker known)
    {
        ArgumentNullException.ThrowIfNull(natives);
        _known = known ?? throw new ArgumentNullException(nameof(known));
        _getLastImpact = ResolveCallable(
            natives.Catalog,
            _known,
            "GET_PED_LAST_WEAPON_IMPACT_COORD");
        _getLastDamageBone = ResolveCallable(
            natives.Catalog,
            _known,
            "GET_PED_LAST_DAMAGE_BONE");
        _getAmmoInClip = ResolveCallable(
            natives.Catalog,
            _known,
            "GET_AMMO_IN_CLIP");
        _getNearbyPeds = ResolveCallable(
            natives.Catalog,
            _known,
            "GET_PED_NEARBY_PEDS");
    }

    internal static void ApplyPedHealthDamage(Ped victim, int damageAmount) =>
        StandardNatives.APPLY_DAMAGE_TO_PED(
            victim.ToNative(),
            damageAmount,
            false,
            new(0),
            0u);

    internal bool TryGetLastImpact(Ped ped, out Vector3 position)
    {
        position = default;
        KnownNativeVector3Output output = new();
        KnownNativeArgument[] arguments =
        [
            KnownNativeArgument.Ped(ped.ToNative()),
            KnownNativeArgument.Vector3Output(output)
        ];

        if (!_known.Invoke(_getLastImpact, arguments).AsBoolean())
        {
            return false;
        }

        Vector3 value = output.Value;
        if (!IsFinite(value))
        {
            return false;
        }

        position = value;
        return true;
    }

    internal bool TryGetLastDamageBone(Ped ped, out int bone)
    {
        bone = 0;
        KnownNativeInt32Output output = new();
        KnownNativeArgument[] arguments =
        [
            KnownNativeArgument.Ped(ped.ToNative()),
            KnownNativeArgument.Int32Output(output)
        ];

        if (!_known.Invoke(_getLastDamageBone, arguments).AsBoolean())
        {
            return false;
        }

        bone = output.Value;
        return bone >= 0 && bone <= ushort.MaxValue;
    }

    internal static bool TryGetCombatTarget(Ped shooter, out Entity target)
    {
        Alloc8orStandardNatives.Source.Entity native =
            StandardNatives.GET_PED_TARGET_FROM_COMBAT_PED(
                shooter.ToNative(),
                new(0));
        target = native.FromNative();
        return target.Value != 0;
    }

    internal bool TryGetAmmoInClip(
        Ped ped,
        uint weaponHash,
        out int ammo)
    {
        ammo = 0;
        KnownNativeInt32Output output = new();
        KnownNativeArgument[] arguments =
        [
            KnownNativeArgument.Ped(ped.ToNative()),
            KnownNativeArgument.Hash32(weaponHash),
            KnownNativeArgument.Int32Output(output)
        ];

        if (!_known.Invoke(_getAmmoInClip, arguments).AsBoolean())
        {
            return false;
        }

        ammo = Math.Max(0, output.Value);
        return true;
    }

    internal Ped[] GetNearbyPeds(Ped origin, int maxAmount)
    {
        int capacity = Math.Clamp(maxAmount, 1, 16);
        int[] seed = new int[(capacity * 2) + 2];
        seed[0] = capacity;

        KnownNativeInt32BufferOutput output = new(seed);
        KnownNativeArgument[] arguments =
        [
            KnownNativeArgument.Ped(origin.ToNative()),
            KnownNativeArgument.AnyInt32BufferOutput(output),
            KnownNativeArgument.Int32(-1)
        ];

        int found = _known.Invoke(_getNearbyPeds, arguments).AsInt32();
        int count = Math.Clamp(found, 0, capacity);
        if (count == 0)
        {
            return [];
        }

        ReadOnlySpan<int> values = output.Values.Span;
        List<Ped> result = [with(count)];
        for (int index = 0; index < count; ++index)
        {
            int valueIndex = (index * 2) + 2;
            if ((uint)valueIndex >= (uint)values.Length)
            {
                break;
            }

            int handle = values[valueIndex];
            if (handle != 0 && handle != origin.Value)
            {
                result.Add(new(handle));
            }
        }

        return [.. result];
    }

    private static ulong ResolveCallable(
        INativeCatalog catalog,
        IKnownNativeInvoker known,
        string name)
    {
        if (!catalog.TryGet(name, out NativeDescriptor? descriptor))
        {
            throw new InvalidDataException(
                $"ASN does not contain native '{name}'.");
        }

        known.Validate(descriptor.Hash);
        return descriptor.Hash;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}