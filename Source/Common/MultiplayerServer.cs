using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.Common.ChatCommands;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public class MultiplayerServer
    {
        static MultiplayerServer()
        {
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerSteam, typeof(ServerSteamState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerJoining, typeof(ServerJoiningState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerBootstrap, typeof(ServerBootstrapState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerLoading, typeof(ServerLoadingState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerPlaying, typeof(ServerPlayingState));
        }

        public static MultiplayerServer? instance;

        public const int DefaultPort = 30502;
        public const int LanBroadcastPort = 5100;
        public const string LanBroadcastName = "mp-server";
        public const int MaxUsernameLength = 15;
        public const int MinUsernameLength = 3;
        public const char EndpointSeparator = '&';
        public const int NetTicksPerSecond = 30; // Not an exact amount. The net loop isn't particularly precise.

        // Adaptive network lag thresholds and multipliers
        public const int LagSlowdownMinTicks = 75;
        public const int LagSlowdownMaxTicks = 180;
        public const int LagCriticalThresholdTicks = 300;
        public const float MaxSlowdownMultiplier = 0.5f;

        public static readonly Regex UsernamePattern = new(@"^[a-zA-Z0-9_]+$");

        public WorldData worldData;
        public FreezeManager freezeManager;
        public CommandHandler commands;
        public ChatCommandManager chatCmdManager;
        public PlayerManager playerManager;
        public List<INetManager> netManagers = [];
        public IEnumerable<ServerPlayer> JoinedPlayers => playerManager.JoinedPlayers;
        public IEnumerable<ServerPlayer> PlayingPlayers => playerManager.PlayingPlayers;
        public IEnumerable<ServerPlayer> PlayingIngamePlayers => playerManager.PlayingPlayers.Where(p => p.status == PlayerStatus.Playing);

        public string? hostUsername;
        public int gameTimer;
        public int startingTimer;
        public int workTicks;
        public ActionQueue queue = new();
        public ServerSettings settings;

        public ServerInitData? InitData => initDataSource.Task.ResultNowOrNull();
        private TaskCompletionSource<ServerInitData?> initDataSource = new();
        public InitDataState InitDataState =>
            InitData != null ? InitDataState.Complete :
            // started init data must've completed with null, meaning the client disconnected while waiting for the data,
            // so we are waiting again
            initDataSource.Task.IsCompleted ? InitDataState.Waiting :
            InitDataState.Requested;

        public volatile bool running;

        public bool ArbiterPlaying => PlayingPlayers.Any(p => p.IsArbiter && p.status == PlayerStatus.Playing);
        public ServerPlayer HostPlayer => PlayingPlayers.First(p => p.IsHost);

        public bool FullyStarted => running && worldData.savedGame != null;

        public const float StandardTimePerTick = 1000.0f / 60.0f;

        public float serverTimePerTick = StandardTimePerTick;
        public int sentCmdsSnapshot;

        public int NetTimer { get; private set; }

        public bool IsStandaloneServer { get; set; }
        public StandalonePersistence? persistence;

        public MultiplayerServer(ServerSettings settings)
        {
            this.settings = settings;

            worldData = new WorldData(this);
            freezeManager = new FreezeManager(this);
            commands = new CommandHandler(this);
            chatCmdManager = new ChatCommandManager(this);
            playerManager = new PlayerManager(this);

            ChatCommandRegistry.Register(chatCmdManager, this);

            initDataSource.SetResult(null);
        }

        public void Run()
        {
            ServerLog.Detail("Server started");

            Stopwatch time = Stopwatch.StartNew();
            Stopwatch tickTime = Stopwatch.StartNew();
            double realTime = 0;

            int lastPauseLogTimer = 0;

            while (running)
            {
                try
                {
                    double elapsed = time.ElapsedMillisDouble();
                    time.Restart();
                    realTime += elapsed;

                    tickTime.Restart();

                    freezeManager.Tick();
                    queue.RunQueue(ServerLog.Error);
                    netManagers.ForEach(manager => manager.Tick());
                    TickNet();

                    int ticked = 0;
                    // Allow up to 3 ticks per net cycle to smoothly absorb OS timer sleep jitter
                    while (realTime > 0 && ticked < 3)
                    {
                        // Zero-allocation flat iteration across players
                        var players = playerManager.Players;
                        int maxExtrapolated = 0;
                        int maxTicksBehind = 0;
                        bool anyIngame = false;
                        string criticalLagPlayer = null!;

                        for (int i = 0; i < players.Count; i++)
                        {
                            var p = players[i];
                            if (!p.IsPlaying) continue;
                            if (p.status != PlayerStatus.Playing) continue;

                            anyIngame = true;
                            int extrap = p.ExtrapolatedTicksBehind;
                            if (extrap > maxExtrapolated)
                            {
                                maxExtrapolated = extrap;
                                if (extrap > LagCriticalThresholdTicks)
                                    criticalLagPlayer = p.Username;
                            }

                            if (p.ticksBehind > maxTicksBehind)
                                maxTicksBehind = p.ticksBehind;
                        }

                        // Hard pause only on critical lag or freeze, requiring at least one fully joined playing player
                        bool canAdvance = !freezeManager.Frozen && anyIngame && maxExtrapolated <= LagCriticalThresholdTicks;

                        if (canAdvance)
                        {
                            gameTimer++;
                            sentCmdsSnapshot = commands.SentCmds;

                            // Gentle linear slowdown between LagSlowdownMinTicks and LagSlowdownMaxTicks
                            float slowdown = 0f;
                            if (maxExtrapolated > LagSlowdownMinTicks)
                            {
                                float lagProgress = (float)(maxExtrapolated - LagSlowdownMinTicks) / (LagSlowdownMaxTicks - LagSlowdownMinTicks);
                                slowdown = Math.Min(lagProgress * MaxSlowdownMultiplier, MaxSlowdownMultiplier);
                            }

                            realTime -= serverTimePerTick * (1f + slowdown);
                            ticked++;
                        }
                        else
                        {
                            // Throttle logging to once per second to avoid IO stall
                            if (criticalLagPlayer != null && NetTimer - lastPauseLogTimer >= NetTicksPerSecond)
                            {
                                lastPauseLogTimer = NetTimer;
                                ServerLog.Log($"Simulation paused because player is too far behind: {criticalLagPlayer} ({maxExtrapolated} ticks)");
                            }

                            // Discard accumulated time during pause so the server does not fast-forward on resume
                            realTime = 0;
                            break;
                        }
                    }

                    if (realTime > 0)
                        realTime = 0f;

                    if (MpVersion.IsDebug && tickTime.ElapsedMillisDouble() > 15f)
                        ServerLog.Log($"Server tick took {tickTime.ElapsedMillisDouble()}ms");

                    // On Windows, the clock ticks 64 times a second and sleep durations too close to a multiple of 15.625ms
                    // tend to be rounded up, so we sleep for a bit less
                    int sleepFor = (int)Math.Floor((1000d / NetTicksPerSecond - tickTime.ElapsedMillisDouble()) * 0.9f);
                    if (sleepFor > 0)
                        Thread.Sleep(sleepFor);
                }
                catch (Exception e)
                {
                    ServerLog.Log($"Exception ticking the server: {e}");
                }
            }

            try
            {
                TryStop();
            }
            catch (Exception e)
            {
                ServerLog.Log($"Exception stopping the server: {e}");
            }
        }

        private void TickNet()
        {
            NetTimer++;

            if (NetTimer % NetTicksPerSecond == 0)
                playerManager.SendLatencies();

            if (NetTimer % (NetTicksPerSecond / 5) == 0)
            {
                var players = playerManager.Players;
                for (int i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    if (player.HasJoined)
                        player.SendKeepAlivePacket();
                }
            }

            // Send to simulating players as well to update the simulation window for them and actually update further
            // during the same simulation.
            SendToPlaying(new ServerTimeControlPacket(gameTimer, sentCmdsSnapshot, serverTimePerTick), false);

            float maxFrameTime = 0f;
            var playerList = playerManager.Players;
            for (int i = 0; i < playerList.Count; i++)
            {
                var p = playerList[i];
                if (p.IsPlaying && p.status == PlayerStatus.Playing && p.frameTime > maxFrameTime)
                {
                    maxFrameTime = p.frameTime;
                }
            }

            serverTimePerTick = maxFrameTime;

            if (serverTimePerTick < StandardTimePerTick)
                serverTimePerTick = StandardTimePerTick;

            if (serverTimePerTick > StandardTimePerTick * 4f)
                serverTimePerTick = StandardTimePerTick * 4f;
        }

        public void TryStop()
        {
            ServerLog.Detail("Server shutting down...");

            playerManager.OnServerStop();
            netManagers.ForEach(manager => manager.Stop());

            instance = null;
        }

        public void Enqueue(Action action)
        {
            queue.Enqueue(action);
        }

        public void SendToPlaying<T>(T packet, bool reliable = true, ServerPlayer? excluding = null) where T : IPacket
        {
            var serialized = packet.Serialize();
            var players = playerManager.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player.IsPlaying && player != excluding)
                    player.conn.Send(serialized, reliable);
            }
        }

        public void SendToIngame<T>(T packet, bool reliable = true, ServerPlayer? excluding = null) where T : IPacket
        {
            var serialized = packet.Serialize();
            var players = playerManager.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player.IsPlaying && player.status == PlayerStatus.Playing && player != excluding)
                    player.conn.Send(serialized, reliable);
            }
        }

        public bool CanUseStandaloneMapStreaming(int mapId) => false;

        public void SendMapResponse(ServerPlayer player, int mapId)
        {
            if (!CanUseStandaloneMapStreaming(mapId))
                return;

            ByteWriter writer = new ByteWriter();
            writer.WriteInt32(mapId);

            var mapCmds = worldData.mapCmds.GetValueSafe(mapId) ?? [];
            writer.WriteInt32(mapCmds.Count);
            foreach (var cmd in mapCmds)
                writer.WritePrefixedBytes(cmd);

            writer.WritePrefixedBytes(worldData.mapData[mapId]);
            player.conn.SendFragmented(Packets.Server_MapResponse, writer.ToArray());
        }

        public ServerPlayer? GetPlayer(string username)
        {
            return playerManager.GetPlayer(username);
        }

        public ServerPlayer? GetPlayer(int id)
        {
            return playerManager.GetPlayer(id);
        }

        public void SendChat(string msg)
        {
            ServerLog.Detail($"[Chat] {msg}");
            SendToPlaying(ServerChatPacket.Create(msg));
        }

        public void SendNotification(string key, params string[] args) =>
            SendToPlaying(new ServerNotificationPacket(key) { args = args });

        public void RegisterChatCommand(string commandName, IChatCommand command) =>
            chatCmdManager.AddCommand(commandName, command);

        public void RegisterChatCommand(string[] commandNames, IChatCommand command, string description = "", string usage = "", bool requiresHost = false) =>
            chatCmdManager.AddCommands(commandNames, command, description, usage, requiresHost);

        [Obsolete("Use RegisterChatCommand instead.")]
        public void RegisterChatCmd(string cmdName, ChatCmdHandler handler) =>
            RegisterChatCommand(cmdName, handler);

        public void HandleChatCommand(IChatSource source, string command) => chatCmdManager.Handle(source, command);

        [Obsolete("Use HandleChatCommand instead.")]
        public void HandleChatCmd(IChatSource source, string cmd) => HandleChatCommand(source, cmd);

        public Task<ServerInitData?> InitDataTask() => initDataSource.Task;

        public bool BootstrapMode { get; set; }

        /// Can only start one init data at a time. A StartInitData is considered complete once
        /// TaskCompletionResult.SetResult is called. Until that time no new calls to StartInitData will succeed.
        public TaskCompletionSource<ServerInitData?> StartInitData()
        {
            if (InitDataState != InitDataState.Waiting)
                throw new InvalidOperationException($"Can't start init data in state {InitDataState}");
            return initDataSource = new TaskCompletionSource<ServerInitData?>();
        }
    }

    public enum InitDataState
    {
        Waiting,
        Requested,
        Complete
    }
}
