using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Something.App;

public sealed partial class HistoryPicker : UserControl
{
    private HistoryRow[] rows = [];
    private readonly Dictionary<Guid, long> counts = [];
    private HistoryRow? selected;
    private bool automatic;
    private bool automaticSelectable;
    private HistoryRow? automaticHistory;
    private HistorySelectionDialog? activeDialog;
    internal Func<Guid[], Task<IReadOnlyDictionary<Guid, long>>>? LoadCounts { get; set; }
    internal string? OtherEndpoint { get; set; }
    public event EventHandler<EventArgs>? SelectionChanged;

    public HistoryPicker()
    {
        InitializeComponent();
        Unloaded += (_, _) => activeDialog?.Hide();
    }

    public string Header
    {
        get => HeaderText.Text;
        set => HeaderText.Text = value;
    }

    public string PlaceholderText { get; set; } = "記録を選択";

    internal IEnumerable<HistoryRow>? ItemsSource
    {
        get => rows;
        set
        {
            activeDialog?.Invalidate();
            rows =
                value
                    ?.OrderByDescending(row => row.Entry.CompletedUtc)
                    .ThenBy(row => row.Entry.Id)
                    .ToArray()
                ?? [];
            var ids = rows.Select(row => row.Entry.Id).ToHashSet();
            foreach (var id in counts.Keys.Where(id => !ids.Contains(id)).ToArray())
                counts.Remove(id);
            SelectedItem = rows.FirstOrDefault(row => row.Entry.Id == selected?.Entry.Id);
        }
    }

    internal IReadOnlyList<HistoryRow> Items => rows;
    internal HistoryRow? SelectedItem
    {
        get => selected;
        set
        {
            var next = value is null
                ? null
                : rows.FirstOrDefault(row => row.Entry.Id == value.Entry.Id);
            bool changed = selected?.Entry.Id != next?.Entry.Id;
            selected = next;
            UpdateValueText();
            if (changed)
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal int SelectedIndex
    {
        get => Array.FindIndex(rows, row => row.Entry.Id == selected?.Entry.Id);
        set => SelectedItem = value >= 0 && value < rows.Length ? rows[value] : null;
    }

    internal HistoryRow? DisplayedHistory => automatic ? automaticHistory : selected;

    internal void SetAutomaticHistory(HistoryRow? history, bool value, bool selectable = false)
    {
        if (value)
            activeDialog?.Invalidate();
        automatic = value;
        automaticSelectable = value && selectable;
        automaticHistory = history;
        PickerChevron.Visibility = value && !selectable ? Visibility.Collapsed : Visibility.Visible;
        PickerButton.IsTabStop = !value || selectable;
        UpdateValueText();
    }

    private void UpdateValueText() =>
        ValueText.Text = DisplayedHistory?.ToString() ?? (automatic ? "記録なし" : PlaceholderText);

    private HistorySelectionDialog CreateDialog() =>
        new(rows, selected, counts, LoadCounts)
        {
            XamlRoot = XamlRoot,
            Title = Header,
            OtherEndpoint = OtherEndpoint,
            RequestedTheme = ActualTheme,
        };

    private async void OnOpen(object sender, RoutedEventArgs args)
    {
        if (automatic && !automaticSelectable || activeDialog is not null)
            return;
        var dialog = CreateDialog();
        activeDialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !dialog.Invalidated)
                SelectedItem = dialog.SelectedHistory;
        }
        finally
        {
            activeDialog = null;
        }
    }

    internal async Task<FrameworkElement> VerifyPreviewAsync(CancellationToken token)
    {
        if (rows.Length == 0 || selected is null)
            throw new InvalidOperationException("History picker smoke requires saved demo scans.");
        var original = selected;
        var dialog = CreateDialog();
        activeDialog = dialog;
        var cancelled = dialog.ShowAsync();
        await Task.Delay(200, token);
        await dialog.VerifyAsync(token);
        dialog.Hide();
        if (await cancelled != ContentDialogResult.None || selected.Entry.Id != original.Entry.Id)
            throw new InvalidOperationException(
                "Cancelling history selection changed the active scan."
            );
        dialog = CreateDialog();
        activeDialog = dialog;
        _ = dialog.ShowAsync();
        await Task.Delay(200, token);
        await dialog.VerifyAsync(token);
        if (selected.Entry.Id != original.Entry.Id)
            throw new InvalidOperationException(
                "Browsing records committed a scan before confirmation."
            );
        return dialog;
    }
}
