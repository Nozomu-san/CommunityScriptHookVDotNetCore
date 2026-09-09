using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using CommunityScriptHookVDotNetCore.Source;

namespace LocalNativeMemories.Source;

internal sealed class EntityPools : ILocalEntityPools, ILocalEntityIdentity
{
    private const int LocalSlotsPerHostFrame = 32;
    private const int MaximumHandleResolutionsPerHostFrame = 8;
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
    private readonly Dictionary<LocalPoolKinds, PublishedStatistics> _statistics = [];
    private readonly Dictionary<LocalPoolKinds, PoolRefreshState> _refreshes = [];
    private readonly Dictionary<LocalPoolKinds, long> _nextRefreshAt = [];
    private readonly Dictionary<LocalPoolKinds, long> _nextStatisticsAt = [];
    private readonly Queue<nint> _identityRequests = new(MaximumIdentityQueue);
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

    public LocalDataResult<LocalPoolStatistics> GetStatistics(
        LocalPoolKinds kind)
    {
        if (!IsSingleKind(kind))
        {
            return LocalDataResult<LocalPoolStatistics>.Failed(
                LocalDataStatus.InvalidRequest);
        }

        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_statistics.TryGetValue(
                    kind,
                    out PublishedStatistics cached) &&
                now < _nextStatisticsAt.GetValueOrDefault(kind))
            {
                return cached.Status is LocalDataStatus.Success
                    ? LocalDataResult<LocalPoolStatistics>.Succeeded(
                        cached.Statistics)
                    : LocalDataResult<LocalPoolStatistics>.Failed(
                        cached.Status);
            }

            LocalDataResult<PoolBackendLocation[]> candidates =
                _backends.ResolvePoolCandidates(kind);
            if (!candidates.IsSuccess)
            {
                PublishStatisticsFailure(kind, candidates.Status, now);
                return ReadPublishedStatistics(kind);
            }

            LocalDataStatus lastFailure = LocalDataStatus.DataUnavailable;
            foreach (PoolBackendLocation candidate in candidates.Value)
            {
                LocalDataResult<LocalPoolStatistics> result =
                    _verification.ReadStatistics(
                        kind,
                        candidate.Address);
                if (result.IsSuccess)
                {
                    PublishStatisticsSuccess(kind, result.Value, now);
                    return result;
                }

                lastFailure = result.Status;
            }

            PublishStatisticsFailure(kind, lastFailure, now);
            return ReadPublishedStatistics(kind);
        }
    }

    public LocalDataResult<EntityPoolsSnapshot> Capture(LocalPoolKinds kinds)
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

    internal void AdvanceHostFrame()
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            int remainingHandleResolutions = MaximumHandleResolutionsPerHostFrame;
            int identityBudget = ResolveIdentityBudget();
            while (identityBudget > 0 &&
                   remainingHandleResolutions > 0 &&
                   AdvanceIdentity(now))
            {
                --identityBudget;
                --remainingHandleResolutions;
            }

            if (_demanded is LocalPoolKinds.None)
            {
                return;
            }

            int remainingSlots = LocalSlotsPerHostFrame;
            int withoutProgress = 0;

            while (remainingSlots > 0 &&
                   remainingHandleResolutions > 0 &&
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
            ProcessVehicleSlots(
                state,
                vehicle,
                ref processedSlots,
                slotBudget,
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
            ProcessFwSlots(
                state,
                fw,
                ref processedSlots,
                slotBudget,
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
        ref bool resolvedHandle)
    {
        while (processed < budget &&
               state.SlotIndex < view.Capacity &&
               !resolvedHandle)
        {
            int index = state.SlotIndex;
            LocalDataResult<PoolSlotProbe> probe =
                _verification.ProbeFwSlot(
                    view,
                    index);
            if (!probe.IsSuccess)
            {
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
        ref bool resolvedHandle)
    {
        while (processed < budget &&
               state.SlotIndex < view.Capacity &&
               !resolvedHandle)
        {
            int index = state.SlotIndex;
            LocalDataResult<PoolSlotProbe> probe =
                _verification.ProbeVehicleSlot(
                    view,
                    index);
            if (!probe.IsSuccess)
            {
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
        _statistics[kind] = new(
            LocalDataStatus.Success,
            snapshot.Statistics);
        _refreshes.Remove(kind);
        long next = checked(now + RefreshIntervalTicks);
        _nextRefreshAt[kind] = next;
        _nextStatisticsAt[kind] = next;
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

    private LocalDataResult<LocalPoolStatistics> ReadPublishedStatistics(
        LocalPoolKinds kind)
    {
        if (_statistics.TryGetValue(
                kind,
                out PublishedStatistics published))
        {
            return published.Status is LocalDataStatus.Success
                ? LocalDataResult<LocalPoolStatistics>.Succeeded(
                    published.Statistics)
                : LocalDataResult<LocalPoolStatistics>.Failed(
                    published.Status);
        }

        return LocalDataResult<LocalPoolStatistics>.Failed(
            LocalDataStatus.DataUnavailable);
    }

    private void PublishStatisticsSuccess(
        LocalPoolKinds kind,
        LocalPoolStatistics statistics,
        long now)
    {
        _statistics[kind] = new(LocalDataStatus.Success, statistics);
        _nextStatisticsAt[kind] = checked(now + RefreshIntervalTicks);
    }

    private void PublishStatisticsFailure(
        LocalPoolKinds kind,
        LocalDataStatus failure,
        long now)
    {
        LocalPoolStatistics retained =
            _statistics.TryGetValue(
                kind,
                out PublishedStatistics current)
                ? current.Statistics
                : LocalPoolStatistics.Empty;
        _statistics[kind] = new(failure, retained);
        _nextStatisticsAt[kind] = checked(now + RefreshIntervalTicks);
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

    private readonly record struct PublishedStatistics(
        LocalDataStatus Status,
        LocalPoolStatistics Statistics);

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
