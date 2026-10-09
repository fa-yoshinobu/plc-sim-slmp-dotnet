using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PlcSim.Slmp.Scripting;

/// <summary>スクリプトの状態。</summary>
public enum ScriptStatus
{
    Stopped,
    Running,
    Faulted,
}

/// <summary>
/// 1 本の Python スクリプト。別プロセス（python -u script.py）で実行し、
/// 環境変数 PLC_SIM_URL で HTTP API の場所を渡す。標準出力/標準エラーはログに流す。
/// </summary>
public sealed class PythonScript
{
    private readonly PythonScriptHost _host;
    private Process? _process;
    private readonly ConcurrentQueue<string> _stderr = new();

    internal PythonScript(PythonScriptHost host, string path)
    {
        _host = host;
        Path = System.IO.Path.GetFullPath(path);
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public string Name { get; }

    public string Path { get; }

    public ScriptStatus Status { get; private set; }

    /// <summary>異常終了時の終了コードと標準エラーの末尾。</summary>
    public string? Error { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public void Start()
    {
        Stop();
        Error = null;
        while (_stderr.TryDequeue(out _))
        {
        }

        if (_host.ApiUrl is null)
        {
            Fault("HTTP API が無効です。設定で API を有効にしてください。");
            return;
        }

        var info = new ProcessStartInfo(_host.PythonExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = System.IO.Path.GetDirectoryName(Path)!,
        };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(Path);
        info.Environment["PLC_SIM_URL"] = _host.ApiUrl;
        info.Environment["PLC_SIM_SOURCE"] = $"Python {Name}";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        if (_host.LibraryDirectory is { } lib)
        {
            var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
            info.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing) ? lib : lib + System.IO.Path.PathSeparator + existing;
        }

        try
        {
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    _host.WriteLog($"[{Name}] {e.Data}");
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                _stderr.Enqueue(e.Data);
                while (_stderr.Count > 20)
                    _stderr.TryDequeue(out string? _);
                _host.WriteLog($"[{Name}] ! {e.Data}");
            };
            process.Exited += (_, _) => OnExited(process);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            Status = ScriptStatus.Running;
            StartedAt = DateTime.Now;
            _host.WriteLog($"[{Name}] 開始");
            _host.NotifyChanged(this);
        }
        catch (Exception ex)
        {
            Fault($"Python を起動できません（{_host.PythonExecutable}）: {ex.Message}");
        }
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        process.Dispose();
        Status = ScriptStatus.Stopped;
        _host.WriteLog($"[{Name}] 停止");
        _host.NotifyChanged(this);
    }

    private void OnExited(Process process)
    {
        if (!ReferenceEquals(process, _process))
            return;
        process.WaitForExit();
        var code = process.ExitCode;
        _process = null;
        if (code == 0)
        {
            Status = ScriptStatus.Stopped;
            _host.WriteLog($"[{Name}] 終了");
            _host.NotifyChanged(this);
        }
        else
        {
            Fault($"終了コード {code}{Environment.NewLine}{string.Join(Environment.NewLine, _stderr)}");
        }
    }

    private void Fault(string error)
    {
        Error = error;
        Status = ScriptStatus.Faulted;
        _host.WriteLog($"[{Name}] エラーで停止: {error}");
        _host.NotifyChanged(this);
    }
}

/// <summary>Python スクリプトの登録・実行・停止を管理する。</summary>
public sealed class PythonScriptHost : IDisposable
{
    private readonly List<PythonScript> _scripts = new();

    public PythonScriptHost(string pythonExecutable, string? apiUrl, string? libraryDirectory)
    {
        PythonExecutable = pythonExecutable;
        ApiUrl = apiUrl;
        LibraryDirectory = libraryDirectory;
    }

    /// <summary>python 実行ファイル（既定 "python"）。</summary>
    public string PythonExecutable { get; set; }

    /// <summary>HTTP API の URL（null なら API 無効）。</summary>
    public string? ApiUrl { get; set; }

    /// <summary>plc_sim.py のあるディレクトリ（PYTHONPATH に追加）。</summary>
    public string? LibraryDirectory { get; }

    public IReadOnlyList<PythonScript> Scripts
    {
        get
        {
            lock (_scripts)
                return _scripts.ToArray();
        }
    }

    public event Action<string>? Log;

    public event Action<PythonScript>? Changed;

    public PythonScript Add(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_scripts)
        {
            var existing = _scripts.FirstOrDefault(s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return existing;
            var script = new PythonScript(this, full);
            _scripts.Add(script);
            Changed?.Invoke(script);
            return script;
        }
    }

    public void Remove(PythonScript script)
    {
        script.Stop();
        lock (_scripts)
            _scripts.Remove(script);
        Changed?.Invoke(script);
    }

    public void StopAll()
    {
        foreach (var script in Scripts)
            script.Stop();
    }

    public void Dispose() => StopAll();

    internal void WriteLog(string message) => Log?.Invoke(message);

    internal void NotifyChanged(PythonScript script) => Changed?.Invoke(script);

    /// <summary>python/plc_sim.py を探す（設定ファイルや実行ファイルの上位ディレクトリ）。</summary>
    public static string? FindLibraryDirectory(params string[] startDirectories)
    {
        foreach (var dir in startDirectories)
        {
            var current = new DirectoryInfo(dir);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "python");
                if (File.Exists(Path.Combine(candidate, "plc_sim.py")))
                    return candidate;
                current = current.Parent;
            }
        }

        return null;
    }
}
