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
    private readonly Stack<FolderLocation> forwardHistory = [];
    private FolderLocation? parentLocation;

    private enum NavigationAction
    {
        None,
        New,
        Back,
        Forward,
    }

    private readonly bool demo = Environment.GetCommandLineArgs().Contains("--demo");

    private sealed class ScanOperation(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
        public ScanProgress? Progress { get; set; }
        public bool Finished { get; set; }
    }

    private readonly Dictionary<DriveCard, ScanOperation> scans = [];
    private ScanSession? session;
    private readonly Dictionary<string, ScanSession> sessions = new(
        StringComparer.OrdinalIgnoreCase
    );
    private DriveCard[] cards = [];
    private DriveCard? selectedCard;
    private FolderLocation? location;
    private CancellationTokenSource? listingCancellation;
    private int navigationVersion;
    private int rankingVersion;
    private bool closed;
    private bool allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Something";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Something.ico"));
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
                if (scans.TryGetValue(card, out var running))
                {
                    running.Cancellation.Cancel();
                    return;
                }
                await SelectDriveAsync(card);
                if (selectedCard != card)
                    return;
                await StartScanAsync(card);
            };
            card.SetBusy(false);
        }
        selectedCard = cards.FirstOrDefault();
        foreach (var card in cards)
            DriveCards.Children.Add(card);
        UpdateDriveCards();
        AppWindow.Closing += OnClosing;
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
            await LoadFolderAsync(((FolderNode)projects.Content).Location, true);
            if (Entries.Items.Count != 2 || navigation.Count != 1)
                throw new InvalidOperationException("Folder navigation did not load.");
            var projectLocation = location!;
            await NavigateBackAsync();
            if (location != rootLocation || !ForwardButton.IsEnabled || UpButton.IsEnabled)
                throw new InvalidOperationException(
                    "Back navigation did not preserve forward history."
                );
            await NavigateForwardAsync();
            if (location != projectLocation || !UpButton.IsEnabled)
                throw new InvalidOperationException(
                    "Forward navigation did not restore the folder."
                );
            await NavigateBackAsync();
            await LoadFolderAsync(projectLocation, true);
            if (ForwardButton.IsEnabled || forwardHistory.Count != 0)
                throw new InvalidOperationException(
                    "New navigation did not discard forward history."
                );
            await LoadFolderAsync(((FolderNode)projects.Children[0].Content).Location, true);
            var media = Folders
                .RootNodes[0]
                .Children.Single(node => node.Content is FolderNode { Name: "Media" });
            await LoadFolderAsync(((FolderNode)media.Content).Location, true);
            await NavigateUpAsync();
            if (location != rootLocation || UpButton.IsEnabled || forwardHistory.Count != 0)
                throw new InvalidOperationException(
                    "Up followed history instead of the observed parent."
                );
            await LoadFolderAsync(projectLocation, true);
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
            forwardHistory.Clear();
            await LoadFolderAsync(rootLocation, false);
            var note = Entries.Items.Cast<EntryRow>().Single(row => row.Entry.Name == "notes.md");
            if (note.Size != DisplayText.Bytes(note.Entry.Data?.AllocatedBytes))
                throw new InvalidOperationException("Displayed size is not allocated capacity.");
            await VerifyIncrementalListAsync(note.Entry);
            await LoadFolderAsync(rootLocation, false);
            await SelectDriveAsync(cards[1]);
            if (session is not null || Entries.Items.Count != 0)
                throw new InvalidOperationException(
                    "Unscanned drive retained another drive's list."
                );
            await SelectDriveAsync(cards[0]);
            if (session is null || Entries.Items.Count != 3)
                throw new InvalidOperationException("Completed drive result was not retained.");
            var displayed = session;
            Task second = StartScanAsync(cards[1]);
            Task third = StartScanAsync(cards[2]);
            await StartScanAsync(cards[1]);
            await Task.WhenAll(second, third);
            if (
                session != displayed
                || selectedCard != cards[0]
                || sessions.Count != 3
                || scans.Count != 0
            )
                throw new InvalidOperationException(
                    "Concurrent scans changed the selected drive or lost results."
                );
            await SelectDriveAsync(cards[1]);
            if (session?.Drive != cards[1].Choice.Drive || Entries.Items.Count != 3)
                throw new InvalidOperationException(
                    "Concurrent drive result could not be browsed."
                );
            await SelectDriveAsync(cards[0]);
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
                cards[0].SetBusy(true);
                DisplayProgress(cards[0], new(ScanPhase.Reading, 4, 0.42, TimeSpan.FromSeconds(5)));
                await Task.Delay(200, lifetime.Token);
                RootLayout.UpdateLayout();
            }
            if (!ExtendsContentIntoTitleBar || AppTitleBar.ActualHeight <= 0)
                throw new InvalidOperationException("The integrated title bar did not load.");
            await bitmap.RenderAsync(WindowLayout);
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
                "UI loaded; root entries=3; ranking=3; back/forward/parent navigation, lazy tree, filters, concurrent drive scans, 600-row incremental scrolling and cancellation passed; synthetic data only."
            );
        }
        catch (Exception error)
        {
            File.WriteAllText(path + ".result", error.ToString());
            Environment.ExitCode = 1;
        }
        finally
        {
            await ShutdownAsync();
            allowClose = true;
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
        if (closed || card == selectedCard)
            return;
        selectedCard = card;
        listingCancellation?.Cancel();
        navigationVersion++;
        rankingVersion++;
        UpdateDriveCards();
        card.StartBringIntoView();
        navigation.Clear();
        forwardHistory.Clear();
        parentLocation = null;
        BackButton.IsEnabled = ForwardButton.IsEnabled = UpButton.IsEnabled = false;
        Folders.RootNodes.Clear();
        Entries.ItemsSource = null;
        EntryCountText.DataContext = null;
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
            EntryCountText.DataContext = null;
            BackButton.IsEnabled = false;
            StatusText.Text = $"{card.Choice.Drive} を選択中 · スキャンを開始してください。";
        }
        if (
            selectedCard == card
            && scans.TryGetValue(card, out var running)
            && running.Progress is { } progress
        )
            DisplayProgress(card, progress);
    }

    private async Task ShowSessionAsync(ScanSession current)
    {
        navigation.Clear();
        forwardHistory.Clear();
        parentLocation = null;
        Folders.RootNodes.Clear();
        location = new(current.Root, current.Drive + "\\");
        var root = new TreeViewNode
        {
            Content = new FolderNode(current.Drive, location),
            HasUnrealizedChildren = true,
        };
        Folders.RootNodes.Add(root);
        await LoadFolderAsync(location, false);
        if (closed || session != current)
            return;
        await LoadChildrenAsync(root, 0);
        if (closed || session != current)
            return;
        root.IsExpanded = true;
        await LoadRankingAsync();
    }

    private Task StartScanAsync(DriveCard? requested = null)
    {
        var card = requested ?? selectedCard;
        if (closed || card is null || scans.ContainsKey(card))
            return Task.CompletedTask;
        var operation = new ScanOperation(
            CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)
        );
        scans.Add(card, operation);
        card.SetBusy(true);
        card.SetProgress(true);
        card.SetState("準備中");
        operation.Task = RunScanAsync(card, operation);
        return operation.Task;
    }

    private async Task RunScanAsync(DriveCard card, ScanOperation operation)
    {
        string drive = card.Choice.Drive;
        MessageBar.IsOpen = false;
        var progress = new Progress<ScanProgress>(value =>
        {
            if (
                closed
                || !scans.TryGetValue(card, out var active)
                || active != operation
                || operation.Finished
                || operation.Cancellation.IsCancellationRequested
                || value.Phase == ScanPhase.Completed
            )
                return;
            operation.Progress = value;
            DisplayProgress(card, value);
        });
        try
        {
            var next = demo
                ? await scanner.CreateDemoAsync(progress, operation.Cancellation.Token, drive)
                : await scanner.ScanAsync(drive, progress, operation.Cancellation.Token);
            operation.Finished = true;
            if (closed)
            {
                await next.DisposeAsync();
                return;
            }
            sessions.TryGetValue(drive, out var previous);
            sessions[drive] = next;
            operation.Progress = null;
            card.SetResult(next);
            card.RefreshVolume();
            try
            {
                if (selectedCard == card)
                {
                    session = next;
                    navigationVersion++;
                    rankingVersion++;
                    StatusText.Text =
                        $"{(demo ? "デモ / " : "")}{next.Elapsed.TotalSeconds:F1}秒で完了 · 管理領域未解決 {next.MetadataUnresolved:N0} · 取得時点の観測";
                    await ShowSessionAsync(next);
                }
            }
            finally
            {
                if (previous is not null)
                    await previous.DisposeAsync();
            }
        }
        catch (OperationCanceledException)
        {
            if (!closed)
            {
                if (selectedCard == card)
                    StatusText.Text = "キャンセルしました。前の結果は保持しています。";
                card.SetResult(sessions.GetValueOrDefault(drive));
            }
        }
        catch (Exception error)
        {
            card.SetResult(sessions.GetValueOrDefault(drive));
            if (!closed)
                ShowError(new InvalidOperationException($"{drive}: {error.Message}", error));
        }
        finally
        {
            scans.Remove(card);
            operation.Cancellation.Dispose();
            if (!closed)
            {
                card.SetBusy(false);
                card.SetProgress(false);
            }
        }
    }

    private void DisplayProgress(DriveCard card, ScanProgress progress)
    {
        card.SetProgress(true);
        string phase = progress.Phase switch
        {
            ScanPhase.WaitingForMemory => "メモリ予算の空きを待機中",
            ScanPhase.Reading => "メタデータを取得中",
            ScanPhase.Aggregating => "フォルダ容量を集計中",
            ScanPhase.Saving => "結果を保存中",
            ScanPhase.WritingSnapshot => "結果をファイルへ書き出し中",
            _ => "スキャン完了",
        };
        if (selectedCard == card)
            StatusText.Text =
                $"{phase} · {progress.Candidates:N0} 件 · {progress.Elapsed.TotalSeconds:F1} 秒";
        card.SetState(
            progress.Phase switch
            {
                ScanPhase.WaitingForMemory => "メモリ待ち",
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

    private Task LoadFolderAsync(FolderLocation target, bool addHistory) =>
        LoadFolderAsync(target, addHistory ? NavigationAction.New : NavigationAction.None);

    private async Task LoadFolderAsync(FolderLocation target, NavigationAction action)
    {
        if (session is not { } current || closed)
            return;
        int version = ++navigationVersion;
        listingCancellation?.Cancel();
        listingCancellation?.Dispose();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        listingCancellation = request;
        var token = request.Token;
        BackButton.IsEnabled = ForwardButton.IsEnabled = UpButton.IsEnabled = false;
        try
        {
            var result = await current.ReadFolderAsync(
                target.Reference,
                0,
                false,
                token,
                sort: FolderEntrySort.AllocatedBytes,
                includeTotalCount: true
            );
            FolderLocation? parent = null;
            if (target.Reference != current.Root)
            {
                string path = target.Path.TrimEnd('\\');
                int separator = path.LastIndexOf('\\');
                if (
                    separator >= 0
                    && await current.ReadParentAsync(
                        target.Reference,
                        path[(separator + 1)..],
                        token
                    )
                        is { } reference
                )
                    parent = new(
                        reference,
                        path[..separator] + (reference == current.Root ? "\\" : "")
                    );
            }
            if (closed || version != navigationVersion || session != current)
                return;
            if (location is { } previous && previous != target)
            {
                switch (action)
                {
                    case NavigationAction.New:
                        navigation.Push(previous);
                        forwardHistory.Clear();
                        break;
                    case NavigationAction.Back:
                        navigation.Pop();
                        forwardHistory.Push(previous);
                        break;
                    case NavigationAction.Forward:
                        forwardHistory.Pop();
                        navigation.Push(previous);
                        break;
                }
            }
            location = target;
            parentLocation = parent;
            PathText.Text = DisplayText.Escape(target.Path);
            var rows = new FolderEntryCollection(
                result,
                (nextPage, cancellation) =>
                    current.ReadFolderAsync(target.Reference, nextPage, false, cancellation),
                error =>
                {
                    if (!closed && session == current && version == navigationVersion)
                        ShowError(error);
                },
                token
            );
            Entries.ItemsSource = rows;
            EntryCountText.DataContext = rows;
            BackButton.IsEnabled = navigation.Count > 0;
            ForwardButton.IsEnabled = forwardHistory.Count > 0;
            UpButton.IsEnabled = parentLocation is not null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (session == current && version == navigationVersion)
                ShowError(error);
        }
        finally
        {
            if (!closed && session == current && version == navigationVersion)
            {
                BackButton.IsEnabled = navigation.Count > 0;
                ForwardButton.IsEnabled = forwardHistory.Count > 0;
                UpButton.IsEnabled = parentLocation is not null;
            }
        }
    }

    private async Task VerifyIncrementalListAsync(FolderEntry template)
    {
        FolderEntry[] facts = Enumerable
            .Range(0, 600)
            .Select(i =>
                template with
                {
                    Reference = Something.Ntfs.FileReference.Create((ulong)(4000 + i), 1),
                    Name = $"file-{i:D4}.txt",
                }
            )
            .ToArray();
        FolderEntryPage Batch(int index) =>
            new([.. facts.Skip(index * 250).Take(250)], index < 2, index == 0 ? 600 : null);
        var rows = new FolderEntryCollection(
            Batch(0),
            async (index, token) =>
            {
                await Task.Delay(25, token);
                return Batch(index);
            },
            error => throw new InvalidOperationException("Incremental list failed.", error),
            lifetime.Token
        );
        Entries.ItemsSource = rows;
        EntryCountText.DataContext = rows;
        for (int i = 0; i < 8 && rows.Count < 600; i++)
        {
            await Task.Delay(150, lifetime.Token);
            Entries.UpdateLayout();
            var scroll =
                FindListScrollViewer(Entries)
                ?? throw new InvalidOperationException("List scroll viewer did not load.");
            scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
        }
        await Task.Delay(100, lifetime.Token);
        if (
            rows.Count != 600
            || rows.HasMoreItems
            || rows.Select(row => row.Entry.Reference).Distinct().Count() != 600
            || EntryCountText.Text != $"{600:N0} 件"
        )
            throw new InvalidOperationException(
                "Scrolling did not load every row and update the total count."
            );

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var pending = new FolderEntryCollection(
            Batch(0),
            async (index, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Batch(index);
            },
            error =>
                throw new InvalidOperationException("Cancelled list reported an error.", error),
            cancellation.Token
        );
        var request = pending.LoadMoreItemsAsync(250).AsTask();
        cancellation.Cancel();
        await request;
        if (pending.Count != 250 || pending.HasMoreItems)
            throw new InvalidOperationException("Cancelled navigation appended stale rows.");
    }

    private static ScrollViewer? FindListScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll)
            return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindListScrollViewer(VisualTreeHelper.GetChild(parent, i)) is { } child)
                return child;
        return null;
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
            await LoadFolderAsync(folder.Location, true);
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
            await LoadFolderAsync(
                new(row.Entry.Reference, parent.Path.TrimEnd('\\') + "\\" + row.Entry.Name),
                true
            );
    }

    private async void OnBack(object sender, RoutedEventArgs args) => await NavigateBackAsync();

    private async void OnForward(object sender, RoutedEventArgs args) =>
        await NavigateForwardAsync();

    private async void OnUp(object sender, RoutedEventArgs args) => await NavigateUpAsync();

    private async Task NavigateBackAsync()
    {
        if (navigation.TryPeek(out var previous))
            await LoadFolderAsync(previous, NavigationAction.Back);
    }

    private async Task NavigateForwardAsync()
    {
        if (forwardHistory.TryPeek(out var next))
            await LoadFolderAsync(next, NavigationAction.Forward);
    }

    private async Task NavigateUpAsync()
    {
        if (parentLocation is { } parent)
            await LoadFolderAsync(parent, NavigationAction.New);
    }

    private async void OnRankingFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (session is not null)
            await LoadRankingAsync();
    }

    private async void OnLeafChanged(object sender, RoutedEventArgs args)
    {
        if (session is not null)
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
            await LoadFolderAsync(
                new(row.Entry.Sample.Reference, current.Drive + "\\" + row.Entry.RelativePath),
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

    private async void OnClosing(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args
    )
    {
        if (allowClose)
            return;
        args.Cancel = true;
        if (closed)
            return;
        try
        {
            await ShutdownAsync();
        }
        finally
        {
            allowClose = true;
            Close();
        }
    }

    private async Task ShutdownAsync()
    {
        closed = true;
        lifetime.Cancel();
        listingCancellation?.Cancel();
        await Task.WhenAll(scans.Values.Select(scan => scan.Task).ToArray());
        foreach (var current in sessions.Values)
            await current.DisposeAsync();
        listingCancellation?.Dispose();
    }
}
