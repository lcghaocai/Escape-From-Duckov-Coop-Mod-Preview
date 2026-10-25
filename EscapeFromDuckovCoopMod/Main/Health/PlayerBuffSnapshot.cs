namespace EscapeFromDuckovCoopMod;

public partial class Buff_
{
    private float _nextLocalSnapshot;

    internal void SendLocalSnapshot()
    {
        var service = Service;
        if (service == null || !service.networkStarted || !CharacterMainControl.Main ||
            Time.unscaledTime < _nextLocalSnapshot) return;
        _nextLocalSnapshot = Time.unscaledTime + 3f;
        var message = new PlayerBuffSnapshotRpc
        {
            PlayerId = service.GetPlayerId(null),
            Buffs = CaptureLocal()
        };
        CoopTool.SendRpc(in message);
    }

    internal void HandleSnapshot(RpcContext context, PlayerBuffSnapshotRpc message)
    {
        if (message.Buffs == null || Service == null) return;
        if (context.IsServer)
        {
            if (context.Sender == null) return;
            // Owners may reconcile only their own replica, never a supplied player ID.
            message.PlayerId = Service.GetPlayerId(context.Sender);
            if (string.IsNullOrEmpty(message.PlayerId)) return;
            ReceiveSnapshot(message.PlayerId, message.Buffs);
            CoopTool.SendRpc(in message, context.Sender);
        }
        else ReceiveSnapshot(message.PlayerId, message.Buffs);
    }

    internal static PlayerBuffState[] CaptureLocal() => CharacterMainControl.Main.GetBuffManager().Buffs
        .Where(b => b).Select(b => new PlayerBuffState { WeaponTypeId = b.fromWeaponID, BuffId = b.ID, Layers = b.CurrentLayers }).ToArray();

    internal void ReceiveSnapshot(string playerId, PlayerBuffState[] buffs)
    {
        if (buffs == null || Service == null || Service.IsSelfId(playerId)) return;
        CharacterMainControl character = null;
        if (Service.IsServer)
        {
            foreach (var pair in Service.remoteCharacters)
                if (Service.GetPlayerId(pair.Key) == playerId && pair.Value)
                    character = pair.Value.GetComponent<CharacterMainControl>();
        }
        else if (Service.clientRemoteCharacters.TryGetValue(playerId, out var go) && go)
            character = go.GetComponent<CharacterMainControl>();
        // A fresh owner heartbeat will reconcile replicas that are still being constructed.
        if (!character || character.IsMainCharacter) return;

        var desired = new HashSet<int>(buffs.Select(b => b.BuffId));
        var known = _replayOrder.BuffsFor(playerId).Concat(character.GetBuffManager().Buffs.Where(b => b).Select(b => b.ID)).Distinct().ToArray();
        foreach (var buffId in known)
            if (!desired.Contains(buffId))
                ApplyBuffToCharacter(playerId, character, 0, buffId, true, 0).Forget();
        foreach (var buff in buffs)
            ApplyBuffToCharacter(playerId, character, buff.WeaponTypeId, buff.BuffId, true, buff.Layers).Forget();
    }
}
