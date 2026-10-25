namespace EscapeFromDuckovCoopMod;

// Per recipient: a slow/busy listener cannot consume another listener's allowance.
internal static class CoopTransientAudio
{
    private static readonly Dictionary<NetPeer, TransientRateLimit> Limits = new();
    private static readonly TransientRateLimit Upload = new();
    private static float _nextCleanup;
    private static bool Take(NetPeer peer)
    {
        if (Time.realtimeSinceStartup >= _nextCleanup)
        {
            _nextCleanup = Time.realtimeSinceStartup + 1;
            foreach (var stale in Limits.Keys.Where(p => p.ConnectionState != ConnectionState.Connected).ToArray()) Limits.Remove(stale);
        }
        if (!Limits.TryGetValue(peer, out var limit)) Limits[peer] = limit = new();
        return limit.Take(Time.realtimeSinceStartup);
    }
    internal static bool AudibleLocally(Vector3 position, float radius) =>
        !CharacterMainControl.Main || (CharacterMainControl.Main.transform.position - position).sqrMagnitude <= radius * radius;
    internal static bool InRange(NetPeer peer, Vector3 position, float radius)
    {
        var statuses = NetService.Instance?.playerStatuses;
        return statuses == null || !statuses.TryGetValue(peer, out var status) || status == null ||
            (status.Position - position).sqrMagnitude <= radius * radius;
    }
    internal static void Send(in AudioEventRpc message, NetPeer exclude = null)
    {
        var service = NetService.Instance;
        if (service == null) return;
        if (!service.IsServer)
        {
            if (Upload.Take(Time.realtimeSinceStartup)) CoopTool.SendUnreliableRpc(in message);
            return;
        }
        var radius = CoopCombatAudio.AudibleRange(message.Payload);
        var position = message.Payload.Position;
        CoopTool.SendRpcToPeers(service.netManager.ConnectedPeerList.Where(peer =>
            peer != exclude && InRange(peer, position, radius) && Take(peer)), in message, DeliveryMethod.Unreliable);
    }
    internal static void Send(in AISoundEventRpc message)
    {
        var service = NetService.Instance;
        if (service?.IsServer != true) return;
        var position = message.Position;
        var radius = message.Radius + 15f;
        CoopTool.SendRpcToPeers(service.netManager.ConnectedPeerList.Where(peer =>
            InRange(peer, position, radius) && Take(peer)), in message);
    }
}
