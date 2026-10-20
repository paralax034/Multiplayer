using System.Collections.Generic;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

public class RecordingConnection : ConnectionBase
{
    public List<Packets> SentPackets { get; } = new();

    public RecordingConnection(string username)
    {
        this.username = username;
    }

    public override int Latency { get => 0; set { } }

    protected override void SendRaw(byte[] raw, int offset, int length, bool reliable = true)
    {
        if (length == 0)
            return;

        SentPackets.Add((Packets)(raw[offset] & 0x3F));
    }

    protected override void OnClose(ServerDisconnectPacket? goodbye) { }
}