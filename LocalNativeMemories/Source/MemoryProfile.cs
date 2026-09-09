namespace LocalNativeMemories.Source;

internal enum PatternAddressMode
{
    RipRelative,
    MatchOffset
}

internal enum PoolDecodeRotationOrder
{
    SecondaryThenDynamic,
    DynamicThenSecondary
}

internal readonly record struct PatternAddressProfile(
    string Expression,
    PatternAddressMode Mode,
    int Offset,
    int InstructionLength);

internal readonly record struct FwPoolLayout(
    int PoolStartOffset,
    int ByteArrayOffset,
    int SizeOffset,
    int ItemSizeOffset,
    int ItemCountOffset);

internal readonly record struct VehiclePoolLayout(
    int PoolAddressOffset,
    int SizeOffset,
    int BitArrayOffset,
    int ItemCountOffset);

internal readonly record struct ScriptGuidPoolLayout(
    int MaxCountOffset,
    int ItemCountOffset);

internal readonly record struct EncryptedPoolDecodeProfile(
    int InitializedDisplacementOffset,
    int InitializedInstructionLength,
    int FirstDisplacementOffset,
    int FirstInstructionLength,
    int SecondDisplacementOffset,
    int SecondInstructionLength,
    int FirstRotateOffset,
    int SecondRotateOffset,
    int AndOffset,
    int AddOffset,
    PoolDecodeRotationOrder RotationOrder,
    nuint MinimumReadableSize);

internal readonly record struct TailBoomProfile(
    string Expression,
    int DisplacementOffset,
    int TailBoomOffsetDelta);

internal sealed class GameMemoryProfile
{
    private GameMemoryProfile(
        LocalGameEdition edition,
        PatternAddressProfile entityAddressResolver,
        PatternAddressProfile createGuid,
        string pedPoolPattern,
        string pedPoolRecoveryPattern,
        string objectPoolPattern,
        string[] scriptGuidPoolPatterns,
        string vehicleAllocatorPattern,
        FwPoolLayout fwPool,
        VehiclePoolLayout vehiclePool,
        ScriptGuidPoolLayout scriptGuidPool,
        EncryptedPoolDecodeProfile pedPoolDecode,
        EncryptedPoolDecodeProfile objectPoolDecode,
        EncryptedPoolDecodeProfile scriptGuidPoolDecode,
        int pedDamageFlagsOffset,
        uint pureInvincibilityMask,
        uint reactionInvincibilityMask,
        TailBoomProfile tailBoom,
        nuint scriptGuidMinimumReadableSize,
        nuint vehicleAllocatorMinimumReadableSize)
    {
        Edition = edition;
        EntityAddressResolver = entityAddressResolver;
        CreateGuid = createGuid;
        PedPoolPattern = pedPoolPattern;
        PedPoolRecoveryPattern = pedPoolRecoveryPattern;
        ObjectPoolPattern = objectPoolPattern;
        ScriptGuidPoolPatterns = scriptGuidPoolPatterns;
        VehicleAllocatorPattern = vehicleAllocatorPattern;
        FwPool = fwPool;
        VehiclePool = vehiclePool;
        ScriptGuidPool = scriptGuidPool;
        PedPoolDecode = pedPoolDecode;
        ObjectPoolDecode = objectPoolDecode;
        ScriptGuidPoolDecode = scriptGuidPoolDecode;
        PedDamageFlagsOffset = pedDamageFlagsOffset;
        PureInvincibilityMask = pureInvincibilityMask;
        ReactionInvincibilityMask = reactionInvincibilityMask;
        TailBoom = tailBoom;
        ScriptGuidMinimumReadableSize = scriptGuidMinimumReadableSize;
        VehicleAllocatorMinimumReadableSize = vehicleAllocatorMinimumReadableSize;
    }

    internal LocalGameEdition Edition { get; }
    internal PatternAddressProfile EntityAddressResolver { get; }
    internal PatternAddressProfile CreateGuid { get; }
    internal string PedPoolPattern { get; }
    internal string PedPoolRecoveryPattern { get; }
    internal string ObjectPoolPattern { get; }
    internal string[] ScriptGuidPoolPatterns { get; }
    internal string VehicleAllocatorPattern { get; }
    internal FwPoolLayout FwPool { get; }
    internal VehiclePoolLayout VehiclePool { get; }
    internal ScriptGuidPoolLayout ScriptGuidPool { get; }
    internal EncryptedPoolDecodeProfile PedPoolDecode { get; }
    internal EncryptedPoolDecodeProfile ObjectPoolDecode { get; }
    internal EncryptedPoolDecodeProfile ScriptGuidPoolDecode { get; }
    internal int PedDamageFlagsOffset { get; }
    internal uint PureInvincibilityMask { get; }
    internal uint ReactionInvincibilityMask { get; }
    internal TailBoomProfile TailBoom { get; }
    internal nuint ScriptGuidMinimumReadableSize { get; }
    internal nuint VehicleAllocatorMinimumReadableSize { get; }

    internal bool IsEnhanced => Edition is LocalGameEdition.Enhanced;

    internal static GameMemoryProfile Resolve(LocalGameEdition edition) =>
        edition switch
        {
            LocalGameEdition.Legacy => Legacy,
            LocalGameEdition.Enhanced => Enhanced,
            _ => throw new PlatformNotSupportedException(
                $"Unsupported local game edition '{edition}'.")
        };

    private static GameMemoryProfile Legacy { get; } = new(
        LocalGameEdition.Legacy,
        new(
            "85 ED 74 0F 8B CD E8 ? ? ? ? 48 8B F8 48 85 C0 74 2E",
            PatternAddressMode.RipRelative,
            7,
            11),
        new(
            "48 F7 F9 49 8B 48 08 48 63 D0 C1 E0 08 0F B6 1C 11 03 D8",
            PatternAddressMode.MatchOffset,
            -0x68,
            0),
        "48 8B 05 ? ? ? ? 41 0F BF C8 0F BF 40 10",
        string.Empty,
        "48 8B 05 ? ? ? ? 8B 78 10 85 FF",
        [
            "4C 8B 05 ? ? ? ? 41 3B 50 ? 7D ? 49 8B 40",
            "4C 8B 0D ? ? ? ? 44 8B C1 49 8B 41 08"
        ],
        "48 8B 05 ? ? ? ? F3 0F 59 F6 48 8B 08",
        new(0x00, 0x08, 0x10, 0x14, 0x20),
        new(0x00, 0x08, 0x30, 0x60),
        new(0x10, 0x20),
        default,
        default,
        default,
        0x188,
        1u << 8,
        1u << 9,
        new(
            "B3 03 22 D3 48 8B CF E8 ? ? ? ? 48 8B CF F3 0F 11 86 ? ? ? ?",
            19,
            8),
        0x30,
        0x70);

    private static GameMemoryProfile Enhanced { get; } = new(
        LocalGameEdition.Enhanced,
        new(
            "41 8B 4C 1C ? E8",
            PatternAddressMode.RipRelative,
            6,
            10),
        new(
            "48 8B ? ? ? ? ? 48 01 D9 E8 ? ? ? ? 48 8B",
            PatternAddressMode.RipRelative,
            11,
            15),
        "8B 05 ? ? ? ? 85 C0 0F 8E ? ? ? ? C1 E8 ? 0F B6 0D",
        "48 83 EC ? 83 3D ? ? ? ? ? 0F 84 ? ? ? ? 0F B6 05",
        "53 48 81 EC ? ? ? ? 0F 29 B4 ? ? ? ? ? 48 89 CE 0F B6 05",
        [
            "C9 41 89 C8 49 C1 E8",
            "83 F9 ? 74 ? 41 89 C8"
        ],
        "48 8B 05 ? ? ? ? 48 85 C0 74 ? 4C 8B 00",
        new(0x08, 0x10, 0x18, 0x1C, 0x28),
        new(0x08, 0x10, 0x38, 0x68),
        new(0x18, 0x28),
        new(
            20,
            24,
            38,
            42,
            27,
            31,
            34,
            48,
            51,
            54,
            PoolDecodeRotationOrder.SecondaryThenDynamic,
            0x30),
        new(
            22,
            26,
            40,
            44,
            29,
            33,
            36,
            59,
            49,
            52,
            PoolDecodeRotationOrder.DynamicThenSecondary,
            0x38),
        new(
            11,
            15,
            29,
            33,
            18,
            22,
            25,
            39,
            42,
            45,
            PoolDecodeRotationOrder.SecondaryThenDynamic,
            0x30),
        0x188,
        1u << 8,
        1u << 9,
        new(
            "F3 0F 11 86 ? ? ? ? F3 0F 10 8F ? ? ? ? B1",
            12,
            8),
        0x30,
        0x70);
}