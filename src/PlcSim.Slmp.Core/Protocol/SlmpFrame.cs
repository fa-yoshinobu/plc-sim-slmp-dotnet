namespace PlcSim.Slmp.Core.Protocol;

/// <summary>SLMP フレーム種別。</summary>
public enum SlmpFrameKind
{
    Frame3E,
    Frame4E,
}

/// <summary>SLMP データコード（バイナリ / ASCII）。</summary>
public enum SlmpDataCode
{
    Binary,
    Ascii,
}

/// <summary>受信した要求フレームを解析した結果。<see cref="Data"/> は要求データ部（要求データの符号化のまま）。</summary>
public sealed record SlmpRequest(
    SlmpFrameKind Frame,
    SlmpDataCode DataCode,
    ushort Serial,
    byte Network,
    byte Station,
    ushort ModuleIo,
    byte Multidrop,
    ushort MonitoringTimer,
    ushort Command,
    ushort Subcommand,
    byte[] Data,
    byte[] Raw)
{
    public bool IsAscii => DataCode == SlmpDataCode.Ascii;
}

/// <summary>応答フレームの内容。</summary>
public sealed record SlmpResponse(ushort EndCode, byte[] Data)
{
    public static SlmpResponse Ok(byte[]? data = null) => new(0, data ?? Array.Empty<byte>());
}

/// <summary>終了コードで応答すべき処理エラー。</summary>
public sealed class SlmpEndCodeException : Exception
{
    public SlmpEndCodeException(ushort endCode, string message)
        : base(message)
    {
        EndCode = endCode;
    }

    public ushort EndCode { get; }

    /// <summary>応答のエラー情報に入れるコマンド（null なら要求のコマンド）。実機の癖の再現用。</summary>
    public ushort? ErrorInfoCommand { get; init; }
}

/// <summary>フレームとして解釈できない受信データ（応答せず破棄・切断する）。</summary>
public sealed class SlmpFrameFormatException : Exception
{
    public SlmpFrameFormatException(string message)
        : base(message)
    {
    }
}
