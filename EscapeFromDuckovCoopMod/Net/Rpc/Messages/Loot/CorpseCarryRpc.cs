namespace EscapeFromDuckovCoopMod;

[Rpc(Op.CORPSE_CARRY, DeliveryMethod.ReliableOrdered, RpcDirection.Bidirectional)]
public struct CorpseCarryRpc : IRpcMessage
{
    public const byte Begin = 0, Pose = 1, End = 2, State = 3, Denied = 4;
    public byte Phase;
    public int LootUid;
    public uint Token;
    public string SceneId, Owner;
    public bool Busy;
    public Vector3 Position;
    public Quaternion Rotation;
    public void Serialize(NetDataWriter w)
    {
        w.Put(Phase); w.Put(LootUid); w.Put(Token); w.Put(SceneId ?? ""); w.Put(Owner ?? "");
        w.Put(Busy); w.PutVector3(Position); w.PutQuaternion(Rotation);
    }
    public void Deserialize(NetPacketReader r)
    {
        Phase = r.GetByte(); LootUid = r.GetInt(); Token = r.GetUInt(); SceneId = r.GetString(); Owner = r.GetString();
        Busy = r.GetBool(); Position = r.GetVector3(); Rotation = r.GetQuaternion();
    }
}
