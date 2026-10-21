using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Something.Application;
using Something.Application.Browsing;
using Something.Application.Comparison;
using Something.Application.History;
using Something.Ntfs;

namespace Something.App;

public sealed partial class EntryRow(
    FolderEntry entry,
    EntryChange? change = null,
    bool comparing = false
)
{
    public FolderEntry Entry { get; } = entry;
    internal EntryChange? Change => change;
    internal bool Comparing => comparing;
    public FileRemovalState Removal { get; internal set; } = new();
    public Visibility RecycleVisibility { get; internal set; } = Visibility.Collapsed;
    public string Name => DisplayText.Escape(Entry.Name);
    public string NameDescription =>
        change is { Before: { } before, After: { } after } && before.Name != after.Name
            ? $"{DisplayText.Escape(before.Name)} → {DisplayText.Escape(after.Name)}"
            : Name;
    public string Kind => Entry.IsDirectory ? Strings.Get("Item_Folder") : Strings.Get("Item_File");
    public bool IsHidden => Entry.IsHidden == true;
    public bool IsSystem => Entry.IsSystem == true;
    public bool AttributesKnown => Entry.StandardFileAttributes is not null;
    public string Size =>
        comparing && change is { After: null }
            ? "—"
            : DisplayText.Bytes(Entry.Data?.AllocatedBytes)
                + (
                    Entry.Data is not null && (Entry.CapacityIncomplete || !Entry.NamesComplete)
                        ? " *"
                        : ""
                );
    public string Delta => comparing ? DisplayText.Change(change?.Delta, change?.Status) : "";
    public GridLength ComparisonWidth => new(comparing ? 140 : 0);
    public string BeforeSize =>
        comparing ? DisplayText.Endpoint(change?.BeforeBytes, change?.Before is not null) : "";
    public string AfterSize =>
        comparing ? DisplayText.Endpoint(change?.AfterBytes, change?.After is not null) : "";
    public string SizeDescription =>
        (
            comparing
                ? Strings.Format("Size_ComparisonDescription", BeforeSize, Size, Delta)
                : Strings.Get("Size_AllocatedDescription")
        )
        + (Entry.CapacityIncomplete ? Strings.Get("Size_IncompleteNote") : "")
        + (!Entry.NamesComplete ? Strings.Get("Size_NamesIncompleteNote") : "");
}

public sealed partial class RankingRow
{
    private readonly CapacityAnalysisItem item;
    private readonly string drive;
    public FileRemovalState Removal { get; internal set; } = new();
    public uint? StandardFileAttributes { get; internal set; }
    public bool IsHidden =>
        (StandardFileAttributes.GetValueOrDefault() & (uint)FileAttributes.Hidden) != 0;
    public bool IsSystem =>
        (StandardFileAttributes.GetValueOrDefault() & (uint)FileAttributes.System) != 0;
    public bool AttributesKnown => StandardFileAttributes is not null;
    public Visibility RecycleVisibility { get; internal set; } = Visibility.Collapsed;

    internal CapacityAnalysisItem Item => item;

    public RankingRow(CapacityAnalysisItem item, string drive)
    {
        this.item = item;
        this.drive = drive;
        StandardFileAttributes = item.StandardFileAttributes;
    }

    public RankingRow(
        RankedFolder entry,
        string drive,
        FolderChange? change = null,
        bool comparing = false
    )
        : this(CapacityAnalysisItem.FromFolder(entry, change: change, comparing: comparing), drive)
    { }

    public RankingRow(FolderChange change, string drive)
        : this(CapacityAnalysisItem.FromChange(change), drive) { }

    public FileReference Reference => item.Reference;
    public string RelativePath => item.RelativePath;
    public bool IsDirectory => item.IsDirectory;
    public string Path => $"{drive}\\{DisplayText.Escape(RelativePath)}";
    public string Size =>
        DisplayText.Bytes(item.AllocatedBytes)
        + (item.AllocatedBytes is not null && item.CapacityIncomplete ? " *" : "");
    public string Delta => item.Comparing ? DisplayText.Change(item.Delta, item.Status) : "";
    public GridLength ComparisonWidth => new(item.Comparing ? 140 : 0);
    public string SizeDescription =>
        item.Comparing
            ? Strings.Format("Size_ComparisonDescription", BeforeSize, Size, Delta)
            : Strings.Get("Size_AllocatedDescription");
    public string BeforeSize =>
        item.Comparing ? DisplayText.Endpoint(item.BeforeBytes, item.HasBefore) : "";
    public string AfterSize =>
        item.Comparing ? DisplayText.Endpoint(item.AfterBytes, item.HasAfter) : "";
}

internal sealed record FolderLocation(FileReference Reference, string Path);

internal sealed record HistoryRow(ScanHistoryEntry Entry, bool IsLatest = false)
{
    public override string ToString() =>
        (IsLatest ? Strings.Get("History_LatestPrefix") : "")
        + Entry.CompletedLocal.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture);
}

internal sealed record FolderNode(
    string Name,
    FolderLocation Location,
    int NextPage = 0,
    bool LoadMore = false,
    bool Comparing = false,
    FolderChange? Comparison = null,
    UInt128? AllocatedBytes = null,
    bool CapacityIncomplete = false
) : INotifyPropertyChanged
{
    private bool isLoading;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsLoading
    {
        get => isLoading;
        internal set
        {
            if (isLoading == value)
                return;
            isLoading = value;
            PropertyChanged?.Invoke(this, new(nameof(IsLoading)));
            PropertyChanged?.Invoke(this, new(nameof(LoadingVisibility)));
        }
    }
    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public string DisplayName => DisplayText.Escape(Name);
    public string? Change =>
        Comparing ? DisplayText.Change(Comparison?.AllocatedDelta, Comparison?.Status) : null;
    public GridLength ChangeWidth => new(Comparing && !LoadMore ? 18 : 0);
    public string ChangeGlyph =>
        !Comparing || LoadMore ? ""
        : Comparison?.AllocatedDelta is { } delta
            ? delta.Sign switch
            {
                > 0 => "\uE74A",
                < 0 => "\uE74B",
                _ => "",
            }
        : "\uE897";
    public Brush ChangeBrush =>
        new SolidColorBrush(
            Comparison?.AllocatedDelta is { } delta
                ? delta.Sign switch
                {
                    > 0 => Microsoft.UI.ColorHelper.FromArgb(255, 198, 106, 0),
                    < 0 => Microsoft.UI.ColorHelper.FromArgb(255, 28, 150, 72),
                    _ => Microsoft.UI.Colors.Gray,
                }
                : Microsoft.UI.Colors.Gray
        );
    public string ChangeDescription =>
        !Comparing || Comparison is null ? ""
        : Comparison?.AllocatedDelta is { } delta
            ? delta.Sign switch
            {
                > 0 => Strings.Get("Change_Increased"),
                < 0 => Strings.Get("Change_Decreased"),
                _ => Strings.Get("Change_Unchanged"),
            }
        : Change ?? Strings.Get("Change_Uncomparable");
    public string Description =>
        DisplayName
        + (
            LoadMore
                ? ""
                : Strings.Format(
                    "FolderTip_Size",
                    (
                        Comparing && Comparison is { AfterPath: null }
                            ? "—"
                            : DisplayText.Bytes(AllocatedBytes)
                    )
                )
        )
        + (CapacityIncomplete ? Strings.Get("FolderTip_KnownOnly") : "")
        + (
            Comparing
                ? Strings.Format(
                    "FolderTip_Comparison",
                    DisplayText.Endpoint(
                        Comparison?.BeforeAllocatedBytes,
                        Comparison?.BeforePath is not null
                    ),
                    Change
                )
                : ""
        );

    public override string ToString() => DisplayName;
}

internal static class DisplayText
{
    internal static string Endpoint(UInt128? bytes, bool observed) => observed ? Bytes(bytes) : "—";

    internal static string Change(BigInteger? delta, FolderChangeStatus? status) =>
        delta is { } value
            ? (
                value > 0 ? "+"
                : value < 0 ? "−"
                : "±"
            ) + Bytes(UInt128.CreateChecked(BigInteger.Abs(value)))
            : status switch
            {
                // No comparison is established; the status text explains why.
                null => "—",
                FolderChangeStatus.NotObservedBefore => Strings.Get("Change_MissingBefore"),
                FolderChangeStatus.NotObservedAfter => Strings.Get("Change_MissingAfter"),
                _ => Strings.Get("Change_Uncomparable"),
            };

    internal static string CapacityRatio(UInt128 used, UInt128 total)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB"];
        double size = (double)total;
        double divisor = 1;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            divisor *= 1024;
            unit++;
        }
        string format = unit == 0 ? "N0" : "N2";
        return $"{((double)used / divisor).ToString(format, CultureInfo.CurrentCulture)} / {size.ToString(format, CultureInfo.CurrentCulture)} {units[unit]}";
    }

    internal static string Escape(string text) =>
        string.Concat(
            text.Select(c =>
                char.IsControl(c) || char.IsSurrogate(c) ? $"\\u{(int)c:X4}" : c.ToString()
            )
        );

    internal static string Bytes(UInt128? bytes)
    {
        if (bytes is null)
            return Strings.Get("Size_Unknown");
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB"];
        double size = (double)bytes.Value;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size.ToString(unit == 0 ? "N0" : "N2", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
