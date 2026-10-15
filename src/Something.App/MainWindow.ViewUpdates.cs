using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Something.App;

public sealed partial class MainWindow
{
    // Operations can nest (history -> comparison -> folder/ranking). Keep the whole view
    // covered until every active operation has unwound, including cancelled older work.
    private readonly List<ViewUpdate> viewUpdates = [];
    private readonly Dictionary<TreeViewNode, DelayedActivity> treeLoads = [];

    private sealed class ViewUpdate(MainWindow window, string message)
        : IDisposable,
            IAsyncDisposable
    {
        private MainWindow? owner = window;
        public string Message { get; } = message;

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
            if (current is { closed: false } && current.viewUpdates.Count == 0)
                await current.ViewLoadingAnimation.WaitForSettledAsync();
        }
    }

    private ViewUpdate BeginViewUpdate(string message)
    {
        var update = new ViewUpdate(this, message);
        viewUpdates.Add(update);
        UpdateViewLoadingPresentation();
        return update;
    }

    private void UpdateViewLoadingPresentation()
    {
        if (closed)
            return;
        bool loading = viewUpdates.Count != 0;
        if (!loading)
        {
            RootLayout.UpdateLayout(); // Lay out the complete result while it is still covered.
            loading = viewUpdates.Count != 0;
        }
        if (viewUpdates.LastOrDefault() is { } latest)
            ViewLoadingText.Text = latest.Message;
        if (loading)
            ViewLoadingOverlay.Visibility = Visibility.Visible;
        ViewLoadingAnimation.SetBusy(loading);
        bool covered = loading || ViewLoadingAnimation.IsPlaying;
        ViewLoadingOverlay.Visibility = covered ? Visibility.Visible : Visibility.Collapsed;
        ViewHost.IsEnabled = !covered;
        ViewControls.Opacity = ResultsContent.Opacity = ViewFooter.Opacity = covered ? 0 : 1;
    }

    private async Task PreviewLoadingAnimationAsync()
    {
        await using var preview = BeginViewUpdate(
            "レコードを確認しています · アニメーションのプレビュー"
        );
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
                || new[] { ViewControls, (FrameworkElement)ResultsContent, ViewFooter }.Any(
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
        int completedCycles = ViewLoadingAnimation.CompletedCycles;
        var quick = BeginViewUpdate("Instant completion");
        bool animated = ViewLoadingAnimation.IsPlaying;
        quick.Dispose();
        if (animated)
            Verify(true);
        await ViewLoadingAnimation.WaitForSettledAsync();
        Verify(false);
        if (animated && ViewLoadingAnimation.CompletedCycles != completedCycles + 1)
            throw new InvalidOperationException(
                "Instant work did not finish exactly one animation cycle."
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
        int localCycles = EntryLoadingRegion.CompletedCycles;
        var second = EntryLoadingRegion.BeginUpdate();
        first.Dispose();
        VerifyRegion(true, true);
        second.Dispose();
        if (localAnimated)
            VerifyRegion(true, true);
        await EntryLoadingRegion.WaitForSettledAsync();
        VerifyRegion(false, false);
        if (localAnimated && EntryLoadingRegion.CompletedCycles != localCycles + 1)
            throw new InvalidOperationException(
                "Local loading did not finish its current chick cycle."
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
