namespace EscapeFromDuckovCoopMod;

// Preserve FIFO within each lane. A sustained combat stream must still allow
// metadata, inventory and scene messages to progress across frame boundaries.
internal sealed class ReceiveWorkQueue<TKey, TPacket>
{
    private readonly OrderedPacketQueue<TKey, TPacket> _normal;
    private readonly OrderedPacketQueue<TKey, TPacket> _combat;
    private int _combatBurst, _importantBurst;
    private readonly LinkedList<(TPacket Packet, double Time)> _transient = new();
    private readonly Action<TPacket> _release;
    internal const int TransientCapacity = 64;
    internal const double TransientLifetime = 0.25;
    public long ExpiredCount { get; private set; }
    public long OverflowCount { get; private set; }
    public int TransientCount => _transient.Count;
    public ReceiveWorkQueue(Action<TPacket> release)
    { _release = release; _normal = new(release); _combat = new(release); }
    public int Count => _normal.Count + _combat.Count + _transient.Count;
    public int CombatCount => _combat.Count;
    public long CoalescedCount => _normal.CoalescedCount;
    public void Enqueue(TPacket packet, bool combat = false) => (combat ? _combat : _normal).Enqueue(packet);
    public void EnqueueLatest(TKey key, TPacket packet, double order = double.NaN) => _normal.EnqueueLatest(key, packet, order);
    public void EnqueueTransient(TPacket packet, double now)
    {
        ExpireTransient(now);
        if (_transient.Count >= TransientCapacity)
        { _release(_transient.First.Value.Packet); _transient.RemoveFirst(); OverflowCount++; }
        _transient.AddLast((packet, now));
    }
    private void ExpireTransient(double now)
    {
        while (_transient.First != null && now - _transient.First.Value.Time >= TransientLifetime)
        { _release(_transient.First.Value.Packet); _transient.RemoveFirst(); ExpiredCount++; }
    }
    public double OldestTime(Func<TPacket, double> time) => Math.Min(
        Math.Min(_normal.OldestTime(time), _combat.OldestTime(time)),
        _transient.First == null ? double.PositiveInfinity : _transient.First.Value.Time);
    public bool TryDequeue(out TPacket packet, double now = 0)
    {
        ExpireTransient(now);
        if (_transient.Count > 0 && (_normal.Count + _combat.Count == 0 || _importantBurst >= 7))
        {
            packet = _transient.First.Value.Packet; _transient.RemoveFirst();
            _importantBurst = 0;
            return true;
        }
        _importantBurst = Math.Min(7, _importantBurst + 1);
        if (_combat.Count > 0 && (_normal.Count == 0 || _combatBurst < 3))
        {
            _combatBurst = Math.Min(3, _combatBurst + 1);
            return _combat.TryDequeue(out packet);
        }
        _combatBurst = 0;
        return _normal.TryDequeue(out packet);
    }
    public void RemoveWhere(Predicate<TPacket> predicate)
    {
        _normal.RemoveWhere(predicate); _combat.RemoveWhere(predicate);
        for (var node = _transient.First; node != null;)
        {
            var next = node.Next;
            if (predicate(node.Value.Packet)) { _release(node.Value.Packet); _transient.Remove(node); }
            node = next;
        }
    }
    public void Clear()
    {
        _normal.Clear(); _combat.Clear();
        foreach (var entry in _transient) _release(entry.Packet);
        _transient.Clear(); _combatBurst = _importantBurst = 0;
        ExpiredCount = OverflowCount = 0;
    }
}
