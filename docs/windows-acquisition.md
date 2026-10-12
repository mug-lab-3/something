# 読み取り専用Windows取得層

## 実装範囲と安全性

`Something.Windows` はボリューム情報と指定番号のMFTレコード取得を担当する。
取得・解析・結合・統計・保存・UIは分離する。`Something.LiveInspect` は少数レコードと
residentまたは対応範囲内の非resident ATTRIBUTE_LISTで参照を追跡できるファイルの診断用で、
高速な全件走査や完成した容量集計ではない。

ボリュームはGENERIC_READのみ、OPEN_EXISTINGで開く。GENERIC_WRITEは要求しない。
共有モードREAD / WRITE / DELETEは他プロセスの操作を許す設定であり、
Something自身の書き込み権限を付与するものではない。
内部のDeviceIoControlは次の3命令のみを許可し、汎用制御コードAPIは公開しない。

| 制御命令 | 値 | 用途 |
|---|---|---|
| FSCTL_GET_NTFS_VOLUME_DATA | 0x00090064 | ジオメトリとMFT長 |
| FSCTL_GET_NTFS_FILE_RECORD | 0x00090068 | FILEレコード |
| IOCTL_STORAGE_QUERY_PROPERTY | 0x002D1400 | StorageAccessAlignmentPropertyの照会のみ |

非resident ATTRIBUTE_LISTの取得に限り、検証済みmapping pairsに対してSetFilePointerExとReadFileを使う。
シークは読み取り位置を移動するだけで、書き込みは行わない。シークと読み取りは同じロック内で行う。
論理セクタ単位のオフセット・長さと、照会した物理セクタ境界に整列したバッファを使う。
物理セクタサイズは最大64 KiBまでとし、照会失敗・不整合時に推測値で続行しない。
バッファはMarshalで確保し、finallyでクリア・解放する。unsafeコードは使わない。

WriteFile、修復、ロック、マウント解除、USN Journalの作成・削除、属性変更は呼ばない。
ボリューム入力はドライブ文字（例: C:）に限定し、PhysicalDriveやUNCは拒否する。
ファイル照合用ハンドルはアクセス権0のメタデータ専用。内容は読まず、
GetFileInformationByHandleでサイズ・参照・更新時刻等を観測する。
--fileの独立照合では同じハンドルでGetFileInformationByHandleExのFileStandardInfo (1)と
FileCompressionInfo (8)を照会する。属性変更APIは使用しない。
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

入力形式はMftRecordEncodingで明示する。

| モード | 取得契約と検証 |
|---|---|
| RawProtected | rawディスク入力。全セクタ末尾の保護値を検証してから、所有するコピーだけを復元する |
| FixupsRestored | raw入力のfixupを復元したコピー。全末尾とUSA保存値が一致することを要求する |
| WindowsDecoded | Windows FILE-record APIの返却データ。USA形状・範囲は検証するが、過去の保存値との末尾一致を要求せず、再復元もしない |

Windows取得はWindowsDecodedとして渡す。FILE署名、レコード・属性の長さと範囲、参照、
mapping pairs等は引き続き検証する。ディスク上のfixupを直接検証したとは主張しない。
WindowsDecodedをrawディスク入力に使わない。USA値から形式を自動推測したり、rawの検証失敗時に
このモードへフォールバックしたりしない。他の形式やボリューム条件への対応は別途検証する。

当初はWindows返却にもFixupsRestoredの末尾一致を要求していたが、全件走査で未解決が観測された。
追加の実機診断では、繰り返し取得したバイト列が一致する一方、使用領域内の1セクタ末尾だけが
保護値とも保存値とも一致しなかった。USA保存値が現在のAPI返却データの検証基準になるという
前提を外し、取得契約をWindowsDecodedとして分離した。変更後、同じ代表レコードが
ObservedUnchangedのディレクトリとして解析できることをユーザーの実機で確認した。
変更後の全件再測定と、旧InvalidRecord全件の解消確認はまだ残る。
範囲診断でInvalidRecord / InvalidAssemblyが出た場合だけ、追加の2回読み取りを行い、
バイト列の一致と `MftFixupDiagnostics` の件数要約を表示する。
保護値一致・保存値一致の件数は、保存値が保護値と同じ場合に重複するので合計しない。
保存値不一致が使用領域内か余白かも分けるが、要約から入力形式を選んだり検証を省略したりはしない。
この再読は元の結果と別時点の観測で、ファイル名・ストリーム名・LCN・raw内容を表示しない。
パーサーを通過したレコードのDATA記述は最大8属性まで、VCN・flags等だけを表示する。
`--scan` では追加再読をしないため、測定の呼び出し数・出力頻度を変えない。

[MicrosoftのMULTI_SECTOR_HEADER仕様](https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header)
では、ディスク書き込み前に末尾をUSAへ保存し、読み取り・書き込み後に復元すると説明されている。
復元後のメモリ上で変更された末尾とUSAの保存値が一致しない可能性がある、という解釈は
この仕様と実機観測からの推論であり、キャッシュ状態そのものは未確認。今回の不一致を破損とは断定しない。
合成テストでは現在のresident値と古いUSA保存値が異なる入力を作り、WindowsDecodedだけが
現在の値を変更せず扱えること、RawProtected / FixupsRestoredは拒否することを確認した。
WindowsDecodedでも不正なUSA・レコード・属性構造、取得中の変更、参照再利用を検出する。

結合エラーはVCNの不連続や容量不整合を構造不正、対応条件外のflags差を未対応として報告する。
Sparseビットのみの差はntfs-3gの先頭extentだけでの状態更新を根拠に許容する。
先頭のストリーム設定と各extentの元のflagsを保持し、容量・物理境界・再検証は省略しない。
変更後のSparse差があった実ファイルの確認とWindows APIでの独立容量照合はまだ残る。
容量解析では、先頭の圧縮形式が1の場合に、継続extentの圧縮ビット省略も許容する。
OSSの読取構成とWindowsの圧縮状態観測からの推論であり、全flags差を許容するものではない。
先頭の設定・容量欄と全runの検証は維持し、逆方向・別形式・暗号化・未知ビットの差は未対応。
エラー文には必要なVCN・flags等だけを含め、ファイル名・ストリーム名は含めない。
実機の別の代表ファイルでは、先頭DATA extentの圧縮単位4に対して継続extentが0だった。
当初は4→0だけを許可する互換処理を追加し、該当ファイルの容量が解決されたことを実機で確認した。
その後の仕様監査ではLinux NTFS3 v6.12のattr_is_frame_compressedに、圧縮単位は
primary segmentだけが有効という説明を確認した。現在は先頭の値をストリーム設定に使い、
継続値との一致は要求しない。元の値は各属性に保持し、個別の4→0例外は撤去した。
VCN連続性・参照・容量とmapping pairsの一致は維持する。
ライブのファイル照合・範囲診断・全件走査では、取得したTotalClustersをDATA結合器へ渡し、
全extent・名前付きDATAを含む物理runの範囲を検証する。末尾クラスタまでの割り当ては有効、
範囲外はInvalidAssemblyで容量未解決とする。Sparseのholeは物理クラスタを参照しないので、
論理VCNが総クラスタ数を超えること自体は拒否しない。
追加のディスク読み取りやレコード単位の出力は行わない。ジオメトリは開始時の観測に固定され、
ボリューム拡張・縮小を走査中に追跡するものではなく、時点スナップショットの保証もない。
この設計変更後の実ボリューム確認とWindows APIでの割り当て容量の独立照合はまだ残る。
根拠と合成／公開コーパス検証の範囲は [ntfs-format-audit.md](ntfs-format-audit.md) を参照。

LiveMftRecordReaderが再取得・照合し最大3回で打ち切る。変更・参照再利用・不正構造を
区別し、容量0へ変換しない。照合直後の変化やABAは防げず、時点スナップショットではない。
LiveMftFileReaderがresident ATTRIBUTE_LISTの参照を取得し、結合後にbaseと全拡張レコードを
再取得して、所有する不変の観測バイト列と照合する。変化した場合はファイル全体を最大3回やり直す。
参照番号だけでなくシーケンス番号も照合し、再試行中にbaseが再利用された場合は別ファイルへ切り替えない。
既定でbaseを含め1024レコードまで（正のint値で設定可能）とし、上限超過を明示して取得を打ち切る。
各レコードの読み取りも最大3回なので、ネイティブ呼び出し数と保持メモリは有限。
全件の高速走査とは別の検証経路として使う。

非residentリストはIAttributeListValueSourceから取得する。Windows取得層は
AttributeListValueReaderで全mapping pairsを先に検証し、VCNの連続性、物理範囲の重複、
ボリューム境界、論理・初期化・割り当てサイズの一致を確認する。
baseレコード内で全範囲を記述できる、無名・非圧縮・非スパースのリストに限定する。
リスト内容と対応するmapping pairsは最大1 MiB分まで（大きなクラスタでは1クラスタの余白を許す）。
読み取りは最大64 KiB、クラスタがそれ以上の場合は1クラスタ（最大2 MiB）単位に分割する。
短い読み取り・I/O失敗を0バイトへ補完しない。クラスタの余白はリストの解析に含めない。
リストを2回取得して照合し、参照元baseを再確認する。結合・全レコード照合の後にもリストとbaseを再確認する。
各リストの結果をすぐ所有するコピーへ移すため、取得層の共有バッファを保持しない。
一致してもABAや最後の確認後の変更を防げず、時点スナップショットではない。
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
比較対象は無名DATAの論理サイズと割り当て容量で、名前付きストリームは含めない。
Windows側の割り当て容量は、通常ファイルにはFileStandardInfo.AllocationSizeを使い、
Compressed / Sparse属性または非ゼロの圧縮形式が観測された場合はFileCompressionInfo.CompressedFileSizeを使う。
allocation-sourceで使用したAPI情報種別も出力する。両APIの値を混同せず個別に保持する。
MS-FSAのFileCompressionInformationの定義だけから通常ファイルのWin32 API返却を
一律に物理割り当て容量と扱った初期実装は、実機で論理サイズと同値が返るケースを見落としていた。
GetCompressedFileSizeのWin32説明も、非圧縮・非SparseではGetFileSizeと同値になると区別する。
解析前後でWindowsStorageInfo（標準情報・圧縮情報・属性）も比較し、変化があれば未解決とする。
標準情報の論理サイズと別取得の論理サイズが異なる場合や削除待ちも未解決として扱う。
API失敗を0バイトや一致へ置き換えない。圧縮形式とshift値はそのまま保持し、shift演算は行わない。
論理サイズと容量の一致はlogical-match / allocated-matchで別々に表示し、両方一致したときだけ
match=Trueとする。常駐DATAの解析側割り当て容量は従来どおり0であり、MFT等の管理領域を含めない。
この追加API照会は--fileだけで使用し、--scanの取得回数・途中出力は増やさない。
割り当て容量は解析値を表示するだけでWindows APIとの照合済みとは扱わない。
フォルダや異なるボリュームへのリンクは比較を拒否する。
不一致は解析バグと断定せず、観測間の変更の可能性も残す。

対応範囲外の非resident ATTRIBUTE_LIST、拡張レコード内のATTRIBUTE_LISTは未解決として報告する。
拡張レコードにもリストのmapping pairsが続く形式の取得には、まだ対応していない。
予約レコード8のルート配下のWin32名$BadClusと非resident $Badストリームが一致した場合も、
専用の容量集計が未実装のためUnsupportedとして返す。参照番号だけや名前だけで識別しない。
通常ファイルの割り当て容量検証は緩めず、管理ファイルの容量も0に置き換えない。
これは破損していないことの保証ではなく、集計対象を識別したうえでの未対応報告。
参照の不足・再利用・不正構造・取得上限超過も未解決とし、部分的な容量を返さない。
ファイルの取得レコード数上限は通常1024（baseを含む）で、NTFS形式上の上限ではない。
上限超過のreasonにはrequired-records / maximum-records / extension-recordsを表示する。
ATTRIBUTE_LIST内の同一参照は重複計数せず、baseの参照がリストになくてもbase分の1件を含める。
必要件数は取得したリストからの観測であり、全extensionを取得できた証明ではない。
上限を自動で増やしたり、未取得extensionの容量を0として補完したりはしない。
全件走査は状態ごとに最大3件の未解決例を終了後に表示する。必要件数を個別に確認する場合は、
その番号で`dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: <record-index> 1`を実行する。
範囲診断・`--file`・`--scan`・`--record-file`には末尾に`--max-records <positive-int>`を指定できる（既定1024、baseを含む）。
例: `dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: <record-index> 1 --max-records 2048`。
指定値以外への自動拡大は行わない。範囲診断のヘッダーにも選択した上限を表示する。
0・負数・int範囲外・不正値はボリュームを開く前に入力エラーとして拒否する。1024超過も明示指定でき、NTFS形式上の上限とは扱わない。
これは取得量の上限だけを変更する。参照番号・シーケンス・extent・容量の検証と再取得は維持する。
全件走査の比較例: `dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --scan --max-records 2048`。
開始表示に選択した上限を含める。5秒ごとの途中表示と、終了後の集約出力は維持する。
終了時のMemory行は、走査区間のプロセス全体のmanaged累積割り当てバイト数と、
プロセス起動後のピークworking setを表示する。累積割り当ては保持中メモリやピークではない。
working setはネイティブ・ランタイムも含み、開始前のピークも含むため走査だけの最大使用量ではない。
既定1024と明示指定した値を別プロセスで比較し、時間・取得回数・未解決数と併せて判断する。
ライブデータやOSキャッシュ状態は同一ではないので、単一の測定差を上限変更の効果と断定しない。
resident DATAのAllocatedBytes=0は追加DATAクラスタの割り当てが0という意味で、
MFT等を含むファイルの総占有量ではない。

終了コード: 0は範囲の処理完了または論理サイズ・割り当て容量の両方一致、1は未解決または不一致、
2は入力・権限・I/Oエラー、130はキャンセル。範囲検査のMissingは報告するが、
それだけでは異常終了しない。0もボリューム全体の正確さの証明ではない。

## 検証状況

単体テストは権限やライブボリュームを必要としない。返却構造・境界値・固定seed変異、
所有権、キャンセル、例外伝達、解放、復元済み入力を検証する。
ファイル情報APIは専用の一時ファイルのサイズと識別情報を取得して確認した。
開発エージェントの通常権限ではGENERIC_READによる取得はWin32 error 5（アクセス拒否）となった。
2026-10-12、ユーザーが実機のCLIを実行し、通常ファイル1件でMFT由来とWindows API由来の
論理サイズ4818バイトが一致した（match=True）。DATA割り当て容量の解析値は8192バイトだった。
容量照合API修正後の通常ファイルの実機検査では、論理サイズ5115バイトとDATA割り当て容量
8192バイトの両方がWindows APIと一致した。allocation-sourceはFileStandardInfoだった。
この通常ファイルの一致は、圧縮・Sparseファイルのライブ照合を検証済みとする根拠にはしない。
ボリューム情報取得、FixupsRestoredでの解析、参照照合、単一レコードの結合、論理サイズ比較が
この条件で動作した。参照番号・絶対パス・rawデータは検証記録に保存しない。
割り当て容量は独立照合していない。複数レコード結合の詳細、圧縮・スパース、
取得中の変更、他のボリューム条件、高速全件走査は実機未検証。
合成テストやこの1件の成功をライブ対応全体の証明にしない。
residentリストからの拡張取得・結合後の再検証は合成テストで検証済み。複数リンク名、DOS名、
名前付きストリーム、欠損、再利用、結合後の変更、変更継続、共有バッファ、キャンセル、取得上限を検証した。
ユーザーの実機でレコード0〜31の診断も実行した。ジオメトリ取得、使用中レコードの解析、
未取得スロットのMissing表示、複数DATA・名前付きDATA・resident DATAの容量表示が動作した。
管理レコード8でInvalidAssembly、9でUnsupportedを報告した。理由表示を追加した再診断では、
8はDATAのサイズとmapping pairsの不一致、9は非resident ATTRIBUTE_LIST取得の未実装だった。
8は$BadClus管理データの特殊表現が原因と考えられるが、実バイト列を取得していないため断定しない。
識別条件が一致した場合に専用容量集計の未対応を報告するよう変更した。変更後の実機確認は残る。
スロット数は使用中のファイル数ではない。resident DATAの追加割り当て0も総占有量0を意味しない。
範囲診断だけから複数レコードの結合が実機で成功したとは判断できず、その経路は実機未検証。
非residentリストの取得・照合を追加し、断片化、取得量の上限、範囲外・重複・欠損、短い読み取り、
バッファ所有権、参照元やリストの変更・削除・再利用、キャンセルを合成テストで検証した。
2026-10-12、ユーザーが次のコマンドを実機で実行した。

```powershell
dotnet run --project tools/Something.LiveInspect -- C: 9 1
```

以前Unsupportedだった管理レコード9で、名前付きDATAの論理サイズ7,223,596バイト、
割り当てサイズ7,225,344バイトを表示できた。対応範囲内の非resident ATTRIBUTE_LISTについて、
raw取得・内容照合・参照元再検証・ファイル結合の経路が、この条件で完了した。
名前付きDATAの容量表示も確認できた。ただし、表示値を別のAPIと独立照合したわけではなく、
出力には取得した拡張レコード数も含まれない。すべての非residentリスト形式や
複数レコード結合の正確さが実機で検証済みとは扱わない。
2026-10-12、DATAの物理クラスタ境界検証を追加した版で、ユーザーがReleaseの全件走査を実行した。
開始時の2,050,304スロットを16.964秒で走査し、候補1,440,448件のうち未解決は43件だった。
内訳はUnsupported 37件、LimitExceeded 6件で、InvalidRecord / InvalidAssemblyは報告されなかった。
今回追加した物理範囲検査による拒否も報告されていない。
Unsupportedの最初の例はDATA extentのflags差、LimitExceededの最初の例はファイルの取得レコード数上限だった。
全37件・6件がそれぞれ同じ理由だったことまでは、この集約出力から判断しない。
この結果は当該ボリュームでの経路完了の確認であり、全DATA容量の独立照合や時点スナップショットではない。
容量合計には未解決ファイルと管理領域の全量が含まれず、ディスク使用量とは扱わない。
測定間の候補数や合計容量の変化を、ファイル増減や肥大化の履歴として確定しない。
次は残る未対応形式・取得上限の診断を進め、階層集計・保存につなげる。

取得上限診断を追加した後、同じユーザーの個別診断ではrequired-records=169、
maximum-records=128、extension-records=168が報告された。
`--max-records 256`で再診断するとDATAの論理サイズ19,346,466,944バイト、
割り当て容量19,346,468,864バイトが表示され、取得・結合・再検証の経路が完了した。
この結果は1ファイルでの取得上限の影響の確認で、容量のWindows API独立照合ではない。
残る5件も同じ上限で解決することや、全件走査の時間・メモリへの影響はまだ未確認。

その後の上限比較では、128側は16.132秒・未解決43件（Unsupported 37、LimitExceeded 6）、
256側は15.857秒・未解決39件（Unsupported 37、LimitExceeded 2）だった。
両走査の候補数は1,440,715で、解決済みファイル数は256側で4件多かった。
128側のmanaged累積割り当ては43,682,223,528バイト、プロセス起動後のピークworking setは
63,115,264バイトだった。256側のreasonの詳細・容量合計・Memory行は提供出力が途中で切れており未確認。
したがって、残る2件の必要レコード数や、上限変更によるピークメモリ差は判断しない。
所要時間の小差もキャッシュ・負荷の影響を含むので、高速化の証明とは扱わない。

Sparseのみのextent flags差を許容した後、以前その差でUnsupportedだったユーザーの個別ファイルで、
DATAの論理サイズ6,278,375,836バイト、割り当て容量6,278,377,472バイトを表示できた。
取得・結合・再検証の経路が当該ファイルで完了したことを確認した。
表示容量のWindows APIとの独立照合、および変更後の全件走査はまだ未確認。

続く上限256の全件走査は16.430秒で完了し、候補1,440,946件・未解決7件
（Unsupported 5、LimitExceeded 2）を報告した。InvalidRecord / InvalidAssemblyは報告されなかった。
Unsupportedの表示3例はいずれも先頭Compressed、継続0のflags差だった。
省略された2例の理由はこの出力から特定しない。圧縮差をSparse差と同様に許容できるとは推定しない。
LimitExceededの2例はそれぞれrequired-records=317 / 289で、上限256を超えていた。
この上限は実装の資源制限であり、NTFS形式上の上限や破損の根拠ではない。
managed累積割り当ては43,756,530,264バイト、プロセス起動後のピークworking setは
68,890,624バイトだった。累積割り当て量と同時使用量を区別する。
別走査間では候補数やキャッシュ条件が異なるため、未解決数の減少や容量差を全件の個別対応関係、
肥大化の履歴、上限変更だけの効果として確定しない。
当該走査での未対応理由の表示と資源計測は確認できたが、解決済み容量全量の独立照合ではない。

## 参照番号によるWindows容量診断

`dotnet run --project tools/Something.LiveInspect -c Release --no-build -- C: --record-file <record-index> --max-records 256`
で、パスを入力せずに無名DATAのWindows側の容量を観測できる。
まずMFT取得で完全な参照（番号＋シーケンス）を確定し、使用中の通常baseファイルに限定する。
OpenFileByIdにはその64ビット参照をFileIdTypeで渡す。ディレクトリや拡張レコードを比較対象にしない。

選択ドライブのルートをアクセス要求0、共有READ / WRITE / DELETE、OPEN_EXISTINGで開き、
OpenFileByIdのvolume hintとして使う。対象ファイルもアクセス要求0で開く。
BACKUP_SEMANTICSとOPEN_REPARSE_POINTを指定し、reparse先を追跡しない。
ファイル本文の読み取り、属性変更、データ書き込み、flush、権限変更、名前の取得は行わない。
Windows APIの契約は[OpenFileById](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-openfilebyid)、
入力構造は[FILE_ID_DESCRIPTOR](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_descriptor)を参照。

取得前後で参照・ボリュームシリアル・論理サイズ・更新時刻・リンク数・属性と、
FileStandardInfo / FileCompressionInfoの観測値を確認する。
削除待ち、識別不一致、観測値の不整合や変更は容量一致へ置き換えない。
OpenFileByIdやメタデータ照会の失敗はI/Oエラーとして報告し、未取得の値は0へ補完しない。
通常権限の一時ファイルテストではrawボリュームを開かないが、このCLIはMFT取得が必要なので管理者権限が必要。

NTFSの結合がUnsupportedの場合も、安定したWindows側の論理・割り当て容量・圧縮状態を表示する。
この場合はWindows観測のみで、NTFS容量の独立照合済みやmatch=Trueとは表示しない（終了コード1）。
解決できた無名DATAのみ、NTFSとWindowsの論理・割り当てを両方比較する。
名前付きDATAの独立比較、圧縮内容の解凍、時点スナップショットの保証は提供しない。
純粋テストは参照再利用・異なるボリューム・状態変化・混在観測を検証する。
WindowsのNTFS上の専用一時ファイルで参照からのopen・rename後の照会・古いシーケンスの拒否を検証する。
圧縮ビット差があるユーザーの実ファイルで、この診断が参照番号からの取得に成功した。
Windowsは圧縮あり・Sparseなし・compression-format=2を報告し、論理サイズ4,517,414バイト、
割り当て容量1,245,184バイトを返した。この時点ではNTFSの結合がUnsupportedであり、
Windows側の観測だけが得られた。NTFS flagsの形式番号1とWindows APIの形式番号2は別の表現で、
同値比較しない。

2026-10-12、継続の圧縮ビット省略への対応後、同じ実ファイルで再実行した。
NTFS-logicalとWindows-logicalはともに4,517,414バイト、DATA-allocatedとWindows-allocatedは
ともに1,245,184バイトで、logical-match=True・allocated-match=True・match=Trueを確認した。
圧縮省略を伴う複数extentの無名DATAについて、この1件の独立容量照合が成功した。
他の圧縮条件・Sparseや変更後の全件走査まで確認したものではなく、観測前後の一致も
時点スナップショットを保証しない。

## 参考資料（リンク）

- [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)
- [NTFS_VOLUME_DATA_BUFFER](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-ntfs_volume_data_buffer)
- [FSCTL_GET_NTFS_FILE_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)
- [NTFS_FILE_RECORD_OUTPUT_BUFFER](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_file_record_output_buffer)
- [GetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle)
- [GetFileInformationByHandleEx](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex)
- [FILE_COMPRESSION_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_compression_info)
- [FILE_STANDARD_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info)
- [GetCompressedFileSizeW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getcompressedfilesizew)
- [MS-FSA: FileCompressionInformation](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/484bc722-6440-49a0-982a-9ba27cddf513)
- [MFTの予約管理レコード](https://learn.microsoft.com/en-us/windows/win32/devnotes/master-file-table)
- [ReadFile](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-readfile)
- [SetFilePointerEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfilepointerex)
- [File bufferingとセクタ整列](https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering)
- [STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-storage_access_alignment_descriptor)
- [libfsntfsのNTFS解析資料](https://github.com/libyal/libfsntfs/blob/main/documentation/New%20Technologies%20File%20System%20%28NTFS%29.asciidoc)（$BadClus:$Badの特異なmapping pairsの観測。通常DATAの規則を緩める根拠とはしない）
