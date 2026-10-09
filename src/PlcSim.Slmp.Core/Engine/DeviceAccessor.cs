using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Engine;

/// <summary>デバイス指定を解決した読み書き対象。通常デバイスとバッファメモリ/リンクダイレクトを同じ操作で扱う。</summary>
public abstract class DeviceAccessor
{
    protected DeviceAccessor(DeviceDefinition device, uint number, string displayName)
    {
        Device = device;
        Number = number;
        DisplayName = displayName;
    }

    public DeviceDefinition Device { get; }

    /// <summary>インデックス修飾適用後の先頭番号。</summary>
    public uint Number { get; }

    /// <summary>表示名（例: D100, U0\G10, J1\W10）。</summary>
    public string DisplayName { get; }

    /// <summary>アクセス記録の領域キー（"D", "LTN", "U0\G", "J1\W"）。</summary>
    public virtual string AreaKey => Device.Name;

    /// <summary>通常デバイス（拡張指定なし相当）か。</summary>
    public virtual bool IsPlainDevice => false;

    public abstract ushort[] ReadWords(int count);

    public abstract void WriteWords(IReadOnlyList<ushort> values, string source);

    public abstract bool[] ReadBits(int count);

    public abstract void WriteBits(IReadOnlyList<bool> values, string source);

    public abstract ushort ReadWordPoint();

    public abstract uint ReadDWordPoint();

    public abstract void WriteWordPoint(ushort value, string source);

    public abstract void WriteDWordPoint(uint value, string source);
}

/// <summary>デバイスメモリ上のデバイス。</summary>
public sealed class MemoryDeviceAccessor : DeviceAccessor
{
    private readonly DeviceMemory _memory;

    public MemoryDeviceAccessor(DeviceMemory memory, DeviceDefinition device, uint number)
        : base(device, number, $"{device.Name}{FormatNumber(memory.Profile, device, number)}")
    {
        _memory = memory;
    }

    public override bool IsPlainDevice => true;

    public override ushort[] ReadWords(int count) => _memory.ReadWords(Device, Number, count);

    public override void WriteWords(IReadOnlyList<ushort> values, string source) => _memory.WriteWords(Device, Number, values, source);

    public override bool[] ReadBits(int count) => _memory.ReadBits(Device, Number, count);

    public override void WriteBits(IReadOnlyList<bool> values, string source) => _memory.WriteBits(Device, Number, values, source);

    public override ushort ReadWordPoint() => _memory.ReadWordPoint(Device, Number);

    public override uint ReadDWordPoint() => _memory.ReadDWordPoint(Device, Number);

    public override void WriteWordPoint(ushort value, string source) => _memory.WriteWordPoint(Device, Number, value, source);

    public override void WriteDWordPoint(uint value, string source) => _memory.WriteDWordPoint(Device, Number, value, source);

    public static string FormatNumber(CpuProfile profile, DeviceDefinition device, uint number) => profile.RadixOf(device) switch
    {
        DeviceRadix.Hex => number.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
        DeviceRadix.Octal => Convert.ToString(number, 8),
        _ => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>疎な記憶域上のデバイス（Un\G, U3En\HG, Jn\X/Y/B/SB/W/SW）。ビットデバイスは 16 点を 1 ワードに格納する。</summary>
public sealed class StoreDeviceAccessor : DeviceAccessor
{
    private readonly SparseWordStore _store;
    private readonly bool _bitDevice;
    private readonly Action<MemoryChange> _notify;

    public StoreDeviceAccessor(SparseWordStore store, DeviceDefinition device, uint number, string prefix, Action<MemoryChange> notify)
        : base(device, number, $"{prefix}\\{device.Name}{(device.Radix == DeviceRadix.Hex ? number.ToString("X", System.Globalization.CultureInfo.InvariantCulture) : number.ToString(System.Globalization.CultureInfo.InvariantCulture))}")
    {
        _store = store;
        _bitDevice = device.Kind == DeviceKind.Bit;
        _notify = notify;
        Prefix = prefix;
    }

    public string Prefix { get; }

    public override string AreaKey => $"{Prefix}\\{Device.Name}";

    public override ushort[] ReadWords(int count)
    {
        if (!_bitDevice)
            return _store.Read(Number, count);
        var result = new ushort[count];
        for (var w = 0; w < count; w++)
        {
            ushort value = 0;
            for (var b = 0; b < 16; b++)
            {
                if (_store.ReadBit(Number + (uint)(w * 16) + (uint)b))
                    value |= (ushort)(1 << b);
            }

            result[w] = value;
        }

        return result;
    }

    public override void WriteWords(IReadOnlyList<ushort> values, string source)
    {
        if (!_bitDevice)
        {
            _store.Write(Number, values);
            Notify(values.Count, source);
            return;
        }

        EnsureBitRange(values.Count * 16);
        for (var w = 0; w < values.Count; w++)
        {
            for (var b = 0; b < 16; b++)
                _store.WriteBit(Number + (uint)(w * 16) + (uint)b, (values[w] & (1 << b)) != 0);
        }

        Notify(values.Count * 16, source);
    }

    public override bool[] ReadBits(int count)
    {
        if (!_bitDevice)
            throw new SlmpEndCodeException(EndCodes.DeviceAccessNotAllowed, $"{DisplayName} はビット単位でアクセスできません");
        var result = new bool[count];
        for (var i = 0; i < count; i++)
            result[i] = _store.ReadBit(Number + (uint)i);
        return result;
    }

    public override void WriteBits(IReadOnlyList<bool> values, string source)
    {
        if (!_bitDevice)
            throw new SlmpEndCodeException(EndCodes.DeviceAccessNotAllowed, $"{DisplayName} はビット単位でアクセスできません");
        EnsureBitRange(values.Count);
        for (var i = 0; i < values.Count; i++)
            _store.WriteBit(Number + (uint)i, values[i]);
        Notify(values.Count, source);
    }

    /// <summary>書く前にビット範囲の最後まで確認する（範囲外なら何も書かない）。</summary>
    private void EnsureBitRange(int bitCount)
    {
        if (bitCount <= 0)
            return;
        var last = (ulong)Number + (ulong)bitCount - 1;
        if (last > uint.MaxValue)
            throw new SlmpEndCodeException(EndCodes.DeviceOutOfRange, $"{DisplayName} から {bitCount} 点は範囲外です");
        _ = _store.ReadBit((uint)last);
    }

    public override ushort ReadWordPoint() => ReadWords(1)[0];

    public override uint ReadDWordPoint()
    {
        var w = ReadWords(2);
        return (uint)(w[0] | (w[1] << 16));
    }

    public override void WriteWordPoint(ushort value, string source) => WriteWords(new[] { value }, source);

    public override void WriteDWordPoint(uint value, string source)
        => WriteWords(new[] { (ushort)value, (ushort)(value >> 16) }, source);

    private void Notify(int count, string source)
        => _notify(new MemoryChange($"{Prefix}\\{Device.Name}", Number, count, source, DateTime.Now));
}
