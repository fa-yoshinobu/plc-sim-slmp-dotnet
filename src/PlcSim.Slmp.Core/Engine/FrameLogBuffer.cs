namespace PlcSim.Slmp.Core.Engine;

/// <summary>直近の処理記録を保持するリングバッファ（通信ログ表示・HTTP API 用）。</summary>
public sealed class FrameLogBuffer
{
    private readonly object _gate = new();
    private readonly LinkedList<FrameLogEntry> _entries = new();

    public FrameLogBuffer(SlmpEngine engine, int capacity = 2000)
    {
        Capacity = capacity;
        engine.FrameProcessed += Add;
    }

    public int Capacity { get; }

    public void Add(FrameLogEntry entry)
    {
        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }
    }

    public IReadOnlyList<FrameLogEntry> Latest(int count)
    {
        lock (_gate)
            return _entries.Skip(Math.Max(0, _entries.Count - count)).ToArray();
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }
}
