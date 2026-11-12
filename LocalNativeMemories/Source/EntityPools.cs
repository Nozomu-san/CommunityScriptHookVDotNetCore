using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using CommunityScriptHookVDotNetCore.Source;

namespace LocalNativeMemories.Source;

internal readonly record struct PoolAdmissionEvidence(
    LocalPoolKinds Kind,
    LocalPoolStatistics Direct,
    int EnumeratedCount,
    bool GuidVerified);

internal sealed class EntityPools : ILocalEntityPools, ILocalEntityIdentity
{
    private const int LocalSlotsPerHostFrame = 16;
    private const int MaximumHandleResolutionsPerHostFrame = 1;
    private static readonly long HostFrameBudgetTicks =
        Math.Max(1L, Stopwatch.Frequency / 1000L);
    private const int MaximumIdentityQueue = 256;
    private const int MaximumIdentityCache = 512;
    private static readonly LocalPoolKinds[] PoolOrder =
    [
        LocalPoolKinds.Peds,
        LocalPoolKinds.Vehicles,
        LocalPoolKinds.Objects
    ];
    private static readonly long RefreshIntervalTicks = Stopwatch.Frequency;
    private static readonly long IdentitySuccessLifetimeTicks =
        checked(Stopwatch.Frequency * 2L);
    private static readonly long IdentityFailureLifetimeTicks =
        Math.Max(1L, Stopwatch.Frequency / 4L);

    private readonly ILocalMemory _memory;
    private readonly PoolBackendResolver _backends;
    private readonly PoolVerification _verification;
    private readonly IGameThreadFunctionTransport _gameThread;
    private readonly Lock _gate = new();
    private readonly Dictionary<LocalPoolKinds, PublishedPool> _published = [];
    private LocalPoolCountsSnapshot? _latestCounts;
    private int _countRequested;
    private int _admissionGranted;
    private int _pendingWork;
    private readonly Dictionary<LocalPoolKinds, PoolRefreshState> _refreshes = [];
    private readonly Dictionary<LocalPoolKinds, long> _nextRefreshAt = [];
    private readonly Queue<nint> _identityRequests = [with(MaximumIdentityQueue)];
    private readonly HashSet<nint> _identityPending = [];
    private readonly Dictionary<nint, CachedIdentity> _identityCache = [];
    private LocalPoolKinds _demanded;
    private int _roundRobinIndex;
    private ulong _nextRevision;
    private ulong _identityRequestsAccepted;
    private ulong _identitySuccessCacheHits;
    private ulong _identityFailureCacheHits;
    private ulong _identityResolved;
    private ulong _identityFailed;
    private ulong _identityAccessRejected;
    private ulong _identityQueueRejected;
    private ulong _identityCreateGuidUnavailable;
    private ulong _identityScriptGuidPoolUnavailable;
    private ulong _identityScriptGuidPoolRejected;
    private ulong _identityAdmissionRejected;
    private ulong _identityGuardRejected;
    private ulong _identitySessionStopping;
    private ulong _identityCallFailed;
    private ulong _identityZeroHandle;

    internal EntityPools(
        ILocalMemory memory,
        PoolBackendResolver backends,
        PoolVerification verification,
        IGameThreadFunctionTransport gameThread)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _verification =
            verification ?? throw new ArgumentNullException(nameof(verification));
        _gameThread =
            gameThread ?? throw new ArgumentNullException(nameof(gameThread));
    }

    public LocalDataResult<LocalPoolCountsSnapshot> GetCountSnapshot()
    {
        Interlocked.Exchange(ref _countRequested, 1);
        LocalPoolCountsSnapshot? counts = Volatile.Read(ref _latestCounts);
        return counts is null
            ? LocalDataResult<LocalPoolCountsSnapshot>.Failed(
                LocalDataStatus.DataUnavailable)
            : LocalDataResult<LocalPoolCountsSnapshot>.Succeeded(counts);
    }

    public LocalDataResult<LocalPoolStatistics> GetStatistics(
        LocalPoolKinds kind)
    {
        if (!IsSingleKind(kind))
        {
            return LocalDataResult<LocalPoolStatistics>.Failed(
                LocalDataStatus.InvalidRequest);
        }

        LocalDataResult<LocalPoolCountsSnapshot> result = GetCountSnapshot();
        if (!result.IsSuccess)
        {
            return LocalDataResult<LocalPoolStatistics>.Failed(result.Status);
        }

        return LocalDataResult<LocalPoolStatistics>.Succeeded(kind switch
        {
            LocalPoolKinds.Peds => result.Value.Peds,
            LocalPoolKinds.Vehicles => result.Value.Vehicles,
            _ => result.Value.Objects
        });
    }

    internal void GrantAdmission()
    {
        Volatile.Write(ref _admissionGranted, 1);
        Interlocked.Exchange(ref _countRequested, 1);
    }

    internal LocalDataResult<LocalPoolCountsSnapshot> ReadDirectCounts(
        ulong hostFrameIndex)
    {
        lock (_gate)
        {
            LocalDataResult<LocalPoolStatistics> peds =
                ReadDirectStatistics(LocalPoolKinds.Peds);
            LocalDataResult<LocalPoolStatistics> vehicles =
                ReadDirectStatistics(LocalPoolKinds.Vehicles);
            LocalDataResult<LocalPoolStatistics> objects =
                ReadDirectStatistics(LocalPoolKinds.Objects);
            if (!peds.IsSuccess || !vehicles.IsSuccess || !objects.IsSuccess)
            {
                return LocalDataResult<LocalPoolCountsSnapshot>.Failed(
                    !peds.IsSuccess ? peds.Status :
                    !vehicles.IsSuccess ? vehicles.Status : objects.Status);
            }

            return LocalDataResult<LocalPoolCountsSnapshot>.Succeeded(
                new(hostFrameIndex, peds.Value, vehicles.Value, objects.Value));
        }
    }

    private LocalDataResult<LocalPoolStatistics> ReadDirectStatistics(
        LocalPoolKinds kind)
    {
        LocalDataResult<PoolBackendLocation[]> candidates =
            _backends.ResolvePoolCandidates(kind);
        if (!candidates.IsSuccess)
        {
            return LocalDataResult<LocalPoolStatistics>.Failed(
                candidates.Status);
        }

        LocalDataStatus lastFailure = LocalDataStatus.DataUnavailable;
        foreach (PoolBackendLocation candidate in candidates.Value)
        {
            LocalDataResult<LocalPoolStatistics> result =
                _verification.ReadStatistics(kind, candidate.Address);
            if (result.IsSuccess)
            {
                return result;
            }
            lastFailure = result.Status;
        }
        return LocalDataResult<LocalPoolStatistics>.Failed(lastFailure);
    }

    internal LocalDataResult<PoolAdmissionEvidence> VerifyAdmissionPool(
        LocalPoolKinds kind, out string details)
    {
        details = "stage=invalid-pool-kind";
        if (!IsSingleKind(kind))
        {
            return LocalDataResult<PoolAdmissionEvidence>.Failed(
                LocalDataStatus.InvalidRequest);
        }

        lock (_gate)
        {
            LocalDataResult<PoolBackendLocation[]> candidates =
                _backends.ResolvePoolCandidates(kind);
            if (!candidates.IsSuccess)
            {
                details = $"stage=resolve-pool-candidates status={candidates.Status}";
                return LocalDataResult<PoolAdmissionEvidence>.Failed(
                    candidates.Status);
            }

            List<string> attempts = [];
            for (int index = 0; index < candidates.Value.Length; ++index)
            {
                PoolBackendLocation candidate = candidates.Value[index];
                string label = $"candidate={index + 1}/{candidates.Value.Length}";
                LocalDataResult<PoolAdmissionProbe> probe =
                    _verification.ReadAdmissionProbe(kind, candidate.Address);
                if (!probe.IsSuccess)
                {
                    attempts.Add($"{label} stage=read-admission-probe status={probe.Status}");
                    continue;
                }

                LocalDataResult<LocalPoolStatistics> counts =
                    _verification.ReadStatistics(kind, candidate.Address);
                if (!counts.IsSuccess)
                {
                    attempts.Add($"{label} stage=read-direct-statistics status={counts.Status}");
                    details = string.Join("; ", attempts);
                    return LocalDataResult<PoolAdmissionEvidence>.Failed(
                        LocalDataStatus.DataUnavailable);
                }
                if (counts.Value.Capacity != probe.Value.Capacity)
                {
                    attempts.Add($"{label} stage=capacity-compare " +
                        $"directCapacity={counts.Value.Capacity} " +
                        $"probeCapacity={probe.Value.Capacity} result=FAIL");
                    details = string.Join("; ", attempts);
                    return LocalDataResult<PoolAdmissionEvidence>.Failed(
                        LocalDataStatus.DataUnavailable);
                }

                if (probe.Value.Sample.IsAllocated)
                {
                    LocalDataResult<nint> createGuid =
                        _backends.ResolveCreateGuid();
                    LocalDataResult<nint> guidPool =
                        _backends.ResolveScriptGuidPool();
                    if (!createGuid.IsSuccess || !guidPool.IsSuccess)
                    {
                        attempts.Add($"{label} stage=guid-resolver " +
                            $"createGuid={createGuid.Status} guidPool={guidPool.Status}");
                        details = string.Join("; ", attempts);
                        return LocalDataResult<PoolAdmissionEvidence>.Failed(
                            LocalDataStatus.DataUnavailable);
                    }
                    if (!_memory.IsExecutableRange(createGuid.Value, 1))
                    {
                        attempts.Add($"{label} stage=guid-executable-range result=FAIL");
                        details = string.Join("; ", attempts);
                        return LocalDataResult<PoolAdmissionEvidence>.Failed(
                            LocalDataStatus.DataUnavailable);
                    }
                    if (!_verification.CanCreateScriptGuid(guidPool.Value))
                    {
                        attempts.Add($"{label} stage=guid-pool-capacity result=FAIL");
                        details = string.Join("; ", attempts);
                        return LocalDataResult<PoolAdmissionEvidence>.Failed(
                            LocalDataStatus.DataUnavailable);
                    }

                    GameThreadInt32CallResult result =
                        _gameThread.InvokeInt32Guarded(
                            createGuid.Value,
                            probe.Value.Sample.Entity,
                            ToGameThreadGuard(
                                probe.Value.Sample.PrimaryGuard),
                            ToGameThreadGuard(
                                probe.Value.Sample.SecondaryGuard));
                    if (!result.IsSuccess || result.Value == 0)
                    {
                        attempts.Add($"{label} stage=guarded-guid-call " +
                            $"callSucceeded={result.IsSuccess} " +
                            $"nonzeroHandle={(result.IsSuccess && result.Value != 0)} " +
                            "result=FAIL");
                        details = string.Join("; ", attempts);
                        return LocalDataResult<PoolAdmissionEvidence>.Failed(
                            LocalDataStatus.DataUnavailable);
                    }
                }

                attempts.Add($"{label} stage=pool-probe-and-capacity-compare " +
                    $"result=PASS guidCallRequired={probe.Value.Sample.IsAllocated}");
                details = string.Join("; ", attempts);
                return LocalDataResult<PoolAdmissionEvidence>.Succeeded(
                    new(kind, counts.Value, probe.Value.OccupiedCount,
                        probe.Value.Sample.IsAllocated));
            }

            if (attempts.Count == 0)
            {
                details = "stage=resolve-pool-candidates result=FAIL candidates=0";
            }
            else
            {
                details = string.Join("; ", attempts);
            }
            return LocalDataResult<PoolAdmissionEvidence>.Failed(
                LocalDataStatus.DataUnavailable);
        }
    }

    public LocalDataResult<EntityPoolsSnapshot> Capture(LocalPoolKinds kinds)
    {
        if (Volatile.Read(ref _admissionGranted) == 0)
        {
            return LocalDataResult<EntityPoolsSnapshot>.Failed(
                LocalDataStatus.DataUnavailable);
        }
        return CaptureCore(kinds);
    }

    private LocalDataResult<EntityPoolsSnapshot> CaptureCore(
        LocalPoolKinds kinds)
    {
        if ((kinds & ~LocalPoolKinds.All) != 0 ||
            kinds is LocalPoolKinds.None)
        {
            return LocalDataResult<EntityPoolsSnapshot>.Failed(
                LocalDataStatus.InvalidRequest);
        }

        lock (_gate)
        {
            _demanded |= kinds;
            Volatile.Write(ref _pendingWork, 1);

            LocalPoolSnapshot peds = LocalPoolSnapshot.Empty;
            LocalPoolSnapshot vehicles = LocalPoolSnapshot.Empty;
            LocalPoolSnapshot objects = LocalPoolSnapshot.Empty;

            if (kinds.HasFlag(LocalPoolKinds.Peds))
            {
                LocalDataResult<LocalPoolSnapshot> result =
                    ReadPublishedPool(LocalPoolKinds.Peds);
                if (!result.IsSuccess)
                {
                    return LocalDataResult<EntityPoolsSnapshot>.Failed(
                        result.Status);
                }

                peds = result.Value;
            }

            if (kinds.HasFlag(LocalPoolKinds.Vehicles))
            {
                LocalDataResult<LocalPoolSnapshot> result =
                    ReadPublishedPool(LocalPoolKinds.Vehicles);
                if (!result.IsSuccess)
                {
                    return LocalDataResult<EntityPoolsSnapshot>.Failed(
                        result.Status);
                }

                vehicles = result.Value;
            }

            if (kinds.HasFlag(LocalPoolKinds.Objects))
            {
                LocalDataResult<LocalPoolSnapshot> result =
                    ReadPublishedPool(LocalPoolKinds.Objects);
                if (!result.IsSuccess)
                {
                    return LocalDataResult<EntityPoolsSnapshot>.Failed(
                        result.Status);
                }

                objects = result.Value;
            }

            return LocalDataResult<EntityPoolsSnapshot>.Succeeded(
                new(peds, vehicles, objects));
        }
    }


    public LocalDataResult<int> TryGetHandle(nint entityAddress)
    {
        if (entityAddress == 0)
        {
            return LocalDataResult<int>.Failed(LocalDataStatus.InvalidRequest);
        }

        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (!_identityCache.TryGetValue(entityAddress, out CachedIdentity cached))
            {
                return LocalDataResult<int>.Failed(LocalDataStatus.DataUnavailable);
            }

            if (now >= cached.ExpiresAt)
            {
                _identityCache.Remove(entityAddress);
                return LocalDataResult<int>.Failed(LocalDataStatus.DataUnavailable);
            }

            if (cached.Status is LocalDataStatus.Success)
            {
                ++_identitySuccessCacheHits;
                return LocalDataResult<int>.Succeeded(cached.Handle);
            }

            ++_identityFailureCacheHits;
            return LocalDataResult<int>.Failed(cached.Status);
        }
    }

    public LocalDataStatus RequestHandle(nint entityAddress)
    {
        if (entityAddress == 0)
        {
            return LocalDataStatus.InvalidRequest;
        }

        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_identityCache.TryGetValue(entityAddress, out CachedIdentity cached))
            {
                if (now < cached.ExpiresAt)
                {
                    return cached.Status;
                }

                _identityCache.Remove(entityAddress);
            }

            if (_identityPending.Contains(entityAddress))
            {
                return LocalDataStatus.DataUnavailable;
            }

            if (!_memory.IsReadableRange(entityAddress, 1))
            {
                CountIdentityFailure(IdentityFailureStage.AccessRejected);
                CacheIdentityFailure(
                    entityAddress,
                    LocalDataStatus.AccessRejected,
                    now);
                return LocalDataStatus.AccessRejected;
            }

            if (_identityRequests.Count >= MaximumIdentityQueue)
            {
                ++_identityQueueRejected;
                return LocalDataStatus.DataUnavailable;
            }

            _identityRequests.Enqueue(entityAddress);
            _identityPending.Add(entityAddress);
            Volatile.Write(ref _pendingWork, 1);
            ++_identityRequestsAccepted;
            return LocalDataStatus.DataUnavailable;
        }
    }

    public LocalEntityIdentityStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new(
                    _identityRequestsAccepted,
                    _identitySuccessCacheHits,
                    _identityFailureCacheHits,
                    _identityResolved,
                    _identityFailed,
                    _identityAccessRejected,
                    _identityQueueRejected,
                    _identityCreateGuidUnavailable,
                    _identityScriptGuidPoolUnavailable,
                    _identityScriptGuidPoolRejected,
                    _identityAdmissionRejected,
                    _identityGuardRejected,
                    _identitySessionStopping,
                    _identityCallFailed,
                    _identityZeroHandle,
                    _identityRequests.Count);
            }
        }
    }

    internal bool HasPendingWork =>
        Volatile.Read(ref _countRequested) != 0 ||
        Volatile.Read(ref _pendingWork) != 0;

    internal void AdvanceHostFrame(ulong hostFrameIndex)
    {
        if (!HasPendingWork)
        {
            return;
        }

        if (Volatile.Read(ref _admissionGranted) != 0 &&
            Interlocked.Exchange(ref _countRequested, 0) != 0)
        {
            LocalDataResult<LocalPoolCountsSnapshot> counts =
                ReadDirectCounts(hostFrameIndex);
            Interlocked.Exchange(ref _latestCounts,
                counts.IsSuccess ? counts.Value : null);
        }

        long deadline = Stopwatch.GetTimestamp() + HostFrameBudgetTicks;
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            int remainingHandleResolutions = MaximumHandleResolutionsPerHostFrame;
            int identityBudget = ResolveIdentityBudget();
            while (identityBudget > 0 &&
                   remainingHandleResolutions > 0 &&
                   Stopwatch.GetTimestamp() < deadline &&
                   AdvanceIdentity(now))
            {
                --identityBudget;
                --remainingHandleResolutions;
            }

            if (_demanded is LocalPoolKinds.None)
            {
                Volatile.Write(ref _pendingWork,
                    _identityRequests.Count != 0 ? 1 : 0);
                return;
            }

            int remainingSlots = LocalSlotsPerHostFrame;
            int withoutProgress = 0;

            while (remainingSlots > 0 &&
                   remainingHandleResolutions > 0 &&
                   Stopwatch.GetTimestamp() < deadline &&
                   _demanded is not LocalPoolKinds.None &&
                   withoutProgress < PoolOrder.Length)
            {
                LocalPoolKinds kind = PoolOrder[_roundRobinIndex];
                _roundRobinIndex = (_roundRobinIndex + 1) % PoolOrder.Length;

                if (!_demanded.HasFlag(kind))
                {
                    ++withoutProgress;
                    continue;
                }

                bool progressed = AdvanceKind(
                    kind,
                    now,
                    remainingSlots,
                    deadline,
                    out int processedSlots,
                    out bool resolvedHandle);

                remainingSlots -= processedSlots;
                if (resolvedHandle)
                {
                    --remainingHandleResolutions;
                }

                if (progressed)
                {
                    withoutProgress = 0;
                }
                else
                {
                    ++withoutProgress;
                }
            }
            Volatile.Write(ref _pendingWork,
                _demanded != LocalPoolKinds.None || _identityRequests.Count != 0
                    ? 1
                    : 0);
        }
    }


    private int ResolveIdentityBudget() =>
        _identityRequests.Count switch
        {
            >= 128 => 8,
            >= 64 => 6,
            >= 32 => 4,
            >= 8 => 2,
            > 0 => 1,
            _ => 0
        };

    private bool AdvanceIdentity(long now)
    {
        if (_identityRequests.Count == 0)
        {
            return false;
        }

        nint entityAddress = _identityRequests.Dequeue();
        _identityPending.Remove(entityAddress);

        if (_identityCache.TryGetValue(entityAddress, out CachedIdentity cached) &&
            cached.Status is LocalDataStatus.Success &&
            now < cached.ExpiresAt)
        {
            TrimIdentityCache(now);
            return true;
        }

        IdentityResolution result = ResolveHandleNow(entityAddress);
        if (result.IsSuccess)
        {
            CacheIdentitySuccess(entityAddress, result.Handle, now);
            ++_identityResolved;
        }
        else
        {
            CountIdentityFailure(result.Failure);
            CacheIdentityFailure(entityAddress, result.Status, now);
        }

        TrimIdentityCache(now);
        return true;
    }

    private IdentityResolution ResolveHandleNow(nint entityAddress)
    {
        if (!_memory.IsReadableRange(entityAddress, 1))
        {
            return IdentityResolution.Failed(
                LocalDataStatus.AccessRejected,
                IdentityFailureStage.AccessRejected);
        }

        LocalDataResult<nint> createGuid = _backends.ResolveCreateGuid();
        if (!createGuid.IsSuccess ||
            !_memory.IsExecutableRange(createGuid.Value, 1))
        {
            return IdentityResolution.Failed(
                createGuid.IsSuccess
                    ? LocalDataStatus.VerificationFailed
                    : createGuid.Status,
                IdentityFailureStage.CreateGuidUnavailable);
        }

        LocalDataResult<nint> guidPool = _backends.ResolveScriptGuidPool();
        if (!guidPool.IsSuccess)
        {
            return IdentityResolution.Failed(
                guidPool.Status,
                IdentityFailureStage.ScriptGuidPoolUnavailable);
        }

        if (!_verification.CanCreateScriptGuid(guidPool.Value))
        {
            return IdentityResolution.Failed(
                LocalDataStatus.VerificationFailed,
                IdentityFailureStage.ScriptGuidPoolRejected);
        }

        LocalDataResult<nint> virtualTable = _memory.ReadPointer(entityAddress);
        if (!virtualTable.IsSuccess)
        {
            return IdentityResolution.Failed(
                virtualTable.Status,
                IdentityFailureStage.AccessRejected);
        }
        if (!_memory.IsExecutableRange(virtualTable.Value, 1))
        {
            return IdentityResolution.Failed(
                LocalDataStatus.VerificationFailed,
                IdentityFailureStage.GuardRejected);
        }

        GameThreadMemoryGuard entityGuard = new(
            entityAddress,
            ulong.MaxValue,
            unchecked((ulong)(nuint)virtualTable.Value),
            IntPtr.Size);

        GameThreadInt32CallResult result =
            _gameThread.InvokeInt32Guarded(
                createGuid.Value,
                entityAddress,
                entityGuard,
                GameThreadMemoryGuard.None);

        if (!result.IsSuccess)
        {
            return result.Status switch
            {
                GameThreadFunctionCallStatus.AdmissionRejected =>
                    IdentityResolution.Failed(
                        LocalDataStatus.DataUnavailable,
                        IdentityFailureStage.AdmissionRejected),
                GameThreadFunctionCallStatus.GuardRejected =>
                    IdentityResolution.Failed(
                        LocalDataStatus.DataUnavailable,
                        IdentityFailureStage.GuardRejected),
                GameThreadFunctionCallStatus.SessionStopping =>
                    IdentityResolution.Failed(
                        LocalDataStatus.DataUnavailable,
                        IdentityFailureStage.SessionStopping),
                _ => IdentityResolution.Failed(
                    LocalDataStatus.VerificationFailed,
                    IdentityFailureStage.CallFailed)
            };
        }

        return result.Value != 0
            ? IdentityResolution.Succeeded(result.Value)
            : IdentityResolution.Failed(
                LocalDataStatus.DataUnavailable,
                IdentityFailureStage.ZeroHandle);
    }

    private void CacheIdentitySuccess(
        nint entityAddress,
        int handle,
        long now)
    {
        if (entityAddress == 0 || handle == 0)
        {
            return;
        }

        _identityCache[entityAddress] = new(
            LocalDataStatus.Success,
            handle,
            checked(now + IdentitySuccessLifetimeTicks));
        _identityPending.Remove(entityAddress);
    }

    private void CacheIdentityFailure(
        nint entityAddress,
        LocalDataStatus status,
        long now)
    {
        _identityCache[entityAddress] = new(
            status,
            0,
            checked(now + IdentityFailureLifetimeTicks));
    }

    private void CountIdentityFailure(IdentityFailureStage failure)
    {
        ++_identityFailed;
        switch (failure)
        {
        case IdentityFailureStage.AccessRejected:
            ++_identityAccessRejected;
            break;
        case IdentityFailureStage.CreateGuidUnavailable:
            ++_identityCreateGuidUnavailable;
            break;
        case IdentityFailureStage.ScriptGuidPoolUnavailable:
            ++_identityScriptGuidPoolUnavailable;
            break;
        case IdentityFailureStage.ScriptGuidPoolRejected:
            ++_identityScriptGuidPoolRejected;
            break;
        case IdentityFailureStage.AdmissionRejected:
            ++_identityAdmissionRejected;
            break;
        case IdentityFailureStage.GuardRejected:
            ++_identityGuardRejected;
            break;
        case IdentityFailureStage.SessionStopping:
            ++_identitySessionStopping;
            break;
        case IdentityFailureStage.ZeroHandle:
            ++_identityZeroHandle;
            break;
        default:
            ++_identityCallFailed;
            break;
        }
    }

    private void TrimIdentityCache(long now)
    {
        if (_identityCache.Count <= MaximumIdentityCache)
        {
            return;
        }

        nint[] expired =
            [.. _identityCache
                .Where(pair => now >= pair.Value.ExpiresAt)
                .Select(pair => pair.Key)];
        foreach (nint address in expired)
        {
            _identityCache.Remove(address);
            if (_identityCache.Count <= MaximumIdentityCache)
            {
                return;
            }
        }

        foreach (nint address in _identityCache.Keys.Take(
                     _identityCache.Count - MaximumIdentityCache).ToArray())
        {
            _identityCache.Remove(address);
        }
    }

    private LocalDataResult<LocalPoolSnapshot> ReadPublishedPool(
        LocalPoolKinds kind)
    {
        if (_published.TryGetValue(kind, out PublishedPool published))
        {
            return published.Status is LocalDataStatus.Success
                ? LocalDataResult<LocalPoolSnapshot>.Succeeded(
                    published.Snapshot)
                : LocalDataResult<LocalPoolSnapshot>.Failed(
                    published.Status);
        }

        return LocalDataResult<LocalPoolSnapshot>.Failed(
            LocalDataStatus.DataUnavailable);
    }

    private bool AdvanceKind(
        LocalPoolKinds kind,
        long now,
        int slotBudget,
        long deadline,
        out int processedSlots,
        out bool resolvedHandle)
    {
        processedSlots = 0;
        resolvedHandle = false;

        if (slotBudget <= 0)
        {
            return false;
        }

        if (!_refreshes.TryGetValue(kind, out PoolRefreshState? state))
        {
            if (now < _nextRefreshAt.GetValueOrDefault(kind))
            {
                SatisfyDemand(kind);
                return false;
            }

            if (!TryBeginRefresh(kind, out state, out LocalDataStatus failure))
            {
                PublishFailure(kind, failure, now);
                SatisfyDemand(kind);
                return false;
            }

            _refreshes[kind] = state;
        }

        if (!_verification.CanCreateScriptGuid(state.GuidPool))
        {
            PublishFailure(kind, LocalDataStatus.DataUnavailable, now);
            SatisfyDemand(kind);
            return false;
        }

        if (!state.HasView && !TryOpenNextCandidate(state))
        {
            PublishFailure(kind, state.LastFailure, now);
            SatisfyDemand(kind);
            return false;
        }

        if (state.VehicleView is VehiclePoolView vehicle)
        {
            if (!_verification.IsCurrent(vehicle))
            {
                state.AbandonCandidate(LocalDataStatus.VerificationFailed);
                return false;
            }
            ProcessVehicleSlots(
                state,
                vehicle,
                ref processedSlots,
                slotBudget,
                deadline,
                ref resolvedHandle);
            if (state.SlotIndex >= vehicle.Capacity)
            {
                CompleteCandidate(
                    state,
                    vehicle.Allocator,
                    vehicle.Capacity,
                    now);
            }
        }
        else if (state.FwView is FwPoolView fw)
        {
            if (!_verification.IsCurrent(fw))
            {
                state.AbandonCandidate(LocalDataStatus.VerificationFailed);
                return false;
            }
            ProcessFwSlots(
                state,
                fw,
                ref processedSlots,
                slotBudget,
                deadline,
                ref resolvedHandle);
            if (state.SlotIndex >= fw.Capacity)
            {
                CompleteCandidate(
                    state,
                    fw.Pool,
                    fw.Capacity,
                    now);
            }
        }
        else
        {
            state.AbandonCandidate(LocalDataStatus.VerificationFailed);
        }

        if (!_refreshes.ContainsKey(kind))
        {
            SatisfyDemand(kind);
        }
        else if (!state.HasView &&
                 state.CandidateIndex >= state.Candidates.Length)
        {
            PublishFailure(kind, state.LastFailure, now);
            SatisfyDemand(kind);
        }

        return processedSlots != 0 || resolvedHandle;
    }

    private bool TryBeginRefresh(
        LocalPoolKinds kind,
        [NotNullWhen(true)] out PoolRefreshState? refresh,
        out LocalDataStatus failure)
    {
        refresh = null;
        failure = LocalDataStatus.DataUnavailable;

        LocalDataResult<PoolBackendLocation[]> candidates =
            _backends.ResolvePoolCandidates(kind);
        if (!candidates.IsSuccess)
        {
            failure = candidates.Status;
            return false;
        }

        LocalDataResult<nint> createGuid =
            _backends.ResolveCreateGuid();
        if (!createGuid.IsSuccess ||
            !_memory.IsExecutableRange(createGuid.Value, 1))
        {
            failure = createGuid.IsSuccess
                ? LocalDataStatus.VerificationFailed
                : createGuid.Status;
            return false;
        }

        LocalDataResult<nint> guidPool =
            _backends.ResolveScriptGuidPool();
        if (!guidPool.IsSuccess)
        {
            failure = guidPool.Status;
            return false;
        }

        if (!_verification.CanCreateScriptGuid(guidPool.Value))
        {
            return false;
        }

        refresh = new(
            kind,
            candidates.Value,
            guidPool.Value,
            createGuid.Value);
        return true;
    }

    private bool TryOpenNextCandidate(PoolRefreshState state)
    {
        while (state.CandidateIndex < state.Candidates.Length)
        {
            PoolBackendLocation candidate =
                state.Candidates[state.CandidateIndex++];

            if (state.Kind is LocalPoolKinds.Vehicles)
            {
                LocalDataResult<VehiclePoolView> result =
                    _verification.ReadVehiclePool(
                        candidate.Address);
                if (result.IsSuccess)
                {
                    state.Open(result.Value);
                    return true;
                }

                state.LastFailure = result.Status;
                continue;
            }

            LocalDataResult<FwPoolView> fw =
                _verification.ReadFwPool(candidate.Address);
            if (fw.IsSuccess)
            {
                state.Open(fw.Value);
                return true;
            }

            state.LastFailure = fw.Status;
        }

        return false;
    }

    private void ProcessFwSlots(
        PoolRefreshState state,
        FwPoolView view,
        ref int processed,
        int budget,
        long deadline,
        ref bool resolvedHandle)
    {
        while (processed < budget &&
               state.SlotIndex < view.Capacity &&
               (processed == 0 || Stopwatch.GetTimestamp() < deadline) &&
               !resolvedHandle)
        {
            int index = state.SlotIndex;
            LocalDataResult<PoolSlotProbe> probe =
                _verification.ProbeFwSlot(
                    view,
                    index);
            if (!probe.IsSuccess)
            {
                if (IsTransientSlotFailure(probe.Status))
                {
                    state.SlotIndex++;
                    ++processed;
                    continue;
                }
                state.AbandonCandidate(probe.Status);
                return;
            }

            state.SlotIndex++;
            ++processed;

            if (!probe.Value.IsAllocated)
            {
                continue;
            }

            resolvedHandle = true;
            if (!TryAddHandle(
                    state,
                    probe.Value,
                    out LocalDataStatus failure))
            {
                state.AbandonCandidate(failure);
                return;
            }
        }
    }

    private void ProcessVehicleSlots(
        PoolRefreshState state,
        VehiclePoolView view,
        ref int processed,
        int budget,
        long deadline,
        ref bool resolvedHandle)
    {
        while (processed < budget &&
               state.SlotIndex < view.Capacity &&
               (processed == 0 || Stopwatch.GetTimestamp() < deadline) &&
               !resolvedHandle)
        {
            int index = state.SlotIndex;
            LocalDataResult<PoolSlotProbe> probe =
                _verification.ProbeVehicleSlot(
                    view,
                    index);
            if (!probe.IsSuccess)
            {
                if (IsTransientSlotFailure(probe.Status))
                {
                    state.SlotIndex++;
                    ++processed;
                    continue;
                }
                state.AbandonCandidate(probe.Status);
                return;
            }

            state.SlotIndex++;
            ++processed;

            if (!probe.Value.IsAllocated)
            {
                continue;
            }

            resolvedHandle = true;
            if (!TryAddHandle(
                    state,
                    probe.Value,
                    out LocalDataStatus failure))
            {
                state.AbandonCandidate(failure);
                return;
            }
        }
    }

    private static bool IsTransientSlotFailure(LocalDataStatus status) =>
        status is LocalDataStatus.DataUnavailable or
            LocalDataStatus.AccessRejected or
            LocalDataStatus.VerificationFailed;

    private bool TryAddHandle(
        PoolRefreshState state,
        PoolSlotProbe probe,
        out LocalDataStatus failure)
    {
        failure = LocalDataStatus.Success;

        GameThreadInt32CallResult result =
            _gameThread.InvokeInt32Guarded(
                state.CreateGuidAddress,
                probe.Entity,
                ToGameThreadGuard(probe.PrimaryGuard),
                ToGameThreadGuard(probe.SecondaryGuard));

        if (result.Status is GameThreadFunctionCallStatus.GuardRejected)
        {
            return true;
        }

        if (!result.IsSuccess)
        {
            failure = result.Status switch
            {
                GameThreadFunctionCallStatus.SessionStopping or
                GameThreadFunctionCallStatus.AdmissionRejected =>
                    LocalDataStatus.DataUnavailable,
                _ => LocalDataStatus.VerificationFailed
            };
            return false;
        }
        if (result.Value == 0)
        {
            return true;
        }

        CacheIdentitySuccess(
            probe.Entity,
            result.Value,
            Stopwatch.GetTimestamp());

        if (state.Seen.Add(result.Value))
        {
            state.Handles.Add(result.Value);
        }
        return true;
    }

    private static GameThreadMemoryGuard ToGameThreadGuard(
        PoolMemoryGuard guard) =>
        guard.IsConfigured
            ? new(
                guard.Address,
                guard.Mask,
                guard.Expected,
                guard.Width)
            : GameThreadMemoryGuard.None;

    private void CompleteCandidate(
        PoolRefreshState state,
        nint poolAddress,
        int expectedCapacity,
        long now)
    {
        LocalDataResult<LocalPoolStatistics> statistics =
            _verification.ReadStatistics(
                state.Kind,
                poolAddress);
        if (!statistics.IsSuccess ||
            statistics.Value.Capacity != expectedCapacity)
        {
            state.AbandonCandidate(
                statistics.IsSuccess
                    ? LocalDataStatus.VerificationFailed
                    : statistics.Status);
            return;
        }

        LocalPoolSnapshot candidate = new(
            0,
            statistics.Value,
            state.Handles.ToArray());

        if (!PoolVerification.IsSnapshotPlausible(candidate))
        {
            state.AbandonCandidate(LocalDataStatus.VerificationFailed);
            return;
        }

        ulong revision = ResolveRevision(state.Kind, candidate);
        PublishSuccess(
            state.Kind,
            candidate with { Revision = revision },
            now);
    }

    private void SatisfyDemand(LocalPoolKinds kind) =>
        _demanded &= ~kind;

    private ulong ResolveRevision(
        LocalPoolKinds kind,
        LocalPoolSnapshot candidate)
    {
        if (_published.TryGetValue(kind, out PublishedPool previous) &&
            previous.Status is LocalDataStatus.Success &&
            previous.Snapshot.Statistics == candidate.Statistics &&
            previous.Snapshot.Handles.Span.SequenceEqual(candidate.Handles.Span))
        {
            return previous.Snapshot.Revision;
        }

        _nextRevision = _nextRevision == ulong.MaxValue
            ? 1
            : _nextRevision + 1;
        return _nextRevision;
    }

    private void PublishSuccess(
        LocalPoolKinds kind,
        LocalPoolSnapshot snapshot,
        long now)
    {
        _published[kind] = new(LocalDataStatus.Success, snapshot);
        _refreshes.Remove(kind);
        long next = checked(now + RefreshIntervalTicks);
        _nextRefreshAt[kind] = next;
    }

    private void PublishFailure(
        LocalPoolKinds kind,
        LocalDataStatus failure,
        long now)
    {
        LocalPoolSnapshot retained =
            _published.TryGetValue(kind, out PublishedPool current)
                ? current.Snapshot
                : LocalPoolSnapshot.Empty;
        _published[kind] = new(failure, retained);
        _refreshes.Remove(kind);
        _nextRefreshAt[kind] = checked(now + RefreshIntervalTicks);
    }

    private static bool IsSingleKind(LocalPoolKinds kind) =>
        kind is LocalPoolKinds.Peds or
            LocalPoolKinds.Vehicles or
            LocalPoolKinds.Objects;

    private enum IdentityFailureStage
    {
        None = 0,
        AccessRejected = 1,
        CreateGuidUnavailable = 2,
        ScriptGuidPoolUnavailable = 3,
        ScriptGuidPoolRejected = 4,
        AdmissionRejected = 5,
        GuardRejected = 6,
        SessionStopping = 7,
        CallFailed = 8,
        ZeroHandle = 9
    }

    private readonly record struct IdentityResolution(
        LocalDataStatus Status,
        int Handle,
        IdentityFailureStage Failure)
    {
        internal bool IsSuccess =>
            Status is LocalDataStatus.Success && Handle != 0;

        internal static IdentityResolution Succeeded(int handle) =>
            new(LocalDataStatus.Success, handle, IdentityFailureStage.None);

        internal static IdentityResolution Failed(
            LocalDataStatus status,
            IdentityFailureStage failure) =>
            new(status, 0, failure);
    }

    private readonly record struct CachedIdentity(
        LocalDataStatus Status,
        int Handle,
        long ExpiresAt);

    private readonly record struct PublishedPool(
        LocalDataStatus Status,
        LocalPoolSnapshot Snapshot);

    private sealed class PoolRefreshState(
        LocalPoolKinds kind,
        PoolBackendLocation[] candidates,
        nint guidPool,
        nint createGuidAddress)
    {
        internal LocalPoolKinds Kind { get; } = kind;
        internal PoolBackendLocation[] Candidates { get; } = candidates;
        internal nint GuidPool { get; } = guidPool;
        internal nint CreateGuidAddress { get; } = createGuidAddress;
        internal int CandidateIndex { get; set; }
        internal int SlotIndex { get; set; }
        internal FwPoolView? FwView { get; private set; }
        internal VehiclePoolView? VehicleView { get; private set; }
        internal List<int> Handles { get; } = [];
        internal HashSet<int> Seen { get; } = [];
        internal LocalDataStatus LastFailure { get; set; } =
            LocalDataStatus.DataUnavailable;
        internal bool HasView => FwView is not null || VehicleView is not null;

        internal void Open(FwPoolView view)
        {
            FwView = view;
            VehicleView = null;
            ResetProgress();
        }

        internal void Open(VehiclePoolView view)
        {
            VehicleView = view;
            FwView = null;
            ResetProgress();
        }

        internal void AbandonCandidate(LocalDataStatus failure)
        {
            LastFailure = failure;
            FwView = null;
            VehicleView = null;
            ResetProgress();
        }

        private void ResetProgress()
        {
            SlotIndex = 0;
            Handles.Clear();
            Seen.Clear();
        }
    }
}