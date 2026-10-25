using ItemStatsSystem;
using ItemStatsSystem.Items;

namespace EscapeFromDuckovCoopMod;

public partial class LootNet
{
    private sealed class TakeDestination
    {
        internal Item Source, Merge, Granted;
        internal Inventory Inventory;
        internal Slot Slot;
        internal int Position;
        internal bool Use, Confirmed;
        internal UniTaskCompletionSource<Item> Split;
        internal NetPeer Peer;
        internal LootTakeRequestRpc Request;
        internal LootTakeOkRpc Result;
        internal float RetryAt, NoticeAt;
    }
    private sealed class TakeEscrow
    {
        internal Item Item;
        internal LootTakeOkRpc Result;
    }
    private readonly Dictionary<uint, TakeDestination> _takes = new();
    private readonly TakeDeliveryLedger _takeDelivery = new();
    private readonly Dictionary<uint, NetPeer> _takeReceiptPeers = new();
    private readonly Dictionary<(NetPeer Peer, uint Token), TakeEscrow> _takeEscrow = new();
    private readonly Dictionary<NetPeer, uint> _takeHighWater = new();

    internal bool HoldWorldLoot(Inventory inv) => networkStarted && !IsServer && !_applyingLootState && inv &&
        !SharedHomeStorage.IsShared(inv) && !LootboxDetectUtil.IsPrivateInventory(inv) && LootboxDetectUtil.IsLootboxInventory(inv);

    internal bool RequestTake(Item item, Inventory destination = null, int position = -1, Slot slot = null,
        Item merge = null, int count = 0, bool use = false, UniTaskCompletionSource<Item> split = null)
    {
        if (!item || !HoldWorldLoot(item.InInventory)) return false;
        if (_takes.Values.Any(p => p.Source == item)) { split?.TrySetResult(null); return true; }
        var source = item.InInventory;
        if (connectedPeer == null || !_cliInitializedLoot.Contains(source) || _takes.Count >= 32)
        {
            split?.TrySetResult(null);
            Client_RequestLootState(source, true);
            return true;
        }
        var token = _nextLootToken++;
        var request = new LootTakeRequestRpc
        {
            Id = LootManagerInstance.BuildLootIdentifier(source), Token = token, Position = source.GetIndex(item), PreferDest = position,
            Protocol = 1, ExpectedVersion = GetCurrentVersion(source), ExpectedType = item.TypeID, ExpectedStack = item.StackCount,
            Count = count > 0 && count < item.StackCount ? count : 0
        };
        _takeDelivery.Register(token);
        _takes[token] = new TakeDestination { Source = item, Inventory = destination, Position = position, Slot = slot,
            Merge = merge, Use = use, Split = split, Peer = connectedPeer, Request = request, RetryAt = Time.unscaledTime + 2f };
        CoopTool.SendRpc(in request);
        return true;
    }
    internal UniTask<Item> SplitWorldLoot(Item item, int count)
    {
        if (!item.Stackable || count <= 0 || count >= item.StackCount) return UniTask.FromResult<Item>(null);
        var completion = new UniTaskCompletionSource<Item>();
        RequestTake(item, count: count, split: completion);
        return completion.Task;
    }
    private void TickTakes()
    {
        if (IsServer) return;
        foreach (var pair in _takes.ToArray())
        {
            var pending = pair.Value;
            if (Time.unscaledTime < pending.RetryAt) continue;
            pending.RetryAt = Time.unscaledTime + 2f;
            if (pending.Confirmed) TryDeliverTake(pair.Key, pending);
            else if (networkStarted && pending.Peer == connectedPeer) CoopTool.SendRpc(in pending.Request);
            else
            {
                // Unconfirmed requests cannot be replayed to a different host.
                pending.Split?.TrySetResult(null);
                _takes.Remove(pair.Key);
            }
        }
    }
    public void Server_HandleLootTakeRequest(RpcContext context, LootTakeRequestRpc request)
    {
        var peer = context.Sender;
        if (!context.IsServer || peer == null || LootManagerInstance == null) return;
        var key = (peer, request.Token);
        if (request.Protocol != 1) { Server_SendLootDeny(peer, "take_protocol_mismatch"); return; }
        if (request.Phase != 0)
        {
            // Only a positive delivery receipt can dispose of the host's detached
            // copy. Timeouts, scene unloads and disconnections never return or delete it.
            if (request.Phase == 1 && _takeEscrow.TryGetValue(key, out var completed))
            {
                _takeEscrow.Remove(key);
                if (completed.Item) completed.Item.DestroyTree();
            }
            return;
        }
        if (_takeEscrow.TryGetValue(key, out var previous))
        { CoopTool.SendRpcTo(peer, in previous.Result); return; }
        if (_takeHighWater.TryGetValue(peer, out var highest) && request.Token <= highest)
        { SendTakeDenied(peer, request); return; }
        _takeHighWater[peer] = request.Token;
        var inv = LootManagerInstance.ResolveLootInv(request.Id.Scene, request.Id.PositionKey, request.Id.InstanceId, request.Id.LootUid);
        var item = inv && request.Position >= 0 && request.Position < inv.Capacity ? inv.GetItemAt(request.Position) : null;
        if (!inv || !LootboxDetectUtil.IsLootboxInventory(inv) || SharedHomeStorage.IsShared(inv) || !item || IsQuestTagged(item, inv) ||
            request.ExpectedVersion != GetCurrentVersion(inv) || item.TypeID != request.ExpectedType || item.StackCount != request.ExpectedStack ||
            request.Count < 0 || request.Count >= item.StackCount || request.Count > 0 && !item.Stackable ||
            _takeEscrow.Keys.Count(k => k.Peer == peer) >= 32)
        { SendTakeDenied(peer, request); return; }

        var applying = _serverApplyingLoot;
        _serverApplyingLoot = true;
        TakeEscrow accepted = null;
        try
        {
            var snapshot = ItemTool.MakeSnapshot(item);
            Item held;
            if (request.Count > 0)
            {
                snapshot.Stack = request.Count;
                held = ItemTool.BuildItemFromSnapshot(snapshot);
                if (!held) { SendTakeDenied(peer, request); return; }
                item.StackCount -= request.Count;
            }
            else if (!inv.RemoveAt(request.Position, out held) || !held)
            { SendTakeDenied(peer, request); return; }
            var version = NextVersion(inv);
            accepted = new TakeEscrow { Item = held, Result = new LootTakeOkRpc
            {
                Id = request.Id, Token = request.Token, Slot = request.Position, TypeId = snapshot.TypeId, Stack = snapshot.Stack,
                Accepted = true, Version = version, Item = snapshot
            } };
            _takeEscrow[key] = accepted;
            held.transform.SetParent(null);
            UnityEngine.Object.DontDestroyOnLoad(held.gameObject);
            BroadcastLootDelta(inv, request.Position, request.Count > 0 ? item.TypeID : -1,
                request.Count > 0 ? item.StackCount : 0, request.Count == 0, version);
            CoopTool.SendRpcTo(peer, in accepted.Result);
        }
        catch (Exception e)
        {
            CoopLogSystem.WriteNetworkDiagnostic($"[LOOT] take failed token={request.Token} committed={accepted != null}: {e}");
            if (accepted == null) SendTakeDenied(peer, request);
        }
        finally { _serverApplyingLoot = applying; }
    }
    private void SendTakeDenied(NetPeer peer, LootTakeRequestRpc request)
    {
        var result = new LootTakeOkRpc { Id = request.Id, Token = request.Token, Slot = request.Position, Accepted = false };
        CoopTool.SendRpcTo(peer, in result);
    }
    public void Client_OnLootTakeOk(LootTakeOkRpc result)
    {
        if (IsServer) return;
        if (_takeDelivery.TryReceipt(result.Token, out var receipt))
        {
            if (receipt == 1 && _takeReceiptPeers.TryGetValue(result.Token, out var peer) && peer == connectedPeer) SendTakeReceipt(result.Token);
            return;
        }
        if (!_takes.TryGetValue(result.Token, out var pending) || pending.Peer != connectedPeer || !_takeDelivery.TryClaim(result.Token)) return;
        if (!result.Accepted)
        {
            _takes.Remove(result.Token);
            _takeDelivery.Finish(result.Token, false);
            pending.Split?.TrySetResult(null);
            var source = pending.Source ? pending.Source.InInventory : null;
            if (source) Client_RequestLootState(source, true);
            CoopLogSystem.WriteNetworkDiagnostic($"[LOOT] take denied token={result.Token} slot={result.Slot}; refreshing source");
            return;
        }
        pending.Result = result;
        pending.Confirmed = true;
        TryDeliverTake(result.Token, pending);
        var sourceInv = LootManagerInstance?.ResolveLootInv(result.Id.Scene, result.Id.PositionKey, result.Id.InstanceId, result.Id.LootUid);
        if (sourceInv && GetCurrentVersion(sourceInv) < result.Version) Client_RequestLootState(sourceInv, true);
    }
    private void TryDeliverTake(uint token, TakeDestination pending)
    {
        var stored = false;
        try
        {
            if (!ReferenceEquals(pending.Granted, null) && (!pending.Granted || pending.Granted.StackCount <= 0))
            {
                // A merge callback may have consumed the item before throwing.
                // Never reconstruct a second copy from the original snapshot.
                CompleteTake(token, pending);
                return;
            }
            if (!pending.Granted)
            {
                pending.Granted = ItemTool.BuildItemFromSnapshot(pending.Result.Item);
                if (!pending.Granted) return;
                pending.Granted.transform.SetParent(null);
                UnityEngine.Object.DontDestroyOnLoad(pending.Granted.gameObject);
            }
            var item = pending.Granted;
            // A previous destination callback can throw after insertion.
            stored = item.InInventory != null || item.PluggedIntoSlot != null;
            if (!stored && pending.Split != null) stored = pending.Split.TrySetResult(item);
            if (!stored && pending.Merge && pending.Merge.TypeID == item.TypeID && !HoldWorldLoot(pending.Merge.InInventory) &&
                pending.Merge.MaxStackCount - pending.Merge.StackCount >= item.StackCount)
            { pending.Merge.Combine(item); stored = !item || item.StackCount == 0; }
            if (!stored && pending.Slot != null && pending.Slot.Content == null) stored = pending.Slot.Plug(item, out _);
            if (!stored && pending.Inventory && pending.Position >= 0 && !pending.Inventory.GetItemAt(pending.Position))
                stored = pending.Inventory.AddAt(item, pending.Position);
            if (!stored && !pending.Use && pending.Inventory && pending.Position < 0 && CanReceiveTake(pending.Inventory, item))
                stored = ItemUtilities.AddAndMerge(pending.Inventory, item);
            var main = CharacterMainControl.Main;
            var mainInv = main?.CharacterItem?.Inventory;
            if (!stored && pending.Use && mainInv && mainInv.GetFirstEmptyPosition() >= 0)
                stored = mainInv.AddItem(item); // Keep the exact item alive for UseItem (no merge-away).
            if (!stored && !pending.Use && mainInv && (CanReceiveTake(mainInv, item) ||
                main.CharacterItem.Slots.Any(s => s.Content == null && s.CanPlug(item))))
                stored = ItemUtilities.SendToPlayerCharacter(item);
        }
        catch (Exception e)
        {
            stored |= !ReferenceEquals(pending.Granted, null) && (!pending.Granted || pending.Granted.InInventory != null || pending.Granted.PluggedIntoSlot != null);
            CoopLogSystem.WriteNetworkDiagnostic($"[LOOT] take delivery token={token} stored={stored}: {e}");
        }
        if (!stored)
        {
            // No negative acknowledgement, rollback or client destruction. Retain
            // the confirmed item and retry when space/resources become available.
            if (Time.unscaledTime >= pending.NoticeAt)
            {
                pending.NoticeAt = Time.unscaledTime + 10;
                Debug.LogWarning($"[LOOT] confirmed take waiting for inventory space token={token} type={pending.Result.TypeId}");
                CharacterMainControl.Main?.PopText("背包空间不足，已取物品暂存中", 1f);
            }
            return;
        }
        CompleteTake(token, pending);
    }
    private static bool CanReceiveTake(Inventory inv, Item item) => inv && item &&
        (inv.GetFirstEmptyPosition() >= 0 || item.Stackable && inv.Where(i => i && i.TypeID == item.TypeID)
            .Sum(i => Math.Max(0, i.MaxStackCount - i.StackCount)) >= item.StackCount);
    private void CompleteTake(uint token, TakeDestination pending)
    {
        _takes.Remove(token);
        _takeDelivery.Finish(token, true);
        _takeReceiptPeers[token] = pending.Peer;
        if (networkStarted && pending.Peer == connectedPeer) SendTakeReceipt(token);
        if (pending.Use && pending.Granted && CharacterMainControl.Main) CharacterMainControl.Main.UseItem(pending.Granted);
    }
    private static void SendTakeReceipt(uint token)
    {
        var receipt = new LootTakeRequestRpc { Protocol = 1, Token = token, Phase = 1 };
        CoopTool.SendRpc(in receipt);
    }
}
