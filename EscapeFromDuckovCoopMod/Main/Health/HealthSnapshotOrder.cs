namespace EscapeFromDuckovCoopMod;

internal sealed class HealthSnapshotOrder
{
    private long _received;
    private readonly Dictionary<int, (long Order, double Timestamp)> _applied = new();

    public long Receive() => ++_received;

    // Receipt order is sufficient only for legacy messages sharing an ordered channel.
    // Snapshots and unordered health updates must compare the host's capture timestamps.
    internal static bool IsNewer(long order, double timestamp, long previousOrder, double previousTimestamp) =>
        order > 0 && !double.IsNaN(timestamp) && !double.IsInfinity(timestamp) &&
        (timestamp > 0d ? timestamp > previousTimestamp : previousTimestamp <= 0d && order > previousOrder);

    public bool TryApply(int id, long order, double timestamp = 0d)
    {
        _applied.TryGetValue(id, out var previous);
        if (!IsNewer(order, timestamp, previous.Order, previous.Timestamp)) return false;
        _applied[id] = (order, timestamp);
        return true;
    }

    public void Remove(int id) => _applied.Remove(id);

    public void Clear()
    {
        _applied.Clear();
        _received = 0;
    }
}
