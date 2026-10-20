# 診断 CLI

## 前提

セットアップは[開発環境](development.md)を参照してください。ライブ診断は Windows のローカル NTFS が対象で、管理者として開いた PowerShell から実行します。ボリュームは読み取り専用で開きます。例の `C:` は、実際に調べるドライブに置き換えてください。

```powershell
dotnet build tools/Something.LiveInspect -c Release
```

以降の例の `--no-build` は、このビルドが済んでいることを前提にしています。

> [!CAUTION]
> 診断の出力には PC 固有の情報が含まれることがあるため、公開する前に確認してください。MFT の生ダンプ、履歴 DB、実際のファイル名を含むログは、リポジトリに含めません。

## 少数レコード

```powershell
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: 0 32
```

番号 0 から 32 スロット分を確認します。省略時は番号 0 から 16 スロットで、指定できる件数の上限は 4096 です。MFT のスロット数とファイル数は同じではなく、スロットには拡張レコード、未使用や返却されなかったもの、管理用のレコードも含まれます。

1 つのファイルを組み立てるために取得するレコード数の上限は別の値で、既定は 1024 です。必要な場合だけ、末尾に `--max-records <正の整数>` を指定します。上限を超えたファイルは容量が不明として扱い、ゼロにはしません。

## Windows API とのサイズ照合

```powershell
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --file "$PWD\README.md"
```

指定したファイルの参照番号を取得し、NTFS の情報から組み立てた無名 DATA の論理サイズと割り当てサイズを、Windows API の値と照合します。パスには、対象ドライブ上の絶対パスを指定します。

```powershell
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --record-file 100
```

こちらは参照番号からファイルを開いて照合します。番号 100 は例で、その番号のファイルが存在するとは限りません。`allocation-source` は Windows 側の取得元で、通常は `FileStandardInfo`、圧縮・スパースなどの場合は `FileCompressionInfo` を使います。名前集合の診断も表示します。名前付きストリームまでは独立に照合しません。

どちらも、ある時点で別々に取得した 2 つの観測を比べるものです。不一致は、競合・再利用・未対応の条件・実装の不具合を調べるきっかけであり、すぐにディスクの破損を意味するものではありません。

## 全件走査

```powershell
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --scan
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --scan 10000
```

1 行目は開始時に観測した範囲全体を、2 行目は最初の 10000 スロットを走査します。5 秒ごとの進捗と最後の要約だけを出力し、Ctrl+C で中断できます。スキャンしたデータは保存しません。

| 出力 | 意味 |
| --- | --- |
| `observed-slots` / `traversed-slots` | 開始時に観測した範囲と、その走査量 |
| `candidates` | 取得した候補レコード数。拡張レコード等を含む |
| `slots-not-returned` | API が返さなかった範囲。削除確定数ではない |
| `Resolved-DATA-only` | 解決できた全 DATA の合計。未解決と他の管理属性は含まない |
| `Resolved-regular-files-DATA` | 通常ファイルに限定した DATA |
| `Volume-at-open` | 開いた時点の Windows の使用済み容量と DATA 合計の比較 |
| `native-read-time` | ネイティブ読み取りに費やした時間 |
| `record-requests` / `raw-reads` | レコード要求数と直接読み取り数 |
| `managed-allocated-during-scan-bytes` | 累積 managed 割り当て量。保持量やピークメモリではない |
| `process-lifetime-peak-working-set-bytes` | プロセス開始以降のピーク working set |

## フォルダー集計

```powershell
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --top5
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --top5 --depth 1
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --top5 --leaf
```

一時的な DB で集計し、ルートを除く上位 5 つのフォルダーを表示します。`--depth 1` はルート直下のフォルダー、`--leaf` は末端のフォルダーが対象です。`--memory-db` / `--file-db` はこの診断で使う DB の選択で、アプリの履歴の保存方式を切り替えるものではありません。

この処理の時間には、バッファの作成・集計・保存・一覧の取得が含まれます。`--scan` だけの時間と混同しないでください。フォルダーの合計、管理用の DATA、所属不明、保存則については[容量集計](capacity-accounting.md)を参照してください。結果は履歴として残しません。

## オフライン解析

```powershell
dotnet run --project tools/Something.MftInspect -c Release -- 'artifacts/sample.mft' 1024
```

自分で用意した raw の `$MFT` ストリームを、指定したレコード長で解析します。dd で取得したディスク全体やパーティションのイメージを、そのまま指定することはできません。出力の `deleted` は InUse フラグが立っていないレコードの数で、走査によってファイルを削除したという意味ではありません。

終了コードは、正常時が 0、不正なレコードがある場合が 1、引数や I/O のエラーが 2、キャンセルが 130 です。入力ファイルは読み取るだけで、変更しません。
