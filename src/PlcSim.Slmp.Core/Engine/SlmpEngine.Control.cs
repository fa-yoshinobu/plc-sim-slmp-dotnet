using System.Text;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Engine;

/// <summary>制御・保守系コマンド（0101/0619/1617/1001〜1006/1630/1631/0613/1613/0601/1601）。</summary>
public sealed partial class SlmpEngine
{
    private byte[] ReadTypeName(RequestContext c)
    {
        RequireFeature(c, Plc.Profile.Features.TypeName);
        // 実機計測（iQ-R 系）: サブコマンドは確認しない（0001 でも正常応答）。
        c.Reader.EnsureEnd();
        var name = Encoding.ASCII.GetBytes(Plc.Profile.ModelName.PadRight(16)[..16]);
        c.Writer.WriteRaw(name);
        c.Writer.WriteU16(Plc.Profile.ModelCode);
        c.Summary = $"形名読出し {Plc.Profile.ModelName} ({Plc.Profile.ModelCode:X4}h)";
        return c.Writer.ToArray();
    }

    private byte[] SelfTest(RequestContext c)
    {
        // 実機計測（iQ-R 系）: サブコマンドは確認しない（0001 でも正常応答）。
        var length = c.Reader.ReadU16();
        var data = c.Reader.ReadRaw(length);
        c.Reader.EnsureEnd();
        if (length is < 1 or > 960 || data.Any(static b => FrameCodec.HexDigit(b) < 0 || b is >= (byte)'a' and <= (byte)'f'))
            throw new SlmpEndCodeException(EndCodes.RequestContentError, "折返しデータは 1〜960 文字の 0〜9/A〜F で指定してください");
        c.Writer.WriteU16(length);
        c.Writer.WriteRaw(data);
        c.Summary = $"折返しテスト {length}文字";
        return c.Writer.ToArray();
    }

    private byte[] ClearError(RequestContext c)
    {
        RequireSubcommand(c, 0x0000);
        c.Reader.EnsureEnd();
        Plc.SetErrorCode(0);
        return Array.Empty<byte>();
    }

    private byte[] RemoteRun(RequestContext c)
    {
        RequireSubcommand(c, 0x0000);
        var mode = c.Reader.ReadU16();
        var clear = c.Reader.ReadU16();
        c.Reader.EnsureEnd();
        if (mode is not (0x0001 or 0x0003) || clear > 2)
            throw new SlmpEndCodeException(EndCodes.RemoteModeError, $"リモートRUN のモード {mode:X4} / クリア {clear:X4} が不正です");
        Plc.ClearOnRun(clear, c.Source);
        Plc.SetState(PlcRunState.Run, c.Source);
        c.Summary = $"リモートRUN (モード{mode:X4} クリア{clear})";
        return Array.Empty<byte>();
    }

    private byte[] RemoteStop(RequestContext c)
    {
        RequireSubcommand(c, 0x0000);
        RequireFixedParameter(c);
        Plc.SetState(PlcRunState.Stop, c.Source);
        return Array.Empty<byte>();
    }

    private byte[] RemotePause(RequestContext c)
    {
        RequireSubcommand(c, 0x0000);
        var mode = c.Reader.ReadU16();
        c.Reader.EnsureEnd();
        if (mode is not (0x0001 or 0x0003))
            throw new SlmpEndCodeException(EndCodes.RemoteModeError, $"リモートPAUSE のモード {mode:X4} が不正です");
        Plc.SetState(PlcRunState.Pause, c.Source);
        return Array.Empty<byte>();
    }

    private byte[] RemoteLatchClear(RequestContext c)
    {
        RequireSubcommand(c, 0x0000);
        RequireFixedParameter(c);
        if (Plc.State != PlcRunState.Stop)
            throw new SlmpEndCodeException(EndCodes.StateConditionError, "ラッチクリアは STOP 中のみ実行できます");
        Plc.LatchClear(c.Source);
        return Array.Empty<byte>();
    }

    private byte[] RemoteReset(RequestContext c)
    {
        RequireSubcommand(c, 0x0000);
        RequireFixedParameter(c);
        if (!Plc.Settings.RemoteResetAllowed)
            throw new SlmpEndCodeException(EndCodes.RemoteResetNotAllowed, "リモートリセットが許可されていません");
        // 実機計測（iQ-R 系）: RUN 中・PAUSE 中は 4013。STOP 中は応答を返さずに接続を切って再起動する。
        if (Plc.State != PlcRunState.Stop)
            throw new SlmpEndCodeException(EndCodes.StateConditionError, "リモートリセットは STOP 中のみ実行できます");
        Plc.Reset(c.Source);
        c.Disconnect = true;
        c.Summary = "リモートリセット（応答なし・全接続を切断して再起動）";
        return Array.Empty<byte>();
    }

    private byte[] RemotePassword(RequestContext c, bool unlock)
    {
        RequireSubcommand(c, 0x0000);
        var length = c.Reader.ReadU16();
        var raw = c.Reader.ReadRaw(length);
        c.Reader.EnsureEnd();
        var expected = Plc.Settings.RemotePassword;
        // 実機計測（iQ-R 系）: パスワードを設定していない PLC は、解除・ロックとも正常終了を返す（ロックはされない）。
        if (expected is null)
        {
            c.Summary = unlock ? "パスワード解除（パスワード未設定）" : "パスワードロック（パスワード未設定）";
            return Array.Empty<byte>();
        }

        if (!string.Equals(Encoding.ASCII.GetString(raw), expected, StringComparison.Ordinal))
            throw new SlmpEndCodeException(EndCodes.RemotePasswordMismatch, "リモートパスワードが一致しません");
        c.Session.PasswordUnlocked = unlock;
        return Array.Empty<byte>();
    }

    private byte[] MemoryRead(RequestContext c)
    {
        RequireFeature(c, Plc.Profile.Features.MemoryAccess);
        RequireSubcommand(c, 0x0000);
        var head = c.Reader.ReadU32();
        var count = c.Reader.ReadU16();
        c.Reader.EnsureEnd();
        if (count is < 1 or > 480)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, $"メモリ読出しワード数 {count} は 1〜480 で指定してください");
        WriteWords(c, Plc.Buffers.OwnMemory.Read(head, count));
        c.Record(AccessKind.Read, "自局", head, count, $"自局 {head:X}h ×{count}");
        c.Summary = $"メモリ読出し {head:X8}h {count}ワード";
        return c.Writer.ToArray();
    }

    private byte[] MemoryWrite(RequestContext c)
    {
        RequireFeature(c, Plc.Profile.Features.MemoryAccess);
        RequireSubcommand(c, 0x0000);
        var head = c.Reader.ReadU32();
        var count = c.Reader.ReadU16();
        if (count is < 1 or > 480)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, $"メモリ書込みワード数 {count} は 1〜480 で指定してください");
        var values = ReadWordValues(c, count);
        c.Reader.EnsureEnd();
        EnsureWritable();
        Plc.Buffers.OwnMemory.Write(head, values);
        c.Record(AccessKind.Write, "自局", head, count, $"自局 {head:X}h ×{count}");
        Plc.RaiseBufferChanged(new MemoryChange("自局", head, count, c.Source, DateTime.Now));
        c.Summary = $"メモリ書込み {head:X8}h {count}ワード";
        return Array.Empty<byte>();
    }

    private byte[] ExtendUnitRead(RequestContext c)
    {
        RequireFeature(c, Plc.Profile.Features.ExtendUnit);
        RequireSubcommand(c, 0x0000);
        var head = c.Reader.ReadU32();
        var bytes = c.Reader.ReadU16();
        var unit = c.Reader.ReadU16();
        c.Reader.EnsureEnd();
        if (bytes is < 2 or > 1920)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, $"拡張ユニット読出しバイト数 {bytes} は 2〜1920 で指定してください");
        EnsureUnit(unit, EndCodes.ModuleNotFound);
        c.Writer.WriteBytes(Plc.Buffers.Module(unit).ReadBytes(head, bytes));
        c.Record(AccessKind.Read, $"U{unit:X}\\G", head / 2, (bytes + 1) / 2, $"U{unit:X}\\G{head / 2} ×{(bytes + 1) / 2}（拡張ユニット）");
        c.Summary = $"拡張ユニット読出し U{unit:X} {head:X8}h {bytes}バイト";
        return c.Writer.ToArray();
    }

    private byte[] ExtendUnitWrite(RequestContext c)
    {
        RequireFeature(c, Plc.Profile.Features.ExtendUnit);
        RequireSubcommand(c, 0x0000);
        var head = c.Reader.ReadU32();
        var bytes = c.Reader.ReadU16();
        var unit = c.Reader.ReadU16();
        if (bytes is < 2 or > 1920)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, $"拡張ユニット書込みバイト数 {bytes} は 2〜1920 で指定してください");
        var data = c.Reader.ReadBytes(bytes);
        c.Reader.EnsureEnd();
        EnsureWritable();
        EnsureUnit(unit, EndCodes.ModuleNotFound);
        Plc.Buffers.Module(unit).WriteBytes(head, data);
        c.Record(AccessKind.Write, $"U{unit:X}\\G", head / 2, (bytes + 1) / 2, $"U{unit:X}\\G{head / 2} ×{(bytes + 1) / 2}（拡張ユニット）");
        Plc.RaiseBufferChanged(new MemoryChange($"U{unit:X}\\G", head / 2, (bytes + 1) / 2, c.Source, DateTime.Now));
        c.Summary = $"拡張ユニット書込み U{unit:X} {head:X8}h {bytes}バイト";
        return Array.Empty<byte>();
    }

    /// <summary>STOP/ラッチクリア/リセットの固定パラメータ 0001h。</summary>
    private static void RequireFixedParameter(RequestContext c)
    {
        var value = c.Reader.ReadU16();
        c.Reader.EnsureEnd();
        // 実機計測（iQ-R 系、リモート STOP）: 0001 以外は 400B。
        if (value != 0x0001)
            throw new SlmpEndCodeException(EndCodes.RemoteModeError, $"パラメータ {value:X4} は 0001 で指定してください");
    }
}
