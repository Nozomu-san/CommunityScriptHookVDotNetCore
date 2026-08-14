using System.Runtime.InteropServices;
using Alloc8orStandardNatives.Source;
using NativeEntity = Alloc8orStandardNatives.Source.Entity;

namespace LocalNativeMemories.Source;

internal enum TargetEntityKind
{
    Ped = 1,
    Vehicle = 2,
    Object = 3
}

internal sealed class TargetedEntityMemory(GameBuildInfo game) : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ulong GetScriptEntityDelegate(int handle);

    private readonly Lock _gate = new();
    private readonly EntityLocator _locator = new(game);
    private bool _disposed;

    internal LocalDataStatus WithPedAddress(
        PedHandle ped,
        Func<nint, int, LocalDataStatus> operation,
        string operationName)
    {
        ArgumentNullException.ThrowIfNull(operationName);
        ArgumentNullException.ThrowIfNull(operation);

        lock (_gate)
        {
            if (_disposed)
            {
                return LocalDataStatus.DataUnavailable;
            }

            LocalDataStatus status = TryResolveAddress(
                ped.Value,
                TargetEntityKind.Ped,
                out nint address,
                out int pedDamageFlagsOffset);

            if (status != LocalDataStatus.Success)
            {
                return status;
            }

            try
            {
                status = operation(address, pedDamageFlagsOffset);
            }
            catch
            {
                return LocalDataStatus.DataUnavailable;
            }

            if (status != LocalDataStatus.Success)
            {
                return status;
            }

            LocalDataStatus verification = TryResolveAddress(
                ped.Value,
                TargetEntityKind.Ped,
                out nint verifiedAddress,
                out _);

            return verification == LocalDataStatus.Success &&
                   verifiedAddress == address
                ? LocalDataStatus.Success
                : LocalDataStatus.VerificationFailed;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _locator.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private LocalDataStatus TryResolveAddress(
        int handle,
        TargetEntityKind expectedKind,
        out nint address,
        out int pedDamageFlagsOffset)
    {
        address = 0;
        pedDamageFlagsOffset = 0;
        if (handle == 0 || !HasExpectedKind(handle, expectedKind))
        {
            return LocalDataStatus.InvalidTarget;
        }

        LocalDataStatus status = _locator.TryResolve(
            out ResolvedEntityLocation location);
        if (status != LocalDataStatus.Success)
        {
            return status;
        }

        pedDamageFlagsOffset = location.PedDamageFlagsOffset;

        if (!MemoryAccess.TryGetDelegate<GetScriptEntityDelegate>(
                location.GetScriptEntity,
                out GetScriptEntityDelegate? resolver) ||
            resolver is null)
        {
            return LocalDataStatus.AccessRejected;
        }

        ulong rawAddress;
        try
        {
            rawAddress = resolver(handle);
        }
        catch
        {
            return LocalDataStatus.DataUnavailable;
        }

        address = unchecked((nint)rawAddress);
        if (address <= 0 ||
            !MemoryAccess.IsReadableRange(address, 0x30))
        {
            address = 0;
            return LocalDataStatus.AccessRejected;
        }

        if (!HasExpectedKind(handle, expectedKind))
        {
            address = 0;
            return LocalDataStatus.InvalidTarget;
        }

        return LocalDataStatus.Success;
    }

    private static bool HasExpectedKind(
        int handle,
        TargetEntityKind expectedKind)
    {
        if (handle == 0)
        {
            return false;
        }

        try
        {
            int observed = StandardNatives.GET_ENTITY_TYPE(
                new NativeEntity(handle));
            return observed == (int)expectedKind;
        }
        catch
        {
            return false;
        }
    }
}