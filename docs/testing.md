# テストと検証の境界

## 実行

xUnit.net v3 と Microsoft.Testing.Platform を使います。セットアップのあと、リポジトリのルートで実行します。

```powershell
dotnet test --solution Something.slnx
```

```powershell
dotnet test --solution Something.slnx --coverage --coverage-output-format cobertura --coverage-settings tests/coverage.runsettings --results-directory artifacts/test-results
```

画面の文言を変えたときは、resw との対応を監査します（[表示文言のリソース](localization/README.md)）。

```powershell
dotnet run --project tools/Something.LocalizationAudit -- check .
```

パーサーのカバレッジの目標は、行 95%・分岐 90% です。統計と保存層は分けて計測し、数値だけで検証が十分かどうかを判断しません。テストの件数やカバレッジの実測値は、対象のコミットと実行条件とあわせて扱います。

## 検証対象

| 領域 | 主な確認 |
| --- | --- |
| 純粋な NTFS 解析 | 正常なバイナリ、境界、長さ・参照の矛盾、オーバーフロー、終端、fixup |
| ライブ取得の契約 | 部分読み取り、返却番号、変更・削除・再利用、再試行、キャンセル、共有バッファ |
| 結合・容量 | 拡張レコード、VCN、mapping pairs、圧縮・スパース、ボリューム境界、未対応状態 |
| 統計 | ハードリンク・DOS 名、階層、所属不明、保存則、未解決容量 |
| 保存・履歴 | 型の全域、UTF-16、共有状態、ロールバック、リース、保持境界、比較、OOM 回復 |
| ファイル操作 | 確認キャンセル、消失・置換、パスと属性の保護、名前単位の状態 |
| UI | 合成ドライブ、一覧とツリー、非同期切り替え、ダイアログ、通知、アニメーション |

通常の単体テストは、管理者権限、実際のボリューム、外部サービスを必要としません。非同期のテストには xUnit のキャンセルトークンを渡し、AAA と FIRST の原則に沿って、互いに独立して再現できるようにします。

固定した seed による変異入力と、ファジングに相当する検査も行いますが、すべてのバイト列を網羅したとは見なしません。MFT と DiscUtils はテスト・比較・公開データの抽出に使うもので、製品のパーサーの代わりではありません。ほかの実装と一致したことだけで、仕様に適合しているとは見なしません。

## 公開コーパス

通常の実行では外部のデータを必要とせず、該当するケースはスキップします。[公開コーパス](public-corpus.md)のスクリプトを使うと、固定したハッシュと期待値による追加の検査を実行できます。ディスクイメージはマウントせず、製品のパーサーとは別の実装で MFT を抽出します。

## デモと画面確認

```powershell
dotnet build src/Something.App -c Release
.\run-demo.bat
.\run-animation-preview.bat
.\run-update-preview.bat
```

デモは仮想ディスクをマウントするのではなく、メモリ上で合成した観測データを使います。3 つのドライブを表示し、スキャンのたびに「初期状態」「サイズ増減・ファイル追加」「ファイル削除・移動」を繰り返します。`run-demo.bat` は `artifacts/demo-history` に専用の履歴を残します。実際のファイルや通常の設定は読み書きしません。

`--demo-static` を付けると、データが変化しなくなります。`--language en-US`（または `ja-JP`、`system`）を付けると、デモの表示言語を指定できます。画面テストはどちらの言語でも通るように書き、検証に使う文言は resw から読むか、言語ごとに用意します。画面テスト用の `--smoke-*` 引数は、`Something.App` の各 `*.Smoke.cs` に実装しています。

画面のスモークテストは、再スキャンしてもデモのデータが変わらないことを前提に比較するため、`--demo-static` が必須です（指定しないと開始時に失敗します）。結果は `<出力先>.result` に書き出し、成功したときは終了コード 0 で終了します。アプリは管理者権限を必要とするため、起動時に UAC の確認が表示されます。

```powershell
$exe = 'src\Something.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\Something.App.exe'
Start-Process $exe -Wait -ArgumentList '--demo','--demo-static','--demo-history','artifacts\smoke\history',
    '--smoke-test','artifacts\smoke\run','--smoke-comparison','--smoke-history-management','--smoke-scan-animation'
Get-Content artifacts\smoke\run.result
```

`--smoke-search` は検索タブ（条件画面、ゼロ件・1 件・全件の検索、ツリーと一覧の連動、戻る・進む・上へ、条件を変えたときの結果の破棄、ドライブを切り替えたときの破棄、グラフのウィンドウが 1 スキャンにつき 1 点で開くこと）を、`--smoke-comparison` は比較と読み込み表示の時間の規則を、`--smoke-history-management` は履歴削除・最適化のアニメーションを、`--smoke-scan-animation` はスキャンのアニメーションを、`--smoke-export` はエクスポート（表示中の一覧とフォルダーの集計、6 つの形式すべての書き出し、ダイアログ）を、`--smoke-tree-follow` は一覧とツリーの連動（設定画面での切り替え、2 階層下までの展開と選択、「戻る」への追従、オフのときに展開しないこと）を、`--smoke-update` は更新の確認（合成した応答による新しい版の通知、要求の形式、通知の優先順位、最新・失敗・不正な応答で通知しないこと、設定画面でのオフ）を追加で確認します。更新の確認のスモークテストはネットワークに接続しません。`--smoke-clipboard` は実際にクリップボードへ書き込むため、実行すると中身が置き換わります（キーやマウスの入力は送らず、ウィンドウを前面に出すこともしません）。`--smoke-navigation-input` は内部のナビゲーション処理を直接呼ぶだけで、実際の入力は送りません。`--smoke-language` は `--language en-US` と組み合わせて使い、英語の文言、設定画面での日本語の選択と再起動の案内を確認したあと、実際に再起動します。再起動したプロセスは日本語の文言と同じ履歴での起動を確認し、`.language.result` を書き出します。最初のプロセスの終了後も、再起動したプロセスの終了と結果ファイルを待ってください。履歴削除・最適化の確認には、エクスポートのアニメーションも含まれます。比較と検索の確認には `--demo-history` が必要です。

UAC の確認なしで自動実行するには、管理者権限を求めない（`asInvoker`）マニフェストを使い、別の出力先にビルドして起動します。配布用の `app.manifest` は変更しません。

```powershell
$manifest = (Resolve-Path 'artifacts').Path + '\smoke-build.manifest'
(Get-Content src\Something.App\app.manifest -Raw).Replace('requireAdministrator','asInvoker') | Set-Content $manifest -Encoding utf8
$root = (Resolve-Path 'artifacts').Path
dotnet build src\Something.App\Something.App.csproj -c Release "-p:ApplicationManifest=$manifest" "-p:OutDir=$root\smoke-build\bin\" "-p:IntermediateOutputPath=$root\smoke-build\obj\"
```

出力先（`OutDir`）だけでなく、中間ファイルの出力先（`IntermediateOutputPath`）も分けてください。分けないと、`asInvoker` のアプリホストが `obj\Release` に残り、あとの通常のビルドがそれを再利用して、管理者権限を要求しない exe ができます。その exe ではボリュームを開けず、履歴が読み込めません（Win32 エラー 5）。通常の `bin\Release` の exe に `requireAdministrator` が入っているかは、バイト列を検索して確かめられます。

画面のスモークテストは `--smoke-test <出力先>` の出力先に、画面の画像（`<出力先>`）を書き出します。`--smoke-search` は、条件画面（`.conditions.png`）とグラフのウィンドウ（`.graph.png`）の画像も書き出します。

> [!WARNING]
> 管理者権限を求めない検証用のマニフェストで別の出力先にビルドする場合は、配布用のマニフェストを上書きしないでください。

## 明示的なライブ確認

実際のボリュームの診断は、[診断 CLI](diagnostics.md) で実行します。合成テストの成功を、実際のボリュームに対応できている証拠とは見なさず、Windows API との独立した照合や、圧縮・スパースなどの条件ごとの照合を行います。ライブ診断は読み取り専用で、ファイルを作成・削除することはありません。

> [!NOTE]
> Windows Shell のごみ箱を使う統合テストは、`SOMETHING_TEST_RECYCLE=1` を明示的に設定した場合だけ実行されます。`FileRecycleTests` は、テスト自身が作った一意な一時ファイルだけを対象にします。既存のファイルで動作を確かめるための設定ではありません。

固定イメージ、疑似的な変更、実際の API による確認は、それぞれ見つけられる不具合が異なります。ある時点の厳密なスナップショット、すべての NTFS 形式への対応、媒体の故障や電源断への耐性は、このテスト構成からは保証できません。
