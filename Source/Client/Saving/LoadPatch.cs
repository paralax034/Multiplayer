using HarmonyLib;
using Multiplayer.Client.Saving;
using Multiplayer.Common;
using Verse;
using Verse.Profile;

namespace Multiplayer.Client
{
    [HarmonyPatch(typeof(SavedGameLoaderNow))]
    [HarmonyPatch(nameof(SavedGameLoaderNow.LoadGameFromSaveFileNow))]
    [HarmonyPatch([typeof(string)])]
    public static class LoadPatch
    {
        public static TempGameData gameToLoad;

        static bool Prefix()
        {
            if (gameToLoad == null) return true;

            SaveCompression.doSaveCompression = true;

            try
            {
                ScribeUtil.InitFromXmlDoc(gameToLoad.SaveData);
                ScribeMetaHeaderUtility.LoadGameDataHeader(ScribeMetaHeaderUtility.ScribeHeaderMode.Map, false);
                Scribe.EnterNode("game");
                Current.Game = new Game();
                Current.Game.LoadGame(); // calls Scribe.loader.FinalizeLoading()

                // Prevent errors when the client is disconnected during loading
                // todo revisit disconnection during loading
                // todo loading can be async, concurrency issues
                if (Multiplayer.Client != null)
                    SessionData.ReadSessionData(gameToLoad.SessionData);
            }
            finally
            {
                SaveCompression.doSaveCompression = false;
                gameToLoad = null;
            }

            Log.Message("Game loaded");

            if (Multiplayer.Client != null)
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    // Update dirty map grids and room structures safely without executing a simulation tick
                    foreach (var map in Find.Maps)
                    {
                        var asyncTime = map.AsyncTime();
                        asyncTime?.UpdateManagers();

                        if (MpVersion.IsDebug && asyncTime != null)
                        {
                            Log.Message($"Loaded Map {map.uniqueID}: mapTicks={asyncTime.mapTicks}, randState=0x{asyncTime.randState:X16}, cmds={asyncTime.cmds.Count}, Timer={TickPatch.Timer}");
                        }
                    }

                    if (MpVersion.IsDebug && Multiplayer.AsyncWorldTime != null)
                    {
                        Log.Message($"Loaded World: worldTicks={Multiplayer.AsyncWorldTime.worldTicks}, randState=0x{Multiplayer.AsyncWorldTime.randState:X16}, Timer={TickPatch.Timer}");
                    }

                    if (!Current.Game.Maps.Any())
                    {
                        MemoryUtility.UnloadUnusedUnityAssets();
                        Find.World.renderer.RegenerateAllLayersNow();
                    }
                });
            }

            return false;
        }
    }
}
