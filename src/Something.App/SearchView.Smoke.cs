using Something.Application.Search;

namespace Something.App;

public sealed partial class SearchView
{
    internal SearchConditionsView ConditionsScreen => Conditions;
    internal bool HasResult => current is not null;
    internal bool CanReturnToResults => Conditions.CanReturnToResults;
    internal bool ConditionsShown => Conditions.Visibility == Microsoft.UI.Xaml.Visibility.Visible;

    // Waits for the latest read of the range, not just for some preview to be shown: an older
    // one may describe scans from before.
    internal async Task WaitForPreviewAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            for (var load = previewLoad; ; load = previewLoad)
            {
                await load.WaitAsync(timeout.Token);
                if (load != previewLoad)
                    continue; // A newer read started meanwhile.
                if (Conditions.HasPreview)
                    return;
                throw new InvalidOperationException("The range preview was not shown.");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("The range preview was not shown in time.");
        }
    }

    // Shows the busy state as a search would: the loading animation over the screen and the
    // cancel button, which stands alone because the animation already says that it is working.
    internal async Task ShowBusyForSmokeAsync(CancellationToken token, Func<Task> capture)
    {
        await using var lease = Region.BeginUpdate();
        CancelBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        try
        {
            for (int waited = 0; !Region.IsIndicatorVisible; waited += 50)
            {
                if (waited > 5_000)
                    throw new InvalidOperationException("The searching animation did not appear.");
                await Task.Delay(50, token);
            }
            await Task.Delay(1200, token);
            if (Region.AnimationScaleFactor <= 1.5 || CancelBar.Children.Count != 1)
                throw new InvalidOperationException(
                    "The searching animation is small or the cancel bar has more than a button."
                );
            await capture();
        }
        finally
        {
            CancelBar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
    }

    internal Task RunForSmokeAsync(HistorySearchCriteria criteria) => RunSearchAsync(criteria);

    internal void ResultsForSmokeBack() => OnBackToConditions(this, EventArgs.Empty);
}
