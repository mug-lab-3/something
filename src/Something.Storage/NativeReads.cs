using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

// DuckDB whole-generation reads. EF materialization took about half of a two-million-file read
// (3.1 s total, 1.5 s in the engine), so these read typed columns directly from ordered streams.
// Values are validated exactly as the entity path validates them; SQLite keeps the EF queries.
internal sealed partial class ScanStore
{
    private sealed class NativeRows : IDisposable
    {
        private readonly DbCommand command;
        internal DbDataReader Reader { get; }
        internal bool HasRow { get; private set; }

        // Streaming keeps a large result out of the engine's memory; the connection then runs
        // no other command until the reader is disposed.
        internal NativeRows(ScanDbContext context, string sql, bool streaming = false)
        {
            command = context.Database.GetDbConnection().CreateCommand();
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = sql;
            if (streaming)
                ((DuckDB.NET.Data.DuckDBCommand)command).UseStreamingMode = true;
            Reader = command.ExecuteReader();
            HasRow = Reader.Read();
        }

        internal long Record => Reader.GetInt64(0);

        internal void Next() => HasRow = Reader.Read();

        public void Dispose()
        {
            Reader.Dispose();
            command.Dispose();
        }
    }

    private static string NativeId(Guid id) => $"'{id:D}'::UUID";

    private IEnumerable<FileCapacityObservation> ReadFilesNative(
        Guid scanId,
        CancellationToken token
    )
    {
        string id = NativeId(scanId);
        using var names = new NativeRows(
            context,
            $"SELECT RecordIndex,ParentIndex,ParentSequence,Name,Namespace,Attributes FROM file_name WHERE ScanId={id} ORDER BY RecordIndex,Ordinal"
        );
        using var streams = new NativeRows(
            context,
            $"SELECT RecordIndex,Name,Logical,Allocated FROM stream WHERE ScanId={id} ORDER BY RecordIndex,Ordinal"
        );
        using var files = new NativeRows(
            context,
            $"SELECT RecordIndex,Sequence,IsDirectory,Domain,Resolution,LinksComplete,ObservedLinkCount,StandardFileAttributes FROM observation WHERE ScanId={id} AND Sequence IS NOT NULL ORDER BY RecordIndex"
        );
        var fileNames = ImmutableArray.CreateBuilder<NtfsFileName>();
        var fileStreams = ImmutableArray.CreateBuilder<StreamCapacity>();
        for (; files.HasRow; files.Next())
        {
            token.ThrowIfCancellationRequested();
            var row = files.Reader;
            long record = row.GetInt64(0);
            while (names.HasRow && names.Record < record)
                names.Next();
            for (; names.HasRow && names.Record == record; names.Next())
            {
                var name = names.Reader;
                fileNames.Add(
                    new(
                        FileReference.Create(
                            checked((ulong)name.GetInt64(1)),
                            name.GetFieldValue<ushort>(2)
                        ),
                        NativeName(name, 3),
                        (FileNameNamespace)name.GetByte(4),
                        checked((uint)name.GetInt64(5))
                    )
                );
            }
            while (streams.HasRow && streams.Record < record)
                streams.Next();
            for (; streams.HasRow && streams.Record == record; streams.Next())
            {
                var stream = streams.Reader;
                fileStreams.Add(new(NativeName(stream, 1), stream.GetInt64(2), stream.GetInt64(3)));
            }
            if (row.IsDBNull(2) || row.IsDBNull(3) || row.IsDBNull(4) || row.IsDBNull(5))
                throw new InvalidDataException("Stored file state or capacity is invalid.");
            var domain = (CapacityDomain)row.GetInt32(3);
            var resolution = (CapacityResolution)row.GetInt32(4);
            if (
                !Enum.IsDefined(domain)
                || !Enum.IsDefined(resolution)
                || (resolution == CapacityResolution.Unresolved && fileStreams.Count != 0)
            )
                throw new InvalidDataException("Stored file state or capacity is invalid.");
            yield return new(
                FileReference.Create(checked((ulong)record), row.GetFieldValue<ushort>(1)),
                row.GetBoolean(2),
                domain,
                resolution,
                fileNames.DrainToImmutable(),
                fileStreams.DrainToImmutable(),
                row.GetBoolean(5)
            )
            {
                ObservedHardLinkCount = row.IsDBNull(6) ? null : row.GetFieldValue<ushort>(6),
                StandardFileAttributes = row.IsDBNull(7) ? null : checked((uint)row.GetInt64(7)),
            };
        }
    }

    private IEnumerable<FolderVisibilityObservation> ReadVisibilityFoldersNative(
        Guid scanId,
        CancellationToken token
    )
    {
        string id = NativeId(scanId);
        string directories =
            $"SELECT RecordIndex,Sequence,StandardFileAttributes FROM observation WHERE ScanId={id} AND IsDirectory AND Sequence IS NOT NULL";
        using var names = new NativeRows(
            context,
            $"SELECT n.RecordIndex,n.ParentIndex,n.ParentSequence FROM file_name n SEMI JOIN ({directories}) d ON d.RecordIndex=n.RecordIndex WHERE n.ScanId={id} AND n.Namespace<>{(int)FileNameNamespace.Dos} ORDER BY n.RecordIndex,n.Ordinal"
        );
        using var folders = new NativeRows(context, directories + " ORDER BY RecordIndex");
        var parents = ImmutableArray.CreateBuilder<FileReference>();
        for (; folders.HasRow; folders.Next())
        {
            token.ThrowIfCancellationRequested();
            var row = folders.Reader;
            long record = row.GetInt64(0);
            while (names.HasRow && names.Record < record)
                names.Next();
            for (; names.HasRow && names.Record == record; names.Next())
            {
                var parent = FileReference.Create(
                    checked((ulong)names.Reader.GetInt64(1)),
                    names.Reader.GetFieldValue<ushort>(2)
                );
                if (!parents.Contains(parent))
                    parents.Add(parent);
            }
            yield return new(
                FileReference.Create(checked((ulong)record), row.GetFieldValue<ushort>(1)),
                parents.DrainToImmutable(),
                row.IsDBNull(2) ? null : checked((uint)row.GetInt64(2))
            );
        }
    }

    private static readonly string[] SampleCapacities =
    [
        nameof(FolderSampleEntity.DirectLogical),
        nameof(FolderSampleEntity.DirectAllocated),
        nameof(FolderSampleEntity.SharedLogical),
        nameof(FolderSampleEntity.SharedAllocated),
        nameof(FolderSampleEntity.DirectoryLogical),
        nameof(FolderSampleEntity.DirectoryAllocated),
        nameof(FolderSampleEntity.SubtreeLogical),
        nameof(FolderSampleEntity.SubtreeAllocated),
        nameof(FolderSampleEntity.EntryDirectLogical),
        nameof(FolderSampleEntity.EntryDirectAllocated),
        nameof(FolderSampleEntity.EntrySubtreeLogical),
        nameof(FolderSampleEntity.EntrySubtreeAllocated),
    ];

    // Each value read through the provider costs about 30 ns, so a 200,000-folder read is bound by
    // the number of values. Capacities stay UHUGEINT; they are read as one UBIGINT (low half) plus
    // a per-row flag, and high halves are read only for the rare rows that exceed 64 bits.
    private IEnumerable<FolderSampleEntity> ReadSamplesNative(Guid scanId, CancellationToken token)
    {
        string low = string.Join(
            ',',
            SampleCapacities.Select(c => $"({c}&18446744073709551615::UHUGEINT)::UBIGINT")
        );
        string high = string.Join(',', SampleCapacities.Select(c => $"({c}>>64)::UBIGINT"));
        string wide = string.Join(" OR ", SampleCapacities.Select(c => $"{c}>>64<>0"));
        using var rows = new NativeRows(
            context,
            $"SELECT RecordIndex,Sequence,AssignedCount,SubtreeCount,DirectUnresolved,SubtreeUnresolved,DirectoryObserved,UnobservedDirectories,EntryDirectFiles,EntrySubtreeFiles,EntryDirectUnresolved,EntrySubtreeUnresolved,coalesce({wide},false),{low},{high} FROM folder_sample WHERE ScanId={NativeId(scanId)}"
        );
        const int Low = 13,
            High = Low + 12;
        var values = new UInt128?[SampleCapacities.Length];
        for (; rows.HasRow; rows.Next())
        {
            token.ThrowIfCancellationRequested();
            var row = rows.Reader;
            bool exceeds = row.GetBoolean(12);
            for (int i = 0; i < values.Length; i++)
                values[i] =
                    row.IsDBNull(Low + i) ? null
                    : exceeds
                        ? ((UInt128)row.GetFieldValue<ulong>(High + i) << 64)
                            | row.GetFieldValue<ulong>(Low + i)
                    : row.GetFieldValue<ulong>(Low + i);
            yield return new()
            {
                ScanId = scanId,
                RecordIndex = row.GetInt64(0),
                Sequence = row.GetFieldValue<ushort>(1),
                AssignedCount = row.GetInt64(2),
                SubtreeCount = row.GetInt64(3),
                DirectUnresolved = row.GetInt64(4),
                SubtreeUnresolved = row.GetInt64(5),
                DirectoryObserved = row.GetBoolean(6),
                UnobservedDirectories = row.GetInt64(7),
                EntryDirectFiles = row.IsDBNull(8) ? null : row.GetInt64(8),
                EntrySubtreeFiles = row.IsDBNull(9) ? null : row.GetInt64(9),
                EntryDirectUnresolved = row.IsDBNull(10) ? null : row.GetInt64(10),
                EntrySubtreeUnresolved = row.IsDBNull(11) ? null : row.GetInt64(11),
                DirectLogical = Required(values[0]),
                DirectAllocated = Required(values[1]),
                SharedLogical = Required(values[2]),
                SharedAllocated = Required(values[3]),
                DirectoryLogical = Required(values[4]),
                DirectoryAllocated = Required(values[5]),
                SubtreeLogical = Required(values[6]),
                SubtreeAllocated = Required(values[7]),
                EntryDirectLogical = values[8],
                EntryDirectAllocated = values[9],
                EntrySubtreeLogical = values[10],
                EntrySubtreeAllocated = values[11],
            };
        }

        static UInt128 Required(UInt128? value) =>
            value ?? throw new InvalidDataException("Missing folder capacity.");
    }

    private static string NativeName(DbDataReader reader, int column) =>
        reader.GetValue(column) switch
        {
            byte[] bytes => StorageEncoding.Name(bytes),
            Stream stream => StorageEncoding.Name(ReadAll(stream)),
            _ => throw new InvalidDataException("Stored name is not a blob."),
        };

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
