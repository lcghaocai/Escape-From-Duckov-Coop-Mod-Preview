using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Debug = UnityEngine.Debug;

namespace EscapeFromDuckovCoopMod;

internal sealed class LiteNetTransport : CoopNetworkManager, INetEventListener
{
    private const double ReceiveBudgetSeconds = 0.004;
    private const int MaxPacketsPerPoll = 512;
    private readonly LiteNetLib.NetManager _manager;
    private readonly Dictionary<LiteNetLib.NetPeer, LiteNetPeer> _peers = new();
    private readonly List<CoopPeer> _connected = new();
    private readonly ReceiveWorkQueue<(CoopPeer Peer, byte Channel, byte Op, string Player, int Ai), PendingPacket> _received
        = new(packet => packet.Reader.Recycle());
    private readonly Func<double> _now;
    private double _nextErrorAt;
    private double _nextBacklogReportAt;
    private int _suppressedErrors;

    private readonly struct PendingPacket
    {
        public readonly LiteNetPeer Peer;
        public readonly LiteNetLib.NetPacketReader Reader;
        public readonly byte Channel;
        public readonly DeliveryMethod Delivery;
        public PendingPacket(LiteNetPeer peer, LiteNetLib.NetPacketReader reader, byte channel, DeliveryMethod delivery)
        { Peer = peer; Reader = reader; Channel = channel; Delivery = delivery; }
    }

    public LiteNetTransport(ICoopNetworkListener listener, Func<double> now = null, LiteNetLib.Layers.PacketLayerBase packetLayer = null) : base(listener)
    {
        _now = now ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        _manager = new LiteNetLib.NetManager(this, packetLayer)
        {
            BroadcastReceiveEnabled = true,
            UpdateTime = 1,
            PingInterval = 500,
            ChannelsCount = DirectMessageChannels.Count,
            EnableStatistics = true,
            AutoRecycle = false // Received buffers remain owned until budgeted dispatch.
        };
    }

    public override bool IsRunning => _manager.IsRunning;
    public override int LocalPort => _manager.LocalPort;
    public override IReadOnlyList<CoopPeer> ConnectedPeerList => _connected;
    public override NetStatistics Statistics => _manager.Statistics;
    public override bool Start(int port = 0) => _manager.Start(port);
    public override void Stop()
    {
        _manager.Stop();
        _received.Clear();
        _peers.Clear();
        _connected.Clear();
        _nextErrorAt = _nextBacklogReportAt = 0;
        _suppressedErrors = 0;
    }
    public override void PollEvents()
    {
        var deadline = _now() + ReceiveBudgetSeconds;
        // Native polling only queues payloads. Expensive Unity/RPC work below has
        // the same four millisecond budget used by the Steam transport.
        _manager.PollEvents();
        for (var i = 0; IsRunning && i < MaxPacketsPerPoll; i++)
        {
            if (i > 0 && _now() >= deadline) break;
            if (!_received.TryDequeue(out var packet)) break;
            try
            {
                if (packet.Peer.ConnectionState == ConnectionState.Connected)
                    Listener.OnNetworkReceive(packet.Peer,
                        new CoopPacketReader(packet.Reader.RawData, packet.Reader.Position, packet.Reader.AvailableBytes),
                        packet.Channel, packet.Delivery);
            }
            catch (Exception ex) { Debug.LogError($"[Direct] Receive failed: {ex}"); }
            finally { packet.Reader.Recycle(); }
        }
        if (_received.Count > 0 && _now() >= _nextBacklogReportAt)
        {
            _nextBacklogReportAt = _now() + 5;
            CoopLogSystem.WriteNetworkDiagnostic($"[Direct] Receive backlog={_received.Count} combat={_received.CombatCount} coalescedStates={_received.CoalescedCount} budgetMs=4");
        }
    }
    public override int GetReliableQueueLength(CoopPeer peer) => peer is LiteNetPeer direct ? direct.ReliableQueueLength : -1;
    public override void TriggerUpdate() => _manager.TriggerUpdate();
    public override void Connect(string address, int port, NetDataWriter data) => _manager.Connect(address, port, data);
    public override void SendUnconnectedMessage(NetDataWriter data, IPEndPoint endpoint) => _manager.SendUnconnectedMessage(data, endpoint);
    public override void SendBroadcast(NetDataWriter data, int port) => _manager.SendBroadcast(data, port);

    public void OnPeerConnected(LiteNetLib.NetPeer peer)
    {
        var wrapper = new LiteNetPeer(peer);
        _peers.Add(peer, wrapper);
        _connected.Add(wrapper);
        Listener.OnPeerConnected(wrapper);
    }
    public void OnPeerDisconnected(LiteNetLib.NetPeer peer, DisconnectInfo info)
    {
        if (!_peers.TryGetValue(peer, out var wrapper))
            wrapper = new LiteNetPeer(peer);
        _received.RemoveWhere(packet => packet.Peer == wrapper);
        _peers.Remove(peer);
        _connected.Remove(wrapper);
        Listener.OnPeerDisconnected(wrapper, info);
    }
    public void OnNetworkReceive(LiteNetLib.NetPeer peer, LiteNetLib.NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        if (!_peers.TryGetValue(peer, out var wrapper)) { reader.Recycle(); return; }
        var packet = new PendingPacket(wrapper, reader, channel, delivery);
        // Coalesce only replaceable state, never shots, damage, spawns, inventory
        // operations or snapshots. The peer AND entity are part of the key.
        if (delivery == DeliveryMethod.Unreliable || delivery == DeliveryMethod.Sequenced)
        {
            try
            {
                var keyReader = new CoopPacketReader(reader.RawData, reader.Position, reader.AvailableBytes);
                var op = keyReader.GetByte();
                if (op == (byte)Op.POSITION_UPDATE || op == (byte)Op.ANIM_SYNC)
                {
                    var player = keyReader.GetString(128);
                    // Position wire layout after the endpoint: cm position (12),
                    // quantized forward (4), velocity (12), timestamp (8), vehicle (4).
                    var order = op == (byte)Op.POSITION_UPDATE && keyReader.AvailableBytes >= 36
                        ? BitConverter.ToDouble(reader.RawData, keyReader.Position + 28) : double.NaN;
                    if (double.IsInfinity(order)) order = double.NaN;
                    _received.EnqueueLatest((wrapper, channel, op, player, 0), packet, order);
                    return;
                }
                if (op == (byte)Op.AI_STATE_UPDATE)
                {
                    var id = keyReader.GetInt();
                    // AIStateUpdateRpc has 80 bytes of pose/health/animation after
                    // its id, followed by the authoritative double timestamp.
                    var order = keyReader.AvailableBytes >= 88
                        ? BitConverter.ToDouble(reader.RawData, keyReader.Position + 80) : double.NaN;
                    if (double.IsInfinity(order)) order = double.NaN;
                    _received.EnqueueLatest((wrapper, channel, op, null, id), packet, order);
                    return;
                }
            }
            catch { /* Malformed payloads go through the normal RPC validation. */ }
        }
        // Do not trust arbitrary channel-one packets to receive combat priority.
        var combat = channel == DirectMessageChannels.Combat && reader.AvailableBytes > 0 &&
            DirectMessageChannels.Select(reader.RawData[reader.Position], delivery) == DirectMessageChannels.Combat;
        _received.Enqueue(packet, combat);
    }
    public void OnNetworkReceiveUnconnected(IPEndPoint endpoint, LiteNetLib.NetPacketReader reader, UnconnectedMessageType type)
    {
        try { Listener.OnNetworkReceiveUnconnected(endpoint, new CoopPacketReader(reader.RawData, reader.Position, reader.AvailableBytes), type); }
        finally { reader.Recycle(); }
    }
    public void OnNetworkLatencyUpdate(LiteNetLib.NetPeer peer, int latency)
    {
        if (_peers.TryGetValue(peer, out var wrapper)) Listener.OnNetworkLatencyUpdate(wrapper, latency);
    }
    public void OnNetworkError(IPEndPoint endpoint, SocketError error)
    {
        // Unreachable hosts can produce dozens of errors (and Unity stack traces)
        // per second. Preserve the error and aggregate repetitions.
        var now = _now();
        if (now < _nextErrorAt) { _suppressedErrors++; return; }
        _nextErrorAt = now + 5;
        if (_suppressedErrors > 0)
            CoopLogSystem.WriteNetworkDiagnostic($"[Direct] Suppressed {_suppressedErrors} network errors in the previous interval.");
        _suppressedErrors = 0;
        Listener.OnNetworkError(endpoint, error);
    }
    public void OnConnectionRequest(ConnectionRequest request) => Listener.OnConnectionRequest(request);
}
