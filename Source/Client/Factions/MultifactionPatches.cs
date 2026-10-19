using HarmonyLib;
using KTrie;
using Multiplayer.API;
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
                TooltipHandler.TipRegion(rect, "MpQuestDesc".Translate(playerFaction.Name, playerFaction == Multiplayer.RealPlayerFaction ? ". (you)" : "."));
            }
            else
            {
                TooltipHandler.TipRegion(rect, "MpPublicQuest".Translate());
            }
        }
    }
}

[HarmonyPatch(typeof(MainTabWindow_Quests), nameof(MainTabWindow_Quests.ShouldListNow))]
public static class MainTabWindow_QuestsShouldListNowPatch
{
    public static bool Prefix(MainTabWindow_Quests __instance, Quest quest, ref bool __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return true;

        // In GodMode or when "DEV: Show all" checkbox is active, display everything
        if (DebugSettings.godMode || __instance.showAll)
            return true;

        // If quest is tied to another player's faction, hide it from our quest list
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

[HarmonyPatch(typeof(QuestUtility), nameof(QuestUtility.CanAcceptQuest))]
public static class QuestUtilityCanAcceptQuestPatch
{
    public static void Postfix(Quest quest, ref AcceptanceReport __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return;

        // Allow bypassing faction restriction if God Mode is enabled
        if (DebugSettings.godMode)
            return;

        // If quest already cannot be accepted due to unmet requirements, leave it as is
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

[HarmonyPatch(typeof(MainTabWindow_Quests), nameof(MainTabWindow_Quests.DoAcceptButton))]
public static class FixDevAcceptInstantlyPatch
{
    private static readonly MethodInfo AcceptMethod = AccessTools.Method(typeof(Quest), nameof(Quest.Accept));

    public static bool Prefix(MainTabWindow_Quests __instance, Rect innerRect, ref float curY)
    {
        // We only intervene if DevMode button is about to be handled and user clicked it
        return true;
    }
}

public static class DevQuestAcceptUtility
{
    [SyncMethod]
    public static void SyncedDevAccept(Quest quest)
    {
        if (quest == null || quest.State != QuestState.NotYetAccepted)
            return;

        var choicePart = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
        if (choicePart != null && choicePart.choices.Any())
        {
            choicePart.Choose(choicePart.choices[0]);
        }

        Pawn accepter = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive
            .FirstOrDefault(p => p.Faction is { IsPlayer: true } && QuestUtility.CanPawnAcceptQuest(p, quest));

        quest.Accept(accepter);
        quest.dismissed = false;
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
                // Do not allow dismissing or deleting other players' quests
                return false;
            }
        }

        return true;
    }
}

[HarmonyPatch(typeof(ColonistBar), nameof(ColonistBar.CheckRecacheEntries))]
public static class ColonistBarCheckRecacheEntriesPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        var findMapsGetter = AccessTools.PropertyGetter(typeof(Find), nameof(Find.Maps));
        foreach (var inst in insts)
        {
            if (inst.Calls(findMapsGetter))
            {
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(ColonistBarCheckRecacheEntriesPatch), nameof(GetFilteredMaps)));
            }
            else
            {
                yield return inst;
            }
        }
    }

    private static readonly List<Map> tmpFilteredMaps = new(8);

    public static IEnumerable<Map> GetFilteredMaps()
    {
        if (Multiplayer.Client == null || !Multiplayer.settings.hideOtherPlayersInColonistBar)
            return Find.Maps;

        tmpFilteredMaps.Clear();
        var maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
        {
            var map = maps[i];
            // Indexed check without LINQ Where/Any allocations
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

    private static bool wasSpectator;
    private static List<MainButtonDef> cachedOriginalButtons;
    private static List<MainButtonDef> cachedSpectatorButtons;
    private static readonly Comparison<MainButtonDef> MainButtonOrderComparer = (a, b) => a.order.CompareTo(b.order);

    private static void EnsureButtonsCached(MainButtonsRoot instance)
    {
        if (cachedOriginalButtons == null)
        {
            var allDefs = DefDatabase<MainButtonDef>.AllDefsListForReading;
            cachedOriginalButtons = new List<MainButtonDef>(allDefs.Count);
            for (int i = 0; i < allDefs.Count; i++)
            {
                var def = allDefs[i];
                if (def.order >= 0)
                    cachedOriginalButtons.Add(def);
            }
            cachedOriginalButtons.Sort(MainButtonOrderComparer);
        }

        if (cachedSpectatorButtons == null)
        {
            cachedSpectatorButtons = new List<MainButtonDef>(SpectatorButtons.Length);
            for (int i = 0; i < SpectatorButtons.Length; i++)
            {
                var defName = SpectatorButtons[i];
                var def = DefDatabase<MainButtonDef>.GetNamedSilentFail(defName);
                if (def != null)
                    cachedSpectatorButtons.Add(def);
            }
        }
    }
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
            bool spectator = IsSpectator;
            // Update list only when spectator state toggles or initial load occurs to eliminate per-frame GC allocations
            if (spectator != wasSpectator || AllButtonsRef(__instance) == null)
            {
                wasSpectator = spectator;
                EnsureButtonsCached(__instance);
                AllButtonsRef(__instance) = spectator ? cachedSpectatorButtons : cachedOriginalButtons;
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
        return __instance.pawn.Faction == Faction.OfPlayer ? gizmos : Enumerable.Empty<Gizmo>();
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
static class PawnChangeRelationGizmo
{
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
    {
        foreach (var gizmo in gizmos)
            yield return gizmo;

        if (Multiplayer.Client == null || Multiplayer.RealPlayerFaction == Multiplayer.WorldComp.spectatorFaction)
            yield break;

        if (__instance.Faction is { IsPlayer: true } && __instance.Faction != Faction.OfPlayer)
        {
            var otherFaction = __instance.Faction;

            yield return new Command_Action
            {
                defaultLabel = "Change faction relation",
                icon = MultiplayerStatic.ChangeRelationIcon,
                action = () =>
                {
                    List<FloatMenuOption> list = new List<FloatMenuOption>
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
            };
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

    static bool FactionIsPlayer(Faction f)
    {
        if (f == null) return false;

        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction || Multiplayer.Ticking || Multiplayer.ExecutingCmds)
            return f.IsPlayer;

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
        if (__result) return;

        // Use indexed loop over reading list to prevent IEnumerator allocation in frequent threat checks
        var factions = Find.FactionManager.AllFactionsListForReading;
        for (int i = 0; i < factions.Count; i++)
        {
            var f = factions[i];
            if (f.IsPlayer && GenHostility.IsActiveThreatTo(target, f))
            {
                __result = true;
                return;
            }
        }
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

public static class LetterSecurityUtility
{
    public static bool IsTargetForFaction(LookTargets lookTargets, Faction faction)
    {
        if (faction == null) return true;
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return true;

        // If message has no explicit targets (e.g. GameCondition eclipse end), isolate it to the current ticking map's owner
        if (lookTargets?.targets == null || lookTargets.targets.Count == 0)
        {
            var activeMap = Multiplayer.MapContext ?? Find.CurrentMap;
            if (activeMap?.ParentFaction is { IsPlayer: true } mapOwner)
            {
                return mapOwner == faction;
            }
            return true;
        }

        bool hasAnyPlayerAffiliation = false;
        bool hasAnyValidTarget = false;
        var targets = lookTargets.targets;

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            Faction targetFaction = null;

            // Target is a Thing on a map or in a caravan
            if (target.HasThing && target.Thing != null)
            {
                var thing = target.Thing;
                if (thing.Faction is { IsPlayer: true } tf)
                    targetFaction = tf;
                else if (thing.Map?.ParentFaction is { IsPlayer: true } mf)
                    targetFaction = mf;
                else if (thing.MapHeld?.ParentFaction is { IsPlayer: true } mhf)
                    targetFaction = mhf;

                // A Thing is a valid anchor if it belongs to a player, or is currently spawned on a map.
                if (thing.Faction is { IsPlayer: true } || thing.Map != null || thing.MapHeld != null)
                    hasAnyValidTarget = true;
            }
            // Target is a WorldObject (Caravan, Settlement, MapParent)
            else if (target.HasWorldObject && target.WorldObject != null)
            {
                hasAnyValidTarget = true;
                var worldObj = target.WorldObject;
                if (worldObj.Faction is { IsPlayer: true } wf)
                    targetFaction = wf;
                else if (worldObj is Caravan caravan && caravan.Faction is { IsPlayer: true } cf)
                    targetFaction = cf;
            }
            // Target is a cell on a player map
            else if (target.Map?.ParentFaction is { IsPlayer: true } cellMapFac)
            {
                hasAnyValidTarget = true;
                targetFaction = cellMapFac;
            }
            else
            {
                // Fallback for abstract targets (like raw world tiles) to prevent accidental hiding
                hasAnyValidTarget = true;
            }

            if (targetFaction != null)
            {
                hasAnyPlayerAffiliation = true;
                if (targetFaction == faction)
                    return true;
            }
        }

        // If all targeted Things are despawned NPCs (e.g. guests who left the map), they lose their map affiliation.
        // Hide the letter to prevent it from falling back to global and appearing on other players' screens.
        if (!hasAnyValidTarget)
            return false;

        // If the event targets another player's colony/caravan, hide it completely
        return !hasAnyPlayerAffiliation;
    }

    public static bool IsLetterForFaction(Letter let, Faction faction)
    {
        if (faction == null) return true;
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return true;
        if (let == null) return false;

        if (let is ChoiceLetter choiceLetter && choiceLetter.quest != null)
        {
            if (choiceLetter.quest.TryGetPlayerFaction(out Faction questFaction))
                return questFaction == faction;
        }

        // Target location on a player map takes precedence over relatedFaction to prevent cross-settlement letter leaks
        if (let.lookTargets?.targets != null && let.lookTargets.targets.Count > 0)
        {
            return IsTargetForFaction(let.lookTargets, faction);
        }

        if (let.relatedFaction is { IsPlayer: true } relatedFac)
        {
            return relatedFac == faction;
        }

        return true;
    }
}

[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.LettersOnGUI))]
static class LettersOnGUIFilterPatch
{
    private static readonly FieldInfo LettersField = AccessTools.Field(typeof(LetterStack), "letters");
    private static readonly MethodInfo FilteredLettersMethod = AccessTools.Method(typeof(LettersOnGUIFilterPatch), nameof(GetFilteredLetters));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var ci in instructions)
        {
            if (ci.LoadsField(LettersField))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = FilteredLettersMethod;
            }
            yield return ci;
        }
    }

    private static int lastFrameCached = -1;
    private static readonly List<Letter> tmpFilteredLetters = new(32);

    public static List<Letter> GetFilteredLetters(LetterStack stack)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return stack.letters;

        if (Time.frameCount != lastFrameCached)
        {
            lastFrameCached = Time.frameCount;
            tmpFilteredLetters.Clear();
            var myFaction = Multiplayer.RealPlayerFaction;
            var allLetters = stack.letters;
            for (int i = 0; i < allLetters.Count; i++)
            {
                var letter = allLetters[i];
                if (LetterSecurityUtility.IsLetterForFaction(letter, myFaction))
                {
                    tmpFilteredLetters.Add(letter);
                }
            }
        }

        return tmpFilteredLetters;
    }
}

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
                yield return new CodeInstruction(OpCodes.Ldarg_1); // Letter let
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

    public static List<Message> GetFilteredLiveMessages()
    {
        var original = Messages.liveMessages;
        // Guard against calls from main menu / UIRoot_Entry before game session initializes
        if (Multiplayer.Client == null || Multiplayer.game == null || !Multiplayer.GameComp.multifaction)
            return original;

        tmpFilteredMessages.Clear();
        var myFaction = Multiplayer.RealPlayerFaction;

        for (int i = 0; i < original.Count; i++)
        {
            var msg = original[i];
            if (LetterSecurityUtility.IsTargetForFaction(msg.lookTargets, myFaction))
            {
                tmpFilteredMessages.Add(msg);
            }
        }

        return tmpFilteredMessages;
    }
}

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
                yield return new CodeInstruction(OpCodes.Ldarg_0); // Message msg
                yield return new CodeInstruction(OpCodes.Call, ReplacementMethod);
            }
            else
            {
                yield return inst;
            }
        }
    }

    // Suppress camera beep for messages that don't belong to our faction
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

[HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.RemoveGravshipFromMap))]
static class RemoveGravshipFromMap_Patch
{
    static void Prefix(Building_GravEngine engine, out bool __state)
    {
        __state = false;
        if (Multiplayer.Client == null) return;
        if (!Multiplayer.GameComp.multifaction) return;
        if (engine == null) return;

        engine.Map.PushFaction(engine.Faction);

        __state = true;
    }

    static void Finalizer(Building_GravEngine engine, bool __state)
    {
        if (!__state) return;
        engine.Map.PopFaction();
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

        map.PushFaction(gravship.Faction);

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
    public static bool Prefix(Map __instance, ref bool __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return true;

        if (!__instance.wasSpawnedViaGravShipLanding)
        {
            MapInfo mapInfo = __instance.info;
            if (mapInfo?.parent == null || mapInfo.parent.Faction?.IsPlayer != true || !mapInfo.parent.def.canBePlayerHome)
            {
                __result = GravshipUtility.PlayerHasGravEngine(__instance);
                return false;
            }
        }

        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.WorldPawnsTick))]
static class IgnoreSpectatorWorldPawnsTickPatch
{
    private static readonly FieldInfo PawnsAliveField = AccessTools.Field(typeof(WorldPawns), "pawnsAlive");
    private static readonly MethodInfo FilterMethod = AccessTools.Method(typeof(IgnoreSpectatorWorldPawnsTickPatch), nameof(FilterPawnsAlive));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instr in instructions)
        {
            yield return instr;
            if (instr.LoadsField(PawnsAliveField))
            {
                yield return new CodeInstruction(OpCodes.Call, FilterMethod);
            }
        }
    }

    private static readonly List<Pawn> tmpFiltered = new();
    private static readonly Comparison<Pawn> PawnIdComparer = (a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber);

    public static IEnumerable<Pawn> FilterPawnsAlive(HashSet<Pawn> pawns)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return pawns;

        var spectator = Multiplayer.WorldComp?.spectatorFaction;
        if (spectator == null)
            return pawns;

        tmpFiltered.Clear();
        foreach (var p in pawns)
        {
            if (p != null && p.Faction != spectator)
                tmpFiltered.Add(p);
        }

        // Sort deterministically using cached delegate to prevent iteration divergence across clients
        tmpFiltered.Sort(PawnIdComparer);
        return tmpFiltered;
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

[HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.FinishProject))]
public static class ResearchManager_FinishProject_Patch
{
    public static Faction currentFinishingFaction;

    // Do not modify ref parameters! Let the simulation run exactly the same for everyone.
    public static void Prefix(Pawn researcher)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return;

        // Resolve researching faction: researcher -> current map owner -> active player faction fallback
        currentFinishingFaction = researcher?.Faction ?? Multiplayer.MapContext?.ParentFaction ?? Faction.OfPlayerSilentFail;
    }

    // Harmony finalizers guarantee execution even if FinishProject throws an unhandled exception
    public static void Finalizer()
    {
        currentFinishingFaction = null;
    }
}

[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), typeof(Letter), typeof(string), typeof(int), typeof(bool))]
public static class AssignFactionToResearchLetterPatch
{
    public static void Prefix(Letter let)
    {
        // If a letter is generated during research completion, tag it with the researching faction.
        // This allows LettersOnGUIFilterPatch to correctly hide it from other players.
        if (ResearchManager_FinishProject_Patch.currentFinishingFaction != null && let != null)
        {
            let.relatedFaction = ResearchManager_FinishProject_Patch.currentFinishingFaction;
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
