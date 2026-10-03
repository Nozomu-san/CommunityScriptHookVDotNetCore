using System.Numerics;
using Alloc8orStandardNatives.Source;
using CommunityScriptHookVDotNetCore.Source;
using LocalNativeMemories.Source;

namespace StandardGameOperations.Source;

public readonly record struct PedVitals(int Armour, int Health);

public readonly record struct PedPopulationStatistics(
    bool Available,
    int Capacity,
    int ActiveCount);

public readonly record struct PedPopulationSnapshot(
    bool Available,
    ulong Revision,
    int Capacity,
    int ActiveCount,
    ReadOnlyMemory<Ped> Peds)
{
    public int Count => Peds.Length;

    public static PedPopulationSnapshot Empty { get; } =
        new(false, 0, 0, 0, ReadOnlyMemory<Ped>.Empty);
}

[Flags]
public enum PedObservationFields
{
    None = 0,
    Life = 1 << 0,
    Actions = 1 << 1,
    Weapon = 1 << 2,
    AmmoInClip = 1 << 3,
    Standard = Life | Actions | Weapon,
    All = Standard | AmmoInClip
}

public enum PedObservationPriority
{
    Background = 0,
    Active = 1
}

public readonly record struct PedObservationSnapshot(
    ulong RequestRevision,
    ulong HostFrameIndex,
    long PerformanceCounter,
    ulong PerformanceFrequency,
    Ped Ped,
    PedObservationFields CapturedFields,
    bool IsValid,
    bool IsHuman,
    bool IsAlive,
    bool IsShooting,
    bool IsPerformingMeleeAction,
    bool IsReloading,
    uint SelectedWeapon,
    uint AmmoType,
    bool AmmoInClipAvailable,
    int AmmoInClip)
{
    public bool Has(PedObservationFields fields) =>
        (CapturedFields & fields) == fields;
}

public readonly record struct PedInvincibilityState(
    bool WasRead,
    bool PureInvincibility,
    bool ReactionInvincibility,
    bool CanBeDamaged)
{
    public bool IsInvincible =>
        PureInvincibility || ReactionInvincibility;

    public bool IsPureInvincible =>
        WasRead && PureInvincibility;

    public bool AllowsRagdollReaction =>
        WasRead &&
        !PureInvincibility &&
        ReactionInvincibility;

    public bool BlocksSyntheticDamage =>
        !WasRead ||
        !CanBeDamaged ||
        PureInvincibility ||
        ReactionInvincibility;
}

public interface IPedOperations
{
    Ped GetPlayerPed();
    PedPopulationStatistics GetPopulationStatistics();
    PedPopulationSnapshot CapturePopulation();
    ulong RequestObservation(
        Ped ped,
        PedObservationFields fields = PedObservationFields.Standard,
        PedObservationPriority priority = PedObservationPriority.Background);
    bool TryGetObservation(
        Ped ped,
        ulong minimumRequestRevision,
        out PedObservationSnapshot snapshot);
    void ForgetObservation(Ped ped);
    ReadOnlyMemory<Ped> GetAllPeds();
    Ped[] GetNearbyPeds(Ped origin, int maxAmount = 16);
    bool IsHuman(Ped ped);
    bool IsAlive(Ped ped);
    bool IsLivingHuman(Ped ped);
    bool IsDead(Ped ped);
    bool IsShooting(Ped ped);
    bool IsTaskActive(Ped ped, int taskType);
    bool IsPerformingMeleeAction(Ped ped);
    bool IsReloading(Ped ped);
    bool IsRagdoll(Ped ped);
    bool IsGettingUp(Ped ped);
    bool IsInCover(Ped ped);
    int GetAccuracy(Ped ped);
    bool TrySetAccuracy(Ped ped, int accuracy);
    PedVitals ReadVitals(Ped ped);
    Vector3 GetChestPosition(Ped ped);
    bool TryConsumeLastDamageBone(Ped ped, out ushort boneTag);
    Vehicle GetCurrentVehicle(Ped ped);
    bool CanRagdoll(Ped ped);
    bool TryRagdoll(Ped ped, int minimumTime = 450, int maximumTime = 900);
    PedInvincibilityState ReadInvincibility(Ped ped);
    void SetPureInvincibility(Ped ped, bool value);
    bool CanReceiveSyntheticDamage(Ped ped);
    bool IsOnFire(Ped ped);
    bool StartFire(Ped ped);
    void StopFire(Ped ped);
    bool RequestLeaveVehicle(Ped ped);
}

internal sealed class PedOperations(
    IEntityOperations entities,
    ILocalEntityPools pools,
    IPedLocalMemory memory,
    NativeBindings known) : IPedOperations
{
    private readonly IEntityOperations _entities =
        entities ?? throw new ArgumentNullException(nameof(entities));
    private readonly ILocalEntityPools _pools =
        pools ?? throw new ArgumentNullException(nameof(pools));
    private readonly IPedLocalMemory _memory =
        memory ?? throw new ArgumentNullException(nameof(memory));
    private readonly NativeBindings _known =
        known ?? throw new ArgumentNullException(nameof(known));
    private readonly Lock _populationGate = new();
    private readonly Lock _observationGate = new();
    private readonly Queue<int> _activeObservationQueue = new();
    private readonly Queue<int> _backgroundObservationQueue = new();
    private readonly Dictionary<int, PendingPedObservation> _pendingObservations = [];
    private readonly Dictionary<int, PedObservationSnapshot> _latestObservations = [];
    private PedPopulationSnapshot _population = PedPopulationSnapshot.Empty;
    private ulong _nextObservationRequestRevision;

    public Ped GetPlayerPed() =>
        StandardNatives.PLAYER_PED_ID().FromNative();

    public PedPopulationStatistics GetPopulationStatistics()
    {
        LocalDataResult<LocalPoolStatistics> result =
            _pools.GetStatistics(LocalPoolKinds.Peds);

        return result.IsSuccess
            ? new(true, result.Value.Capacity, result.Value.ActiveCount)
            : default;
    }

    public PedPopulationSnapshot CapturePopulation()
    {
        LocalDataResult<EntityPoolsSnapshot> snapshot =
            _pools.Capture(LocalPoolKinds.Peds);

        if (!snapshot.IsSuccess)
        {
            return PedPopulationSnapshot.Empty;
        }

        LocalPoolSnapshot pool = snapshot.Value.Peds;

        lock (_populationGate)
        {
            if (_population.Available &&
                _population.Revision == pool.Revision)
            {
                return _population;
            }

            Ped[] peds = new Ped[pool.Handles.Length];
            int count = 0;
            foreach (int handle in pool.Handles.Span)
            {
                if (handle != 0)
                {
                    peds[count++] = new(handle);
                }
            }

            if (count != peds.Length)
            {
                Array.Resize(ref peds, count);
            }

            _population = new(
                true,
                pool.Revision,
                pool.Capacity,
                pool.ActiveCount,
                peds);
            return _population;
        }
    }

    public ulong RequestObservation(
        Ped ped,
        PedObservationFields fields = PedObservationFields.Standard,
        PedObservationPriority priority = PedObservationPriority.Background)
    {
        fields &= PedObservationFields.All;
        if (ped.Value == 0 || fields == PedObservationFields.None)
        {
            return 0;
        }

        priority = priority is PedObservationPriority.Active
            ? PedObservationPriority.Active
            : PedObservationPriority.Background;

        lock (_observationGate)
        {
            ulong revision = NextObservationRequestRevision();
            if (_pendingObservations.TryGetValue(
                    ped.Value,
                    out PendingPedObservation? pending))
            {
                pending.Fields |= fields;
                pending.LatestRequestRevision = revision;
                if ((int)priority > (int)pending.Priority)
                {
                    pending.Priority = priority;
                    _activeObservationQueue.Enqueue(ped.Value);
                }
                return revision;
            }

            pending = new(ped, fields, priority, revision);
            _pendingObservations.Add(ped.Value, pending);
            QueueFor(priority).Enqueue(ped.Value);
            return revision;
        }
    }

    public bool TryGetObservation(
        Ped ped,
        ulong minimumRequestRevision,
        out PedObservationSnapshot snapshot)
    {
        lock (_observationGate)
        {
            if (_latestObservations.TryGetValue(ped.Value, out snapshot) &&
                snapshot.RequestRevision >= minimumRequestRevision)
            {
                return true;
            }
        }

        snapshot = default;
        return false;
    }

    public void ForgetObservation(Ped ped)
    {
        if (ped.Value == 0)
        {
            return;
        }

        lock (_observationGate)
        {
            _pendingObservations.Remove(ped.Value);
            _latestObservations.Remove(ped.Value);
        }
    }

    internal void AdvanceObservationFrame(RuntimeExtensionFrameContext context)
    {
        lock (_observationGate)
        {
            bool active = ProcessObservationQueue(
                _activeObservationQueue,
                PedObservationPriority.Active,
                context);
            bool background = ProcessObservationQueue(
                _backgroundObservationQueue,
                PedObservationPriority.Background,
                context);

            if (active && !background)
            {
                ProcessObservationQueue(
                    _activeObservationQueue,
                    PedObservationPriority.Active,
                    context);
            }
            else if (!active && background)
            {
                ProcessObservationQueue(
                    _backgroundObservationQueue,
                    PedObservationPriority.Background,
                    context);
            }
        }
    }

    internal void ClearObservations()
    {
        lock (_observationGate)
        {
            _activeObservationQueue.Clear();
            _backgroundObservationQueue.Clear();
            _pendingObservations.Clear();
            _latestObservations.Clear();
        }
    }

    private bool ProcessObservationQueue(
        Queue<int> queue,
        PedObservationPriority expectedPriority,
        RuntimeExtensionFrameContext context)
    {
        while (queue.TryDequeue(out int handle))
        {
            if (!_pendingObservations.TryGetValue(
                    handle,
                    out PendingPedObservation? pending) ||
                pending.Priority != expectedPriority)
            {
                continue;
            }

            _pendingObservations.Remove(handle);
            PedObservationEntry entry = new(pending.Ped);
            entry.Capture(pending.Fields, _known);
            _latestObservations[handle] = entry.ToSnapshot(
                pending.LatestRequestRevision,
                context);
            return true;
        }

        return false;
    }

    private Queue<int> QueueFor(PedObservationPriority priority) =>
        priority is PedObservationPriority.Active
            ? _activeObservationQueue
            : _backgroundObservationQueue;

    private ulong NextObservationRequestRevision()
    {
        _nextObservationRequestRevision++;
        if (_nextObservationRequestRevision == 0)
        {
            _nextObservationRequestRevision++;
        }
        return _nextObservationRequestRevision;
    }

    public ReadOnlyMemory<Ped> GetAllPeds() =>
        CapturePopulation().Peds;

    public Ped[] GetNearbyPeds(Ped origin, int maxAmount = 16) =>
        !_entities.IsValid(origin)
            ? []
            : _known.GetNearbyPeds(origin, maxAmount);

    public bool IsHuman(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_HUMAN(ped.ToNative());

    public bool IsAlive(Ped ped) =>
        _entities.IsValid(ped) &&
        !StandardNatives.IS_ENTITY_DEAD(ped.ToNativeEntity(), false);

    public bool IsLivingHuman(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_HUMAN(ped.ToNative()) &&
        !StandardNatives.IS_ENTITY_DEAD(ped.ToNativeEntity(), false);

    public bool IsDead(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_ENTITY_DEAD(ped.ToNativeEntity(), false);

    public bool IsShooting(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_SHOOTING(ped.ToNative());

    public bool IsTaskActive(Ped ped, int taskType) =>
        _entities.IsValid(ped) &&
        taskType >= 0 &&
        StandardNatives.GET_IS_TASK_ACTIVE(
            ped.ToNative(),
            taskType);

    public bool IsPerformingMeleeAction(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_PERFORMING_MELEE_ACTION(ped.ToNative());

    public bool IsReloading(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_RELOADING(ped.ToNative());

    public bool IsRagdoll(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_RAGDOLL(ped.ToNative());

    public bool IsGettingUp(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_PED_GETTING_UP(ped.ToNative());

    public bool IsInCover(Ped ped) =>
        IsLivingHuman(ped) &&
        StandardNatives.IS_PED_IN_COVER(ped.ToNative(), false);

    public int GetAccuracy(Ped ped) =>
        !_entities.IsValid(ped)
            ? 0
            : Math.Clamp(StandardNatives.GET_PED_ACCURACY(ped.ToNative()), 0, 100);

    public bool TrySetAccuracy(Ped ped, int accuracy)
    {
        if (!_entities.IsValid(ped) || accuracy < 0 || accuracy > 100)
        {
            return false;
        }

        StandardNatives.SET_PED_ACCURACY(ped.ToNative(), accuracy);
        return true;
    }

    public PedVitals ReadVitals(Ped ped) =>
        !_entities.IsValid(ped)
            ? default
            : new(
                Math.Max(0, StandardNatives.GET_PED_ARMOUR(ped.ToNative())),
                Math.Max(
                    0,
                    StandardNatives.GET_ENTITY_HEALTH(
                        ped.ToNativeEntity())));

    public Vector3 GetChestPosition(Ped ped) =>
        !_entities.IsValid(ped)
            ? default
            : StandardNatives.GET_PED_BONE_COORDS(
                ped.ToNative(),
                24818,
                0f,
                0f,
                0f);

    public bool TryConsumeLastDamageBone(Ped ped, out ushort boneTag)
    {
        boneTag = 0;
        if (!_entities.IsValid(ped))
        {
            return false;
        }

        bool found = _known.TryGetLastDamageBone(ped, out int bone);
        StandardNatives.CLEAR_PED_LAST_DAMAGE_BONE(ped.ToNative());

        if (!found)
        {
            return false;
        }

        boneTag = (ushort)bone;
        return true;
    }

    public Vehicle GetCurrentVehicle(Ped ped) =>
        !_entities.IsValid(ped)
            ? default
            : StandardNatives.GET_VEHICLE_PED_IS_IN(
                ped.ToNative(),
                false).FromNative();

    public bool RequestLeaveVehicle(Ped ped)
    {
        if (!IsLivingHuman(ped))
        {
            return false;
        }

        Vehicle vehicle = GetCurrentVehicle(ped);
        if (!_entities.IsValid(vehicle))
        {
            return false;
        }

        StandardNatives.TASK_LEAVE_VEHICLE(
            ped.ToNative(),
            vehicle.ToNative(),
            0);
        return true;
    }

    public bool CanRagdoll(Ped ped)
    {
        if (!IsLivingHuman(ped))
        {
            return false;
        }

        PedInvincibilityState invincibility = ReadInvincibility(ped);
        if (invincibility.IsPureInvincible)
        {
            return false;
        }

        return invincibility.AllowsRagdollReaction ||
            StandardNatives.CAN_PED_RAGDOLL(ped.ToNative());
    }

    public bool TryRagdoll(
        Ped ped,
        int minimumTime = 450,
        int maximumTime = 900)
    {
        if (!CanRagdoll(ped))
        {
            return false;
        }

        int minimum = Math.Max(0, minimumTime);
        int maximum = Math.Max(minimum, maximumTime);

        return StandardNatives.SET_PED_TO_RAGDOLL(
            ped.ToNative(),
            minimum,
            maximum,
            0,
            false,
            false,
            false);
    }

    public PedInvincibilityState ReadInvincibility(Ped ped)
    {
        if (!_entities.IsValid(ped))
        {
            return default;
        }

        bool canBeDamaged =
            StandardNatives.GET_ENTITY_CAN_BE_DAMAGED(
                ped.ToNativeEntity());

        LocalDataResult<PedLocalInvincibilityState> local =
            _memory.ReadInvincibility(ped.Value);

        return local.IsSuccess
            ? new(
                true,
                local.Value.PureInvincibility,
                local.Value.ReactionInvincibility,
                canBeDamaged)
            : new(false, false, false, canBeDamaged);
    }

    public void SetPureInvincibility(Ped ped, bool value)
    {
        if (_entities.IsValid(ped))
        {
            StandardNatives.SET_ENTITY_INVINCIBLE(
                ped.ToNativeEntity(),
                value,
                false);
        }
    }

    public bool CanReceiveSyntheticDamage(Ped ped) =>
        !ReadInvincibility(ped).BlocksSyntheticDamage;

    public bool IsOnFire(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.IS_ENTITY_ON_FIRE(ped.ToNativeEntity());

    public bool StartFire(Ped ped) =>
        _entities.IsValid(ped) &&
        StandardNatives.START_ENTITY_FIRE(ped.ToNativeEntity()).Value >= 0;

    public void StopFire(Ped ped)
    {
        if (_entities.IsValid(ped))
        {
            StandardNatives.STOP_ENTITY_FIRE(ped.ToNativeEntity());
        }
    }

    private sealed class PendingPedObservation(
        Ped ped,
        PedObservationFields fields,
        PedObservationPriority priority,
        ulong latestRequestRevision)
    {
        internal Ped Ped { get; } = ped;
        internal PedObservationFields Fields { get; set; } = fields;
        internal PedObservationPriority Priority { get; set; } = priority;
        internal ulong LatestRequestRevision { get; set; } = latestRequestRevision;
    }

    private sealed class PedObservationEntry(Ped ped)
    {
        private bool _isValid;
        private PedObservationFields _capturedFields;
        private bool _isHuman;
        private bool _isAlive;
        private bool _isShooting;
        private bool _isPerformingMeleeAction;
        private bool _isReloading;
        private uint _selectedWeapon;
        private uint _ammoType;
        private bool _ammoInClipAvailable;
        private int _ammoInClip;

        internal void Capture(
            PedObservationFields fields,
            NativeBindings known)
        {
            if (ped.Value != 0)
            {
                var entity = ped.ToNativeEntity();
                _isValid =
                    StandardNatives.DOES_ENTITY_EXIST(entity) &&
                    StandardNatives.IS_ENTITY_A_PED(entity);
            }

            if (!_isValid)
            {
                _capturedFields = fields;
                return;
            }

            if ((fields & PedObservationFields.Life) != 0)
            {
                _isHuman = StandardNatives.IS_PED_HUMAN(ped.ToNative());
                _isAlive = !StandardNatives.IS_ENTITY_DEAD(
                    ped.ToNativeEntity(),
                    false);
                _capturedFields |= PedObservationFields.Life;
            }

            if ((fields & PedObservationFields.Actions) != 0)
            {
                _isShooting = StandardNatives.IS_PED_SHOOTING(ped.ToNative());
                _isPerformingMeleeAction =
                    StandardNatives.IS_PED_PERFORMING_MELEE_ACTION(
                        ped.ToNative());
                _isReloading = StandardNatives.IS_PED_RELOADING(
                    ped.ToNative());
                _capturedFields |= PedObservationFields.Actions;
            }

            if ((fields & PedObservationFields.Weapon) != 0 ||
                (fields & PedObservationFields.AmmoInClip) != 0)
            {
                _selectedWeapon =
                    StandardNatives.GET_SELECTED_PED_WEAPON(ped.ToNative());
                _ammoType = _selectedWeapon == 0
                    ? 0
                    : StandardNatives.GET_PED_AMMO_TYPE_FROM_WEAPON(
                        ped.ToNative(),
                        _selectedWeapon);
                _capturedFields |= PedObservationFields.Weapon;
            }

            if ((fields & PedObservationFields.AmmoInClip) != 0)
            {
                _ammoInClipAvailable =
                    _selectedWeapon != 0 &&
                    known.TryGetAmmoInClip(
                        ped,
                        _selectedWeapon,
                        out _ammoInClip);
                if (!_ammoInClipAvailable)
                {
                    _ammoInClip = 0;
                }
                _capturedFields |= PedObservationFields.AmmoInClip;
            }
        }

        internal PedObservationSnapshot ToSnapshot(
            ulong requestRevision,
            RuntimeExtensionFrameContext context) =>
            new(
                requestRevision,
                context.HostFrameIndex,
                context.PerformanceCounter,
                context.PerformanceFrequency,
                ped,
                _capturedFields,
                _isValid,
                _isHuman,
                _isAlive,
                _isShooting,
                _isPerformingMeleeAction,
                _isReloading,
                _selectedWeapon,
                _ammoType,
                _ammoInClipAvailable,
                _ammoInClip);
    }
}