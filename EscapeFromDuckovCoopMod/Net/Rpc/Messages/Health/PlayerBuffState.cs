namespace EscapeFromDuckovCoopMod;

public struct PlayerBuffState
{
    public int WeaponTypeId;
    public int BuffId;
    public int Layers;

    internal static void Write(NetDataWriter writer, PlayerBuffState[] buffs)
    {
        if (buffs == null) return;
        writer.Put((ushort)buffs.Length);
        foreach (var buff in buffs)
        {
            writer.Put(buff.WeaponTypeId);
            writer.Put(buff.BuffId);
            writer.Put(buff.Layers);
        }
    }

    internal static PlayerBuffState[] Read(NetPacketReader reader, int optionalTailBytes = 0)
    {
        // Null buffs have no wire bytes (and mean "leave buffs unchanged").
        // A legacy array takes 2 + 12*n bytes, so an eight-byte timestamp-only
        // tail is unambiguous. Preserve the existing format and null semantics.
        if (reader.AvailableBytes == 0 || reader.AvailableBytes == optionalTailBytes) return null;
        if (reader.AvailableBytes < sizeof(ushort))
            throw new System.FormatException("Truncated player buff count");
        var count = reader.GetUShort();
        if (count > 512 || count > reader.AvailableBytes / (3 * sizeof(int)))
            throw new System.FormatException("Truncated player buff entries");
        var result = new PlayerBuffState[count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new PlayerBuffState { WeaponTypeId = reader.GetInt(), BuffId = reader.GetInt(), Layers = reader.GetInt() };
        return result;
    }
}
