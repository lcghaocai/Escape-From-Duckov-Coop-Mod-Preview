namespace EscapeFromDuckovCoopMod;

public struct LootIdentifier
{
    public int Scene;
    public int PositionKey;
    public int InstanceId;
    public int LootUid;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Scene);
        writer.Put(PositionKey);
        writer.Put(InstanceId);
        writer.Put(LootUid);
    }

    public void Deserialize(NetPacketReader reader)
    {
        Scene = reader.GetInt();
        PositionKey = reader.GetInt();
        InstanceId = reader.GetInt();
        LootUid = reader.GetInt();
    }
}
