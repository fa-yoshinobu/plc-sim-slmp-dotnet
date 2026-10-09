using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.ViewModels;

public sealed record SessionRow(string Transport, string Remote, long Requests, string Monitor, string Connected);

/// <summary>ダッシュボード: CPU 状態、接続、要求数の推移。</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private const int SparkSamples = 60;
    private readonly SimulatorRuntime _runtime;
    private readonly Queue<double> _rates = new();
    private long _lastRequests;
    private DateTime _lastSample = DateTime.MinValue;

    public DashboardViewModel(SimulatorRuntime runtime)
    {
        _runtime = runtime;
    }

    public ObservableCollection<SessionRow> Sessions { get; } = new();

    [ObservableProperty]
    private string _stateText = "RUN";

    [ObservableProperty]
    private PlcRunState _state;

    [ObservableProperty]
    private string _stateDetail = string.Empty;

    [ObservableProperty]
    private string _errorCodeText = "0000h";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _passwordEnabled;

    [ObservableProperty]
    private string _modelText = string.Empty;

    [ObservableProperty]
    private string _profileText = string.Empty;

    [ObservableProperty]
    private string _clientCount = "0";

    [ObservableProperty]
    private string _totalRequests = "0";

    [ObservableProperty]
    private string _errorResponses = "0";

    [ObservableProperty]
    private string _injectionText = "なし";

    [ObservableProperty]
    private string _scriptText = "なし";

    [ObservableProperty]
    private string _rateText = "0";

    [ObservableProperty]
    private PointCollection _sparkLine = new();

    [ObservableProperty]
    private PointCollection _sparkArea = new();

    [ObservableProperty]
    private string _sparkMax = "0";

    public void Refresh()
    {
        var plc = _runtime.Plc;
        State = plc.State;
        StateText = State switch
        {
            PlcRunState.Run => "RUN",
            PlcRunState.Stop => "STOP",
            _ => "PAUSE",
        };
        var sd203 = State switch
        {
            PlcRunState.Run => "0000h",
            PlcRunState.Stop => "0002h",
            _ => "0003h",
        };
        StateDetail = $"CPU 動作状態（SD203 = {sd203}）";
        ErrorCodeText = $"{plc.ErrorCode:X4}h";
        HasError = plc.ErrorCode != 0;
        PasswordEnabled = plc.Settings.RemotePassword is not null;
        ModelText = $"{plc.Profile.ModelName} / {plc.Profile.ModelCode:X4}h";
        ProfileText = plc.Profile.DisplayName;
        var sessions = _runtime.Server.Sessions;
        ClientCount = sessions.Count.ToString("N0");
        TotalRequests = _runtime.Engine.RequestCount.ToString("N0");
        ErrorResponses = _runtime.Engine.ErrorCount.ToString("N0");
        var rules = _runtime.Engine.Injector.Rules;
        InjectionText = rules.Count == 0
            ? "なし"
            : string.Join(", ", rules.Select(static r => $"{r.Command:X4}→{(r.EndCode is { } e ? e.ToString("X4") : "遅延")}{(r.RemainingCount is { } n ? $"（残り{n}回）" : string.Empty)}"));
        var running = _runtime.Scripts.Scripts.Where(static s => s.Status == ScriptStatus.Running).Select(static s => s.Name).ToArray();
        ScriptText = running.Length == 0 ? "なし" : string.Join(", ", running);

        Sessions.Clear();
        foreach (var s in sessions)
            Sessions.Add(new SessionRow(s.Transport, s.Remote, s.RequestCount, s.Monitor.Count == 0 ? "—" : $"{s.Monitor.Count} 点", s.Connected.ToString("HH:mm:ss")));
        UpdateSpark();
    }

    /// <summary>1 秒ごとに要求数をサンプリングする（全画面共通で呼ばれる）。</summary>
    public void Sample()
    {
        var now = DateTime.Now;
        if ((now - _lastSample).TotalSeconds < 1)
            return;
        var total = _runtime.Engine.RequestCount;
        if (_lastSample != DateTime.MinValue)
        {
            var rate = Math.Max(0, (total - _lastRequests) / (now - _lastSample).TotalSeconds);
            _rates.Enqueue(rate);
            while (_rates.Count > SparkSamples)
                _rates.Dequeue();
            RateText = rate.ToString("0");
        }

        _lastRequests = total;
        _lastSample = now;
    }

    [RelayCommand]
    private void Run() => _runtime.Controller.SetState(PlcRunState.Run, "GUI");

    [RelayCommand]
    private void Stop() => _runtime.Controller.SetState(PlcRunState.Stop, "GUI");

    [RelayCommand]
    private void Pause() => _runtime.Controller.SetState(PlcRunState.Pause, "GUI");

    [RelayCommand]
    private void Reset() => _runtime.Plc.Reset("GUI");

    [RelayCommand]
    private void ClearError() => _runtime.Plc.SetErrorCode(0);

    [RelayCommand]
    private async Task ToggleServer()
    {
        if (_runtime.IsRunning)
            await _runtime.StopAsync();
        else
            await _runtime.StartAsync();
        if (_runtime.LastError is { } error)
            MessageBox.Show($"サーバーを起動できません: {error}", "PLC Sim (SLMP)", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void UpdateSpark()
    {
        const double width = 600;
        const double height = 90;
        var values = _rates.ToArray();
        var max = Math.Max(10, values.DefaultIfEmpty(0).Max() * 1.2);
        SparkMax = max.ToString("0");
        var line = new PointCollection();
        for (var i = 0; i < values.Length; i++)
        {
            var x = SparkSamples <= 1 ? 0 : width * (SparkSamples - values.Length + i) / (SparkSamples - 1);
            line.Add(new Point(x, height - (values[i] / max * (height - 4)) - 2));
        }

        var area = new PointCollection(line);
        if (line.Count > 0)
        {
            area.Add(new Point(line[^1].X, height));
            area.Add(new Point(line[0].X, height));
        }

        SparkLine = line;
        SparkArea = area;
    }
}
