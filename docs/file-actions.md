# ファイル操作

## 開く・コピー

ツリーと一覧の右クリックメニューの先頭に「開く」を置きます。フォルダは Explorer、ファイルは関連付けアプリで開きます。ファイルのダブルクリックも開く操作です。一覧内のフォルダへの移動はアプリ内の階層移動です。

パスのコピーはツリーと一覧から使用でき、ファイル一覧では親ディレクトリとファイルまでのパスを選べます。履歴に記録されたパスが現在存在する保証はありません。存在しない・アクセス拒否・関連付け失敗等はトーストで通知し、アクセス拒否を「存在しない」と断定しません。

## ゴミ箱への移動

ファイル・容量分析の一覧右端に操作アイコンを置きます。現在の対象を読み取り確認し、同じ項目が存在する場合だけ確認ダイアログを表示します。フォルダは配下も対象になることを示します。存在しない場合はダイアログを出さず、トーストで通知します。

確認は続行を Enter キーの既定操作にせず、キャンセルは通常色にします。処理中は重複実行を抑止します。通常のスキャンが読み取り専用であることと、この明示操作の書き込み権限は区別します。

## 保護対象

- System 属性の項目にはアイコンを出さず、実行側でも拒否する。
- 各ドライブの `Windows` とその配下、および Windows API が返す実際の Windows ディレクトリと配下を保護する。
- ルート直下の `Program Files`、`Program Files (x86)`、`ProgramData`、`Users`、`Documents and Settings`、`System Volume Information`、`$Recycle.Bin`、`Recovery`、`PerfLogs` 自体を保護する。
- プロファイル直下のコンテナと、Windows の既知の特別なフォルダ自体も保護する。Windows 以外の全配下を一律禁止にはしない。
- Hidden のみなら許可するが、強い警告を表示する。確認後に Hidden が付いた場合は再確認を求める。System と重なる場合は禁止を優先する。

正確な保護パスの判定は [`FileRecyclePolicy`](../src/Something.Windows/FileRecyclePolicy.cs) が正本です。保存済みの属性でアイコンを決めるだけでなく、確認と実行の各段階で現在の属性を再検証します。

## 識別とパス検証

[`FileRecycleWorkflow`](../src/Something.Application/FileRecycleWorkflow.cs) は、ボリューム GUID に対応する履歴キー、シリアル、MFT 番号とシーケンス、項目の種別を照合します。ドライブ文字と名前だけでは実行しません。確認前・確認後・Shell 操作の直前に検証し、置換されていれば中止します。

相対・UNC・デバイスパス、代替ストリーム、ワイルドカード、`.` / `..`、末尾ドット・空白、予約デバイス名、NTFS 予約レコードを拒否します。親ディレクトリは再解析ポイントをたどらず確認し、削除共有なしの読み取りハンドルを維持します。ジャンクション等を経由するパスは操作しません。

## Shell と結果

専用 STA スレッドの `IFileOperation` を使用し、`FOFX_RECYCLEONDELETE` と `FOFX_ADDUNDORECORD` を指定します。リサイクル以外の削除が示された場合は中止し、完全削除へのフォールバックは設けません。成功は HRESULT・中止状態だけでなく、コールバックのゴミ箱内項目も確認します。

成功した名前と、既に存在しないと確認した名前は取り消し線で表示します。ハードリンクの別名は別に扱い、フォルダの場合は配下へも反映します。この状態はメモリ内で共有し、再スキャンや終了で破棄します。元の履歴・集計値・キャッシュの容量は書き換えず、削除後の実使用量は次のスキャンで確認します。

識別確認と Shell のパス操作は不可分のトランザクションではありません。フォルダ操作の途中で失敗した場合、既に移動した子が自動的に元へ戻る保証はありません。不明な結果を全体の成功として表示しません。

通常テストは偽のバックエンドを使います。実 Shell の明示テストは[テスト](testing.md)を参照してください。

参照: [IFileOperation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation)、[SetOperationFlags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)、[PostDeleteItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-postdeleteitem)。
