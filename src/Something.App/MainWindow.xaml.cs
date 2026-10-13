using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Something.Application;
using Something.Ntfs;
using Something.Storage;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

namespace Something.App;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly DriveScanner scanner = new();
    private readonly Stack<FolderLocation> navigation = [];
    private readonly bool demo = Environment.GetCommandLineArgs().Contains("--demo");
    private CancellationTokenSource? scanCancellation;
    private ScanSession? session;
    private readonly Dictionary<string, ScanSession> sessions = new(
        StringComparer.OrdinalIgnoreCase
    );
    private DriveCard[] cards = [];
    private DriveCard? selectedCard;
    private FolderLocation? location;
    private int page;
    private int navigationVersion;
    private int rankingVersion;
    private bool busy;
    private bool closed;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Something";
        AppWindow.Resize(new(1280, 900));
        RootLayout.Loaded += (_, _) =>
        {
            double scale = RootLayout.XamlRoot.RasterizationScale;
            var workArea = Microsoft
                .UI.Windowing.DisplayArea.GetFromWindowId(
                    AppWindow.Id,
                    Microsoft.UI.Windowing.DisplayAreaFallback.Primary
                )
                .WorkArea;
            AppWindow.Resize(
                new(
                    Math.Min((int)Math.Ceiling(1280 * scale), (int)(workArea.Width * 0.95)),
                    Math.Min((int)Math.Ceiling(900 * scale), (int)(workArea.Height * 0.95))
                )
            );
        };
        var choices = demo
            ? new DriveChoice[]
            {
                new("Demo", "合成データ", 10L << 30, 4L << 30, true),
                new("Demo2", "別の合成データ", 20L << 30, 15L << 30, true),
                new("Demo3", "アーカイブ（合成）", 100L << 30, 80L << 30, true),
            }
            : LocalDrives();
        cards = choices.Select(choice => new DriveCard(choice)).ToArray();
        foreach (var card in cards)
        {
            card.Invoked += async (_, _) => await SelectDriveAsync(card);
            card.ScanRequested += async (_, _) =>
            {
                if (busy)
                {
                    scanCancellation?.Cancel();
                    return;
                }
                await SelectDriveAsync(card);
                if (selectedCard != card)
                    return;
                await StartScanAsync();
            };
            card.SetBusy(false, false);
        }
        selectedCard = cards.FirstOrDefault();
        foreach (var card in cards)
            DriveCards.Children.Add(card);
        UpdateDriveCards();
        Closed += OnClosed;
        if (demo)
        {
            StatusText.Text = "デモモード。実ディスクやファイルは読み取りません。";
            RootLayout.Loaded += async (_, _) =>
            {
                await StartScanAsync();
                var arguments = Environment.GetCommandLineArgs();
                int smoke = Array.IndexOf(arguments, "--smoke-test");
                if (smoke >= 0 && smoke + 1 < arguments.Length)
                    await CaptureSmokeTestAsync(arguments[smoke + 1]);
            };
        }
    }

    private async Task CaptureSmokeTestAsync(string path)
    {
        try
        {
            if (
                session is null
                || Entries.Items.Count != 3
                || Ranking.Items.Count != 3
                || MessageBar.IsOpen
            )
                throw new InvalidOperationException(
                    $"Demo UI did not load: session={session is not null}; entries={Entries.Items.Count}; ranking={Ranking.Items.Count}; error={MessageBar.Title}: {MessageBar.Message}"
                );
            var rootLocation = location!;
            var projects = Folders
                .RootNodes[0]
                .Children.Single(node => node.Content is FolderNode { Name: "Projects" });
            await LoadChildrenAsync(projects, 0);
            if (projects.Children.Count != 1)
                throw new InvalidOperationException("Lazy folder children did not load.");
            await LoadPageAsync(((FolderNode)projects.Content).Location, 0, true);
            if (Entries.Items.Count != 2 || navigation.Count != 1)
                throw new InvalidOperationException("Folder navigation did not load.");
            DepthBox.SelectedIndex = 1;
            await LoadRankingAsync();
            if (Ranking.Items.Count != 2)
                throw new InvalidOperationException("Depth filtering did not apply.");
            LeafBox.IsChecked = true;
            await LoadRankingAsync();
            if (Ranking.Items.Count != 1)
                throw new InvalidOperationException("Leaf filtering did not apply.");
            DepthBox.SelectedIndex = 0;
            LeafBox.IsChecked = false;
            await LoadRankingAsync();
            navigation.Clear();
            await LoadPageAsync(rootLocation, 0, false);
            var note = Entries.Items.Cast<EntryRow>().Single(row => row.Entry.Name == "notes.md");
            if (note.Size != DisplayText.Bytes(note.Entry.Data?.AllocatedBytes))
                throw new InvalidOperationException("Displayed size is not allocated capacity.");
            await SelectDriveAsync(cards[1]);
            if (session is not null || Entries.Items.Count != 0)
                throw new InvalidOperationException(
                    "Unscanned drive retained another drive's list."
                );
            await SelectDriveAsync(cards[0]);
            if (session is null || Entries.Items.Count != 3)
                throw new InvalidOperationException("Completed drive result was not retained.");
            projects = Folders
                .RootNodes[0]
                .Children.Single(node => node.Content is FolderNode { Name: "Projects" });
            if (MessageBar.IsOpen)
                throw new InvalidOperationException(MessageBar.Message);
            await Task.Delay(350, lifetime.Token);
            RootLayout.UpdateLayout();
            DriveScroll.Width = 620;
            RootLayout.UpdateLayout();
            await Task.Delay(100, lifetime.Token);
            if (DriveScroll.ScrollableWidth <= 0 || DriveScroll.ScrollableHeight > 0)
                throw new InvalidOperationException(
                    "Drive cards must scroll horizontally in one row."
                );
            DriveScroll.ChangeView(DriveScroll.ScrollableWidth, null, null, true);
            await Task.Delay(100, lifetime.Token);
            if (DriveScroll.HorizontalOffset <= 0)
                throw new InvalidOperationException("Drive cards did not scroll.");
            DriveScroll.ChangeView(0, null, null, true);
            await Task.Delay(100, lifetime.Token);
            DriveScroll.ClearValue(FrameworkElement.WidthProperty);
            RootLayout.UpdateLayout();
            if (
                FindContextFolder(Folders.ContainerFromNode(projects))?.Location.Path
                != "Demo\\Projects"
            )
                throw new InvalidOperationException(
                    "Tree context target did not resolve its own path."
                );
            var bitmap = new RenderTargetBitmap();
            if (Environment.GetCommandLineArgs().Contains("--smoke-progress"))
            {
                var button = (Button)cards[0].FindName("CardButton");
                VisualStateManager.GoToState(button, "PointerOver", false);
                if (
                    ((Border)cards[0].FindName("SelectionOutline")).Visibility != Visibility.Visible
                )
                    throw new InvalidOperationException("Hover hid the selection outline.");
                SetBusy(true);
                UpdateProgress(new(ScanPhase.Reading, 4, 0.42, TimeSpan.FromSeconds(5)));
                await Task.Delay(200, lifetime.Token);
                RootLayout.UpdateLayout();
            }
            await bitmap.RenderAsync(RootLayout);
            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
                throw new InvalidOperationException("The UI did not render.");
            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
            using var file = File.Open(path, FileMode.Create, FileAccess.ReadWrite);
            using var output = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96,
                96,
                pixels
            );
            await encoder.FlushAsync();
            File.WriteAllText(
                path + ".result",
                "UI loaded; root entries=3; ranking=3; navigation, lazy tree and filters passed; synthetic data only."
            );
        }
        catch (Exception error)
        {
            File.WriteAllText(path + ".result", error.ToString());
            Environment.ExitCode = 1;
        }
        finally
        {
            Close();
        }
    }

    private static DriveChoice[] LocalDrives()
    {
        var drives = new List<DriveChoice>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (
                    drive.DriveType == DriveType.Fixed
                    && drive.IsReady
                    && drive.DriveFormat == "NTFS"
                )
                    drives.Add(
                        new(
                            drive.Name[..2],
                            string.IsNullOrWhiteSpace(drive.VolumeLabel)
                                ? "ローカルディスク"
                                : drive.VolumeLabel,
                            drive.TotalSize,
                            drive.TotalFreeSpace
                        )
                    );
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return [.. drives];
    }

    private void UpdateDriveCards()
    {
        foreach (var card in cards)
            card.SetSelected(card == selectedCard);
    }

    private async Task SelectDriveAsync(DriveCard card)
    {
        if (busy || closed || card == selectedCard)
            return;
        selectedCard = card;
        UpdateDriveCards();
        card.StartBringIntoView();
        navigation.Clear();
        Folders.RootNodes.Clear();
        Entries.ItemsSource = null;
        Ranking.ItemsSource = null;
        location = null;
        session = sessions.GetValueOrDefault(card.Choice.Drive);
        if (session is { } current)
        {
            await ShowSessionAsync(current);
            if (selectedCard == card)
                StatusText.Text =
                    $"{current.Drive} の結果 · {current.Elapsed.TotalSeconds:F1}秒で完了 · 管理領域未解決 {current.MetadataUnresolved:N0} · 取得時点の観測";
        }
        else
        {
            PathText.Text = "未スキャン";
            PageText.Text = "";
            BackButton.IsEnabled = PreviousButton.IsEnabled = NextButton.IsEnabled = false;
            StatusText.Text = $"{card.Choice.Drive} を選択中 · スキャンを開始してください。";
        }
    }

    private async Task ShowSessionAsync(ScanSession current)
    {
        navigation.Clear();
        Folders.RootNodes.Clear();
        location = new(current.Root, current.Drive + "\\");
        var root = new TreeViewNode
        {
            Content = new FolderNode(current.Drive, location),
            HasUnrealizedChildren = true,
        };
        Folders.RootNodes.Add(root);
        await LoadPageAsync(location, 0, false);
        if (closed || session != current)
            return;
        await LoadChildrenAsync(root, 0);
        if (closed || session != current)
            return;
        root.IsExpanded = true;
        await LoadRankingAsync();
    }

    private async Task StartScanAsync()
    {
        if (busy || closed || selectedCard is not { } card)
            return;
        string drive = card.Choice.Drive;
        SetBusy(true);
        MessageBar.IsOpen = false;
        scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var progress = new Progress<ScanProgress>(UpdateProgress);
        try
        {
            var next = demo
                ? await scanner.CreateDemoAsync(progress, scanCancellation.Token, drive)
                : await scanner.ScanAsync(drive, progress, scanCancellation.Token);
            if (closed)
            {
                await next.DisposeAsync();
                return;
            }
            if (sessions.TryGetValue(drive, out var previous))
                await previous.DisposeAsync();
            session = next;
            sessions[drive] = next;
            card.SetResult(next);
            card.RefreshVolume();
            StatusText.Text =
                $"{(demo ? "デモ / " : "")}{next.Elapsed.TotalSeconds:F1}秒で完了 · 管理領域未解決 {next.MetadataUnresolved:N0} · 取得時点の観測";
            await ShowSessionAsync(next);
        }
        catch (OperationCanceledException)
        {
            if (!closed)
            {
                StatusText.Text = "キャンセルしました。前の結果は保持しています。";
                card.SetResult(sessions.GetValueOrDefault(drive));
            }
        }
        catch (Exception error)
        {
            card.SetResult(sessions.GetValueOrDefault(drive));
            ShowError(error);
        }
        finally
        {
            scanCancellation.Dispose();
            scanCancellation = null;
            if (!closed)
                SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        foreach (var card in cards)
            card.SetBusy(value, card == selectedCard);
        Folders.IsEnabled = !value;
        Entries.IsEnabled = !value;
        Ranking.IsEnabled = !value;
        DepthBox.IsEnabled = !value;
        LeafBox.IsEnabled = !value;
        selectedCard?.SetProgress(value);
        if (value)
            selectedCard?.SetState("準備中");
    }

    private void UpdateProgress(ScanProgress progress)
    {
        if (
            closed
            || !busy
            || scanCancellation?.IsCancellationRequested == true
            || progress.Phase == ScanPhase.Completed
        )
            return;
        selectedCard?.SetProgress(true);
        string phase = progress.Phase switch
        {
            ScanPhase.Reading => "メタデータを取得中",
            ScanPhase.Aggregating => "フォルダ容量を集計中",
            ScanPhase.Saving => "結果を保存中",
            ScanPhase.WritingSnapshot => "結果をファイルへ書き出し中",
            _ => "スキャン完了",
        };
        StatusText.Text =
            $"{phase} · {progress.Candidates:N0} 件 · {progress.Elapsed.TotalSeconds:F1} 秒";
        selectedCard?.SetState(
            progress.Phase switch
            {
                ScanPhase.Reading when progress.Fraction is { } fraction =>
                    $"走査中 {Math.Clamp(fraction, 0, 1):P0}",
                ScanPhase.Reading => "走査中",
                ScanPhase.Aggregating => "集計中",
                ScanPhase.Saving => "保存中",
                ScanPhase.WritingSnapshot => "書き出し中",
                _ => "スキャン完了",
            }
        );
    }

    private async Task LoadPageAsync(FolderLocation target, int targetPage, bool addHistory)
    {
        if (session is not { } current || closed)
            return;
        int version = ++navigationVersion;
        try
        {
            var result = await current.ReadFolderAsync(
                target.Reference,
                targetPage,
                false,
                lifetime.Token,
                sort: FolderEntrySort.AllocatedBytes
            );
            if (closed || version != navigationVersion || session != current)
                return;
            if (addHistory && location is { } previous && previous != target)
                navigation.Push(previous);
            location = target;
            page = targetPage;
            PathText.Text = DisplayText.Escape(target.Path);
            Entries.ItemsSource = result.Entries.Select(e => new EntryRow(e)).ToArray();
            PageText.Text = $"{page + 1} ページ · {result.Entries.Length} 件";
            PreviousButton.IsEnabled = page > 0;
            NextButton.IsEnabled = result.HasMore;
            BackButton.IsEnabled = navigation.Count > 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (session == current)
                ShowError(error);
        }
    }

    private async Task LoadChildrenAsync(TreeViewNode parent, int nextPage)
    {
        if (session is not { } current || parent.Content is not FolderNode folder || closed)
            return;
        parent.HasUnrealizedChildren = false;
        try
        {
            var result = await current.ReadFolderAsync(
                folder.Location.Reference,
                nextPage,
                true,
                lifetime.Token
            );
            if (closed || session != current)
                return;
            if (parent.Children.LastOrDefault()?.Content is FolderNode { LoadMore: true })
                parent.Children.RemoveAt(parent.Children.Count - 1);
            foreach (var entry in result.Entries)
                parent.Children.Add(
                    new TreeViewNode
                    {
                        Content = new FolderNode(
                            entry.Name,
                            new(
                                entry.Reference,
                                folder.Location.Path.TrimEnd('\\') + "\\" + entry.Name
                            )
                        ),
                        HasUnrealizedChildren = true,
                    }
                );
            if (result.HasMore)
                parent.Children.Add(
                    new TreeViewNode
                    {
                        Content = new FolderNode(
                            "さらに読み込む…",
                            folder.Location,
                            nextPage + 1,
                            true
                        ),
                    }
                );
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            parent.HasUnrealizedChildren = true;
            ShowError(error);
        }
    }

    private async void OnExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.HasUnrealizedChildren)
            await LoadChildrenAsync(args.Node, 0);
    }

    private async void OnFolderInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node || node.Content is not FolderNode folder)
            return;
        if (folder.LoadMore && node.Parent is { } parent)
            await LoadChildrenAsync(parent, folder.NextPage);
        else
            await LoadPageAsync(folder.Location, 0, true);
    }

    private FolderNode? FindContextFolder(DependencyObject? source)
    {
        for (
            var current = source;
            current is not null && current != Folders;
            current = VisualTreeHelper.GetParent(current)
        )
            if (current is TreeViewItem item)
                return
                    Folders.NodeFromContainer(item)?.Content
                        is FolderNode { LoadMore: false } folder
                    ? folder
                    : null;
        return null;
    }

    private void OnTreeContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (FindContextFolder(args.OriginalSource as DependencyObject) is not { } folder)
            return;
        ShowPathMenu(Folders, folder.Location.Path, args);
    }

    private void OnPathContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (location is { } current)
            ShowPathMenu(PathText, current.Path, args);
    }

    private void ShowPathMenu(
        FrameworkElement target,
        string path,
        ContextRequestedEventArgs args,
        string? directoryPath = null
    )
    {
        var menu = new MenuFlyout();
        void AddCopy(string label, string value)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = new SymbolIcon(Symbol.Copy) };
            // Copy the original full path of this row, even if selection changes.
            item.Click += (_, _) => CopyPath(value);
            menu.Items.Add(item);
        }
        if (directoryPath is not null)
            AddCopy("ディレクトリのパスをコピー", directoryPath);
        AddCopy(directoryPath is null ? "パスをコピー" : "ファイルのパスをコピー", path);
        if (args.TryGetPosition(target, out var position))
            menu.ShowAt(target, new FlyoutShowOptions { Position = position });
        else
            menu.ShowAt(target);
        args.Handled = true;
    }

    private static object? FindContextRow(ListView list, DependencyObject? source)
    {
        for (
            var current = source;
            current is not null && current != list;
            current = VisualTreeHelper.GetParent(current)
        )
            if (current is ListViewItem item)
                return list.ItemFromContainer(item);
        return null;
    }

    private void OnEntryContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (
            location is { } parent
            && FindContextRow(Entries, args.OriginalSource as DependencyObject) is EntryRow row
        )
            ShowPathMenu(
                Entries,
                parent.Path.TrimEnd('\\') + "\\" + row.Entry.Name,
                args,
                row.Entry.IsDirectory ? null : parent.Path
            );
    }

    private void OnRankContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (
            session is { } current
            && FindContextRow(Ranking, args.OriginalSource as DependencyObject) is RankingRow row
        )
            ShowPathMenu(Ranking, current.Drive + "\\" + row.Entry.RelativePath, args);
    }

    private void CopyPath(string path)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(path);
            Clipboard.SetContent(data);
            Clipboard.Flush();
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async void OnEntryInvoked(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (
            location is { } parent
            && Entries.SelectedItem is EntryRow { Entry.CanBrowse: true } row
        )
            await LoadPageAsync(
                new(row.Entry.Reference, parent.Path.TrimEnd('\\') + "\\" + row.Entry.Name),
                0,
                true
            );
    }

    private async void OnBack(object sender, RoutedEventArgs args)
    {
        if (navigation.TryPop(out var previous))
            await LoadPageAsync(previous, 0, false);
    }

    private async void OnPrevious(object sender, RoutedEventArgs args)
    {
        if (location is { } target && page > 0)
            await LoadPageAsync(target, page - 1, false);
    }

    private async void OnNext(object sender, RoutedEventArgs args)
    {
        if (location is { } target)
            await LoadPageAsync(target, page + 1, false);
    }

    private async void OnRankingFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!busy && session is not null)
            await LoadRankingAsync();
    }

    private async void OnLeafChanged(object sender, RoutedEventArgs args)
    {
        if (!busy && session is not null)
            await LoadRankingAsync();
    }

    private async Task LoadRankingAsync()
    {
        if (session is not { } current || closed)
            return;
        int version = ++rankingVersion;
        try
        {
            var rows = await current.ReadLargestAsync(
                new(
                    DepthBox.SelectedIndex == 0 ? null : DepthBox.SelectedIndex,
                    LeafBox.IsChecked == true
                ),
                lifetime.Token,
                FolderEntrySort.AllocatedBytes
            );
            if (closed || version != rankingVersion || current != session)
                return;
            Ranking.ItemsSource = rows.Select(r => new RankingRow(r, current.Drive)).ToArray();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async void OnRankInvoked(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (session is { } current && Ranking.SelectedItem is RankingRow row)
            await LoadPageAsync(
                new(row.Entry.Sample.Reference, current.Drive + "\\" + row.Entry.RelativePath),
                0,
                true
            );
    }

    private void ShowError(Exception error)
    {
        if (closed)
            return;
        bool denied =
            error is UnauthorizedAccessException
            || error.InnerException is Win32Exception { NativeErrorCode: 5 };
        MessageBar.Title = denied
            ? "スキャンには管理者権限が必要です"
            : "処理を完了できませんでした";
        MessageBar.Message = denied
            ? "管理者として開いたPowerShellからアプリを起動してください。ドライブへのアクセスは読み取り専用です。"
            : error.Message;
        MessageBar.Severity = InfoBarSeverity.Error;
        MessageBar.IsOpen = true;
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        lifetime.Cancel();
        scanCancellation?.Cancel();
        foreach (var current in sessions.Values)
            await current.DisposeAsync();
    }
}
