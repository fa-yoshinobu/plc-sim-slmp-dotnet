using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Server;
using PlcComm.Slmp;

namespace PlcSim.Slmp.Tests;

/// <summary>空きポートで仮想 PLC を起動し、PlcComm.Slmp クライアントを接続するテスト用ヘルパー。</summary>
internal sealed class TestServer : IAsyncDisposable
{
    private TestServer(PlcModel plc, SlmpServer server)
    {
        Plc = plc;
        Server = server;
    }

    public PlcModel Plc { get; }

    public SlmpServer Server { get; }

    public SlmpEngine Engine => Server.Engine;

    public static async Task<TestServer> StartAsync(string profileId = CpuProfiles.IqR, PlcSettings? settings = null)
    {
        var plc = new PlcModel(CpuProfiles.Get(profileId), settings);
        var server = new SlmpServer(new SlmpEngine(plc), new SlmpServerOptions { TcpPort = 0, UdpPort = 0 });
        await server.StartAsync();
        return new TestServer(plc, server);
    }

    public Task<SlmpClient> ConnectAsync(SlmpPlcProfile profile, SlmpTransportMode transport = SlmpTransportMode.Tcp)
    {
        var port = transport == SlmpTransportMode.Tcp ? Server.TcpPort : Server.UdpPort;
        var options = new SlmpConnectionOptions("127.0.0.1", profile, port, transport, SlmpTargetAddress.OwnStation);
        return SlmpClientFactory.OpenAndConnectAsync(options);
    }

    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}
