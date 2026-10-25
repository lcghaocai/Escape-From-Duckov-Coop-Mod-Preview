namespace EscapeFromDuckovCoopMod;

// Remember metadata only after it has actually been submitted to the reliable
// channel. Each peer has its own baseline; requests alone do not acknowledge it.
internal sealed class SnapshotMetadataCache<TPeer>
{
    private readonly Dictionary<TPeer, Dictionary<int, int>> _sent = new();
    public bool Matches(TPeer peer, int id, int signature) =>
        _sent.TryGetValue(peer, out var entries) && entries.TryGetValue(id, out var previous) && previous == signature;
    public void Record(TPeer peer, int id, int signature)
    {
        if (!_sent.TryGetValue(peer, out var entries)) _sent[peer] = entries = new();
        entries[id] = signature;
    }
    public void Remove(TPeer peer) => _sent.Remove(peer);
    public void Clear() => _sent.Clear();
}
