using System.Linq;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Common
{
    public class FreezeManager(MultiplayerServer server)
    {
        private bool frozen;

        public bool Frozen
        {
            get => frozen;
            private set
            {
                frozen = value;
                Server.SendToPlaying(new ServerFreezePacket(frozen, Server.gameTimer));
            }
        }

        public MultiplayerServer Server { get; } = server;

        private const int MaxFreezeWaitTime = GenTicks.TicksPerRealSecond * 10; // 10 seconds

        public void Tick()
        {
            ServerPlayer hostPlayer = null!;
            bool anyPlayingFrozen = false;
            bool anyPlaying = false;

            var players = Server.playerManager.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p.IsPlaying)
                {
                    anyPlaying = true;
                    if (p.IsHost)
                        hostPlayer = p;
                    if (p.frozen)
                        anyPlayingFrozen = true;
                }
            }

            if (hostPlayer != null)
            {
                // Host is present: freeze/unfreeze follows the host's state
                if (!Frozen && hostPlayer.frozen)
                    Frozen = true;

                if (Frozen && !hostPlayer.frozen && (!anyPlayingFrozen || Server.NetTimer - hostPlayer.unfrozenAt > MaxFreezeWaitTime))
                    Frozen = false;
            }
            else
            {
                // Host is absent: unfreeze if any player is active
                if (Frozen && anyPlaying)
                    Frozen = false;
            }
        }
    }
}
