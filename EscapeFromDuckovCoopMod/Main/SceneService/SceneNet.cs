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

using Duckov.UI;
using Duckov.Scenes;
using EscapeFromDuckovCoopMod.Utils;
using System;

namespace EscapeFromDuckovCoopMod;

public class SceneNet : MonoBehaviour
{
    public static SceneNet Instance;
    public string _sceneReadySidSent;
    private string _sceneReadyPendingSid;
    private float _sceneReadyEarliestTime;
    private const float SceneReadySettleSeconds = 0.75f;
    public bool sceneVoteActive;
    public string sceneTargetId; // 统一的目标 SceneID
    public string sceneCurtainGuid; // 过场 GUID，可为空
    public bool sceneNotifyEvac;
    public bool sceneSaveToFile = true;

    public bool allowLocalSceneLoad;
    private readonly LocalSceneLoadPermit _localLoadPermit = new();
    private int _clientGateGeneration;

    public bool sceneUseLocation;
    public string sceneLocationName;

    public bool localReady;

    //Scene Gate 等待进入地图系统 Wait join Map
    public volatile bool _cliSceneGateReleased;
    public string _cliGateSid;
    public string _srvGateSid;
    public bool IsMapSelectionEntry;
    private string _teleporterToken;
    private int _teleporterBeaconIndex = -1;

    public bool IsDoteleportMap; //附加地图投票判断

    private bool _localSceneLoadLaunched;
    private bool _srvPendingSceneLoad;
    private float _srvPendingSceneLoadTime;
    private bool _srvPendingBeginLoad;
    private float _srvPendingBeginLoadTime;
    private bool _srvLoadInProgress;

    public readonly Dictionary<string, string> _cliLastSceneIdByPlayer = new();

    // 记录已经“举手”的客户端（用 EndPoint 字符串，与现有 PlayerStatus 保持一致）
    public readonly HashSet<string> _srvGateReadyPids = new();
    // 每个客户端举手时请求放行的场景 ID，放行时原样回显，避免多场景/子场景命名不一致导致客机一直等到超时。
    private readonly Dictionary<string, string> _srvGateReadySids = new(StringComparer.OrdinalIgnoreCase);

    // 客机等待主机放行的最长时间。主机切图时的资源卸载可能达到 20 秒以上，10 秒会提前进门。
    private const float ClientGateTimeoutSeconds = 30f;
    private const float ClientGateRetryInterval = 0.5f;

    // 所有端都使用主机广播的这份参与者 pid 列表（关键：统一 pid）
    public readonly List<string> sceneParticipantIds = new();

    // 就绪表（key = 上面那个 pid）
    public readonly Dictionary<string, bool> sceneReady = new();
    private float _cliGateDeadline;

    private bool _cliForceSpawnAtHost;
    private bool _cliHostReadyReceived;
    private bool _cliPendingPostLoadSnap;
    private float _cliForceSpawnDeadline;
    private Vector3 _cliHostSpawnPosition;
    private Quaternion _cliHostSpawnRotation;
    private string _cliHostSpawnSceneId;
    private bool _cliSpawnTransferPending;
    private int _cliSpawnGeneration;

    private bool _srvSceneGateOpen;
    private NetService Service => NetService.Instance;

    private bool IsServer => Service != null && Service.IsServer;
    private NetManager netManager => Service?.netManager;
    private NetDataWriter writer => Service?.writer;
    private NetPeer connectedPeer => Service?.connectedPeer;
    private PlayerStatus localPlayerStatus => Service?.localPlayerStatus;
    private bool networkStarted => Service != null && Service.networkStarted;
    private int port => Service?.port ?? 0;
    private Dictionary<NetPeer, GameObject> remoteCharacters => Service?.remoteCharacters;
    private Dictionary<NetPeer, PlayerStatus> playerStatuses => Service?.playerStatuses;
    private Dictionary<string, GameObject> clientRemoteCharacters => Service?.clientRemoteCharacters;

    public void Init()
    {
        Instance = this;
    }

    public void AuthorizeLocalSceneLoad(string sceneId, float seconds = 10f, bool throughSettlement = false)
    {
        allowLocalSceneLoad = true;
        _localLoadPermit.Grant(sceneId, Time.unscaledTime, seconds, throughSettlement);
    }

    public bool IsLocalSceneLoadAllowed(string sceneId = null)
    {
        return _localLoadPermit.Allows(sceneId, Time.unscaledTime);
    }

    private void ClearLocalSceneLoadAuthorization()
    {
        allowLocalSceneLoad = false;
        _localLoadPermit.Clear();
    }

    public void TrySendSceneReadyOnce()
    {
        if (!networkStarted) return;
        if (!IsServer && (connectedPeer == null || connectedPeer.ConnectionState != ConnectionState.Connected)) return;

        // 只有真正进入地图（拿到 SceneId）才上报
        if (!LocalPlayerManager.Instance.ComputeIsInGame(out var sid) || string.IsNullOrEmpty(sid)) return;
        if (_sceneReadySidSent == sid) return; // 去抖：本场景只发一次

        // 不要只等 MainCharacter 出现：InitLevel 还没跑完时角色可能仍在旧位置/默认位置，
        // 这时发 SCENE_READY 会让主机用旧坐标创建远端克隆，表现为“生成在地图外”。
        var lm = LevelManager.Instance;
        // LevelInited precedes the native final SetPosition by an async delay.
        if (lm == null || !LevelManager.AfterInit || lm.MainCharacter == null ||
            SceneLoader.IsSceneLoading || MultiSceneCore.Instance?.IsLoading == true) return;
        if (!IsServer && _cliPendingPostLoadSnap && _cliHostReadyReceived)
        {
            if (!Client_TrySnapToHostAnchor(true)) return;
            _cliForceSpawnAtHost = false;
        }

        if (!IsServer && _cliForceSpawnAtHost && sceneLocationName == "OnPointerClick")
        {
            if (!_cliHostReadyReceived)
            {
                if (_cliForceSpawnDeadline <= 0f)
                    _cliForceSpawnDeadline = Time.unscaledTime + ClientGateTimeoutSeconds;
                // 主机场景 ID 一直对不上/SCENE_READY 丢失时不能永久卡住：
                // 超时后退回本地出生点，至少先让角色正常上报。
                if (Time.unscaledTime < _cliForceSpawnDeadline)
                    return;
                _cliForceSpawnAtHost = false;
                CoopLogSystem.WriteNetworkDiagnostic(
                    $"[SCENE] SpawnTogether fallback: host ready not received for target='{sceneTargetId}'");
            }
            else if (!Client_TrySnapToHostAnchor())
            {
                return; // 角色未生成，延迟到下次再尝试
            }
        }

        // 等出生点稳定一小段时间（只对当前场景计一次），避免角色刚 InitLevel 完还在旧坐标时上报。
        if (!string.Equals(_sceneReadyPendingSid, sid, StringComparison.OrdinalIgnoreCase))
        {
            _sceneReadyPendingSid = sid;
            _sceneReadyEarliestTime = Time.unscaledTime + SceneReadySettleSeconds;
        }
        if (Time.unscaledTime < _sceneReadyEarliestTime) return;

        // 先把当前真实位置发给主机，再发 SCENE_READY；主机收到后创建的远端克隆就不会落在旧位置。
        if (!IsServer) SendLocalPlayerStatus.Instance?.SendPositionUpdate();

        var pos = lm.MainCharacter.transform.position;
        var rot = lm.MainCharacter.modelRoot.transform.rotation;
        var faceJson = CustomFace.LoadLocalCustomFaceJson() ?? string.Empty;

        writer.Reset();
        writer.Put((byte)Op.SCENE_READY); // 你的枚举里已有 23 = SCENE_READY
        writer.Put(localPlayerStatus?.EndPoint ?? (IsServer ? $"Host:{port}" : "Client:Unknown"));
        writer.Put(sid);
        writer.PutVector3(pos);
        writer.PutQuaternion(rot);
        writer.Put(faceJson);
        // Active sub-scene names need not share a prefix with the load target.
        writer.Put(MultiSceneCore.MainSceneID ?? string.Empty);

        if (IsServer)
            // 主机广播（本机也等同已就绪，方便让新进来的客户端看到主机）
            netManager?.SendToAll(writer, DeliveryMethod.ReliableOrdered);
        else
            connectedPeer?.Send(writer, DeliveryMethod.ReliableOrdered);

        if (!IsServer && connectedPeer.ConnectionState != ConnectionState.Connected) return;
        _sceneReadySidSent = sid;
        _sceneReadyPendingSid = null;
        _sceneReadyEarliestTime = 0f;
        ClearLocalSceneLoadAuthorization();
    }

    private void Server_BroadcastBeginSceneLoad()
    {
        var returningFromDeath = Spectator.Instance.IsLocalDeathPending;
        if (returningFromDeath)
        {
            Spectator.Instance._spectatorEndOnVotePending = false;
            Spectator.Instance.EndSpectatorAndShowClosure(true);
        }

        var message = new SceneBeginLoadRpc
        {
            SceneId = sceneTargetId ?? string.Empty,
            CurtainGuid = sceneCurtainGuid,
            NotifyEvac = sceneNotifyEvac,
            SaveToFile = sceneSaveToFile,
            UseLocation = sceneUseLocation,
            LocationName = sceneLocationName ?? string.Empty,
            TeleporterToken = _teleporterToken,
            BeaconIndex = _teleporterBeaconIndex
        };

        if (sceneLocationName == "OnPointerClick" && CoopAISettings.ActiveGeneral.TeleporterSpawnTogether)
        {
            message.ForceSpawnAtHost = true;
            var hostChar = LevelManager.Instance && LevelManager.Instance.MainCharacter
                ? LevelManager.Instance.MainCharacter
                : CharacterMainControl.Main;
            if (hostChar != null)
            {
                message.HostSpawnPosition = hostChar.transform.position;
                message.HostSpawnRotation = hostChar.modelRoot.transform.rotation;
            }
        }

        if (!string.IsNullOrEmpty(_teleporterToken) && !TeleporterTravel.AcceptCommit(_teleporterToken)) return;
        CoopTool.SendRpc(in message);

        _localSceneLoadLaunched = false;

        if (!returningFromDeath)
            Server_ScheduleSceneLoad(0.5f);

        // 收尾与清理
        sceneVoteActive = false;
        sceneParticipantIds.Clear();
        sceneReady.Clear();
        localReady = false;
    }

    private void Server_ScheduleBeginSceneLoad(float delaySeconds)
    {
        _srvPendingBeginLoad = true;
        _srvPendingBeginLoadTime = Time.unscaledTime + Mathf.Max(0f, delaySeconds);
    }

    public void Server_TryExecutePendingBeginLoad()
    {
        if (!IsServer || !_srvPendingBeginLoad)
            return;

        if (Time.unscaledTime < _srvPendingBeginLoadTime)
            return;

        _srvPendingBeginLoad = false;
        Server_BroadcastBeginSceneLoad();
    }

    private void Host_PerformSceneLoad()
    {
        _srvLoadInProgress = true;
        _srvSceneGateOpen = false;
        _srvGateReadyPids.Clear();
        _srvGateReadySids.Clear();
        _srvGateSid = sceneTargetId;
        // 主机本地执行加载
        AuthorizeLocalSceneLoad(sceneTargetId);
        var isTeleporterLoad = string.Equals(sceneLocationName, "DoTeleport", StringComparison.Ordinal);
        var isMapSelectionLoad = string.Equals(sceneLocationName, "OnPointerClick", StringComparison.Ordinal);
        var map = TeleporterCost.Resolve(sceneTargetId, _teleporterBeaconIndex);
        if (map != null && (IsMapSelectionEntry || isMapSelectionLoad))
        {
            IsMapSelectionEntry = false;
            LevelManager.loadLevelBeaconIndex = map.BeaconIndex;
        }
        if (isTeleporterLoad)
        {
            IsDoteleportMap = false;
            _localSceneLoadLaunched = CoopTool.GoTeleport(sceneTargetId, sceneCurtainGuid);
        }
        else
        {
            TryPerformSceneLoad_Local(sceneTargetId, sceneCurtainGuid, sceneNotifyEvac, sceneSaveToFile, sceneUseLocation, sceneLocationName);
        }

        // 若首轮加载触发失败，短暂延后再补尝试，避免主机长时间卡在加载界面。
        // 附加场景传送必须只重试 MultiSceneTeleporter.DoTeleport，不能再走主场景加载兜底。
        if (isTeleporterLoad)
            FallbackRetryTeleport(sceneTargetId, sceneCurtainGuid).Forget();
        else
            FallbackRetryLocalSceneLoad(sceneTargetId, sceneCurtainGuid, sceneNotifyEvac, sceneSaveToFile, sceneUseLocation, sceneLocationName).Forget();
    }

    public bool IsServerLoadInProgress()
    {
        return _srvLoadInProgress;
    }

    public void Server_ClearLoadInProgress()
    {
        _srvLoadInProgress = false;
    }

    private void Server_ScheduleSceneLoad(float delaySeconds)
    {
        _srvPendingSceneLoad = true;
        _srvPendingSceneLoadTime = Time.unscaledTime + Mathf.Max(0f, delaySeconds);
    }

    public void Server_TryExecutePendingLoad()
    {
        if (!IsServer || !_srvPendingSceneLoad)
            return;

        if (Time.unscaledTime < _srvPendingSceneLoadTime)
            return;

        _srvPendingSceneLoad = false;
        Host_PerformSceneLoad();
    }

    // ===== 主机：有人（或主机自己）切换准备 =====
    public void Server_OnSceneReadySet(NetPeer fromPeer, bool ready)
    {
        if (!IsServer) return;

        var service = NetService.Instance;
        if (service == null) return;

        // 统一 pid（fromPeer==null 代表主机自己）
        var pid = fromPeer != null ? service.GetPlayerId(fromPeer) : service.GetPlayerId(null);
        pid ??= string.Empty;

        if (!sceneVoteActive) return;
        if (!sceneReady.ContainsKey(pid)) return; // 不在这轮投票里，丢弃
        if (!ready && TeleporterTravel.WithdrawReady(pid)) return;

        sceneReady[pid] = ready;

        var broadcast = new SceneReadySetRpc
        {
            PlayerId = pid,
            IsReady = ready
        };

        CoopTool.SendRpc(in broadcast);

        // 检查是否全员准备
        foreach (var id in sceneParticipantIds)
            if (!sceneReady.TryGetValue(id, out var r) || !r)
                return;

        if (sceneLocationName == "OnPointerClick") TeleporterTravel.VotesReady();
        else Server_ScheduleBeginSceneLoad(0.2f);
    }

    // ===== 客户端：收到“投票开始”（带参与者 pid 列表）=====
    public void Client_OnSceneVoteStart(SceneVoteStartRpc message)
    {
        var ver = message.Version;
        if (ver != 1 && ver != SceneVoteStartRpc.CurrentVersion)
        {
            Debug.LogWarning($"[SCENE] vote: unsupported ver={ver}");
            return;
        }

        var targetId = message.TargetSceneId ?? string.Empty;
        if (string.IsNullOrEmpty(targetId))
        {
            Debug.LogWarning("[SCENE] vote: bad sceneId");
            return;
        }

        var participants = message.ParticipantIds ?? Array.Empty<string>();
        if (participants.Length > 256)
        {
            Debug.LogWarning("[SCENE] vote: weird count");
            return;
        }

        sceneTargetId = targetId;
        sceneCurtainGuid = string.IsNullOrEmpty(message.CurtainGuid) ? null : message.CurtainGuid;
        sceneUseLocation = message.UseLocation;
        sceneNotifyEvac = message.NotifyEvac;
        sceneSaveToFile = message.SaveToFile;
        sceneLocationName = message.LocationName ?? string.Empty;

        var hostSceneId = ver >= 2 ? message.HostSceneId ?? string.Empty : string.Empty;

        string mySceneId = null;
        LocalPlayerManager.Instance.ComputeIsInGame(out mySceneId);
        mySceneId = mySceneId ?? string.Empty;

        if (sceneLocationName != "OnPointerClick" && ver >= 2 && !string.IsNullOrEmpty(hostSceneId) && !string.IsNullOrEmpty(mySceneId))
            if (!SceneIdsMatch(hostSceneId, mySceneId))
            {
                Debug.Log($"[SCENE] vote: ignore (diff scene) host='{hostSceneId}' me='{mySceneId}'");
                return;
            }

        var service = Service;
        var myId = localPlayerStatus != null ? localPlayerStatus.EndPoint ?? string.Empty : string.Empty;
        var foundSelf = false;

        sceneParticipantIds.Clear();
        for (var i = 0; i < participants.Length; i++)
        {
            var pid = participants[i] ?? string.Empty;
            sceneParticipantIds.Add(pid);

            if (foundSelf) continue;
            if (!string.IsNullOrEmpty(myId) && string.Equals(pid, myId, StringComparison.Ordinal))
            {
                foundSelf = true;
            }
            else if (service != null && service.IsSelfId(pid))
            {
                myId = pid;
                foundSelf = true;
            }
        }

        if (participants.Length > 0 && !foundSelf)
        {
            if (string.IsNullOrEmpty(myId) && service != null)
                myId = service.localPlayerStatus != null ? service.localPlayerStatus.EndPoint ?? string.Empty : string.Empty;

            Debug.LogWarning($"[SCENE] vote: local id '{myId}' not present in participant list from host; showing UI anyway");
        }

        sceneReady.Clear();
        foreach (var pid in sceneParticipantIds)
            sceneReady[pid] = false;

        sceneVoteActive = true;
        localReady = false;
        _teleporterToken = message.TeleporterToken;
        _teleporterBeaconIndex = message.BeaconIndex;
        if (sceneLocationName == "OnPointerClick" && !string.IsNullOrEmpty(_teleporterToken))
            TeleporterTravel.BeginLocal(_teleporterToken, sceneTargetId, _teleporterBeaconIndex);

        Debug.Log($"[SCENE] 收到投票 v{ver}: target='{sceneTargetId}', hostScene='{hostSceneId}', myScene='{mySceneId}', players={sceneParticipantIds.Count}");
    }

    public void Client_OnBeginSceneLoad(SceneBeginLoadRpc message)
    {
        if (message.LocationName == "OnPointerClick" && !TeleporterTravel.AcceptCommit(message.TeleporterToken)) return;
        _teleporterBeaconIndex = message.BeaconIndex;
        sceneTargetId = message.SceneId ?? sceneTargetId ?? string.Empty;
        sceneCurtainGuid = string.IsNullOrEmpty(message.CurtainGuid) ? null : message.CurtainGuid;
        sceneNotifyEvac = message.NotifyEvac;
        sceneSaveToFile = message.SaveToFile;
        sceneUseLocation = message.UseLocation;
        sceneLocationName = message.LocationName ?? string.Empty;

        _cliSpawnGeneration++;
        _cliSpawnTransferPending = false;
        _cliHostSpawnSceneId = null;
        _cliForceSpawnAtHost = message.ForceSpawnAtHost && sceneLocationName == "OnPointerClick";
        // Start the timeout only once this client has initialized the destination.
        _cliForceSpawnDeadline = 0f;
        _cliHostReadyReceived = false;
        _cliPendingPostLoadSnap = _cliForceSpawnAtHost;
        _cliHostSpawnPosition = message.HostSpawnPosition;
        _cliHostSpawnRotation = message.HostSpawnRotation;

        AuthorizeLocalSceneLoad(sceneTargetId);

        var map = TeleporterCost.Resolve(sceneTargetId, _teleporterBeaconIndex);
        if (map != null && sceneLocationName == "OnPointerClick")
        {
            IsMapSelectionEntry = false;
            LevelManager.loadLevelBeaconIndex = map.BeaconIndex;
        }
        if (sceneLocationName == "DoTeleport")
        {
            IsDoteleportMap = false;
            CoopTool.GoTeleport(sceneTargetId, sceneCurtainGuid);
        }
        else
        {
            TryPerformSceneLoad_Local(sceneTargetId, sceneCurtainGuid, sceneNotifyEvac, sceneSaveToFile, sceneUseLocation, sceneLocationName);
        }

        sceneVoteActive = false;
        sceneParticipantIds.Clear();
        sceneReady.Clear();
        localReady = false;
    }

    public void Client_OnRemoteSceneReady(string playerId, string sceneId, Vector3 pos, Quaternion rot, string mainSceneId = null)
    {
        if (IsBaseScene(sceneId)) return;
        if (!SceneIdsMatch(sceneTargetId, sceneId) && !SceneIdsMatch(sceneTargetId, mainSceneId))
        {
            CoopLogSystem.WriteNetworkDiagnostic(
                $"[SCENE] SpawnTogether ignored host ready: target='{sceneTargetId}' host='{sceneId}' main='{mainSceneId}'");
            return;
        }

        var fromHost = !string.IsNullOrEmpty(playerId) &&
                       playerId.StartsWith("Host:", StringComparison.OrdinalIgnoreCase);

        // 传送台一起出生：保持原有强制逻辑。
        if (_cliForceSpawnAtHost)
        {
            if (sceneLocationName != "OnPointerClick") return;
            if (_cliHostReadyReceived) return;
        }
        else if (!fromHost)
        {
            return;
        }

        _cliHostReadyReceived = true;
        _cliHostSpawnSceneId = sceneId;
        _cliHostSpawnPosition = pos;
        _cliHostSpawnRotation = rot;
        _cliPendingPostLoadSnap = true;

        // 主机已经在图里时，直接用主机位置校正本地角色；
        // 角色还没生成则等 PostLoad 再校正，避免生成在地图外。
        if (!SceneLoader.IsSceneLoading && LevelManager.LevelInited)
            Client_TrySnapToHostAnchor(true);

        CoopLogSystem.WriteNetworkDiagnostic(
            $"[SCENE] SpawnTogether host ready: target='{sceneTargetId}' host='{sceneId}' main='{mainSceneId}' pos={pos.x:0.0},{pos.y:0.0},{pos.z:0.0}");
    }

    internal static bool SceneIdsMatch(string targetSceneId, string hostSceneId)
    {
        if (string.IsNullOrEmpty(targetSceneId) || string.IsNullOrEmpty(hostSceneId)) return false;
        if (string.Equals(targetSceneId, hostSceneId, StringComparison.OrdinalIgnoreCase)) return true;
        // Base_SceneV2 和 Base 都算基地；Level_X 和 Level_X_Main 也算同一张图。
        if (IsBaseScene(targetSceneId) && IsBaseScene(hostSceneId)) return true;
        return targetSceneId.StartsWith(hostSceneId, StringComparison.OrdinalIgnoreCase) ||
               hostSceneId.StartsWith(targetSceneId, StringComparison.OrdinalIgnoreCase);
    }

    private bool Client_TrySnapToHostAnchor(bool markPostLoadApplied = false)
    {
        // Cache early host READY messages; never teleport the character in the old map.
        if (!LevelManager.AfterInit || SceneLoader.IsSceneLoading ||
            MultiSceneCore.Instance == null || MultiSceneCore.Instance.IsLoading ||
            (!SceneIdsMatch(sceneTargetId, MultiSceneCore.MainSceneID) &&
             !SceneIdsMatch(sceneTargetId, MultiSceneCore.ActiveSubSceneID))) return false;
        var main = CharacterMainControl.Main;
        var modelRoot = main != null ? main.modelRoot : null;

        if (main == null || modelRoot == null)
            return false;

        if (!string.IsNullOrEmpty(_cliHostSpawnSceneId) &&
            !string.Equals(_cliHostSpawnSceneId, MultiSceneCore.ActiveSubSceneID, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_cliHostSpawnSceneId, MultiSceneCore.MainSceneID, StringComparison.OrdinalIgnoreCase))
        {
            if (!_cliSpawnTransferPending)
            {
                _cliSpawnTransferPending = true;
                Client_LoadHostSpawnScene(_cliSpawnGeneration, LevelManager.Instance,
                    _cliHostSpawnSceneId, _cliHostSpawnPosition).Forget();
            }
            return false;
        }

        // ECM2 owns the character position/velocity. A Transform write alone is insufficient.
        main.SetPosition(_cliHostSpawnPosition);
        modelRoot.transform.rotation = _cliHostSpawnRotation;

        if (markPostLoadApplied)
        {
            _cliPendingPostLoadSnap = false;
            CoopLogSystem.WriteNetworkDiagnostic($"[SCENE] SpawnTogether applied scene='{MultiSceneCore.ActiveSubSceneID}' pos={main.transform.position}");
        }
        return true;
    }

    private async UniTaskVoid Client_LoadHostSpawnScene(int generation, LevelManager level, string sceneId, Vector3 position)
    {
        try
        {
            AuthorizeLocalSceneLoad(sceneId);
            CoopLogSystem.WriteNetworkDiagnostic($"[SCENE] SpawnTogether loading host sub-scene='{sceneId}'");
            var loaded = await MultiSceneCore.Instance.LoadAndTeleport(sceneId, position, false);
            if (generation != _cliSpawnGeneration || !networkStarted || LevelManager.Instance != level) return;
            if (!loaded) throw new InvalidOperationException("Unable to load host spawn sub-scene " + sceneId);
            // Complete through TrySendSceneReadyOnce after the native scene operation settles.
            _sceneReadySidSent = null;
        }
        catch (Exception ex)
        {
            if (generation != _cliSpawnGeneration || LevelManager.Instance != level) return;
            // Leave the native local spawn intact; never put world coordinates into an unloaded sub-scene.
            _cliPendingPostLoadSnap = false;
            _cliForceSpawnAtHost = false;
            CoopLogSystem.WriteNetworkDiagnostic($"[SCENE] SpawnTogether sub-scene failed: {ex.Message}");
        }
        finally
        {
            if (generation == _cliSpawnGeneration) _cliSpawnTransferPending = false;
        }
    }

    public void Client_OnLevelInitialized_PostLoad()
    {
        if (!_cliForceSpawnAtHost && !_cliPendingPostLoadSnap)
            return;

        if (!_cliHostReadyReceived)
            return;

        if (_cliForceSpawnAtHost && sceneLocationName != "OnPointerClick")
            return;

        if (_cliPendingPostLoadSnap && Client_TrySnapToHostAnchor(true))
            _cliForceSpawnAtHost = false;
    }

    public void StartValidatedTeleporterLoad()
    {
        if (IsServer && !string.IsNullOrEmpty(_teleporterToken)) Server_ScheduleBeginSceneLoad(0.2f);
    }

    public void ResetTeleporterVote()
    {
        _cliSpawnGeneration++;
        _cliSpawnTransferPending = false;
        _cliHostReadyReceived = false;
        _cliPendingPostLoadSnap = false;
        _cliForceSpawnAtHost = false;
        _srvPendingBeginLoad = false;
        _srvPendingSceneLoad = false;
        _localSceneLoadLaunched = false;
        // 上一次加载失败/中途退出时，这几个状态没有对应场景加载完成事件来清理，
        // 会永久卡住 Host_BeginSceneVote_Simple，表现为之后点地图完全没反应。
        _srvLoadInProgress = false;
        _srvSceneGateOpen = false;
        _srvGateReadyPids.Clear();
        _srvGateReadySids.Clear();
        _srvGateSid = null;
        _sceneReadyPendingSid = null;
        _sceneReadyEarliestTime = 0f;
        _cliForceSpawnDeadline = 0f;
        sceneVoteActive = false;
        sceneParticipantIds.Clear();
        sceneReady.Clear();
        localReady = false;
        IsMapSelectionEntry = false;
        _teleporterToken = null;
        _teleporterBeaconIndex = -1;
        ClearLocalSceneLoadAuthorization();
        EscapeFromDuckovCoopMod.Utils.SceneTriggerResetter.ResetAllSceneTriggers();
    }

    public void Client_SendReadySet(bool ready)
    {
        if (IsServer || connectedPeer == null) return;

        var message = new SceneReadySetRpc
        {
            PlayerId = localPlayerStatus != null ? localPlayerStatus.EndPoint : string.Empty,
            IsReady = ready
        };

        CoopTool.SendRpc(in message);

        // ★ 本地乐观更新：立即把自己的 ready 写进就绪表，以免 UI 卡在"未准备"
        if (sceneVoteActive && localPlayerStatus != null)
        {
            var me = localPlayerStatus.EndPoint ?? string.Empty;
            if (!string.IsNullOrEmpty(me) && sceneReady.ContainsKey(me))
                sceneReady[me] = ready;
        }
    }

    /// <summary>
    /// 取消当前投票（房主或投票发起者可调用）
    /// </summary>
    public void CancelVote()
    {
        if (TeleporterTravel.IsActive) { TeleporterTravel.CancelLocal("ui.teleporter.reason.cancelled"); return; }
        if (!sceneVoteActive)
        {
            Debug.LogWarning("[SCENE] 没有正在进行的投票");
            return;
        }

        Debug.Log("[SCENE] 取消投票，重置场景触发器");
        _srvPendingBeginLoad = false;

        // 如果是服务器，广播取消投票消息给所有客户端
        if (IsServer && networkStarted && netManager != null)
        {
            var message = new SceneVoteCancelRpc();
            CoopTool.SendRpc(in message);
            Debug.Log("[SCENE] 服务器已广播取消投票消息");
        }

        // 清除投票状态
        sceneVoteActive = false;
        sceneParticipantIds.Clear();
        sceneReady.Clear();
        localReady = false;
        IsMapSelectionEntry = false;
        IsDoteleportMap = false;
        // 重置场景触发器，允许重新触发投票
        EscapeFromDuckovCoopMod.Utils.SceneTriggerResetter.ResetAllSceneTriggers();
    }

    /// <summary>
    /// 客户端接收到服务器的取消投票消息
    /// </summary>
    public void Client_OnVoteCancelled()
    {
        if (TeleporterTravel.IsActive) TeleporterTravel.Reset();
        if (IsServer)
        {
            Debug.LogWarning("[SCENE] 服务器不应该接收客户端的取消投票消息");
            return;
        }

        Debug.Log("[SCENE] 收到服务器取消投票通知，重置本地状态");

        // 清除投票状态
        sceneVoteActive = false;
        sceneParticipantIds.Clear();
        sceneReady.Clear();
        localReady = false;

        // 重置场景触发器，允许重新触发投票
        EscapeFromDuckovCoopMod.Utils.SceneTriggerResetter.ResetAllSceneTriggers();
    }

    private void TryPerformSceneLoad_Local(string targetSceneId, string curtainGuid,
        bool notifyEvac, bool save,
        bool useLocation, string locationName)
    {
        try
        {
            var loader = SceneLoader.Instance;
            var launched = false; // 是否已触发加载
            AuthorizeLocalSceneLoad(targetSceneId);

            // （如果后面你把 loader.LoadScene 恢复了，这里可以先试 loader 路径并把 launched=true）

            // 无论 loader 是否存在，都尝试 SceneLoaderProxy 兜底
            foreach (var ii in FindObjectsOfType<SceneLoaderProxy>())
                try
                {
                    if (Traverse.Create(ii).Field<string>("sceneID").Value == targetSceneId)
                    {
                        // This proxy can await the settlement screen before its
                        // nested LoadScene call. Human input has no ten-second limit.
                        AuthorizeLocalSceneLoad(targetSceneId, throughSettlement: true);
                        ii.LoadScene();
                        launched = true;
                        Debug.Log($"[SCENE] Fallback via SceneLoaderProxy -> {targetSceneId}");
                        break;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SCENE] proxy check failed: " + e);
                }

            if (!launched && loader != null && !string.IsNullOrEmpty(targetSceneId))
            {
                loader.LoadScene(targetSceneId, null, true, notifyEvac, true, false, default, save).Forget();
                launched = true;
                Debug.Log($"[SCENE] Direct SceneLoader fallback -> {targetSceneId}");
            }

            _localSceneLoadLaunched = launched;

            if (!launched) Debug.LogWarning($"[SCENE] Local load fallback failed: no proxy for '{targetSceneId}'");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SCENE] Local load failed: " + e);
        }
        finally
        {
            allowLocalSceneLoad = false;
            if (networkStarted)
            {
                if (IsServer) SendLocalPlayerStatus.Instance.SendPlayerStatusUpdate();
                else Send_ClientStatus.Instance.SendClientStatusUpdate();
                LocalPlayerManager.Instance?.RequestFullLoadoutSync();
            }
        }
    }

    public void Server_HandleSceneReady(NetPeer fromPeer, string playerId, string sceneId, Vector3 pos, Quaternion rot, string faceJson)
    {
        if (fromPeer != null) SceneM._srvPeerScene[fromPeer] = sceneId;

        if (fromPeer != null && !string.IsNullOrEmpty(playerId))
        {
            Service?.GrantPlayerInvincibility(playerId, 15f);
        }

        // 1) 回给 fromPeer：同图的所有已知玩家
        foreach (var kv in SceneM._srvPeerScene)
        {
            var other = kv.Key;
            if (other == fromPeer) continue;
            if (kv.Value == sceneId)
            {
                // 取 other 的快照（尽量从 playerStatuses 或远端对象抓取）
                var opos = Vector3.zero;
                var orot = Quaternion.identity;
                var oface = "";
                if (playerStatuses.TryGetValue(other, out var s) && s != null)
                {
                    opos = s.Position;
                    orot = s.Rotation;
                    oface = s.CustomFaceJson ?? "";
                }

                var w = new NetDataWriter();
                w.Put((byte)Op.REMOTE_CREATE);
                w.Put(playerStatuses[other].EndPoint); // other 的 id
                w.Put(sceneId);
                w.PutVector3(opos);
                w.PutQuaternion(orot);
                w.Put(oface);
                fromPeer?.Send(w, DeliveryMethod.ReliableOrdered);
            }
        }

        // 2) 广播给同图的其他人：创建 fromPeer
        foreach (var kv in SceneM._srvPeerScene)
        {
            var other = kv.Key;
            if (other == fromPeer) continue;
            if (kv.Value == sceneId)
            {
                var w = new NetDataWriter();
                w.Put((byte)Op.REMOTE_CREATE);
                w.Put(playerId);
                w.Put(sceneId);
                w.PutVector3(pos);
                w.PutQuaternion(rot);
                var useFace = !string.IsNullOrEmpty(faceJson) ? faceJson :
                    playerStatuses.TryGetValue(fromPeer, out var ss) && !string.IsNullOrEmpty(ss.CustomFaceJson) ? ss.CustomFaceJson : "";
                w.Put(useFace);
                other.Send(w, DeliveryMethod.ReliableOrdered);
            }
        }

        // 3) 对不同图的人，互相 DESPAWN
        foreach (var kv in SceneM._srvPeerScene)
        {
            var other = kv.Key;
            if (other == fromPeer) continue;
            if (kv.Value != sceneId)
            {
                var w1 = new NetDataWriter();
                w1.Put((byte)Op.REMOTE_DESPAWN);
                w1.Put(playerId);
                other.Send(w1, DeliveryMethod.ReliableOrdered);

                var w2 = new NetDataWriter();
                w2.Put((byte)Op.REMOTE_DESPAWN);
                w2.Put(playerStatuses[other].EndPoint);
                fromPeer?.Send(w2, DeliveryMethod.ReliableOrdered);
            }
        }

        // 4) （可选）主机本地也显示客户端：在主机场景创建“该客户端”的远端克隆
        if (remoteCharacters.TryGetValue(fromPeer, out var exists) && exists != null)
        {
            // 同一玩家重新进图时旧克隆可能还在上一张图的位置，必须原地校正。
            var cmc = exists.GetComponent<CharacterMainControl>();
            if (cmc != null)
            {
                cmc.transform.position = pos;
                if (cmc.modelRoot != null) cmc.modelRoot.rotation = rot;
            }
        }
        else
        {
            CreateRemoteCharacter.CreateRemoteCharacterAsync(fromPeer, pos, rot, faceJson).Forget();
        }
    }

    public void Host_BeginSceneVote_Simple(string targetSceneId, string curtainGuid,
        bool notifyEvac, bool saveToFile,
        bool useLocation, string locationName, int beaconIndex = -1)
    {
        if (_srvPendingBeginLoad || _srvPendingSceneLoad || _srvLoadInProgress || SceneLoader.IsSceneLoading || TeleporterTravel.IsActive)
        {
            Debug.LogWarning(
                $"[SCENE] Begin vote blocked: pendingBegin={_srvPendingBeginLoad} pendingLoad={_srvPendingSceneLoad} " +
                $"loadInProgress={_srvLoadInProgress} sceneLoading={SceneLoader.IsSceneLoading} " +
                $"teleporter={TeleporterTravel.IsActive} target='{targetSceneId}'");
            return;
        }
        if (sceneVoteActive)
        {
            MModUI.ShowTip(CoopLocalization.Get("ui.sceneVote.alreadyActive"));
            SceneTriggerResetter.ResetAllSceneTriggers();
            return;
        }

        sceneTargetId = targetSceneId ?? "";
        sceneCurtainGuid = string.IsNullOrEmpty(curtainGuid) ? null : curtainGuid;
        sceneNotifyEvac = notifyEvac;
        sceneSaveToFile = saveToFile;
        sceneUseLocation = useLocation;
        sceneLocationName = locationName ?? "";
        IsDoteleportMap = string.Equals(sceneLocationName, "DoTeleport", StringComparison.Ordinal);
        IsMapSelectionEntry = string.Equals(sceneLocationName, "OnPointerClick", StringComparison.Ordinal);

        // 参与者（同图优先；拿不到 SceneId 的竞态由客户端再过滤）
        sceneParticipantIds.Clear();
        if (IsMapSelectionEntry)
        {
            sceneParticipantIds.Add(Service.GetSelfNetworkId());
            foreach (var peer in netManager.ConnectedPeerList)
                sceneParticipantIds.Add(Service.GetPlayerId(peer));
            _teleporterBeaconIndex = beaconIndex;
            _teleporterToken = TeleporterTravel.BeginHost(sceneTargetId, beaconIndex, sceneParticipantIds);
        }
        else
        {
            sceneParticipantIds.AddRange(CoopTool.BuildParticipantIds_Server());
            _teleporterToken = null;
            _teleporterBeaconIndex = -1;
        }

        sceneVoteActive = true;
        localReady = false;
        sceneReady.Clear();
        foreach (var pid in sceneParticipantIds) sceneReady[pid] = false;

        // 计算主机当前 SceneId
        string hostSceneId = null;
        LocalPlayerManager.Instance.ComputeIsInGame(out hostSceneId);
        hostSceneId = hostSceneId ?? string.Empty;

        var message = new SceneVoteStartRpc
        {
            TargetSceneId = sceneTargetId,
            CurtainGuid = sceneCurtainGuid,
            NotifyEvac = sceneNotifyEvac,
            SaveToFile = sceneSaveToFile,
            UseLocation = sceneUseLocation,
            LocationName = sceneLocationName ?? string.Empty,
            HostSceneId = hostSceneId,
            ParticipantIds = sceneParticipantIds.Count > 0 ? sceneParticipantIds.ToArray() : Array.Empty<string>(),
            TeleporterToken = _teleporterToken,
            BeaconIndex = _teleporterBeaconIndex
        };

        CoopTool.SendRpc(in message);

        Debug.Log($"[SCENE] 投票开始 v{SceneVoteStartRpc.CurrentVersion}: target='{sceneTargetId}', hostScene='{hostSceneId}', loc='{sceneLocationName}', count={sceneParticipantIds.Count}");
    }

    public void Client_RequestBeginSceneVote(
        string targetId, string curtainGuid,
        bool notifyEvac, bool saveToFile,
        bool useLocation, string locationName)
    {
        if (!networkStarted || IsServer || connectedPeer == null) return;
        if (SceneLoader.IsSceneLoading || (Duckov.Scenes.MultiSceneCore.Instance != null && Duckov.Scenes.MultiSceneCore.Instance.IsLoading)) return;
        if (sceneVoteActive)
        {
            MModUI.ShowTip(CoopLocalization.Get("ui.sceneVote.alreadyActive"));
            SceneTriggerResetter.ResetAllSceneTriggers();
            return;
        }

        var message = new SceneVoteRequestRpc
        {
            TargetSceneId = targetId ?? string.Empty,
            CurtainGuid = string.IsNullOrEmpty(curtainGuid) ? null : curtainGuid,
            NotifyEvac = notifyEvac,
            SaveToFile = saveToFile,
            UseLocation = useLocation,
            LocationName = locationName ?? string.Empty
        };

        CoopTool.SendRpc(in message);
    }

    public UniTask AppendSceneGate(UniTask original)
    {
        return Internal();

        async UniTask Internal()
        {
            // 先等待原本的其他初始化
            await original;

            try
            {
                if (!networkStarted) return;

                // 只在“关卡场景”里做门控；LevelManager 在关卡中才存在
                // （这里不去使用 waitForInitializationList / LoadScene）

                await Client_SceneGateAsync();
            }
            catch (Exception e)
            {
                Debug.LogError("[SCENE-GATE] " + e);
            }
        }
    }

    public async UniTask Client_SceneGateAsync(string sceneId = null)
    {
        if (!networkStarted || IsServer) return;
        var sid = string.IsNullOrEmpty(sceneId) ? TryGuessActiveSceneId() : sceneId;
        if (IsBaseScene(sid)) return;
        var generation = ++_clientGateGeneration;
        _cliGateSid = sid;
        _cliSceneGateReleased = false;
        try
        {
            var connectDeadline = Time.realtimeSinceStartup + 8f;
            while (networkStarted && connectedPeer == null && Time.realtimeSinceStartup < connectDeadline)
                await UniTask.Delay(100, ignoreTimeScale: true);
            if (!networkStarted || connectedPeer == null || generation != _clientGateGeneration) return;

            WaitingSynchronizationUI.Instance?.Show();
            _cliGateDeadline = Time.realtimeSinceStartup + ClientGateTimeoutSeconds;
            var nextReadyTime = 0f;
            while (networkStarted && connectedPeer != null && generation == _clientGateGeneration &&
                   !_cliSceneGateReleased && Time.realtimeSinceStartup < _cliGateDeadline)
            {
                // READY 可能因为主机尚未加载完、场景 ID 不一致或网络抖动而没有被处理；
                // 这里周期性重发，主机侧按最后一次 READY 放行，避免单次丢失就等满超时。
                if (Time.realtimeSinceStartup >= nextReadyTime)
                {
                    var peer = connectedPeer;
                    if (peer == null) break;

                    var w = new NetDataWriter();
                    w.Put((byte)Op.SCENE_GATE_READY);
                    w.Put(localPlayerStatus?.EndPoint ?? string.Empty);
                    w.Put(sid ?? string.Empty);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                    nextReadyTime = Time.realtimeSinceStartup + ClientGateRetryInterval;
                }

                SceneLoader.LoadingComment = CoopLocalization.Get("scene.waitingForHost");
                await UniTask.Delay(100, ignoreTimeScale: true);
            }

            CoopLogSystem.WriteNetworkDiagnostic(
                $"[SCENE] Gate scene='{sid}' released={_cliSceneGateReleased} connected={connectedPeer != null} " +
                $"waited={Mathf.Max(0f, ClientGateTimeoutSeconds - (_cliGateDeadline - Time.realtimeSinceStartup)):0.0}s");
        }
        finally
        {
            if (generation == _clientGateGeneration) WaitingSynchronizationUI.Instance?.Hide();
        }
    }

    // 主机：自身初始化完成 → 开门；已举手的立即放行；之后若有迟到的 READY，也会单放行
    public async UniTask Server_SceneGateAsync()
    {
        if (!IsServer || !networkStarted) return;

        _srvGateSid = TryGuessActiveSceneId();
        _srvSceneGateOpen = true;

        // 确保在主线程下一帧再去放行客户端，避免加载钩子里抢占资源导致卡住
        await UniTask.Yield();

        // 放行已经举手的所有客户端。按客机自己上报的 sid 回显，
        // 避免主机 active scene 与客机 LevelManager 所在子场景命名不一致时客户端拒收。
        if (playerStatuses != null && playerStatuses.Count > 0)
            foreach (var kv in playerStatuses)
            {
                var peer = kv.Key;
                var st = kv.Value;
                if (peer == null || st == null) continue;
                if (!_srvGateReadyPids.Contains(st.EndPoint)) continue;
                var releaseSid = _srvGateReadySids.TryGetValue(st.EndPoint, out var readySid) &&
                                 !string.IsNullOrEmpty(readySid)
                    ? readySid
                    : _srvGateSid;
                _srvGateReadySids[st.EndPoint] = releaseSid;
                Server_SendGateRelease(peer, releaseSid);
            }
    }

    public void Server_HandleGateReady(NetPeer peer, string sid)
    {
        if (!IsServer || peer == null || string.IsNullOrEmpty(sid)) return;

        var playerId = Service.GetPlayerId(peer);
        if (!string.IsNullOrEmpty(playerId))
        {
            _srvGateReadyPids.Add(playerId);
            _srvGateReadySids[playerId] = sid;
        }

        if (string.IsNullOrEmpty(_srvGateSid)) _srvGateSid = sid;

        // 主机已经进入关卡、且不是还停在 Base 时，直接放行；放行消息带客机自己的 sid。
        if (Server_HostCanReleaseGate(sid))
        {
            Server_SendGateRelease(peer, sid);
            CoopLogSystem.WriteNetworkDiagnostic($"[SCENE] Gate release -> {peer.EndPoint} sid='{sid}' (host ready)");
            return;
        }

        // 主机还在加载：先记下，等 Server_SceneGateAsync 开门时统一放行。
        CoopLogSystem.WriteNetworkDiagnostic(
            $"[SCENE] Gate ready queued peer={peer.EndPoint} sid='{sid}' hostGateSid='{_srvGateSid}' " +
            $"loading={SceneLoader.IsSceneLoading} inited={LevelManager.LevelInited} gateOpen={_srvSceneGateOpen}");
    }

    internal static bool IsBaseScene(string sceneId)
    {
        if (string.IsNullOrEmpty(sceneId)) return false;
        if (string.Equals(sceneId, SceneInfoCollection.BaseSceneID, StringComparison.OrdinalIgnoreCase)) return true;
        // 游戏在 Base 场景里实际拿到的 active scene 名可能是 Base_SceneV2 之类的变体；
        // 只比较 BaseSceneID 会把 Base 误判成关卡，导致主机还在基地就给客机放行。
        return sceneId.StartsWith("Base_Scene", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sceneId, "Base", StringComparison.OrdinalIgnoreCase);
    }

    private bool Server_HostCanReleaseGate(string sid)
    {
        if (SceneLoader.IsSceneLoading || !LevelManager.LevelInited) return false;

        var level = LevelManager.Instance;
        if (level == null || level.MainCharacter == null) return false;

        var manager = LocalPlayerManager.Instance;
        if (manager == null || !manager.ComputeIsInGame(out var hostScene) || string.IsNullOrEmpty(hostScene))
            return false;

        if (string.Equals(hostScene, sid, StringComparison.OrdinalIgnoreCase)) return true;
        if (MultiSceneCore.MainScene.HasValue &&
            string.Equals(MultiSceneCore.MainSceneID, sid, StringComparison.OrdinalIgnoreCase))
            return true;

        // 主机已经不在 Base，说明确实在关卡里；允许子场景命名差异，避免客机等满超时。
        // 但主机还在 Base 时不能放行，否则客机会进入一张主机根本没加载的地图。
        if (IsBaseScene(hostScene))
        {
            CoopLogSystem.WriteNetworkDiagnostic(
                $"[SCENE] Gate wait: host still in base, client sid='{sid}' hostScene='{hostScene}'");
            return false;
        }

        CoopLogSystem.WriteNetworkDiagnostic(
            $"[SCENE] Gate sid mismatch host='{hostScene}' client='{sid}', host in level -> release");
        return true;
    }

    private void Server_SendGateRelease(NetPeer peer, string sid)
    {
        if (peer == null) return;
        var w = new NetDataWriter();
        w.Put((byte)Op.SCENE_GATE_RELEASE);
        w.Put(sid ?? "");
        peer.Send(w, DeliveryMethod.ReliableOrdered);
    }


    private string TryGuessActiveSceneId()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var id = scene.IsValid() ? SceneInfoCollection.GetSceneID(scene.buildIndex) : null;
        return string.IsNullOrEmpty(id) ? sceneTargetId : id;
    }

    private async UniTaskVoid FallbackRetryLocalSceneLoad(string targetSceneId, string curtainGuid,
        bool notifyEvac, bool save, bool useLocation, string locationName)
    {
        if (locationName == "DoTeleport")
            return;

        // 最多重试两次，每次间隔 1s，防止偶发竞态导致首轮加载没有触发
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(1));

            if (_localSceneLoadLaunched) break;

            Debug.LogWarning($"[SCENE] retry local load (attempt {attempt + 2}) for '{targetSceneId}'");

            AuthorizeLocalSceneLoad(targetSceneId);
            TryPerformSceneLoad_Local(targetSceneId, curtainGuid, notifyEvac, save, useLocation, locationName);
        }
    }

    private async UniTaskVoid FallbackRetryTeleport(string targetSceneId, string teleporterName)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(1));

            if (_localSceneLoadLaunched) break;

            Debug.LogWarning($"[SCENE] retry teleporter load (attempt {attempt + 2}) for '{targetSceneId}' via '{teleporterName}'");

            AuthorizeLocalSceneLoad(targetSceneId);
            _localSceneLoadLaunched = CoopTool.GoTeleport(targetSceneId, teleporterName);
        }
    }

}
