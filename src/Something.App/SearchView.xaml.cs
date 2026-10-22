using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;
using Something.Application.Search;
using Something.Statistics;

namespace Something.App;

/// <summary>The search tab: the conditions screen and the results screen of one drive, one of
/// them at a time. It runs searches, keeps the last result while its conditions are unchanged,
/// and discards the result when the conditions or the drive change.</summary>
public sealed partial class SearchView : UserControl
{
    private HistorySearchService? service;
    private Action<Exception> report = _ => { };
    private CancellationToken lifetime;
    private string? volumeKey;
    private string drive = "";
    private HistorySearch? current;
    private HistorySearchCriteria? searched;
    private CancellationTokenSource? searching;
    private int previewVersion;
    private FileVisibilityOptions displaySettings = new();

    public SearchView()
    {
        InitializeComponent();
        Conditions.IsDriveAvailable = false;
    }

    public SearchResultsView Results => ResultsScreen;

    /// <summary>Whether a search is running.</summary>
    public bool IsSearching => searching is not null;

    /// <summary>Whether the results screen is the one shown.</summary>
    public bool ShowsResults => ResultsScreen.Visibility == Visibility.Visible;

    public void Configure(
        HistorySearchService searchService,
        Action<Exception> reportError,
        CancellationToken windowLifetime
    )
    {
        service = searchService;
        report = reportError;
        lifetime = windowLifetime;
    }

    /// <summary>Tells the tab which drive and display settings it searches with. A different
    /// drive discards the result; different display settings discard it when they would change it.</summary>
    public void SetContext(string? key, string driveName, FileVisibilityOptions settings)
    {
        bool driveChanged = key != volumeKey;
        drive = driveName;
        volumeKey = key;
        displaySettings = settings;
        Conditions.DisplaySettings = settings;
        Conditions.IsDriveAvailable = key is not null;
        if (driveChanged)
            _ = ResetAsync();
        else
            DiscardIfStale();
    }

    /// <summary>Called when the tab is shown, to make sure the range preview is current.</summary>
    public void Activate()
    {
        if (!Conditions.HasPreview)
            _ = LoadPreviewAsync();
    }

    /// <summary>Stops a running search and drops the result, so the scans it holds are released
    /// before the database file is compacted, moved or deleted. The input is kept.</summary>
    public async Task ReleaseAsync()
    {
        CancelSearch();
        await DiscardResultAsync();
        ShowConditions();
    }

    /// <summary>The sizes of one item of the shown result in every scan of its search.</summary>
    public Task<System.Collections.Immutable.ImmutableArray<SearchSizeObservation>> ReadSeriesAsync(
        SearchItem item,
        CancellationToken token
    ) =>
        (
            current ?? throw new InvalidOperationException("There is no search result.")
        ).ReadSeriesAsync(item, token);

    public bool GoBack() => ShowsResults && ResultsScreen.GoBack();

    public bool GoForward() => ShowsResults && ResultsScreen.GoForward();

    public bool GoUp() => ShowsResults && ResultsScreen.GoUp();

    public async ValueTask DisposeAsync()
    {
        CancelSearch();
        await DiscardResultAsync();
    }

    private async Task ResetAsync()
    {
        await ReleaseAsync();
        Conditions.ShowPreview(null);
        await LoadPreviewAsync();
    }

    private void CancelSearch() => searching?.Cancel();

    private async Task DiscardResultAsync()
    {
        var old = current;
        current = null;
        searched = null;
        ResultsScreen.Clear();
        Conditions.CanReturnToResults = false;
        if (old is not null)
            await old.DisposeAsync();
    }

    private void DiscardIfStale()
    {
        if (current is null || searched is null)
            return;
        if (Conditions.Form.DiffersFrom(searched, displaySettings))
            _ = DiscardAndShowConditionsAsync();
    }

    private async Task DiscardAndShowConditionsAsync()
    {
        await DiscardResultAsync();
        ShowConditions();
    }

    private void ShowConditions()
    {
        ResultsScreen.Visibility = Visibility.Collapsed;
        Conditions.Visibility = Visibility.Visible;
    }

    private void ShowResultsScreen()
    {
        Conditions.Visibility = Visibility.Collapsed;
        ResultsScreen.Visibility = Visibility.Visible;
    }

    private async Task LoadPreviewAsync()
    {
        int version = ++previewVersion;
        var form = Conditions.Form;
        if (
            service is null
            || volumeKey is null
            || form.Start is not { } start
            || form.End is not { } end
            || end < start
        )
            return;
        try
        {
            var preview = await service.PreviewAsync(
                volumeKey,
                start.ToUniversalTime(),
                end.ToUniversalTime(),
                lifetime
            );
            if (version == previewVersion)
                Conditions.ShowPreview(preview);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (version == previewVersion)
                report(error);
        }
    }

    private async void OnRangeChanged(object? sender, EventArgs args) => await LoadPreviewAsync();

    private void OnInputChanged(object? sender, EventArgs args) => DiscardIfStale();

    private async void OnSearchRequested(object? sender, HistorySearchCriteria criteria) =>
        await RunSearchAsync(criteria);

    private async Task RunSearchAsync(HistorySearchCriteria criteria)
    {
        if (service is null || volumeKey is not { } key || searching is not null)
            return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        searching = cancellation;
        await DiscardResultAsync();
        Conditions.SetBusy(true);
        CancelBar.Visibility = Visibility.Visible;
        HistorySearch? search = null;
        try
        {
            await using var lease = Region.BeginUpdate();
            search = await service.SearchAsync(key, criteria, cancellation.Token);
            if (cancellation.IsCancellationRequested || key != volumeKey)
                return;
            current = search;
            searched = criteria;
            search = null;
            ResultsScreen.Show(current, drive);
            Conditions.CanReturnToResults = true;
            ShowResultsScreen();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            report(error);
        }
        finally
        {
            if (search is not null)
                await search.DisposeAsync();
            searching = null;
            cancellation.Dispose();
            CancelBar.Visibility = Visibility.Collapsed;
            Conditions.SetBusy(false);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs args) => CancelSearch();

    private void OnBackToConditions(object? sender, EventArgs args) => ShowConditions();

    private void OnReturnToResults(object? sender, EventArgs args)
    {
        if (current is not null)
            ShowResultsScreen();
    }
}
