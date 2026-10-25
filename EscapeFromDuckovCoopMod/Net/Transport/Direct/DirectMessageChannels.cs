namespace EscapeFromDuckovCoopMod;

// Only requests against existing authoritative actors and timestamped health
// results may bypass bulk traffic. Lifecycle, buff snapshots and death reports
// stay ordered with actor metadata / corpse inventory on channel zero.
internal static class DirectMessageChannels
{
    internal const byte Combat = 1;
    internal const byte Count = 2;

    internal static byte Select(byte op, DeliveryMethod delivery)
    {
        if (delivery == DeliveryMethod.ReliableOrdered)
        {
            switch ((Op)op)
            {
                case Op.AI_HEALTH_REPORT:
                case Op.AI_BUFF_REPORT:
                case Op.PLAYER_DAMAGE_REQUEST:
                case Op.PLAYER_DAMAGE_FORWARD:
                    return Combat;
            }
        }
        if (delivery == DeliveryMethod.ReliableUnordered &&
            (op == (byte)Op.AI_HEALTH_BROADCAST || op == (byte)Op.PLAYER_HEALTH_BROADCAST))
            return Combat;
        return 0;
    }
}
