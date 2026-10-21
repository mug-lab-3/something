using System.Buffers.Binary;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Something.Application.Scanning;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

// Publication compares the in-memory buffer with the previous generation while streaming its
// rows once. Working memory is a few arrays per observation; nothing copies the generation into
// TEMP tables. Comparisons are exact over every stored column, including UTF-16 name units,
// aliases, streams and uncertainty. Only changed states and new listings are written.
internal static class SharedHistoryPublication
{
    internal const int LargeListingThreshold = 4096;
    internal const int BlockRecordRange = 1024;

    private const byte ObservationMatched = 1,
        NamesDiffer = 2,
        StreamsDiffer = 4;

    internal static void Publish(
        SharedHistoryDbContext context,
        ScanEntity scan,
        BufferedScan buffer,
        CancellationToken token,
        ScanWorkReporter reporter
    )
    {
        int generation = NumberOf(context, scan);
        int previous = PreviousGeneration(context, scan);
        var members = new Members(buffer.Observations, generation, token);
        reporter.Start(ScanWorkStage.WritingFiles, members.Count);
        if (previous != 0)
            CompareStates(context, previous, members, token);
        WriteStates(context, scan.Id, members, token, reporter);
        var samples = buffer.Aggregation.Folders.Values;
        reporter.Start(ScanWorkStage.WritingFolders, buffer.Aggregation.Folders.Count);
        WriteFolders(context, scan.Id, previous, members, samples, token, reporter);
        reporter.Start(ScanWorkStage.WritingFolderNames);
        reporter.Start(ScanWorkStage.FlushingRows);
        reporter.Start(ScanWorkStage.ValidatingRelationships);
        var listings = PlanListings(context, previous, members, token);
        reporter.Start(ScanWorkStage.BuildingFolderList, listings.Count);
        WriteListings(context, members, listings, token, reporter);
        context.DirtyGenerations.Add(scan.Id);
    }

    private static int NumberOf(SharedHistoryDbContext context, ScanEntity scan) =>
        (int)context.Entry(scan).Property(SharedHistoryDbContext.GenerationNumber).CurrentValue!;

    private static int PreviousGeneration(SharedHistoryDbContext context, ScanEntity scan)
    {
        var prior = context
            .Published.AsNoTracking()
            .Where(p =>
                p.VolumeKey == scan.VolumeKey
                && p.RangeStart == scan.RangeStart
                && p.RangeCount == scan.RangeCount
            )
            .Join(context.Scans.AsNoTracking(), p => p.ScanId, s => s.Id, (p, s) => s)
            .SingleOrDefault();
        return
            prior is { State: StoredScanState.Completed }
            && prior.Serial == scan.Serial
            && prior.AttributionPolicy == scan.AttributionPolicy
            && prior.CapacityPolicy == scan.CapacityPolicy
            ? context
                .Scans.Where(s => s.Id == prior.Id)
                .Select(s => EF.Property<int>(s, SharedHistoryDbContext.GenerationNumber))
                .Single()
            : 0;
    }

    // Observations ordered by record index with per-record comparison state.
    private sealed class Members
    {
        internal readonly StagedObservation[] Items;
        internal readonly long[] Keys;
        internal readonly byte[] Flags;
        internal readonly int[] Names;
        internal readonly int[] Streams;
        internal readonly int[] Version; // reused state version; 0 stores a new state
        internal readonly int Generation;

        internal Members(
            IReadOnlyList<StagedObservation> observations,
            int generation,
            CancellationToken token
        )
        {
            Items = [.. observations];
            Keys = new long[Items.Length];
            for (int i = 0; i < Items.Length; i++)
                Keys[i] = checked((long)Items[i].RecordIndex);
            Array.Sort(Keys, Items);
            for (int i = 1; i < Keys.Length; i++)
                if (Keys[i] == Keys[i - 1])
                    throw new InvalidDataException("Duplicate observation identity.");
            token.ThrowIfCancellationRequested();
            Flags = new byte[Items.Length];
            Names = new int[Items.Length];
            Streams = new int[Items.Length];
            Version = new int[Items.Length];
            Generation = generation;
        }

        internal int Count => Items.Length;

        internal int Find(long record)
        {
            int found = Array.BinarySearch(Keys, record);
            return found < 0 ? -1 : found;
        }

        internal int VersionOf(int member) => Version[member] == 0 ? Generation : Version[member];
    }

    private static void CompareStates(
        SharedHistoryDbContext context,
        int previous,
        Members members,
        CancellationToken token
    )
    {
        int old = previous;
        ForEach(
            context,
            $"SELECT g.RecordIndex,g.Version,s.Kind,s.Status,s.Sequence,s.IsDirectory,s.Domain,s.Resolution,s.LinksComplete,s.ObservedLinkCount,s.StandardFileAttributes FROM generation_observation g JOIN state_observation s ON s.Version=g.Version AND s.RecordIndex=g.RecordIndex WHERE g.Generation={old}",
            reader =>
            {
                int k = members.Find(reader.GetInt64(0));
                if (k < 0)
                    return;
                if ((members.Flags[k] & ObservationMatched) != 0 || members.Version[k] != 0)
                    throw new InvalidDataException("Previous generation repeats an observation.");
                var item = members.Items[k];
                var file = item.File;
                if (
                    reader.GetInt32(2) == (int)item.Kind
                    && reader.GetInt32(3) == (int)item.Status
                    && Optional<ushort>(reader, 4) == file?.Reference.SequenceNumber
                    && Optional<bool>(reader, 5) == file?.IsDirectory
                    && Optional<int>(reader, 6) == (int?)file?.Domain
                    && Optional<int>(reader, 7) == (int?)file?.Resolution
                    && Optional<bool>(reader, 8) == file?.LinkSetComplete
                    && Optional<ushort>(reader, 9) == file?.ObservedHardLinkCount
                    && Optional<long>(reader, 10) == file?.StandardFileAttributes
                )
                {
                    members.Flags[k] |= ObservationMatched;
                    members.Version[k] = reader.GetInt32(1);
                }
                if ((k & 0xffff) == 0)
                    token.ThrowIfCancellationRequested();
            }
        );
        ForEach(
            context,
            $"SELECT g.RecordIndex,g.Version,n.Ordinal,n.ParentIndex,n.ParentSequence,n.Name,n.Namespace,n.Attributes FROM generation_observation g JOIN state_file_name n ON n.Version=g.Version AND n.RecordIndex=g.RecordIndex WHERE g.Generation={old}",
            reader =>
            {
                int k = members.Find(reader.GetInt64(0));
                if (k < 0)
                    return;
                members.Names[k]++;
                var names = members.Items[k].File?.Names ?? [];
                int ordinal = reader.GetInt32(2);
                if (
                    ordinal >= names.Length
                    || !Same(reader.GetInt32(1), members, k)
                    || reader.GetInt64(3) != checked((long)names[ordinal].Parent.RecordIndex)
                    || reader.GetFieldValue<ushort>(4) != names[ordinal].Parent.SequenceNumber
                    || !SameName(Bytes(reader, 5), names[ordinal].Name)
                    || reader.GetByte(6) != (byte)names[ordinal].Namespace
                    || reader.GetInt64(7) != names[ordinal].FileAttributes
                )
                    members.Flags[k] |= NamesDiffer;
            }
        );
        token.ThrowIfCancellationRequested();
        ForEach(
            context,
            $"SELECT g.RecordIndex,g.Version,s.Ordinal,s.Name,s.Logical,s.Allocated FROM generation_observation g JOIN state_stream s ON s.Version=g.Version AND s.RecordIndex=g.RecordIndex WHERE g.Generation={old}",
            reader =>
            {
                int k = members.Find(reader.GetInt64(0));
                if (k < 0)
                    return;
                members.Streams[k]++;
                var streams = members.Items[k].File?.Streams ?? [];
                int ordinal = reader.GetInt32(2);
                if (
                    ordinal >= streams.Length
                    || !Same(reader.GetInt32(1), members, k)
                    || !SameName(Bytes(reader, 3), streams[ordinal].Name)
                    || reader.GetInt64(4) != streams[ordinal].LogicalBytes
                    || reader.GetInt64(5) != streams[ordinal].AllocatedBytes
                )
                    members.Flags[k] |= StreamsDiffer;
            }
        );
        token.ThrowIfCancellationRequested();
        for (int k = 0; k < members.Count; k++)
        {
            var file = members.Items[k].File;
            if (
                members.Flags[k] != ObservationMatched
                || members.Names[k] != (file?.Names.Length ?? 0)
                || members.Streams[k] != (file?.Streams.Length ?? 0)
            )
                members.Version[k] = 0;
        }
    }

    // Name and stream rows must belong to the version whose observation matched.
    private static bool Same(int version, Members members, int k) =>
        members.Version[k] != 0 && members.Version[k] == version;

    private static void WriteStates(
        SharedHistoryDbContext context,
        Guid id,
        Members members,
        CancellationToken token,
        ScanWorkReporter reporter
    )
    {
        var version = ("Version", members.Generation);
        using var rows = new DuckModelInsert<ObservationEntity>(
            context,
            "state_observation",
            version
        );
        using var names = new DuckModelInsert<FileNameEntity>(context, "state_file_name", version);
        using var streams = new DuckModelInsert<StreamEntity>(context, "state_stream", version);
        using var membership = new DuckModelInsert<GenerationObservationEntity>(context);
        var streamNames = new HashSet<string>(StringComparer.Ordinal);
        for (int k = 0; k < members.Count; k++)
        {
            membership.Add(
                new()
                {
                    Generation = members.Generation,
                    RecordIndex = members.Keys[k],
                    Version = members.VersionOf(k),
                },
                token
            );
            if (members.Version[k] == 0)
            {
                var entity = ScanStore.ToEntity(id, members.Items[k]);
                streamNames.Clear();
                foreach (var stream in entity.Streams)
                    if (!streamNames.Add(stream.Name))
                        throw new InvalidDataException("Duplicate stream name in one file.");
                rows.Add(entity, token);
                foreach (var name in entity.Names)
                    names.Add(name, token);
                foreach (var stream in entity.Streams)
                    streams.Add(stream, token);
            }
            reporter.Advance(k + 1);
        }
    }

    private static void WriteFolders(
        SharedHistoryDbContext context,
        Guid id,
        int previous,
        Members members,
        IEnumerable<FolderCapacitySample> samples,
        CancellationToken token,
        ScanWorkReporter reporter
    )
    {
        int generation = members.Generation;
        var folders = new SortedDictionary<long, (FolderSampleEntity Sample, int Version)>();
        foreach (var sample in samples)
        {
            var entity = ScanStore.ToEntity(id, sample);
            if (!folders.TryAdd(entity.RecordIndex, (entity, generation)))
                throw new InvalidDataException("Duplicate folder sample identity.");
        }
        if (previous != 0)
            foreach (
                var row in (
                    from r in context.SharedFolders.AsNoTracking()
                    where r.Generation == previous
                    join s in context.Samples.AsNoTracking()
                        on new { Version = r.SampleVersion, r.RecordIndex } equals new
                        {
                            s.Version,
                            s.RecordIndex,
                        }
                    select s
                ).AsEnumerable()
            )
            {
                token.ThrowIfCancellationRequested();
                if (
                    folders.TryGetValue(row.RecordIndex, out var current)
                    && current.Version == generation
                    && SampleColumns.SameSample(row, current.Sample)
                )
                    folders[row.RecordIndex] = (current.Sample, row.Version);
            }
        using var states = new DuckModelInsert<FolderSampleEntity>(
            context,
            "state_folder_sample",
            ("Version", generation)
        );
        using var references = new DuckModelInsert<SharedFolderReferenceEntity>(context);
        long completed = 0;
        foreach (var (record, (sample, version)) in folders)
        {
            int k = members.Find(record);
            references.Add(
                new()
                {
                    Generation = generation,
                    RecordIndex = record,
                    Sequence = sample.Sequence,
                    ObservationVersion = k < 0 ? null : members.VersionOf(k),
                    SampleVersion = version,
                },
                token
            );
            if (version == generation)
                states.Add(sample, token);
            reporter.Advance(++completed);
        }
    }

    private readonly record struct ListingKey(long ParentIndex, ushort ParentSequence, long Bucket)
        : IComparable<ListingKey>
    {
        public int CompareTo(ListingKey other) =>
            (ParentIndex, ParentSequence, Bucket).CompareTo(
                (other.ParentIndex, other.ParentSequence, other.Bucket)
            );
    }

    private sealed class Listing
    {
        internal int Entries;
        internal int Matched;
        internal bool Differs;
        internal long? Previous;
        internal long Id;
    }

    private sealed class ListingPlan(
        Dictionary<(long, ushort), int> parents,
        SortedDictionary<ListingKey, Listing> listings
    )
    {
        internal SortedDictionary<ListingKey, Listing> Listings => listings;
        internal int Count => listings.Count;

        // Mirrors the visible listing: ordinary non-metadata records with a known identity,
        // every non-DOS name. Large parents split into fixed MFT-number blocks.
        internal static bool Visible(StagedObservation item) =>
            item.File is { Domain: CapacityDomain.Ordinary } && item.RecordIndex >= 16;

        internal ListingKey Key(long record, NtfsFileName name)
        {
            var parent = (checked((long)name.Parent.RecordIndex), name.Parent.SequenceNumber);
            return new(
                parent.Item1,
                parent.SequenceNumber,
                parents[parent] > LargeListingThreshold ? record / BlockRecordRange + 1 : 0
            );
        }
    }

    private static ListingPlan PlanListings(
        SharedHistoryDbContext context,
        int previous,
        Members members,
        CancellationToken token
    )
    {
        var parents = new Dictionary<(long, ushort), int>();
        for (int k = 0; k < members.Count; k++)
            if (ListingPlan.Visible(members.Items[k]))
                foreach (var name in members.Items[k].File!.Names)
                    if (name.Namespace != FileNameNamespace.Dos)
                    {
                        var parent = (
                            checked((long)name.Parent.RecordIndex),
                            name.Parent.SequenceNumber
                        );
                        parents[parent] = parents.GetValueOrDefault(parent) + 1;
                    }
        var plan = new ListingPlan(parents, []);
        for (int k = 0; k < members.Count; k++)
            if (ListingPlan.Visible(members.Items[k]))
                foreach (var name in members.Items[k].File!.Names)
                    if (name.Namespace != FileNameNamespace.Dos)
                    {
                        var key = plan.Key(members.Keys[k], name);
                        if (!plan.Listings.TryGetValue(key, out var listing))
                            plan.Listings.Add(key, listing = new());
                        listing.Entries++;
                    }
        token.ThrowIfCancellationRequested();
        if (previous == 0)
            return plan;
        // A listing is reused only when every previous entry is present under the same key
        // with the same observation version and the entry counts are equal.
        ForEach(
            context,
            $"SELECT m.ParentIndex,m.ParentSequence,m.Bucket,m.Listing,e.RecordIndex,e.Ordinal,e.ObservationVersion FROM scan_listing_ref m JOIN shared_entry e ON e.Listing=m.Listing WHERE m.Generation={previous}",
            reader =>
            {
                var key = new ListingKey(
                    reader.GetInt64(0),
                    reader.GetFieldValue<ushort>(1),
                    reader.GetInt64(2)
                );
                if (!plan.Listings.TryGetValue(key, out var listing))
                    return;
                long id = reader.GetInt64(3);
                if (listing.Previous is { } known && known != id)
                    throw new InvalidDataException("Previous generation repeats a listing key.");
                listing.Previous = id;
                long record = reader.GetInt64(4);
                int ordinal = reader.GetInt32(5);
                int k = members.Find(record);
                if (
                    k >= 0
                    && ListingPlan.Visible(members.Items[k])
                    && members.Items[k].File!.Names is var names
                    && ordinal < names.Length
                    && names[ordinal].Namespace != FileNameNamespace.Dos
                    && plan.Key(record, names[ordinal]) == key
                    && members.VersionOf(k) == reader.GetInt32(6)
                )
                    listing.Matched++;
                else
                    listing.Differs = true;
            }
        );
        token.ThrowIfCancellationRequested();
        return plan;
    }

    private static void WriteListings(
        SharedHistoryDbContext context,
        Members members,
        ListingPlan plan,
        CancellationToken token,
        ScanWorkReporter reporter
    )
    {
        // New listing numbers are (generation << 32) | ordinal, ascending in key order, so each
        // listing's entries stay contiguous and zone maps skip unrelated row groups.
        var order = new Dictionary<ListingKey, int>();
        long first = (long)members.Generation << 32;
        using (var references = new DuckModelInsert<SharedListingReferenceEntity>(context))
        {
            long completed = 0;
            foreach (var (key, listing) in plan.Listings)
            {
                bool reused =
                    !listing.Differs
                    && listing.Previous is not null
                    && listing.Matched == listing.Entries;
                if (!reused)
                    order.Add(key, order.Count);
                listing.Id = reused ? listing.Previous!.Value : first + order.Count;
                references.Add(
                    new()
                    {
                        Generation = members.Generation,
                        ParentIndex = key.ParentIndex,
                        ParentSequence = key.ParentSequence,
                        Bucket = key.Bucket,
                        Listing = listing.Id,
                    },
                    token
                );
                reporter.Advance(++completed);
            }
        }
        var entries = new List<(int Listing, int Member, int Ordinal)>();
        for (int k = 0; k < members.Count; k++)
        {
            if (!ListingPlan.Visible(members.Items[k]))
                continue;
            var names = members.Items[k].File!.Names;
            for (int ordinal = 0; ordinal < names.Length; ordinal++)
                if (
                    names[ordinal].Namespace != FileNameNamespace.Dos
                    && order.TryGetValue(plan.Key(members.Keys[k], names[ordinal]), out int at)
                )
                    entries.Add((at, k, ordinal));
        }
        entries.Sort();
        token.ThrowIfCancellationRequested();
        var listingIds = new long[order.Count];
        foreach (var (key, at) in order)
            listingIds[at] = plan.Listings[key].Id;
        using var rows = new DuckModelInsert<SharedEntryEntity>(context);
        foreach (var (at, k, ordinal) in entries)
        {
            var file = members.Items[k].File!;
            var unnamed = file.Streams.FirstOrDefault(s => s.Name.Length == 0);
            rows.Add(
                new()
                {
                    Listing = listingIds[at],
                    RecordIndex = members.Keys[k],
                    Ordinal = ordinal,
                    ObservationVersion = members.VersionOf(k),
                    Name = file.Names[ordinal].Name,
                    Sequence = file.Reference.SequenceNumber,
                    IsDirectory = file.IsDirectory,
                    Resolution = file.Resolution,
                    LinksComplete = file.LinkSetComplete,
                    StandardFileAttributes = file.StandardFileAttributes,
                    StreamLogical = unnamed?.LogicalBytes,
                    StreamAllocated = unnamed?.AllocatedBytes,
                },
                token
            );
        }
    }

    // Folder samples compare every stored column except the version they are stored under.
    private static class SampleColumns
    {
        private static readonly (
            Func<StateFolderSampleEntity, object?> Stored,
            Func<FolderSampleEntity, object?> Current
        )[] Columns =
        [
            .. typeof(StateFolderSampleEntity)
                .GetProperties()
                .Where(p => p.Name != nameof(StateFolderSampleEntity.Version))
                .Select(p =>
                {
                    var current =
                        typeof(FolderSampleEntity).GetProperty(p.Name)
                        ?? throw new InvalidOperationException("Folder sample models disagree.");
                    return (
                        (Func<StateFolderSampleEntity, object?>)p.GetValue,
                        (Func<FolderSampleEntity, object?>)current.GetValue
                    );
                }),
        ];

        internal static bool SameSample(StateFolderSampleEntity stored, FolderSampleEntity current)
        {
            foreach (var (left, right) in Columns)
                if (!Equals(left(stored), right(current)))
                    return false;
            return true;
        }
    }

    private static T? Optional<T>(DbDataReader reader, int column)
        where T : struct => reader.IsDBNull(column) ? null : reader.GetFieldValue<T>(column);

    private static byte[] Bytes(DbDataReader reader, int column)
    {
        switch (reader.GetValue(column))
        {
            case byte[] bytes:
                return bytes;
            case Stream stream:
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }
            default:
                throw new InvalidDataException("Stored name is not a blob.");
        }
    }

    private static bool SameName(byte[] stored, string value)
    {
        if (stored.Length != value.Length * 2)
            return false;
        for (int i = 0; i < value.Length; i++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(stored.AsSpan(i * 2)) != value[i])
                return false;
        return true;
    }

    // Streams rows once; DuckDB returns them in vector-sized chunks without materializing the result.
    private static void ForEach(
        SharedHistoryDbContext context,
        string sql,
        Action<DbDataReader> row
    )
    {
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            row(reader);
    }

    private static long[] Scalars(SharedHistoryDbContext context, string sql)
    {
        long[] values = [];
        ForEach(
            context,
            sql,
            reader =>
            {
                values = new long[reader.FieldCount];
                for (int i = 0; i < values.Length; i++)
                    values[i] = Convert.ToInt64(reader.GetValue(i));
            }
        );
        return values;
    }

    internal static void Execute(SharedHistoryDbContext context, string sql)
    {
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static int GenerationOf(SharedHistoryDbContext context, Guid scanId) =>
        context
            .Scans.Where(s => s.Id == scanId)
            .Select(s => EF.Property<int>(s, SharedHistoryDbContext.GenerationNumber))
            .Single();

    // Relationship checks use counts per generation; none builds a hash set of all history.
    internal static void Validate(
        SharedHistoryDbContext context,
        CancellationToken token,
        ScanWorkReporter? reporter
    )
    {
        foreach (
            Guid id in context
                .ChangeTracker.Entries<ScanEntity>()
                .Select(e => e.Entity.Id)
                .Concat(context.DirtyGenerations)
                .Distinct()
                .ToArray()
        )
        {
            token.ThrowIfCancellationRequested();
            int gen = GenerationOf(context, id);
            // Each member is distinct and resolves to exactly one observation state.
            long[] members = Scalars(
                context,
                $"SELECT count(*),count(DISTINCT RecordIndex) FROM generation_observation WHERE Generation={gen}"
            );
            long[] resolved = Scalars(
                context,
                $"SELECT count(*),count(DISTINCT g.RecordIndex) FROM generation_observation g JOIN state_observation s ON s.Version=g.Version AND s.RecordIndex=g.RecordIndex WHERE g.Generation={gen}"
            );
            token.ThrowIfCancellationRequested();
            // Folder references are distinct, resolve to one sample and name a member's state.
            long[] folders = Scalars(
                context,
                $"SELECT count(*),count(DISTINCT RecordIndex),count(*) FILTER (WHERE ObservationVersion IS NOT NULL) FROM scan_folder_ref WHERE Generation={gen}"
            );
            long[] samples = Scalars(
                context,
                $"SELECT count(*),count(DISTINCT r.RecordIndex) FROM scan_folder_ref r JOIN state_folder_sample s ON s.Version=r.SampleVersion AND s.RecordIndex=r.RecordIndex AND s.Sequence=r.Sequence WHERE r.Generation={gen}"
            );
            long[] observed = Scalars(
                context,
                $"SELECT count(*) FROM scan_folder_ref r JOIN generation_observation g ON g.Generation=r.Generation AND g.RecordIndex=r.RecordIndex AND g.Version=r.ObservationVersion WHERE r.Generation={gen}"
            );
            token.ThrowIfCancellationRequested();
            // Listing keys are distinct and every referenced listing has entries.
            long[] listings = Scalars(
                context,
                $"SELECT count(*),count(DISTINCT (ParentIndex,ParentSequence,Bucket)),count(*) FILTER (WHERE NOT EXISTS(SELECT 1 FROM shared_entry e WHERE e.Listing=r.Listing)) FROM scan_listing_ref r WHERE r.Generation={gen}"
            );
            if (
                members[1] != members[0]
                || resolved[0] != members[0]
                || resolved[1] != members[0]
                || folders[1] != folders[0]
                || samples[0] != folders[0]
                || samples[1] != folders[0]
                || observed[0] != folders[2]
                || listings[1] != listings[0]
                || listings[2] != 0
            )
                throw new InvalidDataException(
                    "Incomplete or ambiguous shared history relationships."
                );
        }
    }

    /// <summary>Deletes one generation and only the shared states it alone referenced.
    /// Candidates come from the deleted generation, so the cost does not grow with all history.</summary>
    internal static string DeleteGenerationSql(int generation) =>
        $"""
            DROP TABLE IF EXISTS doomed_state; DROP TABLE IF EXISTS doomed_sample; DROP TABLE IF EXISTS doomed_listing;
            CREATE TEMP TABLE doomed_state AS SELECT DISTINCT Version,RecordIndex FROM generation_observation WHERE Generation={generation};
            CREATE TEMP TABLE doomed_sample AS SELECT DISTINCT SampleVersion,RecordIndex FROM scan_folder_ref WHERE Generation={generation};
            CREATE TEMP TABLE doomed_listing AS SELECT DISTINCT Listing FROM scan_listing_ref WHERE Generation={generation};
            DELETE FROM generation_observation WHERE Generation={generation};
            DELETE FROM scan_folder_ref WHERE Generation={generation};
            DELETE FROM scan_listing_ref WHERE Generation={generation};
            DELETE FROM doomed_state d WHERE EXISTS(SELECT 1 FROM generation_observation g WHERE g.Version=d.Version AND g.RecordIndex=d.RecordIndex);
            DELETE FROM doomed_sample d WHERE EXISTS(SELECT 1 FROM scan_folder_ref r WHERE r.SampleVersion=d.SampleVersion AND r.RecordIndex=d.RecordIndex);
            DELETE FROM doomed_listing d WHERE EXISTS(SELECT 1 FROM scan_listing_ref r WHERE r.Listing=d.Listing);
            DELETE FROM state_observation s USING doomed_state d WHERE s.Version=d.Version AND s.RecordIndex=d.RecordIndex;
            DELETE FROM state_file_name s USING doomed_state d WHERE s.Version=d.Version AND s.RecordIndex=d.RecordIndex;
            DELETE FROM state_stream s USING doomed_state d WHERE s.Version=d.Version AND s.RecordIndex=d.RecordIndex;
            DELETE FROM state_folder_sample s USING doomed_sample d WHERE s.Version=d.SampleVersion AND s.RecordIndex=d.RecordIndex;
            DELETE FROM shared_entry e USING doomed_listing d WHERE e.Listing=d.Listing;
            DROP TABLE doomed_state; DROP TABLE doomed_sample; DROP TABLE doomed_listing;
            """;
}
