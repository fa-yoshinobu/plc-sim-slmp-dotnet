using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Server;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.ViewModels;

public sealed record ProfileOption(string Id, string Name);

public sealed record InjectionRow(ErrorInjectionRule Rule, string Text);

public sealed record ThemeOption(string Key, string Name);

public sealed record DataCodeOption(DataCodeSetting Value, string Name);

/// <summary>設定: 機種・待受・動作・HTTP API・Python・エラー注入。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SimulatorRuntime _runtime;

    public SettingsViewModel(SimulatorRuntime runtime)
    {
        _runtime = runtime;
        Profiles = new ObservableCollection<ProfileOption>(CpuProfiles.Ids.Select(static id => new ProfileOption(id, CpuProfiles.Get(id).DisplayName)));
        Load();
    }

    public ObservableCollection<ProfileOption> Profiles { get; }

    public ObservableCollection<InjectionRow> Injections { get; } = new();

    public IReadOnlyList<string> States { get; } = new[] { "Run", "Stop", "Pause" };

    public IReadOnlyList<DataCodeOption> DataCodes { get; } = new[]
    {
        new DataCodeOption(DataCodeSetting.Auto, "自動（バイナリと ASCII の両方）"),
        new DataCodeOption(DataCodeSetting.Binary, "バイナリ"),
        new DataCodeOption(DataCodeSetting.Ascii, "ASCII"),
    };

    public IReadOnlyList<ThemeOption> Themes { get; } = new[]
    {
        new ThemeOption("system", "システム（Windows の設定に従う）"),
        new ThemeOption("light", "ライト"),
        new ThemeOption("dark", "ダーク"),
    };

    /// <summary>表示テーマ。選ぶとすぐ切り替わる（保存は「設定ファイルに保存」）。</summary>
    [ObservableProperty] private ThemeOption? _theme;

    [ObservableProperty] private ProfileOption? _profile;
    [ObservableProperty] private string _modelName = string.Empty;
    [ObservableProperty] private string _modelCode = string.Empty;
    [ObservableProperty] private string _bindAddress = "127.0.0.1";
    [ObservableProperty] private bool _enableTcp = true;
    [ObservableProperty] private string _tcpPort = "1025";
    [ObservableProperty] private bool _enableUdp = true;
    [ObservableProperty] private string _udpPort = "1025";
    [ObservableProperty] private DataCodeOption? _dataCode;
    [ObservableProperty] private bool _runWriteAllowed = true;
    [ObservableProperty] private bool _remoteResetAllowed = true;
    [ObservableProperty] private string _remotePassword = string.Empty;
    [ObservableProperty] private string _initialState = "Run";
    [ObservableProperty] private bool _apiEnabled = true;
    [ObservableProperty] private string _apiPort = "5080";
    [ObservableProperty] private string _pythonExecutable = "python";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _busy;

    // エラー注入の入力欄
    [ObservableProperty] private string _injCommand = "0401";
    [ObservableProperty] private string _injSubcommand = string.Empty;
    [ObservableProperty] private string _injEndCode = "C051";
    [ObservableProperty] private string _injDelay = "0";
    [ObservableProperty] private bool _injDrop;
    [ObservableProperty] private string _injCount = "1";

    public void Load()
    {
        var c = _runtime.Config;
        Profile = Profiles.FirstOrDefault(p => p.Id == c.Plc.Profile) ?? Profiles[0];
        ModelName = c.Plc.ModelName ?? string.Empty;
        ModelCode = c.Plc.ModelCode is { } code ? code.ToString("X4") : string.Empty;
        BindAddress = c.Server.BindAddress;
        EnableTcp = c.Server.EnableTcp;
        TcpPort = c.Server.TcpPort.ToString(CultureInfo.InvariantCulture);
        EnableUdp = c.Server.EnableUdp;
        UdpPort = c.Server.UdpPort.ToString(CultureInfo.InvariantCulture);
        DataCode = DataCodes.First(d => d.Value == c.Server.DataCode);
        RunWriteAllowed = c.Plc.RunWriteAllowed;
        RemoteResetAllowed = c.Plc.RemoteResetAllowed;
        RemotePassword = c.Plc.RemotePassword ?? string.Empty;
        InitialState = c.Plc.InitialState.ToString();
        ApiEnabled = c.Api.Enabled;
        ApiPort = c.Api.Port.ToString(CultureInfo.InvariantCulture);
        PythonExecutable = c.PythonExecutable;
        _theme = Themes.FirstOrDefault(t => t.Key == c.Gui.Theme) ?? Themes[0];
        OnPropertyChanged(nameof(Theme));
        RefreshInjections();
    }

    public string ModelHint => Profile is null ? string.Empty : $"空欄なら {CpuProfiles.Get(Profile.Id).ModelName} / {CpuProfiles.Get(Profile.Id).ModelCode:X4}h";

    partial void OnProfileChanged(ProfileOption? value) => OnPropertyChanged(nameof(ModelHint));

    partial void OnThemeChanged(ThemeOption? value)
    {
        if (value is null)
            return;
        ThemeService.Apply(value.Key);
        _runtime.Config.Gui.Theme = value.Key;
    }

    [RelayCommand]
    private async Task Apply()
    {
        Message = null;
        try
        {
            var config = ConfigEditing.Clone(_runtime.Config);
            var profileChanged = config.Plc.Profile != Profile!.Id;
            config.Plc.Profile = Profile.Id;
            if (profileChanged)
                config.Plc.DevicePoints.Clear();
            config.Plc.ModelName = string.IsNullOrWhiteSpace(ModelName) ? null : ModelName.Trim();
            config.Plc.ModelCode = string.IsNullOrWhiteSpace(ModelCode) ? null : ushort.Parse(ModelCode.Trim().TrimEnd('h', 'H'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            config.Server.BindAddress = BindAddress.Trim();
            config.Server.EnableTcp = EnableTcp;
            config.Server.TcpPort = int.Parse(TcpPort, CultureInfo.InvariantCulture);
            config.Server.EnableUdp = EnableUdp;
            config.Server.UdpPort = int.Parse(UdpPort, CultureInfo.InvariantCulture);
            config.Server.DataCode = DataCode?.Value ?? DataCodeSetting.Auto;
            config.Plc.RunWriteAllowed = RunWriteAllowed;
            config.Plc.RemoteResetAllowed = RemoteResetAllowed;
            config.Plc.RemotePassword = string.IsNullOrEmpty(RemotePassword) ? null : RemotePassword;
            config.Plc.InitialState = Enum.Parse<PlcRunState>(InitialState);
            config.Api.Enabled = ApiEnabled;
            config.Api.Port = int.Parse(ApiPort, CultureInfo.InvariantCulture);
            config.PythonExecutable = string.IsNullOrWhiteSpace(PythonExecutable) ? "python" : PythonExecutable.Trim();
            config.ErrorInjection = _runtime.Engine.Injector.Rules.ToList();

            Busy = true;
            await _runtime.RebuildAsync(config, keepMemory: !profileChanged);
            Message = _runtime.LastError is { } error
                ? $"適用しましたが起動できません: {error}"
                : $"適用しました（{DateTime.Now:HH:mm:ss}）。{(profileChanged ? "機種を変えたためメモリは初期化しました。" : "メモリ内容は引き継ぎました。")}";
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            Message = $"入力が正しくありません: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Revert() => Load();

    [RelayCommand]
    private void Save()
    {
        try
        {
            _runtime.Config.ErrorInjection = _runtime.Engine.Injector.Rules.ToList();
            _runtime.SaveConfig();
            Message = $"設定ファイルに保存しました: {_runtime.ConfigPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存できません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void AddInjection()
    {
        try
        {
            var rule = new ErrorInjectionRule
            {
                Command = ushort.Parse(InjCommand.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                Subcommand = string.IsNullOrWhiteSpace(InjSubcommand) ? null : ushort.Parse(InjSubcommand.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                EndCode = string.IsNullOrWhiteSpace(InjEndCode) ? null : ushort.Parse(InjEndCode.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                DelayMs = string.IsNullOrWhiteSpace(InjDelay) ? 0 : int.Parse(InjDelay, CultureInfo.InvariantCulture),
                Drop = InjDrop,
                RemainingCount = string.IsNullOrWhiteSpace(InjCount) ? null : int.Parse(InjCount, CultureInfo.InvariantCulture),
            };
            _runtime.Engine.Injector.Add(rule);
            RefreshInjections();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            Message = $"エラー注入の入力が正しくありません: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveInjection(InjectionRow? row)
    {
        if (row is null)
            return;
        _runtime.Engine.Injector.Remove(row.Rule);
        RefreshInjections();
    }

    [RelayCommand]
    private void ClearInjections()
    {
        _runtime.Engine.Injector.Clear();
        RefreshInjections();
    }

    public void RefreshInjections()
    {
        Injections.Clear();
        foreach (var rule in _runtime.Engine.Injector.Rules)
        {
            var parts = new List<string> { $"コマンド {rule.Command:X4}{(rule.Subcommand is { } s ? $"/{s:X4}" : string.Empty)}" };
            if (rule.EndCode is { } e)
                parts.Add($"終了コード {e:X4}");
            if (rule.DelayMs > 0)
                parts.Add($"遅延 {rule.DelayMs} ms");
            if (rule.Drop)
                parts.Add("無応答");
            parts.Add(rule.RemainingCount is { } n ? $"残り {n} 回" : "毎回");
            Injections.Add(new InjectionRow(rule, string.Join("  ", parts)));
        }
    }
}
