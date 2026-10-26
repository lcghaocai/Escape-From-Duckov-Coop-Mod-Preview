static partial class Program
{
    private static void IndependentLootTakes()
    {
        var revisions = new LootTakeRevisions<string>();
        for (var i = 0; i < 20; i++) revisions.Observe("box", i, 10);
        // Pick All queues the same observed container revision for every slot.
        // Each successful take changes only its own slot and advances the container.
        for (var i = 0; i < 20; i++)
        {
            Check(revisions.CanTake("box", i, 10, 10 + i), "Unrelated prior take blocked this slot");
            revisions.Changed("box", i, 11 + i);
            Check(!revisions.CanTake("box", i, 10, 11 + i), "Second guest took an already changed slot");
        }
        revisions.Changed("box", 0, 31); // Identical type/count replaced in same slot.
        Check(!revisions.CanTake("box", 0, 30, 31), "Same-looking replacement accepted from stale preview");
        Check(revisions.CanTake("box", 0, 31, 31), "Refreshed slot remained blocked");
        Check(!revisions.CanTake("box", 0, 32, 31), "Future revision accepted");
        Check(!revisions.CanTake("other", 0, 31, 31), "Container identity leaked");
        revisions.Observe("box", 19, 31);
        Check(revisions.CanTake("box", 19, 30, 31), "Read-only snapshot changed a slot revision");
        revisions.Clear();
        Check(!revisions.CanTake("box", 0, 31, 31), "Scene reset reused old slot history");
    }
    private static void TransientAudioQueue()
    {
        var freed = new HashSet<int>();
        var queue = new ReceiveWorkQueue<int, int>(x => Check(freed.Add(x), "Double release"));
        for (var i = 0; i < 1000; i++) queue.EnqueueTransient(i, 1);
        Check(queue.Count == 64 && queue.OverflowCount == 936 && freed.Count == 936, "Unbounded audio burst");
        queue.Enqueue(1000, true); queue.Enqueue(1001);
        Check(queue.TryDequeue(out var packet, 1.3) && packet == 1000, "Expired audio delayed combat");
        Check(queue.ExpiredCount == 64 && freed.Count == 1000, "Expiry failed to release every reader");
        Check(queue.TryDequeue(out packet, 1.3) && packet == 1001, "Transaction lost");
        for (var i = 1100; i < 1120; i++) queue.Enqueue(i, true);
        queue.EnqueueTransient(2000, 2);
        var heard = false;
        for (var i = 0; i < 8; i++) { queue.TryDequeue(out packet, 2.01); heard |= packet == 2000; }
        Check(heard, "Audio starved despite being fresh");
        queue.EnqueueTransient(2001, 2); queue.RemoveWhere(x => x == 2001);
        queue.Clear();
        Check(freed.Contains(2001) && queue.Count == 0, "Disconnect/clear leaked audio buffer");
        var a = new TransientRateLimit(); var b = new TransientRateLimit();
        for (var i = 0; i < 64; i++) Check(a.Take(0), "Initial burst too short");
        Check(!a.Take(0) && b.Take(0) && a.Take(1), "Per-peer limit/refill failed");
    }
    private static void LootTakeWire()
    {
        foreach (var phase in new byte[] { 0, 1 })
        {
            var request = new LootTakeRequestRpc { Id = new LootIdentifier { Scene = 3, PositionKey = -20, InstanceId = 72, LootUid = 88 },
                Token = 0xFFFF0000, Position = 9, PreferDest = -1, Protocol = 1, Phase = phase,
                ExpectedVersion = 212, ExpectedType = 731, ExpectedStack = 80, Count = 4 };
            var writer = new NetDataWriter(); request.Serialize(writer); var bytes = writer.CopyData();
            var reader = new CoopPacketReader(bytes, 0, bytes.Length); var decoded = new LootTakeRequestRpc(); decoded.Deserialize(reader);
            Check(reader.AvailableBytes == 0 && decoded.Token == request.Token && decoded.Id.LootUid == 88 && decoded.Id.PositionKey == -20 &&
                decoded.Protocol == 1 && decoded.Phase == phase && decoded.ExpectedVersion == 212 && decoded.ExpectedType == 731 &&
                decoded.ExpectedStack == 80 && decoded.Count == 4, "Take validation/receipt wire corrupted");
            reader = new CoopPacketReader(bytes, 0, bytes.Length - 18); decoded = new(); decoded.Deserialize(reader);
            Check(reader.AvailableBytes == 0 && decoded.Protocol == 0, "Legacy request masqueraded as a confirmed take");
        }
        var denial = new LootTakeOkRpc { Token = 13, Slot = 4, Accepted = false, Version = 11, Reason = "slot_changed" };
        var denyWriter = new NetDataWriter(); denial.Serialize(denyWriter); var denyBytes = denyWriter.CopyData();
        var denyReader = new CoopPacketReader(denyBytes, 0, denyBytes.Length); var decodedDeny = new LootTakeOkRpc(); decodedDeny.Deserialize(denyReader);
        Check(!decodedDeny.Accepted && decodedDeny.Token == 13 && decodedDeny.Version == 11 && decodedDeny.Reason == "slot_changed" && denyReader.AvailableBytes == 0, "Denial cannot resolve matching pending request");
        denyReader = new CoopPacketReader(denyBytes, 0, 37); // v1 confirmation prefix without a reason tail.
        decodedDeny = new(); decodedDeny.Deserialize(denyReader);
        Check(!decodedDeny.Accepted && decodedDeny.Reason == null && denyReader.AvailableBytes == 0, "Legacy denial tail is not optional");
    }
    private static void TakeDeliveryExactlyOnce()
    {
        var ledger = new TakeDeliveryLedger();
        Check(!ledger.TryClaim(99), "Unsolicited result granted an item");
        Check(ledger.Register(1) && !ledger.Register(1), "Duplicate request registered");
        Check(ledger.TryClaim(1) && !ledger.TryClaim(1), "Reentrant delivery granted twice");
        Check(!ledger.TryReceipt(1, out _), "In-progress delivery prematurely acknowledged");
        ledger.Finish(1, true);
        ledger.Finish(1, false); // Late exception/duplicate cannot roll back completed delivery.
        Check(ledger.TryReceipt(1, out var receipt) && receipt == 1 && !ledger.TryClaim(1), "Acknowledged item rolled back or granted twice");
        ledger.Register(2); ledger.TryClaim(2); ledger.Finish(2, false);
        Check(ledger.TryReceipt(2, out receipt) && receipt == 2 && !ledger.TryClaim(2), "Failed delivery later granted a returned item");
        // Replay/delay all results many times: decisions survive unrelated later requests.
        for (uint token = 3; token < 10000; token++) { ledger.Register(token); ledger.TryClaim(token); ledger.Finish(token, true); }
        Check(ledger.TryReceipt(1, out receipt) && receipt == 1 && !ledger.TryClaim(1), "Old receipt evicted and replayed");
        ledger.Clear(); Check(!ledger.TryClaim(1), "Reconnection accepted an unsolicited old result");
    }
}
