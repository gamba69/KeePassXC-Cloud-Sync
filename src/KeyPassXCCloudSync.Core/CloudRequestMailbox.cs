namespace KeyPassXCCloudSync;

// Coalesces repeated manual cloud requests without dropping requests that arrive
// during an active synchronization cycle. The host drains the mailbox only when
// starting a cycle; requests arriving later belong to the NEXT cycle.
internal sealed class CloudRequestMailbox
{
    private readonly object _gate=new();
    private readonly HashSet<string> _pending=new(StringComparer.Ordinal);

    internal bool HasPending
    {
        get {lock(_gate)return _pending.Count!=0;}
    }

    internal bool Enqueue(string cloudId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cloudId);
        lock(_gate)return _pending.Add(cloudId);
    }

    internal IReadOnlySet<string> Drain()
    {
        lock(_gate)
        {
            var taken=_pending.ToHashSet(StringComparer.Ordinal);
            _pending.Clear();
            return taken;
        }
    }

    internal void Clear()
    {
        lock(_gate)_pending.Clear();
    }
}
