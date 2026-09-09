namespace LocalNativeMemories.Source;

public enum LocalDataStatus
{
    Success,
    InvalidRequest,
    UnsupportedProcess,
    DataUnavailable,
    AccessRejected,
    VerificationFailed
}

public readonly record struct LocalDataResult<T>(
    LocalDataStatus Status,
    T Value)
{
    public bool IsSuccess => Status is LocalDataStatus.Success;

    internal static LocalDataResult<T> Succeeded(T value) =>
        new(LocalDataStatus.Success, value);

    internal static LocalDataResult<T> Failed(LocalDataStatus status) =>
        new(status, default!);
}

internal enum LocalGameEdition
{
    Legacy,
    Enhanced
}

[Flags]
public enum LocalPoolKinds
{
    None = 0,
    Peds = 1,
    Vehicles = 2,
    Objects = 4,
    All = Peds | Vehicles | Objects
}

public readonly record struct LocalPoolStatistics(
    int Capacity,
    int ActiveCount)
{
    public bool IsConsistent =>
        Capacity >= 0 &&
        ActiveCount >= 0 &&
        ActiveCount <= Capacity;

    public static LocalPoolStatistics Empty { get; } = new(0, 0);
}

public readonly record struct LocalPoolSnapshot(
    ulong Revision,
    LocalPoolStatistics Statistics,
    ReadOnlyMemory<int> Handles)
{
    public int Capacity => Statistics.Capacity;
    public int ActiveCount => Statistics.ActiveCount;
    public int Count => Handles.Length;

    public static LocalPoolSnapshot Empty { get; } =
        new(0, LocalPoolStatistics.Empty, ReadOnlyMemory<int>.Empty);
}

public readonly record struct EntityPoolsSnapshot(
    LocalPoolSnapshot Peds,
    LocalPoolSnapshot Vehicles,
    LocalPoolSnapshot Objects)
{
    public int EntityCount =>
        Peds.ActiveCount + Vehicles.ActiveCount + Objects.ActiveCount;

    public int ResolvedEntityCount =>
        Peds.Count + Vehicles.Count + Objects.Count;
}

public readonly record struct PedLocalInvincibilityState(
    bool PureInvincibility,
    bool ReactionInvincibility);

public interface ILocalMemory
{
    LocalDataResult<nint> FindPattern(string expression);

    LocalDataResult<nint> ResolveRipRelative(
        nint instruction,
        int displacementOffset,
        int instructionLength);

    LocalDataResult<nint> Add(nint address, long offset);

    LocalDataResult<nint> InvokeInt32ToPointer(
        nint functionAddress,
        int argument);

    LocalDataResult<byte> ReadByte(nint address);
    LocalDataResult<ushort> ReadUInt16(nint address);
    LocalDataResult<uint> ReadUInt32(nint address);
    LocalDataResult<ulong> ReadUInt64(nint address);
    LocalDataResult<int> ReadInt32(nint address);
    LocalDataResult<nint> ReadPointer(nint address);
    LocalDataResult<float> ReadSingle(nint address);
    LocalDataStatus WriteSingle(nint address, float value);

    bool IsReadableRange(nint address, nuint size);
    bool IsWritableRange(nint address, nuint size);
    bool IsExecutableRange(nint address, nuint size);
}

public interface ILocalEntityPools
{
    LocalDataResult<LocalPoolStatistics> GetStatistics(LocalPoolKinds kind);

    LocalDataResult<EntityPoolsSnapshot> Capture(LocalPoolKinds kinds);
}

public readonly record struct LocalEntityIdentityStatistics(
    ulong Requests,
    ulong SuccessCacheHits,
    ulong FailureCacheHits,
    ulong Resolved,
    ulong Failed,
    ulong AccessRejected,
    ulong QueueRejected,
    ulong CreateGuidUnavailable,
    ulong ScriptGuidPoolUnavailable,
    ulong ScriptGuidPoolRejected,
    ulong AdmissionRejected,
    ulong GuardRejected,
    ulong SessionStopping,
    ulong CallFailed,
    ulong ZeroHandle,
    int Pending);

public interface ILocalEntityIdentity
{
    LocalDataResult<int> TryGetHandle(nint entityAddress);
    LocalDataStatus RequestHandle(nint entityAddress);
    LocalEntityIdentityStatistics Statistics { get; }
}

public interface IPedLocalMemory
{
    LocalDataResult<PedLocalInvincibilityState> ReadInvincibility(
        int pedHandle);
}

public interface IVehicleLocalMemory
{
    LocalDataStatus SetTailBoomHealth(
        int vehicleHandle,
        float health);
}