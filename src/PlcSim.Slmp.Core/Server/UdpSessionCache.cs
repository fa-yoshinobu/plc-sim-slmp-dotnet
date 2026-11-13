using PlcSim.Slmp.Core.Engine;

namespace PlcSim.Slmp.Core.Server;

// Only the UDP receive loop accesses this cache; Clear also runs during shutdown/reset.
internal sealed class UdpSessionCache
{
    internal const int Capacity = 1024;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, LinkedListNode<(SlmpSession Session, DateTime Time)>> _entries = new();
    private readonly LinkedList<(SlmpSession Session, DateTime Time)> _recent = new();

    public int Count { get { lock (_entries) return _entries.Count; } }

    public SlmpSession Get(string remote, DateTime now)
    {
        lock (_entries)
        {
            while (_recent.First is { } oldest && now - oldest.Value.Time >= IdleTimeout)
                RemoveOldest();
            if (_entries.TryGetValue(remote, out var node))
            {
                node.Value = (node.Value.Session, now);
                _recent.Remove(node);
                _recent.AddLast(node);
                return node.Value.Session;
            }
            if (_entries.Count >= Capacity) RemoveOldest();
            var session = new SlmpSession(remote, "UDP");
            _entries.Add(remote, _recent.AddLast((session, now)));
            return session;
        }
    }

    private void RemoveOldest()
    {
        _entries.Remove(_recent.First!.Value.Session.Remote);
        _recent.RemoveFirst();
    }

    public void Clear()
    {
        lock (_entries) { _entries.Clear(); _recent.Clear(); }
    }
}
