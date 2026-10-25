namespace EscapeFromDuckovCoopMod;

internal sealed class TransientRateLimit
{
    private double _tokens = 64, _last;
    internal bool Take(double now)
    {
        _tokens = Math.Min(64, _tokens + Math.Max(0, now - _last) * 120);
        _last = now;
        if (_tokens < 1) return false;
        _tokens--;
        return true;
    }
}
