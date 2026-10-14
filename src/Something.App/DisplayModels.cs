using System.Globalization;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    public string Name => DisplayText.Escape(Entry.Name);
    public string NameDescription =>
        change is { Before: { } before, After: { } after } && before.Name != after.Name
            ? $"{DisplayText.Escape(before.Name)} → {DisplayText.Escape(after.Name)}"
            : Name;
    public string Kind => Entry.IsDirectory ? "フォルダ" : "ファイル";
    public Symbol Icon => Entry.IsDirectory ? Symbol.Folder : Symbol.Document;
    public string Size =>
        comparing
            ? DisplayText.Change(change?.Delta, change?.Status)
            : DisplayText.Bytes(Entry.Data?.AllocatedBytes)
                + (
                    Entry.Data is not null && (Entry.CapacityIncomplete || !Entry.NamesComplete)
                        ? " *"
                        : ""
                );
    public GridLength ComparisonWidth => new(comparing ? 120 : 0);
    public string BeforeSize =>
        comparing ? DisplayText.Endpoint(change?.BeforeBytes, change?.Before is not null) : "";
    public string AfterSize =>
        comparing ? DisplayText.Endpoint(change?.AfterBytes, change?.After is not null) : "";
    public string SizeDescription =>
        (
            comparing
                ? "比較後 − 比較前。" + DisplayText.Change(change?.Delta, change?.Status)
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
    private readonly FolderChange? change;
    private readonly string drive;
    private readonly UInt128 allocated;

    public RankingRow(RankedFolder entry, string drive)
    {
        Reference = entry.Sample.Reference;
        RelativePath = entry.RelativePath;
        allocated = entry.Data.AllocatedBytes;
        this.drive = drive;
    }

    public RankingRow(FolderChange change, string drive)
    {
        Reference = change.Reference;
        RelativePath = change.AfterPath ?? change.BeforePath ?? "";
        this.change = change;
        this.drive = drive;
    }

    public FileReference Reference { get; }
    public string RelativePath { get; }
    public string Path => $"{drive}\\{DisplayText.Escape(RelativePath)}";
    public string Size =>
        change is null
            ? DisplayText.Bytes(allocated)
            : DisplayText.Change(change.AllocatedDelta, change.Status);
    public GridLength ComparisonWidth => new(change is null ? 0 : 120);
    public string BeforeSize =>
        change is null
            ? ""
            : DisplayText.Endpoint(change.BeforeAllocatedBytes, change.BeforePath is not null);
    public string AfterSize =>
        change is null
            ? ""
            : DisplayText.Endpoint(change.AfterAllocatedBytes, change.AfterPath is not null);
}

internal sealed record FolderLocation(FileReference Reference, string Path);

internal sealed record HistoryRow(ScanHistoryEntry Entry)
{
    public override string ToString() =>
        Entry.CompletedLocal.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture);
}

internal sealed record FolderNode(
    string Name,
    FolderLocation Location,
    int NextPage = 0,
    bool LoadMore = false,
    string? Change = null
)
{
    public override string ToString() =>
        DisplayText.Escape(Name) + (Change is null ? "" : "  " + Change);
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
