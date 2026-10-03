using CommunityScriptHookVDotNetCore.Source;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Alloc8orStandardNatives.Source;

public enum GameEdition
{
    Unsupported = -1,
    Legacy = 0,
    Enhanced = 1
}

public static class GameEditionExtensions
{
    extension(GameEdition edition)
    {
        public bool IsSupported() =>
            edition is GameEdition.Legacy or GameEdition.Enhanced;

        public string GetDisplayName() =>
            edition switch
            {
                GameEdition.Legacy => "GTA V Legacy",
                GameEdition.Enhanced => "GTA V Enhanced",
                _ => "Unsupported game process"
            };
    }
}

public readonly record struct GameBuildInfo(
    GameEdition Edition,
    int Build,
    string ExecutablePath,
    string ProductVersion)
{
    public bool IsSupported => Edition.IsSupported() && Build >= 0;
}

public interface IGameBuildService
{
    GameBuildInfo Current { get; }
}

public enum NativeAbiType : byte
{
    Void = 0,
    Boolean32 = 1,
    Int32 = 2,
    Float32 = 3,
    ConstCharPointer = 4,
    Any = 5,
    Hash32 = 6,
    Blip = 7,
    Cam = 8,
    Entity = 9,
    FireId = 10,
    Interior = 11,
    ItemSet = 12,
    Object = 13,
    Ped = 14,
    Pickup = 15,
    Player = 16,
    ScrHandle = 17,
    Vehicle = 18,
    Vector3 = 19,
    AnyPointer = 20,
    Int32Pointer = 21,
    Float32Pointer = 22,
    Vector3Pointer = 23,
    Boolean32Pointer = 24,
    Hash32Pointer = 25,
    CharPointer = 26,
    EntityPointer = 27,
    VehiclePointer = 28,
    PedPointer = 29,
    ObjectPointer = 30,
    ScrHandlePointer = 31,
    BlipPointer = 32
}

public readonly record struct NativeAny(ulong Value);
public readonly record struct NativeAddress(nuint Value);

public readonly record struct Blip(int Value);
public readonly record struct Cam(int Value);
public readonly record struct Entity(int Value);
public readonly record struct FireId(int Value);
public readonly record struct Interior(int Value);
public readonly record struct ItemSet(int Value);
public readonly record struct GameObject(int Value);
public readonly record struct Ped(int Value);
public readonly record struct Pickup(int Value);
public readonly record struct Player(int Value);
public readonly record struct ScrHandle(int Value);
public readonly record struct Vehicle(int Value);

public sealed record NativeParameterDescriptor(
    string Name,
    NativeAbiType Type);

public sealed class NativeSignatureVariant
{
    internal NativeSignatureVariant(
        int minimumBuild,
        NativeAbiType returnType,
        NativeParameterDescriptor[] parameters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumBuild);
        MinimumBuild = minimumBuild;
        ReturnType = returnType;
        Parameters = parameters ?? throw new ArgumentNullException(
            nameof(parameters));
    }

    public int MinimumBuild { get; }
    public NativeAbiType ReturnType { get; }
    public IReadOnlyList<NativeParameterDescriptor> Parameters { get; }

}

public sealed class NativeDescriptor
{
    internal NativeDescriptor(
        int index,
        ulong hash,
        string name,
        NativeSignatureVariant? legacy,
        NativeSignatureVariant? enhanced)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfZero(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (legacy is null && enhanced is null)
        {
            throw new ArgumentException(
                "A native descriptor must support at least one game edition.");
        }

        Index = index;
        Hash = hash;
        Name = name;
        Legacy = legacy;
        Enhanced = enhanced;
    }

    public int Index { get; }
    public ulong Hash { get; }
    public string Name { get; }
    public NativeSignatureVariant? Legacy { get; }
    public NativeSignatureVariant? Enhanced { get; }

    public int MinimumLegacyBuild => Legacy?.MinimumBuild ?? -1;
    public int MinimumEnhancedBuild => Enhanced?.MinimumBuild ?? -1;

    public NativeSignatureVariant? GetVariant(GameEdition edition) =>
        edition switch
        {
            GameEdition.Legacy => Legacy,
            GameEdition.Enhanced => Enhanced,
            _ => null
        };

    public bool Supports(GameBuildInfo game)
    {
        NativeSignatureVariant? variant = GetVariant(game.Edition);
        return game.IsSupported &&
            variant is not null &&
            game.Build >= variant.MinimumBuild;
    }

    public override string ToString() =>
        $"0x{Hash:X16} -> {Name}; Legacy {DescribeVariant(Legacy)}, " +
        $"Enhanced {DescribeVariant(Enhanced)}";

    private static string DescribeVariant(NativeSignatureVariant? variant)
    {
        if (variant is null)
        {
            return "unsupported (-1)";
        }

        string arguments = string.Join(
            ", ",
            variant.Parameters.Select(static parameter =>
                NativeSyntax(parameter.Type) + " " + parameter.Name));
        return $"build {variant.MinimumBuild}; " +
            $"{NativeSyntax(variant.ReturnType)} ({arguments})";
    }

    internal static string NativeSyntax(NativeAbiType type) => type switch
    {
        NativeAbiType.Void => "void",
        NativeAbiType.Boolean32 => "BOOL",
        NativeAbiType.Int32 => "int",
        NativeAbiType.Float32 => "float",
        NativeAbiType.ConstCharPointer => "const char*",
        NativeAbiType.Any => "Any",
        NativeAbiType.Hash32 => "Hash",
        NativeAbiType.Blip => "Blip",
        NativeAbiType.Cam => "Cam",
        NativeAbiType.Entity => "Entity",
        NativeAbiType.FireId => "FireId",
        NativeAbiType.Interior => "Interior",
        NativeAbiType.ItemSet => "ItemSet",
        NativeAbiType.Object => "Object",
        NativeAbiType.Ped => "Ped",
        NativeAbiType.Pickup => "Pickup",
        NativeAbiType.Player => "Player",
        NativeAbiType.ScrHandle => "ScrHandle",
        NativeAbiType.Vehicle => "Vehicle",
        NativeAbiType.Vector3 => "Vector3",
        NativeAbiType.AnyPointer => "Any*",
        NativeAbiType.Int32Pointer => "int*",
        NativeAbiType.Float32Pointer => "float*",
        NativeAbiType.Vector3Pointer => "Vector3*",
        NativeAbiType.Boolean32Pointer => "BOOL*",
        NativeAbiType.Hash32Pointer => "Hash*",
        NativeAbiType.CharPointer => "char*",
        NativeAbiType.EntityPointer => "Entity*",
        NativeAbiType.VehiclePointer => "Vehicle*",
        NativeAbiType.PedPointer => "Ped*",
        NativeAbiType.ObjectPointer => "Object*",
        NativeAbiType.ScrHandlePointer => "ScrHandle*",
        NativeAbiType.BlipPointer => "Blip*",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}

public readonly record struct NativeDatabaseIdentity(
    string SourceFingerprint,
    string DecodedSha256,
    int DescriptorCount,
    int PackedBytes,
    int DecodedBytes);

public interface INativeDatabaseInfo
{
    NativeDatabaseIdentity Identity { get; }
}

public interface INativeCatalog
{
    IReadOnlyList<NativeDescriptor> Entries { get; }

    bool TryGet(
        string name,
        [NotNullWhen(true)] out NativeDescriptor? descriptor);

    bool TryGet(
        ulong hash,
        [NotNullWhen(true)] out NativeDescriptor? descriptor);
}

public sealed class KnownNativeInt32Output
{
    public int Value { get; internal set; }
}

public sealed class KnownNativeInt32BufferOutput
{
    private readonly int[] _initialValues;

    public KnownNativeInt32BufferOutput(int[] initialValues)
    {
        ArgumentNullException.ThrowIfNull(initialValues);
        if (initialValues.Length == 0)
        {
            throw new ArgumentException(
                "A native output buffer cannot be empty.",
                nameof(initialValues));
        }

        _initialValues = [.. initialValues];
        Values = new([.. initialValues]);
    }

    public ReadOnlyMemory<int> Values { get; internal set; }

    internal int[] InitialValues => _initialValues;
}

public sealed class KnownNativeVector3Output
{
    public Vector3 Value { get; internal set; }
}

public readonly struct KnownNativeArgument
{
    private KnownNativeArgument(NativeArgument value) => Value = value;

    internal NativeArgument Value { get; }

    public static KnownNativeArgument Boolean(bool value) =>
        new(NativeArgument.Boolean(value));

    public static KnownNativeArgument Int32<T>(T value)
        where T : INumberBase<T> =>
        new(NativeArgument.Int32(value));

    public static KnownNativeArgument Float32<T>(T value)
        where T : INumberBase<T> =>
        new(NativeArgument.Float32(value));

    public static KnownNativeArgument Text(string? value) =>
        new(NativeArgument.Text(value));

    public static KnownNativeArgument Text(char value) =>
        Text(value.ToString());

    public static KnownNativeArgument Hash32<T>(T value)
        where T : INumberBase<T> =>
        new(NativeArgument.Hash32(value));

    public static KnownNativeArgument Any(NativeAny value) =>
        new(NativeArgument.Any(value.Value));

    public static KnownNativeArgument Any<T>(T value)
        where T : INumberBase<T> =>
        new(NativeArgument.Any(value));

    public static KnownNativeArgument Int32Output(
        KnownNativeInt32Output output) =>
        new(NativeArgument.BindOutput(
            NativeAbiType.Int32Pointer,
            new Int32OutputBinding(
                output ?? throw new ArgumentNullException(nameof(output)))));

    public static KnownNativeArgument AnyInt32BufferOutput(
        KnownNativeInt32BufferOutput output) =>
        new(NativeArgument.BindOutput(
            NativeAbiType.AnyPointer,
            new Int32BufferOutputBinding(
                output ?? throw new ArgumentNullException(nameof(output)))));

    public static KnownNativeArgument Vector3Output(
        KnownNativeVector3Output output) =>
        new(NativeArgument.BindOutput(
            NativeAbiType.Vector3Pointer,
            new Vector3OutputBinding(
                output ?? throw new ArgumentNullException(nameof(output)))));

    public static KnownNativeArgument Blip(Blip value) =>
        new(NativeArgument.Blip(value.Value));

    public static KnownNativeArgument Cam(Cam value) =>
        new(NativeArgument.Cam(value.Value));

    public static KnownNativeArgument Entity(Entity value) =>
        new(NativeArgument.Entity(value.Value));

    public static KnownNativeArgument FireId(FireId value) =>
        new(NativeArgument.FireId(value.Value));

    public static KnownNativeArgument Interior(Interior value) =>
        new(NativeArgument.Interior(value.Value));

    public static KnownNativeArgument ItemSet(ItemSet value) =>
        new(NativeArgument.ItemSet(value.Value));

    public static KnownNativeArgument GameObject(GameObject value) =>
        new(NativeArgument.GameObject(value.Value));

    public static KnownNativeArgument Ped(Ped value) =>
        new(NativeArgument.Ped(value.Value));

    public static KnownNativeArgument Pickup(Pickup value) =>
        new(NativeArgument.Pickup(value.Value));

    public static KnownNativeArgument Player(Player value) =>
        new(NativeArgument.Player(value.Value));

    public static KnownNativeArgument ScrHandle(ScrHandle value) =>
        new(NativeArgument.ScrHandle(value.Value));

    public static KnownNativeArgument Vehicle(Vehicle value) =>
        new(NativeArgument.Vehicle(value.Value));
}

public sealed class KnownNativeResult
{
    private readonly ulong[] _results;
    private readonly string? _text;

    internal KnownNativeResult(
        NativeDescriptor descriptor,
        NativeSignatureVariant variant,
        ulong[] results)
    {
        Descriptor = descriptor;
        Variant = variant;
        _results = results;
        if (variant.ReturnType is NativeAbiType.ConstCharPointer &&
            results.Length != 0 &&
            results[0] != 0)
        {
            _text = Marshal.PtrToStringUTF8(
                unchecked((nint)(nuint)results[0]));
        }
    }

    public NativeDescriptor Descriptor { get; }
    public NativeSignatureVariant Variant { get; }
    internal ReadOnlySpan<ulong> RawResults => _results;

    public bool AsBoolean() =>
        RequireScalar(NativeAbiType.Boolean32) != 0;

    public int AsInt32() =>
        unchecked((int)RequireScalar(NativeAbiType.Int32));

    public float AsFloat32() =>
        BitConverter.Int32BitsToSingle(
            unchecked((int)RequireScalar(NativeAbiType.Float32)));

    public string? AsText()
    {
        RequireReturnType(NativeAbiType.ConstCharPointer);
        return _text;
    }

    public NativeAny AsAny() =>
        new(RequireScalar(NativeAbiType.Any));

    public NativeAddress AsAddress()
    {
        NativeAbiType type = Variant.ReturnType;
        if (!IsPointer(type))
        {
            throw new InvalidOperationException(
                $"Native '{Descriptor.Name}' does not return a pointer.");
        }
        if (_results.Length == 0)
        {
            throw new InvalidDataException(
                "The native result does not contain a pointer payload.");
        }
        return new(unchecked((nuint)_results[0]));
    }

    public uint AsHash32() =>
        unchecked((uint)RequireScalar(NativeAbiType.Hash32));

    public Vector3 AsVector3()
    {
        RequireReturnType(NativeAbiType.Vector3);
        if (_results.Length < 3)
        {
            throw new InvalidDataException(
                "The native result does not contain a Vector3 payload.");
        }

        return new(
            BitConverter.Int32BitsToSingle(unchecked((int)_results[0])),
            BitConverter.Int32BitsToSingle(unchecked((int)_results[1])),
            BitConverter.Int32BitsToSingle(unchecked((int)_results[2])));
    }

    public Blip AsBlip() =>
        new(unchecked((int)RequireScalar(NativeAbiType.Blip)));

    public Cam AsCam() =>
        new(unchecked((int)RequireScalar(NativeAbiType.Cam)));

    public Entity AsEntity() =>
        new(unchecked((int)RequireScalar(NativeAbiType.Entity)));

    public FireId AsFireId() =>
        new(unchecked((int)RequireScalar(NativeAbiType.FireId)));

    public Interior AsInterior() =>
        new(unchecked((int)RequireScalar(NativeAbiType.Interior)));

    public ItemSet AsItemSet() =>
        new(unchecked((int)RequireScalar(NativeAbiType.ItemSet)));

    public GameObject AsGameObject() =>
        new(unchecked((int)RequireScalar(NativeAbiType.Object)));

    public Ped AsPed() =>
        new(unchecked((int)RequireScalar(NativeAbiType.Ped)));

    public Pickup AsPickup() =>
        new(unchecked((int)RequireScalar(NativeAbiType.Pickup)));

    public Player AsPlayer() =>
        new(unchecked((int)RequireScalar(NativeAbiType.Player)));

    public ScrHandle AsScrHandle() =>
        new(unchecked((int)RequireScalar(NativeAbiType.ScrHandle)));

    public Vehicle AsVehicle() =>
        new(unchecked((int)RequireScalar(NativeAbiType.Vehicle)));

    private ulong RequireScalar(NativeAbiType expected)
    {
        RequireReturnType(expected);
        if (_results.Length == 0)
        {
            throw new InvalidDataException(
                "The native result does not contain a scalar payload.");
        }

        return _results[0];
    }

    private void RequireReturnType(NativeAbiType expected)
    {
        if (Variant.ReturnType != expected)
        {
            throw new InvalidOperationException(
                $"Native '{Descriptor.Name}' returns " +
                $"{NativeDescriptor.NativeSyntax(Variant.ReturnType)}, not " +
                $"{NativeDescriptor.NativeSyntax(expected)}.");
        }
    }

    private static bool IsPointer(NativeAbiType type) => type is
        NativeAbiType.AnyPointer or
        NativeAbiType.Int32Pointer or
        NativeAbiType.Float32Pointer or
        NativeAbiType.Vector3Pointer or
        NativeAbiType.Boolean32Pointer or
        NativeAbiType.Hash32Pointer or
        NativeAbiType.CharPointer or
        NativeAbiType.EntityPointer or
        NativeAbiType.VehiclePointer or
        NativeAbiType.PedPointer or
        NativeAbiType.ObjectPointer or
        NativeAbiType.ScrHandlePointer or
        NativeAbiType.BlipPointer;
}

public interface IStandardNatives
{
    IGameBuildService GameBuild { get; }
    INativeCatalog Catalog { get; }
    INativeDatabaseInfo Database { get; }
}

public interface IKnownNativeInvoker
{
    void Validate(ulong hash);

    KnownNativeResult Invoke(
        ulong hash,
        IReadOnlyList<KnownNativeArgument> arguments);
}

internal sealed class StandardNativeServices(
    IGameBuildService gameBuild,
    INativeCatalog catalog,
    INativeDatabaseInfo database) : IStandardNatives
{
    public IGameBuildService GameBuild { get; } = gameBuild;
    public INativeCatalog Catalog { get; } = catalog;
    public INativeDatabaseInfo Database { get; } = database;
}

internal sealed class GameBuildService(GameBuildInfo current) :
    IGameBuildService
{
    public GameBuildInfo Current { get; } = current;

    internal static GameBuildService Detect() =>
        new(ResolveCurrentProcess());

    private static GameBuildInfo ResolveCurrentProcess()
    {
        string executablePath = Environment.ProcessPath ?? string.Empty;
        string processName = Path.GetFileNameWithoutExtension(executablePath);
        GameEdition edition = processName switch
        {
            string value when value.Equals(
                "GTA5",
                StringComparison.OrdinalIgnoreCase) =>
                GameEdition.Legacy,

            string value when value.Equals(
                "GTA5_Enhanced",
                StringComparison.OrdinalIgnoreCase) =>
                GameEdition.Enhanced,

            _ => GameEdition.Unsupported
        };

        if (executablePath.Length == 0)
        {
            return new(
                edition,
                -1,
                string.Empty,
                string.Empty);
        }

        try
        {
            FileVersionInfo version =
                FileVersionInfo.GetVersionInfo(executablePath);
            string productVersion = version.ProductVersion ?? string.Empty;
            int build = version.ProductBuildPart > 0
                ? version.ProductBuildPart
                : ParseBuild(productVersion);

            return new(
                edition,
                build,
                executablePath,
                productVersion);
        }
        catch
        {
            return new(
                edition,
                -1,
                executablePath,
                string.Empty);
        }
    }

    private static int ParseBuild(string productVersion)
    {
        if (productVersion.Length == 0)
        {
            return -1;
        }

        string[] components = productVersion.Split(
            ['.', ' ', '-', '+'],
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

        return components.Length >= 3 &&
            int.TryParse(
                components[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int build) &&
            build >= 0
                ? build
                : -1;
    }
}

internal sealed class NativeCatalog : INativeCatalog, INativeDatabaseInfo
{
    private readonly NativeDescriptor[] _entries;
    private readonly Dictionary<string, NativeDescriptor> _byName;
    private readonly Dictionary<ulong, NativeDescriptor> _byHash;

    private NativeCatalog(
        NativeDescriptor[] entries,
        NativeDatabaseIdentity identity)
    {
        _entries = entries;
        Identity = identity;
        _byName = new(entries.Length, StringComparer.Ordinal);
        _byHash = [with(entries.Length)];

        foreach (NativeDescriptor descriptor in entries)
        {
            if (!_byName.TryAdd(descriptor.Name, descriptor))
            {
                throw new InvalidDataException(
                    $"Duplicate native name '{descriptor.Name}'.");
            }

            if (!_byHash.TryAdd(descriptor.Hash, descriptor))
            {
                throw new InvalidDataException(
                    $"Duplicate native hash 0x{descriptor.Hash:X16}.");
            }
        }
    }

    public IReadOnlyList<NativeDescriptor> Entries => _entries;
    public NativeDatabaseIdentity Identity { get; }

    public bool TryGet(
        string name,
        [NotNullWhen(true)] out NativeDescriptor? descriptor)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            descriptor = null;
            return false;
        }

        return _byName.TryGetValue(name.Trim(), out descriptor);
    }

    public bool TryGet(
        ulong hash,
        [NotNullWhen(true)] out NativeDescriptor? descriptor) =>
        _byHash.TryGetValue(hash, out descriptor);

    internal NativeDescriptor GetByIndex(int index) =>
        (uint)index < (uint)_entries.Length
            ? _entries[index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    internal static Task<NativeCatalog> LoadAsync(
        CancellationToken cancellationToken) =>
        Task.Run(Load, cancellationToken);

    private static NativeCatalog Load()
    {
        byte[] decoded = GC.AllocateUninitializedArray<byte>(
            NativeCatalogData.DecodedLength);
        if (!BrotliDecoder.TryDecompress(
                NativeCatalogData.CompressedCatalog,
                decoded,
                out int bytesWritten) ||
            bytesWritten != decoded.Length)
        {
            throw new InvalidDataException(
                "The ASN catalog could not be decompressed to its expected length.");
        }

        string decodedSha256 = Convert.ToHexString(
            SHA256.HashData(decoded));
        if (!decodedSha256.Equals(
                NativeCatalogData.DecodedSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The ASN catalog checksum is invalid.");
        }

        using MemoryStream stream = new(decoded, writable: false);
        using BinaryReader reader = new(
            stream,
            Encoding.UTF8,
            leaveOpen: false);

        Span<byte> magic = stackalloc byte[4];
        if (reader.Read(magic) != magic.Length ||
            !magic.SequenceEqual("ASNC"u8))
        {
            throw new InvalidDataException(
                "The ASN catalog magic is invalid.");
        }

        ushort format = reader.ReadUInt16();
        if (format != NativeCatalogData.FormatVersion)
        {
            throw new InvalidDataException(
                $"ASN catalog format {format} does not match " +
                $"NativeCatalogData.cs format {NativeCatalogData.FormatVersion}.");
        }

        byte[] sourceFingerprint = reader.ReadBytes(32);
        if (sourceFingerprint.Length != 32)
        {
            throw new EndOfStreamException(
                "The ASN source fingerprint is truncated.");
        }

        int descriptorCount = checked((int)ReadVarUInt32(reader));
        int stringCount = checked((int)ReadVarUInt32(reader));
        string[] strings = new string[stringCount];
        for (int index = 0; index < strings.Length; ++index)
        {
            int length = checked((int)ReadVarUInt32(reader));
            byte[] value = reader.ReadBytes(length);
            if (value.Length != length)
            {
                throw new EndOfStreamException(
                    "The ASN catalog string pool is truncated.");
            }

            strings[index] = Encoding.UTF8.GetString(value);
        }

        NativeDescriptor[] entries = new NativeDescriptor[descriptorCount];
        for (int index = 0; index < entries.Length; ++index)
        {
            entries[index] = ReadDescriptor(
                reader,
                strings,
                index);
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                "The ASN catalog contains trailing data.");
        }

        if (entries.Length != NativeCatalogData.DescriptorCount)
        {
            throw new InvalidDataException(
                "The ASN descriptor count does not match NativeCatalogData.cs.");
        }

        NativeDatabaseIdentity identity = new(
            Convert.ToHexString(sourceFingerprint),
            decodedSha256,
            entries.Length,
            NativeCatalogData.CompressedCatalog.Length,
            decoded.Length);
        return new(entries, identity);
    }

    private static NativeDescriptor ReadDescriptor(
        BinaryReader reader,
        string[] strings,
        int index)
    {
        ulong hash = reader.ReadUInt64();
        string name = GetString(strings, ReadVarUInt32(reader));
        byte editions = reader.ReadByte();
        if ((editions & ~0x03) != 0 || editions == 0)
        {
            throw new InvalidDataException(
                $"Native 0x{hash:X16} has invalid edition flags 0x{editions:X2}.");
        }

        NativeSignatureVariant? legacy = (editions & 0x01) != 0
            ? ReadVariant(reader, strings)
            : null;
        NativeSignatureVariant? enhanced = (editions & 0x02) != 0
            ? ReadVariant(reader, strings)
            : null;
        return new(
            index,
            hash,
            name,
            legacy,
            enhanced);
    }

    private static NativeSignatureVariant ReadVariant(
        BinaryReader reader,
        string[] strings)
    {
        int minimumBuild = DecodeBuild(ReadVarUInt32(reader));
        if (minimumBuild < 0)
        {
            throw new InvalidDataException(
                "A present native edition variant cannot be unsupported.");
        }

        NativeAbiType returnType = ReadAbiType(reader);
        NativeParameterDescriptor[] parameters =
            ReadParameters(reader, strings);
        return new(
            minimumBuild,
            returnType,
            parameters);
    }

    private static NativeParameterDescriptor[] ReadParameters(
        BinaryReader reader,
        string[] strings)
    {
        int parameterCount = checked((int)ReadVarUInt32(reader));
        NativeParameterDescriptor[] parameters =
            new NativeParameterDescriptor[parameterCount];
        for (int index = 0; index < parameters.Length; ++index)
        {
            NativeAbiType type = ReadAbiType(reader);
            string name = GetString(strings, ReadVarUInt32(reader));
            parameters[index] = new(name, type);
        }

        return parameters;
    }

    private static NativeAbiType ReadAbiType(BinaryReader reader)
    {
        byte value = reader.ReadByte();
        NativeAbiType type = (NativeAbiType)value;
        return Enum.IsDefined(type)
            ? type
            : throw new InvalidDataException(
                $"The ASN catalog contains unknown ABI type {value}.");
    }

    private static string GetString(string[] strings, uint index) =>
        index < strings.Length
            ? strings[index]
            : throw new InvalidDataException(
                "The ASN catalog contains an invalid string-pool index.");

    private static int DecodeBuild(uint encoded) =>
        encoded == 0
            ? -1
            : checked((int)encoded - 1);

    private static uint ReadVarUInt32(BinaryReader reader)
    {
        uint value = 0;
        int shift = 0;
        while (shift < 35)
        {
            byte current = reader.ReadByte();
            value |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        throw new InvalidDataException(
            "The ASN catalog contains an invalid variable integer.");
    }
}

internal abstract class NativeOutputBinding
{
    internal abstract NativeAbiType PointerType { get; }
    internal abstract int ByteSize { get; }
    internal abstract void Initialize(nint address);
    internal abstract void Capture(nint address);
}

internal sealed class Int32OutputBinding(
    KnownNativeInt32Output output) : NativeOutputBinding
{
    private readonly KnownNativeInt32Output _output = output;

    internal override NativeAbiType PointerType =>
        NativeAbiType.Int32Pointer;

    internal override int ByteSize => sizeof(int);

    internal override void Initialize(nint address) =>
        Marshal.WriteInt32(address, 0);

    internal override void Capture(nint address) =>
        _output.Value = Marshal.ReadInt32(address);
}

internal sealed class Int32BufferOutputBinding(
    KnownNativeInt32BufferOutput output) : NativeOutputBinding
{
    private readonly KnownNativeInt32BufferOutput _output = output;

    internal override NativeAbiType PointerType =>
        NativeAbiType.AnyPointer;

    internal override int ByteSize =>
        checked(_output.InitialValues.Length * sizeof(int));

    internal override void Initialize(nint address) =>
        Marshal.Copy(
            _output.InitialValues,
            0,
            address,
            _output.InitialValues.Length);

    internal override void Capture(nint address)
    {
        int[] values = new int[_output.InitialValues.Length];
        Marshal.Copy(address, values, 0, values.Length);
        _output.Values = new(values);
    }
}

internal sealed class Vector3OutputBinding(
    KnownNativeVector3Output output) : NativeOutputBinding
{
    private const int PayloadSize = 0x18;
    private const int XOffset = 0x00;
    private const int YOffset = 0x08;
    private const int ZOffset = 0x10;

    private readonly KnownNativeVector3Output _output = output;

    internal override NativeAbiType PointerType =>
        NativeAbiType.Vector3Pointer;

    internal override int ByteSize => PayloadSize;

    internal override void Initialize(nint address)
    {
        Marshal.WriteInt64(address, XOffset, 0L);
        Marshal.WriteInt64(address, YOffset, 0L);
        Marshal.WriteInt64(address, ZOffset, 0L);
    }

    internal override void Capture(nint address) =>
        _output.Value = new(
            ReadFloat(address, XOffset),
            ReadFloat(address, YOffset),
            ReadFloat(address, ZOffset));

    private static float ReadFloat(nint address, int offset) =>
        BitConverter.Int32BitsToSingle(
            Marshal.ReadInt32(address, offset));
}

internal readonly struct NativeArgument
{
    private NativeArgument(
        NativeAbiType type,
        ulong rawValue,
        string? textValue,
        NativeOutputBinding? output = null)
    {
        Type = type;
        RawValue = rawValue;
        TextValue = textValue;
        Output = output;
    }

    internal NativeAbiType Type { get; }
    internal ulong RawValue { get; }
    internal string? TextValue { get; }
    internal NativeOutputBinding? Output { get; }

    internal static NativeArgument Boolean(bool value) =>
        new(NativeAbiType.Boolean32, value ? 1UL : 0UL, null);

    internal static NativeArgument Int32<T>(T value)
        where T : INumberBase<T>
    {
        if (!T.IsInteger(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A GTA Int32 argument must represent an integer value.");
        }

        int normalized = int.CreateChecked(value);
        return new(
            NativeAbiType.Int32,
            unchecked((ulong)(long)normalized),
            null);
    }

    internal static NativeArgument Float32<T>(T value)
        where T : INumberBase<T>
    {
        float normalized = float.CreateChecked(value);
        if (!float.IsFinite(normalized))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A GTA Float32 argument must be finite.");
        }

        return new(
            NativeAbiType.Float32,
            unchecked((uint)BitConverter.SingleToInt32Bits(normalized)),
            null);
    }

    internal static NativeArgument Text(string? value) =>
        new(NativeAbiType.ConstCharPointer, 0, value);

    internal static NativeArgument Hash32<T>(T value)
        where T : INumberBase<T>
    {
        if (!T.IsInteger(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A GTA Hash32 argument must represent an integer value.");
        }

        uint normalized = uint.CreateChecked(value);
        return new(NativeAbiType.Hash32, normalized, null);
    }

    internal static NativeArgument Any(NativeAny value) =>
        new(NativeAbiType.Any, value.Value, null);

    internal static NativeArgument Any(ulong value) =>
        new(NativeAbiType.Any, value, null);

    internal static NativeArgument Any<T>(T value)
        where T : INumberBase<T>
    {
        if (!T.IsInteger(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A GTA Any numeric argument must represent an integer value.");
        }

        ulong normalized = T.IsNegative(value)
            ? unchecked((ulong)long.CreateChecked(value))
            : ulong.CreateChecked(value);
        return new(NativeAbiType.Any, normalized, null);
    }

    internal static NativeArgument BindOutput(
        NativeAbiType type,
        NativeOutputBinding output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (type != output.PointerType)
        {
            throw new ArgumentException(
                "The native output binding does not match the ABI pointer type.",
                nameof(output));
        }

        return new(type, 0, null, output);
    }

    internal static NativeArgument Blip(int value) =>
        Handle(NativeAbiType.Blip, value);
    internal static NativeArgument Cam(int value) =>
        Handle(NativeAbiType.Cam, value);
    internal static NativeArgument Entity(int value) =>
        Handle(NativeAbiType.Entity, value);
    internal static NativeArgument FireId(int value) =>
        Handle(NativeAbiType.FireId, value);
    internal static NativeArgument Interior(int value) =>
        Handle(NativeAbiType.Interior, value);
    internal static NativeArgument ItemSet(int value) =>
        Handle(NativeAbiType.ItemSet, value);
    internal static NativeArgument GameObject(int value) =>
        Handle(NativeAbiType.Object, value);
    internal static NativeArgument Ped(int value) =>
        Handle(NativeAbiType.Ped, value);
    internal static NativeArgument Pickup(int value) =>
        Handle(NativeAbiType.Pickup, value);
    internal static NativeArgument Player(int value) =>
        Handle(NativeAbiType.Player, value);
    internal static NativeArgument ScrHandle(int value) =>
        Handle(NativeAbiType.ScrHandle, value);
    internal static NativeArgument Vehicle(int value) =>
        Handle(NativeAbiType.Vehicle, value);

    private static NativeArgument Handle(NativeAbiType type, int value) =>
        new(type, unchecked((ulong)(long)value), null);
}

internal sealed class CatalogNativeCallAdmissionPolicy(
    NativeCatalog catalog,
    IGameBuildService gameBuild) : INativeCallAdmissionPolicy
{
    public NativeCallAdmissionDecision Evaluate(
        ulong hash,
        int argumentCount,
        int requestedResultCount)
    {
        if (!catalog.TryGet(hash, out NativeDescriptor? descriptor))
        {
            return new(NativeCallAdmissionStatus.UnknownHash);
        }

        GameBuildInfo game = gameBuild.Current;
        if (!game.IsSupported)
        {
            return new(NativeCallAdmissionStatus.UnsupportedTarget);
        }

        NativeSignatureVariant? variant =
            descriptor.GetVariant(game.Edition);
        if (variant is null || game.Build < variant.MinimumBuild)
        {
            return new(NativeCallAdmissionStatus.UnsupportedTarget);
        }
        if (argumentCount != variant.Parameters.Count)
        {
            return new(NativeCallAdmissionStatus.ArgumentCountMismatch);
        }

        int expectedResultCount = variant.ReturnType switch
        {
            NativeAbiType.Void => 0,
            NativeAbiType.Vector3 => 3,
            _ => 1
        };
        if (requestedResultCount != expectedResultCount)
        {
            return new(NativeCallAdmissionStatus.ResultCountMismatch);
        }

        return new(NativeCallAdmissionStatus.Allowed);
    }
}

internal enum NativeExecutionStatus
{
    Success = 0,
    UnknownDescriptor = 1,
    UnsupportedGame = 3,
    UnsupportedEdition = 4,
    UnsupportedBuild = 5,
    ArgumentCountMismatch = 6,
    ArgumentTypeMismatch = 7,
    ReturnTypeMismatch = 8,
    TransportInvalidRequest = 9,
    TransportLimitExceeded = 10,
    NativeReturnedNull = 11,
    SessionStopping = 12,
    NativeAdmissionUnavailable = 13,
    NativeAdmissionRejected = 14,
    TransportFailure = 15
}

internal readonly record struct NativeExecutionResult(
    NativeExecutionStatus Status,
    NativeSignatureVariant? Variant,
    ulong[] Results)
{
    internal bool Succeeded => Status is NativeExecutionStatus.Success;
}

public sealed class UnknownNativeHashException : KeyNotFoundException
{
    internal UnknownNativeHashException(ulong hash)
        : base(
            $"The ASN catalog does not contain native hash 0x{hash:X16}. " +
            "The hash was blocked before Script Hook V execution.")
    {
        Hash = hash;
    }

    public ulong Hash { get; }
}

public sealed class NativeExecutionException : InvalidOperationException
{
    internal NativeExecutionException(
        NativeDescriptor descriptor,
        NativeExecutionStatus status)
        : base(
            $"Native '{descriptor.Name}' (0x{descriptor.Hash:X16}) failed " +
            $"with status {status}.")
    {
        NativeName = descriptor.Name;
        Hash = descriptor.Hash;
        StatusName = status.ToString();
    }

    public string NativeName { get; }
    public ulong Hash { get; }
    public string StatusName { get; }
}

internal sealed class NativeGateway(
    IRawNativeTransport transport,
    IGameBuildService gameBuild,
    NativeCatalog catalog)
{
    internal NativeExecutionResult InvokeGenerated(
        NativeDescriptor descriptor,
        NativeAbiType expectedReturnType,
        ReadOnlySpan<NativeArgument> arguments) =>
        Invoke(descriptor, arguments, expectedReturnType);

    internal NativeExecutionStatus ValidateKnownHash(
        NativeDescriptor descriptor) =>
        ValidateDescriptor(descriptor, out _);

    internal NativeExecutionResult InvokeKnownHash(
        NativeDescriptor descriptor,
        ReadOnlySpan<NativeArgument> arguments) =>
        Invoke(descriptor, arguments, expectedReturnType: null);

    private NativeExecutionResult Invoke(
        NativeDescriptor descriptor,
        ReadOnlySpan<NativeArgument> arguments,
        NativeAbiType? expectedReturnType)
    {
        NativeExecutionStatus validation = ValidateDescriptor(
            descriptor,
            out NativeSignatureVariant? variant);
        if (validation is not NativeExecutionStatus.Success ||
            variant is null)
        {
            return Failure(validation);
        }

        if (expectedReturnType is NativeAbiType expectedReturn &&
            variant.ReturnType != expectedReturn)
        {
            return Failure(NativeExecutionStatus.ReturnTypeMismatch);
        }

        if (arguments.Length != variant.Parameters.Count)
        {
            return Failure(NativeExecutionStatus.ArgumentCountMismatch);
        }

        ulong[]? rawArgumentBuffer = arguments.Length == 0
            ? null
            : ArrayPool<ulong>.Shared.Rent(arguments.Length);
        IReadOnlyList<ulong> rawArguments = rawArgumentBuffer is null
            ? Array.Empty<ulong>()
            : new ArraySegment<ulong>(
                rawArgumentBuffer,
                0,
                arguments.Length);
        List<nint>? textAllocations = null;
        List<NativeOutputAllocation>? outputAllocations = null;
        try
        {
            for (int index = 0; index < arguments.Length; ++index)
            {
                NativeArgument argument = arguments[index];
                NativeAbiType expected = variant.Parameters[index].Type;
                if (argument.Type != expected)
                {
                    return Failure(
                        NativeExecutionStatus.ArgumentTypeMismatch);
                }

                if (argument.Output is not null)
                {
                    nint pointer = Marshal.AllocHGlobal(
                        argument.Output.ByteSize);
                    outputAllocations ??= [];
                    outputAllocations.Add(
                        new(pointer, argument.Output));
                    argument.Output.Initialize(pointer);
                    rawArgumentBuffer![index] =
                        unchecked((ulong)(nuint)pointer);
                    continue;
                }

                if (argument.Type is NativeAbiType.ConstCharPointer)
                {
                    if (argument.TextValue is null)
                    {
                        rawArgumentBuffer![index] = 0;
                    }
                    else
                    {
                        nint pointer = Marshal.StringToCoTaskMemUTF8(
                            argument.TextValue);
                        textAllocations ??= [];
                        textAllocations.Add(pointer);
                        rawArgumentBuffer![index] =
                            unchecked((ulong)(nuint)pointer);
                    }
                }
                else
                {
                    rawArgumentBuffer![index] = argument.RawValue;
                }
            }

            int resultCount = variant.ReturnType switch
            {
                NativeAbiType.Void => 0,
                NativeAbiType.Vector3 => 3,
                _ => 1
            };

            RawNativeCallResult result = transport.Invoke(
                descriptor.Hash,
                rawArguments,
                resultCount);

            if (result.Status is RawNativeCallStatus.Success &&
                outputAllocations is not null)
            {
                foreach (NativeOutputAllocation allocation in outputAllocations)
                {
                    allocation.Binding.Capture(allocation.Address);
                }
            }

            return new(
                MapStatus(result.Status),
                result.Status is RawNativeCallStatus.Success
                    ? variant
                    : null,
                result.Status is RawNativeCallStatus.Success
                    ? result.Results
                    : []);
        }
        finally
        {
            if (textAllocations is not null)
            {
                foreach (nint allocation in textAllocations)
                {
                    Marshal.FreeCoTaskMem(allocation);
                }
            }

            if (outputAllocations is not null)
            {
                foreach (NativeOutputAllocation allocation in outputAllocations)
                {
                    Marshal.FreeHGlobal(allocation.Address);
                }
            }

            if (rawArgumentBuffer is not null)
            {
                ArrayPool<ulong>.Shared.Return(rawArgumentBuffer);
            }
        }
    }

    private NativeExecutionStatus ValidateDescriptor(
        NativeDescriptor descriptor,
        out NativeSignatureVariant? variant)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        variant = null;

        if (!ReferenceEquals(catalog.GetByIndex(descriptor.Index), descriptor))
        {
            return NativeExecutionStatus.UnknownDescriptor;
        }

        GameBuildInfo game = gameBuild.Current;
        if (!game.Edition.IsSupported())
        {
            return NativeExecutionStatus.UnsupportedGame;
        }

        variant = descriptor.GetVariant(game.Edition);
        if (variant is null)
        {
            return NativeExecutionStatus.UnsupportedEdition;
        }

        if (game.Build < variant.MinimumBuild)
        {
            return NativeExecutionStatus.UnsupportedBuild;
        }

        return NativeExecutionStatus.Success;
    }

    private readonly record struct NativeOutputAllocation(
        nint Address,
        NativeOutputBinding Binding);

    private static NativeExecutionResult Failure(
        NativeExecutionStatus status) =>
        new(status, null, []);

    private static NativeExecutionStatus MapStatus(
        RawNativeCallStatus status) => status switch
    {
        RawNativeCallStatus.Success =>
            NativeExecutionStatus.Success,

        RawNativeCallStatus.InvalidRequest =>
            NativeExecutionStatus.TransportInvalidRequest,

        RawNativeCallStatus.TooManyArguments or
        RawNativeCallStatus.TooManyResults =>
            NativeExecutionStatus.TransportLimitExceeded,

        RawNativeCallStatus.NativeReturnedNull =>
            NativeExecutionStatus.NativeReturnedNull,

        RawNativeCallStatus.SessionStopping =>
            NativeExecutionStatus.SessionStopping,

        RawNativeCallStatus.AdmissionUnavailable =>
            NativeExecutionStatus.NativeAdmissionUnavailable,

        RawNativeCallStatus.AdmissionRejected =>
            NativeExecutionStatus.NativeAdmissionRejected,

        _ => NativeExecutionStatus.TransportFailure
    };
}

internal sealed class KnownNativeInvoker(
    NativeCatalog catalog,
    NativeGateway gateway) : IKnownNativeInvoker
{
    public void Validate(ulong hash)
    {
        if (!catalog.TryGet(hash, out NativeDescriptor? descriptor))
        {
            throw new UnknownNativeHashException(hash);
        }

        NativeExecutionStatus status = gateway.ValidateKnownHash(descriptor);
        if (status is not NativeExecutionStatus.Success)
        {
            throw new NativeExecutionException(descriptor, status);
        }
    }

    public KnownNativeResult Invoke(
        ulong hash,
        IReadOnlyList<KnownNativeArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!catalog.TryGet(hash, out NativeDescriptor? descriptor))
        {
            throw new UnknownNativeHashException(hash);
        }

        NativeArgument[] values = new NativeArgument[arguments.Count];
        for (int index = 0; index < values.Length; ++index)
        {
            values[index] = arguments[index].Value;
        }

        NativeExecutionResult result =
            gateway.InvokeKnownHash(descriptor, values);
        if (!result.Succeeded || result.Variant is null)
        {
            throw new NativeExecutionException(
                descriptor,
                result.Status);
        }

        return new(
            descriptor,
            result.Variant,
            result.Results);
    }
}

public static partial class StandardNatives
{
    private static readonly Lock Gate = new();
    private static NativeCatalog? s_catalog;
    private static NativeGateway? s_gateway;

    internal static void Bind(
        NativeCatalog catalog,
        NativeGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(gateway);

        lock (Gate)
        {
            if (s_catalog is not null || s_gateway is not null)
            {
                throw new InvalidOperationException(
                    "Alloc8orStandardNatives is already bound.");
            }

            s_catalog = catalog;
            s_gateway = gateway;
        }
    }

    internal static void Unbind()
    {
        lock (Gate)
        {
            s_gateway = null;
            s_catalog = null;
        }
    }

    private static (NativeDescriptor Descriptor, ulong[] Results) InvokeCore(
        int descriptorIndex,
        NativeAbiType expectedReturnType,
        ReadOnlySpan<NativeArgument> arguments)
    {
        NativeCatalog catalog = Volatile.Read(ref s_catalog) ??
            throw new InvalidOperationException(
                "Alloc8orStandardNatives has not been initialized.");
        NativeGateway gateway = Volatile.Read(ref s_gateway) ??
            throw new InvalidOperationException(
                "The standard native gateway is unavailable.");

        NativeDescriptor descriptor = catalog.GetByIndex(descriptorIndex);
        NativeExecutionResult result =
            gateway.InvokeGenerated(
                descriptor,
                expectedReturnType,
                arguments);
        if (!result.Succeeded || result.Variant is null)
        {
            throw new NativeExecutionException(
                descriptor,
                result.Status);
        }

        return (descriptor, result.Results);
    }

    internal static void InvokeVoid(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        _ = InvokeCore(
            descriptorIndex,
            NativeAbiType.Void,
            arguments);

    internal static bool InvokeBoolean(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        InvokeCore(
            descriptorIndex,
            NativeAbiType.Boolean32,
            arguments).Results[0] != 0;

    internal static int InvokeInt32(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        unchecked((int)InvokeCore(
            descriptorIndex,
            NativeAbiType.Int32,
            arguments).Results[0]);

    internal static float InvokeFloat32(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        BitConverter.Int32BitsToSingle(unchecked((int)InvokeCore(
            descriptorIndex,
            NativeAbiType.Float32,
            arguments).Results[0]));

    internal static string? InvokeText(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments)
    {
        ulong value = InvokeCore(
            descriptorIndex,
            NativeAbiType.ConstCharPointer,
            arguments).Results[0];
        return value == 0
            ? null
            : Marshal.PtrToStringUTF8(unchecked((nint)(nuint)value));
    }

    internal static NativeAny InvokeAny(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeCore(
            descriptorIndex,
            NativeAbiType.Any,
            arguments).Results[0]);

    internal static uint InvokeHash32(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        unchecked((uint)InvokeCore(
            descriptorIndex,
            NativeAbiType.Hash32,
            arguments).Results[0]);

    internal static Vector3 InvokeVector3(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments)
    {
        ulong[] results = InvokeCore(
            descriptorIndex,
            NativeAbiType.Vector3,
            arguments).Results;
        return new(
            BitConverter.Int32BitsToSingle(unchecked((int)results[0])),
            BitConverter.Int32BitsToSingle(unchecked((int)results[1])),
            BitConverter.Int32BitsToSingle(unchecked((int)results[2])));
    }

    private static int InvokeHandle(
        int descriptorIndex,
        ReadOnlySpan<NativeArgument> arguments,
        NativeAbiType expected) =>
        unchecked((int)InvokeCore(
            descriptorIndex,
            expected,
            arguments).Results[0]);

    internal static Blip InvokeBlip(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.Blip));

    internal static Cam InvokeCam(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.Cam));

    internal static Entity InvokeEntity(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.Entity));

    internal static FireId InvokeFireId(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.FireId));

    internal static Interior InvokeInterior(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.Interior));

    internal static ItemSet InvokeItemSet(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.ItemSet));

    internal static GameObject InvokeGameObject(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.Object));

    internal static Ped InvokePed(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.Ped));

    internal static Pickup InvokePickup(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.Pickup));

    internal static Player InvokePlayer(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.Player));

    internal static ScrHandle InvokeScrHandle(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.ScrHandle));

    internal static Vehicle InvokeVehicle(
        int descriptorIndex,
        params ReadOnlySpan<NativeArgument> arguments) =>
        new(InvokeHandle(descriptorIndex, arguments, NativeAbiType.Vehicle));
}