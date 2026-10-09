using System.Globalization;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Control;

/// <summary>
/// 仮想メモリの閲覧・変更 API（スクリプト・HTTP API・コンソール・GUI 共通）。
/// SLMP 要求と同じロックで読み書きし、変更通知の書込み元に <c>source</c> を記録する。
/// </summary>
public sealed class PlcController
{
    public PlcController(PlcModel plc)
    {
        Plc = plc;
    }

    public PlcModel Plc { get; }

    public DeviceAddress Parse(string address) => DeviceAddress.Parse(address, Plc.Profile);

    public ushort[] ReadWords(string address, int count)
    {
        lock (Plc.SyncRoot)
        {
            Plc.RefreshSpecialDevices();
            return Accessor(Parse(address)).ReadWords(count);
        }
    }

    public void WriteWords(string address, IReadOnlyList<ushort> values, string source = "API")
    {
        lock (Plc.SyncRoot)
            Accessor(Parse(address)).WriteWords(values, source);
    }

    public bool[] ReadBits(string address, int count)
    {
        lock (Plc.SyncRoot)
        {
            Plc.RefreshSpecialDevices();
            return Accessor(Parse(address)).ReadBits(count);
        }
    }

    public void WriteBits(string address, IReadOnlyList<bool> values, string source = "API")
    {
        lock (Plc.SyncRoot)
            Accessor(Parse(address)).WriteBits(values, source);
    }

    public bool GetBit(string address) => ReadBits(address, 1)[0];

    public void SetBit(string address, bool value, string source = "API") => WriteBits(address, new[] { value }, source);

    public ushort GetWord(string address)
    {
        lock (Plc.SyncRoot)
        {
            Plc.RefreshSpecialDevices();
            return Accessor(Parse(address)).ReadWordPoint();
        }
    }

    public void SetWord(string address, ushort value, string source = "API")
    {
        lock (Plc.SyncRoot)
            Accessor(Parse(address)).WriteWordPoint(value, source);
    }

    public uint GetDWord(string address)
    {
        lock (Plc.SyncRoot)
        {
            Plc.RefreshSpecialDevices();
            return Accessor(Parse(address)).ReadDWordPoint();
        }
    }

    public void SetDWord(string address, uint value, string source = "API")
    {
        lock (Plc.SyncRoot)
            Accessor(Parse(address)).WriteDWordPoint(value, source);
    }

    public float GetFloat(string address) => BitConverter.Int32BitsToSingle(unchecked((int)GetDWord(address)));

    public void SetFloat(string address, float value, string source = "API")
        => SetDWord(address, unchecked((uint)BitConverter.SingleToInt32Bits(value)), source);

    /// <summary>ワードデバイスに ASCII 文字列を書く（1 ワード 2 文字、下位バイトが先）。</summary>
    public void SetString(string address, string text, int maxWords, string source = "API")
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        var words = new ushort[maxWords];
        for (var i = 0; i < Math.Min(bytes.Length, maxWords * 2); i++)
            words[i / 2] |= (ushort)(bytes[i] << ((i % 2) * 8));
        WriteWords(address, words, source);
    }

    public string GetString(string address, int words)
    {
        var raw = ReadWords(address, words).SelectMany(static w => new[] { (byte)w, (byte)(w >> 8) }).ToArray();
        var end = Array.IndexOf(raw, (byte)0);
        return System.Text.Encoding.ASCII.GetString(raw, 0, end < 0 ? raw.Length : end);
    }

    /// <summary>"0x1234" / "1234" / "-5" / "1.5f" / "true" を型付きで書き込む。</summary>
    public void WriteValue(string address, string value, string type, string source = "API")
    {
        switch (type.ToUpperInvariant())
        {
            case "BIT":
                SetBit(address, value is "1" or "true" or "True" or "ON" or "on", source);
                break;
            case "U16":
            case "S16":
                SetWord(address, unchecked((ushort)ParseInteger(value)), source);
                break;
            case "U32":
            case "S32":
                SetDWord(address, unchecked((uint)ParseInteger(value)), source);
                break;
            case "F32":
                SetFloat(address, float.Parse(value.TrimEnd('f', 'F'), CultureInfo.InvariantCulture), source);
                break;
            default:
                throw new ArgumentException($"型 '{type}' は BIT/U16/S16/U32/S32/F32 で指定してください");
        }
    }

    public void SetState(PlcRunState state, string source = "API") => Plc.SetState(state, source);

    public IReadOnlyList<LabelValueView> Labels()
    {
        lock (Plc.SyncRoot)
            return Plc.Labels.Snapshot();
    }

    public byte[] ReadLabel(string label)
    {
        lock (Plc.SyncRoot)
        {
            var slot = Plc.Labels.Resolve(label);
            return Plc.Labels.Read(slot, slot.Size);
        }
    }

    public void WriteLabel(string label, byte[] data, string source = "API")
    {
        lock (Plc.SyncRoot)
        {
            var slot = Plc.Labels.Resolve(label);
            if (data.Length != slot.Size)
                throw new ArgumentException($"{slot.Path} は {slot.Size} バイトです（指定 {data.Length} バイト）");
            Plc.Labels.Write(slot, data, source);
        }
    }

    /// <summary>アドレスを読み書き対象に解決する。ロック下で呼ぶ。</summary>
    public DeviceAccessor Accessor(DeviceAddress address)
    {
        var def = address.Device;
        if (address.Qualifier is null)
        {
            if (!Plc.Memory.Supports(def))
                throw new SlmpEndCodeException(EndCodes.DeviceOutOfRange, $"{def.Name} はこの機種にありません");
            return new MemoryDeviceAccessor(Plc.Memory, def, address.Number);
        }

        var qualifierNumber = Convert.ToUInt16(address.Qualifier[1..], address.Qualifier[0] == 'U' ? 16 : 10);
        if (address.Qualifier[0] == 'J' && !Plc.Settings.HasNetwork((byte)qualifierNumber))
            throw new SlmpEndCodeException(EndCodes.UnitNotFound, $"ネットワーク {address.Qualifier} は登録されていません");
        if (address.Qualifier[0] == 'U' && !Plc.Settings.HasUnit(qualifierNumber))
            throw new SlmpEndCodeException(EndCodes.UnitNotFound, $"ユニット {address.Qualifier} は装着されていません");
        if (address.Qualifier[0] == 'J')
            return new StoreDeviceAccessor(Plc.Buffers.Link((byte)qualifierNumber, def.Name), def, address.Number, address.Qualifier, Plc.RaiseBufferChanged);
        if (def.Name == "HG")
            return new StoreDeviceAccessor(Plc.Buffers.Hg(qualifierNumber), def, address.Number, address.Qualifier, Plc.RaiseBufferChanged);
        if (def.Name == "G")
            return new StoreDeviceAccessor(Plc.Buffers.Module(qualifierNumber), def, address.Number, address.Qualifier, Plc.RaiseBufferChanged);
        throw new FormatException($"{address.Qualifier}\\ には G/HG を指定してください");
    }

    private static long ParseInteger(string text)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
