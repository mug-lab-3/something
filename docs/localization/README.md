# 表示文言のリソース

## 定義と参照

画面の文言はすべて、言語ごとの `Resources.resw` に置きます。WinUI / MRT Core の XML リソース形式で、既定の言語は `ja-JP` です。

| 言語 | ファイル |
| --- | --- |
| 日本語（基準） | [`Strings/ja-JP/Resources.resw`](../../src/Something.App/Strings/ja-JP/Resources.resw) |
| 英語 | [`Strings/en-US/Resources.resw`](../../src/Something.App/Strings/en-US/Resources.resw) |

> [!IMPORTANT]
> 文言を変えるときは resw だけを編集し、コードや XAML に日本語を直接書かないでください。キーを追加・削除したときは、両方の言語の resw を同じように変更してください。

| 参照元 | 書き方 | resw のキー |
| --- | --- | --- |
| XAML | 要素に `x:Uid="Settings_BrowseButton"` を付け、文言の属性は書かない | `Settings_BrowseButton.Content` |
| XAML の添付プロパティ | 同上 | `Main_BackButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name`、`Main_BackButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip` |
| C# | `Strings.Get("Recycle_Action")` | `Recycle_Action` |
| C# の書式付き文言 | `Strings.Format("Scan_Completed", drive, seconds)` | `Scan_Completed`（値は `{0} のスキャンが完了しました（{1:F1} 秒）。`） |

[`Strings`](../../src/Something.App/Strings.cs) は `ResourceLoader` で値を読みます。キーが見つからない場合は、空の文字を表示せずに例外にします。書式付き文言の `<comment>` には、各引数が何かを記録します。同じ `x:Uid` を複数の要素に付けると、同じ文言を共有できます。

`x:Uid` で設定した値は、あとからコードで設定した値で上書きされます。状況に応じて切り替わる文言は、XAML の初期値を resw に置いたうえで、切り替えをコードで行います。

英語では、日本語と語順や区切りが変わります。書式の引数の順番は言語ごとに変えてかまいませんが、引数の数は日本語と同じにします。前後の文言に連結する接頭辞・接尾辞（` · 選択中` など）は、英語でも先頭や末尾の空白を含めて連結後の形に合わせます。日付は、コード側の書式と合わせて両方の言語で `yyyy/MM/dd` を使います。

## 表示言語の選択

[`AppLanguage`](../../src/Something.App/AppLanguage.cs) が、起動直後（最初の画面を作る前）に表示言語を決めて、`Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride` に設定します。選択の規則は [`DisplayLanguage`](../../src/Something.Application/DisplayLanguage.cs) にあります。

| 設定 | 表示言語 |
| --- | --- |
| システム設定に従う（既定） | Windows の表示言語が日本語なら `ja-JP`、それ以外は `en-US` |
| 日本語 / English | 選んだ言語 |

既定の言語（`ja-JP`）への暗黙のフォールバックに頼らず、必ずどちらかの言語を明示します。`x:Uid` の文言は要素の読み込み時に決まるため、言語の変更は再起動で反映します。設定画面の「今すぐ再起動」は、通常の終了処理を済ませて多重起動防止の登録を解除してから、同じ権限で新しいプロセスを起動します。スキャンなどの処理中は再起動できません。デモは設定を保存しないため、`--language ja-JP` / `en-US` / `system` で言語を指定し、再起動時もこの引数で引き継ぎます。

数値や日時の書式は、Windows の地域設定（`CurrentCulture`）のままです。エクスポートの見出しや項目名は表示言語に合わせ、HTML には `lang` 属性で言語を記録します。インストーラーの画面は日本語のみです。

> [!NOTE]
> 日本語版の Windows で英語表示にしても、パスの区切り記号は「¥」で表示されます。文字の描画に使われる日本語フォントによるもので、英語版の Windows では「\」で表示されます。

## 下位層のエラー

Storage・Windows・Application の各層は、画面のリソースに依存しません。利用者に説明が必要な失敗には、例外の型を変えずにエラー種別を付けます。

| 層 | エラー種別 | 付け方・取り出し方 |
| --- | --- | --- |
| Storage | `HistoryFailure` | `error.WithFailure(HistoryFailure.DatabaseInUse)` / `HistoryFailures.Of(error)` |
| Windows | `RecycleFailure` | `error.WithFailure(RecycleFailure.PathChanged)` / `RecycleFailures.Of(error)` |

例外のメッセージ自体は英語の診断文です。画面側では [`StatusText.Describe`](../../src/Something.App/StatusText.cs) でエラー種別を resw の文言に変換し、種別のない例外は診断文をそのまま表示します。データベース最適化の進捗（`HistoryCompactionStage`）とデモの段階（`DemoScanStage`）も、画面側で文言に変換します。エラー種別や段階を追加したときは、`StatusText` と resw にも対応する文言を追加します。

OS・.NET・DuckDB が生成するメッセージ、実際のファイル名やボリューム名などの利用者のデータ、製品名「Something」は、resw の対象外です。

## 監査

```powershell
dotnet run --project tools/Something.LocalizationAudit -- check .
```

版を固定した SDK の Roslyn で `src` の C# と XAML を解析し、次の問題を検出します。

- C# の文字列リテラルと XAML の文言に残った日本語。画面テスト（`*.Smoke.cs`）で表示する合成のサンプル文は除く。
- XAML の文言の属性に直接書かれた文字。記号（`—` など）と製品名は除く。
- resw にない `x:Uid`・`Strings.Get`・`Strings.Format` のキーと、どこからも参照されない resw のキー。
- 空の値と重複したキー、`Strings.Format` の引数の数と書式の不一致、`Strings.Get` で書式付きの文言を読んでいる箇所。
- 日本語以外の resw で、日本語の resw とのキーの過不足、書式の引数の数の違い、日本語の混入。言語名「日本語」は、どの言語でもその言語の表記で示すため除く。

キーは文字列リテラルで渡します。変数や式で組み立てたキーは監査できないため、エラーにします。監査が通っても、確かめられるのは参照の整合性だけです。文言の意味やレイアウトは、画面で確認してください。

参照: [Microsoft: Localize strings in your UI](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/localize-strings)。
