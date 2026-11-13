# 開発者向けガイド

利用方法は [README](../README.md) を参照してください。

## 構成

| パス | 内容 |
|---|---|
| `src/PlcSim.Slmp.Core` | フレーム（3E/4E × バイナリ/ASCII）、コマンド処理、仮想 PLC、TCP/UDP サーバー、アクセス記録 |
| `src/PlcSim.Slmp.Scripting` | HTTP 制御 API、Python スクリプト実行、起動・再構築 |
| `src/PlcSim.Slmp.Gui` | GUI（WPF + WPF-UI） |
| `src/PlcSim.Slmp.Host` | コンソール版 |
| `tests/PlcSim.Slmp.Tests` | テスト |
| `tests/PlcSim.Slmp.Gui.Tests` | GUI の ViewModel・編集のテスト |
| `tests/release` | 配布・検査スクリプトの Python テスト |
| `assets/icon/make_icons.py` | アイコン生成（Pillow） |
| `Directory.Build.props` | 製品名・バージョン・開発元 |

対応コマンドは [plc-comm-slmp-dotnet](https://github.com/fa-yoshinobu/plc-comm-slmp-dotnet)（NuGet `PlcComm.Slmp`）が使う 26 種です。それ以外のコマンドには C059 を返します。

## ビルドとテスト

.NET 10 SDK が必要です。

```powershell
dotnet run --project src/PlcSim.Slmp.Gui
dotnet test
```

テストは手組みの伝文、`PlcComm.Slmp` による通信、Python スクリプト、HTTP API を確認します。`ApiCoverageTests.cs` は `PlcComm.Slmp` の通信 API 80 件をすべて呼び、10 機種 × TCP/UDP でも確認します。Python のテストには PATH 上の `python` が必要です。

## 配布版の作成

```bat
build.bat              :: テスト後、1 ファイル exe（.NET ランタイム同梱）で artifacts\release に発行
build.bat folder       :: 1 ファイルにせず gui\・host\ に exe と DLL を並べる
build.bat framework    :: .NET ランタイムを同梱しない
build.bat notest       :: テストを省略
```

いずれも `artifacts\release\PlcSim.Slmp-win-x64.zip` を作ります。

## リリース

1. `Directory.Build.props` の `Version` と、[`CHANGELOG.md`](../CHANGELOG.md) の `[Unreleased]` をリリースするバージョンに書き換えます。
2. `.github/release-notes.md` の Changes 欄を CHANGELOG に基づいて英語で更新します。
3. `v` で始まるタグ（例: `v0.2.0`）を push します。タグと製品バージョンの不一致はワークフローで拒否されます。
4. GitHub Actions（`.github/workflows/release.yml`）がビルド・テスト・ZIPの内容とEXEバージョンの検査を行い、ZIPとSHA-256を公開します。本文は `.github/release-notes.md` を元に生成します。
5. 公開したEXEのVirusTotal検査が完了し、本文に結果リンクが載ることを確認します。不完全な場合は `Scan release with VirusTotal` にリリースタグを指定して再実行します。

配布・検査は [共通リリース標準](https://github.com/fa-yoshinobu/release_standard) に従います。リリース本文と検査メッセージは英語です。

### ランタイムだけを更新する場合

`Directory.Build.props` の `BundledRuntimeVersion` を更新し、製品Versionもパッチ版を上げます。CHANGELOGと `.github/release-notes.md` に同梱 .NET / ASP.NET Core / Windows Desktop Runtime の更新を記載し、上記と同じビルド・テスト・新規タグのリリース手順で配布します。公開済みZIPは差し替えません。
`Check bundled runtime` は週1回と手動起動で最新10.0 SDKに含まれるランタイムと固定値を比較し、古い場合は失敗として通知します。GitHubでの実行確認は反映後に行います。

共通標準の [リリース運用](https://github.com/fa-yoshinobu/release_standard/blob/main/WINDOWS_RELEASE_OPERATIONS.md) は、実行スクリプトが各アプリ側にあると定めています。共有スクリプトや再利用ワークフローは提供されていないため、このリポジトリの検査スクリプトと回帰テストを維持します。

## 画面デザイン

[デジタル庁デザインシステム（DADS）](https://design.digital.go.jp/dads/)に合わせています。配色は DADS のデザイントークン（`@digital-go-jp/design-tokens` 2.0.1、MIT License、© デジタル庁）の値です。ダーク配色は DADS に定義がないため、同じコントラスト基準で独自に組み合わせています。配色は `src/PlcSim.Slmp.Gui/Themes/Dads.Light.xaml` / `Dads.Dark.xaml`、部品のスタイルは `Themes/Styles.xaml` にあります。

配布ZIPとその `.zip.sha256` も `artifacts/release/` に作成します。ZIPには必要な配布ファイルだけを含め、既存ZIP・チェックサム・リリース本文は含めません。旧出力フォルダは自動削除しません。
