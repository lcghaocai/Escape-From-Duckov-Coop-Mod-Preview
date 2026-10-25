namespace EscapeFromDuckovCoopMod;

[Rpc(Op.PLAYER_BUFF_SNAPSHOT, DeliveryMethod.ReliableOrdered, RpcDirection.Bidirectional)]
public struct PlayerBuffSnapshotRpc : IRpcMessage
{
    public string PlayerId;
    public PlayerBuffState[] Buffs;
    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId ?? string.Empty);
        PlayerBuffState.Write(writer, Buffs);
    }
    public void Deserialize(NetPacketReader reader)
    {
        PlayerId = reader.GetString();
        Buffs = PlayerBuffState.Read(reader);
    }
}
