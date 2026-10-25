namespace EscapeFromDuckovCoopMod;

// Reliable/event packets stay in arrival order. Only explicitly identified state
// samples may replace older samples, at the NEW sample's position in that order.
internal sealed class OrderedPacketQueue<TKey, TPacket>
{
    private readonly LinkedList<Entry> _packets = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _latest = new();
    private readonly Action<TPacket> _release;

    private readonly struct Entry
    {
        public readonly TPacket Packet;
        public readonly bool Replaceable;
        public readonly TKey Key;
        public readonly double Order;
        public Entry(TPacket packet, bool replaceable, TKey key, double order = double.NaN)
        { Packet = packet; Replaceable = replaceable; Key = key; Order = order; }
    }

    public OrderedPacketQueue(Action<TPacket> release) => _release = release;
    public int Count => _packets.Count;
    public long CoalescedCount { get; private set; }
    public void Enqueue(TPacket packet) => _packets.AddLast(new Entry(packet, false, default));

    public void EnqueueLatest(TKey key, TPacket packet, double order = double.NaN)
    {
        if (_latest.TryGetValue(key, out var previous))
        {
            // Unreliable packets can arrive out of order. Do not replace a newer
            // pose with a delayed older one before the RPC gets to validate it.
            if (!double.IsNaN(order) && !double.IsNaN(previous.Value.Order) && order <= previous.Value.Order)
            {
                _release(packet);
                CoalescedCount++;
                return;
            }
            _packets.Remove(previous);
            _latest.Remove(key);
            _release(previous.Value.Packet);
            CoalescedCount++;
        }
        _latest[key] = _packets.AddLast(new Entry(packet, true, key, order));
    }

    public bool TryDequeue(out TPacket packet)
    {
        var first = _packets.First;
        if (first == null) { packet = default; return false; }
        _packets.RemoveFirst();
        if (first.Value.Replaceable) _latest.Remove(first.Value.Key);
        packet = first.Value.Packet;
        return true; // The dispatcher now owns/recycles this packet.
    }

    public void RemoveWhere(Predicate<TPacket> predicate)
    {
        for (var node = _packets.First; node != null;)
        {
            var next = node.Next;
            if (predicate(node.Value.Packet))
            {
                _packets.Remove(node);
                if (node.Value.Replaceable) _latest.Remove(node.Value.Key);
                _release(node.Value.Packet);
            }
            node = next;
        }
    }

    public void Clear()
    {
        while (TryDequeue(out var packet)) _release(packet);
    }
}
