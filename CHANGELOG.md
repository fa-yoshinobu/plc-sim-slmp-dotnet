# Changelog

このプロジェクトの主な変更を記録します。

書式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に基づき、バージョンは [Semantic Versioning](https://semver.org/lang/ja/) に従います。

**項目の区分**

- `Server`: SLMP サーバー・仮想 PLC の挙動（フレーム、コマンド、メモリマップ、終了コード）。
- `GUI`: Windows GUI。
- `Scripting`: HTTP 制御 API、Python スクリプト・ライブラリ。
- `Tooling`: コンソール版、ビルド用スクリプト。
- `Docs`: README などの文書。
- `Tests`: テスト。

## [Unreleased]

## [0.1.3] - 2026-11-13

### Added

- Tooling: mainへのpush・PRのCIと、同梱ランタイムの更新確認ワークフローを追加。
- Server: UDPセッションの上限（1,024件）と無通信期限（10分）、サイズ上限つき診断ログを追加。
- GUI: バージョン情報にログフォルダーを開くボタンを追加。
- Tests: 設定検証、HTTP API保護、異常TCP伝文、UDP保持上限、メモリ処理、Python終了競合、ビルド失敗経路の回帰テストを追加。

### Changed

- Tooling: .NET 8から.NET 10へ移行し、.NET / Windows Desktop / ASP.NET Core 10.0.14を同梱。
- Scripting: HTTP APIでOrigin・Hostを検査し、ループバック以外の待受にはトークンを必須化。Pythonには環境変数で自動伝達。
- GUI: メモリビューアの値変換・編集・表示・検索・CSV出力を画面から分離。
- Tooling: ランタイムの固定値を一元化し、Actionをコミットで固定。ZIP内容とEXEバージョンを検査。
- Docs: 設定説明を配布物にも同梱し、リリース手順とダウンロード名を実態に合わせて整理。

- Aligned local and CI release output at `artifacts/release/`, with a fixed-name distribution ZIP and UTF-8/LF SHA-256 checksum.

### Fixed

- Tooling: ラベル定義の配列・構造体・要素がnullの設定を検証し、コンソールが理由を表示せず終了する問題を修正。
- GUI: CSV出力でカンマ・引用符・改行を含む文字列を引用し、列や行が崩れる問題を修正。
- Tooling: 設定値の誤りを項目・値・理由で表示。GUIは既定設定へ復帰し、Hostは終了コード1で終了。設定は一時ファイルから置換する。
- Server: TCP解析例外を診断へ記録して接続を閉じ、受信バッファの繰返しコピーを削減。
- Scripting: 不正な書込本文を400で拒否し、Python状態更新と即時終了の競合を修正。

- Reject incomplete VirusTotal responses without a matching API-provided executable SHA-256.

- Stop failed builds immediately instead of entering the duplicated fallback build; keep release metadata and stale content out of distribution ZIPs.

## [0.1.2] - 2026-10-27

### Changed

- Standardized fixed-name Windows ZIP assets, ZIP SHA-256 checksums and English release notes.

### Fixed

- Unified VirusTotal completion checks and preserved unrelated release notes during rescans.

## [0.1.1] - 2026-10-15

### Fixed

- GUI: メモリの数値入力ウィンドウが、ダブルクリック直後に閉じる問題を修正。
- GUI: 16 進表示の編集値に `0x` を付け、10 進数として誤解釈される問題を修正。整数の範囲チェックを追加。
- GUI: 32 ビット値・Float の後半ワードや不完全なペアの編集を防止し、自局メモリへの 2 ワード書込みを修正。
- GUI: Float・REAL・LREAL の編集で精度が失われる問題、WSTRING の Unicode 処理、ASCII の制御文字が表示用文字で上書きされる問題を修正。
- GUI: 「値のある番地だけ表示」が書込み・クリア・メモリ再構築後に更新されない問題を修正。
- GUI: スクリプトの切替・再読込みで未保存の編集が消える問題を修正。終了時の保存確認を追加し、保存失敗時は実行・終了を中止。
- GUI: 設定適用後も未適用のデバイス範囲・装着ユニット・ネットワーク・ラベルの編集を保持。デバイス範囲に未適用の編集がある場合は、機種変更前に確認。
- GUI: 通信ログの取込み件数と保持上限を修正し、一時停止中も表示済みログを絞り込めるよう修正。スクリプト出力は表示待ちの段階から保持量を制限。
- GUI: 小さいウィンドウで設定画面のテーマ・エラー注入項目が隠れる問題を修正。
- Scripting: HTTP API の接続先変更後、実行中の Python スクリプトを新しい接続先で再起動。API 無効化時は停止。再起動によりスクリプト内の変数は初期化されます。

### Added

- Tests: メモリ入力、未保存・未適用の編集保持、ログ表示、API 接続先変更時の Python 再起動を確認する回帰テストを追加。

### Changed

- Tooling: `build.bat` のテスト対象をソリューション全体に変更し、GUI のテストも実行。

## [0.1.0] - 2026-10-09

最初の公開版です。実装済みの機能は [機能一覧](https://github.com/fa-yoshinobu/plc-sim-slmp-dotnet/blob/main/docs/FEATURES.md) を参照してください。

[Unreleased]: https://github.com/fa-yoshinobu/plc-sim-slmp-dotnet/compare/v0.1.3...HEAD
[0.1.3]: https://github.com/fa-yoshinobu/plc-sim-slmp-dotnet/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/fa-yoshinobu/plc-sim-slmp-dotnet/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/fa-yoshinobu/plc-sim-slmp-dotnet/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/fa-yoshinobu/plc-sim-slmp-dotnet/releases/tag/v0.1.0
