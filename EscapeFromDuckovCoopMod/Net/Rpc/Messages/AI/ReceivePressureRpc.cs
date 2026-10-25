namespace EscapeFromDuckovCoopMod;

[Rpc(Op.RECEIVE_PRESSURE, DeliveryMethod.Unreliable, RpcDirection.ClientToServer)]
public struct ReceivePressureRpc : IRpcMessage
{
    public double SampleTime;
    public int Backlog;
    public float OldestWaitMs;
    public void Serialize(NetDataWriter writer) { writer.Put(SampleTime); writer.Put(Backlog); writer.Put(OldestWaitMs); }
    public void Deserialize(NetPacketReader reader) { SampleTime = reader.GetDouble(); Backlog = reader.GetInt(); OldestWaitMs = reader.GetFloat(); }
}
