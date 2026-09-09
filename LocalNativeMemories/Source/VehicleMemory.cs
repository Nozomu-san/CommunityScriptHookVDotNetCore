namespace LocalNativeMemories.Source;

internal sealed class VehicleMemory(
    GameMemoryProfile profile,
    ILocalMemory memory,
    EntityAddressResolver entities) : IVehicleLocalMemory
{
    private readonly GameMemoryProfile _profile =
        profile ?? throw new ArgumentNullException(nameof(profile));
    private readonly ILocalMemory _memory =
        memory ?? throw new ArgumentNullException(nameof(memory));
    private readonly EntityAddressResolver _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));
    private readonly Lock _gate = new();
    private int _tailBoomOffset;

    public LocalDataStatus SetTailBoomHealth(
        int vehicleHandle,
        float health)
    {
        if (vehicleHandle == 0 || !float.IsFinite(health))
        {
            return LocalDataStatus.InvalidRequest;
        }

        LocalDataResult<nint> entity = _entities.Resolve(vehicleHandle);
        if (!entity.IsSuccess)
        {
            return entity.Status;
        }

        LocalDataResult<int> offset = GetTailBoomOffset();
        if (!offset.IsSuccess)
        {
            return offset.Status;
        }

        LocalDataResult<nint> address =
            _memory.Add(entity.Value, offset.Value);
        if (!address.IsSuccess)
        {
            return address.Status;
        }

        return _memory.WriteSingle(address.Value, health);
    }

    private LocalDataResult<int> GetTailBoomOffset()
    {
        lock (_gate)
        {
            if (_tailBoomOffset > 0)
            {
                return LocalDataResult<int>.Succeeded(_tailBoomOffset);
            }

            TailBoomProfile tailBoom = _profile.TailBoom;
            LocalDataResult<nint> match =
                _memory.FindPattern(tailBoom.Expression);
            if (!match.IsSuccess)
            {
                return LocalDataResult<int>.Failed(match.Status);
            }

            LocalDataResult<nint> offsetAddress =
                _memory.Add(match.Value, tailBoom.DisplacementOffset);
            if (!offsetAddress.IsSuccess)
            {
                return LocalDataResult<int>.Failed(offsetAddress.Status);
            }

            LocalDataResult<int> rotorOffset =
                _memory.ReadInt32(offsetAddress.Value);
            if (!rotorOffset.IsSuccess || rotorOffset.Value <= 0)
            {
                return LocalDataResult<int>.Failed(
                    LocalDataStatus.VerificationFailed);
            }

            _tailBoomOffset = checked(
                rotorOffset.Value + tailBoom.TailBoomOffsetDelta);
            return LocalDataResult<int>.Succeeded(_tailBoomOffset);
        }
    }
}