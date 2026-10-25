using System.Net;
using LiteNetLib.Layers;

static partial class Program
{
    private static void CombatFairness()
    {
        var freed = new List<int>();
        var queue = new ReceiveWorkQueue<int, int>(freed.Add);
        for (var i = 0; i < 12; i++) queue.Enqueue(i);
        for (var i = 0; i < 12; i++) queue.Enqueue(100 + i, true);
        var actual = new List<int>();
        while (queue.TryDequeue(out var value)) { actual.Add(value); freed.Add(value); }
        Check(actual.Take(8).SequenceEqual(new[] { 100, 101, 102, 0, 103, 104, 105, 1 }), "Combat not prioritized fairly");
        Check(actual.Where(x => x < 100).SequenceEqual(Enumerable.Range(0, 12)) &&
            actual.Where(x => x >= 100).SequenceEqual(Enumerable.Range(100, 12)), "Lane FIFO order changed");
        queue.EnqueueLatest(9, 200, 2);
        queue.EnqueueLatest(9, 201, 1); // Late older sample is released.
        queue.Enqueue(202, true);
        queue.RemoveWhere(x => x == 202); // Disconnect releases combat buffers too.
        queue.Clear();
        Check(freed.Count == 27 && freed.Distinct().Count() == 27 && queue.Count == 0, "Leaked or double-recycled buffer");
        foreach (var op in new[] { Op.PLAYER_HEALTH_REPORT, Op.PLAYER_DEAD_LOOT_SPAWN, Op.CORPSE_INVENTORY,
            Op.AI_SPAWN, Op.AI_DESPAWN, Op.AI_BUFF_BROADCAST, Op.AI_SNAPSHOT_CHUNK, Op.AI_REFRESH_CHUNK, Op.SHARED_HOME })
            Check(DirectMessageChannels.Select((byte)op, DeliveryMethod.ReliableOrdered) == 0, "Lifecycle/death ordering moved lanes");
        foreach (var op in new[] { Op.AI_HEALTH_REPORT, Op.AI_BUFF_REPORT, Op.PLAYER_DAMAGE_REQUEST,
            Op.PLAYER_DAMAGE_FORWARD, Op.PLAYER_BUFF_SELF_APPLY })
            Check(DirectMessageChannels.Select((byte)op, DeliveryMethod.ReliableOrdered) == 1, "Hit/buff request left behind bulk");
    }

    private static void CompactRefresh()
    {
        var original = new AISnapshotEntry { Id = 7, ModelName = "enemy", CustomFaceJson = new string('f', 2048),
            ScenePath = "farm", SpawnPosition = new Vector3(1, 2, 3), SpawnRotation = Quaternion.identity,
            Status = AIStatus.Active, Activated = true, IsVehicle = true, KeepOutsideRange = true, MaxHealth = 100, CurrentHealth = 100,
            EquipmentSlots = new[] { "head", "body" }, EquipmentItemTypeIds = new[] { 11, 12 },
            WeaponSlots = new[] { "gun" }, WeaponItemTypeIds = new[] { 22 } };
        var cache = new SnapshotMetadataCache<int>();
        var signature = AISnapshotMetadata.Signature(in original);
        for (var peer = 0; peer < 7; peer++)
        {
            Check(!cache.Matches(peer, original.Id, signature), "New peer inherited another baseline");
            cache.Record(peer, original.Id, signature);
        }
        var live = original;
        live.Status = AIStatus.Dead; live.Activated = false;
        live.CurrentHealth = 0; live.BodyArmor = 4; live.HeadArmor = 5;
        live.LastKnownPosition = new Vector3(40, 20, 30); live.LastKnownRotation = new Quaternion(0, 1, 0, 0);
        live.LastKnownVelocity = new Vector3(2, 3, 4); live.LastKnownRemoteTime = 20.25;
        live.ActiveWeaponSlot = "gun"; live.ActiveWeaponTypeId = 22;
        live.BuffIds = new[] { 9, 10 }; live.BuffWeaponTypeIds = new[] { 22, 23 }; live.BuffLayers = new[] { 2, 3 };
        live.HealthTimestamp = 21.5; live.MetadataVersion = signature;
        Check(AISnapshotMetadata.Signature(in live) == signature, "Movement/health/buffs invalidated static metadata");
        var writer = new NetDataWriter();
        new AISnapshotChunkRpc { Entries = new[] { live } }.Serialize(writer);
        var fullBytes = writer.Length;
        writer.Reset();
        new AIRefreshChunkRpc { Entries = new[] { live, live } }.Serialize(writer);
        var compactBytes = writer.Length;
        var reader = new CoopPacketReader(writer.CopyData(), 0, writer.Length);
        var decoded = new AIRefreshChunkRpc(); decoded.Deserialize(reader);
        Check(reader.AvailableBytes == 0 && decoded.Entries.Length == 2, "Compact entries crossed boundaries");
        foreach (var entry in decoded.Entries)
        {
            Check(entry.MetadataOmitted && entry.MetadataVersion == signature && entry.ModelName == null && entry.EquipmentSlots == null, "Compact packet carried metadata");
            Check(entry.Id == 7 && entry.IsVehicle && entry.KeepOutsideRange && entry.Status == AIStatus.Dead && !entry.Activated && entry.MaxHealth == 100 &&
                entry.CurrentHealth == 0 && entry.BodyArmor == 4 && entry.HeadArmor == 5 &&
                entry.LastKnownPosition.x == 40 && entry.LastKnownRotation.y == 1 && entry.LastKnownVelocity.z == 4 &&
                entry.LastKnownRemoteTime == 20.25 && entry.HealthTimestamp == 21.5 &&
                entry.ActiveWeaponSlot == "gun" && entry.ActiveWeaponTypeId == 22 &&
                entry.BuffIds.SequenceEqual(live.BuffIds) && entry.BuffWeaponTypeIds.SequenceEqual(live.BuffWeaponTypeIds) &&
                entry.BuffLayers.SequenceEqual(live.BuffLayers), "Compact refresh lost authoritative state");
        }
        Check(compactBytes < fullBytes, "Refresh still carries full metadata cost");
        writer.Reset();
        new AIRefreshChunkRpc { Entries = new[] { new AISnapshotEntry() } }.Serialize(writer);
        reader = new CoopPacketReader(writer.CopyData(), 0, writer.Length);
        decoded.Deserialize(reader);
        Check(reader.AvailableBytes == 0 && decoded.Entries.Length == 1, "Minimum compact entry rejected");
        writer.Reset();
        new AIRefreshChunkRpc { Entries = Array.Empty<AISnapshotEntry>() }.Serialize(writer);
        reader = new CoopPacketReader(writer.CopyData(), 0, writer.Length);
        decoded.Deserialize(reader);
        Check(reader.AvailableBytes == 0 && decoded.Entries.Length == 0, "Empty refresh rejected");
        var changed = original; changed.EquipmentItemTypeIds = new[] { 11, 999 };
        Check(!cache.Matches(0, 7, AISnapshotMetadata.Signature(in changed)), "Changed equipment did not force full metadata");
        cache.Remove(0);
        Check(!cache.Matches(0, 7, signature) && cache.Matches(1, 7, signature), "Recovery reset affected another player");
        cache.Clear();
        Check(!cache.Matches(1, 7, signature), "Scene reset retained metadata baseline");
        var received = new ReceivedSnapshotMetadata();
        var oldFull = received.Receive();
        Check(received.TryApplyFull(7, 1, oldFull), "Initial metadata missing");
        Check(!received.CanApplyRefresh(7, 2, received.Receive(), out var repair) && repair,
            "Dropped changed metadata silently reused old equipment");
        var queuedOldFull = received.Receive();
        Check(received.TryApplyFull(7, 3, received.Receive()), "New spawn metadata not applied");
        Check(!received.TryApplyFull(7, 2, queuedOldFull), "Deferred old snapshot reverted new spawn metadata");
        Check(!received.CanApplyRefresh(7, 2, queuedOldFull, out repair) && !repair,
            "Old queued refresh triggered redundant full recovery");
        Check(received.CanApplyRefresh(7, 3, received.Receive(), out repair) && !repair,
            "Matching compact metadata rejected");
        received.Clear();
        Check(!received.CanApplyRefresh(7, 3, received.Receive(), out repair) && repair,
            "Reconnection inherited a stale metadata baseline");
        Console.WriteLine($"  Fixture with 2 KiB face metadata: full={fullBytes} bytes; two compact entries={compactBytes} bytes.");
    }

    private sealed class HoldFragments : PacketLayerBase
    {
        public volatile bool Hold = true;
        public int Dropped;
        public HoldFragments() : base(0) { }
        public override void ProcessInboundPacket(ref IPEndPoint endpoint, ref byte[] data, ref int offset, ref int length)
        {
            // LiteNetLib marks fragmented channeled datagrams with bit 7.
            // Drop bulk fragments repeatedly, but continue delivering ACK/control/combat.
            if (Hold && length > 0 && (data[offset] & 0x80) != 0 && (data[offset] & 0x1f) == 1)
            { Interlocked.Increment(ref Dropped); length = 0; }
        }
        public override void ProcessOutBoundPacket(ref IPEndPoint endpoint, ref byte[] data, ref int offset, ref int length) { }
    }

    private static void CombatThroughFragmentLoss()
    {
        var layer = new HoldFragments();
        var hostListener = new Listener(); var clientListener = new Listener();
        var host = new LiteNetTransport(hostListener, packetLayer: layer);
        var client = new LiteNetTransport(clientListener);
        var hits = new List<int>(); var ordinary = new List<Op>();
        hostListener.Receive = (_, reader) =>
        {
            var op = (Op)reader.GetByte();
            if (op == Op.AI_HEALTH_REPORT || op == Op.AI_BUFF_REPORT) { hits.Add(reader.GetInt()); return; }
            ordinary.Add(op);
        };
        try
        {
            Check(host.Start(0) && client.Start(0), "UDP bind failed");
            var key = new NetDataWriter(); key.Put("verification");
            client.Connect("127.0.0.1", host.LocalPort, key);
            void Poll() { host.PollEvents(); client.PollEvents(); }
            Until(() => hostListener.Peers.Count == 1 && clientListener.Peers.Count == 1, Poll, "Connect timeout");
            var peer = clientListener.Peers[0];
            var bulk = new byte[128 * 1024]; bulk[0] = (byte)Op.AI_SNAPSHOT_CHUNK;
            peer.Send(bulk, DeliveryMethod.ReliableOrdered);
            peer.Send(new[] { (byte)Op.PLAYER_HEALTH_REPORT }, DeliveryMethod.ReliableOrdered);
            peer.Send(new[] { (byte)Op.CORPSE_INVENTORY }, DeliveryMethod.ReliableOrdered);
            for (var i = 0; i < 40; i++)
            {
                var writer = new NetDataWriter(); writer.Put((byte)(i % 2 == 0 ? Op.AI_HEALTH_REPORT : Op.AI_BUFF_REPORT)); writer.Put(i);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
            Until(() => hits.Count == 40, Poll, "Combat stalled behind fragmented bulk message");
            Check(layer.Dropped > 0 && ordinary.Count == 0, "Fault injection failed to hold ordered bulk lane");
            Check(hits.SequenceEqual(Enumerable.Range(0, 40)), "Combat events lost or reordered");
            // Keep dropping through a retry, not just the initial burst.
            Until(() => layer.Dropped > 64, Poll, "Bulk retransmission was not exercised");
            Check(ordinary.Count == 0, "Ordered lane escaped while fragments were still blocked");
            layer.Hold = false;
            Until(() => ordinary.Count == 3, Poll, "Bulk failed to recover after loss stopped");
            Check(ordinary.SequenceEqual(new[] { Op.AI_SNAPSHOT_CHUNK, Op.PLAYER_HEALTH_REPORT, Op.CORPSE_INVENTORY }),
                "Death report no longer precedes corpse inventory");
            Console.WriteLine($"  40/40 ordered hits delivered with bulk blocked; dropped {layer.Dropped} bulk fragments; bulk/death/corpse recovered in order.");
        }
        finally { host.Stop(); client.Stop(); }
    }
}
