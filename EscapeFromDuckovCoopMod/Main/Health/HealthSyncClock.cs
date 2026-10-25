namespace EscapeFromDuckovCoopMod;

// Unity's unscaled time is constant throughout one frame. Give each capture a
// distinct stamp so multiple hits/death transitions in that frame stay ordered.
internal sealed class HealthSyncClock
{
    private double _last;
    public double Next(double now) => _last = Math.Max(now, _last + 0.000001d);
}

// Before AI metadata arrives, coalesce state but retain every one-shot hit/kill event.
internal sealed class PendingHealthUpdates<T>
{
    public T Message { get; private set; }
    public long Order { get; private set; }
    private double _timestamp;
    public List<T> Events { get; } = new();

    public void Add(T message, long order, double timestamp, bool hasEvent)
    {
        if (HealthSnapshotOrder.IsNewer(order, timestamp, Order, _timestamp))
        {
            Message = message;
            Order = order;
            _timestamp = timestamp;
        }
        if (hasEvent) Events.Add(message);
    }
}
