# VS Codeでの開発

Visual Studio本体は不要です。.NET 10 SDKとVS Codeでビルド・テストできます。C# 14を明示的に指定し、nullable参照型と警告のエラー扱いを有効にしています。

## SDKと拡張機能

Windows x64 / Arm64でGitとVS Codeを用意し、VS Codeの`code`コマンドがPATHにある状態で、リポジトリ直下から次を実行します。Visual Studioと事前の.NET SDKは不要です。SDKの初回インストールにはWindowsの管理者確認への同意が必要です。初回はMicrosoftのSDK配布先、NuGet、VS Code Marketplaceへ接続できる環境が必要です。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1 -Open
```

このコマンドが行うこと:

1. `global.json`の.NET SDK 10.0.401をWindowsの通常の場所へインストール。`scripts/sdk-downloads.json`に固定したMicrosoft公式インストーラURLとSHA-512で取得内容を検証。既に同じSDKがある場合は再利用。
   `.config/dotnet-tools.json`でバージョン固定したCSharpierも`dotnet tool restore`で復元。
2. `.vscode/extensions.json`の推奨拡張をインストール。拡張のバージョンは固定していません。既存の拡張を強制更新しません。
3. NuGet依存を各プロジェクトの`packages.lock.json`に従って`--locked-mode`で復元。
   CSharpierで整形済みかを検査し、整形漏れがあればファイルを書き換えずに停止。
4. ソリューションのビルドと単体テストを実行し、成功時にリポジトリのフォルダを開く。

同じコマンドを繰り返し実行できます。SDKと拡張が準備済みなら再利用します。SDKは公式インストーラでシステムへ導入し、他のSDKバージョンを削除しません。SDKインストーラによるシステム設定・PATHへの登録が行われます。PowerShellの実行ポリシーは変更せず、`Bypass`は起動したプロセスだけに適用します。検証済みインストーラは再試行用に`.tools/installers`へ保持します。リポジトリ内にSDK本体は展開しません。

```powershell
# エディタ不要のCLI / CI環境: SDK・復元・ビルド・単体テスト
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1 -SkipExtensions
# 公開NISTイメージも取得・検証（追加約7 GiB）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1 -WithPublicCorpus -Open
```

通常のセットアップでは公開コーパスの7テストをスキップします。必要な合成・変異・OSS比較テストは実行します。調査用の`.tools/oss`や`.tools/audit`は開発の前提にしていません。公開データの利用条件と検証範囲は[public-corpus.md](public-corpus.md)を参照してください。VS Code自体の画面上でのデバッグ動作は未検証です。

SDK更新時は`global.json`と`scripts/sdk-downloads.json`を同時に更新し、[Microsoft公式リリース情報](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)のSHA-512を使ってください。NuGet依存変更時は`restore Something.slnx --force-evaluate`でlockファイルを更新し、変更をレビューします。普段のセットアップでlockファイルを自動更新しません。`NuGet.Config`で取得元をnuget.orgへ固定しています。

Windows x64 / Windows PowerShell 5.1で、公式インストーラのダウンロード・ハッシュ検証・システムへの初回インストール、既存SDKの再利用、空の`.tools`からのセットアップ、キャッシュ済みインストーラのハッシュ不一致時の実行拒否を検証済みです。ビルドと単体テスト136件が成功しています。C# Dev KitでもシステムSDKを使用したソリューション読み込み・NuGet復元を確認しました。Arm64の配布先も用意していますが、Arm64実機での実行は未検証です。

VS Codeではリポジトリのフォルダをそのまま開いてください。SDKのパス上書き、専用の起動スクリプト、生成ワークスペースは不要です。ソリューション選択と整形設定はリポジトリの設定で共有します。

## ビルド・テスト

リポジトリ直下で.NET CLIを直接実行します。`global.json`がSDKバージョンを固定します。

```powershell
dotnet build Something.slnx
dotnet test --solution Something.slnx
dotnet test --solution Something.slnx --coverage --coverage-output-format cobertura --coverage-settings tests/coverage.runsettings --results-directory artifacts/test-results
```

VS Codeでは `Ctrl+Shift+B` でbuild、`Tasks: Run Task` でtest／test with coverageを実行できます。F5はオフラインのraw $MFTダンプを検査するCLIをデバッグします。ダンプのパスとレコード長を入力してください。ファイル名や内容は標準出力には出しません。

## フォーマットとコード品質

整形は[CSharpier](https://csharpier.com/docs/Installation) 1.3.0をローカル.NETツールとして固定しています。CLIとVS Codeの保存時に同じバージョンを使用します。CSharpierの標準的な整形を採用し、`.editorconfig`でスペース4つ、折り返し目安100文字、LFを共有します。C#以外の対応XML（csproj、props、slnxなど）はスペース2つです。`.tools`や生成物は`.csharpierignore`で対象外にしています。独自の整形ルールやもう一つの整形エンジンを重ねません。

```powershell
# 一括整形
dotnet csharpier format . --no-cache --ignore-path .csharpierignore
# 書き換えずに検査（整形漏れは終了コード1）
dotnet csharpier check . --ignore-path .csharpierignore
```

VS CodeではC#の保存時に整形されます。`Tasks: Run Task`の`format` / `format check`でも実行できます。`setup.ps1`が拡張とツールを復元し、検査まで実行します。VS Codeの画面上での保存時整形は未検証です。

セットアップは整形漏れで失敗します。ビルドが自動的にソースを変更する構成にはしていません。コード品質・安全性の診断は既存の.NET/Roslyn解析と警告のエラー扱いを継続します。[Microsoftのdotnet format](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format)もEditorConfigを使う公式の選択肢ですが、本リポジトリの空白・折り返しの整形はCSharpierに統一します。

## テスト方針

- xUnit.net v3（パッケージ4.0.2）とMicrosoft.Testing.Platform v2を使用。VSTestやVisual Studio本体は不要。
- AAA、振る舞いを示すテスト名、独立性、再現性を重視。管理者権限・ライブディスク・外部サービスを単体テストに要求しません。
- 正常系、境界値、構造破損、符号／桁あふれ、途中読み取り、キャンセル、削除／変更／再利用を検証。
- 乱数テストは固定seedで実行。5000回のレコード変異と5000回のmapping-pair入力を検査。これは網羅的なファジングの代替ではありません。
- MFT 2026.5.1はテスト専用の比較対象。製品ライブラリは依存しません。合致は正しさの証明ではありません。
- 行カバレッジと分岐カバレッジをSomething.Ntfsに限定して計測。目安は行95%以上／分岐90%以上ですが、数字だけを満たすテストを追加せず、未検証の重要な振る舞いを優先します。
- ライブボリューム取得の実装後は、Windows APIとのサイズ照合と作成・変更・移動・削除の統合テストを追加します。

## 現在の構成

- `src/Something.Ntfs`: WindowsやUIに依存しない解析／レコード結合／読み取り検証層。
- `src/Something.Windows`: 読み取り専用NTFS取得層とファイル情報API。実機の通常ファイル1件で論理サイズ一致を確認。
- `tests/Something.Ntfs.Tests`: 単体・変異・OSS比較テスト。
- `tools/Something.MftInspect`: オフラインraw MFTストリームの検査CLI。
- `tools/Something.LiveInspect`: 少数のライブレコードとファイル論理サイズの検証CLI。

Windows取得層の実装範囲・検証手順は [windows-acquisition.md](windows-acquisition.md) を参照してください。高速全件取得、USN追跡、VSS、容量履歴、GUIはまだ実装していません。
