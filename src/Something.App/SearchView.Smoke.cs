using Microsoft.UI.Xaml;
using Something.Application.Search;

namespace Something.App;

public sealed partial class SearchView
{
    internal SearchConditionsView ConditionsScreen => Conditions;
    internal bool HasResult => current is not null;
    internal bool CanReturnToResults => Conditions.CanReturnToResults;
    internal bool ConditionsShown => Conditions.Visibility == Visibility.Visible;

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

    // Shows the busy state as a search would: the loading animation alone on the screen (the
    // conditions and results are not shown) and the cancel button, which stands alone because the
    // animation already says that it is working. The button must not come before the animation.
    internal async Task ShowBusyForSmokeAsync(CancellationToken token, Func<Task> capture)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(token);
        searching = running;
        try
        {
            await using var lease = Region.BeginUpdate();
            // Right after the start neither the animation nor the button is there.
            if (Region.IsIndicatorVisible || CancelBar.Visibility == Visibility.Visible)
                throw new InvalidOperationException("The busy state showed up without delay.");
            for (int waited = 0; !Region.IsIndicatorVisible; waited += 20)
            {
                if (waited > 5_000)
                    throw new InvalidOperationException("The searching animation did not appear.");
                if (CancelBar.Visibility == Visibility.Visible)
                    throw new InvalidOperationException(
                        "The cancel button came before the animation."
                    );
                await Task.Delay(20, token);
            }
            // With it (in the same step) the button is there.
            if (CancelBar.Visibility != Visibility.Visible)
                throw new InvalidOperationException(
                    "The cancel button did not come with the animation."
                );
            await Task.Delay(1200, token);
            if (Region.BodyOpacity != 0 || CancelBar.Children.Count != 1)
                throw new InvalidOperationException(
                    "The conditions stayed visible or the cancel bar has more than a button."
                );
            await capture();
        }
        finally
        {
            searching = null;
            CancelBar.Visibility = Visibility.Collapsed;
        }
    }

    internal Task RunForSmokeAsync(HistorySearchCriteria criteria) => RunSearchAsync(criteria);

    internal void ResultsForSmokeBack() => OnBackToConditions(this, EventArgs.Empty);
}
