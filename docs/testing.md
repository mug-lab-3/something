# テストと検証の境界

## 実行

xUnit.net v3 と Microsoft.Testing.Platform を使用します。セットアップ後、リポジトリのルートで実行します。

```powershell
dotnet test --solution Something.slnx
```

```powershell
dotnet test --solution Something.slnx --coverage --coverage-output-format cobertura --coverage-settings tests/coverage.runsettings --results-directory artifacts/test-results
```

画面の文言を変更したら、resw との対応を監査します（[表示文言のリソース](localization/README.md)）。

```powershell
dotnet run --project tools/Something.LocalizationAudit -- check .
```

パーサーのカバレッジ目標は行 95%・分岐 90% です。統計・保存層は分けて計測し、数値だけで検証の十分性を判断しません。テスト件数やカバレッジの実測値は、対象コミットと実行条件を伴う結果として扱います。

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

通常の単体テストは管理者権限・ライブボリューム・外部サービスを要求しません。非同期テストは xUnit のキャンセルトークンを渡し、AAA と FIRST の原則に沿って独立・再現可能にします。

固定 seed の変異入力とファジング相当の検査も行いますが、全バイト列の網羅とは扱いません。MFT と DiscUtils はテスト・比較・公開データの抽出用で、製品パーサーの代わりではありません。異なる実装との一致だけで仕様適合を証明しません。

## 公開コーパス

通常実行では外部データを要求せず、該当ケースはスキップします。[公開コーパス](public-corpus.md)のスクリプトを使うと、固定ハッシュと期待値を伴う追加検査を実行できます。ディスクイメージをマウントせず、製品パーサーとは別の実装で MFT を抽出します。

## デモと画面確認

```powershell
dotnet build src/Something.App -c Release
.\run-demo.bat
.\run-animation-preview.bat
```

デモは仮想ディスクのマウントではなく、メモリ上の合成観測です。三つのドライブを表示し、スキャンごとに初期・増減／追加・削除／移動の状態を繰り返します。`run-demo.bat` は `artifacts/demo-history` に専用履歴を残します。通常の実ファイルや通常の設定を読み書きしません。

`--demo-static` を加えると固定データになります。画面テスト用の `--smoke-*` 引数は `Something.App` の各 `*.Smoke.cs` に実装されています。

画面スモークテストは、再スキャンしてもデモのデータが変わらない前提で比較するため、`--demo-static` が必須です（指定しないと開始時に失敗します）。結果は `<出力先>.result` に書き出し、成功時は終了コード 0 で終了します。アプリは管理者権限を要求するため、起動時に UAC の確認が表示されます。

```powershell
$exe = 'src\Something.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\Something.App.exe'
Start-Process $exe -Wait -ArgumentList '--demo','--demo-static','--demo-history','artifacts\smoke\history',
    '--smoke-test','artifacts\smoke\run','--smoke-comparison','--smoke-history-management','--smoke-scan-animation'
Get-Content artifacts\smoke\run.result
```

`--smoke-comparison` は比較と読み込み表示の時間規則、`--smoke-history-management` は履歴削除・最適化のアニメーション、`--smoke-scan-animation` はスキャンのアニメーションを追加で確認します。比較は `--demo-history` が必要です。検証用マニフェストで別出力先へビルドする場合、配布用の昇格マニフェストを上書きしないでください。

## 明示的なライブ確認

実ボリュームの診断は[診断 CLI](diagnostics.md)から実行します。合成テストの成功をライブ対応の証明にはせず、Windows API との独立照合、圧縮・スパース等の条件別照合を行います。ライブ診断は読み取り専用で、実ファイルを作成・削除しません。

Windows Shell のごみ箱統合テストは、`SOMETHING_TEST_RECYCLE=1` を明示設定した場合だけ有効です。`FileRecycleTests` は自分で作った一意な一時ファイルだけを対象にします。既存の利用者ファイルで動作確認するための設定ではありません。

固定イメージ・疑似的な変更・実 API の確認は、それぞれ検出できる不具合が異なります。時点スナップショット、全 NTFS 形式、媒体故障や電源断に対する保証は、このテスト構成からは導けません。
