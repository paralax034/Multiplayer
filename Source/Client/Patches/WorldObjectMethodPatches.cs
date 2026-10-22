using HarmonyLib;
using RimWorld;
using RimWorld.Planet;

namespace Multiplayer.Client.Patches;

/// <summary>
/// Scopes world object ticking and operations to their owning player faction in multifaction mode.
/// Skips redundant context changes when the target world object belongs to an NPC faction or matches the active player faction context.
/// </summary>
public static class WorldObjectMethodPatches
{
    [HarmonyPriority(MpPriority.MpFirst)]
    public static void Prefix(WorldObject __instance, ref bool __state)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return;

        Faction faction = __instance.Faction;
        // Avoid redundant context switches if the world object is an NPC faction or matches the currently active player faction
        if (__instance.def.canHaveFaction && faction is { IsPlayer: true } && faction != Faction.OfPlayer)
        {
            FactionContext.Push(faction);
            __state = true;
        }
    }

    [HarmonyPriority(MpPriority.MpLast)]
    public static void Finalizer(WorldObject __instance, bool __state)
    {
        if (__state)
            FactionContext.Pop();
    }
}