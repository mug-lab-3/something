# 配布

## 開発出力と発行物

通常の `dotnet build` は、exe・DLL・画面のリソースをそのままフォルダーに出力します。Debug / Release のどちらも開発用です。

```text
src/Something.App/bin/<Configuration>/net10.0-windows10.0.26100.0/win-x64/
```

配布用の発行は、次のスクリプトか VS Code の `publish app` タスクで行います。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/publish-app.ps1
```

スクリプトは、ランタイムを含む自己完結のアプリをフォルダー形式で発行し、そのフォルダーから全ユーザー向けの MSI インストーラーを作ります。

```text
artifacts/publish/win-x64/                         発行フォルダー（MSI の中身と同じ）
artifacts/publish/Something-v1.1.2-win-x64.msi     配布用インストーラー
```

発行用の bin / obj / 復元結果は、`artifacts/publish-build` に分けて置きます。スクリプトはロック付きで復元し、exe・PRI・ライセンス通知・アイコンがそろっていてシンボルを含まないことを確認してから、前回の発行フォルダーを置き換えます。失敗した場合は、前回の出力をそのまま残します。

> [!WARNING]
> 生成専用のフォルダーには、手作業でファイルを置かないでください。

## インストーラー

[installer/](../installer/) は WiX Toolset 5（MS-RL）の SDK 形式のプロジェクトで、`dotnet build` だけでビルドできます。Visual Studio や追加のツールのインストールは不要で、SDK は NuGet から取得します。WiX 6 以降は別の利用規約（OSMF）への同意が必要なため、使っていません。

| 項目 | 内容 |
| --- | --- |
| 画面 | 日本語。開始（名前とインストール先）→ インストールの確認 → 進捗 → 完了（起動方法と「Something を起動する」チェック、既定でオン） |
| インストール先 | `C:\Program Files\Something`（全ユーザー、管理者確認あり。変更不可） |
| ショートカット | スタートメニューの「Something」 |
| 更新 | 同じ UpgradeCode の旧版を削除して置き換える。同じ数値の版を再ビルドした場合も置き換えられる。更新失敗時は旧版の削除もロールバックする |
| アンインストール | Windows の「インストールされているアプリ」から。`%LOCALAPPDATA%\Something` の履歴と設定は残す |

MSI のバージョンは、`Something.App.csproj` の `<Version>` の数値部分です（`1.1.0-beta.1` なら `1.1.0`）。

アプリ系列を識別する `UpgradeCode` は固定し、パッケージごとの `ProductCode` は WiX に生成させます。`ProductCode` が異なっても、別アプリとして併存させる構成ではありません。`MajorUpgrade` は同じバージョン以下の既存製品を検出し、`InstallInitialize` の後、新しいファイルの配置前に削除します。新しい版の導入に失敗した場合は旧版を復元します。より新しい版がある場合のダウングレードは拒否します。履歴と設定はインストーラーの削除対象に含めません。

> [!IMPORTANT]
> UpgradeCode は全バージョン共通の固定値です。変更すると、旧版を置き換えられなくなります。

画面は WiX の UI 拡張の標準ダイアログで構成し、文言は [Strings.ja-JP.wxl](../installer/Strings.ja-JP.wxl) で上書きします。各ダイアログの文字の領域は大きさが決まっているため、文言は収まる長さにします。文言中の角かっこはプロパティの参照として置き換えられるため、文字としては使いません。チェックボックスの文字ではプロパティが置き換えられないため、製品名をそのまま書きます。開始・完了画面の左側と、それ以外の画面の上部の画像（`Dialog.bmp` / `Banner.bmp`）は、[generate-installer-images.ps1](../scripts/generate-installer-images.ps1) でアプリのアイコンから作成し、コミットしています。ライセンスの同意画面は設けません。MIT ライセンスは同意を求めるものではなく、`LICENSE` と `THIRD-PARTY-NOTICES.md` はインストール先に配置します。完了画面からの起動は管理者権限のないインストーラー画面から行い、アプリ自身が UAC の確認を表示します。

現在のユーザーだけにインストールする方法は用意していません。インストール先がユーザーの書き込めるフォルダーになり、管理者権限で動くアプリの DLL を差し替えられてしまうためです。

WinUI の DLL が宣言する言語の数が多いことなどで出る既知の ICE 検証の結果は、理由を記録したうえで [Something.Installer.wixproj](../installer/Something.Installer.wixproj) で除外しています。

### 単一 exe にしない理由

アプリは管理者権限で動作します。自己展開型の単一 exe は、起動時に `%TEMP%/.net/` へ DLL を展開して読み込み、以前に展開したものがあれば再利用します。この場所は、同じユーザーの管理者権限のないプロセスからも書き換えられるため、差し替えられた DLL が管理者権限で実行されるおそれがあります。MSI は管理者だけが書き込める `Program Files` にすべてのファイルを置くため、この問題は起きません。同じ理由で、ZIP を展開して使う配布や、ユーザー単位のインストールも行いません。

## GitHub Release

[release.yml](../.github/workflows/release.yml) は、GitHub の Actions 画面から手動で実行します。選択したコミットの `Something.App.csproj` から `<Version>` を読み取り、`1.0.0` なら `v1.0.0` のタグとリリースを自動で作成します。タグやバージョンを手で入力する必要はありません。

1. 必要ならバージョンを更新し、変更をコミットして GitHub へ push する。初回は、ワークフローをデフォルトブランチに含めておく。
2. GitHub の **Actions → Release → Run workflow** を開く。
3. 発行するブランチを選択して **Run workflow** を押す。

Windows ランナー上で、ハッシュ検証付きの既存のセットアップを使って、版を固定した SDK とネイティブランタイムを準備し、`scripts/publish-app.ps1` で MSI を作ります。このワークフローではテストを実行しません。`Something-v1.0.0-win-x64.msi` を、自動生成したリリースノートとともに GitHub Release に添付します。ライセンス通知は MSI に含まれ、アプリと一緒にインストールされます。

`1.1.0-beta.1` のようなバージョンは、プレリリースとして公開します。公開済みのバージョンは作り直さず、同じバージョンのリリースがすでに公開されていればワークフローを失敗させます。既存のタグが別のコミットを指している場合も、タグを付け替えずに失敗させます。

> [!IMPORTANT]
> 内容を変える場合は、バージョンを上げてください。

GitHub 上のタグやリリースは、ビルドと MSI の作成が成功してから変更します。リリースは下書きとして作成し、アップロードに成功してから公開します。途中で失敗した場合は下書きが残ることがあり、同じコミットで再実行したときに限り、その下書きを更新して公開できます。

MSI の SHA-256 を記載した `SHA256SUMS.txt` を同じリリースに添付し、MSI にはビルドの来歴（GitHub Artifact Attestations）を付けます。利用者は次のどちらかの方法で、配布物がこのリポジトリのワークフローで作られたことを確認できます。

```powershell
Get-FileHash .\Something-v1.0.0-win-x64.msi -Algorithm SHA256
gh attestation verify .\Something-v1.0.0-win-x64.msi --repo mug-lab-3/something
```

> [!NOTE]
> MSI はまだコード署名していないため、実行すると Windows SmartScreen が発行元不明の警告を表示します。

Actions のアーティファクトと依存関係のキャッシュは作成せず、削除されるまで残る保存領域を作りません。Release に添付した MSI は、配布物として残します。実行ログは、Actions の通常の保持設定に従います。

認証には組み込みの `GITHUB_TOKEN` と `contents: write` を使い、個人用のトークンは不要です。ビルドの来歴の署名には `id-token: write` と `attestations: write` を使います。リポジトリでは Actions の SHA 固定を必須にしているため、アクションを追加・更新するときはコミットの SHA で指定してください。リポジトリや組織のポリシーで、Actions による書き込みを許可しておく必要があります。コマンドの仕様は [GitHub CLI: release create](https://cli.github.com/manual/gh_release_create) を参照してください。

## ランタイム

`Properties/PublishProfiles/Folder.pubxml` は、.NET と Windows App SDK を自己完結で含め、トリミングせずにフォルダーへ発行します。WinUI、DuckDB、PRI / XBF、ライセンス通知も含めます。WinUI の PRI / XBF は .NET の発行処理では自動で含まれないため、プロファイルで追加しています。

配布先の PC に .NET や Windows App SDK を別途インストールする必要はありませんが、同梱したランタイムの修正を取り込むには発行し直す必要があります。履歴と設定は、インストール先ではなくユーザーごとの保存先に置きます。

## Windows と権限

現在の発行対象は Windows x64 で、ターゲットは Windows 10 SDK 26100、プロジェクトの最小バージョンは 17763 です。この指定は、すべての Windows のバージョンで動作を検証したことを意味するものではありません。

マニフェストでは `requireAdministrator` と `PerMonitorV2` を指定しています。起動するたびに UAC の確認が表示されます。デモとプレビューも同じ exe なので、管理者権限の要求も共通です。

## 依存の管理

WinUI と DWrite のコンポーネントパッケージを参照し、NuGet のロックで推移的な依存関係も固定します。WiX の SDK は、プロジェクトの `Sdk` 属性で版を固定します。発行後にネイティブ DLL や多言語のリソースを手作業で削って構成することはしません。

`LICENSE` と `THIRD-PARTY-NOTICES.md` は、インストール先に含めます。パッケージを追加・更新した場合は、推移的な依存関係やネイティブライブラリも含めて通知を見直します。テストだけで使う依存関係は、アプリと一緒に配布する依存関係と区別します。コード署名と MSIX での発行は、現在は行っていません。

## 発行後の確認

MSI を管理用インストール（`msiexec /a <msi> /qn TARGETDIR=<空のフォルダー>`）で展開すると、`PFiles64\Something` の構成が発行フォルダーと一致することを確認できます。この方法では、システムへの登録は行われません。実際にインストールしたときは、起動と再起動、PRI / XBF の読み込み、デモでの履歴の保存と再表示、アンインストール後も履歴が残ることを確認します。実際の履歴やファイルを使う確認とは、分けて行います。

参照: [WinUI アプリのパッケージなし配布](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)、[WiX Toolset](https://wixtoolset.org/docs/intro/)。
