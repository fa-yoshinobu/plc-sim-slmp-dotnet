using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PlcSim.Slmp.Core.Model;

namespace PlcSim.Slmp.Core.Protocol;

/// <summary>要求データ部のデバイス指定（拡張指定を含む）。</summary>
/// <param name="Device">デバイス定義。</param>
/// <param name="Number">デバイス番号（インデックス修飾適用前）。</param>
/// <param name="DirectMemory">拡張指定の直接メモリ指定（00h: なし, F8h: Un\G, F9h: Jn\, FAh: U3En\HG）。</param>
/// <param name="Extension">拡張指定値（ユニット番号 / ネットワーク番号）。</param>
/// <param name="IndexFlags">デバイス修飾フラグ（40h: Z, 80h: LZ, 08h: 間接）。</param>
/// <param name="IndexNumber">修飾に使うインデックスレジスタ番号。</param>
public sealed record DeviceSpec(
    DeviceDefinition Device,
    uint Number,
    byte DirectMemory = 0,
    ushort Extension = 0,
    byte IndexFlags = 0,
    byte IndexNumber = 0)
{
    public bool IsLinkDirect => DirectMemory == 0xF9;

    public bool IsModuleBuffer => DirectMemory == 0xF8;

    public bool IsHg => DirectMemory == 0xFA;
}

/// <summary>データコード（バイナリ / ASCII）に応じて要求データ部を読む。不足・余剰は C061 で拒否する。</summary>
public sealed class PayloadReader
{
    private readonly byte[] _data;
    private readonly bool _ascii;
    private int _offset;

    public PayloadReader(SlmpRequest request)
    {
        _data = request.Data;
        _ascii = request.IsAscii;
    }

    public bool IsAscii => _ascii;

    /// <summary>データ長の誤りの終了コード（実機計測: ASCII は C058、バイナリは C061）。</summary>
    private ushort LengthErrorCode => _ascii ? EndCodes.DataLengthMismatch : EndCodes.BinaryDataLengthMismatch;

    public int Remaining => _data.Length - _offset;

    public byte ReadU8() => _ascii ? (byte)ReadHex(2) : Take(1)[0];

    public ushort ReadU16() => _ascii ? (ushort)ReadHex(4) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint ReadU32() => _ascii ? (uint)ReadHex(8) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    /// <summary>バイト列（ASCII では 16 進 2 文字で 1 バイト）。</summary>
    public byte[] ReadBytes(int count)
    {
        if (!_ascii)
            return Take(count).ToArray();
        var result = new byte[count];
        for (var i = 0; i < count; i++)
            result[i] = (byte)ReadHex(2);
        return result;
    }

    /// <summary>符号化しない生バイト（折返しデータなど）。</summary>
    public byte[] ReadRaw(int count) => Take(count).ToArray();

    /// <summary>ビット単位の書込みデータ。バイナリは 1 バイトに 2 点（上位ニブルが先）、ASCII は 1 文字 1 点。</summary>
    public bool[] ReadPackedBits(int points)
    {
        var result = new bool[points];
        if (_ascii)
        {
            var raw = Take(points);
            for (var i = 0; i < points; i++)
            {
                result[i] = raw[i] switch
                {
                    (byte)'0' => false,
                    (byte)'1' => true,
                    _ => throw new SlmpEndCodeException(EndCodes.AsciiConversionError, "ビット値は '0' / '1' で指定してください"),
                };
            }

            return result;
        }

        var bytes = Take((points + 1) / 2);
        for (var i = 0; i < points; i++)
        {
            var nibble = (i % 2 == 0 ? bytes[i / 2] >> 4 : bytes[i / 2]) & 0x0F;
            // 実機計測（iQ-R 系）: 0 / 1 以外の値は C060
            if (nibble > 1)
                throw new SlmpEndCodeException(EndCodes.BinaryBitValueError, $"ビット値 {nibble:X} は 0 / 1 で指定してください");
            result[i] = nibble == 1;
        }

        return result;
    }

    /// <summary>
    /// デバイス指定を読む。
    /// バイナリ: Q 形式 = 番号 3 バイト + コード 1 バイト、iQ-R 形式 = 番号 4 バイト + コード 2 バイト。
    /// 拡張指定: 修飾番号, 修飾フラグ, (デバイス指定), 拡張修飾, 00, 拡張指定 2 バイト, 直接メモリ指定（plc-comm-slmp-dotnet の実機検証済み配置）。
    /// ASCII: Q 形式 = コード 2 文字 + 番号 6 桁、iQ-R 形式 = コード 4 文字 + 番号 8 桁。
    /// </summary>
    public DeviceSpec ReadDeviceSpec(bool iqrFormat, bool extended, CpuProfile profile)
    {
        if (_ascii)
        {
            if (extended)
                throw new SlmpEndCodeException(EndCodes.CommandSubcommandError, "ASCII の拡張指定は未対応です");
            return ReadAsciiDeviceSpec(iqrFormat, profile);
        }

        if (!extended)
            return ReadBinaryDevice(iqrFormat);

        var indexNumber = Take(1)[0];
        var indexFlags = Take(1)[0];
        var device = ReadBinaryDevice(iqrFormat);
        _ = Take(2); // 拡張指定修飾, 予約
        var extension = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        var directMemory = Take(1)[0];
        return device with
        {
            DirectMemory = directMemory,
            Extension = extension,
            IndexFlags = indexFlags,
            IndexNumber = indexNumber,
        };
    }

    /// <summary>データ長の確認のあとで返すエラー（実機はデータ長の誤りを先に返す）。</summary>
    public SlmpEndCodeException? DeferredError { get; set; }

    public void EnsureEnd()
    {
        if (_offset != _data.Length)
            throw new SlmpEndCodeException(LengthErrorCode, $"要求データに余分な {_data.Length - _offset} バイトがあります");
        if (DeferredError is { } deferred)
            throw deferred;
    }

    private DeviceSpec ReadBinaryDevice(bool iqrFormat)
    {
        uint number;
        ushort code;
        if (iqrFormat)
        {
            var raw = Take(6);
            number = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            code = BinaryPrimitives.ReadUInt16LittleEndian(raw[4..]);
        }
        else
        {
            var raw = Take(4);
            number = (uint)(raw[0] | (raw[1] << 8) | (raw[2] << 16));
            code = raw[3];
        }

        var def = DeviceCatalog.FindByCode(code, iqrFormat)
            ?? throw new SlmpEndCodeException(EndCodes.DeviceAccessNotAllowed, $"未知のデバイスコード 0x{code:X4}");
        return new DeviceSpec(def, number);
    }

    private DeviceSpec ReadAsciiDeviceSpec(bool iqrFormat, CpuProfile profile)
    {
        var codeLength = iqrFormat ? 4 : 2;
        var numberLength = iqrFormat ? 8 : 6;
        var token = Encoding.ASCII.GetString(Take(codeLength));
        var def = DeviceCatalog.FindByAscii(token, iqrFormat)
            ?? throw new SlmpEndCodeException(EndCodes.DeviceAccessNotAllowed, $"未知の ASCII デバイスコード '{token}'");
        var text = Encoding.ASCII.GetString(Take(numberLength)).Trim();
        var radix = (int)profile.RadixOf(def);
        try
        {
            var number = text.Length == 0 ? 0u : radix switch
            {
                16 => uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                8 => Convert.ToUInt32(text, 8),
                _ => uint.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture),
            };
            return new DeviceSpec(def, number);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new SlmpEndCodeException(EndCodes.AsciiConversionError, $"デバイス番号 '{text}' を解釈できません");
        }
    }

    private int ReadHex(int digits)
    {
        var raw = Take(digits);
        var value = 0;
        foreach (var c in raw)
        {
            var d = FrameCodec.HexDigit(c);
            if (d < 0)
                throw new SlmpEndCodeException(EndCodes.AsciiConversionError, $"ASCII 16進でない文字 0x{c:X2}");
            value = (value << 4) | d;
        }

        return value;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _offset + count > _data.Length)
            throw new SlmpEndCodeException(LengthErrorCode, "要求データが不足しています");
        var span = _data.AsSpan(_offset, count);
        _offset += count;
        return span;
    }
}

/// <summary>データコードに応じて応答データを書く。</summary>
public sealed class PayloadWriter
{
    private readonly List<byte> _data = new();
    private readonly bool _ascii;

    public PayloadWriter(SlmpRequest request)
    {
        _ascii = request.IsAscii;
    }

    public void WriteU8(byte value)
    {
        if (_ascii)
            WriteAscii(value.ToString("X2", CultureInfo.InvariantCulture));
        else
            _data.Add(value);
    }

    public void WriteU16(ushort value)
    {
        if (_ascii)
        {
            WriteAscii(value.ToString("X4", CultureInfo.InvariantCulture));
            return;
        }

        _data.Add((byte)value);
        _data.Add((byte)(value >> 8));
    }

    public void WriteU32(uint value)
    {
        if (_ascii)
        {
            WriteAscii(value.ToString("X8", CultureInfo.InvariantCulture));
            return;
        }

        WriteU16((ushort)value);
        WriteU16((ushort)(value >> 16));
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (_ascii)
            WriteAscii(Convert.ToHexString(bytes));
        else
            _data.AddRange(bytes.ToArray());
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes) => _data.AddRange(bytes.ToArray());

    /// <summary>ビット単位の読出しデータ。バイナリは 1 バイトに 2 点、ASCII は 1 文字 1 点。</summary>
    public void WritePackedBits(IReadOnlyList<bool> bits)
    {
        if (_ascii)
        {
            foreach (var bit in bits)
                _data.Add(bit ? (byte)'1' : (byte)'0');
            return;
        }

        for (var i = 0; i < bits.Count; i += 2)
        {
            var high = bits[i] ? 0x10 : 0x00;
            var low = i + 1 < bits.Count && bits[i + 1] ? 0x01 : 0x00;
            _data.Add((byte)(high | low));
        }
    }

    public byte[] ToArray() => _data.ToArray();

    private void WriteAscii(string text) => _data.AddRange(Encoding.ASCII.GetBytes(text));
}
