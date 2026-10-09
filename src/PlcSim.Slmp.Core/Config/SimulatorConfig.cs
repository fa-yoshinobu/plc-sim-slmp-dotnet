using System.Text.Json;
using System.Text.Json.Serialization;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Server;

namespace PlcSim.Slmp.Core.Config;

/// <summary>起動時の初期値。<see cref="Type"/> は BIT/U16/S16/U32/S32/F32/STRING。</summary>
public sealed class InitialValue
{
    public string Address { get; set; } = string.Empty;

    public string Type { get; set; } = "U16";

    public List<JsonElement> Values { get; set; } = new();
}

/// <summary>PLC 設定。</summary>
public sealed class PlcConfig
{
    /// <summary>プロファイル ID（melsec:iq-r, melsec:qnudv, melsec:iq-f など）。</summary>
    public string Profile { get; set; } = CpuProfiles.IqR;

    public string? ModelName { get; set; }

    public ushort? ModelCode { get; set; }

    /// <summary>ファミリ別点数の上書き（実機のパラメータ設定に合わせる）。</summary>
    public Dictionary<string, uint> DevicePoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool RunWriteAllowed { get; set; } = true;

    public bool RemoteResetAllowed { get; set; } = true;

    /// <summary>リモートリセット後に応答しない時間（ミリ秒）。実機 iQ-R 系は約 22 秒。</summary>
    public int ResetRestartMs { get; set; }

    public string? RemotePassword { get; set; }

    public PlcRunState InitialState { get; set; } = PlcRunState.Run;

    /// <summary>装着ユニット（Un\G）の番号。16 進文字列（"0", "2", "1F"）。最大 4 個。既定は U0。</summary>
    public List<string> Units { get; set; } = new() { "0" };

    /// <summary>リンクダイレクト（Jn\）のネットワーク番号。最大 4 個。既定は J1。</summary>
    public List<int> Networks { get; set; } = new() { 1 };

    /// <summary>ラベル定義。省略時は iQ-R で SLMP_TEST（実機検査定義）。</summary>
    public LabelDefinitions? Labels { get; set; }
}

/// <summary>HTTP 制御 API 設定。</summary>
public sealed class ApiConfig
{
    public bool Enabled { get; set; } = true;

    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5080;
}

/// <summary>GUI の表示設定。</summary>
public sealed class GuiConfig
{
    /// <summary>"system"（Windows の設定に従う）/ "light" / "dark"。</summary>
    public string Theme { get; set; } = "system";
}

/// <summary>シミュレータ設定ファイル（config/default.json）。</summary>
public sealed class SimulatorConfig
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public SlmpServerOptions Server { get; set; } = new();

    public PlcConfig Plc { get; set; } = new();

    public ApiConfig Api { get; set; } = new();

    public GuiConfig Gui { get; set; } = new();

    public List<InitialValue> InitialValues { get; set; } = new();

    /// <summary>Python の実行ファイル（PATH 上の python、または絶対パス）。</summary>
    public string PythonExecutable { get; set; } = "python";

    /// <summary>起動時に実行する Python スクリプト（設定ファイルからの相対パス可）。</summary>
    public List<string> Scripts { get; set; } = new();

    public List<ErrorInjectionRule> ErrorInjection { get; set; } = new();

    /// <summary>設定ファイルのディレクトリ（相対パス解決用）。</summary>
    [JsonIgnore]
    public string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    public static SimulatorConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<SimulatorConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"設定ファイル {path} が空です");
        config.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return config;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseDirectory, path));

    /// <summary>設定から仮想 PLC を作り、初期値を書き込む。</summary>
    public PlcModel CreatePlc()
    {
        var profile = CpuProfiles.Get(Plc.Profile).With(Plc.ModelName, Plc.ModelCode, Plc.DevicePoints);
        var settings = new PlcSettings
        {
            RunWriteAllowed = Plc.RunWriteAllowed,
            RemoteResetAllowed = Plc.RemoteResetAllowed,
            ResetRestartMs = Plc.ResetRestartMs,
            RemotePassword = string.IsNullOrEmpty(Plc.RemotePassword) ? null : Plc.RemotePassword,
            InitialState = Plc.InitialState,
            Units = Plc.Units.Select(static u => Convert.ToUInt16(u.Trim().TrimStart('U', 'u'), 16)).ToArray(),
            Networks = Plc.Networks.Select(static n => checked((byte)n)).ToArray(),
        };
        var plc = new PlcModel(profile, settings, Plc.Labels);
        ApplyInitialValues(new PlcController(plc));
        return plc;
    }

    public SlmpEngine CreateEngine(PlcModel plc)
    {
        var engine = new SlmpEngine(plc);
        foreach (var rule in ErrorInjection)
            engine.Injector.Add(rule);
        return engine;
    }

    private void ApplyInitialValues(PlcController controller)
    {
        foreach (var item in InitialValues)
        {
            var type = item.Type.ToUpperInvariant();
            var address = controller.Parse(item.Address);
            if (type == "STRING")
            {
                var text = item.Values.FirstOrDefault().GetString() ?? string.Empty;
                controller.SetString(item.Address, text, (text.Length + 2) / 2, "初期値");
                continue;
            }

            var step = type is "U32" or "S32" or "F32" ? 2u : 1u;
            for (var i = 0; i < item.Values.Count; i++)
            {
                var target = address with { Number = address.Number + ((uint)i * step) };
                var raw = item.Values[i].ValueKind == JsonValueKind.String ? item.Values[i].GetString()! : item.Values[i].GetRawText();
                controller.WriteValue(target.ToStringForParse(controller.Plc.Profile), raw, type, "初期値");
            }
        }
    }
}

internal static class DeviceAddressFormatting
{
    /// <summary>プロファイルの表記基数で再解析可能な文字列にする。</summary>
    public static string ToStringForParse(this DeviceAddress address, CpuProfile profile)
    {
        var number = PlcSim.Slmp.Core.Engine.MemoryDeviceAccessor.FormatNumber(profile, address.Device, address.Number);
        return address.Qualifier is null ? $"{address.Device.Name}{number}" : $"{address.Qualifier}\\{address.Device.Name}{number}";
    }
}
