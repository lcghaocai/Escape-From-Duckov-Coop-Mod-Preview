using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

static partial class Program
{
    private static int _passed;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action test)
    { test(); Console.WriteLine("PASS " + name); _passed++; }

    public static void Main()
    {
        Test("Pick All and concurrent distinct slots progress without allowing same-slot replays", IndependentLootTakes);
        Test("take request/receipt/denial and legacy wire boundaries", LootTakeWire);
        Test("transient audio expires, stays bounded and preserves transactions", TransientAudioQueue);
        Test("take delivery handles replay, reentrancy, failure and delayed receipts", TakeDeliveryExactlyOnce);
        Test("receive diagnostics retain ownership and age across coalescing/reset", ReceiveDiagnostics);
        Test("per-peer pressure isolates clients and restores quality after recovery/timeout", PressureRecovery);
        Test("entity recovery limits burst, repeated IDs and reconnect state", RecoveryRateLimit);
        Test("pressure and single-entity request wire boundaries round-trip", RecoveryWire);
        Test("ordered buff snapshots preserve empty/null/populated wire boundaries", BuffSnapshotWire);
        Test("combat lane preserves order, ordinary progress and buffer ownership", CombatFairness);
        Test("AI refresh reuses per-peer metadata and carries live state", CompactRefresh);
        Test("real UDP: combat arrives while bulk fragments are repeatedly lost", CombatThroughFragmentLoss);
        Test("player health envelopes preserve null buffs and timestamp boundaries", PlayerHealthWireFormat);
        Test("health snapshots and unordered broadcasts use capture order", HealthCaptureOrder);
        Test("same-frame health changes retain the final state", SameFrameHealth);
        Test("AI metadata delay retains hits and kill credit while coalescing health", PendingHealth);
        Test("death permit survives delayed living reports", DeathOrdering);
        Test("AI snapshot wire tails preserve multi-entry and legacy boundaries", SnapshotWireFormat);
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

    private static void PlayerHealthWireFormat()
    {
        var populated = new[] {
            new PlayerBuffState { WeaponTypeId = 91, BuffId = 6, Layers = 3 },
            new PlayerBuffState { WeaponTypeId = 92, BuffId = 7, Layers = 2 }
        };
        var cases = new PlayerBuffState[][] { null, Array.Empty<PlayerBuffState>(), populated };
        var checks = 0;
        foreach (var buffs in cases)
        foreach (var stamp in new[] { 100.1, 0d, 4096d })
        foreach (var legacy in new[] { false, true })
        {
            void Verify(PlayerBuffState[] actual, double timestamp, CoopPacketReader reader)
            {
                Check((actual == null) == (buffs == null), "Null buffs must mean no reconciliation, not clear all buffs");
                if (buffs != null)
                {
                    Check(actual.Length == buffs.Length, "Buff count corrupted");
                    for (var i = 0; i < buffs.Length; i++)
                        Check(actual[i].WeaponTypeId == buffs[i].WeaponTypeId && actual[i].BuffId == buffs[i].BuffId &&
                            actual[i].Layers == buffs[i].Layers, "Buff entry corrupted");
                }
                Check(timestamp == (legacy ? 0d : stamp), "Buff reader consumed timestamp bytes");
                Check(reader.AvailableBytes == 0, "Health envelope left unread bytes");
                checks++;
            }
            CoopPacketReader Reader(NetDataWriter writer)
            {
                var bytes = writer.CopyData();
                // Pre-timestamp messages have the exact same prefix.
                return new CoopPacketReader(bytes, 0, bytes.Length - (legacy ? sizeof(double) : 0));
            }
            var report = new PlayerHealthReportRpc { MaxHealth = 100, CurrentHealth = 61,
                LifeId = "life", IsDead = false, Buffs = buffs, Timestamp = stamp };
            var writer = new NetDataWriter();
            report.Serialize(writer);
            var reader = Reader(writer);
            var decodedReport = new PlayerHealthReportRpc();
            decodedReport.Deserialize(reader);
            Check(decodedReport.CurrentHealth == 61 && decodedReport.LifeId == "life" && !decodedReport.IsDead,
                "Owner health/death report corrupted");
            Verify(decodedReport.Buffs, decodedReport.Timestamp, reader);

            var broadcast = new PlayerHealthBroadcastRpc { PlayerId = "peer", MaxHealth = 100,
                CurrentHealth = 61, Buffs = buffs, Timestamp = stamp };
            writer.Reset();
            broadcast.Serialize(writer);
            reader = Reader(writer);
            var decodedBroadcast = new PlayerHealthBroadcastRpc();
            decodedBroadcast.Deserialize(reader);
            Check(decodedBroadcast.PlayerId == "peer" && decodedBroadcast.CurrentHealth == 61, "Relay health corrupted");
            Verify(decodedBroadcast.Buffs, decodedBroadcast.Timestamp, reader);
        }
        Console.WriteLine($"  {checks} health envelopes: null/empty/populated buffs, stamped and legacy");
    }

    private static void HealthCaptureOrder()
    {
        var order = new HealthSnapshotOrder();
        // A pre-hit snapshot arrives after the newer unordered damage broadcast.
        Check(order.TryApply(1, order.Receive(), 20), "Initial health not accepted");
        Check(!order.TryApply(1, order.Receive(), 10), "Old snapshot overwrote newer health");
        // A newer broadcast received before model creation is applied after a snapshot.
        var pending = order.Receive();
        Check(order.TryApply(2, order.Receive(), 10), "Spawn snapshot not accepted");
        Check(order.TryApply(2, pending, 20), "Metadata delay discarded newer health");
        Check(!order.TryApply(2, order.Receive(), 20), "Duplicate capture accepted");
        Check(!order.TryApply(2, order.Receive()), "Unstamped confirmation reverted stamped health");
        Check(order.TryApply(3, 2) && !order.TryApply(3, 1), "Legacy ordered fallback broken");
        order.Clear();
        Check(order.TryApply(1, order.Receive(), 1), "Scene reset retained previous ordering");
    }

    private static void SameFrameHealth()
    {
        var clock = new HealthSyncClock();
        var stamps = Enumerable.Range(0, 100).Select(_ => clock.Next(50)).ToArray();
        Check(stamps.Zip(stamps.Skip(1), (a, b) => b > a).All(b => b), "Same-frame captures tied");
        var order = new HealthSnapshotOrder();
        var hp = 100;
        foreach (var i in Enumerable.Range(0, 100).Reverse())
            if (order.TryApply(1, order.Receive(), stamps[i])) hp = 99 - i;
        Check(hp == 0, "Out-of-order same-frame damage lost the lethal state");
        Check(clock.Next(1) > stamps[^1], "Clock restart regressed the relay timestamp");
    }

    private static void PendingHealth()
    {
        var pending = new PendingHealthUpdates<string>();
        pending.Add("newest state", 1, 30, false);
        pending.Add("hit one", 2, 10, true);
        pending.Add("kill credit", 3, 20, true);
        pending.Add("old broadcast", 4, 15, false);
        Check(pending.Message == "newest state" && pending.Order == 1, "Late older packet replaced pending state");
        Check(pending.Events.SequenceEqual(new[] { "hit one", "kill credit" }), "One-shot health events coalesced away");
    }

    private static void DeathOrdering()
    {
        var peer = new object();
        var gate = new PlayerDeathReportGate<object>();
        var order = new HealthSnapshotOrder();
        var life = Guid.NewGuid().ToString("N");
        void Receive(bool dead, double stamp)
        {
            if (order.TryApply(1, order.Receive(), stamp)) gate.Observe(peer, life, dead);
        }
        Receive(true, 20);
        Receive(false, 10);
        Check(gate.TryConsume(peer, life), "Delayed living report revoked death permit");
        Check(!gate.TryConsume(peer, life), "Death inventory consumed twice");
    }

    private static void SnapshotWireFormat()
    {
        var entries = new[]
        {
            new AISnapshotEntry { Id = 7, CurrentHealth = 91, HealthTimestamp = 10.5, MetadataVersion = 91,
                BuffIds = new[] { 4, 5 }, BuffWeaponTypeIds = new[] { 8, 9 }, BuffLayers = new[] { 2, 3 } },
            new AISnapshotEntry { Id = 8, CurrentHealth = 37, HealthTimestamp = 10.6, MetadataVersion = 92, BuffLayers = Array.Empty<int>() }
        };
        CoopPacketReader Reader(byte[] bytes) => new(bytes, 0, bytes.Length);
        foreach (var count in new[] { 0, 1, 2 })
        foreach (var useList in new[] { false, true })
        {
            var subset = entries.Take(count).ToArray();
            var chunk = new AISnapshotChunkRpc { Reset = true,
                Entries = useList ? null : subset, EntryList = useList ? subset : null, EntryCount = count };
            var writer = new NetDataWriter();
            chunk.Serialize(writer);
            var bytes = writer.CopyData();
            var reader = Reader(bytes);
            var parsed = new AISnapshotChunkRpc();
            parsed.Deserialize(reader);
            Check(reader.AvailableBytes == 0 && parsed.Entries.Length == count, "Chunk boundaries shifted");
            for (var i = 0; i < count; i++)
                Check(parsed.Entries[i].Id == subset[i].Id && parsed.Entries[i].CurrentHealth == subset[i].CurrentHealth &&
                    parsed.Entries[i].HealthTimestamp == subset[i].HealthTimestamp &&
                    parsed.Entries[i].MetadataVersion == subset[i].MetadataVersion &&
                    parsed.Entries[i].BuffLayers.SequenceEqual(subset[i].BuffLayers), "Health tail corrupted entry/buff data");

            var healthOnly = Reader(bytes.Take(bytes.Length - count * sizeof(int)).ToArray());
            parsed.Deserialize(healthOnly);
            Check(healthOnly.AvailableBytes == 0 && parsed.Entries.All(e => e.MetadataVersion == 0), "Health-only tail not readable");
            // Older hosts have buff layers but neither optional tail.
            var legacy = Reader(bytes.Take(bytes.Length - count * (sizeof(double) + sizeof(int))).ToArray());
            parsed.Deserialize(legacy);
            Check(legacy.AvailableBytes == 0 && parsed.Entries.All(e => e.HealthTimestamp == 0), "Legacy chunk not readable");
            // An older reader still consumes exactly the old prefix on a new message.
            var prefix = Reader(bytes);
            prefix.GetBool(); prefix.GetUShort();
            var oldEntries = new AISnapshotEntry[count];
            for (var i = 0; i < count; i++) oldEntries[i].Deserialize(prefix);
            for (var i = 0; i < count; i++) oldEntries[i].ReadBuffLayers(prefix);
            Check(prefix.AvailableBytes == count * (sizeof(double) + sizeof(int)), "New fields inserted into legacy entry layout");
        }
        var spawn = new AISpawnRpc { Entry = entries[0] };
        var spawnWriter = new NetDataWriter();
        spawn.Serialize(spawnWriter);
        var spawnBytes = spawnWriter.CopyData();
        var spawnReader = Reader(spawnBytes);
        var parsedSpawn = new AISpawnRpc();
        parsedSpawn.Deserialize(spawnReader);
        Check(spawnReader.AvailableBytes == 0 && parsedSpawn.Entry.HealthTimestamp == 10.5 && parsedSpawn.Entry.MetadataVersion == 91, "Spawn tail missing");
        parsedSpawn.Deserialize(Reader(spawnBytes.Take(spawnBytes.Length - sizeof(double) - sizeof(int)).ToArray()));
        Check(parsedSpawn.Entry.HealthTimestamp == 0 && parsedSpawn.Entry.BuffLayers.Length == 2, "Legacy spawn retained old timestamp");
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
            var backlog = host.GetReceiveQueueSnapshot();
            Check(backlog.Supported && backlog.Count > 0 && backlog.OldestWaitMs > 0, "Transport feedback missed deferred native readers");
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
            var cleared = host.GetReceiveQueueSnapshot();
            Check(cleared.Count == 0 && cleared.CombatCount == 0 && cleared.OldestWaitMs == 0, "Stopped transport retained pressure feedback");
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
