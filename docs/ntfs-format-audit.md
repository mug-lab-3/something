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
| 全DATA extentのflagsが同じでなければ結合を拒否する | 保守的な実装方針 | 一律のNTFS仕様条件である根拠は確認できていない。extentごとの情報とストリーム全体の設定の分離が必要 |
| 先頭が圧縮／Sparseかつ圧縮単位4、継続が0なら許可する | 先頭extentから設定を取得するOSS実装と実機観測 | 限定的な互換処理。4だけを特別扱いする一般規則が公開仕様から導けたわけではない。モデルを見直す対象 |
| 全物理runのクラスタ数を合計し、先頭の容量欄と照合する | 実装知見、容量検証方針 | 一致は必要な検証だが、flagsや容量欄の解釈が正しいことの独立証明ではない |
| 最大レコード長64 KiB、リスト1 MiB、ファイル128レコード等 | 実装方針: 資源上限 | NTFS仕様上の上限ではない。超過は容量不明として明示する |

## 監査で見つかった追加の確認点

1. MicrosoftのATTRIBUTE_RECORD_HEADERはoffset 34からの6 byteをReservedとして公開しており、
   圧縮単位の詳細をこの公開構造だけから確定できない。ntfs-3gのlayoutはoffset 34を1 byteの
   compression_unit、その後を5 byteの予約領域としている。現パーサーはU16で読んでおり、
   予約領域の1 byteを圧縮単位へ混ぜる可能性がある。フィールド幅と予約領域の扱いを見直す対象。
2. flagsのextent間差をすべて破損相当として扱う根拠は未確立。現在のInvalidAssemblyは
   当実装が結合できなかったことを示すが、形式未対応と確定的な構造不正をより明確に分ける必要がある。
3. 合計が一致しても、個々のDATAの物理LCNが実ボリュームの範囲内にある証明にはならない。
   ATTRIBUTE_LISTのraw取得ではボリューム境界を検証するが、純粋なDATA結合器は
   bytesPerClusterしか受け取らない。ボリューム境界を確認する責務を取得・容量検証層へ明示する必要がある。
4. 容量の独立照合は現状、通常ファイル1件の論理サイズのみ。拡張・圧縮・Sparseの割り当て容量を
   Windows APIや独立した公開コーパスの期待値で照合する必要がある。

## 次の実装の進め方

現物で出たエラーを通すためのflags例外追加や上限引き上げを先に行わない。
元のextentメタデータを保持したまま、ストリーム全体の設定・extentごとのmapping・
容量検証に必要な情報を分けて、フィールドごとの有効範囲を定義する。
未確定の組み合わせは容量不明のまま扱う。

テストは「今回通したい値」だけから生成しない。
公開仕様の境界値、独立した解析器／公開データとの照合、検査用一時ファイルとWindows APIの
独立照合を組み合わせ、どこまでが確認済みかを各規則に対応づける。
本番ボリュームへの書き込み・修復・強制flushは行わない。

## 一次資料

- [Microsoft: MULTI_SECTOR_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header)
- [Microsoft: ATTRIBUTE_RECORD_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header)
- [Microsoft: FSCTL_GET_NTFS_FILE_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)
- [ntfs-3g: layout.h](https://github.com/tuxera/ntfs-3g/blob/master/include/ntfs-3g/layout.h)
- [ntfs-3g: attrib.c](https://github.com/tuxera/ntfs-3g/blob/master/libntfs-3g/attrib.c)

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
