using System.Diagnostics;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Engine;

/// <summary>1 要求の処理記録（通信ログ表示用）。</summary>
public sealed record FrameLogEntry(
    DateTime Time,
    string Session,
    SlmpRequest Request,
    byte[]? Response,
    ushort EndCode,
    string Summary,
    TimeSpan Elapsed);

/// <summary>要求処理の結果。<see cref="Frame"/> が <c>null</c> なら応答しない。</summary>
/// <param name="Disconnect">true なら応答せずに全接続を切る（リモートリセット）。</param>
public sealed record EngineResult(byte[]? Frame, int DelayMs, bool Disconnect = false);

/// <summary>SLMP コマンド名。</summary>
public static class SlmpCommands
{
    public const ushort ReadTypeName = 0x0101;
    public const ushort DeviceRead = 0x0401;
    public const ushort DeviceWrite = 0x1401;
    public const ushort DeviceReadRandom = 0x0403;
    public const ushort DeviceWriteRandom = 0x1402;
    public const ushort DeviceReadBlock = 0x0406;
    public const ushort DeviceWriteBlock = 0x1406;
    public const ushort MonitorRegister = 0x0801;
    public const ushort Monitor = 0x0802;
    public const ushort LabelArrayRead = 0x041A;
    public const ushort LabelArrayWrite = 0x141A;
    public const ushort LabelReadRandom = 0x041C;
    public const ushort LabelWriteRandom = 0x141B;
    public const ushort MemoryRead = 0x0613;
    public const ushort MemoryWrite = 0x1613;
    public const ushort ExtendUnitRead = 0x0601;
    public const ushort ExtendUnitWrite = 0x1601;
    public const ushort RemoteRun = 0x1001;
    public const ushort RemoteStop = 0x1002;
    public const ushort RemotePause = 0x1003;
    public const ushort RemoteLatchClear = 0x1005;
    public const ushort RemoteReset = 0x1006;
    public const ushort SelfTest = 0x0619;
    public const ushort ClearError = 0x1617;
    public const ushort RemotePasswordUnlock = 0x1630;
    public const ushort RemotePasswordLock = 0x1631;

    public static string NameOf(ushort command) => command switch
    {
        ReadTypeName => "形名読出し",
        DeviceRead => "一括読出し",
        DeviceWrite => "一括書込み",
        DeviceReadRandom => "ランダム読出し",
        DeviceWriteRandom => "ランダム書込み",
        DeviceReadBlock => "ブロック読出し",
        DeviceWriteBlock => "ブロック書込み",
        MonitorRegister => "モニタ登録",
        Monitor => "モニタ",
        LabelArrayRead => "配列ラベル読出し",
        LabelArrayWrite => "配列ラベル書込み",
        LabelReadRandom => "ラベルランダム読出し",
        LabelWriteRandom => "ラベルランダム書込み",
        MemoryRead => "メモリ読出し",
        MemoryWrite => "メモリ書込み",
        ExtendUnitRead => "拡張ユニット読出し",
        ExtendUnitWrite => "拡張ユニット書込み",
        RemoteRun => "リモートRUN",
        RemoteStop => "リモートSTOP",
        RemotePause => "リモートPAUSE",
        RemoteLatchClear => "リモートラッチクリア",
        RemoteReset => "リモートRESET",
        SelfTest => "折返しテスト",
        ClearError => "エラーコード初期化",
        RemotePasswordUnlock => "パスワード解除",
        RemotePasswordLock => "パスワードロック",
        _ => "未対応コマンド",
    };
}

/// <summary>
/// SLMP 要求を仮想 PLC に対して実行し応答フレームを作る。フレーム形式・通信路に依存しない。
/// </summary>
public sealed partial class SlmpEngine
{
    public SlmpEngine(PlcModel plc, ErrorInjector? injector = null)
    {
        Plc = plc;
        Injector = injector ?? new ErrorInjector();
    }

    public PlcModel Plc { get; }

    public ErrorInjector Injector { get; }

    /// <summary>正常終了した要求のデバイス・バッファ・ラベルへのアクセスごとに発生する。</summary>
    public event Action<AccessRecord>? Accessed;

    /// <summary>要求を処理するたびに発生する。</summary>
    public event Action<FrameLogEntry>? FrameProcessed;

    public long RequestCount => Interlocked.Read(ref _requestCount);

    public long ErrorCount => Interlocked.Read(ref _errorCount);

    private long _requestCount;
    private long _errorCount;

    /// <summary>1 フレームを処理する。</summary>
    public EngineResult Process(SlmpRequest request, SlmpSession session)
    {
        if (Plc.IsRestarting)
            return Ignore(request, session, "無応答（リセット後の再起動中）");

        var stopwatch = Stopwatch.StartNew();
        Interlocked.Increment(ref _requestCount);
        session.RequestCount++;

        var injected = Injector.Take(request.Command, request.Subcommand);
        SlmpResponse response;
        string summary;
        ushort? errorInfoCommand = null;
        var disconnect = false;
        if (injected?.EndCode is { } forced)
        {
            response = new SlmpResponse(forced, Array.Empty<byte>());
            summary = "エラー注入";
        }
        else
        {
            (response, summary, errorInfoCommand, disconnect) = Execute(request, session);
        }

        if (response.EndCode != EndCodes.Success)
            Interlocked.Increment(ref _errorCount);

        var frame = injected?.Drop == true || disconnect ? null : FrameCodec.BuildResponse(errorInfoCommand is { } cmd ? request with { Command = cmd } : request, response.EndCode, response.Data);
        FrameProcessed?.Invoke(new FrameLogEntry(
            DateTime.Now,
            session.ToString(),
            request,
            frame,
            response.EndCode,
            injected?.Drop == true ? $"{summary}（無応答注入）" : summary,
            stopwatch.Elapsed));
        return new EngineResult(frame, injected?.DelayMs ?? 0, disconnect);
    }

    /// <summary>IPv4 の UDP で 1 データグラムに載せられる最大バイト数。</summary>
    public const int MaxUdpPayload = 65507;

    /// <summary>
    /// 1 回の応答に入れられるデータの最大長。データ長欄（16 ビット、終了コードを含む）と、UDP では 1 データグラムの上限
    /// （ヘッダ込み 65,507 バイト）で決まる。
    /// </summary>
    public static int MaxResponseData(SlmpRequest request, SlmpSession session)
    {
        var endCode = request.IsAscii ? 4 : 2;
        var max = ushort.MaxValue - endCode;
        if (session.Transport == "UDP")
            max = Math.Min(max, MaxUdpPayload - FrameCodec.ResponseOverhead(request));
        return max;
    }

    /// <summary>応答しない要求を通信ログに残す（交信データコードの不一致など）。</summary>
    /// <param name="countRequest">要求数に数えるか（すでに <see cref="Process"/> で数えた要求なら false）。</param>
    public EngineResult Ignore(SlmpRequest request, SlmpSession session, string reason, bool countRequest = true)
    {
        if (countRequest)
        {
            Interlocked.Increment(ref _requestCount);
            session.RequestCount++;
        }

        FrameProcessed?.Invoke(new FrameLogEntry(DateTime.Now, session.ToString(), request, null, EndCodes.Success, reason, TimeSpan.Zero));
        return new EngineResult(null, 0);
    }

    private (SlmpResponse Response, string Summary, ushort? ErrorInfoCommand, bool Disconnect) Execute(SlmpRequest request, SlmpSession session)
    {
        try
        {
            if (Plc.Settings.RemotePassword is not null &&
                !session.PasswordUnlocked &&
                request.Command is not (SlmpCommands.RemotePasswordUnlock or SlmpCommands.RemotePasswordLock))
            {
                throw new SlmpEndCodeException(EndCodes.RemotePasswordLocked, "リモートパスワードでロックされています");
            }

            lock (Plc.SyncRoot)
            {
                Plc.RefreshSpecialDevices();
                var context = new RequestContext(request, session);
                var data = Dispatch(context);
                if (data.Length > MaxResponseData(request, session))
                    throw new SlmpEndCodeException(EndCodes.RequestContentError, $"応答データ {data.Length} バイトは 1 回の応答で返せません");
                foreach (var access in context.Accesses)
                    Accessed?.Invoke(access);
                return (SlmpResponse.Ok(data), context.Summary, null, context.Disconnect);
            }
        }
        catch (SlmpEndCodeException ex)
        {
            return (new SlmpResponse(ex.EndCode, Array.Empty<byte>()), ex.Message, ex.ErrorInfoCommand, false);
        }
    }

    private byte[] Dispatch(RequestContext c) => c.Request.Command switch
    {
        SlmpCommands.DeviceRead => DeviceRead(c),
        SlmpCommands.DeviceWrite => DeviceWrite(c),
        SlmpCommands.DeviceReadRandom => DeviceReadRandom(c),
        SlmpCommands.DeviceWriteRandom => DeviceWriteRandom(c),
        SlmpCommands.DeviceReadBlock => DeviceReadBlock(c),
        SlmpCommands.DeviceWriteBlock => DeviceWriteBlock(c),
        SlmpCommands.MonitorRegister => MonitorRegister(c),
        SlmpCommands.Monitor => MonitorExecute(c),
        SlmpCommands.LabelArrayRead => LabelArrayRead(c),
        SlmpCommands.LabelArrayWrite => LabelArrayWrite(c),
        SlmpCommands.LabelReadRandom => LabelReadRandom(c),
        SlmpCommands.LabelWriteRandom => LabelWriteRandom(c),
        SlmpCommands.ReadTypeName => ReadTypeName(c),
        SlmpCommands.MemoryRead => MemoryRead(c),
        SlmpCommands.MemoryWrite => MemoryWrite(c),
        SlmpCommands.ExtendUnitRead => ExtendUnitRead(c),
        SlmpCommands.ExtendUnitWrite => ExtendUnitWrite(c),
        SlmpCommands.RemoteRun => RemoteRun(c),
        SlmpCommands.RemoteStop => RemoteStop(c),
        SlmpCommands.RemotePause => RemotePause(c),
        SlmpCommands.RemoteLatchClear => RemoteLatchClear(c),
        SlmpCommands.RemoteReset => RemoteReset(c),
        SlmpCommands.SelfTest => SelfTest(c),
        SlmpCommands.ClearError => ClearError(c),
        SlmpCommands.RemotePasswordUnlock => RemotePassword(c, unlock: true),
        SlmpCommands.RemotePasswordLock => RemotePassword(c, unlock: false),
        _ => throw Unsupported(c.Request),
    };

    private static SlmpEndCodeException Unsupported(SlmpRequest request)
        => new(EndCodes.CommandSubcommandError, $"未対応: コマンド {request.Command:X4} / サブコマンド {request.Subcommand:X4}");

    private static void RequireSubcommand(RequestContext c, ushort subcommand)
    {
        if (c.Request.Subcommand != subcommand)
            throw Unsupported(c.Request);
    }

    private static void RequireFeature(RequestContext c, bool supported)
    {
        if (!supported)
            throw Unsupported(c.Request);
    }

    private void EnsureWritable()
    {
        if (!Plc.CanWriteDevices)
            throw new SlmpEndCodeException(EndCodes.WriteDuringRunProhibited, "RUN 中書込み禁止の設定です");
    }

    /// <summary>1 要求の処理中の情報。</summary>
    private sealed class RequestContext
    {
        public RequestContext(SlmpRequest request, SlmpSession session)
        {
            Request = request;
            Session = session;
            Reader = new PayloadReader(request);
            Writer = new PayloadWriter(request);
            Source = $"SLMP {session.Remote}";
            Summary = SlmpCommands.NameOf(request.Command);
        }

        public SlmpRequest Request { get; }

        public SlmpSession Session { get; }

        /// <summary>応答せずに全接続を切る（リモートリセット）。</summary>
        public bool Disconnect { get; set; }

        public PayloadReader Reader { get; }

        public PayloadWriter Writer { get; }

        public string Source { get; }

        public string Summary { get; set; }

        public List<AccessRecord> Accesses { get; } = new();

        public void Record(AccessKind kind, DeviceAccessor acc, int points, string? unit = null)
            => Accesses.Add(new AccessRecord(DateTime.Now, kind, acc.AreaKey, acc.Number, points, $"{acc.DisplayName} ×{points}{unit}", Source));

        public void Record(AccessKind kind, string area, uint start, int count, string display)
            => Accesses.Add(new AccessRecord(DateTime.Now, kind, area, start, count, display, Source));
    }
}
