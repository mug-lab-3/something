# 表示文言のリソース

## 定義と参照

画面の文言はすべて [`Resources.resw`](../../src/Something.App/Strings/ja-JP/Resources.resw) に置きます。WinUI / MRT Core の XML リソース形式で、既定言語は `ja-JP` です。文言を変えるときは resw だけを編集し、コードや XAML に日本語を直接書きません。

| 参照元 | 書き方 | resw のキー |
| --- | --- | --- |
| XAML | 要素に `x:Uid="Settings_BrowseButton"` を付け、文言の属性は書かない | `Settings_BrowseButton.Content` |
| XAML の添付プロパティ | 同上 | `Main_BackButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name`、`Main_BackButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip` |
| C# | `Strings.Get("Recycle_Action")` | `Recycle_Action` |
| C# の書式付き文言 | `Strings.Format("Scan_Completed", drive, seconds)` | `Scan_Completed`（値は `{0} のスキャンが完了しました（{1:F1} 秒）。`） |

[`Strings`](../../src/Something.App/Strings.cs) は `ResourceLoader` で値を読み、キーがない場合は空文字を表示せずに例外にします。書式付き文言の `<comment>` には、各引数が何かを記録します。同じ `x:Uid` を複数の要素に付けると、同じ文言を共有できます。

`x:Uid` で設定した値は、コードで後から設定した値に上書きされます。読み込み中だけ表示する文言などは、XAML の初期値を resw に置いたうえで、切り替えはコードで行います。

## 下位層のエラー

Storage・Windows・Application は画面のリソースに依存しません。利用者に説明が必要な失敗は、例外の型を変えずにエラー種別を付けます。

| 層 | エラー種別 | 付け方・取り出し方 |
| --- | --- | --- |
| Storage | `HistoryFailure` | `error.WithFailure(HistoryFailure.DatabaseInUse)` / `HistoryFailures.Of(error)` |
| Windows | `RecycleFailure` | `error.WithFailure(RecycleFailure.PathChanged)` / `RecycleFailures.Of(error)` |

例外のメッセージ自体は英語の診断文です。画面は [`StatusText.Describe`](../../src/Something.App/StatusText.cs) でエラー種別を resw の文言に変換し、種別のない例外は診断文をそのまま表示します。データベース最適化の進捗（`HistoryCompactionStage`）とデモの段階（`DemoScanStage`）も、画面側で文言に変換します。エラー種別や段階を追加したら、`StatusText` と resw に対応する文言を追加します。

OS / .NET / DuckDB が生成するメッセージ、実ファイル名・ボリューム名などの利用者データ、製品名「Something」は resw の対象外です。

## 監査

```powershell
dotnet run --project tools/Something.LocalizationAudit -- check .
```

固定 SDK の Roslyn で `src` の C# と XAML を解析し、次を検出します。

- C# の文字列リテラルと XAML の文言に残った日本語。画面テスト（`*.Smoke.cs`）が表示する合成のサンプル文は除きます。
- XAML の文言属性に直接書かれた文字。記号（`—` など）と製品名は除きます。
- resw にない `x:Uid`・`Strings.Get`・`Strings.Format` のキーと、どこからも参照されない resw のキー。
- 空の値・重複キー、`Strings.Format` の引数の数と書式の不一致、`Strings.Get` で書式付き文言を読んでいる箇所。

キーは文字列リテラルで渡します。変数や式で組み立てたキーは監査できないため、エラーにします。チェック成功は参照の整合性を示すもので、文言の意味やレイアウトは画面で確認します。

参照: [Microsoft: Localize strings in your UI](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/localize-strings)。
