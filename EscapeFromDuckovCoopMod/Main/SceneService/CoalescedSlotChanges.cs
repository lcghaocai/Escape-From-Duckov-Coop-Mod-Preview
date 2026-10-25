using System.Collections.Generic;

namespace EscapeFromDuckovCoopMod;

internal sealed class CoalescedSlotChanges
{
    private readonly SortedSet<int> _slots = new();
    internal void Add(int slot) { if (slot >= 0) _slots.Add(slot); }
    internal bool Any => _slots.Count > 0;
    internal int[] Drain()
    {
        var result = new int[_slots.Count];
        _slots.CopyTo(result);
        _slots.Clear();
        return result;
    }
    internal void Clear() => _slots.Clear();
}
