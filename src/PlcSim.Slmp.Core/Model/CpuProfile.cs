namespace PlcSim.Slmp.Core.Model;

/// <summary>CPU 系列。</summary>
public enum CpuSeries
{
    IqR,
    Q,
    IqF,
}

/// <summary>SD レジスタに格納するデバイス点数の規則。</summary>
/// <param name="Family">点数のファミリ名。</param>
/// <param name="Register">SD 番号。</param>
/// <param name="DoubleWord">2 ワード（下位, 上位）で格納するか。</param>
/// <param name="Clip">格納値の上限（R の 32768 制限など）。</param>
public sealed record SdPointRule(string Family, int Register, bool DoubleWord, uint? Clip = null);

/// <summary>1 要求の点数上限と超過時の終了コード。</summary>
public sealed record RequestLimit(int Max, ushort OverEndCode, int? WeightedMax = null);

/// <summary>プロファイルの要求点数上限一式（plc-comm-slmp-dotnet の実機計測値）。</summary>
public sealed record ProfileLimits(
    RequestLimit DirectWord,
    RequestLimit DirectBit,
    RequestLimit RandomRead,
    RequestLimit RandomWrite,
    RequestLimit RandomBitWrite,
    RequestLimit Monitor,
    RequestLimit RandomReadExt,
    RequestLimit RandomWriteExt,
    RequestLimit RandomBitWriteExt);

/// <summary>機能の可否。不可の機能はコマンド/サブコマンド誤り (C059) で応答する。</summary>
public sealed record ProfileFeatures(
    bool TypeName = true,
    bool Block = true,
    bool Monitor = true,
    bool IqrSubcommands = true,
    bool ExtendedDevice = true,
    bool ModuleAccess = true,
    bool LinkDirect = true,
    bool HgCpuBuffer = true,
    bool Labels = true,
    bool MemoryAccess = true,
    bool ExtendUnit = true);

/// <summary>
/// 仮想 PLC の機種プロファイル。デバイス点数（メモリマップ）、SD 点数レジスタ配置、機能可否、点数上限を定義する。
/// </summary>
public sealed class CpuProfile
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required CpuSeries Series { get; init; }

    public required string ModelName { get; init; }

    public required ushort ModelCode { get; init; }

    /// <summary>ファミリ名 → 点数。含まれないファミリはこの機種に存在しない。</summary>
    public required IReadOnlyDictionary<string, uint> Points { get; init; }

    public required IReadOnlyList<SdPointRule> SdPointRules { get; init; }

    public required ProfileFeatures Features { get; init; }

    public required ProfileLimits Limits { get; init; }

    /// <summary>書込み不可のデバイス名。</summary>
    public IReadOnlySet<string> ReadOnlyDevices { get; init; } = new HashSet<string>();

    /// <summary>R が ZR の先頭ブロックを共有するか（iQ-R / Q）。</summary>
    public bool FileRegisterShared { get; init; } = true;

    /// <summary>X/Y を 8 進表記するか（iQ-F）。</summary>
    public bool XyOctal { get; init; }

    /// <summary>SD210〜SD216 に時計データを反映するか。</summary>
    public bool ClockInSd { get; init; } = true;

    public bool Has(string family) => Points.ContainsKey(family);

    public uint PointsOf(string family) => Points.TryGetValue(family, out var points) ? points : 0;

    public DeviceRadix RadixOf(DeviceDefinition device)
        => XyOctal && device.Name is "X" or "Y" ? DeviceRadix.Octal : device.Radix;

    public CpuProfile With(
        string? modelName = null,
        ushort? modelCode = null,
        IReadOnlyDictionary<string, uint>? pointOverrides = null)
    {
        var points = new Dictionary<string, uint>(Points, StringComparer.OrdinalIgnoreCase);
        if (pointOverrides is not null)
        {
            foreach (var (family, value) in pointOverrides)
                points[family] = value;
            // 同じメモリを使うデバイスは点数を揃える（DX/DY = X/Y、R = ZR 先頭 最大 32768 点）。
            if (FileRegisterShared && points.ContainsKey("ZR") && points.ContainsKey("R"))
                points["R"] = Math.Min(points["ZR"], 32768u);
            foreach (var (direct, normal) in new[] { ("DX", "X"), ("DY", "Y") })
            {
                if (points.ContainsKey(direct) && points.ContainsKey(normal))
                    points[direct] = points[normal];
            }
        }

        return new CpuProfile
        {
            Id = Id,
            DisplayName = DisplayName,
            Series = Series,
            ModelName = modelName ?? ModelName,
            ModelCode = modelCode ?? ModelCode,
            Points = points,
            SdPointRules = SdPointRules,
            Features = Features,
            Limits = Limits,
            ReadOnlyDevices = ReadOnlyDevices,
            FileRegisterShared = FileRegisterShared,
            XyOctal = XyOctal,
            ClockInSd = ClockInSd,
        };
    }
}
