# 開発環境

## セットアップ

開発対象は Windows x64 です。Git と VS Code をインストールし、`code` コマンドを PATH に追加してください。Visual Studio は使いません。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1 -Open
```

`setup.ps1` は次の処理を行います。

1. `global.json` で固定したシステム SDK と Visual C++ ランタイムを確認し、必要なら公式のインストーラーを取得する。
2. チェックインしたハッシュと照合してからインストールする。
3. ローカルの .NET ツールと、VS Code の推奨拡張機能を復元する。
4. NuGet のロック付き復元、書式の検査、ビルド、単体テストを実行する。
5. `-Open` を指定した場合は、リポジトリを VS Code で開く。

未導入のシステムコンポーネントをインストールするときは、管理者の確認が必要です。SDK はリポジトリの `.tools` にはインストールせず、通常のシステム SDK を使います。`.tools` はダウンロードキャッシュなどを置く生成物の領域で、Git には含めません。初回はネットワーク接続が必要です。

エディターの拡張機能が不要な場合や、公開データでも検証する場合は、次のように実行します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1 -SkipExtensions
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1 -WithPublicCorpus
```

公開データを使う場合は、追加のダウンロードと約 7 GiB の展開領域が必要です。詳しくは[公開コーパス](public-corpus.md)を参照してください。拡張機能の一覧は [extensions.json](../.vscode/extensions.json)、タスクは [tasks.json](../.vscode/tasks.json) を正とします。

## CLI と VS Code

```powershell
dotnet restore Something.slnx --locked-mode
dotnet build Something.slnx --no-restore
dotnet test --solution Something.slnx --no-restore
```

SDK は .NET 10.0.401、言語は C# 14 です。SDK の選択は `global.json`、言語と解析の設定は `Directory.Build.props` で確認できます。範囲を限定しない preview の言語バージョンは使いません。

VS Code では、リポジトリのフォルダーをそのまま開きます。SDK のパスを独自に上書きしたり、開発者専用のワークスペースを用意したりする必要はありません。`build` / `test` / `test with coverage` タスクで、CLI と同じ処理を実行できます。

通常のビルドと単体テストに、管理者権限は不要です。実行中のアプリが DLL を使っている場合は、アプリを閉じてからビルドしてください。実際のドライブを読み取るアプリの `dotnet run` とデバッグは、管理者として開いたターミナルか VS Code で行います。

## 整形とコード品質

CSharpier をローカルツールとして版を固定し、C# と対応する XML の書式をそろえます。VS Code の保存時の整形と CLI は同じツールを使います。標準のルールと `.editorconfig` に従い、ほかの整形ツールを重ねて使うことはしません。

```powershell
dotnet tool restore
dotnet csharpier format . --ignore-path .csharpierignore
dotnet csharpier check . --ignore-path .csharpierignore
```

書式とは別に、Roslyn アナライザーと、警告をエラーとして扱う設定で品質を検査します。テストの実行とカバレッジは[テスト](testing.md)を参照してください。

## アプリのバージョン

バージョンは `src/Something.App/Something.App.csproj` の `<Version>` で変更します。アセンブリ・ファイル・情報バージョンのメタデータは SDK が生成するため、`AssemblyInfo.cs` や C# の定数を手書きで追加しません。

タイトルバーには、生成された情報バージョンを `Something 1.0.0` の形式で表示します。`1.1.0-beta.1` のようなプレリリース名はそのまま表示し、`+` 以降のビルド情報は表示しません。通常のウィンドウとアニメーションプレビューで、同じ定義を使います。

`app.manifest` の `assemblyIdentity` は Windows のマニフェスト上の識別情報で、画面に表示するバージョンとは関係ありません。

## 依存と SDK の更新

NuGet パッケージとローカルツールは版を固定し、各 `packages.lock.json` をコミットします。依存関係を変えたときだけ `dotnet restore Something.slnx --force-evaluate` でロックを更新し、推移的な依存関係も確認します。通常のセットアップではロックを更新しません。

SDK を更新するときは、`global.json` と `scripts/sdk-downloads.json` を同時に変更し、[Microsoft 公式リリースメタデータ](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)の SHA-512 を使います。ネイティブランタイムの情報は `scripts/native-runtime-download.json` で管理します。変更したセットアップは空の `.tools` から確認します。

## 保存モデルの変更

共有履歴のモデルは `SharedHistoryDbContext`、単独スキャン用の DuckDB は `DuckScanDbContext` です。SQLite は比較と回帰テストのためだけに使います。マイグレーションは、対象のコンテキストを明示して生成します。

```powershell
dotnet ef migrations add ChangeName --project src/Something.Storage --context SharedHistoryDbContext --output-dir SharedMigrations
```

生成された DDL、型アダプター、モデルのスナップショット、形式の識別、公開前の論理参照の検査を、まとめてレビューします。物理 FK とカスケードは追加せず、依存行を明示的に削除できることも検証します。既存の DB を自動で変換する処理は用意しません。

## 公開物に含めないもの

SDK、キャッシュ、ビルド出力、スキャンのダンプ、実際の履歴、個人のパスを含むログは、Git で管理しません。確認用の生成物は `artifacts` に、外部のコーパスは `.tools` に置きます。ライセンスや再現手順が必要な資料は、生成物へのリンクだけで済ませず、公開できる仕様・条件・コマンドを文書に残します。
