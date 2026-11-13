# 設定ファイル

`config/default.json` は JSON（コメント・末尾カンマも可）です。GUIで保存するとJSONを整形し、説明へのリンクを先頭に付けます。個別のコメントは保存しないため、標準の説明はここにまとめています。保存は同じディレクトリの一時ファイルを完成させてから置き換えます。

| 項目 | 設定方法 |
| --- | --- |
| `server.bindAddress` | SLMP待受IP。既定は `127.0.0.1` |
| `server.enableTcp` / `enableUdp` | TCP/UDPを有効化 |
| `server.tcpPort` / `udpPort` | 既定1025。0はテスト用の空きポート自動割当て、上限65535 |
| `server.dataCode` | `Auto` / `Binary` / `Ascii`。Autoはシミュレータ独自で両方へ応答 |
| `plc.profile` | `melsec:iq-r` などの機種ID。設定画面の機種一覧を参照 |
| `plc.modelName` / `modelCode` | 形名・数値の形名コード。省略時は機種の既定値 |
| `plc.devicePoints` | `{ "ZR": 4915200 }` のように機種のデバイス点数を上書き |
| `plc.units` | 16進文字列0〜FF、重複なし・最大4個。U3E0〜U3E3はCPUバッファなので登録不要 |
| `plc.networks` | 整数1〜239、重複なし・最大4個 |
| `plc.runWriteAllowed` / `remoteResetAllowed` | RUN中の書込み・リモートリセットの許可 |
| `plc.resetRestartMs` | リセット後の無応答時間。0以上、既定0。実機iQ-R系の計測値は約22000ms |
| `plc.remotePassword` | 設定するとSLMP接続ごとにロック状態で開始。nullは無効 |
| `plc.initialState` | `Run` / `Stop` / `Pause` |
| `api.enabled` / `bindAddress` / `port` | HTTP API。有効時は既定 `127.0.0.1:5080`。ポート1〜65535 |
| `api.token` | Bearerトークン。ループバック以外は必須。[APIの保護](../README.md)参照 |
| `initialValues` | 番地 `address`、型 `type`、配列 `values`。型はBIT/U16/S16/U32/S32/F32/STRING |
| `gui.theme` | `system` / `light` / `dark` |
| `pythonExecutable` | PATH上のpython、または実行ファイルの絶対パス |
| `scripts` | 起動時のPythonスクリプト。相対パスは設定ファイルのディレクトリから解決 |
| `errorInjection` | エラー注入ルールの配列。例: `{ "command": 1025, "endCode": 49233, "remainingCount": 1 }` |

誤りは項目名・値・理由で表示します。GUIは既定設定で起動し、コンソールは終了コード1で停止します。トークンやパスワードを含むファイルは共有しないでください。
