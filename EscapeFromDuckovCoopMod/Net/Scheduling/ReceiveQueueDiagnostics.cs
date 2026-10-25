namespace EscapeFromDuckovCoopMod;

public readonly struct ReceiveQueueSnapshot
{
    public readonly bool Supported;
    public readonly int Count, CombatCount;
    public readonly double OldestWaitMs;
    public ReceiveQueueSnapshot(int count, int combat, double age)
    { Supported = true; Count = count; CombatCount = combat; OldestWaitMs = age; }
}

internal sealed class ReceiveQueueDiagnostics
{
    private readonly int[] _pending = new int[256];
    private readonly long[] _bytes = new long[256], _arrived = new long[256], _handled = new long[256];
    private readonly double[] _cost = new double[256], _maxCost = new double[256], _maxWait = new double[256];
    public void Enqueue(byte op, int bytes) { _pending[op]++; _bytes[op] += bytes; _arrived[op]++; }
    public void Release(byte op, int bytes) { _pending[op]--; _bytes[op] -= bytes; }
    public void Handled(byte op, double waitMs, double costMs)
    {
        _handled[op]++; _cost[op] += costMs;
        _maxCost[op] = Math.Max(_maxCost[op], costMs); _maxWait[op] = Math.Max(_maxWait[op], waitMs);
    }
    public bool HasActivity => _arrived.Any(n => n > 0) || _handled.Any(n => n > 0);
    public string Summary()
    {
        var ids = Enumerable.Range(0, 256).Where(i => _pending[i] > 0 || _arrived[i] > 0 || _handled[i] > 0).ToArray();
        var top = ids.OrderByDescending(i => _cost[i]).Take(4)
            .Concat(ids.OrderByDescending(i => _pending[i]).Take(4)).Distinct();
        return string.Join("; ", top.Select(i => $"{(Op)i}:pending={_pending[i]} bytes={_bytes[i]} in={_arrived[i]} handled={_handled[i]} costMs={_cost[i]:0.0} maxMs={_maxCost[i]:0.0} waitMaxMs={_maxWait[i]:0.0}"));
    }
    public void ResetWindow()
    {
        Array.Clear(_arrived, 0, 256); Array.Clear(_handled, 0, 256);
        Array.Clear(_cost, 0, 256); Array.Clear(_maxCost, 0, 256); Array.Clear(_maxWait, 0, 256);
    }
    public void Clear() { ResetWindow(); Array.Clear(_pending, 0, 256); Array.Clear(_bytes, 0, 256); }
}
