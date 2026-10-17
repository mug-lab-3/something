using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Something.Application;
using Something.Windows;
using Windows.UI.Text;

namespace Something.App;

public sealed partial class MainWindow
{
    private async Task VerifyFileRecycleAsync()
    {
        if (!demo || session is not { } current || location is not { } root)
            throw new InvalidOperationException("Recycle smoke requires synthetic data.");
        var backend = new DemoRecycleBackend();
        recycleWorkflow = new(backend);
        var summary = current.RootSample;
        var file = Entries.Items.Cast<EntryRow>().Single(r => r.Entry.Name == "notes.md");
        var folder = Entries.Items.Cast<EntryRow>().Single(r => r.Entry.Name == "Projects");
        var fileTarget = RecycleTarget(
            current,
            root.Path.TrimEnd('\\') + "\\" + file.Entry.Name,
            file.Entry.Reference,
            false
        );
        await VerifyDialogAsync(fileTarget, file.Removal, confirm: false);
        if (file.Removal.IsRemoved || backend.RecycleCalls != 0)
            throw new InvalidOperationException("Cancelling recycle removed an item.");

        // Invoke the actual row button; the native backend is replaced with a demo fake.
        RootLayout.UpdateLayout();
        var container =
            Entries.ContainerFromItem(file) as DependencyObject
            ?? throw new InvalidOperationException("File recycle row was not realized.");
        var button =
            FindTreePart<Button>(container, "RecycleEntryButton")
            ?? throw new InvalidOperationException("File recycle button was not loaded.");
        Invoke(button);
        await Task.Delay(180, lifetime.Token);
        Confirm();
        await Task.WhenAll(historyTasks.ToArray());
        RootLayout.UpdateLayout();
        var name = FindTreePart<TextBlock>(container, "EntryNameText");
        if (
            !file.Removal.IsRemoved
            || backend.RecycleCalls != 1
            || button.IsEnabled
            || name?.TextDecorations != TextDecorations.Strikethrough
        )
            throw new InvalidOperationException(
                "Recycling did not strike and disable the actual file row."
            );

        var folderTarget = RecycleTarget(
            current,
            root.Path.TrimEnd('\\') + "\\" + folder.Entry.Name,
            folder.Entry.Reference,
            true
        );
        await VerifyDialogAsync(folderTarget, folder.Removal, confirm: true);
        var analysis = Ranking.Items.Cast<RankingRow>().Single(r => r.RelativePath == "Projects");
        if (!analysis.Removal.IsRemoved || !ReferenceEquals(analysis.Removal, folder.Removal))
            throw new InvalidOperationException(
                "Recycling a folder did not update the analysis view."
            );
        await LoadFolderAsync(new(folder.Entry.Reference, folderTarget.Path), true);
        if (Entries.Items.Cast<EntryRow>().Any(r => !r.Removal.IsRemoved))
            throw new InvalidOperationException(
                "Lazy descendants did not inherit their recycled folder state."
            );
        await LoadFolderAsync(root, false);
        if (
            Entries.Items.Cast<EntryRow>().Count(r => r.Removal.IsRemoved) != 2
            || current.RootSample != summary
            || Entries.Items.Count != 3
        )
            throw new InvalidOperationException(
                "Navigation lost removal markers or rewrote historical totals."
            );

        backend.Inspection = FileRecycleResult.Missing;
        var missing = Entries.Items.Cast<EntryRow>().Single(r => r.Entry.Name == "Media");
        await TrackRecycleAsync(
            RecycleTarget(
                current,
                root.Path.TrimEnd('\\') + "\\Media",
                missing.Entry.Reference,
                true
            ),
            missing.Removal
        );
        if (
            recycleDialog is not null
            || backend.RecycleCalls != 2
            || !MessageBar.Title.Contains("存在しません", StringComparison.Ordinal)
        )
            throw new InvalidOperationException(
                "An absent item showed a dialog or missed its toast."
            );
        string absent = Path.Combine(
            Path.GetTempPath(),
            "something-absent-" + Guid.NewGuid().ToString("N")
        );
        await OpenPathAsync(absent, false);
        if (!MessageBar.Title.Contains("存在しません", StringComparison.Ordinal))
            throw new InvalidOperationException("Opening an absent file did not show a toast.");
        await OpenPathAsync(absent, true);
        if (!MessageBar.Title.Contains("存在しません", StringComparison.Ordinal))
            throw new InvalidOperationException("Opening an absent folder did not show a toast.");
        backend.Inspection = FileRecycleResult.Ready;
        var hiddenTarget = fileTarget with { StandardFileAttributes = 2 };
        var hiddenTask = TrackRecycleAsync(hiddenTarget, new());
        await Task.Delay(180, lifetime.Token);
        if (
            recycleDialog
                is not {
                    DefaultButton: ContentDialogButton.None,
                    Content: StackPanel hiddenContent
                }
            || !hiddenContent
                .Children.OfType<InfoBar>()
                .Any(w => w.IsOpen && w.Severity == InfoBarSeverity.Warning)
        )
            throw new InvalidOperationException(
                "Hidden item confirmation did not show its warning or neutral cancel button."
            );
        recycleDialog.Hide();
        await hiddenTask;
        if (backend.RecycleCalls != 2)
            throw new InvalidOperationException("Cancelling a hidden item recycled it.");
        await TrackRecycleAsync(fileTarget with { StandardFileAttributes = 4 }, new());
        await TrackRecycleAsync(
            fileTarget with
            {
                Path = current.Drive + @"\Windows\ordinary.txt",
            },
            new()
        );
        if (recycleDialog is not null || backend.RecycleCalls != 2)
            throw new InvalidOperationException(
                "A protected item reached confirmation or recycling."
            );
        removals.ClearVolume(current.VolumeKey);
        recycleWorkflow = null;
        MessageBar.IsOpen = false;
        await ShowSessionAsync(current);

        async Task VerifyDialogAsync(FileRecycleTarget target, FileRemovalState state, bool confirm)
        {
            var task = TrackRecycleAsync(target, state);
            await Task.Delay(180, lifetime.Token);
            if (recycleDialog is not { DefaultButton: ContentDialogButton.None })
                throw new InvalidOperationException(
                    "Recycle dialog did not default to cancellation."
                );
            if (confirm)
                Confirm();
            else
                recycleDialog.Hide();
            await task;
        }
        void Confirm()
        {
            var dialog =
                recycleDialog
                ?? throw new InvalidOperationException("Recycle dialog was not shown.");
            var primary =
                FindTreePart<Button>(dialog, "PrimaryButton")
                ?? throw new InvalidOperationException(
                    "Recycle dialog primary button was not loaded."
                );
            Invoke(primary);
        }
        static void Invoke(Button button)
        {
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }
    }
}
