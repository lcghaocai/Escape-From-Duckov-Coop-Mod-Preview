using System.Diagnostics;
using Duckov.Scenes;

namespace EscapeFromDuckovCoopMod;

public sealed partial class ItemNet
{
    private sealed class DropSnapshotWork
    {
        public uint[] Ids;
        public int Offset;
        public bool Reset;
        public string Scene;
        public double RequestedAt;
    }

    private readonly SnapshotWorkQueue<NetPeer, DropSnapshotWork> _serverDropSnapshots = new();
    private bool _serverDropRegistryBuilt;
    private NetPeer _clientDropRequestPeer;
    private string _clientDropRequestScene;
    private double _lastDropRequestAt = double.NegativeInfinity;
    private double _lastDropReceiveAt = double.NegativeInfinity;
    private static double SnapshotNow() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private void EnsureClientDropSnapshot()
    {
        var service = NetService.Instance;
        if (service == null || service.IsServer || !service.networkStarted || !LevelManager.LevelInited ||
            SceneLoader.IsSceneLoading || string.IsNullOrEmpty(SceneNet.Instance?._sceneReadySidSent)) return;
        var newScene = _clientDropRequestPeer != service.connectedPeer ||
                       _clientDropRequestScene != SceneNet.Instance._sceneReadySidSent;
        var noResponse = _lastDropReceiveAt < _lastDropRequestAt;
        if (newScene || (noResponse && SnapshotNow() - _lastDropRequestAt >= 3))
            Client_RequestDropSnapshot();
    }

    private void Server_SendDropSnapshotTo(NetPeer target, bool includeReset)
    {
        var service = NetService.Instance;
        if (service == null || !service.IsServer || target == null ||
            target.ConnectionState != ConnectionState.Connected) return;
        var scene = service.playerStatuses.TryGetValue(target, out var status) ? status.SceneId : null;
        var now = SnapshotNow();
        if (_serverDropSnapshots.TryGet(target, out var pending))
        {
            if (pending.Scene == scene && now - pending.RequestedAt < 1) return;
            // A later ready/request may follow a client-only map reload. Start a
            // fresh ordered reset, instead of sending only the old stream's tail.
            _serverDropSnapshots.Remove(target);
        }
        _serverDropSnapshots.Add(target, new DropSnapshotWork
        { Reset = includeReset, Scene = scene, RequestedAt = now });
    }

    private bool Server_SendNextDropChunk(NetPeer target, DropSnapshotWork work)
    {
        if (work.Ids == null)
        {
            if (!_serverDropRegistryBuilt) Server_RebuildDropRegistryFromScene();
            // Capture ids when this stream starts, after the host finishes loading.
            // Later chunks sample current items from the maintained registry.
            work.Ids = CoopSyncDatabase.Drops.Entries.Where(e => e != null).Select(e => e.DropId).ToArray();
            var begin = new ItemDropSnapshotChunkRpc
            {
                Version = 1, Reset = work.Reset, IsLast = work.Ids.Length == 0,
                Entries = Array.Empty<ItemDropSnapshotEntry>()
            };
            CoopTool.SendRpcTo(target, in begin);
            return work.Ids.Length == 0;
        }
        if (work.Offset >= work.Ids.Length) return true;
        const int chunkSize = 4;
        var entries = new List<ItemDropSnapshotEntry>(chunkSize);
        // Bound scanned ids as well as instantiated snapshots when many items
        // were picked up while this peer was congested.
        var end = Math.Min(work.Offset + chunkSize, work.Ids.Length);
        while (work.Offset < end)
        {
            var id = work.Ids[work.Offset++];
            if (!CoopSyncDatabase.Drops.TryGetById(id, out var entry) || entry == null || !entry.Item ||
                entry.Item.InInventory || ItemTool.HasQuestTag(entry.Item)) continue;
            var throwable = entry.Agent && entry.Agent.GetComponentInParent<PickupThrowableTag>();
            entries.Add(new ItemDropSnapshotEntry
            {
                DropId = id,
                Position = entry.Agent ? entry.Agent.transform.position : entry.Item.transform.position,
                Direction = Vector3.zero,
                Angle = 0f,
                CreateRigidbody = false,
                PickupThrowable = throwable,
                Velocity = throwable ? entry.Agent.GetComponentInParent<Rigidbody>()?.velocity ?? Vector3.zero : Vector3.zero,
                SceneBuildIndex = GetDropSceneBuildIndex(entry.Agent),
                Snapshot = ItemTool.MakeSnapshot(entry.Item)
            });
        }
        var last = work.Offset >= work.Ids.Length;
        if (entries.Count > 0 || last)
        {
            var chunk = new ItemDropSnapshotChunkRpc
            { Version = 1, IsLast = last, Entries = entries.ToArray() };
            CoopTool.SendRpcTo(target, in chunk);
        }
        return last;
    }
}
