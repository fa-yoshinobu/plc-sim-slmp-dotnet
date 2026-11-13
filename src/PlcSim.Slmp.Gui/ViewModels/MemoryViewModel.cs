using static PlcSim.Slmp.Gui.ViewModels.MemoryAreaCatalog;
using System.Collections.ObjectModel;
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
    private static readonly TimeSpan RecentSpan = TimeSpan.FromSeconds(2);

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


    [ObservableProperty]
    private string _selectedUnit = "U0";

    [ObservableProperty]
    private string _selectedNetwork = "J1";

    public bool ShowUnitSelector => SelectedArea?.Key == UnitAreaKey;

    public bool ShowNetworkSelector => SelectedArea?.Key.StartsWith(NetworkAreaPrefix, StringComparison.Ordinal) == true;


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
        foreach (var area in MemoryAreaCatalog.BuildAreas(_runtime.Controller, SelectedUnit, SelectedCpu, SelectedNetwork))
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

        // Rebuild is already in progress; the generated setters would re-enter it.
#pragma warning disable MVVMTK0034
        if (!UnitChoices.Contains(SelectedUnit) && UnitChoices.Count > 0)
            _selectedUnit = UnitChoices[0];
        if (!NetworkChoices.Contains(SelectedNetwork) && NetworkChoices.Count > 0)
            _selectedNetwork = NetworkChoices[0];
#pragma warning restore MVVMTK0034
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
            var (key, number) = MemoryAreaCatalog.JumpTarget(_runtime.Controller, text);
            var area = FindArea(key);
            if (area is null)
            {
                Message = $"{text} はこの機種にありません";
                return;
            }

            NonZeroOnly = false;
            SelectedArea = area;
            ScrollToNumber(number);
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

        var name = MemoryData.CellAddress(area, cell);
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
        if (SelectedArea is not { } area || !cell.Editable) return null;
        if (ReadOnlyMode) return "読出し専用モードです。ツールバーで解除してください。";
        var error = MemoryEditor.Edit(_runtime.Controller, area, cell, input, Format);
        if (error is null) Refresh();
        return error;
    }

    private void ComputeNonZeroRows()
    {
        if (SelectedArea is { } area)
            _nonZeroRows = MemoryQuery.NonZeroRows(_runtime.Controller, area, ItemsPerRow);
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
                return $"0x{MemoryData.ReadWords(_runtime.Controller, area, cell.Number, 1)[0]:X4}";
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
        MemoryCsv.Write(dialog.FileName, Headers, Rows);
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
                    MemoryRowRenderer.FillRow(_runtime.Controller, Columns, Format, area, row, (uint)(logical * ItemsPerRow), recent);
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
        var layout = MemoryRowRenderer.Layout(area);
        Columns = layout.Columns;
        foreach (var header in layout.Headers) Headers.Add(header);

        for (var i = 0; i < Math.Max(1, VisibleRows); i++)
            Rows.Add(new MemoryRow(Columns));
        OnPropertyChanged(nameof(SelectedArea));
        Refresh();
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
                : $"値のある {_nonZeroRows.Count:N0} 行{(_nonZeroRows.Count >= MemoryQuery.MaxNonZeroRows ? "（先頭のみ）" : string.Empty)}";
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

}
