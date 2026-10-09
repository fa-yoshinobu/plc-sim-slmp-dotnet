using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Scripting;
using PlcComm.Slmp;

namespace PlcSim.Slmp.Tests;

/// <summary>Python スクリプト・HTTP 制御 API・設定ファイル・スナップショット・アクセス記録の確認。</summary>
public sealed class ScriptingAndApiTests
{
    private static SlmpDeviceAddress Dev(string text) => SlmpAddress.Parse(text, SlmpPlcProfile.IqR);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }

        Assert.Fail("条件が成立しませんでした");
    }

    private static async Task<(ControlApi Api, HttpClient Http, string Url)> StartApiAsync(TestServer server, PythonScriptHost? scripts = null)
    {
        var api = new ControlApi(server.Server, new PlcController(server.Plc), new FrameLogBuffer(server.Engine), scripts);
        var port = FreePort();
        await api.StartAsync("127.0.0.1", port);
        var url = $"http://127.0.0.1:{port}";
        return (api, new HttpClient { BaseAddress = new Uri(url) }, url);
    }

    [Fact]
    public async Task PythonScriptTimerAndOnWriteReactToClient()
    {
        var lib = PythonScriptHost.FindLibraryDirectory(AppContext.BaseDirectory);
        Assert.NotNull(lib);
        await using var server = await TestServer.StartAsync();
        var (api, http, url) = await StartApiAsync(server);
        await using var _ = api;
        using var __ = http;

        var dir = Directory.CreateTempSubdirectory();
        var scriptPath = Path.Combine(dir.FullName, "test_script.py");
        await File.WriteAllTextAsync(scriptPath, """
            from plc_sim import Plc
            plc = Plc()

            @plc.every(0.02)
            def tick():
                plc.inc("D100")

            @plc.on_write("M0")
            def on_m0(event):
                if plc.get_bit("M0"):
                    plc.set("D200", 1234)

            plc.run_forever()
            """);
        using var host = new PythonScriptHost("python", url, lib);
        var logs = new List<string>();
        host.Log += m => { lock (logs) logs.Add(m); };
        var script = host.Add(scriptPath);
        script.Start();
        if (script.Status == ScriptStatus.Faulted)
        {
            // Python が無い環境では検証できない。
            Assert.Fail("Python を起動できません: " + script.Error);
        }

        await using var client = await server.ConnectAsync(SlmpPlcProfile.IqR);
        await WaitUntilAsync(async () => (await client.ReadWordsAsync(Dev("D100"), 1))[0] >= 3);

        // イベント購読（SSE）の接続を待ってから書き込む
        await Task.Delay(500);
        await client.WriteBitsAsync(Dev("M0"), new[] { true });
        await WaitUntilAsync(async () => (await client.ReadWordsAsync(Dev("D200"), 1))[0] == 1234);

        script.Stop();
        Assert.Equal(ScriptStatus.Stopped, script.Status);
        dir.Delete(true);
    }

    [Fact]
    public async Task PythonScriptErrorIsReported()
    {
        var lib = PythonScriptHost.FindLibraryDirectory(AppContext.BaseDirectory);
        await using var server = await TestServer.StartAsync();
        var (api, http, url) = await StartApiAsync(server);
        await using var _ = api;
        using var __ = http;
        var dir = Directory.CreateTempSubdirectory();
        var scriptPath = Path.Combine(dir.FullName, "bad.py");
        await File.WriteAllTextAsync(scriptPath, "from plc_sim import Plc\nPlc().set('D99999999', 1)\n");
        using var host = new PythonScriptHost("python", url, lib);
        var script = host.Add(scriptPath);
        script.Start();
        await WaitUntilAsync(() => Task.FromResult(script.Status == ScriptStatus.Faulted));
        Assert.Contains("PlcError", script.Error);
        dir.Delete(true);
    }

    [Fact]
    public async Task HttpApiWritesAreVisibleToClientAndViceVersa()
    {
        await using var server = await TestServer.StartAsync();
        var (api, http, _) = await StartApiAsync(server);
        await using var a = api;
        using var h = http;
        await using var client = await server.ConnectAsync(SlmpPlcProfile.IqR);

        (await http.PutAsJsonAsync("/api/devices/D100", new { values = new[] { 42, 43 } })).EnsureSuccessStatusCode();
        Assert.Equal(new ushort[] { 42, 43 }, await client.ReadWordsAsync(Dev("D100"), 2));

        await client.WriteDWordsAsync(Dev("D300"), new uint[] { 70000 });
        var json = await http.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/devices/D300?type=u32");
        Assert.Equal(70000u, json.GetProperty("values")[0].GetUInt32());

        (await http.PutAsJsonAsync("/api/devices/U0%5CG5", new { values = new[] { 7 } })).EnsureSuccessStatusCode();
        Assert.Equal(new ushort[] { 7 }, await client.ReadWordsExtendedAsync(SlmpQualifiedDeviceParser.Parse(@"U0\G5", SlmpPlcProfile.IqR), 1));

        (await http.PostAsJsonAsync("/api/devices/D500/inc", new { step = 5 })).EnsureSuccessStatusCode();
        Assert.Equal(new ushort[] { 5 }, await client.ReadWordsAsync(Dev("D500"), 1));

        (await http.PostAsync("/api/plc/stop", null)).EnsureSuccessStatusCode();
        Assert.Equal(SlmpCpuOperationStatus.Stop, (await client.ReadCpuOperationStateAsync()).Status);

        var bad = await http.GetAsync("/api/devices/D999999");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task EventsStreamDeliversClientWrites()
    {
        await using var server = await TestServer.StartAsync();
        var (api, http, _) = await StartApiAsync(server);
        await using var a = api;
        using var h = http;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = await http.GetStreamAsync("/api/events", cts.Token);
        using var reader = new StreamReader(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync(cts.Token));

        await using var client = await server.ConnectAsync(SlmpPlcProfile.IqR);
        await client.WriteWordsAsync(Dev("D7"), new ushort[] { 1 });

        string? line;
        do
            line = await reader.ReadLineAsync(cts.Token);
        while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Contains("\"device\":\"D\"", line);
        Assert.Contains("\"start\":7", line);
    }

    [Fact]
    public async Task AccessLogRecordsReadsAndWrites()
    {
        await using var server = await TestServer.StartAsync();
        var access = new AccessLog(server.Engine);
        await using var client = await server.ConnectAsync(SlmpPlcProfile.IqR);

        await client.WriteWordsAsync(Dev("D10"), new ushort[] { 1, 2 });
        await client.ReadWordsAsync(Dev("W1F"), 1);
        await client.ReadWordsAsync(Dev("W1F"), 1);
        new PlcController(server.Plc).SetWord("D20", 5, "テスト");

        var latest = access.Latest();
        Assert.Equal(AccessKind.Write, latest[0].Kind);
        Assert.Equal("テスト", latest[0].Source);
        Assert.Equal(AccessKind.Read, latest[1].Kind);
        Assert.Equal("W", latest[1].Area);
        Assert.Equal(0x1Fu, latest[1].Start);
        Assert.Equal(2, latest[1].Times);
        Assert.Equal("D", latest[2].Area);
        Assert.Equal(2, latest[2].Count);
        Assert.NotNull(access.LastWrite("D", 11));
    }

    [Fact]
    public async Task ConfigInitialValuesAndSnapshotRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "c.json");
        await File.WriteAllTextAsync(path, """
            {
              // コメント可
              "plc": { "profile": "melsec:iq-r", "modelName": "R16CPU", "devicePoints": { "D": 12288 }, "units": ["0", "2"], "networks": [1, 3] },
              "initialValues": [
                { "address": "D0", "type": "U16", "values": [1, "0x10"] },
                { "address": "D10", "type": "F32", "values": [1.5] },
                { "address": "X1F", "type": "BIT", "values": [1] }
              ]
            }
            """);
        var config = SimulatorConfig.Load(path);
        var plc = config.CreatePlc();
        var controller = new PlcController(plc);
        Assert.Equal("R16CPU", plc.Profile.ModelName);
        Assert.Equal(new ushort[] { 1, 0x10 }, controller.ReadWords("D0", 2));
        Assert.Equal(1.5f, controller.GetFloat("D10"));
        Assert.True(controller.GetBit("X1F"));
        Assert.Throws<PlcSim.Slmp.Core.Protocol.SlmpEndCodeException>(() => controller.ReadWords("D12288", 1));
        Assert.Equal(new byte[] { 1, 3 }, plc.Settings.Networks);
        Assert.Throws<PlcSim.Slmp.Core.Protocol.SlmpEndCodeException>(() => controller.GetWord(@"U1\G0"));

        controller.SetWord(@"U2\G100", 9);
        var snapshotPath = Path.Combine(dir.FullName, "s.json");
        MemorySnapshot.Capture(plc).Save(snapshotPath);
        var restored = config.CreatePlc();
        MemorySnapshot.Load(snapshotPath).Restore(restored);
        var restoredController = new PlcController(restored);
        Assert.Equal(9, restoredController.GetWord(@"U2\G100"));
        Assert.True(restoredController.GetBit("X1F"));
        dir.Delete(true);
    }

    [Fact]
    public async Task RuntimeRebuildKeepsMemoryAndAppliesNewPoints()
    {
        var config = new SimulatorConfig();
        config.Server.TcpPort = 0;
        config.Server.UdpPort = 0;
        config.Api.Enabled = false;
        await using var runtime = new SimulatorRuntime(config, null);
        runtime.Controller.SetWord("D100", 77);

        var next = new SimulatorConfig { Server = config.Server, Api = config.Api };
        next.Plc.DevicePoints["D"] = 1024;
        await runtime.RebuildAsync(next);

        Assert.Equal(77, runtime.Controller.GetWord("D100"));
        Assert.Throws<PlcSim.Slmp.Core.Protocol.SlmpEndCodeException>(() => runtime.Controller.GetWord("D1024"));
        Assert.Equal(1024, runtime.Controller.GetWord("SD280"));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
