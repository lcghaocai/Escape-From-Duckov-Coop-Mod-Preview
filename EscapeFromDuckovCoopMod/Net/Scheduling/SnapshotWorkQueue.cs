namespace EscapeFromDuckovCoopMod;

// One pending job per peer; a congested peer keeps its job and its turn without
// blocking others. Each tick visits at most one chunk per peer, with a shared budget.
internal sealed class SnapshotWorkQueue<TPeer, TWork>
{
    private readonly Dictionary<TPeer, (TWork Work, LinkedListNode<TPeer> Node)> _work = new();
    private readonly LinkedList<TPeer> _turns = new();
    private int _generation;
    public bool Contains(TPeer peer) => _work.ContainsKey(peer);
    public int Count => _work.Count;
    public bool TryGet(TPeer peer, out TWork work)
    {
        if (_work.TryGetValue(peer, out var entry)) { work = entry.Work; return true; }
        work = default;
        return false;
    }

    public bool Add(TPeer peer, TWork work)
    {
        if (_work.ContainsKey(peer)) return false;
        _work.Add(peer, (work, _turns.AddLast(peer)));
        return true;
    }

    public void Remove(TPeer peer)
    {
        if (!_work.TryGetValue(peer, out var entry)) return;
        _turns.Remove(entry.Node);
        _work.Remove(peer);
    }
    public void Clear() { _work.Clear(); _turns.Clear(); _generation++; }

    public void Tick(Func<TPeer, bool> connected, Func<TPeer, bool> congested,
        Func<TPeer, TWork, bool> sendChunk, Func<double> now, double budgetSeconds)
    {
        var generation = _generation;
        var turns = _turns.Count;
        var deadline = now() + budgetSeconds;
        for (var i = 0; i < turns; i++)
        {
            if (i > 0 && now() >= deadline) break;
            if (_turns.First == null) break;
            var node = _turns.First;
            var peer = node.Value;
            var work = _work[peer].Work;
            if (!connected(peer)) { Remove(peer); continue; }
            // Requeue before callbacks, which can disconnect/reset the session.
            _turns.RemoveFirst();
            _turns.AddLast(node);
            if (!congested(peer) && sendChunk(peer, work) &&
                _work.TryGetValue(peer, out var current) && current.Node == node) Remove(peer);
            if (_generation != generation || _turns.Count == 0) break;
        }
    }
}
