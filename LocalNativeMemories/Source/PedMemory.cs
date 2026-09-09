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

    public LocalDataResult<PedLocalInvincibilityState> ReadInvincibility(
        int pedHandle)
    {
        LocalDataResult<nint> entity = _entities.Resolve(pedHandle);

        if (!entity.IsSuccess)
        {
            return LocalDataResult<PedLocalInvincibilityState>.Failed(
                entity.Status);
        }

        LocalDataResult<nint> flagsAddress =
            _memory.Add(entity.Value, _profile.PedDamageFlagsOffset);
        if (!flagsAddress.IsSuccess)
        {
            return LocalDataResult<PedLocalInvincibilityState>.Failed(
                flagsAddress.Status);
        }

        LocalDataResult<uint> flags =
            _memory.ReadUInt32(flagsAddress.Value);
        if (!flags.IsSuccess)
        {
            return LocalDataResult<PedLocalInvincibilityState>.Failed(
                flags.Status);
        }

        return LocalDataResult<PedLocalInvincibilityState>.Succeeded(
            new(
                (flags.Value & _profile.PureInvincibilityMask) != 0,
                (flags.Value & _profile.ReactionInvincibilityMask) != 0));
    }
}