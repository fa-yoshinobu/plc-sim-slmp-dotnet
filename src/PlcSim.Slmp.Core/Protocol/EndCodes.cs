namespace PlcSim.Slmp.Core.Protocol;

/// <summary>
/// 応答に使う終了コード。
/// 根拠の区分: [live] = plc-comm-slmp-dotnet の実機計測値（SlmpCapabilityProfiles）、
/// [manual] = SLMP リファレンスマニュアル記載、[ref] = 参考実装 SlmpMockServer の採用値、
/// [unverified] = 実機未確認の暫定値。暫定値は設定で上書きできる。
/// [live-iqr] = 実機 iQ-R 系（R08PCPU、2026-10-09）の ASCII 交信で計測した値（Q / L も同じ値を使う。Q / L 実機では未確認）。
/// </summary>
public static class EndCodes
{
    public const ushort Success = 0x0000;

    /// <summary>[live] 未対応のコマンド / サブコマンド。</summary>
    public const ushort CommandSubcommandError = 0xC059;

    /// <summary>[live] 一括ワード点数超過（iQ-R/Q）。iQ-F ではビット側。</summary>
    public const ushort PointsOverC051 = 0xC051;

    /// <summary>[live] 一括ビット点数超過（iQ-R/Q）。iQ-F ではワード側。</summary>
    public const ushort PointsOverC052 = 0xC052;

    /// <summary>[live] ランダムビット書込み点数超過。</summary>
    public const ushort RandomBitPointsOver = 0xC053;

    /// <summary>[live] ランダム / モニタ ワード点数超過。</summary>
    public const ushort RandomWordPointsOver = 0xC054;

    /// <summary>[live] QJ71E71 経由のランダム書込み点数超過。</summary>
    public const ushort UnitRandomWritePointsOver = 0x4080;

    /// <summary>[live-iqr] ASCII の要求データ部の長さが内容と合わない（不足・過多）。</summary>
    public const ushort DataLengthMismatch = 0xC058;

    /// <summary>[live-iqr] バイナリの要求データ部の長さが内容と合わない（不足・過多）、バイナリでランダム読出し・モニタ登録にビット単位を指定した。</summary>
    public const ushort BinaryDataLengthMismatch = 0xC061;

    /// <summary>[live-iqr] バイナリのビット単位書込みで、値が 0 / 1 以外。</summary>
    public const ushort BinaryBitValueError = 0xC060;

    /// <summary>[live-iqr] iQ-R 系で、インデックス修飾を Q/L 形式の拡張指定（0080/0081）で指定した（iQ-R 形式 0082/0083 なら受け付ける）。</summary>
    public const ushort ModificationFormatError = 0xC05A;

    /// <summary>[live-iqr] ASCII データの変換エラー（16 進・10 進でない文字、ビット値が 0/1 以外）。</summary>
    public const ushort AsciiConversionError = 0xC050;

    /// <summary>[live-iqr] ブロック読書きでビット単位のサブコマンドを指定した。</summary>
    public const ushort BlockBitUnitError = 0xC0D9;

    /// <summary>[live-iqr] 読出し専用デバイス（S）への書込み（一括・ランダム）、LTC/LTS の一括読出し・一括書込み。</summary>
    public const ushort DeviceAccessError = 0x4030;

    /// <summary>[live-iqr] LTC/LTS をワード・ダブルワード単位で指定した（ランダム読書き、モニタ実行）。</summary>
    public const ushort DeviceUnitError = 0x4032;

    /// <summary>[live-iqr] 拡張ユニット読書き（0601/1601）で指定したユニットがない。</summary>
    public const ushort ModuleNotFound = 0x4043;

    /// <summary>[live-iqr] リモートリセットがパラメータで許可されていない。</summary>
    public const ushort RemoteResetNotAllowed = 0x408B;

    /// <summary>[live-iqr] リモート RUN / STOP / PAUSE のモード指定が不正。</summary>
    public const ushort RemoteModeError = 0x400B;

    /// <summary>[live-iqr] モニタ実行（0802）のサブコマンドが不正。実機はエラー情報のコマンドに 0403 を返す。</summary>
    public const ushort MonitorSubcommandError = 0x4002;

    /// <summary>[ref] 要求内容の誤り（パラメータ値不正など）。[live-iqr] デバイス系コマンドの未定義サブコマンドもこの値。</summary>
    public const ushort RequestContentError = 0xC05C;

    /// <summary>[ref] RUN 中書込み禁止時の書込み要求。</summary>
    public const ushort WriteDuringRunProhibited = 0xC05F;

    /// <summary>[live-iqr] デバイス範囲外（パラメータ点数を超える番地、最終番地をまたぐ範囲）。</summary>
    public const ushort DeviceOutOfRange = 0x4031;

    /// <summary>
    /// [live-iqr] 未知のデバイスコード、ランダム読出し・モニタ登録でのビット単位指定。
    /// [unverified] ブロックに指定できないデバイス等にも使う。
    /// </summary>
    public const ushort DeviceAccessNotAllowed = 0xC05B;

    /// <summary>[ref] 状態条件エラー（RUN 中のラッチクリア等）。</summary>
    public const ushort StateConditionError = 0x4013;

    /// <summary>[ref] リモートパスワードロック中。</summary>
    public const ushort RemotePasswordLocked = 0xC201;

    /// <summary>[ref] リモートパスワード不一致。</summary>
    public const ushort RemotePasswordMismatch = 0xC810;

    /// <summary>[live-iqr] 拡張指定で、指定したユニット（Un\G）・ネットワーク（Jn\）が装着/登録されていない。</summary>
    public const ushort UnitNotFound = 0x4031;

    /// <summary>[live-iqr] 指定ラベルが存在しない。</summary>
    public const ushort LabelNotFound = 0x40C5;

    public static string Describe(ushort endCode) => endCode switch
    {
        Success => "正常終了",
        CommandSubcommandError => "コマンド/サブコマンド誤り",
        PointsOverC051 => "点数超過(C051)",
        PointsOverC052 => "点数超過(C052)",
        RandomBitPointsOver => "ランダムビット点数超過",
        RandomWordPointsOver => "ランダムワード点数超過",
        UnitRandomWritePointsOver => "ランダム書込み点数超過(ユニット)",
        DataLengthMismatch => "データ長不一致",
        BinaryDataLengthMismatch => "データ長不一致（バイナリ）",
        BinaryBitValueError => "ビット値エラー",
        ModificationFormatError => "デバイス修飾指定エラー",
        AsciiConversionError => "ASCII 変換エラー",
        BlockBitUnitError => "ブロック指定エラー",
        ModuleNotFound => "ユニットなし",
        DeviceAccessError => "デバイスアクセスエラー",
        DeviceUnitError => "デバイス指定エラー",
        RemoteResetNotAllowed => "リモートリセット不可",
        RemoteModeError => "リモート操作指定エラー",
        MonitorSubcommandError => "モニタ指定エラー",
        RequestContentError => "要求内容誤り",
        WriteDuringRunProhibited => "RUN中書込み禁止",
        DeviceOutOfRange => "デバイス範囲外",
        DeviceAccessNotAllowed => "デバイスアクセス不可",
        StateConditionError => "状態条件エラー",
        RemotePasswordLocked => "リモートパスワードロック中",
        RemotePasswordMismatch => "リモートパスワード不一致",
        LabelNotFound => "ラベルなし",
        _ => "エラー",
    };
}
