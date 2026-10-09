using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSim.Slmp.Gui.Views;
using PlcSim.Slmp.Scripting;
using Wpf.Ui.Controls;

namespace PlcSim.Slmp.Gui.ViewModels;

/// <summary>左ナビの 1 項目。</summary>
public sealed record NavItem(string Key, string Title, SymbolRegular Icon);

/// <summary>画面全体。200ms ごとに各画面の表示を更新する。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;

    public MainViewModel(SimulatorRuntime runtime)
    {
        Runtime = runtime;
        Dashboard = new DashboardViewModel(runtime);
        Memory = new MemoryViewModel(runtime);
        Log = new LogViewModel(runtime);
        Scripts = new ScriptsViewModel(runtime);
        DeviceRange = new DeviceRangeViewModel(runtime);
        Labels = new LabelsViewModel(runtime);
        Settings = new SettingsViewModel(runtime);
        Navigation = new ObservableCollection<NavItem>
        {
            new("dash", "ダッシュボード", SymbolRegular.Gauge24),
            new("mem", "メモリビューア", SymbolRegular.Table24),
            new("log", "通信ログ", SymbolRegular.ArrowSwap24),
            new("script", "スクリプト", SymbolRegular.Code24),
            new("range", "デバイス範囲", SymbolRegular.TableSettings24),
            new("label", "ラベル", SymbolRegular.Tag24),
            new("settings", "設定", SymbolRegular.Settings24),
        };
        _selectedNav = Navigation[0];

        runtime.Rebuilt += () => App.Current.Dispatcher.Invoke(OnRebuilt);
        runtime.RunningChanged += () => App.Current.Dispatcher.Invoke(UpdateTitle);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick(), App.Current.Dispatcher);
        UpdateTitle();
    }

    public SimulatorRuntime Runtime { get; }

    public DashboardViewModel Dashboard { get; }

    public MemoryViewModel Memory { get; }

    public LogViewModel Log { get; }

    public ScriptsViewModel Scripts { get; }

    public DeviceRangeViewModel DeviceRange { get; }

    public LabelsViewModel Labels { get; }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavItem> Navigation { get; }

    [ObservableProperty]
    private NavItem _selectedNav;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _endpointText = string.Empty;

    [ObservableProperty]
    private bool _isServerRunning;

    [RelayCommand]
    private static void About() => new AboutWindow { Owner = App.Current.MainWindow }.ShowDialog();

    public string CurrentPage => SelectedNav.Key;

    partial void OnSelectedNavChanged(NavItem value) => OnPropertyChanged(nameof(CurrentPage));

    /// <summary>起動後の初期化（スクリプト読込み後に呼ぶ）。</summary>
    public void AfterStart()
    {
        UpdateTitle();
        Scripts.Reload();
        _timer.Start();
    }

    /// <summary>メモリビューアを開いて番地へ移動する（他画面からのジャンプ）。</summary>
    public void ShowMemory(string area, uint start)
    {
        SelectedNav = Navigation.First(static n => n.Key == "mem");
        Memory.JumpTo(area, start);
    }

    private void Tick()
    {
        switch (CurrentPage)
        {
            case "dash":
                Dashboard.Refresh();
                break;
            case "mem":
                Memory.Refresh();
                break;
            case "log":
                Log.Refresh();
                break;
            case "script":
                Scripts.Refresh();
                break;
        }

        Dashboard.Sample();
    }

    private void OnRebuilt()
    {
        Memory.Rebuild();
        Scripts.Reload();
        DeviceRange.Load();
        Labels.Load();
        Settings.RefreshInjections();
        Log.Clear();
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var plc = Runtime.Plc;
        Title = $"PLC Sim (SLMP) — {plc.Profile.ModelName} / {plc.Profile.Id}";
        IsServerRunning = Runtime.IsRunning;
        var server = Runtime.Config.Server;
        EndpointText = Runtime.IsRunning
            ? $"待受中  TCP {(server.EnableTcp ? Runtime.Server.TcpPort.ToString() : "—")} · UDP {(server.EnableUdp ? Runtime.Server.UdpPort.ToString() : "—")}"
            : Runtime.LastError is { } error ? $"停止中（{error}）" : "停止中";
    }
}
