using System.Collections.Immutable;
using System.Numerics;
using Something.Application.Browsing;
using Something.Ntfs;

namespace Something.Application.Comparison;

public sealed record EntryChange(
    FolderEntry? Before,
    FolderEntry? After,
    bool CapacityUncertain = false
)
{
    public FolderEntry Entry =>
        After ?? Before ?? throw new InvalidOperationException("Empty comparison row.");
    public UInt128? BeforeBytes => Known(Before);
    public UInt128? AfterBytes => Known(After);
    public BigInteger? Delta =>
        !CapacityUncertain && BeforeBytes is { } before && AfterBytes is { } after
            ? BigInteger.CreateChecked(after) - BigInteger.CreateChecked(before)
            : null;
    public FolderChangeStatus Status =>
        Before is null ? FolderChangeStatus.NotObservedBefore
        : After is null ? FolderChangeStatus.NotObservedAfter
        : Delta is null ? FolderChangeStatus.UncertainCapacity
        : FolderChangeStatus.Comparable;

    private static UInt128? Known(FolderEntry? entry) =>
        entry is { CapacityIncomplete: false, NamesComplete: true }
            ? entry.Data?.AllocatedBytes
            : null;
}

public static class EntryChanges
{
    /// <summary>Match visible names with sequence identity; pair an unambiguous rename only.
    /// An absent endpoint is never a zero-sized file or a confirmed creation/deletion.</summary>
    public static ImmutableArray<EntryChange> Compare(
        IEnumerable<FolderEntry> before,
        IEnumerable<FolderEntry> after,
        CancellationToken token = default
    )
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        token.ThrowIfCancellationRequested();
        var old = before.ToDictionary(row => (row.Reference, row.Name));
        var current = after.ToDictionary(row => (row.Reference, row.Name));
        var rows = new List<EntryChange>();
        foreach (var key in old.Keys.Intersect(current.Keys).ToArray())
        {
            token.ThrowIfCancellationRequested();
            rows.Add(new(old[key], current[key]));
            old.Remove(key);
            current.Remove(key);
        }
        var oldGroups = old
            .Values.GroupBy(row => row.Reference)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var newGroups = current
            .Values.GroupBy(row => row.Reference)
            .ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var reference in oldGroups.Keys.Union(newGroups.Keys))
        {
            token.ThrowIfCancellationRequested();
            var left = oldGroups.GetValueOrDefault(reference, []);
            var right = newGroups.GetValueOrDefault(reference, []);
            if (
                left.Length == 1
                && right.Length == 1
                && left[0].IsDirectory == right[0].IsDirectory
            )
                rows.Add(new(left[0], right[0]));
            else
            {
                rows.AddRange(left.Select(row => new EntryChange(row, null)));
                rows.AddRange(right.Select(row => new EntryChange(null, row)));
            }
        }
        token.ThrowIfCancellationRequested();
        return
        [
            .. rows.OrderByDescending(row => row.Delta is not null)
                .ThenByDescending(row =>
                    row.Delta is { } delta ? BigInteger.Abs(delta) : BigInteger.Zero
                )
                .ThenBy(row => row.Entry.Name, StringComparer.Ordinal)
                .ThenBy(row => row.Entry.Reference.RecordIndex)
                .ThenBy(row => row.Entry.Reference.SequenceNumber),
        ];
    }
}
