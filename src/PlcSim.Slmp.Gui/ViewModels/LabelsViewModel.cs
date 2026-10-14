using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Gui.ViewModels;

/// <summary>ラベル（グローバルラベル・構造体メンバ）の 1 行。</summary>
public sealed partial class LabelRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _type = "WORD";

    /// <summary>配列の要素数（空欄・0 は配列なし）。</summary>
    [ObservableProperty]
    private string _lengthText = string.Empty;

    public int? Length => string.IsNullOrWhiteSpace(LengthText)
        ? 0
        : int.TryParse(LengthText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>定義に変換する。基本型は大文字にそろえ、要素数が数値でなければ -1（検証でエラーにする）。</summary>
    public LabelMemberDefinition ToDefinition()
    {
        var type = Type.Trim();
        return new(Name.Trim(), LabelDataType.Parse(type) is null ? type : type.ToUpperInvariant(), Length ?? -1);
    }

    public static LabelRow From(LabelMemberDefinition def) => new()
    {
        Name = def.Name,
        Type = def.Type,
        LengthText = def.Length > 0 ? def.Length.ToString(CultureInfo.InvariantCulture) : string.Empty,
    };
}

/// <summary>構造体の型定義（名前とメンバ）。</summary>
public sealed partial class StructRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    public ObservableCollection<LabelRow> Members { get; } = new();
}

/// <summary>
/// ラベル定義の編集画面（GX Works の構造体定義と外部公開グローバルラベルに相当）。
/// 適用すると定義を差し替えて仮想 PLC を作り直す。名前と型が変わらないラベルの値は引き継ぐ。
/// </summary>
public sealed partial class LabelsViewModel : ObservableObject
{
    private static readonly string[] BasicTypes = ["BIT", "WORD", "DWORD", "INT", "DINT", "REAL", "LREAL", "STRING(32)", "WSTRING(32)"];

    private readonly SimulatorRuntime _runtime;
    private string _loadedState = string.Empty;

    private string EditState => JsonSerializer.Serialize(new { Globals, Structs });

    public bool HasPendingChanges => EditState != _loadedState;

    public LabelsViewModel(SimulatorRuntime runtime)
    {
        _runtime = runtime;
        Load();
    }

    public ObservableCollection<LabelRow> Globals { get; } = new();

    public ObservableCollection<StructRow> Structs { get; } = new();

    /// <summary>データ型の候補（基本型と構造体名）。</summary>
    public ObservableCollection<string> TypeChoices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveStructCommand), nameof(AddMemberCommand))]
    [NotifyPropertyChangedFor(nameof(IsStructSelected))]
    private StructRow? _selectedStruct;

    public bool IsStructSelected => SelectedStruct is not null;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _busy;

    /// <summary>機種がラベルのコマンドに対応しないときの注意書き（対応していれば空）。</summary>
    [ObservableProperty]
    private string _profileNote = string.Empty;

    public void Load(bool preserveEdits = false)
    {
        ProfileNote = _runtime.Plc.Profile.Features.Labels
            ? string.Empty
            : $"現在の機種（{_runtime.Plc.Profile.DisplayName}）はラベルのコマンドに対応していないため、SLMP からは読み書きできません（HTTP API・スクリプト・メモリビューアからは使えます）。";
        if (preserveEdits && HasPendingChanges)
            return;
        var defs = _runtime.Plc.Labels.Definitions;
        Structs.Clear();
        foreach (var (name, members) in defs.Structs)
            Structs.Add(CreateStruct(name, members.Select(LabelRow.From)));
        Globals.Clear();
        foreach (var global in defs.Globals)
            Globals.Add(LabelRow.From(global));
        SelectedStruct = Structs.FirstOrDefault();
        UpdateTypeChoices();
        _loadedState = EditState;
    }

    [RelayCommand]
    private void AddGlobal()
    {
        Globals.Add(new LabelRow { Name = NextName("Label", Globals.Select(static g => g.Name)), Type = "WORD" });
    }

    [RelayCommand]
    private void RemoveGlobal(LabelRow? row)
    {
        if (row is not null)
            Globals.Remove(row);
    }

    [RelayCommand]
    private void AddStruct()
    {
        var row = CreateStruct(NextName("STRUCT", Structs.Select(static s => s.Name)), [new LabelRow { Name = "Value", Type = "WORD" }]);
        Structs.Add(row);
        SelectedStruct = row;
        UpdateTypeChoices();
    }

    private bool HasSelectedStruct() => SelectedStruct is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedStruct))]
    private void RemoveStruct()
    {
        if (SelectedStruct is null)
            return;
        var index = Structs.IndexOf(SelectedStruct);
        Structs.Remove(SelectedStruct);
        SelectedStruct = Structs.Count == 0 ? null : Structs[Math.Min(index, Structs.Count - 1)];
        UpdateTypeChoices();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedStruct))]
    private void AddMember()
    {
        SelectedStruct?.Members.Add(new LabelRow { Name = NextName("Member", SelectedStruct.Members.Select(static m => m.Name)), Type = "WORD" });
    }

    [RelayCommand]
    private void RemoveMember(LabelRow? row)
    {
        if (row is not null)
            SelectedStruct?.Members.Remove(row);
    }

    /// <summary>既定の SLMP_TEST の定義に戻す。</summary>
    [RelayCommand]
    private void ResetDefaults()
    {
        var defs = LabelDefinitions.SlmpTestDefault();
        Structs.Clear();
        foreach (var (name, members) in defs.Structs)
            Structs.Add(CreateStruct(name, members.Select(LabelRow.From)));
        Globals.Clear();
        foreach (var global in defs.Globals)
            Globals.Add(LabelRow.From(global));
        SelectedStruct = Structs.FirstOrDefault();
        UpdateTypeChoices();
        Message = "既定（SLMP_TEST）の定義にしました。反映するには「適用」を押してください。";
    }

    [RelayCommand]
    private async Task Apply()
    {
        var defs = new LabelDefinitions();
        foreach (var s in Structs)
        {
            if (defs.Structs.ContainsKey(s.Name.Trim()))
            {
                Message = $"適用できません: 構造体名 '{s.Name.Trim()}' が重複しています。";
                return;
            }

            defs.Structs[s.Name.Trim()] = s.Members.Select(static m => m.ToDefinition()).ToList();
        }

        defs.Globals = Globals.Select(static g => g.ToDefinition()).ToList();
        if (defs.Validate() is { Count: > 0 } errors)
        {
            Message = "適用できません: " + string.Join(" ", errors);
            return;
        }

        Busy = true;
        try
        {
            var config = ConfigEditing.Clone(_runtime.Config);
            config.Plc.Labels = defs;
            config.ErrorInjection = _runtime.Engine.Injector.Rules.ToList();
            await _runtime.RebuildAsync(config, keepMemory: true);
            Load();
            Message = $"適用しました（{DateTime.Now:HH:mm:ss}）。名前と型が変わらないラベルの値は引き継いでいます。設定ファイルに残すには「設定ファイルに保存」を押してください。";
        }
        catch (Exception ex)
        {
            Message = $"適用できません: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _runtime.SaveConfig();
            Message = $"設定ファイルに保存しました: {_runtime.ConfigPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存できません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private StructRow CreateStruct(string name, IEnumerable<LabelRow> members)
    {
        var row = new StructRow { Name = name };
        foreach (var member in members)
            row.Members.Add(member);
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StructRow.Name))
                UpdateTypeChoices();
        };
        return row;
    }

    private void UpdateTypeChoices()
    {
        var choices = BasicTypes.Concat(Structs.Select(static s => s.Name.Trim()).Where(static n => n.Length > 0)).ToList();
        if (choices.SequenceEqual(TypeChoices))
            return;
        TypeChoices.Clear();
        foreach (var choice in choices)
            TypeChoices.Add(choice);
    }

    private static string NextName(string prefix, IEnumerable<string> existing)
    {
        var used = existing.ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++)
        {
            var name = $"{prefix}{i}";
            if (!used.Contains(name))
                return name;
        }
    }
}
