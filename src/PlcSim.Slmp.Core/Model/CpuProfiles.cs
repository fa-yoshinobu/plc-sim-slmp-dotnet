using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Model;

/// <summary>
/// 組込みプロファイル（Q / L / iQ-R / iQ-F）。
/// iQ-R の点数は実機 iQ-R の最大デバイス試験で確認した値。
/// SD 点数レジスタ配置・機能可否・点数上限は plc-comm-slmp-dotnet の
/// slmp_device_range_rules.json / SlmpCapabilityProfiles（実機計測）に合わせる。
/// Q / iQ-F の点数は参考実装 SlmpMockServer の値（実機未照合）で、設定で上書きできる。
/// L は plc-comm-slmp-dotnet で QnU と同じ機能・点数上限・SD 配置のため QnU と同じ定義を使う（点数・形名コードは実機未照合）。
/// </summary>
public static class CpuProfiles
{
    public const string IqR = "melsec:iq-r";
    public const string IqRRj71En71 = "melsec:iq-r:rj71en71";
    public const string IqF = "melsec:iq-f";
    public const string QnU = "melsec:qnu";
    public const string QnUDV = "melsec:qnudv";
    public const string QCpuQj71E71100 = "melsec:qcpu:qj71e71-100";
    public const string QnUQj71E71100 = "melsec:qnu:qj71e71-100";
    public const string QnUDVQj71E71100 = "melsec:qnudv:qj71e71-100";
    public const string LCpu = "melsec:lcpu";
    public const string LCpuLj71E71100 = "melsec:lcpu:lj71e71-100";

    public static IReadOnlyList<string> Ids { get; } = new[]
    {
        IqR, IqRRj71En71, QnUDV, QnU, QCpuQj71E71100, QnUQj71E71100, QnUDVQj71E71100, LCpu, LCpuLj71E71100, IqF,
    };

    public static CpuProfile Get(string id) => id switch
    {
        IqR => IqRProfile(IqR, "MELSEC iQ-R (CPU内蔵Ethernet)"),
        IqRRj71En71 => IqRProfile(IqRRj71En71, "MELSEC iQ-R (RJ71EN71)"),
        QnUDV => QBuiltIn(QnUDV, "MELSEC-Q QnUDV (CPU内蔵Ethernet)"),
        QnU => QBuiltIn(QnU, "MELSEC-Q QnU (CPU内蔵Ethernet)"),
        QCpuQj71E71100 => QUnit(QCpuQj71E71100, "MELSEC-Q QCPU (QJ71E71-100)", BasicQSdRules(), extReadMax: 185),
        QnUQj71E71100 => QUnit(QnUQj71E71100, "MELSEC-Q QnU (QJ71E71-100)", QnUSdRules(), extReadMax: 192),
        QnUDVQj71E71100 => QUnit(QnUDVQj71E71100, "MELSEC-Q QnUDV (QJ71E71-100)", QnUSdRules(), extReadMax: 192),
        // [unverified] L の形名コードは未確認のため 0 を返す。実機に合わせるときは設定の plc.modelCode で指定する。
        LCpu => QBuiltIn(LCpu, "MELSEC-L (CPU内蔵Ethernet)", "L26CPU-BT", 0),
        LCpuLj71E71100 => QUnit(LCpuLj71E71100, "MELSEC-L (LJ71E71-100)", QnUSdRules(), extReadMax: 192, "L26CPU-BT", 0),
        IqF => IqFProfile(),
        _ => throw new ArgumentException($"未対応のプロファイル: {id}。使用可能: {string.Join(", ", Ids)}", nameof(id)),
    };

    private static CpuProfile IqRProfile(string id, string displayName) => new()
    {
        Id = id,
        DisplayName = displayName,
        Series = CpuSeries.IqR,
        ModelName = "R08CPU",
        ModelCode = 0x4801,
        Points = Points(
            ("X", 0x3000), ("Y", 0x3000), ("DX", 0x3000), ("DY", 0x3000),
            ("M", 262144), ("B", 0x40000), ("SB", 0x40000),
            ("F", 32768), ("V", 32768), ("L", 32768), ("S", 16384),
            ("D", 262144), ("W", 0x40000), ("SW", 0x40000),
            ("T", 262144), ("ST", 262144), ("C", 262144),
            ("LT", 262144), ("LST", 262144), ("LC", 262144),
            ("Z", 20), ("LZ", 2), ("ZR", 4915200), ("R", 32768), ("RD", 1048576),
            ("SM", 4096), ("SD", 4096), ("G", 65536), ("HG", 65536)),
        SdPointRules = IqrSdRules(includeLongTimers: true, rRegister: null),
        Features = new ProfileFeatures(),
        Limits = new ProfileLimits(
            DirectWord: new(960, EndCodes.PointsOverC051),
            DirectBit: new(7168, EndCodes.PointsOverC052),
            RandomRead: new(96, EndCodes.RandomWordPointsOver),
            RandomWrite: new(80, EndCodes.RandomWordPointsOver, 960),
            RandomBitWrite: new(94, EndCodes.RandomBitPointsOver),
            Monitor: new(96, EndCodes.RandomWordPointsOver),
            RandomReadExt: new(96, EndCodes.RandomWordPointsOver),
            RandomWriteExt: new(80, EndCodes.RandomWordPointsOver, 960),
            RandomBitWriteExt: new(94, EndCodes.RandomBitPointsOver)),
        ReadOnlyDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "S" },
    };

    /// <summary>Q / L の CPU 内蔵 Ethernet。</summary>
    private static CpuProfile QBuiltIn(string id, string displayName, string modelName = "Q03UDVCPU", ushort modelCode = 0x0366) => new()
    {
        Id = id,
        DisplayName = displayName,
        Series = CpuSeries.Q,
        ModelName = modelName,
        ModelCode = modelCode,
        Points = QPoints(),
        SdPointRules = QnUSdRules(),
        // 実機計測: 形名読出し・ブロック読書きは C059、U\G / J\ は内蔵ポートで不可。
        Features = new ProfileFeatures(
            TypeName: false,
            Block: false,
            IqrSubcommands: false,
            ModuleAccess: false,
            LinkDirect: false,
            HgCpuBuffer: false,
            Labels: false),
        Limits = QLimits(extReadMax: 192, extReadOver: EndCodes.RandomWordPointsOver, randomWriteOver: EndCodes.RandomWordPointsOver),
        ReadOnlyDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "S" },
    };

    /// <summary>Q / L の Ethernet ユニット（QJ71E71-100 / LJ71E71-100）経由。</summary>
    private static CpuProfile QUnit(string id, string displayName, IReadOnlyList<SdPointRule> sdRules, int extReadMax, string modelName = "Q03UDVCPU", ushort modelCode = 0x0366) => new()
    {
        Id = id,
        DisplayName = displayName,
        Series = CpuSeries.Q,
        ModelName = modelName,
        ModelCode = modelCode,
        Points = QPoints(),
        SdPointRules = sdRules,
        Features = new ProfileFeatures(IqrSubcommands: false, HgCpuBuffer: false, Labels: false),
        Limits = QLimits(
            extReadMax,
            extReadMax == 185 ? EndCodes.UnitRandomWritePointsOver : EndCodes.RandomWordPointsOver,
            EndCodes.UnitRandomWritePointsOver),
    };

    private static CpuProfile IqFProfile() => new()
    {
        Id = IqF,
        DisplayName = "MELSEC iQ-F (CPU内蔵Ethernet)",
        Series = CpuSeries.IqF,
        ModelName = "FX5U-32MR/ES",
        ModelCode = 0x4A21,
        Points = Points(
            ("X", 1024), ("Y", 1024), ("M", 32768), ("L", 32768), ("F", 32768), ("B", 0x8000),
            ("S", 4096), ("SB", 0x8000), ("SM", 10000), ("SD", 12000),
            ("T", 1024), ("ST", 1024), ("C", 1024), ("LC", 1024),
            ("D", 8000), ("W", 0x8000), ("SW", 0x8000),
            ("Z", 24), ("LZ", 2), ("R", 32768), ("G", 65536)),
        SdPointRules = IqrSdRules(includeLongTimers: false, rRegister: 304),
        // 実機計測: モニタ (0801/0802) は C059、J\ リンクダイレクトはエラー、HG は iQ-R 専用。
        Features = new ProfileFeatures(Monitor: false, IqrSubcommands: false, LinkDirect: false, HgCpuBuffer: false, Labels: false),
        Limits = new ProfileLimits(
            DirectWord: new(960, EndCodes.PointsOverC052),
            DirectBit: new(3584, EndCodes.PointsOverC051),
            RandomRead: new(192, EndCodes.RandomWordPointsOver),
            RandomWrite: new(160, EndCodes.RandomWordPointsOver, 1920),
            RandomBitWrite: new(188, EndCodes.RandomBitPointsOver),
            Monitor: new(192, EndCodes.RandomWordPointsOver),
            RandomReadExt: new(96, EndCodes.RandomWordPointsOver),
            RandomWriteExt: new(80, EndCodes.RandomWordPointsOver, 960),
            RandomBitWriteExt: new(94, EndCodes.RandomBitPointsOver)),
        FileRegisterShared = false,
        XyOctal = true,
    };

    private static Dictionary<string, uint> QPoints() => Points(
        ("X", 0x2000), ("Y", 0x2000), ("DX", 0x2000), ("DY", 0x2000),
        ("M", 32768), ("L", 8192), ("F", 2048), ("V", 2048), ("B", 0x8000), ("S", 8192),
        ("SB", 0x800), ("SM", 2048), ("SD", 2048),
        ("T", 2048), ("ST", 2048), ("C", 1024),
        ("D", 32768), ("W", 0x8000), ("SW", 0x800),
        ("Z", 20), ("ZR", 131072), ("R", 32768), ("G", 65536));

    private static ProfileLimits QLimits(int extReadMax, ushort extReadOver, ushort randomWriteOver) => new(
        DirectWord: new(960, EndCodes.PointsOverC051),
        DirectBit: new(7168, EndCodes.PointsOverC052),
        RandomRead: new(192, EndCodes.RandomWordPointsOver),
        RandomWrite: new(160, randomWriteOver, 1920),
        RandomBitWrite: new(188, EndCodes.RandomBitPointsOver),
        Monitor: new(192, EndCodes.RandomWordPointsOver),
        RandomReadExt: new(extReadMax, extReadOver),
        RandomWriteExt: new(160, EndCodes.UnitRandomWritePointsOver, 1920),
        RandomBitWriteExt: new(188, EndCodes.RandomBitPointsOver));

    private static List<SdPointRule> IqrSdRules(bool includeLongTimers, int? rRegister)
    {
        var rules = new List<SdPointRule>
        {
            new("X", 260, true), new("Y", 262, true), new("M", 264, true), new("B", 266, true),
            new("SB", 268, true), new("F", 270, true), new("V", 272, true), new("L", 274, true),
            new("S", 276, true), new("D", 280, true), new("W", 282, true), new("SW", 284, true),
            new("T", 288, true), new("ST", 290, true), new("C", 292, true),
            new("LC", 298, true), new("Z", 300, false), new("LZ", 302, false),
        };
        if (includeLongTimers)
        {
            rules.Add(new("LT", 294, true));
            rules.Add(new("LST", 296, true));
            rules.Add(new("ZR", 306, true));
            rules.Add(new("RD", 308, true));
        }

        if (rRegister is { } r)
            rules.Add(new("R", r, true));
        return rules;
    }

    private static List<SdPointRule> QnUSdRules() => new()
    {
        new("M", 286, true), new("B", 288, true), new("X", 290, false), new("Y", 291, false),
        new("L", 293, false), new("F", 295, false), new("SB", 296, false), new("V", 297, false),
        new("S", 298, false), new("T", 299, false), new("ST", 300, false), new("C", 301, false),
        new("SW", 304, false), new("ZR", 306, true), new("D", 308, true), new("W", 310, true),
    };

    private static List<SdPointRule> BasicQSdRules() => new()
    {
        new("X", 290, false), new("Y", 291, false), new("M", 292, false, 32768), new("L", 293, false),
        new("B", 294, false, 32768), new("F", 295, false), new("SB", 296, false), new("V", 297, false),
        new("S", 298, false), new("T", 299, false), new("ST", 300, false), new("C", 301, false),
        new("D", 302, false, 32768), new("W", 303, false, 32768), new("SW", 304, false),
    };

    private static Dictionary<string, uint> Points(params (string Family, uint Points)[] entries)
        => entries.ToDictionary(static e => e.Family, static e => e.Points, StringComparer.OrdinalIgnoreCase);
}
