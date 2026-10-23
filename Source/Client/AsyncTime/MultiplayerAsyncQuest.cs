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

    /// <summary>
    /// Caches the generated quest into its target time domain (map or world) and normalizes its initial
    /// tick timestamps right before QuestGen clears its static working state.
    /// Running on ClearQuestGenState instead of Generate Postfix guarantees that QuestGen.slate (containing "map")
    /// and QuestGen.quest are still populated before ClearQuestGenState wipes them in Generate's finally block.
    /// </summary>
    [HarmonyPatch(typeof(QuestGen), nameof(QuestGen.ClearQuestGenState))]
    static class CacheQuestBeforeClearState
    {
        static void Prefix()
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime) return;

            var quest = QuestGen.quest;
            if (quest == null) return;

            var mapComp = MultiplayerAsyncQuest.CacheQuest(quest);
            int targetTicks = mapComp != null
                ? mapComp.mapTicks
                : (Multiplayer.AsyncWorldTime?.worldTicks ?? Find.TickManager.TicksGame);

            int delta = targetTicks - quest.appearanceTick;
            if (delta != 0)
            {
                quest.appearanceTick += delta;
                if (quest.acceptanceTick >= 0)
                    quest.acceptanceTick += delta;
                if (quest.acceptanceExpireTick >= 0)
                    quest.acceptanceExpireTick += delta;
            }
        }
    }

    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.QuestManagerTick))]
    [HarmonyPriority(Priority.Last)]
    static class DisableQuestManagerTickTest
    {
        public static bool Prefix()
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

    /// <summary>
    /// Enters the quest's authoritative time domain (target map AsyncTimeComp or world ticks) during
    /// Quest lifecycle transitions that read Find.TickManager.TicksGame (Accept, SetInitiallyAccepted,
    /// Initiate, CleanupQuestParts). Prevents QuestPartActivable.Enable from latching mapTicks onto
    /// a world quest (or vice versa), which previously caused QuestPart_WorldObjectTimeout to expire instantly.
    /// </summary>
    [HarmonyPatch]
    static class SetContextForQuest
    {
        static IEnumerable<MethodInfo> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Quest), nameof(Quest.Accept));
            yield return AccessTools.Method(typeof(Quest), nameof(Quest.SetInitiallyAccepted));
            yield return AccessTools.Method(typeof(Quest), nameof(Quest.Initiate));
            yield return AccessTools.Method(typeof(Quest), nameof(Quest.CleanupQuestParts));
        }

        static void Prefix(Quest __instance, MethodBase __originalMethod, ref (AsyncTimeComp mapComp, TimeSnapshot? worldSnapshot) __state)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime || __instance == null) return;

            // If SetInitiallyAccepted runs inside QuestGen.Generate before ClearQuestGenState,
            // CacheQuestBeforeClearState will normalize acceptanceTick once all parts and slate vars are finalized.
            if (QuestGen.Working && QuestGen.quest == __instance) return;

            var mapComp = __originalMethod.Name == nameof(Quest.Initiate)
                ? MultiplayerAsyncQuest.CacheQuest(__instance)
                : MultiplayerAsyncQuest.TryGetCachedQuestMap(__instance);

            if (mapComp != null)
            {
                __state.mapComp = mapComp;
                mapComp.PreContext();
            }
            else if (Multiplayer.AsyncWorldTime != null)
            {
                __state.worldSnapshot = TimeSnapshot.Current();
                Find.TickManager.ticksGameInt = Multiplayer.AsyncWorldTime.worldTicks;
            }
        }

        static void Finalizer((AsyncTimeComp mapComp, TimeSnapshot? worldSnapshot) __state)
        {
            __state.mapComp?.PostContext();
            __state.worldSnapshot?.Set();
        }
    }

    /// <summary>
    /// Evaluates Quest.TicksSinceAppeared against the quest's assigned time domain (map or world)
    /// rather than whichever clock happens to be loaded in Find.TickManager.TicksGame during UI rendering.
    /// </summary>
    [HarmonyPatch(typeof(Quest), nameof(Quest.TicksSinceAppeared), MethodType.Getter)]
    static class QuestTicksSinceAppearedPatch
    {
        public static bool Prefix(Quest __instance, ref int __result)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime) return true;

            int currentTicks = MultiplayerAsyncQuest.GetQuestDomainTicks(__instance);
            __result = currentTicks - __instance.appearanceTick;
            return false;
        }
    }

    /// <summary>
    /// Evaluates Quest.TicksSinceAccepted against the quest's assigned time domain (map or world).
    /// </summary>
    [HarmonyPatch(typeof(Quest), nameof(Quest.TicksSinceAccepted), MethodType.Getter)]
    static class QuestTicksSinceAcceptedPatch
    {
        public static bool Prefix(Quest __instance, ref int __result)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime) return true;

            if (__instance.acceptanceTick < 0)
            {
                __result = -1;
                return false;
            }

            int currentTicks = MultiplayerAsyncQuest.GetQuestDomainTicks(__instance);
            __result = currentTicks - __instance.acceptanceTick;
            return false;
        }
    }

    /// <summary>
    /// Evaluates Quest.TicksSinceCleanup against the quest's assigned time domain (map or world).
    /// Preserves vanilla's !cleanedUp check so uncleaned quests always return -1.
    /// </summary>
    [HarmonyPatch(typeof(Quest), nameof(Quest.TicksSinceCleanup), MethodType.Getter)]
    static class QuestTicksSinceCleanupPatch
    {
        public static bool Prefix(Quest __instance, ref int __result)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime) return true;

            if (!__instance.cleanedUp || __instance.cleanupTick < 0)
            {
                __result = -1;
                return false;
            }

            int currentTicks = MultiplayerAsyncQuest.GetQuestDomainTicks(__instance);
            __result = currentTicks - __instance.cleanupTick;
            return false;
        }
    }

    /// <summary>
    /// Evaluates Quest.TicksUntilExpiry against the quest's assigned time domain (map or world).
    /// Without this patch, MainTabWindow_Quests and world ticks evaluate map quests against worldTicks
    /// (and vice versa), causing quests on maps with lower mapTicks to immediately report 0 ticks remaining
    /// and transition into QuestState.EndedOfferExpired.
    /// </summary>
    [HarmonyPatch(typeof(Quest), nameof(Quest.TicksUntilExpiry), MethodType.Getter)]
    static class QuestTicksUntilExpiryPatch
    {
        public static bool Prefix(Quest __instance, ref int __result)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime) return true;

            if (__instance.acceptanceExpireTick == -1)
            {
                __result = -1;
                return false;
            }

            int currentTicks = MultiplayerAsyncQuest.GetQuestDomainTicks(__instance);
            __result = Math.Max(__instance.acceptanceExpireTick - currentTicks, 0);
            return false;
        }
    }

    /// <summary>
    /// Ensures QuestPartActivable.enableTick is recorded against the parent quest's time domain.
    /// Prevents cross-domain signal propagation (e.g. initiating a world quest from a map command)
    /// from storing mapTicks inside world quest delay parts, which caused instant timeout expiration.
    /// </summary>
    [HarmonyPatch(typeof(QuestPartActivable), "Enable")]
    static class QuestPartActivableEnablePatch
    {
        static void Postfix(QuestPartActivable __instance)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime || __instance?.quest == null) return;

            int domainTicks = MultiplayerAsyncQuest.GetQuestDomainTicks(__instance.quest);
            AccessTools.Field(typeof(QuestPartActivable), "enableTick").SetValue(__instance, domainTicks);
        }
    }

    /// <summary>
    /// Evaluates QuestPart_Delay.TicksLeft against the quest's authoritative time domain.
    /// Fixes shuttle departures, world object timeouts, and raid delays in UI and inspectors.
    /// </summary>
    [HarmonyPatch(typeof(QuestPart_Delay), nameof(QuestPart_Delay.TicksLeft), MethodType.Getter)]
    static class QuestPartDelayTicksLeftPatch
    {
        public static bool Prefix(QuestPart_Delay __instance, ref int __result)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime) return true;

            if (__instance.State != QuestPartState.Enabled)
            {
                __result = 0;
                return false;
            }

            int currentTicks = MultiplayerAsyncQuest.GetQuestDomainTicks(__instance.quest);
            __result = Math.Max(__instance.EnableTick + __instance.delayTicks - currentTicks, 0);
            return false;
        }
    }

    /// <summary>
    /// Ensures QuestPart_Delay.QuestPartTick compares elapsed time against the quest's domain ticks
    /// rather than whichever clock is active in Find.TickManager.TicksGame.
    /// </summary>
    [HarmonyPatch(typeof(QuestPart_Delay), nameof(QuestPart_Delay.QuestPartTick))]
    static class QuestPartDelayTickPatch
    {
        public static bool Prefix(QuestPart_Delay __instance)
        {
            if (Multiplayer.Client == null || !Multiplayer.GameComp.asyncTime) return true;

            if (__instance.waitUntilPlayerHasHomeMap && Find.AnyPlayerHomeMap == null)
                return false;

            int currentTicks = MultiplayerAsyncQuest.GetQuestDomainTicks(__instance.quest);
            if (currentTicks >= __instance.EnableTick + __instance.delayTicks)
            {
                __instance.DebugForceEnd();
            }

            return false;
        }
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
        /// Returns the current tick count for the quest's time domain (mapTicks if bound to a player home map,
        /// otherwise worldTicks).
        /// </summary>
        public static int GetQuestDomainTicks(Quest quest)
        {
            var mapComp = TryGetCachedQuestMap(quest);
            if (mapComp != null)
                return mapComp.mapTicks;

            return Multiplayer.AsyncWorldTime?.worldTicks ?? Find.TickManager.TicksGame;
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
        /// Runs QuestTick() on all quests in the given domain cache using a static reusable buffer.
        /// Processes Ongoing, NotYetAccepted, and Historical quests to ensure offer expiry cleanup
        /// and historical quest transitions match vanilla QuestManager.QuestManagerTick behavior.
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
                if (quest != null)
                {
                    TmpQuestsToTick.Add(quest);
                }
            }

            for (int i = 0; i < TmpQuestsToTick.Count; i++)
            {
                var quest = TmpQuestsToTick[i];
                quest.QuestTick();

                if (quest.Historical && Find.QuestManager.activeQuests.Remove(quest))
                {
                    if (!Find.QuestManager.historicalQuests.Contains(quest))
                        Find.QuestManager.historicalQuests.Add(quest);
                }
            }

            TmpQuestsToTick.Clear();
        }

        /// <summary>
        /// Attempts to find the map the quest targets by checking active QuestGen slate variables,
        /// top-level and choice-nested QuestParts via cached FieldInfo reflection, and QuestLookTargets.
        /// </summary>
        /// <param name="quest">Quest to check Map Parts on</param>
        /// <returns>MapAsyncTimeComp for that quest or Null if not found</returns>
        private static AsyncTimeComp TryGetQuestMap(Quest quest)
        {
            // 1. If currently inside QuestGen.Generate for this quest, check the root slate's "map" variable first.
            // Many quests (Hospitality_Prisoners, Refugee, Beggars) resolve their target map into slate["map"]
            // while keeping reward/spawn parts inside unselected QuestPart_Choice branches until acceptance.
            if (QuestGen.Working && QuestGen.quest == quest &&
                QuestGen.slate.TryGet<Map>("map", out var slateMap) &&
                slateMap is { IsPlayerHome: true })
            {
                var slateMapComp = slateMap.AsyncTime();
                if (slateMapComp != null)
                    return slateMapComp;
            }

            var parts = quest.parts;
            if (parts == null) return null;

            // 2. Scan top-level quest parts and nested QuestPart_Choice branches for mapParent fields.
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null) continue;

                var comp = TryGetMapFromPart(part);
                if (comp != null)
                    return comp;

                if (part is QuestPart_Choice choicePart && choicePart.choices != null)
                {
                    for (int j = 0; j < choicePart.choices.Count; j++)
                    {
                        var choiceParts = choicePart.choices[j]?.questParts;
                        if (choiceParts == null) continue;

                        for (int k = 0; k < choiceParts.Count; k++)
                        {
                            comp = TryGetMapFromPart(choiceParts[k]);
                            if (comp != null)
                                return comp;
                        }
                    }
                }
            }

            // 3. Fallback: inspect any explicit player home map targets in QuestLookTargets.
            foreach (var target in quest.QuestLookTargets)
            {
                Map targetMap = null;
                if (target.HasWorldObject && target.WorldObject is MapParent { HasMap: true } mp)
                    targetMap = mp.Map;
                else if (target.HasThing && target.Thing != null)
                    targetMap = target.Thing.MapHeld;
                else if (target.Map != null)
                    targetMap = target.Map;

                if (targetMap is { IsPlayerHome: true } && targetMap.AsyncTime() is { } targetComp)
                    return targetComp;
            }

            return null;
        }

        private static AsyncTimeComp TryGetMapFromPart(QuestPart part)
        {
            if (part == null) return null;

            var partType = part.GetType();
            if (!MapParentFieldCache.TryGetValue(partType, out var field))
            {
                field = partType.GetField("mapParent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MapParentFieldCache[partType] = field;
            }

            if (field != null && field.FieldType == typeof(MapParent) &&
                field.GetValue(part) is MapParent { Map: { IsPlayerHome: true } map })
            {
                return map.AsyncTime();
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
