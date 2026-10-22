using Something.Application.Search;

namespace Something.App;

public sealed partial class SearchView
{
    internal SearchConditionsView ConditionsScreen => Conditions;
    internal bool HasResult => current is not null;
    internal bool CanReturnToResults => Conditions.CanReturnToResults;
    internal bool ConditionsShown => Conditions.Visibility == Microsoft.UI.Xaml.Visibility.Visible;

    internal async Task WaitForPreviewAsync(CancellationToken token)
    {
        for (int waited = 0; !Conditions.HasPreview; waited += 50)
        {
            if (waited > 10_000)
                throw new InvalidOperationException("The range preview was not shown.");
            await Task.Delay(50, token);
        }
    }

    internal Task RunForSmokeAsync(HistorySearchCriteria criteria) => RunSearchAsync(criteria);

    internal void ResultsForSmokeBack() => OnBackToConditions(this, EventArgs.Empty);
}
