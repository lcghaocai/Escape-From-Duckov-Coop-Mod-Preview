using LiteNetLib;
using LiteNetLib.Utils;

namespace EscapeFromDuckovCoopMod;

[Rpc(Op.LOOT_TAKE_OK, DeliveryMethod.ReliableOrdered, RpcDirection.ServerToClient)]
public struct LootTakeOkRpc : IRpcMessage
{
    public LootIdentifier Id;
    public uint Token;
    public int Slot;
    public int TypeId;
    public int Stack;
    public bool Accepted;
    public int Version;
    public ItemSnapshot Item;
    public string Reason;

    public void Serialize(NetDataWriter writer)
    {
        Id.Serialize(writer);
        writer.Put(Token);
        writer.Put(Slot);
        writer.Put(TypeId);
        writer.Put(Stack);
        writer.Put(Accepted); writer.Put(Version);
        if (Accepted) ItemTool.WriteItemSnapshot(writer, Item);
        else writer.Put(Reason ?? string.Empty);
    }

    public void Deserialize(NetPacketReader reader)
    {
        Id.Deserialize(reader);
        Token = reader.GetUInt();
        Slot = reader.GetInt();
        TypeId = reader.GetInt();
        Stack = reader.GetInt();
        if (reader.AvailableBytes == 0) return;
        Accepted = reader.GetBool(); Version = reader.GetInt();
        if (Accepted) Item = ItemTool.ReadItemSnapshot(reader);
        else if (reader.AvailableBytes > 0) Reason = reader.GetString();
    }
}
