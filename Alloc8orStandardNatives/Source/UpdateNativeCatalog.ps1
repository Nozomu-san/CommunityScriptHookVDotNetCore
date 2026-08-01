#requires -Version 7.6

[CmdletBinding()]
param(
    [string] $InspectName,
    [string] $InspectHash,
    [switch] $VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [string]::IsNullOrWhiteSpace($InspectName) -and
    -not [string]::IsNullOrWhiteSpace($InspectHash)) {
    throw 'Specify either -InspectName or -InspectHash, not both.'
}

$compilerSource = @'
#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Alloc8orStandardNatives.CatalogTool;

public static class CatalogCompilerV5
{
    private const ushort FormatVersion = 2;
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex IdentifierRegex = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ByteRegex = new(
        "0x(?<byte>[0-9A-Fa-f]{2})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex IntegerConstantRegex = new(
        "internal\\s+const\\s+int\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?<value>[0-9]+)\\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StringConstantRegex = new(
        "internal\\s+const\\s+string\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*\"(?<value>[0-9A-Fa-f]+)\"\\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Dictionary<string, AbiType> NativeTypes =
        new(StringComparer.Ordinal)
        {
            ["void"] = AbiType.Void,
            ["BOOL"] = AbiType.Boolean32,
            ["int"] = AbiType.Int32,
            ["float"] = AbiType.Float32,
            ["const char*"] = AbiType.ConstCharPointer,
            ["Any"] = AbiType.Any,
            ["Hash"] = AbiType.Hash32,
            ["Blip"] = AbiType.Blip,
            ["Cam"] = AbiType.Cam,
            ["Entity"] = AbiType.Entity,
            ["FireId"] = AbiType.FireId,
            ["Interior"] = AbiType.Interior,
            ["ItemSet"] = AbiType.ItemSet,
            ["Object"] = AbiType.Object,
            ["Ped"] = AbiType.Ped,
            ["Pickup"] = AbiType.Pickup,
            ["Player"] = AbiType.Player,
            ["ScrHandle"] = AbiType.ScrHandle,
            ["Vehicle"] = AbiType.Vehicle,
            ["Vector3"] = AbiType.Vector3,
            ["Any*"] = AbiType.AnyPointer,
            ["int*"] = AbiType.Int32Pointer,
            ["float*"] = AbiType.Float32Pointer,
            ["Vector3*"] = AbiType.Vector3Pointer,
            ["BOOL*"] = AbiType.Boolean32Pointer,
            ["Hash*"] = AbiType.Hash32Pointer,
            ["char*"] = AbiType.CharPointer,
            ["Entity*"] = AbiType.EntityPointer,
            ["Vehicle*"] = AbiType.VehiclePointer,
            ["Ped*"] = AbiType.PedPointer,
            ["Object*"] = AbiType.ObjectPointer,
            ["ScrHandle*"] = AbiType.ScrHandlePointer,
            ["Blip*"] = AbiType.BlipPointer,
        };

    private static readonly string[] NativeTypeNames =
    [
        "void", "BOOL", "int", "float", "const char*", "Any", "Hash",
        "Blip", "Cam", "Entity", "FireId", "Interior", "ItemSet",
        "Object", "Ped", "Pickup", "Player", "ScrHandle", "Vehicle",
        "Vector3", "Any*", "int*", "float*", "Vector3*", "BOOL*",
        "Hash*", "char*", "Entity*", "Vehicle*", "Ped*", "Object*",
        "ScrHandle*", "Blip*"
    ];

    private static readonly Dictionary<string, ReturnProjection> Returns =
        new(StringComparer.Ordinal)
        {
            ["void"] = new("void", "InvokeVoid"),
            ["BOOL"] = new("bool", "InvokeBoolean"),
            ["int"] = new("int", "InvokeInt32"),
            ["float"] = new("float", "InvokeFloat32"),
            ["const char*"] = new("string?", "InvokeText"),
            ["Any"] = new("NativeAny", "InvokeAny"),
            ["Hash"] = new("uint", "InvokeHash32"),
            ["Blip"] = new("Blip", "InvokeBlip"),
            ["Cam"] = new("Cam", "InvokeCam"),
            ["Entity"] = new("Entity", "InvokeEntity"),
            ["FireId"] = new("FireId", "InvokeFireId"),
            ["Interior"] = new("Interior", "InvokeInterior"),
            ["ItemSet"] = new("ItemSet", "InvokeItemSet"),
            ["Object"] = new("GameObject", "InvokeGameObject"),
            ["Ped"] = new("Ped", "InvokePed"),
            ["Pickup"] = new("Pickup", "InvokePickup"),
            ["Player"] = new("Player", "InvokePlayer"),
            ["ScrHandle"] = new("ScrHandle", "InvokeScrHandle"),
            ["Vehicle"] = new("Vehicle", "InvokeVehicle"),
            ["Vector3"] = new("Vector3", "InvokeVector3"),
        };

    private static readonly Dictionary<string, ParameterProjection> Parameters =
        new(StringComparer.Ordinal)
        {
            ["BOOL"] = new("bool", "Boolean", false),
            ["int"] = new("int", "Int32", false),
            ["float"] = new("NativeFloat32", "Float32", true),
            ["const char*"] = new("string?", "Text", false),
            ["Hash"] = new("uint", "Hash32", false),
            ["Blip"] = new("Blip", "Blip", true),
            ["Cam"] = new("Cam", "Cam", true),
            ["Entity"] = new("Entity", "Entity", true),
            ["FireId"] = new("FireId", "FireId", true),
            ["Interior"] = new("Interior", "Interior", true),
            ["ItemSet"] = new("ItemSet", "ItemSet", true),
            ["Object"] = new("GameObject", "GameObject", true),
            ["Ped"] = new("Ped", "Ped", true),
            ["Pickup"] = new("Pickup", "Pickup", true),
            ["Player"] = new("Player", "Player", true),
            ["ScrHandle"] = new("ScrHandle", "ScrHandle", true),
            ["Vehicle"] = new("Vehicle", "Vehicle", true),
        };

    private static readonly HashSet<string> Keywords = new(
        new[]
        {
            "abstract", "as", "base", "bool", "break", "byte", "case",
            "catch", "char", "checked", "class", "const", "continue",
            "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally",
            "fixed", "float", "for", "foreach", "goto", "if", "implicit",
            "in", "int", "interface", "internal", "is", "lock", "long",
            "namespace", "new", "null", "object", "operator", "out",
            "override", "params", "private", "protected", "public",
            "readonly", "record", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct",
            "switch", "this", "throw", "true", "try", "typeof", "uint",
            "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
            "void", "volatile", "while", "add", "alias", "and",
            "ascending", "async", "await", "by", "descending", "dynamic",
            "equals", "extension", "field", "file", "from", "get", "global", "group", "init",
            "into", "join", "let", "managed", "nameof", "nint", "not",
            "notnull", "nuint", "on", "or", "orderby", "partial", "remove",
            "required", "scoped", "select", "set", "unmanaged", "value",
            "var", "when", "where", "with", "yield"
        },
        StringComparer.Ordinal);

    public static int Run(
        string sourceDirectory,
        string? inspectName,
        string? inspectHash,
        bool verifyOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        sourceDirectory = Path.GetFullPath(sourceDirectory);
        string dataPath = Path.Combine(sourceDirectory, "NativeCatalogData.cs");
        string standardPath = Path.Combine(sourceDirectory, "StandardNatives.cs");
        string jsonDirectory = Path.Combine(sourceDirectory, "Json");
        string legacyPath = Path.Combine(jsonDirectory, "natives.json");
        string enhancedPath = Path.Combine(jsonDirectory, "natives_gen9.json");

        if (!string.IsNullOrWhiteSpace(inspectName) ||
            !string.IsNullOrWhiteSpace(inspectHash))
        {
            CatalogImage current = ReadGeneratedCatalog(dataPath);
            NativeRecord selected = ResolveInspection(
                current.Records,
                inspectName,
                inspectHash);
            PrintRecord(selected);
            Console.WriteLine();
            Console.WriteLine(
                "The record above was decoded from NativeCatalogData.cs, " +
                "not read from JSON.");
            return 0;
        }

        if (verifyOnly)
        {
            CatalogImage generated = ReadGeneratedCatalog(dataPath);
            ValidateCatalog(generated.Records);
            Console.WriteLine(
                $"Current packed catalog format {generated.FormatVersion} " +
                $"verified: {generated.Records.Count} descriptors, " +
                $"{generated.PackedLength} packed bytes, " +
                $"{generated.DecodedLength} decoded bytes.");
            if (File.Exists(legacyPath) && File.Exists(enhancedPath))
            {
                List<NativeRecord> sourceRecords = ReadAndMerge(
                    legacyPath,
                    enhancedPath);
                AssertEquivalent(sourceRecords, generated.Records);
                Console.WriteLine(
                    "Current generated catalog exactly matches both JSON inputs.");
            }
            else
            {
                Console.WriteLine(
                    "JSON inputs are absent; only the generated catalog was verified.");
            }
            PrintProofSamples(generated.Records);
            return 0;
        }

        RequireFile(legacyPath);
        RequireFile(enhancedPath);
        List<NativeRecord> records = ReadAndMerge(legacyPath, enhancedPath);
        byte[] sourceFingerprint = ComputeSourceFingerprint(
            legacyPath,
            enhancedPath);
        CatalogBuild catalog = BuildCatalog(records, sourceFingerprint);

        CatalogImage roundTrip = DecodeCatalog(
            catalog.Packed,
            catalog.Decoded.Length,
            Convert.ToHexString(catalog.DecodedSha256));
        AssertEquivalent(records, roundTrip.Records);

        string dataSource = RenderCatalogData(catalog);
        CatalogImage renderedRoundTrip = ReadGeneratedCatalogText(dataSource);
        AssertEquivalent(records, renderedRoundTrip.Records);

        string standardSource = RenderStandardNatives(records);
        WriteAtomic(dataPath, dataSource);
        WriteAtomic(standardPath, standardSource);

        Console.WriteLine(
            $"Updated NativeCatalogData.cs and StandardNatives.cs: " +
            $"format {FormatVersion}, {records.Count} descriptors, " +
            $"{catalog.Packed.Length} packed bytes, " +
            $"{catalog.Decoded.Length} decoded bytes, " +
            $"{CountSafeMethods(records)} safe public methods.");
        PrintProofSamples(records);
        return 0;
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Required Alloc8or database input is missing.",
                path);
        }
    }

    private static List<NativeRecord> ReadAndMerge(
        string legacyPath,
        string enhancedPath)
    {
        Dictionary<ulong, SourceEntry> legacy = ReadDatabase(
            legacyPath,
            "Legacy");
        Dictionary<ulong, SourceEntry> enhanced = ReadDatabase(
            enhancedPath,
            "Enhanced");
        int sharedCount = legacy.Keys.Count(enhanced.ContainsKey);
        if (sharedCount < 1000)
        {
            throw new InvalidDataException(
                "natives_gen9.json is not a complete Enhanced catalog. " +
                "Refusing to regenerate because per-native Enhanced data " +
                "for shared hashes would be lost.");
        }

        SortedSet<ulong> hashes = new(legacy.Keys);
        hashes.UnionWith(enhanced.Keys);
        Console.WriteLine(
            $"JSON input: Legacy {legacy.Count}, Enhanced {enhanced.Count}, " +
            $"shared hashes {sharedCount}.");

        List<NativeRecord> records = new(hashes.Count);
        foreach (ulong hash in hashes)
        {
            legacy.TryGetValue(hash, out SourceEntry? legacyEntry);
            enhanced.TryGetValue(hash, out SourceEntry? enhancedEntry);
            SourceEntry source = legacyEntry ?? enhancedEntry ??
                throw new InvalidOperationException("Merged native is missing.");

            if (legacyEntry is not null &&
                enhancedEntry is not null &&
                !legacyEntry.Name.Equals(
                    enhancedEntry.Name,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Native 0x{hash:X16} has different canonical names: " +
                    $"Legacy '{legacyEntry.Name}', Enhanced " +
                    $"'{enhancedEntry.Name}'.");
            }

            records.Add(new NativeRecord(
                hash,
                source.Name,
                legacyEntry is null ? null : NativeVariant.From(legacyEntry),
                enhancedEntry is null ? null : NativeVariant.From(enhancedEntry)));
        }

        ValidateCatalog(records);
        for (int index = 0; index < records.Count; ++index)
        {
            records[index].Index = index;
        }
        return records;
    }

    private static Dictionary<ulong, SourceEntry> ReadDatabase(
        string path,
        string edition)
    {
        byte[] content = File.ReadAllBytes(path);
        using JsonDocument document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} root must be a JSON object.");
        }

        Dictionary<ulong, SourceEntry> entries = new();
        foreach (JsonProperty namespaceProperty in
                 document.RootElement.EnumerateObject())
        {
            if (namespaceProperty.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Namespace '{namespaceProperty.Name}' must be an object.");
            }

            foreach (JsonProperty nativeProperty in
                     namespaceProperty.Value.EnumerateObject())
            {
                ulong hash = ParseHash(nativeProperty.Name);
                if (nativeProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"Native 0x{hash:X16} must be an object.");
                }

                string context = edition + " " + namespaceProperty.Name +
                    ".0x" + hash.ToString("X16", CultureInfo.InvariantCulture);
                string name = RequiredString(
                    nativeProperty.Value,
                    "name",
                    context);
                string returnType = RequiredString(
                    nativeProperty.Value,
                    "return_type",
                    context);
                RequireKnownType(returnType, context);
                int build = RequiredBuild(
                    nativeProperty.Value,
                    "build",
                    context);
                List<NativeParameter> parameters = ReadParameters(
                    nativeProperty.Value,
                    context);

                if (!IdentifierRegex.IsMatch(name))
                {
                    throw new InvalidDataException(
                        $"{context}: invalid canonical name '{name}'.");
                }

                SourceEntry entry = new(
                    namespaceProperty.Name,
                    name,
                    build,
                    returnType,
                    parameters);
                if (!entries.TryAdd(hash, entry))
                {
                    throw new InvalidDataException(
                        $"{edition}: duplicate hash 0x{hash:X16}.");
                }
            }
        }
        return entries;
    }

    private static List<NativeParameter> ReadParameters(
        JsonElement native,
        string context)
    {
        if (!native.TryGetProperty("params", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"{context}: params must be an array.");
        }

        List<NativeParameter> parameters = new();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"{context}: parameter must be an object.");
            }
            string type = RequiredString(item, "type", context + " parameter");
            string name = RequiredString(item, "name", context + " parameter");
            RequireKnownType(type, context);
            if (!IdentifierRegex.IsMatch(name))
            {
                throw new InvalidDataException(
                    $"{context}: invalid parameter name '{name}'.");
            }
            parameters.Add(new NativeParameter(type, name));
        }
        return parameters;
    }

    private static string RequiredString(
        JsonElement owner,
        string propertyName,
        string context)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"{context}: '{propertyName}' must be a string.");
        }
        string? result = value.GetString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException(
                $"{context}: '{propertyName}' cannot be empty.");
        }
        return result;
    }

    private static int RequiredBuild(
        JsonElement owner,
        string propertyName,
        string context)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new InvalidDataException(
                $"{context}: '{propertyName}' is missing.");
        }

        int build;
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString() ?? string.Empty;
            if (!int.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out build))
            {
                throw new InvalidDataException(
                    $"{context}: invalid build '{text}'.");
            }
        }
        else if (value.ValueKind == JsonValueKind.Number &&
                 value.TryGetInt32(out build))
        {
        }
        else
        {
            throw new InvalidDataException(
                $"{context}: '{propertyName}' must be an integer or integer string.");
        }

        if (build < 0)
        {
            throw new InvalidDataException(
                $"{context}: a source edition entry cannot have a negative build.");
        }
        return build;
    }

    private static void ValidateCatalog(IReadOnlyList<NativeRecord> records)
    {
        if (records.Count == 0)
        {
            throw new InvalidDataException("The native catalog is empty.");
        }

        HashSet<ulong> hashes = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        ulong previous = 0;
        for (int index = 0; index < records.Count; ++index)
        {
            NativeRecord record = records[index];
            if (record.Hash == 0 || !hashes.Add(record.Hash))
            {
                throw new InvalidDataException(
                    $"Duplicate/zero native hash 0x{record.Hash:X16}.");
            }
            if (!names.Add(record.Name))
            {
                throw new InvalidDataException(
                    $"Canonical name '{record.Name}' maps to more than one hash.");
            }
            if (index != 0 && record.Hash <= previous)
            {
                throw new InvalidDataException(
                    "Catalog records must be strictly sorted by numeric hash.");
            }
            previous = record.Hash;
            if (record.Legacy is null && record.Enhanced is null)
            {
                throw new InvalidDataException(
                    $"Native '{record.Name}' supports no edition.");
            }
            if (record.Legacy is not null)
            {
                ValidateVariant(record.Name + " Legacy", record.Legacy);
            }
            if (record.Enhanced is not null)
            {
                ValidateVariant(record.Name + " Enhanced", record.Enhanced);
            }
        }
    }

    private static void ValidateVariant(string context, NativeVariant variant)
    {
        if (variant.MinimumBuild < 0)
        {
            throw new InvalidDataException(
                $"{context}: a present edition variant cannot be unsupported.");
        }
        RequireKnownType(variant.ReturnType, context);
        foreach (NativeParameter parameter in variant.Parameters)
        {
            RequireKnownType(parameter.Type, context);
        }
    }

    private static void RequireKnownType(string type, string context)
    {
        if (!NativeTypes.ContainsKey(type))
        {
            throw new InvalidDataException(
                $"{context}: unsupported native ABI type '{type}'.");
        }
    }

    private static ulong ParseHash(string value)
    {
        string normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }
        if (normalized.Length != 16 ||
            !ulong.TryParse(
                normalized,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ulong result) ||
            result == 0)
        {
            throw new InvalidDataException(
                $"Invalid 64-bit native hash '{value}'.");
        }
        return result;
    }

    private static byte[] ComputeSourceFingerprint(
        string legacyPath,
        string enhancedPath)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendFingerprintInput(hash, "natives.json", File.ReadAllBytes(legacyPath));
        AppendFingerprintInput(
            hash,
            "natives_gen9.json",
            File.ReadAllBytes(enhancedPath));
        return hash.GetHashAndReset();
    }

    private static void AppendFingerprintInput(
        IncrementalHash hash,
        string name,
        byte[] content)
    {
        hash.AppendData(Utf8.GetBytes(name));
        hash.AppendData(new byte[] { 0 });
        hash.AppendData(content);
        hash.AppendData(new byte[] { 0 });
    }

    private static CatalogBuild BuildCatalog(
        List<NativeRecord> records,
        byte[] sourceFingerprint)
    {
        StringPool pool = new();
        foreach (NativeRecord record in records)
        {
            pool.GetIndex(record.Name);
            AddVariantStrings(pool, record.Legacy);
            AddVariantStrings(pool, record.Enhanced);
        }

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Utf8, leaveOpen: true))
        {
            writer.Write("ASNC"u8);
            writer.Write(FormatVersion);
            writer.Write(sourceFingerprint);
            WriteVarUInt32(writer, checked((uint)records.Count));
            WriteVarUInt32(writer, checked((uint)pool.Values.Count));
            foreach (string value in pool.Values)
            {
                byte[] bytes = Utf8.GetBytes(value);
                WriteVarUInt32(writer, checked((uint)bytes.Length));
                writer.Write(bytes);
            }

            foreach (NativeRecord record in records)
            {
                writer.Write(record.Hash);
                WriteVarUInt32(writer, checked((uint)pool.GetIndex(record.Name)));
                byte editions = 0;
                if (record.Legacy is not null)
                {
                    editions |= 0x01;
                }
                if (record.Enhanced is not null)
                {
                    editions |= 0x02;
                }
                writer.Write(editions);
                if (record.Legacy is not null)
                {
                    WriteVariant(writer, pool, record.Legacy);
                }
                if (record.Enhanced is not null)
                {
                    WriteVariant(writer, pool, record.Enhanced);
                }
            }
        }

        byte[] decoded = stream.ToArray();
        byte[] packed = new byte[BrotliEncoder.GetMaxCompressedLength(decoded.Length)];
        if (!BrotliEncoder.TryCompress(
                decoded,
                packed,
                out int bytesWritten,
                quality: 11,
                window: 22))
        {
            throw new InvalidOperationException(
                "Brotli failed to compress the native catalog.");
        }
        Array.Resize(ref packed, bytesWritten);
        return new CatalogBuild(
            sourceFingerprint,
            SHA256.HashData(decoded),
            decoded,
            packed,
            records.Count);
    }

    private static void AddVariantStrings(
        StringPool pool,
        NativeVariant? variant)
    {
        if (variant is null)
        {
            return;
        }
        foreach (NativeParameter parameter in variant.Parameters)
        {
            pool.GetIndex(parameter.Name);
        }
    }

    private static void WriteVariant(
        BinaryWriter writer,
        StringPool pool,
        NativeVariant variant)
    {
        WriteVarUInt32(writer, EncodeBuild(variant.MinimumBuild));
        writer.Write((byte)NativeTypes[variant.ReturnType]);
        writer.Write((byte)GetExposure(variant));
        WriteVarUInt32(writer, checked((uint)variant.Parameters.Count));
        foreach (NativeParameter parameter in variant.Parameters)
        {
            writer.Write((byte)NativeTypes[parameter.Type]);
            WriteVarUInt32(
                writer,
                checked((uint)pool.GetIndex(parameter.Name)));
        }
    }

    private static Exposure GetExposure(NativeVariant variant)
    {
        if (variant.Parameters.Any(
                static parameter => parameter.Type == "Any"))
        {
            return Exposure.CatalogOnly;
        }
        if ((variant.ReturnType.Contains('*') &&
             variant.ReturnType != "const char*") ||
            variant.Parameters.Any(static parameter =>
                parameter.Type.Contains('*') &&
                parameter.Type != "const char*"))
        {
            return Exposure.ManualContractRequired;
        }
        return Exposure.SafePublic;
    }

    private static uint EncodeBuild(int build) =>
        build < 0 ? 0u : checked((uint)build + 1u);

    private static int DecodeBuild(uint encoded) =>
        encoded == 0 ? -1 : checked((int)encoded - 1);

    private static void WriteVarUInt32(BinaryWriter writer, uint value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        writer.Write((byte)value);
    }

    private static uint ReadVarUInt32(BinaryReader reader)
    {
        uint result = 0;
        int shift = 0;
        while (shift < 35)
        {
            byte value = reader.ReadByte();
            result |= (uint)(value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                return result;
            }
            shift += 7;
        }
        throw new InvalidDataException("Invalid unsigned varint.");
    }

    private static CatalogImage ReadGeneratedCatalog(string path)
    {
        RequireFile(path);
        return ReadGeneratedCatalogText(File.ReadAllText(path, Utf8));
    }

    private static CatalogImage ReadGeneratedCatalogText(string content)
    {
        int declaredFormat = ReadIntegerConstant(content, "FormatVersion");
        int declaredCount = ReadIntegerConstant(content, "DescriptorCount");
        int decodedLength = ReadIntegerConstant(content, "DecodedLength");
        string declaredFingerprint = ReadStringConstant(
            content,
            "SourceFingerprint");
        string decodedSha256 = ReadStringConstant(content, "DecodedSha256");
        MatchCollection matches = ByteRegex.Matches(content);
        byte[] packed = new byte[matches.Count];
        for (int index = 0; index < matches.Count; ++index)
        {
            packed[index] = byte.Parse(
                matches[index].Groups["byte"].Value,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture);
        }
        if (packed.Length == 0)
        {
            throw new InvalidDataException(
                "NativeCatalogData.cs contains no compressed catalog bytes.");
        }
        CatalogImage image = DecodeCatalog(
            packed,
            decodedLength,
            decodedSha256);
        if (declaredFormat != image.FormatVersion ||
            declaredCount != image.Records.Count ||
            !declaredFingerprint.Equals(
                image.SourceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "NativeCatalogData.cs header does not match its compressed payload.");
        }
        return image;
    }

    private static int ReadIntegerConstant(string content, string name)
    {
        foreach (Match match in IntegerConstantRegex.Matches(content))
        {
            if (match.Groups["name"].Value.Equals(name, StringComparison.Ordinal))
            {
                return int.Parse(
                    match.Groups["value"].Value,
                    CultureInfo.InvariantCulture);
            }
        }
        throw new InvalidDataException($"Missing integer constant '{name}'.");
    }

    private static string ReadStringConstant(string content, string name)
    {
        foreach (Match match in StringConstantRegex.Matches(content))
        {
            if (match.Groups["name"].Value.Equals(name, StringComparison.Ordinal))
            {
                return match.Groups["value"].Value.ToUpperInvariant();
            }
        }
        throw new InvalidDataException($"Missing string constant '{name}'.");
    }

    private static CatalogImage DecodeCatalog(
        ReadOnlySpan<byte> packed,
        int decodedLength,
        string decodedSha256)
    {
        byte[] decoded = GC.AllocateUninitializedArray<byte>(decodedLength);
        if (!BrotliDecoder.TryDecompress(
                packed,
                decoded,
                out int bytesWritten) ||
            bytesWritten != decoded.Length)
        {
            throw new InvalidDataException(
                "Decoded catalog length does not match NativeCatalogData.cs.");
        }
        if (!Convert.ToHexString(SHA256.HashData(decoded)).Equals(
                decodedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Decoded catalog SHA-256 does not match NativeCatalogData.cs.");
        }

        using MemoryStream stream = new(decoded, writable: false);
        using BinaryReader reader = new(stream, Utf8, leaveOpen: false);
        byte[] magic = reader.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual("ASNC"u8))
        {
            throw new InvalidDataException("Catalog magic is invalid.");
        }
        ushort format = reader.ReadUInt16();
        if (format is not 1 and not 2)
        {
            throw new InvalidDataException($"Unsupported catalog format {format}.");
        }
        byte[] fingerprint = reader.ReadBytes(32);
        if (fingerprint.Length != 32)
        {
            throw new EndOfStreamException("Source fingerprint is truncated.");
        }
        int descriptorCount = checked((int)ReadVarUInt32(reader));
        int stringCount = checked((int)ReadVarUInt32(reader));
        string[] strings = new string[stringCount];
        for (int index = 0; index < strings.Length; ++index)
        {
            int length = checked((int)ReadVarUInt32(reader));
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException("String pool is truncated.");
            }
            strings[index] = Utf8.GetString(bytes);
        }

        List<NativeRecord> records = new(descriptorCount);
        for (int index = 0; index < descriptorCount; ++index)
        {
            NativeRecord record = format == 1
                ? ReadVersion1Record(reader, strings)
                : ReadVersion2Record(reader, strings);
            record.Index = index;
            records.Add(record);
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Catalog contains trailing data.");
        }
        ValidateCatalog(records);
        return new CatalogImage(
            records,
            Convert.ToHexString(fingerprint),
            packed.Length,
            decoded.Length,
            format);
    }

    private static NativeRecord ReadVersion1Record(
        BinaryReader reader,
        string[] strings)
    {
        ulong hash = reader.ReadUInt64();
        string name = GetString(strings, ReadVarUInt32(reader));
        int legacyBuild = DecodeBuild(ReadVarUInt32(reader));
        int enhancedBuild = DecodeBuild(ReadVarUInt32(reader));
        string returnType = NativeTypeNames[(byte)ReadAbiType(reader)];
        _ = ReadExposure(reader);
        List<NativeParameter> parameters = ReadParameters(reader, strings);
        NativeVariant? legacy = legacyBuild < 0
            ? null
            : new NativeVariant(
                legacyBuild,
                returnType,
                CloneParameters(parameters));
        NativeVariant? enhanced = enhancedBuild < 0
            ? null
            : new NativeVariant(
                enhancedBuild,
                returnType,
                CloneParameters(parameters));
        return new NativeRecord(hash, name, legacy, enhanced);
    }

    private static NativeRecord ReadVersion2Record(
        BinaryReader reader,
        string[] strings)
    {
        ulong hash = reader.ReadUInt64();
        string name = GetString(strings, ReadVarUInt32(reader));
        byte editions = reader.ReadByte();
        if (editions == 0 || (editions & ~0x03) != 0)
        {
            throw new InvalidDataException(
                $"Native 0x{hash:X16} has invalid edition flags 0x{editions:X2}.");
        }
        NativeVariant? legacy = (editions & 0x01) != 0
            ? ReadVariant(reader, strings)
            : null;
        NativeVariant? enhanced = (editions & 0x02) != 0
            ? ReadVariant(reader, strings)
            : null;
        return new NativeRecord(hash, name, legacy, enhanced);
    }

    private static NativeVariant ReadVariant(
        BinaryReader reader,
        string[] strings)
    {
        int minimumBuild = DecodeBuild(ReadVarUInt32(reader));
        if (minimumBuild < 0)
        {
            throw new InvalidDataException(
                "A present native edition variant cannot be unsupported.");
        }
        string returnType = NativeTypeNames[(byte)ReadAbiType(reader)];
        _ = ReadExposure(reader);
        return new NativeVariant(
            minimumBuild,
            returnType,
            ReadParameters(reader, strings));
    }

    private static List<NativeParameter> ReadParameters(
        BinaryReader reader,
        string[] strings)
    {
        int parameterCount = checked((int)ReadVarUInt32(reader));
        List<NativeParameter> parameters = new(parameterCount);
        for (int index = 0; index < parameterCount; ++index)
        {
            AbiType type = ReadAbiType(reader);
            string name = GetString(strings, ReadVarUInt32(reader));
            parameters.Add(new NativeParameter(
                NativeTypeNames[(byte)type],
                name));
        }
        return parameters;
    }

    private static List<NativeParameter> CloneParameters(
        IReadOnlyList<NativeParameter> parameters) =>
        [.. parameters.Select(static parameter =>
            new NativeParameter(parameter.Type, parameter.Name))];

    private static AbiType ReadAbiType(BinaryReader reader)
    {
        byte value = reader.ReadByte();
        if (value >= NativeTypeNames.Length)
        {
            throw new InvalidDataException($"Unknown ABI type {value}.");
        }
        return (AbiType)value;
    }

    private static Exposure ReadExposure(BinaryReader reader)
    {
        byte value = reader.ReadByte();
        if (value > (byte)Exposure.CatalogOnly)
        {
            throw new InvalidDataException($"Unknown exposure {value}.");
        }
        return (Exposure)value;
    }

    private static string GetString(string[] values, uint index) =>
        index < values.Length
            ? values[index]
            : throw new InvalidDataException("Invalid string-pool index.");

    private static void AssertEquivalent(
        IReadOnlyList<NativeRecord> expected,
        IReadOnlyList<NativeRecord> actual)
    {
        if (expected.Count != actual.Count)
        {
            throw new InvalidDataException(
                $"Catalog count mismatch: {expected.Count} != {actual.Count}.");
        }
        for (int index = 0; index < expected.Count; ++index)
        {
            if (!RecordEquals(expected[index], actual[index]))
            {
                throw new InvalidDataException(
                    "Catalog round-trip mismatch at descriptor " + index +
                    ": expected " + Describe(expected[index]) +
                    ", actual " + Describe(actual[index]) + ".");
            }
        }
    }

    private static bool RecordEquals(NativeRecord left, NativeRecord right) =>
        left.Hash == right.Hash &&
        left.Name.Equals(right.Name, StringComparison.Ordinal) &&
        VariantEquals(left.Legacy, right.Legacy) &&
        VariantEquals(left.Enhanced, right.Enhanced);

    private static bool VariantEquals(
        NativeVariant? left,
        NativeVariant? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null ||
            left.MinimumBuild != right.MinimumBuild ||
            !left.ReturnType.Equals(right.ReturnType, StringComparison.Ordinal) ||
            left.Parameters.Count != right.Parameters.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Parameters.Count; ++index)
        {
            if (!left.Parameters[index].Type.Equals(
                    right.Parameters[index].Type,
                    StringComparison.Ordinal) ||
                !left.Parameters[index].Name.Equals(
                    right.Parameters[index].Name,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static string RenderCatalogData(CatalogBuild catalog)
    {
        StringBuilder builder = new();
        builder.AppendLine("// <auto-generated />")
            .AppendLine("// Run UpdateNativeCatalog.ps1 -VerifyOnly to validate this payload.")
            .AppendLine("// Run UpdateNativeCatalog.ps1 -InspectName <NAME> for a readable record.")
            .AppendLine("using System;")
            .AppendLine()
            .AppendLine("namespace Alloc8orStandardNatives.Source;")
            .AppendLine()
            .AppendLine("internal static class NativeCatalogData")
            .AppendLine("{")
            .Append("    internal const int FormatVersion = ")
            .Append(FormatVersion.ToString(CultureInfo.InvariantCulture))
            .AppendLine(";")
            .Append("    internal const int DescriptorCount = ")
            .Append(catalog.DescriptorCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine(";")
            .Append("    internal const int DecodedLength = ")
            .Append(catalog.Decoded.Length.ToString(CultureInfo.InvariantCulture))
            .AppendLine(";")
            .Append("    internal const string SourceFingerprint = \"")
            .Append(Convert.ToHexString(catalog.SourceFingerprint))
            .AppendLine("\";")
            .Append("    internal const string DecodedSha256 = \"")
            .Append(Convert.ToHexString(catalog.DecodedSha256))
            .AppendLine("\";")
            .AppendLine()
            .AppendLine("    internal static ReadOnlySpan<byte> CompressedCatalog =>")
            .AppendLine("    [");

        for (int offset = 0; offset < catalog.Packed.Length; offset += 24)
        {
            builder.Append("        ");
            int length = Math.Min(24, catalog.Packed.Length - offset);
            for (int index = 0; index < length; ++index)
            {
                if (index != 0)
                {
                    builder.Append(", ");
                }
                builder.Append("0x")
                    .Append(catalog.Packed[offset + index].ToString("X2"));
            }
            builder.AppendLine(",");
        }
        builder.AppendLine("    ];")
            .AppendLine("}");
        return builder.ToString();
    }

    private static string RenderStandardNatives(List<NativeRecord> records)
    {
        int safeCount = CountSafeMethods(records);
        StringBuilder builder = new();
        builder.AppendLine("// <auto-generated />")
            .Append("// Safe generated methods: ")
            .Append(safeCount.ToString(CultureInfo.InvariantCulture))
            .Append("; catalog descriptors: ")
            .Append(records.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine(".")
            .AppendLine("using System.Numerics;")
            .AppendLine()
            .AppendLine("namespace Alloc8orStandardNatives.Source;")
            .AppendLine()
            .AppendLine("public static partial class StandardNatives")
            .AppendLine("{");

        foreach (NativeRecord record in records)
        {
            foreach (GeneratedMethod method in GetGeneratedMethods(record))
            {
                RenderGeneratedMethod(builder, record, method);
            }
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void RenderGeneratedMethod(
        StringBuilder builder,
        NativeRecord record,
        GeneratedMethod method)
    {
        ReturnProjection result = Returns[method.Variant.ReturnType];
        builder.Append("    public static ")
            .Append(result.ClrType).Append(' ')
            .Append(record.Name).AppendLine("(");
        for (int parameterIndex = 0;
             parameterIndex < method.Variant.Parameters.Count;
             ++parameterIndex)
        {
            NativeParameter parameter = method.Variant.Parameters[parameterIndex];
            ParameterProjection projection = Parameters[parameter.Type];
            builder.Append("        ")
                .Append(projection.ClrType).Append(' ')
                .Append(EscapeIdentifier(parameter.Name))
                .AppendLine(
                    parameterIndex + 1 < method.Variant.Parameters.Count
                        ? ","
                        : string.Empty);
        }
        builder.AppendLine("    )")
            .Append("        => ").Append(result.Invoker).Append('(')
            .Append(record.Index.ToString(CultureInfo.InvariantCulture));
        if (method.Variant.Parameters.Count == 0)
        {
            builder.AppendLine(");")
                .AppendLine();
            return;
        }

        builder.AppendLine(",");
        for (int parameterIndex = 0;
             parameterIndex < method.Variant.Parameters.Count;
             ++parameterIndex)
        {
            NativeParameter parameter = method.Variant.Parameters[parameterIndex];
            ParameterProjection projection = Parameters[parameter.Type];
            string name = EscapeIdentifier(parameter.Name);
            builder.Append("            NativeArgument.")
                .Append(projection.Factory).Append('(')
                .Append(name);
            if (projection.UseValueProperty)
            {
                builder.Append(".Value");
            }
            builder.AppendLine(
                parameterIndex + 1 < method.Variant.Parameters.Count
                    ? "),"
                    : "));");
        }
        builder.AppendLine();
    }

    private static IReadOnlyList<GeneratedMethod> GetGeneratedMethods(
        NativeRecord record)
    {
        List<NativeVariant> candidates = [];
        AddSafeVariant(candidates, record.Legacy);
        AddSafeVariant(candidates, record.Enhanced);
        if (candidates.Count == 0)
        {
            return [];
        }

        Dictionary<string, GeneratedMethod> methods =
            new(StringComparer.Ordinal);
        HashSet<string> collisions = new(StringComparer.Ordinal);
        foreach (NativeVariant variant in candidates)
        {
            string signature = string.Join(
                "|",
                variant.Parameters.Select(parameter =>
                    Parameters[parameter.Type].ClrType));
            if (collisions.Contains(signature))
            {
                continue;
            }
            if (!methods.TryGetValue(signature, out GeneratedMethod? existing))
            {
                methods.Add(signature, new GeneratedMethod(variant));
                continue;
            }
            if (existing.Variant.HasSameWireContract(variant))
            {
                continue;
            }

            methods.Remove(signature);
            collisions.Add(signature);
        }
        return [.. methods.Values];
    }

    private static void AddSafeVariant(
        List<NativeVariant> values,
        NativeVariant? variant)
    {
        if (variant is null ||
            GetExposure(variant) != Exposure.SafePublic ||
            !Returns.ContainsKey(variant.ReturnType) ||
            variant.Parameters.Any(parameter =>
                !Parameters.ContainsKey(parameter.Type)))
        {
            return;
        }
        values.Add(variant);
    }

    private static int CountSafeMethods(IReadOnlyList<NativeRecord> records) =>
        records.Sum(record => GetGeneratedMethods(record).Count);

    private static string EscapeIdentifier(string value) =>
        Keywords.Contains(value) ? "@" + value : value;

    private static void WriteAtomic(string path, string content)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content, Utf8);
        File.Move(temporary, path, overwrite: true);
    }

    private static NativeRecord ResolveInspection(
        IReadOnlyList<NativeRecord> records,
        string? name,
        string? hash)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return records.SingleOrDefault(record =>
                    record.Name.Equals(name.Trim(), StringComparison.Ordinal))
                ?? throw new KeyNotFoundException(
                    $"Catalog has no canonical name '{name}'.");
        }
        if (!string.IsNullOrWhiteSpace(hash))
        {
            ulong value = ParseHash(hash);
            return records.SingleOrDefault(record => record.Hash == value)
                ?? throw new KeyNotFoundException(
                    $"Catalog has no hash 0x{value:X16}.");
        }
        throw new InvalidOperationException("Inspection target is missing.");
    }

    private static void PrintProofSamples(IReadOnlyList<NativeRecord> records)
    {
        Console.WriteLine();
        Console.WriteLine("Readable proof decoded from the packed catalog:");
        foreach (string name in new[]
        {
            "SWITCH_TO_MULTI_FIRSTPART",
            "SC_PAUSE_NEWS_INIT_STARTER_PACK",
            "NETWORK_POST_UDS_ACTIVITY_RESUME_WITH_TASKS",
            "OPEN_COMMERCE_STORE"
        })
        {
            NativeRecord? record = records.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.Ordinal));
            if (record is not null)
            {
                Console.WriteLine("  " + Describe(record));
            }
        }
    }

    private static void PrintRecord(NativeRecord record)
    {
        Console.WriteLine($"Descriptor index: {record.Index}");
        Console.WriteLine($"Name: {record.Name}");
        Console.WriteLine($"Hash: 0x{record.Hash:X16}");
        PrintVariant("Legacy", record.Legacy);
        PrintVariant("Enhanced", record.Enhanced);
    }

    private static void PrintVariant(string edition, NativeVariant? variant)
    {
        if (variant is null)
        {
            Console.WriteLine($"{edition}: unsupported (-1)");
            return;
        }

        Console.WriteLine(
            $"{edition}: minimum build {variant.MinimumBuild}; " +
            $"return {variant.ReturnType}; {variant.Parameters.Count} argument(s)");
        for (int index = 0; index < variant.Parameters.Count; ++index)
        {
            NativeParameter parameter = variant.Parameters[index];
            Console.WriteLine(
                $"  {index + 1}. {parameter.Type} {parameter.Name}");
        }
    }

    private static string Describe(NativeRecord record) =>
        $"#{record.Index} {record.Hash:X16} -> {record.Name}; " +
        $"Legacy {DescribeVariant(record.Legacy)}; " +
        $"Enhanced {DescribeVariant(record.Enhanced)}";

    private static string DescribeVariant(NativeVariant? variant)
    {
        if (variant is null)
        {
            return "unsupported (-1)";
        }
        string parameters = string.Join(
            ", ",
            variant.Parameters.Select(parameter =>
                parameter.Type + " " + parameter.Name));
        return $"build {variant.MinimumBuild}; " +
            $"{variant.ReturnType} ({parameters})";
    }

    private enum AbiType : byte
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

    private enum Exposure : byte
    {
        SafePublic = 0,
        ManualContractRequired = 1,
        CatalogOnly = 2
    }

    private sealed class SourceEntry
    {
        internal SourceEntry(
            string nativeNamespace,
            string name,
            int build,
            string returnType,
            List<NativeParameter> parameters)
        {
            Namespace = nativeNamespace;
            Name = name;
            Build = build;
            ReturnType = returnType;
            Parameters = parameters;
        }

        internal string Namespace { get; }
        internal string Name { get; }
        internal int Build { get; }
        internal string ReturnType { get; }
        internal List<NativeParameter> Parameters { get; }
    }

    private sealed class NativeParameter
    {
        internal NativeParameter(string type, string name)
        {
            Type = type;
            Name = name;
        }

        internal string Type { get; }
        internal string Name { get; }
    }

    private sealed class NativeVariant
    {
        internal NativeVariant(
            int minimumBuild,
            string returnType,
            List<NativeParameter> parameters)
        {
            MinimumBuild = minimumBuild;
            ReturnType = returnType;
            Parameters = parameters;
        }

        internal int MinimumBuild { get; }
        internal string ReturnType { get; }
        internal List<NativeParameter> Parameters { get; }

        internal static NativeVariant From(SourceEntry source) =>
            new(
                source.Build,
                source.ReturnType,
                CloneParameters(source.Parameters));

        internal bool HasSameWireContract(NativeVariant other)
        {
            if (!ReturnType.Equals(other.ReturnType, StringComparison.Ordinal) ||
                Parameters.Count != other.Parameters.Count)
            {
                return false;
            }
            for (int index = 0; index < Parameters.Count; ++index)
            {
                if (!Parameters[index].Type.Equals(
                        other.Parameters[index].Type,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }
    }

    private sealed class NativeRecord
    {
        internal NativeRecord(
            ulong hash,
            string name,
            NativeVariant? legacy,
            NativeVariant? enhanced)
        {
            Hash = hash;
            Name = name;
            Legacy = legacy;
            Enhanced = enhanced;
        }

        internal int Index { get; set; }
        internal ulong Hash { get; }
        internal string Name { get; }
        internal NativeVariant? Legacy { get; }
        internal NativeVariant? Enhanced { get; }
    }

    private sealed record ReturnProjection(string ClrType, string Invoker);

    private sealed record ParameterProjection(
        string ClrType,
        string Factory,
        bool UseValueProperty);

    private sealed record CatalogBuild(
        byte[] SourceFingerprint,
        byte[] DecodedSha256,
        byte[] Decoded,
        byte[] Packed,
        int DescriptorCount);

    private sealed record CatalogImage(
        List<NativeRecord> Records,
        string SourceFingerprint,
        int PackedLength,
        int DecodedLength,
        ushort FormatVersion);

    private sealed record GeneratedMethod(NativeVariant Variant);

    private sealed class StringPool
    {
        private readonly Dictionary<string, int> _indexes =
            new(StringComparer.Ordinal);
        private readonly List<string> _values = new();

        internal IReadOnlyList<string> Values => _values;

        internal int GetIndex(string value)
        {
            if (_indexes.TryGetValue(value, out int index))
            {
                return index;
            }
            index = _values.Count;
            _values.Add(value);
            _indexes.Add(value, index);
            return index;
        }
    }
}

'@

$typeName = 'Alloc8orStandardNatives.CatalogTool.CatalogCompilerV5'
if ($null -eq ($typeName -as [type])) {
    $compilerOptions = @(
        '/langversion:14',
        '/nullable:enable',
        '/optimize+',
        '/checked+'
    )

    $arguments = @{
        TypeDefinition = $compilerSource
        Language = 'CSharp'
        CompilerOptions = $compilerOptions
        ErrorAction = 'Stop'
    }

    $referenceDirectory = Join-Path $PSHOME 'ref'
    if (Test-Path -LiteralPath $referenceDirectory -PathType Container) {
        $references = @(
            Get-ChildItem -LiteralPath $referenceDirectory -Filter '*.dll' -File |
                ForEach-Object { $_.FullName }
        )
        if ($references.Count -gt 0) {
            $arguments.ReferencedAssemblies = $references
        }
    }

    Add-Type @arguments
}

[Alloc8orStandardNatives.CatalogTool.CatalogCompilerV5]::Run(
    $PSScriptRoot,
    $InspectName,
    $InspectHash,
    $VerifyOnly.IsPresent
) | Out-Null



