namespace StandardGameOperations.Source;

public interface IStandardGameOperations
{
    IGameOperations Game { get; }
    IEntityOperations Entities { get; }
    IPedOperations Peds { get; }
    IWeaponOperations Weapons { get; }
    ICombatOperations Combat { get; }
    IDamageOperations Damage { get; }
}

internal sealed class StandardGameOperationsService(
    IGameOperations game,
    IEntityOperations entities,
    IPedOperations peds,
    IWeaponOperations weapons,
    ICombatOperations combat,
    IDamageOperations damage) : IStandardGameOperations
{
    public IGameOperations Game { get; } = game;
    public IEntityOperations Entities { get; } = entities;
    public IPedOperations Peds { get; } = peds;
    public IWeaponOperations Weapons { get; } = weapons;
    public ICombatOperations Combat { get; } = combat;
    public IDamageOperations Damage { get; } = damage;
}