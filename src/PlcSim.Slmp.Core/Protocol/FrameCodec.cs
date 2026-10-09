using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PlcSim.Slmp.Core.Protocol;

/// <summary>3E/4E × バイナリ/ASCII の要求フレーム解析と応答フレーム生成。</summary>
public static class FrameCodec
{
    private const int Bin3EHeader = 9;
    private const int Bin4EHeader = 13;
    private const int Ascii3EHeader = 18;
    private const int Ascii4EHeader = 26;

    /// <summary>応答のヘッダ（サブヘッダ〜データ長）と終了コードの長さ。この後ろに応答データが続く。</summary>
    public static int ResponseOverhead(SlmpRequest request) => request.IsAscii
        ? (request.Frame == SlmpFrameKind.Frame4E ? Ascii4EHeader : Ascii3EHeader) + 4
        : (request.Frame == SlmpFrameKind.Frame4E ? Bin4EHeader : Bin3EHeader) + 2;

    /// <summary>バイナリの要求データ長の最小値（監視タイマ+コマンド+サブコマンド）。</summary>
    private const int BinMinBody = 6;

    /// <summary>ASCII の要求データ長の最小値。</summary>
    private const int AsciiMinBody = 12;

    /// <summary>
    /// 受信バッファ先頭から 1 フレームの長さを求める。
    /// データ不足なら <c>0</c>、既知のサブヘッダでなければ <see cref="SlmpFrameFormatException"/>。
    /// </summary>
    public static int GetFrameLength(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2)
            return 0;

        if (buffer[0] == 0x50 && buffer[1] == 0x00)
            return BinaryLength(buffer, Bin3EHeader, 7);
        if (buffer[0] == 0x54 && buffer[1] == 0x00)
            return BinaryLength(buffer, Bin4EHeader, 11);

        if (buffer.Length < 4)
            return IsAsciiPrefix(buffer) ? 0 : throw new SlmpFrameFormatException(UnknownHeader(buffer));
        if (AsciiEquals(buffer[..4], "5000"))
            return AsciiLength(buffer, Ascii3EHeader);
        if (AsciiEquals(buffer[..4], "5400"))
            return AsciiLength(buffer, Ascii4EHeader);

        throw new SlmpFrameFormatException(UnknownHeader(buffer));
    }

    /// <summary>1 フレーム分のバイト列を解析する。</summary>
    public static SlmpRequest Parse(byte[] frame)
    {
        var length = GetFrameLength(frame);
        if (length == 0 || length != frame.Length)
            throw new SlmpFrameFormatException($"フレーム長が不正です: 実長={frame.Length}, ヘッダ長={length}");

        if (frame[0] == 0x50 || frame[0] == 0x54)
        {
            var is4E = frame[0] == 0x54;
            var o = is4E ? 6 : 2;
            var serial = is4E ? BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2, 2)) : (ushort)0;
            var bodyStart = is4E ? Bin4EHeader : Bin3EHeader;
            return new SlmpRequest(
                is4E ? SlmpFrameKind.Frame4E : SlmpFrameKind.Frame3E,
                SlmpDataCode.Binary,
                serial,
                frame[o],
                frame[o + 1],
                BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(o + 2, 2)),
                frame[o + 4],
                BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(bodyStart, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(bodyStart + 2, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(bodyStart + 4, 2)),
                frame.AsSpan(bodyStart + 6).ToArray(),
                frame);
        }

        var ascii4E = AsciiEquals(frame.AsSpan(0, 4), "5400");
        var a = ascii4E ? 12 : 4;
        var body = ascii4E ? Ascii4EHeader : Ascii3EHeader;
        return new SlmpRequest(
            ascii4E ? SlmpFrameKind.Frame4E : SlmpFrameKind.Frame3E,
            SlmpDataCode.Ascii,
            ascii4E ? (ushort)Hex(frame, 4, 4) : (ushort)0,
            (byte)Hex(frame, a, 2),
            (byte)Hex(frame, a + 2, 2),
            (ushort)Hex(frame, a + 4, 4),
            (byte)Hex(frame, a + 8, 2),
            (ushort)Hex(frame, body, 4),
            (ushort)Hex(frame, body + 4, 4),
            (ushort)Hex(frame, body + 8, 4),
            frame.AsSpan(body + 12).ToArray(),
            frame);
    }

    /// <summary>応答フレームを生成する。<paramref name="data"/> は要求と同じデータコードで符号化済みの応答データ。</summary>
    public static byte[] BuildResponse(SlmpRequest request, ushort endCode, ReadOnlySpan<byte> data)
    {
        var withErrorInfo = endCode != EndCodes.Success;
        return request.IsAscii
            ? BuildAsciiResponse(request, endCode, withErrorInfo, data)
            : BuildBinaryResponse(request, endCode, withErrorInfo, data);
    }

    private static byte[] BuildBinaryResponse(SlmpRequest request, ushort endCode, bool withErrorInfo, ReadOnlySpan<byte> data)
    {
        var is4E = request.Frame == SlmpFrameKind.Frame4E;
        var header = is4E ? Bin4EHeader : Bin3EHeader;
        var payload = withErrorInfo ? 9 : data.Length;
        var frame = new byte[header + 2 + payload];
        var o = 0;
        if (is4E)
        {
            frame[0] = 0xD4;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), request.Serial);
            o = 6;
        }
        else
        {
            frame[0] = 0xD0;
            o = 2;
        }

        WriteRoute(frame.AsSpan(o), request);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(header - 2, 2), checked((ushort)(2 + payload)));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(header, 2), endCode);
        if (withErrorInfo)
        {
            var info = frame.AsSpan(header + 2);
            WriteRoute(info, request);
            BinaryPrimitives.WriteUInt16LittleEndian(info.Slice(5, 2), request.Command);
            BinaryPrimitives.WriteUInt16LittleEndian(info.Slice(7, 2), request.Subcommand);
        }
        else
        {
            data.CopyTo(frame.AsSpan(header + 2));
        }

        return frame;
    }

    private static byte[] BuildAsciiResponse(SlmpRequest request, ushort endCode, bool withErrorInfo, ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        var route = $"{request.Network:X2}{request.Station:X2}{request.ModuleIo:X4}{request.Multidrop:X2}";
        string body;
        if (withErrorInfo)
            body = $"{endCode:X4}{route}{request.Command:X4}{request.Subcommand:X4}";
        else
            body = $"{endCode:X4}{Encoding.ASCII.GetString(data)}";

        if (request.Frame == SlmpFrameKind.Frame4E)
            sb.Append("D400").Append(request.Serial.ToString("X4", CultureInfo.InvariantCulture)).Append("0000");
        else
            sb.Append("D000");
        sb.Append(route).Append(body.Length.ToString("X4", CultureInfo.InvariantCulture)).Append(body);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void WriteRoute(Span<byte> output, SlmpRequest request)
    {
        output[0] = request.Network;
        output[1] = request.Station;
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(2, 2), request.ModuleIo);
        output[4] = request.Multidrop;
    }

    private static int BinaryLength(ReadOnlySpan<byte> buffer, int headerSize, int lengthOffset)
    {
        if (buffer.Length < headerSize)
            return 0;
        var body = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(lengthOffset, 2));
        if (body < BinMinBody)
            throw new SlmpFrameFormatException($"要求データ長が短すぎます: {body}");
        return headerSize + body;
    }

    private static int AsciiLength(ReadOnlySpan<byte> buffer, int headerSize)
    {
        if (buffer.Length < headerSize)
            return 0;
        var body = Hex(buffer, headerSize - 4, 4);
        if (body < AsciiMinBody)
            throw new SlmpFrameFormatException($"要求データ長が短すぎます: {body}");
        return headerSize + body;
    }

    internal static int Hex(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || offset + length > data.Length)
            throw new SlmpFrameFormatException("ASCII フィールドが短すぎます");
        var value = 0;
        for (var i = 0; i < length; i++)
        {
            var d = HexDigit(data[offset + i]);
            if (d < 0)
                throw new SlmpFrameFormatException($"ASCII 16進でない文字: 0x{data[offset + i]:X2}");
            value = (value << 4) | d;
        }

        return value;
    }

    internal static int HexDigit(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        _ => -1,
    };

    private static bool AsciiEquals(ReadOnlySpan<byte> data, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.ToUpperInvariant((char)data[i]) != text[i])
                return false;
        }

        return true;
    }

    private static bool IsAsciiPrefix(ReadOnlySpan<byte> buffer)
    {
        var text = Encoding.ASCII.GetString(buffer).ToUpperInvariant();
        return "5000".StartsWith(text, StringComparison.Ordinal) || "5400".StartsWith(text, StringComparison.Ordinal);
    }

    private static string UnknownHeader(ReadOnlySpan<byte> buffer)
        => $"未対応のサブヘッダ: {Convert.ToHexString(buffer[..Math.Min(4, buffer.Length)])}";
}
