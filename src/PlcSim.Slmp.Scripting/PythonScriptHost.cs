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
    private readonly object _sync = new();
    private readonly object _lifecycle = new();
    private readonly ConcurrentQueue<string> _stderr = new();
    private Process? _process;
    private ScriptStatus _status;
    private string? _error;
    private DateTime? _startedAt;

    internal PythonScript(PythonScriptHost host, string path)
    {
        _host = host;
        Path = System.IO.Path.GetFullPath(path);
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public string Name { get; }
    public string Path { get; }
    public ScriptStatus Status { get { lock (_sync) return _status; } }
    public string? Error { get { lock (_sync) return _error; } }
    public DateTime? StartedAt { get { lock (_sync) return _startedAt; } }

    public void Start()
    {
        string message;
        lock (_lifecycle)
        {
            StopProcess();
            lock (_sync) _error = null;
            while (_stderr.TryDequeue(out _)) { }
            try
            {
                if (_host.ApiUrl is null)
                    throw new InvalidOperationException("HTTP API が無効です。設定で API を有効にしてください。");
                var info = new ProcessStartInfo(_host.PythonExecutable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(Path)!,
                };
                info.ArgumentList.Add("-u");
                info.ArgumentList.Add(Path);
                info.Environment["PLC_SIM_URL"] = _host.ApiUrl;
                info.Environment["PLC_SIM_TOKEN"] = _host.ApiToken ?? string.Empty;
                info.Environment["PLC_SIM_SOURCE"] = $"Python {Name}";
                info.Environment["PYTHONIOENCODING"] = "utf-8";
                if (_host.LibraryDirectory is { } lib)
                {
                    var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
                    info.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing) ? lib : lib + System.IO.Path.PathSeparator + existing;
                }
                var process = new Process { StartInfo = info };
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) _host.WriteLog($"[{Name}] {e.Data}"); };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    _stderr.Enqueue(e.Data);
                    while (_stderr.Count > 20) _stderr.TryDequeue(out string? _);
                    _host.WriteLog($"[{Name}] ! {e.Data}");
                };
                process.Exited += (_, _) => OnExited(process);
                // Publish before Start: even an immediately exiting script must be observed.
                lock (_sync)
                {
                    _process = process;
                    _status = ScriptStatus.Running;
                    _startedAt = DateTime.Now;
                }
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.EnableRaisingEvents = true;
                message = "開始";
            }
            catch (Exception ex)
            {
                StopProcess();
                lock (_sync)
                {
                    _error = $"Python を起動できません（{_host.PythonExecutable}）: {ex.Message}";
                    _status = ScriptStatus.Faulted;
                    message = _error;
                }
            }
        }
        _host.WriteLog($"[{Name}] {message}");
        _host.NotifyChanged(this);
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            if (!StopProcess()) return;
            lock (_sync) _status = ScriptStatus.Stopped;
        }
        _host.WriteLog($"[{Name}] 停止");
        _host.NotifyChanged(this);
    }

    private bool StopProcess()
    {
        Process? process;
        lock (_sync) { process = _process; _process = null; }
        if (process is null) return false;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException ex)
        {
            PlcSim.Slmp.Core.Diagnostics.DiagnosticLog.Default.Write($"Script already exited: {Name}", ex);
        }
        finally { process.Dispose(); }
        return true;
    }

    private void OnExited(Process process)
    {
        lock (_sync)
            if (!ReferenceEquals(process, _process)) return;
        // Drain redirected output without holding _sync: output subscribers may read Status.
        try { process.WaitForExit(); }
        catch (InvalidOperationException) { return; } // Stop already disposed the process.
        string message;
        lock (_sync)
        {
            if (!ReferenceEquals(process, _process)) return;
            var code = process.ExitCode;
            _process = null;
            _error = code == 0 ? null : $"終了コード {code}{Environment.NewLine}{string.Join(Environment.NewLine, _stderr)}";
            _status = code == 0 ? ScriptStatus.Stopped : ScriptStatus.Faulted;
            message = _error ?? "終了";
        }
        process.Dispose();
        _host.WriteLog($"[{Name}] {message}");
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

    public string? ApiToken { get; set; }

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
        PythonScript script;
        lock (_scripts)
        {
            var existing = _scripts.FirstOrDefault(s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return existing;
            script = new PythonScript(this, full);
            _scripts.Add(script);
        }
        Changed?.Invoke(script);
        return script;
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
