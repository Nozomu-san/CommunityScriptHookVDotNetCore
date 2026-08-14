using System.Reflection;
using Alloc8orStandardNatives.Source;
using CommunityScriptHookVDotNetCore.Source;
using LocalNativeMemories.Source;

[assembly: AssemblyMetadata("CSHVDNC.Role", "RuntimeExtension")]
[assembly: AssemblyMetadata("CSHVDNC.Id", "StandardGameOperations")]
[assembly: AssemblyMetadata(
    "CSHVDNC.EntryType",
    "StandardGameOperations.Source.StandardGameOperationsExtension")]
[assembly: AssemblyMetadata("CSHVDNC.ContractMajor", "1")]
[assembly: AssemblyMetadata("CSHVDNC.ContractMinor", "0")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Provides",
    "game.operations;game.environment;game.entities;game.peds;" +
    "game.weapons;game.combat;game.damage")]
[assembly: AssemblyMetadata(
    "CSHVDNC.Requires",
    "native.standard;memory.ped.invincibility")]

namespace StandardGameOperations.Source;

internal sealed class StandardGameOperationsExtension :
    IScript4RuntimeExtension
{
    private StandardGameOperationsService? _service;

    public ValueTask InitializeAsync(
        RuntimeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_service is not null)
        {
            throw new InvalidOperationException(
                "StandardGameOperations is already initialized.");
        }

        IStandardNatives natives =
            context.Services.GetRequired<IStandardNatives>();
        IPedInvincibilityMemory invincibility =
            context.Services.GetRequired<IPedInvincibilityMemory>();

        GameOperations game = new(natives.GameBuild);
        EntityOperations entities = new();
        NativeBindings nativeBindings = new(natives);
        PedOperations peds = new(
            entities,
            invincibility,
            nativeBindings);
        WeaponOperations weapons = new(entities, nativeBindings);
        CombatOperations combat = new(entities, peds, nativeBindings);
        DamageOperations damage = new(entities, peds, nativeBindings);

        StandardGameOperationsService service = new(
            game,
            entities,
            peds,
            weapons,
            combat,
            damage);

        context.Services.Register<IGameOperations>(game);
        context.Services.Register<IEntityOperations>(entities);
        context.Services.Register<IPedOperations>(peds);
        context.Services.Register<IWeaponOperations>(weapons);
        context.Services.Register<ICombatOperations>(combat);
        context.Services.Register<IDamageOperations>(damage);
        context.Services.Register<IStandardGameOperations>(service);

        _service = service;
        return ValueTask.CompletedTask;
    }

    public void AdvanceHostFrame(RuntimeExtensionFrameContext context)
    {
        _ = context;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _service = null;
        return ValueTask.CompletedTask;
    }
}