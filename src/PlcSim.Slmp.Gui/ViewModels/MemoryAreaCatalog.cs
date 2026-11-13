using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Gui.ViewModels;

public static class MemoryAreaCatalog
{
    public const string CpuAreaKey = "U3E\\G";
    public const string CpuHgAreaKey = "U3E\\HG";
    public const string UnitAreaKey = "U\\G";
    public const string NetworkAreaPrefix = "J\\";

    private static readonly (string Group, string[] Families)[] DeviceGroups =
    {
        ("入出力", new[] { "X", "Y" }),
        ("内部リレー", new[] { "M", "L", "F", "V", "B", "SB", "S" }),
        ("ワード", new[] { "D", "W", "SW" }),
        ("タイマ・カウンタ", new[] { "T", "ST", "C", "LT", "LST", "LC" }),
        ("インデックス", new[] { "Z", "LZ" }),
        ("ファイルレジスタ", new[] { "R", "ZR", "RD" }),
        ("特殊", new[] { "SM", "SD" }),
    };

    private static readonly Dictionary<string, (AreaKind Kind, string[] Parts, string Description)> Composite = new()
    {
        ["T"] = (AreaKind.Timer, new[] { "TS", "TC", "TN" }, "タイマ（接点 TS / コイル TC / 現在値 TN）"),
        ["ST"] = (AreaKind.Timer, new[] { "STS", "STC", "STN" }, "積算タイマ（STS / STC / STN）"),
        ["C"] = (AreaKind.Timer, new[] { "CS", "CC", "CN" }, "カウンタ（CS / CC / CN）"),
        ["LT"] = (AreaKind.LongTimer, new[] { "LTS", "LTC", "LTN" }, "ロングタイマ（LTN 4 ワード構造）"),
        ["LST"] = (AreaKind.LongTimer, new[] { "LSTS", "LSTC", "LSTN" }, "ロング積算タイマ（LSTN 4 ワード構造）"),
        ["LC"] = (AreaKind.LongCounter, new[] { "LCS", "LCC", "LCN" }, "ロングカウンタ（LCS / LCC / LCN）"),
    };

    private static MemoryArea StoreArea(string key, string qualifier, DeviceDefinition def, string title, string description)
    {
        var link = qualifier.StartsWith('J');
        return new MemoryArea
        {
            Group = link ? "リンクダイレクト" : "バッファメモリ",
            Key = key,
            Title = title,
            Description = description,
            Points = link
                ? (def.Kind == DeviceKind.Bit ? BufferMemory.LinkDirectWords * 16 : BufferMemory.LinkDirectWords)
                : BufferMemory.ModuleBufferWords,
            Kind = link && def.Kind == DeviceKind.Bit ? AreaKind.Bit : AreaKind.Word,
            Radix = link ? 16 : 10,
            Prefix = $"{qualifier}\\{def.Name}",
            Device = def,
            Qualifier = qualifier,
            AccessKeys = new[] { $"{qualifier}\\{def.Name}" },
        };
    }

    public static IEnumerable<MemoryArea> BuildAreas(PlcController controller, string selectedUnit, string selectedCpu, string selectedNetwork)
    {
        var plc = controller.Plc;
        var profile = plc.Profile;
        foreach (var (group, families) in DeviceGroups)
        {
            foreach (var family in families)
            {
                if (!profile.Has(family))
                    continue;
                var points = profile.PointsOf(family);
                if (Composite.TryGetValue(family, out var composite))
                {
                    yield return new MemoryArea
                    {
                        Group = group,
                        Key = family,
                        Title = family,
                        Description = composite.Description,
                        Points = points,
                        Kind = composite.Kind,
                        Prefix = composite.Parts[2],
                        Parts = composite.Parts,
                        Device = Def(composite.Parts[2]),
                        AccessKeys = composite.Parts,
                    };
                    continue;
                }

                if (family == "R" && profile.FileRegisterShared && profile.Has("ZR"))
                    continue;
                var def = Def(family);
                var description = def.Description;
                if (family == "ZR" && profile.FileRegisterShared && profile.Has("R"))
                    description += $"（R と同じメモリ。R は先頭 {profile.PointsOf("R"):N0} 点）";
                if (family is "X" or "Y" && profile.Has("D" + family))
                    description += $"（D{family} と同じメモリ）";
                var readOnly = profile.ReadOnlyDevices.Contains(family);
                yield return new MemoryArea
                {
                    Group = group,
                    Key = family,
                    Title = family,
                    Description = readOnly ? description + "（読出し専用）" : description,
                    Points = points,
                    Kind = def.Kind switch
                    {
                        DeviceKind.Bit => AreaKind.Bit,
                        DeviceKind.DoubleWord => AreaKind.DoubleWord,
                        _ => AreaKind.Word,
                    },
                    Radix = (int)profile.RadixOf(def),
                    ReadOnly = readOnly,
                    Prefix = family,
                    Device = def,
                    AccessKeys = family switch
                    {
                        "X" or "Y" => new[] { family, "D" + family },
                        "ZR" when profile.FileRegisterShared => new[] { "ZR", "R" },
                        _ => new[] { family },
                    },
                };
            }
        }

        // ユニットバッファ: 登録済みユニットを番号切替で表示（最大 4 個）
        if ((profile.Features.ModuleAccess || profile.Features.ExtendUnit) && plc.Settings.Units.Count > 0)
            yield return StoreArea(UnitAreaKey, selectedUnit, Def("G"), "Un\\G", $"ユニット {selectedUnit} のバッファメモリ（登録 {plc.Settings.Units.Count}/{PlcSettings.MaxUnits}）");

        // CPU バッファメモリ（iQ-R の CPU 内蔵、登録不要）
        if (profile.Series == CpuSeries.IqR && profile.Features.ModuleAccess)
            yield return StoreArea(CpuAreaKey, selectedCpu, Def("G"), "U3En\\G", $"CPU バッファメモリ（{selectedCpu}、U3E0〜U3E3）");
        if (profile.Features.HgCpuBuffer)
            yield return StoreArea(CpuHgAreaKey, selectedCpu, Def("HG"), "U3En\\HG", $"CPU バッファメモリ定周期エリア（{selectedCpu}、U3E0〜U3E3）");

        // リンクダイレクト: 登録済みネットワークを番号切替で表示（最大 4 個）
        if (profile.Features.LinkDirect && plc.Settings.Networks.Count > 0)
        {
            foreach (var device in new[] { "X", "Y", "B", "SB", "W", "SW" })
            {
                var def = Def(device);
                yield return StoreArea(NetworkAreaPrefix + device, selectedNetwork, def, $"Jn\\{device}",
                    $"ネットワーク {selectedNetwork} の{def.Description}（登録 {plc.Settings.Networks.Count}/{PlcSettings.MaxNetworks}）");
            }
        }

        if (profile.Features.MemoryAccess)
        {
            yield return new MemoryArea
            {
                Group = "その他",
                Key = "自局",
                Title = "自局",
                Description = "自局メモリ（0613/1613）",
                Points = BufferMemory.OwnMemoryWords,
                Kind = AreaKind.Word,
                Radix = 16,
                Prefix = "自局 ",
                AccessKeys = new[] { "自局" },
            };
        }

        var labelCount = plc.Labels.Snapshot().Count;
        if (labelCount > 0)
        {
            yield return new MemoryArea
            {
                Group = "その他",
                Key = "ラベル",
                Title = "ラベル",
                Description = string.Join(", ", plc.Labels.Definitions.Globals.Select(static g => g.Name)),
                Points = (uint)labelCount,
                Kind = AreaKind.Label,
                Prefix = string.Empty,
                AccessKeys = new[] { "ラベル" },
            };
        }
    }

    private static DeviceDefinition Def(string name) => DeviceCatalog.FindByName(name)!;

    public static (string Key, uint Number) JumpTarget(PlcController controller, string text)
    {
            var address = controller.Parse(text);
            var key = address.Qualifier is not null
                ? $"{address.Qualifier}\\{address.Device.Name}"
                : Composite.ContainsKey(address.Device.Family) ? address.Device.Family
                : address.Device.Name is "DX" or "DY" ? address.Device.Name[1..]
                : address.Device.Name == "R" && controller.Plc.Profile.FileRegisterShared && controller.Plc.Profile.Has("ZR") ? "ZR"
                : address.Device.Name;
        return (key, address.Number);
    }
}
