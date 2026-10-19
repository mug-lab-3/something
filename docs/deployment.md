# 配布

## 開発出力と発行物

通常の `dotnet build` は exe・DLL・画面リソースを展開した状態で出力します。Debug / Release とも開発用で、単一ファイルへの梱包は行いません。

```text
src/Something.App/bin/<Configuration>/net10.0-windows10.0.26100.0/win-x64/
```

配布用だけ、次のスクリプトまたは VS Code の `publish app` タスクで発行します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/publish-app.ps1
```

出力は `artifacts/publish/win-x64/Something.App.exe` です。発行用の bin / obj / 復元結果は `artifacts/publish-build` に分離します。スクリプトはロック付き復元を行い、新しい出力が exe 一つだけであることを検証してから前回の発行フォルダを置き換えます。失敗時は前回の出力を維持します。生成専用フォルダに手作業のファイルを置かないでください。

## GitHub Release

[release.yml](../.github/workflows/release.yml) は GitHub の Actions 画面から手動実行します。選択したコミットの `Something.App.csproj` の `<Version>` を読み取り、`1.0.0` なら `v1.0.0` のタグとリリースを自動作成します。タグやバージョンの手入力は不要です。

1. 必要ならバージョンを更新し、変更をコミットして GitHub へ push する。初回はワークフローをデフォルトブランチに含める。
2. GitHub の **Actions → Release → Run workflow** を開く。
3. 発行するブランチを選択して **Run workflow** を押す。

Windows ランナーで、既存のハッシュ検証付きセットアップを使って固定 SDK とネイティブランタイムを準備し、単一 exe を発行します。このワークフローではテストを実行しません。`Something-v1.0.0-win-x64.zip` に次の三ファイルを同梱し、生成したリリースノートとともに GitHub Release へ直接添付します。

```text
Something.App.exe
LICENSE
THIRD-PARTY-NOTICES.md
```

`1.1.0-beta.1` のようなバージョンはプレリリースにします。公開済みのバージョンは作り直さず、同じバージョンのリリースが公開済みならワークフローを失敗させます。既存のタグが別のコミットを指している場合も、タグを付け替えずに失敗させます。内容を変える場合はバージョンを上げてください。

ビルドと ZIP 作成が成功してからリモートを変更します。リリースは下書きで作成し、アップロード成功後に公開します。途中で失敗した場合は下書きが残ることがあり、同じコミットでの再実行に限り、その下書きを更新して公開できます。

ZIP と exe の SHA-256 を記載した `SHA256SUMS.txt` を同じリリースに添付し、両ファイルにはビルド来歴（GitHub Artifact Attestations）を付けます。利用者は次のどちらかで、配布物がこのリポジトリのワークフローで作られたことを確認できます。

```powershell
Get-FileHash .\Something-v1.0.0-win-x64.zip -Algorithm SHA256
gh attestation verify .\Something-v1.0.0-win-x64.zip --repo mug-lab-3/something
```

Actions のアーティファクトと依存キャッシュは作成せず、削除待ちの保存領域を残しません。Release に添付した ZIP は配布物として保持します。実行ログは通常の Actions の保持設定に従います。

認証は組み込みの `GITHUB_TOKEN` と `contents: write` を使用し、追加の個人トークンは不要です。ビルド来歴の署名には `id-token: write` と `attestations: write` を使います。リポジトリは Actions の SHA 固定を必須にしているため、アクションを追加・更新するときはコミット SHA で指定してください。リポジトリ・組織のポリシーで Actions の書き込みを許可している必要があります。コマンドの仕様は [GitHub CLI: release create](https://cli.github.com/manual/gh_release_create) を参照してください。

## ランタイムと展開

`Properties/PublishProfiles/SingleFile.pubxml` は .NET と Windows App SDK を自己完結で含め、ネイティブ依存とコンテンツの自己展開を有効にします。WinUI、DuckDB、PRI / XBF、ライセンス通知もバンドルします。トリミングは無効です。

起動時には通常 `%TEMP%/.net/Something.App/<bundle-id>` に必要ファイルを展開し、同じバンドルは再利用します。`DOTNET_BUNDLE_EXTRACT_BASE_DIR` があればその場所を使用します。単一 exe は静的リンクや展開不要の意味ではありません。版の異なる exe では旧キャッシュが残る場合があります。

配布先に .NET / Windows App SDK を別途導入する必要はありませんが、同梱ランタイムの修正を取り込むには再発行が必要です。履歴と設定は展開先ではなくユーザー別の保存先へ置きます。

## Windows と権限

現在の発行対象は Windows x64、ターゲットは Windows 10 SDK 26100、プロジェクトの最小バージョン指定は 17763 です。この指定だけを全 Windows 版での検証保証とは扱いません。

マニフェストは `requireAdministrator` と `PerMonitorV2` を指定します。初回起動時を含め UAC 確認が表示されます。デモとプレビューも同じ exe なので昇格指定は共通です。

## 依存の管理

WinUI と DWrite のコンポーネントパッケージを参照し、NuGet ロックで推移的依存も固定します。発行後にネイティブ DLL や多言語リソースを手作業で削って構成を作る方式にはしません。

`LICENSE` と `THIRD-PARTY-NOTICES.md` を配布物へ含めます。パッケージを追加・更新した場合は、推移的依存・ネイティブライブラリを含めて通知を見直します。テスト専用依存はアプリの配布依存と区別します。署名・インストーラー・MSIX の発行は、このスクリプトでは行いません。

## 発行後の確認

exe だけを置いた専用ディレクトリから起動し、初回展開と再起動、PRI / XBF の読み込み、デモの履歴保存・再表示を確認します。実履歴や実ファイルを使う確認とは分離します。配布マニフェストの権限指定と、必要な通知が含まれていることも確認します。

参照: [WinUI の単一 exe](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app#single-file-exe)、[.NET の単一ファイル展開](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)。
