static partial class Program
{
    private static void CorpseCarryOwnership()
    {
        var lease = new CorpseCarryLease();
        Check(!lease.Acquire("", 1, 0) && !lease.Acquire("a", 0, 0), "Invalid owner/token acquired box");
        Check(lease.Acquire("a", 1, 0), "First carrier denied");
        Check(!lease.Acquire("b", 1, 1) && !lease.Renew("b", 1, 1), "Competing carrier moved box");
        Check(lease.Renew("a", 1, 4) && lease.Held(8), "Heartbeat did not extend lease");
        Check(!lease.Held(9) && !lease.Renew("a", 1, 9), "Expired carrier can still move box");
        Check(lease.Acquire("b", 2, 9), "Timed-out box cannot be recovered");
        Check(!lease.Release("a", 1) && lease.Held(10), "Late drop unlocked another carrier");
        Check(lease.Release("b", 2) && !lease.Held(10), "Drop did not unlock box");
        Check(lease.Acquire("b", 3, 10), "Same player cannot pick box up again");
        Check(!lease.Release("b", 2) && !lease.Renew("b", 2, 11), "Old session changed new carry");
        Check(lease.Held(11), "Old session cleared new ownership");
    }

    private static void CorpseCarryWire()
    {
        for (byte phase = CorpseCarryRpc.Begin; phase <= CorpseCarryRpc.Denied; phase++)
        {
            var source = new CorpseCarryRpc { Phase = phase, LootUid = 37, Token = uint.MaxValue,
                SceneId = "Level_Farm_Main", Owner = "peer", Busy = phase != CorpseCarryRpc.End,
                Position = new UnityEngine.Vector3(1, -2, 3), Rotation = new UnityEngine.Quaternion(0, 1, 0, 0) };
            var writer = new NetDataWriter(); source.Serialize(writer);
            var bytes = writer.CopyData(); var reader = new CoopPacketReader(bytes, 0, bytes.Length);
            var result = new CorpseCarryRpc(); result.Deserialize(reader);
            Check(result.Phase == phase && result.LootUid == 37 && result.Token == uint.MaxValue &&
                  result.SceneId == source.SceneId && result.Owner == "peer" && result.Busy == source.Busy &&
                  result.Position.y == -2 && result.Rotation.y == 1 && reader.AvailableBytes == 0,
                  "Carry message lost identity, pose or wire boundary");
        }
    }
}
