using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using LocalNativeMemories.Source;

namespace LowLevelEvents.Source;

internal sealed class EventStream :
    ILowLevelEventStream,
    ILowLevelDamageStream,
    ILowLevelWeaponEventStream,
    ILowLevelCombatEventStream,
    IDisposable
{
    private const int MaximumNativeDrainPerHostFrame = 1024;
    private const int MaximumSubscriptionCapacity = 2048;
    private const uint ReactionStreamMask = 1u << 0;
    private const uint GroupStreamMask = 1u << 1;
    private const uint GlobalStreamMask = 1u << 2;
    private const uint AllDispatchStreamMask =
        ReactionStreamMask | GroupStreamMask | GlobalStreamMask;
    private const int SourcePositionOffset = 56;
    private const int ImpactPositionOffset = 72;
    private const int WeaponHashOffset = 96;
    private static readonly long PendingIdentityLifetimeTicks =
        checked(Stopwatch.Frequency * 2L);

    private readonly NativeBridge _bridge;
    private readonly ILocalEntityIdentity _identity;
    private readonly Lock _subscriptionGate = new();
    private readonly Dictionary<Subscription<RawLowLevelEvent>, HashSet<uint>>
        _rawSubscriptions = [];
    private readonly Dictionary<Subscription<RawLowLevelEvent>, HashSet<uint>>
        _catalogSubscriptions = [];
    private readonly List<Subscription<EntityDamageEvent>> _damageSubscriptions = [];
    private readonly List<Subscription<MeleeActionEvent>> _meleeActionSubscriptions = [];
    private readonly List<Subscription<GunAimedAtEvent>> _gunAimedAtSubscriptions = [];
    private readonly List<ListenerSubscription<GunAimedAtEvent>> _gunAimedAtListeners = [];
    private readonly List<Subscription<GunShotEvent>> _gunShotSubscriptions = [];
    private readonly List<Subscription<GunShotWhizzedByEvent>> _gunShotWhizzedBySubscriptions = [];
    private readonly List<Subscription<BulletImpactEvent>> _bulletImpactSubscriptions = [];
    private readonly Queue<PendingDamageEvent> _pendingDamageEvents = [];
    private readonly Dictionary<ulong, PendingAimEvent> _pendingAimEvents = [];
    private readonly Queue<PendingWhizzedByEvent> _pendingWhizzedByEvents = [];
    private readonly Queue<PendingWeaponEvent> _pendingWeaponEvents = [];
    private ulong _continuityRevision;
    private ulong _continuityLossCount;
    private ulong _lastNativeDroppedCount;
    private ulong _subscriberDroppedCount;
    private ulong _damageRawCount;
    private ulong _damagePublishedCount;
    private ulong _damageDroppedCount;
    private ulong _damageIdentityQueuedCount;
    private ulong _damageVictimIdentityResolvedCount;
    private ulong _damageCulpritIdentityResolvedCount;
    private ulong _damageVictimIdentityExpiredCount;
    private ulong _damageCulpritIdentityExpiredCount;
    private ulong _damageMagnitudeAvailableCount;
    private ulong _damageMagnitudeUnavailableCount;
    private ulong _gunAimedAtRawCount;
    private ulong _gunAimedAtReactionCount;
    private ulong _gunAimedAtGroupCount;
    private ulong _gunAimedAtGlobalCount;
    private ulong _gunAimedAtPublishedCount;
    private ulong _gunAimedAtMissingSourceCount;
    private ulong _gunAimedAtMissingTargetCount;
    private ulong _meleeActionPublishedCount;
    private ulong _meleeActionDroppedCount;
    private ulong _gunAimedAtDroppedCount;
    private ulong _gunShotPublishedCount;
    private ulong _gunShotDroppedCount;
    private ulong _gunShotWhizzedByDroppedCount;
    private ulong _bulletImpactPublishedCount;
    private ulong _bulletImpactDroppedCount;
    private ulong _weaponMalformedCount;
    private ulong _weaponMissingShooterCount;
    private ulong _weaponIdentityQueuedCount;
    private ulong _weaponIdentityResolvedCount;
    private ulong _weaponIdentityExpiredCount;
    private ulong _gunShotReactionCount;
    private ulong _gunShotGroupCount;
    private ulong _gunShotGlobalCount;
    private ulong _bulletImpactReactionCount;
    private ulong _bulletImpactGroupCount;
    private ulong _bulletImpactGlobalCount;
    private bool _disposed;

    internal EventStream(
        NativeBridge bridge,
        ILocalEntityIdentity identity)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _lastNativeDroppedCount = NativeBridge.DroppedCount;
    }

    public ulong ContinuityRevision => _continuityRevision;
    public ulong ContinuityLossCount => _continuityLossCount;
    public uint GameBuild => NativeBridge.GameBuild;
    public LowLevelEventBridgeStatus BridgeStatus => NativeBridge.Status;
    public ulong NativeDroppedCount => NativeBridge.DroppedCount;
    public ulong ManagedDroppedCount =>
        _subscriberDroppedCount +
        _damageDroppedCount +
        _meleeActionDroppedCount +
        _gunAimedAtDroppedCount +
        _gunShotDroppedCount +
        _gunShotWhizzedByDroppedCount +
        _bulletImpactDroppedCount +
        _weaponMalformedCount +
        _weaponMissingShooterCount +
        _weaponIdentityExpiredCount;

    public IReadOnlyList<GameEventDescriptor> Catalog => _bridge.Catalog;

    public LowLevelEventDiagnostics Diagnostics => new(
        _continuityRevision,
        _continuityLossCount,
        NativeBridge.DroppedCount,
        NativeBridge.PendingCount,
        ManagedDroppedCount,
        _subscriberDroppedCount,
        _damageRawCount,
        _damagePublishedCount,
        _damageDroppedCount,
        _damageIdentityQueuedCount,
        _damageVictimIdentityResolvedCount,
        _damageCulpritIdentityResolvedCount,
        _damageVictimIdentityExpiredCount,
        _damageCulpritIdentityExpiredCount,
        _pendingDamageEvents.Count,
        _damageMagnitudeAvailableCount,
        _damageMagnitudeUnavailableCount,
        _gunAimedAtRawCount,
        _gunAimedAtReactionCount,
        _gunAimedAtGroupCount,
        _gunAimedAtGlobalCount,
        _gunAimedAtPublishedCount,
        _gunAimedAtMissingSourceCount,
        _gunAimedAtMissingTargetCount,
        _meleeActionPublishedCount,
        _meleeActionDroppedCount,
        _gunShotPublishedCount,
        _gunShotDroppedCount,
        _bulletImpactPublishedCount,
        _bulletImpactDroppedCount,
        _weaponMalformedCount,
        _weaponMissingShooterCount,
        _weaponIdentityQueuedCount,
        _weaponIdentityResolvedCount,
        _weaponIdentityExpiredCount,
        _pendingWeaponEvents.Count,
        _gunShotReactionCount,
        _gunShotGroupCount,
        _gunShotGlobalCount,
        _bulletImpactReactionCount,
        _bulletImpactGroupCount,
        _bulletImpactGlobalCount,
        SubscriptionCount,
        NativeBridge.ObservedCount(NativeAbi.EntityDamageMetadataEventId),
        NativeBridge.ObservedCount(EventCatalog.MeleeAction),
        NativeBridge.ObservedCount(EventCatalog.ShockingGunshotFired),
        NativeBridge.ObservedCount(EventCatalog.ShockingVisibleWeapon),
        NativeBridge.ObservedCount(EventCatalog.GunShot),
        NativeBridge.ObservedCount(EventCatalog.GunShotBulletImpact),
        NativeBridge.ObservedCount(EventCatalog.GunShotWhizzedBy),
        NativeBridge.ObservedCount(EventCatalog.EntityDamaged));

    private int SubscriptionCount
    {
        get
        {
            lock (_subscriptionGate)
            {
                return _rawSubscriptions.Count +
                    _catalogSubscriptions.Count +
                    _damageSubscriptions.Count +
                    _meleeActionSubscriptions.Count +
                    _gunAimedAtSubscriptions.Count +
                    _gunAimedAtListeners.Count +
                    _gunShotSubscriptions.Count +
                    _gunShotWhizzedBySubscriptions.Count +
                    _bulletImpactSubscriptions.Count;
            }
        }
    }

    internal void ConfigureDetailedEventIds(IEnumerable<uint> eventIds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _bridge.ReplaceDetailedEventIds(eventIds);
    }

    internal void ConfigureCapturePolicy()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_subscriptionGate)
        {
            RefreshNativeCapturePolicyLocked();
        }
    }

    private void RefreshNativeCapturePolicyLocked()
    {
        Dictionary<uint, uint> rules = [];
        Dictionary<uint, uint> catalogRules = [];

        static void AddRule(
            Dictionary<uint, uint> rules,
            uint eventId,
            uint streamMask)
        {
            if (rules.TryGetValue(eventId, out uint existing))
            {
                rules[eventId] = existing | streamMask;
            }
            else
            {
                rules.Add(eventId, streamMask);
            }
        }

        static void AddCatalogRule(
            Dictionary<uint, uint> rules,
            uint catalogEventId,
            uint streamMask)
        {
            if (rules.TryGetValue(catalogEventId, out uint existing))
            {
                rules[catalogEventId] = existing | streamMask;
            }
            else
            {
                rules.Add(catalogEventId, streamMask);
            }
        }

        if (_meleeActionSubscriptions.Count != 0)
        {
            AddRule(rules, EventCatalog.MeleeAction, AllDispatchStreamMask);
        }
        if (_gunAimedAtSubscriptions.Count != 0 ||
            _gunAimedAtListeners.Count != 0)
        {
            AddRule(rules, EventCatalog.GunAimedAt, AllDispatchStreamMask);
        }
        if (_gunShotSubscriptions.Count != 0)
        {
            AddRule(rules, EventCatalog.GunShot, GroupStreamMask);
        }
        if (_gunShotWhizzedBySubscriptions.Count != 0)
        {
            AddRule(
                rules,
                EventCatalog.GunShotWhizzedBy,
                ReactionStreamMask);
        }
        if (_bulletImpactSubscriptions.Count != 0)
        {
            AddRule(rules, EventCatalog.GunShotBulletImpact, GroupStreamMask);
        }

        foreach (HashSet<uint> ids in _rawSubscriptions.Values)
        {
            foreach (uint eventId in ids)
            {
                AddRule(rules, eventId, AllDispatchStreamMask);
            }
        }

        foreach (HashSet<uint> catalogIds in _catalogSubscriptions.Values)
        {
            foreach (uint catalogEventId in catalogIds)
            {
                AddCatalogRule(catalogRules, catalogEventId, AllDispatchStreamMask);
            }
        }


        _bridge.ReplaceCaptureStreamMasks(rules);
        _bridge.ReplaceCatalogCaptureStreamMasks(catalogRules);
    }

    internal void Advance(ulong _)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObserveNativeDrops();
        DrainPendingDamageEvents();
        DrainPendingAimEvents();
        DrainPendingWhizzedByEvents();
        DrainPendingWeaponEvents();

        int nativeDrainBudget = ResolveNativeDrainBudget();
        for (int drained = 0;
             drained < nativeDrainBudget && _bridge.TryDequeue();
             ++drained)
        {
            RawLowLevelEvent lowLevelEvent = EventDecoder.Decode(_bridge.Buffer);
            if (lowLevelEvent.EventName is null && lowLevelEvent.CatalogEventId != 0)
            {
                lowLevelEvent = lowLevelEvent with
                {
                    EventName = _bridge.ResolveCatalogEventName(lowLevelEvent.CatalogEventId)
                };
            }
            lowLevelEvent = ResolveEventIdentities(lowLevelEvent);

            if (lowLevelEvent.EventId == NativeAbi.EntityDamageMetadataEventId)
            {
                if (TryDecodeDamage(lowLevelEvent, out PendingDamageEvent pendingDamage))
                {
                    ++_damageRawCount;
                    QueueOrPublishDamage(pendingDamage);
                }
                else
                {
                    MarkContinuityLoss();
                }
            }

            ProcessMeleeAction(lowLevelEvent);
            ProcessAimEvent(lowLevelEvent);
            ProcessWhizzedByEvent(lowLevelEvent);
            ProcessWeaponEvent(lowLevelEvent);
            _subscriberDroppedCount += PublishRaw(lowLevelEvent);
        }
    }

    private static int ResolveNativeDrainBudget()
    {
        ulong pending = NativeBridge.PendingCount;
        if (pending == 0)
        {
            return 0;
        }

        return checked((int)Math.Min(
            pending,
            (ulong)MaximumNativeDrainPerHostFrame));
    }

    public ILowLevelSubscription<RawLowLevelEvent> SubscribeRaw(
        IEnumerable<uint> eventIds,
        int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(eventIds);
        ValidateCapacity(capacity);

        HashSet<uint> ids = [.. eventIds];
        if (ids.Count == 0)
        {
            throw new ArgumentException(
                "At least one event id is required.",
                nameof(eventIds));
        }

        Subscription<RawLowLevelEvent>? subscription = null;
        subscription = new(
            capacity,
            () => RemoveRawSubscription(subscription!));

        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rawSubscriptions.Add(subscription, ids);
            RefreshNativeCapturePolicyLocked();
        }
        return subscription;
    }

    public ILowLevelSubscription<RawLowLevelEvent> SubscribeCatalog(
        IEnumerable<string> eventNames,
        int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(eventNames);
        ValidateCapacity(capacity);

        HashSet<uint> catalogIds = [];
        foreach (string eventName in eventNames)
        {
            if (!_bridge.TryResolveCatalogEvent(eventName, out GameEventDescriptor descriptor))
            {
                throw new ArgumentException(
                    $"Unknown CEvent catalog entry: {eventName}.",
                    nameof(eventNames));
            }

            catalogIds.Add(descriptor.CatalogId);
        }

        if (catalogIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one CEvent catalog entry is required.",
                nameof(eventNames));
        }

        Subscription<RawLowLevelEvent>? subscription = null;
        subscription = new(
            capacity,
            () => RemoveCatalogSubscription(subscription!));

        lock (_subscriptionGate)
        {
            _catalogSubscriptions.Add(subscription, catalogIds);
            RefreshNativeCapturePolicyLocked();
        }

        return subscription;
    }


    public ILowLevelSubscription<EntityDamageEvent> SubscribeDamage(
        int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCapacity(capacity);

        Subscription<EntityDamageEvent>? subscription = null;
        subscription = new(
            capacity,
            () => RemoveDamageSubscription(subscription!));

        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _damageSubscriptions.Add(subscription);
        }
        return subscription;
    }

    public ILowLevelSubscription<MeleeActionEvent> SubscribeMeleeActions(
        int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCapacity(capacity);

        Subscription<MeleeActionEvent>? subscription = null;
        subscription = new(
            capacity,
            () => RemoveMeleeActionSubscription(subscription!));

        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _meleeActionSubscriptions.Add(subscription);
            RefreshNativeCapturePolicyLocked();
        }
        return subscription;
    }

    public ILowLevelSubscription<GunAimedAtEvent> SubscribeGunAimedAt(
        int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCapacity(capacity);

        Subscription<GunAimedAtEvent>? subscription = null;
        subscription = new(
            capacity,
            () => RemoveGunAimedAtSubscription(subscription!));

        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _gunAimedAtSubscriptions.Add(subscription);
            RefreshNativeCapturePolicyLocked();
        }
        return subscription;
    }

    public ILowLevelListenerSubscription ListenGunAimedAt(
        Action<GunAimedAtEvent> listener)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(listener);

        ListenerSubscription<GunAimedAtEvent>? subscription = null;
        subscription = new(
            listener,
            () => RemoveGunAimedAtListener(subscription!));

        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _gunAimedAtListeners.Add(subscription);
            RefreshNativeCapturePolicyLocked();
        }
        return subscription;
    }

    public ILowLevelSubscription<GunShotEvent> SubscribeGunShots(
        int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCapacity(capacity);

        Subscription<GunShotEvent>? subscription = null;
        subscription = new(
            capacity,
            () => RemoveGunShotSubscription(subscription!));

        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _gunShotSubscriptions.Add(subscription);
            RefreshNativeCapturePolicyLocked();
        }
        return subscription;
    }

    public ILowLevelSubscription<GunShotWhizzedByEvent> SubscribeGunShotWhizzedBy(
        int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCapacity(capacity);

        Subscription<GunShotWhizzedByEvent>? subscription = null;
        subscription = new(
            capacity,
            () => RemoveGunShotWhizzedBySubscription(subscription!));

        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _gunShotWhizzedBySubscriptions.Add(subscription);
            RefreshNativeCapturePolicyLocked();
        }
        return subscription;
    }

    public ILowLevelSubscription<BulletImpactEvent> SubscribeBulletImpacts(
        int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCapacity(capacity);

        Subscription<BulletImpactEvent>? subscription = null;
        subscription = new(
            capacity,
            () => RemoveBulletImpactSubscription(subscription!));

        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _bulletImpactSubscriptions.Add(subscription);
            RefreshNativeCapturePolicyLocked();
        }
        return subscription;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Subscription<RawLowLevelEvent>[] raw;
        Subscription<RawLowLevelEvent>[] catalog;
        Subscription<EntityDamageEvent>[] damage;
        Subscription<MeleeActionEvent>[] meleeActions;
        Subscription<GunAimedAtEvent>[] gunAimedAt;
        ListenerSubscription<GunAimedAtEvent>[] gunAimedAtListeners;
        Subscription<GunShotEvent>[] gunShots;
        Subscription<GunShotWhizzedByEvent>[] whizzedBy;
        Subscription<BulletImpactEvent>[] impacts;
        lock (_subscriptionGate)
        {
            raw = [.. _rawSubscriptions.Keys];
            catalog = [.. _catalogSubscriptions.Keys];
            damage = [.. _damageSubscriptions];
            meleeActions = [.. _meleeActionSubscriptions];
            gunAimedAt = [.. _gunAimedAtSubscriptions];
            gunAimedAtListeners = [.. _gunAimedAtListeners];
            gunShots = [.. _gunShotSubscriptions];
            whizzedBy = [.. _gunShotWhizzedBySubscriptions];
            impacts = [.. _bulletImpactSubscriptions];
            _rawSubscriptions.Clear();
            _catalogSubscriptions.Clear();
            _damageSubscriptions.Clear();
            _meleeActionSubscriptions.Clear();
            _gunAimedAtSubscriptions.Clear();
            _gunAimedAtListeners.Clear();
            _gunShotSubscriptions.Clear();
            _gunShotWhizzedBySubscriptions.Clear();
            _bulletImpactSubscriptions.Clear();
            RefreshNativeCapturePolicyLocked();
        }

        foreach (Subscription<RawLowLevelEvent> subscription in raw)
        {
            subscription.Detach();
        }
        foreach (Subscription<RawLowLevelEvent> subscription in catalog)
        {
            subscription.Detach();
        }
        foreach (Subscription<EntityDamageEvent> subscription in damage)
        {
            subscription.Detach();
        }
        foreach (Subscription<MeleeActionEvent> subscription in meleeActions)
        {
            subscription.Detach();
        }
        foreach (Subscription<GunAimedAtEvent> subscription in gunAimedAt)
        {
            subscription.Detach();
        }
        foreach (ListenerSubscription<GunAimedAtEvent> subscription in gunAimedAtListeners)
        {
            subscription.Detach();
        }
        foreach (Subscription<GunShotEvent> subscription in gunShots)
        {
            subscription.Detach();
        }
        foreach (
            Subscription<GunShotWhizzedByEvent> subscription
            in whizzedBy)
        {
            subscription.Detach();
        }
        foreach (Subscription<BulletImpactEvent> subscription in impacts)
        {
            subscription.Detach();
        }

        _pendingDamageEvents.Clear();
        _pendingAimEvents.Clear();
        _pendingWhizzedByEvents.Clear();
        _pendingWeaponEvents.Clear();
        _bridge.Dispose();
    }

    private void ObserveNativeDrops()
    {
        ulong current = NativeBridge.DroppedCount;
        if (current == _lastNativeDroppedCount)
        {
            return;
        }

        ulong lost = current > _lastNativeDroppedCount
            ? current - _lastNativeDroppedCount
            : 1;
        MarkContinuityLoss(lost);
        _lastNativeDroppedCount = current;
    }

    private void MarkContinuityLoss(ulong count = 1)
    {
        if (count == 0)
        {
            return;
        }

        _continuityLossCount += count;
        ++_continuityRevision;
    }

    private RawLowLevelEvent ResolveEventIdentities(
        RawLowLevelEvent lowLevelEvent)
    {
        if (!EventCatalog.RequiresEntityIdentity(
                lowLevelEvent.EventId,
                lowLevelEvent.CatalogEventId))
        {
            return lowLevelEvent;
        }

        int relatedHandle = ResolveCachedOrRequest(
            lowLevelEvent.RelatedEntityAddress);
        int dispatchHandle = ResolveCachedOrRequest(
            lowLevelEvent.DispatchEntityAddress);

        return lowLevelEvent with
        {
            RelatedEntityHandle = relatedHandle,
            DispatchEntityHandle = dispatchHandle
        };
    }

    private int ResolveCachedOrRequest(ulong entityAddress)
    {
        if (entityAddress == 0)
        {
            return 0;
        }

        nint address = unchecked((nint)(long)entityAddress);
        LocalDataResult<int> cached = _identity.TryGetHandle(address);
        if (cached.IsSuccess)
        {
            return cached.Value;
        }

        _identity.RequestHandle(address);
        return 0;
    }

    private static bool TryDecodeDamage(
        RawLowLevelEvent lowLevelEvent,
        out PendingDamageEvent pending)
    {
        pending = default;
        if (lowLevelEvent.EventId != NativeAbi.EntityDamageMetadataEventId ||
            lowLevelEvent.RelatedEntityAddress == 0 ||
            lowLevelEvent.Arguments.Length < 6)
        {
            return false;
        }

        ReadOnlySpan<ulong> arguments = lowLevelEvent.Arguments.Span;
        DamageMagnitudeSource magnitudeSource =
            (DamageMagnitudeSource)unchecked((uint)arguments[5]);
        if (magnitudeSource is not DamageMagnitudeSource.Unavailable and
            not DamageMagnitudeSource.DamageProcess)
        {
            return false;
        }

        float baseDamage = BitConverter.Int32BitsToSingle(
            unchecked((int)(uint)arguments[4]));
        if (!float.IsFinite(baseDamage) || baseDamage < 0f)
        {
            return false;
        }

        long now = Stopwatch.GetTimestamp();
        pending = new(
            lowLevelEvent.Sequence,
            lowLevelEvent.PerformanceCounter,
            lowLevelEvent.RelatedEntityAddress,
            lowLevelEvent.RelatedEntityHandle,
            arguments[0],
            0,
            unchecked((uint)arguments[1]),
            unchecked((uint)arguments[2]),
            arguments[3] != 0,
            baseDamage,
            magnitudeSource,
            checked(now + PendingIdentityLifetimeTicks));
        return true;
    }

    private void QueueOrPublishDamage(PendingDamageEvent pending)
    {
        int victimHandle = pending.VictimHandle;
        if (victimHandle == 0)
        {
            victimHandle = ResolveCachedOrRequest(pending.VictimAddress);
            if (victimHandle != 0)
            {
                ++_damageVictimIdentityResolvedCount;
            }
        }

        int culpritHandle = pending.CulpritHandle;
        if (pending.CulpritAddress != 0 && culpritHandle == 0)
        {
            culpritHandle = ResolveCachedOrRequest(pending.CulpritAddress);
            if (culpritHandle != 0)
            {
                ++_damageCulpritIdentityResolvedCount;
            }
        }

        if (victimHandle != 0 &&
            (pending.CulpritAddress == 0 || culpritHandle != 0))
        {
            PublishResolvedDamage(pending, victimHandle, culpritHandle);
            return;
        }

        ++_damageIdentityQueuedCount;
        _pendingDamageEvents.Enqueue(
            pending with
            {
                VictimHandle = victimHandle,
                CulpritHandle = culpritHandle
            });
    }

    private void DrainPendingDamageEvents()
    {
        if (_pendingDamageEvents.Count == 0)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        int count = _pendingDamageEvents.Count;
        for (int index = 0; index < count; ++index)
        {
            PendingDamageEvent pending = _pendingDamageEvents.Dequeue();
            int victimHandle = pending.VictimHandle;
            if (victimHandle == 0)
            {
                victimHandle = ResolveCachedOrRequest(pending.VictimAddress);
                if (victimHandle != 0)
                {
                    ++_damageVictimIdentityResolvedCount;
                }
            }

            int culpritHandle = pending.CulpritHandle;
            if (pending.CulpritAddress != 0 && culpritHandle == 0)
            {
                culpritHandle = ResolveCachedOrRequest(pending.CulpritAddress);
                if (culpritHandle != 0)
                {
                    ++_damageCulpritIdentityResolvedCount;
                }
            }

            if (victimHandle != 0 &&
                (pending.CulpritAddress == 0 || culpritHandle != 0))
            {
                PublishResolvedDamage(pending, victimHandle, culpritHandle);
                continue;
            }

            if (now >= pending.ExpiresAt)
            {
                if (victimHandle == 0)
                {
                    ++_damageVictimIdentityExpiredCount;
                }
                if (pending.CulpritAddress != 0 && culpritHandle == 0)
                {
                    ++_damageCulpritIdentityExpiredCount;
                }
                ++_damageDroppedCount;
                MarkContinuityLoss();
                continue;
            }

            _pendingDamageEvents.Enqueue(
                pending with
                {
                    VictimHandle = victimHandle,
                    CulpritHandle = culpritHandle
                });
        }
    }

    private void PublishResolvedDamage(
        PendingDamageEvent pending,
        int victimHandle,
        int culpritHandle)
    {
        EntityDamageEvent damage = new(
            pending.Sequence,
            pending.PerformanceCounter,
            pending.VictimAddress,
            victimHandle,
            pending.CulpritAddress,
            culpritHandle,
            pending.WeaponHash,
            pending.GameTime,
            pending.State,
            pending.BaseDamage,
            pending.MagnitudeSource);
        ++_damagePublishedCount;
        if (pending.MagnitudeSource is DamageMagnitudeSource.DamageProcess)
        {
            ++_damageMagnitudeAvailableCount;
        }
        else
        {
            ++_damageMagnitudeUnavailableCount;
        }
        _damageDroppedCount += PublishDamage(damage);
    }

    private void ProcessMeleeAction(RawLowLevelEvent lowLevelEvent)
    {
        if (lowLevelEvent.EventId != EventCatalog.MeleeAction)
        {
            return;
        }

        MeleeActionEvent melee = new(
            lowLevelEvent.Sequence,
            lowLevelEvent.PerformanceCounter,
            lowLevelEvent.RelatedEntityAddress,
            lowLevelEvent.RelatedEntityHandle,
            lowLevelEvent.Stream,
            lowLevelEvent.Arguments,
            lowLevelEvent.ProbeData);
        ++_meleeActionPublishedCount;
        _meleeActionDroppedCount += PublishMeleeAction(melee);
    }

    private void ProcessAimEvent(RawLowLevelEvent lowLevelEvent)
    {
        if (lowLevelEvent.EventId != EventCatalog.GunAimedAt)
        {
            return;
        }

        ++_gunAimedAtRawCount;
        switch (lowLevelEvent.Stream)
        {
            case LowLevelEventStream.Reaction:
                ++_gunAimedAtReactionCount;
                break;
            case LowLevelEventStream.Group:
                ++_gunAimedAtGroupCount;
                break;
            case LowLevelEventStream.Global:
                ++_gunAimedAtGlobalCount;
                break;
        }

        ulong shooterAddress = lowLevelEvent.RelatedEntityAddress;
        int shooterHandle = lowLevelEvent.RelatedEntityHandle;
        ulong victimAddress = lowLevelEvent.DispatchEntityAddress;
        int victimHandle = lowLevelEvent.DispatchEntityHandle;

        if (lowLevelEvent.Stream == LowLevelEventStream.Reaction)
        {
            shooterAddress = lowLevelEvent.DispatchEntityAddress;
            shooterHandle = lowLevelEvent.DispatchEntityHandle;
            victimAddress = lowLevelEvent.RelatedEntityAddress;
            victimHandle = lowLevelEvent.RelatedEntityHandle;
        }

        if (shooterAddress == 0)
        {
            ++_gunAimedAtMissingSourceCount;
            return;
        }
        if (victimAddress == 0)
        {
            ++_gunAimedAtMissingTargetCount;
            return;
        }

        PendingAimEvent pending = new(
            lowLevelEvent.Sequence,
            lowLevelEvent.PerformanceCounter,
            shooterAddress,
            shooterHandle,
            victimAddress,
            victimHandle);

        if (pending.ShooterHandle != 0 && pending.VictimHandle != 0)
        {
            _pendingAimEvents.Remove(pending.ShooterAddress);
            PublishAim(pending, pending.ShooterHandle, pending.VictimHandle);
            return;
        }

        _pendingAimEvents[pending.ShooterAddress] = pending;
    }

    private void DrainPendingAimEvents()
    {
        if (_pendingAimEvents.Count == 0)
        {
            return;
        }

        PendingAimEvent[] pendingEvents = [.. _pendingAimEvents.Values];
        foreach (PendingAimEvent pending in pendingEvents)
        {
            if (!_pendingAimEvents.TryGetValue(
                    pending.ShooterAddress,
                    out PendingAimEvent current) ||
                current.Sequence != pending.Sequence)
            {
                continue;
            }

            int shooterHandle = pending.ShooterHandle;
            int victimHandle = pending.VictimHandle;

            if (shooterHandle == 0)
            {
                shooterHandle = ResolveCachedOrRequest(pending.ShooterAddress);
            }
            if (victimHandle == 0)
            {
                victimHandle = ResolveCachedOrRequest(pending.VictimAddress);
            }

            if (shooterHandle != 0 && victimHandle != 0)
            {
                _pendingAimEvents.Remove(pending.ShooterAddress);
                PublishAim(pending, shooterHandle, victimHandle);
                continue;
            }

            _pendingAimEvents[pending.ShooterAddress] =
                pending with
                {
                    ShooterHandle = shooterHandle,
                    VictimHandle = victimHandle
                };
        }
    }

    private void PublishAim(
        PendingAimEvent pending,
        int shooterHandle,
        int victimHandle)
    {
        GunAimedAtEvent aimedAt = new(
            pending.Sequence,
            pending.PerformanceCounter,
            pending.ShooterAddress,
            shooterHandle,
            pending.VictimAddress,
            victimHandle);
        ++_gunAimedAtPublishedCount;
        _gunAimedAtDroppedCount += PublishGunAimedAt(aimedAt);
    }

    private void ProcessWhizzedByEvent(RawLowLevelEvent lowLevelEvent)
    {
        if (lowLevelEvent.EventId != EventCatalog.GunShotWhizzedBy ||
            lowLevelEvent.Stream != LowLevelEventStream.Reaction)
        {
            return;
        }

        ulong shooterAddress = lowLevelEvent.DispatchEntityAddress;
        int shooterHandle = lowLevelEvent.DispatchEntityHandle;
        ulong victimAddress = lowLevelEvent.RelatedEntityAddress;
        int victimHandle = lowLevelEvent.RelatedEntityHandle;

        if (shooterAddress == 0 || victimAddress == 0 ||
            shooterAddress == victimAddress)
        {
            return;
        }

        PendingWhizzedByEvent pending = new(
            lowLevelEvent.Sequence,
            lowLevelEvent.PerformanceCounter,
            shooterAddress,
            shooterHandle,
            victimAddress,
            victimHandle,
            checked(Stopwatch.GetTimestamp() + PendingIdentityLifetimeTicks));

        if (shooterHandle != 0 && victimHandle != 0)
        {
            PublishWhizzedBy(pending, shooterHandle, victimHandle);
            return;
        }

        if (shooterHandle == 0)
        {
            _identity.RequestHandle(unchecked((nint)(long)shooterAddress));
        }
        if (victimHandle == 0)
        {
            _identity.RequestHandle(unchecked((nint)(long)victimAddress));
        }

        _pendingWhizzedByEvents.Enqueue(pending);
    }

    private void DrainPendingWhizzedByEvents()
    {
        if (_pendingWhizzedByEvents.Count == 0)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        int count = _pendingWhizzedByEvents.Count;
        for (int index = 0; index < count; ++index)
        {
            PendingWhizzedByEvent pending =
                _pendingWhizzedByEvents.Dequeue();

            int shooterHandle = pending.ShooterHandle;
            int victimHandle = pending.VictimHandle;

            if (shooterHandle == 0)
            {
                shooterHandle =
                    ResolveCachedOrRequest(pending.ShooterAddress);
            }
            if (victimHandle == 0)
            {
                victimHandle =
                    ResolveCachedOrRequest(pending.VictimAddress);
            }

            if (shooterHandle != 0 && victimHandle != 0)
            {
                PublishWhizzedBy(
                    pending,
                    shooterHandle,
                    victimHandle);
                continue;
            }

            if (now < pending.ExpiresAt)
            {
                _pendingWhizzedByEvents.Enqueue(
                    pending with
                    {
                        ShooterHandle = shooterHandle,
                        VictimHandle = victimHandle
                    });
            }
        }
    }

    private void PublishWhizzedBy(
        PendingWhizzedByEvent pending,
        int shooterHandle,
        int victimHandle)
    {
        GunShotWhizzedByEvent whizzedBy = new(
            pending.Sequence,
            pending.PerformanceCounter,
            pending.ShooterAddress,
            shooterHandle,
            pending.VictimAddress,
            victimHandle);

        _gunShotWhizzedByDroppedCount +=
            PublishGunShotWhizzedBy(whizzedBy);
    }

    private void ProcessWeaponEvent(RawLowLevelEvent lowLevelEvent)
    {
        PendingWeaponKind kind;
        if (lowLevelEvent.EventId == EventCatalog.GunShot)
        {
            kind = PendingWeaponKind.GunShot;
            CountGunShotStream(lowLevelEvent.Stream);
        }
        else if (lowLevelEvent.EventId == EventCatalog.GunShotBulletImpact)
        {
            kind = PendingWeaponKind.BulletImpact;
            CountBulletImpactStream(lowLevelEvent.Stream);
        }
        else
        {
            return;
        }

        if (lowLevelEvent.Stream is not LowLevelEventStream.Group)
        {
            return;
        }

        if (!TryDecodeWeaponFacts(lowLevelEvent, kind, out PendingWeaponEvent pending))
        {
            ++_weaponMalformedCount;
            MarkContinuityLoss();
            return;
        }

        if (pending.ShooterAddress == 0)
        {
            ++_weaponMissingShooterCount;
            MarkContinuityLoss();
            return;
        }

        if (lowLevelEvent.RelatedEntityHandle != 0)
        {
            PublishPendingWeapon(pending, lowLevelEvent.RelatedEntityHandle);
            return;
        }

        _identity.RequestHandle(unchecked((nint)(long)pending.ShooterAddress));
        _pendingWeaponEvents.Enqueue(pending);
        ++_weaponIdentityQueuedCount;
    }

    private void DrainPendingWeaponEvents()
    {
        if (_pendingWeaponEvents.Count == 0)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        int count = _pendingWeaponEvents.Count;
        for (int index = 0; index < count; ++index)
        {
            PendingWeaponEvent pending = _pendingWeaponEvents.Dequeue();
            nint address = unchecked((nint)(long)pending.ShooterAddress);
            LocalDataResult<int> result = _identity.TryGetHandle(address);
            if (result.IsSuccess && result.Value != 0)
            {
                ++_weaponIdentityResolvedCount;
                PublishPendingWeapon(pending, result.Value);
                continue;
            }

            if (now >= pending.ExpiresAt)
            {
                ++_weaponIdentityExpiredCount;
                MarkContinuityLoss();
                continue;
            }

            _identity.RequestHandle(address);
            _pendingWeaponEvents.Enqueue(pending);
        }
    }

    private void PublishPendingWeapon(PendingWeaponEvent pending, int shooterHandle)
    {
        if (pending.Kind is PendingWeaponKind.GunShot)
        {
            GunShotEvent gunShot = new(
                pending.Sequence,
                pending.PerformanceCounter,
                pending.ShooterAddress,
                shooterHandle,
                pending.WeaponHash,
                pending.SourcePosition);
            ++_gunShotPublishedCount;
            _gunShotDroppedCount += PublishGunShot(gunShot);
            return;
        }

        BulletImpactEvent impact = new(
            pending.Sequence,
            pending.PerformanceCounter,
            pending.ShooterAddress,
            shooterHandle,
            pending.WeaponHash,
            pending.SourcePosition,
            pending.ImpactPosition);
        ++_bulletImpactPublishedCount;
        _bulletImpactDroppedCount += PublishBulletImpact(impact);
    }

    private static bool TryDecodeWeaponFacts(
        RawLowLevelEvent lowLevelEvent,
        PendingWeaponKind kind,
        out PendingWeaponEvent pending)
    {
        pending = default;
        ReadOnlySpan<byte> probe = lowLevelEvent.ProbeData.Span;
        if (!TryReadVector3(probe, SourcePositionOffset, out Vector3 sourcePosition) ||
            !TryReadUInt32(probe, WeaponHashOffset, out uint weaponHash) ||
            weaponHash == 0)
        {
            return false;
        }

        Vector3 impactPosition = default;
        if (kind is PendingWeaponKind.BulletImpact &&
            !TryReadVector3(probe, ImpactPositionOffset, out impactPosition))
        {
            return false;
        }

        long now = Stopwatch.GetTimestamp();
        pending = new(
            kind,
            lowLevelEvent.Sequence,
            lowLevelEvent.PerformanceCounter,
            lowLevelEvent.RelatedEntityAddress,
            weaponHash,
            sourcePosition,
            impactPosition,
            checked(now + PendingIdentityLifetimeTicks));
        return true;
    }

    private static bool TryReadVector3(
        ReadOnlySpan<byte> probe,
        int offset,
        out Vector3 value)
    {
        value = default;
        if (offset < 0 || offset + sizeof(float) * 3 > probe.Length)
        {
            return false;
        }

        float x = BitConverter.Int32BitsToSingle(unchecked((int)
            BitConverter.ToUInt32(probe.Slice(offset, sizeof(uint)))));
        float y = BitConverter.Int32BitsToSingle(unchecked((int)
            BitConverter.ToUInt32(probe.Slice(
                offset + sizeof(float),
                sizeof(uint)))));
        float z = BitConverter.Int32BitsToSingle(unchecked((int)
            BitConverter.ToUInt32(probe.Slice(
                offset + sizeof(float) * 2,
                sizeof(uint)))));

        if (!float.IsFinite(x) ||
            !float.IsFinite(y) ||
            !float.IsFinite(z) ||
            MathF.Abs(x) > 20_000f ||
            MathF.Abs(y) > 20_000f ||
            MathF.Abs(z) > 20_000f)
        {
            return false;
        }

        value = new(x, y, z);
        return true;
    }

    private static bool TryReadUInt32(
        ReadOnlySpan<byte> probe,
        int offset,
        out uint value)
    {
        value = 0;
        if (offset < 0 || offset + sizeof(uint) > probe.Length)
        {
            return false;
        }

        value = BitConverter.ToUInt32(probe.Slice(offset, sizeof(uint)));
        return true;
    }

    private void CountGunShotStream(LowLevelEventStream stream)
    {
        switch (stream)
        {
        case LowLevelEventStream.Reaction:
            ++_gunShotReactionCount;
            break;
        case LowLevelEventStream.Group:
            ++_gunShotGroupCount;
            break;
        case LowLevelEventStream.Global:
            ++_gunShotGlobalCount;
            break;
        }
    }

    private void CountBulletImpactStream(LowLevelEventStream stream)
    {
        switch (stream)
        {
        case LowLevelEventStream.Reaction:
            ++_bulletImpactReactionCount;
            break;
        case LowLevelEventStream.Group:
            ++_bulletImpactGroupCount;
            break;
        case LowLevelEventStream.Global:
            ++_bulletImpactGlobalCount;
            break;
        }
    }


    private ulong PublishRaw(RawLowLevelEvent lowLevelEvent)
    {
        KeyValuePair<Subscription<RawLowLevelEvent>, HashSet<uint>>[] rawSubscriptions;
        KeyValuePair<Subscription<RawLowLevelEvent>, HashSet<uint>>[] catalogSubscriptions;
        lock (_subscriptionGate)
        {
            rawSubscriptions = [.. _rawSubscriptions];
            catalogSubscriptions = [.. _catalogSubscriptions];
        }

        ulong dropped = 0;
        foreach (var pair in rawSubscriptions)
        {
            if (pair.Value.Contains(lowLevelEvent.EventId) &&
                !pair.Key.Publish(lowLevelEvent))
            {
                ++dropped;
            }
        }

        if (lowLevelEvent.CatalogEventId != 0)
        {
            foreach (var pair in catalogSubscriptions)
            {
                if (pair.Value.Contains(lowLevelEvent.CatalogEventId) &&
                    !pair.Key.Publish(lowLevelEvent))
                {
                    ++dropped;
                }
            }
        }
        return dropped;
    }

    private ulong PublishDamage(EntityDamageEvent damageEvent)
    {
        Subscription<EntityDamageEvent>[] subscriptions;
        lock (_subscriptionGate)
        {
            subscriptions = [.. _damageSubscriptions];
        }

        ulong dropped = 0;
        foreach (Subscription<EntityDamageEvent> subscription in subscriptions)
        {
            if (!subscription.Publish(damageEvent))
            {
                ++dropped;
            }
        }
        return dropped;
    }

    private ulong PublishMeleeAction(MeleeActionEvent meleeAction)
    {
        Subscription<MeleeActionEvent>[] subscriptions;
        lock (_subscriptionGate)
        {
            subscriptions = [.. _meleeActionSubscriptions];
        }

        ulong dropped = 0;
        foreach (Subscription<MeleeActionEvent> subscription in subscriptions)
        {
            if (!subscription.Publish(meleeAction))
            {
                ++dropped;
            }
        }
        return dropped;
    }

    private ulong PublishGunAimedAt(GunAimedAtEvent aimedAt)
    {
        Subscription<GunAimedAtEvent>[] subscriptions;
        ListenerSubscription<GunAimedAtEvent>[] listeners;
        lock (_subscriptionGate)
        {
            subscriptions = [.. _gunAimedAtSubscriptions];
            listeners = [.. _gunAimedAtListeners];
        }

        ulong dropped = 0;
        foreach (Subscription<GunAimedAtEvent> subscription in subscriptions)
        {
            if (!subscription.Publish(aimedAt))
            {
                ++dropped;
            }
        }
        foreach (ListenerSubscription<GunAimedAtEvent> listener in listeners)
        {
            if (!listener.Publish(aimedAt))
            {
                ++dropped;
            }
        }
        return dropped;
    }

    private ulong PublishGunShot(GunShotEvent gunShot)
    {
        Subscription<GunShotEvent>[] subscriptions;
        lock (_subscriptionGate)
        {
            subscriptions = [.. _gunShotSubscriptions];
        }

        ulong dropped = 0;
        foreach (Subscription<GunShotEvent> subscription in subscriptions)
        {
            if (!subscription.Publish(gunShot))
            {
                ++dropped;
            }
        }
        return dropped;
    }

    private ulong PublishGunShotWhizzedBy(
        GunShotWhizzedByEvent whizzedBy)
    {
        Subscription<GunShotWhizzedByEvent>[] subscriptions;
        lock (_subscriptionGate)
        {
            subscriptions = [.. _gunShotWhizzedBySubscriptions];
        }

        ulong dropped = 0;
        foreach (
            Subscription<GunShotWhizzedByEvent> subscription
            in subscriptions)
        {
            if (!subscription.Publish(whizzedBy))
            {
                ++dropped;
            }
        }
        return dropped;
    }

    private ulong PublishBulletImpact(BulletImpactEvent impact)
    {
        Subscription<BulletImpactEvent>[] subscriptions;
        lock (_subscriptionGate)
        {
            subscriptions = [.. _bulletImpactSubscriptions];
        }

        ulong dropped = 0;
        foreach (Subscription<BulletImpactEvent> subscription in subscriptions)
        {
            if (!subscription.Publish(impact))
            {
                ++dropped;
            }
        }
        return dropped;
    }

    private void RemoveRawSubscription(Subscription<RawLowLevelEvent> subscription)
    {
        lock (_subscriptionGate)
        {
            _rawSubscriptions.Remove(subscription);
            RefreshNativeCapturePolicyLocked();
        }
    }

    private void RemoveCatalogSubscription(
        Subscription<RawLowLevelEvent> subscription)
    {
        lock (_subscriptionGate)
        {
            _catalogSubscriptions.Remove(subscription);
            RefreshNativeCapturePolicyLocked();
        }
    }


    private void RemoveDamageSubscription(
        Subscription<EntityDamageEvent> subscription)
    {
        lock (_subscriptionGate)
        {
            _damageSubscriptions.Remove(subscription);
        }
    }

    private void RemoveMeleeActionSubscription(
        Subscription<MeleeActionEvent> subscription)
    {
        lock (_subscriptionGate)
        {
            _meleeActionSubscriptions.Remove(subscription);
            RefreshNativeCapturePolicyLocked();
        }
    }

    private void RemoveGunAimedAtSubscription(
        Subscription<GunAimedAtEvent> subscription)
    {
        lock (_subscriptionGate)
        {
            _gunAimedAtSubscriptions.Remove(subscription);
            RefreshNativeCapturePolicyLocked();
        }
    }

    private void RemoveGunAimedAtListener(
        ListenerSubscription<GunAimedAtEvent> subscription)
    {
        lock (_subscriptionGate)
        {
            _gunAimedAtListeners.Remove(subscription);
            RefreshNativeCapturePolicyLocked();
        }
    }

    private void RemoveGunShotSubscription(Subscription<GunShotEvent> subscription)
    {
        lock (_subscriptionGate)
        {
            _gunShotSubscriptions.Remove(subscription);
            RefreshNativeCapturePolicyLocked();
        }
    }

    private void RemoveGunShotWhizzedBySubscription(
        Subscription<GunShotWhizzedByEvent> subscription)
    {
        lock (_subscriptionGate)
        {
            _gunShotWhizzedBySubscriptions.Remove(subscription);
            RefreshNativeCapturePolicyLocked();
        }
    }

    private void RemoveBulletImpactSubscription(
        Subscription<BulletImpactEvent> subscription)
    {
        lock (_subscriptionGate)
        {
            _bulletImpactSubscriptions.Remove(subscription);
            RefreshNativeCapturePolicyLocked();
        }
    }

    private static void ValidateCapacity(int capacity)
    {
        if (capacity <= 0 || capacity > MaximumSubscriptionCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
    }

    private readonly record struct PendingDamageEvent(
        ulong Sequence,
        long PerformanceCounter,
        ulong VictimAddress,
        int VictimHandle,
        ulong CulpritAddress,
        int CulpritHandle,
        uint WeaponHash,
        uint GameTime,
        bool State,
        float BaseDamage,
        DamageMagnitudeSource MagnitudeSource,
        long ExpiresAt);

    private readonly record struct PendingAimEvent(
        ulong Sequence,
        long PerformanceCounter,
        ulong ShooterAddress,
        int ShooterHandle,
        ulong VictimAddress,
        int VictimHandle);

    private readonly record struct PendingWhizzedByEvent(
        ulong Sequence,
        long PerformanceCounter,
        ulong ShooterAddress,
        int ShooterHandle,
        ulong VictimAddress,
        int VictimHandle,
        long ExpiresAt);

    private enum PendingWeaponKind
    {
        GunShot,
        BulletImpact
    }

    private readonly record struct PendingWeaponEvent(
        PendingWeaponKind Kind,
        ulong Sequence,
        long PerformanceCounter,
        ulong ShooterAddress,
        uint WeaponHash,
        Vector3 SourcePosition,
        Vector3 ImpactPosition,
        long ExpiresAt);

    private sealed class ListenerSubscription<T>(
        Action<T> listener,
        Action onDispose) : ILowLevelListenerSubscription
    {
        private readonly Lock _gate = new();
        private Action<T>? _listener = listener ??
            throw new ArgumentNullException(nameof(listener));
        private Action? _onDispose = onDispose ??
            throw new ArgumentNullException(nameof(onDispose));
        private ulong _faultCount;
        private bool _faulted;
        private bool _disposed;

        public ulong FaultCount
        {
            get
            {
                lock (_gate)
                {
                    return _faultCount;
                }
            }
        }

        internal bool Publish(T value)
        {
            Action<T>? callback;
            lock (_gate)
            {
                if (_disposed || _faulted)
                {
                    return true;
                }
                callback = _listener;
            }

            if (callback is null)
            {
                return true;
            }

            try
            {
                callback(value);
                return true;
            }
            catch
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        ++_faultCount;
                        _faulted = true;
                        _listener = null;
                    }
                }
                return false;
            }
        }

        internal void Detach()
        {
            lock (_gate)
            {
                _disposed = true;
                _listener = null;
                _onDispose = null;
            }
        }

        public void Dispose()
        {
            Action? remove;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _listener = null;
                remove = Interlocked.Exchange(ref _onDispose, null);
            }
            remove?.Invoke();
        }
    }

    private sealed class Subscription<T>(
        int capacity,
        Action onDispose) : ILowLevelSubscription<T>
    {
        private readonly Lock _gate = new();
        private readonly Queue<T> _queue = new(capacity);
        private Action? _onDispose = onDispose;
        private ulong _dropped;
        private bool _disposed;

        public ulong DroppedCount
        {
            get
            {
                lock (_gate)
                {
                    return _dropped;
                }
            }
        }

        public bool TryRead([MaybeNullWhen(false)] out T value)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _queue.TryDequeue(out value);
            }
        }

        internal bool Publish(T value)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return true;
                }

                if (_queue.Count >= capacity)
                {
                    ++_dropped;
                    return false;
                }

                _queue.Enqueue(value);
                return true;
            }
        }

        internal void Detach()
        {
            lock (_gate)
            {
                _disposed = true;
                _queue.Clear();
                _onDispose = null;
            }
        }

        public void Dispose()
        {
            Action? remove;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _queue.Clear();
                remove = Interlocked.Exchange(ref _onDispose, null);
            }
            remove?.Invoke();
        }
    }
}