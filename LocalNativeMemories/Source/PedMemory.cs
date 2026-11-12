namespace LocalNativeMemories.Source;

internal sealed class PedMemory(
    GameMemoryProfile profile,
    ILocalMemory memory,
    EntityAddressResolver entities) : IPedLocalMemory
{
    private readonly GameMemoryProfile _profile =
        profile ?? throw new ArgumentNullException(nameof(profile));
    private readonly ILocalMemory _memory =
        memory ?? throw new ArgumentNullException(nameof(memory));
    private readonly EntityAddressResolver _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));

    private LocalDataResult<nint> ResolveDamageFlagsAddress(int pedHandle)
    {
        if (pedHandle == 0)
        {
            return LocalDataResult<nint>.Failed(LocalDataStatus.InvalidRequest);
        }

        LocalDataResult<nint> entity = _entities.Resolve(pedHandle);
        return entity.IsSuccess
            ? _memory.Add(entity.Value, _profile.PedDamageFlagsOffset)
            : LocalDataResult<nint>.Failed(entity.Status);
    }

    public LocalDataResult<PedLocalInvincibilityState> ReadInvincibility(
        int pedHandle)
    {
        LocalDataResult<nint> address = ResolveDamageFlagsAddress(pedHandle);
        if (!address.IsSuccess)
        {
            return LocalDataResult<PedLocalInvincibilityState>.Failed(address.Status);
        }

        LocalDataResult<uint> flags = _memory.ReadUInt32(address.Value);
        return flags.IsSuccess
            ? LocalDataResult<PedLocalInvincibilityState>.Succeeded(
                new(
                    (flags.Value & _profile.PureInvincibilityMask) != 0,
                    (flags.Value & _profile.ReactionInvincibilityMask) != 0))
            : LocalDataResult<PedLocalInvincibilityState>.Failed(flags.Status);
    }

    public LocalDataStatus SetReactionInvincibility(int pedHandle, bool value)
    {
        uint mask = _profile.ReactionInvincibilityMask;
        if (mask == 0 || (mask & _profile.PureInvincibilityMask) != 0)
        {
            return LocalDataStatus.VerificationFailed;
        }

        LocalDataResult<nint> address = ResolveDamageFlagsAddress(pedHandle);
        if (!address.IsSuccess)
        {
            return address.Status;
        }

        LocalDataResult<uint> current = _memory.ReadUInt32(address.Value);
        if (!current.IsSuccess)
        {
            return current.Status;
        }

        uint changed = value
            ? current.Value | mask
            : current.Value & ~mask;
        if (changed == current.Value)
        {
            return LocalDataStatus.Success;
        }

        LocalDataStatus written = _memory.WriteUInt32(address.Value, changed);
        if (written is not LocalDataStatus.Success)
        {
            return written;
        }

        LocalDataResult<uint> verified = _memory.ReadUInt32(address.Value);
        if (!verified.IsSuccess)
        {
            return verified.Status;
        }
        return (verified.Value & mask) == (changed & mask)
            ? LocalDataStatus.Success
            : LocalDataStatus.VerificationFailed;
    }
}