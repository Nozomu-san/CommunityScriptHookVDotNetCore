namespace LowLevelEvents.Source;

internal static class EventCatalog
{
    internal const uint Damage = 17;
    internal const uint GunAimedAt = 29;
    internal const uint MeleeAction = 42;
    internal const uint ShockingGunshotFired = 91;
    internal const uint ShockingVisibleWeapon = 123;
    internal const uint GunShot = 124;
    internal const uint GunShotBulletImpact = 125;
    internal const uint GunShotWhizzedBy = 126;
    internal const uint EntityDamaged = 141;

    private static readonly uint[] DetailedIds =
    [
        Damage,
        GunAimedAt,
        MeleeAction,
        GunShot,
        GunShotBulletImpact,
        EntityDamaged
    ];

    internal static IEnumerable<uint> GetDetailedEventIds() => DetailedIds;

    internal static string? ResolveName(uint _, uint eventId)
    {
        if (eventId == NativeAbi.EntityDamageMetadataEventId)
        {
            return "EntityDamageMetadata";
        }

        return eventId switch
        {
            Damage => "CEventDamage",
            GunAimedAt => "CEventGunAimedAt",
            MeleeAction => "CEventMeleeAction",
            ShockingGunshotFired => "CEventShockingGunshotFired",
            ShockingVisibleWeapon => "CEventShockingVisibleWeapon",
            GunShot => "CEventGunShot",
            GunShotBulletImpact => "CEventGunShotBulletImpact",
            GunShotWhizzedBy => "CEventGunShotWhizzedBy",
            EntityDamaged => "CEventEntityDamaged",
            _ => null
        };
    }

    internal static bool RequiresEntityIdentity(uint eventId) =>
        eventId is Damage or
            GunAimedAt or
            MeleeAction or
            ShockingGunshotFired or
            ShockingVisibleWeapon or
            GunShot or
            GunShotBulletImpact or
            GunShotWhizzedBy or
            EntityDamaged or
            NativeAbi.EntityDamageMetadataEventId;
}