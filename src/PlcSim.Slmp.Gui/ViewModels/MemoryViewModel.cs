using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.ViewModels;

/// <summary>
/// メモリビューア: MELSEC の全領域を実機と同じ点数・番地で表示する。
/// 表示行だけを作り直す方式（スクロール位置 = 先頭行番号）なので、ZR のような数百万点の領域も扱える。
/// </summary>
public sealed partial class MemoryViewModel : ObservableObject
{
    private const int MaxNonZeroRows = 20000;
    private static readonly TimeSpan RecentSpan = TimeSpan.FromSeconds(2);

    private static readonly (string Group, string[] Families)[] DeviceGroups =
    {
        ("入出力", new[] { "X", "Y" }),
        ("内部リレー", new[] { "M", "L", "F", "V", "B", "SB", "S" }),
        ("ワード", new[] { "D", "W", "SW" }),
        ("タイマ・カウンタ", new[] { "T", "ST", "C", "LT", "LST", "LC" }),
        ("インデックス", new[] { "Z", "LZ" }),
        ("ファイルレジスタ", new[] { "R", "ZR", "RD" }),
        ("特殊", new[] { "SM", "SD" }),
    };

    private static readonly Dictionary<string, (AreaKind Kind, string[] Parts, string Description)> Composite = new()
    {
        ["T"] = (AreaKind.Timer, new[] { "TS", "TC", "TN" }, "タイマ（接点 TS / コイル TC / 現在値 TN）"),
        ["ST"] = (AreaKind.Timer, new[] { "STS", "STC", "STN" }, "積算タイマ（STS / STC / STN）"),
        ["C"] = (AreaKind.Timer, new[] { "CS", "CC", "CN" }, "カウンタ（CS / CC / CN）"),
        ["LT"] = (AreaKind.LongTimer, new[] { "LTS", "LTC", "LTN" }, "ロングタイマ（LTN 4 ワード構造）"),
        ["LST"] = (AreaKind.LongTimer, new[] { "LSTS", "LSTC", "LSTN" }, "ロング積算タイマ（LSTN 4 ワード構造）"),
        ["LC"] = (AreaKind.LongCounter, new[] { "LCS", "LCC", "LCN" }, "ロングカウンタ（LCS / LCC / LCN）"),
    };

    private readonly SimulatorRuntime _runtime;
    private List<int>? _nonZeroRows;
    private string _recentSignature = string.Empty;
    private bool _suspend;
    private PlcModel? _observedPlc;
    private int _nonZeroDirty = 1;

    public MemoryViewModel(SimulatorRuntime runtime)
    {
        _runtime = runtime;
        Rebuild();
    }

    public ObservableCollection<MemoryArea> Areas { get; } = new();

    public ObservableCollection<MemoryRow> Rows { get; } = new();

    public ObservableCollection<string> Headers { get; } = new();

    public ObservableCollection<RecentItem> Recent { get; } = new();

    public ObservableCollection<double> Marks { get; } = new();

    /// <summary>登録済みユニット（"U0", "U2" …）。メモリビューアの Un\G で切り替える。</summary>
    public ObservableCollection<string> UnitChoices { get; } = new();

    /// <summary>登録済みネットワーク（"J1" …）。Jn\X/Y/B/SB/W/SW で切り替える。</summary>
    public ObservableCollection<string> NetworkChoices { get; } = new();

    /// <summary>CPU バッファメモリ（マルチ CPU の CPU No.1〜4 = U3E0〜U3E3、登録不要）。</summary>
    public IReadOnlyList<string> CpuChoices { get; } = new[] { "U3E0", "U3E1", "U3E2", "U3E3" };

    [ObservableProperty]
    private string _selectedCpu = "U3E0";

    public bool ShowCpuSelector => SelectedArea?.Key is CpuAreaKey or CpuHgAreaKey;

    private const string CpuAreaKey = "U3E\\G";
    private const string CpuHgAreaKey = "U3E\\HG";

    [ObservableProperty]
    private string _selectedUnit = "U0";

    [ObservableProperty]
    private string _selectedNetwork = "J1";

    public bool ShowUnitSelector => SelectedArea?.Key == UnitAreaKey;

    public bool ShowNetworkSelector => SelectedArea?.Key.StartsWith(NetworkAreaPrefix, StringComparison.Ordinal) == true;

    private const string UnitAreaKey = "U\\G";
    private const string NetworkAreaPrefix = "J\\";

    [ObservableProperty]
    private MemoryArea? _selectedArea;

    [ObservableProperty]
    private string _areaFilter = string.Empty;

    [ObservableProperty]
    private string _jumpText = "D0";

    /// <summary>"dec" / "hex" / "s16" / "u32" / "f32" / "asc"。</summary>
    [ObservableProperty]
    private string _format = "dec";

    [ObservableProperty]
    private bool _nonZeroOnly;

    [ObservableProperty]
    private bool _readOnlyMode;

    [ObservableProperty]
    private string _recentKind = "all";

    /// <summary>先頭行の行番号（スクロールバーと連動）。</summary>
    [ObservableProperty]
    private double _topRow;

    [ObservableProperty]
    private double _maxTopRow;

    [ObservableProperty]
    private int _visibleRows = 20;

    [ObservableProperty]
    private int _columns = 8;

    [ObservableProperty]
    private string _viewRangeText = string.Empty;

    [ObservableProperty]
    private double _thumbStart;

    [ObservableProperty]
    private double _thumbSize = 0.01;

    [ObservableProperty]
    private string _selectedInfo = "セルをクリックすると最終書込み時刻と書込み元を表示します。ダブルクリックで値を変更します。";

    [ObservableProperty]
    private string? _message;

    public IEnumerable<MemoryArea> FilteredAreas => string.IsNullOrWhiteSpace(AreaFilter)
        ? Areas
        : Areas.Where(a => (a.Key + a.Description).Contains(AreaFilter.Trim(), StringComparison.OrdinalIgnoreCase));

    private int ItemsPerRow => SelectedArea?.Kind is AreaKind.Bit or AreaKind.Word ? Columns : 1;

    private long TotalRows => _nonZeroRows?.Count ?? (SelectedArea is null ? 0 : (SelectedArea.Points + ItemsPerRow - 1) / ItemsPerRow);

    /// <summary>プロファイル変更・再構築後に領域一覧を作り直す。</summary>
    public void Rebuild()
    {
        if (_observedPlc is { } oldPlc)
        {
            oldPlc.Memory.Changed -= OnMemoryChanged;
            oldPlc.BufferChanged -= OnMemoryChanged;
            oldPlc.Labels.Changed -= OnLabelChanged;
        }
        _observedPlc = _runtime.Plc;
        _observedPlc.Memory.Changed += OnMemoryChanged;
        _observedPlc.BufferChanged += OnMemoryChanged;
        _observedPlc.Labels.Changed += OnLabelChanged;
        Interlocked.Exchange(ref _nonZeroDirty, 1);
        RefreshChoices();
        var previous = SelectedArea?.Key ?? "D";
        Areas.Clear();
        foreach (var area in BuildAreas())
            Areas.Add(area);
        OnPropertyChanged(nameof(FilteredAreas));
        SelectedArea = Areas.FirstOrDefault(a => a.Key == previous) ?? Areas.FirstOrDefault();
    }

    private void OnMemoryChanged(MemoryChange change) => Interlocked.Exchange(ref _nonZeroDirty, 1);

    private void OnLabelChanged(string path, string source) => Interlocked.Exchange(ref _nonZeroDirty, 1);

    /// <summary>領域キーと番号を指定して表示する（直近アクセス・他画面から）。</summary>
    public void JumpTo(string areaKey, uint number)
    {
        var area = FindArea(areaKey);
        if (area is null)
            return;
        NonZeroOnly = false;
        if (!ReferenceEquals(area, SelectedArea))
            SelectedArea = area;
        ScrollToNumber(number);
    }

    partial void OnSelectedAreaChanged(MemoryArea? value)
    {
        OnPropertyChanged(nameof(ShowUnitSelector));
        OnPropertyChanged(nameof(ShowNetworkSelector));
        OnPropertyChanged(nameof(ShowCpuSelector));
        _suspend = true;
        _nonZeroRows = null;
        TopRow = 0;
        _suspend = false;
        RebuildRows();
        if (NonZeroOnly)
        {
            ComputeNonZeroRows();
            Refresh();
        }
    }

    partial void OnAreaFilterChanged(string value) => OnPropertyChanged(nameof(FilteredAreas));

    partial void OnSelectedUnitChanged(string value)
    {
        if (UnitChoices.Contains(value))
            Rebuild();
    }

    partial void OnSelectedCpuChanged(string value)
    {
        if (CpuChoices.Contains(value))
            Rebuild();
    }

    partial void OnSelectedNetworkChanged(string value)
    {
        if (NetworkChoices.Contains(value))
            Rebuild();
    }

    private void RefreshChoices()
    {
        var settings = _runtime.Plc.Settings;
        var units = settings.Units.OrderBy(static u => u).Select(static u => $"U{u:X}").ToList();
        var networks = settings.Networks.OrderBy(static n => n).Select(static n => $"J{n}").ToList();
        if (!units.SequenceEqual(UnitChoices))
        {
            UnitChoices.Clear();
            foreach (var u in units)
                UnitChoices.Add(u);
        }

        if (!networks.SequenceEqual(NetworkChoices))
        {
            NetworkChoices.Clear();
            foreach (var n in networks)
                NetworkChoices.Add(n);
        }

        if (!UnitChoices.Contains(SelectedUnit) && UnitChoices.Count > 0)
            _selectedUnit = UnitChoices[0];
        if (!NetworkChoices.Contains(SelectedNetwork) && NetworkChoices.Count > 0)
            _selectedNetwork = NetworkChoices[0];
        OnPropertyChanged(nameof(SelectedUnit));
        OnPropertyChanged(nameof(SelectedNetwork));
    }

    partial void OnFormatChanged(string value) => Refresh();

    partial void OnNonZeroOnlyChanged(bool value)
    {
        _nonZeroRows = null;
        if (value)
            ComputeNonZeroRows();
        _suspend = true;
        TopRow = 0;
        _suspend = false;
        Refresh();
    }

    partial void OnTopRowChanged(double value)
    {
        var clamped = Math.Clamp(Math.Floor(value), 0, Math.Max(0, MaxTopRow));
        if (Math.Abs(clamped - value) > 0.0001)
        {
            TopRow = clamped;
            return;
        }

        Refresh();
    }

    partial void OnVisibleRowsChanged(int value) => RebuildRows();

    partial void OnRecentKindChanged(string value)
    {
        _recentSignature = string.Empty;
        RefreshRecent();
    }

    /// <summary>マウスホイール（行単位）。</summary>
    public void Scroll(int rows) => TopRow = Math.Clamp(TopRow + rows, 0, MaxTopRow);

    [RelayCommand]
    private void Jump()
    {
        Message = null;
        var text = JumpText.Trim();
        if (text.Length == 0)
            return;
        try
        {
            var address = _runtime.Controller.Parse(text);
            var key = address.Qualifier is not null
                ? $"{address.Qualifier}\\{address.Device.Name}"
                : Composite.ContainsKey(address.Device.Family) ? address.Device.Family
                : address.Device.Name is "DX" or "DY" ? address.Device.Name[1..]
                : address.Device.Name == "R" && _runtime.Plc.Profile.FileRegisterShared && _runtime.Plc.Profile.Has("ZR") ? "ZR"
                : address.Device.Name;
            var area = FindArea(key);
            if (area is null)
            {
                Message = $"{text} はこの機種にありません";
                return;
            }

            NonZeroOnly = false;
            SelectedArea = area;
            ScrollToNumber(address.Number);
        }
        catch (FormatException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenRecent(RecentItem? item)
    {
        if (item is not null)
            JumpTo(item.Area, item.Start);
    }

    /// <summary>セル選択時の情報表示。</summary>
    public void Select(MemoryCell cell)
    {
        var area = SelectedArea;
        if (area is null)
            return;
        if (area.Kind == AreaKind.Label)
        {
            SelectedInfo = $"{cell.LabelPath}";
            return;
        }

        var name = CellAddress(area, cell);
        var key = cell.Role switch
        {
            "contact" => area.Parts[0],
            "coil" => area.Parts[1],
            _ when area.Parts.Length > 0 => area.Parts[2],
            _ => area.AccessKeys[0],
        };
        var last = _runtime.Access.LastWrite(key, cell.Number);
        SelectedInfo = last is { } w
            ? $"{name} = {cell.Text}（最終書込み {w.Time:HH:mm:ss.fff}  {w.Source}）"
            : $"{name} = {cell.Text}（書込み記録なし）";
    }

    /// <summary>セルの値を書き換える。ビットは反転、ワードは入力値。</summary>
    public string? Edit(MemoryCell cell, string? input)
    {
        var area = SelectedArea;
        if (area is null || !cell.Editable)
            return null;
        if (ReadOnlyMode)
            return "読出し専用モードです。ツールバーで解除してください。";
        try
        {
            var controller = _runtime.Controller;
            if (area.Kind == AreaKind.Label)
            {
                WriteLabel(cell.LabelPath!, input ?? string.Empty);
                Refresh();
                return null;
            }

            var address = CellAddress(area, cell);
            if (cell.IsBit)
            {
                controller.SetBit(address, !cell.IsOn, "GUI");
            }
            else if (area.Key == "自局")
            {
                ushort[] values;
                if (Format is "u32" or "f32")
                {
                    var value = Format == "f32"
                        ? BitConverter.SingleToUInt32Bits(float.Parse(input!, CultureInfo.InvariantCulture))
                        : ParseDWord(input!);
                    values = [(ushort)value, (ushort)(value >> 16)];
                }
                else
                    values = [Format == "asc" ? ParseAsciiWord(input!) : ParseWord(input!, Format == "s16")];
                lock (_runtime.Plc.SyncRoot)
                    _runtime.Plc.Buffers.OwnMemory.Write(cell.Number, values);
                _runtime.Plc.RaiseBufferChanged(new MemoryChange("自局", cell.Number, values.Length, "GUI", DateTime.Now));
            }
            else if (area.Kind is AreaKind.LongTimer or AreaKind.LongCounter or AreaKind.DoubleWord && cell.Role == "value")
            {
                controller.SetDWord(address, ParseDWord(input!), "GUI");
            }
            else if (Format == "f32" && area.Kind == AreaKind.Word)
            {
                controller.SetFloat(address, float.Parse(input!, CultureInfo.InvariantCulture), "GUI");
            }
            else if (Format == "u32" && area.Kind == AreaKind.Word)
            {
                controller.SetDWord(address, ParseDWord(input!), "GUI");
            }
            else if (Format == "asc" && area.Kind == AreaKind.Word)
            {
                controller.SetWord(address, ParseAsciiWord(input ?? string.Empty), "GUI");
            }
            else
            {
                controller.SetWord(address, ParseWord(input!, Format == "s16" && area.Kind == AreaKind.Word), "GUI");
            }

            Refresh();
            return null;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or SlmpEndCodeException or ArgumentException)
        {
            return ex.Message;
        }
    }

    /// <summary>入力欄の初期値。</summary>
    public string EditText(MemoryCell cell)
    {
        if (Format == "hex" && SelectedArea?.Kind is AreaKind.Word or AreaKind.DoubleWord)
            return "0x" + cell.Text;
        if (Format == "asc" && SelectedArea is { Kind: AreaKind.Word } area && cell.Text.Contains('·'))
        {
            // 制御文字の表示用記号を実際の文字として書き戻さない。
            lock (_runtime.Plc.SyncRoot)
                return $"0x{ReadWords(area, cell.Number, 1)[0]:X4}";
        }
        return cell.Text;
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var area = SelectedArea;
        if (area is null)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{area.Key.Replace("\\", "_")}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV|*.csv",
        };
        if (dialog.ShowDialog() != true)
            return;
        var sb = new StringBuilder();
        sb.AppendLine("番地," + string.Join(",", Headers));
        foreach (var row in Rows.Where(static r => r.IsVisible))
            sb.AppendLine(row.Address + "," + string.Join(",", row.Cells.Take(Headers.Count).Select(static c => c.Text)));
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        Message = $"表示中の {Rows.Count(static r => r.IsVisible)} 行を保存しました: {dialog.FileName}";
    }

    /// <summary>200ms ごとの更新。</summary>
    public void Refresh()
    {
        var area = SelectedArea;
        if (area is null || _suspend)
            return;
        long top = 0;
        var recent = RecentWrites(area);
        try
        {
            lock (_runtime.Plc.SyncRoot)
            {
                _runtime.Plc.RefreshSpecialDevices();
                if (NonZeroOnly && (Interlocked.Exchange(ref _nonZeroDirty, 0) != 0 || area.Key is "SD" or "SM"))
                    ComputeNonZeroRows();
                MaxTopRow = Math.Max(0, TotalRows - VisibleRows);
                top = (long)Math.Clamp(TopRow, 0, MaxTopRow);
                if (TopRow != top)
                {
                    _suspend = true;
                    TopRow = top;
                    _suspend = false;
                }
                for (var i = 0; i < Rows.Count; i++)
                {
                    var rowIndex = top + i;
                    if (rowIndex >= TotalRows)
                    {
                        Rows[i].IsVisible = false;
                        continue;
                    }

                    var row = Rows[i];
                    row.IsVisible = true;
                    var logical = _nonZeroRows is null ? rowIndex : _nonZeroRows[(int)rowIndex];
                    FillRow(area, row, (uint)(logical * ItemsPerRow), recent);
                }
            }
        }
        catch (SlmpEndCodeException ex)
        {
            Message = ex.Message;
        }

        UpdateRangeBar(area, top);
        RefreshRecent();
    }

    private void RebuildRows()
    {
        var area = SelectedArea;
        Headers.Clear();
        Rows.Clear();
        if (area is null)
            return;
        Columns = area.Kind switch
        {
            AreaKind.Bit => 16,
            AreaKind.Word => area.Radix == 16 ? 16 : 8,
            AreaKind.Timer or AreaKind.LongCounter => 3,
            AreaKind.LongTimer => 4,
            AreaKind.DoubleWord => 1,
            _ => 3,
        };
        switch (area.Kind)
        {
            case AreaKind.Bit:
            case AreaKind.Word:
                for (var i = 0; i < Columns; i++)
                    Headers.Add("+" + (area.Radix == 16 || area.Kind == AreaKind.Bit ? i.ToString("X") : i.ToString()));
                break;
            case AreaKind.Timer:
            case AreaKind.LongCounter:
                Headers.Add($"接点 {area.Parts[0]}");
                Headers.Add($"コイル {area.Parts[1]}");
                Headers.Add($"現在値 {area.Parts[2]}");
                break;
            case AreaKind.LongTimer:
                Headers.Add($"接点 {area.Parts[0]}");
                Headers.Add($"コイル {area.Parts[1]}");
                Headers.Add($"現在値 {area.Parts[2]}（32bit）");
                Headers.Add("状態ワード");
                break;
            case AreaKind.DoubleWord:
                Headers.Add("値（32bit）");
                break;
            case AreaKind.Label:
                Headers.Add("型");
                Headers.Add("値");
                Headers.Add("16進");
                break;
        }

        for (var i = 0; i < Math.Max(1, VisibleRows); i++)
            Rows.Add(new MemoryRow(Columns));
        OnPropertyChanged(nameof(SelectedArea));
        Refresh();
    }

    private void FillRow(MemoryArea area, MemoryRow row, uint start, IReadOnlyList<(uint Start, int Count)> recent)
    {
        bool IsRecent(uint n) => recent.Any(r => n >= r.Start && n < r.Start + (uint)Math.Max(1, r.Count));
        var hasValue = false;
        switch (area.Kind)
        {
            case AreaKind.Bit:
                {
                    row.Address = area.Prefix + area.FormatNumber(start);
                    var count = (int)Math.Min(Columns, area.Points - start);
                    var bits = ReadBits(area, start, count);
                    for (var i = 0; i < Columns; i++)
                    {
                        var cell = row.Cells[i];
                        cell.IsBit = true;
                        cell.Number = start + (uint)i;
                        cell.Editable = i < count && !area.ReadOnly;
                        cell.IsOn = i < count && bits[i];
                        cell.Text = i < count ? (bits[i] ? "ON" : "·") : string.Empty;
                        cell.IsRecent = i < count && IsRecent(cell.Number);
                        hasValue |= cell.IsOn;
                    }

                    break;
                }

            case AreaKind.Word:
                {
                    row.Address = area.Prefix + area.FormatNumber(start);
                    var count = (int)Math.Min(Columns, area.Points - start);
                    var extra = Format is "u32" or "f32" && start + (uint)count < area.Points ? 1 : 0;
                    var words = ReadWords(area, start, count + extra);
                    for (var i = 0; i < Columns; i++)
                    {
                        var cell = row.Cells[i];
                        cell.IsBit = false;
                        cell.Number = start + (uint)i;
                        cell.Editable = i < count && !area.ReadOnly &&
                            (Format is not ("u32" or "f32") || (i % 2 == 0 && i + 1 < count));
                        cell.Text = i < count ? FormatWord(words, i) : string.Empty;
                        cell.IsRecent = i < count && IsRecent(cell.Number);
                        hasValue |= i < count && words[i] != 0;
                    }

                    break;
                }

            case AreaKind.Timer:
            case AreaKind.LongTimer:
            case AreaKind.LongCounter:
                {
                    row.Address = area.Prefix + start;
                    var memory = _runtime.Plc.Memory;
                    var contact = memory.ReadBits(Def(area.Parts[0]), start, 1)[0];
                    var coil = memory.ReadBits(Def(area.Parts[1]), start, 1)[0];
                    uint value;
                    ushort status = 0;
                    if (area.Kind == AreaKind.LongTimer)
                    {
                        var w = memory.ReadWords(Def(area.Parts[2]), start, 4);
                        value = (uint)(w[0] | (w[1] << 16));
                        status = w[2];
                    }
                    else if (area.Kind == AreaKind.LongCounter)
                    {
                        value = memory.ReadDWordPoint(Def(area.Parts[2]), start);
                    }
                    else
                    {
                        value = memory.ReadWords(Def(area.Parts[2]), start, 1)[0];
                    }

                    SetBitCell(row.Cells[0], start, contact, "contact", recent: false);
                    SetBitCell(row.Cells[1], start, coil, "coil", recent: false);
                    var valueCell = row.Cells[2];
                    valueCell.IsBit = false;
                    valueCell.Role = "value";
                    valueCell.Number = start;
                    valueCell.Editable = true;
                    valueCell.Text = value.ToString("N0");
                    valueCell.IsRecent = IsRecent(start);
                    if (area.Kind == AreaKind.LongTimer)
                    {
                        var statusCell = row.Cells[3];
                        statusCell.IsBit = false;
                        statusCell.Role = "status";
                        statusCell.Editable = false;
                        statusCell.Text = $"{status:X4}h";
                    }

                    hasValue = contact || coil || value != 0;
                    break;
                }

            case AreaKind.DoubleWord:
                {
                    row.Address = area.Prefix + start;
                    var value = _runtime.Plc.Memory.ReadDWordPoint(area.Device!, start);
                    var cell = row.Cells[0];
                    cell.Number = start;
                    cell.Role = "value";
                    cell.Text = Format == "hex" ? $"{value:X8}" : value.ToString();
                    cell.IsRecent = IsRecent(start);
                    hasValue = value != 0;
                    break;
                }

            case AreaKind.Label:
                {
                    var labels = _runtime.Plc.Labels.Snapshot();
                    if (start >= labels.Count)
                        break;
                    var label = labels[(int)start];
                    row.Address = label.Path;
                    row.Cells[0].Text = label.Type;
                    row.Cells[0].Editable = false;
                    row.Cells[1].Text = LabelValueText(label);
                    row.Cells[1].LabelPath = label.Path;
                    row.Cells[1].Role = "label";
                    row.Cells[1].Editable = true;
                    row.Cells[2].Text = Convert.ToHexString(label.Data);
                    row.Cells[2].Editable = false;
                    hasValue = label.Data.Any(static b => b != 0);
                    break;
                }
        }

        row.HasValue = hasValue;

        void SetBitCell(MemoryCell cell, uint number, bool on, string role, bool recent)
        {
            cell.IsBit = true;
            cell.Role = role;
            cell.Number = number;
            cell.Editable = true;
            cell.IsOn = on;
            cell.Text = on ? "ON" : "·";
            cell.IsRecent = recent;
        }
    }

    private string FormatWord(ushort[] words, int i)
    {
        var v = words[i];
        switch (Format)
        {
            case "hex":
                return v.ToString("X4");
            case "s16":
                return ((short)v).ToString();
            case "asc":
                return $"{AsciiChar((byte)v)}{AsciiChar((byte)(v >> 8))}";
            case "u32":
            case "f32":
                if (i % 2 == 1 || i + 1 >= words.Length)
                    return string.Empty;
                var d = (uint)(v | (words[i + 1] << 16));
                return Format == "u32" ? d.ToString() : BitConverter.Int32BitsToSingle((int)d).ToString("R", CultureInfo.InvariantCulture);
            default:
                return v.ToString();
        }

        static char AsciiChar(byte b) => b is >= 32 and < 127 ? (char)b : '·';
    }

    private bool[] ReadBits(MemoryArea area, uint start, int count)
    {
        if (count <= 0)
            return Array.Empty<bool>();
        if (area.Qualifier is not null)
            return _runtime.Controller.Accessor(new DeviceAddress(area.Device!, start, area.Qualifier)).ReadBits(count);
        return _runtime.Plc.Memory.ReadBits(area.Device!, start, count);
    }

    private ushort[] ReadWords(MemoryArea area, uint start, int count)
    {
        if (count <= 0)
            return Array.Empty<ushort>();
        if (area.Key == "自局")
            return _runtime.Plc.Buffers.OwnMemory.Read(start, count);
        if (area.Qualifier is not null)
            return _runtime.Controller.Accessor(new DeviceAddress(area.Device!, start, area.Qualifier)).ReadWords(count);
        return _runtime.Plc.Memory.ReadWords(area.Device!, start, count);
    }

    private string CellAddress(MemoryArea area, MemoryCell cell)
    {
        var name = cell.Role switch
        {
            "contact" => area.Parts[0],
            "coil" => area.Parts[1],
            "value" when area.Parts.Length > 0 => area.Parts[2],
            _ => area.Device?.Name ?? area.Key,
        };
        var number = area.Parts.Length > 0 ? cell.Number.ToString() : area.FormatNumber(cell.Number);
        return area.Qualifier is null ? $"{name}{number}" : $"{area.Qualifier}\\{name}{number}";
    }

    private IReadOnlyList<(uint Start, int Count)> RecentWrites(MemoryArea area)
    {
        var now = DateTime.Now;
        return area.AccessKeys
            .Where(static k => k is not ("TS" or "TC" or "STS" or "STC" or "CS" or "CC" or "LTS" or "LTC" or "LSTS" or "LSTC" or "LCS" or "LCC"))
            .SelectMany(k => _runtime.Access.WritesIn(k))
            .Where(w => now - w.Time < RecentSpan)
            .Select(static w => (w.Start, w.Count))
            .ToArray();
    }

    private void UpdateRangeBar(MemoryArea area, long top)
    {
        if (area.Kind == AreaKind.Label)
        {
            ViewRangeText = $"{area.Points} 項目";
            ThumbStart = 0;
            ThumbSize = 1;
            Marks.Clear();
            return;
        }

        if (_nonZeroRows is not null)
        {
            ViewRangeText = _nonZeroRows.Count == 0
                ? "0 以外の値はありません"
                : $"値のある {_nonZeroRows.Count:N0} 行{(_nonZeroRows.Count >= MaxNonZeroRows ? "（先頭のみ）" : string.Empty)}";
        }
        else
        {
            var first = (uint)(top * ItemsPerRow);
            var last = (uint)Math.Min(area.Points - 1, ((top + VisibleRows) * ItemsPerRow) - 1);
            ViewRangeText = $"表示中 {area.Prefix}{area.FormatNumber(first)}〜{area.Prefix}{area.FormatNumber(last)}";
            ThumbStart = (double)first / area.Points;
            ThumbSize = Math.Max(0.004, (double)(last - first + 1) / area.Points);
        }

        var marks = area.AccessKeys
            .SelectMany(k => _runtime.Access.WritesIn(k))
            .Select(w => (double)w.Start / area.Points)
            .Where(static f => f is >= 0 and <= 1)
            .Distinct()
            .Take(200)
            .ToArray();
        if (!marks.SequenceEqual(Marks))
        {
            Marks.Clear();
            foreach (var m in marks)
                Marks.Add(m);
        }
    }

    private void RefreshRecent()
    {
        var items = _runtime.Access.Latest(200)
            .Where(a => RecentKind == "all" || (RecentKind == "W") == (a.Kind == AccessKind.Write))
            .Take(100)
            .ToArray();
        var signature = string.Join("|", items.Take(30).Select(static a => $"{a.LastTime.Ticks}:{a.Times}"));
        if (signature == _recentSignature)
            return;
        _recentSignature = signature;
        Recent.Clear();
        foreach (var a in items)
        {
            Recent.Add(new RecentItem(
                a.Kind,
                a.Kind == AccessKind.Write ? "書込" : "読出",
                a.Display,
                a.LastTime.ToString("HH:mm:ss.fff"),
                a.Times > 1 ? $"×{a.Times}" : string.Empty,
                a.Source,
                a.Area,
                a.Start));
        }
    }

    private void ScrollToNumber(uint number)
    {
        RebuildRowsIfNeeded();
        var row = number / (uint)ItemsPerRow;
        MaxTopRow = Math.Max(0, TotalRows - VisibleRows);
        TopRow = Math.Clamp(row, 0, MaxTopRow);
        Refresh();
    }

    private void RebuildRowsIfNeeded()
    {
        if (Rows.Count == 0)
            RebuildRows();
    }

    private void ComputeNonZeroRows()
    {
        var area = SelectedArea;
        if (area is null)
            return;
        var rows = new SortedSet<int>();
        var per = ItemsPerRow;
        lock (_runtime.Plc.SyncRoot)
        {
            var memory = _runtime.Plc.Memory;
            void AddNonZeroWords(ushort[] words, int wordsPerPoint, uint points)
            {
                var limit = Math.Min(words.Length, (long)points * wordsPerPoint);
                for (var i = 0; i < limit && rows.Count < MaxNonZeroRows; i++)
                {
                    if (words[i] != 0)
                        rows.Add((int)(i / wordsPerPoint / per));
                }
            }

            void AddNonZeroBits(bool[] bits, uint points)
            {
                var limit = Math.Min(bits.Length, points);
                for (var i = 0; i < limit && rows.Count < MaxNonZeroRows; i++)
                {
                    if (bits[i])
                        rows.Add(i / per);
                }
            }

            switch (area.Kind)
            {
                case AreaKind.Label:
                    var labels = _runtime.Plc.Labels.Snapshot();
                    for (var i = 0; i < labels.Count; i++)
                    {
                        if (labels[i].Data.Any(static b => b != 0))
                            rows.Add(i);
                    }

                    break;
                case AreaKind.Timer:
                case AreaKind.LongTimer:
                case AreaKind.LongCounter:
                    foreach (var part in area.Parts)
                    {
                        var def = Def(part);
                        var key = memory.StorageKey(def);
                        if (def.Kind == DeviceKind.Bit && memory.BitAreas.TryGetValue(key, out var b))
                            AddNonZeroBits(b, area.Points);
                        else if (memory.WordAreas.TryGetValue(key, out var w))
                            AddNonZeroWords(w, def.Kind == DeviceKind.LongTimer ? 4 : def.Kind == DeviceKind.DoubleWord ? 2 : 1, area.Points);
                    }

                    break;
                default:
                    if (area.Key == "自局" || area.Qualifier is not null)
                    {
                        var store = area.Key == "自局" ? _runtime.Plc.Buffers.OwnMemory : StoreOf(area);
                        foreach (var address in store?.NonZeroWords.Keys ?? Enumerable.Empty<uint>())
                        {
                            if (area.Kind == AreaKind.Bit)
                            {
                                for (var b = 0u; b < 16; b++)
                                {
                                    if ((store!.NonZeroWords[address] & (1 << (int)b)) != 0)
                                        rows.Add((int)(((address * 16) + b) / (uint)per));
                                }
                            }
                            else
                            {
                                rows.Add((int)(address / (uint)per));
                            }
                        }
                    }
                    else if (area.Device is { } device)
                    {
                        var key = memory.StorageKey(device);
                        if (area.Kind == AreaKind.Bit && memory.BitAreas.TryGetValue(key, out var bits))
                            AddNonZeroBits(bits, area.Points);
                        else if (memory.WordAreas.TryGetValue(key, out var words))
                            AddNonZeroWords(words, area.Kind == AreaKind.DoubleWord ? 2 : 1, area.Points);
                    }

                    break;
            }
        }

        _nonZeroRows = rows.Take(MaxNonZeroRows).ToList();
    }

    private SparseWordStore? StoreOf(MemoryArea area)
    {
        if (area.Qualifier is null || area.Device is null)
            return null;
        var number = Convert.ToUInt16(area.Qualifier[1..], area.Qualifier[0] == 'U' ? 16 : 10);
        var buffers = _runtime.Plc.Buffers;
        return area.Qualifier[0] == 'J'
            ? buffers.Link((byte)number, area.Device.Name)
            : area.Device.Name == "HG" ? buffers.Hg(number) : buffers.Module(number);
    }

    /// <summary>
    /// 領域キー（"D", "LTN", "U2\\G", "J1\\W" など）から表示領域を探す。
    /// ユニット・ネットワーク指定は登録済みならその番号に切り替えて Un\\G / Jn\\… の領域を返す。
    /// </summary>
    private MemoryArea? FindArea(string key)
    {
        var area = Areas.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))
                   ?? Areas.FirstOrDefault(a => a.AccessKeys.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (area is not null || !key.Contains('\\'))
            return area;

        var parts = key.Split('\\');
        var qualifier = parts[0].ToUpperInvariant();
        if (CpuChoices.Contains(qualifier))
        {
            SelectedCpu = qualifier;
            return Areas.FirstOrDefault(a => a.Key == (parts[1].Equals("HG", StringComparison.OrdinalIgnoreCase) ? CpuHgAreaKey : CpuAreaKey));
        }

        if (qualifier.StartsWith('U') && UnitChoices.Contains(qualifier))
        {
            SelectedUnit = qualifier;
            return Areas.FirstOrDefault(static a => a.Key == UnitAreaKey);
        }

        if (qualifier.StartsWith('J') && NetworkChoices.Contains(qualifier))
        {
            SelectedNetwork = qualifier;
            return Areas.FirstOrDefault(a => a.Key == NetworkAreaPrefix + parts[1].ToUpperInvariant());
        }

        Message = $"{qualifier} は登録されていません。「デバイス範囲」画面で登録してください（最大 {PlcSettings.MaxUnits} 個）。";
        return null;
    }

    private static MemoryArea StoreArea(string key, string qualifier, DeviceDefinition def, string title, string description)
    {
        var link = qualifier.StartsWith('J');
        return new MemoryArea
        {
            Group = link ? "リンクダイレクト" : "バッファメモリ",
            Key = key,
            Title = title,
            Description = description,
            Points = link
                ? (def.Kind == DeviceKind.Bit ? BufferMemory.LinkDirectWords * 16 : BufferMemory.LinkDirectWords)
                : BufferMemory.ModuleBufferWords,
            Kind = link && def.Kind == DeviceKind.Bit ? AreaKind.Bit : AreaKind.Word,
            Radix = link ? 16 : 10,
            Prefix = $"{qualifier}\\{def.Name}",
            Device = def,
            Qualifier = qualifier,
            AccessKeys = new[] { $"{qualifier}\\{def.Name}" },
        };
    }

    private IEnumerable<MemoryArea> BuildAreas()
    {
        var plc = _runtime.Plc;
        var profile = plc.Profile;
        foreach (var (group, families) in DeviceGroups)
        {
            foreach (var family in families)
            {
                if (!profile.Has(family))
                    continue;
                var points = profile.PointsOf(family);
                if (Composite.TryGetValue(family, out var composite))
                {
                    yield return new MemoryArea
                    {
                        Group = group,
                        Key = family,
                        Title = family,
                        Description = composite.Description,
                        Points = points,
                        Kind = composite.Kind,
                        Prefix = composite.Parts[2],
                        Parts = composite.Parts,
                        Device = Def(composite.Parts[2]),
                        AccessKeys = composite.Parts,
                    };
                    continue;
                }

                if (family == "R" && profile.FileRegisterShared && profile.Has("ZR"))
                    continue;
                var def = Def(family);
                var description = def.Description;
                if (family == "ZR" && profile.FileRegisterShared && profile.Has("R"))
                    description += $"（R と同じメモリ。R は先頭 {profile.PointsOf("R"):N0} 点）";
                if (family is "X" or "Y" && profile.Has("D" + family))
                    description += $"（D{family} と同じメモリ）";
                var readOnly = profile.ReadOnlyDevices.Contains(family);
                yield return new MemoryArea
                {
                    Group = group,
                    Key = family,
                    Title = family,
                    Description = readOnly ? description + "（読出し専用）" : description,
                    Points = points,
                    Kind = def.Kind switch
                    {
                        DeviceKind.Bit => AreaKind.Bit,
                        DeviceKind.DoubleWord => AreaKind.DoubleWord,
                        _ => AreaKind.Word,
                    },
                    Radix = (int)profile.RadixOf(def),
                    ReadOnly = readOnly,
                    Prefix = family,
                    Device = def,
                    AccessKeys = family switch
                    {
                        "X" or "Y" => new[] { family, "D" + family },
                        "ZR" when profile.FileRegisterShared => new[] { "ZR", "R" },
                        _ => new[] { family },
                    },
                };
            }
        }

        // ユニットバッファ: 登録済みユニットを番号切替で表示（最大 4 個）
        if ((profile.Features.ModuleAccess || profile.Features.ExtendUnit) && UnitChoices.Count > 0)
            yield return StoreArea(UnitAreaKey, SelectedUnit, Def("G"), "Un\\G", $"ユニット {SelectedUnit} のバッファメモリ（登録 {UnitChoices.Count}/{PlcSettings.MaxUnits}）");

        // CPU バッファメモリ（iQ-R の CPU 内蔵、登録不要）
        if (profile.Series == CpuSeries.IqR && profile.Features.ModuleAccess)
            yield return StoreArea(CpuAreaKey, SelectedCpu, Def("G"), "U3En\\G", $"CPU バッファメモリ（{SelectedCpu}、U3E0〜U3E3）");
        if (profile.Features.HgCpuBuffer)
            yield return StoreArea(CpuHgAreaKey, SelectedCpu, Def("HG"), "U3En\\HG", $"CPU バッファメモリ定周期エリア（{SelectedCpu}、U3E0〜U3E3）");

        // リンクダイレクト: 登録済みネットワークを番号切替で表示（最大 4 個）
        if (profile.Features.LinkDirect && NetworkChoices.Count > 0)
        {
            foreach (var device in new[] { "X", "Y", "B", "SB", "W", "SW" })
            {
                var def = Def(device);
                yield return StoreArea(NetworkAreaPrefix + device, SelectedNetwork, def, $"Jn\\{device}",
                    $"ネットワーク {SelectedNetwork} の{def.Description}（登録 {NetworkChoices.Count}/{PlcSettings.MaxNetworks}）");
            }
        }

        if (profile.Features.MemoryAccess)
        {
            yield return new MemoryArea
            {
                Group = "その他",
                Key = "自局",
                Title = "自局",
                Description = "自局メモリ（0613/1613）",
                Points = BufferMemory.OwnMemoryWords,
                Kind = AreaKind.Word,
                Radix = 16,
                Prefix = "自局 ",
                AccessKeys = new[] { "自局" },
            };
        }

        var labelCount = plc.Labels.Snapshot().Count;
        if (labelCount > 0)
        {
            yield return new MemoryArea
            {
                Group = "その他",
                Key = "ラベル",
                Title = "ラベル",
                Description = string.Join(", ", plc.Labels.Definitions.Globals.Select(static g => g.Name)),
                Points = (uint)labelCount,
                Kind = AreaKind.Label,
                Prefix = string.Empty,
                AccessKeys = new[] { "ラベル" },
            };
        }
    }

    private static DeviceDefinition Def(string name) => DeviceCatalog.FindByName(name)!;

    private static string LabelValueText(LabelValueView label)
    {
        var d = label.Data;
        return label.Type.ToUpperInvariant() switch
        {
            "BIT" or "BOOL" => d[0] != 0 ? "ON" : "OFF",
            "WORD" => BinaryPrimitives.ReadUInt16LittleEndian(d).ToString(),
            "INT" => BinaryPrimitives.ReadInt16LittleEndian(d).ToString(),
            "DWORD" => BinaryPrimitives.ReadUInt32LittleEndian(d).ToString(),
            "DINT" => BinaryPrimitives.ReadInt32LittleEndian(d).ToString(),
            "REAL" or "FLOAT" => BinaryPrimitives.ReadSingleLittleEndian(d).ToString("R", CultureInfo.InvariantCulture),
            "LREAL" or "DOUBLE" => BinaryPrimitives.ReadDoubleLittleEndian(d).ToString("R", CultureInfo.InvariantCulture),
            _ => (label.Type.StartsWith("WSTRING(", StringComparison.OrdinalIgnoreCase) ? Encoding.Unicode : Encoding.ASCII).GetString(d).TrimEnd('\0'),
        };
    }

    private void WriteLabel(string path, string input)
    {
        var label = _runtime.Plc.Labels.Snapshot().First(l => l.Path == path);
        var data = new byte[label.Data.Length];
        switch (label.Type.ToUpperInvariant())
        {
            case "BIT":
            case "BOOL":
                data[0] = (byte)(label.Data[0] == 0 ? 1 : 0);
                break;
            case "WORD":
            case "INT":
                BinaryPrimitives.WriteUInt16LittleEndian(data, ParseWord(input, label.Type == "INT"));
                break;
            case "DWORD":
            case "DINT":
                BinaryPrimitives.WriteUInt32LittleEndian(data, ParseDWord(input, label.Type == "DINT"));
                break;
            case "REAL":
            case "FLOAT":
                BinaryPrimitives.WriteSingleLittleEndian(data, float.Parse(input, CultureInfo.InvariantCulture));
                break;
            case "LREAL":
            case "DOUBLE":
                BinaryPrimitives.WriteDoubleLittleEndian(data, double.Parse(input, CultureInfo.InvariantCulture));
                break;
            default:
                var wide = label.Type.StartsWith("WSTRING(", StringComparison.OrdinalIgnoreCase);
                var encoding = wide ? Encoding.Unicode : Encoding.ASCII;
                encoding.GetEncoder().Convert(input.AsSpan(), data.AsSpan(0, data.Length - (wide ? 2 : 1)),
                    true, out _, out _, out _);
                break;
        }

        _runtime.Controller.WriteLabel(path, data, "GUI");
    }

    private static ushort ParseWord(string text, bool signed = false)
        => unchecked((ushort)ParseInteger(text, signed ? short.MinValue : 0, signed ? short.MaxValue : ushort.MaxValue));

    private static ushort ParseAsciiWord(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ParseWord(text);
        var bytes = Encoding.ASCII.GetBytes(text.PadRight(2, '\0')[..2]);
        return (ushort)(bytes[0] | (bytes[1] << 8));
    }

    private static uint ParseDWord(string text, bool signed = false)
        => unchecked((uint)ParseInteger(text, signed ? int.MinValue : 0, signed ? int.MaxValue : uint.MaxValue));

    private static long ParseInteger(string text, long minimum, long maximum)
    {
        text = text.Trim().Replace(",", string.Empty);
        var hex = true;
        long value;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        else if (text.EndsWith('h') || text.EndsWith('H'))
            value = long.Parse(text[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        else
        {
            hex = false;
            value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
        if (hex && minimum < 0)
        {
            minimum = 0;
            maximum = maximum * 2 + 1;
        }
        if (value < minimum || value > maximum)
            throw new OverflowException($"値は {minimum}〜{maximum} の範囲で入力してください。");
        return value;
    }
}
