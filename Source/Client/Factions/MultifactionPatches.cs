using HarmonyLib;
using KTrie;
using Multiplayer.API;
using Multiplayer.Client.AsyncTime;
using Multiplayer.Client.Factions;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Remoting.Messaging;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;
using static HarmonyLib.AccessTools;

namespace Multiplayer.Client.Patches;

[HarmonyPatch(typeof(MainTabWindow_Quests), nameof(MainTabWindow_Quests.DoRow))]
public static class MainTabWindow_QuestsDoRowPatch
{
    public static void Prefix(ref Rect rect, Quest quest)
    {
        if (Multiplayer.Client != null && !Multiplayer.settings.hideOtherPlayersQuests)
        {
            if (quest.TryGetPlayerFaction(out Faction playerFaction))
            {
                Rect iconRect = new Rect(rect.x + 2f, rect.y + 2f, 4f, rect.height - 4f);
                Widgets.DrawBoxSolid(iconRect, playerFaction.Color);
                rect.xMin += 8f;
                TooltipHandler.TipRegion(rect, "MpQuestDesc".Translate(playerFaction.Name, playerFaction == Faction.OfPlayer ? ". (you)" : "."));
            }
            else
            {
                TooltipHandler.TipRegion(rect, "MpPublicQuest".Translate());
            }
        }
    }
}

/// <summary>
/// Filters quest entries in the Quests UI tab so players only see quests belonging to their faction when the filter is enabled.
/// </summary>
[HarmonyPatch(typeof(MainTabWindow_Quests), nameof(MainTabWindow_Quests.ShouldListNow))]
public static class MainTabWindow_QuestsShouldListNowPatch
{
    public static bool Prefix(MainTabWindow_Quests __instance, Quest quest, ref bool __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return true;

        if (!Multiplayer.settings.hideOtherPlayersQuests)
            return true;

        // Allow developers and admins to inspect all quests
        if (DebugSettings.godMode || __instance.showAll)
            return true;

        // Hide quests associated with an opposing player faction
        if (quest.TryGetPlayerFaction(out Faction questFaction))
        {
            var myFaction = Multiplayer.RealPlayerFaction;
            if (myFaction != null && questFaction != null && questFaction != myFaction)
            {
                __result = false;
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Controls whether a quest can be accepted by the current client in multifaction mode.
/// Prevents players from claiming quests explicitly designated for other player factions,
/// while allowing public and unassigned quests to be accepted freely.
/// </summary>
[HarmonyPatch(typeof(QuestUtility), nameof(QuestUtility.CanAcceptQuest))]
public static class QuestUtilityCanAcceptQuestPatch
{
    public static void Postfix(Quest quest, ref AcceptanceReport __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return;

        // God mode bypasses all faction acceptance restrictions
        if (DebugSettings.godMode)
            return;

        // If quest is already unacceptable due to vanilla requirements (e.g. tech level, missing title), preserve the reason
        if (!__result.Accepted)
            return;

        // Only enforce restriction if the quest is explicitly tied to a specific player faction
        if (quest.TryGetPlayerFaction(out Faction questFaction))
        {
            Faction myFaction = Multiplayer.RealPlayerFaction;
            if (myFaction != null && questFaction != null && questFaction != myFaction)
            {
                __result = new AcceptanceReport("MpCannotAcceptOtherPlayerQuest".Translate(questFaction.Name));
            }
        }
    }
}

/// <summary>
/// Prevents clients from dismissing or cancelling quests belonging to other player factions.
/// </summary>
[HarmonyPatch(typeof(MainTabWindow_Quests), nameof(MainTabWindow_Quests.DoDismissButton))]
public static class MainTabWindow_Quests_DoDismissButton_Patch
{
    public static bool Prefix(MainTabWindow_Quests __instance)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return true;

        if (DebugSettings.godMode)
            return true;

        var quest = __instance.selected;
        if (quest != null && quest.TryGetPlayerFaction(out Faction questFaction))
        {
            var myFaction = Multiplayer.RealPlayerFaction;
            if (myFaction != null && questFaction != null && questFaction != myFaction)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Provides network-synchronized methods for accepting quests in multiplayer sessions.
/// Handles pawn selection, reward choices, and faction alignment in multifaction mode.
/// </summary>
public static class DevQuestAcceptUtility
{
    /// <summary>
    /// Executes synchronized quest acceptance across all clients.
    /// Selects default reward choice if available and resolves an appropriate accepting pawn for the player's faction.
    /// </summary>
    /// <param name="quest">The quest to accept.</param>
    /// <param name="selectedAccepter">Specific pawn chosen to accept the quest (e.g. for royal favor), or null for automatic resolution.</param>
    [SyncMethod]
    public static void SyncedAcceptQuest(Quest quest, Pawn selectedAccepter)
    {
        if (quest == null || quest.State != QuestState.NotYetAccepted)
            return;

        // Ensure that if the quest has choice parts, at least the primary choice is marked selected
        var choicePart = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
        if (choicePart != null && choicePart.choices.Count > 0 && !choicePart.choiceUsed)
        {
            choicePart.Choose(choicePart.choices[0]);
        }

        // Determine which faction is accepting the quest
        Faction targetFaction = null;
        if (quest.TryGetPlayerFaction(out Faction qf))
        {
            targetFaction = qf;
        }
        else
        {
            targetFaction = Multiplayer.RealPlayerFaction ?? Faction.OfPlayer;
        }

        Pawn accepter = selectedAccepter;

        // If no valid accepter was passed from UI, find the first valid pawn belonging to the accepting faction
        if (accepter == null || accepter.Faction != targetFaction || !QuestUtility.CanPawnAcceptQuest(accepter, quest))
        {
            accepter = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive
                .FirstOrDefault(p => p.Faction == targetFaction && QuestUtility.CanPawnAcceptQuest(p, quest));
        }

        // Fallback: search any player faction pawn if no specific faction pawn could accept
        accepter ??= PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive
                .FirstOrDefault(p => p.Faction is { IsPlayer: true } && QuestUtility.CanPawnAcceptQuest(p, quest));

        quest.Accept(accepter);
        quest.dismissed = false;
    }

    /// <summary>
    /// Legacy compatibility hook for instant DevMode quest acceptance.
    /// </summary>
    [SyncMethod]
    public static void SyncedDevAccept(Quest quest)
    {
        SyncedAcceptQuest(quest, null);
    }
}

[HarmonyPatch(typeof(WorldObject), nameof(WorldObject.ShowRelatedQuests), MethodType.Getter)]
static class WorldObjectShowRelatedQuestsPatch
{
    static void Postfix(ref bool __result)
    {
        if (Multiplayer.Client != null && Multiplayer.RealPlayerFaction == Multiplayer.WorldComp.spectatorFaction)
            __result = false;
    }
}

[HarmonyPatch(typeof(Settlement), nameof(Settlement.ShowRelatedQuests), MethodType.Getter)]
static class SettlementShowRelatedQuestsPatch
{
    static void Postfix(ref bool __result)
    {
        if (Multiplayer.Client != null && Multiplayer.RealPlayerFaction == Multiplayer.WorldComp.spectatorFaction)
            __result = false;
    }
}

[HarmonyPatch(typeof(WorldInspectPane), nameof(WorldInspectPane.PaneTopY), MethodType.Getter)]
static class WorldInspectPanePaneTopYPatch
{
    static void Postfix(ref float __result)
    {
        if (Multiplayer.Client != null && Multiplayer.RealPlayerFaction == Multiplayer.WorldComp.spectatorFaction)
            __result += 35f;
    }
}

[HarmonyPatch(typeof(MainTabWindow_Quests), nameof(MainTabWindow_Quests.DoRewardsPrefsButton))]
public static class MainTabWindow_QuestsDoRewardsPrefsButtonPatch
{
    public static bool Prefix(MainTabWindow_Quests __instance, Rect rect, out Rect __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
        {
            __result = rect;
            return true;
        }
        rect.yMin = rect.yMax - 40f;

        float buttonHeight = 40f;

        float rewardButtonWidth = rect.width * 0.5f - 5f;
        float toggleButtonWidth = rect.width - rewardButtonWidth - 10f;

        Rect rewardRect = new Rect(rect.x, rect.yMax - buttonHeight, rewardButtonWidth, buttonHeight);
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(rewardRect, "ChooseRewards".Translate(), true, true, true))
        {
            Find.WindowStack.Add(new Dialog_RewardPrefsConfig());
        }

        Rect toggleRect = new Rect(rewardRect.xMax + 10f, rewardRect.y, toggleButtonWidth, buttonHeight);
        bool value = Multiplayer.settings.hideOtherPlayersQuests;
        string label = value ? "MpHideQuestsOn".Translate() : "MpHideQuestsOff".Translate();
        if (Widgets.ButtonText(toggleRect, label, true, true))
        {
            Multiplayer.settings.hideOtherPlayersQuests = !value;
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }
        TooltipHandler.TipRegion(toggleRect, "MpHideQuestsDesc".Translate());

        __result = rect;
        return false;
    }
}

/// <summary>
/// Replaces Find.Maps in ColonistBar recaching with a zero-allocation map filter.
/// </summary>
[HarmonyPatch(typeof(ColonistBar), nameof(ColonistBar.CheckRecacheEntries))]
public static class ColonistBarCheckRecacheEntriesPatch
{
    private static readonly MethodInfo Maps = AccessTools.PropertyGetter(typeof(Find), nameof(Find.Maps));
    private static readonly MethodInfo FilteredMaps = AccessTools.Method(typeof(ColonistBarCheckRecacheEntriesPatch), nameof(GetFilteredMaps));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(Maps))
                instruction.operand = FilteredMaps;

            yield return instruction;
        }
    }

    private static readonly List<Map> tmpFilteredMaps = new(8);

    /// <summary>
    /// Returns only maps containing active colonists or owned by the player, using reusable buffers instead of LINQ Where.
    /// </summary>
    public static IEnumerable<Map> GetFilteredMaps()
    {
        if (Multiplayer.Client == null || !Multiplayer.settings.hideOtherPlayersInColonistBar)
            return Find.Maps;

        tmpFilteredMaps.Clear();
        var maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
        {
            var map = maps[i];
            if (map.mapPawns.FreeColonistsSpawned.Count > 0 || map.Parent?.Faction == Faction.OfPlayer)
            {
                tmpFilteredMaps.Add(map);
            }
        }

        return tmpFilteredMaps;
    }
}

[HarmonyPatch(typeof(WorldRoutePlanner), nameof(WorldRoutePlanner.DoRoutePlannerButton))]
public static class WorldRoutePlannerPatch
{
    static bool Prefix()
    {
        return Multiplayer.Client == null || Multiplayer.RealPlayerFaction != Multiplayer.WorldComp.spectatorFaction;
    }
}

[HarmonyPatch(typeof(WorldGizmoUtility), nameof(WorldGizmoUtility.TryGetCaravanGizmo))]
public static class WorldGizmoUtilityTryGetCaravanGizmoPatch
{
    static bool Prefix(ref Gizmo gizmo, ref bool __result)
    {
        if (Multiplayer.Client != null && Multiplayer.RealPlayerFaction == Multiplayer.WorldComp.spectatorFaction)
        {
            gizmo = null;
            __result = false;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(MainButtonsRoot), nameof(MainButtonsRoot.DoButtons))]
static class MainButtonsRootDoButtonsPatch
{
    private static bool IsSpectator =>
      Multiplayer.Client != null &&
      Multiplayer.RealPlayerFaction == Multiplayer.WorldComp.spectatorFaction;

    private static readonly string[] SpectatorButtons = { "Menu", "World" };

    private static readonly FieldRef<MainButtonsRoot, List<MainButtonDef>> AllButtonsRef = AccessTools.FieldRefAccess<MainButtonsRoot, List<MainButtonDef>>("allButtonsInOrder");

    private static readonly Dictionary<string, int> SpectatorButtonOrder = SpectatorButtons.Select((name, idx) => new { name, idx }).ToDictionary(x => x.name, x => x.idx);

    [HarmonyPrefix]
    private static bool ReplaceOriginalDrawing(MainButtonsRoot __instance)
    {
        if (!IsSpectator)
            return true;

        try
        {
            var allButtons = AllButtonsRef(__instance);
            if (allButtons == null)
                return true;

            var toDraw = SpectatorButtons
                .Select(name => allButtons.Find(b => b.defName == name))
                .Where(b => b?.Worker.Visible ?? false)
                .ToList();

            DrawCornerButtons(toDraw);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[MainButtonsRootPatch] ReplaceOriginalDrawing failed: {ex}");
            return true;
        }
    }

    [HarmonyPostfix]
    private static void FilterButtons(MainButtonsRoot __instance)
    {
        try
        {
            var allButtons = AllButtonsRef(__instance);
            if (allButtons == null)
                return;

            if (IsSpectator)
            {
                // Keep only SpectatorButtons, sorted by our map
                var filtered = allButtons
                    .Where(b => SpectatorButtonOrder.ContainsKey(b.defName))
                    .OrderBy(b => SpectatorButtonOrder[b.defName])
                    .ToList();

                AllButtonsRef(__instance) = filtered;
            }
            else
            {
                // Restore original full list in normal mode
                var original = DefDatabase<MainButtonDef>.AllDefsListForReading
                    .Where(b => b.order >= 0)
                    .OrderBy(b => b.order)
                    .ToList();

                AllButtonsRef(__instance) = original;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[MainButtonsRootPatch] FilterButtons failed: {ex}");
        }
    }

    private static void DrawCornerButtons(List<MainButtonDef> buttons)
    {
        const float buttonHeight = 35f;
        const float defaultPadding = 20f;
        const float spacing = 0f;

        float x = UI.screenWidth;
        float y = UI.screenHeight - buttonHeight;

        foreach (var btn in buttons)
        {
            float padding = btn.defName == "World" ? 120f : defaultPadding;
            float width = btn.ShortenedLabelCapWidth + padding;

            x -= width;
            btn.Worker.DoButton(new Rect(x, y, width, buttonHeight));
            x -= spacing;
        }
    }
}


[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.GetGizmos))]
static class DisableDraftGizmo
{
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn_DraftController __instance)
    {
        return __instance.pawn.Faction == Faction.OfPlayer ? gizmos : [];
    }
}

/// <summary>
/// Injects a relation change gizmo on pawns belonging to other player factions.
/// Lazily instantiates and caches the Command_Action on the main UI thread to prevent premature static class initialization during background sync registration.
/// </summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
static class PawnChangeRelationGizmo
{
    private static Command_Action cachedCommand;

    /// <summary>
    /// Opens the relation change float menu for the currently inspected other player faction pawn.
    /// </summary>
    private static void OpenChangeRelationMenu()
    {
        if (Find.Selector.SingleSelectedThing is not Pawn pawn)
            return;

        var otherFaction = pawn.Faction;
        if (otherFaction == null || !otherFaction.IsPlayer || otherFaction == Faction.OfPlayer)
            return;

        List<FloatMenuOption> list = new List<FloatMenuOption>(2)
        {
            new(FactionRelationKind.Hostile.ToString(), () =>
            {
                SetRelation(otherFaction, FactionRelationKind.Hostile);
            }),
            new(FactionRelationKind.Neutral.ToString(), () =>
            {
                SetRelation(otherFaction, FactionRelationKind.Neutral);
            })
        };

        Find.WindowStack.Add(new FloatMenu(list));
    }

    /// <summary>
    /// Postfix yields existing gizmos and appends the cached relation command when inspecting another player's colonist.
    /// Defers command allocation to ensure MultiplayerStatic and ContentFinder are accessed only on the main thread after defs load.
    /// </summary>
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
    {
        foreach (var gizmo in gizmos)
            yield return gizmo;

        if (Multiplayer.Client == null || Multiplayer.RealPlayerFaction == Multiplayer.WorldComp.spectatorFaction)
            yield break;

        if (__instance.Faction is { IsPlayer: true } && __instance.Faction != Faction.OfPlayer)
        {
            cachedCommand ??= new Command_Action
            {
                defaultLabel = "Change faction relation",
                icon = MultiplayerStatic.ChangeRelationIcon,
                action = OpenChangeRelationMenu
            };

            yield return cachedCommand;
        }
    }

    [SyncMethod]
    static void SetRelation(Faction other, FactionRelationKind kind)
    {
        Faction.OfPlayer.SetRelation(new FactionRelation(other, kind));

        foreach (Map map in Find.Maps)
            map.attackTargetsCache.Notify_FactionHostilityChanged(Faction.OfPlayer, other);
    }
}

[HarmonyPatch(typeof(SettlementDefeatUtility), nameof(SettlementDefeatUtility.CheckDefeated))]
static class CheckDefeatedPatch
{
    static bool Prefix(Settlement factionBase)
    {
        return factionBase.Faction is not { IsPlayer: true };
    }
}

[HarmonyPatch(typeof(MapParent), nameof(MapParent.CheckRemoveMapNow))]
static class CheckRemoveMapNowPatch
{
    static bool Prefix(MapParent __instance)
    {
        if (Multiplayer.Client == null) return true;
        if (__instance is Camp) return true;

        bool parentBelongsToNonPlayerFaction = __instance.Faction is not { IsPlayer: true };

        return parentBelongsToNonPlayerFaction;
    }
}

[HarmonyPatch(typeof(Settlement), nameof(Settlement.Attackable), MethodType.Getter)]
static class SettlementAttackablePatch
{
    static bool Prefix(Settlement __instance) => __instance.Faction is not { IsPlayer: true };
}

[HarmonyPatch(typeof(Settlement), nameof(Settlement.Material), MethodType.Getter)]
static class SettlementNullFactionPatch1
{
    static bool Prefix(Settlement __instance, ref Material __result)
    {
        if (__instance.factionInt == null)
        {
            __result = BaseContent.BadMat;
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(Settlement), nameof(Settlement.ExpandingIcon), MethodType.Getter)]
static class SettlementNullFactionPatch2
{
    static bool Prefix(Settlement __instance, ref Texture2D __result)
    {
        if (__instance.factionInt == null)
        {
            __result = BaseContent.BadTex;
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(GoodwillSituationManager), nameof(GoodwillSituationManager.GetNaturalGoodwill))]
static class GetNaturalGoodwillPatch
{
    static bool Prefix(Faction other)
    {
        return other is not { IsPlayer: true };
    }
}

[HarmonyPatch(typeof(GoodwillSituationManager), nameof(GoodwillSituationManager.GetMaxGoodwill))]
static class GetMaxGoodwillPatch
{
    static bool Prefix(Faction other)
    {
        return other is not { IsPlayer: true };
    }
}

[HarmonyPatch(typeof(ScenPart_StartingAnimal), nameof(ScenPart_StartingAnimal.PetWeight))]
static class StartingAnimalPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        var playerFactionField = AccessTools.Field(typeof(GameInitData), nameof(GameInitData.playerFaction));
        var factionOfPlayer = FactionOfPlayer;

        foreach (var inst in insts)
        {
            yield return inst;

            if (inst.operand as FieldInfo == playerFactionField)
                yield return new CodeInstruction(OpCodes.Call, factionOfPlayer.Method);
        }
    }

    static Faction FactionOfPlayer(Faction f)
    {
        return Faction.OfPlayer;
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.IsColonist), MethodType.Getter)]
static class PawnIsColonistPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        var isPlayerMethodGetter = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.IsPlayer));
        var factionIsPlayer = FactionIsPlayer;

        foreach (var inst in insts)
        {
            if (inst.operand as MethodInfo == isPlayerMethodGetter)
                inst.operand = factionIsPlayer.Method;

            yield return inst;
        }
    }

    /// <summary>
    /// High-performance colonist check. Evaluated tens of thousands of times per second at 4x speed.
    /// Fast-paths simulation contexts before evaluating player faction properties.
    /// </summary>
    static bool FactionIsPlayer(Faction f)
    {
        if (f == null) return false;

        // In simulation contexts (ticking maps, executing commands, sim long events), all player colonies are valid colonists
        if (AsyncTimeComp.tickingMap != null || AsyncWorldTimeComp.tickingWorld ||
            TickPatch.currentExecutingCmdType != null || Patches.SimLongEventContext.InSimulation)
        {
            return f.IsPlayer;
        }

        // Outside simulation (UI render/inspection loops), restrict to the local player's faction
        Faction myFaction = Multiplayer.RealPlayerFaction ?? Find.FactionManager.OfPlayer;
        return f == myFaction;
    }
}

[HarmonyPatch(typeof(Ideo), nameof(Ideo.RecacheColonistBelieverCount))]
static class RecacheColonistBelieverCountPatch
{
    private static MethodInfo allColonists = AccessTools.PropertyGetter(typeof(PawnsFinder), nameof(PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoCryptosleep));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        foreach (var inst in insts)
        {
            if (inst.operand as MethodInfo == allColonists)
                inst.operand = AccessTools.Method(typeof(RecacheColonistBelieverCountPatch), nameof(ColonistsAllPlayerFactions));
            yield return inst;
        }
    }

    private static List<Pawn> colonistsAllFactions = new();

    private static List<Pawn> ColonistsAllPlayerFactions()
    {
        colonistsAllFactions.Clear();

        foreach (var p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive)
        {
            if (IsColonistAnyFaction(p) && p.HostFaction == null && !p.InCryptosleep)
                colonistsAllFactions.Add(p);
        }

        return colonistsAllFactions;
    }

    // Note: IsColonist is patched above to compare to Faction.OfPlayer
    public static bool IsColonistAnyFaction(Pawn p)
    {
        if (p.Faction is { IsPlayer: true } && p.RaceProps.Humanlike)
            return !p.IsSlave || p.guest.SlaveIsSecure;
        return false;
    }

    public static bool IsColonyMechAnyFaction(Pawn p)
    {
        if (ModsConfig.BiotechActive && p.RaceProps.IsMechanoid && p.Faction is { IsPlayer: true } && p.MentalStateDef == null)
            return p.HostFaction == null || p.IsSlave;
        return false;
    }

    public static bool IsColonySubhumanAnyFaction(Pawn p)
    {
        return p.IsSubhuman && p.Faction is { IsPlayer: true };
    }
}

[HarmonyPatch(typeof(MapPawns), nameof(MapPawns.AnyPawnBlockingMapRemoval), MethodType.Getter)]
static class AnyPawnBlockingMapRemovalPatch
{
    private static MethodInfo isColonist = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsColonist));
    private static MethodInfo isColonyMech = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsColonyMech));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        foreach (var inst in insts)
        {
            if (inst.operand as MethodInfo == isColonyMech)
                inst.operand = AccessTools.Method(typeof(RecacheColonistBelieverCountPatch), nameof(RecacheColonistBelieverCountPatch.IsColonyMechAnyFaction));

            yield return inst;
        }
    }

    static void Postfix(MapPawns __instance, ref bool __result)
    {
        for (int i = 0; i < __instance.pawnsSpawned.Count; i++)
        {
            var p = __instance.pawnsSpawned[i];
            if (p.Faction is { IsPlayer: true } || p.HostFaction is { IsPlayer: true })
            {
                Job curJob = p.CurJob;
                if (curJob is { exitMapOnArrival: true })
                {
                    __result = true;
                    break;
                }

                if (p.health.hediffSet.InLabor())
                {
                    __result = true;
                    break;
                }
            }
        }
    }
}

[HarmonyPatch(typeof(MapPawns), nameof(MapPawns.IsValidColonyPawn))]
static class IsValidColonyPawnPatch
{
    private static MethodInfo isColonist = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsColonist));
    private static MethodInfo isColonySubhuman = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsColonySubhuman));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        foreach (var inst in insts)
        {
            if (inst.operand as MethodInfo == isColonist)
                inst.operand = AccessTools.Method(typeof(RecacheColonistBelieverCountPatch), nameof(RecacheColonistBelieverCountPatch.IsColonistAnyFaction));

            if (inst.operand as MethodInfo == isColonySubhuman)
                inst.operand = AccessTools.Method(typeof(RecacheColonistBelieverCountPatch), nameof(RecacheColonistBelieverCountPatch.IsColonySubhumanAnyFaction));

            yield return inst;
        }
    }
}

[HarmonyPatch(typeof(Precept_Role), nameof(Precept_Role.ValidatePawn))]
static class ValidatePawnPatch
{
    private static MethodInfo isFreeNonSlaveColonist = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsFreeNonSlaveColonist));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        foreach (var inst in insts)
        {
            if (inst.operand as MethodInfo == isFreeNonSlaveColonist)
                inst.operand = AccessTools.Method(typeof(ValidatePawnPatch), nameof(IsFreeNonSlaveColonistAnyFaction));

            yield return inst;
        }
    }

    public static bool IsFreeNonSlaveColonistAnyFaction(Pawn p)
    {
        return p.Faction is { IsPlayer: true } && p.RaceProps.Humanlike && p.HostFaction == null && !p.IsSlave;
    }
}

[HarmonyPatch(typeof(Faction), nameof(Faction.HasGoodwill), MethodType.Getter)]
static class PlayerFactionsHaveGoodwill
{
    static void Postfix(Faction __instance, ref bool __result)
    {
        if (__instance.IsPlayer)
            __result = true;
    }
}

[HarmonyPatch(typeof(GenHostility), nameof(GenHostility.IsActiveThreatToPlayer))]
static class IsActiveThreatToAnyPlayer
{
    static void Postfix(IAttackTarget target, ref bool __result)
    {
        foreach (var f in Find.FactionManager.AllFactions)
            if (f.IsPlayer)
                __result |= GenHostility.IsActiveThreatTo(target, f);
    }
}

[HarmonyPatch]
static class TakeToBedGuestFactionPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(JobDriver_TakeToBed), nameof(JobDriver_TakeToBed.CheckMakeTakeeGuest));
        yield return AccessTools.DeclaredMethod(typeof(JobDriver_CarryDownedPawn), nameof(JobDriver_CarryDownedPawn.CheckMakeTakeeGuest));
    }
    static bool Prefix(JobDriver __instance)
    {
        var takee = __instance.job.GetTarget(TargetIndex.A).Pawn;
        return takee?.Faction?.IsPlayer != true && takee?.HostFaction?.IsPlayer != true;
    }
}


/// <summary>
/// Comprehensive security and routing utility for evaluating faction ownership of Things, Pawns, Prisoners, Corpses, and Map-level events in Multifaction mode.
/// Contains temporal tile-to-faction caching to eliminate repeated linear scans over Maps and World Settlements during UI render loops.
/// </summary>
public static class LetterSecurityUtility
{
    private static readonly Dictionary<int, Faction> TileFactionCache = new(64);
    private static int lastTileCacheTick = -1;

    /// <summary>
    /// Resolves the player faction associated with a specific planet tile.
    /// Memoizes lookups per simulation tick to eliminate O(Maps + Settlements) traversal on UI loops.
    /// </summary>
    /// <param name="tile">Target world tile.</param>
    /// <returns>Player faction owning the map or settlement at the tile, or null if none.</returns>
    public static Faction GetPlayerFactionAtTile(PlanetTile tile)
    {
        int currentTick = TickPatch.Timer;
        if (lastTileCacheTick != currentTick)
        {
            TileFactionCache.Clear();
            lastTileCacheTick = currentTick;
        }

        int tileId = tile.tileId;
        if (TileFactionCache.TryGetValue(tileId, out var cached))
            return cached;

        Faction result = null;
        var maps = Find.Maps;
        if (maps != null)
        {
            for (int m = 0; m < maps.Count; m++)
            {
                var map = maps[m];
                if (map.Tile == tile && map.ParentFaction is { IsPlayer: true } mapFac)
                {
                    result = mapFac;
                    break;
                }
            }
        }

        if (result == null && Find.WorldObjects?.Settlements != null)
        {
            var settlements = Find.WorldObjects.Settlements;
            for (int s = 0; s < settlements.Count; s++)
            {
                var settlement = settlements[s];
                if (settlement.Tile == tile && settlement.Faction is { IsPlayer: true } setFac)
                {
                    result = setFac;
                    break;
                }
            }
        }

        TileFactionCache[tileId] = result;
        return result;
    }

    /// <summary>
    /// Resolves the owning player faction for a single GlobalTargetInfo instance.
    /// Evaluates prisoners, colonists, corpses, world objects, maps, and world tiles in strict priority order.
    /// </summary>
    /// <param name="target">The target to inspect.</param>
    /// <returns>The controlling player faction, or null if none or non-player.</returns>
    public static Faction GetTargetPlayerFaction(GlobalTargetInfo target)
    {
        if (target.HasThing && target.Thing != null)
        {
            var thing = target.Thing;

            // 1. Prisoner check takes highest precedence:
            // A prisoner's Faction is typically hostile/NPC, but HostFaction represents the capturing player faction.
            // Notifications (diseases, malnutrition, prison breaks) must route exclusively to the captor.
            if (thing is Pawn pawn && pawn.HostFaction is { IsPlayer: true } hostFac)
                return hostFac;

            // 2. Direct player pawn or constructed player building ownership.
            if (thing.Faction is { IsPlayer: true } tf)
                return tf;

            // 3. Corpse check: evaluate inner pawn to ensure dead colonists and dead prisoners remain properly attributed.
            if (thing is Corpse corpse && corpse.InnerPawn != null)
            {
                if (corpse.InnerPawn.HostFaction is { IsPlayer: true } chf)
                    return chf;
                if (corpse.InnerPawn.Faction is { IsPlayer: true } ctf)
                    return ctf;
            }

            // 4. Physical map placement: items on the ground, ambient terrain fires, or unowned structures belong to the map owner.
            if (thing.Map?.ParentFaction is { IsPlayer: true } mf)
                return mf;
            if (thing.MapHeld?.ParentFaction is { IsPlayer: true } mhf)
                return mhf;

            // 5. Despawned tile tracking via memoized cache (e.g. items dropped on world map or travelling pods).
            if (thing.Tile.Valid)
                return GetPlayerFactionAtTile(thing.Tile);
        }
        else if (target.HasWorldObject && target.WorldObject != null)
        {
            var worldObj = target.WorldObject;

            // Direct world object faction (e.g. player Caravan, player Settlement).
            if (worldObj.Faction is { IsPlayer: true } wf)
                return wf;

            // World map parent having an active player-owned map.
            if (worldObj is MapParent { HasMap: true } mp && mp.Map.ParentFaction is { IsPlayer: true } mpf)
                return mpf;
        }
        else if (target.Map?.ParentFaction is { IsPlayer: true } cellMapFac)
        {
            // Direct cell target pointing onto a player-owned map.
            return cellMapFac;
        }
        else if (target.Tile.Valid)
        {
            // Direct world tile target.
            return GetPlayerFactionAtTile(target.Tile);
        }

        return null;
    }

    /// <summary>
    /// Extracts the first deterministic owning player faction from a set of LookTargets.
    /// Used by letter recording to attribute a newly received letter to a specific faction bucket.
    /// </summary>
    /// <param name="lookTargets">LookTargets collection from the incoming Letter or Message.</param>
    /// <returns>First player faction resolved, or null if unassigned/public.</returns>
    public static Faction ResolveOwnerPlayerFaction(LookTargets lookTargets)
    {
        if (lookTargets?.targets == null)
            return null;

        var targets = lookTargets.targets;
        for (int i = 0; i < targets.Count; i++)
        {
            if (GetTargetPlayerFaction(targets[i]) is { } fac)
                return fac;
        }

        return null;
    }

    /// <summary>
    /// Resolves target affiliation for onscreen messages and audio alerts.
    /// Ensures events referencing another player's colonists, bases, or prisoners do not display on this client.
    /// </summary>
    /// <param name="lookTargets">Target references associated with the notification.</param>
    /// <param name="faction">Faction against which visibility is evaluated (usually Multiplayer.RealPlayerFaction).</param>
    /// <returns>True if the target belongs to or is relevant to the specified faction; otherwise, false.</returns>
    public static bool IsTargetForFaction(LookTargets lookTargets, Faction faction)
    {
        if (faction == null) return true;
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return true;

        // Fallback for map-level condition alerts without explicit targets (e.g. Eclipse, Flashstorm, Toxic Fallout)
        if (lookTargets?.targets == null || lookTargets.targets.Count == 0)
        {
            var mapContext = Multiplayer.MapContext;
            if (mapContext?.ParentFaction is { IsPlayer: true } mapFaction)
                return mapFaction == faction;

            return true;
        }

        bool hasAnyPlayerAffiliation = false;
        bool hasAnyThingTarget = false;
        var targets = lookTargets.targets;

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (target.HasThing && target.Thing != null)
                hasAnyThingTarget = true;

            Faction targetFaction = GetTargetPlayerFaction(target);
            if (targetFaction != null)
            {
                hasAnyPlayerAffiliation = true;
                if (targetFaction == faction)
                    return true;
            }
        }

        // If targets are concrete Things (e.g. wild animal on another map) and neither belongs to the local player, suppress it
        if (hasAnyThingTarget && !hasAnyPlayerAffiliation)
            return false;

        return !hasAnyPlayerAffiliation;
    }

    /// <summary>
    /// Evaluates letter visibility using quest context, targets (with prisoner/corpse resolution), and MapContext fallback.
    /// </summary>
    /// <param name="let">Letter instance to validate.</param>
    /// <param name="faction">Faction against which visibility is evaluated.</param>
    /// <returns>True if the letter is allowed to be displayed to the specified faction.</returns>
    public static bool IsLetterForFaction(Letter let, Faction faction)
    {
        if (faction == null) return true;
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return true;
        if (let == null) return false;

        // 1. Quests have explicit player faction bounds
        if (let is ChoiceLetter choiceLetter && choiceLetter.quest != null)
        {
            if (choiceLetter.quest.TryGetPlayerFaction(out Faction questFaction))
                return questFaction == faction;
        }

        // 2. Concrete targets override generic relatedFaction fields
        if (let.lookTargets?.targets != null && let.lookTargets.targets.Count > 0)
            return IsTargetForFaction(let.lookTargets, faction);

        // 3. Direct faction association
        if (let.relatedFaction is { IsPlayer: true } relatedFac)
            return relatedFac == faction;

        // 4. Map context fallback for weather and ambient incidents
        var mapContext = Multiplayer.MapContext;
        if (mapContext?.ParentFaction is { IsPlayer: true } mapFaction)
            return mapFaction == faction;

        return true;
    }
}

/// <summary>
/// Filters onscreen message display so notifications regarding other players' colonists or bases remain hidden.
/// Caches the filtered sequence per GUI frame to eliminate repeated O(N^2) filtering passes inside MessagesDoGUI loops.
/// </summary>
[HarmonyPatch(typeof(Messages), nameof(Messages.MessagesDoGUI))]
static class MessagesDoGUIFilterPatch
{
    private static readonly FieldInfo LiveMessagesField = AccessTools.Field(typeof(Messages), "liveMessages");
    private static readonly MethodInfo FilteredLiveMessagesMethod = AccessTools.Method(typeof(MessagesDoGUIFilterPatch), nameof(GetFilteredLiveMessages));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var ci in instructions)
        {
            if (ci.LoadsField(LiveMessagesField))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = FilteredLiveMessagesMethod;
            }
            yield return ci;
        }
    }

    private static readonly List<Message> tmpFilteredMessages = new(16);
    private static int lastEvaluatedFrame = -1;
    private static int lastEvaluatedCount = -1;

    /// <summary>
    /// Returns the live messages filtered by the local player's faction visibility.
    /// Uses frame-level and source-count memoization to prevent redundant evaluations on every loop iteration in MessagesDoGUI.
    /// </summary>
    public static List<Message> GetFilteredLiveMessages()
    {
        var original = Messages.liveMessages;
        if (Multiplayer.Client == null || Multiplayer.game == null || !Multiplayer.GameComp.multifaction)
            return original;

        int curFrame = Time.frameCount;
        int origCount = original.Count;

        // Return cached buffer if MessagesDoGUI is evaluating consecutive instructions within the same UI frame
        if (lastEvaluatedFrame == curFrame && lastEvaluatedCount == origCount)
            return tmpFilteredMessages;

        tmpFilteredMessages.Clear();
        var myFaction = Multiplayer.RealPlayerFaction;

        for (int i = 0; i < origCount; i++)
        {
            var msg = original[i];
            if (LetterSecurityUtility.IsTargetForFaction(msg.lookTargets, myFaction))
            {
                tmpFilteredMessages.Add(msg);
            }
        }

        lastEvaluatedFrame = curFrame;
        lastEvaluatedCount = origCount;
        return tmpFilteredMessages;
    }
}

/// <summary>
/// Attaches active MapContext or Faction context to targetless messages (e.g. GameCondition Eclipse start/end)
/// when they are created during simulation ticks, and suppresses audio cues for other factions.
/// </summary>
[HarmonyPatch(typeof(Messages), nameof(Messages.Message), typeof(Message), typeof(bool))]
static class MessagesSoundFilterPatch
{
    private static readonly MethodInfo PlayOneShotOnCamera =
        AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShotOnCamera), new[] { typeof(SoundDef), typeof(Map) });
    private static readonly MethodInfo ReplacementMethod =
        AccessTools.Method(typeof(MessagesSoundFilterPatch), nameof(PlayMessageSoundReplacement));

    static void Prefix(Message msg)
    {
        if (Multiplayer.Client == null || Multiplayer.game == null || !Multiplayer.GameComp.multifaction || msg == null)
            return;

        if (msg.lookTargets?.targets == null || msg.lookTargets.targets.Count == 0)
        {
            if (Multiplayer.MapContext is { } map)
            {
                if (map.Parent != null && map.Parent.Spawned)
                    msg.lookTargets = new LookTargets(map.Parent);
                else if (map.Tile.Valid)
                    msg.lookTargets = new LookTargets(map.Tile);
            }
        }
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        foreach (var inst in insts)
        {
            if (inst.Calls(PlayOneShotOnCamera))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0); // Pass Message argument
                yield return new CodeInstruction(OpCodes.Call, ReplacementMethod);
            }
            else
            {
                yield return inst;
            }
        }
    }

    static void PlayMessageSoundReplacement(SoundDef sound, Map map, Message msg)
    {
        if (sound == null) return;
        if (Multiplayer.Client == null || Multiplayer.game == null || !Multiplayer.GameComp.multifaction)
        {
            sound.PlayOneShotOnCamera(map);
            return;
        }

        if (LetterSecurityUtility.IsTargetForFaction(msg.lookTargets, Multiplayer.RealPlayerFaction))
        {
            sound.PlayOneShotOnCamera(map);
        }
    }
}

[HarmonyPatch(typeof(Apparel), nameof(Apparel.WornGraphicPath), MethodType.Getter)]
static class ApparelWornGraphicPathGetterPatch
{
    private static FieldInfo thingIDNumberField = AccessTools.Field(typeof(Thing), nameof(Thing.thingIDNumber));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        foreach (var inst in insts)
        {
            yield return inst;

            // This instruction is part of wornGraphicPaths[thingIDNumber % wornGraphicPaths.Count]
            // The function makes sure the id is positive
            if (inst.operand as FieldInfo == thingIDNumberField)
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(ApparelWornGraphicPathGetterPatch), nameof(MakeIdPositive)));
        }
    }

    private static int MakeIdPositive(int id)
    {
        return id < 0 ? -id : id;
    }
}

#region Map generation

[HarmonyPatch(typeof(GenStep_Monolith), nameof(GenStep_Monolith.GenerateMonolith))]
public static class GenStepMonolithPatch
{
    static bool Prefix()
    {
        if (Multiplayer.Client == null)
            return true;

        var anomalyComp = Current.Game.components.OfType<GameComponent_Anomaly>().FirstOrDefault();

        return anomalyComp?.monolith == null;
    }
}

[HarmonyPatch(typeof(ScenPart_ScatterThings), nameof(ScenPart_ScatterThings.GenerateIntoMap))]
static class ScenPartScatterThingsPatch
{
    static bool Prefix()
    {
        return Multiplayer.Client == null || FactionCreator.generatingMap;
    }
}

[HarmonyPatch(typeof(ScenPart_PlayerPawnsArriveMethod), nameof(ScenPart_PlayerPawnsArriveMethod.GenerateIntoMap))]
static class ScenPartPlayerPawnsArriveMethodPatch
{
    static bool Prefix()
    {
        return Multiplayer.Client == null || FactionCreator.generatingMap;
    }
}

#endregion

[HarmonyPatch(typeof(CharacterCardUtility), nameof(CharacterCardUtility.DoTopStack))]
static class CharacterCardUtilityDontDrawIdeoPlate
{
    private static FieldInfo classicModeField = AccessTools.Field(typeof(IdeoManager), nameof(IdeoManager.classicMode));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        foreach (var inst in insts)
        {
            yield return inst;

            // Don't draw the ideo plate while choosing starting pawns in multifaction
            if (inst.operand as FieldInfo == classicModeField)
            {
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(CharacterCardUtilityDontDrawIdeoPlate), nameof(DontDrawIdeoPlate)));
                yield return new CodeInstruction(OpCodes.Or);
            }
        }
    }

    private static bool DontDrawIdeoPlate(bool generating)
    {
        return Multiplayer.Client != null && generating;
    }
}

[HarmonyPatch(typeof(CompShuttle), nameof(CompShuttle.ContainedColonistCount), MethodType.Getter)]
static class CompShuttle_ContainedColonistCount_Patch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        var isFreeColonist = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsFreeColonist));
        var replacement = AccessTools.Method(typeof(CompShuttle_ContainedColonistCount_Patch),
            nameof(IsFreeColonistAnyPlayerFaction));

        foreach (var ci in insts)
        {
            if (ci.Calls(isFreeColonist))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = replacement;
            }
            yield return ci;
        }
    }
    public static bool IsFreeColonistAnyPlayerFaction(Pawn pawn)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return pawn.IsFreeColonist;

        return pawn.Faction != null &&
               pawn.Faction.IsPlayer &&
               pawn.RaceProps.Humanlike &&
               (!pawn.IsSlave || pawn.guest.SlaveIsSecure) &&
               !pawn.IsSubhuman &&
               pawn.HostFaction == null;
    }

}

// The pushes are forced: in a world command Faction.OfPlayer already is the gravship's faction while the maps hold
// the spectator's data, so a regular push would be a no-op and the gravship would use the spectator's managers.
[HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.RemoveGravshipFromMap))]
static class RemoveGravshipFromMap_Patch
{
    static void Prefix(Building_GravEngine engine, out Map __state)
    {
        __state = null;
        if (Multiplayer.Client == null) return;
        if (!Multiplayer.GameComp.multifaction) return;
        if (engine?.Map == null) return;

        // Remember the map, the engine is despawned by the time the finalizer runs
        __state = engine.Map;
        __state.PushFaction(engine.Faction, force: true);
    }

    static void Finalizer(Map __state)
    {
        __state?.PopFaction();
    }
}

[HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.PlaceGravship))]
static class PlaceGravship_FactionContext_Patch
{
    static void Prefix(Gravship gravship, Map map, out bool __state)
    {
        __state = false;
        if (Multiplayer.Client == null) return;
        if (!Multiplayer.GameComp.multifaction) return;

        map.PushFaction(gravship.Faction, force: true);

        __state = true;
    }

    static void Finalizer(Gravship gravship, Map map, bool __state)
    {
        if (!__state) return;
        map.PopFaction();
    }
}

[HarmonyPatch(typeof(QuestPart_LendColonistsToFaction), nameof(QuestPart_LendColonistsToFaction.Enable))]
static class QuestPart_LendColonistsToFaction_Enable_Patch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        var isFreeColonist = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsFreeColonist));
        var replacement = AccessTools.Method(typeof(CompShuttle_ContainedColonistCount_Patch),
            nameof(CompShuttle_ContainedColonistCount_Patch.IsFreeColonistAnyPlayerFaction));
        foreach (var ci in insts)
        {
            if (ci.Calls(isFreeColonist))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = replacement;
            }
            yield return ci;
        }
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.IsPlayerHome), MethodType.Getter)]
static class Map_IsPlayerHome_Spectator_Patch
{
    static bool Prefix(Map __instance, ref bool __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction ||
            Faction.OfPlayer != Multiplayer.WorldComp.spectatorFaction)
            return true;

        {
            // Is repeat through all player faction a better idea?
            if (!__instance.wasSpawnedViaGravShipLanding)
            {
                MapInfo mapInfo = __instance.info;
                if (((mapInfo != null) ? mapInfo.parent : null) == null || __instance.info.parent.Faction?.IsPlayer != true || !__instance.info.parent.def.canBePlayerHome)
                {
                    __result = GravshipUtility.PlayerHasGravEngine(__instance);
                    return false;
                }
            }
            __result = true;
            return false;
        }
    }
}

[HarmonyPatch]
static class Pawn_TraderTracker_ColonyThingsWillingToBuy_Patch
{
    static MethodInfo TargetMethod()
    {
        return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Pawn_TraderTracker), nameof(Pawn_TraderTracker.ColonyThingsWillingToBuy)));
    }
    public static List<Building> AllBuildingsColonistOfDefOfPlayer(this ListerBuildings lister, ThingDef def)
    {
        if (def.CanHaveFaction)
            return lister.AllBuildingsColonistOfDef(def).FindAll(building => building.Faction == Faction.OfPlayer);
        else
            return lister.AllBuildingsColonistOfDef(def);
    }
    public static IEnumerable<T> AllColonistBuildingsOfTypeOfPlayer<T>(this ListerBuildings lister)
    {
        return lister.AllColonistBuildingsOfType<T>().Where(t =>
        {
            if (t is Building building && (!building.def.CanHaveFaction || building.Faction == Faction.OfPlayer))
                return true;
            return false;
        });
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        var allBuildingsColonistOfDef = AccessTools.Method(typeof(ListerBuildings), nameof(ListerBuildings.AllBuildingsColonistOfDef));
        var allBuildingsColonistOfDefOfPlayer = AccessTools.Method(typeof(Pawn_TraderTracker_ColonyThingsWillingToBuy_Patch), nameof(Pawn_TraderTracker_ColonyThingsWillingToBuy_Patch.AllBuildingsColonistOfDefOfPlayer));
        var allColonistBuildingsOfType = AccessTools.Method(typeof(ListerBuildings), nameof(ListerBuildings.AllColonistBuildingsOfType)).MakeGenericMethod([typeof(IHaulSource)]);
        var allColonistBuildingsOfTypeOfPlayer = AccessTools.Method(typeof(Pawn_TraderTracker_ColonyThingsWillingToBuy_Patch), nameof(Pawn_TraderTracker_ColonyThingsWillingToBuy_Patch.AllColonistBuildingsOfTypeOfPlayer)).MakeGenericMethod([typeof(IHaulSource)]);
        foreach (var ci in insts)
        {
            if (ci.Calls(allBuildingsColonistOfDef))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = allBuildingsColonistOfDefOfPlayer;
            }
            else if (ci.Calls(allColonistBuildingsOfType))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = allColonistBuildingsOfTypeOfPlayer;
            }
            yield return ci;
        }
    }
}

[HarmonyPatch]
static class TradeUtility_AllLaunchableThingsForTrade_Patch
{
    static MethodInfo TargetMethod()
    {
        return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(TradeUtility), nameof(TradeUtility.AllLaunchableThingsForTrade)));
    }

    public static IEnumerable<Building_OrbitalTradeBeacon> AllPoweredOfPlayer(Map map)
    {
        return Building_OrbitalTradeBeacon.AllPowered(map).Where(beacon => beacon.Faction == Faction.OfPlayer);
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        var allPowered = AccessTools.Method(typeof(Building_OrbitalTradeBeacon), nameof(Building_OrbitalTradeBeacon.AllPowered));
        var allPoweredOfPlayer = AccessTools.Method(typeof(TradeUtility_AllLaunchableThingsForTrade_Patch), nameof(TradeUtility_AllLaunchableThingsForTrade_Patch.AllPoweredOfPlayer));
        foreach (var ci in insts)
        {
            if (ci.Calls(allPowered))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = allPoweredOfPlayer;
            }

            yield return ci;
        }
    }
}

// Multifaction: trade request quests (QuestNode_GetNearbySettlement) and caravan/transporter visits could pick another
// player's settlement, which has nothing to trade. Vanilla only excludes the settlements of Faction.OfPlayer.
[HarmonyPatch(typeof(Settlement), nameof(Settlement.Visitable), MethodType.Getter)]
static class Settlement_Visitable_OtherPlayers_Patch
{
    static void Postfix(Settlement __instance, ref bool __result)
    {
        if (__result && Multiplayer.Client != null && Multiplayer.GameComp.multifaction &&
            __instance.Faction is { IsPlayer: true })
            __result = false;
    }
}

// The ritual reward "goodwill with the nearest faction" could pick another player's settlement and change the
// goodwill between the two players. Skip player factions only here: CanChangeGoodwillFor itself is also used for
// goodwill between players (e.g. friendly fire), which has to keep working.
[HarmonyPatch(typeof(RitualAttachableOutcomeEffectWorker_NearbyFactionGoodwill),
    nameof(RitualAttachableOutcomeEffectWorker_NearbyFactionGoodwill.Apply))]
static class RitualNearbyFactionGoodwill_SkipPlayers_Patch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        var canChangeGoodwillFor = AccessTools.Method(typeof(Faction), nameof(Faction.CanChangeGoodwillFor));
        var replacement = AccessTools.Method(typeof(RitualNearbyFactionGoodwill_SkipPlayers_Patch), nameof(CanChangeGoodwillFor));
        var replaced = false;

        foreach (var ci in insts)
        {
            if (ci.Calls(canChangeGoodwillFor))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = replacement;
                replaced = true;
            }

            yield return ci;
        }

        if (!replaced)
            Log.Error("Multiplayer: CanChangeGoodwillFor not found in RitualAttachableOutcomeEffectWorker_NearbyFactionGoodwill.Apply");
    }

    static bool CanChangeGoodwillFor(Faction faction, Faction other, int goodwillChange)
    {
        if (Multiplayer.Client != null && faction is { IsPlayer: true })
            return false;
        return faction.CanChangeGoodwillFor(other, goodwillChange);
    }
}

/// <summary>
/// Prevents the dummy spectator faction from participating in goodwill changes or relation notifications.
/// </summary>
[HarmonyPatch(typeof(Faction), nameof(Faction.CanChangeGoodwillFor))]
static class Faction_CanChangeGoodwillFor_Spectator_Patch
{
    static void Postfix(Faction __instance, Faction other, ref bool __result)
    {
        if (!__result || Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return;

        var spectator = Multiplayer.WorldComp?.spectatorFaction;
        if (spectator != null && (__instance == spectator || other == spectator))
            __result = false;
    }
}

/// <summary>
/// Isolates Faction.OfPlayer context during goodwill and relation mutations so that:
/// 1. Direct violent/diplomatic actions by a player pawn on any map affect that pawn's player faction rather than the map owner or spectator.
/// 2. Goodwill messages and hostility letters are routed exclusively to the involved player faction.
/// 3. Passing ships are only removed from maps belonging to the newly hostile player faction.
/// </summary>
public static class MultifactionGoodwillIsolation
{
    public static bool Active => Multiplayer.Client != null && Multiplayer.GameComp?.multifaction == true;

    public static bool IsRealPlayerFaction(Faction faction) =>
        faction is { IsPlayer: true } && faction != Multiplayer.WorldComp?.spectatorFaction;

    public static Faction ResolveInvolvedPlayerFaction(Faction a, Faction b)
    {
        if (IsRealPlayerFaction(a) && !IsRealPlayerFaction(b))
            return a;
        if (IsRealPlayerFaction(b) && !IsRealPlayerFaction(a))
            return b;
        if (IsRealPlayerFaction(a) && IsRealPlayerFaction(b))
            return a == Faction.OfPlayer || b == Faction.OfPlayer ? Faction.OfPlayer : a;
        return null;
    }
}

[HarmonyPatch(typeof(Faction), nameof(Faction.TryAffectGoodwillWith))]
static class Faction_TryAffectGoodwillWith_Multifaction_Patch
{
    private static readonly MethodInfo MessageMethod = AccessTools.Method(
        typeof(Messages),
        nameof(Messages.Message),
        new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) });

    private static readonly MethodInfo FilteredMessageMethod = AccessTools.Method(
        typeof(Faction_TryAffectGoodwillWith_Multifaction_Patch),
        nameof(SendGoodwillMessage));

    static void Prefix(Faction __instance, Faction other, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active)
            return;

        var playerFac = MultifactionGoodwillIsolation.ResolveInvolvedPlayerFaction(__instance, other);
        if (playerFac != null)
        {
            FactionContext.Push(playerFac);
            __state = true;
        }
    }

    static void Finalizer(bool __state)
    {
        if (__state)
            FactionContext.Pop();
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        foreach (var ci in insts)
        {
            if (ci.Calls(MessageMethod))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Call, FilteredMessageMethod);
            }
            else
            {
                yield return ci;
            }
        }
    }

    /// <summary>
    /// Emits the goodwill change message only if the local client belongs to one of the factions involved.
    /// Uses non-historical message emission in multifaction to avoid advancing the shared UniqueIDsManager message counter on one client only.
    /// </summary>
    private static void SendGoodwillMessage(string text, LookTargets lookTargets, MessageTypeDef def, bool historical, Faction self, Faction other)
    {
        if (!MultifactionGoodwillIsolation.Active)
        {
            Messages.Message(text, lookTargets, def, historical);
            return;
        }

        var myFaction = Multiplayer.RealPlayerFaction;
        if (myFaction != null && (self == myFaction || other == myFaction))
        {
            Messages.Message(text, lookTargets, def, historical: false);
        }
    }
}

[HarmonyPatch(typeof(Faction), nameof(Faction.Notify_RelationKindChanged))]
static class Faction_NotifyRelationKindChanged_Multifaction_Patch
{
    private static readonly MethodInfo RemoveAllShipsMethod = AccessTools.Method(
        typeof(PassingShipManager),
        nameof(PassingShipManager.RemoveAllShipsOfFaction));

    private static readonly MethodInfo FilteredRemoveAllShipsMethod = AccessTools.Method(
        typeof(Faction_NotifyRelationKindChanged_Multifaction_Patch),
        nameof(RemoveAllShipsOfFactionIfHostile));

    static void Prefix(Faction __instance, Faction other, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active)
            return;

        var playerFac = MultifactionGoodwillIsolation.ResolveInvolvedPlayerFaction(__instance, other);
        if (playerFac != null)
        {
            FactionContext.Push(playerFac);
            __state = true;
        }
    }

    static void Finalizer(bool __state)
    {
        if (__state)
            FactionContext.Pop();
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        foreach (var ci in insts)
        {
            if (ci.Calls(RemoveAllShipsMethod))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_1); // Faction other
                yield return new CodeInstruction(OpCodes.Call, FilteredRemoveAllShipsMethod);
            }
            else
            {
                yield return ci;
            }
        }
    }

    /// <summary>
    /// Prevents a faction becoming hostile to Player A from ejecting orbital traders on Player B's map.
    /// </summary>
    private static void RemoveAllShipsOfFactionIfHostile(PassingShipManager manager, Faction faction, Faction other)
    {
        if (MultifactionGoodwillIsolation.Active &&
            manager?.map?.ParentFaction is { IsPlayer: true } mapOwner &&
            other is { IsPlayer: true } &&
            mapOwner != other &&
            !faction.HostileTo(mapOwner))
        {
            return;
        }

        manager.RemoveAllShipsOfFaction(faction);
    }
}

/// <summary>
/// Pushes the instigator's or violator's player faction into FactionContext during Faction notification callbacks
/// so vanilla checks against Faction.OfPlayer resolve to the actual acting player faction.
/// </summary>
[HarmonyPatch]
static class Faction_ActorContext_Notifications_Patch
{
    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberTookDamage))]
    [HarmonyPrefix]
    static void MemberTookDamagePrefix(DamageInfo dinfo, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active) return;
        var fac = dinfo.Instigator?.Faction;
        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
        {
            FactionContext.Push(fac);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberTookDamage))]
    [HarmonyFinalizer]
    static void MemberTookDamageFinalizer(bool __state)
    {
        if (__state) FactionContext.Pop();
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_BuildingTookDamage))]
    [HarmonyPrefix]
    static void BuildingTookDamagePrefix(DamageInfo dinfo, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active) return;
        var fac = dinfo.Instigator?.Faction;
        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
        {
            FactionContext.Push(fac);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_BuildingTookDamage))]
    [HarmonyFinalizer]
    static void BuildingTookDamageFinalizer(bool __state)
    {
        if (__state) FactionContext.Pop();
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_BuildingRemoved))]
    [HarmonyPrefix]
    static void BuildingRemovedPrefix(Pawn deconstructor, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active) return;
        var fac = deconstructor?.Faction;
        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
        {
            FactionContext.Push(fac);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_BuildingRemoved))]
    [HarmonyFinalizer]
    static void BuildingRemovedFinalizer(bool __state)
    {
        if (__state) FactionContext.Pop();
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberCaptured))]
    [HarmonyPrefix]
    static void MemberCapturedPrefix(Faction violator, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active) return;
        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(violator))
        {
            FactionContext.Push(violator);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberCaptured))]
    [HarmonyFinalizer]
    static void MemberCapturedFinalizer(bool __state)
    {
        if (__state) FactionContext.Pop();
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberStripped))]
    [HarmonyPrefix]
    static void MemberStrippedPrefix(Faction violator, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active) return;
        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(violator))
        {
            FactionContext.Push(violator);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberStripped))]
    [HarmonyFinalizer]
    static void MemberStrippedFinalizer(bool __state)
    {
        if (__state) FactionContext.Pop();
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_PlayerTraded))]
    [HarmonyPrefix]
    static void PlayerTradedPrefix(Pawn playerNegotiator, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active) return;
        var fac = playerNegotiator?.Faction;
        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
        {
            FactionContext.Push(fac);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_PlayerTraded))]
    [HarmonyFinalizer]
    static void PlayerTradedFinalizer(bool __state)
    {
        if (__state) FactionContext.Pop();
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberDied))]
    [HarmonyPrefix]
    static void MemberDiedPrefix(DamageInfo? dinfo, Map map, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active) return;
        var fac = dinfo?.Instigator?.Faction;
        if (!MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
            fac = map?.ParentFaction;

        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
        {
            FactionContext.Push(fac);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberDied))]
    [HarmonyFinalizer]
    static void MemberDiedFinalizer(bool __state)
    {
        if (__state) FactionContext.Pop();
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberExitedMap))]
    [HarmonyPrefix]
    static void MemberExitedMapPrefix(Pawn member, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active || member == null) return;
        var fac = member.HostFaction;
        if (!MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
            fac = member.Faction;
        if (!MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
            fac = member.MapHeld?.ParentFaction;

        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
        {
            FactionContext.Push(fac);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberExitedMap))]
    [HarmonyFinalizer]
    static void MemberExitedMapFinalizer(bool __state)
    {
        if (__state) FactionContext.Pop();
    }
}

/// <summary>
/// Evaluates SettlementProximityGoodwillUtility per player faction during the world tick,
/// rather than once under the spectator faction.
/// </summary>
[HarmonyPatch(typeof(SettlementProximityGoodwillUtility), nameof(SettlementProximityGoodwillUtility.CheckSettlementProximityGoodwillChange))]
static class SettlementProximityGoodwill_Multifaction_Patch
{
    private static bool ignore;

    static bool Prefix()
    {
        if (!MultifactionGoodwillIsolation.Active || ignore)
            return true;

        if (Find.TickManager.TicksGame == 0 || Find.TickManager.TicksGame % 900000 != 0)
            return false;

        return FactionRepeater.Template(
            Multiplayer.WorldComp.factionData,
            _ => SettlementProximityGoodwillUtility.CheckSettlementProximityGoodwillChange(),
            null,
            ref ignore);
    }
}

/// <summary>
/// Restricts colony-wide thoughts (ThoughtToAddToAll: ColonistLost on kidnap, ColonistBanished,
/// PrisonerBanishedToDie, ColonyPrisonerEscaped, DeniedJoining) strictly to colonists belonging
/// to the same player faction as the victim/prisoner (or the active Faction.OfPlayer context).
/// In multifaction simulation, Pawn.IsColonist is true for all player factions; without this guard,
/// PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists applies the debuff to every player's colony.
/// </summary>
[HarmonyPatch(typeof(ThoughtToAddToAll), nameof(ThoughtToAddToAll.Add))]
static class ThoughtToAddToAll_Multifaction_Patch
{
    static bool Prefix(ThoughtToAddToAll __instance, Pawn to)
    {
        if (!MultifactionGoodwillIsolation.Active || to == null)
            return true;

        Faction targetFaction = null;
        var other = __instance.otherPawn;
        if (other != null)
        {
            if (other.Faction is { IsPlayer: true })
                targetFaction = other.Faction;
            else if (other.HostFaction is { IsPlayer: true })
                targetFaction = other.HostFaction;
            else if (other.SlaveFaction is { IsPlayer: true })
                targetFaction = other.SlaveFaction;
        }

        if (targetFaction == null && MultifactionGoodwillIsolation.IsRealPlayerFaction(Faction.OfPlayer))
            targetFaction = Faction.OfPlayer;

        if (targetFaction != null && to.Faction != targetFaction)
            return false;

        return true;
    }
}

/// <summary>
/// Sets the victim's or prisoner's player faction context during PawnDiedOrDownedThoughtsUtility.TryGiveThoughts
/// and KidnappedPawnsTracker operations so witness thoughts, responsible colonist lookup, bills, and kidnap letters
/// strictly evaluate against the owning player faction.
/// </summary>
[HarmonyPatch]
static class KidnapAndThoughts_FactionContext_Patch
{
    [HarmonyPatch(typeof(PawnDiedOrDownedThoughtsUtility), nameof(PawnDiedOrDownedThoughtsUtility.TryGiveThoughts),
        new[] { typeof(Pawn), typeof(DamageInfo?), typeof(PawnDiedOrDownedThoughtsKind) })]
    [HarmonyPrefix]
    static void TryGiveThoughtsPrefix(Pawn victim, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active || victim == null)
            return;

        Faction fac = null;
        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(victim.Faction))
            fac = victim.Faction;
        else if (MultifactionGoodwillIsolation.IsRealPlayerFaction(victim.HostFaction))
            fac = victim.HostFaction;
        else if (MultifactionGoodwillIsolation.IsRealPlayerFaction(victim.MapHeld?.ParentFaction))
            fac = victim.MapHeld.ParentFaction;

        if (fac != null)
        {
            FactionContext.Push(fac);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(PawnDiedOrDownedThoughtsUtility), nameof(PawnDiedOrDownedThoughtsUtility.TryGiveThoughts),
        new[] { typeof(Pawn), typeof(DamageInfo?), typeof(PawnDiedOrDownedThoughtsKind) })]
    [HarmonyFinalizer]
    static void TryGiveThoughtsFinalizer(bool __state)
    {
        if (__state)
            FactionContext.Pop();
    }

    [HarmonyPatch(typeof(KidnappedPawnsTracker), nameof(KidnappedPawnsTracker.Kidnap))]
    [HarmonyPrefix]
    static void KidnapPrefix(Pawn pawn, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active || pawn == null)
            return;

        var fac = pawn.Faction;
        if (!MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
            fac = pawn.HostFaction;

        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(fac))
        {
            ((Map)null).PushFaction(fac, force: true);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(KidnappedPawnsTracker), nameof(KidnappedPawnsTracker.Kidnap))]
    [HarmonyFinalizer]
    static void KidnapFinalizer(bool __state)
    {
        if (__state)
            FactionExtensions.PopFaction();
    }

    [HarmonyPatch(typeof(KidnappedPawnsTracker), nameof(KidnappedPawnsTracker.RemoveKidnappedPawn))]
    [HarmonyPrefix]
    static void RemoveKidnappedPawnPrefix(Pawn pawn, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active || pawn == null)
            return;

        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(pawn.Faction))
        {
            FactionContext.Push(pawn.Faction);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(KidnappedPawnsTracker), nameof(KidnappedPawnsTracker.RemoveKidnappedPawn))]
    [HarmonyFinalizer]
    static void RemoveKidnappedPawnFinalizer(bool __state)
    {
        if (__state)
            FactionContext.Pop();
    }

    [HarmonyPatch(typeof(ThoughtWorker_ColonistLeftUnburied), "CurrentStateInternal")]
    [HarmonyPrefix]
    static void ColonistLeftUnburiedPrefix(Pawn p, ref bool __state)
    {
        if (!MultifactionGoodwillIsolation.Active || p == null)
            return;

        if (MultifactionGoodwillIsolation.IsRealPlayerFaction(p.Faction))
        {
            FactionContext.Push(p.Faction);
            __state = true;
        }
    }

    [HarmonyPatch(typeof(ThoughtWorker_ColonistLeftUnburied), "CurrentStateInternal")]
    [HarmonyFinalizer]
    static void ColonistLeftUnburiedFinalizer(bool __state)
    {
        if (__state)
            FactionContext.Pop();
    }
}