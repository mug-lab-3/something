# 表示文言のリソース

## 定義と参照

画面の文言はすべて [`Resources.resw`](../../src/Something.App/Strings/ja-JP/Resources.resw) に置きます。WinUI / MRT Core の XML リソース形式で、既定の言語は `ja-JP` です。

> [!IMPORTANT]
> 文言を変えるときは resw だけを編集し、コードや XAML に日本語を直接書かないでください。

| 参照元 | 書き方 | resw のキー |
| --- | --- | --- |
| XAML | 要素に `x:Uid="Settings_BrowseButton"` を付け、文言の属性は書かない | `Settings_BrowseButton.Content` |
| XAML の添付プロパティ | 同上 | `Main_BackButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name`、`Main_BackButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip` |
| C# | `Strings.Get("Recycle_Action")` | `Recycle_Action` |
| C# の書式付き文言 | `Strings.Format("Scan_Completed", drive, seconds)` | `Scan_Completed`（値は `{0} のスキャンが完了しました（{1:F1} 秒）。`） |

[`Strings`](../../src/Something.App/Strings.cs) は `ResourceLoader` で値を読みます。キーが見つからない場合は、空の文字を表示せずに例外にします。書式付き文言の `<comment>` には、各引数が何かを記録します。同じ `x:Uid` を複数の要素に付けると、同じ文言を共有できます。

`x:Uid` で設定した値は、あとからコードで設定した値で上書きされます。状況に応じて切り替わる文言は、XAML の初期値を resw に置いたうえで、切り替えをコードで行います。

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

キーは文字列リテラルで渡します。変数や式で組み立てたキーは監査できないため、エラーにします。監査が通っても、確かめられるのは参照の整合性だけです。文言の意味やレイアウトは、画面で確認してください。

参照: [Microsoft: Localize strings in your UI](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/localize-strings)。
