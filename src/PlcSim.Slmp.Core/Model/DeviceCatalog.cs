namespace PlcSim.Slmp.Core.Model;

/// <summary>デバイスの記憶形態。</summary>
public enum DeviceKind
{
    /// <summary>ビットデバイス（X, M, TS など）。</summary>
    Bit,

    /// <summary>16 ビットワードデバイス（D, TN など）。</summary>
    Word,

    /// <summary>32 ビット値を 1 点 2 ワードで持つデバイス（LCN, LZ）。</summary>
    DoubleWord,

    /// <summary>ロングタイマ現在値（LTN, LSTN）。1 点 4 ワード [現在値L, 現在値H, 状態, システム]。</summary>
    LongTimer,

    /// <summary>ロングタイマの接点/コイル（LTS/LTC, LSTS/LSTC）。対応する LTN の状態ワードのビット。</summary>
    LongTimerBit,
}

/// <summary>デバイス番号の表記基数。</summary>
public enum DeviceRadix
{
    Decimal = 10,
    Octal = 8,
    Hex = 16,
}

/// <summary>SLMP デバイスコード 1 種の定義。</summary>
/// <param name="Name">デバイス名（クライアントの表記に合わせる: STS/STC/STN など）。</param>
/// <param name="Code">バイナリのデバイスコード（iQ-R 形式は 2 バイト、Q 形式は下位 1 バイト）。</param>
/// <param name="Family">点数を共有するファミリ名（TS/TC/TN → T）。</param>
/// <param name="AsciiQ">ASCII Q/L 形式の 2 文字コード。</param>
/// <param name="AsciiR">ASCII iQ-R 形式の 4 文字コード。</param>
public sealed record DeviceDefinition(
    string Name,
    ushort Code,
    DeviceKind Kind,
    DeviceRadix Radix,
    string Family,
    string AsciiQ,
    string AsciiR,
    string Description)
{
    public bool IsBitUnit => Kind is DeviceKind.Bit or DeviceKind.LongTimerBit;
}

/// <summary>全デバイスコードの定義表（SLMP リファレンスマニュアルのデバイスコード表に準拠）。</summary>
public static class DeviceCatalog
{
    public static readonly IReadOnlyList<DeviceDefinition> All = new DeviceDefinition[]
    {
        new("SM", 0x0091, DeviceKind.Bit, DeviceRadix.Decimal, "SM", "SM", "SM**", "特殊リレー"),
        new("SD", 0x00A9, DeviceKind.Word, DeviceRadix.Decimal, "SD", "SD", "SD**", "特殊レジスタ"),
        new("X", 0x009C, DeviceKind.Bit, DeviceRadix.Hex, "X", "X*", "X***", "入力"),
        new("Y", 0x009D, DeviceKind.Bit, DeviceRadix.Hex, "Y", "Y*", "Y***", "出力"),
        new("M", 0x0090, DeviceKind.Bit, DeviceRadix.Decimal, "M", "M*", "M***", "内部リレー"),
        new("L", 0x0092, DeviceKind.Bit, DeviceRadix.Decimal, "L", "L*", "L***", "ラッチリレー"),
        new("F", 0x0093, DeviceKind.Bit, DeviceRadix.Decimal, "F", "F*", "F***", "アナンシェータ"),
        new("V", 0x0094, DeviceKind.Bit, DeviceRadix.Decimal, "V", "V*", "V***", "エッジリレー"),
        new("B", 0x00A0, DeviceKind.Bit, DeviceRadix.Hex, "B", "B*", "B***", "リンクリレー"),
        new("S", 0x0098, DeviceKind.Bit, DeviceRadix.Decimal, "S", "S*", "S***", "ステップリレー"),
        new("D", 0x00A8, DeviceKind.Word, DeviceRadix.Decimal, "D", "D*", "D***", "データレジスタ"),
        new("W", 0x00B4, DeviceKind.Word, DeviceRadix.Hex, "W", "W*", "W***", "リンクレジスタ"),
        new("TS", 0x00C1, DeviceKind.Bit, DeviceRadix.Decimal, "T", "TS", "TS**", "タイマ接点"),
        new("TC", 0x00C0, DeviceKind.Bit, DeviceRadix.Decimal, "T", "TC", "TC**", "タイマコイル"),
        new("TN", 0x00C2, DeviceKind.Word, DeviceRadix.Decimal, "T", "TN", "TN**", "タイマ現在値"),
        new("LTS", 0x0051, DeviceKind.LongTimerBit, DeviceRadix.Decimal, "LT", "", "LTS*", "ロングタイマ接点"),
        new("LTC", 0x0050, DeviceKind.LongTimerBit, DeviceRadix.Decimal, "LT", "", "LTC*", "ロングタイマコイル"),
        new("LTN", 0x0052, DeviceKind.LongTimer, DeviceRadix.Decimal, "LT", "", "LTN*", "ロングタイマ現在値"),
        new("STS", 0x00C7, DeviceKind.Bit, DeviceRadix.Decimal, "ST", "SS", "STS*", "積算タイマ接点"),
        new("STC", 0x00C6, DeviceKind.Bit, DeviceRadix.Decimal, "ST", "SC", "STC*", "積算タイマコイル"),
        new("STN", 0x00C8, DeviceKind.Word, DeviceRadix.Decimal, "ST", "SN", "STN*", "積算タイマ現在値"),
        new("LSTS", 0x0059, DeviceKind.LongTimerBit, DeviceRadix.Decimal, "LST", "", "LSTS", "ロング積算タイマ接点"),
        new("LSTC", 0x0058, DeviceKind.LongTimerBit, DeviceRadix.Decimal, "LST", "", "LSTC", "ロング積算タイマコイル"),
        new("LSTN", 0x005A, DeviceKind.LongTimer, DeviceRadix.Decimal, "LST", "", "LSTN", "ロング積算タイマ現在値"),
        new("CS", 0x00C4, DeviceKind.Bit, DeviceRadix.Decimal, "C", "CS", "CS**", "カウンタ接点"),
        new("CC", 0x00C3, DeviceKind.Bit, DeviceRadix.Decimal, "C", "CC", "CC**", "カウンタコイル"),
        new("CN", 0x00C5, DeviceKind.Word, DeviceRadix.Decimal, "C", "CN", "CN**", "カウンタ現在値"),
        new("LCS", 0x0055, DeviceKind.Bit, DeviceRadix.Decimal, "LC", "", "LCS*", "ロングカウンタ接点"),
        new("LCC", 0x0054, DeviceKind.Bit, DeviceRadix.Decimal, "LC", "", "LCC*", "ロングカウンタコイル"),
        new("LCN", 0x0056, DeviceKind.DoubleWord, DeviceRadix.Decimal, "LC", "", "LCN*", "ロングカウンタ現在値"),
        new("SB", 0x00A1, DeviceKind.Bit, DeviceRadix.Hex, "SB", "SB", "SB**", "リンク特殊リレー"),
        new("SW", 0x00B5, DeviceKind.Word, DeviceRadix.Hex, "SW", "SW", "SW**", "リンク特殊レジスタ"),
        new("DX", 0x00A2, DeviceKind.Bit, DeviceRadix.Hex, "DX", "DX", "DX**", "ダイレクトアクセス入力"),
        new("DY", 0x00A3, DeviceKind.Bit, DeviceRadix.Hex, "DY", "DY", "DY**", "ダイレクトアクセス出力"),
        new("Z", 0x00CC, DeviceKind.Word, DeviceRadix.Decimal, "Z", "Z*", "Z***", "インデックスレジスタ"),
        new("LZ", 0x0062, DeviceKind.DoubleWord, DeviceRadix.Decimal, "LZ", "", "LZ**", "ロングインデックスレジスタ"),
        new("R", 0x00AF, DeviceKind.Word, DeviceRadix.Decimal, "R", "R*", "R***", "ファイルレジスタ(ブロック切換)"),
        new("ZR", 0x00B0, DeviceKind.Word, DeviceRadix.Decimal, "ZR", "ZR", "ZR**", "ファイルレジスタ(連番)"),
        new("RD", 0x002C, DeviceKind.Word, DeviceRadix.Decimal, "RD", "", "RD**", "リフレッシュデータレジスタ"),
        new("G", 0x00AB, DeviceKind.Word, DeviceRadix.Decimal, "G", "G*", "G***", "バッファメモリ"),
        new("HG", 0x002E, DeviceKind.Word, DeviceRadix.Decimal, "HG", "", "HG**", "CPUバッファメモリ定周期"),
    };

    private static readonly Dictionary<string, DeviceDefinition> ByName =
        All.ToDictionary(static d => d.Name, StringComparer.OrdinalIgnoreCase);

    public static DeviceDefinition? FindByName(string name)
        => ByName.TryGetValue(name, out var def) ? def : Aliases(name);

    public static DeviceDefinition? FindByCode(ushort code, bool iqrFormat)
    {
        foreach (var def in All)
        {
            if (iqrFormat ? def.Code == code : (def.Code & 0xFF) == code && def.Code <= 0xFF)
                return def;
        }

        return null;
    }

    public static DeviceDefinition? FindByAscii(string token, bool iqrFormat)
    {
        var normalized = token.Trim().ToUpperInvariant();
        foreach (var def in All)
        {
            var code = iqrFormat ? def.AsciiR : def.AsciiQ;
            if (code.Length > 0 && string.Equals(code, normalized, StringComparison.Ordinal))
                return def;
        }

        return null;
    }

    private static DeviceDefinition? Aliases(string name) => name.ToUpperInvariant() switch
    {
        "SS" => ByName["STS"],
        "SC" => ByName["STC"],
        "SN" => ByName["STN"],
        _ => null,
    };
}
