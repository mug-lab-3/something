# 配布

## 開発出力と発行物

通常の `dotnet build` は exe・DLL・画面リソースを展開した状態で出力します。Debug / Release とも開発用です。

```text
src/Something.App/bin/<Configuration>/net10.0-windows10.0.26100.0/win-x64/
```

配布用だけ、次のスクリプトまたは VS Code の `publish app` タスクで発行します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/publish-app.ps1
```

スクリプトは自己完結のアプリをフォルダ形式で発行し、そのフォルダから全ユーザー向けの MSI インストーラーを作ります。

```text
artifacts/publish/win-x64/                         発行フォルダ（MSI の中身と同一）
artifacts/publish/Something-v1.0.0-win-x64.msi     配布用インストーラー
```

発行用の bin / obj / 復元結果は `artifacts/publish-build` に分離します。スクリプトはロック付き復元を行い、exe・PRI・ライセンス通知・アイコンがあり、シンボルを含まないことを検証してから前回の発行フォルダを置き換えます。失敗時は前回の出力を維持します。生成専用フォルダに手作業のファイルを置かないでください。

## インストーラー

[installer/](../installer/) は WiX Toolset 5（MS-RL）の SDK 形式プロジェクトで、`dotnet build` だけでビルドできます。Visual Studio や追加のツール導入は不要で、SDK は NuGet から取得します。WiX 6 以降は別途の利用規約（OSMF）への同意が必要なため使っていません。

| 項目 | 内容 |
| --- | --- |
| インストール先 | `C:\Program Files\Something`（全ユーザー、管理者確認あり） |
| ショートカット | スタートメニューの「Something」 |
| 更新 | 同じ UpgradeCode の旧版を置き換える。プレリリースは同じ数値版の正式版で置き換えられる |
| アンインストール | Windows の「インストールされているアプリ」から。`%LOCALAPPDATA%\Something` の履歴と設定は残す |

MSI の版は `Something.App.csproj` の `<Version>` の数値部分です（`1.1.0-beta.1` は `1.1.0`）。UpgradeCode は全版で共通の固定値で、変更すると旧版を置き換えられなくなります。

WinUI の DLL が宣言する言語数が多いことなどによる既知の ICE 検証結果は、理由を記録したうえで [Something.Installer.wixproj](../installer/Something.Installer.wixproj) で除外しています。

### 単一 exe にしない理由

アプリは管理者権限で動作します。自己展開型の単一 exe は起動時に `%TEMP%/.net/` へ DLL を展開して読み込み、既存の展開物を再利用します。この場所は同じユーザーの管理者でないプロセスも書き換えられるため、差し替えられた DLL が管理者権限で実行される恐れがあります。MSI は管理者だけが書き込める `Program Files` に全ファイルを置くため、この経路を作りません。同じ理由で、ZIP の展開配布やユーザー単位のインストールも行いません。

## GitHub Release

[release.yml](../.github/workflows/release.yml) は GitHub の Actions 画面から手動実行します。選択したコミットの `Something.App.csproj` の `<Version>` を読み取り、`1.0.0` なら `v1.0.0` のタグとリリースを自動作成します。タグやバージョンの手入力は不要です。

1. 必要ならバージョンを更新し、変更をコミットして GitHub へ push する。初回はワークフローをデフォルトブランチに含める。
2. GitHub の **Actions → Release → Run workflow** を開く。
3. 発行するブランチを選択して **Run workflow** を押す。

Windows ランナーで、既存のハッシュ検証付きセットアップを使って固定 SDK とネイティブランタイムを準備し、`scripts/publish-app.ps1` で MSI を作ります。このワークフローではテストを実行しません。`Something-v1.0.0-win-x64.msi` を、生成したリリースノートとともに GitHub Release へ直接添付します。ライセンス通知は MSI の中にインストールされます。

`1.1.0-beta.1` のようなバージョンはプレリリースにします。公開済みのバージョンは作り直さず、同じバージョンのリリースが公開済みならワークフローを失敗させます。既存のタグが別のコミットを指している場合も、タグを付け替えずに失敗させます。内容を変える場合はバージョンを上げてください。

ビルドと MSI 作成が成功してからリモートを変更します。リリースは下書きで作成し、アップロード成功後に公開します。途中で失敗した場合は下書きが残ることがあり、同じコミットでの再実行に限り、その下書きを更新して公開できます。

MSI の SHA-256 を記載した `SHA256SUMS.txt` を同じリリースに添付し、MSI にはビルド来歴（GitHub Artifact Attestations）を付けます。利用者は次のどちらかで、配布物がこのリポジトリのワークフローで作られたことを確認できます。

```powershell
Get-FileHash .\Something-v1.0.0-win-x64.msi -Algorithm SHA256
gh attestation verify .\Something-v1.0.0-win-x64.msi --repo mug-lab-3/something
```

MSI はまだコード署名していません。実行時に Windows SmartScreen が発行元不明の警告を表示します。

Actions のアーティファクトと依存キャッシュは作成せず、削除待ちの保存領域を残しません。Release に添付した MSI は配布物として保持します。実行ログは通常の Actions の保持設定に従います。

認証は組み込みの `GITHUB_TOKEN` と `contents: write` を使用し、追加の個人トークンは不要です。ビルド来歴の署名には `id-token: write` と `attestations: write` を使います。リポジトリは Actions の SHA 固定を必須にしているため、アクションを追加・更新するときはコミット SHA で指定してください。リポジトリ・組織のポリシーで Actions の書き込みを許可している必要があります。コマンドの仕様は [GitHub CLI: release create](https://cli.github.com/manual/gh_release_create) を参照してください。

## ランタイム

`Properties/PublishProfiles/Folder.pubxml` は .NET と Windows App SDK を自己完結で含め、トリミングせずにフォルダへ発行します。WinUI、DuckDB、PRI / XBF、ライセンス通知も含めます。WinUI の PRI / XBF は .NET の発行処理が自動で含めないため、プロファイルで追加しています。

配布先に .NET / Windows App SDK を別途導入する必要はありませんが、同梱ランタイムの修正を取り込むには再発行が必要です。履歴と設定はインストール先ではなくユーザー別の保存先へ置きます。

## Windows と権限

現在の発行対象は Windows x64、ターゲットは Windows 10 SDK 26100、プロジェクトの最小バージョン指定は 17763 です。この指定だけを全 Windows 版での検証保証とは扱いません。

マニフェストは `requireAdministrator` と `PerMonitorV2` を指定します。初回起動時を含め UAC 確認が表示されます。デモとプレビューも同じ exe なので昇格指定は共通です。

## 依存の管理

WinUI と DWrite のコンポーネントパッケージを参照し、NuGet ロックで推移的依存も固定します。WiX SDK はプロジェクトの `Sdk` 属性で版を固定します。発行後にネイティブ DLL や多言語リソースを手作業で削って構成を作る方式にはしません。

`LICENSE` と `THIRD-PARTY-NOTICES.md` をインストール先へ含めます。パッケージを追加・更新した場合は、推移的依存・ネイティブライブラリを含めて通知を見直します。テスト専用依存はアプリの配布依存と区別します。コード署名と MSIX の発行は行いません。

## 発行後の確認

MSI を管理用展開（`msiexec /a <msi> /qn TARGETDIR=<空のフォルダ>`）し、`PFiles64\Something` の構成が発行フォルダと一致することを確認できます。システムへの登録は行いません。実際のインストールでは、起動と再起動、PRI / XBF の読み込み、デモの履歴保存・再表示、アンインストール後に履歴が残ることを確認します。実履歴や実ファイルを使う確認とは分離します。

参照: [WinUI アプリのパッケージなし配布](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)、[WiX Toolset](https://wixtoolset.org/docs/intro/)。
