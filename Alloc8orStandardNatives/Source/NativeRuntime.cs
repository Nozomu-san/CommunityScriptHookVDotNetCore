using CommunityScriptHookVDotNetCore.Source;
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

public enum NativeExposure : byte
{
    SafePublic = 0,
    ManualContractRequired = 1,
    CatalogOnly = 2
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

public readonly struct NativeFloat32 : IEquatable<NativeFloat32>
{
    private readonly float _value;

    private NativeFloat32(float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A GTA Float32 argument must be finite.");
        }

        _value = value;
    }

    internal float Value => _value;

    public static NativeFloat32 FromSingle(float value) => new(value);

    public static NativeFloat32 FromDouble(double value)
    {
        if (!double.IsFinite(value) ||
            value > float.MaxValue ||
            value < -float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "The value cannot be represented by the GTA Float32 ABI.");
        }

        return new NativeFloat32((float)value);
    }

    public static NativeFloat32 FromDecimal(decimal value) =>
        new((float)value);

    public static implicit operator NativeFloat32(float value) =>
        FromSingle(value);

    public static implicit operator NativeFloat32(double value) =>
        FromDouble(value);

    public static implicit operator NativeFloat32(decimal value) =>
        FromDecimal(value);

    public bool Equals(NativeFloat32 other) => _value.Equals(other._value);
    public override bool Equals(object? obj) =>
        obj is NativeFloat32 other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public override string ToString() =>
        _value.ToString("R", CultureInfo.InvariantCulture);

    public static bool operator ==(NativeFloat32 left, NativeFloat32 right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(NativeFloat32 left, NativeFloat32 right)
    {
        return !(left == right);
    }
}

public sealed record NativeParameterDescriptor(
    string Name,
    NativeAbiType Type);

public sealed class NativeSignatureVariant
{
    internal NativeSignatureVariant(
        int minimumBuild,
        NativeAbiType returnType,
        NativeParameterDescriptor[] parameters,
        NativeExposure exposure)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumBuild);
        MinimumBuild = minimumBuild;
        ReturnType = returnType;
        Parameters = parameters ?? throw new ArgumentNullException(
            nameof(parameters));
        Exposure = exposure;
    }

    public int MinimumBuild { get; }
    public NativeAbiType ReturnType { get; }
    public IReadOnlyList<NativeParameterDescriptor> Parameters { get; }
    public NativeExposure Exposure { get; }

    internal bool HasSameWireContract(NativeSignatureVariant other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReturnType != other.ReturnType ||
            Parameters.Count != other.Parameters.Count)
        {
            return false;
        }

        for (int index = 0; index < Parameters.Count; ++index)
        {
            if (Parameters[index].Type != other.Parameters[index].Type)
            {
                return false;
            }
        }

        return true;
    }
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

public readonly struct KnownNativeArgument
{
    private readonly NativeArgument _value;

    private KnownNativeArgument(NativeArgument value)
    {
        _value = value;
    }

    internal NativeArgument Value => _value;

    public static KnownNativeArgument Boolean(bool value) =>
        new(NativeArgument.Boolean(value));

    public static KnownNativeArgument Int32(int value) =>
        new(NativeArgument.Int32(value));

    public static KnownNativeArgument Float32(NativeFloat32 value) =>
        new(NativeArgument.Float32(value.Value));

    public static KnownNativeArgument Float32(float value) =>
        Float32(NativeFloat32.FromSingle(value));

    public static KnownNativeArgument Float32(double value) =>
        Float32(NativeFloat32.FromDouble(value));

    public static KnownNativeArgument Float32(decimal value) =>
        Float32(NativeFloat32.FromDecimal(value));

    public static KnownNativeArgument Text(string? value) =>
        new(NativeArgument.Text(value));

    public static KnownNativeArgument Hash32(uint value) =>
        new(NativeArgument.Hash32(value));

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

    internal KnownNativeResult(
        NativeDescriptor descriptor,
        NativeSignatureVariant variant,
        ulong[] results)
    {
        Descriptor = descriptor;
        Variant = variant;
        _results = results;
    }

    public NativeDescriptor Descriptor { get; }
    public NativeSignatureVariant Variant { get; }
    public IReadOnlyList<ulong> RawResults => _results;

    public bool AsBoolean() =>
        RequireScalar(NativeAbiType.Boolean32) != 0;

    public int AsInt32() =>
        unchecked((int)RequireScalar(NativeAbiType.Int32));

    public float AsFloat32() =>
        BitConverter.Int32BitsToSingle(
            unchecked((int)RequireScalar(NativeAbiType.Float32)));

    public string? AsText()
    {
        ulong value = RequireScalar(NativeAbiType.ConstCharPointer);
        return value == 0
            ? null
            : Marshal.PtrToStringUTF8(unchecked((nint)(nuint)value));
    }

    public NativeAny AsAny() =>
        new(RequireScalar(NativeAbiType.Any));

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

        return new Vector3(
            BitConverter.Int32BitsToSingle(unchecked((int)_results[0])),
            BitConverter.Int32BitsToSingle(unchecked((int)_results[1])),
            BitConverter.Int32BitsToSingle(unchecked((int)_results[2])));
    }

    public int AsHandle(NativeAbiType expectedHandleType)
    {
        if (expectedHandleType is < NativeAbiType.Blip or
            > NativeAbiType.Vehicle)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedHandleType),
                expectedHandleType,
                "The requested ABI type is not a handle.");
        }

        return unchecked((int)RequireScalar(expectedHandleType));
    }

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
}

public interface IKnownNativeInvoker
{
    KnownNativeResult Invoke(
        ulong hash,
        ReadOnlySpan<KnownNativeArgument> arguments);
}

public interface IStandardNatives
{
    IGameBuildService GameBuild { get; }
    INativeCatalog Catalog { get; }
    INativeDatabaseInfo Database { get; }
    IKnownNativeInvoker Known { get; }
}

internal sealed class StandardNativeServices(
    IGameBuildService gameBuild,
    INativeCatalog catalog,
    INativeDatabaseInfo database,
    IKnownNativeInvoker known) : IStandardNatives
{
    public IGameBuildService GameBuild { get; } = gameBuild;
    public INativeCatalog Catalog { get; } = catalog;
    public INativeDatabaseInfo Database { get; } = database;
    public IKnownNativeInvoker Known { get; } = known;
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
            return new GameBuildInfo(
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

            return new GameBuildInfo(
                edition,
                build,
                executablePath,
                productVersion);
        }
        catch
        {
            return new GameBuildInfo(
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
    private const ushort MinimumSupportedFormatVersion = 1;
    private const ushort MaximumSupportedFormatVersion = 2;
    private readonly NativeDescriptor[] _entries;
    private readonly Dictionary<string, NativeDescriptor> _byName;
    private readonly Dictionary<ulong, NativeDescriptor> _byHash;

    private NativeCatalog(
        NativeDescriptor[] entries,
        NativeDatabaseIdentity identity)
    {
        _entries = entries;
        Identity = identity;
        _byName = new Dictionary<string, NativeDescriptor>(
            entries.Length,
            StringComparer.Ordinal);
        _byHash = new Dictionary<ulong, NativeDescriptor>(entries.Length);

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
        if (format < MinimumSupportedFormatVersion ||
            format > MaximumSupportedFormatVersion ||
            format != NativeCatalogData.FormatVersion)
        {
            throw new InvalidDataException(
                $"ASN catalog format {format} is unsupported or does not " +
                "match NativeCatalogData.cs.");
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
            entries[index] = format switch
            {
                1 => ReadVersion1Descriptor(
                    reader,
                    strings,
                    index),
                2 => ReadVersion2Descriptor(
                    reader,
                    strings,
                    index),
                _ => throw new InvalidDataException(
                    $"ASN catalog format {format} is unsupported.")
            };
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
        return new NativeCatalog(entries, identity);
    }

    private static NativeDescriptor ReadVersion1Descriptor(
        BinaryReader reader,
        string[] strings,
        int index)
    {
        ulong hash = reader.ReadUInt64();
        string name = GetString(strings, ReadVarUInt32(reader));
        int legacyBuild = DecodeBuild(ReadVarUInt32(reader));
        int enhancedBuild = DecodeBuild(ReadVarUInt32(reader));
        NativeAbiType returnType = ReadAbiType(reader);
        NativeExposure exposure = ReadExposure(reader);
        NativeParameterDescriptor[] parameters =
            ReadParameters(reader, strings);

        NativeSignatureVariant? legacy = legacyBuild < 0
            ? null
            : new NativeSignatureVariant(
                legacyBuild,
                returnType,
                parameters,
                exposure);
        NativeSignatureVariant? enhanced = enhancedBuild < 0
            ? null
            : new NativeSignatureVariant(
                enhancedBuild,
                returnType,
                CloneParameters(parameters),
                exposure);
        return new NativeDescriptor(
            index,
            hash,
            name,
            legacy,
            enhanced);
    }

    private static NativeDescriptor ReadVersion2Descriptor(
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
        return new NativeDescriptor(
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
        NativeExposure exposure = ReadExposure(reader);
        NativeParameterDescriptor[] parameters =
            ReadParameters(reader, strings);
        return new NativeSignatureVariant(
            minimumBuild,
            returnType,
            parameters,
            exposure);
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
            parameters[index] = new NativeParameterDescriptor(name, type);
        }

        return parameters;
    }

    private static NativeParameterDescriptor[] CloneParameters(
        NativeParameterDescriptor[] parameters) =>
        [.. parameters.Select(static parameter =>
            new NativeParameterDescriptor(parameter.Name, parameter.Type))];

    private static NativeAbiType ReadAbiType(BinaryReader reader)
    {
        byte value = reader.ReadByte();
        return Enum.IsDefined(typeof(NativeAbiType), value)
            ? (NativeAbiType)value
            : throw new InvalidDataException(
                $"The ASN catalog contains unknown ABI type {value}.");
    }

    private static NativeExposure ReadExposure(BinaryReader reader)
    {
        byte value = reader.ReadByte();
        return value <= (byte)NativeExposure.CatalogOnly
            ? (NativeExposure)value
            : throw new InvalidDataException(
                $"The ASN catalog contains unknown exposure value {value}.");
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

internal readonly struct NativeArgument
{
    private NativeArgument(
        NativeAbiType type,
        ulong rawValue,
        string? textValue)
    {
        Type = type;
        RawValue = rawValue;
        TextValue = textValue;
    }

    internal NativeAbiType Type { get; }
    internal ulong RawValue { get; }
    internal string? TextValue { get; }

    internal static NativeArgument Boolean(bool value) =>
        new(NativeAbiType.Boolean32, value ? 1UL : 0UL, null);

    internal static NativeArgument Int32(int value) =>
        new(
            NativeAbiType.Int32,
            unchecked((ulong)(long)value),
            null);

    internal static NativeArgument Float32(float value) =>
        new(
            NativeAbiType.Float32,
            unchecked((uint)BitConverter.SingleToInt32Bits(value)),
            null);

    internal static NativeArgument Text(string? value) =>
        new(NativeAbiType.ConstCharPointer, 0, value);

    internal static NativeArgument Hash32(uint value) =>
        new(NativeAbiType.Hash32, value, null);

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

internal enum NativeExecutionStatus
{
    Success = 0,
    UnknownDescriptor = 1,
    CatalogOnly = 2,
    UnsupportedGame = 3,
    UnsupportedEdition = 4,
    UnsupportedBuild = 5,
    ArgumentCountMismatch = 6,
    ArgumentTypeMismatch = 7,
    TransportInvalidRequest = 8,
    TransportLimitExceeded = 9,
    NativeReturnedNull = 10,
    SessionStopping = 11,
    TransportFailure = 12
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
        ReadOnlySpan<NativeArgument> arguments) =>
        Invoke(
            descriptor,
            arguments,
            requireSafePublic: true);

    internal NativeExecutionResult InvokeKnownHash(
        NativeDescriptor descriptor,
        ReadOnlySpan<NativeArgument> arguments) =>
        Invoke(
            descriptor,
            arguments,
            requireSafePublic: false);

    private NativeExecutionResult Invoke(
        NativeDescriptor descriptor,
        ReadOnlySpan<NativeArgument> arguments,
        bool requireSafePublic)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!ReferenceEquals(catalog.GetByIndex(descriptor.Index), descriptor))
        {
            return Failure(NativeExecutionStatus.UnknownDescriptor);
        }

        GameBuildInfo game = gameBuild.Current;
        if (!game.Edition.IsSupported())
        {
            return Failure(NativeExecutionStatus.UnsupportedGame);
        }

        NativeSignatureVariant? variant =
            descriptor.GetVariant(game.Edition);
        if (variant is null)
        {
            return Failure(NativeExecutionStatus.UnsupportedEdition);
        }

        if (game.Build < variant.MinimumBuild)
        {
            return Failure(NativeExecutionStatus.UnsupportedBuild);
        }

        if (requireSafePublic &&
            variant.Exposure is not NativeExposure.SafePublic)
        {
            return Failure(NativeExecutionStatus.CatalogOnly);
        }

        if (arguments.Length != variant.Parameters.Count)
        {
            return Failure(NativeExecutionStatus.ArgumentCountMismatch);
        }

        Span<ulong> rawArguments = stackalloc ulong[arguments.Length];
        List<nint>? allocations = null;
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

                if (argument.Type is NativeAbiType.ConstCharPointer)
                {
                    if (argument.TextValue is null)
                    {
                        rawArguments[index] = 0;
                    }
                    else
                    {
                        nint pointer = Marshal.StringToCoTaskMemUTF8(
                            argument.TextValue);
                        allocations ??= [];
                        allocations.Add(pointer);
                        rawArguments[index] =
                            unchecked((ulong)(nuint)pointer);
                    }
                }
                else
                {
                    rawArguments[index] = argument.RawValue;
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

            return new NativeExecutionResult(
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
            if (allocations is not null)
            {
                foreach (nint allocation in allocations)
                {
                    Marshal.FreeCoTaskMem(allocation);
                }
            }
        }
    }

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

        _ => NativeExecutionStatus.TransportFailure
    };
}

internal sealed class KnownNativeInvoker(
    NativeCatalog catalog,
    NativeGateway gateway) : IKnownNativeInvoker
{
    public KnownNativeResult Invoke(
        ulong hash,
        ReadOnlySpan<KnownNativeArgument> arguments)
    {
        if (!catalog.TryGet(hash, out NativeDescriptor? descriptor))
        {
            throw new UnknownNativeHashException(hash);
        }

        NativeArgument[] values = new NativeArgument[arguments.Length];
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

        return new KnownNativeResult(
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
            gateway.InvokeGenerated(descriptor, arguments);
        if (!result.Succeeded || result.Variant is null)
        {
            throw new NativeExecutionException(
                descriptor,
                result.Status);
        }

        if (result.Variant.ReturnType != expectedReturnType)
        {
            throw new InvalidDataException(
                $"Native '{descriptor.Name}' has active catalog return type " +
                $"{result.Variant.ReturnType}, not {expectedReturnType}.");
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
        return new Vector3(
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