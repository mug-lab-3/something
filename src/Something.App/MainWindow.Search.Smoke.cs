using Microsoft.UI.Xaml;
using Something.Application;
using Something.Application.Search;

namespace Something.App;

public sealed partial class MainWindow
{
    // Needs --demo-static and --demo-history: the scans of the demo drive hold the same files each
    // time, so the expected results are fixed. Leaves the results screen shown for the capture.
    private async Task VerifySearchAsync(string capture)
    {
        if (!demo || history is null || cards.Length < 2)
            throw new InvalidOperationException("Search smoke needs the demo history.");
        // Earlier checks may leave another drive selected; the expectations are about the first.
        await SelectDriveAsync(cards[0]);
        // Two scans are enough for conditions about change.
        await StartScanAsync(cards[0]);
        await StartScanAsync(cards[0]);
        WorkspaceTabs.SelectedItem = SearchWorkspaceTab;
        SyncSearchPanel();
        SearchPanel.Activate();
        await SearchPanel.WaitForPreviewAsync(lifetime.Token);
        var conditions = SearchPanel.ConditionsScreen;
        if (
            !SearchPanel.ConditionsShown
            || SearchPanel.Visibility != Visibility.Visible
            || ViewHost.Visibility != Visibility.Collapsed
            || !conditions.SearchEnabledForSmoke
            || conditions.RangePreviewForSmoke == "—"
        )
            throw new InvalidOperationException(
                $"The conditions screen was not ready (shown {SearchPanel.ConditionsShown}, "
                    + $"panel {SearchPanel.Visibility}, files view {ViewHost.Visibility}, "
                    + $"search enabled {conditions.SearchEnabledForSmoke}, "
                    + $"range {conditions.RangePreviewForSmoke})."
            );
        // The name applies to the whole path unless that is switched off.
        if (!conditions.NameIncludesPathForSmoke || !conditions.Form.NameIncludesPath)
            throw new InvalidOperationException("The name does not apply to the path by default.");
        conditions.VerifyNumberInputsForSmoke();
        // A range of "the last days" follows the clock when the tab is shown again.
        conditions.AgeRangeForSmoke();
        SearchPanel.Activate();
        await SearchPanel.WaitForPreviewAsync(lifetime.Token);
        if (conditions.Form.End <= DateTimeOffset.Now || conditions.RangePreviewForSmoke == "—")
            throw new InvalidOperationException("The range did not follow the clock.");

        // One matching file: zip files, shown with their folders only.
        var form = new SearchConditionsForm
        {
            Start = conditions.Form.Start,
            End = conditions.Form.End,
            Target = SearchTarget.Files,
            Extensions = "zip",
        };
        var built = form.Build(visibilityOptions, null);
        if (!built.CanSearch)
            throw new InvalidOperationException("The zip condition was not accepted.");
        await SearchPanel.RunForSmokeAsync(built.Criteria!);
        var results = SearchPanel.Results;
        if (
            !SearchPanel.ShowsResults
            || results.Result is not { MatchedFiles: 1, MatchedFolders: 0 }
        )
            throw new InvalidOperationException("The zip search did not show one file.");
        // The labels are laid out when the screen has a size.
        RootLayout.UpdateLayout();
        await Task.Delay(200, lifetime.Token);
        if (results.ChipCount < 2 || results.CountsTextForSmoke.Length == 0)
            throw new InvalidOperationException("The conditions or counts were not summarized.");
        await ExpectRowsAsync(results, ["Projects"]);
        // Projects holds preview.png and more, but only source.zip (1.5 GiB) matched.
        if (results.Rows[0].Size != DisplayText.Bytes(1_610_612_736))
            throw new InvalidOperationException(
                $"The folder showed {results.Rows[0].Size}, not the matched size."
            );
        results.OpenFolder(results.Result.ChildFolders(results.Result.Root)[0].Reference);
        await ExpectRowsAsync(results, ["Archive"]);
        results.OpenFolder(results.Browser!.TreeChildren(results.Browser.Current)[0].Reference);
        await ExpectRowsAsync(results, ["source.zip"]);
        var zip = results.Rows.Single();
        if (
            zip.Size.Length == 0
            || !results.PathTextForSmoke.EndsWith("Archive", StringComparison.Ordinal)
        )
            throw new InvalidOperationException("The file row or the path was not shown.");
        if (results.FolderTree.SelectedNode?.Content is not SearchTreeEntry { Name: "Archive" })
            throw new InvalidOperationException("The tree did not follow the list.");
        // Back, forward and up use the folder history, not the screens.
        if (
            !SearchPanel.GoBack()
            || !results.PathTextForSmoke.EndsWith("Projects", StringComparison.Ordinal)
        )
            throw new InvalidOperationException("Back did not return to the parent folder.");
        // Forward returns to Archive; up climbs to Projects and then to the root, and stops there.
        if (
            !SearchPanel.GoForward()
            || !results.PathTextForSmoke.EndsWith("Archive", StringComparison.Ordinal)
        )
            throw new InvalidOperationException("Forward did not return to the folder.");
        if (!SearchPanel.GoUp() || !SearchPanel.GoUp())
            throw new InvalidOperationException("Up did not climb to the root.");
        if (SearchPanel.GoUp() || !SearchPanel.ShowsResults)
            throw new InvalidOperationException("Up passed the root or left the results.");

        // The graph of the file opens in its own window with one point per scan.
        int before = graphWindows.Count;
        await OpenGraphAsync(zip);
        if (graphWindows.Count != before + 1)
            throw new InvalidOperationException("The graph window did not open.");
        graphWindows[^1].VerifyForSmoke(results.Result!.Scans.Length);
        await Task.Delay(1500, lifetime.Token); // The chart draws after its first layout.
        await RenderPngAsync(graphWindows[^1].ContentForSmoke, capture + ".graph.png");
        graphWindows[^1].Close();

        // Back to the conditions: the result stays until a condition changes.
        SearchPanel.ResultsForSmokeBack();
        RootLayout.UpdateLayout();
        await Task.Delay(200, lifetime.Token);
        await RenderPngAsync(WindowLayout, capture + ".conditions.png");
        if (
            !SearchPanel.ConditionsShown
            || !SearchPanel.HasResult
            || !SearchPanel.CanReturnToResults
        )
            throw new InvalidOperationException("Going back to the conditions dropped the result.");
        conditions.TypeNameForSmoke("zip");
        await Task.Delay(50, lifetime.Token);
        if (SearchPanel.HasResult || SearchPanel.CanReturnToResults)
            throw new InvalidOperationException("A changed condition did not drop the result.");

        // The busy state of a search: the animation alone, and only a cancel button.
        RootLayout.UpdateLayout();
        await SearchPanel.ShowBusyForSmokeAsync(
            lifetime.Token,
            () => RenderPngAsync(WindowLayout, capture + ".searching.png")
        );

        // Everything: folders first, then files; sorting by size keeps folders first.
        conditions.TypeNameForSmoke("");
        form = new SearchConditionsForm
        {
            Start = conditions.Form.Start,
            End = conditions.Form.End,
        };
        await SearchPanel.RunForSmokeAsync(form.Build(visibilityOptions, null).Criteria!);
        string[] order = [.. results.Rows.Select(row => row.Item.Name)];
        if (!order.SequenceEqual(["Media", "Projects", "notes.md"]))
            throw new InvalidOperationException($"Unexpected order: {string.Join(", ", order)}.");
        results.SortForSmoke(SearchColumn.Size);
        if (
            !results.Rows[0].IsDirectory
            || !results.Rows[1].IsDirectory
            || results.Rows[2].IsDirectory
        )
            throw new InvalidOperationException("Sorting by size moved a file above a folder.");
        results.SortForSmoke(SearchColumn.Name);

        // Another drive discards the result.
        await SelectDriveAsync(cards[1]);
        if (SearchPanel.HasResult || !SearchPanel.ConditionsShown)
            throw new InvalidOperationException("Switching the drive kept the result.");
        await SelectDriveAsync(cards[0]);
        await SearchPanel.WaitForPreviewAsync(lifetime.Token);
        await SearchPanel.RunForSmokeAsync(form.Build(visibilityOptions, null).Criteria!);
        RootLayout.UpdateLayout();
        await Task.Delay(300, lifetime.Token);
    }

    private static async Task ExpectRowsAsync(SearchResultsView results, string[] names)
    {
        await Task.Yield();
        string[] rows = [.. results.Rows.Select(row => row.Item.Name)];
        if (!rows.SequenceEqual(names))
            throw new InvalidOperationException(
                $"Expected {string.Join(", ", names)} but the list showed {string.Join(", ", rows)}."
            );
    }
}
