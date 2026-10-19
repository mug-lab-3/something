# Something

Windows 向けのディスク容量・フォルダ肥大化分析アプリです。ローカル NTFS ドライブの MFT を解析し、現在の大きなフォルダと、スキャン間で容量が増減した場所を調べます。

## 主な機能

- ドライブ単位のスキャン、進捗・キャンセル、複数ドライブの並行取得。
- フォルダツリーとファイル一覧、選択フォルダ以下を横断する容量分析。
- スキャン履歴と、任意の二記録・期間プリセットによるサイズ比較。
- 隠し・システム項目の表示と集計の切り替え。
- 履歴の選択削除、DB 最適化、保存先の変更。
- パスのコピー、関連付けアプリで開く、確認付きのゴミ箱への移動。

画面の「サイズ」はディスク上の DATA 割り当て容量を基準とします。ハードリンクは可視名ごとに数えるため、一覧の合計とドライブの使用済み容量は同じ指標ではありません。[容量の意味](docs/capacity-accounting.md)を参照してください。

## 開発を始める

Windows x64、Git、VS Code の `code` コマンドを用意し、リポジトリのルートで実行します。Visual Studio は不要です。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1 -Open
```

固定 SDK、必要なネイティブランタイム、ローカルツール、拡張、NuGet 依存を準備し、整形検査・ビルド・テストを実行します。初回はネットワーク接続と、未導入ランタイムのインストールに管理者確認が必要です。[開発環境の詳細](docs/development.md)を参照してください。

```powershell
dotnet build src/Something.App -c Release
```

開発用 exe は `src/Something.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/Something.App.exe` です。起動時に UAC 確認が表示されます。CLI から起動・デバッグする場合は管理者ターミナルを使用します。

## 実データを使わずに試す

Release ビルド後に `run-demo.bat` を実行すると、合成した三ドライブで画面と履歴比較を確認できます。専用履歴は `artifacts/demo-history` に保存し、実ディスクはスキャンしません。アニメーション単体は `run-animation-preview.bat` で確認できます。

## 配布用 exe

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/publish-app.ps1
```

`artifacts/publish/win-x64/Something.App.exe` を生成します。必要なランタイムとリソースを同梱し、起動時に展開する単一 exe です。通常のビルド出力とは分離しています。[配布の仕様](docs/deployment.md)を参照してください。

## データと制限

履歴は既定で `%LOCALAPPDATA%\Something\History\catalog-v4.duckdb` に保存します。直近 7 日間は全記録、それ以前は各日の最終記録を保持し、スキャン成功後に整理します。常時監視や定期スキャンは行いません。

NTFS 取得は読み取り専用です。実ファイルの変更は、利用者が確認したゴミ箱操作だけです。スキャンは複数時点にまたがる観測であり、VSS のような時点スナップショットではありません。未解決・未観測をゼロ容量や削除済みと断定しません。

対象はローカル NTFS ドライブです。フォルダ単位の取得、ネットワーク共有、他のファイルシステム、USN 差分取得、データ種別分類は未対応です。既存の非対応 DB の自動移行は行いません。

## 技術情報

C# 14 / .NET 10、WinUI 3、DuckDB、EF Core を使用します。解析・Windows 取得・統計・保存・UI は独立した層に分けています。

[技術ドキュメントの目次](docs/README.md)から、設計・テスト・診断 CLI・性能比較を参照できます。

ライセンスは [LICENSE](LICENSE)、配布に含める依存の通知は [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)を参照してください。
