using ItemStatsSystem;
using ItemStatsSystem.Items;
using Newtonsoft.Json.Linq;
using Saves;

namespace EscapeFromDuckovCoopMod;

internal static class SharedHomeStorage
{
    internal const int LootUid = int.MaxValue - 17;
    private static InteractableLootbox _registered;
    private sealed class Destination
    {
        internal Item Item;
        internal Inventory Inventory;
        internal Slot Slot;
        internal Item Merge;
        internal bool Use;
        internal int Position;
        internal UniTaskCompletionSource<Item> Split;
        internal float Deadline;
    }
    private static readonly Dictionary<string, Destination> Pending = new();
    private static readonly Dictionary<string, (NetPeer Peer, Item Item)> Escrow = new();
    private const string EscrowKey = "SharedHome/StorageEscrow";
    private const float PendingTimeoutSeconds = 8f;
    private static float _nextBusyTipTime;
    private static bool _recovered;
    internal static bool Sorting { get; private set; }
    internal static bool IsShared(Inventory inventory) => SharedHome.Active && (!SharedHome.Guest || SharedHomeSave.OverlayActive) &&
        inventory && ReferenceEquals(inventory, PlayerStorage.Inventory);
    internal static bool CanTransfer => !PlayerStorage.Loading && !(PlayerStorage.Inventory && PlayerStorage.Inventory.Loading) &&
        CharacterMainControl.Main && CharacterMainControl.Main.CharacterItem;

    private static void ExpirePending()
    {
        if (Pending.Count == 0) return;
        var now = Time.unscaledTime;
        foreach (var pair in Pending.ToArray())
        {
            if (now < pair.Value.Deadline) continue;
            Pending.Remove(pair.Key);
            pair.Value.Split?.TrySetResult(null);
            CoopLogSystem.WriteNetworkDiagnostic($"[SharedHome] storage request timed out token={pair.Key}");
        }
    }

    private static void NotifyBusy()
    {
        if (Time.unscaledTime < _nextBusyTipTime) return;
        _nextBusyTipTime = Time.unscaledTime + 1.5f;
        Debug.LogWarning("[SharedHome] storage request skipped: loadout not ready or session inactive");
    }
    internal static void BeginSort()
    {
        COOPManager.LootNet.Server_FlushWarehouseChanges();
        Sorting = true;
    }
    internal static void EndSort()
    {
        Sorting = false;
        if (!IsShared(PlayerStorage.Inventory) || !NetService.Instance.IsServer) return;
        COOPManager.LootNet.Server_CompleteWarehouseSort();
        COOPManager.LootNet.Server_SendLootboxStateToPeers(
            NetService.Instance.netManager.ConnectedPeerList.Where(SharedHomeCompatibility.Allows), PlayerStorage.Inventory);
        SharedHome.MarkSaveDirty();
    }
    internal static void Ensure()
    {
        if (!SharedHome.Active || !PlayerStorage.Instance || PlayerStorage.Loading || !LootManager.Instance) return;
        ExpirePending();
        if (SharedHome.Guest && !SharedHomeSave.OverlayActive) return;
        if (NetService.Instance.IsServer && !_recovered)
        {
            _recovered = true;
            var saved = SavesSystem.Load<List<byte[]>>(EscrowKey);
            if (saved != null)
                foreach (var bytes in saved)
                {
                    var reader = new NetPacketReader(bytes, 0, bytes.Length);
                    PlayerStorage.Push(ItemTool.BuildItemFromSnapshot(ItemTool.ReadItemSnapshot(reader)));
                }
            SaveEscrow();
        }
        var box = PlayerStorage.Instance.InteractableLootBox;
        if (!box || _registered == box && CoopSyncDatabase.Loot.TryGetByLootUid(LootUid, out var entry) && entry.Inventory == PlayerStorage.Inventory) return;
        Unregister();
        _registered = box;
        CoopSyncDatabase.Loot.Register(box, PlayerStorage.Inventory, LootUid);
        if (NetService.Instance.IsServer)
        {
            LootManager.Instance._srvLootByUid[LootUid] = PlayerStorage.Inventory;
            // Guests can finish loading first and have their initial request denied.
            COOPManager.LootNet.Server_SendLootboxStateToPeers(
                NetService.Instance.netManager.ConnectedPeerList.Where(SharedHomeCompatibility.Allows), PlayerStorage.Inventory);
        }
        else
        {
            LootManager.Instance._cliLootByUid[LootUid] = PlayerStorage.Inventory;
            COOPManager.LootNet.Client_RequestLootState(PlayerStorage.Inventory, true);
        }
    }
    internal static void Reset()
    {
        foreach (var entry in Escrow.Values)
            if (entry.Item && PlayerStorage.Inventory) PlayerStorage.Push(entry.Item);
        Escrow.Clear();
        foreach (var pending in Pending.Values) pending.Split?.TrySetResult(null);
        Pending.Clear();
        _nextBusyTipTime = 0f;
        SharedHomeStorageOperations.Reset();
        SharedWarehouseDisplay.Reset();
        if (_recovered) SaveEscrow();
        _recovered = false;
        Unregister();
    }
    private static void Unregister()
    {
        if (_registered) CoopSyncDatabase.Loot.Unregister(_registered);
        _registered = null;
        LootManager.Instance?._srvLootByUid.Remove(LootUid);
        LootManager.Instance?._cliLootByUid.Remove(LootUid);
    }
    internal static bool RequestTake(Item item, Inventory destination = null, int position = -1, Slot slot = null, Item merge = null, int count = 0, bool use = false)
    {
        ExpirePending();
        if (!item || !Hold(item.InInventory)) return false;
        if (IsShared(destination))
            return RequestEdit(item, new { operation = "move", target = position, expected = SharedHomeStorageOperations.Key(destination.GetItemAt(position)) });
        if (Pending.Values.Any(p => p.Item == item)) return true;
        if (!SharedHome.CanRequest) { NotifyBusy(); return true; }

        var token = Guid.NewGuid().ToString("N");
        Pending[token] = new Destination
        {
            Item = item, Inventory = destination, Position = position, Slot = slot, Merge = merge, Use = use,
            Deadline = Time.unscaledTime + PendingTimeoutSeconds
        };
        if (!SharedHome.RequestChecked("storageTake", new { token, position = item.InInventory.GetIndex(item), type = item.TypeID,
            count = count >= item.StackCount ? 0 : count, expected = SharedHomeStorageOperations.Key(item) }))
        {
            Pending.Remove(token);
            NotifyBusy();
        }
        return true;
    }
    internal static bool RequestEdit(Item item, object details)
    {
        ExpirePending();
        if (!item || !Hold(item.InInventory)) return false;
        if (Pending.Values.Any(p => p.Item == item)) return true;
        if (!SharedHome.CanRequest) { NotifyBusy(); return true; }

        var token = Guid.NewGuid().ToString("N");
        Pending[token] = new Destination { Item = item, Deadline = Time.unscaledTime + PendingTimeoutSeconds };
        if (!SharedHome.RequestChecked("storageEdit", new { token, position = item.InInventory.GetIndex(item), expected = SharedHomeStorageOperations.Key(item), details }))
        {
            Pending.Remove(token);
            NotifyBusy();
        }
        return true;
    }
    internal static void Take(JObject args, NetPeer peer)
    {
        var token = (string)args["token"];
        if (string.IsNullOrEmpty(token) || token.Length > 64 || Escrow.ContainsKey(token)) return;
        var writer = new NetDataWriter();
        try
        {
            var position = (int)args["position"];
            var inv = PlayerStorage.Inventory;
            var item = inv?.GetItemAt(position);
            var accepted = item && item.TypeID == (int)args["type"] && SharedHomeStorageOperations.Key(item) == (string)args["expected"];
            var count = (int?)args["count"] ?? 0;
            if (count > 0 && (!accepted || !item.Stackable || count >= item.StackCount)) accepted = false;
            writer.Put(token);
            writer.Put(accepted);
            if (accepted)
            {
                var snapshot = ItemTool.MakeSnapshot(item);
                var held = item;
                if (count > 0)
                {
                    snapshot.Stack = count;
                    held = ItemTool.BuildItemFromSnapshot(snapshot);
                    held.StackCount = count;
                    item.StackCount -= count;
                }
                else inv.RemoveAt(position, out _);
                ItemTool.WriteItemSnapshot(writer, ItemTool.MakeSnapshot(held));
                held.transform.SetParent(null);
                UnityEngine.Object.DontDestroyOnLoad(held.gameObject);
                Escrow[token] = (peer, held);
                SaveEscrow();
            }
        }
        catch (Exception e)
        {
            // 任何异常都必须回包，否则客机的 Pending token 会一直挂着，表现为仓库卡死。
            writer.Reset();
            writer.Put(token);
            writer.Put(false);
            CoopLogSystem.WriteNetworkDiagnostic($"[SharedHome] storageTake failed token={token}: {e}");
        }
        SharedHome.Reply(peer, writer.CopyData());
    }
    internal static void ReceiveTake(byte[] bytes)
    {
        var reader = new NetPacketReader(bytes, 0, bytes.Length);
        var token = reader.GetString();
        var accepted = reader.GetBool();

        Pending.TryGetValue(token, out var destination);
        if (destination != null) Pending.Remove(token);

        if (!accepted)
        {
            destination?.Split?.TrySetResult(null);
            return;
        }

        var item = ItemTool.BuildItemFromSnapshot(ItemTool.ReadItemSnapshot(reader));
        if (!item)
        {
            // 回包到了但物品构建失败：让主机把暂存物退回仓库，避免 escrow 永久占住。
            SharedHome.RequestChecked("storageTaken", new { token, stored = false });
            return;
        }

        var stored = false;
        try
        {
            SharedHome.Apply(() =>
            {
                // 正常 split：只要完成等待就直接交付；如果等待已超时/取消，
                // 不要提前 return，继续走普通入库路径，并照常释放主机 escrow。
                if (destination?.Split != null && destination.Split.TrySetResult(item))
                {
                    stored = true;
                    return;
                }
                if (destination?.Merge && destination.Merge.TypeID == item.TypeID &&
                    destination.Merge.MaxStackCount - destination.Merge.StackCount >= item.StackCount)
                {
                    destination.Merge.Combine(item);
                    stored = true;
                }
                if (!stored && destination?.Slot != null && destination.Slot.Content == null)
                    stored = destination.Slot.Plug(item, out _);
                if (!stored && destination?.Inventory && destination.Position >= 0 &&
                    !destination.Inventory.GetItemAt(destination.Position))
                    stored = destination.Inventory.AddAt(item, destination.Position);
                if (!stored && destination?.Inventory && destination.Position < 0 && CanReceive(destination.Inventory, item))
                    stored = ItemUtilities.AddAndMerge(destination.Inventory, item);
                if (!stored && CharacterMainControl.Main?.CharacterItem != null &&
                    (CanReceive(CharacterMainControl.Main.CharacterItem.Inventory, item) ||
                     CharacterMainControl.Main.CharacterItem.Slots.Any(s => s.Content == null && s.CanPlug(item))))
                    stored = ItemUtilities.SendToPlayerCharacter(item);
                if (stored && destination?.Use == true && item) CharacterMainControl.Main.UseItem(item);
            });
        }
        catch (Exception e)
        {
            stored = false;
            CoopLogSystem.WriteNetworkDiagnostic($"[SharedHome] receive take failed token={token}: {e}");
        }

        if (!stored && item) item.DestroyTree();
        SharedHomeLoadouts.Flush();
        // 无论 split 是否完成，都要释放主机 escrow；旧代码 split 分支提前 return 会留下暂存物。
        SharedHome.RequestChecked("storageTaken", new { token, stored });
    }
    internal static UniTask<Item> Split(Item item, int count)
    {
        ExpirePending();
        if (!item.Stackable || count <= 0 || count >= item.StackCount || Pending.Values.Any(p => p.Item == item))
            return UniTask.FromResult<Item>(null);
        if (!SharedHome.CanRequest) { NotifyBusy(); return UniTask.FromResult<Item>(null); }

        var completion = new UniTaskCompletionSource<Item>();
        var token = Guid.NewGuid().ToString("N");
        Pending[token] = new Destination
        {
            Item = item, Split = completion, Deadline = Time.unscaledTime + PendingTimeoutSeconds
        };
        if (!SharedHome.RequestChecked("storageTake", new { token, position = item.InInventory.GetIndex(item), type = item.TypeID, count,
            expected = SharedHomeStorageOperations.Key(item) }))
        {
            Pending.Remove(token);
            NotifyBusy();
            return UniTask.FromResult<Item>(null);
        }
        return completion.Task;
    }
    private static bool CanReceive(Inventory inventory, Item item) => inventory && item &&
        (inventory.GetFirstEmptyPosition() >= 0 || item.Stackable && inventory.Where(i => i && i.TypeID == item.TypeID)
            .Sum(i => Math.Max(0, i.MaxStackCount - i.StackCount)) >= item.StackCount);
    internal static void FinishTake(JObject args, NetPeer peer)
    {
        var token = (string)args["token"];
        if (token == null || !Escrow.TryGetValue(token, out var entry) || entry.Peer != peer) return;
        Escrow.Remove(token);
        if ((bool)args["stored"]) entry.Item.DestroyTree();
        else PlayerStorage.Push(entry.Item);
        SaveEscrow();
    }
    internal static void PeerDisconnected(NetPeer peer)
    {
        if (Escrow.Count == 0) return;
        foreach (var key in Escrow.Where(p => p.Value.Peer == peer).Select(p => p.Key).ToArray())
        {
            var item = Escrow[key].Item;
            Escrow.Remove(key);
            if (item) PlayerStorage.Push(item);
        }
        SaveEscrow();
    }
    internal static bool Hold(Inventory inventory) => SharedHome.Guest && !SharedHome.Applying &&
        CanTransfer && COOPManager.LootNet?.ApplyingLootState != true && IsShared(inventory);
    internal static string BufferKey(int index)
    {
        if (index < 0 || index >= PlayerStorage.IncomingItemBuffer.Count) return string.Empty;
        using var hash = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(ES3.Serialize(PlayerStorage.IncomingItemBuffer[index])));
    }
    internal static async UniTaskVoid TakeBuffer(int index, string expected)
    {
        if (index < 0 || index >= PlayerStorage.IncomingItemBuffer.Count || BufferKey(index) != expected) return;
        await PlayerStorage.TakeBufferItem(index);
        SharedHome.MarkDirty();
    }
    internal static void SendBuffer(Item item, bool toBuffer = true, int position = -1)
    {
        if (!item || !CanTransfer) return;
        if (!SharedHome.CanRequest) { NotifyBusy(); return; }

        var data = new ES3File(false);
        data.Save("item", ItemStatsSystem.Data.ItemTreeData.FromItem(item));
        var character = SharedHomeLoadouts.Report();
        // 先确认请求真的发出去，再销毁本地物品；否则 Ready 变化的瞬间会直接吞掉物品。
        if (!SharedHome.RequestChecked("storageBufferPut", new
            {
                item = Convert.ToBase64String(data.LoadRawBytes()),
                character = Convert.ToBase64String(character),
                toBuffer,
                position
            }))
        {
            NotifyBusy();
            return;
        }

        item.Detach();
        item.DestroyTree();
    }
    internal static async UniTaskVoid PutBuffer(JObject args, NetPeer peer)
    {
        var data = new ES3File(Convert.FromBase64String((string)args["item"]));
        var tree = data.Load<ItemStatsSystem.Data.ItemTreeData>("item");
        if (tree?.entries == null || tree.entries.Count == 0 || tree.entries.Count > 10000) return;
        SharedHomeLoadouts.Receive(peer, 6, Convert.FromBase64String((string)args["character"]));
        var item = await ItemStatsSystem.Data.ItemTreeData.InstantiateAsync(tree);
        if (item)
        {
            var buffer = (bool?)args["toBuffer"] ?? true;
            var position = (int?)args["position"] ?? -1;
            var inventory = PlayerStorage.Inventory;
            if (!buffer && inventory && position >= 0 && position < inventory.Capacity)
            {
                var target = inventory.GetItemAt(position);
                if (!target)
                {
                    if (inventory.AddAt(item, position)) item = null;
                }
                else if (target.Stackable && target.TypeID == item.TypeID) target.Combine(item);
            }
            if (item && item.StackCount > 0) PlayerStorage.Push(item, buffer);
        }
        SharedHome.MarkSaveDirty();
    }
    private static void SaveEscrow()
    {
        if (NetService.Instance?.IsServer != true) return;
        var items = new List<byte[]>();
        foreach (var entry in Escrow.Values)
        {
            var writer = new NetDataWriter();
            ItemTool.WriteItemSnapshot(writer, ItemTool.MakeSnapshot(entry.Item));
            items.Add(writer.CopyData());
        }
        SavesSystem.Save(EscrowKey, items);
        SharedHome.MarkSaveDirty();
    }
}
