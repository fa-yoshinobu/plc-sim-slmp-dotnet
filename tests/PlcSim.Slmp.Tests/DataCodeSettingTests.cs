using System.Net;
using System.Net.Sockets;
using System.Text;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Server;

namespace PlcSim.Slmp.Tests;

/// <summary>
/// 交信データコードの設定。実機（iQ-R 系、2026-10-09）は設定と違うコードの要求にエラーも返さず無応答だった。
/// </summary>
public sealed class DataCodeSettingTests
{
    // 3E 0401/0000 D100 1 点（バイナリと ASCII）
    private static readonly byte[] BinaryRead = Convert.FromHexString("500000FFFF03000C00100001040000640000A80100");
    private static readonly byte[] AsciiRead = Encoding.ASCII.GetBytes("500000FF03FF000018001004010000D*0001000001");

    private static async Task<SlmpServer> StartAsync(DataCodeSetting dataCode)
    {
        var server = new SlmpServer(
            new SlmpEngine(new PlcModel(CpuProfiles.Get(CpuProfiles.IqR))),
            new SlmpServerOptions { TcpPort = 0, UdpPort = 0, DataCode = dataCode });
        await server.StartAsync();
        return server;
    }

    private static async Task<byte[]?> SendTcpAsync(SlmpServer server, byte[] request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.TcpPort);
        var stream = client.GetStream();
        await stream.WriteAsync(request);
        var buffer = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            return buffer[..read];
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> SendUdpAsync(SlmpServer server, byte[] request)
    {
        using var client = new UdpClient();
        await client.SendAsync(request, new IPEndPoint(IPAddress.Loopback, server.UdpPort));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            return (await client.ReceiveAsync(timeout.Token)).Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Theory]
    [InlineData(DataCodeSetting.Auto, true, true)]
    [InlineData(DataCodeSetting.Binary, true, false)]
    [InlineData(DataCodeSetting.Ascii, false, true)]
    public async Task RespondsOnlyToConfiguredDataCode(DataCodeSetting setting, bool binaryAnswered, bool asciiAnswered)
    {
        await using var server = await StartAsync(setting);
        Assert.Equal(binaryAnswered, await SendTcpAsync(server, BinaryRead) is not null);
        Assert.Equal(asciiAnswered, await SendTcpAsync(server, AsciiRead) is not null);
        Assert.Equal(binaryAnswered, await SendUdpAsync(server, BinaryRead) is not null);
        Assert.Equal(asciiAnswered, await SendUdpAsync(server, AsciiRead) is not null);
    }

    [Fact]
    public async Task IgnoredRequestIsLoggedAndConnectionStaysUsable()
    {
        await using var server = await StartAsync(DataCodeSetting.Binary);
        var log = new List<FrameLogEntry>();
        server.Engine.FrameProcessed += log.Add;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.TcpPort);
        var stream = client.GetStream();
        await stream.WriteAsync(AsciiRead);
        await stream.WriteAsync(BinaryRead);
        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer);

        // ASCII は無応答で通信ログに残り、同じ接続の次のバイナリ要求には応答する。
        Assert.Equal(0xD0, buffer[0]);
        Assert.True(read >= 11);
        Assert.Contains(log, e => e.Response is null && e.Summary.Contains("交信データコード", StringComparison.Ordinal));
    }
}
