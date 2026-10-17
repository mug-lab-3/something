using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Something.Application;
using Something.Windows;

namespace Something.App;

public sealed partial class MainWindow
{
    private readonly FileRemovalTracker removals = new();
    private bool recycling;
    private ContentDialog? recycleDialog;
    private FileRecycleWorkflow? recycleWorkflow;

    private static FileRecycleTarget RecycleTarget(
        ScanSession scan,
        string path,
        Something.Ntfs.FileReference reference,
        bool directory,
        uint? attributes = null
    ) =>
        new(scan.Drive, path, scan.VolumeKey, scan.VolumeSerial, reference, directory)
        {
            StandardFileAttributes = attributes,
        };

    private void OnRecycleDoubleTapped(object sender, DoubleTappedRoutedEventArgs args) =>
        args.Handled = true;

    private async void OnRecycleEntry(object sender, RoutedEventArgs args)
    {
        if (
            sender is Button { DataContext: EntryRow row }
            && session is { } current
            && location is { } parent
        )
            await TrackRecycleAsync(
                RecycleTarget(
                    current,
                    parent.Path.TrimEnd('\\') + "\\" + row.Entry.Name,
                    row.Entry.Reference,
                    row.Entry.IsDirectory,
                    row.Entry.StandardFileAttributes
                ),
                row.Removal
            );
    }

    private async void OnRecycleAnalysis(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: RankingRow row } && session is { } current)
            await TrackRecycleAsync(
                RecycleTarget(
                    current,
                    current.Drive + "\\" + row.RelativePath,
                    row.Reference,
                    true,
                    row.StandardFileAttributes
                ),
                row.Removal
            );
    }

    private async Task TrackRecycleAsync(FileRecycleTarget target, FileRemovalState row)
    {
        if (closed || recycling || !row.CanRecycle || historyMaintenance)
            return;
        recycling = true;
        row.SetBusy(true);
        var task = RecycleCoreAsync(target);
        historyTasks.Add(task);
        try
        {
            await task;
        }
        finally
        {
            historyTasks.Remove(task);
            row.SetBusy(false);
            recycling = false;
        }
    }

    private async Task RecycleCoreAsync(FileRecycleTarget target)
    {
        ViewUpdate? update = null;
        try
        {
            // Demo/smoke mode never invokes a native destructive operation.
            recycleWorkflow ??= new(demo ? new DemoRecycleBackend() : new WindowsRecycleBackend());
            var workflow = recycleWorkflow;
            async Task<bool> Confirm(FileRecycleTarget item, CancellationToken token)
            {
                bool accepted = await ConfirmRecycleAsync(item, token);
                if (accepted)
                    update = BeginViewUpdate("ゴミ箱に移動しています…");
                return accepted;
            }
            var result = await workflow.RunAsync(
                target,
                Confirm,
                WinRT.Interop.WindowNative.GetWindowHandle(this),
                lifetime.Token
            );
            if (result == FileRecycleResult.Recycled)
            {
                removals.Mark(target, FileRemovalMark.Recycled);
                ShowToast(
                    DisplayText.Escape(target.Path),
                    InfoBarSeverity.Success,
                    "ゴミ箱に移動しました",
                    replacePersistent: true
                );
            }
            else if (result == FileRecycleResult.Missing)
            {
                removals.Mark(target, FileRemovalMark.Missing);
                ShowMissingItem(target.Path);
            }
            else if (result == FileRecycleResult.Replaced)
                ShowToast(
                    "スキャン時と同じ項目であることを確認できません。再スキャンしてください。",
                    InfoBarSeverity.Warning,
                    "項目が変更されています"
                );
            else if (result == FileRecycleResult.Protected)
                ShowToast(
                    "システム項目・Windowsの標準フォルダは削除できません。",
                    InfoBarSeverity.Warning,
                    "保護されている項目です"
                );
            else if (result == FileRecycleResult.HiddenNeedsConfirmation)
                ShowToast(
                    "確認中に隠し属性が変更されました。もう一度削除ボタンを押して警告を確認してください。",
                    InfoBarSeverity.Warning,
                    "属性が変更されています"
                );
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            ShowToast(error.Message, InfoBarSeverity.Error, "ゴミ箱に移動できませんでした");
        }
        finally
        {
            if (update is not null)
                await update.DisposeAsync();
        }
    }

    private async Task<bool> ConfirmRecycleAsync(FileRecycleTarget target, CancellationToken token)
    {
        var content = new StackPanel { Spacing = 12 };
        if (FileRecyclePolicy.IsHidden(target))
            content.Children.Add(
                new InfoBar
                {
                    IsOpen = true,
                    IsClosable = false,
                    Severity = InfoBarSeverity.Warning,
                    Title = "隠し項目の削除に注意してください",
                    Message =
                        "Windowsやアプリが使用するデータの可能性があります。ゴミ箱に移動すると、アプリの動作や設定に影響することがあります。内容を確認してください。",
                }
            );
        content.Children.Add(
            new TextBlock
            {
                Text = DisplayText.Escape(target.Path),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            }
        );
        if (target.IsDirectory)
            content.Children.Add(
                new TextBlock
                {
                    Text = "フォルダ内のファイル・フォルダも移動します。",
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        var dialog = new ContentDialog
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "ゴミ箱に移動しますか？",
            Content = content,
            PrimaryButtonText = "ゴミ箱に移動",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.None,
            PrimaryButtonStyle = (Style)
                Microsoft.UI.Xaml.Application.Current.Resources["DestructiveDialogButtonStyle"],
            CloseButtonStyle = new(typeof(Button))
            {
                BasedOn = (Style)
                    Microsoft.UI.Xaml.Application.Current.Resources["DefaultButtonStyle"],
            },
        };
        recycleDialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            token.ThrowIfCancellationRequested();
            return result == ContentDialogResult.Primary;
        }
        finally
        {
            recycleDialog = null;
        }
    }

    private void ShowMissingItem(string path) =>
        ShowToast(
            DisplayText.Escape(path),
            InfoBarSeverity.Informational,
            "このファイル・フォルダはすでに存在しません",
            replacePersistent: true
        );

    private async Task<bool> FolderAvailableAsync(string path)
    {
        if (demo)
            return true;
        try
        {
            var attributes = await Task.Run(() => File.GetAttributes(path), lifetime.Token);
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("現在の項目はフォルダではありません。");
            return true;
        }
        catch (IOException error)
            when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            ShowMissingItem(path);
            return false;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            ShowToast(error.Message, InfoBarSeverity.Error, "開けませんでした");
            return false;
        }
    }

    private sealed class DemoRecycleBackend : IFileRecycleBackend
    {
        internal FileRecycleResult Inspection { get; set; } = FileRecycleResult.Ready;
        internal int RecycleCalls { get; private set; }

        public Task<FileRecycleResult> InspectAsync(
            FileRecycleTarget target,
            CancellationToken token
        )
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Inspection);
        }

        public Task<FileRecycleResult> RecycleAsync(
            FileRecycleTarget target,
            nint owner,
            CancellationToken token
        )
        {
            token.ThrowIfCancellationRequested();
            RecycleCalls++;
            return Task.FromResult(FileRecycleResult.Recycled);
        }
    }
}
