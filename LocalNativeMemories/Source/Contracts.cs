namespace LocalNativeMemories.Source;

public enum GameProcessFamily
{
    Unknown,
    Legacy,
    Enhanced
}

public enum LocalDataStatus
{
    Success,
    InvalidRequest,
    InvalidTarget,
    UnsupportedProcess,
    DataUnavailable,
    AccessRejected,
    VerificationFailed
}

public readonly record struct LocalDataResult<T>(
    LocalDataStatus Status,
    T Value)
{
    public bool IsSuccess => Status == LocalDataStatus.Success;

    internal static LocalDataResult<T> Succeeded(T value) =>
        new(LocalDataStatus.Success, value);

    internal static LocalDataResult<T> Failed(LocalDataStatus status) =>
        new(status, default!);
}

public readonly record struct PedHandle(int Value);

public readonly record struct PedInvincibilityState(
    bool Standard,
    bool Reactive)
{
    public bool IsInvincible => Standard || Reactive;
}

public interface IPedInvincibilityMemory
{
    LocalDataResult<PedInvincibilityState> Read(PedHandle ped);
}