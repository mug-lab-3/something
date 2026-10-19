# 診断 CLI

## 前提

セットアップは[開発環境](development.md)を参照してください。ライブ診断は Windows のローカル NTFS を対象とし、管理者 PowerShell から実行します。取得ハンドルは読み取り専用です。例の `C:` は実際に調べるドライブへ変更します。

```powershell
dotnet build tools/Something.LiveInspect -c Release
```

以下の `--no-build` は、このビルドが完了している場合に使います。診断出力にはローカル情報が含まれ得るため、公開前に確認してください。MFT の生ダンプ・履歴 DB・実ファイル名のログはリポジトリへ含めません。

## 少数レコード

```powershell
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: 0 32
```

開始番号 0 から 32 スロットを確認します。個別表示の既定は開始 0・16 スロット、件数の上限は 4096 です。MFT スロットとファイル数は同じではありません。拡張レコード、未使用・返却なし、管理レコードを含みます。

ファイル一つの結合に必要な取得数の上限は別の値で、既定 1024 です。必要な場合だけ末尾に `--max-records <正の整数>` を指定します。上限超過は容量不明であり、ゼロサイズではありません。

## Windows API とのサイズ照合

```powershell
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --file "$PWD\README.md"
```

指定したファイルの参照を取得し、NTFS で組み立てた無名 DATA の論理・割り当て容量を Windows API と照合します。パスは対象ドライブの絶対パスを指定します。

```powershell
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --record-file 100
```

参照番号からファイルを開いて照合する形式です。番号 100 は例で、実在を保証しません。`allocation-source` は Windows 側の取得元を示し、通常は `FileStandardInfo`、圧縮・スパース等では `FileCompressionInfo` を使います。名前集合の診断も表示します。名前付きストリーム全体の独立照合ではありません。

どちらも楽観的な二つの観測です。不一致は競合・再利用・未対応条件・実装不具合を調査する入口で、直ちにディスク破損を示すものではありません。

## 全件走査

```powershell
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --scan
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --scan 10000
```

上は開始時の観測範囲全体、下は最初の 10000 スロットを走査します。5 秒ごとの進捗と最後の要約だけを出し、Ctrl+C に対応します。スキャンデータは保存しません。

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

## フォルダ集計

```powershell
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --top5
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --top5 --depth 1
dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --top5 --leaf
```

一時 DB で集計し、ルートを除く上位五フォルダを表示します。`--depth 1` はルート直下、`--leaf` は最下位フォルダです。`--memory-db` / `--file-db` はこの診断の DB 選択で、通常アプリの共有履歴方式を切り替えるものではありません。

この経路はバッファ・集計・保存・一覧取得を含みます。`--scan` 単独の時間と混同しません。フォルダの合計・管理 DATA・所属不明・保存則は[容量集計](capacity-accounting.md)を参照してください。結果は履歴として保持しません。

## オフライン解析

```powershell
dotnet run --project tools/Something.MftInspect -c Release -- 'artifacts/sample.mft' 1024
```

自分で用意した raw `$MFT` ストリームを、指定レコード長で解析します。dd のディスク全体やパーティションイメージをそのまま指定する形式ではありません。出力の `deleted` は InUse の立っていないレコード数で、今回の走査でファイルを削除した意味ではありません。

正常時 0、不正レコードがある場合 1、引数・I/O エラーは 2、キャンセルは 130 を返します。入力ファイルの所有者は呼び出し側で、検査は読み取り専用です。
