using System.Globalization;
using System.Reflection;
using System.Text;
using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;
using PlcSim.Slmp.Scripting;

Console.OutputEncoding = Encoding.UTF8;

var configPath = ArgValue("--config") ?? FindDefaultConfig();
var snapshotPath = ArgValue("--snapshot");
var verbose = args.Contains("--verbose");

var config = configPath is not null ? SimulatorConfig.Load(configPath) : new SimulatorConfig();
if (ArgValue("--profile") is { } profileArg)
    config.Plc.Profile = profileArg;
if (ArgValue("--port") is { } portArg)
    config.Server.TcpPort = config.Server.UdpPort = int.Parse(portArg, CultureInfo.InvariantCulture);
if (ArgValue("--api-port") is { } apiPortArg)
    config.Api.Port = int.Parse(apiPortArg, CultureInfo.InvariantCulture);

await using var runtime = new SimulatorRuntime(config, configPath);
var plc = runtime.Plc;
var engine = runtime.Engine;
var controller = runtime.Controller;
if (snapshotPath is not null && File.Exists(snapshotPath))
{
    MemorySnapshot.Load(snapshotPath).Restore(plc);
    Console.WriteLine($"スナップショットを復元しました: {snapshotPath}");
}

runtime.Scripts.Log += message => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
runtime.Server.Diagnostic += message => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} ! {message}");
runtime.Server.SessionOpened += s => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} 接続 {s}");
runtime.Server.SessionClosed += s => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} 切断 {s}");
if (verbose)
{
    engine.FrameProcessed += e => Console.WriteLine(
        $"{e.Time:HH:mm:ss.fff} {e.Session} {e.Request.Command:X4}/{e.Request.Subcommand:X4} → {e.EndCode:X4} {e.Summary}{Environment.NewLine}" +
        $"    RX {Convert.ToHexString(e.Request.Raw)}{Environment.NewLine}    TX {(e.Response is null ? "(無応答)" : Convert.ToHexString(e.Response))}");
}

await runtime.StartAsync();
if (runtime.LastError is { } startError)
{
    Console.WriteLine($"起動できません: {startError}");
    return;
}

var version = typeof(SimulatorRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
Console.WriteLine($"PLC Sim (SLMP) {version}  {plc.Profile.DisplayName} / {plc.Profile.ModelName}");
Console.WriteLine($"  SLMP  TCP {(config.Server.EnableTcp ? $"{config.Server.BindAddress}:{runtime.Server.TcpPort}" : "無効")}  UDP {(config.Server.EnableUdp ? $"{config.Server.BindAddress}:{runtime.Server.UdpPort}" : "無効")}");
if (configPath is not null)
    Console.WriteLine($"  設定  {configPath}");
if (runtime.Api?.Url is { } apiUrl)
    Console.WriteLine($"  API   {apiUrl}/api/status   （Python: python/plc_sim.py）");

runtime.StartConfiguredScripts();
Console.WriteLine("コマンドは help で一覧表示。終了は quit または Ctrl+C。");

using var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    quit.Cancel();
};

_ = Task.Run(() =>
{
    while (!quit.IsCancellationRequested)
    {
        var line = Console.ReadLine();
        if (line is null)
            return;
        if (!Execute(line.Trim()))
        {
            quit.Cancel();
            return;
        }
    }
});

try
{
    await Task.Delay(Timeout.Infinite, quit.Token);
}
catch (OperationCanceledException)
{
}

runtime.Scripts.StopAll();
if (snapshotPath is not null)
{
    MemorySnapshot.Capture(plc).Save(snapshotPath);
    Console.WriteLine($"スナップショットを保存しました: {snapshotPath}");
}

return;

bool Execute(string line)
{
    if (line.Length == 0)
        return true;
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    try
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "quit":
            case "exit":
                return false;
            case "help":
                PrintHelp();
                break;
            case "state":
            case "status":
                Console.WriteLine($"状態 {plc.State}  エラー {plc.ErrorCode:X4}h  要求 {engine.RequestCount}  エラー応答 {engine.ErrorCount}  接続 {runtime.Server.Sessions.Count}");
                foreach (var s in runtime.Server.Sessions)
                    Console.WriteLine($"  {s}  要求 {s.RequestCount}  モニタ登録 {s.Monitor.Count}点");
                break;
            case "dump":
                Dump(parts[1], parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 16, parts.Length > 3 ? parts[3] : "dec");
                break;
            case "set":
                controller.WriteValue(parts[1], parts[2], parts.Length > 3 ? parts[3] : "U16", "コンソール");
                Console.WriteLine("OK");
                break;
            case "run":
                controller.SetState(PlcRunState.Run, "コンソール");
                break;
            case "stop":
                controller.SetState(PlcRunState.Stop, "コンソール");
                break;
            case "pause":
                controller.SetState(PlcRunState.Pause, "コンソール");
                break;
            case "labels":
                foreach (var label in controller.Labels())
                    Console.WriteLine($"  {label.Path,-32} {label.Type,-10} {Convert.ToHexString(label.Data)}");
                break;
            case "log":
                foreach (var e in runtime.FrameLog.Latest(parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 20))
                    Console.WriteLine($"  {e.Time:HH:mm:ss.fff} {e.Session} {e.Request.Command:X4}/{e.Request.Subcommand:X4} → {e.EndCode:X4} {e.Summary}");
                break;
            case "access":
                foreach (var a in runtime.Access.Latest(parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 20))
                    Console.WriteLine($"  {a.LastTime:HH:mm:ss.fff} {(a.Kind == AccessKind.Write ? "書込" : "読出")} {a.Display,-28} ×{a.Times,-5} {a.Source}");
                break;
            case "scripts":
                foreach (var s in runtime.Scripts.Scripts)
                    Console.WriteLine($"  {s.Name,-20} {s.Status} {s.Path} {s.Error}");
                break;
            case "script":
                if (parts[1] == "add")
                {
                    runtime.Scripts.Add(parts[2]).Start();
                    break;
                }

                var target = runtime.Scripts.Scripts.First(s => s.Name == parts[2]);
                if (parts[1] == "start")
                    target.Start();
                else
                    target.Stop();
                break;
            case "error":
                engine.Injector.Add(new ErrorInjectionRule
                {
                    Command = ushort.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    EndCode = ushort.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    RemainingCount = parts.Length > 3 ? int.Parse(parts[3], CultureInfo.InvariantCulture) : null,
                });
                Console.WriteLine("エラー注入を追加しました");
                break;
            case "clearerrors":
                engine.Injector.Clear();
                break;
            case "save":
                MemorySnapshot.Capture(plc).Save(parts[1]);
                Console.WriteLine($"保存しました: {parts[1]}");
                break;
            case "load":
                MemorySnapshot.Load(parts[1]).Restore(plc);
                Console.WriteLine($"復元しました: {parts[1]}");
                break;
            default:
                Console.WriteLine("不明なコマンドです。help で一覧を表示します。");
                break;
        }
    }
    catch (Exception ex) when (ex is FormatException or ArgumentException or SlmpEndCodeException or IndexOutOfRangeException or InvalidOperationException or OverflowException)
    {
        Console.WriteLine($"エラー: {ex.Message}");
    }

    return true;
}

void Dump(string address, int count, string format)
{
    var parsed = controller.Parse(address);
    if (format == "bit" || (parsed.Device.IsBitUnit && format == "dec"))
    {
        var bits = controller.ReadBits(address, count);
        for (var i = 0; i < bits.Length; i += 16)
        {
            var row = string.Join(' ', bits.Skip(i).Take(16).Select(static b => b ? "1" : "0"));
            Console.WriteLine($"  {Label(parsed, (uint)i),-12} {row}");
        }

        return;
    }

    var words = controller.ReadWords(address, count);
    for (var i = 0; i < words.Length; i += 8)
    {
        var row = words.Skip(i).Take(8).Select(w => format switch
        {
            "hex" => $"{w:X4}",
            "s16" => $"{(short)w,6}",
            _ => $"{w,5}",
        });
        Console.WriteLine($"  {Label(parsed, (uint)i),-12} {string.Join(' ', row)}");
    }
}

string Label(DeviceAddress parsed, uint offset)
{
    var number = MemoryDeviceAccessor.FormatNumber(plc.Profile, parsed.Device, parsed.Number + offset);
    return parsed.Qualifier is null ? $"{parsed.Device.Name}{number}" : $"{parsed.Qualifier}\\{parsed.Device.Name}{number}";
}

void PrintHelp()
{
    Console.WriteLine("""
      status                      状態・接続一覧
      dump <addr> [点数] [dec|hex|s16|bit]   仮想メモリ表示（例: dump D100 32 hex / dump M0 64 / dump U0\G0 8）
      set <addr> <値> [BIT|U16|S16|U32|S32|F32]  値を書込み（例: set D100 0x1234 / set M0 1 BIT）
      run | stop | pause          CPU 状態を変更
      labels                      ラベル一覧
      log [件数]                  通信ログ
      access [件数]               直近アクセス（読出し・書込み）
      scripts                     Python スクリプト一覧
      script add <file.py> / script start|stop <名前>
      error <cmd16進> <終了コード16進> [回数]  エラー注入（例: error 0401 C051 1）/ clearerrors
      save <file> / load <file>   スナップショット保存・復元
      quit                        終了
    """);
}

string? ArgValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string? FindDefaultConfig()
{
    foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        var current = new DirectoryInfo(dir);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "config", "default.json");
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
    }

    return null;
}
