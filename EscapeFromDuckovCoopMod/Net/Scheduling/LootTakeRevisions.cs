namespace EscapeFromDuckovCoopMod;

// Container revisions still order snapshots/deltas. A take only conflicts with
// changes to its own slot; otherwise Pick All invalidates every request but one.
internal sealed class LootTakeRevisions<TKey>
{
    private readonly Dictionary<(TKey Container, int Slot), int> _changed = new();
    internal void Observe(TKey container, int slot, int revision)
    {
        var key = (container, slot);
        if (!_changed.ContainsKey(key)) _changed[key] = revision;
    }
    internal void Changed(TKey container, int slot, int revision) => _changed[(container, slot)] = revision;
    internal bool CanTake(TKey container, int slot, int observed, int current) => observed >= 0 && observed <= current &&
        _changed.TryGetValue((container, slot), out var changed) && changed <= observed;
    internal void Clear() => _changed.Clear();
}
