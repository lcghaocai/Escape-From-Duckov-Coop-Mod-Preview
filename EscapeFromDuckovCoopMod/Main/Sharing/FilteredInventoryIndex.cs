using System;
using System.Collections.Generic;

namespace EscapeFromDuckovCoopMod;

internal static class FilteredInventoryIndex
{
    internal static void Rebuild(List<int> indexes, int capacity, Func<int, bool> matches)
    {
        indexes.Clear();
        for (var i = 0; i < capacity; i++)
            if (matches(i)) indexes.Add(i);
    }

    internal static void Update(List<int> indexes, IEnumerable<int> changed, int capacity, Func<int, bool> matches)
    {
        foreach (var slot in changed)
        {
            if (slot < 0 || slot >= capacity) continue;
            var at = indexes.BinarySearch(slot);
            var included = matches(slot);
            if (included && at < 0) indexes.Insert(~at, slot);
            else if (!included && at >= 0) indexes.RemoveAt(at);
        }
    }

    internal static int PageCount(int count, int pageSize) => count == 0 ? 0 : 1 + (count - 1) / Math.Max(1, pageSize);
    internal static int ClampPage(int page, int count, int pageSize) =>
        Math.Max(0, Math.Min(page, PageCount(count, pageSize) - 1));
    internal static int VisibleCount(int count, bool pages, int page, int pageSize) =>
        pages ? Math.Max(0, Math.Min(Math.Max(1, pageSize), count - page * Math.Max(1, pageSize))) : count;
}
