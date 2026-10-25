namespace EscapeFromDuckovCoopMod;

// One ownership decision per token. Scene changes must NOT clear this ledger.
internal sealed class TakeDeliveryLedger
{
    private readonly Dictionary<uint, byte> _states = new();
    internal bool Register(uint token)
    {
        if (_states.ContainsKey(token)) return false;
        _states[token] = 0;
        return true;
    }
    internal bool TryClaim(uint token)
    {
        if (!_states.TryGetValue(token, out var state) || state != 0) return false;
        _states[token] = 3; // Delivering: reentrant/duplicate result cannot enter.
        return true;
    }
    internal void Finish(uint token, bool delivered)
    {
        if (_states.TryGetValue(token, out var state) && state == 3) _states[token] = delivered ? (byte)1 : (byte)2;
    }
    internal bool TryReceipt(uint token, out byte phase) => _states.TryGetValue(token, out phase) && (phase == 1 || phase == 2);
    internal void Clear() => _states.Clear(); // Only a different connection may clear receipts.
}
