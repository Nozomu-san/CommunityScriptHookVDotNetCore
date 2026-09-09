using System.Runtime.InteropServices;

namespace CommunityScriptHookVDotNetCore.Source;

internal enum BrainRunResult
{
    Success = 0,
    InvalidArgument = 1,
    AlreadyRunning = 2,
    InternalFailure = 3,
    MissingCapability = 4
}

internal enum NativeCallStatus
{
    Pending = -1,
    Success = 0,
    InvalidRequest = 1,
    TooManyArguments = 2,
    TooManyResults = 3,
    NativeReturnedNull = 4,
    SessionStopping = 5,
    FunctionFault = 6,
    GuardRejected = 7
}

internal enum HostCallOperation : ushort
{
    ScriptNative = 0,
    Int32FunctionOnePointer = 1,
    Int32FunctionOneGuardedObjectPointer = 2
}

[Flags]
internal enum HostCapability : ulong
{
    None = 0,
    FrameBridge = 1UL << 0,
    NativeBridge = 1UL << 1,
    CooperativeShutdown = 1UL << 2,
    GameThreadFunctionBridge = 1UL << 3,
    GameThreadGuardedFunctionBridge = 1UL << 4
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal readonly struct HostRunRequest
{
    public readonly uint Size;
    public readonly HostCapability Capabilities;
    public readonly nint ReadyEvent;
    public readonly nint FrameRequestedEvent;
    public readonly nint FrameCompletedEvent;
    public readonly nint StopRequestedEvent;
    public readonly nint NativeRequestedEvent;
    public readonly nint NativeCompletedEvent;
    public readonly nint Frame;
    public readonly nint NativeCall;
    public readonly ulong PerformanceFrequency;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal readonly struct HostFrameMailbox
{
    public readonly uint Size;
    public readonly uint Reserved;
    public readonly ulong FrameIndex;
    public readonly long PerformanceCounter;
}

internal static class HostContract
{
    public const int RunRequestSize = 88;
    public const int FrameMailboxSize = 24;
    public const int NativeMailboxSize = 320;
    public const int MaximumNativeArguments = 32;
    public const int MaximumNativeResults = 4;

    public const int NativeSizeOffset = 0;
    public const int NativeArgumentCountOffset = 4;
    public const int NativeOperationOffset = 6;
    public const int NativeRequestedResultCountOffset = 8;
    public const int NativeStatusOffset = 12;
    public const int NativeRequestIdOffset = 16;
    public const int NativeHashOffset = 24;
    public const int NativeArgumentsOffset = 32;
    public const int NativeResultsOffset = 288;

    private const HostCapability RequiredCapabilities =
        HostCapability.FrameBridge |
        HostCapability.NativeBridge |
        HostCapability.CooperativeShutdown |
        HostCapability.GameThreadFunctionBridge |
        HostCapability.GameThreadGuardedFunctionBridge;

    public static BrainRunResult Validate(
        nint request,
        int requestSize,
        out HostRunRequest value)
    {
        value = default;
        if (request == 0 ||
            requestSize < RunRequestSize ||
            Marshal.SizeOf<HostRunRequest>() != RunRequestSize ||
            Marshal.SizeOf<HostFrameMailbox>() != FrameMailboxSize)
        {
            return BrainRunResult.InvalidArgument;
        }

        value = Marshal.PtrToStructure<HostRunRequest>(request);
        if (value.Size < RunRequestSize)
        {
            return BrainRunResult.InvalidArgument;
        }

        if ((value.Capabilities & RequiredCapabilities) != RequiredCapabilities)
        {
            return BrainRunResult.MissingCapability;
        }

        if (value.ReadyEvent == 0 ||
            value.FrameRequestedEvent == 0 ||
            value.FrameCompletedEvent == 0 ||
            value.StopRequestedEvent == 0 ||
            value.NativeRequestedEvent == 0 ||
            value.NativeCompletedEvent == 0 ||
            value.Frame == 0 ||
            value.NativeCall == 0 ||
            value.PerformanceFrequency == 0)
        {
            return BrainRunResult.InvalidArgument;
        }

        return BrainRunResult.Success;
    }
}