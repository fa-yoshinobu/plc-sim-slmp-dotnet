# PLC Sim (SLMP) 改善ゴール

[README](README.md) · [機能一覧](docs/FEATURES.md) · [開発者向けガイド](docs/DEVELOPMENT.md) · [TODO](TODO.md)

v0.1.2（.NET 10 へ移行した時点）のレビューを起点に、2026-11-13のローカル実装・検証結果を反映した作業記録です。背景は着手前の状態で、現行コードとの相違は各項目に注記します。チェック済みはローカルで実装・確認した条件を示し、GitHub反映済みを意味しません。

実機照合（終了コード・デバイス点数・形名コードなど）と複数局対応は [TODO](TODO.md) で管理しているため、本書には含めません。

## 共通ルール

- コミット・PRは依頼された場合に行い、ゴール単位でレビューできるようにする。
- SLMP の応答（伝文のバイト列・終了コード）を変えない。実機計測に基づくテスト（`RealPlcBinaryResponseTests`、`RealPlcAsciiResponseTests`）が通ること。
- 既存の `config/default.json` とスナップショットをそのまま読めること。
- HTTP API のパスと応答の形式を変えない。変える場合はゴールに明記されたものだけ。
- 変更に対応する検証を行い、最終差分で次の共通検証を成功させる。同じ全検証をゴールごとに繰り返さない（Python のテストには PATH 上の `python` が必要）。

```powershell
dotnet build PlcSim.Slmp.slnx -c Release -p:TreatWarningsAsErrors=true
dotnet test PlcSim.Slmp.slnx -c Release --no-build
python -m unittest discover -s tests/release
```

## 進捗

| # | ゴール | 優先度 | 状態 |
| --- | --- | --- | --- |
| G1 | `build.bat` が失敗時に正しく止まる | 高 | 実装・ローカル検証済み |
| G2 | push/PRでビルドとテストが走る | 高 | 実装済み／GitHub実行待ち |
| G3 | 他のWebページからHTTP APIを操作できない | 高 | 実装・統合テスト済み |
| G4 | 文書の記述が実態と一致している | 高 | 記述修正済み |
| G5 | 設定ファイルの誤りで起動不能にならない | 中 | 実装・起動確認済み |
| G6 | 長時間動かしてもサーバーが劣化しない | 中 | 実装・100万要求検証済み |
| G7 | 診断とエラーがファイルに残る | 中 | 実装・画面確認済み |
| G8 | `MemoryViewModel` を役割ごとに分ける | 中 | 分離・テスト済み／手動確認一部 |
| G9 | リリースの仕組みを他のリポジトリと揃える | 中 | 標準確認・実装済み／公開検証待ち |
| G10 | 細かな堅牢化 | 低 | 一部完了／別PC検証など残件あり |

### 2026-11-13の検証結果

以下はローカル検証時点の記録です。GitHubへの反映とCIの最新結果は[Pull Request](https://github.com/fa-yoshinobu/plc-sim-slmp-dotnet/pulls)を参照してください。

- 警告をエラーにしたReleaseビルド、.NET 217件（Core等163・GUI54）が成功。Python配布テスト42件は前回確認済みで、今回のラベル・CSV修正では再実行していない。スキップなし。
- 追加レビューの2件を修正。ラベル定義内のnullを項目名つきの設定エラーにし、CSVのカンマ・引用符・改行をエスケープする。修正前に11ケースの失敗を確認し、修正後は省略・空のラベル設定も含めて成功。CSVは標準パーサーで元の値・列・行に復元できることを確認。配布Hostでもnull設定5ケースすべてで理由の表示と終了コード1を確認し、更新したZIPの内容・SHA-256を照合済み。
- `build.bat` の通常発行成功と、復元〜ZIP作成の各失敗経路・配布必須ファイル不足を検証。ZIPとSHA-256をローカル生成。
- 配布EXEで設定誤りからのGUI復帰、Hostの引数エラー、ポート競合の診断ログ、ログフォルダー表示、メモリ画面のTab移動・フォーカス・CSV保存を確認。
- UDP 100万要求／10万クライアント生成を135.41秒で完了。10万要求ごとのWorking Setは99,880,960〜101,298,176 bytes、最後は99,971,072 bytes。数日間の連続運転を保証する検証ではない。
- ローカルの詳細記録: `artifacts/validation/udp-stress.json`、`artifacts/validation/memory-manual.csv`（成果物フォルダーはGit管理外）。
- ローカル検証時点の残件: GitHub上のCI成功・意図的失敗、定期ランタイム確認、プレリリース一式、.NET未導入PC、全画面のキーボード操作と各表示形式の手動編集。リリースは未実施。

---

## G1: `build.bat` が失敗時に正しく止まる

**Goal**: `build.bat` のどの段階で失敗しても、エラーを表示して終了コード 1 で終わる。失敗後に別の手順が走らない。

**背景**: レビュー文書には重複した失敗ラベルと旧手順が記載されていたが、着手時の `8f86260` では削除済みで、ZIP作成も `tools/package-release.ps1` に移行済みだった。今回は残っていたREADME・LICENSEコピー失敗の未検出と、配布ファイル・ZIP内容の確認不足を補う。

**対象**: `build.bat`、`.github/workflows/release.yml`

**完了条件**
- [x] 旧手順がなく、`:fail` が1つであることを確認する（着手時に対応済み）。
- [x] 各段階（復元・ビルド・テスト・発行・コピー・ZIP作成）の失敗で、終了コード 1 になる。`copy` の失敗も検出する。
- [x] 発行後、期待するファイル（`PlcSim.Slmp.Gui.exe`、`PlcSim.Slmp.Host.exe`、`config\default.json`、`python\plc_sim.py`、ZIP）が存在することを確認し、なければ失敗にする。
- [x] リリース用ワークフローで、ZIPの中身（上記のファイルが入っていること）を確認する。

**検証済み**: `tests/release/test_build_batch.py` が実際のバッチの制御構造を使い、各外部コマンドの失敗・必須ファイル不足・成功を検証する。通常の `build.bat` でもZIP生成に成功。設定説明書のコピー失敗も対象に含む。

**対象外**: ビルド手順の PowerShell への書き換え。

---

## G2: push/PRでビルドとテストが走る

**Goal**: main へのpushとPull Requestのたびに、ビルドと全テストが自動で実行され、失敗が分かる。

**背景**: テストが走るのはタグpush時（`release.yml`）だけで、TODOにも「CIを追加する」が残っている。`release.yml` はActionをタグ指定（`actions/checkout@v4`、`setup-dotnet@v4`、`setup-python@v5`）で使い、`setup-python` を2回実行し、`timeout-minutes` も `persist-credentials: false` もない。タグと `Directory.Build.props` の一致確認は着手時に追加済みだったため、今回はEXEの製品バージョン検査を補う。

**対象**: `.github/workflows/`（新規 `ci.yml`）、`.github/workflows/release.yml`、`TODO.md`

**完了条件**
- [x] `ci.yml` が `push`（main）と `pull_request` で起動し、`windows-latest` で `dotnet build`・`dotnet test`（ソリューション全体）・`python -m unittest discover -s tests/release` を実行する。
- [x] Python を使うテスト（`ScriptingAndApiTests`）のために Python をセットアップする。
- [x] `ci.yml` は `permissions: contents: read` のみ。
- [x] `release.yml` を含むすべてのActionをコミットハッシュで固定し、`persist-credentials: false` と `timeout-minutes` を設定する。`setup-python` は1回にまとめる。
- [x] `release.yml` で、タグと `Directory.Build.props` の `<Version>` が一致しなければ中止する。発行したEXEのバージョンも確認する。
- [x] CIのビルドで警告をエラーにし（`TreatWarningsAsErrors`）、警告を解消する。
- [x] TODOの該当項目を完了にする。

**残検証**: GitHub反映後、成功するPRと意図的に失敗するテストのブランチで結果を確認する。ローカルではCI相当のビルド・テストが成功。

**対象外**: 実機を使うテストの自動化。

---

## G3: 他のWebページからHTTP APIを操作できない

**Goal**: 利用者がブラウザで開いている別のWebページから、HTTP 制御 API を呼んで CPU 状態や値を変えることができない。

**背景**: `src/PlcSim.Slmp.Scripting/ControlApi.cs` は接続元（Origin）も Host ヘッダーも確認しない。本文なしの POST（`/api/plc/run`・`stop`・`pause`・`reset`、`/api/devices/{番地}/toggle`・`inc`）は、ブラウザの制約（事前確認）にかからないため、任意のページから送信できる。Host ヘッダーを見ていないので、名前解決を切り替える手口（DNSリバインディング）では読み書きすべてが可能になる。`api.bindAddress` を `0.0.0.0` にすると、認証なしで他のPCからも操作できる。試験用ツールだが、自動テストの最中に外部から状態を変えられると原因の特定が難しい。

**対象**: `src/PlcSim.Slmp.Scripting/ControlApi.cs`、`src/PlcSim.Slmp.Core/Config/SimulatorConfig.cs`、`python/plc_sim.py`、`config/default.json`、`README.md`、`docs/FEATURES.md`、`tests/PlcSim.Slmp.Tests/ScriptingAndApiTests.cs`

**完了条件**
- [x] Host ヘッダーが待受アドレス（`127.0.0.1`・`localhost`・設定した待受アドレス）とポートに一致しない要求を 403 で断る。
- [x] Origin ヘッダーが付いた要求（ブラウザからの別オリジンの呼び出し）を 403 で断る。PowerShell・Python・curl など Origin を付けないクライアントは従来どおり動く。
- [x] 待受アドレスがループバック以外のときは、設定したトークン（`api.token`）を必須にする。トークン未設定でループバック以外に待ち受けようとしたら、起動エラーとして表示する。
- [x] `plc_sim.py` は環境変数（`PLC_SIM_TOKEN`）でトークンを受け取り、ヘッダーで送る。GUI・コンソールから起動したスクリプトには自動で渡す。
- [x] 本文つきの要求で本文が空・不正なときは、500 ではなく 400 を返す。
- [x] Origin つき・Host 不一致・トークンなしが断られることのテストがある。
- [x] README と `FEATURES.md` の HTTP API の説明に、この保護とトークンの設定方法を追記する。

**検証**: 別オリジンのテスト用HTML（`file://`）から `fetch('http://127.0.0.1:5080/api/plc/stop', {method:'POST', mode:'no-cors'})` を実行し、CPU 状態が変わらない。README の PowerShell の例はそのまま動く。

**対象外**: HTTPS 対応、SLMP 側の接続制限。

---

## G4: 文書の記述が実態と一致している

**Goal**: README・DEVELOPMENT・CHANGELOG・ワークフローのコメントが、現在の動作と一致している。

**背景**: リリースの仕組みを共通化したあと、古い記述が残っている。

**対象**: `README.md`、`docs/DEVELOPMENT.md`、`CHANGELOG.md`、`.github/workflows/release.yml`

**完了条件**
- [x] `CHANGELOG.md` 末尾のリンク定義に `[0.1.2]` を追加し、`[Unreleased]` の比較元を `v0.1.2` にする。
- [x] README「はじめかた」のZIP名（`PlcSim.Slmp-vX.Y.Z-win-x64.zip`）を、現在の固定名（`PlcSim.Slmp-win-x64.zip`）に合わせる。展開先のフォルダー名（`PlcSim.Slmp`）も書く。
- [x] `DEVELOPMENT.md` の「リリースノートは CHANGELOG の該当バージョンの節」を、実際の手順（`.github/release-notes.md` の Changes 欄を更新）に直す。`release.yml` 冒頭のコメントも同じく直す。
- [x] `DEVELOPMENT.md` の構成表に `tests/PlcSim.Slmp.Gui.Tests` と `tests/release` を追加する。
- [x] README 末尾の英語ブロックと日本語本文でダウンロードの説明が重複しないようにし、保守者向けの手順は `DEVELOPMENT.md` へ移す。

**確認**: 利用者向けREADMEの配布名・展開フォルダー・リリース手順を現行コードと照合。変更した文書のリポジトリ内リンクは存在確認済み。外部リンクの一括疎通確認は未実施。旧ZIP名は変更前の説明として本書にのみ残す。

**対象外**: 英語版文書の追加。

---

## G5: 設定ファイルの誤りで起動不能にならない

**Goal**: `config/default.json` に誤りがあっても、どの項目が誤りかを表示して既定値で起動する。GUI から保存しても、ファイルが壊れたり説明のコメントが消えたりしない。

**背景**: 設定の読込み（`SimulatorConfig.Load`）は JSON の構文エラーだけを扱う。値の誤り（未知の機種名、16進でないユニット番号、範囲外のネットワーク番号、5個以上の登録など）は `SimulatorRuntime` の生成時に例外になる。GUI（`App.xaml.cs`）ではこの呼び出しが `try` の外にあり、エラー表示のあとウィンドウが出ないままプロセスが残る。コンソール版は例外の詳細を出して終了する。保存（`SimulatorConfig.Save`）は直接上書きで、書込み中に落ちるとファイルが壊れる。同梱の `default.json` には各項目の説明がコメントで書いてあるが、GUI の「設定ファイルに保存」で消える。

**対象**: `src/PlcSim.Slmp.Core/Config/SimulatorConfig.cs`、`src/PlcSim.Slmp.Gui/App.xaml.cs`、`src/PlcSim.Slmp.Host/Program.cs`、`src/PlcSim.Slmp.Scripting/SimulatorRuntime.cs`、`tests/`

**完了条件**
- [x] 設定を検証する処理が1か所にあり、誤りを「項目名・値・理由」の一覧で返す（機種、ポート範囲、待受アドレス、ユニット番号、ネットワーク番号、登録数の上限、デバイス点数、初期値のアドレスと型）。
- [x] GUI は誤りの一覧を表示し、既定設定で起動する。ウィンドウが出ないままプロセスが残ることがない。
- [x] コンソール版は誤りの一覧を表示して、終了コード 1 で終わる（例外の詳細は出さない）。
- [x] 起動引数の数値（`--port`、`--api-port`）が不正なときも同じ形で表示する。
- [x] 保存は一時ファイルに書いてから置き換える。
- [x] 保存で説明コメントが消えることへの対処を決めて実装する（例: 説明を `docs/` に移して `default.json` からコメントをなくす、または保存時に既定の説明コメントを付け直す）。
- [x] 設定フォルダーに書き込めない場合（読み取り専用の場所に展開した場合）は、理由と保存先を表示する。
- [x] 検証の単体テストがある。

**検証**: `default.json` の `plc.profile` を `"melsec:unknown"` にして GUI とコンソール版を起動し、誤りが表示される。GUI は既定設定で操作できる。

**対象外**: 設定項目の追加、設定ファイルの保存場所の変更。

---

## G6: 長時間動かしてもサーバーが劣化しない

**Goal**: 数日動かし続けても、メモリ使用量が増え続けず、想定外の例外で接続が黙って切れない。

**背景**: `src/PlcSim.Slmp.Core/Server/SlmpServer.cs` の UDP セッション（`_udpSessions`）は送信元（IPとポート）ごとに増え、停止するまで消えない。UDP クライアントは接続のたびに送信元ポートが変わることが多い。TCP の受信処理（`HandleClientAsync`）は `IOException` と `SocketException` 以外の例外を捕まえず、その場合は診断も出ずに接続が閉じる。伝文の解析（`FrameCodec.Parse`）は `ProcessFrame` の `try` の外にある。受信バッファは `List<byte>` への追加と先頭からの削除で、伝文ごとに配列を作り直している。

**対象**: `src/PlcSim.Slmp.Core/Server/SlmpServer.cs`、`src/PlcSim.Slmp.Core/Engine/SlmpSession.cs`、`tests/PlcSim.Slmp.Tests/RobustnessTests.cs`

**完了条件**
- [x] UDP セッションに保持の上限と期限を設ける（例: 最後の要求から一定時間で破棄、件数上限を超えたら古い順に破棄）。保持中はリモートパスワードの解除状態とモニタ登録を維持し、期限切れ・上限による破棄後は新規セッションとして扱う。
- [x] TCP の受信処理で想定外の例外が起きたら、診断（`Diagnostic`）に残してからその接続を閉じる。待受は続く。
- [x] 伝文の解析の失敗も、同じ経路で診断に残る。
- [x] 受信バッファを、伝文ごとの配列の作り直しが少ない方式にする（応答の内容は変えない）。
- [x] 10万個の異なる送信元エンドポイントをキャッシュへ登録しても、セッション数が上限以下であるテストがある（1 IPのポート番号は16 bitのため「10万個の異なるポート」は不可能）。
- [x] 解析中に例外を起こす入力で、接続が閉じ、診断が1件出て、次の接続を受け付けるテストがある。

**検証済み**: 上限1,024件・無通信10分のキャッシュ、解除状態保持と期限後の再生成、異常ASCII TCP伝文の切断と再接続をテスト。配布HostにUDP 100万要求を送り、メモリは約100 MBで安定した（測定値は上記）。

**対象外**: 接続数の上限、アイドル接続の切断（実機の挙動を確認してから）。

---

## G7: 診断とエラーがファイルに残る

**Goal**: サーバーの診断（不正な伝文、内部エラー、起動失敗）と未処理例外が、利用者のPCのログファイルに残る。

**背景**: 診断（`SlmpServer.Diagnostic`）はコンソール版では画面に出るだけで、GUI でも保存されない。未処理例外の記録は GUI だけで、保存先は `%TEMP%\plc_sim_gui_error.log`（上限なし、一時フォルダーの掃除で消える）。コンソール版には未処理例外の処理がない。`catch` で握りつぶしている箇所もある。

**対象**: `src/PlcSim.Slmp.Gui/App.xaml.cs`、`src/PlcSim.Slmp.Host/Program.cs`、`src/PlcSim.Slmp.Scripting/SimulatorRuntime.cs`、`README.md`

**完了条件**
- [x] ログを `%LOCALAPPDATA%\PlcSim.Slmp\logs\` に保存し、サイズ上限つきで古い分を捨てる。GUI とコンソール版で共通の仕組みを使う。
- [x] 記録するもの: 起動・停止、待受アドレスとポート、起動エラー、サーバーの診断、スクリプトの異常終了、未処理例外、G3 で断った要求。
- [x] 通信ログ（伝文の中身）は記録しない（量が多く、既に画面と API で見られるため）。
- [x] コンソール版に未処理例外の処理を入れ、記録してから終了コード 1 で終わる。
- [x] GUI の「バージョン情報」または「設定」から、ログのフォルダーを開ける。
- [x] ログの書込みに失敗してもアプリが落ちない。

**検証済み**: 使用中の1025番でHostを起動し、終了コード1と `Startup failed` のログを確認。GUIボタンから `diagnostic.log` のあるフォルダーを表示。MSIXのLocalAppDataリダイレクトを継承した環境でも実体パスを開く。現在分＋3世代、各1 MiBの上限と書込み失敗時の継続を単体テストで確認。

**対象外**: ログの外部送信。

---

## G8: `MemoryViewModel` を役割ごとに分ける

**Goal**: メモリビューアの表示・編集・検索・出力の処理が別のクラスになり、それぞれを単体でテストできる。

**背景**: `src/PlcSim.Slmp.Gui/ViewModels/MemoryViewModel.cs` は1,224行あり、ほかの ViewModel（145〜331行）に比べて突出している。v0.1.1 の修正の多くがメモリの編集（16進入力、32ビット値、Float、文字列）に集中しており、変更の影響範囲が読みにくい。

**対象**: `src/PlcSim.Slmp.Gui/ViewModels/`、`tests/PlcSim.Slmp.Gui.Tests/`

**完了条件**
- [x] 値の表示形式の変換（10進・16進・符号付・32bit・Float・文字列）を、画面に依存しないクラスに分ける。
- [x] 入力値の解釈と範囲チェック（編集）を、画面に依存しないクラスに分ける。
- [x] 番地ジャンプと「値のある番地だけ表示」の絞り込み、CSV 出力をそれぞれ分ける。
- [x] `MemoryViewModel` は、上記の組み立てと画面の状態管理が中心になる（目安500行以下、今回1,227行→569行。行数合わせだけの分割はしない）。
- [x] 既存の `MemoryEditingTests` がそのまま通る。分けたクラスごとの単体テストを追加する。
- [ ] 画面の動作（表示・編集・ハイライト）は変えない。

**確認**: 既存の `MemoryEditingTests` は変更せず成功。分離した変換・編集・表示・絞込・CSVを `MemoryServicesTests` で検証。画面表示、16進数0x1234の書込みとハイライト、CSV保存、D100への番地ジャンプを確認。全表示形式の手動編集は残件。

**対象外**: 画面デザインの変更、機能追加。

---

## G9: リリースの仕組みを他のリポジトリと揃える

**Goal**: 共通標準の運用に従い、同梱ランタイムの更新漏れを検出する。共有されていない実装の同一性は要件にしない。

**背景**: `check_release_virustotal.py` は window-rescue とは同一だが、Ladder_viewer とは内容が違う（sequence-dojo はさらに別の構成）。スクリプトを各リポジトリへコピーしているため、修正のたびに差が生まれる。`PlcSim.Slmp.Gui.csproj` と `PlcSim.Slmp.Host.csproj` は `RuntimeFrameworkVersion` を 10.0.14 に固定しており、自己完結型で配布しているため、ランタイムのセキュリティ修正は新しいリリースを出さない限り利用者に届かない。コンソール版は ASP.NET Core（HTTP API）も同梱する。

**対象**: `.github/workflows/`、`.github/scripts/`、`tests/release/`、`src/PlcSim.Slmp.Gui/PlcSim.Slmp.Gui.csproj`、`src/PlcSim.Slmp.Host/PlcSim.Slmp.Host.csproj`、`docs/DEVELOPMENT.md`

**完了条件**
- [x] [共通リリース標準](https://github.com/fa-yoshinobu/release_standard) の現行運用を確認する。[WINDOWS_RELEASE_OPERATIONS.md](https://github.com/fa-yoshinobu/release_standard/blob/main/WINDOWS_RELEASE_OPERATIONS.md) は実行スクリプトを各アプリ側に置くと定め、共有実装・再利用ワークフローは提供していない。既存の標準対応スクリプトと回帰テストを維持する。
- [x] 同梱ランタイムの扱いを決める。固定を続けるなら、古くなったことを知らせる定期ワークフローを用意する。外すなら、同梱されたバージョンをリリース本文かログに出す。
- [x] ランタイムのバージョン指定を `Directory.Build.props` の1か所にまとめる（現在は2つの csproj に重複）。
- [x] `DEVELOPMENT.md` に、ランタイム更新だけのリリース手順を書く。

**確認／残検証**: 配布回帰テストとローカルZIP・チェックサム生成は成功。定期ワークフローのGitHub実行とプレリリースタグからの一連の公開・検査は未実施。

**対象外**: コード署名。

---

## G10: 細かな堅牢化

**Goal**: 下記の小さな問題がそれぞれ解消されている。項目ごとに独立して着手できる。

**完了条件**
- [x] **`.gitattributes` / `.editorconfig`**: 改行と文字コード、C# の書式を定義する。
- [x] **コンソール版の引数**: 未知の引数を無視せず、誤りとして表示する。`--help` で引数の一覧を出す。
- [x] **スクリプトの状態**: `PythonScript` の `Status`・`Error` は複数のスレッドから書き換わるため、排他するか、変更を1つのスレッドにまとめる。`PythonScriptHost.Add` はロックの外でイベントを発行する。
- [x] **握りつぶしの見直し**: 想定外の例外・スナップショットの復元スキップ・DLL読込み失敗をG7のログへ記録する。通常の切断・停止キャンセルは記録しない。
- [ ] **キーボード操作**: TODOの「GUI のキーボード操作を確認する」を実施し、結果を反映する。
- [ ] **配布物の確認**: TODOの「.NET 未導入のPCでの確認」を実施し、結果を反映する。

**検証**: 共通検証と、各項目の目視確認。

**確認／残件**: CLIの `--help` は0、未知の引数・不正ポートは1で終了。Pythonの即時終了とイベントのロック外発行をテスト。メモリ画面のTab移動・フォーカス枠・EnterでのCSV保存を確認したが、全画面のキーボード確認と.NET未導入PCは未実施。TCPの想定外例外とスクリプト停止時例外は記録し、正常な切断・キャンセルの無記録処理は維持。残る無記録catchは通常の切断・停止キャンセルのため維持。診断追加後に関連43テストを再実行し成功。
