static partial class Program
{
    private static void ReceiveDiagnostics()
    {
        var metrics = new ReceiveQueueDiagnostics();
        var released = 0;
        var queue = new ReceiveWorkQueue<int, (byte Op, int Bytes, double At)>(p => {
            metrics.Release(p.Op, p.Bytes); released++;
        });
        var op = (byte)Op.AI_STATE_UPDATE;
        void State(double at) { metrics.Enqueue(op, 100); queue.EnqueueLatest(1, (op, 100, at), at); }
        State(1);
        metrics.Enqueue((byte)Op.AI_HEALTH_BROADCAST, 30);
        queue.Enqueue(((byte)Op.AI_HEALTH_BROADCAST, 30, 2), true);
        State(3);
        Check(released == 1 && queue.Count == 2 && queue.OldestTime(p => p.At) == 2, "Replaced state retains obsolete age/ownership");
        Check(metrics.Summary().Contains("AI_STATE_UPDATE:pending=1 bytes=100 in=2"), "Coalesced buffer counted as pending");
        metrics.ResetWindow();
        Check(metrics.Summary().Contains("AI_STATE_UPDATE:pending=1 bytes=100 in=0"), "Window reset lost outstanding buffers");
        Check(queue.TryDequeue(out var packet), "Combat message missing");
        metrics.Handled(packet.Op, 250, 7);
        metrics.Release(packet.Op, packet.Bytes);
        Check(metrics.Summary().Contains("handled=1") && metrics.Summary().Contains("waitMaxMs=250"), "Handler diagnostics missing");
        queue.RemoveWhere(p => p.Op == op);
        Check(queue.Count == 0 && double.IsPositiveInfinity(queue.OldestTime(p => p.At)), "Disconnect retained queue age");
        Check(!metrics.Summary().Contains("pending=-") && !metrics.Summary().Contains("bytes=-"), "Buffer released twice");
        metrics.Clear();
        Check(!metrics.HasActivity && metrics.Summary() == "", "Session reset retained diagnostics");
    }
    private static void PressureRecovery()
    {
        var policy = new PeerReceivePressure<string>();
        Check(policy.Report("slow", 1, 600, 200, 1) && policy.Tier("slow", 1) == 1, "Pressure threshold missed");
        Check(policy.AllowPose("slow", 10, 10000, 1, false), "First remote pose lost");
        Check(!policy.AllowPose("slow", 10, 10000, 1.01, false), "Remote stream not throttled");
        Check(policy.AllowPose("fast", 10, 10000, 1.01, false), "Slow client throttled healthy peer");
        Check(policy.AllowPose("slow", 10, 100, 1.01, false) && policy.AllowPose("slow", 10, 10000, 1.01, true), "Nearby/critical update throttled");
        Check(policy.AllowPose("slow", 10, 10000, 1.2, false), "Remote stream starved");
        Check(policy.AllowPeriodicSnapshot("slow", 1) && !policy.AllowPeriodicSnapshot("slow", 1.5) && policy.AllowPeriodicSnapshot("slow", 2), "Snapshot cadence incorrect");
        Check(policy.Report("slow", 2, 0, 600, 2) && policy.Tier("slow", 2) == 2, "Queue age did not trigger severe pressure");
        Check(!policy.Report("slow", 1, 0, 0, 3) && !policy.Report("slow", double.NaN, 0, 0, 3) &&
            !policy.Report("slow", 3, 0, double.PositiveInfinity, 3) && !policy.Report("slow", 3, -1, 0, 3), "Invalid/stale feedback accepted");
        policy.Report("slow", 3, 0, 0, 3); policy.Report("slow", 4, 0, 0, 4);
        Check(policy.Tier("slow", 4) == 2, "Single healthy sample cleared pressure");
        policy.Report("slow", 5, 0, 0, 5);
        Check(policy.Tier("slow", 5) == 0 && policy.AllowPeriodicSnapshot("slow", 5), "Sustained recovery did not restore quality");
        policy.Report("slow", 6, 2000, 0, 6);
        Check(policy.Tier("slow", 12) == 0 && policy.AllowPose("slow", 10, 10000, 12, false), "Lost feedback caused permanent throttling");
        policy.Remove("slow");
        Check(policy.Report("slow", 1, 2000, 0, 13), "Reconnect retained old source clock");
        policy.Clear(); Check(policy.Tier("slow", 13) == 0, "Scene reset retained pressure");
    }
    private static void RecoveryRateLimit()
    {
        var limiter = new EntityRecoveryLimiter();
        Check(!limiter.TryAcquire(0, 1), "Invalid ID accepted");
        for (var i = 1; i <= 8; i++) Check(limiter.TryAcquire(i, 1), "Burst capacity missing");
        Check(!limiter.TryAcquire(9, 1), "Unbounded recovery burst");
        Check(!limiter.TryAcquire(1, 1.5), "Repeated ID bypassed cooldown");
        Check(limiter.TryAcquire(9, 1.5) && limiter.TryAcquire(10, 1.5) && !limiter.TryAcquire(11, 1.5), "Refill rate incorrect");
        Check(limiter.TryAcquire(1, 3), "Failed request never becomes eligible again");
        limiter.Clear(); Check(limiter.TryAcquire(1, 3), "New scene retained entity cooldown");
    }
    private static void RecoveryWire()
    {
        var writer = new NetDataWriter();
        var pressure = new ReceivePressureRpc { SampleTime = 125.5, Backlog = 629, OldestWaitMs = 210.25f };
        pressure.Serialize(writer);
        var reader = new CoopPacketReader(writer.CopyData(), 0, writer.Length);
        var decoded = new ReceivePressureRpc(); decoded.Deserialize(reader);
        Check(decoded.SampleTime == pressure.SampleTime && decoded.Backlog == 629 && decoded.OldestWaitMs == 210.25f && reader.AvailableBytes == 0, "Pressure wire boundary corrupted");
        var request = new AISnapshotRequestRpc { EntityId = 123, SceneId = "Farm_Main", ForceFull = false };
        writer.Reset(); request.Serialize(writer);
        var bytes = writer.CopyData();
        reader = new CoopPacketReader(bytes, 0, bytes.Length);
        var recovered = new AISnapshotRequestRpc(); recovered.Deserialize(reader);
        Check(recovered.EntityId == 123 && recovered.SceneId == "Farm_Main" && !recovered.ForceFull && reader.AvailableBytes == 0, "Targeted recovery identity corrupted");
        foreach (var legacySize in new[] { 18, 19 })
        {
            reader = new CoopPacketReader(bytes, 0, legacySize);
            recovered = new AISnapshotRequestRpc(); recovered.Deserialize(reader);
            Check(recovered.EntityId == 0 && recovered.SceneId == null && reader.AvailableBytes == 0, "Legacy full request parsed as entity request");
        }
    }
}
