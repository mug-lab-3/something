using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;
using Something.Application.Scanning;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.StorageCompare;

// Isolated synthetic prototype. Source facts use production typed entities/codecs/constraints.
// Its rewritten tables/views must never be opened as an application history database.
internal sealed class HistoryDedupStore : IDisposable
{
    private sealed record FactVersion(Guid Id, StagedObservation Fact);

    private sealed record SampleVersion(Guid Id, FolderCapacitySample Sample);

    private readonly record struct Entry(long RecordIndex, int Ordinal, Guid Version);

    private sealed record Listing(long Id, Entry[] Entries);

    private readonly ScanStore store;
    private readonly DuckScanDbContext context;
    private readonly string mode;
    private readonly Dictionary<ulong, FactVersion> latestFacts = [];
    private readonly Dictionary<ulong, List<FactVersion>> olderFacts = [];
    private readonly Dictionary<FileReference, SampleVersion> latestSamples = [];
    private readonly Dictionary<FileReference, List<SampleVersion>> olderSamples = [];
    private readonly Dictionary<FileReference, Listing> listings = [];
    private readonly Dictionary<(FileReference Parent, long Bucket), Listing> blocks = [];
    private long nextListing,
        nextBlock;
    internal DuckDBConnection Connection => (DuckDBConnection)context.Database.GetDbConnection();
    internal long NewFacts { get; private set; }
    internal long NewListings { get; private set; }

    internal HistoryDedupStore(string path, string mode, int budgetMiB, CancellationToken token)
    {
        this.mode = mode;
        store =
            mode == "production"
                ? ScanStore.OpenSharedHistoryWriter(path, (ulong)budgetMiB << 20, token)
                : ScanStore.OpenDuckDiagnostic(path, token, (ulong)budgetMiB << 20);
        context = (DuckScanDbContext)store.HistoryContext;
        if (mode is "full" or "production")
            return;
        try
        {
            InitializeSharedSchema();
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    private void InitializeSharedSchema()
    {
        Execute("UPDATE something_format SET Application='Something.HistoryPrototype'");
        foreach (string table in new[] { "observation", "file_name", "stream", "folder_sample" })
        {
            var entity = context.Model.GetEntityTypes().Single(e => e.GetTableName() == table);
            var indexes = entity.GetIndexes().ToArray();
            foreach (var index in indexes)
                Execute($"DROP INDEX IF EXISTS \"{index.GetDatabaseName()}\"");
            Execute($"ALTER TABLE {table} RENAME TO state_{table}");
            foreach (var index in indexes)
            {
                string columns = string.Join(
                    ',',
                    index.Properties.Select(p => $"\"{p.GetColumnName()}\"")
                );
                Execute(
                    $"CREATE {(index.IsUnique ? "UNIQUE " : "")}INDEX \"state_{index.GetDatabaseName()}\" ON state_{table}({columns})"
                );
            }
        }
        Execute("DROP TABLE folder_name_history; DROP TABLE browser;");
        Execute(
            """
            CREATE TABLE scan_folder_ref(ScanId UUID,RecordIndex BIGINT,Sequence USMALLINT,
                ObservationVersion UUID,SampleVersion UUID,PRIMARY KEY(ScanId,RecordIndex));
            CREATE TABLE snapshot_ref(ScanId UUID,RecordIndex BIGINT,VersionId UUID,PRIMARY KEY(ScanId,RecordIndex));
            CREATE TABLE auxiliary_ref(ScanId UUID,RecordIndex BIGINT,VersionId UUID,PRIMARY KEY(ScanId,RecordIndex));
            CREATE TABLE scan_listing_ref(ScanId UUID,ParentIndex BIGINT,ParentSequence USMALLINT,
                ListingId BIGINT,PRIMARY KEY(ScanId,ParentIndex,ParentSequence));
            CREATE TABLE listing_entry(ListingId BIGINT,RecordIndex BIGINT,Ordinal INTEGER,VersionId UUID,
                Name BLOB,Sequence USMALLINT,IsDirectory BOOLEAN,Resolution INTEGER,LinksComplete BOOLEAN,
                StreamLogical BIGINT,StreamAllocated BIGINT,PRIMARY KEY(ListingId,RecordIndex,Ordinal));
            CREATE TABLE listing_block_map(ListingId BIGINT,Bucket BIGINT,BlockId BIGINT,PRIMARY KEY(ListingId,Bucket));
            CREATE TABLE block_entry(ListingId BIGINT,RecordIndex BIGINT,Ordinal INTEGER,VersionId UUID,
                Name BLOB,Sequence USMALLINT,IsDirectory BOOLEAN,Resolution INTEGER,LinksComplete BOOLEAN,
                StreamLogical BIGINT,StreamAllocated BIGINT,PRIMARY KEY(ListingId,RecordIndex,Ordinal));
            CREATE INDEX listing_parent ON scan_listing_ref(ParentIndex);
            CREATE INDEX entry_list ON listing_entry(ListingId);
            CREATE INDEX block_list ON block_entry(ListingId);
            """
        );
        string entries =
            mode == "blocks"
                ? "SELECT m.ListingId,e.* EXCLUDE(ListingId) FROM listing_block_map m JOIN block_entry e ON e.ListingId=m.BlockId"
                : "SELECT * FROM listing_entry";
        Execute($"CREATE VIEW shared_entry AS {entries}");
        string refs =
            mode == "reference"
                ? "SELECT * FROM snapshot_ref"
                : """
                    SELECT DISTINCT ScanId,RecordIndex,VersionId FROM (
                        SELECT m.ScanId,e.RecordIndex,e.VersionId FROM scan_listing_ref m JOIN shared_entry e ON e.ListingId=m.ListingId
                        UNION ALL SELECT ScanId,RecordIndex,ObservationVersion FROM scan_folder_ref
                        UNION ALL SELECT * FROM auxiliary_ref)
                    """;
        Execute($"CREATE VIEW observation_ref AS {refs}");
        View<ObservationEntity>("observation", "state_observation", "observation_ref", "VersionId");
        View<FileNameEntity>("file_name", "state_file_name", "observation_ref", "VersionId");
        View<StreamEntity>("stream", "state_stream", "observation_ref", "VersionId");
        View<FolderSampleEntity>(
            "folder_sample",
            "state_folder_sample",
            "scan_folder_ref",
            "SampleVersion"
        );
        Execute(
            """
            CREATE VIEW folder_name_history AS SELECT m.ScanId,n.* EXCLUDE(ScanId)
            FROM scan_folder_ref m JOIN state_file_name n ON n.ScanId=m.ObservationVersion AND n.RecordIndex=m.RecordIndex
            """
        );
        if (mode == "reference")
            Execute(
                """
                CREATE VIEW browser AS SELECT n.ScanId,n.ParentIndex,n.ParentSequence,n.RecordIndex,n.Ordinal,n.Name,
                    o.Sequence,o.IsDirectory,o.Resolution,o.LinksComplete,f.EntrySubtreeLogical,f.EntrySubtreeAllocated,
                    f.Sequence FolderSequence,f.EntrySubtreeUnresolved,f.UnobservedDirectories,s.Logical StreamLogical,s.Allocated StreamAllocated
                FROM file_name n JOIN observation o ON o.ScanId=n.ScanId AND o.RecordIndex=n.RecordIndex
                LEFT JOIN folder_sample f ON f.ScanId=n.ScanId AND f.RecordIndex=n.RecordIndex
                LEFT JOIN stream s ON s.ScanId=n.ScanId AND s.RecordIndex=n.RecordIndex AND octet_length(s.Name)=0
                WHERE n.Namespace<>2 AND o.Domain=0 AND o.RecordIndex>=16 AND o.Sequence IS NOT NULL AND o.IsDirectory IS NOT NULL
                """
            );
        else
            Execute(
                """
                CREATE VIEW browser AS SELECT m.ScanId,m.ParentIndex,m.ParentSequence,e.RecordIndex,e.Ordinal,e.Name,
                    e.Sequence,e.IsDirectory,e.Resolution,e.LinksComplete,f.EntrySubtreeLogical,f.EntrySubtreeAllocated,
                    f.Sequence FolderSequence,f.EntrySubtreeUnresolved,f.UnobservedDirectories,e.StreamLogical,e.StreamAllocated
                FROM scan_listing_ref m JOIN shared_entry e ON e.ListingId=m.ListingId
                LEFT JOIN scan_folder_ref r ON r.ScanId=m.ScanId AND r.RecordIndex=e.RecordIndex
                LEFT JOIN state_folder_sample f ON f.ScanId=r.SampleVersion AND f.RecordIndex=r.RecordIndex
                """
            );
    }

    private void View<T>(string name, string source, string refs, string version)
        where T : class
    {
        var entity = context.Model.FindEntityType(typeof(T))!;
        string columns = string.Join(
            ',',
            entity.GetProperties().Where(p => p.Name != "ScanId").Select(p => $"s.\"{p.Name}\"")
        );
        Execute(
            $"CREATE VIEW {name} AS SELECT m.ScanId,{columns} FROM {refs} m JOIN {source} s ON s.ScanId=m.{version} AND s.RecordIndex=m.RecordIndex"
        );
    }

    internal Guid Publish(BufferedScan buffer, CancellationToken token)
    {
        if (mode is "full" or "production")
        {
            Guid published = store.PublishBufferedScan(
                buffer,
                buffer.Descriptor.StartedUtc.AddMinutes(1),
                token
            );
            if (context is SharedHistoryDbContext shared)
            {
                NewFacts = shared.States.LongCount();
                NewListings = shared.Entries.Select(e => e.Listing).Distinct().LongCount();
            }
            else
                NewFacts += buffer.Count;
            return published;
        }
        var scan = ScanStore.CreateScanEntity(buffer.Descriptor);
        scan.State = StoredScanState.Completed;
        scan.Ended = buffer.Descriptor.StartedUtc.AddMinutes(1);
        var versions = new Dictionary<ulong, Guid>(buffer.Count);
        var changed = new List<StagedObservation>();
        foreach (var fact in buffer.Observations)
        {
            token.ThrowIfCancellationRequested();
            if (
                !latestFacts.TryGetValue(fact.RecordIndex, out var old)
                || !HistoryDedupData.Equal(old.Fact, fact)
            )
            {
                FactVersion? reused = olderFacts
                    .GetValueOrDefault(fact.RecordIndex)
                    ?.FirstOrDefault(v => HistoryDedupData.Equal(v.Fact, fact));
                var version = reused ?? new FactVersion(scan.Id, fact);
                if (old is not null)
                {
                    if (!olderFacts.TryGetValue(fact.RecordIndex, out var history))
                        olderFacts.Add(fact.RecordIndex, history = []);
                    if (!history.Any(v => v.Id == old.Id))
                        history.Add(old);
                }
                latestFacts[fact.RecordIndex] = version;
                if (reused is null)
                    changed.Add(fact);
            }
            versions.Add(fact.RecordIndex, latestFacts[fact.RecordIndex].Id);
        }
        using var transaction = context.Database.BeginTransaction();
        Append([scan], "scan", token);
        Append(changed.Select(o => ScanStore.ToEntity(scan.Id, o)), "state_observation", token);
        Append(
            changed.SelectMany(o => ScanStore.ToEntity(scan.Id, o).Names),
            "state_file_name",
            token
        );
        Append(
            changed.SelectMany(o => ScanStore.ToEntity(scan.Id, o).Streams),
            "state_stream",
            token
        );
        NewFacts += changed.Count;
        var newSamples = new List<FolderCapacitySample>();
        using (var map = Connection.CreateAppender("scan_folder_ref"))
            foreach (var sample in buffer.Aggregation.Folders.Values)
            {
                token.ThrowIfCancellationRequested();
                if (
                    !latestSamples.TryGetValue(sample.Reference, out var old)
                    || old.Sample != sample
                )
                {
                    var reused = olderSamples
                        .GetValueOrDefault(sample.Reference)
                        ?.FirstOrDefault(v => v.Sample == sample);
                    if (old is not null)
                    {
                        if (!olderSamples.TryGetValue(sample.Reference, out var history))
                            olderSamples.Add(sample.Reference, history = []);
                        if (!history.Any(v => v.Id == old.Id))
                            history.Add(old);
                    }
                    latestSamples[sample.Reference] = reused ?? new(scan.Id, sample);
                    if (reused is null)
                        newSamples.Add(sample);
                }
                map.CreateRow()
                    .AppendValue(scan.Id)
                    .AppendValue(checked((long)sample.Reference.RecordIndex))
                    .AppendValue(sample.Reference.SequenceNumber)
                    .AppendValue(versions[sample.Reference.RecordIndex])
                    .AppendValue(latestSamples[sample.Reference].Id)
                    .EndRow();
            }
        Append(
            newSamples.Select(s => ScanStore.ToEntity(scan.Id, s)),
            "state_folder_sample",
            token
        );
        if (mode == "reference")
        {
            using var map = Connection.CreateAppender("snapshot_ref");
            foreach (var pair in versions)
                map.CreateRow()
                    .AppendValue(scan.Id)
                    .AppendValue(checked((long)pair.Key))
                    .AppendValue(pair.Value)
                    .EndRow();
        }
        else
            PublishListings(buffer, versions, scan.Id, token);
        Append([ScanStore.ToEntity(scan.Id, buffer.Aggregation)], "volume_sample", token);
        Execute("DELETE FROM published_generation");
        Append<PublishedGenerationEntity>(
            [
                new()
                {
                    VolumeKey = scan.VolumeKey,
                    RangeStart = scan.RangeStart,
                    RangeCount = scan.RangeCount,
                    ScanId = scan.Id,
                },
            ],
            "published_generation",
            token
        );
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        context.DirtyGenerations.Clear();
        return scan.Id;
    }

    private void PublishListings(
        BufferedScan buffer,
        Dictionary<ulong, Guid> versions,
        Guid scanId,
        CancellationToken token
    )
    {
        var grouped = new Dictionary<FileReference, List<Entry>>();
        var listed = new HashSet<ulong>();
        foreach (var item in buffer.Observations)
        {
            token.ThrowIfCancellationRequested();
            if (item.File is not { Domain: CapacityDomain.Ordinary } file || item.RecordIndex < 16)
                continue;
            for (int i = 0; i < file.Names.Length; i++)
            {
                var name = file.Names[i];
                if (name.Namespace == FileNameNamespace.Dos)
                    continue;
                if (!grouped.TryGetValue(name.Parent, out var entries))
                    grouped.Add(name.Parent, entries = []);
                entries.Add(new(checked((long)item.RecordIndex), i, versions[item.RecordIndex]));
                listed.Add(item.RecordIndex);
            }
        }
        using (var aux = Connection.CreateAppender("auxiliary_ref"))
            foreach (var item in buffer.Observations)
                if (
                    !listed.Contains(item.RecordIndex) && item.File is not { IsDirectory: true }
                    || item.File is null
                    || item.File.IsDirectory
                        && !buffer.Aggregation.Folders.ContainsKey(item.File.Reference)
                )
                    aux.CreateRow()
                        .AppendValue(scanId)
                        .AppendValue(checked((long)item.RecordIndex))
                        .AppendValue(versions[item.RecordIndex])
                        .EndRow();
        using var map = Connection.CreateAppender("scan_listing_ref");
        using var entriesAppender = Connection.CreateAppender(
            mode == "blocks" ? "block_entry" : "listing_entry"
        );
        using var blockMap = Connection.CreateAppender("listing_block_map");
        foreach (var (parent, items) in grouped.OrderBy(p => p.Key.RecordIndex))
        {
            token.ThrowIfCancellationRequested();
            Entry[] entries = [.. items.OrderBy(e => e.RecordIndex).ThenBy(e => e.Ordinal)];
            if (
                !listings.TryGetValue(parent, out var listing)
                || !listing.Entries.AsSpan().SequenceEqual(entries)
            )
            {
                listing = new(++nextListing, entries);
                listings[parent] = listing;
                NewListings++;
                if (mode == "blocks")
                {
                    foreach (var bucket in entries.GroupBy(e => e.RecordIndex / 1024))
                    {
                        Entry[] rows = [.. bucket];
                        if (
                            !blocks.TryGetValue((parent, bucket.Key), out var block)
                            || !block.Entries.AsSpan().SequenceEqual(rows)
                        )
                        {
                            block = new(++nextBlock, rows);
                            blocks[(parent, bucket.Key)] = block;
                            WriteEntries(entriesAppender, block.Id, rows, token);
                        }
                        blockMap
                            .CreateRow()
                            .AppendValue(listing.Id)
                            .AppendValue(bucket.Key)
                            .AppendValue(block.Id)
                            .EndRow();
                    }
                }
                else
                    WriteEntries(entriesAppender, listing.Id, entries, token);
            }
            map.CreateRow()
                .AppendValue(scanId)
                .AppendValue(checked((long)parent.RecordIndex))
                .AppendValue(parent.SequenceNumber)
                .AppendValue(listing.Id)
                .EndRow();
        }
    }

    private void WriteEntries(
        DuckDBAppender appender,
        long id,
        Entry[] entries,
        CancellationToken token
    )
    {
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var file = latestFacts[(ulong)entry.RecordIndex].Fact.File!;
                var stream = file.Streams.FirstOrDefault(s => s.Name.Length == 0);
                var row = appender
                    .CreateRow()
                    .AppendValue(id)
                    .AppendValue(entry.RecordIndex)
                    .AppendValue(entry.Ordinal)
                    .AppendValue(entry.Version)
                    .AppendValue(StorageEncoding.Name(file.Names[entry.Ordinal].Name))
                    .AppendValue(file.Reference.SequenceNumber)
                    .AppendValue(file.IsDirectory)
                    .AppendValue((int)file.Resolution)
                    .AppendValue(file.LinkSetComplete);
                if (stream is null)
                    row.AppendNullValue().AppendNullValue();
                else
                    row.AppendValue(stream.LogicalBytes).AppendValue(stream.AllocatedBytes);
                row.EndRow();
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Initial append failure, list={id}: {error}");
            throw;
        }
    }

    private void Append<T>(IEnumerable<T> values, string table, CancellationToken token)
        where T : class
    {
        using var insert = new DuckModelInsert<T>(context, table);
        foreach (var value in values)
            insert.Add(value, token);
    }

    internal void Checkpoint() => Execute("CHECKPOINT");

    internal static void DeleteGeneration(DuckDBConnection connection, string mode, Guid id)
    {
        using var transaction = connection.BeginTransaction();
        void Delete(string table, string column = "ScanId")
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {table} WHERE {column}='{id:D}'::UUID";
            command.ExecuteNonQuery();
        }
        if (mode == "production")
        {
            foreach (
                string table in new[]
                {
                    "published_generation",
                    "scan_step_timing",
                    "scan_timing",
                    "volume_sample",
                }
            )
                Delete(table);
            using var collect = connection.CreateCommand();
            collect.CommandText = $"SELECT Number FROM scan WHERE Id='{id:D}'::UUID";
            collect.CommandText = SharedHistoryPublication.DeleteGenerationSql(
                Convert.ToInt32(collect.ExecuteScalar())
            );
            collect.ExecuteNonQuery();
            Delete("history_snapshot", "Id");
        }
        else if (mode == "full")
        {
            foreach (
                string table in new[]
                {
                    "published_generation",
                    "scan_step_timing",
                    "scan_timing",
                    "browser",
                    "folder_name_history",
                    "file_name",
                    "stream",
                    "folder_sample",
                    "observation",
                    "volume_sample",
                }
            )
                Delete(table);
            Delete("history_snapshot", "Id");
        }
        else
            foreach (
                string table in new[]
                {
                    "published_generation",
                    "snapshot_ref",
                    "auxiliary_ref",
                    "scan_listing_ref",
                    "scan_folder_ref",
                    "volume_sample",
                }
            )
                Delete(table);
        Delete("scan", "Id");
        transaction.Commit();
    }

    internal void Execute(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        store.Dispose();
        latestFacts.Clear();
        olderFacts.Clear();
        latestSamples.Clear();
        olderSamples.Clear();
        listings.Clear();
        blocks.Clear();
    }
}
