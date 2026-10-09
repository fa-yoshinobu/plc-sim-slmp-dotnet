using System.Text.Json;
using System.Threading.Channels;
using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;
using PlcSim.Slmp.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PlcSim.Slmp.Scripting;

/// <summary>PUT /api/devices の本文。</summary>
public sealed record DeviceWriteRequest(List<JsonElement> Values, string? Type);

/// <summary>PUT /api/labels の本文（hex か words のどちらか）。</summary>
public sealed record LabelWriteRequest(string? Hex, List<ushort>? Words);

/// <summary>POST /api/plc/error の本文。</summary>
public sealed record ErrorCodeRequest(ushort Code);

/// <summary>POST /api/devices/{address}/inc の本文。</summary>
public sealed record IncrementRequest(long? Step, string? Type);

/// <summary>
/// Python などの外部スクリプト向け HTTP/JSON 制御 API。既定は 127.0.0.1 のみ待受。
/// 書込み元は X-Source ヘッダ（なければ "HTTP API"）で記録する。
/// GET /api/events は Server-Sent Events で書込み・状態変化を配信する。
/// </summary>
public sealed class ControlApi : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly List<Channel<string>> _subscribers = new();
    private PlcModel? _subscribedPlc;

    public ControlApi(SlmpServer server, PlcController controller, FrameLogBuffer log, PythonScriptHost? scripts = null)
    {
        Server = server;
        Controller = controller;
        FrameLog = log;
        Scripts = scripts;
    }

    public SlmpServer Server { get; }

    public PlcController Controller { get; }

    public FrameLogBuffer FrameLog { get; }

    public PythonScriptHost? Scripts { get; }

    public string? Url { get; private set; }

    public async Task StartAsync(string bindAddress, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });
        Url = $"http://{bindAddress}:{port}";
        builder.WebHost.UseUrls(Url);
        var app = builder.Build();
        Map(app);
        SubscribeEvents();
        await app.StartAsync().ConfigureAwait(false);
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        UnsubscribeEvents();
        lock (_subscribers)
        {
            foreach (var channel in _subscribers)
                channel.Writer.TryComplete();
            _subscribers.Clear();
        }

        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }

    private static string SourceOf(HttpContext http)
        => http.Request.Headers.TryGetValue("X-Source", out var value) && !string.IsNullOrWhiteSpace(value) ? Uri.UnescapeDataString(value.ToString()) : "HTTP API";

    private void Map(WebApplication app)
    {
        var plc = Controller.Plc;

        app.MapGet("/api/status", () => Results.Ok(new
        {
            profile = plc.Profile.Id,
            model = plc.Profile.ModelName,
            modelCode = $"{plc.Profile.ModelCode:X4}",
            state = plc.State,
            errorCode = plc.ErrorCode,
            tcpPort = Server.TcpPort,
            udpPort = Server.UdpPort,
            requests = Server.Engine.RequestCount,
            errors = Server.Engine.ErrorCount,
            sessions = Server.Sessions.Select(static s => new { s.Remote, s.Transport, s.Connected, s.RequestCount }),
        }));

        app.MapGet("/api/devices/{address}", (string address, int? count, string? type) => Guard(() =>
        {
            var n = count ?? 1;
            var values = (type ?? "u16").ToLowerInvariant() switch
            {
                "bit" => Controller.ReadBits(address, n).Select(static b => (object)b).ToArray(),
                "s16" => Controller.ReadWords(address, n).Select(static w => (object)(short)w).ToArray(),
                "hex" => Controller.ReadWords(address, n).Select(static w => (object)$"{w:X4}").ToArray(),
                "u32" => ReadDWords(address, n).Select(static d => (object)d).ToArray(),
                "s32" => ReadDWords(address, n).Select(static d => (object)(int)d).ToArray(),
                "f32" => ReadDWords(address, n).Select(static d => (object)BitConverter.Int32BitsToSingle((int)d)).ToArray(),
                "str" => new object[] { Controller.GetString(address, n) },
                _ => Controller.ReadWords(address, n).Select(static w => (object)w).ToArray(),
            };
            return Results.Ok(new { address, type = type ?? "u16", values });
        }));

        app.MapPut("/api/devices/{address}", (string address, DeviceWriteRequest body, HttpContext http) => Guard(() =>
        {
            var source = SourceOf(http);
            var type = (body.Type ?? "u16").ToUpperInvariant();
            if (type == "STR")
            {
                var text = body.Values.FirstOrDefault().GetString() ?? string.Empty;
                Controller.SetString(address, text, (text.Length + 2) / 2, source);
                return Results.Ok(new { address, written = 1 });
            }

            var parsed = Controller.Parse(address);
            var step = type is "U32" or "S32" or "F32" && parsed.Device.Kind is not (DeviceKind.DoubleWord or DeviceKind.LongTimer) ? 2u : 1u;
            lock (plc.SyncRoot)
            {
                for (var i = 0; i < body.Values.Count; i++)
                {
                    var target = parsed with { Number = parsed.Number + ((uint)i * step) };
                    var raw = body.Values[i].ValueKind == JsonValueKind.String ? body.Values[i].GetString()! : body.Values[i].GetRawText();
                    Controller.WriteValue(Format(target), raw, type, source);
                }
            }

            return Results.Ok(new { address, written = body.Values.Count });
        }));

        app.MapPost("/api/devices/{address}/inc", (string address, IncrementRequest? body, HttpContext http) => Guard(() =>
        {
            var source = SourceOf(http);
            var dword = (body?.Type ?? "u16").ToUpperInvariant() is "U32" or "S32";
            lock (plc.SyncRoot)
            {
                if (dword)
                {
                    var value = unchecked((uint)(Controller.GetDWord(address) + (body?.Step ?? 1)));
                    Controller.SetDWord(address, value, source);
                    return Results.Ok(new { address, value });
                }

                var word = unchecked((ushort)(Controller.GetWord(address) + (body?.Step ?? 1)));
                Controller.SetWord(address, word, source);
                return Results.Ok(new { address, value = (uint)word });
            }
        }));

        app.MapPost("/api/devices/{address}/toggle", (string address, HttpContext http) => Guard(() =>
        {
            lock (plc.SyncRoot)
            {
                var value = !Controller.GetBit(address);
                Controller.SetBit(address, value, SourceOf(http));
                return Results.Ok(new { address, value });
            }
        }));

        app.MapGet("/api/labels", () => Results.Ok(Controller.Labels().Select(static l => new { l.Path, l.Type, hex = Convert.ToHexString(l.Data) })));
        app.MapGet("/api/labels/{label}", (string label) => Guard(() => Results.Ok(new { label, hex = Convert.ToHexString(Controller.ReadLabel(label)) })));
        app.MapPut("/api/labels/{label}", (string label, LabelWriteRequest body, HttpContext http) => Guard(() =>
        {
            var data = body.Hex is not null
                ? Convert.FromHexString(body.Hex)
                : (body.Words ?? new List<ushort>()).SelectMany(static w => new[] { (byte)w, (byte)(w >> 8) }).ToArray();
            Controller.WriteLabel(label, data, SourceOf(http));
            return Results.Ok(new { label, bytes = data.Length });
        }));

        app.MapPost("/api/plc/run", (HttpContext http) => SetState(PlcRunState.Run, http));
        app.MapPost("/api/plc/stop", (HttpContext http) => SetState(PlcRunState.Stop, http));
        app.MapPost("/api/plc/pause", (HttpContext http) => SetState(PlcRunState.Pause, http));
        app.MapPost("/api/plc/reset", (HttpContext http) =>
        {
            plc.Reset(SourceOf(http));
            return Results.Ok(new { state = plc.State });
        });
        app.MapPost("/api/plc/error", (ErrorCodeRequest body) =>
        {
            plc.SetErrorCode(body.Code);
            return Results.Ok(new { errorCode = body.Code });
        });

        app.MapGet("/api/errors", () => Results.Ok(Server.Engine.Injector.Rules));
        app.MapPost("/api/errors", (ErrorInjectionRule rule) =>
        {
            Server.Engine.Injector.Add(rule);
            return Results.Ok(Server.Engine.Injector.Rules);
        });
        app.MapDelete("/api/errors", () =>
        {
            Server.Engine.Injector.Clear();
            return Results.Ok();
        });

        app.MapGet("/api/log", (int? count) => Results.Ok(FrameLog.Latest(count ?? 100).Select(static e => new
        {
            e.Time,
            e.Session,
            command = $"{e.Request.Command:X4}",
            subcommand = $"{e.Request.Subcommand:X4}",
            endCode = $"{e.EndCode:X4}",
            e.Summary,
            request = Convert.ToHexString(e.Request.Raw),
            response = e.Response is null ? null : Convert.ToHexString(e.Response),
        })));

        app.MapGet("/api/snapshot", () => Results.Ok(MemorySnapshot.Capture(plc)));
        app.MapPost("/api/snapshot", (MemorySnapshot snapshot) =>
        {
            snapshot.Restore(plc);
            return Results.Ok();
        });

        app.MapGet("/api/scripts", () => Results.Ok((Scripts?.Scripts ?? Array.Empty<PythonScript>()).Select(static s => new { s.Name, s.Path, s.Status, s.Error })));

        // Server-Sent Events: {"type":"write","device":"D","start":100,"count":1,"source":"..."} / {"type":"state","state":"Stop"}
        app.MapGet("/api/events", async (HttpContext http) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(10000) { FullMode = BoundedChannelFullMode.DropOldest });
            lock (_subscribers)
                _subscribers.Add(channel);
            try
            {
                await http.Response.WriteAsync(": connected\n\n", http.RequestAborted).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
                await foreach (var message in channel.Reader.ReadAllAsync(http.RequestAborted).ConfigureAwait(false))
                {
                    await http.Response.WriteAsync($"data: {message}\n\n", http.RequestAborted).ConfigureAwait(false);
                    await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_subscribers)
                    _subscribers.Remove(channel);
            }
        });

        IResult SetState(PlcRunState state, HttpContext http)
        {
            Controller.SetState(state, SourceOf(http));
            return Results.Ok(new { state = plc.State });
        }
    }

    private void SubscribeEvents()
    {
        _subscribedPlc = Controller.Plc;
        _subscribedPlc.Memory.Changed += OnMemoryChanged;
        _subscribedPlc.BufferChanged += OnMemoryChanged;
        _subscribedPlc.StateChanged += OnStateChanged;
        _subscribedPlc.Labels.Changed += OnLabelChanged;
    }

    private void UnsubscribeEvents()
    {
        if (_subscribedPlc is null)
            return;
        _subscribedPlc.Memory.Changed -= OnMemoryChanged;
        _subscribedPlc.BufferChanged -= OnMemoryChanged;
        _subscribedPlc.StateChanged -= OnStateChanged;
        _subscribedPlc.Labels.Changed -= OnLabelChanged;
        _subscribedPlc = null;
    }

    private void OnMemoryChanged(MemoryChange change)
        => Publish(JsonSerializer.Serialize(new { type = "write", device = change.Device, start = change.Start, count = change.Count, source = change.Source }));

    private void OnStateChanged(PlcRunState state)
        => Publish(JsonSerializer.Serialize(new { type = "state", state = state.ToString() }));

    private void OnLabelChanged(string path, string source)
        => Publish(JsonSerializer.Serialize(new { type = "label", label = path, source }));

    private void Publish(string message)
    {
        lock (_subscribers)
        {
            foreach (var channel in _subscribers)
                channel.Writer.TryWrite(message);
        }
    }

    private string Format(DeviceAddress address)
    {
        var number = MemoryDeviceAccessor.FormatNumber(Controller.Plc.Profile, address.Device, address.Number);
        if (address.Qualifier is not null && address.Device.Name is "G" or "HG")
            number = address.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return address.Qualifier is null ? $"{address.Device.Name}{number}" : $"{address.Qualifier}\\{address.Device.Name}{number}";
    }

    private uint[] ReadDWords(string address, int count)
    {
        var parsed = Controller.Parse(address);
        if (parsed.Qualifier is null && parsed.Device.Kind is DeviceKind.DoubleWord or DeviceKind.LongTimer)
        {
            // LTN/LSTN/LCN/LZ は 1 点が 32 ビット値。
            return Enumerable.Range(0, count)
                .Select(i => Controller.GetDWord(Format(parsed with { Number = parsed.Number + (uint)i })))
                .ToArray();
        }

        var words = Controller.ReadWords(address, count * 2);
        return Enumerable.Range(0, count).Select(i => (uint)(words[i * 2] | (words[(i * 2) + 1] << 16))).ToArray();
    }

    private static IResult Guard(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (SlmpEndCodeException ex)
        {
            return Results.BadRequest(new { error = ex.Message, endCode = $"{ex.EndCode:X4}" });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
