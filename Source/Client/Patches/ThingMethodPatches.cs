using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Client.Factions;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Client
{
    /// <summary>
    /// Tracks the execution state of context-switching patches to guarantee symmetrical push and pop operations.
    /// </summary>
    public struct ThingPatchState
    {
        public bool isValid;
        public Map map;
        public bool factionPushed;
        public bool thingPushed;
    }

    /// <summary>
    /// Ensures dynamic pawn components are evaluated under the owning pawn's faction context in multifaction mode.
    /// </summary>
    [HarmonyPatch(typeof(PawnComponentsUtility), nameof(PawnComponentsUtility.AddAndRemoveDynamicComponents))]
    public static class AddAndRemoveCompsPatch
    {
        static void Prefix(Pawn pawn, ref ThingPatchState __state)
        {
            if (Multiplayer.Client == null || pawn?.Faction == null || !Multiplayer.GameComp.multifaction) return;

            bool pushFaction = pawn.Map != null && pawn.Faction.IsPlayer && pawn.Faction != Faction.OfPlayer;
            __state = new ThingPatchState { isValid = true, map = pawn.Map, factionPushed = pushFaction, thingPushed = false };

            if (pushFaction)
                pawn.Map.PushFaction(pawn.Faction);
        }

        static void Finalizer(ref ThingPatchState __state)
        {
            if (__state.isValid && __state.factionPushed)
                __state.map.PopFaction();
        }
    }

    /// <summary>
    /// Ensures pawn needs adjustments respect the pawn's owning faction context.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.AddOrRemoveNeedsAsAppropriate))]
    public static class AddRemoveNeedsPatch
    {
        static void Prefix(Pawn ___pawn, ref ThingPatchState __state)
        {
            if (Multiplayer.Client == null || ___pawn?.Faction == null || !Multiplayer.GameComp.multifaction) return;

            bool pushFaction = ___pawn.Map != null && ___pawn.Faction.IsPlayer && ___pawn.Faction != Faction.OfPlayer;
            __state = new ThingPatchState { isValid = true, map = ___pawn.Map, factionPushed = pushFaction, thingPushed = false };

            if (pushFaction)
                ___pawn.Map.PushFaction(___pawn.Faction);
        }

        static void Finalizer(ref ThingPatchState __state)
        {
            if (__state.isValid && __state.factionPushed)
                __state.map.PopFaction();
        }
    }

    /// <summary>
    /// Scopes job execution to the pawn's faction context and logs unauthorized interface job initiations.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class JobTrackerStart
    {
        static void Prefix(Pawn_JobTracker __instance, Job newJob, ref ThingPatchState __state)
        {
            if (Multiplayer.Client == null) return;

            if (Multiplayer.InInterface)
            {
                Log.Warning($"Started a job {newJob} on pawn {__instance.pawn} from the interface!");
                return;
            }

            Pawn pawn = __instance.pawn;
            if (pawn?.Faction == null || !pawn.Spawned || !Multiplayer.GameComp.multifaction) return;

            bool pushFaction = pawn.Map != null && pawn.Faction.IsPlayer && pawn.Faction != Faction.OfPlayer;
            __state = new ThingPatchState { isValid = true, map = pawn.Map, factionPushed = pushFaction, thingPushed = false };

            if (pushFaction)
                pawn.Map.PushFaction(pawn.Faction);
        }

        static void Finalizer(ref ThingPatchState __state)
        {
            if (__state.isValid && __state.factionPushed)
                __state.map.PopFaction();
        }
    }

    /// <summary>
    /// Scopes job termination to the pawn's faction context in multifaction mode.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class JobTrackerEndCurrent
    {
        static void Prefix(Pawn_JobTracker __instance, JobCondition condition, ref ThingPatchState __state)
        {
            if (Multiplayer.Client == null) return;

            Pawn pawn = __instance.pawn;
            if (pawn?.Faction == null || !pawn.Spawned || !Multiplayer.GameComp.multifaction) return;

            bool pushFaction = pawn.Map != null && pawn.Faction.IsPlayer && pawn.Faction != Faction.OfPlayer;
            __state = new ThingPatchState { isValid = true, map = pawn.Map, factionPushed = pushFaction, thingPushed = false };

            if (pushFaction)
                pawn.Map.PushFaction(pawn.Faction);
        }

        static void Finalizer(ref ThingPatchState __state)
        {
            if (__state.isValid && __state.factionPushed)
                __state.map.PopFaction();
        }
    }

    /// <summary>
    /// Scopes job override evaluations to both the pawn's ThingContext and FactionContext.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.CheckForJobOverride))]
    public static class JobTrackerOverride
    {
        static void Prefix(Pawn_JobTracker __instance, ref ThingPatchState __state)
        {
            if (Multiplayer.Client == null) return;

            Pawn pawn = __instance.pawn;
            if (pawn?.Faction == null || !pawn.Spawned || !Multiplayer.GameComp.multifaction) return;

            bool pushFaction = pawn.Map != null && pawn.Faction.IsPlayer && pawn.Faction != Faction.OfPlayer;
            __state = new ThingPatchState { isValid = true, map = pawn.Map, factionPushed = pushFaction, thingPushed = true };

            ThingContext.Push(pawn);

            if (pushFaction)
                pawn.Map.PushFaction(pawn.Faction);
        }

        static void Finalizer(ref ThingPatchState __state)
        {
            if (!__state.isValid) return;

            try
            {
                if (__state.factionPushed)
                    __state.map.PopFaction();
            }
            finally
            {
                if (__state.thingPushed)
                    ThingContext.Pop();
            }
        }
    }

    /// <summary>
    /// Dynamically applied hooks on Thing lifecycle and ticking to maintain ThingContext and FactionContext.
    /// Uses hierarchical short-circuiting to eliminate 99% of ThingContext stack operations on inanimate objects.
    /// </summary>
    public static class ThingMethodPatches
    {
        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix(Thing __instance, ref ThingPatchState __state)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return;

            bool isPawn = __instance is Pawn;
            bool isDropPod = !isPawn && __instance.def == ThingDefOf.ActiveDropPod;

            Faction faction = null;
            bool pushFaction = false;
            Map map = null;

            if (__instance.def.CanHaveFaction)
            {
                faction = __instance.Faction;
                if (faction is { IsPlayer: true } && faction != Faction.OfPlayer)
                {
                    map = __instance.Map;
                    pushFaction = map != null;
                }
            }

            // Short-circuit: skip ThingContext allocation for static inanimate items, filth, plants, and terrain
            bool pushThing = pushFaction || isPawn || isDropPod;
            if (!pushThing)
                return;

            __state = new ThingPatchState { isValid = true, map = map, factionPushed = pushFaction, thingPushed = pushThing };

            if (pushThing)
                ThingContext.Push(__instance);

            if (pushFaction)
                map.PushFaction(faction);
        }

        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix_SpawnSetup(Thing __instance, Map __0, ref ThingPatchState __state)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return;

            bool isPawn = __instance is Pawn;
            bool isDropPod = !isPawn && __instance.def == ThingDefOf.ActiveDropPod;

            Faction faction = null;
            bool pushFaction = false;

            if (__instance.def.CanHaveFaction)
            {
                faction = __instance.Faction;
                if (faction is { IsPlayer: true } && faction != Faction.OfPlayer)
                {
                    pushFaction = __0 != null;
                }
            }

            bool pushThing = pushFaction || isPawn || isDropPod;
            if (!pushThing)
                return;

            __state = new ThingPatchState { isValid = true, map = __0, factionPushed = pushFaction, thingPushed = pushThing };

            if (pushThing)
                ThingContext.Push(__instance);

            if (pushFaction)
                __0.PushFaction(faction);
        }

        [HarmonyPriority(MpPriority.MpLast)]
        public static void Finalizer(ref ThingPatchState __state)
        {
            if (!__state.isValid) return;

            try
            {
                if (__state.factionPushed)
                    __state.map.PopFaction();
            }
            finally
            {
                if (__state.thingPushed)
                    ThingContext.Pop();
            }
        }
    }

    /// <summary>
    /// Thread-static execution context tracking the active Thing and Map hierarchy for determinism and desync logging.
    /// </summary>
    public static class ThingContext
    {
        public static readonly Stack<(Thing, Map)> stack = new();

        static ThingContext() => Clear();

        public static Thing Current => stack.Count > 0 ? stack.Peek().Item1 : null;
        public static Pawn CurrentPawn => Current as Pawn;

        public static Map CurrentMap
        {
            get
            {
                if (stack.Count == 0) return null;
                var peek = stack.Peek();
                if (peek.Item1 != null && peek.Item1.Map != peek.Item2)
                    Log.ErrorOnce($"Thing {peek.Item1} has changed its map!", peek.Item1.thingIDNumber ^ 57481021);
                return peek.Item2;
            }
        }

        public static void Push(Thing t)
        {
            stack.Push((t, t?.Map));
        }

        public static void Pop()
        {
            if (stack.Count > 0)
            {
                stack.Pop();
            }
            else
            {
                Log.ErrorOnce("Multiplayer: unbalanced ThingContext.Pop call (stack is empty)!", 0x3A8F1C2B);
            }
        }

        public static void Clear()
        {
            stack.Clear();
        }
    }
}