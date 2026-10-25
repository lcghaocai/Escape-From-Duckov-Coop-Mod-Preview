using System.Diagnostics;

namespace EscapeFromDuckovCoopMod;

public partial class Weather
{
    private sealed class EnvironmentSnapshotWork
    {
        public bool Full;
        public string Scene;
        public double RequestedAt;
        public IEnumerator<int> Steps;
    }
    private readonly SnapshotWorkQueue<NetPeer, EnvironmentSnapshotWork> _environmentSnapshots = new();
    private string _lastRequestScene;
    private double _lastRequestAt = double.NegativeInfinity;
    private static double EnvironmentNow() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public void ResetSnapshots()
    {
        _environmentSnapshots.Clear();
        _lastRequestScene = null;
        _lastRequestAt = double.NegativeInfinity;
        _clientClockSynced = _clientWeatherSynced = _hasPendingWeather = false;
        _clientResyncTimer = _lootSyncTimer = 0;
        _pendingDoorStates.Clear();
    }

    private void QueueEnvironmentSnapshots()
    {
        foreach (var peer in Service.netManager.ConnectedPeerList)
            if (!_environmentSnapshots.Contains(peer))
                _environmentSnapshots.Add(peer, new EnvironmentSnapshotWork
                { Steps = SendLootSnapshotChunks(peer).GetEnumerator() });
    }

    private void FlushEnvironmentSnapshots()
    {
        _environmentSnapshots.Tick(
            peer => peer.ConnectionState == ConnectionState.Connected,
            peer => Service.netManager.GetReliableQueueLength(peer) >= 16,
            (peer, work) =>
            {
                if (work.Steps.MoveNext()) return false;
                work.Steps.Dispose();
                return true;
            }, EnvironmentNow, 0.002);
    }

    private IEnumerable<int> SendEnvironmentSnapshot(NetPeer peer)
    {
        SendClockState(peer, true);
        SendWeatherState(peer, true);
        yield return 0;
        foreach (var chunk in SendLootSnapshotChunks(peer)) yield return chunk;
        DeadLootBox.Instance?.Server_ReplayToPeer(peer);
        yield return 0;
        foreach (var chunk in SendDoorSnapshotChunks(peer)) yield return chunk;
        LevelDataBoolNet.Server_SendCostTakerSnapshot(peer);
        yield return 0;
        SendDestructibleSnapshot(peer);
        yield return 0;
        SendExplosiveBarrelSnapshot(peer);
        yield return 0;
    }

    private IEnumerable<int> SendLootSnapshotChunks(NetPeer peer)
    {
        var keys = CoopSyncDatabase.Loot.Entries
            .Where(e => e != null && e.PositionKey != 0 && !LootboxDetectUtil.IsPrivateInventory(e.Inventory))
            .Select(e => e.PositionKey).ToArray();
        var offset = 0;
        do
        {
            var chunkKeys = new List<int>(LootChunkSize);
            var states = new List<bool>(LootChunkSize);
            var end = Math.Min(offset + LootChunkSize, keys.Length);
            for (var i = offset; i < end; i++)
            {
                if (!CoopSyncDatabase.Loot.TryGetByPositionKey(keys[i], out var entry) || entry == null ||
                    LootboxDetectUtil.IsPrivateInventory(entry.Inventory)) continue;
                var go = entry.Loader ? entry.Loader.gameObject : entry.Lootbox ? entry.Lootbox.gameObject : null;
                if (!go) continue;
                chunkKeys.Add(keys[i]);
                states.Add(go.activeSelf); // Read current state, not a stale queued copy.
            }
            var rpc = new EnvLootChunkRpc
            { Reset = offset == 0, Keys = chunkKeys.ToArray(), States = states.ToArray() };
            CoopTool.SendRpcTo(peer, in rpc);
            offset = end;
            yield return 0;
        } while (offset < keys.Length);
    }

    private IEnumerable<int> SendDoorSnapshotChunks(NetPeer peer)
    {
        var registry = CoopSyncDatabase.Environment.Doors;
        var entries = registry.Entries.ToArray();
        for (var offset = 0; offset < entries.Length; offset += DoorChunkSize)
        {
            var keys = new List<int>(DoorChunkSize);
            var states = new List<bool>(DoorChunkSize);
            var end = Math.Min(offset + DoorChunkSize, entries.Length);
            for (var i = offset; i < end; i++)
            {
                var entry = entries[i];
                var door = entry?.Door;
                if (!door) continue;
                registry.Register(door);
                var key = entry.Key != 0 ? entry.Key : Door.TryGetDoorKey(door);
                if (key == 0) continue;
                keys.Add(key);
                states.Add(!door.IsOpen);
            }
            var rpc = new EnvDoorChunkRpc
            { Reset = offset == 0, Keys = keys.ToArray(), ClosedStates = states.ToArray() };
            CoopTool.SendRpcTo(peer, in rpc);
            yield return 0;
        }
    }
}
