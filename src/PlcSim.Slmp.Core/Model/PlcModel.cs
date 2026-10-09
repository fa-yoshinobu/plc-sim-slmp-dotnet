namespace PlcSim.Slmp.Core.Model;

/// <summary>CPU 動作状態。</summary>
public enum PlcRunState
{
    Run,
    Stop,
    Pause,
}

/// <summary>仮想 PLC の動作設定。</summary>
public sealed class PlcSettings
{
    /// <summary>RUN 中の書込みを許可するか（iQ-R の既定は許可）。</summary>
    public bool RunWriteAllowed { get; set; } = true;

    /// <summary>リモートリセットを許可するか。</summary>
    public bool RemoteResetAllowed { get; set; } = true;

    /// <summary>リモートパスワード。<c>null</c> なら無効。</summary>
    public string? RemotePassword { get; set; }

    /// <summary>起動時の動作状態。</summary>
    public PlcRunState InitialState { get; set; } = PlcRunState.Run;

    /// <summary>
    /// リモートリセット後、要求に応答しない時間（ミリ秒）。実機 iQ-R 系は約 22 秒だった。既定 0 はすぐ応答する。
    /// </summary>
    public int ResetRestartMs { get; set; }

    /// <summary>登録できるユニット・ネットワークの上限。</summary>
    public const int MaxUnits = 4;

    public const int MaxNetworks = 4;

    /// <summary>
    /// 装着済みのユニット番号（Un\G・拡張ユニット読書きで使える。先頭入出力番号の上位桁、16 進）。既定は U0。
    /// CPU バッファメモリ U3E0〜U3E3 は CPU 内蔵のため登録不要。
    /// </summary>
    public IReadOnlyList<ushort> Units { get; set; } = new ushort[] { 0 };

    /// <summary>リンクダイレクト（Jn\）で使えるネットワーク番号。既定は J1。</summary>
    public IReadOnlyList<byte> Networks { get; set; } = new byte[] { 1 };

    public static bool IsCpuBuffer(ushort unit) => unit is >= 0x3E0 and <= 0x3E3;

    public bool HasUnit(ushort unit) => IsCpuBuffer(unit) || Units.Contains(unit);

    public bool HasNetwork(byte network) => Networks.Contains(network);

    /// <summary>登録内容を検査する（上限・範囲・重複）。</summary>
    public void Validate()
    {
        if (Units.Count > MaxUnits)
            throw new ArgumentException($"ユニットは {MaxUnits} 個まで登録できます（{Units.Count} 個）");
        if (Networks.Count > MaxNetworks)
            throw new ArgumentException($"ネットワークは {MaxNetworks} 個まで登録できます（{Networks.Count} 個）");
        if (Units.Distinct().Count() != Units.Count || Networks.Distinct().Count() != Networks.Count)
            throw new ArgumentException("ユニット番号・ネットワーク番号が重複しています");
        if (Units.Any(static u => u > 0xFF))
            throw new ArgumentException("ユニット番号は U0〜UFF で指定してください（U3E0〜U3E3 は CPU バッファメモリで登録不要）");
        if (Networks.Any(static n => n is < 1 or > 239))
            throw new ArgumentException("ネットワーク番号は J1〜J239 で指定してください");
    }
}

/// <summary>
/// 仮想 PLC 本体。デバイスメモリ・バッファメモリ・ラベル・動作状態を保持する。
/// すべての読み書きは <see cref="SyncRoot"/> のロック下で行う（SLMP 要求・スクリプト・GUI 共通）。
/// </summary>
public sealed class PlcModel
{
    /// <summary>ラッチクリアの対象外（ラッチ）デバイスのファミリ。</summary>
    private static readonly string[] LatchFamilies = { "L" };

    private PlcRunState _state;
    private ushort _errorCode;

    public PlcModel(CpuProfile profile, PlcSettings? settings = null, LabelDefinitions? labels = null)
    {
        Profile = profile;
        Settings = settings ?? new PlcSettings();
        Settings.Validate();
        Memory = new DeviceMemory(profile);
        Buffers = new BufferMemory();
        Labels = new LabelStore(labels ?? (profile.Features.Labels ? LabelDefinitions.SlmpTestDefault() : new LabelDefinitions()));
        _state = Settings.InitialState;
        InitializeSpecialDevices();
    }

    public object SyncRoot { get; } = new();

    public CpuProfile Profile { get; }

    public PlcSettings Settings { get; }

    public DeviceMemory Memory { get; }

    public BufferMemory Buffers { get; }

    public LabelStore Labels { get; }

    public PlcRunState State
    {
        get
        {
            lock (SyncRoot)
                return _state;
        }
    }

    /// <summary>自己診断エラーコード（SD0）。</summary>
    public ushort ErrorCode
    {
        get
        {
            lock (SyncRoot)
                return _errorCode;
        }
    }

    public event Action<PlcRunState>? StateChanged;

    /// <summary>バッファメモリ・リンクダイレクト・自局メモリの書込み後に発生する。</summary>
    public event Action<MemoryChange>? BufferChanged;

    public void RaiseBufferChanged(MemoryChange change) => BufferChanged?.Invoke(change);

    public bool CanWriteDevices => _state != PlcRunState.Run || Settings.RunWriteAllowed;

    public void SetState(PlcRunState state, string source)
    {
        lock (SyncRoot)
        {
            if (_state == state)
                return;
            _state = state;
            RefreshSpecialDevices();
        }

        StateChanged?.Invoke(state);
    }

    public void SetErrorCode(ushort code)
    {
        lock (SyncRoot)
        {
            _errorCode = code;
            RefreshSpecialDevices();
        }
    }

    /// <summary>RUN 時のデバイスクリア（0: しない, 1: ラッチ以外, 2: 全て）。</summary>
    public void ClearOnRun(ushort clearMode, string source)
    {
        lock (SyncRoot)
        {
            if (clearMode == 0)
                return;
            var families = Profile.Points.Keys
                .Where(static f => f is not ("SM" or "SD"))
                .Where(f => clearMode == 2 || !LatchFamilies.Contains(f, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            Memory.Clear(families, source);
            RefreshSpecialDevices();
        }
    }

    /// <summary>ラッチクリア（ラッチデバイスを含むユーザデバイスのクリア）。</summary>
    public void LatchClear(string source)
    {
        lock (SyncRoot)
        {
            Memory.Clear(LatchFamilies, source);
        }
    }

    /// <summary>リモートリセット: STOP にして自己診断エラーを解除する。</summary>
    /// <summary>
    /// リセット（再起動）。実機計測（iQ-R 系）: 再起動後は RUN/STOP スイッチに従う。PLC Sim では起動時の状態（InitialState）に従う。
    /// </summary>
    public void Reset(string source)
    {
        PlcRunState state;
        lock (SyncRoot)
        {
            _errorCode = 0;
            _state = state = Settings.InitialState;
            RestartingUntil = Settings.ResetRestartMs > 0 ? DateTime.UtcNow.AddMilliseconds(Settings.ResetRestartMs) : null;
            RefreshSpecialDevices();
        }

        StateChanged?.Invoke(state);
    }

    /// <summary>リセット後の再起動が終わる時刻（UTC）。null なら再起動中ではない。</summary>
    public DateTime? RestartingUntil { get; private set; }

    /// <summary>リセット後の再起動中か（この間は要求に応答しない）。</summary>
    public bool IsRestarting => RestartingUntil is { } until && DateTime.UtcNow < until;

    /// <summary>SD/SM の動的な値（動作状態・エラー・時計）を更新する。ロック下で呼ぶ。</summary>
    public void RefreshSpecialDevices()
    {
        var stateCode = _state switch
        {
            PlcRunState.Run => (ushort)0x0000,
            PlcRunState.Stop => (ushort)0x0002,
            _ => (ushort)0x0003,
        };
        Memory.SetSystemWord("SD", 203, stateCode);
        Memory.SetSystemWord("SD", 0, _errorCode);
        Memory.SetSystemBit("SM", 0, _errorCode != 0);
        Memory.SetSystemBit("SM", 1, _errorCode != 0);
        if (Profile.Series != CpuSeries.IqF)
        {
            Memory.SetSystemBit("SM", 400, true);
            Memory.SetSystemBit("SM", 401, false);
        }
        else
        {
            Memory.SetSystemBit("SM", 400, _state == PlcRunState.Run);
            Memory.SetSystemBit("SM", 401, _state != PlcRunState.Run);
        }

        if (Profile.ClockInSd && Profile.Series != CpuSeries.Q)
        {
            var now = DateTime.Now;
            Memory.SetSystemWord("SD", 210, (ushort)now.Year);
            Memory.SetSystemWord("SD", 211, (ushort)now.Month);
            Memory.SetSystemWord("SD", 212, (ushort)now.Day);
            Memory.SetSystemWord("SD", 213, (ushort)now.Hour);
            Memory.SetSystemWord("SD", 214, (ushort)now.Minute);
            Memory.SetSystemWord("SD", 215, (ushort)now.Second);
            Memory.SetSystemWord("SD", 216, (ushort)now.DayOfWeek);
        }
    }

    private void InitializeSpecialDevices()
    {
        foreach (var rule in Profile.SdPointRules)
        {
            var points = Profile.PointsOf(rule.Family);
            if (rule.Clip is { } clip)
                points = Math.Min(points, clip);
            Memory.SetSystemWord("SD", (uint)rule.Register, (ushort)points);
            if (rule.DoubleWord)
                Memory.SetSystemWord("SD", (uint)rule.Register + 1, (ushort)(points >> 16));
        }

        RefreshSpecialDevices();
    }
}
