using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Something.Statistics;
using Something.Storage;

namespace Something.App;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly WindowId window;
    private readonly string? currentDirectory;
    internal bool DeleteRequested { get; private set; }
    internal string? DirectoryPath =>
        currentDirectory is null ? null : Path.GetFullPath(DirectoryBox.Text.Trim());
    internal FileVisibilityOptions VisibilityOptions =>
        new(IncludeSystem.IsChecked == true, IncludeHidden.IsChecked == true);

    internal SettingsDialog(
        FileVisibilityOptions options,
        ScanHistoryArchive? archive,
        bool canMaintain,
        WindowId window
    )
    {
        InitializeComponent();
        this.window = window;
        IncludeSystem.IsChecked = options.IncludeSystem;
        IncludeHidden.IsChecked = options.IncludeHidden;
        currentDirectory = archive?.DirectoryPath;
        DatabaseSection.Visibility = archive is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        DirectoryBox.Text = currentDirectory ?? "";
        DirectoryBox.IsReadOnly = !canMaintain;
        BrowseButton.IsEnabled = canMaintain;
        DeleteDatabaseButton.IsEnabled = canMaintain && archive is not null;
        BusyHint.Visibility = canMaintain ? Visibility.Collapsed : Visibility.Visible;
        if (archive is not null)
        {
            var info = archive.ReadDatabaseInfo();
            SizeText.Text = DisplayText.Bytes((UInt128)info.TotalBytes);
            CurrentPathText.Text = info.Path;
        }
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (currentDirectory is null)
            return;
        try
        {
            if (!Path.IsPathFullyQualified(DirectoryBox.Text.Trim()))
                throw new ArgumentException(Strings.Get("Settings_AbsolutePathRequired"));
            _ = DirectoryPath;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            args.Cancel = true;
            ShowError(error);
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs args)
    {
        if (DirectoryBox.IsReadOnly)
            return;
        try
        {
            var result = await new FolderPicker(window).PickSingleFolderAsync();
            if (result is not null)
                DirectoryBox.Text = result.Path;
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs args)
    {
        try
        {
            if (currentDirectory is null)
                return;
            Directory.CreateDirectory(currentDirectory);
            Process.Start(
                new ProcessStartInfo { FileName = currentDirectory, UseShellExecute = true }
            );
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void OnDeleteDatabase(object sender, RoutedEventArgs args)
    {
        DeleteRequested = true;
        Hide();
    }

    private void ShowError(Exception error)
    {
        ErrorText.Text = StatusText.Describe(error);
        ErrorText.Visibility = Visibility.Visible;
    }
}
