namespace EscapeFromDuckovCoopMod;

// Advisory per-connection pressure; never drops events or changes the authority of game state.
internal sealed class PeerReceivePressure<TPeer>
{
    private sealed class State
    {
        public double SourceTime, ReceivedAt, NextSnapshot;
        public int Tier, HealthySamples;
        public readonly Dictionary<int, double> NextPose = new();
    }
    private readonly Dictionary<TPeer, State> _peers = new();
    public bool Report(TPeer peer, double sourceTime, int backlog, double ageMs, double now)
    {
        if (double.IsNaN(sourceTime) || double.IsInfinity(sourceTime) || sourceTime <= 0 ||
            double.IsNaN(ageMs) || double.IsInfinity(ageMs) || ageMs < 0 || backlog < 0) return false;
        if (!_peers.TryGetValue(peer, out var state)) _peers[peer] = state = new();
        if (sourceTime <= state.SourceTime || state.SourceTime > 0 && now - state.ReceivedAt < 0.25) return false;
        state.SourceTime = sourceTime; state.ReceivedAt = now;
        var tier = backlog >= 1024 || ageMs >= 500 ? 2 : backlog >= 256 || ageMs >= 150 ? 1 : 0;
        if (tier > 0) { state.Tier = tier; state.HealthySamples = 0; }
        else if (backlog < 64 && ageMs < 40)
        {
            if (++state.HealthySamples >= 3) { state.Tier = 0; state.NextPose.Clear(); }
        }
        else state.HealthySamples = 0;
        return true;
    }
    public int Tier(TPeer peer, double now)
    {
        if (!_peers.TryGetValue(peer, out var state)) return 0;
        if (now - state.ReceivedAt > 5) { state.Tier = 0; state.NextPose.Clear(); }
        return state.Tier;
    }
    public bool AllowPose(TPeer peer, int id, double distanceSquared, double now, bool critical)
    {
        var tier = Tier(peer, now);
        if (critical || tier == 0 || distanceSquared <= 40 * 40) return true;
        var state = _peers[peer];
        if (state.NextPose.TryGetValue(id, out var next) && now < next) return false;
        var interval = distanceSquared <= 120 * 120 ? 0.15 : 0.3;
        state.NextPose[id] = now + interval * tier;
        return true;
    }
    public bool AllowPeriodicSnapshot(TPeer peer, double now)
    {
        var tier = Tier(peer, now);
        if (tier == 0) return true;
        var state = _peers[peer];
        if (now < state.NextSnapshot) return false;
        state.NextSnapshot = now + tier;
        return true;
    }
    public void Remove(TPeer peer) => _peers.Remove(peer);
    public void Clear() => _peers.Clear();
}

internal sealed class EntityRecoveryLimiter
{
    private readonly Dictionary<int, double> _next = new();
    private double _tokens = 8, _last;
    public bool TryAcquire(int id, double now)
    {
        if (id <= 0) return false;
        _tokens = Math.Min(8, _tokens + Math.Max(0, now - _last) * 4); _last = now;
        if (_tokens < 1 || _next.TryGetValue(id, out var next) && now < next) return false;
        if (_next.Count >= 128)
            foreach (var key in _next.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _next.Remove(key);
        if (_next.Count >= 128 && !_next.ContainsKey(id)) return false;
        _next[id] = now + 2; _tokens--; return true;
    }
    public void Clear() { _next.Clear(); _tokens = 8; _last = 0; }
}
