namespace PlcSim.Slmp.Core.Engine;

/// <summary>エラー注入ルール。<see cref="Subcommand"/> が <c>null</c> なら全サブコマンドに一致する。</summary>
public sealed record ErrorInjectionRule
{
    public ushort Command { get; init; }

    public ushort? Subcommand { get; init; }

    /// <summary>返す終了コード。<c>null</c> なら通常処理（遅延・無応答のみ適用）。</summary>
    public ushort? EndCode { get; init; }

    /// <summary>応答前の遅延（ミリ秒）。</summary>
    public int DelayMs { get; init; }

    /// <summary>応答を返さない。</summary>
    public bool Drop { get; init; }

    /// <summary>残り適用回数。<c>null</c> なら無制限。</summary>
    public int? RemainingCount { get; set; }

    public bool Matches(ushort command, ushort subcommand)
        => Command == command && (Subcommand is null || Subcommand == subcommand);
}

/// <summary>モック固有のエラー注入（任意終了コード・遅延・無応答）。</summary>
public sealed class ErrorInjector
{
    private readonly object _gate = new();
    private readonly List<ErrorInjectionRule> _rules = new();

    public IReadOnlyList<ErrorInjectionRule> Rules
    {
        get
        {
            lock (_gate)
                return _rules.ToArray();
        }
    }

    public void Add(ErrorInjectionRule rule)
    {
        lock (_gate)
            _rules.Add(rule);
    }

    public void Clear()
    {
        lock (_gate)
            _rules.Clear();
    }

    public void Remove(ErrorInjectionRule rule)
    {
        lock (_gate)
            _rules.Remove(rule);
    }

    /// <summary>一致するルールを取り出す（回数指定があれば 1 減らす）。</summary>
    public ErrorInjectionRule? Take(ushort command, ushort subcommand)
    {
        lock (_gate)
        {
            var rule = _rules.FirstOrDefault(r => r.Matches(command, subcommand));
            if (rule?.RemainingCount is { } remaining)
            {
                rule.RemainingCount = remaining - 1;
                if (remaining - 1 <= 0)
                    _rules.Remove(rule);
            }

            return rule;
        }
    }
}
