# 公開 NTFS コーパス

## 出典と保存範囲

追加検証には National Institute of Standards and Technology (NIST) の [CFReDS DFR Test Images](https://cfreds-archive.nist.gov/dfr-test-images.html) を使用します。削除ファイル復元ツール向けの、操作と配置を制御した dd イメージです。取得対象・URL・期待値・ハッシュは [`nist-corpus.json`](../tests/fixtures/nist-corpus.json) が正本です。

NIST の[利用条件](https://www.nist.gov/copyrights-disclaimers)は、著作権表示のある資料や個別条件を区別し、出典の明記を求めています。外部データへ本プロジェクトのライセンスを適用しません。データそのものを再配布する構成にはせず、原本・展開物・抽出 MFT はローカルの `.tools` に保存します。

Git に含めるのはマニフェスト・検証コード・数値期待値です。NIST の配置説明は [Image Creation and Layout](https://cfreds-archive.nist.gov/dfr-images/setup-july-10-2012.pdf) を参照してください。

## 再実行

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test-public-corpus.ps1 -Download
```

取得済みデータの検証だけを行う場合:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test-public-corpus.ps1
```

約 7 GiB の展開領域を使います。スクリプトはハッシュ照合・抽出・ビルド・テストを行い、`SOMETHING_NIST_CORPUS` をテストに渡します。出力は新規作成し、途中失敗したものを成功結果として再利用しません。既存データを自動削除しないため、中断時はその出力を確認してから再実行します。

## 固定した検査条件

| ケース | 主な条件 | クラスタバイト数 | 有効 | 未使用 | ゼロ領域 | 構造エラー |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 01 | 非断片化 | 4096 | 29 | 38 | 0 | 0 |
| 02 | 二断片 | 4096 | 30 | 9 | 25 | 0 |
| 03 | 多断片 | 512 | 32 | 1 | 37 | 0 |
| 04 | 非 ASCII 名 | 512 | 39 | 12 | 37 | 0 |
| 11-MFT | 常駐ディレクトリ | 8192 | 35 | 4 | 25 | 0 |
| 11-COMPRESSED | 圧縮 | 1024 | 37 | 4 | 23 | 0 |
| 17 | 深いパス | 512 | 117 | 0 | 37 | 0 |

全ケースのレコード長は 1024 バイト、合計 571 スロットです。これらはマニフェストに固定したローカル検査の期待値で、NIST が保証する結果ではありません。「未使用」はヘッダー上の InUse がオフの意味で、削除履歴の確定数ではありません。

圧縮データ・展開イメージ・抽出 MFT の SHA-256 はローカル算出値で、公開元の署名やチェックサムによる認証ではありません。再取得時の変更検出と再現性に使います。

## 独立性と限界

SharpCompress で展開し、DiscUtils でパーティションと MFT ストリームを解決します。Something 自身の解析結果で抽出位置を決めないことで、同じ誤りを共有する可能性を減らします。マウントや管理者権限、実ディスクへの書き込みは不要です。検証用ツールの依存を製品の NTFS パーサーへ持ち込みません。

全スロットの構造と、ユーザーファイルの結合・容量を検査します。ただし予約メタデータの専用容量会計、外部 ATTRIBUTE_LIST の全形式、現在の Windows で変更が続くライブ取得の網羅的検証ではありません。圧縮ビット省略の互換条件など、コーパスに独立した期待例のない形式は別の検証が必要です。

コーパス成功や比較ライブラリとの一致だけで全 NTFS 形式を保証しません。データやテスト用ツールを別途配布する場合は、その時点の個別条件と依存の許諾表示を確認してください。
