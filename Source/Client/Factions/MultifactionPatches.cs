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
            Faction playerFaction;
            if (quest.TryGetPlayerFaction(out playerFaction))
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
/// Filters quest entries in the Quests UI tab so players only see quests belonging to their faction unless God Mode is enabled.
/// </summary>
[HarmonyPatch(typeof(MainTabWindow_Quests), nameof(MainTabWindow_Quests.ShouldListNow))]
public static class MainTabWindow_QuestsShouldListNowPatch
{
    public static bool Prefix(MainTabWindow_Quests __instance, Quest quest, ref bool __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
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
/// Blocks player clients from accepting quests assigned to a different player colony.
/// </summary>
[HarmonyPatch(typeof(QuestUtility), nameof(QuestUtility.CanAcceptQuest))]
public static class QuestUtilityCanAcceptQuestPatch
{
    public static void Postfix(Quest quest, ref AcceptanceReport __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return;

        if (DebugSettings.godMode)
            return;

        if (!__result.Accepted)
            return;

        if (quest.TryGetPlayerFaction(out Faction questFaction))
        {
            var myFaction = Multiplayer.RealPlayerFaction;
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
            if (cachedCommand == null)
            {
                cachedCommand = new Command_Action
                {
                    defaultLabel = "Change faction relation",
                    icon = MultiplayerStatic.ChangeRelationIcon,
                    action = OpenChangeRelationMenu
                };
            }

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
/// Comprehensive security utility for resolving faction ownership of Things, Pawns, Prisoners, Corpses, and Map-level events.
/// Includes temporal tile-to-faction caching to prevent nested linear scans over maps and world settlements during GUI frames.
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
    /// Resolves target affiliation for onscreen messages and audio alerts.
    /// Handles colonists, prisoners (via HostFaction), corpses, and map-level weather conditions.
    /// </summary>
    /// <param name="lookTargets">Target references associated with the notification.</param>
    /// <param name="faction">Faction against which visibility is evaluated.</param>
    /// <returns>True if the target belongs to or is relevant to the specified faction; otherwise, false.</returns>
    public static bool IsTargetForFaction(LookTargets lookTargets, Faction faction)
    {
        if (faction == null) return true;
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return true;

        // Fallback for map-level condition alerts without explicit targets (e.g. Flashstorm, Toxic Fallout)
        if (lookTargets?.targets == null || lookTargets.targets.Count == 0)
        {
            var mapContext = Multiplayer.MapContext;
            if (mapContext?.ParentFaction is { IsPlayer: true } mapFaction)
            {
                return mapFaction == faction;
            }

            return true;
        }

        bool hasAnyPlayerAffiliation = false;
        bool hasAnyThingTarget = false;
        var targets = lookTargets.targets;

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            Faction targetFaction = null;

            if (target.HasThing && target.Thing != null)
            {
                hasAnyThingTarget = true;
                var thing = target.Thing;

                // 1. Direct colonist or player structure ownership
                if (thing.Faction is { IsPlayer: true } tf)
                {
                    targetFaction = tf;
                }
                // 2. Prisoner check: prisoner faction is enemy, but HostFaction is the captor
                else if (thing is Pawn pawn && pawn.HostFaction is { IsPlayer: true } hostFac)
                {
                    targetFaction = hostFac;
                }
                // 3. Corpse check: resolve inner pawn colonist or prisoner status
                else if (thing is Corpse corpse && corpse.InnerPawn != null)
                {
                    if (corpse.InnerPawn.Faction is { IsPlayer: true } ctf)
                        targetFaction = ctf;
                    else if (corpse.InnerPawn.HostFaction is { IsPlayer: true } chf)
                        targetFaction = chf;
                }
                // 4. Physical map placement
                else if (thing.Map?.ParentFaction is { IsPlayer: true } mf)
                {
                    targetFaction = mf;
                }
                else if (thing.MapHeld?.ParentFaction is { IsPlayer: true } mhf)
                {
                    targetFaction = mhf;
                }
                // 5. Despawned tile tracking via memoized lookup
                else if (thing.Tile.Valid)
                {
                    targetFaction = GetPlayerFactionAtTile(thing.Tile);
                }
            }
            // World object targets (caravans, sites, settlements)
            else if (target.HasWorldObject && target.WorldObject != null)
            {
                var worldObj = target.WorldObject;
                if (worldObj.Faction is { IsPlayer: true } wf)
                {
                    targetFaction = wf;
                }
                else if (worldObj is Caravan caravan && caravan.Faction is { IsPlayer: true } cf)
                {
                    targetFaction = cf;
                }
            }
            // Target points directly to a cell on a player-owned map
            else if (target.Map?.ParentFaction is { IsPlayer: true } cellMapFac)
            {
                targetFaction = cellMapFac;
            }

            if (targetFaction != null)
            {
                hasAnyPlayerAffiliation = true;
                if (targetFaction == faction)
                    return true;
            }
        }

        if (hasAnyThingTarget && !hasAnyPlayerAffiliation)
        {
            return false;
        }

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

        // Quests have explicit faction bounds
        if (let is ChoiceLetter choiceLetter && choiceLetter.quest != null)
        {
            if (choiceLetter.quest.TryGetPlayerFaction(out Faction questFaction))
                return questFaction == faction;
        }

        // Target locations override generic relatedFaction
        if (let.lookTargets?.targets != null && let.lookTargets.targets.Count > 0)
        {
            return IsTargetForFaction(let.lookTargets, faction);
        }

        // Direct faction association
        if (let.relatedFaction is { IsPlayer: true } relatedFac)
        {
            return relatedFac == faction;
        }

        // Map context fallback for weather and ambient incidents
        var mapContext = Multiplayer.MapContext;
        if (mapContext?.ParentFaction is { IsPlayer: true } mapFaction)
        {
            return mapFaction == faction;
        }

        return true;
    }
}

/// <summary>
/// Silences camera audio cue for incoming letters belonging to other factions.
/// </summary>
[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), typeof(Letter), typeof(string), typeof(int), typeof(bool))]
static class LetterStackReceiveSoundOnlyMyFaction
{
    private static readonly MethodInfo PlayOneShotOnCamera =
        AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShotOnCamera), new[] { typeof(SoundDef), typeof(Map) });
    private static readonly MethodInfo ReplacementMethod =
        AccessTools.Method(typeof(LetterStackReceiveSoundOnlyMyFaction), nameof(PlaySoundReplacement));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        foreach (var inst in insts)
        {
            if (inst.Calls(PlayOneShotOnCamera))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_1); // Pass Letter argument
                yield return new CodeInstruction(OpCodes.Call, ReplacementMethod);
            }
            else
            {
                yield return inst;
            }
        }
    }

    static void PlaySoundReplacement(SoundDef sound, Map map, Letter let)
    {
        if (sound == null) return;
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
        {
            sound.PlayOneShotOnCamera(map);
            return;
        }

        if (LetterSecurityUtility.IsLetterForFaction(let, Multiplayer.RealPlayerFaction))
        {
            sound.PlayOneShotOnCamera(map);
        }
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
/// Suppresses audio beeps for messages belonging to other factions.
/// </summary>
[HarmonyPatch(typeof(Messages), nameof(Messages.Message), typeof(Message), typeof(bool))]
static class MessagesSoundFilterPatch
{
    private static readonly MethodInfo PlayOneShotOnCamera =
        AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShotOnCamera), new[] { typeof(SoundDef), typeof(Map) });
    private static readonly MethodInfo ReplacementMethod =
        AccessTools.Method(typeof(MessagesSoundFilterPatch), nameof(PlayMessageSoundReplacement));

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
