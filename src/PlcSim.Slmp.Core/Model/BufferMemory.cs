using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Model;

/// <summary>番地 → ワードの疎な記憶域（未書込み番地は 0）。</summary>
public sealed class SparseWordStore
{
    private readonly Dictionary<uint, ushort> _words = new();

    public SparseWordStore(uint size)
    {
        Size = size;
    }

    /// <summary>有効ワード数（番地 0〜Size-1）。</summary>
    public uint Size { get; }

    public IReadOnlyDictionary<uint, ushort> NonZeroWords => _words;

    public ushort[] Read(uint start, int count)
    {
        EnsureRange(start, count);
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
            result[i] = _words.TryGetValue(start + (uint)i, out var v) ? v : (ushort)0;
        return result;
    }

    public void Write(uint start, IReadOnlyList<ushort> values)
    {
        EnsureRange(start, values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var address = start + (uint)i;
            if (values[i] == 0)
                _words.Remove(address);
            else
                _words[address] = values[i];
        }
    }

    public bool ReadBit(uint bitAddress) => (Read(bitAddress / 16, 1)[0] & (1 << (int)(bitAddress % 16))) != 0;

    public void WriteBit(uint bitAddress, bool value)
    {
        var word = Read(bitAddress / 16, 1)[0];
        var mask = (ushort)(1 << (int)(bitAddress % 16));
        Write(bitAddress / 16, new[] { value ? (ushort)(word | mask) : (ushort)(word & ~mask) });
    }

    /// <summary>バイト番地での読出し（ワードはリトルエンディアン）。</summary>
    public byte[] ReadBytes(uint byteAddress, int count)
    {
        if (count > 0 && ((ulong)byteAddress + (ulong)count - 1) / 2 >= Size)
            throw new SlmpEndCodeException(EndCodes.DeviceOutOfRange, $"バイト番地 {byteAddress:X} から {count} バイトは範囲外です（{Size} ワード）");
        var result = new byte[count];
        for (var i = 0; i < count; i++)
        {
            var address = byteAddress + (uint)i;
            var word = Read(address / 2, 1)[0];
            result[i] = (address & 1) == 0 ? (byte)word : (byte)(word >> 8);
        }

        return result;
    }

    public void WriteBytes(uint byteAddress, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;
        // 全体の範囲を先に確認する（範囲外なら何も書かない）
        var lastWord = ((ulong)byteAddress + (ulong)data.Length - 1) / 2;
        if (lastWord >= Size)
            throw new SlmpEndCodeException(EndCodes.DeviceOutOfRange, $"バイト番地 {byteAddress:X} から {data.Length} バイトは範囲外です（{Size} ワード）");
        for (var i = 0; i < data.Length; i++)
        {
            var address = byteAddress + (uint)i;
            var word = Read(address / 2, 1)[0];
            word = (address & 1) == 0
                ? (ushort)((word & 0xFF00) | data[i])
                : (ushort)((word & 0x00FF) | (data[i] << 8));
            Write(address / 2, new[] { word });
        }
    }

    public void Clear() => _words.Clear();

    private void EnsureRange(uint start, int count)
    {
        if (count < 0 || (ulong)start + (ulong)count > Size)
            throw new SlmpEndCodeException(EndCodes.DeviceOutOfRange, $"番地 {start} から {count} ワードは範囲外です（{Size} ワード）");
    }
}

/// <summary>
/// デバイスメモリ以外の記憶域。
/// ・ユニットバッファメモリ Un\G（U3E0〜U3E3 は CPU バッファメモリ）。拡張ユニット読出し 0601/1601 と共有。
/// ・CPU バッファメモリ定周期エリア U3En\HG。
/// ・リンクダイレクト Jn\X/Y/B/SB/W/SW（ネットワーク番号ごと）。
/// ・自局メモリ（0613/1613）。
/// </summary>
public sealed class BufferMemory
{
    /// <summary>1 ユニットあたりのバッファメモリワード数。</summary>
    public const uint ModuleBufferWords = 0x10_0000;

    /// <summary>リンクダイレクトの 1 デバイスあたりのワード数（W/SW のワード数、X/Y/B/SB はビット数/16）。</summary>
    public const uint LinkDirectWords = 0x10_0000;

    /// <summary>自局メモリ（0613/1613）のワード数。</summary>
    public const uint OwnMemoryWords = 0x10_0000;

    private readonly Dictionary<ushort, SparseWordStore> _modules = new();
    private readonly Dictionary<ushort, SparseWordStore> _hg = new();
    private readonly Dictionary<(byte Network, string Device), SparseWordStore> _link = new();

    public SparseWordStore OwnMemory { get; } = new(OwnMemoryWords);

    public IReadOnlyDictionary<ushort, SparseWordStore> Modules => _modules;

    public IReadOnlyDictionary<ushort, SparseWordStore> CpuBufferFixedCycle => _hg;

    public IReadOnlyDictionary<(byte Network, string Device), SparseWordStore> LinkDirect => _link;

    /// <summary>ユニット番号（先頭入出力番号の上位 3 桁）のバッファメモリ。</summary>
    public SparseWordStore Module(ushort unit)
    {
        if (!_modules.TryGetValue(unit, out var store))
            _modules[unit] = store = new SparseWordStore(ModuleBufferWords);
        return store;
    }

    public SparseWordStore Hg(ushort unit)
    {
        if (!_hg.TryGetValue(unit, out var store))
            _hg[unit] = store = new SparseWordStore(ModuleBufferWords);
        return store;
    }

    public SparseWordStore Link(byte network, string device)
    {
        var key = (network, device.ToUpperInvariant());
        if (!_link.TryGetValue(key, out var store))
            _link[key] = store = new SparseWordStore(LinkDirectWords);
        return store;
    }
}
