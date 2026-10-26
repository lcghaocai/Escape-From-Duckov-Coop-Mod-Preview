using ItemStatsSystem;
using ItemStatsSystem.Data;
using Saves;

namespace EscapeFromDuckovCoopMod;

// The pet is a separate inventory, outside CharacterItem's tree. Keep its
// per-player room save separate from both the host pet and the personal save.
internal static class SharedHomePetInventory
{
    internal const string ReportKey = "petInventory";
    internal const string SaveSuffix = "/PetInventory";
    private const string NativeKey = "Inventory/Inventory_Safe";
    private static InventoryData _personal;
    private static int _loads;
    internal static bool Loading => _loads > 0;
    internal static bool NativeReady => !LevelConfig.SavePet || PetProxy.PetInventory && !PetProxy.PetInventory.Loading;

    internal static void CapturePersonal()
    {
        if (_personal == null && LevelConfig.SavePet && NativeReady)
            _personal = InventoryData.FromInventory(PetProxy.PetInventory);
    }
    internal static InventoryData TakePersonal()
    {
        var data = _personal;
        _personal = null;
        return data;
    }
    internal static void AddReport(ES3File report)
    {
        // Missing means "not ready", not "empty". An actual empty inventory is
        // still reported so taking everything out cannot resurrect old items.
        if (Loading || !LevelManager.LevelInited || !LevelConfig.SavePet || !NativeReady) return;
        report.Save(ReportKey, InventoryData.FromInventory(PetProxy.PetInventory));
    }
    internal static void Validate(InventoryData data)
    {
        if (data?.entries == null || data.capacity < 0 || data.capacity > 16384 || data.entries.Count > 16384)
            throw new InvalidOperationException("Invalid shared pet inventory");
        var positions = new HashSet<int>();
        foreach (var entry in data.entries)
            if (entry.inventoryPosition < 0 || entry.inventoryPosition >= 16384 ||
                !positions.Add(entry.inventoryPosition) || entry.itemTreeData?.RootData == null ||
                entry.itemTreeData.entries.Count > 10000)
                throw new InvalidOperationException("Invalid shared pet inventory entry");
    }
    internal static async UniTask<bool> Restore(InventoryData data, Func<bool> current, bool personal)
    {
        Validate(data);
        _loads++;
        var incoming = new List<(Item Item, int Position)>();
        var attached = false;
        try
        {
            // Native PetProxy.Start launches its own asynchronous inventory load.
            // Let that finish before installing the host's per-player snapshot.
            await UniTask.WaitUntil(() => !current() || NativeReady);
            if (!current()) return false;
            var target = PetProxy.PetInventory;
            if (!target || !LevelConfig.SavePet)
            {
                if (!personal) SavesSystem.Save(NativeKey, data);
                return true;
            }
            foreach (var entry in data.entries)
            {
                var item = await ItemTreeData.InstantiateAsync(entry.itemTreeData);
                if (!item) throw new InvalidOperationException("Unable to restore pet inventory item");
                incoming.Add((item, entry.inventoryPosition));
                if (!current() || !target || target != PetProxy.PetInventory) return false;
            }
            if (!current() || !target || target != PetProxy.PetInventory) return false;
            SharedHome.Apply(() =>
            {
                var old = target.Where(item => item).Select(item => (Item: item, Position: target.GetIndex(item))).ToArray();
                var locks = target.lockedIndexes.ToArray();
                target.Loading = true;
                try
                {
                    target.SetCapacity(Math.Max(target.Capacity, Math.Max(data.capacity,
                        incoming.Select(entry => entry.Position + 1).DefaultIfEmpty(0).Max())));
                    foreach (var entry in old) entry.Item.Detach();
                    foreach (var entry in incoming)
                        if (!target.AddAt(entry.Item, entry.Position))
                            throw new InvalidOperationException("Unable to attach pet inventory item");
                    target.lockedIndexes.Clear();
                    if (data.lockedIndexes != null) target.lockedIndexes.AddRange(data.lockedIndexes);
                    attached = true;
                }
                catch
                {
                    foreach (var entry in incoming) if (entry.Item) entry.Item.Detach();
                    foreach (var entry in old)
                        if (entry.Item && entry.Item.InInventory != target && !target.AddAt(entry.Item, entry.Position))
                            Debug.LogError("[SharedHome][Pet] Could not return original item to its slot");
                    target.lockedIndexes.Clear();
                    target.lockedIndexes.AddRange(locks);
                    throw;
                }
                finally { target.Loading = false; }
                foreach (var entry in old) if (entry.Item) entry.Item.DestroyTree();
            });
            if (!personal) SavesSystem.Save(NativeKey, data);
            CoopLogSystem.WriteNetworkDiagnostic($"[SharedHome][Pet] restored personal={personal} items={data.entries.Count}");
            return true;
        }
        finally
        {
            if (!attached) foreach (var entry in incoming) if (entry.Item && !entry.Item.InInventory) entry.Item.DestroyTree();
            _loads--;
        }
    }
}

[HarmonyPatch(typeof(PetProxy), "OnCollectSaveData")]
internal static class SharedHomePetSaveGuardPatch
{
    private static bool Prefix(PetProxy __instance)
    {
        if (SharedHomeLoadouts.Restoring || SharedHomePetInventory.Loading) return false;
        return !SharedHome.Guest || __instance.Inventory && !__instance.Inventory.Loading;
    }
}
