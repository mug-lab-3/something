# 単一exeの配布構成

`scripts/publish-app.ps1` / VS Codeの `publish app` タスクを明示的に実行したときだけ、`artifacts/publish/win-x64/Something.App.exe` に発行する。配布物はこのexeひとつ。.NET 10、WinUI、DuckDB、画面リソース、依存ライセンス通知を同梱する。SDKの `--artifacts-path` で発行用のbin・obj・復元結果を `artifacts/publish-build` に分離する。ロック付き復元を行ってから発行し、開発用アプリのファイルを上書きせず、検証用のマニフェストなどの中間生成物も混ぜない。

通常の `dotnet build` / `dotnet run` とVS Codeの開発タスクはこのプロファイルを指定しない。Debug・Releaseとも `src/Something.App/bin/<Configuration>/net10.0-windows10.0.26100.0/win-x64` のexeとDLL・リソースを使い、自己展開や単一ファイルへの梱包は行わない。開発タスクはpublishを依存タスクにしない。

## 発行と展開

`Properties/PublishProfiles/SingleFile.pubxml` で `PublishSingleFile`、`SelfContained`、`WindowsAppSDKSelfContained`、`EnableMsixTooling`、`IncludeAllContentForSelfExtract` を有効にする。`WindowsPackageType=None` は維持する。WinUI・EF Core等を含むため、トリミングは無効のままにする。開発ビルドも単一ファイル用のアナライザーを有効にし、SDKが追加するILLinkツールの依存を通常のNuGetロックに含める。Visual Studioは不要。

初回起動時に通常 `%TEMP%/.net/Something.App/<bundle-id>` に内容を展開し、同じバンドルでは再利用する。`DOTNET_BUNDLE_EXTRACT_BASE_DIR` が指定されていればその場所を使う。実行時にはDLL等が必要なので、静的リンクや展開不要の実行形式ではない。exeを更新すると別のキャッシュが作られ、旧キャッシュが残る場合がある。.NETも同梱するため、.NETの修正を取り込む場合はSDKを更新して再発行する。履歴DBは展開先ではなく、従来のユーザー別保存先に置く。

Microsoftの[WinUI単一exeの条件](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app#single-file-exe)と[.NETの展開動作](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)に従う。通常の配布用exeは引き続き管理者権限を要求する。

## 過剰な依存を修正

初回は総合パッケージ `Microsoft.WindowsAppSDK 1.8.260921001` を指定していた。このパッケージはWinUIだけでなく、AI・ML・ウィジェット・全体のRuntimeパッケージを含む。そのため、発行時に未使用の `onnxruntime.dll`、`DirectML.dll` 等が取り込まれた。標準のpublish出力を、そのまま「Somethingが全て使用するファイル」と扱ったのは誤りだった。

現在は元のパッケージが指定していた版の `Microsoft.WindowsAppSDK.WinUI 1.8.260803003` と `Microsoft.WindowsAppSDK.DWrite 1.8.25122902` を直接参照する。WinUIが依存するFoundation・InteractiveExperiences・Base・WebView2はNuGetで解決する。単独コンポーネント使用に必要なBuildTools.MSIXも既存と同じ版を直接固定する。APIのダウングレードや、発行後のネイティブDLLの手動削除は行っていない。NuGetロックを更新し、依存経路そのものを限定した。

Microsoftの[1.8リリースノート](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8)にもコンポーネント単独使用の説明がある。未使用ML DLLが総合同梱で残る問題は、[公式リポジトリの報告](https://github.com/microsoft/WindowsAppSDK/issues/6015)でも確認できる。

## ファイルの内訳と確認

2026-10-14のフォルダ形式での比較では、総合パッケージ構成の356ファイル・116 DLLから、299ファイル・87 DLL、約138.6MiBへ減った。299ファイルのうち172ファイルはWinUI等の多言語MUIリソースだった。現在は.NETランタイムも含め、これらの依存を単一exeにまとめる。単一exeは約211.0MiB（221,280,828バイト）。依存の削減と単一ファイルへの梱包は別の効果であり、梱包だけでは依存自体は減らない。

SDKのコンポーネント内部も全てが現在の画面から直接呼ばれるわけではない。ここでの削減は、使わないSDKコンポーネントを依存関係から除くことであり、個別DLLの全使用経路を証明した「最小ファイル数」ではない。WinRTメタデータは[Microsoftの配布ガイド](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)で同梱が推奨されている。多言語対応や将来の互換性を変える削除は行わない。

PDBとAPIドキュメントを発行対象から除き、アプリ用PRIとXBFは明示的にバンドル対象に含める。WinUIのビルド出力にはPRI/XBFがあるが、標準.NET publishだけでは取り込まれなかったため補っている。バンドルのファイル選択前に追加し、exeの隣にコピーするだけの構成にしない。ライセンス通知は削除しない。

発行スクリプトは、新しい出力が `Something.App.exe` ひとつだけであることを確認してから前回のフォルダを置き換える。失敗時は前回の配布物を残す。単一exeの検証では、昇格を要求しない検証専用マニフェストと独立したbin/objを使い、exeしかないフォルダから既存の `--demo --demo-history --smoke-test` を実行した。初回展開、画面初期化、ツリー、一覧、ランキング、戻る/進む/親移動、600件の連続表示、複数デモドライブの同時スキャン、キャンセル、DuckDB履歴の保存・再表示が成功した。2回目も成功し、展開された481ファイルの更新時刻が変化しないことを確認した。検証は合成データと専用DBのみで、実ボリュームやユーザーの履歴を使わない。配布用exeの埋め込みマニフェストの `requireAdministrator` は別途確認する。

通常のインストーラーやMSIXへの変更は行わない。展開キャッシュは.NETの標準機構に任せ、他プロセスが使う可能性のある旧キャッシュの自動削除は実装しない。
