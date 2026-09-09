#pragma warning disable SYSLIB1054

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LowLevelEvents.Source;

internal sealed class NativeBridge : IDisposable
{
    private const string ModuleName = "CEventGenerator.asi";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);

    private readonly nint _buffer;
    private bool _disposed;

    private NativeBridge()
    {
        if (GetRecordSize() != NativeAbi.RecordSize)
        {
            throw new InvalidOperationException(
                "CEventGenerator event-record size is incompatible with LowLevelEvents.");
        }

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
                throw new InvalidOperationException(
                    $"CEventGenerator is unavailable with status {Status} on GTA build {GameBuild}.");
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
        EntryPoint = "CEG_GetGameBuild",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GetGameBuild();

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
}

#pragma warning restore SYSLIB1054