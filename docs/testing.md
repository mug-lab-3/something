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
```

デモは仮想ディスクをマウントするのではなく、メモリ上で合成した観測データを使います。3 つのドライブを表示し、スキャンのたびに「初期状態」「サイズ増減・ファイル追加」「ファイル削除・移動」を繰り返します。`run-demo.bat` は `artifacts/demo-history` に専用の履歴を残します。実際のファイルや通常の設定は読み書きしません。

`--demo-static` を付けると、データが変化しなくなります。画面テスト用の `--smoke-*` 引数は、`Something.App` の各 `*.Smoke.cs` に実装しています。

画面のスモークテストは、再スキャンしてもデモのデータが変わらないことを前提に比較するため、`--demo-static` が必須です（指定しないと開始時に失敗します）。結果は `<出力先>.result` に書き出し、成功したときは終了コード 0 で終了します。アプリは管理者権限を必要とするため、起動時に UAC の確認が表示されます。

```powershell
$exe = 'src\Something.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\Something.App.exe'
Start-Process $exe -Wait -ArgumentList '--demo','--demo-static','--demo-history','artifacts\smoke\history',
    '--smoke-test','artifacts\smoke\run','--smoke-comparison','--smoke-history-management','--smoke-scan-animation'
Get-Content artifacts\smoke\run.result
```

`--smoke-comparison` は比較と読み込み表示の時間の規則を、`--smoke-history-management` は履歴削除・最適化のアニメーションを、`--smoke-scan-animation` はスキャンのアニメーションを追加で確認します。比較の確認には `--demo-history` が必要です。

> [!WARNING]
> 管理者権限を求めない検証用のマニフェストで別の出力先にビルドする場合は、配布用のマニフェストを上書きしないでください。

## 明示的なライブ確認

実際のボリュームの診断は、[診断 CLI](diagnostics.md) で実行します。合成テストの成功を、実際のボリュームに対応できている証拠とは見なさず、Windows API との独立した照合や、圧縮・スパースなどの条件ごとの照合を行います。ライブ診断は読み取り専用で、ファイルを作成・削除することはありません。

> [!NOTE]
> Windows Shell のごみ箱を使う統合テストは、`SOMETHING_TEST_RECYCLE=1` を明示的に設定した場合だけ実行されます。`FileRecycleTests` は、テスト自身が作った一意な一時ファイルだけを対象にします。既存のファイルで動作を確かめるための設定ではありません。

固定イメージ、疑似的な変更、実際の API による確認は、それぞれ見つけられる不具合が異なります。ある時点の厳密なスナップショット、すべての NTFS 形式への対応、媒体の故障や電源断への耐性は、このテスト構成からは保証できません。
