using LiteNetLib;
using LiteNetLib.Utils;

namespace EscapeFromDuckovCoopMod;

[Rpc(Op.LOOT_REQ_TAKE, DeliveryMethod.ReliableOrdered, RpcDirection.ClientToServer)]
public struct LootTakeRequestRpc : IRpcMessage
{
    public LootIdentifier Id;
    public uint Token;
    public int Position;
    public int PreferDest;
    public byte Protocol, Phase; // v1: 0=request, 1=delivery receipt
    public int ExpectedVersion, ExpectedType, ExpectedStack, Count;

    public void Serialize(NetDataWriter writer)
    {
        Id.Serialize(writer);
        writer.Put(Token);
        writer.Put(Position);
        writer.Put(PreferDest);
        writer.Put(Protocol); writer.Put(Phase);
        writer.Put(ExpectedVersion); writer.Put(ExpectedType); writer.Put(ExpectedStack); writer.Put(Count);
    }

    public void Deserialize(NetPacketReader reader)
    {
        Id.Deserialize(reader);
        Token = reader.GetUInt();
        Position = reader.GetInt();
        PreferDest = reader.GetInt();
        if (reader.AvailableBytes == 0) return;
        Protocol = reader.GetByte(); Phase = reader.GetByte();
        ExpectedVersion = reader.GetInt(); ExpectedType = reader.GetInt(); ExpectedStack = reader.GetInt(); Count = reader.GetInt();
    }
}
