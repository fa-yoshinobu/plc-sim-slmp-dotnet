using System.Globalization;
using System.Net;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Config;

public sealed record ConfigIssue(string Field, string Value, string Reason)
{
    public override string ToString() => $"{Field} = {Value}: {Reason}";
}

public sealed class ConfigValidationException(IReadOnlyList<ConfigIssue> issues)
    : ArgumentException(string.Join(Environment.NewLine, issues))
{
    public IReadOnlyList<ConfigIssue> Issues { get; } = issues;
}

public sealed partial class SimulatorConfig
{
    public static int ParsePort(string field, string value, bool allowZero = false)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port < (allowZero ? 0 : 1) || port > 65535)
            throw new ConfigValidationException([new(field, value, $"{(allowZero ? 0 : 1)}〜65535の整数を指定してください")]);
        return port;
    }

    public void ValidateAndThrow()
    {
        var issues = Validate();
        if (issues.Count != 0)
            throw new ConfigValidationException(issues);
    }

    public IReadOnlyList<ConfigIssue> Validate()
    {
        var issues = new List<ConfigIssue>();
        void Check(bool valid, string field, object? value, string reason)
        {
            if (!valid) issues.Add(new(field, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null", reason));
        }
        Check(Server is not null, "server", null, "オブジェクトが必要です");
        Check(Api is not null, "api", null, "オブジェクトが必要です");
        Check(Plc is not null, "plc", null, "オブジェクトが必要です");
        Check(Gui is not null, "gui", null, "オブジェクトが必要です");
        Check(InitialValues is not null, "initialValues", null, "配列が必要です");
        Check(Scripts is not null && Scripts.All(s => !string.IsNullOrWhiteSpace(s)), "scripts", null, "パスの配列が必要です");
        Check(ErrorInjection is not null && ErrorInjection.All(r => r is not null), "errorInjection", null, "ルールの配列が必要です");
        if (issues.Count != 0) return issues;
        Check(IPAddress.TryParse(Server!.BindAddress, out _), "server.bindAddress", Server.BindAddress, "IPアドレスを指定してください");
        Check(Server.TcpPort is >= 0 and <= 65535, "server.tcpPort", Server.TcpPort, "0〜65535で指定してください");
        Check(Server.UdpPort is >= 0 and <= 65535, "server.udpPort", Server.UdpPort, "0〜65535で指定してください");
        Check(Enum.IsDefined(Server.DataCode), "server.dataCode", Server.DataCode, "Auto/Binary/Asciiを指定してください");
        var localhost = string.Equals(Api!.BindAddress, "localhost", StringComparison.OrdinalIgnoreCase);
        var ipValid = IPAddress.TryParse(Api.BindAddress, out var apiIp);
        Check(localhost || ipValid, "api.bindAddress", Api.BindAddress, "IPアドレスまたはlocalhostを指定してください");
        Check(Api.Port is >= 1 and <= 65535, "api.port", Api.Port, "1〜65535で指定してください");
        Check(!Api.Enabled || localhost || apiIp is not null && IPAddress.IsLoopback(apiIp) || !string.IsNullOrWhiteSpace(Api.Token),
            "api.token", "(非表示)", "ループバック以外ではトークンが必要です");
        Check(Gui!.Theme is "system" or "light" or "dark", "gui.theme", Gui.Theme, "system/light/darkを指定してください");
        Check(!string.IsNullOrWhiteSpace(PythonExecutable), "pythonExecutable", PythonExecutable, "実行ファイルを指定してください");
        CpuProfile? profile = null;
        try { profile = CpuProfiles.Get(Plc!.Profile); }
        catch (ArgumentException ex) { issues.Add(new("plc.profile", Plc!.Profile ?? "null", ex.Message)); }
        Check(Plc!.ResetRestartMs >= 0, "plc.resetRestartMs", Plc.ResetRestartMs, "0以上で指定してください");
        Check(Enum.IsDefined(Plc.InitialState), "plc.initialState", Plc.InitialState, "Run/Stop/Pauseを指定してください");
        Check(Plc.Units is not null && Plc.Units.Count <= PlcSettings.MaxUnits, "plc.units", Plc.Units?.Count, "最大4個の配列が必要です");
        var units = new HashSet<ushort>();
        foreach (var unit in Plc.Units ?? [])
        {
            var valid = ushort.TryParse(unit?.Trim().TrimStart('U', 'u'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number);
            Check(valid && number <= 255 && units.Add(number), "plc.units", unit, "重複のない16進数0〜FFで指定してください");
        }
        Check(Plc.Networks is not null && Plc.Networks.Count <= PlcSettings.MaxNetworks, "plc.networks", Plc.Networks?.Count, "最大4個の配列が必要です");
        var networks = new HashSet<int>();
        foreach (var network in Plc.Networks ?? [])
            Check(network is >= 1 and <= 239 && networks.Add(network), "plc.networks", network, "重複のない1〜239で指定してください");
        Check(Plc.DevicePoints is not null, "plc.devicePoints", null, "オブジェクトが必要です");
        foreach (var (family, count) in Plc.DevicePoints ?? [])
        {
            Check(profile?.Has(family) != false, $"plc.devicePoints.{family}", count, "この機種にはないデバイスです");
            var words = DeviceCatalog.All.Where(d => d.Family == family)
                .Select(d => d.Kind == DeviceKind.LongTimer ? 4 : d.Kind == DeviceKind.DoubleWord ? 2 : 1).DefaultIfEmpty(1).Max();
            Check(count > 0 && (ulong)count * (uint)words <= (ulong)Array.MaxLength,
                $"plc.devicePoints.{family}", count, "点数は1以上で、格納先配列の上限以内にしてください");
        }
        if (Plc.Labels is { } labels)
            foreach (var error in labels.Validate())
                issues.Add(new("plc.labels", "設定値", error));
        if (issues.Count != 0) return issues;
        PlcController controller;
        try { controller = new PlcController(CreatePlcModel()); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            issues.Add(new("plc", "設定値", ex.Message));
            return issues;
        }
        for (var i = 0; i < InitialValues!.Count; i++)
        {
            var item = InitialValues[i];
            if (item is null || item.Values is not { Count: > 0 } || string.IsNullOrWhiteSpace(item.Address)
                || item.Type?.ToUpperInvariant() is not ("BIT" or "U16" or "S16" or "U32" or "S32" or "F32" or "STRING"))
            {
                issues.Add(new($"initialValues[{i}]", item?.Address ?? "null", "番地・対応する型・空でないvaluesが必要です"));
                continue;
            }
            try
            {
                foreach (var value in item.Values)
                {
                    var raw = value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString()! : value.GetRawText();
                    var type = item.Type.ToUpperInvariant();
                    if (type is "U16" or "S16" or "U32" or "S32")
                    {
                        raw = raw.Trim();
                        var hexadecimal = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                        var number = hexadecimal
                            ? long.Parse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                            : long.Parse(raw, CultureInfo.InvariantCulture);
                        (long min, long max) = type switch
                        {
                            "U16" => (0L, (long)ushort.MaxValue), "S16" => ((long)short.MinValue, (long)short.MaxValue),
                            "U32" => (0L, (long)uint.MaxValue), _ => ((long)int.MinValue, (long)int.MaxValue),
                        };
                        // Signed hexadecimal initial values also accept their raw bit pattern.
                        if (hexadecimal && type == "S16") (min, max) = (0, ushort.MaxValue);
                        if (hexadecimal && type == "S32") (min, max) = (0, uint.MaxValue);
                        if (number < min || number > max) throw new ArgumentException($"{raw} は {type} の範囲外です");
                    }
                    if (type == "BIT" && raw.ToLowerInvariant() is not ("0" or "1" or "false" or "true" or "on" or "off"))
                        throw new ArgumentException($"{raw} はBIT値ではありません");
                    if (type == "F32" && !float.IsFinite(float.Parse(raw.TrimEnd('f', 'F'), CultureInfo.InvariantCulture)))
                        throw new ArgumentException($"{raw} は有限のF32値ではありません");
                }
                ApplyInitialValue(controller, item);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException or SlmpEndCodeException)
            {
                issues.Add(new($"initialValues[{i}]", $"{item.Address}/{item.Type}", ex.Message));
            }
        }
        return issues;
    }
}
