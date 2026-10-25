namespace EscapeFromDuckovCoopMod;

// A streamed snapshot can be empty between chunks while live removals arrive.
// Keep those removals until the final chunk has ALSO finished being applied.
internal sealed class SnapshotReceiveQueue<TKey, TEntry>
{
    private readonly Queue<TEntry> _entries = new();
    private readonly HashSet<TKey> _removed = new();
    private bool _lastReceived;
    private bool _resetPending;
    public bool InProgress { get; private set; }
    public int Count => _entries.Count;

    public void Receive(IEnumerable<TEntry> entries, bool reset, bool last)
    {
        if (!InProgress) _lastReceived = false;
        if (reset)
        {
            _entries.Clear();
            _removed.Clear();
            _resetPending = true;
            _lastReceived = false;
        }
        if (entries != null)
            foreach (var entry in entries) _entries.Enqueue(entry);
        _lastReceived |= last;
        InProgress = true;
    }

    public bool ConsumeReset()
    {
        var reset = _resetPending;
        _resetPending = false;
        return reset;
    }
    public void Remove(TKey key) { if (InProgress) _removed.Add(key); }
    public bool IsRemoved(TKey key) => _removed.Contains(key);
    public TEntry Dequeue() => _entries.Dequeue();

    public void FinishFrame()
    {
        if (!_lastReceived || _entries.Count != 0 || _resetPending) return;
        InProgress = false;
        _removed.Clear();
    }
    public void Clear()
    {
        _entries.Clear();
        _removed.Clear();
        _lastReceived = _resetPending = InProgress = false;
    }
}
