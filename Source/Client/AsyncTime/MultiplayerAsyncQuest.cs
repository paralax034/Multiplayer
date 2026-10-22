using HarmonyLib;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace Multiplayer.Client.Comp
{

    [HarmonyPatch(typeof(SettlementAbandonUtility), nameof(SettlementAbandonUtility.Abandon))]
    static class RemoveMapCacheOnAbandon
    {
        static void Prefix(MapParent settlement)
        {
            if (Multiplayer.Client == null) return;
            if (!Multiplayer.GameComp.asyncTime) return;

            var mapAsyncTimeComp = settlement.Map.AsyncTime();
            if (mapAsyncTimeComp != null)
            {
                MultiplayerAsyncQuest.TryRemoveCachedMap(mapAsyncTimeComp);
            }
        }
    }

    [HarmonyPatch(typeof(QuestGen), nameof(QuestGen.Generate))]
    static class CacheQuestAfterGeneration
    {
        static void Postfix(ref Quest __result)
        {
            if (Multiplayer.Client == null) return;
            if (!Multiplayer.GameComp.asyncTime) return;

            MultiplayerAsyncQuest.CacheQuest(__result);
        }
    }

    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.QuestManagerTick))]
    [HarmonyPriority(Priority.Last)]
    static class DisableQuestManagerTickTest
    {
        static bool Prefix()
        {
            if (Multiplayer.Client == null) return true;
            if (!Multiplayer.GameComp.asyncTime) return true;

            //Only tick world quest during world time
            MultiplayerAsyncQuest.TickWorldQuests();
            return false;
        }
    }

    //Clear Cache then Cache all quests on game load
    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    [HarmonyPriority(Priority.Last)]
    static class SetupAsyncTimeLookupForQuests
    {
        static void Postfix()
        {
            if (Multiplayer.Client == null) return;
            if (!Multiplayer.GameComp.asyncTime) return;

            MultiplayerAsyncQuest.Reset();

            foreach (var quest in Find.QuestManager.QuestsListForReading)
            {
                MultiplayerAsyncQuest.CacheQuest(quest);
            }
        }
    }

    [HarmonyPatch]
    static class SetContextForQuest
    {
        static IEnumerable<MethodInfo> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Quest), nameof(Quest.SetInitiallyAccepted));
            yield return AccessTools.Method(typeof(Quest), nameof(Quest.CleanupQuestParts));
        }

        static void Prefix(Quest __instance, ref AsyncTimeComp __state)
        {
            if (Multiplayer.Client == null) return;
            if (!Multiplayer.GameComp.asyncTime) return;

            __state = MultiplayerAsyncQuest.TryGetCachedQuestMap(__instance);
            __state?.PreContext();
        }

        static void Finalizer(AsyncTimeComp __state) => __state?.PostContext();
    }

    [HarmonyPatch(typeof(Quest), nameof(Quest.TicksSinceAppeared), MethodType.Getter)]
    static class QuestTicksSinceAppearedPatch
    {
        static bool Prefix(Quest __instance, ref int __result)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime) return true;

            var mapComp = MultiplayerAsyncQuest.TryGetCachedQuestMap(__instance);
            int currentTicks = mapComp != null ? mapComp.mapTicks : Find.TickManager.TicksGame;
            __result = currentTicks - __instance.appearanceTick;
            return false;
        }
    }

    [HarmonyPatch(typeof(Quest), nameof(Quest.TicksSinceAccepted), MethodType.Getter)]
    static class QuestTicksSinceAcceptedPatch
    {
        static bool Prefix(Quest __instance, ref int __result)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime) return true;

            if (__instance.acceptanceTick < 0)
            {
                __result = -1;
                return false;
            }

            var mapComp = MultiplayerAsyncQuest.TryGetCachedQuestMap(__instance);
            int currentTicks = mapComp != null ? mapComp.mapTicks : Find.TickManager.TicksGame;
            __result = currentTicks - __instance.acceptanceTick;
            return false;
        }
    }

    [HarmonyPatch(typeof(Quest), nameof(Quest.TicksSinceCleanup), MethodType.Getter)]
    static class QuestTicksSinceCleanupPatch
    {
        static bool Prefix(Quest __instance, ref int __result)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime) return true;

            if (__instance.cleanupTick < 0)
            {
                __result = -1;
                return false;
            }

            var mapComp = MultiplayerAsyncQuest.TryGetCachedQuestMap(__instance);
            int currentTicks = mapComp != null ? mapComp.mapTicks : Find.TickManager.TicksGame;
            __result = currentTicks - __instance.cleanupTick;
            return false;
        }
    }

    /// <summary>
    /// Ensures that quest initiation executes within the correct map AsyncTimeComp context.
    /// Setting context during Quest.Initiate guarantees that generated quest parts, threats,
    /// and timers are initialized against the target map's tick timeline rather than global world time.
    /// </summary>
    [HarmonyPatch(typeof(Quest), nameof(Quest.Initiate))]
    static class SetContextForInitiate
    {
        static void Prefix(Quest __instance, ref AsyncTimeComp __state)
        {
            if (Multiplayer.Client == null) return;
            if (!Multiplayer.GameComp.asyncTime || __instance.parts == null) return;

            __state = MultiplayerAsyncQuest.CacheQuest(__instance);
            __state?.PreContext();
        }

        static void Postfix(AsyncTimeComp __state) => __state?.PostContext();
    }

    [HarmonyPatch(typeof(Quest), nameof(Quest.End))]
    static class RemoveQuestFromCacheOnQuestEnd
    {
        static void Postfix(Quest __instance)
        {
            if (Multiplayer.Client == null) return;
            MultiplayerAsyncQuest.TryRemoveCachedQuest(__instance);
        }
    }

    public static class MultiplayerAsyncQuest
    {
        // Reusable buffer for map and world quest lookups
        private static readonly Dictionary<AsyncTimeComp, List<Quest>> mapQuestsCache = [];
        private static readonly Dictionary<Quest, AsyncTimeComp> questToMapCache = [];
        private static readonly List<Quest> worldQuestsCache = [];
        private static readonly List<Quest> TmpQuestsToTick = new List<Quest>(32);

        // Fast O(1) set lookup replacing List.Contains for quest part types
        private static readonly HashSet<Type> questPartsToCheck =
        [
            typeof(QuestPart_DropPods),
            typeof(QuestPart_SpawnThing),
            typeof(QuestPart_PawnsArrive),
            typeof(QuestPart_Incident),
            typeof(QuestPart_RandomRaid),
            typeof(QuestPart_ThreatsGenerator),
            typeof(QuestPart_Infestation),
            typeof(QuestPart_GameCondition),
            typeof(QuestPart_JoinPlayer),
            typeof(QuestPart_TrackWhenExitMentalState),
            typeof(QuestPart_RequirementsToAcceptBedroom),
            typeof(QuestPart_MechCluster),
            typeof(QuestPart_DropMonumentMarkerCopy),
            typeof(QuestPart_PawnsAvailable)
        ];

        // Reflection field cache for mapParent member resolution
        private static readonly Dictionary<Type, FieldInfo> MapParentFieldCache = [];

        /// <summary>
        /// Tries to remove Map from cache, then moves all Quests cached to that map to WorldQuestsCache
        /// </summary>
        /// <param name="mapAsyncTimeComp">Map to remove</param>
        /// <returns>If map is found in cache</returns>
        public static bool TryRemoveCachedMap(AsyncTimeComp mapAsyncTimeComp)
        {
            if (mapQuestsCache.TryGetValue(mapAsyncTimeComp, out var quests))
            {
                for (int i = 0; i < quests.Count; i++)
                    questToMapCache.Remove(quests[i]);

                worldQuestsCache.AddRange(quests);
                return mapQuestsCache.Remove(mapAsyncTimeComp);
            }

            return false;
        }

        /// <summary>
        /// Tries to remove Quest from Map and World cache
        /// </summary>
        /// <param name="quest">Quest to remove</param>
        /// <returns>If quest is found in cache</returns>
        public static bool TryRemoveCachedQuest(Quest quest)
        {
            questToMapCache.Remove(quest);

            // Removes every entry: a quest cached twice would be ticked twice per tick
            bool removed = worldQuestsCache.RemoveAll(q => q == quest) > 0;
            foreach (var quests in mapQuestsCache.Values)
                removed |= quests.RemoveAll(q => q == quest) > 0;
            return removed;
        }

        /// <summary>
        /// Attempts to get the MapAsyncTimeComp cached for that quest via zero-allocation dictionary lookup.
        /// </summary>
        /// <param name="quest">Quest to find MapAsyncTimeComp for</param>
        /// <returns>MapAsyncTimeComp for that quest or Null if not found</returns>
        public static AsyncTimeComp TryGetCachedQuestMap(Quest quest)
        {
            if (!Multiplayer.GameComp.asyncTime || quest == null) return null;
            return questToMapCache.TryGetValue(quest, out var comp) ? comp : null;
        }

        /// <summary>
        /// Determines if a quest has a MapAsyncTimeComp, if it does it will cache it to the mapQuestsCache otherwise to worldQuestsCache
        /// </summary>
        /// <param name="quest">Quest to Cache</param>
        /// <returns>MapAsyncTimeComp for that quest or Null if not found</returns>
        public static AsyncTimeComp CacheQuest(Quest quest)
        {
            if (!Multiplayer.GameComp.asyncTime || quest == null) return null;

            // Check if quest targets players map
            var mapAsyncTimeComp = TryGetQuestMap(quest);

            // If it does add it to the cache for that map
            if (mapAsyncTimeComp != null)
            {
                UpsertQuestMap(mapAsyncTimeComp, quest);
                if (MpVersion.IsDebug)
                    Log.Message($"Info: Found AsyncTimeMap: '{mapAsyncTimeComp.map.Parent.Label}' for Quest: '{quest.name}'");
            }
            // If it doesn't add it to the world quest list
            else
            {
                // Accepting caches a quest again after its generation did
                TryRemoveCachedQuest(quest);
                worldQuestsCache.Add(quest);
                if (MpVersion.IsDebug)
                    Log.Message($"Info: Could not find AsyncTimeMap for Quest: '{quest.name}'");
            }

            return mapAsyncTimeComp;
        }

        /// <summary>
        /// Clears all cache for both mapQuestsCache, questToMapCache and worldQuestsCache
        /// </summary>
        public static void Reset()
        {
            mapQuestsCache.Clear();
            questToMapCache.Clear();
            worldQuestsCache.Clear();
        }

        /// <summary>
        /// Runs QuestTick() on all quests cached for worldQuestsCache
        /// </summary>
        public static void TickWorldQuests()
        {
            TickQuests(worldQuestsCache);
        }

        /// <summary>
        /// Runs QuestTick() on all quests cached for a specific map
        /// </summary>
        /// <param name="mapAsyncTimeComp">Map to tick Quests for</param>
        public static void TickMapQuests(AsyncTimeComp mapAsyncTimeComp)
        {
            if (mapQuestsCache.TryGetValue(mapAsyncTimeComp, out var quests))
            {
                TickQuests(quests);
            }
        }

        /// <summary>
        /// Runs QuestTick() on all ongoing quests using a static reusable buffer to eliminate heap allocations.
        /// </summary>
        /// <param name="quests">Collection of quests to tick.</param>
        private static void TickQuests(List<Quest> quests)
        {
            if (quests == null || quests.Count == 0)
                return;

            TmpQuestsToTick.Clear();
            for (int i = 0; i < quests.Count; i++)
            {
                var quest = quests[i];
                if (quest != null && quest.State == QuestState.Ongoing)
                {
                    TmpQuestsToTick.Add(quest);
                }
            }

            for (int i = 0; i < TmpQuestsToTick.Count; i++)
            {
                TmpQuestsToTick[i].QuestTick();
            }

            TmpQuestsToTick.Clear();
        }

        /// <summary>
        /// Attempts to find the map the quest will target via cached FieldInfo reflection lookups.
        /// </summary>
        /// <param name="quest">Quest to check Map Parts on</param>
        /// <returns>MapAsyncTimeComp for that quest or Null if not found</returns>
        private static AsyncTimeComp TryGetQuestMap(Quest quest)
        {
            var parts = quest.parts;
            if (parts == null) return null;

            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null) continue;

                var partType = part.GetType();
                if (!questPartsToCheck.Contains(partType)) continue;

                if (!MapParentFieldCache.TryGetValue(partType, out var field))
                {
                    field = partType.GetField("mapParent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    MapParentFieldCache[partType] = field;
                }

                if (field?.GetValue(part) is MapParent { Map: not null } mapParent)
                {
                    var map = mapParent.Map;
                    var mapAsyncTimeComp = map.IsPlayerHome ? map.AsyncTime() : null;
                    if (mapAsyncTimeComp != null)
                        return mapAsyncTimeComp;
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts to update or insert a new cache for a Map and Quest, will always attempt to remove the quest before adding in case it existed on another map.
        /// </summary>
        /// <param name="mapAsyncTimeComp">Map that will hold the quest</param>
        /// <param name="quest">Quest for the map</param>
        private static void UpsertQuestMap(AsyncTimeComp mapAsyncTimeComp, Quest quest)
        {
            TryRemoveCachedQuest(quest);

            questToMapCache[quest] = mapAsyncTimeComp;

            if (mapQuestsCache.TryGetValue(mapAsyncTimeComp, out var quests))
            {
                quests.Add(quest);
            }
            else
            {
                mapQuestsCache[mapAsyncTimeComp] = new List<Quest> { quest };
            }
        }
    }
}
