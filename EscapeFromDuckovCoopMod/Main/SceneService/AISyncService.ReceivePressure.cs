using Duckov.Scenes;

namespace EscapeFromDuckovCoopMod;

public sealed partial class AISyncService
{
    private readonly PeerReceivePressure<NetPeer> _receivePressure = new();
    private readonly EntityRecoveryLimiter _clientEntityRecoveryLimiter = new();
    private readonly Dictionary<NetPeer, EntityRecoveryLimiter> _serverEntityRecoveryLimiters = new();
    private readonly SnapshotWorkQueue<NetPeer, Queue<int>> _serverEntityRecoveries = new();
    private readonly HashSet<NetPeer> _essentialSnapshots = new();
    private float _nextPressureReport;
    private int _entityRecoveryRequests;
    private readonly Dictionary<int, double> _clientMetadataReceivedAt = new();
    private int _visibleSamples;
    private int _fireMissingActors, _fireHiddenActors, _fireInactiveModels;
    private float _nextFireVisibilityLog;

    internal CharacterMainControl Client_ObserveFiringActor(int id, Vector3 position)
    {
        var actor = TryGetCharacter(id);
        var model = actor ? actor.characterModel : null;
        var hidden = actor && actor.Hidden;
        if (!actor)
        {
            _fireMissingActors++;
            if (IsClientWithinRange(position, DeactivationRadius))
            {
                Client_RequestEntityRecovery(id);
                if (CoopSyncDatabase.AI.TryGet(id, out var entry) && entry != null)
                    Client_EnsureReplica(entry);
            }
        }
        else if (hidden) _fireHiddenActors++;
        else if (!model || !model.gameObject.activeInHierarchy) _fireInactiveModels++;
        else return actor;

        if (Time.unscaledTime >= _nextFireVisibilityLog)
        {
            _nextFireVisibilityLog = Time.unscaledTime + 5f;
            CoopLogSystem.WriteNetworkDiagnostic($"[AI][FireVisibility] id={id} actor={(bool)actor} model={(bool)model} hidden={hidden} modelActive={(model && model.gameObject.activeInHierarchy)} spawning={_clientReplicaSpawning.Contains(id)}");
        }
        return actor;
    }
    private double _visibleTotalMs, _visibleMaxMs, _buildMaxMs;

    private void Client_TrackMetadata(int id)
    {
        if (!_clientReplicas.ContainsKey(id) && !_clientMetadataReceivedAt.ContainsKey(id))
            _clientMetadataReceivedAt[id] = SnapshotTime();
    }
    private void Client_RecordVisible(int id, double buildStart)
    {
        var now = SnapshotTime();
        var elapsed = (now - (_clientMetadataReceivedAt.TryGetValue(id, out var received) ? received : buildStart)) * 1000;
        _clientMetadataReceivedAt.Remove(id);
        _visibleSamples++; _visibleTotalMs += elapsed;
        _visibleMaxMs = Math.Max(_visibleMaxMs, elapsed);
        _buildMaxMs = Math.Max(_buildMaxMs, (now - buildStart) * 1000);
    }
    private void Client_LogQueueDiagnostics()
    {
        CoopLogSystem.WriteNetworkDiagnostic($"[AI][ClientQueue] snapshots={_pendingSnapshotQueue.Count} spawnQueue={_clientReplicaSpawnQueue.Count} spawning={_clientReplicaSpawning.Count} visibleSamples={_visibleSamples} receiveToVisibleAvgMs={(_visibleSamples == 0 ? 0 : _visibleTotalMs / _visibleSamples):0.0} receiveToVisibleMaxMs={_visibleMaxMs:0.0} buildMaxMs={_buildMaxMs:0.0} entityRequests={_entityRecoveryRequests} fireMissingActors={_fireMissingActors} fireHiddenActors={_fireHiddenActors} fireInactiveModels={_fireInactiveModels}");
        _visibleSamples = _entityRecoveryRequests = 0;
        _fireMissingActors = _fireHiddenActors = _fireInactiveModels = 0;
        _visibleTotalMs = _visibleMaxMs = _buildMaxMs = 0;
    }

    private void Client_ReportReceivePressure()
    {
        if (Time.unscaledTime < _nextPressureReport) return;
        _nextPressureReport = Time.unscaledTime + 1f;
        var snapshot = Service.netManager.GetReceiveQueueSnapshot();
        if (!snapshot.Supported) return;
        var message = new ReceivePressureRpc { SampleTime = Time.unscaledTimeAsDouble,
            Backlog = snapshot.Count, OldestWaitMs = (float)snapshot.OldestWaitMs };
        CoopTool.SendRpc(in message);
    }
    public void Server_HandleReceivePressure(RpcContext context, ReceivePressureRpc message)
    {
        if (!IsServer || context.Sender == null) return;
        var previous = _receivePressure.Tier(context.Sender, Time.unscaledTimeAsDouble);
        if (_receivePressure.Report(context.Sender, message.SampleTime, message.Backlog,
            message.OldestWaitMs, Time.unscaledTimeAsDouble))
        {
            var tier = _receivePressure.Tier(context.Sender, Time.unscaledTimeAsDouble);
            if (tier != previous)
                CoopLogSystem.WriteNetworkDiagnostic($"[AI][Pressure] peer={context.Sender.Id} tier={tier} backlog={message.Backlog} oldestMs={message.OldestWaitMs:0.0}");
        }
    }
    private bool Server_AllowPose(NetPeer peer, AISyncEntry entry, bool periodic)
    {
        if (!periodic || entry.IsVehicle) return true;
        if (!Service.playerStatuses.TryGetValue(peer, out var status) || status == null) return true;
        return _receivePressure.AllowPose(peer, entry.Id, (GetEntryAnchor(entry) - status.Position).sqrMagnitude,
            Time.unscaledTimeAsDouble, false);
    }
    private void Client_RequestEntityRecovery(int id)
    {
        if (IsServer || Service == null || !Service.networkStarted || !MultiSceneCore.MainScene.HasValue ||
            !_clientEntityRecoveryLimiter.TryAcquire(id, Time.unscaledTimeAsDouble)) return;
        var message = new AISnapshotRequestRpc { EntityId = id, SceneId = MultiSceneCore.MainSceneID };
        CoopTool.SendRpc(in message);
        _entityRecoveryRequests++;
    }
    private void Server_QueueEntityRecovery(NetPeer peer, AISnapshotRequestRpc message)
    {
        if (string.IsNullOrEmpty(message.SceneId) || !MultiSceneCore.MainScene.HasValue ||
            !string.Equals(message.SceneId, MultiSceneCore.MainSceneID, StringComparison.OrdinalIgnoreCase)) return;
        if (!CoopSyncDatabase.AI.TryGet(message.EntityId, out var entry) || entry == null) return;
        if (!IsPeerWithinRange(peer, GetEntryAnchor(entry), DeactivationRadius * 1.2f) && !HasNativeArea(entry.CharacterPresetKey)) return;
        if (!_serverEntityRecoveryLimiters.TryGetValue(peer, out var limiter))
            _serverEntityRecoveryLimiters[peer] = limiter = new();
        if (!limiter.TryAcquire(message.EntityId, Time.unscaledTimeAsDouble)) return;
        if (!_serverEntityRecoveries.TryGet(peer, out var ids))
        {
            ids = new(); _serverEntityRecoveries.Add(peer, ids);
        }
        if (ids.Count < 32 && !ids.Contains(message.EntityId)) ids.Enqueue(message.EntityId);
    }
    private void Server_FlushEntityRecoveries()
    {
        _serverEntityRecoveries.Tick(peer => peer.ConnectionState == ConnectionState.Connected,
            peer => Service.netManager.GetReliableQueueLength(peer) >= 16,
            (peer, ids) =>
            {
                if (ids.Count == 0) return true;
                var id = ids.Dequeue();
                if (CoopSyncDatabase.AI.TryGet(id, out var entry) && entry != null)
                {
                    Server_SendSnapshotChunk(peer, new List<AISnapshotEntry> { BuildSnapshotEntry(entry) }, false, true);
                    if (entry.Activated && entry.Status == AIStatus.Active &&
                        IsPeerWithinRange(peer, GetEntryAnchor(entry), DeactivationRadius))
                    {
                        if (!_serverWatchers.TryGetValue(id, out var watchers)) _serverWatchers[id] = watchers = new();
                        watchers.Add(peer);
                    }
                }
                return ids.Count == 0;
            }, SnapshotTime, 0.001);
    }
    private void ResetReceivePressure()
    {
        _fireMissingActors = _fireHiddenActors = _fireInactiveModels = 0; _nextFireVisibilityLog = 0;
        _clientMetadataReceivedAt.Clear(); _visibleSamples = 0; _visibleTotalMs = _visibleMaxMs = _buildMaxMs = 0;
        _receivePressure.Clear(); _clientEntityRecoveryLimiter.Clear(); _serverEntityRecoveryLimiters.Clear();
        _serverEntityRecoveries.Clear(); _essentialSnapshots.Clear(); _nextPressureReport = 0; _entityRecoveryRequests = 0;
    }
}
