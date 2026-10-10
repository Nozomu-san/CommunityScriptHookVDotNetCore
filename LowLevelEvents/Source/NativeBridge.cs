#pragma warning disable SYSLIB1054
#pragma warning disable CA2101

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LowLevelEvents.Source;

internal sealed class NativeBridge : IDisposable
{
    private const string ModuleName = "CEventGenerator.asi";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);

    private enum LocatorFailure : uint
    {
        None = 0,
        ImageUnavailable = 1,
        PedMatchCount = 2,
        GlobalMatchCount = 3,
        DamageMatchCount = 4,
        TargetInvalid = 5,
        EnhancedCEventCountAnchor = 6,
        EnhancedCEventStackAnchor = 7,
        EnhancedEventTypeAnchor = 8,
        EnhancedStructureInvalid = 9,
        EnhancedDispatchUnresolved = 10,
        EditionUnsupported = 11
    }

    private enum GameEdition : uint
    {
        Unknown = 0,
        Legacy = 1,
        Enhanced = 2
    }

    [Flags]
    private enum EnhancedCandidateRelations : uint
    {
        None = 0,
        CallsCount = 1 << 0,
        CallsEventType = 1 << 1,
        CalledByCount = 1 << 2,
        CalledByEventType = 1 << 3,
        SharesCallerWithCount = 1 << 4,
        SharesCallerWithEventType = 1 << 5,
        IsCountFunction = 1 << 6,
        IsEventTypeFunction = 1 << 7,
        EventObjectFlow = 1 << 8,
        IterationLoop = 1 << 9,
        AddressTaken = 1 << 10
    }

    [Flags]
    private enum DispatchCalleeRelations : uint
    {
        None = 0,
        EventArgumentRdx = 1 << 0,
        FirstArgumentPrepared = 1 << 1,
        AfterEventTypeCall = 1 << 2,
        MultipleCallsites = 1 << 3,
        MultipleWalkers = 1 << 4,
        IncomingGroupRead = 1 << 5,
        IncomingEventRead = 1 << 6,
        EventVtableLoaded = 1 << 7,
        CallsEventTypeVirtual = 1 << 8,
        CallsEventArgumentsVirtual = 1 << 9,
        CallsEventEntityVirtual = 1 << 10,
        EventSpilledToStack = 1 << 11,
        EventReloadedFromStack = 1 << 12,
        GroupSpilledToStack = 1 << 13,
        GroupReloadedFromStack = 1 << 14,
        ForwardsEvent = 1 << 15,
        ForwardsGroup = 1 << 16
    }

    [Flags]
    private enum EventConsumerRelations : uint
    {
        None = 0,
        Read = 1 << 0,
        MemoryStore = 1 << 1,
        Compare = 1 << 2,
        VtableLoaded = 1 << 3,
        Forward = 1 << 4,
        TailForward = 1 << 5,
        SpilledToStack = 1 << 6,
        ReloadedFromStack = 1 << 7,
        MultipleCallsites = 1 << 8,
        MultipleWalkers = 1 << 9,
        UseBeforeClobber = 1 << 10,
        RecoveredLeaf = 1 << 11,
        TailChainResolved = 1 << 12,
        TailChainCycle = 1 << 13,
        TailChainDepthLimit = 1 << 14,
        TailChainUnresolved = 1 << 15,
        IndirectTailResolved = 1 << 16,
        RegisterTailResolved = 1 << 17,
        IndirectTailRegister = 1 << 18,
        IndirectTailMemory = 1 << 19,
        MemoryTailEventOperand = 1 << 20,
        MemoryTailVtableOperand = 1 << 21,
        MemoryTailIndexed = 1 << 22,
        MemoryTailResolved = 1 << 23,
        SystemImportThunk = 1 << 24,
        GameLocalConsumer = 1 << 25
    }

    [Flags]
    private enum EventConsumerCallsiteRelations : uint
    {
        None = 0,
        Direct = 1 << 0,
        Tail = 1 << 1,
        Indirect = 1 << 2,
        Unresolved = 1 << 3,
        Register = 1 << 4,
        Memory = 1 << 5
    }

    [Flags]
    private enum TailSlotFlags : uint
    {
        None = 0,
        RipRelative = 1 << 0,
        InImage = 1 << 1,
        Readable = 1 << 2,
        InIat = 1 << 3
    }

    [Flags]
    private enum TailTargetFlags : uint
    {
        None = 0,
        Readable = 1 << 0,
        Executable = 1 << 1,
        InMainImage = 1 << 2,
        ModuleResolved = 1 << 3,
        OtherModule = 1 << 4
    }

    [Flags]
    private enum VirtualSlotRelations : uint
    {
        None = 0,
        VtableProvenance = 1 << 0,
        EventInRcx = 1 << 1,
        EventType = 1 << 2,
        EventArguments = 1 << 3,
        EventEntity = 1 << 4
    }

    [Flags]
    private enum VirtualReturnRelations : uint
    {
        None = 0,
        Used = 1 << 0,
        Compare = 1 << 1,
        Dereference = 1 << 2,
        MemoryStore = 1 << 3,
        Forward = 1 << 4
    }

    private readonly nint _buffer;
    private readonly GameEventDescriptor[] _catalog;
    private readonly Dictionary<string, GameEventDescriptor> _catalogByName;
    private bool _disposed;

    private NativeBridge()
    {
        uint abiVersion = GetAbiVersion();
        if (abiVersion != NativeAbi.BridgeAbiVersion)
        {
            throw new InvalidOperationException(
                $"CEventGenerator ABI version {abiVersion} is incompatible with LowLevelEvents ABI version {NativeAbi.BridgeAbiVersion}.");
        }

        uint capabilities = GetCapabilities();
        uint missingCapabilities =
            NativeAbi.RequiredBridgeCapabilities & ~capabilities;
        if (missingCapabilities != 0)
        {
            throw new InvalidOperationException(
                $"CEventGenerator is missing required bridge capabilities 0x{missingCapabilities:X8}; exposed=0x{capabilities:X8}.");
        }

        if (GetRecordSize() != NativeAbi.RecordSize)
        {
            throw new InvalidOperationException(
                "CEventGenerator event-record size is incompatible with LowLevelEvents.");
        }

        _catalog = LoadCatalog();
        _catalogByName = _catalog.ToDictionary(
            descriptor => descriptor.Name,
            StringComparer.Ordinal);
        _buffer = Marshal.AllocHGlobal(NativeAbi.RecordSize);
    }

    internal static uint GameBuild => GetGameBuild();
    internal static ulong PerformanceFrequency => GetPerformanceFrequency();
    internal static ulong PendingCount => GetPendingCount();
    internal static ulong DroppedCount => GetDroppedCount();
    internal static ulong ObservedCount(uint eventId) => GetObservedCount(eventId);
    internal static LowLevelEventBridgeStatus Status =>
        (LowLevelEventBridgeStatus)GetStatus();
    internal nint Buffer => _buffer;
    internal IReadOnlyList<GameEventDescriptor> Catalog => _catalog;

    internal bool TryResolveCatalogEvent(
        string eventName,
        out GameEventDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return _catalogByName.TryGetValue(eventName, out descriptor);
    }


    internal string? ResolveCatalogEventName(uint catalogEventId)
    {
        if (catalogEventId == 0 || catalogEventId > _catalog.Length)
        {
            return null;
        }

        return _catalog[checked((int)catalogEventId - 1)].Name;
    }

    private static GameEventDescriptor[] LoadCatalog()
    {
        uint count = GetCatalogEventCount();
        if (count == 0 || count > 4096)
        {
            throw new InvalidOperationException(
                $"CEventGenerator exposed an invalid game-event catalog size: {count}.");
        }

        int catalogCount = checked((int)count);
        GameEventDescriptor[] catalog = new GameEventDescriptor[catalogCount];
        for (uint id = 1; id <= count; ++id)
        {
            StringBuilder name = new(256);
            uint required = GetCatalogEventName(id, name, 256);
            if (required == 0 || required > 256 || name.Length == 0)
            {
                throw new InvalidOperationException(
                    $"CEventGenerator exposed an invalid catalog entry at id {id}.");
            }

            catalog[checked((int)id - 1)] = new(id, name.ToString());
        }

        return catalog;
    }


    private static string BuildEnhancedCandidateDiagnostics()
    {
        uint count = GetEnhancedCandidateDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }

        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (uint index = 0; index < count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }

            diagnostics
                .Append("{Rva=0x")
                .Append(GetEnhancedCandidateRva(index).ToString("X"))
                .Append(";Size=0x")
                .Append(GetEnhancedCandidateSize(index).ToString("X"))
                .Append(";Relations=")
                .Append((EnhancedCandidateRelations)GetEnhancedCandidateRelations(index))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }

    private static string BuildDispatchCallsiteDiagnostics()
    {
        uint count = GetDispatchCallsiteDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }

        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (uint index = 0; index < count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }

            diagnostics
                .Append("{WalkerRva=0x")
                .Append(GetDispatchCallsiteWalkerRva(index).ToString("X"))
                .Append(";CallsiteRva=0x")
                .Append(GetDispatchCallsiteRva(index).ToString("X"))
                .Append(";CalleeRva=0x")
                .Append(GetDispatchCallsiteCalleeRva(index).ToString("X"))
                .Append(";Relations=")
                .Append((DispatchCalleeRelations)GetDispatchCallsiteRelations(index))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }


    private static string BuildDispatchCalleeDiagnostics()
    {
        uint count = GetDispatchCalleeDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }

        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (uint index = 0; index < count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }

            diagnostics
                .Append("{Rva=0x")
                .Append(GetDispatchCalleeRva(index).ToString("X"))
                .Append(";Size=0x")
                .Append(GetDispatchCalleeSize(index).ToString("X"))
                .Append(";Relations=")
                .Append((DispatchCalleeRelations)GetDispatchCalleeRelations(index))
                .Append(";Callsites=")
                .Append(GetDispatchCalleeCallsiteCount(index))
                .Append(";Walkers=")
                .Append(GetDispatchCalleeWalkerCount(index))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }


    private static string FormatDispatchArgumentMask(uint mask)
    {
        if (mask == 0)
        {
            return "None";
        }

        StringBuilder value = new();
        ReadOnlySpan<string> names = ["RCX", "RDX", "R8", "R9"];
        for (int index = 0; index < names.Length; ++index)
        {
            if ((mask & (1u << index)) == 0)
            {
                continue;
            }

            if (value.Length != 0)
            {
                value.Append('|');
            }
            value.Append(names[index]);
        }
        return value.ToString();
    }

    private static string BuildDispatchForwardCallsiteDiagnostics()
    {
        uint count = GetDispatchForwardCallsiteDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }

        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (uint index = 0; index < count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }

            diagnostics
                .Append("{ParentRva=0x")
                .Append(GetDispatchForwardCallsiteParentRva(index).ToString("X"))
                .Append(";CallsiteRva=0x")
                .Append(GetDispatchForwardCallsiteRva(index).ToString("X"))
                .Append(";CalleeRva=0x")
                .Append(GetDispatchForwardCallsiteCalleeRva(index).ToString("X"))
                .Append(";EventArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetDispatchForwardCallsiteEventArgumentMask(index)))
                .Append(";GroupArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetDispatchForwardCallsiteGroupArgumentMask(index)))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }

    private static string BuildDispatchForwardCalleeDiagnostics()
    {
        uint count = GetDispatchForwardCalleeDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }

        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (uint index = 0; index < count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }

            diagnostics
                .Append("{Rva=0x")
                .Append(GetDispatchForwardCalleeRva(index).ToString("X"))
                .Append(";Size=0x")
                .Append(GetDispatchForwardCalleeSize(index).ToString("X"))
                .Append(";Relations=")
                .Append((DispatchCalleeRelations)GetDispatchForwardCalleeRelations(index))
                .Append(";Callsites=")
                .Append(GetDispatchForwardCalleeCallsiteCount(index))
                .Append(";Parents=")
                .Append(GetDispatchForwardCalleeParentCount(index))
                .Append(";EventArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetDispatchForwardCalleeEventArgumentMask(index)))
                .Append(";GroupArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetDispatchForwardCalleeGroupArgumentMask(index)))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }


    private static string FormatEncoding(
        uint length,
        uint word0,
        uint word1,
        uint word2,
        uint word3)
    {
        Span<uint> words = [word0, word1, word2, word3];
        StringBuilder value = new();
        for (uint index = 0; index < length && index < 15; ++index)
        {
            uint word = words[(int)(index / 4)];
            byte current = (byte)(word >> ((int)(index % 4) * 8));
            value.Append(current.ToString("X2"));
        }
        return value.ToString();
    }

    private static string FormatRegister(uint register) =>
        register == uint.MaxValue ? "None" : $"R{register}";

    private static string BuildEventConsumerCallsiteDiagnostics()
    {
        uint count = GetEventConsumerCallsiteDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }

        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (uint index = 0; index < count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }

            diagnostics
                .Append("{WalkerRva=0x")
                .Append(GetEventConsumerCallsiteWalkerRva(index).ToString("X"))
                .Append(";CallsiteRva=0x")
                .Append(GetEventConsumerCallsiteRva(index).ToString("X"))
                .Append(";CalleeRva=0x")
                .Append(GetEventConsumerCallsiteCalleeRva(index).ToString("X"))
                .Append(";EventArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetEventConsumerCallsiteEventArgumentMask(index)))
                .Append(";Relations=")
                .Append((EventConsumerCallsiteRelations)GetEventConsumerCallsiteRelations(index))
                .Append(";Bytes=")
                .Append(FormatEncoding(
                    GetEventConsumerCallsiteInstructionLength(index),
                    GetEventConsumerCallsiteEncoding(index, 0),
                    GetEventConsumerCallsiteEncoding(index, 1),
                    GetEventConsumerCallsiteEncoding(index, 2),
                    GetEventConsumerCallsiteEncoding(index, 3)))
                .Append(";Operand=0x")
                .Append(GetEventConsumerCallsiteOperandInfo(index).ToString("X8"))
                .Append(";Disp=0x")
                .Append(GetEventConsumerCallsiteDisplacement(index).ToString("X8"))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }

    private static string ResolveModuleName(ulong moduleBase)
    {
        if (moduleBase == 0)
        {
            return "None";
        }
        try
        {
            using Process process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if ((ulong)(nuint)module.BaseAddress == moduleBase)
                {
                    return module.ModuleName ?? $"0x{moduleBase:X}";
                }
            }
        }
        catch
        {
        }
        return $"0x{moduleBase:X}";
    }

    private static bool IsImageRange(int imageSize, uint rva, uint length) =>
        imageSize > 0 && (ulong)rva + length <= (ulong)(uint)imageSize;

    private static bool TryReadUInt16(
        nint imageBase,
        int imageSize,
        uint rva,
        out ushort value)
    {
        value = 0;
        if (!IsImageRange(imageSize, rva, sizeof(ushort)))
        {
            return false;
        }
        value = unchecked((ushort)Marshal.ReadInt16(imageBase, checked((int)rva)));
        return true;
    }

    private static bool TryReadUInt32(
        nint imageBase,
        int imageSize,
        uint rva,
        out uint value)
    {
        value = 0;
        if (!IsImageRange(imageSize, rva, sizeof(uint)))
        {
            return false;
        }
        value = unchecked((uint)Marshal.ReadInt32(imageBase, checked((int)rva)));
        return true;
    }

    private static bool TryReadUInt64(
        nint imageBase,
        int imageSize,
        uint rva,
        out ulong value)
    {
        value = 0;
        if (!IsImageRange(imageSize, rva, sizeof(ulong)))
        {
            return false;
        }
        value = unchecked((ulong)Marshal.ReadInt64(imageBase, checked((int)rva)));
        return true;
    }

    private static bool TryReadAscii(
        nint imageBase,
        int imageSize,
        uint rva,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (maximumLength <= 0 || !IsImageRange(imageSize, rva, 1))
        {
            return false;
        }
        StringBuilder builder = new();
        int available = Math.Min(maximumLength, imageSize - checked((int)rva));
        for (int index = 0; index < available; ++index)
        {
            byte current = Marshal.ReadByte(imageBase, checked((int)rva + index));
            if (current == 0)
            {
                value = builder.ToString();
                return value.Length != 0;
            }
            if (current is < 0x20 or > 0x7E)
            {
                return false;
            }
            builder.Append((char)current);
        }
        return false;
    }

    private static bool TryGetPeDataDirectory(
        nint imageBase,
        int imageSize,
        int directoryIndex,
        out uint directoryRva,
        out uint directorySize)
    {
        directoryRva = 0;
        directorySize = 0;
        if (directoryIndex < 0 || directoryIndex >= 16 ||
            !TryReadUInt16(imageBase, imageSize, 0, out ushort dosMagic) ||
            dosMagic != 0x5A4D ||
            !TryReadUInt32(imageBase, imageSize, 0x3C, out uint ntRva) ||
            !TryReadUInt32(imageBase, imageSize, ntRva, out uint signature) ||
            signature != 0x00004550)
        {
            return false;
        }
        uint optionalHeaderRva = ntRva + 24u;
        if (!TryReadUInt16(
                imageBase,
                imageSize,
                optionalHeaderRva,
                out ushort optionalMagic))
        {
            return false;
        }
        uint dataDirectoryOffset = optionalMagic switch
        {
            0x20B => 112u,
            0x10B => 96u,
            _ => 0u
        };
        if (dataDirectoryOffset == 0)
        {
            return false;
        }
        uint entryRva = optionalHeaderRva + dataDirectoryOffset +
            checked((uint)directoryIndex * 8u);
        return TryReadUInt32(imageBase, imageSize, entryRva, out directoryRva) &&
            TryReadUInt32(imageBase, imageSize, entryRva + 4u, out directorySize) &&
            directoryRva != 0 && directorySize != 0 &&
            IsImageRange(imageSize, directoryRva, Math.Min(directorySize, 1u));
    }

    private static bool TryResolveProcessModule(
        ulong moduleBase,
        out nint baseAddress,
        out int imageSize,
        out string moduleName)
    {
        baseAddress = 0;
        imageSize = 0;
        moduleName = "None";
        if (moduleBase == 0)
        {
            return false;
        }
        try
        {
            using Process process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if ((ulong)(nuint)module.BaseAddress != moduleBase)
                {
                    continue;
                }
                baseAddress = module.BaseAddress;
                imageSize = module.ModuleMemorySize;
                moduleName = module.ModuleName ?? $"0x{moduleBase:X}";
                return imageSize > 0;
            }
        }
        catch
        {
        }
        return false;
    }

    private static string ResolveImportIdentity(
        uint slotRva,
        ulong targetAddress)
    {
        if (slotRva == uint.MaxValue)
        {
            return "None";
        }
        try
        {
            using Process process = Process.GetCurrentProcess();
            ProcessModule? mainModule = process.MainModule;
            if (mainModule is null || mainModule.ModuleMemorySize <= 0)
            {
                return "Unavailable";
            }
            nint imageBase = mainModule.BaseAddress;
            int imageSize = mainModule.ModuleMemorySize;
            if (!TryGetPeDataDirectory(
                    imageBase,
                    imageSize,
                    1,
                    out uint importRva,
                    out uint importSize))
            {
                return "NoImportDirectory";
            }
            const int descriptorSize = 20;
            uint descriptorCount = Math.Min(
                importSize / descriptorSize,
                4096u);
            for (uint descriptorIndex = 0;
                 descriptorIndex < descriptorCount;
                 ++descriptorIndex)
            {
                uint descriptorRva = importRva + descriptorIndex * descriptorSize;
                if (!TryReadUInt32(imageBase, imageSize, descriptorRva, out uint originalFirstThunk) ||
                    !TryReadUInt32(imageBase, imageSize, descriptorRva + 12u, out uint nameRva) ||
                    !TryReadUInt32(imageBase, imageSize, descriptorRva + 16u, out uint firstThunk))
                {
                    break;
                }
                if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
                {
                    break;
                }
                if (firstThunk == 0 || slotRva < firstThunk)
                {
                    continue;
                }
                uint delta = slotRva - firstThunk;
                if ((delta & 7u) != 0)
                {
                    continue;
                }
                uint thunkIndex = delta / 8u;
                if (thunkIndex > 65535u)
                {
                    continue;
                }
                uint lookupTable = originalFirstThunk != 0
                    ? originalFirstThunk
                    : firstThunk;
                bool validIndex = true;
                ulong lookupValue = 0;
                for (uint index = 0; index <= thunkIndex; ++index)
                {
                    uint thunkRva = lookupTable + index * 8u;
                    if (!TryReadUInt64(
                            imageBase,
                            imageSize,
                            thunkRva,
                            out lookupValue) ||
                        lookupValue == 0)
                    {
                        validIndex = false;
                        break;
                    }
                }
                if (!validIndex)
                {
                    continue;
                }
                if (!TryReadUInt64(
                        imageBase,
                        imageSize,
                        slotRva,
                        out ulong loadedTarget))
                {
                    loadedTarget = 0;
                }
                string dllName = TryReadAscii(
                    imageBase,
                    imageSize,
                    nameRva,
                    260,
                    out string resolvedDll)
                    ? resolvedDll
                    : "Unknown";
                bool targetMatches = targetAddress == 0 || loadedTarget == targetAddress;
                if (originalFirstThunk == 0)
                {
                    return $"{{Dll={dllName};Name=Unavailable;Ordinal=0;Hint=0;IatMatchesTarget={targetMatches}}}";
                }
                const ulong ordinalFlag = 0x8000000000000000UL;
                if ((lookupValue & ordinalFlag) != 0)
                {
                    ushort ordinal = (ushort)(lookupValue & 0xFFFFu);
                    return $"{{Dll={dllName};Name=None;Ordinal={ordinal};Hint=0;IatMatchesTarget={targetMatches}}}";
                }
                if (lookupValue > uint.MaxValue)
                {
                    return $"{{Dll={dllName};Name=InvalidLookupRva;Ordinal=0;Hint=0;IatMatchesTarget={targetMatches}}}";
                }
                uint importByNameRva = (uint)lookupValue;
                TryReadUInt16(
                    imageBase,
                    imageSize,
                    importByNameRva,
                    out ushort hint);
                string importName = TryReadAscii(
                    imageBase,
                    imageSize,
                    importByNameRva + 2u,
                    512,
                    out string resolvedName)
                    ? resolvedName
                    : "Unknown";
                return $"{{Dll={dllName};Name={importName};Ordinal=0;Hint={hint};IatMatchesTarget={targetMatches}}}";
            }
        }
        catch
        {
        }
        return "Unresolved";
    }

    private static string ResolveExportIdentity(
        ulong moduleBase,
        uint targetRva)
    {
        if (!TryResolveProcessModule(
                moduleBase,
                out nint imageBase,
                out int imageSize,
                out string moduleName))
        {
            return "Unavailable";
        }
        if (!TryGetPeDataDirectory(
                imageBase,
                imageSize,
                0,
                out uint exportRva,
                out uint exportSize))
        {
            return $"{{Module={moduleName};Name=None;Ordinal=0;Forwarded=False}}";
        }
        if (!IsImageRange(imageSize, exportRva, 40))
        {
            return "InvalidExportDirectory";
        }
        if (!TryReadUInt32(imageBase, imageSize, exportRva + 16u, out uint ordinalBase) ||
            !TryReadUInt32(imageBase, imageSize, exportRva + 20u, out uint functionCount) ||
            !TryReadUInt32(imageBase, imageSize, exportRva + 24u, out uint nameCount) ||
            !TryReadUInt32(imageBase, imageSize, exportRva + 28u, out uint addressTableRva) ||
            !TryReadUInt32(imageBase, imageSize, exportRva + 32u, out uint nameTableRva) ||
            !TryReadUInt32(imageBase, imageSize, exportRva + 36u, out uint ordinalTableRva))
        {
            return "InvalidExportDirectory";
        }
        functionCount = Math.Min(functionCount, 1_000_000u);
        uint functionIndex = uint.MaxValue;
        for (uint index = 0; index < functionCount; ++index)
        {
            if (!TryReadUInt32(
                    imageBase,
                    imageSize,
                    addressTableRva + index * 4u,
                    out uint functionRva))
            {
                break;
            }
            if (functionRva == targetRva)
            {
                functionIndex = index;
                break;
            }
        }
        if (functionIndex == uint.MaxValue)
        {
            return $"{{Module={moduleName};Name=None;Ordinal=0;Forwarded=False}}";
        }
        uint ordinal = ordinalBase + functionIndex;
        string exportName = "None";
        nameCount = Math.Min(nameCount, 1_000_000u);
        for (uint index = 0; index < nameCount; ++index)
        {
            if (!TryReadUInt16(
                    imageBase,
                    imageSize,
                    ordinalTableRva + index * 2u,
                    out ushort nameOrdinal) ||
                nameOrdinal != functionIndex)
            {
                continue;
            }
            if (TryReadUInt32(
                    imageBase,
                    imageSize,
                    nameTableRva + index * 4u,
                    out uint nameRva) &&
                TryReadAscii(
                    imageBase,
                    imageSize,
                    nameRva,
                    512,
                    out string resolvedName))
            {
                exportName = resolvedName;
            }
            break;
        }
        bool forwarded = targetRva >= exportRva &&
            (ulong)targetRva < (ulong)exportRva + exportSize;
        return $"{{Module={moduleName};Name={exportName};Ordinal={ordinal};Forwarded={forwarded}}}";
    }

    private static string BuildConvergedConsumerRanking()
    {
        uint count = GetEventConsumerDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }
        List<(uint Rva, int Score, string Disposition)> values = [];
        for (uint index = 0; index < count; ++index)
        {
            EventConsumerRelations relations =
                (EventConsumerRelations)GetEventConsumerRelations(index);
            TailSlotFlags slotFlags =
                (TailSlotFlags)GetEventConsumerTailSlotFlags(index);
            TailTargetFlags targetFlags =
                (TailTargetFlags)GetEventConsumerTailTargetFlags(index);
            if (relations.HasFlag(EventConsumerRelations.SystemImportThunk) ||
                slotFlags.HasFlag(TailSlotFlags.InIat) &&
                targetFlags.HasFlag(TailTargetFlags.OtherModule))
            {
                values.Add((GetEventConsumerRva(index), -100, "SystemImportThunk"));
                continue;
            }
            int score = 0;
            score += checked((int)Math.Min(GetEventConsumerReadCount(index), 8u) * 3);
            score += checked((int)Math.Min(GetEventConsumerMemoryStoreCount(index), 8u) * 2);
            score += checked((int)Math.Min(GetEventConsumerCompareCount(index), 8u));
            score += checked((int)Math.Min(GetEventConsumerVtableLoadCount(index), 8u) * 5);
            score += checked((int)Math.Min(GetEventConsumerForwardCount(index), 8u) * 4);
            score += checked((int)Math.Min(GetEventConsumerWalkerCount(index), 8u) * 2);
            if (relations.HasFlag(EventConsumerRelations.UseBeforeClobber))
            {
                score += 3;
            }
            string disposition = relations.HasFlag(EventConsumerRelations.GameLocalConsumer)
                ? "GameLocal"
                : "Unclassified";
            values.Add((GetEventConsumerRva(index), score, disposition));
        }
        values.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (int index = 0; index < values.Count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }
            diagnostics
                .Append("{Rva=0x")
                .Append(values[index].Rva.ToString("X"))
                .Append(";Score=")
                .Append(values[index].Score)
                .Append(";Disposition=")
                .Append(values[index].Disposition)
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }

    private static string BuildVirtualSemanticRanking()
    {
        List<(uint Slot, int Score, VirtualReturnRelations Relations, uint Uses)> ranking = [];
        uint count = GetVirtualSlotDiagnosticCount();
        for (uint index = 0; index < count; ++index)
        {
            uint slot = GetVirtualSlotOffset(index);
            VirtualReturnRelations relations =
                (VirtualReturnRelations)GetVirtualSlotReturnRelations(index);
            uint uses = GetVirtualSlotReturnUseCount(index);
            int score = checked((int)uses * 2);
            if ((relations & VirtualReturnRelations.Dereference) != 0)
            {
                score += 8;
            }
            if ((relations & VirtualReturnRelations.Forward) != 0)
            {
                score += 6;
            }
            if ((relations & VirtualReturnRelations.MemoryStore) != 0)
            {
                score += 4;
            }
            if ((relations & VirtualReturnRelations.Compare) != 0)
            {
                score += 2;
            }
            if (((VirtualSlotRelations)GetVirtualSlotRelations(index) &
                 VirtualSlotRelations.EventType) != 0)
            {
                score -= 100;
            }
            ranking.Add((slot, score, relations, uses));
        }
        ranking.Sort(static (left, right) =>
        {
            int score = right.Score.CompareTo(left.Score);
            return score != 0 ? score : left.Slot.CompareTo(right.Slot);
        });
        if (ranking.Count == 0)
        {
            return "[]";
        }
        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (int index = 0; index < ranking.Count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }
            var (Slot, Score, Relations, Uses) = ranking[index];
            diagnostics
                .Append("{Slot=0x")
                .Append(Slot.ToString("X"))
                .Append(";Score=")
                .Append(Score)
                .Append(";Return=")
                .Append(Relations)
                .Append(";Uses=")
                .Append(Uses)
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }

    private static string BuildVirtualInputRanking()
    {
        Dictionary<uint, (int Score, uint Callsites, uint Prepared, uint Memory, uint Address, uint Immediate, uint Zero)> values = [];
        uint count = GetVirtualSlotCallsiteDiagnosticCount();
        uint eventType = GetEventTypeVFuncOffset();
        for (uint index = 0; index < count; ++index)
        {
            uint slot = GetVirtualSlotCallsiteOffset(index);
            uint eventArguments =
                GetVirtualSlotCallsiteEventArgumentMask(index);
            uint prepared =
                GetVirtualSlotCallsitePreparedArgumentMask(index) & 0xFu;
            uint nonEventPrepared = prepared & ~eventArguments & 0xFu;
            uint memory =
                GetVirtualSlotCallsiteMemoryArgumentMask(index) &
                nonEventPrepared;
            uint address =
                GetVirtualSlotCallsiteAddressArgumentMask(index) &
                nonEventPrepared;
            uint immediate =
                GetVirtualSlotCallsiteImmediateArgumentMask(index) &
                nonEventPrepared;
            uint zero =
                GetVirtualSlotCallsiteZeroArgumentMask(index) &
                nonEventPrepared;

            int score = 0;
            if (nonEventPrepared != 0)
            {
                score += 4;
            }
            if (memory != 0)
            {
                score += 6;
            }
            if (address != 0)
            {
                score += 5;
            }
            if (immediate != 0)
            {
                score += 2;
            }
            if (zero != 0)
            {
                score += 1;
            }
            if (slot == eventType)
            {
                score -= 100;
            }

            if (!values.TryGetValue(slot, out var current))
            {
                current = default;
            }
            current.Score += score;
            if (nonEventPrepared != 0)
            {
                ++current.Callsites;
            }
            current.Prepared |= nonEventPrepared;
            current.Memory |= memory;
            current.Address |= address;
            current.Immediate |= immediate;
            current.Zero |= zero;
            values[slot] = current;
        }

        List<(uint Slot, int Score, uint Callsites, uint Prepared, uint Memory, uint Address, uint Immediate, uint Zero)> ranking = [];
        foreach (KeyValuePair<uint, (int Score, uint Callsites, uint Prepared, uint Memory, uint Address, uint Immediate, uint Zero)> pair in values)
        {
            ranking.Add((
                pair.Key,
                pair.Value.Score,
                pair.Value.Callsites,
                pair.Value.Prepared,
                pair.Value.Memory,
                pair.Value.Address,
                pair.Value.Immediate,
                pair.Value.Zero));
        }
        ranking.Sort(static (left, right) =>
        {
            int score = right.Score.CompareTo(left.Score);
            return score != 0 ? score : left.Slot.CompareTo(right.Slot);
        });
        if (ranking.Count == 0)
        {
            return "[]";
        }

        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (int index = 0; index < ranking.Count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }
            var candidate = ranking[index];
            diagnostics
                .Append("{Slot=0x")
                .Append(candidate.Slot.ToString("X"))
                .Append(";Score=")
                .Append(candidate.Score)
                .Append(";InputCallsites=")
                .Append(candidate.Callsites)
                .Append(";Prepared=")
                .Append(FormatDispatchArgumentMask(candidate.Prepared))
                .Append(";Memory=")
                .Append(FormatDispatchArgumentMask(candidate.Memory))
                .Append(";Address=")
                .Append(FormatDispatchArgumentMask(candidate.Address))
                .Append(";Immediate=")
                .Append(FormatDispatchArgumentMask(candidate.Immediate))
                .Append(";Zero=")
                .Append(FormatDispatchArgumentMask(candidate.Zero))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }

    private static string BuildDerivedVirtualLayoutCheck()
    {
        uint eventType = GetEventTypeVFuncOffset();
        uint arguments = eventType + checked((uint)(3 * IntPtr.Size));
        uint entity = eventType + checked((uint)(22 * IntPtr.Size));
        bool argumentsObserved = false;
        bool entityObserved = false;
        uint count = GetVirtualSlotDiagnosticCount();
        for (uint index = 0; index < count; ++index)
        {
            uint slot = GetVirtualSlotOffset(index);
            argumentsObserved |= slot == arguments;
            entityObserved |= slot == entity;
        }
        return $"{{EventType=0x{eventType:X};Arguments=0x{arguments:X};ArgumentsObserved={argumentsObserved};Entity=0x{entity:X};EntityObserved={entityObserved}}}";
    }

    private static string BuildEventConsumerDiagnostics()
    {
        uint count = GetEventConsumerDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }

        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (uint index = 0; index < count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }

            diagnostics
                .Append("{Rva=0x")
                .Append(GetEventConsumerRva(index).ToString("X"))
                .Append(";Size=0x")
                .Append(GetEventConsumerSize(index).ToString("X"))
                .Append(";Relations=")
                .Append((EventConsumerRelations)GetEventConsumerRelations(index))
                .Append(";EventArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetEventConsumerArgumentMask(index)))
                .Append(";FirstUseRva=0x")
                .Append(GetEventConsumerFirstUseRva(index).ToString("X"))
                .Append(";FirstClobberRva=0x")
                .Append(GetEventConsumerFirstClobberRva(index).ToString("X"))
                .Append(";Callsites=")
                .Append(GetEventConsumerCallsiteCount(index))
                .Append(";Walkers=")
                .Append(GetEventConsumerWalkerCount(index))
                .Append(";Reads=")
                .Append(GetEventConsumerReadCount(index))
                .Append(";Stores=")
                .Append(GetEventConsumerMemoryStoreCount(index))
                .Append(";Compares=")
                .Append(GetEventConsumerCompareCount(index))
                .Append(";Vtables=")
                .Append(GetEventConsumerVtableLoadCount(index))
                .Append(";Forwards=")
                .Append(GetEventConsumerForwardCount(index))
                .Append(";TailForwards=")
                .Append(GetEventConsumerTailForwardCount(index))
                .Append(";TailRva=0x")
                .Append(GetEventConsumerTailInstructionRva(index).ToString("X"))
                .Append(";TailBytes=")
                .Append(FormatEncoding(
                    GetEventConsumerTailInstructionLength(index),
                    GetEventConsumerTailEncoding(index, 0),
                    GetEventConsumerTailEncoding(index, 1),
                    GetEventConsumerTailEncoding(index, 2),
                    GetEventConsumerTailEncoding(index, 3)))
                .Append(";TailOperand=0x")
                .Append(GetEventConsumerTailOperandInfo(index).ToString("X8"))
                .Append(";TailDisp=0x")
                .Append(GetEventConsumerTailDisplacement(index).ToString("X8"))
                .Append(";TailBase=")
                .Append(FormatRegister(GetEventConsumerTailBaseRegister(index)))
                .Append(";TailIndex=")
                .Append(FormatRegister(GetEventConsumerTailIndexRegister(index)))
                .Append(";TailScale=")
                .Append(GetEventConsumerTailScale(index))
                .Append(";ImageSize=0x")
                .Append(GetEventConsumerTailImageSize(index).ToString("X"))
                .Append(";SlotRva=0x")
                .Append(GetEventConsumerTailSlotRva(index).ToString("X8"))
                .Append(";SlotAddress=0x")
                .Append(GetEventConsumerTailSlotAddress(index).ToString("X"))
                .Append(";SlotProtection=0x")
                .Append(GetEventConsumerTailSlotProtection(index).ToString("X"))
                .Append(";SlotFlags=")
                .Append((TailSlotFlags)GetEventConsumerTailSlotFlags(index))
                .Append(";TargetAddress=0x")
                .Append(GetEventConsumerTailTargetAddress(index).ToString("X"))
                .Append(";TargetProtection=0x")
                .Append(GetEventConsumerTailTargetProtection(index).ToString("X"))
                .Append(";TargetFlags=")
                .Append((TailTargetFlags)GetEventConsumerTailTargetFlags(index))
                .Append(";TargetModule=")
                .Append(ResolveModuleName(GetEventConsumerTailTargetModuleBase(index)))
                .Append(";TargetModuleBase=0x")
                .Append(GetEventConsumerTailTargetModuleBase(index).ToString("X"))
                .Append(";TargetModuleSize=0x")
                .Append(GetEventConsumerTailTargetModuleSize(index).ToString("X"))
                .Append(";TargetModuleRva=0x")
                .Append(GetEventConsumerTailTargetModuleRva(index).ToString("X"))
                .Append(";ImportIdentity=")
                .Append(ResolveImportIdentity(
                    GetEventConsumerTailSlotRva(index),
                    GetEventConsumerTailTargetAddress(index)))
                .Append(";ExportIdentity=")
                .Append(ResolveExportIdentity(
                    GetEventConsumerTailTargetModuleBase(index),
                    GetEventConsumerTailTargetModuleRva(index)))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }

    private static string BuildVirtualSlotDiagnostics()
    {
        uint count = GetVirtualSlotDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }
        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (uint index = 0; index < count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }
            diagnostics
                .Append("{Slot=0x")
                .Append(GetVirtualSlotOffset(index).ToString("X"))
                .Append(";Relations=")
                .Append((VirtualSlotRelations)GetVirtualSlotRelations(index))
                .Append(";Callsites=")
                .Append(GetVirtualSlotCallsiteCount(index))
                .Append(";Walkers=")
                .Append(GetVirtualSlotWalkerCount(index))
                .Append(";EventRcx=")
                .Append(GetVirtualSlotEventRcxCount(index))
                .Append(";EventArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetVirtualSlotEventArgumentMask(index)))
                .Append(";Return=")
                .Append((VirtualReturnRelations)GetVirtualSlotReturnRelations(index))
                .Append(";ReturnUses=")
                .Append(GetVirtualSlotReturnUseCount(index))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }

    private static string FormatVirtualArgumentSourceKind(uint kind) =>
        kind switch
        {
            1 => "Memory",
            2 => "Address",
            3 => "Immediate",
            4 => "Write",
            _ => "None"
        };

    private static string BuildVirtualArgumentSource(
        uint callsiteIndex,
        uint argumentIndex,
        string name)
    {
        uint rva = GetVirtualSlotCallsiteArgumentSourceRva(
            callsiteIndex,
            argumentIndex);
        uint kind = GetVirtualSlotCallsiteArgumentSourceKind(
            callsiteIndex,
            argumentIndex);
        uint encoding = GetVirtualSlotCallsiteArgumentSourceEncoding(
            callsiteIndex,
            argumentIndex);
        uint operand = GetVirtualSlotCallsiteArgumentSourceOperandInfo(
            callsiteIndex,
            argumentIndex);
        uint displacement = GetVirtualSlotCallsiteArgumentSourceDisplacement(
            callsiteIndex,
            argumentIndex);
        uint value = GetVirtualSlotCallsiteArgumentSourceValue(
            callsiteIndex,
            argumentIndex);
        return $"{name}={{Kind={FormatVirtualArgumentSourceKind(kind)};Rva=0x{rva:X};Bytes={encoding:X8};Operand=0x{operand:X8};Disp=0x{displacement:X8};Value=0x{value:X8}}}";
    }

    private static string BuildRttiEventTypeDiagnostics()
    {
        uint count = GetRttiEventTypeDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }

        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (uint index = 0; index < count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }

            StringBuilder name = new(192);
            uint required = GetRttiEventTypeName(
                index,
                name,
                192);
            string typeName =
                required is > 0 and <= 192 && name.Length != 0
                    ? name.ToString()
                    : "Unavailable";
            uint typeValue = GetRttiEventTypeEventTypeValue(index);
            diagnostics
                .Append("{Name=")
                .Append(typeName)
                .Append(";TypeDescriptorRva=0x")
                .Append(GetRttiEventTypeTypeDescriptorRva(index).ToString("X"))
                .Append(";CompleteObjectLocatorRva=0x")
                .Append(GetRttiEventTypeCompleteObjectLocatorRva(index).ToString("X"))
                .Append(";VftableRva=0x")
                .Append(GetRttiEventTypeVftableRva(index).ToString("X"))
                .Append(";EventTypeValue=")
                .Append(typeValue == uint.MaxValue
                    ? "Unknown"
                    : $"0x{typeValue:X}")
                .Append(";Slot30=0x")
                .Append(GetRttiEventTypeSlotTargetRva(index, 0x30).ToString("X"))
                .Append(";Slot48=0x")
                .Append(GetRttiEventTypeSlotTargetRva(index, 0x48).ToString("X"))
                .Append(";Slot50=0x")
                .Append(GetRttiEventTypeSlotTargetRva(index, 0x50).ToString("X"))
                .Append(";Slot60=0x")
                .Append(GetRttiEventTypeSlotTargetRva(index, 0x60).ToString("X"))
                .Append(";Slot68=0x")
                .Append(GetRttiEventTypeSlotTargetRva(index, 0x68).ToString("X"))
                .Append(";SlotB0=0x")
                .Append(GetRttiEventTypeSlotTargetRva(index, 0xB0).ToString("X"))
                .Append(";SlotF8=0x")
                .Append(GetRttiEventTypeSlotTargetRva(index, 0xF8).ToString("X"))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }

    private static string BuildVirtualSlotCallsiteDiagnostics()
    {
        uint count = GetVirtualSlotCallsiteDiagnosticCount();
        if (count == 0)
        {
            return "[]";
        }
        StringBuilder diagnostics = new();
        diagnostics.Append('[');
        for (uint index = 0; index < count; ++index)
        {
            if (index != 0)
            {
                diagnostics.Append(", ");
            }
            diagnostics
                .Append("{WalkerRva=0x")
                .Append(GetVirtualSlotCallsiteWalkerRva(index).ToString("X"))
                .Append(";CallsiteRva=0x")
                .Append(GetVirtualSlotCallsiteRva(index).ToString("X"))
                .Append(";Slot=0x")
                .Append(GetVirtualSlotCallsiteOffset(index).ToString("X"))
                .Append(";Relations=")
                .Append((VirtualSlotRelations)GetVirtualSlotCallsiteRelations(index))
                .Append(";EventArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetVirtualSlotCallsiteEventArgumentMask(index)))
                .Append(";PreparedArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetVirtualSlotCallsitePreparedArgumentMask(index)))
                .Append(";MemoryArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetVirtualSlotCallsiteMemoryArgumentMask(index)))
                .Append(";AddressArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetVirtualSlotCallsiteAddressArgumentMask(index)))
                .Append(";ImmediateArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetVirtualSlotCallsiteImmediateArgumentMask(index)))
                .Append(";ZeroArgs=")
                .Append(FormatDispatchArgumentMask(
                    GetVirtualSlotCallsiteZeroArgumentMask(index)))
                .Append(";Return=")
                .Append((VirtualReturnRelations)GetVirtualSlotCallsiteReturnRelations(index))
                .Append(";ReturnFirstUseRva=0x")
                .Append(GetVirtualSlotCallsiteReturnFirstUseRva(index).ToString("X"))
                .Append(";ReturnFirstClobberRva=0x")
                .Append(GetVirtualSlotCallsiteReturnFirstClobberRva(index).ToString("X"))
                .Append(';')
                .Append(BuildVirtualArgumentSource(index, 0, "RDX"))
                .Append(';')
                .Append(BuildVirtualArgumentSource(index, 1, "R8"))
                .Append(';')
                .Append(BuildVirtualArgumentSource(index, 2, "R9"))
                .Append('}');
        }
        diagnostics.Append(']');
        return diagnostics.ToString();
    }

    internal static async Task<NativeBridge> OpenAsync(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        string companionPath = Path.Combine(rootDirectory, ModuleName);
        if (!File.Exists(companionPath))
        {
            throw new FileNotFoundException(
                "LowLevelEvents requires CEventGenerator.asi in the GTA root directory.",
                companionPath);
        }

        if (GetModuleHandle(ModuleName) == 0)
        {
            throw new InvalidOperationException(
                "CEventGenerator.asi exists but was not loaded into the GTA process by the ASI loader.");
        }

        NativeBridge bridge = new();
        try
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (Status is LowLevelEventBridgeStatus.Uninitialized or
                   LowLevelEventBridgeStatus.Initializing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timeout.Elapsed >= ReadyTimeout)
                {
                    throw new TimeoutException(
                        "CEventGenerator did not become ready before the LowLevelEvents initialization deadline.");
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

            if (Status != LowLevelEventBridgeStatus.Ready)
            {
                LocatorFailure locatorFailure =
                    (LocatorFailure)GetLocatorFailure();
                GameEdition edition = (GameEdition)GetGameEdition();
                throw new InvalidOperationException(
                    $"CEventGenerator is unavailable with status {Status} on GTA {edition} build {GameBuild}. " +
                    $"AbiVersion={GetAbiVersion()}; Capabilities=0x{GetCapabilities():X8}; " +
                    $"LocatorRevision={GetLocatorRevision()}; LocatorFailure={locatorFailure}; " +
                    $"DynamicProbeArmed={GetDynamicProbeArmed() != 0}; DynamicProbeActive={GetDynamicProbeActive() != 0}; " +
                    $"DynamicProbeSites={GetDynamicProbeSiteCount()}; DynamicProbeVirtualSites={GetDynamicProbeVirtualSiteCount()}; " +
                    $"DynamicProbeWalkerSites={GetDynamicProbeWalkerSiteCount()}; DynamicProbeSessions={GetDynamicProbeSessionCount()}; " +
                    $"DynamicProbeHits={GetDynamicProbeHitCount()}; DynamicProbeMode=EventGatedCorrelation; DynamicProbeToggle=F10; " +
                    $"DynamicProbeLog=CEventGenerator-dynamic.log; " +
                    $"GuardFlags=0x{GetGuardFlags():X8}; GuardCFFunctions={GetGuardCFFunctionCount()}; " +
                    $"GuardLongJumps={GetGuardLongJumpTargetCount()}; GuardAddressTakenIat={GetGuardAddressTakenIatCount()}; " +
                    $"PedMatches={GetPedMatchCount()}; " +
                    $"GlobalMatches={GetGlobalMatchCount()}; " +
                    $"DamageMatches={GetDamageMatchCount()}; " +
                    $"CEventCountAnchors={GetCEventCountAnchorCount()}; " +
                    $"CEventStackAnchors={GetCEventStackAnchorCount()}; " +
                    $"EventTypeAnchors={GetEventTypeAnchorCount()}; " +
                    $"CEventCountOffset=0x{GetCEventCountOffset():X}; " +
                    $"CEventStackOffset=0x{GetCEventStackOffset():X}; " +
                    $"EventTypeVFuncOffset=0x{GetEventTypeVFuncOffset():X}; " +
                    $"StackFunctionMatches={GetCEventStackFunctionMatchCount()}; " +
                    $"StackAnchorRva=0x{GetCEventStackAnchorRva():X}; " +
                    $"CountFunctionRva=0x{GetCEventCountFunctionRva():X}; " +
                    $"CountFunctionSize=0x{GetCEventCountFunctionSize():X}; " +
                    $"EventTypeFunctionRva=0x{GetEventTypeFunctionRva():X}; " +
                    $"EventTypeFunctionSize=0x{GetEventTypeFunctionSize():X}; " +
                    $"EventWalkerCandidates={GetEventWalkerCandidateCount()}; " +
                    $"EventWalkerRva=0x{GetEventWalkerCandidateRva():X}; " +
                    $"EventWalkerSize=0x{GetEventWalkerCandidateSize():X}; " +
                    $"TypedEventWalkerCandidates={GetTypedEventWalkerCandidateCount()}; " +
                    $"TypedEventWalkerRva=0x{GetTypedEventWalkerCandidateRva():X}; " +
                    $"TypedEventWalkerSize=0x{GetTypedEventWalkerCandidateSize():X}; " +
                    $"DirectCountCallers={GetDirectCountCallerCount()}; " +
                    $"DirectEventTypeCallers={GetDirectEventTypeCallerCount()}; " +
                    $"RelatedWalkerCandidates={GetRelatedEventWalkerCandidateCount()}; " +
                    $"CandidateDiagnostics={BuildEnhancedCandidateDiagnostics()}; " +
                    $"DispatchCallsites={GetDispatchCallsiteCandidateCount()}; " +
                    $"DispatchCallsiteDiagnostics={BuildDispatchCallsiteDiagnostics()}; " +
                    $"DispatchCallees={GetDispatchCalleeCandidateCount()}; " +
                    $"DispatchCalleeDiagnostics={BuildDispatchCalleeDiagnostics()}; " +
                    $"DispatchForwardCallsites={GetDispatchForwardCallsiteCandidateCount()}; " +
                    $"DispatchForwardCallsiteDiagnostics={BuildDispatchForwardCallsiteDiagnostics()}; " +
                    $"DispatchForwardCallees={GetDispatchForwardCalleeCandidateCount()}; " +
                    $"DispatchForwardCalleeDiagnostics={BuildDispatchForwardCalleeDiagnostics()}; " +
                    $"EventConsumerCallsites={GetEventConsumerCallsiteCandidateCount()}; " +
                    $"EventConsumerCallsiteDiagnostics={BuildEventConsumerCallsiteDiagnostics()}; " +
                    $"EventConsumers={GetEventConsumerCandidateCount()}; " +
                    $"EventConsumerDiagnostics={BuildEventConsumerDiagnostics()}; " +
                    $"VirtualSlots={GetVirtualSlotCandidateCount()}; VirtualSlotDiagnostics={BuildVirtualSlotDiagnostics()}; " +
                    $"VirtualSlotCallsites={GetVirtualSlotCallsiteCandidateCount()}; VirtualSlotCallsiteDiagnostics={BuildVirtualSlotCallsiteDiagnostics()}; " +
                    $"DerivedVirtualLayout={BuildDerivedVirtualLayoutCheck()}; " +
                    $"VirtualSemanticRanking={BuildVirtualSemanticRanking()}; " +
                    $"VirtualInputRanking={BuildVirtualInputRanking()}; " +
                    $"RttiEventTypes={GetRttiEventTypeCandidateCount()}; RttiEventTypeDiagnostics={BuildRttiEventTypeDiagnostics()}; " +
                    $"ConsumerRanking={BuildConvergedConsumerRanking()}.");
            }

            if (PerformanceFrequency == 0)
            {
                throw new InvalidOperationException(
                    "CEventGenerator did not expose a valid performance-counter frequency.");
            }

            return bridge;
        }
        catch
        {
            bridge.Dispose();
            throw;
        }
    }

    internal bool TryDequeue()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return TryDequeueNative(_buffer, NativeAbi.RecordSize) != 0;
    }

    internal void ReplaceDetailedEventIds(IEnumerable<uint> eventIds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(eventIds);

        ClearDetailedEventIds();

        HashSet<uint> unique = [];
        foreach (uint eventId in eventIds)
        {
            if (!unique.Add(eventId))
            {
                continue;
            }

            if (AddDetailedEventId(eventId) == 0)
            {
                throw new InvalidOperationException(
                    "CEventGenerator detailed-event filter reached its fixed capacity.");
            }
        }
    }

    internal void ReplaceCaptureStreamMasks(
        IEnumerable<KeyValuePair<uint, uint>> rules)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(rules);

        ClearCaptureStreamMasks();
        foreach (KeyValuePair<uint, uint> rule in rules)
        {
            if (SetCaptureStreamMask(rule.Key, rule.Value) == 0)
            {
                ClearCaptureStreamMasks();
                throw new InvalidOperationException(
                    $"CEventGenerator cannot configure capture policy for event id {rule.Key}.");
            }
        }
    }

    internal void ReplaceCatalogCaptureStreamMasks(
        IEnumerable<KeyValuePair<uint, uint>> rules)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(rules);

        ClearCatalogCaptureStreamMasks();
        foreach (KeyValuePair<uint, uint> rule in rules)
        {
            if (SetCatalogCaptureStreamMask(rule.Key, rule.Value) == 0)
            {
                ClearCatalogCaptureStreamMasks();
                throw new InvalidOperationException(
                    $"CEventGenerator cannot configure catalog capture policy for catalog id {rule.Key}.");
            }
        }
    }


    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Marshal.FreeHGlobal(_buffer);
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetModuleHandleW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern nint GetModuleHandle(string moduleName);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetAbiVersion",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetAbiVersion();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCapabilities",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetCapabilities();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetLocatorRevision",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetLocatorRevision();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDynamicProbeArmed",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDynamicProbeArmed();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDynamicProbeActive",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDynamicProbeActive();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDynamicProbeSiteCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDynamicProbeSiteCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDynamicProbeVirtualSiteCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDynamicProbeVirtualSiteCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDynamicProbeWalkerSiteCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDynamicProbeWalkerSiteCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDynamicProbeSessionCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDynamicProbeSessionCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDynamicProbeHitCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern ulong GetDynamicProbeHitCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetRecordSize",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetRecordSize();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetStatus",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetStatus();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetGameEdition",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetGameEdition();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetGameBuild",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetGameBuild();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetLocatorFailure",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetLocatorFailure();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetPedMatchCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetPedMatchCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetGlobalMatchCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetGlobalMatchCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDamageMatchCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDamageMatchCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCEventCountAnchorCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetCEventCountAnchorCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCEventStackAnchorCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetCEventStackAnchorCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventTypeAnchorCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventTypeAnchorCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCEventCountOffset",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetCEventCountOffset();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCEventStackOffset",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetCEventStackOffset();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventTypeVFuncOffset",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventTypeVFuncOffset();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCEventStackFunctionMatchCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetCEventStackFunctionMatchCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCEventStackAnchorRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetCEventStackAnchorRva();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCEventCountFunctionRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetCEventCountFunctionRva();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCEventCountFunctionSize",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetCEventCountFunctionSize();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventTypeFunctionRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventTypeFunctionRva();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventTypeFunctionSize",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventTypeFunctionSize();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventWalkerCandidateCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventWalkerCandidateCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventWalkerCandidateRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventWalkerCandidateRva();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventWalkerCandidateSize",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventWalkerCandidateSize();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetTypedEventWalkerCandidateCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetTypedEventWalkerCandidateCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetTypedEventWalkerCandidateRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetTypedEventWalkerCandidateRva();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetTypedEventWalkerCandidateSize",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetTypedEventWalkerCandidateSize();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDirectCountCallerCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDirectCountCallerCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDirectEventTypeCallerCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDirectEventTypeCallerCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetRelatedEventWalkerCandidateCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetRelatedEventWalkerCandidateCount();

    [DllImport(ModuleName, EntryPoint = "CEG_GetGuardFlags", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetGuardFlags();

    [DllImport(ModuleName, EntryPoint = "CEG_GetGuardCFFunctionCount", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetGuardCFFunctionCount();

    [DllImport(ModuleName, EntryPoint = "CEG_GetGuardLongJumpTargetCount", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetGuardLongJumpTargetCount();

    [DllImport(ModuleName, EntryPoint = "CEG_GetGuardAddressTakenIatCount", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetGuardAddressTakenIatCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEnhancedCandidateDiagnosticCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEnhancedCandidateDiagnosticCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEnhancedCandidateRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEnhancedCandidateRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEnhancedCandidateSize",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEnhancedCandidateSize(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEnhancedCandidateRelations",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEnhancedCandidateRelations(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCallsiteCandidateCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCallsiteCandidateCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCallsiteDiagnosticCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCallsiteDiagnosticCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCallsiteWalkerRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCallsiteWalkerRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCallsiteRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCallsiteRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCallsiteCalleeRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCallsiteCalleeRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCallsiteRelations",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCallsiteRelations(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCalleeCandidateCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCalleeCandidateCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCalleeDiagnosticCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCalleeDiagnosticCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCalleeRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCalleeRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCalleeSize",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCalleeSize(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCalleeRelations",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCalleeRelations(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCalleeCallsiteCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCalleeCallsiteCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchCalleeWalkerCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchCalleeWalkerCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCallsiteCandidateCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCallsiteCandidateCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCallsiteDiagnosticCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCallsiteDiagnosticCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCallsiteParentRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCallsiteParentRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCallsiteRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCallsiteRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCallsiteCalleeRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCallsiteCalleeRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCallsiteEventArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCallsiteEventArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCallsiteGroupArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCallsiteGroupArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCalleeCandidateCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCalleeCandidateCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCalleeDiagnosticCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCalleeDiagnosticCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCalleeRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCalleeRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCalleeSize",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCalleeSize(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCalleeRelations",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCalleeRelations(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCalleeCallsiteCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCalleeCallsiteCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCalleeParentCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCalleeParentCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCalleeEventArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCalleeEventArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDispatchForwardCalleeGroupArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetDispatchForwardCalleeGroupArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerCallsiteCandidateCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteCandidateCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerCallsiteDiagnosticCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteDiagnosticCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerCallsiteWalkerRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteWalkerRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerCallsiteRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerCallsiteCalleeRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteCalleeRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerCallsiteEventArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteEventArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerCallsiteRelations",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteRelations(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerCallsiteInstructionLength", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteInstructionLength(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerCallsiteEncoding", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteEncoding(uint index, uint word);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerCallsiteOperandInfo", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteOperandInfo(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerCallsiteDisplacement", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteDisplacement(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerCandidateCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerCandidateCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerDiagnosticCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerDiagnosticCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerSize",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerSize(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerRelations",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerRelations(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerFirstUseRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerFirstUseRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerFirstClobberRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerFirstClobberRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerCallsiteCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerCallsiteCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerWalkerCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerWalkerCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerReadCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerReadCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerMemoryStoreCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerMemoryStoreCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerCompareCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerCompareCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerVtableLoadCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerVtableLoadCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerForwardCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerForwardCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEventConsumerTailForwardCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEventConsumerTailForwardCount(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailInstructionRva", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailInstructionRva(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailInstructionLength", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailInstructionLength(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailEncoding", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailEncoding(uint index, uint word);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailOperandInfo", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailOperandInfo(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailDisplacement", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailDisplacement(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailBaseRegister", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailBaseRegister(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailIndexRegister", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailIndexRegister(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailScale", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailScale(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailImageSize", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailImageSize(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailSlotRva", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailSlotRva(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailSlotAddress", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern ulong GetEventConsumerTailSlotAddress(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailSlotProtection", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailSlotProtection(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailSlotFlags", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailSlotFlags(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailTargetAddress", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern ulong GetEventConsumerTailTargetAddress(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailTargetProtection", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailTargetProtection(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailTargetFlags", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailTargetFlags(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailTargetModuleBase", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern ulong GetEventConsumerTailTargetModuleBase(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailTargetModuleSize", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailTargetModuleSize(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetEventConsumerTailTargetModuleRva", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetEventConsumerTailTargetModuleRva(uint index);
    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotCandidateCount", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotCandidateCount();

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotDiagnosticCount", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotDiagnosticCount();

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotOffset", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotOffset(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotRelations", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotRelations(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotCallsiteCount", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteCount(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotWalkerCount", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotWalkerCount(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotEventRcxCount", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotEventRcxCount(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotEventArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotEventArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotReturnRelations",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotReturnRelations(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotReturnUseCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotReturnUseCount(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotCallsiteCandidateCount", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteCandidateCount();

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotCallsiteDiagnosticCount", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteDiagnosticCount();

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotCallsiteWalkerRva", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteWalkerRva(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotCallsiteRva", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteRva(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotCallsiteOffset", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteOffset(uint index);

    [DllImport(ModuleName, EntryPoint = "CEG_GetVirtualSlotCallsiteRelations", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteRelations(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteEventArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteEventArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteReturnRelations",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteReturnRelations(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteReturnFirstUseRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteReturnFirstUseRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteReturnFirstClobberRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteReturnFirstClobberRva(uint index);


    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsitePreparedArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsitePreparedArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteMemoryArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteMemoryArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteAddressArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteAddressArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteImmediateArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteImmediateArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteZeroArgumentMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteZeroArgumentMask(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteArgumentSourceRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteArgumentSourceRva(
        uint index,
        uint argumentIndex);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteArgumentSourceKind",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteArgumentSourceKind(
        uint index,
        uint argumentIndex);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteArgumentSourceEncoding",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteArgumentSourceEncoding(
        uint index,
        uint argumentIndex);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteArgumentSourceOperandInfo",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteArgumentSourceOperandInfo(
        uint index,
        uint argumentIndex);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteArgumentSourceDisplacement",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteArgumentSourceDisplacement(
        uint index,
        uint argumentIndex);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetVirtualSlotCallsiteArgumentSourceValue",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetVirtualSlotCallsiteArgumentSourceValue(
        uint index,
        uint argumentIndex);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetRttiEventTypeCandidateCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetRttiEventTypeCandidateCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetRttiEventTypeDiagnosticCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetRttiEventTypeDiagnosticCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetRttiEventTypeName",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi,
        ExactSpelling = true)]
    private static extern uint GetRttiEventTypeName(
        uint index,
        StringBuilder destination,
        uint destinationSize);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetRttiEventTypeTypeDescriptorRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetRttiEventTypeTypeDescriptorRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetRttiEventTypeCompleteObjectLocatorRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetRttiEventTypeCompleteObjectLocatorRva(
        uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetRttiEventTypeVftableRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetRttiEventTypeVftableRva(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetRttiEventTypeEventTypeValue",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetRttiEventTypeEventTypeValue(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetRttiEventTypeSlotTargetRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetRttiEventTypeSlotTargetRva(
        uint index,
        uint slotOffset);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetPerformanceFrequency",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern ulong GetPerformanceFrequency();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetPendingCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern ulong GetPendingCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetDroppedCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern ulong GetDroppedCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetObservedCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern ulong GetObservedCount(uint eventId);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCatalogEventCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetCatalogEventCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetCatalogEventName",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi,
        ExactSpelling = true)]
    private static extern uint GetCatalogEventName(
        uint catalogEventId,
        StringBuilder destination,
        uint destinationSize);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_TryDequeue",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint TryDequeueNative(
        nint destination,
        uint destinationSize);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_ClearDetailedEventIds",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern void ClearDetailedEventIds();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_AddDetailedEventId",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint AddDetailedEventId(uint eventId);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_ClearCaptureStreamMasks",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern void ClearCaptureStreamMasks();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_SetCaptureStreamMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint SetCaptureStreamMask(
        uint eventId,
        uint streamMask);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_ClearCatalogCaptureStreamMasks",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern void ClearCatalogCaptureStreamMasks();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_SetCatalogCaptureStreamMask",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint SetCatalogCaptureStreamMask(
        uint catalogEventId,
        uint streamMask);

}

#pragma warning restore CA2101
#pragma warning restore SYSLIB1054