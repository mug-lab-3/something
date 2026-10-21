using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

/// <summary>One visible name in one shared listing: the same entry in every scan of the range that
/// uses the listing. File sizes are per entry; folder sizes come from <see cref="SearchFolderSizes"/>.</summary>
internal readonly record struct SearchSegmentRow(
    FileReference Reference,
    FileReference Parent,
    string Name,
    bool IsDirectory,
    uint? Attributes,
    int FirstScan,
    int LastScan,
    int ScanCount,
    ulong? FileSize
);

/// <summary>Subtree sizes of one folder identity over the scans that have its sample.</summary>
internal readonly record struct SearchFolderSizes(
    int ScanCount,
    int FirstScan,
    int LastScan,
    bool HasUnresolvedSize,
    UInt128? FirstSize,
    UInt128? LastSize,
    UInt128? MinimumSize,
    UInt128? MaximumSize
);

internal readonly record struct SearchFolderLink(FileReference Parent, string Name);

internal readonly record struct SearchSeriesRow(
    FileReference Reference,
    FileReference Parent,
    string Name,
    int ScanIndex,
    UInt128? Size
);

// Searches read every visible name of every scan in the range. Like the whole-generation reads in
// NativeReads, these read typed columns from streams: EF materialization took about half of a
// two-million-file read. An entry of a shared listing is identical in every scan that uses the
// listing, so names are read once per listing, not once per scan: with 2 million files and 30
// scans, 60 million per-scan rows became about 2.1 million listing rows. Aggregating those in
// DuckDB with the name in the key exceeded a 1 GiB limit and spilled; DuckDB now only sorts and
// the managed engine folds each identity as the stream passes. Results stream instead of
// materializing, so the engine's memory holds the sort, not the whole answer.
public sealed partial class ScanStore
{
    private const string LowMask = "18446744073709551615::UHUGEINT";

    private static string ScanValues(IReadOnlyList<int> generations) =>
        string.Join(',', generations.Select((generation, index) => $"({generation},{index})"));

    private static string FileSize(SearchSizeKind kind) =>
        kind == SearchSizeKind.Logical ? "e.StreamLogical" : "e.StreamAllocated";

    private static string FolderSize(SearchSizeKind kind) =>
        kind == SearchSizeKind.Logical ? "f.EntrySubtreeLogical" : "f.EntrySubtreeAllocated";

    // Every listing used by the range, with the first, last and number of scans that use it.
    private static string SearchListings(IReadOnlyList<int> generations) =>
        $"""
            s(Generation,Idx) AS (VALUES {ScanValues(generations)}),
            l AS (
                SELECT r.Listing,r.ParentIndex,r.ParentSequence,min(s.Idx) FirstIdx,max(s.Idx) LastIdx,
                    count(*) Scans
                FROM s JOIN scan_listing_ref r ON r.Generation=s.Generation
                GROUP BY r.Listing,r.ParentIndex,r.ParentSequence)
            """;

    // A folder size is known only when its sample belongs to the same identity and nothing below
    // it is unresolved or unobserved.
    private static string SearchFolderSamples(
        IReadOnlyList<int> generations,
        SearchSizeKind kind,
        string? filter = null
    ) =>
        $"""
            s(Generation,Idx) AS (VALUES {ScanValues(generations)}),
            f AS (
                SELECT fr.RecordIndex,fr.Sequence,s.Idx,
                    CASE WHEN f.Sequence=fr.Sequence AND f.EntrySubtreeUnresolved=0
                        AND f.UnobservedDirectories=0 THEN {FolderSize(kind)} END Size
                FROM s JOIN scan_folder_ref fr ON fr.Generation=s.Generation
                JOIN state_folder_sample f ON f.Version=fr.SampleVersion AND f.RecordIndex=fr.RecordIndex
                {filter})
            """;

    /// <summary>The number of listing rows of the range and the largest record index, which size
    /// the partitions of <see cref="ReadSearchSegments"/>.</summary>
    internal (long Rows, long MaximumRecord) ReadSearchSegmentBounds(IReadOnlyList<int> generations)
    {
        using var rows = new NativeRows(
            context,
            $"""
            WITH {SearchListings(generations)}
            SELECT count(*),coalesce(max(e.RecordIndex),0) FROM l JOIN shared_entry e ON e.Listing=l.Listing
            """
        );
        return (rows.Reader.GetInt64(0), rows.Reader.GetInt64(1));
    }

    /// <summary>Listing rows of records in [<paramref name="first"/>, <paramref name="last"/>],
    /// ordered by identity and first scan.</summary>
    internal IEnumerable<SearchSegmentRow> ReadSearchSegments(
        IReadOnlyList<int> generations,
        SearchSizeKind kind,
        long first,
        long last,
        CancellationToken token
    )
    {
        using var rows = new NativeRows(
            context,
            $"""
            WITH {SearchListings(generations)}
            SELECT e.RecordIndex,e.Sequence,l.ParentIndex,l.ParentSequence,e.Name,e.IsDirectory,
                e.StandardFileAttributes,l.FirstIdx,l.LastIdx,l.Scans,
                CASE WHEN NOT e.IsDirectory AND e.Resolution={(int)CapacityResolution.Resolved}
                    THEN coalesce({FileSize(kind)},0) END
            FROM l JOIN shared_entry e ON e.Listing=l.Listing
            WHERE e.RecordIndex BETWEEN {first} AND {last}
            ORDER BY e.RecordIndex,e.Sequence,l.FirstIdx
            """,
            streaming: true
        );
        for (; rows.HasRow; rows.Next())
        {
            token.ThrowIfCancellationRequested();
            var row = rows.Reader;
            yield return new(
                Reference(row, 0, 1),
                Reference(row, 2, 3),
                NativeName(row, 4),
                row.GetBoolean(5),
                row.IsDBNull(6) ? null : checked((uint)row.GetInt64(6)),
                row.GetInt32(7),
                row.GetInt32(8),
                checked((int)row.GetInt64(9)),
                row.IsDBNull(10) ? null : checked((ulong)row.GetInt64(10))
            );
        }
    }

    /// <summary>Subtree sizes of every folder identity over the range.</summary>
    internal Dictionary<FileReference, SearchFolderSizes> ReadSearchFolderSizes(
        IReadOnlyList<int> generations,
        SearchSizeKind kind,
        CancellationToken token
    )
    {
        using var rows = new NativeRows(
            context,
            $"""
            WITH {SearchFolderSamples(generations, kind)},
            a AS (
                SELECT RecordIndex,Sequence,count(*) Scans,min(Idx) FirstIdx,max(Idx) LastIdx,
                    bool_or(Size IS NULL) AnyUnknown,max(Size) FILTER (WHERE Idx=0) FirstSize,
                    arg_max_null(Size,Idx) LastSize,min(Size) MinSize,max(Size) MaxSize
                FROM f GROUP BY RecordIndex,Sequence)
            SELECT RecordIndex,Sequence,Scans,FirstIdx,LastIdx,AnyUnknown,coalesce(MaxSize>>64<>0,false),
                (FirstSize&{LowMask})::UBIGINT,(LastSize&{LowMask})::UBIGINT,
                (MinSize&{LowMask})::UBIGINT,(MaxSize&{LowMask})::UBIGINT,
                (FirstSize>>64)::UBIGINT,(LastSize>>64)::UBIGINT,(MinSize>>64)::UBIGINT,(MaxSize>>64)::UBIGINT
            FROM a
            """,
            streaming: true
        );
        var result = new Dictionary<FileReference, SearchFolderSizes>();
        for (; rows.HasRow; rows.Next())
        {
            token.ThrowIfCancellationRequested();
            var row = rows.Reader;
            bool wide = row.GetBoolean(6);
            result.Add(
                Reference(row, 0, 1),
                new(
                    checked((int)row.GetInt64(2)),
                    row.GetInt32(3),
                    row.GetInt32(4),
                    row.GetBoolean(5),
                    Capacity(row, 7, 11, wide),
                    Capacity(row, 8, 12, wide),
                    Capacity(row, 9, 13, wide),
                    Capacity(row, 10, 14, wide)
                )
            );
        }
        return result;
    }

    /// <summary>Subtree sizes of the given folders in every scan that has them, ordered by
    /// identity and scan.</summary>
    internal IEnumerable<(
        FileReference Reference,
        int ScanIndex,
        UInt128? Size
    )> ReadSearchFolderSeries(
        IReadOnlyList<int> generations,
        SearchSizeKind kind,
        IReadOnlyCollection<FileReference> identities,
        CancellationToken token
    )
    {
        using var identity = new SearchIdentities(this, identities, token);
        using var rows = new NativeRows(
            context,
            $"""
            WITH {SearchFolderSamples(
                generations,
                kind,
                "SEMI JOIN search_identity i ON i.RecordIndex=fr.RecordIndex AND i.Sequence=fr.Sequence"
            )}
            SELECT RecordIndex,Sequence,Idx,coalesce(Size>>64<>0,false),(Size&{LowMask})::UBIGINT,
                (Size>>64)::UBIGINT
            FROM f ORDER BY RecordIndex,Sequence,Idx
            """,
            streaming: true
        );
        for (; rows.HasRow; rows.Next())
        {
            token.ThrowIfCancellationRequested();
            var row = rows.Reader;
            yield return (
                Reference(row, 0, 1),
                row.GetInt32(2),
                Capacity(row, 4, 5, row.GetBoolean(3))
            );
        }
    }

    // A temporary table of identities for semi joins. TEMP tables stay in memory; the engine
    // writes them to disk only after spilling was enabled as a last resort.
    private sealed class SearchIdentities : IDisposable
    {
        private readonly ScanStore store;

        internal SearchIdentities(
            ScanStore store,
            IReadOnlyCollection<FileReference> identities,
            CancellationToken token
        )
        {
            this.store = store;
            store.ExecuteSearch(
                "CREATE OR REPLACE TEMP TABLE search_identity(RecordIndex BIGINT,Sequence USMALLINT);"
            );
            try
            {
                using var appender = (
                    (DuckDB.NET.Data.DuckDBConnection)store.context.Database.GetDbConnection()
                ).CreateAppender("search_identity");
                foreach (var identity in identities)
                {
                    token.ThrowIfCancellationRequested();
                    appender
                        .CreateRow()
                        .AppendValue(checked((long)identity.RecordIndex))
                        .AppendValue(identity.SequenceNumber)
                        .EndRow();
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose() => store.ExecuteSearch("DROP TABLE IF EXISTS search_identity;");
    }

    private void ExecuteSearch(string sql)
    {
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Entries of every scan with the size of the requested kind. A folder size is known only when
    // its sample belongs to the same identity and nothing below it is unresolved or unobserved.
    private static string SearchObservations(
        IReadOnlyList<int> generations,
        SearchSizeKind kind,
        string? entryFilter = null
    )
    {
        string file = kind == SearchSizeKind.Logical ? "e.StreamLogical" : "e.StreamAllocated";
        string folder =
            kind == SearchSizeKind.Logical ? "f.EntrySubtreeLogical" : "f.EntrySubtreeAllocated";
        return $"""
            s(Generation,Idx) AS (VALUES {ScanValues(generations)}),
            o AS (
                SELECT s.Idx,r.ParentIndex,r.ParentSequence,e.RecordIndex,e.Sequence,e.Name,e.IsDirectory,
                    e.StandardFileAttributes,
                    CASE WHEN e.IsDirectory THEN
                            CASE WHEN f.Sequence=e.Sequence AND f.EntrySubtreeUnresolved=0
                                AND f.UnobservedDirectories=0 THEN {folder} END
                        WHEN e.Resolution={(int)CapacityResolution.Resolved}
                            THEN coalesce({file},0)::UHUGEINT END Size
                FROM s JOIN scan_listing_ref r ON r.Generation=s.Generation
                JOIN shared_entry e ON e.Listing=r.Listing
                -- Only folders have references, so files need no IsDirectory condition here; one
                -- in this join made DuckDB leave the hash join (measured 1.5 s instead of 5 ms).
                LEFT JOIN scan_folder_ref fr ON fr.Generation=s.Generation
                    AND fr.RecordIndex=e.RecordIndex AND fr.Sequence=e.Sequence
                LEFT JOIN state_folder_sample f ON f.Version=fr.SampleVersion AND f.RecordIndex=fr.RecordIndex
                {entryFilter})
            """;
    }

    /// <summary>Observations of the given identities in every scan, ordered by identity and scan.</summary>
    internal IEnumerable<SearchSeriesRow> ReadSearchSeries(
        IReadOnlyList<int> generations,
        SearchSizeKind kind,
        IReadOnlyCollection<FileReference> identities,
        CancellationToken token
    )
    {
        using var identity = new SearchIdentities(this, identities, token);
        using var rows = new NativeRows(
            context,
            $"""
            WITH {SearchObservations(
                generations,
                kind,
                "SEMI JOIN search_identity i ON i.RecordIndex=e.RecordIndex AND i.Sequence=e.Sequence"
            )}
            SELECT RecordIndex,Sequence,ParentIndex,ParentSequence,Name,Idx,coalesce(Size>>64<>0,false),
                (Size&{LowMask})::UBIGINT,(Size>>64)::UBIGINT
            FROM o ORDER BY RecordIndex,Sequence,Idx
            """,
            streaming: true
        );
        for (; rows.HasRow; rows.Next())
        {
            token.ThrowIfCancellationRequested();
            var row = rows.Reader;
            yield return new(
                Reference(row, 0, 1),
                Reference(row, 2, 3),
                NativeName(row, 4),
                row.GetInt32(5),
                Capacity(row, 7, 8, row.GetBoolean(6))
            );
        }
    }

    /// <summary>Visible folder names of one generation: identity to parent and name.</summary>
    internal Dictionary<FileReference, SearchFolderLink> ReadSearchFolderLinks(
        int generation,
        CancellationToken token
    )
    {
        using var rows = new NativeRows(
            context,
            $"SELECT e.RecordIndex,e.Sequence,r.ParentIndex,r.ParentSequence,e.Name FROM scan_listing_ref r JOIN shared_entry e ON e.Listing=r.Listing WHERE r.Generation={generation} AND e.IsDirectory"
        );
        var links = new Dictionary<FileReference, SearchFolderLink>();
        for (; rows.HasRow; rows.Next())
        {
            token.ThrowIfCancellationRequested();
            var row = rows.Reader;
            links.TryAdd(Reference(row, 0, 1), new(Reference(row, 2, 3), NativeName(row, 4)));
        }
        return links;
    }

    /// <summary>Records of one generation that have a named DATA stream.</summary>
    internal HashSet<ulong> ReadSearchNamedStreams(int generation, CancellationToken token)
    {
        using var rows = new NativeRows(
            context,
            $"""
            SELECT DISTINCT m.RecordIndex FROM generation_observation m
            JOIN state_stream t ON t.Version=m.Version AND t.RecordIndex=m.RecordIndex
            WHERE m.Generation={generation} AND octet_length(t.Name)>0
            """
        );
        var result = new HashSet<ulong>();
        for (; rows.HasRow; rows.Next())
        {
            token.ThrowIfCancellationRequested();
            result.Add(checked((ulong)rows.Reader.GetInt64(0)));
        }
        return result;
    }

    internal int[] ReadGenerationNumbers(IReadOnlyList<Guid> scanIds)
    {
        var db = (SharedHistoryDbContext)context;
        var numbers = db
            .Scans.AsNoTracking()
            .Where(s => scanIds.Contains(s.Id))
            .Select(s => new
            {
                s.Id,
                Number = EF.Property<int>(s, SharedHistoryDbContext.GenerationNumber),
            })
            .ToDictionary(s => s.Id, s => s.Number);
        return
        [
            .. scanIds.Select(id =>
                numbers.TryGetValue(id, out int number)
                    ? number
                    : throw new InvalidDataException("A history entry has no generation.")
            ),
        ];
    }

    private static FileReference Reference(DbDataReader row, int index, int sequence) =>
        FileReference.Create(
            checked((ulong)row.GetInt64(index)),
            row.GetFieldValue<ushort>(sequence)
        );

    private static UInt128? Capacity(DbDataReader row, int low, int high, bool wide) =>
        row.IsDBNull(low) ? null
        : wide ? ((UInt128)row.GetFieldValue<ulong>(high) << 64) | row.GetFieldValue<ulong>(low)
        : row.GetFieldValue<ulong>(low);
}
