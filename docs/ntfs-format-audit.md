# NTFS解析規則の根拠と未確定事項

2026-10-12時点の設計監査。現在の実装全体がNTFS仕様に適合済み、という宣言ではない。
ライブ検査で正常に解析できた件数や合成テストの成功だけでは、形式の解釈の正しさを証明しない。
以下はコードの判断根拠を区別したもので、実機の個別ファイルの情報やdumpは保存しない。

## 根拠の区分

- **公開仕様**: Microsoftが説明しているフィールドやAPIの契約。
- **実装知見**: ntfs-3g等の実装者が公開する構造・読み取り処理。Microsoftの仕様保証とは区別する。
- **実機観測**: 今回のWindows返却データで確認した挙動。全ボリューム・全形式へ一般化しない。
- **実装方針**: 資源上限や保守的な拒否条件。本来のNTFS形式制約とは区別する。

## 現在の主要な判断

| 判断 | 根拠 | 現状／必要な見直し |
|---|---|---|
| raw FILEの512バイト刻みの全末尾を検証し、検証後に復元する | 公開仕様: MULTI_SECTOR_HEADER | 維持。raw検証失敗を別入力モードで救済しない |
| USAが第1stride末尾より前に収まることを検証する | 公開仕様: MULTI_SECTOR_HEADER | 維持。配列要素数とbyte長を区別する |
| 属性長を8バイト単位とし、レコードの使用領域内で検査する | 公開仕様: ATTRIBUTE_RECORD_HEADER、境界検証方針 | 維持 |
| LowestVcn / HighestVcnがextentの範囲を示す | 公開仕様: ATTRIBUTE_RECORD_HEADER | 維持。VCN不連続を省略値で補わない |
| 非常駐サイズの正本はLowestVcn 0のextentにある | 公開仕様: AllocatedLength / FileSize / ValidDataLength、実装知見 | 維持。継続extentのサイズ欄を合計しない |
| mapping pairsを属性ごとに初期化して解読する | 公開仕様: ATTRIBUTE_RECORD_HEADERの解読手順、実装知見 | 維持。signed LCN差分、hole表現、終端、範囲を検証する |
| FSCTLで返された番号を起点に降順列挙する | 公開仕様: FSCTL_GET_NTFS_FILE_RECORD | 維持。空き範囲を歴史的削除と推定しない |
| Windows返却をrawディスク画像と同じUSA保存値基準で検査しない | 公開仕様の書き込み／復元手順からの推論と実機観測 | WindowsDecodedの取得契約として分離。APIの全条件での挙動保証やディスク整合性の証明とは扱わない |
| 全DATA extentのflagsが同じでなければ結合を拒否する | 保守的な実装方針 | 一律のNTFS仕様条件という根拠は未確立。安定した観測でもUnsupportedを返し、破損とは断定しない |
| 圧縮単位をoffset 34の1 byteとして読み、予約5 byteを別に保持する | NTFS3 v6.12、ntfs-3g 2022.10.3の構造定義 | U16読み取りを撤去。予約byteが非ゼロでも圧縮単位へ混ぜず、入力を変更しない |
| ストリームの圧縮単位は先頭extentだけから取得する | NTFS3 v6.12 attr_is_frame_compressedの明示的な有効範囲、ntfs-3g属性初期化 | 4→0の個別例外と継続値の一致要求を撤去。継続値は元の属性として保持する |
| 全物理runのクラスタ数を合計し、先頭の容量欄と照合する | 実装知見、容量検証方針 | 一致は必要な検証だが、flagsや容量欄の解釈が正しいことの独立証明ではない |
| 最大レコード長64 KiB、リスト1 MiB、ファイル128レコード等 | 実装方針: 資源上限 | NTFS仕様上の上限ではない。超過は容量不明として明示する |

## 監査で見つかった追加の確認点

1. Microsoftの公開構造ではoffset 34以降はReserved。幅と有効範囲は下記の固定版OSSを
   根拠に修正した。Microsoftの公開仕様から圧縮形式全体が確定したとは扱わない。
2. flagsのextent間差の意味はまだ未確定。NtfsAssemblyException.Kindとライブ取得の
   Unsupportedで構造不正と区別する。VCN不連続・容量不一致は引き続きInvalidAssembly。
   どちらの失敗でも容量を返さず、変更の可能性を再取得で検査してから分類する。
3. 合計が一致しても、個々のDATAの物理LCNが実ボリュームの範囲内にある証明にはならない。
   ATTRIBUTE_LISTのraw取得ではボリューム境界を検証するが、純粋なDATA結合器は
   bytesPerClusterしか受け取らない。ボリューム境界を確認する責務を取得・容量検証層へ明示する必要がある。
4. 容量の独立照合は現状、通常ファイル1件の論理サイズのみ。拡張・圧縮・Sparseの割り当て容量を
   Windows APIや独立した公開コーパスの期待値で照合する必要がある。
   --fileにWindowsのFileStandardInfo（通常）／FileCompressionInfo（圧縮・Sparse）を
   使った容量照合を追加した。通常ファイルにも圧縮情報の値を割り当て容量として使った
   初期実装は修正済み。APIのフィールド名やMS-FSAの定義だけでWin32返却を一般化しない。
   同一ハンドルで前後の容量も検査し、API失敗や変化は一致へ変換しない。
   自身の一時ファイルでAPI取得を検証したが、MFTとの圧縮・Sparse容量照合の実機結果はまだ未確認。

## 次の実装の進め方

現物で出たエラーを通すためのflags例外追加や上限引き上げを先に行わない。
元のextentメタデータを保持したまま、ストリーム全体の設定・extentごとのmapping・
容量検証に必要な情報を分けて、フィールドごとの有効範囲を定義する。
未確定の組み合わせは容量不明のまま扱う。

テストは「今回通したい値」だけから生成しない。
公開仕様の境界値、独立した解析器／公開データとの照合、検査用一時ファイルとWindows APIの
独立照合を組み合わせ、どこまでが確認済みかを各規則に対応づける。
本番ボリュームへの書き込み・修復・強制flushは行わない。

## 2026-10-12の実装照合

参照版はLinux v6.12とntfs-3g 2022.10.3。可変のmasterではなく以下のタグ付き資料を基準とする。

| 対象 | 参照箇所 | 確認した事実と採用範囲 |
|---|---|---|
| フィールド幅 | NTFS3 ntfs.h: ATTR_NONRESIDENT、ntfs-3g layout.h: ATTR_RECORD | 両者ともoffset 34は1 byte、35..39は予約5 byte。パーサーは別々の所有する値として保持 |
| 圧縮単位の有効範囲 | NTFS3 attrib.c: attr_is_frame_compressed（1434..1457）、attr_load_runs_vcn（1229..1267） | 圧縮単位はprimary segmentだけが有効。継続のmappingはVCNとmapping pairsから取得。結合後のStorageには先頭値を設定 |
| 初期化 | ntfs-3g attrib.c: ntfs_attr_open、ntfs_attr_init | 最初の属性からサイズと圧縮設定を初期化。圧縮形式の対応制限は実装固有であり、すべてをパーサーの構造制約へ転用しない |

容量解析は圧縮単位を使ったshiftや解凍を行わない。パーサーはbyteの全範囲を保持し、
値を保持できることを、その圧縮形式の解凍対応や構造全体の妥当性保証とは扱わない。
結合器は全extentの物理run合計・論理容量・VCN連続性を検証する。
Storageと元の各NonResidentAttributeを分離し、継続値を先頭値で上書きしない。

テストは予約領域非ゼロ、byte境界、継続値の省略／相違、VCN欠落、容量不整合、
フラグ差の未対応分類、分類前の変更検出を含む。
既存のSHA-256固定NIST公開データ7ケースも再検証する。
このコーパス検証はWindowsの実ボリュームでの割り当て容量照合を代替しない。

## 一次資料

- [Microsoft: MULTI_SECTOR_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header)
- [Microsoft: ATTRIBUTE_RECORD_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header)
- [Microsoft: FSCTL_GET_NTFS_FILE_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)
- [Linux NTFS3 v6.12: ntfs.h](https://github.com/torvalds/linux/blob/v6.12/fs/ntfs3/ntfs.h)
- [Linux NTFS3 v6.12: attrib.c](https://github.com/torvalds/linux/blob/v6.12/fs/ntfs3/attrib.c)
- [ntfs-3g 2022.10.3: layout.h](https://github.com/tuxera/ntfs-3g/blob/2022.10.3/include/ntfs-3g/layout.h)
- [ntfs-3g 2022.10.3: attrib.c](https://github.com/tuxera/ntfs-3g/blob/2022.10.3/libntfs-3g/attrib.c)

## OSS実装の参照方針

Microsoftの公開説明で不足する部分はLinuxのNTFS実装（NTFS3）とntfs-3gを比較する。
これらの実装の挙動をMicrosoftの保証する仕様とは扱わない。

- 参照する版／commit、ファイル、対象フィールド、有効範囲、確認できた事実と推論を記録する。
- コードやコメント、テストを転記せず、C#への逐語的な移植も行わない。
- 形式上の事実を自分の言葉で記述し、その記述と独立した期待値から実装・テストを作る。
- GPLライブラリを製品へ組み込む場合は、この調査方針とは別にライセンス条件を確認する。
- ソースを読んだ担当が実装するため、厳密なクリーンルーム開発を行ったとは主張しない。

参照したという理由だけで製品全体へGPLが適用されるとは扱わないが、
著作権で保護される表現の流用と、形式上の事実の確認は区別する。
これは調査手順の方針であり、個別のコードについて法的な適合性を保証するものではない。

- [Linux kernel licensing rules](https://docs.kernel.org/process/license-rules.html)
- [GNU GPL FAQ](https://www.gnu.org/licenses/gpl-faq.en.html)
- [U.S. Copyright Office: What Does Copyright Protect?](https://www.copyright.gov/help/faq/faq-protect.html)
