using System.Collections.Generic;
using System.Linq;
using System.Text;
using Multiplayer.Client.Factions;
using Multiplayer.Client.Util;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Multiplayer.Client
{
    public static class FactionSidebar
    {
        private static SidebarTabs currentTab = SidebarTabs.CreateFaction;

        private static string factionNameTextField;
        private static Color factionColor = new Color32(0x00, 0xBC, 0xD8, 255);
        private static bool setupNextMapFromTickZero;
        private static ScenarioDef chosenScenarioInt;

        /// <summary>
        /// Resolves the selected starting scenario with a safe fallback to prevent null references during early mod init.
        /// </summary>
        private static ScenarioDef ChosenScenario
        {
            get => chosenScenarioInt ??= ScenarioDefOf.Crashlanded ?? DefDatabase<ScenarioDef>.GetNamedSilentFail("Crashlanded");
            set => chosenScenarioInt = value;
        }

        private static Vector2 joinFactionScroll;

        private enum SidebarTabs
        {
            CreateFaction,
            JoinFaction
        }

        public static void DoFactionSidebarContents(Rect inRect)
        {
            using var _ = MpStyle.Set(GameFont.Small);

            var tabs = new List<TabRecord>
            {
            new("MpCreateFaction".Translate(), () => currentTab = SidebarTabs.CreateFaction, currentTab == SidebarTabs.CreateFaction),
            new("MpJoinFaction".Translate(), () => currentTab = SidebarTabs.JoinFaction, currentTab == SidebarTabs.JoinFaction),
            };
            inRect.yMin += 35f;

            TabDrawer.DrawTabs(inRect, tabs);


            GUI.BeginGroup(new Rect(0, inRect.yMin, inRect.width, inRect.height));
            {
                Rect groupRect = inRect.AtZero();
                switch (currentTab)
                {
                    case SidebarTabs.CreateFaction:
                        DrawFactionCreator(groupRect);
                        break;
                    case SidebarTabs.JoinFaction:
                        DrawFactionChooser(groupRect);
                        break;
                }
            }
            GUI.EndGroup();
        }

        private static readonly List<Faction> TmpPlayerFactions = new(8);
        private static readonly List<Map> TmpFactionHomes = new(4);
        private static TaggedString CachedJoinLabel;
        private static TaggedString CachedLocateLabel;

        /// <summary>
        /// Renders the faction chooser list for joining existing player factions.
        /// Uses reusable list buffers and cached translation strings to eliminate per-frame allocations during scrolling.
        /// </summary>
        /// <param name="inRect">Bounding client area for the faction selection view.</param>
        private static void DrawFactionChooser(Rect inRect)
        {
            if (CachedJoinLabel.NullOrEmpty())
                CachedJoinLabel = "MpJoin".Translate();
            if (CachedLocateLabel.NullOrEmpty())
                CachedLocateLabel = "MpLocateOnMap".Translate();

            TmpPlayerFactions.Clear();
            var allFactions = Find.FactionManager.AllFactionsListForReading;
            for (int f = 0; f < allFactions.Count; f++)
            {
                var fac = allFactions[f];
                // Exclude defeated factions and spectator from the faction chooser
                if (fac.IsPlayer && !fac.defeated && fac.name != "Spectator")
                {
                    TmpPlayerFactions.Add(fac);
                }
            }

            // Join Faction Headline
            inRect.yMin += 5f;
            using (MpStyle.Set(GameFont.Medium))
                Widgets.Label(inRect.Right(16), "MpJoinFaction".Translate());
            inRect.yMin += 25f + 5f;
            // END Join Faction Headline

            // Line
            float lineX = inRect.x + 16f;
            float lineY = inRect.yMin;
            float lineWidth = inRect.width - 32f;
            Widgets.DrawLineHorizontal(lineX, lineY, lineWidth);
            inRect.yMin += 5f;
            // END Line

            // Faction List
            Rect outRect = new Rect(0, inRect.yMin + 10, inRect.width - 16, inRect.height - 20);
            float height = TmpPlayerFactions.Count * 40f;
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, height);
            Widgets.BeginScrollView(outRect, ref joinFactionScroll, viewRect, true);

            float y = 0;

            for (int i = 0; i < TmpPlayerFactions.Count; i++)
            {
                var playerFaction = TmpPlayerFactions[i];
                Rect entryRect = new Rect(16, y, viewRect.width, 40);
                if (i % 2 == 0)
                    Widgets.DrawAltRect(entryRect);

                using (MpStyle.Set(TextAnchor.MiddleLeft))
                    LabelWithIcon(entryRect, playerFaction.Name, playerFaction.def.FactionIcon, playerFaction.Color, 0.6f);

                Rect joinButton = new Rect(entryRect.xMax - 75, entryRect.y + 5, 70, 40 - 10);
                if (Widgets.ButtonText(joinButton, CachedJoinLabel))
                {
                    Map factionHome = null;
                    var maps = Find.Maps;
                    for (int m = 0; m < maps.Count; m++)
                    {
                        if (maps[m].ParentFaction == playerFaction)
                        {
                            factionHome = maps[m];
                            break;
                        }
                    }

                    if (factionHome != null)
                    {
                        Current.Game.CurrentMap = factionHome;
                        Find.World.renderer.wantedMode = WorldRenderMode.None;
                    }

                    Multiplayer.Client.Send(
                        new ClientSetFactionPacket(Multiplayer.session.playerId, playerFaction.loadID));
                }

                Rect locateButton = new Rect(entryRect.xMax - 110, entryRect.y + 7, 24, 24);
                if (Widgets.ButtonImage(locateButton, TexButton.OpenInspector))
                {
                    TmpFactionHomes.Clear();
                    var maps = Find.Maps;
                    for (int m = 0; m < maps.Count; m++)
                    {
                        if (maps[m].ParentFaction == playerFaction)
                            TmpFactionHomes.Add(maps[m]);
                    }

                    if (TmpFactionHomes.Count == 1)
                    {
                        CameraJumper.TryJumpAndSelect(TmpFactionHomes[0].Parent, CameraJumper.MovementMode.Pan);
                    }
                    else if (TmpFactionHomes.Count > 1)
                    {
                        List<FloatMenuOption> list = new();
                        for (int h = 0; h < TmpFactionHomes.Count; h++)
                        {
                            var m = TmpFactionHomes[h];
                            if (!m.IsPocketMap)
                            {
                                string label = m.Parent.LabelCap;
                                if (GravshipUtility.TryGetNameOfGravshipOnMap(m, out string shipName))
                                    label += " (" + shipName + ")";

                                var capturedMap = m;
                                list.Add(new FloatMenuOption(label, () =>
                                {
                                    CameraJumper.TryJumpAndSelect(capturedMap.Parent, CameraJumper.MovementMode.Pan);
                                },
                                capturedMap.Parent.ExpandingIcon,
                                capturedMap.Parent.ExpandingIconColor));
                            }
                        }
                        Find.WindowStack.Add(new FloatMenu(list));
                    }

                    TmpFactionHomes.Clear();
                }

                TooltipHandler.TipRegion(locateButton, CachedLocateLabel);

                y += entryRect.height;
            }

            Widgets.EndScrollView();
            TmpPlayerFactions.Clear();
            // END Faction List
        }
        private static void DrawFactionCreator(Rect inRect)
        {
            // Create Faction Headline
            inRect.yMin += 5f;
            using (MpStyle.Set(GameFont.Medium))
                Widgets.Label(inRect.Right(16), "MpCreateFaction".Translate());
            inRect.yMin += 25f + 5f;
            // END Create Faction Headline

            // Line
            float lineX = inRect.x + 16f;
            float lineY = inRect.yMin;
            float lineWidth = inRect.width - 32f;
            Widgets.DrawLineHorizontal(lineX, lineY, lineWidth);
            inRect.yMin += 5f;
            // END Line

            // Row Settings
            float rowHeight = 25f;
            float spacing = 5f;

            Rect outRect = new Rect(0, inRect.yMin + 10, inRect.width - 16, inRect.height - 20);
            Rect viewRect = new Rect(16, outRect.y, outRect.width - 16f, 300);
            // END Row Settings

            // Faction Name Row
            float fieldWidth = 160f;

            viewRect.yMin += 5f;
            Vector2 labelSize = Text.CalcSize("MpFactionName".Translate());
            float labelWidth = labelSize.x;

            Rect factionNameRowRect = new Rect(
                viewRect.x,
                viewRect.y,
                viewRect.width,
                rowHeight
            );

            Rect labelRect = new Rect(
                factionNameRowRect.x,
                factionNameRowRect.y,
                labelWidth,
                rowHeight
            );
            using (MpStyle.Set(GameFont.Small))
                Widgets.Label(labelRect, "MpFactionName".Translate());

            Rect fieldRect = new Rect(
                factionNameRowRect.xMax - fieldWidth,
                factionNameRowRect.y,
                fieldWidth,
                rowHeight
            );
            factionNameTextField = Widgets.TextField(fieldRect, factionNameTextField);

            viewRect.yMin += rowHeight + spacing;
            // END Faction Name Row

            // Faction Color Picker
            rowHeight = 24f;
            float boxSize = 24f;

            Rect colorRowRect = new Rect(viewRect.x, viewRect.y, viewRect.width, rowHeight);

            Rect colorPreviewRect = new Rect(
                colorRowRect.xMax - boxSize,
                colorRowRect.y + (rowHeight - boxSize) / 2f,
                boxSize,
                boxSize
            );

            Rect colorPreviewlabelRect = new Rect(
              colorRowRect.x,
              colorRowRect.y,
              colorRowRect.width - boxSize - spacing,
              rowHeight
            );
            using (MpStyle.Set(GameFont.Small))
                Widgets.Label(colorPreviewlabelRect, "MpFactionColor".Translate());

            Widgets.DrawBoxSolid(colorPreviewRect, factionColor);
            Widgets.DrawBox(colorPreviewRect);
            Widgets.DrawHighlightIfMouseover(colorPreviewRect);

            if (Widgets.ButtonInvisible(colorPreviewRect))
            {
                Find.WindowStack.Add(new Dialog_ChooseFactionColor(color =>
                {
                    factionColor = color;
                }, factionColor));
            }
            viewRect.yMin += rowHeight + spacing;
            // END Faction Color Picker

            // Default Map Time Row
            float checkSize = 24f;
            bool asyncTimeDisabled = !Multiplayer.GameComp.asyncTime;

            StringBuilder sb = new StringBuilder();
            if (asyncTimeDisabled)
            {
                sb.Append("MpAsyncTimeDisabled".Translate());
            }
            sb.AppendLine("MpDefaultMapTimeDesc1".Translate());
            sb.AppendLine("MpDefaultMapTimeDesc2".Translate());
            sb.AppendLine("MpDefaultMapTimeDesc3".Translate());

            Rect defaultMapTimeRowRect = new Rect(viewRect.x, viewRect.y, viewRect.width, rowHeight);

            Rect checkRect = new Rect(
                defaultMapTimeRowRect.xMax - checkSize,
                defaultMapTimeRowRect.y + (rowHeight - checkSize) / 2f,
                checkSize,
                checkSize
            );

            Rect defaultMapTimelabelRect = new Rect(
                defaultMapTimeRowRect.x,
                defaultMapTimeRowRect.y,
                defaultMapTimeRowRect.width - checkSize - spacing,
                rowHeight
            );

            using (MpStyle.Set(GameFont.Small))
                Widgets.Label(defaultMapTimelabelRect, "MpDefaultMapTime".Translate());

            MpUI.Checkbox(
                checkRect.position.x,
               checkRect.position.y,
               ref setupNextMapFromTickZero,
               asyncTimeDisabled,
               checkSize);

            TooltipHandler.TipRegion(defaultMapTimeRowRect, sb.ToString());

            viewRect.yMin += rowHeight + spacing;
            // END Default Map Time Row

            // Scanerio Chooser
            rowHeight = 30f;

            Rect scenarioRect = new Rect(
                viewRect.x,
                viewRect.y,
                viewRect.width,
                rowHeight
            );

            if (Mouse.IsOver(scenarioRect))
                Widgets.DrawMenuSection(scenarioRect);
            else
                Widgets.DrawHighlight(scenarioRect);

            if (Widgets.ButtonInvisible(scenarioRect))
                OpenScenarioChooser();

            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(scenarioRect, $"{ChosenScenario?.label ?? Find.Scenario.name}");
            Text.Anchor = TextAnchor.UpperLeft;

            viewRect.yMin += rowHeight + spacing;
            // END Scanerio Chooser

            // Buttons
            float buttonHeight = 35f;

            Rect createFactionRect = new Rect(
                viewRect.x,
                viewRect.yMin + spacing + 5f,
                viewRect.width,
                buttonHeight
            );
            if (Widgets.ButtonText(createFactionRect, "MpSettleFaction".Translate()) && FactionCreationCanBeStarted())
            {
                OpenConfigurationPages();
            }

            Rect selectRandomSiteRect = new Rect(
                viewRect.x,
                createFactionRect.yMax + spacing + 10f,
                viewRect.width,
                buttonHeight
            );
            if (Widgets.ButtonText(selectRandomSiteRect, "MpSelectRandomSite".Translate()))
            {
                SoundDefOf.Click.PlayOneShotOnCamera(null);
                if (ModsConfig.OdysseyActive && Rand.Bool)
                {
                    Find.WorldInterface.SelectedTile = TileFinder.RandomSettlementTileFor(Find.WorldGrid.Surface, Faction.OfPlayer, true, (PlanetTile x) => x.Tile.Landmark != null);
                }
                else
                {
                    Find.WorldInterface.SelectedTile = TileFinder.RandomStartingTile();
                }
                CameraJumper.TryJumpAndSelect(new GlobalTargetInfo(Find.WorldInterface.SelectedTile), CameraJumper.MovementMode.Pan);
            }

            Rect worldFactionsRect = new Rect(
             viewRect.x,
             selectRandomSiteRect.yMax + spacing,
             viewRect.width,
             buttonHeight
            );
            if (Widgets.ButtonText(worldFactionsRect, "MpWorldFactions".Translate()))
            {
                Find.WindowStack.Add(new Dialog_FactionDuringLanding());
            }
            // END Buttons
        }

        private static void DoCreateFaction(IdeologyData chooseIdeoInfo, bool generateMap)
        {
            int playerId = Multiplayer.session.playerId;
            var prevState = Current.programStateInt;
            List<Pawn> startingPawns = new List<Pawn>();
            FactionCreationData factionCreationDto = new FactionCreationData();

            Current.programStateInt = ProgramState.Playing;

            try
            {
                if (Current.Game.InitData?.startingAndOptionalPawns is { } pawns)
                    for (int i = 0; i < Find.GameInitData.startingPawnCount; i++)
                    {
                        FactionCreator.SendPawn(playerId, FactionCreator.WriteStartingPawn(pawns[i]));
                        startingPawns.Add(pawns[i]);
                    }

                factionCreationDto.factionName = factionNameTextField;
                factionCreationDto.factionColor = factionColor;
                factionCreationDto.startingTile = Find.WorldInterface.SelectedTile;
                factionCreationDto.scenarioDef = ChosenScenario;
                factionCreationDto.chooseIdeoInfo = chooseIdeoInfo;
                factionCreationDto.generateMap = generateMap;
                factionCreationDto.startingPossessions = GetStartingPossessions(startingPawns);
                factionCreationDto.setupNextMapFromTickZero = setupNextMapFromTickZero;

                FactionCreator.CreateFaction(playerId, factionCreationDto);
            }
            finally
            {
                Current.programStateInt = prevState;
            }
        }

        private static void OpenScenarioChooser()
        {
            Find.WindowStack.Add(new Page_SelectScenario_Multifaction
            {
                curScen = ChosenScenario?.scenario,
                onScenChosen = chosenScen =>
                {
                    var selectedDef = DefDatabase<ScenarioDef>.AllDefsListForReading.Find(def => def.scenario == chosenScen);

                    if (selectedDef != null)
                        ChosenScenario = selectedDef;
                }
            });
        }

        private static void OpenConfigurationPages()
        {
            var gameConfigurationPages = new List<Page>();
            Page_ChooseIdeo_Multifaction ideologyConfigPage = null;
            Page_ConfigureStartingPawns pawnConfigPage = null;
            var scenario = ChosenScenario?.scenario ?? Find.Scenario;

            InitializeDataForGameConfigurationPages();

            ideologyConfigPage = TryGetIdeologyConfigurationPage(scenario);

            pawnConfigPage = new Page_ConfigureStartingPawns_Multifaction(scenario)
            {
                nextAct = () =>
                {
                    IdeologyData ideologyData = ideologyConfigPage?.GetIdeologyData() ?? new IdeologyData();
                    DoCreateFaction(ideologyData, true);
                }
            };

            if (ideologyConfigPage != null)
                gameConfigurationPages.Add(ideologyConfigPage);

            gameConfigurationPages.Add(pawnConfigPage);

            Find.WindowStack.Add(PageUtility.StitchedPages(gameConfigurationPages));
        }

        // Find.Scenario is shared game state the simulation reads (Scenario.TickScenario, stat factors, disabled
        // incidents, ...), so the configuration pages only apply their scenario while their own code runs
        internal static MpScope UseScenario(Scenario scenario)
        {
            Scenario prevScenario = null;
            return MpScope.Create(
                () =>
                {
                    prevScenario = Current.Game.Scenario;
                    if (scenario != null)
                        Current.Game.Scenario = scenario;
                },
                () => Current.Game.Scenario = prevScenario);
        }

        private static void InitializeDataForGameConfigurationPages()
        {
            Current.Game.InitData = new GameInitData
            {
                startedFromEntry = true,
                gameToLoad = FactionCreator.preventSpecialCalculationPathInGenTicksTicksAbs
            };
        }

        private static List<ThingDefCount> GetStartingPossessions(List<Pawn> startingPawns)
        {
            Dictionary<Pawn, List<ThingDefCount>> allPossessions = Find.GameInitData.startingPossessions;
            List<ThingDefCount> startingPossessions = new List<ThingDefCount>();

            foreach (Pawn pawn in startingPawns)
            {
                startingPossessions.AddRange(allPossessions[pawn]);
            }

            return startingPossessions;
        }
        private static Page_ChooseIdeo_Multifaction TryGetIdeologyConfigurationPage(Scenario scenario)
        {
            Page_ChooseIdeo_Multifaction chooseIdeoPage = null;

            if (ModsConfig.IdeologyActive && !Find.IdeoManager.classicMode)
                chooseIdeoPage = new Page_ChooseIdeo_Multifaction { scenario = scenario };

            return chooseIdeoPage;
        }
        private static bool FactionCreationCanBeStarted()
        {
            var tileError = new StringBuilder();

            if (string.IsNullOrWhiteSpace(factionNameTextField))
            {
                ShowRejectInputMessage("MpEmptyFactionName".Translate());
                return false;
            }
            else if (Find.FactionManager.AllFactions.Any(f => f.Name == factionNameTextField))
            {
                ShowRejectInputMessage("MpTakenFactionName".Translate());
                return false;
            }
            else if (Find.WorldInterface.SelectedTile < 0)
            {
                ShowRejectInputMessage("MustSelectStartingSite".TranslateWithBackup("MustSelectLandingSite"));
                return false;
            }
            else if (!TileFinder.IsValidTileForNewSettlement(Find.WorldInterface.SelectedTile, tileError))
            {
                ShowRejectInputMessage(tileError.ToString());
                return false;
            }
            return true;
        }

        private static void ShowRejectInputMessage(string message)
        {
            Messages.Message(message, MessageTypeDefOf.RejectInput, historical: false);
        }
        public static void LabelWithIcon(Rect rect, string label, Texture2D labelIcon, Color iconColor, float labelIconScale = 1f)
        {
            float num = Mathf.Min((float)labelIcon.width, rect.height);
            Rect outerRect = new Rect(rect.x, rect.y, num, rect.height);
            rect.xMin += num;
            Color color = GUI.color;
            GUI.color = new Color(iconColor.r, iconColor.g, iconColor.b, iconColor.a * GUI.color.a);
            Widgets.DrawTextureFitted(outerRect, labelIcon, labelIconScale, 1f);
            GUI.color = color;
            Widgets.Label(rect, label);
        }
    }
}
