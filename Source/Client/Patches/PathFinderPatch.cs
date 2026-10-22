using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// Isolates worker thread pathfinding jobs from mutating pawn simulation state.
    /// Snapshots all pawn positions, collision flags, and job collision settings on the main thread
    /// before job dispatch, allowing PathGridDoorsBlockedJob to execute concurrently without freezing the main thread.
    /// </summary>
    internal static class PathFinderPatch
    {
        /// <summary>
        /// Immutable snapshot of pawn state read by worker threads during path cost calculation.
        /// </summary>
        private sealed class PawnSnapShot(Pawn pawn)
        {
            public readonly IntVec3 Position = pawn.Position;
            public readonly bool ShouldCollideWithPawns = PawnUtility.ShouldCollideWithPawns(pawn);
            public readonly bool CollideWithNonHostile = pawn.CurJob != null && (pawn.CurJob.collideWithPawns
                    || pawn.CurJob.def.collideWithPawns || pawn.jobs?.curDriver?.collideWithPawns == true);
        }

        /// <summary>
        /// Thread-safe dictionary storing immutable pawn snapshots for the current tick's pathfinding calculations.
        /// </summary>
        private static class PawnPositionSnapshot
        {
            private static readonly Dictionary<Pawn, PawnSnapShot> SnapshotDict = new();

            public static void Rebuild(IReadOnlyList<Pawn> pawns)
            {
                SnapshotDict.Clear();
                if (pawns == null) return;

                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn pawn = pawns[i];
                    if (pawn != null && pawn.Spawned)
                    {
                        SnapshotDict[pawn] = new PawnSnapShot(pawn);
                    }
                }
            }

            public static bool TryGet(Pawn pawn, out PawnSnapShot snapshot) =>
                SnapshotDict.TryGetValue(pawn, out snapshot);
        }

        /// <summary>
        /// Rebuilds pawn snapshots immediately before batched path jobs are dispatched.
        /// Ensures cachedDoors and cachedPawns are populated on the main thread prior to scheduling.
        /// </summary>
        [HarmonyPatch(typeof(PathFinder), "ScheduleBatchedPathJobs")]
        private static class ScheduleBatchedPathJobsSnapshotPositions
        {
            private static readonly MethodInfo EnsureCachedMethod =
                AccessTools.Method(typeof(PathFinder), "EnsureDoorsPawnsCached");
            private static readonly FieldInfo CachedPawnsField =
                AccessTools.Field(typeof(PathFinder), "cachedPawns");

            private static void Prefix(PathFinder __instance)
            {
                if (Multiplayer.Client == null) return;

                EnsureCachedMethod?.Invoke(__instance, null);
                var cachedPawns = (IReadOnlyList<Pawn>)CachedPawnsField.GetValue(__instance);
                PawnPositionSnapshot.Rebuild(cachedPawns);
            }
        }

        /// <summary>
        /// Redirects worker thread pawn.Position reads to the snapshot dictionary.
        /// Prevents race conditions where the main thread modifies pawn positions during PatherTick.
        /// </summary>
        [HarmonyPatch]
        private static class PathGridDoorsBlockedJobPositionPatch
        {
            private static readonly MethodInfo PositionGetter =
                AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Position));
            private static readonly MethodInfo ReplacementPositionGetter =
                AccessTools.Method(typeof(PathGridDoorsBlockedJobPositionPatch), nameof(GetPosition));

            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(PathGridDoorsBlockedJob), nameof(PathGridDoorsBlockedJob.Execute));
                yield return AccessTools.Method(typeof(PathGridDoorsBlockedJob), "CanBlockEver");
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var inst in instructions)
                {
                    if (inst.Calls(PositionGetter))
                    {
                        inst.opcode = OpCodes.Call;
                        inst.operand = ReplacementPositionGetter;
                    }
                    yield return inst;
                }
            }

            internal static IntVec3 GetPosition(Pawn pawn)
            {
                if (Multiplayer.Client != null && PawnPositionSnapshot.TryGet(pawn, out PawnSnapShot snapshot))
                    return snapshot.Position;

                return pawn.Position;
            }
        }

        /// <summary>
        /// Redirects collision and blocking evaluations in PathGridDoorsBlockedJob to snapshot-aware logic,
        /// avoiding main-thread managed property queries from worker threads.
        /// </summary>
        [HarmonyPatch]
        private static class PathGridDoorsBlockedJobPawnBlockedByPatch
        {
            private static readonly MethodInfo TargetPawnBlockedBy =
                AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.PawnBlockedBy));
            private static readonly MethodInfo ReplacementPawnBlockedBy =
                AccessTools.Method(typeof(PathGridDoorsBlockedJobPawnBlockedByPatch), nameof(PawnBlockedBySnapshotAware));

            private static readonly MethodInfo TargetShouldCollide =
                AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.ShouldCollideWithPawns));
            private static readonly MethodInfo ReplacementShouldCollide =
                AccessTools.Method(typeof(PathGridDoorsBlockedJobPawnBlockedByPatch), nameof(GetShouldCollideWithPawns));

            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(PathGridDoorsBlockedJob), nameof(PathGridDoorsBlockedJob.Execute));
                yield return AccessTools.Method(typeof(PathGridDoorsBlockedJob), "CanBlockEver");
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var inst in instructions)
                {
                    if (inst.Calls(TargetPawnBlockedBy))
                    {
                        inst.opcode = OpCodes.Call;
                        inst.operand = ReplacementPawnBlockedBy;
                    }
                    else if (inst.Calls(TargetShouldCollide))
                    {
                        inst.opcode = OpCodes.Call;
                        inst.operand = ReplacementShouldCollide;
                    }
                    yield return inst;
                }
            }

            internal static bool GetShouldCollideWithPawns(Pawn pawn)
            {
                if (Multiplayer.Client != null && PawnPositionSnapshot.TryGet(pawn, out PawnSnapShot snapshot))
                    return snapshot.ShouldCollideWithPawns;

                return PawnUtility.ShouldCollideWithPawns(pawn);
            }

            internal static bool PawnBlockedBySnapshotAware(Pawn forPawn, Pawn other, bool collideOnlyWithStandingPawns, bool collideWithNonHostile, bool forPathFinder, bool useId)
            {
                if (Multiplayer.Client != null && PawnPositionSnapshot.TryGet(other, out PawnSnapShot snapshot))
                {
                    if (!snapshot.ShouldCollideWithPawns)
                        return false;

                    if (other == forPawn)
                        return false;

                    if (PawnUtility.PawnsCanShareCellBecauseOfBodySize(other, forPawn))
                        return false;

                    if (other.IsPsychologicallyInvisible() || !other.kindDef.collidesWithPawns)
                        return false;

                    if (other.HostileTo(forPawn))
                        return true;

                    if (collideWithNonHostile)
                        return snapshot.CollideWithNonHostile;

                    return false;
                }

                return PawnUtility.PawnBlockedBy(forPawn, other, collideOnlyWithStandingPawns, collideWithNonHostile, forPathFinder, useId);
            }
        }
    }
}