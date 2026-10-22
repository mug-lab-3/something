using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Something.Application;
using Something.Application.Search;
using Something.Statistics;
using Windows.System;

namespace Something.App;

/// <summary>The conditions screen of the search tab. It owns the typed input
/// (<see cref="Form"/>), shows each problem next to its input, and asks for a search only when
/// the input can be searched.</summary>
public sealed partial class SearchConditionsView : UserControl
{
    private bool loading = true;
    private bool busy;
    private SearchRangePreview? preview;
    private bool driveAvailable = true;
    private FileVisibilityOptions displaySettings = new();

    public SearchConditionsView()
    {
        InitializeComponent();
        // Handled Enter is needed too: a number box handles it when it commits its text.
        Scroller.AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        loading = true;
        FillChoices();
        // The last 30 days, up to the next minute so that a scan finishing now is included.
        Form.Start = Minute(DateTimeOffset.Now.AddDays(-30));
        Form.End = Minute(DateTimeOffset.Now).AddMinutes(1);
        ShowForm();
        loading = false;
        Revalidate();
    }

    public SearchConditionsForm Form { get; } = new();

    /// <summary>The display settings; they apply to the search when no attribute is specified.</summary>
    public FileVisibilityOptions DisplaySettings
    {
        get => displaySettings;
        set
        {
            displaySettings = value;
            Revalidate();
        }
    }

    /// <summary>The user asked to search with these criteria (the search button or Enter).</summary>
    public event EventHandler<HistorySearchCriteria>? SearchRequested;

    /// <summary>The user chose to see the previous result again.</summary>
    public event EventHandler? ReturnToResultsRequested;

    /// <summary>The range changed; the host answers with <see cref="ShowPreview"/>.</summary>
    public event EventHandler? RangeChanged;

    /// <summary>Any input changed; the host decides whether a previous result still applies.</summary>
    public event EventHandler? InputChanged;

    /// <summary>Asks the host for the scans of the current range.</summary>
    public void RequestPreview() => RangeChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows the scans the range selects; null while they are being read.</summary>
    public void ShowPreview(SearchRangePreview? value)
    {
        preview = value;
        UpdatePreviewText();
        Revalidate();
    }

    /// <summary>Whether the previous result is still valid and can be shown again.</summary>
    public bool CanReturnToResults
    {
        get => ReturnToResultsButton.Visibility == Visibility.Visible;
        set => ReturnToResultsButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>False when the selected drive has no history to search.</summary>
    public bool IsDriveAvailable
    {
        get => driveAvailable;
        set
        {
            driveAvailable = value;
            Revalidate();
        }
    }

    /// <summary>Disables searching, for example while another search runs.</summary>
    public void SetBusy(bool value)
    {
        busy = value;
        Revalidate();
    }

    /// <summary>Whether the scans of the range have been read.</summary>
    public bool HasPreview => preview is not null;

    /// <summary>The current input as criteria, or null when the range is incomplete.</summary>
    public SearchFormResult Current => Form.Build(displaySettings, preview);

    private static DateTimeOffset Minute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Offset);

    private void FillChoices()
    {
        Fill(
            TargetBox,
            (Strings.Get("Search_TargetBoth"), SearchTarget.Both),
            (Strings.Get("Search_TargetFolders"), SearchTarget.Folders),
            (Strings.Get("Search_TargetFiles"), SearchTarget.Files)
        );
        Fill(
            NameModeBox,
            (Strings.Get("Search_NameContains"), NameMatchMode.Contains),
            (Strings.Get("Search_NameWildcard"), NameMatchMode.Wildcard),
            (Strings.Get("Search_NameRegex"), NameMatchMode.RegularExpression)
        );
        foreach (var box in new[] { HiddenBox, SystemBox, CompressedBox, SparseBox, StreamsBox })
            Fill(
                box,
                (Strings.Get("Search_AttributeAny"), AttributeRequirement.Any),
                (Strings.Get("Search_AttributeSet"), AttributeRequirement.Set),
                (Strings.Get("Search_AttributeClear"), AttributeRequirement.Clear)
            );
        foreach (var box in new[] { SizeUnitBox, DeltaUnitBox, SwingUnitBox })
            Fill(
                box,
                (Strings.Get("Search_UnitBytes"), SearchSizeUnit.Bytes),
                (Strings.Get("Search_UnitKiB"), SearchSizeUnit.KiB),
                (Strings.Get("Search_UnitMiB"), SearchSizeUnit.MiB),
                (Strings.Get("Search_UnitGiB"), SearchSizeUnit.GiB),
                (Strings.Get("Search_UnitTiB"), SearchSizeUnit.TiB)
            );
        Fill(
            TrendBox,
            (Strings.Get("Search_TrendAny"), SizeTrend.Any),
            (Strings.Get("Search_TrendIncreasing"), SizeTrend.Increasing),
            (Strings.Get("Search_TrendDecreasing"), SizeTrend.Decreasing)
        );
        Fill(
            PresenceBox,
            (Strings.Get("Search_PresenceAny"), SearchPresence.Any),
            (Strings.Get("Search_PresenceAll"), SearchPresence.InAllScans),
            (Strings.Get("Search_PresenceAppeared"), SearchPresence.Appeared),
            (Strings.Get("Search_PresenceDisappeared"), SearchPresence.Disappeared)
        );
    }

    private static void Fill<T>(ComboBox box, params (string Text, T Value)[] items)
        where T : notnull
    {
        foreach (var (text, value) in items)
            box.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        box.SelectedIndex = 0;
    }

    private static T Chosen<T>(ComboBox box) => (T)((ComboBoxItem)box.SelectedItem).Tag;

    private static void Choose<T>(ComboBox box, T value)
    {
        for (int index = 0; index < box.Items.Count; index++)
            if (Equals(((ComboBoxItem)box.Items[index]).Tag, value))
            {
                box.SelectedIndex = index;
                return;
            }
        throw new ArgumentOutOfRangeException(nameof(value));
    }

    // The controls take their values from the form, for the first display and after clearing.
    private void ShowForm()
    {
        RangeInput.Start = Form.Start;
        RangeInput.End = Form.End;
        Choose(TargetBox, Form.Target);
        NameBox.Text = Form.NamePattern;
        Choose(NameModeBox, Form.NameMode);
        ExtensionsBox.Text = Form.Extensions;
        BaseFolderBox.Text = Form.BaseFolder;
        MinDepthBox.Value = ValueOf(Form.MinimumDepth);
        MaxDepthBox.Value = ValueOf(Form.MaximumDepth);
        Choose(HiddenBox, Form.Hidden);
        Choose(SystemBox, Form.System);
        Choose(CompressedBox, Form.Compressed);
        Choose(SparseBox, Form.Sparse);
        Choose(StreamsBox, Form.NamedStreams);
        MinSizeBox.Value = ValueOf(Form.MinimumSize);
        MaxSizeBox.Value = ValueOf(Form.MaximumSize);
        Choose(SizeUnitBox, Form.SizeUnit);
        MinDeltaBox.Value = ValueOf(Form.MinimumDelta);
        MaxDeltaBox.Value = ValueOf(Form.MaximumDelta);
        Choose(DeltaUnitBox, Form.DeltaUnit);
        MinRateBox.Value = ValueOf(Form.MinimumRate);
        MaxRateBox.Value = ValueOf(Form.MaximumRate);
        MinSwingBox.Value = ValueOf(Form.MinimumSwing);
        Choose(SwingUnitBox, Form.SwingUnit);
        Choose(TrendBox, Form.Trend);
        Choose(PresenceBox, Form.Presence);
    }

    // The form takes the values of the controls, which are what the user sees.
    private void ReadControls()
    {
        Form.Start = RangeInput.Start;
        Form.End = RangeInput.End;
        Form.Target = Chosen<SearchTarget>(TargetBox);
        Form.NamePattern = NameBox.Text;
        Form.NameMode = Chosen<NameMatchMode>(NameModeBox);
        Form.Extensions = ExtensionsBox.Text;
        Form.BaseFolder = BaseFolderBox.Text;
        Form.MinimumDepth = TextOf(MinDepthBox);
        Form.MaximumDepth = TextOf(MaxDepthBox);
        Form.Hidden = Chosen<AttributeRequirement>(HiddenBox);
        Form.System = Chosen<AttributeRequirement>(SystemBox);
        Form.Compressed = Chosen<AttributeRequirement>(CompressedBox);
        Form.Sparse = Chosen<AttributeRequirement>(SparseBox);
        Form.NamedStreams = Chosen<AttributeRequirement>(StreamsBox);
        Form.MinimumSize = TextOf(MinSizeBox);
        Form.MaximumSize = TextOf(MaxSizeBox);
        Form.SizeUnit = Chosen<SearchSizeUnit>(SizeUnitBox);
        Form.MinimumDelta = TextOf(MinDeltaBox);
        Form.MaximumDelta = TextOf(MaxDeltaBox);
        Form.DeltaUnit = Chosen<SearchSizeUnit>(DeltaUnitBox);
        Form.MinimumRate = TextOf(MinRateBox);
        Form.MaximumRate = TextOf(MaxRateBox);
        Form.MinimumSwing = TextOf(MinSwingBox);
        Form.SwingUnit = Chosen<SearchSizeUnit>(SwingUnitBox);
        Form.Trend = Chosen<SizeTrend>(TrendBox);
        Form.Presence = Chosen<SearchPresence>(PresenceBox);
    }

    private void Edited()
    {
        if (loading)
            return;
        ReadControls();
        Revalidate();
        InputChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs args) => Edited();

    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Edited();

    // The form keeps what was typed as text, in the culture of the screen. A number box holds a
    // double, so a value is written back with at most 15 significant digits (what the box shows)
    // and, when large, as whole digits, which the form reports as too large instead of misreading.
    private static string TextOf(NumberBox box)
    {
        double value = box.Value;
        if (double.IsNaN(value))
            return "";
        var culture = CultureInfo.CurrentCulture;
        return Math.Abs(value) >= 1e15
            ? value.ToString("F0", culture)
            : ((decimal)value).ToString(culture);
    }

    private static double ValueOf(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
            ? value
            : double.NaN;

    private IEnumerable<SteppingNumberBox> NumberBoxes =>
        [
            MinDepthBox,
            MaxDepthBox,
            MinSizeBox,
            MaxSizeBox,
            MinDeltaBox,
            MaxDeltaBox,
            MinRateBox,
            MaxRateBox,
            MinSwingBox,
        ];

    private static bool IsInNumberBox(object? source)
    {
        for (var node = source as DependencyObject; node is not null; )
        {
            if (node is SteppingNumberBox)
                return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnChoiceChanged(object sender, SelectionChangedEventArgs args) => Edited();

    private void OnRangeChanged(object? sender, EventArgs args)
    {
        if (loading)
            return;
        ReadControls();
        // The earlier scans no longer describe the range.
        ShowPreview(null);
        InputChanged?.Invoke(this, EventArgs.Empty);
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreset(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string tag })
            return;
        int days = int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
        loading = true;
        RangeInput.Start = Minute(DateTimeOffset.Now.AddDays(-days));
        RangeInput.End = Minute(DateTimeOffset.Now).AddMinutes(1);
        loading = false;
        OnRangeChanged(this, EventArgs.Empty);
    }

    private void OnClear(object sender, RoutedEventArgs args)
    {
        loading = true;
        Form.ClearConditions();
        ShowForm();
        loading = false;
        Revalidate();
        InputChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSearch(object sender, RoutedEventArgs args) => Submit();

    private void OnReturnToResults(object sender, RoutedEventArgs args) =>
        ReturnToResultsRequested?.Invoke(this, EventArgs.Empty);

    // A number box may mark Enter as handled when it commits its text, so it is also taken then.
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || (args.Handled && !IsInNumberBox(args.OriginalSource)))
            return;
        args.Handled = true;
        Submit();
    }

    private void Submit()
    {
        // Typed text that is not committed yet counts as input.
        foreach (var box in NumberBoxes)
            box.Commit();
        ReadControls();
        var result = Current;
        if (result.CanSearch && !busy)
            SearchRequested?.Invoke(this, result.Criteria!);
    }

    private void UpdatePreviewText()
    {
        RangePreviewText.Text = preview switch
        {
            null => "—",
            { ScanCount: 0 } => Strings.Get("Search_RangeEmpty"),
            { First: { } first, Last: { } last } => Strings.Format(
                "Search_RangeScans",
                preview.ScanCount,
                first.CompletedLocal.ToString("yyyy/MM/dd HH:mm"),
                last.CompletedLocal.ToString("yyyy/MM/dd HH:mm")
            ),
            _ => Strings.Format("Search_RangeScansCount", preview.ScanCount),
        };
    }

    private void Revalidate()
    {
        if (loading)
            return;
        var result = Current;
        foreach (
            var (field, text) in new[]
            {
                (SearchFormField.Range, RangeIssueText),
                (SearchFormField.Name, NameIssueText),
                (SearchFormField.Extensions, ExtensionsIssueText),
                (SearchFormField.Depth, DepthIssueText),
                (SearchFormField.Size, SizeIssueText),
                (SearchFormField.Delta, DeltaIssueText),
                (SearchFormField.Rate, RateIssueText),
                (SearchFormField.Swing, SwingIssueText),
            }
        )
        {
            string[] reasons =
            [
                .. result
                    .Issues.Where(issue => issue.Field == field)
                    .Select(SearchIssueText.Describe)
                    .Distinct(),
            ];
            text.Text = string.Join('\n', reasons);
            text.Visibility = reasons.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        SearchButton.IsEnabled = !busy && driveAvailable && result.CanSearch;
        StatusText.Text =
            !driveAvailable ? Strings.Get("Search_NoDriveHistory")
            : result.CanSearch ? ""
            : Strings.Get("Search_CannotSearch");
    }
}
