using PlcSim.Slmp.Core.Model;

namespace PlcSim.Slmp.Core.Engine;

/// <summary>アクセス種別。</summary>
public enum AccessKind
{
    Read,
    Write,
}

/// <summary>
/// デバイス・バッファ・ラベルへの 1 回のアクセス。
/// <see cref="Area"/> は表示領域のキー（デバイス名 "D"/"LTN"、"U0\G"、"J1\W"、"自局"、"ラベル"）。
/// </summary>
public sealed record AccessRecord(
    DateTime Time,
    AccessKind Kind,
    string Area,
    uint Start,
    int Count,
    string Display,
    string Source);

/// <summary>集約済みの直近アクセス（同じ範囲・種別・アクセス元の連続アクセスを回数にまとめる）。</summary>
public sealed class AccessSummary
{
    internal AccessSummary(AccessRecord first)
    {
        Kind = first.Kind;
        Area = first.Area;
        Start = first.Start;
        Count = first.Count;
        Display = first.Display;
        Source = first.Source;
        FirstTime = LastTime = first.Time;
        Times = 1;
    }

    public AccessKind Kind { get; }

    public string Area { get; }

    public uint Start { get; }

    public int Count { get; }

    public string Display { get; }

    public string Source { get; }

    public DateTime FirstTime { get; }

    public DateTime LastTime { get; internal set; }

    public long Times { get; internal set; }

    internal bool SameAs(AccessRecord r)
        => r.Kind == Kind && r.Area == Area && r.Start == Start && r.Count == Count && r.Source == Source;
}

/// <summary>
/// 直近アクセスの一覧。SLMP の読出し・書込み（エンジン通知）と、API・スクリプト・GUI の書込み（メモリ変更通知）を記録する。
/// </summary>
public sealed class AccessLog
{
    private readonly object _gate = new();
    private readonly CpuProfile _profile;
    private readonly LinkedList<AccessSummary> _items = new();
    private readonly List<(string Area, uint Start, int Count, DateTime Time)> _writes = new();

    public AccessLog(SlmpEngine engine, int capacity = 500)
    {
        Capacity = capacity;
        _profile = engine.Plc.Profile;
        engine.Accessed += Add;
        engine.Plc.Memory.Changed += OnChanged;
        engine.Plc.BufferChanged += OnChanged;
        engine.Plc.Labels.Changed += (path, source) =>
        {
            if (!source.StartsWith("SLMP", StringComparison.Ordinal))
                Add(new AccessRecord(DateTime.Now, AccessKind.Write, "ラベル", 0, 1, path, source));
        };
    }

    public int Capacity { get; }

    /// <summary>まとめる対象を探す直近件数。</summary>
    public int MergeWindow { get; set; } = 50;

    /// <summary>新しい順の直近アクセス。</summary>
    public IReadOnlyList<AccessSummary> Latest(int count = 200)
    {
        lock (_gate)
            return _items.Take(count).ToArray();
    }

    /// <summary>指定領域の書込み位置（範囲バー表示用、新しい順）。</summary>
    public IReadOnlyList<(uint Start, int Count, DateTime Time)> WritesIn(string area)
    {
        lock (_gate)
        {
            return _writes.Where(w => string.Equals(w.Area, area, StringComparison.OrdinalIgnoreCase))
                .Select(static w => (w.Start, w.Count, w.Time))
                .Reverse()
                .ToArray();
        }
    }

    /// <summary>指定番地の最終書込み（なければ null）。</summary>
    public (DateTime Time, string Source)? LastWrite(string area, uint number)
    {
        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (item.Kind == AccessKind.Write &&
                    string.Equals(item.Area, area, StringComparison.OrdinalIgnoreCase) &&
                    number >= item.Start && number < item.Start + (uint)Math.Max(item.Count, 1))
                {
                    return (item.LastTime, item.Source);
                }
            }
        }

        return null;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            _writes.Clear();
        }
    }

    public void Add(AccessRecord record)
    {
        lock (_gate)
        {
            // ポーリングのように同じ範囲を繰り返すアクセスは、直近の同じ項目にまとめて先頭へ移す。
            var node = _items.First;
            for (var i = 0; node is not null && i < MergeWindow; i++, node = node.Next)
            {
                if (node.Value.SameAs(record))
                    break;
            }

            if (node is not null && node.Value.SameAs(record))
            {
                node.Value.LastTime = record.Time;
                node.Value.Times++;
                _items.Remove(node);
                _items.AddFirst(node);
            }
            else
            {
                _items.AddFirst(new AccessSummary(record));
                while (_items.Count > Capacity)
                    _items.RemoveLast();
            }

            if (record.Kind == AccessKind.Write)
            {
                _writes.Add((record.Area, record.Start, record.Count, record.Time));
                if (_writes.Count > Capacity * 4)
                    _writes.RemoveRange(0, _writes.Count - (Capacity * 4));
            }
        }
    }

    private void OnChanged(MemoryChange change)
    {
        // SLMP の書込みはエンジン側で記録済み。
        if (change.Source.StartsWith("SLMP", StringComparison.Ordinal))
            return;
        // "U0\G" / "J1\W" は修飾子の後ろのデバイス名で基数を決める（リンクダイレクトの W/X 等は 16 進）。
        var def = DeviceCatalog.FindByName(change.Device.Contains('\\') ? change.Device[(change.Device.IndexOf('\\') + 1)..] : change.Device);
        var number = def is null ? change.Start.ToString(System.Globalization.CultureInfo.InvariantCulture) : MemoryDeviceAccessor.FormatNumber(_profile, def, change.Start);
        Add(new AccessRecord(change.Time, AccessKind.Write, change.Device, change.Start, change.Count, $"{change.Device}{number} ×{change.Count}", change.Source));
    }
}
