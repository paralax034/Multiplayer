using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Factions;

public static class FactionExtensions
{
    // Sets the current Faction.OfPlayer
    // Applies faction's world components
    // Applies faction's map components if map not null
    //
    // force: the faction is applied even if it already is Faction.OfPlayer, and to every map, not only the given one.
    // Forced pushes set up simulation contexts (map/world ticks and commands). Outside of them every map holds the
    // data of the local client's own faction, so applying the context's faction to all maps makes sure simulation
    // code touching another map (portals, pocket maps, gravships, ...) never sees client-specific data.
    // The matching pop restores exactly the factions the world and each map had before the push.
    public static void PushFaction(this Map map, Faction f, bool force = false)
    {
        var faction = FactionContext.Push(f, force);
        if (faction == null) return;

        if (force)
            FactionContext.SetTopState(AppliedFactions.Capture());
        else if (map != null)
            FactionContext.SetTopState(map); // Lets an unwind (FactionContext.RestoreStack) restore the map too

        Multiplayer.WorldComp?.SetFaction(faction);
        if (force)
            SetFactionOnAllMaps(faction);
        else
            map?.MpComp().SetFaction(faction);
    }

    public static void PushFaction(this Map map, int factionId)
    {
        Faction faction = Find.FactionManager.GetById(factionId);
        map.PushFaction(faction);
    }

    public static Faction PopFaction() => PopFaction(null);

    public static Faction PopFaction(this Map map)
    {
        Faction faction = FactionContext.Pop(out var state);
        if (faction == null) return null;

        if (state is AppliedFactions applied)
        {
            applied.Restore(faction);
            return faction;
        }

        Multiplayer.WorldComp?.SetFaction(faction);
        (map ?? state as Map)?.MpComp()?.SetFaction(faction);

        return faction;
    }

    private static void SetFactionOnAllMaps(Faction faction)
    {
        var comps = Multiplayer.game?.mapComps;
        if (comps == null) return;

        for (int i = 0; i < comps.Count; i++)
            if (IsLive(comps[i]))
                comps[i].SetFaction(faction);
    }

    // Comps of removed maps are never removed from the list
    private static bool IsLive(MultiplayerMapComp comp) => comp.map is { Disposed: false };

    // The factions whose data the world and the maps held before a forced push
    private class AppliedFactions
    {
        private static readonly Stack<AppliedFactions> Pool = new(16);

        private int worldFactionId;
        private List<MultiplayerMapComp> comps;
        private int[] mapFactionIds = new int[16];
        private int mapCount;

        /// <summary>
        /// Captures current map faction mappings using pooled state objects to eliminate GC pressure during ticks.
        /// </summary>
        public static AppliedFactions Capture()
        {
            AppliedFactions applied;
            lock (Pool)
            {
                applied = Pool.Count > 0 ? Pool.Pop() : new AppliedFactions();
            }

            applied.worldFactionId = Multiplayer.WorldComp?.GetAppliedFactionId() ?? -1;
            applied.comps = Multiplayer.game?.mapComps;

            int count = applied.comps?.Count ?? 0;
            applied.mapCount = count;

            if (applied.mapFactionIds.Length < count)
            {
                int newCap = Math.Max(applied.mapFactionIds.Length * 2, count);
                applied.mapFactionIds = new int[newCap];
            }

            for (int i = 0; i < count; i++)
            {
                var comp = applied.comps[i];
                applied.mapFactionIds[i] = IsLive(comp) ? comp.GetAppliedFactionId() : -1;
            }

            return applied;
        }

        // Maps without a known previous faction (e.g. generated in between) get the popped faction
        public void Restore(Faction popped)
        {
            try
            {
                var worldComp = Multiplayer.WorldComp;
                if (worldComp != null && (worldFactionId == -1 || !worldComp.SetFaction(worldFactionId)))
                    worldComp.SetFaction(popped);

                var currentComps = Multiplayer.game?.mapComps;
                if (currentComps != null)
                {
                    for (int i = 0; i < currentComps.Count; i++)
                    {
                        var comp = currentComps[i];
                        if (!IsLive(comp)) continue;

                        var id = currentComps == comps && i < mapCount ? mapFactionIds[i] : -1;
                        if (id == -1 || !comp.SetFaction(id))
                            comp.SetFaction(popped);
                    }
                }
            }
            finally
            {
                comps = null;
                lock (Pool)
                {
                    if (Pool.Count < 32)
                        Pool.Push(this);
                }
            }
        }
    }

    public static bool TryGetPlayerFaction(this Quest quest, out Faction faction)
    {
        faction = null;
        if (quest == null)
            return false;

        // O(1) fast path: check the deterministic quest-to-faction registry
        if (Multiplayer.WorldComp != null && Multiplayer.WorldComp.questFactions.TryGetValue(quest.id, out int fId))
        {
            faction = Find.FactionManager.GetById(fId);
            if (faction != null)
                return true;
        }

        foreach (var target in quest.QuestLookTargets)
        {
            if (target.HasWorldObject && target.WorldObject != null)
            {
                if (target.WorldObject.Faction is { IsPlayer: true } wf)
                {
                    faction = wf;
                    return true;
                }

                if (target.WorldObject is MapParent { HasMap: true } mp && mp.Map.ParentFaction is { IsPlayer: true } mpf)
                {
                    faction = mpf;
                    return true;
                }
            }
            else if (target.HasThing && target.Thing != null)
            {
                var thing = target.Thing;
                if (thing.Faction is { IsPlayer: true } tf)
                {
                    faction = tf;
                    return true;
                }

                if (thing is Pawn { HostFaction: { IsPlayer: true } hf })
                {
                    faction = hf;
                    return true;
                }

                if (thing.MapHeld?.ParentFaction is { IsPlayer: true } mf)
                {
                    faction = mf;
                    return true;
                }
            }
            else if (target.Map?.ParentFaction is { IsPlayer: true } cellMapFac)
            {
                faction = cellMapFac;
                return true;
            }
        }

        return false;
    }
}
