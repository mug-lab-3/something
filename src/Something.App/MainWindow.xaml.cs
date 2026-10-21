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
using Something.Application.Browsing;
using Something.Application.Comparison;
using Something.Application.Scanning;
using Something.Ntfs;
using Something.Windows;
using Windows.Graphics.Imaging;

namespace Something.App;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private DriveScanner scanner;
    private HistoryService? history;
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
    private readonly Dictionary<string, DemoScanStage> demoStages = new(
        StringComparer.OrdinalIgnoreCase
    );

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
        // The overlay covers the view at once; the chick and captions appear and fade together.
        ViewLoadingAnimation.PresentationTarget = ViewLoadingContent;
        InitializeDisplaySettings();
        UpdateSortHeaders();
        InitializeNotifications();
        ViewLoadingAnimation.Settled += (_, _) => UpdateViewLoadingPresentation();
        var arguments = Environment.GetCommandLineArgs();
        int demoHistory = Array.IndexOf(arguments, "--demo-history");
        history = demo
            ? demoHistory >= 0 && demoHistory + 1 < arguments.Length
                ? Composition.OpenHistory(arguments[demoHistory + 1])
                : null
            : CreateConfiguredHistory();
        scanner = Composition.CreateScanner(history);
        HistoryBox.LoadCounts = ComparisonBeforeBox.LoadCounts = ids =>
            history?.ReadRecordCountsAsync(ids, lifetime.Token)
            ?? Task.FromResult<IReadOnlyDictionary<Guid, long>>(new Dictionary<Guid, long>());
        InitializeComparisonControls();
        InitializeHistoryManagement();
        InitializeSettingsPage();
        InitializeNavigationInput();
        if (demo && history is null)
            HistoryBox.Visibility = Visibility.Collapsed;
        Title = AppVersion.WindowTitle;
        AppTitleBar.Title = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Something.ico"));
        SetStartupWindowPresentation(true, StartupRasterizationScale());
        RootLayout.Loaded += (_, _) =>
        {
            SetStartupWindowPresentation(starting, RootLayout.XamlRoot.RasterizationScale);
        };
        var choices = demo
            ? new DriveChoice[]
            {
                new("Demo", Strings.Get("Demo_Drive1Label"), 10L << 30, 4L << 30, true),
                new("Demo2", Strings.Get("Demo_Drive2Label"), 20L << 30, 15L << 30, true),
                new("Demo3", Strings.Get("Demo_Drive3Label"), 100L << 30, 80L << 30, true),
            }
            : LocalDrives();
        cards = choices.Select(choice => new DriveCard(choice)).ToArray();
        foreach (var card in cards)
        {
            card.Invoked += async (_, _) => await SelectDriveAsync(card);
            card.ScanRequested += async (_, _) => await ToggleDriveScanAsync(card);
            card.SetBusy(false);
            if (demo)
                card.SetDemoNextStage(DemoScanStage.Initial);
        }
        selectedCard = cards.FirstOrDefault();
        foreach (var card in cards)
            DriveCards.Children.Add(card);
        UpdateDriveCards();
        AppWindow.Closing += OnClosing;
        RootLayout.Loaded += OnStartupLoaded;
        if (demo)
        {
            ToolTipService.SetToolTip(AppTitleBar, Strings.Get("Demo_Notice"));
        }
    }

    private async Task CaptureSmokeTestAsync(string path)
    {
        try
        {
            // The checks compare rescans and fixed demo files; the changing demo data does not fit.
            if (!Environment.GetCommandLineArgs().Contains("--demo-static"))
                throw new InvalidOperationException("Smoke checks require --demo-static.");
            // The IME (TSF) and other COM-based input only work on an STA UI thread.
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("The UI thread is not STA.");
            if (
                session is null
                || Entries.Items.Count != 3
                || Ranking.Items.Count != 3
                || HasPersistentNotification
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
            LeafBox.IsChecked = true;
            await LoadRankingAsync();
            if (
                Ranking.Items.Count != 1
                || Ranking.Items.Cast<RankingRow>().Single().RelativePath != @"Projects\Archive"
            )
                throw new InvalidOperationException("Leaf filtering did not apply.");
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
            Task second = ToggleDriveScanAsync(cards[1]);
            Task third = ToggleDriveScanAsync(cards[2]);
            if (
                selectedCard != cards[0]
                || session != displayed
                || ViewLoadingOverlay.Visibility != Visibility.Collapsed
                || ViewLoadingAnimation.IsPlaying
            )
                throw new InvalidOperationException(
                    "Background scan requests changed selection or showed an animation."
                );
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
            var previousBackground = sessions[cards[1].Choice.Drive];
            var cancelling = ToggleDriveScanAsync(cards[1]);
            await ToggleDriveScanAsync(cards[1]);
            await cancelling;
            if (
                selectedCard != cards[0]
                || session != displayed
                || sessions[cards[1].Choice.Drive] != previousBackground
                || ViewLoadingOverlay.Visibility != Visibility.Collapsed
                || ViewLoadingAnimation.IsPlaying
            )
                throw new InvalidOperationException(
                    "Cancelling a background scan changed the current view or its retained result."
                );
            await SelectDriveAsync(cards[1]);
            if (session?.Drive != cards[1].Choice.Drive || Entries.Items.Count != 3)
                throw new InvalidOperationException(
                    "Concurrent drive result could not be browsed."
                );
            await SelectDriveAsync(cards[0]);
            if (history is not null)
                await VerifyHistorySelectionAsync();
            await VerifyDisplaySettingsAsync();
            if (Environment.GetCommandLineArgs().Contains("--smoke-recycle"))
                await VerifyFileRecycleAsync();
            if (Environment.GetCommandLineArgs().Contains("--smoke-comparison"))
                await VerifyComparisonModeAsync();
            projects = Folders
                .RootNodes[0]
                .Children.Single(node => node.Content is FolderNode { Name: "Projects" });
            if (HasPersistentNotification)
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
            if (Environment.GetCommandLineArgs().Contains("--smoke-scan-animation"))
                await VerifyScanAnimationAsync();
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
                ? BeginViewUpdate(Strings.Get("Preview_LoadingMessage"))
                : null;
            if (loadingPreview is not null)
                await Task.Delay(660, lifetime.Token);
            await VerifyTabPresentationAsync();
            await VerifyAnalysisWorkspaceAsync();
            await VerifyColumnSortingAsync();
            if (Environment.GetCommandLineArgs().Contains("--smoke-history-management"))
                await VerifyHistoryManagementAsync();
            await VerifyNotificationsAsync();
            if (Environment.GetCommandLineArgs().Contains("--smoke-navigation-input"))
                await VerifyNavigationInputAsync();
            if (Environment.GetCommandLineArgs().Contains("--smoke-clipboard"))
                await VerifyClipboardAsync();
            if (Environment.GetCommandLineArgs().Contains("--smoke-theme"))
                await VerifyThemeSettingsAsync(path);
            if (Environment.GetCommandLineArgs().Contains("--smoke-database-settings"))
                await VerifyDatabaseSettingsAsync(path);
            if (Environment.GetCommandLineArgs().Contains("--smoke-language"))
                await VerifyLanguageAsync(path);
            if (Environment.GetCommandLineArgs().Contains("--smoke-update"))
                await VerifyUpdateNoticeAsync(path);
            if (Environment.GetCommandLineArgs().Contains("--smoke-tree-follow"))
                await VerifyTreeFollowsListAsync(path);
            if (Environment.GetCommandLineArgs().Contains("--smoke-export"))
                await VerifyExportAsync(path);
            if (Environment.GetCommandLineArgs().Contains("--smoke-analysis"))
            {
                WorkspaceTabs.SelectedItem = AnalysisWorkspaceTab;
                if (Environment.GetCommandLineArgs().Contains("--smoke-analysis-files"))
                    AnalysisFoldersOnlyBox.IsChecked = false;
                await LoadRankingAsync();
                RootLayout.UpdateLayout();
                await Task.Delay(200, lifetime.Token); // Let the compositor present newly bound rows.
            }
            var deletionPreview =
                demo && Environment.GetCommandLineArgs().Contains("--smoke-delete-dialog")
                    ? await HistoryManagement.PreviewSyntheticDeleteDialogAsync()
                    : null;
            if (demo && Environment.GetCommandLineArgs().Contains("--smoke-header-hover"))
            {
                VisualStateManager.GoToState(EntryNameHeader, "PointerOver", false);
                RootLayout.UpdateLayout();
                await Task.Delay(200, lifetime.Token);
            }
            if (demo && Environment.GetCommandLineArgs().Contains("--smoke-custom-comparison"))
                await PreviewCustomComparisonAsync();
            var pickerPreview =
                demo && Environment.GetCommandLineArgs().Contains("--smoke-history-picker")
                    ? await HistoryBox.VerifyPreviewAsync(lifetime.Token)
                    : null;
            var toastPreview =
                demo && Environment.GetCommandLineArgs().Contains("--smoke-toasts")
                    ? await PreviewNotificationsAsync()
                    : null;
            await bitmap.RenderAsync(
                toastPreview ?? pickerPreview ?? deletionPreview ?? (FrameworkElement)WindowLayout
            );
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
                                ? Strings.Get("Drive_LocalDisk")
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
        var foreign = await history!.ListAsync($"demo:{cards[1].Choice.Drive}", lifetime.Token);
        await ChangeHistoryAsync(new(foreign[0]));
        if (session != latest)
            throw new InvalidOperationException("History selection crossed volume identities.");
        foreach (var current in sessions.Values)
            await current.DisposeAsync();
        sessions.Clear();
        session = null;
        Entries.ItemsSource = null;
        Ranking.ItemsSource = null;
        await RestoreDriveHistoriesAsync();
        if (sessions.Count != 3 || Entries.Items.Count != 0 || Ranking.Items.Count != 0)
            throw new InvalidOperationException(
                "Drive history restoration rendered files before every drive was ready."
            );
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
        await using var viewUpdate = BeginViewUpdate(Strings.Get("Startup_LoadingDrives"));
        foreach (var card in cards)
        {
            card.IsEnabled = false;
            if (!sessions.ContainsKey(card.Choice.Drive) && !scans.ContainsKey(card))
                card.SetState(Strings.Get("DriveCard_CheckingHistory"));
        }
        try
        {
            // Restore every card before starting visibility projection, tree, list, or ranking queries.
            await RestoreDriveHistoriesAsync();
            if (closed || selectedCard is not { } selected)
                return;
            foreach (var card in cards)
                card.IsEnabled = !historyMaintenance && !changingVisibility;
            if (
                historicalSession is null
                && sessions.TryGetValue(selected.Choice.Drive, out var latest)
            )
            {
                SetStartupMessage(Strings.Get("Startup_PreparingFiles"));
                session = latest;
                await ShowSessionAsync(latest);
            }
            await RefreshHistoryChoicesAsync(selected);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            if (!closed)
                foreach (var card in cards)
                    card.IsEnabled = !historyMaintenance && !changingVisibility;
        }
    }

    private async Task RestoreDriveHistoriesAsync()
    {
        if (history is null)
            return;
        var failures = new List<Exception>();
        foreach (var card in cards)
        {
            try
            {
                SetStartupMessage(Strings.Format("Startup_LoadingHistory", card.Choice.Drive));
                lifetime.Token.ThrowIfCancellationRequested();
                string key = demo
                    ? $"demo:{card.Choice.Drive}"
                    : await Task.Run(
                        () => WindowsVolumeIdentity.ReadHistoryKey(card.Choice.Drive),
                        lifetime.Token
                    );
                if (closed)
                    return;
                volumeKeys[card] = key;
                var rows = await history.ListAsync(key, lifetime.Token);
                if (
                    rows.Length > 0
                    && !sessions.ContainsKey(card.Choice.Drive)
                    && !scans.ContainsKey(card)
                )
                {
                    var latest = await history.OpenAsync(
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
                    }
                }
                else if (!scans.ContainsKey(card))
                    card.SetResult(sessions.GetValueOrDefault(card.Choice.Drive));
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                card.SetState(
                    Strings.Get("DriveCard_HistoryUnavailable"),
                    StatusText.Describe(error)
                );
                failures.Add(
                    new IOException($"{card.Choice.Drive}: {StatusText.Describe(error)}", error)
                );
            }
        }
        if (failures.Count > 0 && !closed)
            ShowError(new AggregateException(Strings.Get("Startup_HistoryFailed"), failures));
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
                ? await history.ListAsync(key, lifetime.Token)
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

    private async void OnHistoryChanged(object? sender, EventArgs args)
    {
        if (!updatingHistoryChoices && HistoryBox.SelectedItem is HistoryRow row)
            await ChangeHistoryAsync(row);
    }

    private async Task ChangeHistoryAsync(HistoryRow row)
    {
        await using var viewUpdate = BeginViewUpdate(
            Strings.Format("History_LoadingScan", row.Entry.CompletedLocal)
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
                    : owned = await history.OpenAsync(
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
            Strings.Format("View_SwitchingDrive", card.Choice.Drive)
        );
        selectedCard = card;
        UpdateScanActivity();
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
        AnalysisCountText.Text = "";
        location = null;
        session = sessions.GetValueOrDefault(card.Choice.Drive);
        if (oldHistory is not null)
            await oldHistory.DisposeAsync();
        if (selectedCard != card || closed)
            return;
        if (session is { } current)
        {
            await ShowSessionAsync(current);
        }
        else
        {
            PathText.Text = Strings.Get("Drive_NotScanned");
            EntryCountText.DataContext = null;
            BackButton.IsEnabled = false;
            EmptyScanText.Visibility = Visibility.Visible;
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
        await using var viewUpdate = BeginViewUpdate(
            Strings.Format("View_ShowingResult", current.Drive)
        );
        await current.SetVisibilityAsync(visibilityOptions, lifetime.Token);
        EmptyScanText.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(
            HistoryBox,
            Strings.Format(
                "History_ScanDetails",
                current.History?.CompletedLocal.ToString("yyyy/MM/dd HH:mm:ss")
                    ?? Strings.Get("History_CurrentScan"),
                current.Elapsed.TotalSeconds,
                current.MetadataUnresolved
            )
        );
        navigation.Clear();
        forwardHistory.Clear();
        parentLocation = null;
        Folders.RootNodes.Clear();
        Entries.ItemsSource = null;
        EntryCountText.DataContext = null;
        Ranking.ItemsSource = null;
        AnalysisCountText.Text = "";
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
        if (!Analyzing)
            await LoadRankingAsync();
    }

    private Task ToggleDriveScanAsync(DriveCard card)
    {
        if (scans.TryGetValue(card, out var running))
        {
            running.Cancellation.Cancel();
            return Task.CompletedTask;
        }
        // A scan-button click targets its own drive; selection belongs to the card body.
        return StartScanAsync(card);
    }

    private Task StartScanAsync(DriveCard? requested = null)
    {
        var card = requested ?? selectedCard;
        if (closed || historyMaintenance || card is null || scans.ContainsKey(card))
            return Task.CompletedTask;
        var operation = new ScanOperation(
            CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)
        );
        scans.Add(card, operation);
        card.SetBusy(true);
        card.SetProgress(true);
        card.SetState(Strings.Get("Scan_Preparing"));
        UpdateScanActivity();
        operation.Task = RunScanAsync(card, operation);
        return operation.Task;
    }

    private async Task RunScanAsync(DriveCard card, ScanOperation operation)
    {
        string drive = card.Choice.Drive;
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
            // Static fixtures keep layout smoke tests reproducible; interactive demos cycle by drive.
            var stage = Environment.GetCommandLineArgs().Contains("--demo-static")
                ? DemoScanStage.Initial
                : demoStages.GetValueOrDefault(drive);
            var next = demo
                ? await scanner.CreateDemoAsync(
                    progress,
                    operation.Cancellation.Token,
                    drive,
                    stage
                )
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
            if (demo)
                demoStages[drive] = DemoScanScenario.Next(stage);
            if (closed)
            {
                await next.DisposeAsync();
                return;
            }
            sessions.TryGetValue(drive, out var previous);
            removals.ClearVolume(next.VolumeKey);
            sessions[drive] = next;
            operation.Progress = null;
            card.SetResult(next);
            card.RefreshVolume();
            try
            {
                if (selectedCard == card)
                {
                    await using var viewUpdate = BeginViewUpdate(
                        Strings.Format("Scan_ApplyingDrive", drive)
                    );
                    historyVersion++;
                    var oldHistory = historicalSession;
                    historicalSession = null;
                    if (!applyingComparison)
                        await ClearComparisonAsync();
                    session = next;
                    navigationVersion++;
                    rankingVersion++;
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
                        ShowToast(
                            cleanupError,
                            InfoBarSeverity.Warning,
                            Strings.Get("Scan_CleanupFailed")
                        );
                    }
                }
                else if (selectedCard is { } selected)
                {
                    // Saving another volume can consolidate older generations in this volume too.
                    await RefreshHistoryChoicesAsync(selected);
                }
                ShowToast(
                    Strings.Format("Scan_Completed", drive, next.Elapsed.TotalSeconds),
                    InfoBarSeverity.Success
                );
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
                ShowToast(Strings.Format("Scan_Cancelled", drive), InfoBarSeverity.Informational);
                card.SetResult(sessions.GetValueOrDefault(drive));
            }
        }
        catch (Exception error)
        {
            card.SetResult(sessions.GetValueOrDefault(drive));
            if (!closed)
                ShowError(
                    new InvalidOperationException($"{drive}: {StatusText.Describe(error)}", error)
                );
        }
        finally
        {
            scans.Remove(card);
            operation.Cancellation.Dispose();
            if (!closed)
            {
                card.SetBusy(false);
                card.SetProgress(false);
                UpdateScanActivity();
                if (selectedCard == card && viewUpdates.Count == 0 && !HasScanPresentation)
                    await ViewLoadingAnimation.WaitForSettledAsync();
                if (demo)
                    card.SetDemoNextStage(
                        Environment.GetCommandLineArgs().Contains("--demo-static")
                            ? DemoScanStage.Initial
                            : demoStages.GetValueOrDefault(drive)
                    );
            }
        }
    }

    private void DisplayProgress(DriveCard card, ScanProgress progress)
    {
        card.SetProgress(true);
        string phase = progress.Phase switch
        {
            ScanPhase.SavingHistory => Strings.Get("ScanPhase_SavingHistory"),
            ScanPhase.WaitingForMemory => Strings.Get("ScanPhase_WaitingForMemory"),
            ScanPhase.Reading => Strings.Get("ScanPhase_Reading"),
            ScanPhase.Aggregating => Strings.Get("ScanPhase_Aggregating"),
            ScanPhase.Saving => Strings.Get("ScanPhase_Saving"),
            ScanPhase.WritingSnapshot => Strings.Get("ScanPhase_WritingSnapshot"),
            _ => Strings.Get("ScanPhase_Completed"),
        };
        string? detail = progress.Work?.Stage switch
        {
            ScanWorkStage.CollectingFolders => Strings.Get("ScanStage_CollectingFolders"),
            ScanWorkStage.BuildingHierarchy => Strings.Get("ScanStage_BuildingHierarchy"),
            ScanWorkStage.AggregatingFiles => Strings.Get("ScanStage_AggregatingFiles"),
            ScanWorkStage.RollingUpFolders => Strings.Get("ScanStage_RollingUpFolders"),
            ScanWorkStage.ValidatingAggregation => Strings.Get("ScanStage_ValidatingAggregation"),
            ScanWorkStage.WritingFiles => Strings.Get("ScanStage_WritingFiles"),
            ScanWorkStage.WritingFolders => Strings.Get("ScanStage_WritingFolders"),
            ScanWorkStage.WritingFolderNames => Strings.Get("ScanStage_WritingFolderNames"),
            ScanWorkStage.FlushingRows => Strings.Get("ScanStage_FlushingRows"),
            ScanWorkStage.ValidatingRelationships => Strings.Get(
                "ScanStage_ValidatingRelationships"
            ),
            ScanWorkStage.BuildingFolderList => Strings.Get("ScanStage_BuildingFolderList"),
            ScanWorkStage.Committing => Strings.Get("ScanStage_Committing"),
            ScanWorkStage.MaintainingHistory => Strings.Get("ScanStage_MaintainingHistory"),
            _ => null,
        };
        string percentage = progress.Fraction is { } value ? $" {Math.Clamp(value, 0, 1):P0}" : "";
        string counts = progress.Work is { Total: { } total } work
            ? Strings.Format("ScanProgress_WorkCount", work.Completed, total)
            : Strings.Format("ScanProgress_CandidateCount", progress.Candidates);
        var step = progress.Step;
        string retry = progress.Work is { RetryAttempt: > 0 } retrying
            ? Strings.Format(
                "ScanProgress_Retry",
                retrying.RetryAttempt,
                retrying.MemoryLimitBytes / (double)(1UL << 30)
            )
            : "";
        string stepText =
            step.Number == 0
                ? Strings.Get("Scan_Preparing")
                : Strings.Format("ScanProgress_Step", step.Number, step.Total);
        card.SetState(
            (step.Number == 0 ? "" : $"{step.Number}/{step.Total} · ")
                + (
                    detail is not null
                        ? detail + percentage
                        : progress.Phase switch
                        {
                            ScanPhase.SavingHistory => Strings.Get("ScanShort_SavingHistory"),
                            ScanPhase.WaitingForMemory => Strings.Get("ScanShort_WaitingForMemory"),
                            ScanPhase.Reading when progress.Fraction is { } fraction =>
                                Strings.Format(
                                    "ScanShort_ReadingPercent",
                                    Math.Clamp(fraction, 0, 1)
                                ),
                            ScanPhase.Reading => Strings.Get("ScanShort_Reading"),
                            ScanPhase.Aggregating => Strings.Get("ScanShort_Aggregating"),
                            ScanPhase.Saving => Strings.Get("ScanShort_Saving"),
                            ScanPhase.WritingSnapshot => Strings.Get("ScanShort_WritingSnapshot"),
                            _ => Strings.Get("ScanPhase_Completed"),
                        }
                ),
            Strings.Format(
                "ScanProgress_Details",
                stepText,
                phase,
                (detail is null ? "" : $" · {detail}"),
                percentage,
                counts,
                retry,
                progress.Elapsed.TotalSeconds
            )
        );
        if (selectedCard == card)
            UpdateScanActivity();
    }

    private Task LoadFolderAsync(FolderLocation target, bool addHistory) =>
        LoadFolderAsync(target, addHistory ? NavigationAction.New : NavigationAction.None);

    private async Task LoadFolderAsync(FolderLocation target, NavigationAction action)
    {
        if (session is not { } current || closed)
            return;
        await using var viewUpdate =
            viewUpdates.Count == 0
                ? (Analyzing ? RankingLoadingRegion : EntryLoadingRegion).BeginUpdate()
                : null;
        int version = ++navigationVersion;
        rankingVersion++; // Discard analysis for the previous scope as soon as navigation starts.
        listingCancellation?.Cancel();
        listingCancellation?.Dispose();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        listingCancellation = request;
        var token = request.Token;
        var order = entryOrder;
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
                includeTotalCount: true,
                descending: order.Descending
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
            ) =>
                browser.ReadFolderAsync(
                    target.Reference,
                    nextPage,
                    false,
                    cancellation,
                    sort: FolderEntrySort.AllocatedBytes,
                    descending: order.Descending
                );
            if (!Comparing && order.Column == BrowserColumn.Name)
            {
                var all = await ReadAllEntriesAsync(browser, target.Reference, token);
                var sorted = await Task.Run(() => BrowserOrdering.Entries(all, order), token);
                FolderEntryPage Page(int index) =>
                    new(
                        [.. sorted.Skip(index * 250).Take(250)],
                        (long)(index + 1) * 250 < sorted.Length,
                        index == 0 ? sorted.Length : null
                    );
                result = Page(0);
                readPage = (index, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return Task.FromResult(Page(index));
                };
            }
            if (Comparing && comparisonReport is not null)
            {
                var changes = await ReadComparedEntriesAsync(current, target.Reference, token);
                var compared = await Task.Run(
                    () => BrowserOrdering.EntryChanges(changes, order),
                    token
                );
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
                entry =>
                {
                    var row = createRow?.Invoke(entry) ?? new(entry);
                    row.RecycleVisibility = FileRecyclePolicy.IsProtected(
                        RecycleTarget(
                            current,
                            target.Path.TrimEnd('\\') + "\\" + entry.Name,
                            entry.Reference,
                            entry.IsDirectory,
                            entry.StandardFileAttributes
                        )
                    )
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    row.Removal = removals.StateFor(
                        RecycleTarget(
                            current,
                            target.Path.TrimEnd('\\') + "\\" + entry.Name,
                            entry.Reference,
                            entry.IsDirectory
                        )
                    );
                    return row;
                }
            );
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
            Entries.ItemsSource = rows;
            EntryCountText.DataContext = rows;
            BackButton.IsEnabled = navigation.Count > 0;
            ForwardButton.IsEnabled = forwardHistory.Count > 0;
            UpButton.IsEnabled = parentLocation is not null;
            // The list is shown already; the tree catches up without delaying it.
            RevealCurrentFolder(target);
            if (Analyzing)
                await LoadRankingAsync();
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
            || EntryCountText.Text != Strings.Format("Count_Items", 600)
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
        var read = ReadTreeChildrenAsync(parent, nextPage);
        treeReads.Add(read);
        try
        {
            await read;
        }
        finally
        {
            treeReads.Remove(read);
        }
    }

    private async Task ReadTreeChildrenAsync(TreeViewNode parent, int nextPage)
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
                            Strings.Get("List_LoadMore"),
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
        {
            if (location != folder.Location && pendingTreeLocation != folder.Location)
                await LoadFolderAsync(folder.Location, true);
        }
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
            Text = Strings.Get("Menu_Open"),
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
            item.Click += async (_, _) => await CopyPathAsync(value);
            menu.Items.Add(item);
        }
        if (directoryPath is not null)
            AddCopy(Strings.Get("Menu_CopyFolderPath"), directoryPath);
        AddCopy(
            directoryPath is null ? Strings.Get("Menu_CopyPath") : Strings.Get("Menu_CopyFilePath"),
            path
        );
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
        {
            string path = current.Drive + "\\" + row.RelativePath;
            string parent = path[..path.LastIndexOf('\\')];
            if (parent == current.Drive)
                parent += "\\";
            ShowPathMenu(Ranking, path, args, row.IsDirectory ? null : parent, row.IsDirectory);
        }
    }

    private async Task CopyPathAsync(string path)
    {
        try
        {
            await WindowsTextClipboard.SetTextAsync(
                WinRT.Interop.WindowNative.GetWindowHandle(this),
                path,
                lifetime.Token
            );
            ShowToast(
                path,
                InfoBarSeverity.Success,
                Strings.Get("Clipboard_Copied"),
                replacePersistent: true
            );
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            ShowToast(
                StatusText.Describe(error),
                InfoBarSeverity.Error,
                Strings.Get("Clipboard_CopyFailed")
            );
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
                if (row.Entry.CanBrowse && await FolderAvailableAsync(path))
                    await LoadFolderAsync(new(row.Entry.Reference, path), true);
                else if (!row.Entry.CanBrowse)
                    ShowToast(
                        Strings.Get("Open_FolderContentsUnavailable"),
                        InfoBarSeverity.Informational,
                        Strings.Get("Open_FolderFailedTitle")
                    );
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
                        throw new IOException(Strings.Get("Open_LocationUnavailable"));
                    ProcessStartInfo start;
                    if (isDirectory)
                    {
                        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
                            throw new IOException(Strings.Get("Open_NotFolder"));
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
                        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                            throw new IOException(Strings.Get("Open_NotFile"));
                        start = new(path) { UseShellExecute = true, Verb = "open" };
                    }
                    using var process = Process.Start(start);
                },
                lifetime.Token
            );
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (IOException error)
            when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            ShowMissingItem(path);
        }
        catch (Exception error)
        {
            ShowToast(
                StatusText.Describe(error),
                InfoBarSeverity.Error,
                Strings.Get("Open_FailedTitle")
            );
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
        var order = analysisOrder;
        bool leafOnly = LeafBox.IsChecked == true;
        bool comparing = Comparing;
        bool foldersOnly = AnalysisFoldersOnlyBox.IsChecked != false;
        var scope = location;
        string prefix =
            scope is null || scope.Reference == current.Root
                ? ""
                : scope.Path[(current.Drive.Length + 1)..].TrimEnd('\\') + "\\";
        bool InScope(string path) => path.StartsWith(prefix, StringComparison.Ordinal);
        try
        {
            var attributes = await current.ReadFolderAttributesAsync(lifetime.Token);
            if (closed || version != rankingVersion || current != session || scope != location)
                return;
            if (!foldersOnly)
            {
                await LoadMixedAnalysisAsync(
                    current,
                    scope,
                    version,
                    order,
                    leafOnly,
                    comparing,
                    attributes,
                    InScope
                );
                return;
            }
            if (comparing)
            {
                var baselineAttributes = comparisonBaseline is { } baseline
                    ? await baseline.ReadFolderAttributesAsync(lifetime.Token)
                    : null;
                var report = comparisonReport;
                var changed = report is null
                    ? Array.Empty<FolderChange>()
                    : await Task.Run(
                        () =>
                            BrowserOrdering.FolderChanges(
                                FilterChanges(report, leafOnly)
                                    .Where(row => InScope(row.AfterPath!)),
                                order
                            ),
                        lifetime.Token
                    );
                if (!closed && version == rankingVersion && current == session && scope == location)
                    PublishAnalysis(
                        changed.Length,
                        index => new RankingRow(changed[index], current.Drive)
                        {
                            StandardFileAttributes = changed[index].AfterPath is null
                                ? baselineAttributes?.GetValueOrDefault(changed[index].Reference)
                                : attributes.GetValueOrDefault(changed[index].Reference),
                        },
                        version,
                        current
                    );
                return;
            }
            var facts = await current.ReadFolderAnalysisAsync(
                new(LeafOnly: leafOnly),
                lifetime.Token
            );
            var rows = await Task.Run(
                () => BrowserOrdering.Folders(facts.Where(row => InScope(row.RelativePath)), order),
                lifetime.Token
            );
            if (closed || version != rankingVersion || current != session || scope != location)
                return;
            PublishAnalysis(
                rows.Length,
                index => new RankingRow(rows[index], current.Drive)
                {
                    StandardFileAttributes = attributes.GetValueOrDefault(
                        rows[index].Sample.Reference
                    ),
                },
                version,
                current
            );
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed && version == rankingVersion && current == session && scope == location)
                ShowError(error);
        }
    }

    private async void OnRankInvoked(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (Ranking.SelectedItem is RankingRow row)
            await OpenAnalysisFolderAsync(row);
    }

    private async Task OpenAnalysisFolderAsync(RankingRow row)
    {
        if (session is { } current)
        {
            if (!row.IsDirectory)
            {
                await OpenPathAsync(current.Drive + "\\" + row.RelativePath, false);
                return;
            }
            if (!await FolderAvailableAsync(current.Drive + "\\" + row.RelativePath))
                return;
            await LoadFolderAsync(
                new(row.Reference, current.Drive + "\\" + row.RelativePath),
                true
            );
        }
    }

    private void PublishAnalysis(
        int count,
        Func<int, RankingRow> createRow,
        int version,
        ScanSession current
    )
    {
        Ranking.ItemsSource = new AnalysisRowCollection(
            count,
            index =>
            {
                var row = createRow(index);
                row.RecycleVisibility = FileRecyclePolicy.IsProtected(
                    RecycleTarget(
                        current,
                        current.Drive + "\\" + row.RelativePath,
                        row.Reference,
                        row.IsDirectory,
                        row.StandardFileAttributes
                    )
                )
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                row.Removal = removals.StateFor(
                    RecycleTarget(
                        current,
                        current.Drive + "\\" + row.RelativePath,
                        row.Reference,
                        row.IsDirectory
                    )
                );
                return row;
            },
            () => !closed && version == rankingVersion && current == session,
            lifetime.Token
        );
        AnalysisCountText.Text = Strings.Format("Count_Items", count);
    }

    private void ShowError(Exception error)
    {
        if (closed)
            return;
        bool denied =
            error is UnauthorizedAccessException
            || error.InnerException is Win32Exception { NativeErrorCode: 5 };
        ShowToast(
            denied ? Strings.Get("Scan_AdminRequiredMessage") : StatusText.Describe(error),
            InfoBarSeverity.Error,
            denied ? Strings.Get("Scan_AdminRequiredTitle") : Strings.Get("Error_Title")
        );
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
        toastTimer.Stop();
        ViewLoadingAnimation.Cancel();
        StartupAnimation.Cancel();
        EntryLoadingRegion.Cancel();
        RankingLoadingRegion.Cancel();
        foreach (var indicator in treeLoads.Values)
            indicator.Dispose();
        lifetime.Cancel();
        recycleDialog?.Hide();
        databaseDeleteDialog?.Hide();
        exportDialog?.Hide();
        HistoryBox.CloseDialog();
        ComparisonBeforeBox.CloseDialog();
        HistoryManagement.CloseForShutdown();
        listingCancellation?.Cancel();
        await Task.WhenAll(scans.Values.Select(scan => scan.Task).ToArray());
        await startupTask;
        await Task.WhenAll(historyTasks.Concat(settingsTasks).ToArray());
        await Task.WhenAll([treeReveal, updateCheck, .. treeReads]);
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
