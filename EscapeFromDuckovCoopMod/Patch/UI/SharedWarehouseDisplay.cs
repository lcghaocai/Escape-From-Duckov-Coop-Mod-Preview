using System;
using System.Collections.Generic;
using System.Linq;
using Duckov.UI;
using Duckov.Utilities;
using ItemStatsSystem;

namespace EscapeFromDuckovCoopMod;

// Keep the game's filtered slot order and pooled entries, but avoid restarting
// LoadEntriesTask for every remote deposit. Initial setup/page changes stay native.
internal static class SharedWarehouseDisplay
{
    private sealed class Pending
    {
        internal Inventory Inventory;
        internal Func<Item, bool> Filter;
        internal int Capacity;
        internal readonly HashSet<int> Slots = new();
        internal bool Rebuild;
    }

    private static readonly Dictionary<InventoryDisplay, Pending> Displays = new();
    private static readonly HashSet<InventoryDisplay> Fallback = new();
    private static readonly AccessTools.FieldRef<InventoryDisplay, List<int>> Indexes =
        AccessTools.FieldRefAccess<InventoryDisplay, List<int>>("cachedIndexesToDisplay");
    private static readonly AccessTools.FieldRef<InventoryDisplay, List<InventoryEntry>> Entries =
        AccessTools.FieldRefAccess<InventoryDisplay, List<InventoryEntry>>("entries");
    private static readonly AccessTools.FieldRef<InventoryDisplay, PrefabPool<InventoryEntry>> Pool =
        AccessTools.FieldRefAccess<InventoryDisplay, PrefabPool<InventoryEntry>>("_entryPool");
    private static readonly AccessTools.FieldRef<InventoryDisplay, Transform> Parent =
        AccessTools.FieldRefAccess<InventoryDisplay, Transform>("entriesParent");
    private static readonly AccessTools.FieldRef<InventoryDisplay, int> PageSize =
        AccessTools.FieldRefAccess<InventoryDisplay, int>("itemsEachPage");
    private static readonly AccessTools.FieldRef<InventoryDisplay, int> Page =
        AccessTools.FieldRefAccess<InventoryDisplay, int>("cachedSelectedPage");
    private static readonly AccessTools.FieldRef<InventoryDisplay, int> Pages =
        AccessTools.FieldRefAccess<InventoryDisplay, int>("cachedMaxPage");
    private static readonly AccessTools.FieldRef<InventoryDisplay, Action> PageInfo =
        AccessTools.FieldRefAccess<InventoryDisplay, Action>("onPageInfoRefreshed");
    private static readonly Action<InventoryDisplay> RefreshCapacity = AccessTools.MethodDelegate<Action<InventoryDisplay>>(
        AccessTools.Method(typeof(InventoryDisplay), "RefreshCapacityText"));
    private static readonly Action<InventoryDisplay> RefreshLayout = AccessTools.MethodDelegate<Action<InventoryDisplay>>(
        AccessTools.Method(typeof(InventoryDisplay), "RefreshGridLayoutPreferredHeight"));

    internal static void Cached(InventoryDisplay display)
    {
        if (!SharedHomeStorage.IsShared(display.Target) || Fallback.Contains(display)) return;
        Displays[display] = new Pending
        {
            Inventory = display.Target,
            Filter = display.filter,
            Capacity = display.Target.Capacity
        };
    }

    internal static bool Queue(InventoryDisplay display, int slot)
    {
        if (!display || !SharedHomeStorage.IsShared(display.Target) || Fallback.Contains(display) ||
            !Displays.TryGetValue(display, out var pending) ||
            !ReferenceEquals(pending.Inventory, display.Target) || !ReferenceEquals(pending.Filter, display.filter))
            return false;
        if (display.Target.Loading) return true;
        if (slot < 0 || pending.Capacity != display.Target.Capacity) pending.Rebuild = true;
        else pending.Slots.Add(slot);
        return true;
    }

    internal static void Opened(InventoryDisplay display)
    {
        if (!SharedHomeStorage.IsShared(display.Target) || Pool(display) == null) return;
        Cached(display);
        var expected = FilteredInventoryIndex.VisibleCount(Indexes(display).Count, display.UsePages,
            Page(display), Math.Max(1, PageSize(display)));
        // OnDisable cancels the native load. Restart it once if it was incomplete.
        if (Entries(display).Count != expected) display.SetPage(display.SelectedPage);
        else Queue(display, -1);
    }

    internal static void Forget(InventoryDisplay display)
    {
        Displays.Remove(display);
        Fallback.Remove(display);
    }
    internal static void Reset() { Displays.Clear(); Fallback.Clear(); }

    internal static void Tick()
    {
        if (Displays.Count == 0) return;
        foreach (var pair in Displays.ToArray())
        {
            var display = pair.Key;
            var pending = pair.Value;
            if (!display || !display.isActiveAndEnabled || !SharedHomeStorage.IsShared(display.Target) ||
                !ReferenceEquals(display.Target, pending.Inventory))
            {
                Forget(display);
                continue;
            }
            if (!pending.Rebuild && pending.Slots.Count == 0) continue;
            try { Flush(display, pending); }
            catch (Exception e)
            {
                // A game UI change must leave a usable native view.
                Displays.Remove(display);
                Fallback.Add(display);
                CoopLogSystem.WriteNetworkDiagnostic($"[SharedHome] incremental warehouse UI fallback: {e}");
                display.SetPage(display.SelectedPage);
            }
        }
    }

    private static void Flush(InventoryDisplay display, Pending pending)
    {
        var indexes = Indexes(display);
        var entries = Entries(display);
        var size = Math.Max(1, PageSize(display));
        var oldPage = Page(display);
        var expected = FilteredInventoryIndex.VisibleCount(indexes.Count, display.UsePages, oldPage, size);
        // Let the initial native async page load finish; never cancel/restart it.
        if (entries.Count != expected || display.Target.Loading) return;

        bool Matches(int slot) => display.filter == null || display.filter(display.Target.GetItemAt(slot));
        var rebuild = pending.Rebuild || pending.Capacity != display.Target.Capacity;
        var changedSlots = new HashSet<int>(pending.Slots);
        pending.Slots.Clear();
        pending.Rebuild = false;
        if (rebuild) FilteredInventoryIndex.Rebuild(indexes, display.Target.Capacity, Matches);
        else FilteredInventoryIndex.Update(indexes, changedSlots, display.Target.Capacity, Matches);
        pending.Capacity = display.Target.Capacity;

        var oldPages = Pages(display);
        Pages(display) = FilteredInventoryIndex.PageCount(indexes.Count, size);
        Page(display) = FilteredInventoryIndex.ClampPage(oldPage, indexes.Count, size);
        var count = FilteredInventoryIndex.VisibleCount(indexes.Count, display.UsePages, Page(display), size);
        var start = display.UsePages ? Page(display) * size : 0;
        var wanted = new HashSet<int>(indexes.Skip(start).Take(count));
        var existing = entries.ToDictionary(entry => entry.Index);
        var spare = new Queue<InventoryEntry>(entries.Where(entry => !wanted.Contains(entry.Index)));
        var next = new List<InventoryEntry>(count);
        var layoutChanged = entries.Count != count || oldPage != Page(display);
        for (var i = 0; i < count; i++)
        {
            var slot = indexes[start + i];
            if (!existing.TryGetValue(slot, out var entry))
            {
                entry = spare.Count > 0 ? spare.Dequeue() : Pool(display).Get(null);
                entry.gameObject.SetActive(true);
                entry.Setup(display, slot, false);
                entry.transform.SetParent(Parent(display), false);
                layoutChanged = true;
            }
            else if (rebuild || changedSlots.Contains(slot))
                entry.Setup(display, slot, entry.Disabled);

            // Reuse the entries for unchanged items, including their selection.
            if (i >= entries.Count || !ReferenceEquals(entries[i], entry)) layoutChanged = true;
            next.Add(entry);
        }
        while (spare.Count > 0) Pool(display).Release(spare.Dequeue());
        entries.Clear();
        entries.AddRange(next);
        if (layoutChanged)
        {
            for (var i = 0; i < entries.Count; i++) entries[i].transform.SetSiblingIndex(i);
            RefreshLayout(display);
        }
        RefreshCapacity(display);
        if (oldPages != Pages(display) || oldPage != Page(display)) PageInfo(display)?.Invoke();
    }
}

[HarmonyPatch(typeof(InventoryDisplay), "CacheIndexesToDisplay")]
internal static class SharedWarehouseCachePatch
{
    private static void Postfix(InventoryDisplay __instance) => SharedWarehouseDisplay.Cached(__instance);
}
[HarmonyPatch(typeof(InventoryDisplay), "OnTargetContentChanged")]
internal static class SharedWarehouseContentPatch
{
    private static bool Prefix(InventoryDisplay __instance, int position) => !SharedWarehouseDisplay.Queue(__instance, position);
}
[HarmonyPatch(typeof(InventoryDisplay), "OnTargetSorted")]
internal static class SharedWarehouseSortedPatch
{
    private static bool Prefix(InventoryDisplay __instance) => !SharedWarehouseDisplay.Queue(__instance, -1);
}
[HarmonyPatch(typeof(InventoryDisplay), "OnEnable")]
internal static class SharedWarehouseEnablePatch
{
    private static void Postfix(InventoryDisplay __instance) => SharedWarehouseDisplay.Opened(__instance);
}
[HarmonyPatch(typeof(InventoryDisplay), "OnDisable")]
internal static class SharedWarehouseDisablePatch
{
    private static void Postfix(InventoryDisplay __instance) => SharedWarehouseDisplay.Forget(__instance);
}
