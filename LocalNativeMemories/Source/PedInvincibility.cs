namespace LocalNativeMemories.Source;

internal sealed class PedInvincibilityMemoryService(
    TargetedEntityMemory memory) : IPedInvincibilityMemory
{
    private const uint StandardMask = 1U << 8;
    private const uint ReactiveMask = 1U << 9;

    private readonly TargetedEntityMemory _memory =
        memory ?? throw new ArgumentNullException(nameof(memory));

    public LocalDataResult<PedInvincibilityState> Read(PedHandle ped)
    {
        PedInvincibilityState state = default;
        LocalDataStatus status = _memory.WithPedAddress(
            ped,
            (address, damageFlagsOffset) =>
            {
                if (!TryReadFlags(
                        address,
                        damageFlagsOffset,
                        out uint flags))
                {
                    return LocalDataStatus.AccessRejected;
                }

                state = new(
                    Standard: (flags & StandardMask) != 0,
                    Reactive: (flags & ReactiveMask) != 0);
                return LocalDataStatus.Success;
            },
            "Ped.Invincibility.Read");

        return status == LocalDataStatus.Success
            ? LocalDataResult<PedInvincibilityState>.Succeeded(state)
            : LocalDataResult<PedInvincibilityState>.Failed(status);
    }

    private static bool TryReadFlags(
        nint pedAddress,
        int damageFlagsOffset,
        out uint flags)
    {
        flags = 0;
        return MemoryAccess.TryAdd(
                   pedAddress,
                   damageFlagsOffset,
                   out nint flagsAddress) &&
               MemoryAccess.TryReadUInt32(flagsAddress, out flags);
    }
}