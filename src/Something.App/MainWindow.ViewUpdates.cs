using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Something.App;

public sealed partial class MainWindow
{
    // Operations can nest (history -> comparison -> folder/ranking). Keep the whole view
    // covered until every active operation has unwound, including cancelled older work.
    private readonly List<ViewUpdate> viewUpdates = [];
    private readonly Dictionary<TreeViewNode, DelayedActivity> treeLoads = [];

    // Tree reads use the open sessions, so shutdown waits for them before disposing those.
    private readonly HashSet<Task> treeReads = [];

    private sealed class ViewUpdate(MainWindow window, string message, LoadingAnimationKind kind)
        : IDisposable,
            IAsyncDisposable
    {
        private MainWindow? owner = window;
        public string Message { get; } = message;
        public LoadingAnimationKind Kind { get; } = kind;

        public void Dispose()
        {
            var current = owner;
            owner = null;
            if (current is null)
                return;
            current.viewUpdates.Remove(this);
            current.UpdateViewLoadingPresentation();
        }

        public async ValueTask DisposeAsync()
        {
            var current = owner;
            Dispose();
            if (
                current is { closed: false }
                && current.viewUpdates.Count == 0
                && !current.HasScanPresentation
            )
                await current.ViewLoadingAnimation.WaitForSettledAsync();
        }
    }

    private ViewUpdate BeginViewUpdate(
        string message,
        LoadingAnimationKind kind = LoadingAnimationKind.Inspect
    )
    {
        var update = new ViewUpdate(this, message, kind);
        viewUpdates.Add(update);
        UpdateViewLoadingPresentation();
        return update;
    }

    private void UpdateViewLoadingPresentation()
    {
        if (closed)
            return;
        if (starting)
        {
            ViewLoadingOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        bool loading = viewUpdates.Count != 0 || HasScanPresentation;
        if (!loading)
        {
            RootLayout.UpdateLayout(); // Lay out the complete result while it is still covered.
            loading = viewUpdates.Count != 0 || HasScanPresentation;
        }
        if (loading)
        {
            ViewLoadingOverlay.Visibility = Visibility.Visible;
            // Maintenance > foreground view loading > background scan. Select the mode,
            // title and message from the same owner so scan progress cannot overwrite them.
            var presentation =
                viewUpdates.LastOrDefault(update => update.Kind != LoadingAnimationKind.Inspect)
                ?? viewUpdates.LastOrDefault();
            bool showingScan = presentation is null && HasScanPresentation;
            var kind = presentation?.Kind ?? ScanPresentationKind;
            ViewLoadingAnimation.SetKind(
                kind,
                interruptCurrentCycle: presentation?.Kind == LoadingAnimationKind.Inspect
            );
            ViewLoadingTitle.Text = showingScan
                ? Strings.Format("Scan_InProgress", selectedCard!.Choice.Drive)
                : kind switch
                {
                    LoadingAnimationKind.DeleteHistory => Strings.Get(
                        "HistoryManagement_Organizing"
                    ),
                    LoadingAnimationKind.OptimizeDatabase => Strings.Get("Database_Optimizing"),
                    LoadingAnimationKind.Export => Strings.Get("Export_Writing"),
                    _ => Strings.Get("View_Preparing"),
                };
            if (presentation is not null)
                ViewLoadingText.Text = presentation.Message;
            else if (showingScan)
                ViewLoadingText.Text = scans[selectedCard!].Finished
                    ? viewUpdates.LastOrDefault()?.Message ?? Strings.Get("Scan_Applying")
                    : selectedCard!.StateDescription;
            ViewLoadingHint.Text =
                kind == LoadingAnimationKind.Inspect
                    ? Strings.Get("View_LoadingHint")
                    : Strings.Get("View_BusyHint");
        }
        ViewLoadingAnimation.SetBusy(loading);
        bool covered = loading || ViewLoadingAnimation.IsPlaying;
        ViewLoadingOverlay.Visibility = covered ? Visibility.Visible : Visibility.Collapsed;
        ViewHost.IsEnabled = !covered;
        SettingsButton.IsEnabled = !covered && !historyMaintenance && !changingVisibility;
        ViewControls.Opacity = ResultsContent.Opacity = EmptyScanText.Opacity = covered ? 0 : 1;
        UpdateSettingsAvailability();
    }

    private async Task PreviewLoadingAnimationAsync()
    {
        await using var preview = BeginViewUpdate(Strings.Get("Preview_LoadingMessage"));
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task VerifyViewUpdatesAsync()
    {
        void Verify(bool loading)
        {
            if (
                ViewLoadingOverlay.Visibility
                    != (loading ? Visibility.Visible : Visibility.Collapsed)
                || new[] { ViewControls, (FrameworkElement)ResultsContent, EmptyScanText }.Any(
                    element => element.Opacity != (loading ? 0 : 1)
                )
                || ViewHost.IsEnabled == loading
            )
                throw new InvalidOperationException(
                    "Result switching exposed a partial or interactive view."
                );
        }

        await ViewLoadingAnimation.WaitForSettledAsync();
        Verify(false);
        await VerifyDelayedUpdatesAsync();
        var reducedMotion = new RecordLoadingAnimation();
        reducedMotion.SetBusy(true, animationsEnabled: false);
        reducedMotion.SetBusy(false, animationsEnabled: false);
        if (reducedMotion.IsPlaying || !reducedMotion.WaitForSettledAsync().IsCompleted)
            throw new InvalidOperationException("Reduced motion added an animation wait.");
        var closingAnimation = new RecordLoadingAnimation();
        closingAnimation.SetBusy(true, animationsEnabled: true);
        var closingWait = closingAnimation.WaitForSettledAsync();
        closingAnimation.Cancel();
        if (closingAnimation.IsPlaying || !closingWait.IsCompleted)
            throw new InvalidOperationException("Closing left an animation waiter blocked.");
        var quick = BeginViewUpdate("Instant completion");
        Verify(true);
        quick.Dispose();
        // Work that ends before the appearance delay never shows or waits for the animation.
        if (ViewLoadingAnimation.IsPlaying)
            throw new InvalidOperationException("Instant work presented the loading animation.");
        Verify(false);
        var shown = BeginViewUpdate("Shown completion");
        await Task.Delay(
            RecordLoadingAnimation.DefaultAppearDelay + TimeSpan.FromMilliseconds(100)
        );
        if (!ViewLoadingAnimation.IsPresented)
            throw new InvalidOperationException("Slow work did not present the animation.");
        shown.Dispose();
        Verify(true);
        await ViewLoadingAnimation.WaitForSettledAsync();
        Verify(false);
        if (ViewLoadingAnimation.LastPresentedFor < RecordLoadingAnimation.MinimumVisible)
            throw new InvalidOperationException(
                "A shown animation ended before its minimum display time."
            );
        using (var older = BeginViewUpdate("Earlier request"))
        {
            Verify(true);
            using var newer = BeginViewUpdate("Current request");
            older.Dispose();
            older.Dispose();
            await Task.Yield();
            Verify(true);
            if (ViewLoadingText.Text != "Current request")
                throw new InvalidOperationException(
                    "An older request replaced the current loading message."
                );
            using (BeginViewUpdate("Nested folder request"))
                Verify(true);
            Verify(true);
        }
        await ViewLoadingAnimation.WaitForSettledAsync();
        Verify(false);
        try
        {
            using var cancelled = BeginViewUpdate("Cancelled request");
            await Task.Yield();
            throw new OperationCanceledException();
        }
        catch (OperationCanceledException) { }
        await ViewLoadingAnimation.WaitForSettledAsync();
        Verify(false);
        using (BeginViewUpdate("Refresh all results"))
        {
            if (session is not { } current)
                throw new InvalidOperationException("View switching smoke requires a session.");
            await ShowSessionAsync(current);
            Verify(true);
            if (
                Folders.RootNodes.Count != 1
                || Entries.Items.Count == 0
                || Ranking.Items.Count == 0
            )
                throw new InvalidOperationException(
                    "Result refresh did not prepare every result section."
                );
        }
        await ViewLoadingAnimation.WaitForSettledAsync();
        Verify(false);
        var otherDrive = SelectDriveAsync(cards[1]);
        if (!otherDrive.IsCompleted)
            Verify(true);
        await otherDrive;
        Verify(false);
        var originalDrive = SelectDriveAsync(cards[0]);
        if (!originalDrive.IsCompleted)
            Verify(true);
        await originalDrive;
        Verify(false);
        var superseded = SelectDriveAsync(cards[1]);
        var latest = SelectDriveAsync(cards[0]);
        await Task.WhenAll(superseded, latest);
        Verify(false);
        if (session?.Drive != cards[0].Choice.Drive)
            throw new InvalidOperationException(
                "An obsolete drive switch replaced the current drive."
            );
    }

    private async Task VerifyDelayedUpdatesAsync()
    {
        int cycles = ViewLoadingAnimation.CompletedCycles;
        void VerifyRegion(bool pending, bool visible)
        {
            if (
                EntryLoadingRegion.IsPending != pending
                || EntryLoadingRegion.IsIndicatorVisible != visible
                || ViewLoadingOverlay.Visibility != Visibility.Collapsed
                || ViewLoadingAnimation.CompletedCycles != cycles
            )
                throw new InvalidOperationException(
                    "A local update used the wrong loading presentation."
                );
        }

        using (EntryLoadingRegion.BeginUpdate())
            VerifyRegion(true, false);
        VerifyRegion(false, false);
        await Task.Delay(400, lifetime.Token);
        VerifyRegion(false, false); // A completed fast read must not flash later.
        var first = EntryLoadingRegion.BeginUpdate();
        VerifyRegion(true, false);
        await Task.Delay(400, lifetime.Token);
        VerifyRegion(true, true);
        bool localAnimated = EntryLoadingRegion.IsPlaying;
        var second = EntryLoadingRegion.BeginUpdate();
        first.Dispose();
        VerifyRegion(true, true);
        second.Dispose();
        if (localAnimated)
            VerifyRegion(true, true);
        await EntryLoadingRegion.WaitForSettledAsync();
        VerifyRegion(false, false);
        if (
            localAnimated
            && EntryLoadingRegion.LastPresentedFor < RecordLoadingAnimation.MinimumVisible
        )
            throw new InvalidOperationException(
                "Local loading ended before its minimum display time."
            );

        var cancelled = EntryLoadingRegion.BeginUpdate();
        EntryLoadingRegion.Cancel();
        using (EntryLoadingRegion.BeginUpdate())
        {
            cancelled.Dispose();
            VerifyRegion(true, false);
        }
        VerifyRegion(false, false);

        var folder = (FolderNode)Folders.RootNodes[0].Content;
        using (var tree = new DelayedActivity((_, visible) => folder.IsLoading = visible))
        {
            using (tree.Begin())
            {
                if (folder.IsLoading)
                    throw new InvalidOperationException(
                        "Tree loading appeared before the threshold."
                    );
                await Task.Delay(400, lifetime.Token);
                if (
                    !folder.IsLoading
                    || folder.LoadingVisibility != Visibility.Visible
                    || ViewLoadingOverlay.Visibility != Visibility.Collapsed
                )
                    throw new InvalidOperationException(
                        "Slow tree expansion did not show its row indicator."
                    );
                RootLayout.UpdateLayout();
                var container = Folders.ContainerFromNode(Folders.RootNodes[0]);
                var ring = container is null
                    ? null
                    : FindTreePart<ProgressRing>(container, "TreeLoadingRing");
                if (ring is not { IsActive: true, Visibility: Visibility.Visible })
                    throw new InvalidOperationException(
                        "The tree row did not bind its loading indicator."
                    );
            }
            if (folder.IsLoading)
                throw new InvalidOperationException("Tree loading remained after completion.");
        }
        if (location is not { } currentLocation)
            throw new InvalidOperationException("Delayed loading smoke needs a selected folder.");
        await LoadFolderAsync(currentLocation, false);
        await LoadRankingAsync();
        VerifyRegion(false, false);
        if (
            RankingLoadingRegion.IsPending
            || RankingLoadingRegion.IsIndicatorVisible
            || ViewLoadingAnimation.IsPlaying
        )
            throw new InvalidOperationException("Local navigation started a global animation.");
    }
}
