using Duckov.Scenes;

namespace EscapeFromDuckovCoopMod;

// Only dynamically registered corpse boxes participate. Inventory remains on
// the same stable loot UID while one carrier publishes bounded pose updates.
internal sealed class CorpseCarrySync : MonoBehaviour
{
    private static uint _nextToken = 1;
    private static readonly Action<Carriable, bool> SetPhysics =
        (Action<Carriable, bool>)AccessTools.Method(typeof(Carriable), "SetRigidbodyActive")
            .CreateDelegate(typeof(Action<Carriable, bool>));
    private static readonly AccessTools.FieldRef<Carriable, Rigidbody> Body =
        AccessTools.FieldRefAccess<Carriable, Rigidbody>("rb");
    private readonly CorpseCarryLease _lease = new();
    private InteractableLootbox _box;
    private Carriable _carry;
    private CA_Carry _action;
    private NetPeer _ownerPeer;
    private int _uid;
    private uint _token;
    private bool _local, _busy;
    private float _nextSend, _settleUntil, _lastSentAt, _nextDenialLog;
    private Vector3 _lastPosition;
    private Quaternion _lastRotation;
    private NetService Service => NetService.Instance;

    internal static CorpseCarrySync For(Carriable carry) =>
        carry && carry.lootbox ? carry.lootbox.GetComponent<CorpseCarrySync>() : null;

    internal static void Register(InteractableLootbox box, int uid)
    {
        if (!box || uid < 0) return;
        var carry = box.GetComponentInChildren<Carriable>(true);
        if (!carry || carry.lootbox != box) return;
        var sync = box.GetComponent<CorpseCarrySync>() ?? box.gameObject.AddComponent<CorpseCarrySync>();
        sync._box = box; sync._carry = carry; sync._uid = uid;
    }

    internal bool BeginLocal(CA_Carry action)
    {
        if (Service?.networkStarted != true || !action || action.characterController != CharacterMainControl.Main)
            return true;
        if (_local || _busy || Service.IsServer && _lease.Held(Time.unscaledTime)) return false;
        _token = _nextToken++;
        if (_token == 0) _token = _nextToken++;
        _action = action; _local = true; _settleUntil = 0; _nextSend = 0;
        var message = Capture(CorpseCarryRpc.Begin);
        if (Service.IsServer)
        {
            if (!Accept(null, message)) { _local = false; return false; }
        }
        else CoopTool.SendRpc(in message);
        return true;
    }

    internal void DropLocal()
    {
        if (!_local || _settleUntil > 0) return;
        // Native DropTask simulates gravity for three seconds before freezing.
        _settleUntil = Time.time + 3.2f;
        _nextSend = 0;
    }

    private void Update()
    {
        if (Service?.networkStarted != true || !_box || !_carry || SceneLoader.IsSceneLoading) return;
        var now = Time.unscaledTime;
        if (_local)
        {
            if (_settleUntil == 0 && (!_action || !_action.Running)) DropLocal();
            var done = _settleUntil > 0 && Time.time >= _settleUntil;
            if (!done && now < _nextSend) return;
            _nextSend = now + 0.2f;
            var moved = (_box.transform.position - _lastPosition).sqrMagnitude > 0.0001f ||
                        Quaternion.Angle(_box.transform.rotation, _lastRotation) > 0.5f;
            if (!done && !moved && now - _lastSentAt < 1) return;
            var message = Capture(done ? CorpseCarryRpc.End : CorpseCarryRpc.Pose);
            _lastPosition = message.Position; _lastRotation = message.Rotation; _lastSentAt = now;
            if (Service.IsServer) Accept(null, message);
            else CoopTool.SendRpc(in message);
            RefreshPosition();
            if (done) { _local = false; _action = null; SetPhysics(_carry, false); }
        }
        else if (Service.IsServer && _lease.Owner != null &&
                 (!_lease.Held(now) || _ownerPeer != null && _ownerPeer.ConnectionState != ConnectionState.Connected))
        {
            // A disconnected carrier cannot leave the box permanently locked or
            // hanging in the air. The host settles it, then publishes a final pose.
            CoopLogSystem.WriteNetworkDiagnostic($"[LOOT][Carry] recover uid={_uid} owner={_lease.Owner}");
            _lease.Release(_lease.Owner, _lease.Token);
            _busy = false; _token = _nextToken++; _local = true; _action = null;
            if (_token == 0) _token = _nextToken++;
            _settleUntil = Time.time + 3.2f; _nextSend = 0;
            _lease.Acquire(Service.GetPlayerId(null), _token, now); _ownerPeer = null;
            SetPhysics(_carry, true);
            var body = Body(_carry);
            if (body) body.velocity = Vector3.zero;
        }
    }

    private CorpseCarryRpc Capture(byte phase) => new()
    {
        Phase = phase, LootUid = _uid, Token = _token,
        SceneId = MultiSceneCore.MainSceneID, Position = _box.transform.position, Rotation = _box.transform.rotation
    };

    public static void Handle(RpcContext context, CorpseCarryRpc message)
    {
        if (context.Service?.networkStarted != true || SceneLoader.IsSceneLoading ||
            !Spectator.AreSameMap(message.SceneId, MultiSceneCore.MainSceneID) ||
            !CoopSyncDatabase.Loot.TryGetByLootUid(message.LootUid, out var entry) || !entry.Lootbox) return;
        var sync = entry.Lootbox.GetComponent<CorpseCarrySync>();
        if (!sync) return;
        if (context.IsServer)
        {
            if (context.Sender != null && message.Phase <= CorpseCarryRpc.End) sync.Accept(context.Sender, message);
        }
        else if (message.Phase == CorpseCarryRpc.State || message.Phase == CorpseCarryRpc.Denied)
            sync.ApplyState(message);
    }

    private bool Accept(NetPeer peer, CorpseCarryRpc message)
    {
        var owner = Service.GetPlayerId(peer);
        var player = peer == null ? CharacterMainControl.Main : CoopTool.TryGetRemoteCharacterForPeer(peer);
        var now = Time.unscaledTime;
        var valid = player && Finite(message.Position) && Finite(message.Rotation) &&
                    Spectator.AreSameMap(message.SceneId, MultiSceneCore.MainSceneID);
        if (valid && message.Phase == CorpseCarryRpc.Begin)
            valid = (_box.transform.position - player.transform.position).sqrMagnitude <= 36 &&
                    (message.Position - _box.transform.position).sqrMagnitude <= 9 &&
                    _lease.Acquire(owner, message.Token, now);
        else if (valid)
            valid = ((message.Position - player.transform.position).sqrMagnitude <= 144 ||
                     (message.Position - _box.transform.position).sqrMagnitude <= 4) &&
                    _lease.Renew(owner, message.Token, now);
        if (!valid)
        {
            if (now >= _nextDenialLog)
            {
                _nextDenialLog = now + 2;
                CoopLogSystem.WriteNetworkDiagnostic($"[LOOT][Carry] denied uid={_uid} phase={message.Phase} owner={owner} active={_lease.Owner}");
            }
            if (peer != null)
            {
                var denied = CurrentState(); denied.Phase = CorpseCarryRpc.Denied; denied.Token = message.Token;
                CoopTool.SendRpcTo(peer, in denied);
            }
            return false;
        }
        _ownerPeer = peer;
        if (peer != null) MoveBox(message.Position, message.Rotation);
        RefreshPosition();
        if (message.Phase == CorpseCarryRpc.End) _lease.Release(owner, message.Token);
        if (message.Phase == CorpseCarryRpc.Begin || message.Phase == CorpseCarryRpc.End)
            CoopLogSystem.WriteNetworkDiagnostic($"[LOOT][Carry] phase={message.Phase} uid={_uid} owner={owner} pos={_box.transform.position}");
        var state = CurrentState(); state.Token = message.Token; state.Owner = owner;
        CoopTool.SendRpc(in state);
        return true;
    }

    private CorpseCarryRpc CurrentState() => new()
    {
        Phase = CorpseCarryRpc.State, LootUid = _uid, Token = _lease.Token,
        SceneId = MultiSceneCore.MainSceneID, Owner = _lease.Owner ?? "", Busy = _lease.Held(Time.unscaledTime),
        Position = _box.transform.position, Rotation = _box.transform.rotation
    };

    private void ApplyState(CorpseCarryRpc message)
    {
        if (!Finite(message.Position) || !Finite(message.Rotation)) return;
        if (message.Phase == CorpseCarryRpc.Denied)
        {
            // A delayed denial for an older attempt must not stop a newer carry.
            if (message.Token != _token) return;
            _local = false;
            if (_action && _action.Running) _action.StopAction();
            _action = null; _settleUntil = 0;
        }
        else if (Service.IsSelfId(message.Owner))
        {
            // Ignore old self echoes, including those still in flight after dropping.
            if (message.Token != _token) return;
            _busy = message.Busy;
            if (_local || message.Busy) return;
        }
        _busy = message.Busy;
        // A concurrent claimant is resolved by its ordered accept/deny response.
        if (_local) return;
        MoveBox(message.Position, message.Rotation);
        RefreshPosition();
    }

    private void MoveBox(Vector3 position, Quaternion rotation)
    {
        SetPhysics(_carry, false);
        var body = Body(_carry);
        var root = body ? body.transform : _box.transform;
        var delta = rotation.normalized * Quaternion.Inverse(_box.transform.rotation);
        var rootPosition = position + delta * (root.position - _box.transform.position);
        root.SetPositionAndRotation(rootPosition, delta * root.rotation);
    }

    private void RefreshPosition() => CoopSyncDatabase.Loot.UpdatePosition(_box);
    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
    private static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w) &&
        q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w > 0.01f && q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w < 4;

    internal static void Replay(NetPeer peer, InteractableLootbox box)
    {
        var sync = box ? box.GetComponent<CorpseCarrySync>() : null;
        if (!sync) return;
        var state = sync.CurrentState();
        CoopTool.SendRpcTo(peer, in state);
    }
}
