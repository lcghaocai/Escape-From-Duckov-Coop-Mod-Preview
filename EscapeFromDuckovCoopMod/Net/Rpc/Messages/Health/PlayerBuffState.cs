namespace EscapeFromDuckovCoopMod;

public struct PlayerBuffState
{
    public int WeaponTypeId;
    public int BuffId;
    public int Layers;

    internal static void Write(NetDataWriter writer, PlayerBuffState[] buffs)
    {
        // 即使没有 buff 也必须写长度；后面还有 Timestamp，
        // 否则接收端会把 Timestamp 的前两个字节当成 buff 数量。
        writer.Put((ushort)(buffs?.Length ?? 0));
        if (buffs == null) return;
        foreach (var buff in buffs)
        {
            writer.Put(buff.WeaponTypeId);
            writer.Put(buff.BuffId);
            writer.Put(buff.Layers);
        }
    }

    internal static PlayerBuffState[] Read(NetPacketReader reader)
    {
        if (reader.AvailableBytes < sizeof(ushort)) return null;
        var count = reader.GetUShort();
        if (count > 512) return null;
        if (reader.AvailableBytes < count * 12) return null;

        var result = new PlayerBuffState[count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new PlayerBuffState { WeaponTypeId = reader.GetInt(), BuffId = reader.GetInt(), Layers = reader.GetInt() };
        return result;
    }
}
