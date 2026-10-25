namespace EscapeFromDuckovCoopMod;

// Same ordered lane as full metadata and buff snapshots. A compact refresh is
// only useful after that connection has received the entity's full metadata.
[Rpc(Op.AI_REFRESH_CHUNK, DeliveryMethod.ReliableOrdered, RpcDirection.ServerToClient)]
public struct AIRefreshChunkRpc : IRpcMessage
{
    public AISnapshotEntry[] Entries;
    public void Serialize(NetDataWriter writer)
    {
        var count = Entries?.Length ?? 0;
        writer.Put((ushort)count);
        for (var i = 0; i < count; i++) Entries[i].SerializeRefresh(writer);
    }
    public void Deserialize(NetPacketReader reader)
    {
        var count = reader.GetUShort();
        if (count > reader.AvailableBytes / 94)
            throw new System.FormatException("Truncated AI refresh chunk");
        Entries = new AISnapshotEntry[count];
        for (var i = 0; i < count; i++) Entries[i].DeserializeRefresh(reader);
    }
}
