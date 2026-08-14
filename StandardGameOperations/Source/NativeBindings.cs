using System.Numerics;
using Alloc8orStandardNatives.Source;

namespace StandardGameOperations.Source;

internal sealed class NativeBindings
{
    private readonly IStandardNatives _natives;
    private readonly NativeDescriptor _applyDamageToPed;
    private readonly NativeDescriptor _getLastImpact;
    private readonly NativeDescriptor _getCombatTarget;
    private readonly NativeDescriptor _getAmmoInClip;
    private readonly NativeDescriptor _getNearbyPeds;

    internal NativeBindings(IStandardNatives natives)
    {
        _natives = natives ?? throw new ArgumentNullException(nameof(natives));

        _applyDamageToPed = Resolve(
            "APPLY_DAMAGE_TO_PED",
            NativeAbiType.Void,
            NativeAbiType.Ped,
            NativeAbiType.Int32,
            NativeAbiType.Boolean32,
            NativeAbiType.Any,
            NativeAbiType.Hash32);

        _getLastImpact = Resolve(
            "GET_PED_LAST_WEAPON_IMPACT_COORD",
            NativeAbiType.Boolean32,
            NativeAbiType.Ped,
            NativeAbiType.Vector3Pointer);

        _getCombatTarget = Resolve(
            "GET_PED_TARGET_FROM_COMBAT_PED",
            NativeAbiType.Entity,
            NativeAbiType.Ped,
            NativeAbiType.Any);

        _getAmmoInClip = Resolve(
            "GET_AMMO_IN_CLIP",
            NativeAbiType.Boolean32,
            NativeAbiType.Ped,
            NativeAbiType.Hash32,
            NativeAbiType.Int32Pointer);

        _getNearbyPeds = Resolve(
            "GET_PED_NEARBY_PEDS",
            NativeAbiType.Int32,
            NativeAbiType.Ped,
            NativeAbiType.AnyPointer,
            NativeAbiType.Int32);
    }

    internal void ApplyPedHealthDamage(Ped victim, int damageAmount)
    {
        KnownNativeArgument[] arguments =
        [
            KnownNativeArgument.Ped(NativeHandles.ToNative(victim)),
            KnownNativeArgument.Int32(damageAmount),
            KnownNativeArgument.Boolean(false),
            KnownNativeArgument.Any(new NativeAny(0)),
            KnownNativeArgument.Hash32(0)
        ];

        _ = _natives.Known.Invoke(_applyDamageToPed.Hash, arguments);
    }

    internal bool TryGetLastImpact(Ped ped, out Vector3 position)
    {
        position = default;
        KnownNativeVector3Output output = new();
        KnownNativeArgument[] arguments =
        [
            KnownNativeArgument.Ped(NativeHandles.ToNative(ped)),
            KnownNativeArgument.Vector3Output(output)
        ];

        if (!_natives.Known.Invoke(_getLastImpact.Hash, arguments).AsBoolean())
        {
            return false;
        }

        Vector3 value = output.Value;
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z))
        {
            return false;
        }

        position = value;
        return true;
    }

    internal bool TryGetCombatTarget(Ped shooter, out Entity target)
    {
        KnownNativeArgument[] arguments =
        [
            KnownNativeArgument.Ped(NativeHandles.ToNative(shooter)),
            KnownNativeArgument.Any(new NativeAny(0))
        ];

        KnownNativeResult result = _natives.Known.Invoke(
            _getCombatTarget.Hash,
            arguments);

        Alloc8orStandardNatives.Source.Entity native = result.AsEntity();
        target = new(native.Value);
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
            KnownNativeArgument.Ped(NativeHandles.ToNative(ped)),
            KnownNativeArgument.Hash32(weaponHash),
            KnownNativeArgument.Int32Output(output)
        ];

        if (!_natives.Known.Invoke(_getAmmoInClip.Hash, arguments).AsBoolean())
        {
            return false;
        }

        ammo = Math.Max(0, output.Value);
        return true;
    }

    internal Ped[] GetNearbyPeds(
        Ped origin,
        int maxAmount)
    {
        int capacity = Math.Clamp(maxAmount, 1, 16);
        int[] seed = new int[(capacity * 2) + 2];
        seed[0] = capacity;

        KnownNativeInt32BufferOutput output = new(seed);
        KnownNativeArgument[] arguments =
        [
            KnownNativeArgument.Ped(NativeHandles.ToNative(origin)),
            KnownNativeArgument.AnyInt32BufferOutput(output),
            KnownNativeArgument.Int32(-1)
        ];

        int found = _natives.Known.Invoke(
            _getNearbyPeds.Hash,
            arguments).AsInt32();

        int count = Math.Clamp(found, 0, capacity);
        if (count == 0)
        {
            return [];
        }

        ReadOnlySpan<int> values = output.Values.Span;
        List<Ped> result = new(count);
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
                result.Add(new Ped(handle));
            }
        }

        return [.. result];
    }

    private NativeDescriptor Resolve(
        string name,
        NativeAbiType returnType,
        params NativeAbiType[] parameterTypes)
    {
        if (!_natives.Catalog.TryGet(name, out NativeDescriptor? descriptor))
        {
            throw new InvalidDataException(
                $"ASN does not contain native '{name}'.");
        }

        GameBuildInfo game = _natives.GameBuild.Current;
        NativeSignatureVariant variant = descriptor.GetVariant(game.Edition) ??
            throw new InvalidDataException(
                $"Native '{name}' has no ABI variant for '{game.Edition}'.");

        if (game.Build < variant.MinimumBuild)
        {
            throw new PlatformNotSupportedException(
                $"Native '{name}' requires build {variant.MinimumBuild}, " +
                $"but the current build is {game.Build}.");
        }

        if (variant.ReturnType != returnType ||
            variant.Parameters.Count != parameterTypes.Length)
        {
            throw new InvalidDataException(
                $"Native '{name}' does not match the expected ABI contract.");
        }

        for (int index = 0; index < parameterTypes.Length; ++index)
        {
            if (variant.Parameters[index].Type != parameterTypes[index])
            {
                throw new InvalidDataException(
                    $"Native '{name}' parameter {index} is " +
                    $"{variant.Parameters[index].Type}, not " +
                    $"{parameterTypes[index]}.");
            }
        }

        return descriptor;
    }
}