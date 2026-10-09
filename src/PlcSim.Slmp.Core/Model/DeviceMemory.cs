using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Model;

/// <summary>デバイスメモリ変更の通知内容。<see cref="Start"/>/<see cref="Count"/> はデバイス点単位。</summary>
public sealed record MemoryChange(string Device, uint Start, int Count, string Source, DateTime Time);

/// <summary>
/// 仮想 PLC のデバイスメモリ。プロファイルの点数どおりに確保し、範囲外は終了コードで拒否する。
/// 実機と同じ共有関係を再現する: DX/DY は X/Y、R は ZR 先頭ブロック、LTS/LTC は LTN 状態ワードのビット。
/// スレッド安全性は呼出し側（<see cref="PlcModel"/> のロック）で保証する。
/// </summary>
public sealed class DeviceMemory
{
    private readonly Dictionary<string, bool[]> _bits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ushort[]> _words = new(StringComparer.OrdinalIgnoreCase);

    public DeviceMemory(CpuProfile profile)
    {
        Profile = profile;
        foreach (var def in DeviceCatalog.All)
        {
            if (!profile.Has(def.Family) || def.Name is "G" or "HG")
                continue;
            var key = StorageKey(def);
            var points = profile.PointsOf(def.Family);
            switch (def.Kind)
            {
                case DeviceKind.Bit:
                    if (!_bits.ContainsKey(key))
                        _bits[key] = new bool[points];
                    break;
                case DeviceKind.Word:
                    if (!_words.ContainsKey(key))
                        _words[key] = new ushort[key == "ZR" ? Math.Max(points, profile.PointsOf("ZR")) : points];
                    break;
                case DeviceKind.DoubleWord:
                    _words[key] = new ushort[checked(points * 2)];
                    break;
                case DeviceKind.LongTimer:
                    _words[key] = new ushort[checked(points * 4)];
                    break;
            }
        }
    }

    public CpuProfile Profile { get; }

    /// <summary>書込み後に発生する。</summary>
    public event Action<MemoryChange>? Changed;

    /// <summary>この機種でデバイスが使えるか（G/HG はバッファメモリ側で扱う）。</summary>
    public bool Supports(DeviceDefinition def) => Profile.Has(def.Family) && def.Name is not ("G" or "HG");

    public uint PointsOf(DeviceDefinition def) => Profile.PointsOf(def.Family);

    /// <summary>ワード単位読出し（伝文上のワード数）。ビットデバイスは 16 点を 1 ワードに詰める。</summary>
    public ushort[] ReadWords(DeviceDefinition def, uint start, int count)
    {
        var result = new ushort[count];
        switch (def.Kind)
        {
            case DeviceKind.Bit:
                {
                    EnsureRange(def, start, (long)count * 16);
                    var bits = _bits[StorageKey(def)];
                    for (var w = 0; w < count; w++)
                    {
                        ushort value = 0;
                        for (var b = 0; b < 16; b++)
                        {
                            if (bits[start + (w * 16) + b])
                                value |= (ushort)(1 << b);
                        }

                        result[w] = value;
                    }

                    break;
                }

            case DeviceKind.Word:
                EnsureRange(def, start, count);
                Array.Copy(_words[StorageKey(def)], start, result, 0, count);
                break;
            case DeviceKind.DoubleWord:
                EnsureWireWords(def, start, count, 2);
                Array.Copy(_words[StorageKey(def)], start * 2, result, 0, count);
                break;
            case DeviceKind.LongTimer:
                EnsureWireWords(def, start, count, 4);
                Array.Copy(_words[StorageKey(def)], start * 4, result, 0, count);
                break;
            default:
                throw NotAccessible(def, "ワード単位");
        }

        return result;
    }

    /// <summary>ワード単位書込み（伝文上のワード数）。</summary>
    public void WriteWords(DeviceDefinition def, uint start, IReadOnlyList<ushort> values, string source)
    {
        var count = values.Count;
        int changedPoints;
        switch (def.Kind)
        {
            case DeviceKind.Bit:
                {
                    EnsureRange(def, start, (long)count * 16);
                    var bits = _bits[StorageKey(def)];
                    for (var w = 0; w < count; w++)
                    {
                        for (var b = 0; b < 16; b++)
                            bits[start + (w * 16) + b] = (values[w] & (1 << b)) != 0;
                    }

                    changedPoints = count * 16;
                    break;
                }

            case DeviceKind.Word:
                EnsureRange(def, start, count);
                CopyWords(values, _words[StorageKey(def)], start);
                changedPoints = count;
                break;
            case DeviceKind.DoubleWord:
                EnsureWireWords(def, start, count, 2);
                CopyWords(values, _words[StorageKey(def)], start * 2);
                changedPoints = (count + 1) / 2;
                break;
            case DeviceKind.LongTimer:
                EnsureWireWords(def, start, count, 4);
                CopyWords(values, _words[StorageKey(def)], start * 4);
                changedPoints = (count + 3) / 4;
                break;
            default:
                throw NotAccessible(def, "ワード単位");
        }

        Raise(def, start, changedPoints, source);
    }

    public bool[] ReadBits(DeviceDefinition def, uint start, int count)
    {
        EnsureRange(def, start, count);
        var result = new bool[count];
        switch (def.Kind)
        {
            case DeviceKind.Bit:
                Array.Copy(_bits[StorageKey(def)], start, result, 0, count);
                break;
            case DeviceKind.LongTimerBit:
                {
                    var words = _words[StorageKey(def)];
                    var mask = LongTimerMask(def);
                    for (var i = 0; i < count; i++)
                        result[i] = (words[((start + i) * 4) + 2] & mask) != 0;
                    break;
                }

            default:
                throw NotAccessible(def, "ビット単位");
        }

        return result;
    }

    public void WriteBits(DeviceDefinition def, uint start, IReadOnlyList<bool> values, string source)
    {
        EnsureRange(def, start, values.Count);
        switch (def.Kind)
        {
            case DeviceKind.Bit:
                {
                    var bits = _bits[StorageKey(def)];
                    for (var i = 0; i < values.Count; i++)
                        bits[start + i] = values[i];
                    break;
                }

            case DeviceKind.LongTimerBit:
                {
                    var words = _words[StorageKey(def)];
                    var mask = LongTimerMask(def);
                    for (var i = 0; i < values.Count; i++)
                    {
                        var index = ((start + i) * 4) + 2;
                        words[index] = values[i] ? (ushort)(words[index] | mask) : (ushort)(words[index] & ~mask);
                    }

                    break;
                }

            default:
                throw NotAccessible(def, "ビット単位");
        }

        Raise(def, start, values.Count, source);
    }

    /// <summary>ランダム/モニタのワード 1 点読出し。</summary>
    public ushort ReadWordPoint(DeviceDefinition def, uint number) => def.Kind switch
    {
        DeviceKind.DoubleWord or DeviceKind.LongTimer => (ushort)ReadDWordPoint(def, number),
        _ => ReadWords(def, number, 1)[0],
    };

    /// <summary>ランダム/モニタのダブルワード 1 点読出し。LTN/LCN/LZ は 1 点の 32 ビット値。</summary>
    public uint ReadDWordPoint(DeviceDefinition def, uint number)
    {
        switch (def.Kind)
        {
            case DeviceKind.DoubleWord:
            case DeviceKind.LongTimer:
                {
                    EnsureRange(def, number, 1);
                    var words = _words[StorageKey(def)];
                    var index = number * (def.Kind == DeviceKind.LongTimer ? 4u : 2u);
                    return (uint)(words[index] | (words[index + 1] << 16));
                }

            case DeviceKind.Bit:
                {
                    var w = ReadWords(def, number, 2);
                    return (uint)(w[0] | (w[1] << 16));
                }

            default:
                {
                    var w = ReadWords(def, number, 2);
                    return (uint)(w[0] | (w[1] << 16));
                }
        }
    }

    public void WriteWordPoint(DeviceDefinition def, uint number, ushort value, string source)
    {
        if (def.Kind is DeviceKind.DoubleWord or DeviceKind.LongTimer)
        {
            var current = ReadDWordPoint(def, number);
            WriteDWordPoint(def, number, (current & 0xFFFF0000u) | value, source);
            return;
        }

        WriteWords(def, number, new[] { value }, source);
    }

    public void WriteDWordPoint(DeviceDefinition def, uint number, uint value, string source)
    {
        if (def.Kind is DeviceKind.DoubleWord or DeviceKind.LongTimer)
        {
            EnsureRange(def, number, 1);
            var words = _words[StorageKey(def)];
            var index = number * (def.Kind == DeviceKind.LongTimer ? 4u : 2u);
            words[index] = (ushort)value;
            words[index + 1] = (ushort)(value >> 16);
            Raise(def, number, 1, source);
            return;
        }

        WriteWords(def, number, new[] { (ushort)value, (ushort)(value >> 16) }, source);
    }

    /// <summary>指定ファミリ群のデバイスを 0 クリアする（ラッチクリア、RUN 時クリア）。</summary>
    public void Clear(IEnumerable<string> families, string source)
    {
        var set = new HashSet<string>(families, StringComparer.OrdinalIgnoreCase);
        foreach (var def in DeviceCatalog.All)
        {
            if (!set.Contains(def.Family) || !Supports(def))
                continue;
            var key = StorageKey(def);
            if (_bits.TryGetValue(key, out var bits))
                Array.Clear(bits);
            if (_words.TryGetValue(key, out var words))
                Array.Clear(words);
        }

        foreach (var family in set)
            Changed?.Invoke(new MemoryChange(family, 0, 0, source, DateTime.Now));
    }

    /// <summary>格納キー → ワード記憶域（閲覧・スナップショット用）。</summary>
    public IReadOnlyDictionary<string, ushort[]> WordAreas => _words;

    /// <summary>格納キー → ビット記憶域（閲覧・スナップショット用）。</summary>
    public IReadOnlyDictionary<string, bool[]> BitAreas => _bits;

    /// <summary>システム管理値（SD/SM）を通知なしで更新する。範囲外は無視する。</summary>
    internal void SetSystemWord(string device, uint number, ushort value)
    {
        if (_words.TryGetValue(device, out var words) && number < words.Length)
            words[number] = value;
    }

    internal void SetSystemBit(string device, uint number, bool value)
    {
        if (_bits.TryGetValue(device, out var bits) && number < bits.Length)
            bits[number] = value;
    }

    /// <summary>実体の格納キー（共有関係の解決）。</summary>
    public string StorageKey(DeviceDefinition def) => def.Name switch
    {
        "DX" => "X",
        "DY" => "Y",
        "R" when Profile.FileRegisterShared => "ZR",
        "LTS" or "LTC" => "LTN",
        "LSTS" or "LSTC" => "LSTN",
        _ => def.Name,
    };

    /// <summary>範囲チェック（デバイス点単位）。</summary>
    public void EnsureRange(DeviceDefinition def, uint start, long count)
    {
        if (!Supports(def))
            throw new SlmpEndCodeException(EndCodes.DeviceOutOfRange, $"{def.Name} はこの機種にありません");
        var points = PointsOf(def);
        if (count < 0 || start + count > points)
            throw new SlmpEndCodeException(
                EndCodes.DeviceOutOfRange,
                $"{def.Name}{start} から {count} 点は範囲外です（点数 {points}）");
    }

    private void EnsureWireWords(DeviceDefinition def, uint start, int words, int wordsPerPoint)
    {
        if (!Supports(def))
            throw new SlmpEndCodeException(EndCodes.DeviceOutOfRange, $"{def.Name} はこの機種にありません");
        var total = (long)PointsOf(def) * wordsPerPoint;
        if (((long)start * wordsPerPoint) + words > total)
            throw new SlmpEndCodeException(EndCodes.DeviceOutOfRange, $"{def.Name}{start} から {words} ワードは範囲外です");
    }

    private static ushort LongTimerMask(DeviceDefinition def)
        => def.Name is "LTC" or "LSTC" ? (ushort)0x0001 : (ushort)0x0002;

    private static void CopyWords(IReadOnlyList<ushort> values, ushort[] target, long offset)
    {
        for (var i = 0; i < values.Count; i++)
            target[offset + i] = values[i];
    }

    private static SlmpEndCodeException NotAccessible(DeviceDefinition def, string unit)
        => new(EndCodes.DeviceAccessNotAllowed, $"{def.Name} は{unit}でアクセスできません");

    private void Raise(DeviceDefinition def, uint start, int count, string source)
        => Changed?.Invoke(new MemoryChange(def.Name, start, count, source, DateTime.Now));
}
