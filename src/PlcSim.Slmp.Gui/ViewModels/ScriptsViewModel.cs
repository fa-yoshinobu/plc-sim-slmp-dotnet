using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.ViewModels;

public sealed partial class ScriptRow : ObservableObject
{
    public ScriptRow(PythonScript script)
    {
        Script = script;
    }

    public PythonScript Script { get; }

    public string Name => Script.Name;

    public string Path => Script.Path;

    [ObservableProperty]
    private ScriptStatus _status;

    [ObservableProperty]
    private string _statusText = "停止";

    [ObservableProperty]
    private string? _error;

    public void Update()
    {
        Status = Script.Status;
        StatusText = Status switch
        {
            ScriptStatus.Running => "実行中",
            ScriptStatus.Faulted => "エラー停止",
            _ => "停止",
        };
        Error = Script.Error;
    }
}

/// <summary>
/// スクリプト: Python（.py）を編集・実行・停止する。スクリプトは python/plc_sim.py を使って HTTP API 経由で PLC を操作する。
/// </summary>
public sealed partial class ScriptsViewModel : ObservableObject
{
    private const int MaxLogCharacters = 60000;
    private const string Template = """
        # PLC Sim (SLMP)用 Python スクリプト
        # plc_sim は python フォルダにあり、GUI から実行すると自動で読み込まれる。
        from plc_sim import Plc

        plc = Plc()


        @plc.every(0.1)          # 100ms ごと
        def tick():
            plc.inc("D100")


        @plc.on_write("M0")      # クライアント等が M0 に書いたとき
        def on_m0(event):
            if plc.get_bit("M0"):
                plc.set("D200", 1234)
                print("M0 ON", event.source)


        plc.run_forever()
        """;

    private readonly SimulatorRuntime _runtime;
    private readonly StringBuilder _pendingLog = new();
    private bool _samplesAdded;
    private readonly Dictionary<string, string> _drafts = new(StringComparer.OrdinalIgnoreCase);

    public ScriptsViewModel(SimulatorRuntime runtime)
    {
        _runtime = runtime;
        runtime.Scripts.Log += message =>
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
            if (line.Length > MaxLogCharacters)
                line = line[^MaxLogCharacters..];
            lock (_pendingLog)
            {
                var overflow = _pendingLog.Length + line.Length - MaxLogCharacters;
                if (overflow > 0)
                    _pendingLog.Remove(0, overflow);
                _pendingLog.Append(line);
            }
        };
    }

    public ObservableCollection<ScriptRow> Scripts { get; } = new();

    [ObservableProperty]
    private ScriptRow? _selected;

    [ObservableProperty]
    private string _code = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private string _hint = string.Empty;

    /// <summary>エディタの内容を差し替える要求（AvalonEdit との同期用）。</summary>
    public event Action<string>? CodeReplaced;

    public void Reload()
    {
        if (!_samplesAdded)
        {
            // scripts フォルダの .py を一覧に並べる（実行はしない）。
            _samplesAdded = true;
            var dir = ScriptDirectory();
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.py").OrderBy(static f => f))
                    _runtime.Scripts.Add(file);
            }
        }

        var selectedPath = Selected?.Path;
        Scripts.Clear();
        foreach (var script in _runtime.Scripts.Scripts)
        {
            var row = new ScriptRow(script);
            row.Update();
            Scripts.Add(row);
        }

        Selected = Scripts.FirstOrDefault(s => s.Path == selectedPath) ?? Scripts.FirstOrDefault();
        UpdateHint();
    }

    public void Refresh()
    {
        if (Scripts.Count != _runtime.Scripts.Scripts.Count)
            Reload();
        foreach (var row in Scripts)
            row.Update();
        string pending;
        lock (_pendingLog)
        {
            pending = _pendingLog.ToString();
            _pendingLog.Clear();
        }

        if (pending.Length > 0)
        {
            var text = LogText + pending;
            LogText = text.Length > MaxLogCharacters ? text[^MaxLogCharacters..] : text;
        }
    }

    partial void OnSelectedChanged(ScriptRow? value)
    {
        var dirty = value is not null && _drafts.ContainsKey(value.Path);
        var code = dirty ? _drafts[value!.Path]
            : value is not null && File.Exists(value.Path) ? File.ReadAllText(value.Path) : string.Empty;
        Code = code;
        IsDirty = dirty;
        CodeReplaced?.Invoke(code);
    }

    public void EditorChanged(string text)
    {
        Code = text;
        IsDirty = Selected is not null;
        if (Selected is not null)
            _drafts[Selected.Path] = text;
    }

    public bool ConfirmClose()
    {
        if (_drafts.Count == 0)
            return true;
        var answer = MessageBox.Show("未保存のスクリプトがあります。すべて保存して終了しますか？",
            "スクリプトの保存", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return answer == MessageBoxResult.No;
        try
        {
            foreach (var (path, code) in _drafts)
                File.WriteAllText(path, code, new UTF8Encoding(false));
            _drafts.Clear();
            IsDirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "保存できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    [RelayCommand]
    private void New()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "新しい Python スクリプト",
            Filter = "Python スクリプト|*.py",
            FileName = "script.py",
            InitialDirectory = ScriptDirectory(),
        };
        if (dialog.ShowDialog() != true)
            return;
        File.WriteAllText(dialog.FileName, Template.Replace("\r\n", "\n"), new UTF8Encoding(false));
        AddAndSelect(dialog.FileName);
    }

    [RelayCommand]
    private void Open()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Python スクリプト|*.py",
            InitialDirectory = ScriptDirectory(),
        };
        if (dialog.ShowDialog() == true)
            AddAndSelect(dialog.FileName);
    }

    [RelayCommand]
    private void Save() => SaveSelected();

    private bool SaveSelected()
    {
        if (Selected is null)
            return false;
        try
        {
            File.WriteAllText(Selected.Path, Code, new UTF8Encoding(false));
            _drafts.Remove(Selected.Path);
            IsDirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Hint = $"保存できません: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    private void Start()
    {
        if (Selected is null)
            return;
        if (IsDirty && !SaveSelected())
            return;
        Selected.Script.Start();
        Selected.Update();
    }

    [RelayCommand]
    private void Stop()
    {
        Selected?.Script.Stop();
        Selected?.Update();
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null)
            return;
        _runtime.Scripts.Remove(Selected.Script);
        Reload();
    }

    [RelayCommand]
    private void ClearLog()
    {
        lock (_pendingLog)
            _pendingLog.Clear();
        LogText = string.Empty;
    }

    private void AddAndSelect(string path)
    {
        var script = _runtime.Scripts.Add(path);
        Reload();
        Selected = Scripts.FirstOrDefault(s => s.Script == script);
    }

    private string ScriptDirectory()
    {
        var dir = Path.Combine(Path.GetDirectoryName(_runtime.Scripts.LibraryDirectory ?? _runtime.Config.BaseDirectory) ?? _runtime.Config.BaseDirectory, "scripts");
        return Directory.Exists(dir) ? dir : _runtime.Config.BaseDirectory;
    }

    private void UpdateHint()
    {
        Hint = _runtime.Scripts.ApiUrl is null
            ? "HTTP API が無効のためスクリプトを実行できません（設定で有効にしてください）。"
            : $"python: {_runtime.Scripts.PythonExecutable}　API: {_runtime.Scripts.ApiUrl}　ライブラリ: {_runtime.Scripts.LibraryDirectory ?? "（python/plc_sim.py が見つかりません）"}";
    }
}
