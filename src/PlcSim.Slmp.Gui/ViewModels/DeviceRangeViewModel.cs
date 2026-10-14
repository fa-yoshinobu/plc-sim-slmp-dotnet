using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.ViewModels;

/// <summary>デバイス範囲の 1 行（ファミリ単位）。</summary>
public sealed partial class DeviceRangeRow : ObservableObject
{
    public required string Family { get; init; }

    public required string Devices { get; init; }

    public required string Description { get; init; }

    public required uint DefaultPoints { get; init; }

    public required int Radix { get; init; }

    public required string SdText { get; init; }

    /// <summary>1 点あたりのメモリ使用量（バイト）。</summary>
    public required double BytesPerPoint { get; init; }

    public required string Prefix { get; init; }

    /// <summary>同じメモリを使うデバイスの説明。</summary>
    public string SharedNote { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText), nameof(IsChanged), nameof(HasError))]
    private string _pointsText = string.Empty;

    public uint? Points => uint.TryParse(PointsText.Replace(",", string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    public bool HasError => Points is null;

    public bool IsChanged => Points is { } p && p != DefaultPoints;

    public string DefaultText => DefaultPoints.ToString("N0");

    public string RangeText => Points is { } p
        ? $"{Prefix}{Format(0)}〜{Prefix}{Format(p - 1)}"
        : "点数は 1 以上の整数で入力してください";

    private string Format(uint n) => Radix switch
    {
        16 => n.ToString("X"),
        8 => Convert.ToString(n, 8),
        _ => n.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// デバイス範囲（使用点数の上限）の設定画面。実機のパラメータ設定に合わせて点数を変え、
/// 適用するとメモリ内容を引き継いで仮想 PLC を作り直す（SD の点数レジスタも更新）。
/// </summary>
public sealed partial class DeviceRangeViewModel : ObservableObject
{
    private static readonly Dictionary<string, (string Devices, string Description)> FamilyInfo = new()
    {
        ["X"] = ("X / DX", "入力（DX と同じメモリ）"), ["Y"] = ("Y / DY", "出力（DY と同じメモリ）"), ["DX"] = ("DX", "ダイレクト入力（X と共有）"), ["DY"] = ("DY", "ダイレクト出力（Y と共有）"),
        ["M"] = ("M", "内部リレー"), ["L"] = ("L", "ラッチリレー"), ["F"] = ("F", "アナンシェータ"), ["V"] = ("V", "エッジリレー"),
        ["B"] = ("B", "リンクリレー"), ["SB"] = ("SB", "リンク特殊リレー"), ["S"] = ("S", "ステップリレー"),
        ["D"] = ("D", "データレジスタ"), ["W"] = ("W", "リンクレジスタ"), ["SW"] = ("SW", "リンク特殊レジスタ"),
        ["T"] = ("TS / TC / TN", "タイマ"), ["ST"] = ("STS / STC / STN", "積算タイマ"), ["C"] = ("CS / CC / CN", "カウンタ"),
        ["LT"] = ("LTS / LTC / LTN", "ロングタイマ"), ["LST"] = ("LSTS / LSTC / LSTN", "ロング積算タイマ"), ["LC"] = ("LCS / LCC / LCN", "ロングカウンタ"),
        ["Z"] = ("Z", "インデックスレジスタ"), ["LZ"] = ("LZ", "ロングインデックスレジスタ"),
        ["R"] = ("R", "ファイルレジスタ（ブロック切換）"), ["ZR"] = ("ZR / R", "ファイルレジスタ（R は先頭 32768 点）"), ["RD"] = ("RD", "リフレッシュデータレジスタ"),
        ["SM"] = ("SM", "特殊リレー"), ["SD"] = ("SD", "特殊レジスタ"),
    };

    private readonly SimulatorRuntime _runtime;
    private string _loadedState = string.Empty;
    private string _loadedProfile = string.Empty;

    private string EditState => JsonSerializer.Serialize(new
    {
        Points = Rows.Select(static r => new { r.Family, r.PointsText }),
        Units,
        Networks,
    });

    public bool HasPendingChanges => EditState != _loadedState;

    public DeviceRangeViewModel(SimulatorRuntime runtime)
    {
        _runtime = runtime;
        Load();
    }

    public ObservableCollection<DeviceRangeRow> Rows { get; } = new();

    /// <summary>装着ユニット（"U0" など）。最大 4 個。</summary>
    public ObservableCollection<string> Units { get; } = new();

    /// <summary>リンクダイレクトのネットワーク（"J1" など）。最大 4 個。</summary>
    public ObservableCollection<string> Networks { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddUnitCommand))]
    private string _newUnit = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddNetworkCommand))]
    private string _newNetwork = string.Empty;

    public string UnitCountText => $"{Units.Count} / {PlcSettings.MaxUnits}";

    public string NetworkCountText => $"{Networks.Count} / {PlcSettings.MaxNetworks}";

    [ObservableProperty]
    private string _profileText = string.Empty;

    [ObservableProperty]
    private string _memoryText = string.Empty;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _busy;

    public void Load(bool preserveEdits = false)
    {
        if (preserveEdits && _loadedProfile == _runtime.Plc.Profile.Id && HasPendingChanges)
            return;
        Units.Clear();
        foreach (var unit in _runtime.Plc.Settings.Units.OrderBy(static u => u))
            Units.Add($"U{unit:X}");
        Networks.Clear();
        foreach (var network in _runtime.Plc.Settings.Networks.OrderBy(static n => n))
            Networks.Add($"J{network}");
        RaiseUnitState();
        Rows.Clear();
        var current = _runtime.Plc.Profile;
        var defaults = CpuProfiles.Get(current.Id);
        ProfileText = $"{current.DisplayName}（{current.Id}）";
        // DX/DY は X/Y、R は ZR の先頭（最大 32768 点）と同じメモリなので個別には設定しない。
        var shared = new HashSet<string> { "G", "HG", "DX", "DY" };
        if (current.FileRegisterShared && current.Has("ZR"))
            shared.Add("R");
        foreach (var family in defaults.Points.Keys.Where(f => !shared.Contains(f)).OrderBy(Order))
        {
            var def = DeviceCatalog.All.First(d => d.Family == family);
            var info = FamilyInfo.TryGetValue(family, out var i) ? i : (family, def.Description);
            var sd = current.SdPointRules.FirstOrDefault(r => r.Family == family);
            var row = new DeviceRangeRow
            {
                Family = family,
                Devices = info.Item1,
                Description = info.Item2,
                DefaultPoints = defaults.PointsOf(family),
                Radix = (int)current.RadixOf(def),
                Prefix = family is "T" or "ST" or "C" or "LT" or "LST" or "LC" ? info.Item1.Split(" / ")[^1] : family,
                SharedNote = family switch
                {
                    "X" or "Y" when current.Has("D" + family) => $"D{family} も同じ範囲",
                    "ZR" when current.FileRegisterShared && current.Has("R") => "R = 先頭 min(点数, 32768)",
                    _ => string.Empty,
                },
                SdText = sd is null ? "—" : sd.DoubleWord ? $"SD{sd.Register}〜SD{sd.Register + 1}" : $"SD{sd.Register}",
                BytesPerPoint = family switch
                {
                    "T" or "ST" or "C" => 2 + 2,
                    "LT" or "LST" => 8 + 2,
                    "LC" => 4 + 2,
                    "LZ" => 4,
                    _ => def.Kind == DeviceKind.Bit ? 1 : 2,
                },
                PointsText = current.PointsOf(family).ToString(CultureInfo.InvariantCulture),
            };
            row.PropertyChanged += (_, _) => UpdateMemoryText();
            Rows.Add(row);
        }

        UpdateMemoryText();
        _loadedState = EditState;
        _loadedProfile = current.Id;
    }

    private bool CanAddUnit() => Units.Count < PlcSettings.MaxUnits && !string.IsNullOrWhiteSpace(NewUnit);

    /// <summary>ユニット番号を追加する（16 進。"2" / "U2" / "1F"）。</summary>
    [RelayCommand(CanExecute = nameof(CanAddUnit))]
    private void AddUnit()
    {
        var text = NewUnit.Trim().ToUpperInvariant().TrimStart('U');
        if (!ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unit) || unit > 0xFF)
        {
            Message = "ユニット番号は 16 進の 0〜FF で入力してください（例: 2, 1F）。U3E0〜U3E3 は CPU バッファメモリのため登録不要です。";
            return;
        }

        var key = $"U{unit:X}";
        if (Units.Contains(key))
        {
            Message = $"{key} は登録済みです。";
            return;
        }

        Units.Add(key);
        NewUnit = string.Empty;
        RaiseUnitState();
    }

    [RelayCommand]
    private void RemoveUnit(string? unit)
    {
        if (unit is not null && Units.Count > 1)
            Units.Remove(unit);
        else if (unit is not null)
            Message = "ユニットは 1 個以上残してください。";
        RaiseUnitState();
    }

    private bool CanAddNetwork() => Networks.Count < PlcSettings.MaxNetworks && !string.IsNullOrWhiteSpace(NewNetwork);

    /// <summary>ネットワーク番号を追加する（10 進。"2" / "J2"）。</summary>
    [RelayCommand(CanExecute = nameof(CanAddNetwork))]
    private void AddNetwork()
    {
        var text = NewNetwork.Trim().ToUpperInvariant().TrimStart('J');
        if (!byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var network) || network is < 1 or > 239)
        {
            Message = "ネットワーク番号は 1〜239 で入力してください（例: 2）。";
            return;
        }

        var key = $"J{network}";
        if (Networks.Contains(key))
        {
            Message = $"{key} は登録済みです。";
            return;
        }

        Networks.Add(key);
        NewNetwork = string.Empty;
        RaiseUnitState();
    }

    [RelayCommand]
    private void RemoveNetwork(string? network)
    {
        if (network is not null && Networks.Count > 1)
            Networks.Remove(network);
        else if (network is not null)
            Message = "ネットワークは 1 個以上残してください。";
        RaiseUnitState();
    }

    private void RaiseUnitState()
    {
        OnPropertyChanged(nameof(UnitCountText));
        OnPropertyChanged(nameof(NetworkCountText));
        AddUnitCommand.NotifyCanExecuteChanged();
        AddNetworkCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        foreach (var row in Rows)
            row.PointsText = row.DefaultPoints.ToString(CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (Rows.Any(static r => r.HasError))
        {
            Message = "点数が正しくない行があります。";
            return;
        }

        Busy = true;
        try
        {
            var config = ConfigEditing.Clone(_runtime.Config);
            config.Plc.DevicePoints = Rows.Where(static row => row.IsChanged)
                .ToDictionary(static row => row.Family, static row => row.Points!.Value, StringComparer.OrdinalIgnoreCase);
            config.ErrorInjection = _runtime.Engine.Injector.Rules.ToList();
            config.Plc.Units = Units.Select(static u => u[1..]).ToList();
            config.Plc.Networks = Networks.Select(static n => int.Parse(n[1..], CultureInfo.InvariantCulture)).ToList();
            await _runtime.RebuildAsync(config, keepMemory: true);
            Load();
            Message = $"適用しました（{DateTime.Now:HH:mm:ss}）。範囲外になった値は破棄しました。設定ファイルに残すには「保存」を押してください。";
        }
        catch (Exception ex)
        {
            Message = $"適用できません: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _runtime.SaveConfig();
            Message = $"設定ファイルに保存しました: {_runtime.ConfigPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存できません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateMemoryText()
    {
        var bytes = Rows.Sum(static r => (r.Points ?? 0) * r.BytesPerPoint);
        var changed = Rows.Count(static r => r.IsChanged);
        MemoryText = $"仮想メモリ使用量 約 {bytes / 1024 / 1024:0.0} MB　既定から変更 {changed} 件";
    }

    private static int Order(string family)
    {
        var order = new[] { "X", "Y", "DX", "DY", "M", "L", "F", "V", "B", "SB", "S", "D", "W", "SW", "T", "ST", "C", "LT", "LST", "LC", "Z", "LZ", "R", "ZR", "RD", "SM", "SD" };
        var index = Array.IndexOf(order, family);
        return index < 0 ? 99 : index;
    }
}
