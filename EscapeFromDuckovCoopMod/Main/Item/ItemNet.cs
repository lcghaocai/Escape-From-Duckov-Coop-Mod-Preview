// Escape-From-Duckov-Coop-Mod-Preview
// Copyright (C) 2025  Mr.sans and InitLoader's team
//
// This program is not a free software.
// It's distributed under a license based on AGPL-3.0,
// with strict additional restrictions:
//  YOU MUST NOT use this software for commercial purposes.
//  YOU MUST NOT use this software to run a headless game server.
//  YOU MUST include a conspicuous notice of attribution to
//  Mr-sans-and-InitLoader-s-team/Escape-From-Duckov-Coop-Mod-Preview as the original author.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;
using Duckov.Scenes;
using LiteNetLib;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using UnityEngine;

namespace EscapeFromDuckovCoopMod;

public sealed partial class ItemNet
{
    [ThreadStatic] public static bool InNetworkDrop;

    private readonly SnapshotReceiveQueue<uint, ItemDropSnapshotEntry> _snapshotStream = new();
    // Live drops that arrived after the current drop-snapshot request started.
    // A late reset must not delete them; the snapshot was built before they existed.
    private readonly HashSet<uint> _dropsCreatedSinceSnapshot = new();

    private readonly Dictionary<uint, uint> _pendingByToken = new();
    private readonly HashSet<uint> _pendingPickups = new();
    private uint _nextDropId = 1;

    public void Server_Update(float deltaTime)
    {
        var service = NetService.Instance;
        if (service == null || !service.IsServer || !service.networkStarted || !LevelManager.LevelInited) return;
        _serverDropSnapshots.Tick(
            peer => peer.ConnectionState == ConnectionState.Connected,
            peer => NetService.Instance.netManager.GetReliableQueueLength(peer) >= 16,
            Server_SendNextDropChunk, SnapshotNow, 0.002);
    }

    public void Client_Update(float deltaTime)
    {
        EnsureClientDropSnapshot();
        if (!_snapshotStream.InProgress)
            return;

        ApplyPendingSnapshotReset();

        const int maxSpawnsPerFrame = 6;
        var processed = 0;
        var deadline = SnapshotNow() + 0.002;

        while (processed < maxSpawnsPerFrame && _snapshotStream.Count > 0 &&
               (processed == 0 || SnapshotNow() < deadline))
        {
            processed++;
            var entry = _snapshotStream.Dequeue();
            if (_snapshotStream.IsRemoved(entry.DropId) ||
                CoopSyncDatabase.Drops.TryGetById(entry.DropId, out _)) continue;

            var item = ItemTool.BuildItemFromSnapshot(entry.Snapshot);
            if (item == null) continue;

            DuckovItemAgent agent = null;
            try
            {
                InNetworkDrop = true;
                agent = entry.PickupThrowable ? PickupThrowableSync.Spawn(item, entry.Position, entry.Velocity) :
                    item.Drop(entry.Position, entry.CreateRigidbody, entry.Direction, entry.Angle);
                if (agent && entry.SceneBuildIndex >= 0)
                    MultiSceneCore.MoveToActiveWithScene(entry.PickupThrowable ? agent.transform.parent.gameObject : agent.gameObject, entry.SceneBuildIndex);
            }
            finally
            {
                InNetworkDrop = false;
            }

            ItemTool.AddNetDropTag(agent ? agent.gameObject : item.gameObject, entry.DropId);
            CoopSyncDatabase.Drops.Register(entry.DropId, item, agent ? agent.gameObject : item.gameObject);
            _nextDropId = Math.Max(_nextDropId, entry.DropId + 1);
        }

        _snapshotStream.FinishFrame();
    }

    public void Reset() => Reset(preserveDropIds: false);

    internal void Reset(bool preserveDropIds)
    {
        _snapshotStream.Clear();
        _dropsCreatedSinceSnapshot.Clear();
        _pendingByToken.Clear();
        _pendingPickups.Clear();
        _serverDropSnapshots.Clear();
        _serverDropRegistryBuilt = false;
        _clientDropRequestPeer = null;
        _clientDropRequestScene = null;
        _lastDropRequestAt = double.NegativeInfinity;
        _lastDropReceiveAt = double.NegativeInfinity;
        if (!preserveDropIds) _nextDropId = 1;
    }

    public void Server_RebuildDropRegistryFromScene()
    {
        if (!NetService.Instance.IsServer || NetService.Instance?.networkStarted != true)
            return;

        _serverDropRegistryBuilt = true;
        var seenDropIds = new HashSet<uint>();
        CoopSyncDatabase.Drops.Clear();

        foreach (var tag in UnityEngine.Object.FindObjectsOfType<NetDropTag>(true))
        {
            if (tag == null) continue;

            if (ItemTool.HasQuestTag(tag.gameObject))
                continue;

            var dropId = tag.id;
            if (dropId == 0) continue;

            if (!seenDropIds.Add(dropId))
                continue;

            var item = ResolveItemFromTag(tag);
            if (item == null) continue;

            CoopSyncDatabase.Drops.Register(dropId, item.Item, tag.gameObject);
            _nextDropId = Math.Max(_nextDropId, dropId + 1);
        }
    }

    public void Client_RequestDropSnapshot(bool includeReset = true)
    {
        var service = NetService.Instance;
        var scene = SceneNet.Instance?._sceneReadySidSent;
        if (service == null || service.IsServer || !service.networkStarted ||
            service.connectedPeer?.ConnectionState != ConnectionState.Connected ||
            !LevelManager.LevelInited || SceneLoader.IsSceneLoading || string.IsNullOrEmpty(scene)) return;
        var now = SnapshotNow();
        if (_clientDropRequestPeer == service.connectedPeer && _clientDropRequestScene == scene &&
            now - _lastDropRequestAt < 1) return;
        _clientDropRequestPeer = service.connectedPeer;
        _clientDropRequestScene = scene;
        _lastDropRequestAt = now;
        var rpc = new ItemDropSnapshotRequestRpc { Reset = includeReset };
        CoopTool.SendRpc(in rpc);
    }

    public void Server_HandleDropRequest(RpcContext context, ItemDropRequestRpc message)
    {
        if (!context.IsServer) return;

        var item = ItemTool.BuildItemFromSnapshot(message.Snapshot);
        if (item == null) return;

        if (ItemTool.HasQuestTag(item))
            return;

        DuckovItemAgent agent = null;
        try
        {
            InNetworkDrop = true;
            agent = message.PickupThrowable ? PickupThrowableSync.Spawn(item, message.Position, message.Velocity,
                CoopTool.TryGetRemoteCharacterForPeer(context.Sender)) :
                item.Drop(message.Position, message.CreateRigidbody, message.Direction, message.Angle);
        }
        finally
        {
            InNetworkDrop = false;
        }

        var dropId = _nextDropId++;
        CoopSyncDatabase.Drops.Register(dropId, item, agent ? agent.gameObject : item.gameObject);
        ItemTool.AddNetDropTag(agent ? agent.gameObject : item.gameObject, dropId);

        var rpc = new ItemSpawnRpc
        {
            Token = message.Token,
            DropId = dropId,
            Position = message.Position,
            Direction = message.Direction,
            Angle = message.Angle,
            CreateRigidbody = message.CreateRigidbody,
            Snapshot = ItemTool.MakeSnapshot(item),
            PickupThrowable = message.PickupThrowable,
            Velocity = message.Velocity
        };

        CoopTool.SendRpc(in rpc);
    }

    public void Client_HandleSpawn(ItemSpawnRpc message)
    {
        if (NetService.Instance?.IsServer == true) return;
        ApplyPendingSnapshotReset();
        if (CoopSyncDatabase.Drops.TryGetById(message.DropId, out _)) return;

        var item = ItemTool.BuildItemFromSnapshot(message.Snapshot);
        if (item == null) return;

        DuckovItemAgent agent = null;
        try
        {
            InNetworkDrop = true;
            agent = message.PickupThrowable ? PickupThrowableSync.Spawn(item, message.Position, message.Velocity) :
                item.Drop(message.Position, message.CreateRigidbody, message.Direction, message.Angle);
        }
        finally
        {
            InNetworkDrop = false;
        }

        ItemTool.AddNetDropTag(agent ? agent.gameObject : item.gameObject, message.DropId);
        CoopSyncDatabase.Drops.Register(message.DropId, item, agent ? agent.gameObject : item.gameObject);
        _dropsCreatedSinceSnapshot.Add(message.DropId);
        _nextDropId = Math.Max(_nextDropId, message.DropId + 1);
    }

    public void Server_RegisterLocalDrop(Item item, DuckovItemAgent agent, Vector3 pos, bool createRigidbody, Vector3 dir, float angle) =>
        Server_RegisterDrop(item, agent, pos, createRigidbody, dir, angle, false, Vector3.zero);

    internal void Server_RegisterPickupThrowable(Item item, DuckovItemAgent agent, Vector3 position, Vector3 velocity) =>
        Server_RegisterDrop(item, agent, position, true, Vector3.zero, 0f, true, velocity);

    private void Server_RegisterDrop(Item item, DuckovItemAgent agent, Vector3 pos, bool createRigidbody, Vector3 dir, float angle,
        bool pickupThrowable, Vector3 velocity)
    {
        var service = NetService.Instance;
        if (service == null || !service.IsServer || !service.networkStarted || item == null) return;

        var dropGo = agent ? agent.gameObject : item.gameObject;

        // If this world item is already tracked as a network drop, don't allocate a new
        // drop id or broadcast an extra spawn. This can happen when host pickup fails
        // (e.g. backpack full) and gameplay code re-runs drop placement for the same item.
        if (CoopSyncDatabase.Drops.TryGetByItem(item, out var existingByItem) && existingByItem != null)
        {
            CoopSyncDatabase.Drops.Register(existingByItem.DropId, item, dropGo);
            ItemTool.AddNetDropTag(dropGo, existingByItem.DropId);
            return;
        }

        var existingTag = dropGo ? dropGo.GetComponent<NetDropTag>() : null;
        if (existingTag != null && existingTag.id != 0 &&
            CoopSyncDatabase.Drops.TryGetById(existingTag.id, out var existingById) && existingById != null)
        {
            CoopSyncDatabase.Drops.Register(existingById.DropId, item, dropGo);
            return;
        }

        if (ItemTool.HasQuestTag(item) || (agent && ItemTool.HasQuestTag(agent.gameObject)))
            return;

        var dropId = _nextDropId++;
        CoopSyncDatabase.Drops.Register(dropId, item, dropGo);
        ItemTool.AddNetDropTag(dropGo, dropId);

        var spawnRpc = new ItemSpawnRpc
        {
            DropId = dropId,
            Snapshot = ItemTool.MakeSnapshot(item),
            Position = pos,
            Direction = dir,
            Angle = angle,
            CreateRigidbody = createRigidbody,
            PickupThrowable = pickupThrowable,
            Velocity = velocity
        };

        CoopTool.SendRpc(in spawnRpc);
    }

    public void Server_HandlePickupRequest(RpcContext context, ItemPickupRequestRpc message)
    {
        if (!context.IsServer) return;

        if (!CoopSyncDatabase.Drops.TryGetById(message.DropId, out var entry) || entry == null)
            return;

        CoopSyncDatabase.Drops.Unregister(message.DropId);

        try
        {
            if (entry.Agent)
                UnityEngine.Object.Destroy(entry.Agent);
        }
        catch
        {
        }

        var rpc = new ItemDespawnRpc { DropId = message.DropId };
        CoopTool.SendRpc(in rpc);
    }

    public void Server_HandleLocalPickup(Item item)
    {
        if (item == null) return;
        var service = NetService.Instance;
        if (service == null || !service.IsServer || !service.networkStarted) return;

        if (!CoopSyncDatabase.Drops.TryGetByItem(item, out var entry) || entry == null)
            return;

        CoopSyncDatabase.Drops.Unregister(entry.DropId);

        _pendingPickups.Remove(entry.DropId);

        try
        {
            if (entry.Agent)
                UnityEngine.Object.Destroy(entry.Agent);
        }
        catch
        {
        }

        var rpc = new ItemDespawnRpc { DropId = entry.DropId };
        CoopTool.SendRpc(in rpc);
    }

    public void Client_HandleDespawn(ItemDespawnRpc message)
    {
        if (NetService.Instance?.IsServer == true) return;
        ApplyPendingSnapshotReset();
        // A snapshot entry may still be awaiting Unity instantiation. Do not let
        // it resurrect an item that another player has already picked up.
        _snapshotStream.Remove(message.DropId);
        if (!CoopSyncDatabase.Drops.TryGetById(message.DropId, out var entry) || entry == null)
            return;

        var isLocalPickup = _pendingPickups.Remove(message.DropId);

        try
        {
            if (!isLocalPickup)
            {
                if (entry.Agent)
                {
                    UnityEngine.Object.Destroy(entry.Agent);
                }
                else if (entry.Item)
                {
                    // If the item is already in an inventory (e.g., because this client
                    // picked it up locally), don't destroy it—just let the drop entry be
                    // cleared so the pickup persists in the player's backpack.
                    if (!entry.Item.InInventory)
                    {
                        entry.Item.DestroyTree();
                    }
                }
            }
        }
        catch
        {
        }

        CoopSyncDatabase.Drops.Unregister(message.DropId);
        _dropsCreatedSinceSnapshot.Remove(message.DropId);
    }

    public void Server_HandleDropSnapshotRequest(RpcContext context, ItemDropSnapshotRequestRpc message)
    {
        if (!context.IsServer || context.Sender == null) return;

        Server_SendDropSnapshotTo(context.Sender, message.Reset);
    }

    public void Server_BroadcastDropSnapshot()
    {
        var service = NetService.Instance;
        if (service == null || !service.IsServer || !service.networkStarted) return;

        var peers = service.netManager?.ConnectedPeerList;
        if (peers == null) return;

        foreach (var peer in peers)
        {
            if (peer == null) continue;
            Server_SendDropSnapshotTo(peer, true);
        }
    }

    public void Client_HandleDropSnapshotChunk(ItemDropSnapshotChunkRpc message)
    {
        if (NetService.Instance?.IsServer == true) return;
        _lastDropReceiveAt = SnapshotNow();
        _snapshotStream.Receive(message.Entries, message.Reset, message.IsLast);
    }

    private void ApplyPendingSnapshotReset()
    {
        if (_snapshotStream.ConsumeReset())
        {
            var keep = _dropsCreatedSinceSnapshot;
            var oldEntries = CoopSyncDatabase.Drops.Entries
                .Where(entry => entry != null && !keep.Contains(entry.DropId))
                .ToArray();

            foreach (var entry in oldEntries)
            {
                try
                {
                    if (entry.Agent)
                    {
                        UnityEngine.Object.Destroy(entry.Agent);
                    }
                    else if (entry.Item && !entry.Item.InInventory)
                    {
                        entry.Item.DestroyTree();
                    }

                    CoopSyncDatabase.Drops.Unregister(entry.DropId);
                }
                catch
                {
                }
            }

            // Keep live drops that arrived after this snapshot request was sent.
            // The snapshot was built before them, so clearing everything would
            // make a just-thrown item disappear.
            _nextDropId = keep.Count == 0 ? 1 : keep.Max() + 1;
            keep.Clear();
            _pendingPickups.Clear();
        }
    }

    private static int GetDropSceneBuildIndex(GameObject agent)
    {
        if (!agent) return -1;
        var core = MultiSceneCore.Instance;
        if (core)
        {
            for (var parent = agent.transform.parent; parent; parent = parent.parent)
            {
                if (parent.parent == core.transform)
                    return SceneInfoCollection.GetSceneInfo(parent.name)?.BuildIndex ?? agent.scene.buildIndex;
            }
        }
        return agent.scene.buildIndex;
    }

    public void Client_RequestDrop(Item item, Vector3 pos, bool createRigidbody, Vector3 dir, float angle, bool allowSlotOwned = false) =>
        Client_RequestDrop(item, pos, createRigidbody, dir, angle, allowSlotOwned, false, Vector3.zero);

    internal void Client_RequestPickupThrowable(Item item, Vector3 position, Vector3 velocity) =>
        Client_RequestDrop(item, position, true, Vector3.zero, 0f, true, true, velocity);

    private void Client_RequestDrop(Item item, Vector3 pos, bool createRigidbody, Vector3 dir, float angle, bool allowSlotOwned,
        bool pickupThrowable, Vector3 velocity)
    {
        var service = NetService.Instance;
        if (service == null || service.IsServer || !service.networkStarted || item == null) return;

        // Ignore requests that come from world items that aren't actually in the client's
        // inventory; repeatedly "dropping" those would just duplicate the same scene loot
        // on the host if the client keeps interacting while full.
        var inv = item.InInventory ?? item.Slots?.Master?.InInventory;
        var hasOwnership = inv != null || (allowSlotOwned && item.Slots != null);
        if (!hasOwnership && !pickupThrowable)
            return;

        if (!allowSlotOwned && !item.InInventory)
            return;

        var token = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        _pendingByToken[token] = token;

        var rpc = new ItemDropRequestRpc
        {
            Token = token,
            Position = pos,
            Direction = dir,
            Angle = angle,
            CreateRigidbody = createRigidbody,
            Snapshot = ItemTool.MakeSnapshot(item),
            PickupThrowable = pickupThrowable,
            Velocity = velocity
        };

        CoopTool.SendRpc(in rpc);

        try
        {
            item.Detach();
            item.DestroyTree();
        }
        catch
        {
        }
    }

    public void Client_RequestPickup(Item item)
    {
        var service = NetService.Instance;
        if (service == null || service.IsServer || !service.networkStarted || item == null) return;

        if (!CoopSyncDatabase.Drops.TryGetByItem(item, out var entry) || entry == null)
            return;

        _pendingPickups.Add(entry.DropId);

        var rpc = new ItemPickupRequestRpc { DropId = entry.DropId };
        CoopTool.SendRpc(in rpc);
    }

    private static DuckovItemAgent ResolveItemFromTag(NetDropTag tag)
    {
        if (tag == null) return null;

        var item = tag.GetComponent<DuckovItemAgent>() ?? tag.GetComponentInChildren<DuckovItemAgent>() ?? tag.GetComponentInParent<DuckovItemAgent>();
        return item;
    }
}
