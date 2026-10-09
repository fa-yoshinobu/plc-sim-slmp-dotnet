using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Engine;

/// <summary>デバイスアクセス系コマンド（0401/1401/0403/1402/0406/1406/0801/0802）。</summary>
public sealed partial class SlmpEngine
{
    private readonly record struct SubcommandFormat(bool BitUnit, bool Iqr, bool Extended);

    /// <summary>モニタ登録（0801）。接続ごとではなく PLC で 1 つ。</summary>
    private List<MonitorEntry> _monitor = new();

    private byte[] DeviceRead(RequestContext c)
    {
        var fmt = ParseDeviceSubcommand(c, allowBit: true);
        var spec = c.Reader.ReadDeviceSpec(fmt.Iqr, fmt.Extended, Plc.Profile);
        var points = c.Reader.ReadU16();
        c.Reader.EnsureEnd();
        CheckLimit(points, fmt.BitUnit ? DirectBitLimit(c) : Plc.Profile.Limits.DirectWord);

        var acc = Resolve(c, spec);
        if (acc.IsPlainDevice && acc.Device.Kind == DeviceKind.LongTimerBit)
            throw new SlmpEndCodeException(EndCodes.DeviceAccessError, $"{acc.Device.Name} は一括読出しで直接指定できません（LTN/LSTN の 4 ワード単位で読出し）");

        if (fmt.BitUnit)
            c.Writer.WritePackedBits(acc.ReadBits(points));
        else
            WriteWords(c, acc.ReadWords(points));
        c.Record(AccessKind.Read, acc, WirePoints(acc, points, fmt.BitUnit), fmt.BitUnit ? "（ビット）" : null);
        c.Summary = $"一括読出し {acc.DisplayName} {points}点{(fmt.BitUnit ? "(ビット)" : string.Empty)}";
        return c.Writer.ToArray();
    }

    private byte[] DeviceWrite(RequestContext c)
    {
        var fmt = ParseDeviceSubcommand(c, allowBit: true);
        var spec = c.Reader.ReadDeviceSpec(fmt.Iqr, fmt.Extended, Plc.Profile);
        var points = c.Reader.ReadU16();
        CheckLimit(points, fmt.BitUnit ? DirectBitLimit(c) : Plc.Profile.Limits.DirectWord);
        bool[]? bits = null;
        ushort[]? words = null;
        if (fmt.BitUnit)
            bits = c.Reader.ReadPackedBits(points);
        else
            words = ReadWordValues(c, points);
        c.Reader.EnsureEnd();

        EnsureWritable();
        var acc = Resolve(c, spec);
        EnsureDeviceWritable(acc);
        // 実機計測（iQ-R 系）: LTC/LTS の一括書込みはビット単位・ワード単位とも 4030。
        if (acc.IsPlainDevice && acc.Device.Kind == DeviceKind.LongTimerBit)
            throw new SlmpEndCodeException(EndCodes.DeviceAccessError, $"{acc.Device.Name} は一括書込みで指定できません");
        // 実機計測（iQ-R 系）: LTN/LSTN の一括書込みは 4 ワード（1 点分の構造）単位なら受け付け、それ以外は C051。
        if (acc.IsPlainDevice && acc.Device.Kind == DeviceKind.LongTimer && (fmt.BitUnit || points % 4 != 0))
            throw new SlmpEndCodeException(Plc.Profile.Limits.DirectWord.OverEndCode, $"{acc.Device.Name} の一括書込みは 4 ワード単位で指定してください");

        if (bits is not null)
            acc.WriteBits(bits, c.Source);
        else
            acc.WriteWords(words!, c.Source);
        c.Record(AccessKind.Write, acc, WirePoints(acc, points, fmt.BitUnit), fmt.BitUnit ? "（ビット）" : null);
        c.Summary = $"一括書込み {acc.DisplayName} {points}点{(fmt.BitUnit ? "(ビット)" : string.Empty)}";
        return Array.Empty<byte>();
    }

    private byte[] DeviceReadRandom(RequestContext c)
    {
        var fmt = ParseDeviceSubcommand(c, allowBit: false);
        var wordCount = c.Reader.ReadU8();
        var dwordCount = c.Reader.ReadU8();
        CheckLimit(wordCount + dwordCount, fmt.Extended ? Plc.Profile.Limits.RandomReadExt : Plc.Profile.Limits.RandomRead);
        var words = ReadSpecs(c, fmt, wordCount);
        var dwords = ReadSpecs(c, fmt, dwordCount);
        c.Reader.EnsureEnd();

        // 実機計測（iQ-R 系）: LTC/LTS をワード単位で指定すると 4032。
        foreach (var spec in words.Concat(dwords))
        {
            if (spec.Device.Kind == DeviceKind.LongTimerBit)
                throw new SlmpEndCodeException(EndCodes.DeviceUnitError, $"{spec.Device.Name} はランダム読出しのワード単位で指定できません");
        }

        foreach (var spec in words)
        {
            var acc = Resolve(c, spec);
            c.Writer.WriteU16(acc.ReadWordPoint());
            c.Record(AccessKind.Read, acc, 1);
        }

        foreach (var spec in dwords)
        {
            var acc = Resolve(c, spec);
            c.Writer.WriteU32(acc.ReadDWordPoint());
            c.Record(AccessKind.Read, acc, 1, "（ダブルワード）");
        }
        c.Summary = $"ランダム読出し ワード{wordCount}点 ダブルワード{dwordCount}点";
        return c.Writer.ToArray();
    }

    private byte[] DeviceWriteRandom(RequestContext c)
    {
        var fmt = ParseDeviceSubcommand(c, allowBit: true);
        return fmt.BitUnit ? DeviceWriteRandomBits(c, fmt) : DeviceWriteRandomWords(c, fmt);
    }

    private byte[] DeviceWriteRandomBits(RequestContext c, SubcommandFormat fmt)
    {
        var count = c.Reader.ReadU8();
        CheckLimit(count, fmt.Extended ? Plc.Profile.Limits.RandomBitWriteExt : Plc.Profile.Limits.RandomBitWrite);
        var entries = new List<(DeviceSpec Spec, bool Value)>(count);
        for (var i = 0; i < count; i++)
        {
            var spec = c.Reader.ReadDeviceSpec(fmt.Iqr, fmt.Extended, Plc.Profile);
            // 実機計測（iQ-R 系）: 0 以外の値（02・03・FF など）はすべて ON として書き込む。
            var value = fmt.Iqr ? c.Reader.ReadU16() : c.Reader.ReadU8();
            entries.Add((spec, value != 0));
        }

        c.Reader.EnsureEnd();
        EnsureWritable();
        var accessors = entries.Select(e => (Acc: Resolve(c, e.Spec), e.Value)).ToList();
        foreach (var (acc, _) in accessors)
        {
            EnsureDeviceWritable(acc);
            _ = acc.ReadBits(1); // 範囲の確認（1 件でも範囲外なら何も書かない）
        }
        foreach (var (acc, value) in accessors)
        {
            acc.WriteBits(new[] { value }, c.Source);
            c.Record(AccessKind.Write, acc, 1, "（ビット）");
        }
        c.Summary = $"ランダム書込み ビット{count}点";
        return Array.Empty<byte>();
    }

    private byte[] DeviceWriteRandomWords(RequestContext c, SubcommandFormat fmt)
    {
        var wordCount = c.Reader.ReadU8();
        var dwordCount = c.Reader.ReadU8();
        var limit = fmt.Extended ? Plc.Profile.Limits.RandomWriteExt : Plc.Profile.Limits.RandomWrite;
        var total = wordCount + dwordCount;
        var weighted = (wordCount * 12) + (dwordCount * 14);
        if (total < 1 || total > limit.Max || (limit.WeightedMax is { } wmax && weighted > wmax))
            throw new SlmpEndCodeException(limit.OverEndCode, $"ランダム書込み点数超過: ワード{wordCount} ダブルワード{dwordCount}");

        var words = new List<(DeviceSpec Spec, ushort Value)>(wordCount);
        for (var i = 0; i < wordCount; i++)
            words.Add((c.Reader.ReadDeviceSpec(fmt.Iqr, fmt.Extended, Plc.Profile), c.Reader.ReadU16()));
        var dwords = new List<(DeviceSpec Spec, uint Value)>(dwordCount);
        for (var i = 0; i < dwordCount; i++)
            dwords.Add((c.Reader.ReadDeviceSpec(fmt.Iqr, fmt.Extended, Plc.Profile), c.Reader.ReadU32()));
        c.Reader.EnsureEnd();

        EnsureWritable();
        var wordAccessors = words.Select(e => (Acc: Resolve(c, e.Spec), e.Value)).ToList();
        var dwordAccessors = dwords.Select(e => (Acc: Resolve(c, e.Spec), e.Value)).ToList();
        // 実機計測（iQ-R 系）: LTC/LTS をワード・ダブルワード単位で書くと 4032。
        if (wordAccessors.Select(static x => x.Acc).Concat(dwordAccessors.Select(static x => x.Acc)).Any(IsLongTimerBit))
            throw new SlmpEndCodeException(EndCodes.DeviceUnitError, "LTC/LTS はランダム書込みのワード単位で指定できません");
        foreach (var acc in wordAccessors.Select(static x => x.Acc).Concat(dwordAccessors.Select(static x => x.Acc)))
            EnsureDeviceWritable(acc);
        // 範囲の確認を書込みの前に全件行う（1 件でも範囲外なら何も書かない）
        foreach (var (acc, _) in wordAccessors)
            _ = acc.ReadWordPoint();
        foreach (var (acc, _) in dwordAccessors)
            _ = acc.ReadDWordPoint();
        foreach (var (acc, value) in wordAccessors)
        {
            acc.WriteWordPoint(value, c.Source);
            c.Record(AccessKind.Write, acc, 1);
        }

        foreach (var (acc, value) in dwordAccessors)
        {
            acc.WriteDWordPoint(value, c.Source);
            c.Record(AccessKind.Write, acc, 1, "（ダブルワード）");
        }
        c.Summary = $"ランダム書込み ワード{wordCount}点 ダブルワード{dwordCount}点";
        return Array.Empty<byte>();
    }

    private byte[] DeviceReadBlock(RequestContext c)
    {
        RequireFeature(c, Plc.Profile.Features.Block);
        var fmt = ParseDeviceSubcommand(c, allowBit: false);
        var wordBlocks = c.Reader.ReadU8();
        var bitBlocks = c.Reader.ReadU8();
        var blocks = new List<(DeviceSpec Spec, ushort Points, bool Bit)>();
        for (var i = 0; i < wordBlocks + bitBlocks; i++)
            blocks.Add((c.Reader.ReadDeviceSpec(fmt.Iqr, fmt.Extended, Plc.Profile), c.Reader.ReadU16(), i >= wordBlocks));
        c.Reader.EnsureEnd();
        CheckBlockLimit(fmt, blocks.Count, blocks.Sum(static b => b.Points), 0);

        foreach (var (spec, points, bit) in blocks)
        {
            var acc = Resolve(c, spec);
            EnsureBlockCategory(acc, bit);
            WriteWords(c, acc.ReadWords(points));
            c.Record(AccessKind.Read, acc, WirePoints(acc, points, false));
        }

        c.Summary = $"ブロック読出し ワード{wordBlocks}ブロック ビット{bitBlocks}ブロック";
        return c.Writer.ToArray();
    }

    private byte[] DeviceWriteBlock(RequestContext c)
    {
        RequireFeature(c, Plc.Profile.Features.Block);
        var fmt = ParseDeviceSubcommand(c, allowBit: false);
        var wordBlocks = c.Reader.ReadU8();
        var bitBlocks = c.Reader.ReadU8();
        var blocks = new List<(DeviceSpec Spec, ushort[] Values, bool Bit)>();
        for (var i = 0; i < wordBlocks + bitBlocks; i++)
        {
            var spec = c.Reader.ReadDeviceSpec(fmt.Iqr, fmt.Extended, Plc.Profile);
            var points = c.Reader.ReadU16();
            blocks.Add((spec, ReadWordValues(c, points), i >= wordBlocks));
        }

        c.Reader.EnsureEnd();
        CheckBlockLimit(fmt, blocks.Count, blocks.Sum(static b => b.Values.Length), fmt.Iqr ? 9 : 4);
        EnsureWritable();
        var resolved = blocks.Select(b => (Acc: Resolve(c, b.Spec), b.Values, b.Bit)).ToList();
        foreach (var (acc, values, bit) in resolved)
        {
            EnsureBlockCategory(acc, bit);
            EnsureDeviceWritable(acc);
            _ = acc.ReadWords(values.Length); // 範囲の確認（1 件でも範囲外なら何も書かない）
        }

        foreach (var (acc, values, _) in resolved)
        {
            acc.WriteWords(values, c.Source);
            c.Record(AccessKind.Write, acc, WirePoints(acc, values.Length, false));
        }
        c.Summary = $"ブロック書込み ワード{wordBlocks}ブロック ビット{bitBlocks}ブロック";
        return Array.Empty<byte>();
    }

    private byte[] MonitorRegister(RequestContext c)
    {
        RequireFeature(c, Plc.Profile.Features.Monitor);
        var fmt = ParseDeviceSubcommand(c, allowBit: false);
        var wordCount = c.Reader.ReadU8();
        var dwordCount = c.Reader.ReadU8();
        CheckLimit(wordCount + dwordCount, Plc.Profile.Limits.Monitor);
        var words = ReadSpecs(c, fmt, wordCount);
        var dwords = ReadSpecs(c, fmt, dwordCount);
        c.Reader.EnsureEnd();

        var entries = new List<MonitorEntry>();
        // 実機計測（iQ-R 系）: LTC/LTS もワード単位で登録できる（実行すると 4032）。それ以外は登録時に範囲を確かめる。
        foreach (var spec in words)
        {
            var acc = Resolve(c, spec);
            if (!IsLongTimerBit(acc))
                _ = acc.ReadWordPoint();
            entries.Add(new MonitorEntry(acc, false));
        }

        foreach (var spec in dwords)
        {
            var acc = Resolve(c, spec);
            if (!IsLongTimerBit(acc))
                _ = acc.ReadDWordPoint();
            entries.Add(new MonitorEntry(acc, true));
        }

        // 実機計測（iQ-R 系）: モニタ登録は接続をまたいで残る（別の接続の 0802 でも登録したデバイスを返す）。
        _monitor = entries;
        c.Session.Monitor = entries;
        c.Summary = $"モニタ登録 ワード{wordCount}点 ダブルワード{dwordCount}点";
        return Array.Empty<byte>();
    }

    private byte[] MonitorExecute(RequestContext c)
    {
        RequireFeature(c, Plc.Profile.Features.Monitor);
        // 実機計測（iQ-R 系、2026-10-09）: 0802 はサブコマンド 0002 でも正常応答する。
        if (c.Request.Subcommand == 0x0002 && !Plc.Profile.Features.IqrSubcommands)
            throw Unsupported(c.Request);
        if (c.Request.Subcommand is not (0x0000 or 0x0002))
        {
            // 実機計測（iQ-R 系）: 0802 の不正なサブコマンドは 4002 で、エラー情報のコマンドは 0403。
            throw new SlmpEndCodeException(EndCodes.MonitorSubcommandError, $"モニタのサブコマンド {c.Request.Subcommand:X4} が不正です") { ErrorInfoCommand = SlmpCommands.DeviceReadRandom };
        }

        c.Reader.EnsureEnd();
        var monitor = _monitor;
        // 実機計測（iQ-R 系）: LTC/LTS を登録したモニタの実行は 4032 で、エラー情報のコマンドは 0403。
        if (monitor.Any(static e => IsLongTimerBit(e.Accessor)))
            throw new SlmpEndCodeException(EndCodes.DeviceUnitError, "LTC/LTS はワード単位で読み出せません") { ErrorInfoCommand = SlmpCommands.DeviceReadRandom };
        if (monitor.Count == 0)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, "モニタデバイスが登録されていません");
        foreach (var entry in monitor.Where(static e => !e.DoubleWord))
        {
            c.Writer.WriteU16(entry.Accessor.ReadWordPoint());
            c.Record(AccessKind.Read, entry.Accessor, 1, "（モニタ）");
        }

        foreach (var entry in monitor.Where(static e => e.DoubleWord))
        {
            c.Writer.WriteU32(entry.Accessor.ReadDWordPoint());
            c.Record(AccessKind.Read, entry.Accessor, 1, "（モニタ）");
        }
        c.Summary = $"モニタ {monitor.Count}点";
        return c.Writer.ToArray();
    }

    /// <summary>伝文上の点数をデバイス点数に換算する（ワード単位のビットデバイスは 16 倍、LTN は 1/4、LCN/LZ は 1/2）。</summary>
    private static int WirePoints(DeviceAccessor acc, int wirePoints, bool bitUnit)
    {
        if (bitUnit)
            return wirePoints;
        return acc.Device.Kind switch
        {
            DeviceKind.Bit => wirePoints * 16,
            DeviceKind.LongTimer when acc.IsPlainDevice => Math.Max(1, wirePoints / 4),
            DeviceKind.DoubleWord when acc.IsPlainDevice => Math.Max(1, wirePoints / 2),
            _ => wirePoints,
        };
    }

    private SubcommandFormat ParseDeviceSubcommand(RequestContext c, bool allowBit)
    {
        var sub = c.Request.Subcommand;
        // 実機計測（iQ-R 系）: 未定義のビットを含むサブコマンド（例 0401/0005）は C05C。
        // ASCII はデータ長を確認したあと（1401/0005 はビット単位として読みデータ長の誤り C058 になる）、バイナリはすぐに返す。
        if ((sub & ~0x0083) != 0)
        {
            var error = new SlmpEndCodeException(EndCodes.RequestContentError, $"サブコマンド {sub:X4} が不正です");
            if (!c.Request.IsAscii)
                throw error;
            c.Reader.DeferredError = error;
            sub &= 0x0083;
        }

        var fmt = new SubcommandFormat((sub & 0x0001) != 0, (sub & 0x0002) != 0, (sub & 0x0080) != 0);
        if (fmt.BitUnit && !allowBit)
        {
            // 実機計測（iQ-R 系）: ブロック読書きは C0D9。ランダム読出し・モニタ登録は ASCII が C05B、バイナリが C061。
            var block = c.Request.Command is SlmpCommands.DeviceReadBlock or SlmpCommands.DeviceWriteBlock;
            var code = block ? EndCodes.BlockBitUnitError : c.Request.IsAscii ? EndCodes.DeviceAccessNotAllowed : EndCodes.BinaryDataLengthMismatch;
            throw new SlmpEndCodeException(code, "このコマンドはビット単位を指定できません");
        }

        if ((fmt.Iqr && !Plc.Profile.Features.IqrSubcommands) ||
            (fmt.Extended && !Plc.Profile.Features.ExtendedDevice))
        {
            throw Unsupported(c.Request);
        }

        return fmt;
    }

    private List<DeviceSpec> ReadSpecs(RequestContext c, SubcommandFormat fmt, int count)
    {
        var specs = new List<DeviceSpec>(count);
        for (var i = 0; i < count; i++)
            specs.Add(c.Reader.ReadDeviceSpec(fmt.Iqr, fmt.Extended, Plc.Profile));
        return specs;
    }

    private static ushort[] ReadWordValues(RequestContext c, int count)
    {
        var values = new ushort[count];
        for (var i = 0; i < count; i++)
            values[i] = c.Reader.ReadU16();
        return values;
    }

    private static void WriteWords(RequestContext c, IEnumerable<ushort> words)
    {
        foreach (var w in words)
            c.Writer.WriteU16(w);
    }

    /// <summary>LTC/LTS（ロングタイマの接点・コイル）か。</summary>
    private static bool IsLongTimerBit(DeviceAccessor acc) => acc.IsPlainDevice && acc.Device.Kind == DeviceKind.LongTimerBit;

    /// <summary>ASCII のビット単位一括読書きの上限（SLMP マニュアル・CC-Link 協会サンプル: バイナリ 7168 点、ASCII 3584 点）。</summary>
    private const int AsciiDirectBitMax = 3584;

    /// <summary>一括読書きのビット単位の上限。ASCII は機種の上限と 3584 点の小さい方。</summary>
    private RequestLimit DirectBitLimit(RequestContext c)
    {
        var limit = Plc.Profile.Limits.DirectBit;
        return c.Request.IsAscii && limit.Max > AsciiDirectBitMax ? limit with { Max = AsciiDirectBitMax } : limit;
    }

    private static void CheckLimit(int points, RequestLimit limit)
    {
        if (points < 1 || points > limit.Max)
            throw new SlmpEndCodeException(limit.OverEndCode, $"点数 {points} は範囲 1〜{limit.Max} 外です");
    }

    private void CheckBlockLimit(SubcommandFormat fmt, int blocks, int points, int perBlockWeight)
    {
        var maxBlocks = fmt.Iqr ? 60 : 120;
        if (blocks < 1 || blocks > maxBlocks || points + (blocks * perBlockWeight) > 960)
            throw new SlmpEndCodeException(Plc.Profile.Limits.DirectWord.OverEndCode, $"ブロック数 {blocks} / 点数 {points} が上限を超えます");
    }

    private static void EnsureBlockCategory(DeviceAccessor acc, bool bitBlock)
    {
        if (bitBlock && !acc.Device.IsBitUnit)
            throw new SlmpEndCodeException(EndCodes.DeviceAccessNotAllowed, $"{acc.DisplayName} はビットブロックに指定できません");
        if (acc.IsPlainDevice && acc.Device.Kind == DeviceKind.LongTimerBit)
            throw new SlmpEndCodeException(EndCodes.DeviceAccessNotAllowed, $"{acc.DisplayName} はブロックに指定できません");
    }

    private void EnsureDeviceWritable(DeviceAccessor acc)
    {
        if (acc.IsPlainDevice && Plc.Profile.ReadOnlyDevices.Contains(acc.Device.Name))
            throw new SlmpEndCodeException(EndCodes.DeviceAccessError, $"{acc.Device.Name} は読出し専用です");
    }

    /// <summary>デバイス指定（拡張指定・インデックス修飾を含む）を読み書き対象に解決する。</summary>
    private DeviceAccessor Resolve(RequestContext c, DeviceSpec spec)
    {
        // 実機計測（iQ-R 系）: iQ-R 系の CPU は、インデックス修飾を Q/L 形式（0080/0081）で指定すると C05A。iQ-R 形式（0082/0083）なら受け付ける。
        if (spec.IndexFlags != 0 && Plc.Profile.Features.IqrSubcommands && (c.Request.Subcommand & 0x0002) == 0)
            throw new SlmpEndCodeException(EndCodes.ModificationFormatError, "iQ-R 系ではインデックス修飾を iQ-R 形式（0082/0083）で指定してください");
        var number = ApplyIndexModification(spec);
        var def = spec.Device;
        var features = Plc.Profile.Features;
        switch (spec.DirectMemory)
        {
            case 0x00:
                if (def.Name is "G" or "HG")
                    throw new SlmpEndCodeException(EndCodes.DeviceAccessNotAllowed, $"{def.Name} はユニット指定 (U\\) が必要です");
                if (!Plc.Memory.Supports(def))
                    throw new SlmpEndCodeException(EndCodes.DeviceOutOfRange, $"{def.Name} はこの機種にありません");
                return new MemoryDeviceAccessor(Plc.Memory, def, number);

            case 0xF8:
                if (!features.ModuleAccess)
                    throw Unsupported(c.Request);
                if (def.Name != "G")
                    throw new SlmpEndCodeException(EndCodes.RequestContentError, $"U\\ 指定に {def.Name} は使えません");
                EnsureUnit(spec.Extension);
                return new StoreDeviceAccessor(Plc.Buffers.Module(spec.Extension), def, number, $"U{spec.Extension:X}", Plc.RaiseBufferChanged);

            case 0xFA:
                if (!features.HgCpuBuffer)
                    throw Unsupported(c.Request);
                if (def.Name != "HG" || spec.Extension is < 0x3E0 or > 0x3E3)
                    throw new SlmpEndCodeException(EndCodes.RequestContentError, "HG は U3E0〜U3E3 で指定してください");
                return new StoreDeviceAccessor(Plc.Buffers.Hg(spec.Extension), def, number, $"U{spec.Extension:X}", Plc.RaiseBufferChanged);

            case 0xF9:
                if (!features.LinkDirect)
                    throw Unsupported(c.Request);
                if (def.Name is not ("X" or "Y" or "B" or "SB" or "W" or "SW"))
                    throw new SlmpEndCodeException(EndCodes.RequestContentError, $"リンクダイレクトに {def.Name} は使えません");
                var network = (byte)spec.Extension;
                EnsureNetwork(network);
                return new StoreDeviceAccessor(Plc.Buffers.Link(network, def.Name), def, number, $"J{network}", Plc.RaiseBufferChanged);

            default:
                throw new SlmpEndCodeException(EndCodes.RequestContentError, $"未対応の直接メモリ指定 {spec.DirectMemory:X2}h");
        }
    }

    /// <summary>ユニットが装着（登録）されているか。CPU バッファ U3E0〜U3E3 は常に有効。</summary>
    /// <param name="endCode">未装着のときの終了コード（拡張ユニット読書き 0601/1601 は実機計測で 4043）。</param>
    private void EnsureUnit(ushort unit, ushort endCode = EndCodes.UnitNotFound)
    {
        if (!Plc.Settings.HasUnit(unit))
            throw new SlmpEndCodeException(endCode, $"ユニット U{unit:X} は装着されていません（登録: {string.Join(", ", Plc.Settings.Units.Select(static u => $"U{u:X}"))}）");
    }

    private void EnsureNetwork(byte network)
    {
        if (!Plc.Settings.HasNetwork(network))
            throw new SlmpEndCodeException(EndCodes.UnitNotFound, $"ネットワーク J{network} は登録されていません（登録: {string.Join(", ", Plc.Settings.Networks.Select(static n => $"J{n}"))}）");
    }

    private uint ApplyIndexModification(DeviceSpec spec)
    {
        long offset = 0;
        switch (spec.IndexFlags)
        {
            case 0x00:
                break;
            case 0x40:
                offset = (short)Plc.Memory.ReadWords(DeviceCatalog.FindByName("Z")!, spec.IndexNumber, 1)[0];
                break;
            case 0x80:
                offset = (int)Plc.Memory.ReadDWordPoint(DeviceCatalog.FindByName("LZ")!, spec.IndexNumber);
                break;
            default:
                throw new SlmpEndCodeException(EndCodes.RequestContentError, $"未対応のデバイス修飾 {spec.IndexFlags:X2}h");
        }

        var number = spec.Number + offset;
        if (number < 0 || number > uint.MaxValue)
            throw new SlmpEndCodeException(EndCodes.DeviceOutOfRange, "インデックス修飾後のデバイス番号が範囲外です");
        return (uint)number;
    }
}
