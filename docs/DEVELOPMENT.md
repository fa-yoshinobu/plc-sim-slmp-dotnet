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
| `assets/icon/make_icons.py` | アイコン生成（Pillow） |
| `Directory.Build.props` | 製品名・バージョン・開発元 |

対応コマンドは [plc-comm-slmp-dotnet](https://github.com/fa-yoshinobu/plc-comm-slmp-dotnet)（NuGet `PlcComm.Slmp`）が使う 26 種です。それ以外のコマンドには C059 を返します。

## ビルドとテスト

.NET 8 SDK が必要です。

```powershell
dotnet run --project src/PlcSim.Slmp.Gui
dotnet test
```

テストは手組みの伝文、`PlcComm.Slmp` による通信、Python スクリプト、HTTP API を確認します。`ApiCoverageTests.cs` は `PlcComm.Slmp` の通信 API 80 件をすべて呼び、10 機種 × TCP/UDP でも確認します。Python のテストには PATH 上の `python` が必要です。

## 配布版の作成

```bat
build.bat              :: テスト後、1 ファイル exe（.NET ランタイム同梱）で dist\PlcSim.Slmp に発行
build.bat folder       :: 1 ファイルにせず gui\・host\ に exe と DLL を並べる
build.bat framework    :: .NET ランタイムを同梱しない
build.bat notest       :: テストを省略
```

いずれも `dist\PlcSim.Slmp-win-x64.zip` を作ります。

## リリース

1. `Directory.Build.props` の `Version` と、[`CHANGELOG.md`](../CHANGELOG.md) の `[Unreleased]` をリリースするバージョンに書き換えます。
2. `v` で始まるタグ（例: `v0.2.0`）を push します。
3. GitHub Actions（`.github/workflows/release.yml`）がビルド・テストし、zip を GitHub Release に添付します。リリースノートは `CHANGELOG.md` の該当バージョンの節です。

## 画面デザイン

[デジタル庁デザインシステム（DADS）](https://design.digital.go.jp/dads/)に合わせています。配色は DADS のデザイントークン（`@digital-go-jp/design-tokens` 2.0.1、MIT License、© デジタル庁）の値です。ダーク配色は DADS に定義がないため、同じコントラスト基準で独自に組み合わせています。配色は `src/PlcSim.Slmp.Gui/Themes/Dads.Light.xaml` / `Dads.Dark.xaml`、部品のスタイルは `Themes/Styles.xaml` にあります。
