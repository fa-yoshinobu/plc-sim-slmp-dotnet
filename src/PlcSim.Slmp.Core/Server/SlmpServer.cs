using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Server;

/// <summary>待受設定。ポート 0 は空きポートを自動割当て（テスト用）。</summary>
/// <summary>交信データコード（実機の「交信データコード設定」）。</summary>
public enum DataCodeSetting
{
    /// <summary>バイナリと ASCII の両方に応答する（PLC Sim 独自。実機はどちらか一方）。</summary>
    Auto,

    /// <summary>バイナリだけに応答する。ASCII の要求は無応答（実機計測どおり）。</summary>
    Binary,

    /// <summary>ASCII だけに応答する。バイナリの要求は無応答（実機計測どおり）。</summary>
    Ascii,
}

public sealed class SlmpServerOptions
{
    public string BindAddress { get; set; } = "127.0.0.1";

    public bool EnableTcp { get; set; } = true;

    public int TcpPort { get; set; } = 1025;

    public bool EnableUdp { get; set; } = true;

    public int UdpPort { get; set; } = 1025;

    /// <summary>
    /// 応答する交信データコード。実機（iQ-R 系、2026-10-09）は設定と違うコードの要求にエラーも返さず無応答だった。
    /// </summary>
    public DataCodeSetting DataCode { get; set; } = DataCodeSetting.Auto;
}

/// <summary>SLMP の TCP / UDP サーバー。TCP は複数接続を同時に受け付け、接続ごとにセッションを持つ。</summary>
public sealed class SlmpServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SlmpSession> _sessions = new();
    private readonly ConcurrentDictionary<string, SlmpSession> _udpSessions = new();
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new();
    private CancellationTokenSource? _cts;
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private readonly List<Task> _loops = new();

    public SlmpServer(SlmpEngine engine, SlmpServerOptions options)
    {
        Engine = engine;
        Options = options;
    }

    public SlmpEngine Engine { get; }

    public SlmpServerOptions Options { get; }

    /// <summary>待受中か。受信処理が異常終了していれば false。</summary>
    public bool IsRunning => _cts is not null && !_loops.Any(static t => t.IsFaulted);

    public int TcpPort { get; private set; }

    public int UdpPort { get; private set; }

    /// <summary>接続中の TCP セッション。</summary>
    public IReadOnlyCollection<SlmpSession> Sessions => _sessions.Values.ToArray();

    public event Action<SlmpSession>? SessionOpened;

    public event Action<SlmpSession>? SessionClosed;

    /// <summary>不正フレームなどの通知。</summary>
    public event Action<string>? Diagnostic;

    public Task StartAsync()
    {
        if (_cts is not null)
            throw new InvalidOperationException("既に起動しています");
        _cts = new CancellationTokenSource();
        var address = IPAddress.Parse(Options.BindAddress);
        try
        {
            if (Options.EnableTcp)
            {
                _tcp = new TcpListener(address, Options.TcpPort);
                _tcp.Start();
                TcpPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;
                _loops.Add(AcceptLoopAsync(_tcp, _cts.Token));
            }

            if (Options.EnableUdp)
            {
                _udp = new UdpClient(new IPEndPoint(address, Options.UdpPort));
                UdpPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
                _loops.Add(UdpLoopAsync(_udp, _cts.Token));
            }
        }
        catch
        {
            _ = StopAsync();
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null)
            return;
        _cts = null;
        cts.Cancel();
        _tcp?.Stop();
        _udp?.Dispose();
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch
        {
            // 停止時の例外は無視する
        }

        _loops.Clear();
        _udpSessions.Clear();
        cts.Dispose();
        _tcp = null;
        _udp = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        var clients = new List<Task>();
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            // リセット後の再起動中は接続を受け付けない（実機は再起動中に接続できなかった）。
            if (Engine.Plc.IsRestarting)
            {
                client.Dispose();
                continue;
            }

            clients.RemoveAll(static t => t.IsCompleted);
            clients.Add(HandleClientAsync(client, token));
        }

        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var session = new SlmpSession(remote, "TCP");
        var key = $"{remote}#{Guid.NewGuid():N}";
        _sessions[key] = session;
        _clients[key] = client;
        SessionOpened?.Invoke(session);
        try
        {
            client.NoDelay = true;
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var pending = new List<byte>();
                while (!token.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    pending.AddRange(buffer.AsSpan(0, read).ToArray());
                    while (true)
                    {
                        int length;
                        try
                        {
                            length = FrameCodec.GetFrameLength(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending));
                        }
                        catch (SlmpFrameFormatException ex)
                        {
                            Diagnostic?.Invoke($"{session}: {ex.Message}（受信データを破棄）");
                            pending.Clear();
                            break;
                        }

                        if (length == 0 || pending.Count < length)
                            break;
                        var frame = pending.GetRange(0, length).ToArray();
                        pending.RemoveRange(0, length);
                        var response = ProcessFrame(frame, session);
                        if (response.Disconnect)
                        {
                            DisconnectAll();
                            return;
                        }

                        if (response.Frame is null)
                            continue;
                        if (response.DelayMs > 0)
                            await Task.Delay(response.DelayMs, token).ConfigureAwait(false);
                        await stream.WriteAsync(response.Frame, token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            _sessions.TryRemove(key, out _);
            _clients.TryRemove(key, out _);
            SessionClosed?.Invoke(session);
        }
    }

    private async Task UdpLoopAsync(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var remote = received.RemoteEndPoint.ToString();
            var session = _udpSessions.GetOrAdd(remote, static r => new SlmpSession(r, "UDP"));
            EngineResult response;
            try
            {
                if (FrameCodec.GetFrameLength(received.Buffer) != received.Buffer.Length)
                    throw new SlmpFrameFormatException("データグラム長とフレーム長が一致しません");
                response = ProcessFrame(received.Buffer, session);
            }
            catch (SlmpFrameFormatException ex)
            {
                Diagnostic?.Invoke($"{session}: {ex.Message}（破棄）");
                continue;
            }

            if (response.Disconnect)
                DisconnectAll();
            if (response.Frame is null)
                continue;
            _ = SendUdpAsync(udp, response, received.RemoteEndPoint, token);
        }
    }

    private async Task SendUdpAsync(UdpClient udp, EngineResult response, IPEndPoint remote, CancellationToken token)
    {
        try
        {
            if (response.DelayMs > 0)
                await Task.Delay(response.DelayMs, token).ConfigureAwait(false);
            await udp.SendAsync(response.Frame!, remote, token).ConfigureAwait(false);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // 停止中は無視する
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Diagnostic?.Invoke($"UDP {remote}: 応答 {response.Frame!.Length} バイトを送れません（{ex.Message}）");
        }
    }

    /// <summary>全 TCP 接続を切る（リモートリセットで PLC が再起動するとき）。</summary>
    private void DisconnectAll()
    {
        foreach (var client in _clients.Values)
        {
            try
            {
                client.Client.Close(0);
            }
            catch (Exception)
            {
                // すでに閉じている接続は無視する
            }
        }

        _udpSessions.Clear();
    }

    private EngineResult ProcessFrame(byte[] frame, SlmpSession session)
    {
        var request = FrameCodec.Parse(frame);
        try
        {
            return ProcessRequest(request, session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 想定外の例外で受信処理全体を止めない。その要求には応答せず、通信ログと診断に残す。
            Diagnostic?.Invoke($"{session}: 要求の処理中に想定外のエラー {ex.GetType().Name}: {ex.Message}");
            return Engine.Ignore(request, session, $"無応答（PLC Sim の内部エラー: {ex.GetType().Name}）", countRequest: false);
        }
    }

    private EngineResult ProcessRequest(SlmpRequest request, SlmpSession session)
    {
        var mismatch = Options.DataCode switch
        {
            DataCodeSetting.Binary => request.IsAscii,
            DataCodeSetting.Ascii => !request.IsAscii,
            _ => false,
        };
        return mismatch
            ? Engine.Ignore(request, session, $"無応答（交信データコードが{(Options.DataCode == DataCodeSetting.Binary ? "バイナリ" : "ASCII")}設定のため）")
            : Engine.Process(request, session);
    }
}
