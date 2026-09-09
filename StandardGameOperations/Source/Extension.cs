using System.Reflection;
using Alloc8orStandardNatives.Source;
using CommunityScriptHookVDotNetCore.Source;
using LocalNativeMemories.Source;

[assembly: AssemblyMetadata("CSHVDNC.Role", "RuntimeExtension")]
[assembly: AssemblyMetadata("CSHVDNC.Id", "StandardGameOperations")]
[assembly: AssemblyMetadata(
    "CSHVDNC.EntryType",
    "StandardGameOperations.Source.StandardGameOperationsExtension")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Provides",
    "game.standard;game.entities;game.peds;game.vehicles;game.weapons;game.combat;game.ballistics;game.damage")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Requires",
    "native.standard;memory.entity.pools;memory.ped;memory.vehicle")]

namespace StandardGameOperations.Source;

public interface IStandardGameOperations
{
    IEntityOperations Entities { get; }
    IPedOperations Peds { get; }
    IVehicleOperations Vehicles { get; }
    IWeaponOperations Weapons { get; }
    ICombatOperations Combat { get; }
    IBallisticOperations Ballistics { get; }
    IDamageOperations Damage { get; }
}

internal sealed class StandardGameOperationsService(
    IEntityOperations entities,
    IPedOperations peds,
    IVehicleOperations vehicles,
    IWeaponOperations weapons,
    ICombatOperations combat,
    IBallisticOperations ballistics,
    IDamageOperations damage) : IStandardGameOperations
{
    public IEntityOperations Entities { get; } = entities;
    public IPedOperations Peds { get; } = peds;
    public IVehicleOperations Vehicles { get; } = vehicles;
    public IWeaponOperations Weapons { get; } = weapons;
    public ICombatOperations Combat { get; } = combat;
    public IBallisticOperations Ballistics { get; } = ballistics;
    public IDamageOperations Damage { get; } = damage;
}

internal sealed class StandardGameOperationsExtension :
    IScript4RuntimeExtension
{
    private StandardGameOperationsService? _service;
    private PedOperations? _peds;

    public Task InitializeAsync(
        RuntimeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_service is not null || _peds is not null)
        {
            throw new InvalidOperationException(
                "StandardGameOperations is already initialized.");
        }

        IStandardNatives natives =
            context.Services.GetRequired<IStandardNatives>();
        IKnownNativeInvoker known =
            context.Services.GetRequired<IKnownNativeInvoker>();
        ILocalEntityPools pools =
            context.Services.GetRequired<ILocalEntityPools>();
        IPedLocalMemory pedMemory =
            context.Services.GetRequired<IPedLocalMemory>();
        IVehicleLocalMemory vehicleMemory =
            context.Services.GetRequired<IVehicleLocalMemory>();

        EntityOperations entities = new(pools);
        NativeBindings nativeBindings = new(natives, known);
        PedOperations peds = new(
            entities,
            pools,
            pedMemory,
            nativeBindings);
        VehicleOperations vehicles = new(entities, vehicleMemory);
        WeaponOperations weapons = new(entities, nativeBindings);
        CombatOperations combat = new(entities, peds, nativeBindings);
        BallisticOperations ballistics = new(entities, peds);
        DamageOperations damage = new(
            entities,
            peds,
            vehicles,
            nativeBindings);

        StandardGameOperationsService service = new(
            entities,
            peds,
            vehicles,
            weapons,
            combat,
            ballistics,
            damage);

        context.Services.Register<IEntityOperations>(entities);
        context.Services.Register<IPedOperations>(peds);
        context.Services.Register<IVehicleOperations>(vehicles);
        context.Services.Register<IWeaponOperations>(weapons);
        context.Services.Register<ICombatOperations>(combat);
        context.Services.Register<IBallisticOperations>(ballistics);
        context.Services.Register<IDamageOperations>(damage);
        context.Services.Register<IStandardGameOperations>(service);

        _peds = peds;
        _service = service;
        return Task.CompletedTask;
    }

    public void AdvanceHostFrame(RuntimeExtensionFrameContext context)
    {
        _peds?.AdvanceObservationFrame(context);
    }

    public Task ShutdownAsync()
    {
        _peds?.ClearObservations();
        _peds = null;
        _service = null;
        return Task.CompletedTask;
    }
}