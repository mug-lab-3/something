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

    internal Task RunForSmokeAsync(HistorySearchCriteria criteria) => RunSearchAsync(criteria);

    internal void ResultsForSmokeBack() => OnBackToConditions(this, EventArgs.Empty);
}
