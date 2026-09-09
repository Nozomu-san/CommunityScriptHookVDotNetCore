namespace LocalNativeMemories.Source;

internal readonly record struct PoolMemoryGuard(
    nint Address,
    ulong Mask,
    ulong Expected,
    int Width)
{
    internal bool IsConfigured =>
        Address != 0 &&
        Width is 1 or 2 or 4 or 8;

    internal static PoolMemoryGuard None { get; } =
        new(0, 0, 0, 0);
}

internal readonly record struct PoolSlotProbe(
    bool IsAllocated,
    nint Entity,
    PoolMemoryGuard PrimaryGuard,
    PoolMemoryGuard SecondaryGuard)
{
    internal static PoolSlotProbe Empty { get; } =
        new(false, 0, PoolMemoryGuard.None, PoolMemoryGuard.None);
}

internal readonly record struct FwPoolView(
    nint Pool,
    int Capacity,
    int ItemSize);

internal readonly record struct VehiclePoolView(
    nint Allocator,
    int Capacity);

internal sealed class PoolVerification(
    ILocalMemory memory,
    GameMemoryProfile profile)
{
    private const int MaximumPlausibleCapacity = 1_000_000;
    private const uint GuidSafetyReserve = 256;

    private readonly ILocalMemory _memory =
        memory ?? throw new ArgumentNullException(nameof(memory));
    private readonly GameMemoryProfile _profile =
        profile ?? throw new ArgumentNullException(nameof(profile));

    internal LocalDataResult<LocalPoolStatistics> ReadStatistics(
        LocalPoolKinds kind,
        nint address) =>
        kind switch
        {
            LocalPoolKinds.Peds or LocalPoolKinds.Objects =>
                ReadFwStatistics(address),
            LocalPoolKinds.Vehicles =>
                ReadVehicleStatistics(address),
            _ => LocalDataResult<LocalPoolStatistics>.Failed(
                LocalDataStatus.InvalidRequest)
        };

    internal LocalDataResult<FwPoolView> ReadFwPool(
        nint pool)
    {
        if (!TryReadFwShape(
                pool,
                out nint poolStart,
                out nint byteArray,
                out uint size,
                out uint itemSize,
                out _))
        {
            return LocalDataResult<FwPoolView>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        long extent;
        try
        {
            extent = checked((long)size * itemSize);
        }
        catch (OverflowException)
        {
            return LocalDataResult<FwPoolView>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        if (extent <= 0 ||
            !_memory.IsReadableRange(
                poolStart,
                checked((nuint)extent)) ||
            !_memory.IsReadableRange(
                byteArray,
                checked((nuint)size)))
        {
            return LocalDataResult<FwPoolView>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        return LocalDataResult<FwPoolView>.Succeeded(
            new(
                pool,
                checked((int)size),
                checked((int)itemSize)));
    }

    internal LocalDataResult<VehiclePoolView> ReadVehiclePool(
        nint allocator)
    {
        if (!TryReadVehicleShape(
                allocator,
                out nint poolAddress,
                out nint bitArray,
                out uint size,
                out _))
        {
            return LocalDataResult<VehiclePoolView>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        int pointerBytes;
        int wordCount;
        try
        {
            pointerBytes = checked((int)size * IntPtr.Size);
            wordCount = checked(((int)size + 31) / 32);
        }
        catch (OverflowException)
        {
            return LocalDataResult<VehiclePoolView>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        if (!_memory.IsReadableRange(
                poolAddress,
                checked((nuint)pointerBytes)) ||
            !_memory.IsReadableRange(
                bitArray,
                checked((nuint)(wordCount * sizeof(uint)))))
        {
            return LocalDataResult<VehiclePoolView>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        return LocalDataResult<VehiclePoolView>.Succeeded(
            new(allocator, checked((int)size)));
    }

    internal LocalDataResult<PoolSlotProbe> ProbeFwSlot(
        FwPoolView view,
        int index)
    {
        if (index < 0 || index >= view.Capacity)
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                LocalDataStatus.InvalidRequest);
        }

        if (!TryReadFwShape(
                view.Pool,
                out nint poolStart,
                out nint byteArray,
                out uint size,
                out uint itemSize,
                out _) ||
            size != checked((uint)view.Capacity) ||
            itemSize != checked((uint)view.ItemSize))
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        if (!MemoryAccess.TryAdd(
                byteArray,
                index,
                out nint allocationAddress))
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        LocalDataResult<byte> allocation =
            _memory.ReadByte(allocationAddress);
        if (!allocation.IsSuccess)
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                allocation.Status);
        }

        if ((allocation.Value & 0x80) != 0)
        {
            return LocalDataResult<PoolSlotProbe>.Succeeded(
                PoolSlotProbe.Empty);
        }

        long offset;
        try
        {
            offset = checked((long)index * view.ItemSize);
        }
        catch (OverflowException)
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        if (!MemoryAccess.TryAdd(
                poolStart,
                offset,
                out nint entity) ||
            !IsPlausibleEntityObject(entity))
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        return LocalDataResult<PoolSlotProbe>.Succeeded(
            new(
                true,
                entity,
                new(
                    allocationAddress,
                    byte.MaxValue,
                    allocation.Value,
                    sizeof(byte)),
                PoolMemoryGuard.None));
    }

    internal LocalDataResult<PoolSlotProbe> ProbeVehicleSlot(
        VehiclePoolView view,
        int index)
    {
        if (index < 0 || index >= view.Capacity)
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                LocalDataStatus.InvalidRequest);
        }

        if (!TryReadVehicleShape(
                view.Allocator,
                out nint poolAddress,
                out nint bitArray,
                out uint size,
                out _) ||
            size != checked((uint)view.Capacity))
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        int wordIndex = index >> 5;
        if (!MemoryAccess.TryAdd(
                bitArray,
                checked((long)wordIndex * sizeof(uint)),
                out nint allocationAddress))
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        LocalDataResult<uint> allocation =
            _memory.ReadUInt32(allocationAddress);
        if (!allocation.IsSuccess)
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                allocation.Status);
        }

        uint mask = 1u << (index & 31);
        if ((allocation.Value & mask) == 0)
        {
            return LocalDataResult<PoolSlotProbe>.Succeeded(
                PoolSlotProbe.Empty);
        }

        if (!MemoryAccess.TryAdd(
                poolAddress,
                checked((long)index * IntPtr.Size),
                out nint pointerAddress))
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        LocalDataResult<nint> pointer = _memory.ReadPointer(pointerAddress);
        if (!pointer.IsSuccess ||
            !IsPlausibleEntityObject(pointer.Value))
        {
            return LocalDataResult<PoolSlotProbe>.Failed(
                pointer.IsSuccess
                    ? LocalDataStatus.VerificationFailed
                    : pointer.Status);
        }

        ulong rawEntity = unchecked((ulong)(nuint)pointer.Value);
        return LocalDataResult<PoolSlotProbe>.Succeeded(
            new(
                true,
                pointer.Value,
                new(
                    allocationAddress,
                    mask,
                    mask,
                    sizeof(uint)),
                new(
                    pointerAddress,
                    ulong.MaxValue,
                    rawEntity,
                    IntPtr.Size)));
    }

    internal bool CanCreateScriptGuid(
        nint guidPool)
    {
        ScriptGuidPoolLayout layout = _profile.ScriptGuidPool;

        if (!TryReadUInt32(guidPool, layout.MaxCountOffset, out uint maxCount) ||
            !TryReadUInt32(guidPool, layout.ItemCountOffset, out uint rawItemCount))
        {
            return false;
        }

        uint itemCount = rawItemCount & 0x3FFFFFFF;
        return maxCount > itemCount &&
               maxCount - itemCount > GuidSafetyReserve;
    }

    internal static bool IsSnapshotPlausible(LocalPoolSnapshot snapshot)
    {
        if (!snapshot.Statistics.IsConsistent ||
            snapshot.Capacity <= 0 ||
            snapshot.Count < 0 ||
            snapshot.Count > snapshot.Capacity)
        {
            return false;
        }

        HashSet<int> seen = [];
        foreach (int handle in snapshot.Handles.Span)
        {
            if (handle == 0 || !seen.Add(handle))
            {
                return false;
            }
        }

        return true;
    }

    private LocalDataResult<LocalPoolStatistics> ReadFwStatistics(
        nint pool)
    {
        if (!TryReadFwShape(
                pool,
                out _,
                out _,
                out uint size,
                out _,
                out uint itemCount))
        {
            return LocalDataResult<LocalPoolStatistics>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        return LocalDataResult<LocalPoolStatistics>.Succeeded(
            new(checked((int)size), checked((int)itemCount)));
    }

    private LocalDataResult<LocalPoolStatistics> ReadVehicleStatistics(
        nint allocator)
    {
        if (!TryReadVehicleShape(
                allocator,
                out _,
                out _,
                out uint size,
                out uint itemCount))
        {
            return LocalDataResult<LocalPoolStatistics>.Failed(
                LocalDataStatus.VerificationFailed);
        }

        return LocalDataResult<LocalPoolStatistics>.Succeeded(
            new(checked((int)size), checked((int)itemCount)));
    }

    private bool TryReadFwShape(
        nint pool,
        out nint poolStart,
        out nint byteArray,
        out uint size,
        out uint itemSize,
        out uint itemCount)
    {
        poolStart = 0;
        byteArray = 0;
        size = 0;
        itemSize = 0;
        itemCount = 0;

        FwPoolLayout layout = _profile.FwPool;

        if (!TryReadPointer(pool, layout.PoolStartOffset, out poolStart) ||
            !TryReadPointer(pool, layout.ByteArrayOffset, out byteArray) ||
            !TryReadUInt32(pool, layout.SizeOffset, out size) ||
            !TryReadUInt32(pool, layout.ItemSizeOffset, out itemSize) ||
            !TryReadUInt16(pool, layout.ItemCountOffset, out ushort rawItemCount) ||
            !IsPlausiblePoolShape(size, itemSize, rawItemCount))
        {
            return false;
        }

        itemCount = rawItemCount;
        return true;
    }

    private bool TryReadVehicleShape(
        nint allocator,
        out nint poolAddress,
        out nint bitArray,
        out uint size,
        out uint itemCount)
    {
        poolAddress = 0;
        bitArray = 0;
        size = 0;
        itemCount = 0;

        VehiclePoolLayout layout = _profile.VehiclePool;

        return TryReadPointer(
                   allocator,
                   layout.PoolAddressOffset,
                   out poolAddress) &&
               TryReadUInt32(
                   allocator,
                   layout.SizeOffset,
                   out size) &&
               TryReadPointer(
                   allocator,
                   layout.BitArrayOffset,
                   out bitArray) &&
               TryReadUInt32(
                   allocator,
                   layout.ItemCountOffset,
                   out itemCount) &&
               IsPlausiblePoolShape(
                   size,
                   (uint)IntPtr.Size,
                   itemCount);
    }

    private bool IsPlausibleEntityObject(nint entity)
    {
        if (entity <= 0 ||
            !_memory.IsReadableRange(
                entity,
                checked((nuint)IntPtr.Size)))
        {
            return false;
        }

        LocalDataResult<nint> virtualTable =
            _memory.ReadPointer(entity);
        return virtualTable.IsSuccess &&
               virtualTable.Value > 0 &&
               _memory.IsExecutableRange(
                   virtualTable.Value,
                   1);
    }

    private static bool IsPlausiblePoolShape(
        uint size,
        uint itemSize,
        uint itemCount) =>
        IsPlausibleCapacity(size) &&
        itemSize != 0 &&
        itemCount <= size;

    private static bool IsPlausibleCapacity(uint size) =>
        size != 0 && size <= MaximumPlausibleCapacity;

    private bool TryReadPointer(
        nint baseAddress,
        int offset,
        out nint value)
    {
        value = 0;
        LocalDataResult<nint> address = _memory.Add(baseAddress, offset);
        if (!address.IsSuccess)
        {
            return false;
        }

        LocalDataResult<nint> result = _memory.ReadPointer(address.Value);
        if (!result.IsSuccess)
        {
            return false;
        }

        value = result.Value;
        return value > 0;
    }

    private bool TryReadUInt16(
        nint baseAddress,
        int offset,
        out ushort value)
    {
        value = 0;
        LocalDataResult<nint> address = _memory.Add(baseAddress, offset);
        if (!address.IsSuccess)
        {
            return false;
        }

        LocalDataResult<ushort> result = _memory.ReadUInt16(address.Value);
        if (!result.IsSuccess)
        {
            return false;
        }

        value = result.Value;
        return true;
    }

    private bool TryReadUInt32(
        nint baseAddress,
        int offset,
        out uint value)
    {
        value = 0;
        LocalDataResult<nint> address = _memory.Add(baseAddress, offset);
        if (!address.IsSuccess)
        {
            return false;
        }

        LocalDataResult<uint> result = _memory.ReadUInt32(address.Value);
        if (!result.IsSuccess)
        {
            return false;
        }

        value = result.Value;
        return true;
    }
}