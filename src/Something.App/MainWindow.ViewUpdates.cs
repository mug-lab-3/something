using Microsoft.UI.Xaml;

namespace Something.App;

public sealed partial class MainWindow
{
    // Operations can nest (history -> comparison -> folder/ranking). Keep the whole view
    // covered until every active operation has unwound, including cancelled older work.
    private readonly List<ViewUpdate> viewUpdates = [];

    private sealed class ViewUpdate(MainWindow window, string message) : IDisposable
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
        ViewLoadingText.Text = viewUpdates.LastOrDefault()?.Message ?? "";
        ViewLoadingRing.IsActive = loading;
        ViewLoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ViewHost.IsEnabled = !loading;
        ViewControls.Opacity = ResultsContent.Opacity = ViewFooter.Opacity = loading ? 0 : 1;
    }

    private async Task VerifyViewUpdatesAsync()
    {
        void Verify(bool loading)
        {
            if (
                ViewLoadingOverlay.Visibility
                    != (loading ? Visibility.Visible : Visibility.Collapsed)
                || ViewLoadingRing.IsActive != loading
                || new[] { ViewControls, (FrameworkElement)ResultsContent, ViewFooter }.Any(
                    element => element.Opacity != (loading ? 0 : 1)
                )
                || ViewHost.IsEnabled == loading
            )
                throw new InvalidOperationException(
                    "Result switching exposed a partial or interactive view."
                );
        }

        Verify(false);
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
        Verify(false);
        try
        {
            using var cancelled = BeginViewUpdate("Cancelled request");
            await Task.Yield();
            throw new OperationCanceledException();
        }
        catch (OperationCanceledException) { }
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
}
