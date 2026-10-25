using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

static class Program
{
    private static int _passed;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action test)
    { test(); Console.WriteLine("PASS " + name); _passed++; }

    public static void Main()
    {
        Test("replaced state moves behind intervening reliable events", StateOrdering);
        Test("timestamped UDP state rejects late older samples", OutOfOrderState);
        Test("seven-player state burst retains every transaction and releases every buffer once", StateFlood);
        Test("streamed drops retain removals across empty frames and reset correctly", StreamedDrops);
        Test("disconnect removes only that peer's packets", DisconnectCleanup);
        Test("snapshot budget rotates fairly across eight peers", SnapshotFairness);
        Test("congestion defers work without losing or duplicating requests", SnapshotCongestion);
        Test("reset/disconnect/re-enqueue cannot complete a replacement job", SnapshotLifecycle);
        Test("real LiteNetLib loopback: seven clients, time budget, retained readers and exceptions", Loopback);
        Test("unreachable-host errors are aggregated", NetworkErrors);
        Console.WriteLine($"All {_passed} network scheduling checks passed.");
    }

    private static void StateOrdering()
    {
        var released = new List<int>();
        var queue = new OrderedPacketQueue<string, int>(released.Add);
        queue.EnqueueLatest("pose", 1);
        queue.Enqueue(2); // health/shot event
        queue.EnqueueLatest("pose", 3);
        queue.Enqueue(4); // snapshot
        Check(released.SequenceEqual(new[] { 1 }), "Superseded sample was not recycled");
        var actual = new List<int>();
        while (queue.TryDequeue(out var packet)) { actual.Add(packet); released.Add(packet); }
        Check(actual.SequenceEqual(new[] { 2, 3, 4 }), "Reliable ordering changed");
        Check(released.Distinct().Count() == 4, "Buffer ownership lost");
    }

    private static void OutOfOrderState()
    {
        var released = new List<int>();
        var queue = new OrderedPacketQueue<int, int>(released.Add);
        queue.EnqueueLatest(1, 20, 2);
        queue.Enqueue(30);
        queue.EnqueueLatest(1, 10, 1);
        Check(queue.TryDequeue(out var state) && state == 20, "Delayed UDP packet replaced a newer pose");
        Check(queue.TryDequeue(out var reliable) && reliable == 30, "Rejected state moved a reliable event");
        Check(released.SequenceEqual(new[] { 10 }), "Old UDP buffer leaked");
    }

    private static void StreamedDrops()
    {
        var stream = new SnapshotReceiveQueue<int, int>();
        stream.Receive(Array.Empty<int>(), true, false);
        Check(stream.ConsumeReset(), "Missing initial reset");
        stream.FinishFrame();
        Check(stream.InProgress, "Empty first chunk incorrectly ended the stream");
        stream.Remove(42); // Another player picks up before this drop is instantiated.
        stream.Receive(new[] { 42, 43 }, false, true);
        Check(stream.Dequeue() == 42 && stream.IsRemoved(42), "Picked-up drop would be resurrected");
        stream.FinishFrame();
        Check(stream.InProgress && stream.IsRemoved(42), "Removal expired before queued items finished");
        Check(stream.Dequeue() == 43 && !stream.IsRemoved(43), "Unrelated drop was suppressed");
        stream.FinishFrame();
        Check(!stream.InProgress, "Completed stream remained busy");
        stream.Receive(Array.Empty<int>(), false, false);
        stream.FinishFrame();
        Check(stream.InProgress, "Previous IsLast leaked into a new non-reset stream");
        stream.Remove(44);
        stream.Receive(new[] { 44 }, true, true);
        Check(stream.ConsumeReset() && !stream.ConsumeReset(), "Reset not consumed exactly once");
        Check(!stream.IsRemoved(44) && stream.Dequeue() == 44, "New authoritative reset retained old tombstones");
        stream.Clear();
        Check(!stream.InProgress && stream.Count == 0 && !stream.ConsumeReset(), "Scene reset left pending data");
    }

    private static void StateFlood()
    {
        var released = new HashSet<int>();
        void Release(int packet) => Check(released.Add(packet), "Double recycle " + packet);
        var queue = new OrderedPacketQueue<(int Peer, int Entity, int Op), int>(Release);
        var events = new List<int>();
        var sequence = 0;
        for (var sample = 0; sample < 1000; sample++)
            for (var peer = 0; peer < 7; peer++)
            {
                queue.EnqueueLatest((peer, 1, 3), sequence++);
                queue.EnqueueLatest((peer, 2, 3), sequence++);
                queue.EnqueueLatest((peer, 1, 4), sequence++);
                if (sample % 100 == 0) { events.Add(sequence); queue.Enqueue(sequence++); }
            }
        Check(queue.Count == 21 + 70, "State backlog grew with historical samples");
        var delivered = new List<int>();
        while (queue.TryDequeue(out var packet)) { delivered.Add(packet); Release(packet); }
        Check(delivered.Where(events.Contains).SequenceEqual(events), "Transactions lost/reordered");
        Check(released.Count == sequence, "A retained reader leaked");
        Console.WriteLine($"  21000 state samples -> 21 latest samples; all 70 reliable events retained.");
    }

    private static void DisconnectCleanup()
    {
        var released = new List<(int Peer, int Id)>();
        var queue = new OrderedPacketQueue<int, (int Peer, int Id)>(released.Add);
        queue.Enqueue((1, 1)); queue.Enqueue((2, 2)); queue.EnqueueLatest(1, (1, 3));
        queue.RemoveWhere(p => p.Peer == 1);
        Check(queue.Count == 1 && released.Count == 2, "Wrong peer removed");
        queue.EnqueueLatest(1, (1, 4));
        queue.Clear();
        Check(released.Count == 4 && released.Distinct().Count() == 4, "Clear/reuse leaked buffers");
    }

    private static void SnapshotFairness()
    {
        var jobs = new SnapshotWorkQueue<int, int>();
        for (var peer = 0; peer < 8; peer++) jobs.Add(peer, 100);
        var served = new List<int>(); double now = 0;
        for (var frame = 0; frame < 4; frame++)
            jobs.Tick(_ => true, _ => false, (peer, work) =>
            { served.Add(peer); now += .0015; return false; }, () => now, .002);
        Check(served.SequenceEqual(Enumerable.Range(0, 8)), "Early peers starved later peers");
    }

    private static void SnapshotCongestion()
    {
        var jobs = new SnapshotWorkQueue<int, Queue<int>>();
        var waiting = new Queue<int>(new[] { 1, 2 });
        jobs.Add(1, waiting); jobs.Add(2, new Queue<int>(new[] { 3 }));
        Check(!jobs.Add(1, new Queue<int>()), "Duplicate request replaced pending work");
        var delivered = new List<int>(); bool congested = true;
        bool Send(int peer, Queue<int> work) { delivered.Add(work.Dequeue()); return work.Count == 0; }
        jobs.Tick(_ => true, peer => peer == 1 && congested, Send, () => 0, .002);
        Check(delivered.SequenceEqual(new[] { 3 }) && jobs.Contains(1), "Congested request dropped or blocked another peer");
        congested = false;
        jobs.Tick(_ => true, _ => false, Send, () => 0, .002);
        Check(delivered.SequenceEqual(new[] { 3, 1 }), "More than one chunk sent per peer per frame");
        jobs.Tick(_ => true, _ => false, Send, () => 0, .002);
        Check(delivered.SequenceEqual(new[] { 3, 1, 2 }) && jobs.Count == 0, "Deferred request failed to finish");
    }

    private static void SnapshotLifecycle()
    {
        var jobs = new SnapshotWorkQueue<int, int>(); jobs.Add(1, 1); jobs.Add(2, 2);
        var oldFrameCalls = 0;
        jobs.Tick(_ => true, _ => false, (peer, work) =>
        { oldFrameCalls++; jobs.Clear(); jobs.Add(1, 10); return true; }, () => 0, .002);
        Check(oldFrameCalls == 1, "Old frame processed new scene work after reset");
        Check(jobs.TryGet(1, out var replacement) && replacement == 10, "Old completion removed new job");
        var calls = 0;
        jobs.Tick(_ => false, _ => false, (_, _) => { calls++; return false; }, () => 0, .002);
        Check(calls == 0 && jobs.Count == 0, "Disconnected peer was sent work");
        jobs.Add(1, 1); jobs.Remove(1); jobs.Add(1, 2);
        jobs.Tick(_ => true, _ => false, (_, _) => { calls++; return false; }, () => 0, .002);
        Check(calls == 1, "Removed peer retained an extra turn");
    }

    private sealed class Listener : ICoopNetworkListener
    {
        public readonly List<CoopPeer> Peers = new();
        public Action<CoopPeer, CoopPacketReader> Receive;
        public int Errors;
        public void OnPeerConnected(CoopPeer peer) => Peers.Add(peer);
        public void OnPeerDisconnected(CoopPeer peer, DisconnectInfo info) => Peers.Remove(peer);
        public void OnNetworkReceive(CoopPeer peer, CoopPacketReader reader, byte channel, DeliveryMethod delivery) => Receive?.Invoke(peer, reader);
        public void OnNetworkReceiveUnconnected(IPEndPoint endpoint, CoopPacketReader reader, UnconnectedMessageType type) { }
        public void OnNetworkError(IPEndPoint endpoint, SocketError error) => Errors++;
        public void OnNetworkLatencyUpdate(CoopPeer peer, int latency) { }
        public void OnConnectionRequest(ConnectionRequest request) => request.AcceptIfKey("verification");
    }

    private static void Until(Func<bool> done, Action tick, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(8)) { tick(); Thread.Sleep(2); }
        Check(done(), failure);
    }

    private static void Loopback()
    {
        double clock = 0;
        var hostListener = new Listener();
        var host = new LiteNetTransport(hostListener, () => clock);
        var clients = new List<(LiteNetTransport Transport, Listener Listener)>();
        var delivered = new Dictionary<CoopPeer, List<int>>();
        var latest = new Dictionary<(CoopPeer Peer, string Player), int>();
        var handled = 0; var poseCallbacks = 0; var threw = false;
        hostListener.Receive = (peer, reader) =>
        {
            clock += .0025; // Deterministic simulation of an expensive RPC.
            var op = reader.GetByte();
            if (op == (byte)Op.POSITION_UPDATE)
            { poseCallbacks++; latest[(peer, reader.GetString())] = reader.GetInt(); return; }
            var seq = reader.GetInt();
            if (!delivered.TryGetValue(peer, out var list)) delivered[peer] = list = new();
            // Validate the retained native buffer after many later PollEvents.
            for (var i = 0; i < 64; i++) Check(reader.GetByte() == (byte)(seq + i), "Native packet recycled before dispatch");
            list.Add(seq); handled++;
            if (!threw) { threw = true; throw new InvalidOperationException("expected test handler failure"); }
        };
        try
        {
            Check(host.Start(0), "Host failed to bind loopback UDP");
            for (var i = 0; i < 7; i++)
            {
                var listener = new Listener(); var client = new LiteNetTransport(listener);
                Check(client.Start(0), "Client failed to bind"); clients.Add((client, listener));
                var key = new NetDataWriter(); key.Put("verification"); client.Connect("127.0.0.1", host.LocalPort, key);
            }
            void PollAll()
            {
                var before = handled + poseCallbacks;
                host.PollEvents();
                Check(handled + poseCallbacks - before <= 2, "Receive work exceeded deterministic 4ms budget");
                foreach (var client in clients) client.Transport.PollEvents();
            }
            Until(() => hostListener.Peers.Count == 7 && clients.All(c => c.Listener.Peers.Count == 1), PollAll, "Connect timeout");
            foreach (var client in clients)
                for (var seq = 0; seq < 40; seq++)
                {
                    var writer = new NetDataWriter(); writer.Put((byte)Op.ITEM_PICKUP_REQUEST); writer.Put(seq);
                    for (var i = 0; i < 64; i++) writer.Put((byte)(seq + i));
                    client.Listener.Peers[0].Send(writer, DeliveryMethod.ReliableOrdered);
                }
            Thread.Sleep(100); // Accumulate a burst before the first host dispatch.
            PollAll();
            Check(handled > 0 && handled <= 2, "Reliable burst was drained in a single frame");
            Until(() => handled == 280, PollAll, "Reliable burst failed to drain");
            Check(delivered.Count == 7 && delivered.Values.All(v => v.SequenceEqual(Enumerable.Range(0, 40))), "Reliable messages lost/reordered across deferred frames");
            foreach (var client in clients)
                for (var seq = 0; seq < 100; seq++)
                    foreach (var player in new[] { "player-A", "player-B" })
                    {
                        var writer = PositionPacket(player, seq);
                        client.Listener.Peers[0].Send(writer, DeliveryMethod.Unreliable);
                    }
            // Deliberately send older samples LAST, simulating UDP reordering.
            foreach (var client in clients)
                foreach (var player in new[] { "player-A", "player-B" })
                    client.Listener.Peers[0].Send(PositionPacket(player, 50), DeliveryMethod.Unreliable);
            Thread.Sleep(100);
            Until(() => latest.Count == 14 && latest.Values.All(v => v == 99), PollAll, "Latest state/peer identity was lost");
            Check(UnityEngine.Debug.Errors.Count(e => e.Contains("expected test handler failure")) == 1, "Handler exception was not contained");
            Console.WriteLine($"  7 real UDP clients: 280/280 reliable messages in order; {poseCallbacks} pose callbacks for 1414 samples, all 14 entities current.");
            host.Stop(); host.PollEvents();
        }
        finally
        {
            foreach (var client in clients) client.Transport.Stop();
            host.Stop();
        }
    }

    private static NetDataWriter PositionPacket(string player, int sequence)
    {
        var writer = new NetDataWriter(); writer.Put((byte)Op.POSITION_UPDATE); writer.Put(player);
        writer.Put(sequence); writer.Put(0); writer.Put(0); // cm position
        writer.Put((ushort)0); writer.Put((ushort)0); // direction
        writer.Put(0f); writer.Put(0f); writer.Put(0f); // velocity
        writer.Put((double)sequence); writer.Put(0); // timestamp, vehicle
        return writer;
    }

    private static void NetworkErrors()
    {
        var listener = new Listener(); double now = 0;
        var transport = new LiteNetTransport(listener, () => now);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);
        for (var i = 0; i < 100; i++) transport.OnNetworkError(endpoint, SocketError.HostUnreachable);
        Check(listener.Errors == 1, "Repeated errors still spam the Unity logger");
        now = 5; transport.OnNetworkError(endpoint, SocketError.HostUnreachable);
        Check(listener.Errors == 2 && UnityEngine.Debug.Warnings.Last().Contains("99"), "Error summary missing");
        transport.Stop();
    }
}
