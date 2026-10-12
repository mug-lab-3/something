# MFT解析の境界と整合性

現在の規則には公開仕様から確認できるものと、実機観測に基づく限定的な互換処理が混在しています。
根拠・未確定事項・見直し対象を [ntfs-format-audit.md](ntfs-format-audit.md) に整理しました。
現状をNTFS仕様への適合確認済みとは扱いません。

## 層を分離する

1. **取得層**: ボリューム情報と少数レコードの読み取り専用取得を実装。実機の通常ファイル1件で論理サイズを照合済み。追加条件の検証、高速全件取得、USNは今後の作業。詳細は [windows-acquisition.md](windows-acquisition.md)。
2. **MftRecordParser**: raw FILEレコード1個を解析。入力を変更せず、更新シーケンスを検査・復元して、所有権の明確な不変モデルを返す。
3. **AttributeValueParser / DataRunParser**: ファイル名、ATTRIBUTE_LIST、符号付きの相対LCN・スパース領域を解読。
4. **MftFileAssembler**: baseレコードと指定された拡張レコードを結合。シーケンス番号、参照、VCNの連続性、ボリュームのクラスタサイズと容量の一致を検査。
5. **統計層（今後実装）**: ハードリンクの帰属、階層集計、種別分類、履歴比較。パーサーに集計方針を埋め込まない。

`LiveMftScanner` は取得層の降順列挙を受け、順序・範囲を検証して完全結合済みファイルの観測結果を逐次返します。全件を保持せず、拡張レコードは別の容量として返しません。基準測定CLIの出力・競合時の扱い・計測範囲は [scan-benchmark.md](scan-benchmark.md) に記載します。

## APIの使い分け

`MftRecordParser.Parse` は不正なバイト列に `NtfsParseException`（入力構造を基準とするOffset付き）を返します。`TryParse` は同じエラーをoutで返します。呼び出し側の誤った引数はArgument例外です。

`MftRecordReader.ReadAsync` はraw $MFTストリームを順に解析します。空スロット、解析成功、構造エラーを区別し、最後の端数も報告します。Streamの所有権は呼び出し側にあり、途中で列挙を終了しても閉じません。

`MftFileAssembler` には**実ボリュームのbytesPerCluster**を渡します。非常駐ATTRIBUTE_LISTの内容は取得層から別途渡す必要があります。取得できない場合や拡張レコードの不足を部分的な容量として返しません。レコードモデルはパーサーから得たものを渡してください。

DATA結合ではVCN連続性と全extentの物理run合計・先頭の容量フィールドを照合します。圧縮単位は1 byte、後続5 byteは別の予約領域として保持します。ストリームのStorageは先頭extentの設定を使い、継続の圧縮単位との一致は要求しません。これはNTFS3のprimary segmentに関する有効範囲の説明に基づき、以前の4→0の個別例外を置き換えたものです。元の属性値は保持します。Sparseビットだけのflags差は、ntfs-3gの先頭extentだけでの状態更新を根拠に許容します。先頭の圧縮形式（flagsの下位8 bit）が1の場合は、継続extentでCompressedビットが省略されていても、先頭設定と全runの物理容量から集計します。この限定対応はLinux実装の読み取り方とWindowsの実機観測からの推論であり、Microsoftの明示的な保証ではありません。逆方向の差、別の圧縮形式、暗号化・未知ビットの差はUnsupportedとして容量不明を返し、破損と断定しません。これは容量解析であり、圧縮データ本体の解凍は行いません。根拠・検証状況・対応範囲は [ntfs-format-audit.md](ntfs-format-audit.md) に記載します。

## 読み取り中の変更

`LiveMftRecordReader` は入力形式を明示する `IMftRecordSource` に対し最大3回（設定可能）の読み取りを試みます。`IRawMftRecordSource` はRawProtectedを指定するインターフェースとして維持します。1回の試行で取得したレコードを自分のバッファへコピーして解析し、もう一度取得してバイト列を比較します。共有バッファを返す取得層にも対応します。

`LiveMftFileReader` はATTRIBUTE_LISTの参照先を取得して結合し、結合後にbaseと全拡張レコードの観測バイト列を再照合します。非residentリストには `IAttributeListValueSource` が必要です。リスト自体も結合前後に照合し、その取得直後にbaseの記述を再確認します。ファイル全体の再試行は既定3回、取得はbaseを含め既定128レコードまでに制限します。拡張レコード内のリストは未対応として返し、容量を部分的に返しません。Missing・Replaced・ChangedDuringRead・InvalidRecord・InvalidAssembly・Unsupported・LimitExceededを区別します。取得層のI/Oエラーとキャンセルは例外として伝達します。

`AttributeListValueReader` は `IVolumeClusterSource` に対する純粋な取得計画・検証を担当します。base内で完結する無名・非圧縮・非スパースのmapping pairsだけを扱い、全範囲の連続性・重複・ボリューム境界・サイズ一致をI/O前に検証します。最大1 MiBの値を分割取得し、短い読み取りを補完せず、所有するバッファへコピーします。Windows API、バッファ整列、ハンドル管理はWindows取得層に残します。対応範囲と実機検証状況は [windows-acquisition.md](windows-acquisition.md) に記載します。

| 状態 | 意味／扱い |
|---|---|
| ObservedUnchanged | 2回の観測が一致。時点スナップショットの保証ではない |
| NotInUse | 一致したレコードが未使用。これだけで履歴上の削除を確定しない |
| Missing | 取得層が存在しないと報告。競合や取得範囲の変更を含みうる |
| Replaced | 期待した参照とシーケンスが異なる。以前のファイルと結合しない |
| ChangedDuringRead | 再試行の上限まで変化し続けた。容量不明として扱う |
| Invalid | 再試行しても構造を解読できない。ディスク破損と断定しない |

デバイスI/O失敗や権限エラーを「ファイルが存在しない」に変換しません。例外として上位に返します。レコードは照合直後にも変更でき、同じバイト列へ戻る変更（ABA）も検出できないため、この仕組みは完全な同期保証にはなりません。

baseとextensionがそれぞれ安定して見えても、結合全体が同じ時点とは限りません。取得層では参照を解決し、結合後に関連レコードを再検証する必要があります。ライブ全件読み取りの高速経路と、変化した対象の再検証経路は分離します。全件の二重読み取りを無条件に高速経路へ入れません。

## ボリューム全体と履歴の計画

- 開始時のボリュームID、USN Journal ID、USNを記録し、スキャン中の変更を終了後に読み直す。
- USNの切り詰め／Journalの再作成などで変更を追跡できない場合は再スキャン扱い。
- 更新レコードは変更通知であり、サイズの正本ではない。再取得したメタデータから容量を更新する。
- フォルダ移動・ハードリンク変更は影響する階層も更新する。file referenceだけでなくボリュームIDも永続IDに含める。
- シーケンス番号は有限で再利用がある。長期履歴ではJournalの継続性なども確認する。
- 未取得／変化継続／構造エラー件数を計測結果に保存する。未取得を0バイトとせず、不完全な計測から削除や減少を確定しない。
- 厳密に一時点の整合性が必要ならVSS等のスナップショットを別経路として用意する。USNの差分補正でも動き続けるボリュームの完全な時点保証は主張しない。

## 対応範囲と容量の意味

- NTFS 3.xのraw FILEレコード、512バイト刻みのupdate sequence fixup、512〜65536バイトのレコード長。
- 常駐／非常駐、名前付きDATA、複数FILE_NAME、ATTRIBUTE_LIST、圧縮／スパースの物理割り当てフィールド、拡張レコード。
- FILE_NAMEのキャッシュ済みサイズは使用しない。DOS名を勝手に削除せず、ハードリンクも保持する。
- 容量はDATAストリームの論理サイズ／割り当てサイズ。常駐DATAの追加クラスタ割り当ては0で、MFT自体の占有量がないという意味ではない。
- ディレクトリ索引、その他のNTFS管理属性、重複排除、WOFなどを含む全占有量の帰属は別途設計する。
- 暗号化／圧縮されたファイル本体の復号・展開は行わない。未知の属性は構造検証して保持するが、意味を理解したとは扱わない。
- WindowsのFSCTL返却はWindowsDecodedとして渡す。rawディスク入力はRawProtectedの全末尾検証を行い、検証失敗時に別モードへフォールバックしない。rawを復元したコピーの厳格な保存値一致にはFixupsRestoredを使う。API返却でのUSA保存値との不一致だけをディスク破損の証明としない。取得層とパーサー間でMftRecordEncodingを明示し、形式を自動推測しない。

## 検証とOSSの位置づけ

自前の解析器は小さく保ち、製品固有の履歴／整合性の規則を別クラスへ分離します。EricZimmerman/MFTはテスト専用の比較実装として利用。DiscUtilsも候補ですが、ライブ読み取りの整合性や本用途の速度は別途検証が必要です。

MITライセンスのEricZimmerman/MFTリポジトリの公開fixtureをローカルで取得して追加検査しました（ダンプはGitへ追加しません）。NIST/DFR-16は154スロット、tdunganは52,320スロットで構造エラー0。tdunganで発見した「圧縮ストリームのcontinuationは64バイトヘッダーの場合がある」は合成回帰テストへ反映しました。

同リポジトリのxw fixtureでは15件を厳格な構造検査が拒否しました。レコードごとの調査とEricZimmerman/MFTの結果との照合で、直接の拒否理由を次のように分類しました。

| 件数 | レコード番号 | 直接の拒否理由 |
|---|---|---|
| 10 | 179, 540, 546, 551, 554, 566, 567, 568, 586, 616 | 更新シーケンス配列の期待値と512バイト区切りの末尾が不一致 |
| 2 | 500, 565 | FILEの代わりにBAADシグネチャ。比較ライブラリもIsBad=true |
| 1 | 0 | DATAの宣言VCN範囲は0〜297だが、mapping pairsは314クラスタ（0〜313）を示す |
| 1 | 202 | 使用済み長504バイトまで属性が続き、その範囲内に終端マーカーがない |
| 1 | 605 | DATAの宣言VCN範囲は0〜81919だが、mapping pairsは1クラスタのみ |

更新シーケンス不一致10件とBAADの2件は、ヘッダー上ではすべてInUseビットが立っていないレコードです。これらは有効な現行ファイルの容量として集計してはいけません。未使用レコードにも更新シーケンス検査を適用しているため、オフライン検査のInvalid件数へ含まれています。比較ライブラリはEntryFlags=0の未使用レコードに対して更新シーケンス不一致をFixupOk=falseとしないため、FixupOk=trueだけでは一致を証明できません（540はDirectoryビットのみなので比較ライブラリも不一致を報告）。

残る3件はInUseが立っています。比較ライブラリはVCNとmapping pairsの一致を検査せず、終端マーカーがない場合も属性長0などでループを抜けるため、これらを解読できることがあります。本解析器では矛盾したメタデータを容量の正本に使用せず、構造エラーとして返します。今回の調査では、これらを正常な未対応形式と判断できる根拠は見つかりませんでした。

直接の拒否条件は判明しましたが、その不整合が生じた経緯（削除後の残骸、取得中の変更、加工されたfixture、元媒体の問題など）は不明です。ディスク故障と断定しません。また、12件の未使用状態はヘッダーからの観測であり、不整合レコード全体の信頼性を保証するものではありません。ライブ集計では未使用スロットの検査結果と、有効なファイルを取得できなかった件数を分けます。

この分類は、全NTFSデータへの対応やライブ読み取りの正確性の証明ではありません。実ボリュームとの照合は取得層の実装後に必要です。

参考:

- https://learn.microsoft.com/en-us/windows/win32/devnotes/file-record-segment-header
- https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header
- https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header
- https://flatcap.github.io/linux-ntfs/ntfs/attributes/attribute_list.html
- https://flatcap.github.io/linux-ntfs/ntfs/attributes/file_name.html
- https://github.com/EricZimmerman/MFT
- https://github.com/LTRData/DiscUtils
