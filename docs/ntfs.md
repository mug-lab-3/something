# NTFS の取得と解析

## Windows 取得層

[`WindowsMftRecordSource`](../src/Something.Windows/WindowsMftRecordSource.cs) は `IMftScanSource`、`IAttributeListValueSource`、`IVolumeClusterSource` を実装します。`NativeNtfsVolumeIo` はボリュームを `GENERIC_READ` で開き、NTFS 情報と FILE レコードを取得します。ライブ取得には管理者権限が必要です。

`FSCTL_GET_NTFS_FILE_RECORD` は、要求した番号以下の使用中レコードを返すことがあります。全件走査では、実際に返された番号を検証しながら降順に進みます。特定の番号を取得するときに、別の番号のレコードをそのファイルとして扱うことはありません。返されなかった範囲を、履歴上の削除を確定する根拠にはしません。

非常駐 `ATTRIBUTE_LIST` が必要な場合だけ、その mapping pairs に対応するクラスタを読みます。整列とネイティブハンドルは Windows 層が担当し、範囲検証と取得計画は純粋な解析側に置きます。キャンセルは同期的なネイティブ呼び出しの前後で確認し、呼び出しを途中で強制的に中断することはしません。

## 入力形式を明示する

| `MftRecordEncoding` | 入力の契約 |
| --- | --- |
| `RawProtected` | 生のディスクデータ。512 バイト刻みの update sequence を検証して復元する |
| `FixupsRestored` | fixup 復元済みコピー。保存された末尾値との厳格な一致を検査する |
| `WindowsDecoded` | Windows API が返したデコード済みレコード。raw 入力の保存末尾値との一致を要求しない |

形式は取得元が指定します。raw の検査に失敗した入力を、別の形式に自動で切り替えて受け入れることはしません。Windows API が返したレコードと raw イメージで、検査の契約を混同しないことが重要です。

## パーサーと結合器

| 型 | 処理 |
| --- | --- |
| `MftRecordParser` | FILE ヘッダー、update sequence、属性境界、終端を検査 |
| `AttributeValueParser` | FILE_NAME、STANDARD_INFORMATION、ATTRIBUTE_LIST 等の値を解読 |
| `DataRunParser` | 符号付き相対 LCN、hole、終端、宣言 VCN 範囲を検査 |
| `MftFileAssembler` | base と extension の識別・参照・VCN・容量を検証し、完全なファイルを構成 |
| `AttributeListValueReader` | 非常駐リストの取得範囲を I/O 前に検証し、所有する値バッファを構築 |

長さ・オフセット・算術のオーバーフロー・名前領域・参照シーケンス・重複レコードを検証します。FILE_NAME のキャッシュ済みサイズを DATA 容量の正本にしません。DOS 別名と名前付き DATA は捨てずに保持します。

非常駐 DATA では LowestVcn が 0 の extent のサイズ欄を使用し、継続 extent のサイズ欄を合計しません。VCN の連続性と全物理 run の合計を照合します。ライブ経路はクラスタサイズと総クラスタ数を渡し、物理 LCN がボリューム範囲内にあることも検証します。総クラスタ数を渡さない単体の解析では、媒体上の範囲まで検証したとは見なしません。

STANDARD_INFORMATION は常駐・無名の 48 / 72 バイト形式に対応します。本体の属性は `uint?` で保持し、欠落や曖昧さを通常属性へ置き換えません。FILE_NAME の属性による補完もしません。

## 圧縮・スパースの解釈

ストリームの設定と圧縮単位は先頭 extent を基準とします。圧縮単位は 1 バイトで読み、後続の予約領域とは分けて保持します。継続 extent に先頭と同じ圧縮単位を要求しません。

容量解析では Sparse ビットの差、および先頭の圧縮形式が 1 の場合の継続 extent の圧縮ビット省略を限定的に許容します。逆方向の圧縮差、別形式、暗号化・未知ビットの差は `Unsupported` とし、部分容量を返しません。元の属性を書き換えず、VCN・物理容量・参照・ボリューム境界の検査は維持します。

この解釈には Microsoft の公開構造に加え、Linux NTFS3 v6.12 と ntfs-3g 2022.10.3 の先頭設定・継続 mapping の扱い、および Windows API の観測からの推論が含まれます。Microsoft による全形式の保証とは区別します。圧縮本体の解凍やフレーム検証は行いません。

## 読み取り中の変更

`LiveMftRecordReader` は所有するバッファへコピーして解析し、再取得したバイト列と比較します。`LiveMftFileReader` は base・extension・外部 ATTRIBUTE_LIST を結合した後、関係する観測を再検証します。既定の再試行は 3 回です。

| 状態 | 解釈 |
| --- | --- |
| `ObservedUnchanged` | 再観測が一致した。時点スナップショットの保証ではない |
| `Missing` / `NotInUse` | 今回取得できない、または未使用。履歴上の削除を確定しない |
| `Replaced` | 期待したシーケンスと異なる。別のファイルとして扱う |
| `ChangedDuringRead` | 再試行しても安定しなかった |
| `InvalidRecord` / `InvalidAssembly` | レコード構造、または結合条件を検証できない |
| `Unsupported` / `LimitExceeded` | 未対応形式、またはアプリの取得上限超過 |

I/O・権限のエラーとキャンセルは、例外として呼び出し元に伝えます。未解決の容量をゼロに置き換えることはしません。再観測の直後の変更や ABA 問題は検出できず、複数のレコードを原子的に取得するものでもありません。

## 対応範囲と資源上限

- FILE レコード長は 512 バイトから 64 KiB の 2 の冪。fixup の stride は 512 バイト。
- 常駐・非常駐 DATA、名前付きストリーム、複数 FILE_NAME、拡張レコードに対応。
- 1 ファイルの取得は base を含め既定 1024 レコード。診断 CLI は正の整数で変更可能。
- 非常駐 ATTRIBUTE_LIST は最大 1 MiB。base 内で完結する無名・非圧縮・非スパースの run 構成に対応。拡張レコード内のリストは未対応。
- `$BadClus` の専用容量会計、WOF・重複排除等を含む物理使用量の完全な帰属、索引を含む管理属性の完全な容量会計は対象外。

これらの上限は NTFS の仕様上の最大値ではなく、アプリの資源の上限です。上限に達したファイルを破損と断定せず、不明の状態を返します。

## 根拠と検証

公開されている仕様、OSS 実装から得た知見、アプリの資源の制限、実際のボリュームでの観測は、区別して扱います。形式上の事実を文書にまとめ、独立に用意した期待値でテストします。参照実装のコード・コメント・テストを転記したり、そのまま移植したりはしません。

- [Microsoft: FILE_RECORD_SEGMENT_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/file-record-segment-header)
- [Microsoft: MULTI_SECTOR_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header)
- [Microsoft: ATTRIBUTE_RECORD_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header)
- [Microsoft: FSCTL_GET_NTFS_FILE_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)
- [Linux NTFS3 v6.12: ntfs.h](https://github.com/torvalds/linux/blob/v6.12/fs/ntfs3/ntfs.h) / [attrib.c](https://github.com/torvalds/linux/blob/v6.12/fs/ntfs3/attrib.c)
- [ntfs-3g 2022.10.3: layout.h](https://github.com/tuxera/ntfs-3g/blob/2022.10.3/include/ntfs-3g/layout.h) / [attrib.c](https://github.com/tuxera/ntfs-3g/blob/2022.10.3/libntfs-3g/attrib.c)

合成入力・固定イメージ・Windows API との照合は、互いを補う検証です。1 つの照合や合計値の一致だけで、すべての NTFS 形式やすべてのファイルが正しいとは保証できません。[テスト方針](testing.md)も参照してください。
