using System.Numerics;

namespace LocalNativeMemories.Source;

internal enum PoolBackendKind
{
    PrimaryPattern,
    RecoveryPattern
}

internal readonly record struct PoolBackendLocation(
    LocalPoolKinds Kind,
    PoolBackendKind Backend,
    nint Address);

internal sealed class EntityAddressResolver(
    ILocalMemory memory,
    GameMemoryProfile profile)
{
    private readonly ILocalMemory _memory =
        memory ?? throw new ArgumentNullException(nameof(memory));
    private readonly GameMemoryProfile _profile =
        profile ?? throw new ArgumentNullException(nameof(profile));
    private readonly Lock _gate = new();
    private nint _resolver;

    internal LocalDataResult<nint> Resolve(int handle)
    {
        if (handle == 0)
        {
            return LocalDataResult<nint>.Failed(
                LocalDataStatus.InvalidRequest);
        }

        LocalDataResult<nint> resolver = GetResolver();
        if (!resolver.IsSuccess)
        {
            return resolver;
        }

        return _memory.InvokeInt32ToPointer(
            resolver.Value,
            handle);
    }

    private LocalDataResult<nint> GetResolver()
    {
        lock (_gate)
        {
            if (_resolver > 0 && _memory.IsExecutableRange(_resolver, 1))
            {
                return LocalDataResult<nint>.Succeeded(_resolver);
            }

            LocalDataResult<nint> resolved =
                ResolvePatternAddress(_profile.EntityAddressResolver);

            if (!resolved.IsSuccess ||
                !_memory.IsExecutableRange(resolved.Value, 1))
            {
                _resolver = 0;
                return LocalDataResult<nint>.Failed(
                    resolved.IsSuccess
                        ? LocalDataStatus.VerificationFailed
                        : resolved.Status);
            }

            _resolver = resolved.Value;
            return resolved;
        }
    }

    private LocalDataResult<nint> ResolvePatternAddress(
        PatternAddressProfile pattern)
    {
        LocalDataResult<nint> match =
            _memory.FindPattern(pattern.Expression);
        if (!match.IsSuccess)
        {
            return match;
        }

        return pattern.Mode switch
        {
            PatternAddressMode.RipRelative =>
                _memory.ResolveRipRelative(
                    match.Value,
                    pattern.Offset,
                    pattern.InstructionLength),
            PatternAddressMode.MatchOffset =>
                _memory.Add(match.Value, pattern.Offset),
            _ => LocalDataResult<nint>.Failed(
                LocalDataStatus.InvalidRequest)
        };
    }
}

internal sealed class PoolBackendResolver(
    ILocalMemory memory,
    GameMemoryProfile profile)
{
    private readonly ILocalMemory _memory =
        memory ?? throw new ArgumentNullException(nameof(memory));
    private readonly GameMemoryProfile _profile =
        profile ?? throw new ArgumentNullException(nameof(profile));
    private readonly Lock _gate = new();
    private readonly Dictionary<LocalPoolKinds, PoolBackendLocation[]> _poolCandidates = [];
    private nint _createGuid;
    private nint _scriptGuidPool;

    internal LocalDataResult<PoolBackendLocation[]> ResolvePoolCandidates(
        LocalPoolKinds kind)
    {
        if (kind is not (
            LocalPoolKinds.Peds or
            LocalPoolKinds.Vehicles or
            LocalPoolKinds.Objects))
        {
            return LocalDataResult<PoolBackendLocation[]>.Failed(
                LocalDataStatus.InvalidRequest);
        }

        lock (_gate)
        {
            if (_poolCandidates.TryGetValue(
                    kind,
                    out PoolBackendLocation[]? cached) &&
                cached.Length != 0 &&
                cached.All(value =>
                    value.Address > 0 &&
                    _memory.IsReadableRange(value.Address, 1)))
            {
                return LocalDataResult<PoolBackendLocation[]>.Succeeded(
                    cached);
            }

            List<PoolBackendLocation> candidates = [];
            HashSet<nint> seen = [];

            if (kind == LocalPoolKinds.Vehicles)
            {
                AddCandidate(
                    candidates,
                    seen,
                    kind,
                    PoolBackendKind.PrimaryPattern,
                    ResolveVehicleAllocator());
            }
            else if (_profile.IsEnhanced)
            {
                if (kind == LocalPoolKinds.Peds)
                {
                    AddCandidate(
                        candidates,
                        seen,
                        kind,
                        PoolBackendKind.PrimaryPattern,
                        ResolveEnhancedPool(
                            _profile.PedPoolPattern,
                            _profile.PedPoolDecode));

                    AddCandidate(
                        candidates,
                        seen,
                        kind,
                        PoolBackendKind.RecoveryPattern,
                        ResolveEnhancedPool(
                            _profile.PedPoolRecoveryPattern,
                            _profile.PedPoolDecode));
                }
                else
                {
                    AddCandidate(
                        candidates,
                        seen,
                        kind,
                        PoolBackendKind.PrimaryPattern,
                        ResolveEnhancedPool(
                            _profile.ObjectPoolPattern,
                            _profile.ObjectPoolDecode));
                }
            }
            else
            {
                AddCandidate(
                    candidates,
                    seen,
                    kind,
                    PoolBackendKind.PrimaryPattern,
                    ResolveLegacyFwPool(kind));
            }

            if (candidates.Count == 0)
            {
                _poolCandidates.Remove(kind);
                return LocalDataResult<PoolBackendLocation[]>.Failed(
                    LocalDataStatus.DataUnavailable);
            }

            PoolBackendLocation[] result = [.. candidates];
            _poolCandidates[kind] = result;
            return LocalDataResult<PoolBackendLocation[]>.Succeeded(result);
        }
    }

    internal LocalDataResult<nint> ResolveCreateGuid()
    {
        lock (_gate)
        {
            if (_createGuid > 0 &&
                _memory.IsExecutableRange(_createGuid, 1))
            {
                return LocalDataResult<nint>.Succeeded(_createGuid);
            }

            LocalDataResult<nint> result =
                ResolvePatternAddress(_profile.CreateGuid);

            if (result.IsSuccess &&
                _memory.IsExecutableRange(result.Value, 1))
            {
                _createGuid = result.Value;
                return result;
            }

            _createGuid = 0;
            return LocalDataResult<nint>.Failed(
                result.IsSuccess
                    ? LocalDataStatus.VerificationFailed
                    : result.Status);
        }
    }

    internal LocalDataResult<nint> ResolveScriptGuidPool()
    {
        lock (_gate)
        {
            if (_scriptGuidPool > 0 &&
                _memory.IsReadableRange(
                    _scriptGuidPool,
                    _profile.ScriptGuidMinimumReadableSize))
            {
                return LocalDataResult<nint>.Succeeded(_scriptGuidPool);
            }

            LocalDataResult<nint> result =
                _profile.IsEnhanced
                    ? ResolveEnhancedScriptGuidPool()
                    : ResolveLegacyScriptGuidPool();

            if (result.IsSuccess &&
                _memory.IsReadableRange(
                    result.Value,
                    _profile.ScriptGuidMinimumReadableSize))
            {
                _scriptGuidPool = result.Value;
                return result;
            }

            _scriptGuidPool = 0;
            return LocalDataResult<nint>.Failed(
                result.IsSuccess
                    ? LocalDataStatus.VerificationFailed
                    : result.Status);
        }
    }

    private static void AddCandidate(
        List<PoolBackendLocation> candidates,
        HashSet<nint> seen,
        LocalPoolKinds kind,
        PoolBackendKind backend,
        LocalDataResult<nint> result)
    {
        if (result.IsSuccess && result.Value > 0 && seen.Add(result.Value))
        {
            candidates.Add(new(kind, backend, result.Value));
        }
    }

    private LocalDataResult<nint> ResolvePatternAddress(
        PatternAddressProfile pattern)
    {
        LocalDataResult<nint> match =
            _memory.FindPattern(pattern.Expression);
        if (!match.IsSuccess)
        {
            return match;
        }

        return pattern.Mode switch
        {
            PatternAddressMode.RipRelative =>
                _memory.ResolveRipRelative(
                    match.Value,
                    pattern.Offset,
                    pattern.InstructionLength),
            PatternAddressMode.MatchOffset =>
                _memory.Add(match.Value, pattern.Offset),
            _ => LocalDataResult<nint>.Failed(
                LocalDataStatus.InvalidRequest)
        };
    }

    private LocalDataResult<nint> ResolveLegacyFwPool(
        LocalPoolKinds kind)
    {
        string pattern = kind switch
        {
            LocalPoolKinds.Peds => _profile.PedPoolPattern,
            LocalPoolKinds.Objects => _profile.ObjectPoolPattern,
            _ => string.Empty
        };

        if (pattern.Length == 0)
        {
            return LocalDataResult<nint>.Failed(
                LocalDataStatus.InvalidRequest);
        }

        LocalDataResult<nint> match = _memory.FindPattern(pattern);
        if (!match.IsSuccess)
        {
            return match;
        }

        LocalDataResult<nint> location =
            _memory.ResolveRipRelative(match.Value, 3, 7);

        return !location.IsSuccess
            ? location
            : _memory.ReadPointer(location.Value);
    }

    private LocalDataResult<nint> ResolveEnhancedPool(
        string pattern,
        EncryptedPoolDecodeProfile decode)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return LocalDataResult<nint>.Failed(
                LocalDataStatus.DataUnavailable);
        }

        LocalDataResult<nint> match = _memory.FindPattern(pattern);
        return !match.IsSuccess
            ? match
            : DecodeEnhancedPool(match.Value, decode);
    }

    private LocalDataResult<nint> ResolveEnhancedScriptGuidPool()
    {
        foreach (string pattern in _profile.ScriptGuidPoolPatterns)
        {
            LocalDataResult<nint> match = _memory.FindPattern(pattern);
            if (!match.IsSuccess)
            {
                continue;
            }

            LocalDataResult<nint> decoded =
                DecodeEnhancedPool(
                    match.Value,
                    _profile.ScriptGuidPoolDecode);
            if (decoded.IsSuccess)
            {
                return decoded;
            }
        }

        return LocalDataResult<nint>.Failed(
            LocalDataStatus.DataUnavailable);
    }

    private LocalDataResult<nint> ResolveLegacyScriptGuidPool()
    {
        foreach (string pattern in _profile.ScriptGuidPoolPatterns)
        {
            LocalDataResult<nint> match = _memory.FindPattern(pattern);
            if (!match.IsSuccess)
            {
                continue;
            }

            LocalDataResult<nint> location =
                _memory.ResolveRipRelative(match.Value, 3, 7);
            if (!location.IsSuccess)
            {
                continue;
            }

            LocalDataResult<nint> pool = _memory.ReadPointer(location.Value);
            if (pool.IsSuccess)
            {
                return pool;
            }
        }

        return LocalDataResult<nint>.Failed(
            LocalDataStatus.DataUnavailable);
    }

    private LocalDataResult<nint> DecodeEnhancedPool(
        nint match,
        EncryptedPoolDecodeProfile decode)
    {
        if (!TryReadRipByte(
                match,
                decode.InitializedDisplacementOffset,
                decode.InitializedInstructionLength,
                out byte initialized) ||
            (initialized & 1) == 0 ||
            !TryReadRipUInt64(
                match,
                decode.FirstDisplacementOffset,
                decode.FirstInstructionLength,
                out ulong first) ||
            !TryReadRipUInt64(
                match,
                decode.SecondDisplacementOffset,
                decode.SecondInstructionLength,
                out ulong second) ||
            !TryReadByteAt(match, decode.FirstRotateOffset, out byte firstRol) ||
            !TryReadByteAt(match, decode.SecondRotateOffset, out byte secondRol) ||
            !TryReadByteAt(match, decode.AndOffset, out byte andValue) ||
            !TryReadByteAt(match, decode.AddOffset, out byte addValue))
        {
            return LocalDataResult<nint>.Failed(
                LocalDataStatus.DataUnavailable);
        }

        ulong rotatedSecond = BitOperations.RotateLeft(second, firstRol & 63);
        ulong decoded = first ^ rotatedSecond;
        byte dynamicRotation = unchecked(
            (byte)(((byte)rotatedSecond & andValue) + addValue));

        if (decode.RotationOrder is
            PoolDecodeRotationOrder.SecondaryThenDynamic)
        {
            decoded = BitOperations.RotateLeft(decoded, secondRol & 63);
            decoded = BitOperations.RotateLeft(decoded, dynamicRotation & 63);
        }
        else
        {
            decoded = BitOperations.RotateLeft(decoded, dynamicRotation & 63);
            decoded = BitOperations.RotateLeft(decoded, secondRol & 63);
        }

        decoded = ~decoded;
        return VerifyDecodedPointer(decoded, decode.MinimumReadableSize);
    }

    private LocalDataResult<nint> ResolveVehicleAllocator()
    {
        LocalDataResult<nint> match =
            _memory.FindPattern(_profile.VehicleAllocatorPattern);

        if (!match.IsSuccess)
        {
            return match;
        }

        LocalDataResult<nint> root =
            _memory.ResolveRipRelative(match.Value, 3, 7);
        if (!root.IsSuccess)
        {
            return root;
        }

        LocalDataResult<nint> wrapper = _memory.ReadPointer(root.Value);
        if (!wrapper.IsSuccess || wrapper.Value <= 0)
        {
            return LocalDataResult<nint>.Failed(
                LocalDataStatus.DataUnavailable);
        }

        LocalDataResult<nint> allocator =
            _memory.ReadPointer(wrapper.Value);

        return allocator.IsSuccess &&
               allocator.Value > 0 &&
               _memory.IsReadableRange(
                   allocator.Value,
                   _profile.VehicleAllocatorMinimumReadableSize)
            ? allocator
            : LocalDataResult<nint>.Failed(
                LocalDataStatus.VerificationFailed);
    }

    private LocalDataResult<nint> VerifyDecodedPointer(
        ulong raw,
        nuint minimumReadableSize)
    {
        nint result = unchecked((nint)(nuint)raw);
        return result > 0 &&
               _memory.IsReadableRange(result, minimumReadableSize)
            ? LocalDataResult<nint>.Succeeded(result)
            : LocalDataResult<nint>.Failed(
                LocalDataStatus.VerificationFailed);
    }

    private bool TryReadByteAt(
        nint address,
        int offset,
        out byte value)
    {
        value = 0;
        LocalDataResult<nint> target = _memory.Add(address, offset);
        if (!target.IsSuccess)
        {
            return false;
        }

        LocalDataResult<byte> result = _memory.ReadByte(target.Value);
        if (!result.IsSuccess)
        {
            return false;
        }

        value = result.Value;
        return true;
    }

    private bool TryReadRipByte(
        nint instruction,
        int displacementOffset,
        int instructionLength,
        out byte value)
    {
        value = 0;
        LocalDataResult<nint> target =
            _memory.ResolveRipRelative(
                instruction,
                displacementOffset,
                instructionLength);
        if (!target.IsSuccess)
        {
            return false;
        }

        LocalDataResult<byte> result = _memory.ReadByte(target.Value);
        if (!result.IsSuccess)
        {
            return false;
        }

        value = result.Value;
        return true;
    }

    private bool TryReadRipUInt64(
        nint instruction,
        int displacementOffset,
        int instructionLength,
        out ulong value)
    {
        value = 0;
        LocalDataResult<nint> target =
            _memory.ResolveRipRelative(
                instruction,
                displacementOffset,
                instructionLength);
        if (!target.IsSuccess)
        {
            return false;
        }

        LocalDataResult<ulong> result = _memory.ReadUInt64(target.Value);
        if (!result.IsSuccess)
        {
            return false;
        }

        value = result.Value;
        return true;
    }
}