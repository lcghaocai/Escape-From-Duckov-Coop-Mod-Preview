// Run: dotnet run --project validation/WarehouseRegression
// Links the production state/index code; no Unity process or NuGet test runner required.
using EscapeFromDuckovCoopMod;

var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

foreach (var guests in new[] { 1, 2, 4, 8 })
{
    var host = new LootVersionState<string>();
    var clients = Enumerable.Range(0, guests).Select(_ => new LootVersionState<string>()).ToArray();
    foreach (var client in clients)
        client.Applied("warehouse", host.Current("warehouse"));
    for (var mutation = 0; mutation < 100; mutation++)
    {
        // A single recipient reopens/refreshes the warehouse between mutations.
        clients[mutation % guests].Applied("warehouse", host.Current("warehouse"));
        var revision = host.Advance("warehouse");
        foreach (var client in clients)
        {
            Check(client.ClassifyDelta("warehouse", revision) == LootDeltaDisposition.Apply,
                $"{guests} guests: a snapshot recipient created a version gap");
            client.Applied("warehouse", revision);
        }
    }
    Check(clients.All(c => c.Current("warehouse") == 100), "All replicas must converge");
}

var replica = new LootVersionState<string>();
replica.Applied("warehouse", 3);
Check(replica.ClassifyDelta("warehouse", 3) == LootDeltaDisposition.Ignore, "Duplicate delta");
Check(replica.ClassifyDelta("warehouse", 1) == LootDeltaDisposition.Ignore, "Stale delta");
Check(replica.ClassifyDelta("warehouse", 5) == LootDeltaDisposition.RequestSnapshot, "Missing delta");
Check(replica.TryRequest("warehouse", 10), "First resync request");
Check(!replica.TryRequest("warehouse", 10.01f), "Coalesce burst resyncs");
Check(!replica.TryRequest("warehouse", 11.99f), "Wait for in-flight response");
Check(replica.TryRequest("warehouse", 12), "Lost response retries");
Check(replica.IsStaleSnapshot("warehouse", 2), "Do not roll back with stale snapshots");
Check(!replica.IsStaleSnapshot("warehouse", 3), "Equal revision must reconcile local divergent state");
// A failed apply does not advance or complete the pending request.
Check(replica.Current("warehouse") == 3 && replica.HasPendingRequests, "Failed apply remains recoverable");
replica.Applied("warehouse", 6);
replica.SnapshotReceived("warehouse");
Check(!replica.HasPendingRequests, "Successful snapshot completes recovery");
Check(replica.ClassifyDelta("warehouse", 7) == LootDeltaDisposition.Apply, "Recovery restores delta flow");
replica.Applied("warehouse", 8); // Same content can legitimately have a newer revision.
Check(replica.ClassifyDelta("warehouse", 9) == LootDeltaDisposition.Apply, "No-op snapshot still advances revision");
Check(replica.ClassifyDelta("other", 1) == LootDeltaDisposition.Apply, "Inventories have independent versions");
replica.Clear();
Check(replica.Current("warehouse") == 0 && !replica.HasPendingRequests, "Session reset");

var changes = new CoalescedSlotChanges();
changes.Add(9); changes.Add(9); changes.Add(2); changes.Add(2); changes.Add(-1);
Check(changes.Drain().SequenceEqual(new[] { 2, 9 }), "Native/Harmony notifications coalesce per slot");
Check(!changes.Any, "Drain starts a new mutation batch");
changes.Add(9);
Check(changes.Drain().SequenceEqual(new[] { 9 }), "Later changes to the same slot still send");

var weapons = new bool[700];
foreach (var slot in new[] { 1, 5, 20, 100 }) weapons[slot] = true;
var indexes = new List<int>();
FilteredInventoryIndex.Rebuild(indexes, weapons.Length, slot => weapons[slot]);
var before = indexes.ToArray();
var evaluations = 0;
weapons[200] = true;
FilteredInventoryIndex.Update(indexes, new[] { 200 }, weapons.Length, slot => { evaluations++; return weapons[slot]; });
Check(evaluations == 1, "One deposit must not re-evaluate all 700 slots");
Check(indexes.Take(3).SequenceEqual(before.Take(3)), "Weapon added outside the current page leaves its slots unchanged");
Check(indexes.SequenceEqual(new[] { 1, 5, 20, 100, 200 }), "Weapon is available on later pages");
weapons[3] = true;
FilteredInventoryIndex.Update(indexes, new[] { 3 }, weapons.Length, slot => weapons[slot]);
Check(indexes.Take(3).SequenceEqual(new[] { 1, 3, 5 }), "Insertion before current page boundary shifts visible slots");
var afterWeapon = indexes.ToArray();
FilteredInventoryIndex.Update(indexes, new[] { 300 }, weapons.Length, slot => weapons[slot]);
Check(indexes.SequenceEqual(afterWeapon), "Non-weapon deposit leaves the weapon view unchanged");
weapons[1] = false;
FilteredInventoryIndex.Update(indexes, new[] { 1, 1 }, weapons.Length, slot => weapons[slot]);
Check(!indexes.Contains(1), "Concurrent take and duplicate notification");
Check(FilteredInventoryIndex.ClampPage(2, 100, 50) == 1, "Removing last item clamps the vanished page");
Check(FilteredInventoryIndex.ClampPage(0, 0, 50) == 0, "Empty warehouse");
Check(FilteredInventoryIndex.PageCount(101, 50) == 3, "Partial final page");
Check(FilteredInventoryIndex.VisibleCount(101, true, 2, 50) == 1, "Last page size");
Check(FilteredInventoryIndex.VisibleCount(101, false, 0, 50) == 101, "Non-paged views");

// Compare incremental indexes to an independent full filter after concurrent batches.
var random = new Random(94721);
for (var batch = 0; batch < 1000; batch++)
{
    var dirty = new HashSet<int>();
    for (var operation = 0; operation < 12; operation++)
    {
        var slot = random.Next(weapons.Length);
        weapons[slot] = random.Next(2) == 1;
        dirty.Add(slot);
    }
    FilteredInventoryIndex.Update(indexes, dirty, weapons.Length, slot => weapons[slot]);
    var expected = Enumerable.Range(0, weapons.Length).Where(slot => weapons[slot]).ToArray();
    Check(indexes.SequenceEqual(expected), $"Concurrent filter batch {batch} diverged");
    var page = FilteredInventoryIndex.ClampPage(3, indexes.Count, 50);
    Check(indexes.Skip(page * 50).Take(50).SequenceEqual(expected.Skip(page * 50).Take(50)),
        "Current page mapping must remain authoritative");
}
FilteredInventoryIndex.Rebuild(indexes, 25, slot => weapons[slot]);
Check(indexes.All(slot => slot < 25), "Capacity change rebuilds indexes");
Console.WriteLine($"PASS: {checks} warehouse replication and filtered-view checks.");
