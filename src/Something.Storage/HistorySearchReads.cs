using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

/// <summary>One visible name of one identity, aggregated over the scans in which it was observed.</summary>
internal readonly record struct SearchSummaryRow(
    FileReference Reference,
    FileReference Parent,
    string Name,
    bool IsDirectory,
    int ObservedCount,
    int FirstObserved,
    int LastObserved,
    uint? Attributes,
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
// NativeReads, these read typed columns from ordered streams: EF materialization took about half of
// a two-million-file read. Shared listings and folder samples are joined per generation in the
// engine; the aggregation runs in DuckDB under the engine's memory limit, not in .NET.
public sealed partial class ScanStore
{
    private const string LowMask = "18446744073709551615::UHUGEINT";

    private static string ScanValues(IReadOnlyList<int> generations) =>
        string.Join(',', generations.Select((generation, index) => $"({generation},{index})"));

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

    /// <summary>Rows are ordered by identity, then by first observation.</summary>
    internal IEnumerable<SearchSummaryRow> ReadSearchSummaries(
        IReadOnlyList<int> generations,
        SearchSizeKind kind,
        CancellationToken token
    )
    {
        using var rows = new NativeRows(
            context,
            $"""
            WITH {SearchObservations(generations, kind)},
            a AS (
                SELECT RecordIndex,Sequence,ParentIndex,ParentSequence,Name,bool_or(IsDirectory) IsDirectory,
                    count(*) Observed,min(Idx) FirstIdx,max(Idx) LastIdx,
                    arg_max_null(StandardFileAttributes,Idx) Attributes,bool_or(Size IS NULL) AnyUnknown,
                    max(Size) FILTER (WHERE Idx=0) FirstSize,arg_max_null(Size,Idx) LastSize,
                    min(Size) MinSize,max(Size) MaxSize
                FROM o GROUP BY RecordIndex,Sequence,ParentIndex,ParentSequence,Name)
            SELECT RecordIndex,Sequence,ParentIndex,ParentSequence,Name,IsDirectory,Observed,FirstIdx,LastIdx,
                Attributes,AnyUnknown,coalesce(MaxSize>>64<>0,false),
                (FirstSize&{LowMask})::UBIGINT,(LastSize&{LowMask})::UBIGINT,
                (MinSize&{LowMask})::UBIGINT,(MaxSize&{LowMask})::UBIGINT,
                (FirstSize>>64)::UBIGINT,(LastSize>>64)::UBIGINT,(MinSize>>64)::UBIGINT,(MaxSize>>64)::UBIGINT
            FROM a ORDER BY RecordIndex,Sequence,FirstIdx
            """
        );
        for (; rows.HasRow; rows.Next())
        {
            token.ThrowIfCancellationRequested();
            var row = rows.Reader;
            bool wide = row.GetBoolean(11);
            yield return new(
                Reference(row, 0, 1),
                Reference(row, 2, 3),
                NativeName(row, 4),
                row.GetBoolean(5),
                checked((int)row.GetInt64(6)),
                row.GetInt32(7),
                row.GetInt32(8),
                row.IsDBNull(9) ? null : checked((uint)row.GetInt64(9)),
                row.GetBoolean(10),
                Capacity(row, 12, 16, wide),
                Capacity(row, 13, 17, wide),
                Capacity(row, 14, 18, wide),
                Capacity(row, 15, 19, wide)
            );
        }
    }

    /// <summary>Observations of the given identities, ordered by identity and scan.</summary>
    internal IEnumerable<SearchSeriesRow> ReadSearchSeries(
        IReadOnlyList<int> generations,
        SearchSizeKind kind,
        IReadOnlyCollection<FileReference> identities,
        CancellationToken token
    )
    {
        var connection = context.Database.GetDbConnection();
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        Execute(
            connection,
            transaction,
            "CREATE OR REPLACE TEMP TABLE search_identity(RecordIndex BIGINT,Sequence USMALLINT);"
        );
        try
        {
            using (
                var appender = ((DuckDB.NET.Data.DuckDBConnection)connection).CreateAppender(
                    "search_identity"
                )
            )
                foreach (var identity in identities)
                {
                    token.ThrowIfCancellationRequested();
                    appender
                        .CreateRow()
                        .AppendValue(checked((long)identity.RecordIndex))
                        .AppendValue(identity.SequenceNumber)
                        .EndRow();
                }
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
                """
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
        finally
        {
            Execute(connection, transaction, "DROP TABLE IF EXISTS search_identity;");
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

    private static void Execute(DbConnection connection, DbTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
