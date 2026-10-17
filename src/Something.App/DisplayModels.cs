using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Something.Application;
using Something.Ntfs;
using Something.Storage;

namespace Something.App;

public sealed partial class EntryRow(
    FolderEntry entry,
    EntryChange? change = null,
    bool comparing = false
)
{
    public FolderEntry Entry { get; } = entry;
    public FileRemovalState Removal { get; internal set; } = new();
    public Visibility RecycleVisibility { get; internal set; } = Visibility.Collapsed;
    public string Name => DisplayText.Escape(Entry.Name);
    public string NameDescription =>
        change is { Before: { } before, After: { } after } && before.Name != after.Name
            ? $"{DisplayText.Escape(before.Name)} → {DisplayText.Escape(after.Name)}"
            : Name;
    public string Kind => Entry.IsDirectory ? "フォルダ" : "ファイル";
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
                ? $"比較前：{BeforeSize}\nサイズ：{Size}\n増減：{Delta}（比較後 − 比較前）"
                : "ディスク上の割り当て容量"
        )
        + (
            Entry.CapacityIncomplete
                ? "。容量が未解決の項目があります。未解決分は表示値に含まれません"
                : ""
        )
        + (!Entry.NamesComplete ? "。名前集合が未確定です" : "");
}

public sealed partial class RankingRow
{
    public FileRemovalState Removal { get; internal set; } = new();
    public uint? StandardFileAttributes { get; internal set; }
    public bool IsHidden =>
        (StandardFileAttributes.GetValueOrDefault() & (uint)FileAttributes.Hidden) != 0;
    public bool IsSystem =>
        (StandardFileAttributes.GetValueOrDefault() & (uint)FileAttributes.System) != 0;
    public bool AttributesKnown => StandardFileAttributes is not null;
    public Visibility RecycleVisibility { get; internal set; } = Visibility.Collapsed;
    private readonly FolderChange? change;
    private readonly string drive;
    private readonly UInt128? allocated;
    private readonly bool comparing;

    public RankingRow(
        RankedFolder entry,
        string drive,
        FolderChange? change = null,
        bool comparing = false
    )
    {
        Reference = entry.Sample.Reference;
        RelativePath = entry.RelativePath;
        allocated = entry.Data.AllocatedBytes;
        this.drive = drive;
        this.change = change;
        this.comparing = comparing;
    }

    public RankingRow(FolderChange change, string drive)
    {
        Reference = change.Reference;
        RelativePath = change.AfterPath ?? change.BeforePath ?? "";
        this.change = change;
        this.drive = drive;
        allocated = change.AfterAllocatedBytes;
        comparing = true;
    }

    public FileReference Reference { get; }
    public string RelativePath { get; }
    public string Path => $"{drive}\\{DisplayText.Escape(RelativePath)}";
    public string Size => DisplayText.Bytes(allocated);
    public string Delta =>
        comparing ? DisplayText.Change(change?.AllocatedDelta, change?.Status) : "";
    public GridLength ComparisonWidth => new(comparing ? 140 : 0);
    public string SizeDescription =>
        comparing
            ? $"比較前：{BeforeSize}\nサイズ：{Size}\n増減：{Delta}（比較後 − 比較前）"
            : "ディスク上の割り当て容量";
    public string BeforeSize =>
        change is null
            ? comparing
                ? "—"
                : ""
            : DisplayText.Endpoint(change.BeforeAllocatedBytes, change.BeforePath is not null);
    public string AfterSize =>
        change is null
            ? ""
            : DisplayText.Endpoint(change.AfterAllocatedBytes, change.AfterPath is not null);
}

internal sealed record FolderLocation(FileReference Reference, string Path);

internal sealed record HistoryRow(ScanHistoryEntry Entry, bool IsLatest = false)
{
    public override string ToString() =>
        (IsLatest ? "最新 · " : "")
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
        !Comparing ? ""
        : Comparison?.AllocatedDelta is { } delta
            ? delta.Sign switch
            {
                > 0 => "容量が増加",
                < 0 => "容量が減少",
                _ => "変化なし",
            }
        : Change ?? "比較不能";
    public string Description =>
        DisplayName
        + (
            LoadMore
                ? ""
                : $"\nサイズ：{(Comparing && Comparison is { AfterPath: null } ? "—" : DisplayText.Bytes(AllocatedBytes))}"
        )
        + (CapacityIncomplete ? "（既知分。未解決容量を含みません）" : "")
        + (
            Comparing
                ? $"\n比較前：{DisplayText.Endpoint(Comparison?.BeforeAllocatedBytes, Comparison?.BeforePath is not null)}\n増減：{Change}"
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
                FolderChangeStatus.NotObservedBefore => "前の記録なし",
                FolderChangeStatus.NotObservedAfter => "後の記録なし",
                _ => "比較不能",
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
            return "未解決";
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
