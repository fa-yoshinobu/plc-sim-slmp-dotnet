using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Protocol;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.ViewModels;

public sealed record LogRow(FrameLogEntry Entry, string Time, string Session, string Command, string Summary, string EndCode, bool IsError, bool IsInjected);

public sealed record FieldRow(string Name, string Value);

/// <summary>通信ログ: 要求/応答の一覧と伝文の分解表示。</summary>
public sealed partial class LogViewModel : ObservableObject
{
    private const int MaxRows = 2000;
    private readonly SimulatorRuntime _runtime;
    private FrameLogEntry? _lastSeen;

    public LogViewModel(SimulatorRuntime runtime)
    {
        _runtime = runtime;
    }

    public ObservableCollection<LogRow> Rows { get; } = new();

    public ObservableCollection<FieldRow> Fields { get; } = new();

    [ObservableProperty]
    private LogRow? _selected;

    [ObservableProperty]
    private bool _paused;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private string _requestHex = string.Empty;

    [ObservableProperty]
    private string _responseHex = string.Empty;

    [ObservableProperty]
    private string _detailTitle = "行を選ぶと伝文を表示します";

    public void Refresh()
    {
        if (Paused)
            return;
        var latest = _runtime.FrameLog.Latest(500);
        var start = 0;
        if (_lastSeen is not null)
        {
            var index = Array.LastIndexOf(latest.ToArray(), _lastSeen);
            start = index >= 0 ? index + 1 : 0;
        }

        for (var i = start; i < latest.Count; i++)
        {
            var row = ToRow(latest[i]);
            if (!Matches(row))
                continue;
            Rows.Insert(0, row);
        }

        if (latest.Count > 0)
            _lastSeen = latest[^1];
        while (Rows.Count > MaxRows)
            Rows.RemoveAt(Rows.Count - 1);
    }

    public void Clear()
    {
        Rows.Clear();
        _lastSeen = null;
        Selected = null;
    }

    [RelayCommand]
    private void ClearLog()
    {
        _runtime.FrameLog.Clear();
        Clear();
    }

    partial void OnFilterChanged(string value)
    {
        Rows.Clear();
        _lastSeen = null;
        Refresh();
    }

    partial void OnSelectedChanged(LogRow? value)
    {
        Fields.Clear();
        if (value is null)
        {
            RequestHex = ResponseHex = string.Empty;
            DetailTitle = "行を選ぶと伝文を表示します";
            return;
        }

        var e = value.Entry;
        var r = e.Request;
        DetailTitle = $"伝文 {e.Time:HH:mm:ss.fff}（{(r.Frame == SlmpFrameKind.Frame4E ? "4E" : "3E")} {(r.IsAscii ? "ASCII" : "バイナリ")}）";
        RequestHex = Hex(r.Raw, r.IsAscii);
        ResponseHex = e.Response is null ? "（応答なし）" : Hex(e.Response, r.IsAscii);
        if (r.Frame == SlmpFrameKind.Frame4E)
            Fields.Add(new("シリアル", $"{r.Serial:X4}h"));
        Fields.Add(new("宛先", $"NW {r.Network:X2} / 局 {r.Station:X2} / {r.ModuleIo:X4} / {r.Multidrop:X2}"));
        Fields.Add(new("監視タイマ", $"{r.MonitoringTimer:X4}h（{r.MonitoringTimer * 250} ms）"));
        Fields.Add(new("コマンド", $"{r.Command:X4} {SlmpCommands.NameOf(r.Command)}"));
        Fields.Add(new("サブコマンド", $"{r.Subcommand:X4}{SubcommandText(r.Command, r.Subcommand)}"));
        Fields.Add(new("要求データ", $"{r.Data.Length} バイト"));
        Fields.Add(new("内容", e.Summary));
        Fields.Add(new("終了コード", $"{e.EndCode:X4} {EndCodes.Describe(e.EndCode)}"));
        Fields.Add(new("処理時間", $"{e.Elapsed.TotalMilliseconds:0.00} ms"));
        Fields.Add(new("接続", e.Session));
    }

    private bool Matches(LogRow row)
        => string.IsNullOrWhiteSpace(Filter) ||
           (row.Command + row.Summary + row.Session + row.EndCode).Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static LogRow ToRow(FrameLogEntry e)
        => new(
            e,
            e.Time.ToString("HH:mm:ss.fff"),
            e.Session,
            $"{e.Request.Command:X4}/{e.Request.Subcommand:X4}",
            e.Summary,
            e.Response is null ? "無応答" : $"{e.EndCode:X4}",
            e.EndCode != 0 || e.Response is null,
            e.Summary.Contains("注入", StringComparison.Ordinal));

    private static string SubcommandText(ushort command, ushort sub)
    {
        if (command is not (SlmpCommands.DeviceRead or SlmpCommands.DeviceWrite or SlmpCommands.DeviceReadRandom or SlmpCommands.DeviceWriteRandom
            or SlmpCommands.DeviceReadBlock or SlmpCommands.DeviceWriteBlock or SlmpCommands.MonitorRegister))
        {
            return string.Empty;
        }

        var parts = new List<string> { (sub & 1) != 0 ? "ビット単位" : "ワード単位", (sub & 2) != 0 ? "iQ-R 形式" : "Q/L 形式" };
        if ((sub & 0x80) != 0)
            parts.Add("拡張指定");
        return "（" + string.Join("・", parts) + "）";
    }

    private static string Hex(byte[] data, bool ascii)
    {
        if (ascii)
            return Encoding.ASCII.GetString(data);
        var sb = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0)
                sb.Append(i % 16 == 0 ? '\n' : ' ');
            sb.Append(data[i].ToString("X2"));
        }

        return sb.ToString();
    }
}
