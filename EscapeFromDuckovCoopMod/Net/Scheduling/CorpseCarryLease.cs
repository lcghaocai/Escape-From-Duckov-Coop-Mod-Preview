namespace EscapeFromDuckovCoopMod;

internal sealed class CorpseCarryLease
{
    internal string Owner { get; private set; }
    internal uint Token { get; private set; }
    private double _expires;
    internal bool Held(double now) => Owner != null && now < _expires;
    internal bool Acquire(string owner, uint token, double now)
    {
        if (string.IsNullOrEmpty(owner) || token == 0 ||
            Held(now) && (Owner != owner || Token != token)) return false;
        Owner = owner; Token = token; _expires = now + 5;
        return true;
    }
    internal bool Renew(string owner, uint token, double now)
    {
        if (!Held(now) || Owner != owner || Token != token) return false;
        _expires = now + 5;
        return true;
    }
    internal bool Release(string owner, uint token)
    {
        if (Owner != owner || Token != token) return false;
        Owner = null; Token = 0; _expires = 0;
        return true;
    }
}
