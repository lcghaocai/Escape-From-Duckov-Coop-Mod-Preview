namespace EscapeFromDuckovCoopMod;

// Receipt order is shared by full spawns and queued snapshots. A dropped or
// skipped full update must never leave compact refreshes using stale equipment.
internal sealed class ReceivedSnapshotMetadata
{
    private readonly Dictionary<int, (int Version, long Order)> _applied = new();
    private long _received;
    public long Receive() => ++_received;
    public bool TryApplyFull(int id, int version, long order)
    {
        if (_applied.TryGetValue(id, out var old) && old.Order >= order) return false;
        _applied[id] = (version, order);
        return true;
    }
    public bool CanApplyRefresh(int id, int version, long order, out bool needsRecovery)
    {
        needsRecovery = false;
        if (_applied.TryGetValue(id, out var old))
        {
            if (old.Version == version) return true;
            if (old.Order > order) return false; // Newer full spawn already won.
        }
        needsRecovery = true;
        return false;
    }
    public void Clear() { _applied.Clear(); _received = 0; }
}
