using System.Net;
using System.Net.Sockets;
using System.Text;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Server;

namespace PlcSim.Slmp.Tests;

/// <summary>
/// リモートリセット（1006）。実機 iQ-R 系（2026-10-09）: RUN 中・PAUSE 中は 4013、STOP 中は応答せずに接続を切って再起動し、
/// 約 22 秒は接続できず、再起動後は RUN/STOP スイッチに従う。
/// </summary>
public sealed class RemoteResetTests
{
    private static readonly byte[] Reset = Encoding.ASCII.GetBytes("500000FF03FF0000100010100600000001");
    private static readonly byte[] Stop = Encoding.ASCII.GetBytes("500000FF03FF0000100010100200000001");
    private static readonly byte[] Pause = Encoding.ASCII.GetBytes("500000FF03FF0000100010100300000001");
    private static readonly byte[] ReadSd203 = Encoding.ASCII.GetBytes("500000FF03FF000018001004010000SD0002030001");

    private static async Task<SlmpServer> StartAsync(int restartMs = 0)
    {
        var server = new SlmpServer(
            new SlmpEngine(new PlcModel(CpuProfiles.Get(CpuProfiles.IqR), new PlcSettings { ResetRestartMs = restartMs })),
            new SlmpServerOptions { TcpPort = 0, UdpPort = 0 });
        await server.StartAsync();
        return server;
    }

    /// <summary>1 要求を送り、応答（なければ null、切断なら空文字）を返す。</summary>
    private static async Task<string?> SendAsync(SlmpServer server, byte[] request, int timeoutMs = 500)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.TcpPort);
        var stream = client.GetStream();
        await stream.WriteAsync(request);
        var buffer = new byte[256];
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            return Encoding.ASCII.GetString(buffer, 0, read);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    [Fact]
    public async Task ResetIsRejectedInRunAndPause()
    {
        await using var server = await StartAsync();
        Assert.Equal("D00000FF03FF000016401300FF03FF0010060000", await SendAsync(server, Reset));
        await SendAsync(server, Pause);
        Assert.Equal("D00000FF03FF000016401300FF03FF0010060000", await SendAsync(server, Reset));
    }

    [Fact]
    public async Task ResetInStopClosesConnectionsWithoutResponseAndRestartsInRun()
    {
        await using var server = await StartAsync();
        await SendAsync(server, Stop);

        // 別の接続も一緒に切れる
        using var other = new TcpClient();
        await other.ConnectAsync(IPAddress.Loopback, server.TcpPort);
        Assert.Equal(string.Empty, await SendAsync(server, Reset));
        var buffer = new byte[16];
        using var timeout = new CancellationTokenSource(1000);
        int read;
        try
        {
            read = await other.GetStream().ReadAsync(buffer, timeout.Token);
        }
        catch (IOException)
        {
            read = 0; // 強制切断
        }

        Assert.Equal(0, read);

        Assert.Equal(PlcRunState.Run, server.Engine.Plc.State);
        Assert.Equal("D00000FF03FF00000800000000", await SendAsync(server, ReadSd203));
    }

    [Fact]
    public async Task NoResponseWhileRestarting()
    {
        await using var server = await StartAsync(restartMs: 800);
        await SendAsync(server, Stop);
        await SendAsync(server, Reset);
        Assert.True(server.Engine.Plc.IsRestarting);
        Assert.True(await SendAsync(server, ReadSd203) is null or "");
        await Task.Delay(900);
        Assert.Equal("D00000FF03FF00000800000000", await SendAsync(server, ReadSd203));
    }
}
