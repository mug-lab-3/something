using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Something.Ntfs;
using Something.Storage;

namespace Something.App;

public sealed partial class EntryRow(FolderEntry entry)
{
    public FolderEntry Entry { get; } = entry;
    public string Name => DisplayText.Escape(Entry.Name);
    public string Kind => Entry.IsDirectory ? "フォルダ" : "ファイル";
    public Symbol Icon => Entry.IsDirectory ? Symbol.Folder : Symbol.Document;
    public string Size =>
        DisplayText.Bytes(Entry.Data?.AllocatedBytes)
        + (
            Entry.Data is not null && (Entry.CapacityIncomplete || !Entry.NamesComplete) ? " *" : ""
        );
    public string SizeDescription =>
        "ディスク上の割り当て容量"
        + (
            Entry.CapacityIncomplete
                ? "。容量が未解決の項目があります。未解決分は表示値に含まれません"
                : ""
        )
        + (!Entry.NamesComplete ? "。名前集合が未確定です" : "");
}

public sealed partial class RankingRow(RankedFolder entry, string drive)
{
    public RankedFolder Entry { get; } = entry;
    public string Path => $"{drive}\\{DisplayText.Escape(Entry.RelativePath)}";
    public string Size => DisplayText.Bytes(Entry.Data.AllocatedBytes);
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
    bool LoadMore = false
)
{
    public override string ToString() => DisplayText.Escape(Name);
}

internal static class DisplayText
{
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
