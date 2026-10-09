namespace PlcSim.Slmp.Core.Engine;

/// <summary>モニタ登録の 1 点。</summary>
public sealed record MonitorEntry(DeviceAccessor Accessor, bool DoubleWord);

/// <summary>接続（TCP 接続 / UDP 送信元）ごとの状態。</summary>
public sealed class SlmpSession
{
    public SlmpSession(string remote, string transport)
    {
        Remote = remote;
        Transport = transport;
        Connected = DateTime.Now;
    }

    public string Remote { get; }

    public string Transport { get; }

    public DateTime Connected { get; }

    /// <summary>リモートパスワード解除済みか。</summary>
    public bool PasswordUnlocked { get; set; }

    /// <summary>0801 で登録されたモニタデバイス。</summary>
    public IReadOnlyList<MonitorEntry> Monitor { get; set; } = Array.Empty<MonitorEntry>();

    public long RequestCount { get; set; }

    public override string ToString() => $"{Transport} {Remote}";
}
