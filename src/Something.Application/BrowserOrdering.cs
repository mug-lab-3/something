using Something.Storage;

namespace Something.Application;

public enum BrowserColumn
{
    Name,
    Size,
    Change,
}

public readonly record struct BrowserOrder(BrowserColumn Column, bool Descending)
{
    public BrowserOrder Toggle(BrowserColumn column) =>
        new(column, column == Column ? !Descending : column != BrowserColumn.Name);
}

/// <summary>Order complete result sets before pagination; compare raw values, never display text.</summary>
public static class BrowserOrdering
{
    public static FolderEntry[] Entries(IEnumerable<FolderEntry> rows, BrowserOrder order)
    {
        var grouped = rows.OrderByDescending(row => row.IsDirectory);
        var sorted =
            order.Column == BrowserColumn.Name
                ? Text(grouped, row => row.Name, order.Descending)
                : Number(grouped, row => row.Data?.AllocatedBytes, order.Descending);
        return
        [
            .. sorted
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.Ordinal)
                .ThenBy(row => row.Reference.RecordIndex)
                .ThenBy(row => row.Reference.SequenceNumber)
                .ThenBy(row => row.NameOrdinal),
        ];
    }

    public static EntryChange[] EntryChanges(IEnumerable<EntryChange> rows, BrowserOrder order)
    {
        var grouped = rows.OrderByDescending(row => row.Entry.IsDirectory);
        var sorted = order.Column switch
        {
            BrowserColumn.Name => Text(grouped, row => row.Entry.Name, order.Descending),
            BrowserColumn.Change => Number(grouped, row => row.Delta, order.Descending),
            _ => Number(grouped, row => row.Entry.Data?.AllocatedBytes, order.Descending),
        };
        return
        [
            .. sorted
                .ThenBy(row => row.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Entry.Name, StringComparer.Ordinal)
                .ThenBy(row => row.Entry.Reference.RecordIndex)
                .ThenBy(row => row.Entry.Reference.SequenceNumber)
                .ThenBy(row => row.Entry.NameOrdinal),
        ];
    }

    public static RankedFolder[] Folders(IEnumerable<RankedFolder> rows, BrowserOrder order)
    {
        var grouped = rows.OrderBy(_ => 0);
        var sorted =
            order.Column == BrowserColumn.Name
                ? Text(grouped, row => row.RelativePath, order.Descending)
                : Number<RankedFolder, UInt128>(
                    grouped,
                    row => row.Data.AllocatedBytes,
                    order.Descending
                );
        return
        [
            .. sorted
                .ThenBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.RelativePath, StringComparer.Ordinal)
                .ThenBy(row => row.Sample.Reference.RecordIndex)
                .ThenBy(row => row.Sample.Reference.SequenceNumber),
        ];
    }

    public static FolderChange[] FolderChanges(IEnumerable<FolderChange> rows, BrowserOrder order)
    {
        var grouped = rows.OrderBy(_ => 0);
        var sorted = order.Column switch
        {
            BrowserColumn.Name => Text(
                grouped,
                row => row.AfterPath ?? row.BeforePath ?? "",
                order.Descending
            ),
            BrowserColumn.Change => Number(grouped, row => row.AllocatedDelta, order.Descending),
            _ => Number(grouped, row => row.AfterAllocatedBytes, order.Descending),
        };
        return
        [
            .. sorted
                .ThenBy(row => row.AfterPath ?? row.BeforePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.AfterPath ?? row.BeforePath, StringComparer.Ordinal)
                .ThenBy(row => row.Reference.RecordIndex)
                .ThenBy(row => row.Reference.SequenceNumber),
        ];
    }

    private static IOrderedEnumerable<T> Text<T>(
        IOrderedEnumerable<T> rows,
        Func<T, string> key,
        bool descending
    ) =>
        descending
            ? rows.ThenByDescending(key, StringComparer.OrdinalIgnoreCase)
            : rows.ThenBy(key, StringComparer.OrdinalIgnoreCase);

    private static IOrderedEnumerable<T> Number<T, TKey>(
        IOrderedEnumerable<T> rows,
        Func<T, TKey?> key,
        bool descending
    )
        where TKey : struct, IComparable<TKey>
    {
        var known = rows.ThenBy(row => key(row) is null);
        return descending ? known.ThenByDescending(key) : known.ThenBy(key);
    }
}
