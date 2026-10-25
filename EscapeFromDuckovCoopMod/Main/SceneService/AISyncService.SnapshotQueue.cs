using System.Diagnostics;

namespace EscapeFromDuckovCoopMod;

public sealed partial class AISyncService
{
    private readonly SnapshotWorkQueue<NetPeer, Queue<(int[] Ids, bool Reset)>> _serverSnapshotChunks = new();
    private readonly SnapshotMetadataCache<NetPeer> _serverSnapshotMetadata = new();
    private readonly Dictionary<NetPeer, (string Scene, float Time, float FullTime)> _serverSnapshotRequests = new();
    private static double SnapshotTime() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private IEnumerable<AISyncEntry> Server_SnapshotEntries() => CoopSyncDatabase.AI.Entries
        .OrderByDescending(entry => entry != null && HasNativeArea(entry.CharacterPresetKey));

    private void QueueSnapshotIds(NetPeer peer, int[] ids, bool reset)
    {
        if (!IsServer || peer == null) return;
        if (reset) _serverSnapshotMetadata.Remove(peer);
        if (!_serverSnapshotChunks.TryGet(peer, out var chunks))
        {
            chunks = new();
            _serverSnapshotChunks.Add(peer, chunks);
        }
        chunks.Enqueue((ids, reset));
    }

    private void Server_FlushSnapshotChunks()
    {
        _serverSnapshotChunks.Tick(
            peer => peer.ConnectionState == ConnectionState.Connected,
            peer => Service.netManager.GetReliableQueueLength(peer) >= 16,
            (peer, chunks) =>
            {
                if (chunks.Count == 0) return true;
                var chunk = chunks.Dequeue();
                var entries = new List<AISnapshotEntry>(chunk.Ids.Length);
                foreach (var id in chunk.Ids)
                    if (CoopSyncDatabase.AI.TryGet(id, out var entry)) entries.Add(BuildSnapshotEntry(entry));
                // Sample health/buffs/pose only when sending, after live health events.
                Server_SendSnapshotChunk(peer, entries, chunk.Reset);
                return chunks.Count == 0;
            }, SnapshotTime, 0.002);
    }
}
