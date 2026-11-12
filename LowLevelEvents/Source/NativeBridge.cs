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
                    $"CEventGenerator status={Status}; edition={edition}; build={GameBuild}; " +
                    $"failure={locatorFailure}; ABI={GetAbiVersion()}; revision={GetLocatorRevision()}; " +
                    $"receiver shape candidates: reaction/group={GetPedMatchCount()}, " +
                    $"global={GetGlobalMatchCount()}, damage={GetDamageMatchCount()}; " +
                    $"shared-callee={GetEnhancedReceiverCorrelation()}; " +
                    $"Enhanced anchors: count={GetCEventCountAnchorCount()}, " +
                    $"stack={GetCEventStackAnchorCount()}, type={GetEventTypeAnchorCount()}; " +
                    $"layout: count=0x{GetCEventCountOffset():X}, " +
                    $"stack=0x{GetCEventStackOffset():X}, type=0x{GetEventTypeVFuncOffset():X}; " +
                    "signature scan only.");
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

    private static string GetEnhancedReceiverCorrelation()
    {
        if ((GameEdition)GetGameEdition() != GameEdition.Enhanced ||
            GetEnhancedCandidateDiagnosticCount() != 3)
        {
            return "none";
        }
        if ((GetEnhancedCandidateRelations(0) & 0x200u) == 0 ||
            (GetEnhancedCandidateRelations(1) & 0x200u) == 0 ||
            (GetEnhancedCandidateRelations(2) & 0x200u) == 0)
        {
            return "none";
        }
        return $"0x{GetEnhancedCandidateRva(0):X},0x{GetEnhancedCandidateRva(1):X},0x{GetEnhancedCandidateRva(2):X}";
    }

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEnhancedCandidateDiagnosticCount",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEnhancedCandidateDiagnosticCount();

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEnhancedCandidateRelations",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEnhancedCandidateRelations(uint index);

    [DllImport(
        ModuleName,
        EntryPoint = "CEG_GetEnhancedCandidateRva",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetEnhancedCandidateRva(uint index);

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