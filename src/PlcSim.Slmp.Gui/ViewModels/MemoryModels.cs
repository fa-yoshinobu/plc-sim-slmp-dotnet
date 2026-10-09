using CommunityToolkit.Mvvm.ComponentModel;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;

namespace PlcSim.Slmp.Gui.ViewModels;

/// <summary>メモリビューアの領域の種類。</summary>
public enum AreaKind
{
    Bit,
    Word,

    /// <summary>接点・コイル・16 ビット現在値（T/ST/C）。</summary>
    Timer,

    /// <summary>接点・コイル・32 ビット現在値と状態ワード（LT/LST: LTN 4 ワード構造）。</summary>
    LongTimer,

    /// <summary>接点・コイル・32 ビット現在値（LC）。</summary>
    LongCounter,

    /// <summary>32 ビット値（LZ）。</summary>
    DoubleWord,

    Label,
}

/// <summary>メモリビューアの 1 領域（デバイスファミリ・バッファメモリ・リンクダイレクト等）。</summary>
public sealed class MemoryArea
{
    public required string Group { get; init; }

    /// <summary>一意キー（"D", "T", "LT", "U0\G", "J1\W", "自局", "ラベル"）。</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required uint Points { get; init; }

    public required AreaKind Kind { get; init; }

    /// <summary>番号の基数（16/10/8）。</summary>
    public int Radix { get; init; } = 10;

    public bool ReadOnly { get; init; }

    /// <summary>番号表示の接頭辞（D、TN、U0\G など）。</summary>
    public required string Prefix { get; init; }

    /// <summary>通常デバイスの定義（タイマ系は値デバイス）。</summary>
    public DeviceDefinition? Device { get; init; }

    /// <summary>タイマ系の接点・コイル・現在値デバイス名。</summary>
    public string[] Parts { get; init; } = Array.Empty<string>();

    /// <summary>アクセス記録の領域キー（タイマなら TS/TC/TN）。</summary>
    public required string[] AccessKeys { get; init; }

    /// <summary>バッファ系の修飾子（"U0", "J1"）。</summary>
    public string? Qualifier { get; init; }

    public bool IsStore => Qualifier is not null || Key == "自局";

    public string PointsText => Points.ToString("N0");

    public string FormatNumber(uint n) => Radix switch
    {
        16 => n.ToString("X"),
        8 => Convert.ToString(n, 8),
        _ => n.ToString(),
    };

    public string RangeText => Kind == AreaKind.Label
        ? $"{Points} 項目"
        : $"{Prefix}{FormatNumber(0)}〜{Prefix}{FormatNumber(Points - 1)}（{Points:N0} 点・{(Radix == 16 ? "16進" : Radix == 8 ? "8進" : "10進")}{(ReadOnly ? "・読出し専用" : string.Empty)}）";
}

/// <summary>グリッドの 1 セル。</summary>
public sealed partial class MemoryCell : ObservableObject
{
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private bool _isRecent;

    [ObservableProperty]
    private bool _isBit;

    /// <summary>セルが指すデバイス番号。</summary>
    public uint Number { get; set; }

    /// <summary>タイマ系の列の役割（"contact" / "coil" / "value" / "status"）、ラベルは "label"。</summary>
    public string Role { get; set; } = "value";

    /// <summary>ラベル行のパス。</summary>
    public string? LabelPath { get; set; }

    public bool Editable { get; set; } = true;
}

/// <summary>グリッドの 1 行。</summary>
public sealed partial class MemoryRow : ObservableObject
{
    public MemoryRow(int columns)
    {
        Cells = Enumerable.Range(0, columns).Select(static _ => new MemoryCell()).ToArray();
    }

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private bool _hasValue;

    [ObservableProperty]
    private bool _isVisible = true;

    public MemoryCell[] Cells { get; }
}

/// <summary>直近アクセス一覧の 1 行。</summary>
public sealed record RecentItem(
    AccessKind Kind,
    string KindText,
    string Display,
    string Time,
    string Times,
    string Source,
    string Area,
    uint Start);
