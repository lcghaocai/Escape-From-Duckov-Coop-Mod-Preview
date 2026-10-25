namespace EscapeFromDuckovCoopMod;

// Preserve FIFO within each lane. A sustained combat stream must still allow
// metadata, inventory and scene messages to progress across frame boundaries.
internal sealed class ReceiveWorkQueue<TKey, TPacket>
{
    private readonly OrderedPacketQueue<TKey, TPacket> _normal;
    private readonly OrderedPacketQueue<TKey, TPacket> _combat;
    private int _combatBurst;
    public ReceiveWorkQueue(Action<TPacket> release)
    { _normal = new(release); _combat = new(release); }
    public int Count => _normal.Count + _combat.Count;
    public int CombatCount => _combat.Count;
    public long CoalescedCount => _normal.CoalescedCount;
    public void Enqueue(TPacket packet, bool combat = false) => (combat ? _combat : _normal).Enqueue(packet);
    public void EnqueueLatest(TKey key, TPacket packet, double order = double.NaN) => _normal.EnqueueLatest(key, packet, order);
    public bool TryDequeue(out TPacket packet)
    {
        if (_combat.Count > 0 && (_normal.Count == 0 || _combatBurst < 3))
        {
            _combatBurst = Math.Min(3, _combatBurst + 1);
            return _combat.TryDequeue(out packet);
        }
        _combatBurst = 0;
        return _normal.TryDequeue(out packet);
    }
    public void RemoveWhere(Predicate<TPacket> predicate)
    { _normal.RemoveWhere(predicate); _combat.RemoveWhere(predicate); }
    public void Clear() { _normal.Clear(); _combat.Clear(); _combatBurst = 0; }
}
