using System.ComponentModel;
using System.Diagnostics;
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
using Something.Windows;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

namespace Something.App;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly DriveScanner scanner;
    private readonly ScanHistoryArchive? history;
    private readonly Dictionary<DriveCard, string> volumeKeys = [];
    private ScanSession? historicalSession;
    private int historyVersion;
    private bool updatingHistoryChoices;
    private Task startupTask = Task.CompletedTask;
    private readonly HashSet<Task> historyTasks = [];
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
        ViewLoadingAnimation.Settled += (_, _) => UpdateViewLoadingPresentation();
        var arguments = Environment.GetCommandLineArgs();
        int demoHistory = Array.IndexOf(arguments, "--demo-history");
        history = demo
            ? demoHistory >= 0 && demoHistory + 1 < arguments.Length
                ? new ScanHistoryArchive(arguments[demoHistory + 1])
                : null
            : ScanHistoryArchive.CreateDefault();
        scanner = new(history);
        InitializeComparisonControls();
        InitializeHistoryManagement();
        if (demo && history is null)
            HistoryBox.Visibility = Visibility.Collapsed;
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
        if (!demo)
        {
            RootLayout.Loaded += async (_, _) =>
            {
                startupTask = InitializeHistoryAsync();
                await startupTask;
            };
        }
        if (demo)
        {
            StatusText.Text = "デモモード。実ディスクやファイルは読み取りません。";
            RootLayout.Loaded += async (_, _) =>
            {
                var arguments = Environment.GetCommandLineArgs();
                if (arguments.Contains("--preview-loading"))
                {
                    await PreviewLoadingAnimationAsync();
                    return;
                }
                await StartScanAsync();
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
            await VerifyTreeHorizontalScrollingAsync();
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
            if (history is not null)
                await VerifyHistorySelectionAsync();
            if (Environment.GetCommandLineArgs().Contains("--smoke-comparison"))
                await VerifyComparisonModeAsync();
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
            await using var loadingPreview = Environment
                .GetCommandLineArgs()
                .Contains("--smoke-loading")
                ? BeginViewUpdate("レコードを確認しています · アニメーションのプレビュー")
                : null;
            if (loadingPreview is not null)
                await Task.Delay(660, lifetime.Token);
            if (Environment.GetCommandLineArgs().Contains("--smoke-history-management"))
                await VerifyHistoryManagementAsync();
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
                "UI loaded; back/forward/parent navigation, lazy tree, filters, concurrent drive scans, 600-row incremental scrolling, cancellation, optional history selection and requested comparison checks passed; synthetic data only."
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

    private async Task VerifyHistorySelectionAsync()
    {
        var original = session ?? throw new InvalidOperationException("No demo session.");
        await StartScanAsync(cards[0]);
        var latest = session ?? throw new InvalidOperationException("No rescanned session.");
        if (original.ScanId == latest.ScanId || HistoryBox.Items.Count < 2 || !HistoryBox.IsEnabled)
            throw new InvalidOperationException("Completed scan was not added to history.");
        var oldRow = HistoryBox
            .Items.Cast<HistoryRow>()
            .Single(row => row.Entry.Id == original.ScanId);
        await ChangeHistoryAsync(oldRow);
        if (
            session?.ScanId != original.ScanId
            || historicalSession is null
            || Entries.Items.Count != 3
        )
            throw new InvalidOperationException("Saved history could not be browsed.");
        var newRow = HistoryBox
            .Items.Cast<HistoryRow>()
            .Single(row => row.Entry.Id == latest.ScanId);
        await ChangeHistoryAsync(newRow);
        if (session != latest || historicalSession is not null)
            throw new InvalidOperationException("Latest scan could not be restored from history.");
        var foreign = await Task.Run(
            () => history!.List($"demo:{cards[1].Choice.Drive}", lifetime.Token),
            lifetime.Token
        );
        await ChangeHistoryAsync(new(foreign[0]));
        if (session != latest)
            throw new InvalidOperationException("History selection crossed volume identities.");
        foreach (var current in sessions.Values)
            await current.DisposeAsync();
        sessions.Clear();
        session = null;
        await InitializeHistoryAsync();
        if (sessions.Count != 3 || session?.ScanId != latest.ScanId || !HistoryBox.IsEnabled)
            throw new InvalidOperationException(
                "Latest histories did not reload after releasing all sessions."
            );
    }

    private async Task InitializeHistoryAsync()
    {
        if (history is null)
            return;
        try
        {
            foreach (var card in cards)
            {
                await using var viewUpdate =
                    selectedCard == card
                        ? BeginViewUpdate(
                            $"{card.Choice.Drive} の保存済みの結果を読み込んでいます…"
                        )
                        : null;
                string key = demo
                    ? $"demo:{card.Choice.Drive}"
                    : await Task.Run(
                        () => WindowsVolumeIdentity.ReadHistoryKey(card.Choice.Drive),
                        lifetime.Token
                    );
                if (closed)
                    return;
                volumeKeys[card] = key;
                var rows = await Task.Run(() => history.List(key, lifetime.Token), lifetime.Token);
                if (
                    rows.Length > 0
                    && !sessions.ContainsKey(card.Choice.Drive)
                    && !scans.ContainsKey(card)
                )
                {
                    int viewingVersion = historyVersion;
                    var latest = await ScanSession.OpenHistoryAsync(
                        history,
                        rows[0].Id,
                        card.Choice.Drive,
                        lifetime.Token
                    );
                    if (
                        closed
                        || sessions.ContainsKey(card.Choice.Drive)
                        || scans.ContainsKey(card)
                    )
                        await latest.DisposeAsync();
                    else
                    {
                        sessions[card.Choice.Drive] = latest;
                        card.SetResult(latest);
                        if (
                            selectedCard == card
                            && viewingVersion == historyVersion
                            && historicalSession is null
                        )
                        {
                            session = latest;
                            await ShowSessionAsync(latest);
                            if (selectedCard == card)
                                StatusText.Text =
                                    $"保存済みの履歴 · {rows[0].CompletedLocal:yyyy/MM/dd HH:mm:ss} · 取得時点の観測";
                        }
                    }
                }
                await RefreshHistoryChoicesAsync(card);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async Task RefreshHistoryChoicesAsync(DriveCard card)
    {
        if (history is null || closed || selectedCard != card)
            return;
        try
        {
            if (sessions.GetValueOrDefault(card.Choice.Drive)?.History is { } saved)
                volumeKeys[card] = saved.VolumeKey;
            var entries = volumeKeys.TryGetValue(card, out var key)
                ? await Task.Run(() => history.List(key, lifetime.Token), lifetime.Token)
                : [];
            if (closed || selectedCard != card)
                return;
            var rows = entries.Select((entry, index) => new HistoryRow(entry, index == 0)).ToList();
            if (
                historicalSession?.History is { } viewing
                && rows.All(row => row.Entry.Id != viewing.Id)
            )
                rows.Add(new(viewing)); // A retired snapshot stays readable until this view releases it.
            updatingHistoryChoices = true;
            try
            {
                HistoryBox.ItemsSource = rows;
                HistoryBox.SelectedItem = rows.FirstOrDefault(row =>
                    row.Entry.Id == session?.ScanId
                );
                HistoryBox.IsEnabled = rows.Count > 0;
            }
            finally
            {
                updatingHistoryChoices = false;
            }
            UpdateComparisonChoices(rows);
            UpdateComparisonLayout();
            if (Comparing && !applyingComparison && ComparisonNeedsRefresh())
                await TrackComparisonAsync();
            await RefreshManagementIfVisibleAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async void OnHistoryChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!updatingHistoryChoices && HistoryBox.SelectedItem is HistoryRow row)
            await ChangeHistoryAsync(row);
    }

    private async Task ChangeHistoryAsync(HistoryRow row)
    {
        await using var viewUpdate = BeginViewUpdate(
            $"{row.Entry.CompletedLocal:yyyy/MM/dd HH:mm:ss} の記録を読み込んでいます…"
        );
        var task = LoadHistorySelectionAsync(row);
        historyTasks.Add(task);
        try
        {
            await task;
        }
        finally
        {
            historyTasks.Remove(task);
        }
    }

    private async Task LoadHistorySelectionAsync(HistoryRow row)
    {
        if (
            history is null
            || closed
            || selectedCard is not { } card
            || session?.ScanId == row.Entry.Id
        )
            return;
        string? currentVolume =
            sessions.GetValueOrDefault(card.Choice.Drive)?.History?.VolumeKey
            ?? volumeKeys.GetValueOrDefault(card);
        if (currentVolume is not null && row.Entry.VolumeKey != currentVolume)
            return;
        int version = ++historyVersion;
        if (Comparing && !applyingComparison)
            await ClearComparisonAsync();
        if (closed || selectedCard != card || version != historyVersion)
            return;
        ScanSession? owned = null;
        try
        {
            var latest = sessions.GetValueOrDefault(card.Choice.Drive);
            var next =
                latest?.ScanId == row.Entry.Id
                    ? latest
                    : owned = await ScanSession.OpenHistoryAsync(
                        history,
                        row.Entry.Id,
                        card.Choice.Drive,
                        lifetime.Token
                    );
            if (closed || selectedCard != card || version != historyVersion)
                return;
            var previous = historicalSession;
            historicalSession = owned;
            owned = null;
            session = next;
            try
            {
                await ShowSessionAsync(next);
                if (selectedCard == card && session == next)
                {
                    StatusText.Text =
                        $"履歴 · {row.Entry.CompletedLocal:yyyy/MM/dd HH:mm:ss} · 管理領域未解決 {next.MetadataUnresolved:N0} · 取得時点の観測";
                    await RefreshHistoryChoicesAsync(card);
                }
            }
            finally
            {
                if (previous is not null)
                    await previous.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed && selectedCard == card && version == historyVersion)
            {
                ShowError(error);
                await RefreshHistoryChoicesAsync(card);
            }
        }
        finally
        {
            if (owned is not null)
                await owned.DisposeAsync();
        }
    }

    private async Task SelectDriveAsync(DriveCard card)
    {
        if (closed || card == selectedCard)
            return;
        await using var viewUpdate = BeginViewUpdate(
            $"{card.Choice.Drive} の表示に切り替えています…"
        );
        selectedCard = card;
        await ClearComparisonAsync();
        if (closed || selectedCard != card)
            return;
        UpdateComparisonChoices([]);
        updatingHistoryChoices = true;
        try
        {
            HistoryBox.ItemsSource = null;
            HistoryBox.IsEnabled = false;
        }
        finally
        {
            updatingHistoryChoices = false;
        }
        historyVersion++;
        var oldHistory = historicalSession;
        historicalSession = null;
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
        if (oldHistory is not null)
            await oldHistory.DisposeAsync();
        if (selectedCard != card || closed)
            return;
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
        await RefreshHistoryChoicesAsync(card);
    }

    private async Task ShowSessionAsync(ScanSession current)
    {
        await using var viewUpdate = BeginViewUpdate($"{current.Drive} の結果を表示しています…");
        navigation.Clear();
        forwardHistory.Clear();
        parentLocation = null;
        Folders.RootNodes.Clear();
        Entries.ItemsSource = null;
        EntryCountText.DataContext = null;
        Ranking.ItemsSource = null;
        location = new(current.Root, current.Drive + "\\");
        var root = new TreeViewNode
        {
            Content = new FolderNode(
                current.Drive,
                location,
                Comparing: Comparing,
                Comparison: GetFolderComparison(current.Root),
                AllocatedBytes: current.RootSample.Entries?.SubtreeData.AllocatedBytes,
                CapacityIncomplete: !current.RootSample.DirectoryCapacityObserved
                    || current.RootSample.SubtreeUnobservedDirectoryCount != 0
                    || current.RootSample.Entries?.SubtreeUnresolvedCount != 0
            ),
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
            if (demo && history is not null)
            {
                try
                {
                    await next.SaveHistoryAsync(history, operation.Cancellation.Token);
                }
                catch
                {
                    await next.DisposeAsync();
                    throw;
                }
            }
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
                    await using var viewUpdate = BeginViewUpdate(
                        $"{drive} のスキャン結果を反映しています…"
                    );
                    historyVersion++;
                    var oldHistory = historicalSession;
                    historicalSession = null;
                    if (!applyingComparison)
                        await ClearComparisonAsync();
                    session = next;
                    navigationVersion++;
                    rankingVersion++;
                    StatusText.Text =
                        $"{(demo ? "デモ / " : "")}{next.Elapsed.TotalSeconds:F1}秒で完了 · 管理領域未解決 {next.MetadataUnresolved:N0} · 取得時点の観測";
                    try
                    {
                        await ShowSessionAsync(next);
                    }
                    finally
                    {
                        if (oldHistory is not null)
                            await oldHistory.DisposeAsync();
                    }
                    await RefreshHistoryChoicesAsync(card);
                    if (history?.MaintenanceError is { } cleanupError)
                    {
                        MessageBar.Title =
                            "履歴は保存済みですが、古い履歴の整理を完了できませんでした";
                        MessageBar.Message = cleanupError;
                        MessageBar.Severity = InfoBarSeverity.Warning;
                        MessageBar.IsOpen = true;
                    }
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
            ScanPhase.SavingHistory => "履歴を保存中",
            ScanPhase.WaitingForMemory => "メモリ予算の空きを待機中",
            ScanPhase.Reading => "メタデータを取得中",
            ScanPhase.Aggregating => "フォルダ容量を集計中",
            ScanPhase.Saving => "結果を保存中",
            ScanPhase.WritingSnapshot => "結果をファイルへ書き出し中",
            _ => "スキャン完了",
        };
        string? detail = progress.Work?.Stage switch
        {
            ScanWorkStage.CollectingFolders => "フォルダを抽出中",
            ScanWorkStage.BuildingHierarchy => "階層を構築中",
            ScanWorkStage.AggregatingFiles => "ファイル容量を集計中",
            ScanWorkStage.RollingUpFolders => "サブフォルダを集計中",
            ScanWorkStage.ValidatingAggregation => "集計結果を検証中",
            ScanWorkStage.WritingFiles => "ファイル情報を保存中",
            ScanWorkStage.WritingFolders => "フォルダ容量を保存中",
            ScanWorkStage.WritingFolderNames => "フォルダ名を保存中",
            ScanWorkStage.FlushingRows => "保存データを反映中",
            ScanWorkStage.ValidatingRelationships => "保存データを検証中",
            ScanWorkStage.BuildingFolderList => "一覧用データを生成中",
            ScanWorkStage.Committing => "保存を確定中",
            ScanWorkStage.MaintainingHistory => "履歴を整理中",
            _ => null,
        };
        string percentage = progress.Fraction is { } value ? $" {Math.Clamp(value, 0, 1):P0}" : "";
        string counts = progress.Work is { Total: { } total } work
            ? $" · {work.Completed:N0}/{total:N0} 件"
            : $" · {progress.Candidates:N0} 件";
        var step = progress.Step;
        string retry = progress.Work is { RetryAttempt: > 0 } retrying
            ? $" · 再試行 {retrying.RetryAttempt}（メモリ上限 {retrying.MemoryLimitBytes / (double)(1UL << 30):F1} GiB）"
            : "";
        string stepText = step.Number == 0 ? "準備中" : $"ステップ {step.Number}/{step.Total}";
        if (selectedCard == card)
            StatusText.Text =
                $"{stepText} · {phase}{(detail is null ? "" : $" · {detail}")}{percentage}{counts}{retry} · {progress.Elapsed.TotalSeconds:F1} 秒";
        card.SetState(
            (step.Number == 0 ? "" : $"{step.Number}/{step.Total} · ")
                + (
                    detail is not null
                        ? detail + percentage
                        : progress.Phase switch
                        {
                            ScanPhase.SavingHistory => "履歴保存中",
                            ScanPhase.WaitingForMemory => "メモリ待ち",
                            ScanPhase.Reading when progress.Fraction is { } fraction =>
                                $"走査中 {Math.Clamp(fraction, 0, 1):P0}",
                            ScanPhase.Reading => "走査中",
                            ScanPhase.Aggregating => "集計中",
                            ScanPhase.Saving => "保存中",
                            ScanPhase.WritingSnapshot => "書き出し中",
                            _ => "スキャン完了",
                        }
                )
        );
    }

    private Task LoadFolderAsync(FolderLocation target, bool addHistory) =>
        LoadFolderAsync(target, addHistory ? NavigationAction.New : NavigationAction.None);

    private async Task LoadFolderAsync(FolderLocation target, NavigationAction action)
    {
        if (session is not { } current || closed)
            return;
        await using var viewUpdate =
            viewUpdates.Count == 0 ? EntryLoadingRegion.BeginUpdate() : null;
        int version = ++navigationVersion;
        listingCancellation?.Cancel();
        listingCancellation?.Dispose();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        listingCancellation = request;
        var token = request.Token;
        BackButton.IsEnabled = ForwardButton.IsEnabled = UpButton.IsEnabled = false;
        try
        {
            var browser = ComparisonBrowser(current, target.Reference);
            var result = await browser.ReadFolderAsync(
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
                    && await browser.ReadParentAsync(
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
            Func<FolderEntry, EntryRow>? createRow = Comparing
                ? entry => new(entry, comparing: true)
                : null;
            Func<int, CancellationToken, Task<FolderEntryPage>> readPage = (
                nextPage,
                cancellation
            ) => browser.ReadFolderAsync(target.Reference, nextPage, false, cancellation);
            if (Comparing && comparisonReport is not null)
            {
                var compared = await ReadComparedEntriesAsync(current, target.Reference, token);
                if (closed || version != navigationVersion || session != current)
                    return;
                FolderEntryPage Page(int index) =>
                    new(
                        [.. compared.Skip(index * 250).Take(250).Select(row => row.Entry)],
                        (long)(index + 1) * 250 < compared.Length,
                        index == 0 ? compared.Length : null
                    );
                result = Page(0);
                readPage = (index, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return Task.FromResult(Page(index));
                };
                var lookup = compared.ToDictionary(row => (row.Entry.Reference, row.Entry.Name));
                createRow = entry => new(entry, lookup[(entry.Reference, entry.Name)], true);
            }
            var rows = new FolderEntryCollection(
                result,
                readPage,
                error =>
                {
                    if (!closed && session == current && version == navigationVersion)
                        ShowError(error);
                },
                token,
                createRow
            );
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
        if (treeLoads.ContainsKey(parent))
            return;
        using var indicator = new DelayedActivity((_, visible) => folder.IsLoading = visible);
        using var pending = indicator.Begin();
        treeLoads.Add(parent, indicator);
        int requestVersion = comparisonVersion;
        parent.HasUnrealizedChildren = false;
        try
        {
            var result = await ComparisonBrowser(current, folder.Location.Reference)
                .ReadFolderAsync(folder.Location.Reference, nextPage, true, lifetime.Token);
            if (closed || session != current || requestVersion != comparisonVersion)
                return;
            if (Comparing && comparisonReport is not null)
            {
                var compared = await ReadComparedEntriesAsync(
                    current,
                    folder.Location.Reference,
                    lifetime.Token
                );
                if (closed || session != current || requestVersion != comparisonVersion)
                    return;
                var directories = compared
                    .Where(row => row.Entry.IsDirectory && row.Entry.CanBrowse)
                    .Select(row => row.Entry)
                    .ToArray();
                result = new(
                    [.. directories.Skip(nextPage * 250).Take(250)],
                    (long)(nextPage + 1) * 250 < directories.Length
                );
            }
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
                            ),
                            Comparing: Comparing,
                            Comparison: GetFolderComparison(entry.Reference),
                            AllocatedBytes: entry.Data?.AllocatedBytes,
                            CapacityIncomplete: entry.CapacityIncomplete || !entry.NamesComplete
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
        catch (OperationCanceledException)
            when (lifetime.IsCancellationRequested || requestVersion != comparisonVersion) { }
        catch (Exception error)
        {
            if (closed || requestVersion != comparisonVersion)
                return;
            parent.HasUnrealizedChildren = true;
            ShowError(error);
        }
        finally
        {
            treeLoads.Remove(parent);
        }
    }

    private async void OnExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        var selected = Folders.SelectedNode;
        if (args.Node.HasUnrealizedChildren)
            await LoadChildrenAsync(args.Node, 0);
        if (Folders.SelectedNode == selected)
            QueueTreeVisibility(args.Node);
    }

    private async void OnFolderInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node || node.Content is not FolderNode folder)
            return;
        QueueTreeVisibility(node);
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
        string? directoryPath = null,
        bool isDirectory = true
    )
    {
        var menu = new MenuFlyout();
        menu.Opened += (_, _) =>
        {
            MenuCursor.RestoreArrow();
            // Let popup creation finish before correcting any late cursor update.
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (!closed && menu.IsOpen)
                        MenuCursor.RestoreArrow();
                }
            );
        };
        var open = new MenuFlyoutItem
        {
            Text = "開く",
            Icon = new SymbolIcon(isDirectory ? Symbol.Folder : Symbol.OpenFile),
        };
        // Capture the clicked row's original path, independently of later selection changes.
        open.Click += async (_, _) => await OpenPathAsync(path, isDirectory);
        menu.Items.Add(open);
        menu.Items.Add(new MenuFlyoutSeparator());
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
                row.Entry.IsDirectory ? null : parent.Path,
                isDirectory: row.Entry.IsDirectory
            );
    }

    private void OnRankContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (
            session is { } current
            && FindContextRow(Ranking, args.OriginalSource as DependencyObject) is RankingRow row
        )
            ShowPathMenu(Ranking, current.Drive + "\\" + row.RelativePath, args);
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
            && FindContextRow(Entries, args.OriginalSource as DependencyObject) is EntryRow row
        )
        {
            string path = parent.Path.TrimEnd('\\') + "\\" + row.Entry.Name;
            if (row.Entry.IsDirectory)
            {
                if (row.Entry.CanBrowse)
                    await LoadFolderAsync(new(row.Entry.Reference, path), true);
            }
            else
                await OpenPathAsync(path, isDirectory: false);
            args.Handled = true;
        }
    }

    private async Task OpenPathAsync(string path, bool isDirectory)
    {
        try
        {
            await Task.Run(
                () =>
                {
                    // Paths are passed directly to the Windows shell, never through a command shell.
                    if (!Path.IsPathFullyQualified(path))
                        throw new IOException("開く対象の絶対パスを取得できませんでした。");
                    ProcessStartInfo start;
                    if (isDirectory)
                    {
                        if (!Directory.Exists(path))
                            throw new DirectoryNotFoundException(
                                "フォルダーが見つからないか、アクセスできません。"
                            );
                        start = new(
                            Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                "explorer.exe"
                            )
                        )
                        {
                            UseShellExecute = true,
                        };
                        start.ArgumentList.Add(path);
                    }
                    else
                    {
                        if (!File.Exists(path))
                            throw new FileNotFoundException(
                                "ファイルが見つからないか、アクセスできません。"
                            );
                        start = new(path) { UseShellExecute = true, Verb = "open" };
                    }
                    using var process = Process.Start(start);
                },
                lifetime.Token
            );
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            ShowError(error);
        }
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
        if (session is not null && !updatingComparisonChoices)
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
        await using var viewUpdate =
            viewUpdates.Count == 0 ? RankingLoadingRegion.BeginUpdate() : null;
        int version = ++rankingVersion;
        try
        {
            if (Comparing && ChangeOrderBox.SelectedIndex > 0)
            {
                var report = comparisonReport;
                int depth = DepthBox.SelectedIndex;
                bool leafOnly = LeafBox.IsChecked == true;
                var order = ChangeOrderBox.SelectedIndex switch
                {
                    2 => FolderChangeRanking.Shrinkage,
                    3 => FolderChangeRanking.Magnitude,
                    _ => FolderChangeRanking.Growth,
                };
                var changed = report is null
                    ? Array.Empty<RankingRow>()
                    : await Task.Run(
                        () =>
                            (report with { Folders = [.. FilterChanges(report, depth, leafOnly)] })
                                .Rank(order)
                                .Select(row => new RankingRow(row, current.Drive))
                                .ToArray(),
                        lifetime.Token
                    );
                if (!closed && version == rankingVersion && current == session)
                    Ranking.ItemsSource = changed;
                return;
            }
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
            Ranking.ItemsSource = rows.Select(r => new RankingRow(
                    r,
                    current.Drive,
                    Comparing ? GetFolderComparison(r.Sample.Reference) : null,
                    Comparing
                ))
                .ToArray();
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
                new(row.Reference, current.Drive + "\\" + row.RelativePath),
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
        ViewLoadingAnimation.Cancel();
        EntryLoadingRegion.Cancel();
        RankingLoadingRegion.Cancel();
        foreach (var indicator in treeLoads.Values)
            indicator.Dispose();
        lifetime.Cancel();
        listingCancellation?.Cancel();
        await Task.WhenAll(scans.Values.Select(scan => scan.Task).ToArray());
        await startupTask;
        await Task.WhenAll(historyTasks.ToArray());
        if (historicalSession is not null)
            await historicalSession.DisposeAsync();
        if (comparisonBaseline is not null)
            await comparisonBaseline.DisposeAsync();
        comparisonCancellation?.Dispose();
        foreach (var current in sessions.Values)
            await current.DisposeAsync();
        listingCancellation?.Dispose();
    }
}
