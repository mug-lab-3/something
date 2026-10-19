# 開発環境

## セットアップ

開発対象は Windows x64 です。Git と VS Code をインストールし、`code` を PATH へ追加してください。Visual Studio 本体は使用しません。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1 -Open
```

`setup.ps1` は次を実行します。

1. `global.json` に固定したシステム SDK と Visual C++ ランタイムを確認し、必要なら公式インストーラーを取得する。
2. チェックインしたハッシュと照合してからインストールする。
3. ローカル .NET ツールと VS Code 推奨拡張を復元する。
4. NuGet のロック付き復元、整形検査、ビルド、単体テストを実行する。
5. `-Open` 指定時はリポジトリを VS Code で開く。

未導入のシステムコンポーネントのインストールには管理者確認が必要です。SDK はリポジトリの `.tools` にインストールせず、通常のシステム SDK を使います。`.tools` はダウンロードキャッシュ等の生成領域で、Git に含めません。初回はネットワーク接続が必要です。

エディタ拡張が不要な場合と、公開データも検証する場合は次のように実行します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1 -SkipExtensions
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1 -WithPublicCorpus
```

公開データは追加のダウンロードと約 7 GiB の展開領域を使います。[公開コーパス](public-corpus.md)を参照してください。拡張の一覧は [extensions.json](../.vscode/extensions.json)、タスクは [tasks.json](../.vscode/tasks.json) が正本です。

## CLI と VS Code

```powershell
dotnet restore Something.slnx --locked-mode
dotnet build Something.slnx --no-restore
dotnet test --solution Something.slnx --no-restore
```

SDK は現在 .NET 10.0.401、言語は C# 14 です。SDK 選択は `global.json`、言語・解析設定は `Directory.Build.props` を確認してください。無制限の preview バージョンは使いません。

VS Code はリポジトリのフォルダをそのまま開きます。SDK パスの独自上書きや開発者専用ワークスペースは不要です。`build` / `test` / `test with coverage` タスクで CLI と同じ処理を実行できます。

通常ビルドと単体テストに昇格は不要です。実行中のアプリが DLL を使用している場合は閉じてから出力を更新します。ライブ取得を含むアプリの `dotnet run` とデバッグは、管理者として開いたターミナルまたは VS Code で行います。

## 整形とコード品質

CSharpier をローカルツールとして固定し、C# と対応 XML の整形を統一します。VS Code の保存時整形と CLI は同じツールを使用します。標準ルールと `.editorconfig` を維持し、別の空白フォーマッターを重ねません。

```powershell
dotnet tool restore
dotnet csharpier format . --ignore-path .csharpierignore
dotnet csharpier check . --ignore-path .csharpierignore
```

整形とは別に Roslyn アナライザーと警告のエラー扱いで品質を検査します。テスト実行とカバレッジは[テスト](testing.md)を参照してください。

## アプリのバージョン

`src/Something.App/Something.App.csproj` の `<Version>` を変更します。SDK がアセンブリ・ファイル・情報バージョンのメタデータを生成するため、手書きの `AssemblyInfo.cs` や C# の定数を重ねません。

タイトルバーは生成された情報バージョンを使用し、`Something 1.0.0` の形式で表示します。`1.1.0-beta.1` のようなプレリリース名は保持し、`+` 以降のビルド情報は表示しません。通常ウィンドウとアニメーションプレビューは同じ定義を使います。

`app.manifest` の `assemblyIdentity` は Windows マニフェストの識別情報で、画面に表示するアプリバージョンの定義元ではありません。

## 依存と SDK の更新

NuGet パッケージとローカルツールを固定し、各 `packages.lock.json` をコミットします。依存変更時だけ `dotnet restore Something.slnx --force-evaluate` でロックを更新し、推移的依存も確認します。通常セットアップではロックを更新しません。

SDK 更新は `global.json` と `scripts/sdk-downloads.json` を同時に変更し、[Microsoft 公式リリースメタデータ](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)の SHA-512 を使います。ネイティブランタイムの情報は `scripts/native-runtime-download.json` で管理します。変更したセットアップは空の `.tools` から確認します。

## 保存モデルの変更

共有履歴のモデルは `SharedHistoryDbContext`、単独スキャン用 DuckDB は `DuckScanDbContext` です。SQLite は比較・回帰用途です。目的のコンテキストを明示してマイグレーションを生成します。

```powershell
dotnet ef migrations add ChangeName --project src/Something.Storage --context SharedHistoryDbContext --output-dir SharedMigrations
```

生成 DDL、型アダプター、モデルスナップショット、形式識別、公開前の論理参照検査を合わせてレビューします。物理 FK・カスケードを追加しない運用を維持し、依存行の明示削除も検証します。既存 DB の自動変換とは別の工程です。

## 公開物に含めないもの

SDK、キャッシュ、ビルド出力、スキャンダンプ、実履歴、個人のパスを含むログは追跡しません。確認用の生成物は `artifacts`、外部コーパスは `.tools` に置きます。ライセンスや再現手順を必要とする資料は、生成物へのリンクだけで済ませず、公開可能な仕様・条件・コマンドを文書に残します。
