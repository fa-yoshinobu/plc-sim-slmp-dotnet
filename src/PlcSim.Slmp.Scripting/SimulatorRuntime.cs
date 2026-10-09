using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Server;

namespace PlcSim.Slmp.Scripting;

/// <summary>
/// 仮想 PLC・SLMP サーバー・HTTP API・Python スクリプトをまとめて起動/停止/再構築する（Host・GUI 共通）。
/// 機種やデバイス点数を変えるときは <see cref="RebuildAsync"/> でメモリ内容を引き継いで作り直す。
/// </summary>
public sealed class SimulatorRuntime : IAsyncDisposable
{
    public SimulatorRuntime(SimulatorConfig config, string? configPath)
    {
        Config = config;
        ConfigPath = configPath;
        Scripts = new PythonScriptHost(
            config.PythonExecutable,
            ApiUrlOf(config),
            PythonScriptHost.FindLibraryDirectory(config.BaseDirectory, AppContext.BaseDirectory, Directory.GetCurrentDirectory()));
        Build(null);
    }

    public SimulatorConfig Config { get; private set; }

    public string? ConfigPath { get; }

    public PlcModel Plc { get; private set; } = null!;

    public SlmpEngine Engine { get; private set; } = null!;

    public SlmpServer Server { get; private set; } = null!;

    public PlcController Controller { get; private set; } = null!;

    public FrameLogBuffer FrameLog { get; private set; } = null!;

    public AccessLog Access { get; private set; } = null!;

    /// <summary>Python スクリプト（再構築後も同じインスタンス。HTTP API 経由で新しい PLC にアクセスする）。</summary>
    public PythonScriptHost Scripts { get; }

    public ControlApi? Api { get; private set; }

    public bool IsRunning => Server.IsRunning;

    /// <summary>直近の起動エラー（ポート使用中など）。</summary>
    public string? LastError { get; private set; }

    /// <summary>再構築後（新しい Plc/Engine に差し替わった後）に発生する。</summary>
    public event Action? Rebuilt;

    /// <summary>起動・停止で発生する。</summary>
    public event Action? RunningChanged;

    public async Task StartAsync()
    {
        LastError = null;
        try
        {
            await Server.StartAsync().ConfigureAwait(false);
            if (Config.Api.Enabled)
            {
                Api = new ControlApi(Server, Controller, FrameLog, Scripts);
                await Api.StartAsync(Config.Api.BindAddress, Config.Api.Port).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            await StopAsync().ConfigureAwait(false);
        }

        RunningChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        if (Api is not null)
        {
            await Api.DisposeAsync().ConfigureAwait(false);
            Api = null;
        }

        await Server.StopAsync().ConfigureAwait(false);
        RunningChanged?.Invoke();
    }

    /// <summary>設定を差し替えて作り直す。<paramref name="keepMemory"/> ならメモリ内容を引き継ぐ（新しい範囲外は捨てる）。</summary>
    public async Task RebuildAsync(SimulatorConfig config, bool keepMemory = true)
    {
        var wasRunning = IsRunning;
        var snapshot = keepMemory ? MemorySnapshot.Capture(Plc) : null;
        await StopAsync().ConfigureAwait(false);
        config.BaseDirectory = Config.BaseDirectory;
        Config = config;
        Scripts.PythonExecutable = config.PythonExecutable;
        Scripts.ApiUrl = ApiUrlOf(config);
        Build(snapshot);
        Rebuilt?.Invoke();
        if (wasRunning)
            await StartAsync().ConfigureAwait(false);
    }

    /// <summary>設定ファイルへ保存する。</summary>
    public void SaveConfig(string? path = null)
    {
        var target = path ?? ConfigPath ?? throw new InvalidOperationException("保存先の設定ファイルがありません");
        Config.Save(target);
    }

    /// <summary>設定ファイルに列挙されたスクリプトを登録して開始する。</summary>
    public void StartConfiguredScripts()
    {
        foreach (var path in Config.Scripts)
        {
            var full = Config.ResolvePath(path);
            if (File.Exists(full))
                Scripts.Add(full).Start();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Scripts.Dispose();
        await StopAsync().ConfigureAwait(false);
    }

    private static string? ApiUrlOf(SimulatorConfig config)
        => config.Api.Enabled ? $"http://{config.Api.BindAddress}:{config.Api.Port}" : null;

    private void Build(MemorySnapshot? snapshot)
    {
        Plc = Config.CreatePlc();
        snapshot?.Restore(Plc);
        Engine = Config.CreateEngine(Plc);
        FrameLog = new FrameLogBuffer(Engine);
        Access = new AccessLog(Engine);
        Controller = new PlcController(Plc);
        Server = new SlmpServer(Engine, Config.Server);
    }
}
