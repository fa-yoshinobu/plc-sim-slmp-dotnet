using System.Text;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Engine;

/// <summary>ラベル系コマンド（041A/141A/041C/141B）。バイナリのみ対応。</summary>
public sealed partial class SlmpEngine
{
    private byte[] LabelArrayRead(RequestContext c)
    {
        var (points, abbreviations) = BeginLabelCommand(c);
        var items = new List<(LabelSlot Slot, byte Unit, ushort Length)>();
        for (var i = 0; i < points; i++)
        {
            var slot = ResolveLabel(c, abbreviations);
            var unit = c.Reader.ReadU8();
            _ = c.Reader.ReadU8();
            var length = c.Reader.ReadU16();
            items.Add((slot, unit, length));
        }

        c.Reader.EnsureEnd();
        // 応答の大きさを読出しの前に確かめる（大きすぎる要求で応答を組み立てない）
        EnsureResponseFits(c, 2 + items.Sum(static x => 4L + ArrayWireBytes(x.Unit, x.Length)));
        c.Writer.WriteU16(points);
        foreach (var (slot, unit, length) in items)
        {
            var data = ReadArrayData(slot, unit, length);
            c.Record(AccessKind.Read, "ラベル", 0, 1, slot.Path);
            c.Writer.WriteU8(slot.Type?.TypeId ?? 0);
            c.Writer.WriteU8(unit);
            c.Writer.WriteU16(length);
            c.Writer.WriteRaw(data);
        }

        c.Summary = $"配列ラベル読出し {string.Join(", ", items.Select(static x => x.Slot.Path))}";
        return c.Writer.ToArray();
    }

    private byte[] LabelArrayWrite(RequestContext c)
    {
        var (points, abbreviations) = BeginLabelCommand(c);
        var items = new List<(LabelSlot Slot, byte Unit, ushort Length, byte[] Data)>();
        for (var i = 0; i < points; i++)
        {
            var slot = ResolveLabel(c, abbreviations);
            var unit = c.Reader.ReadU8();
            _ = c.Reader.ReadU8();
            var length = c.Reader.ReadU16();
            var data = c.Reader.ReadRaw(ArrayWireBytes(unit, length));
            items.Add((slot, unit, length, data));
        }

        c.Reader.EnsureEnd();
        EnsureWritable();
        // 型・長さ・範囲の確認を書込みの前に全件行う（1 件でも誤りがあれば何も書かない）
        foreach (var (slot, unit, length, _) in items)
            _ = ReadArrayData(slot, unit, length);
        foreach (var (slot, unit, length, data) in items)
        {
            WriteArrayData(slot, unit, length, data, c.Source);
            c.Record(AccessKind.Write, "ラベル", 0, 1, slot.Path);
        }
        c.Summary = $"配列ラベル書込み {string.Join(", ", items.Select(static x => x.Slot.Path))}";
        return Array.Empty<byte>();
    }

    private byte[] LabelReadRandom(RequestContext c)
    {
        var (points, abbreviations) = BeginLabelCommand(c);
        var slots = new List<LabelSlot>();
        for (var i = 0; i < points; i++)
            slots.Add(ResolveLabel(c, abbreviations));
        c.Reader.EnsureEnd();
        // 応答の大きさを読出しの前に確かめる（大きすぎる要求で応答を組み立てない）
        EnsureResponseFits(c, 2 + slots.Sum(static s => 4L + s.Size));

        c.Writer.WriteU16(points);
        foreach (var slot in slots)
        {
            if (slot.Type is null)
                throw new SlmpEndCodeException(EndCodes.RequestContentError, $"{slot.Path} は基本データ型ではありません");
            if (slot.Size > ushort.MaxValue)
                throw new SlmpEndCodeException(EndCodes.RequestContentError, $"{slot.Path} は {slot.Size} バイトあり、1 回の応答で返せません");
            c.Writer.WriteU8(slot.Type.TypeId);
            c.Writer.WriteU8(0);
            c.Writer.WriteU16((ushort)slot.Size);
            c.Writer.WriteRaw(Plc.Labels.Read(slot, slot.Size));
            c.Record(AccessKind.Read, "ラベル", 0, 1, slot.Path);
        }

        c.Summary = $"ラベルランダム読出し {string.Join(", ", slots.Select(static x => x.Path))}";
        return c.Writer.ToArray();
    }

    private byte[] LabelWriteRandom(RequestContext c)
    {
        var (points, abbreviations) = BeginLabelCommand(c);
        var items = new List<(LabelSlot Slot, byte[] Data)>();
        for (var i = 0; i < points; i++)
        {
            var slot = ResolveLabel(c, abbreviations);
            var length = c.Reader.ReadU16();
            items.Add((slot, c.Reader.ReadRaw(length)));
        }

        c.Reader.EnsureEnd();
        foreach (var (slot, data) in items)
        {
            if (slot.Type is null || data.Length != slot.Size)
                throw new SlmpEndCodeException(EndCodes.RequestContentError, $"{slot.Path} の書込みデータ長 {data.Length} が型のサイズ {slot.Size} と一致しません");
        }

        EnsureWritable();
        foreach (var (slot, data) in items)
        {
            Plc.Labels.Write(slot, data, c.Source);
            c.Record(AccessKind.Write, "ラベル", 0, 1, slot.Path);
        }
        c.Summary = $"ラベルランダム書込み {string.Join(", ", items.Select(static x => x.Slot.Path))}";
        return Array.Empty<byte>();
    }

    private (ushort Points, List<string> Abbreviations) BeginLabelCommand(RequestContext c)
    {
        RequireFeature(c, Plc.Profile.Features.Labels && !c.Request.IsAscii);
        RequireSubcommand(c, 0x0000);
        var points = c.Reader.ReadU16();
        var abbreviationCount = c.Reader.ReadU16();
        if (points < 1)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, "ラベル点数が 0 です");
        var abbreviations = new List<string>(abbreviationCount);
        for (var i = 0; i < abbreviationCount; i++)
            abbreviations.Add(ReadLabelName(c));
        return (points, abbreviations);
    }

    private LabelSlot ResolveLabel(RequestContext c, List<string> abbreviations)
    {
        var name = ReadLabelName(c);
        var expanded = System.Text.RegularExpressions.Regex.Replace(name, @"%(\d+)", m =>
        {
            if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) ||
                index < 1 || index > abbreviations.Count)
                throw new SlmpEndCodeException(EndCodes.LabelNotFound, $"省略ラベル %{index} が未定義です");
            return abbreviations[index - 1];
        });
        return Plc.Labels.Resolve(expanded);
    }

    /// <summary>応答データ（バイナリでのバイト数）が 1 回の応答に収まるか確かめる。</summary>
    private static void EnsureResponseFits(RequestContext c, long binaryBytes)
    {
        var wire = c.Request.IsAscii ? binaryBytes * 2 : binaryBytes;
        if (wire > MaxResponseData(c.Request, c.Session))
            throw new SlmpEndCodeException(EndCodes.RequestContentError, $"応答データ {wire} バイトは 1 回の応答で返せません");
    }

    private static string ReadLabelName(RequestContext c)
    {
        var chars = c.Reader.ReadU16();
        return Encoding.Unicode.GetString(c.Reader.ReadRaw(chars * 2));
    }

    private static int ArrayWireBytes(byte unit, ushort length) => unit switch
    {
        0 => ((length + 15) / 16) * 2,
        1 => ((length + 1) / 2) * 2,
        _ => throw new SlmpEndCodeException(EndCodes.RequestContentError, $"単位指定 {unit} は 0(ビット)/1(バイト) で指定してください"),
    };

    private byte[] ReadArrayData(LabelSlot slot, byte unit, ushort length)
    {
        if (length == 0)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, "配列データ長が 0 です");
        var wire = ArrayWireBytes(unit, length);
        if (unit == 1)
            return Plc.Labels.Read(slot, wire);

        // ビット単位: BIT 型配列の要素（記憶域は 1 要素 2 バイト）を 16 点/ワードに詰める。
        if (slot.Type != LabelDataType.Bit)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, $"{slot.Path} は BIT 型ではありません");
        var raw = Plc.Labels.Read(slot, length * 2);
        var result = new byte[wire];
        for (var i = 0; i < length; i++)
        {
            if (raw[i * 2] != 0)
                result[i / 8] |= (byte)(1 << (i % 8));
        }

        return result;
    }

    private void WriteArrayData(LabelSlot slot, byte unit, ushort length, byte[] data, string source)
    {
        if (length == 0)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, "配列データ長が 0 です");
        if (unit == 1)
        {
            Plc.Labels.Write(slot, data, source);
            return;
        }

        if (slot.Type != LabelDataType.Bit)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, $"{slot.Path} は BIT 型ではありません");
        var raw = new byte[length * 2];
        for (var i = 0; i < length; i++)
            raw[i * 2] = (byte)((data[i / 8] >> (i % 8)) & 1);
        Plc.Labels.Write(slot, raw, source);
    }
}
