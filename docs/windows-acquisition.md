# 読み取り専用Windows取得層

## 実装範囲と安全性

`Something.Windows` はボリューム情報と指定番号のMFTレコード取得を担当する。
取得・解析・結合・統計・保存・UIは分離する。`Something.LiveInspect` は少数レコードと
resident ATTRIBUTE_LISTで参照を追跡できるファイルの診断用で、高速な全件走査や完成した容量集計ではない。

ボリュームはGENERIC_READのみ、OPEN_EXISTINGで開く。GENERIC_WRITEは要求しない。
共有モードREAD / WRITE / DELETEは他プロセスの操作を許す設定であり、
Something自身の書き込み権限を付与するものではない。
内部のDeviceIoControlは次の2命令のみを許可し、汎用制御コードAPIは公開しない。

| 制御命令 | 値 | 用途 |
|---|---|---|
| FSCTL_GET_NTFS_VOLUME_DATA | 0x00090064 | ジオメトリとMFT長 |
| FSCTL_GET_NTFS_FILE_RECORD | 0x00090068 | FILEレコード |

WriteFile、修復、ロック、マウント解除、USN Journalの作成・削除、属性変更は呼ばない。
ボリューム入力はドライブ文字（例: C:）に限定し、PhysicalDriveやUNCは拒否する。
ファイル照合用ハンドルはアクセス権0のメタデータ専用。内容は読まず、
GetFileInformationByHandleでサイズ・参照・更新時刻等を観測する。
CLIは名前・パス・rawデータを出力せず、DBや履歴を作成しない。
ファイルAPIの統合テストは自身で作成した一時ファイルだけを使用・削除する。

## 返却データと競合

FSCTL_GET_NTFS_FILE_RECORDは指定番号以下の最初の使用中レコードを返す。
番号を照合し、前のレコードが返された場合はMissingを返す。
別のファイルを指定ファイルとして解析しない。Missingは歴史的な削除の証明ではない。
API失敗はI/Oエラーとして返し、Missingや0バイトへ変換しない。

返却長・番号・レコード長・ジオメトリ・容量演算を検証する。
レコード本体は構造体の12バイト目から始まり、末尾パディングを本体に含めない。
結果は所有するコピーを返し、次の取得で上書きしない。

入力形式はMftRecordEncodingで明示する。rawはRawProtected、Windows取得は
FixupsRestoredとして渡す。後者もFILE署名・USA位置・長さ・復元末尾と保存値の一致を
検証するが、ディスク上のfixupを直接検証したとは主張しない。
形式を自動推測して破損を受け入れない。この入力形式で通常ファイル1件の実機照合が成功した。
他の形式やボリューム条件への対応は別途検証する。

LiveMftRecordReaderが再取得・照合し最大3回で打ち切る。変更・参照再利用・不正構造を
区別し、容量0へ変換しない。照合直後の変化やABAは防げず、時点スナップショットではない。
LiveMftFileReaderがresident ATTRIBUTE_LISTの参照を取得し、結合後にbaseと全拡張レコードを
再取得して、所有する不変の観測バイト列と照合する。変化した場合はファイル全体を最大3回やり直す。
参照番号だけでなくシーケンス番号も照合し、再試行中にbaseが再利用された場合は別ファイルへ切り替えない。
既定でbaseを含め128レコードまで（設定上限256）とし、上限超過を明示して取得を打ち切る。
各レコードの読み取りも最大3回なので、ネイティブ呼び出し数と保持メモリは有限。
全件の高速走査とは別の検証経路として使う。
取得とDisposeは同じロックで直列化し、I/O中にハンドルを閉じない。
ネイティブAPIは同期呼び出しで、ValueTaskが非同期I/Oを意味するわけではない。
将来のUIではワーカースレッドから使う。キャンセルは呼び出し前後で確認するが、
実行中のネイティブ呼び出しやロック待機を即時中断する保証はない。

## CLIの検証手順

通常の開発ターミナルでビルドする。

```powershell
dotnet build Something.slnx
```

実ボリューム検査は管理者権限が必要な環境がある。管理者PowerShellをリポジトリ直下で
開いて明示的に実行する。CLIの自動昇格はしない。
アクセス拒否（Win32 error 5）の場合は現在のプロセスの管理者権限も表示する。
Falseなら通常権限のプロセスであり、管理者アカウントへの所属だけでは足りない。
Trueでも拒否される場合はボリュームのアクセス制限等を別途調査する。

```powershell
# レコード0から16スロット（既定値）
dotnet run --project tools/Something.LiveInspect --no-build -- C:
# 開始番号と件数を指定。上限4096件
dotnet run --project tools/Something.LiveInspect --no-build -- C: 16 32
# READMEを照合。リポジトリがC:以外ならドライブも変更
dotnet run --project tools/Something.LiveInspect --no-build -- C: --file "$PWD\README.md"
```

--fileは同じハンドルの観測前後の参照・サイズ・更新時刻等も比較する。
比較対象は無名DATAの論理サイズで、名前付きストリームは含めない。
割り当て容量は解析値を表示するだけでWindows APIとの照合済みとは扱わない。
フォルダや異なるボリュームへのリンクは比較を拒否する。
不一致は解析バグと断定せず、観測間の変更の可能性も残す。

非resident ATTRIBUTE_LIST、拡張レコード内のATTRIBUTE_LISTはUnsupportedとして報告する。
参照の不足・再利用・不正構造・取得上限超過も未解決とし、部分的な容量を返さない。
resident DATAのAllocatedBytes=0は追加DATAクラスタの割り当てが0という意味で、
MFT等を含むファイルの総占有量ではない。

終了コード: 0は範囲の処理完了または論理サイズ一致、1は未解決または不一致、
2は入力・権限・I/Oエラー、130はキャンセル。範囲検査のMissingは報告するが、
それだけでは異常終了しない。0もボリューム全体の正確さの証明ではない。

## 検証状況

単体テストは権限やライブボリュームを必要としない。返却構造・境界値・固定seed変異、
所有権、キャンセル、例外伝達、解放、復元済み入力を検証する。
ファイル情報APIは専用の一時ファイルのサイズと識別情報を取得して確認した。
開発エージェントの通常権限ではGENERIC_READによる取得はWin32 error 5（アクセス拒否）となった。
2026-10-12、ユーザーが実機のCLIを実行し、通常ファイル1件でMFT由来とWindows API由来の
論理サイズ4818バイトが一致した（match=True）。DATA割り当て容量の解析値は8192バイトだった。
ボリューム情報取得、FixupsRestoredでの解析、参照照合、単一レコードの結合、論理サイズ比較が
この条件で動作した。参照番号・絶対パス・rawデータは検証記録に保存しない。
割り当て容量は独立照合していない。複数レコード、圧縮・スパース・名前付きストリーム、
取得中の変更、他のボリューム条件、高速全件走査は実機未検証。
合成テストやこの1件の成功をライブ対応全体の証明にしない。
residentリストからの拡張取得・結合後の再検証は合成テストで検証済み。複数リンク名、DOS名、
名前付きストリーム、欠損、再利用、結合後の変更、変更継続、共有バッファ、キャンセル、取得上限を検証した。
この拡張経路は実機未検証。次は少数レコードで診断し、非residentリストの取得方式を設計してから、
全件走査の速度測定、集計・保存へ進む。

## 参考資料

- [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)
- [NTFS_VOLUME_DATA_BUFFER](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-ntfs_volume_data_buffer)
- [FSCTL_GET_NTFS_FILE_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)
- [NTFS_FILE_RECORD_OUTPUT_BUFFER](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_file_record_output_buffer)
- [GetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle)
