using System.Globalization;
using System.Text.RegularExpressions;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Core.Model;

/// <summary>
/// ラベルの基本データ型。<see cref="TypeId"/> は SLMP のデータ型 ID。
/// WORD=2 は実機 iQ-R の検査（SLMP_TEST.WordValue）で確認済み。その他は SLMP マニュアル準拠の値（実機未照合）。
/// </summary>
public sealed record LabelDataType(string Name, byte TypeId, int Size)
{
    public static readonly LabelDataType Bit = new("BIT", 0x01, 2);
    public static readonly LabelDataType Word = new("WORD", 0x02, 2);
    public static readonly LabelDataType DWord = new("DWORD", 0x03, 4);
    public static readonly LabelDataType Real = new("REAL", 0x04, 4);
    public static readonly LabelDataType LReal = new("LREAL", 0x05, 8);
    public static readonly LabelDataType Int = new("INT", 0x08, 2);
    public static readonly LabelDataType DInt = new("DINT", 0x09, 4);

    public static LabelDataType? Parse(string name)
    {
        var upper = name.Trim().ToUpperInvariant();
        var m = Regex.Match(upper, @"^(STRING|WSTRING)\((\d+)\)$");
        if (m.Success)
        {
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var chars) || chars > LabelDefinitions.MaxLabelBytes)
                return null;
            return m.Groups[1].Value == "STRING"
                ? new LabelDataType(upper, 0x06, ((chars + 2) / 2) * 2)
                : new LabelDataType(upper, 0x07, (chars + 1) * 2);
        }

        return upper switch
        {
            "BIT" or "BOOL" => Bit,
            "WORD" => Word,
            "DWORD" => DWord,
            "REAL" or "FLOAT" => Real,
            "LREAL" or "DOUBLE" => LReal,
            "INT" => Int,
            "DINT" => DInt,
            _ => null,
        };
    }
}

/// <summary>ラベル定義（グローバルラベルまたは構造体メンバ）。<see cref="Length"/> &gt; 0 なら配列。</summary>
public sealed record LabelMemberDefinition(string Name, string Type, int Length = 0);

/// <summary>ラベル定義一式（GX Works の構造体型と外部公開グローバルラベルに相当）。</summary>
public sealed class LabelDefinitions
{
    public Dictionary<string, List<LabelMemberDefinition>> Structs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<LabelMemberDefinition> Globals { get; set; } = new();

    /// <summary>配列の最大要素数。</summary>
    public const int MaxArrayLength = 65535;

    /// <summary>1 つのグローバルラベルの最大バイト数。</summary>
    public const int MaxLabelBytes = 1024 * 1024;

    private static readonly Regex NamePattern = new(@"^[\p{L}_][\p{L}\p{Nd}_]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 定義の誤りを列挙する（空なら正しい）。名前の形式と重複、未定義の型、構造体の循環参照、配列の要素数、サイズを確認する。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var structNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, members) in Structs)
        {
            if (!NamePattern.IsMatch(name))
                errors.Add($"構造体名 '{name}' に使えない文字があります（先頭は文字か _、以降は文字・数字・_）。");
            if (LabelDataType.Parse(name) is not null)
                errors.Add($"構造体名 '{name}' は基本データ型と同じ名前です。");
            if (!structNames.Add(name))
                errors.Add($"構造体名 '{name}' が重複しています。");
            if (members.Count == 0)
                errors.Add($"構造体 '{name}' にメンバがありません。");
            CheckMembers($"構造体 '{name}' のメンバ", members, errors);
        }

        CheckMembers("グローバルラベル", Globals, errors);
        if (errors.Count > 0)
            return errors;

        // 構造体の循環参照（A が B を含み、B が A を含む等）。
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Structs.Keys)
        {
            if (HasCycle(name, state))
            {
                errors.Add($"構造体 '{name}' が自分自身を含んでいます（循環参照）。");
                return errors;
            }
        }

        foreach (var global in Globals)
        {
            if (SizeOf(global) > MaxLabelBytes)
                errors.Add($"グローバルラベル '{global.Name}' が大きすぎます（最大 {MaxLabelBytes / 1024} KB）。");
        }

        return errors;
    }

    private void CheckMembers(string owner, List<LabelMemberDefinition> members, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (!NamePattern.IsMatch(member.Name ?? string.Empty))
                errors.Add($"{owner} '{member.Name}' の名前に使えない文字があります（先頭は文字か _、以降は文字・数字・_）。");
            else if (!names.Add(member.Name!))
                errors.Add($"{owner} '{member.Name}' が重複しています。");
            if (LabelDataType.Parse(member.Type ?? string.Empty) is null && !Structs.ContainsKey(member.Type ?? string.Empty))
                errors.Add($"{owner} '{member.Name}' のデータ型 '{member.Type}' が未定義です。");
            if (member.Length is < 0 or > MaxArrayLength)
                errors.Add($"{owner} '{member.Name}' の配列要素数は 0〜{MaxArrayLength} にしてください（0 は配列なし）。");
        }
    }

    private bool HasCycle(string name, Dictionary<string, int> state)
    {
        // 0: 未訪問 / 1: 探索中 / 2: 完了
        if (state.TryGetValue(name, out var s))
            return s == 1;
        state[name] = 1;
        foreach (var member in Structs[name])
        {
            if (Structs.ContainsKey(member.Type) && HasCycle(member.Type, state))
                return true;
        }

        state[name] = 2;
        return false;
    }

    private long SizeOf(LabelMemberDefinition def)
    {
        long element = LabelDataType.Parse(def.Type) is { } type ? type.Size : Structs[def.Type].Sum(SizeOf);
        return element * Math.Max(def.Length, 1);
    }

    /// <summary>実機 iQ-R の検査で使った GX Works3 登録定義（SLMP_TEST）。</summary>
    public static LabelDefinitions SlmpTestDefault() => new()
    {
        Structs =
        {
            ["SLMP_TEST_TYPE"] = new()
            {
                new("WordValue", "WORD"),
                new("WordArray", "WORD", 2),
            },
        },
        Globals = { new("SLMP_TEST", "SLMP_TEST_TYPE") },
    };
}

/// <summary>ラベル指定を解決した結果。</summary>
/// <param name="Path">正規化したラベル名。</param>
/// <param name="Buffer">所属するグローバルラベルの記憶域。</param>
/// <param name="Offset">要素の先頭バイト位置。</param>
/// <param name="Type">要素の型。構造体なら <c>null</c>。</param>
/// <param name="Size">要素（構造体ならその全体）のバイト数。</param>
/// <param name="AvailableBytes">配列の残り要素を含めて読み書きできるバイト数。</param>
public sealed record LabelSlot(string Path, byte[] Buffer, int Offset, LabelDataType? Type, int Size, int AvailableBytes);

/// <summary>ラベル値の一覧表示用。</summary>
public sealed record LabelValueView(string Path, string Type, byte[] Data);

/// <summary>ラベルの記憶域と名前解決。</summary>
public sealed class LabelStore
{
    private readonly Dictionary<string, (LabelMemberDefinition Def, byte[] Buffer)> _globals = new(StringComparer.Ordinal);
    private readonly LabelDefinitions _definitions;

    public LabelStore(LabelDefinitions definitions)
    {
        if (definitions.Validate() is { Count: > 0 } errors)
            throw new InvalidOperationException("ラベル定義が正しくありません: " + string.Join(" ", errors));
        _definitions = definitions;
        foreach (var global in definitions.Globals)
        {
            var size = SizeOf(global);
            _globals[global.Name] = (global, new byte[size]);
        }
    }

    public LabelDefinitions Definitions => _definitions;

    public event Action<string, string>? Changed;

    /// <summary>"SLMP_TEST.WordArray[0]" のようなラベル指定を解決する。</summary>
    public LabelSlot Resolve(string label)
    {
        var parts = Tokenize(label);
        if (parts.Count == 0 || !_globals.TryGetValue(parts[0].Name, out var root))
            throw NotFound(label);

        var buffer = root.Buffer;
        var def = root.Def;
        var offset = 0;
        var index = parts[0].Index;
        for (var p = 0; ; p++)
        {
            var elementSize = ElementSize(def);
            var count = Math.Max(def.Length, 1);
            int available;
            if (index is { } i)
            {
                if (def.Length == 0 || i < 0 || i >= def.Length)
                    throw NotFound(label);
                offset += i * elementSize;
                available = (count - i) * elementSize;
            }
            else
            {
                available = count * elementSize;
            }

            if (p == parts.Count - 1)
            {
                var type = LabelDataType.Parse(def.Type);
                var size = index is null && def.Length > 0 ? count * elementSize : elementSize;
                return new LabelSlot(string.Join('.', parts.Select(static x => x.Text)), buffer, offset, type, size, available);
            }

            if (def.Length > 0 && index is null)
                throw NotFound(label);
            if (!_definitions.Structs.TryGetValue(def.Type, out var members))
                throw NotFound(label);

            var next = parts[p + 1];
            var memberOffset = 0;
            LabelMemberDefinition? found = null;
            foreach (var member in members)
            {
                if (string.Equals(member.Name, next.Name, StringComparison.Ordinal))
                {
                    found = member;
                    break;
                }

                memberOffset += SizeOf(member);
            }

            if (found is null)
                throw NotFound(label);
            offset += memberOffset;
            def = found;
            index = next.Index;
        }
    }

    public byte[] Read(LabelSlot slot, int byteCount)
    {
        if (byteCount > slot.AvailableBytes)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, $"{slot.Path}: {byteCount} バイトは配列範囲を超えます");
        return slot.Buffer.AsSpan(slot.Offset, byteCount).ToArray();
    }

    public void Write(LabelSlot slot, ReadOnlySpan<byte> data, string source)
    {
        if (data.Length > slot.AvailableBytes)
            throw new SlmpEndCodeException(EndCodes.RequestContentError, $"{slot.Path}: {data.Length} バイトは配列範囲を超えます");
        data.CopyTo(slot.Buffer.AsSpan(slot.Offset));
        Changed?.Invoke(slot.Path, source);
    }

    /// <summary>全ラベルの基本型要素を列挙する（表示用）。</summary>
    public IReadOnlyList<LabelValueView> Snapshot()
    {
        var result = new List<LabelValueView>();
        foreach (var (name, (def, buffer)) in _globals)
            Flatten(name, def, buffer, 0, result);
        return result;
    }

    private void Flatten(string path, LabelMemberDefinition def, byte[] buffer, int offset, List<LabelValueView> result)
    {
        var elementSize = ElementSize(def);
        var count = Math.Max(def.Length, 1);
        for (var i = 0; i < count; i++)
        {
            var elementPath = def.Length > 0 ? $"{path}[{i}]" : path;
            var elementOffset = offset + (i * elementSize);
            if (_definitions.Structs.TryGetValue(def.Type, out var members))
            {
                var memberOffset = elementOffset;
                foreach (var member in members)
                {
                    Flatten($"{elementPath}.{member.Name}", member, buffer, memberOffset, result);
                    memberOffset += SizeOf(member);
                }
            }
            else
            {
                result.Add(new LabelValueView(elementPath, def.Type, buffer.AsSpan(elementOffset, elementSize).ToArray()));
            }
        }
    }

    private int SizeOf(LabelMemberDefinition def) => ElementSize(def) * Math.Max(def.Length, 1);

    private int ElementSize(LabelMemberDefinition def)
    {
        if (LabelDataType.Parse(def.Type) is { } type)
            return type.Size;
        if (_definitions.Structs.TryGetValue(def.Type, out var members))
            return members.Sum(SizeOf);
        throw new InvalidOperationException($"ラベル {def.Name} のデータ型 {def.Type} が未定義です");
    }

    private static List<(string Name, int? Index, string Text)> Tokenize(string label)
    {
        var result = new List<(string, int?, string)>();
        foreach (var segment in label.Split('.'))
        {
            var m = Regex.Match(segment, @"^([^\[\]]+)(?:\[(\d+)\])?$");
            if (!m.Success)
                return new List<(string, int?, string)>();
            int? index = null;
            if (m.Groups[2].Success)
            {
                // 範囲外の大きな添字は「ラベルなし」として扱う
                if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    return new List<(string, int?, string)>();
                index = parsed;
            }

            result.Add((m.Groups[1].Value, index, segment));
        }

        return result;
    }

    private static SlmpEndCodeException NotFound(string label)
        => new(EndCodes.LabelNotFound, $"ラベル '{label}' は存在しません");
}
