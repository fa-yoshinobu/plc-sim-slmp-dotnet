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

[Unreleased]: https://github.com/fa-yoshinobu/plc-sim-slmp-dotnet/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/fa-yoshinobu/plc-sim-slmp-dotnet/releases/tag/v0.1.0
