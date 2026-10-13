using System.ComponentModel;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Storage;
using Something.Windows;

if (args.Length >= 1 && args[0] == "--storage-benchmark")
{
    int count = 100_000;
    if (
        (args.Length >= 2 && (!int.TryParse(args[1], out count) || count is < 1 or > 2_000_000))
        || args.Length > 4
        || args.Skip(2).Any(a => a is not ("--buffered" or "--memory-db"))
        || args.Skip(2).Distinct(StringComparer.Ordinal).Count() != Math.Max(0, args.Length - 2)
    )
    {
        Console.Error.WriteLine(
            "Usage: --storage-benchmark [count=100000, max=2000000] [--buffered] [--memory-db]"
        );
        return 2;
    }
    try
    {
        return StorageBenchmark.Run(
            count,
            args.Contains("--buffered"),
            args.Contains("--memory-db")
        );
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Storage benchmark cancelled.");
        return 130;
    }
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This diagnostic requires Windows and a local NTFS volume.");
    return 2;
}

// Work-budget overrides are explicit, bounded, and trailing.
int maximumRecords = LiveMftFileReader.DefaultMaximumRecords;
bool invalidRecordLimit = false;
if (args.Length >= 3 && args[^2] == "--max-records")
{
    invalidRecordLimit = !int.TryParse(args[^1], out maximumRecords) || maximumRecords < 1;
    args = args[..^2];
}
var rankingFilter = new FolderRankingFilter();
bool invalidRankingFilter = false;
var databasePreference = DiagnosticDatabasePreference.Automatic;
if (args.Length >= 2 && args[1] == "--top5")
{
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--leaf" && !rankingFilter.LeafOnly)
            rankingFilter = rankingFilter with { LeafOnly = true };
        else if (
            args[i] is "--memory-db" or "--file-db"
            && databasePreference == DiagnosticDatabasePreference.Automatic
        )
            databasePreference =
                args[i] == "--memory-db"
                    ? DiagnosticDatabasePreference.Memory
                    : DiagnosticDatabasePreference.File;
        else if (
            args[i] == "--depth"
            && rankingFilter.Depth is null
            && i + 1 < args.Length
            && int.TryParse(args[++i], out int depth)
            && depth >= 1
        )
            rankingFilter = rankingFilter with { Depth = depth };
        else
        {
            invalidRankingFilter = true;
            break;
        }
    }
    args = args[..2];
}
if (
    invalidRecordLimit
    || invalidRankingFilter
    || args.Length is < 1 or > 3
    || (
        args.Length > 1
        && args[1] is not ("--file" or "--scan" or "--record-file" or "--top5")
        && !ulong.TryParse(args[1], out _)
    )
    || (args.Length > 1 && args[1] is "--file" or "--record-file" && args.Length != 3)
    || (
        args.Length > 2
        && args[1] is not ("--file" or "--scan" or "--record-file" or "--top5")
        && !int.TryParse(args[2], out _)
    )
    || (
        args.Length > 2
        && args[1] == "--record-file"
        && (
            !ulong.TryParse(args[2], out ulong recordIndex)
            || recordIndex > FileReference.MaximumRecordIndex
        )
    )
    || (args.Length > 2 && args[1] == "--scan" && !ulong.TryParse(args[2], out _))
    || (args.Length > 2 && args[1] == "--top5")
)
{
    Console.Error.WriteLine(
        "Usage: Something.LiveInspect <drive, e.g. C:> [start-index=0] [count=16, max=4096] [--max-records <positive-int>]"
    );
    Console.Error.WriteLine(
        "   or: Something.LiveInspect <drive> --file <absolute-file-path> [--max-records <positive-int>]"
    );
    Console.Error.WriteLine(
        "   or: Something.LiveInspect <drive> --scan [max-slots] [--max-records <positive-int>]"
    );
    Console.Error.WriteLine(
        "   or: Something.LiveInspect <drive> --top5 [--depth <positive-int>] [--leaf] [--memory-db | --file-db] [--max-records <positive-int>]"
    );
    Console.Error.WriteLine(
        $"Per-file record limit defaults to {LiveMftFileReader.DefaultMaximumRecords}."
    );
    Console.Error.WriteLine(
        "   or: Something.LiveInspect <drive> --record-file <record-index> [--max-records <positive-int>]"
    );
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
try
{
    if (args.Length == 2 && args[1] == "--top5")
    {
        using var volume = WindowsMftRecordSource.Open(args[0]);
        return await TopFolders.RunAsync(
            volume,
            args[0],
            maximumRecords,
            cancellation.Token,
            rankingFilter,
            databasePreference
        );
    }
    if (args.Length == 3 && args[1] == "--record-file")
        return await RecordFileComparison.RunAsync(
            args[0],
            ulong.Parse(args[2]),
            maximumRecords,
            cancellation.Token
        );
    if (args.Length >= 2 && args[1] == "--scan")
    {
        using var volume = WindowsMftRecordSource.Open(args[0]);
        ulong slotLimit =
            args.Length == 3 ? ulong.Parse(args[2]) : volume.Volume.ObservedRecordCount;
        if (slotLimit == 0 || slotLimit > volume.Volume.ObservedRecordCount)
            throw new ArgumentException(
                "Scan limit must be between 1 and the observed slot count."
            );
        return await ScanBenchmark.RunAsync(
            volume,
            new(0, slotLimit),
            maximumRecords,
            cancellation.Token
        );
    }
    if (args.Length == 3 && args[1] == "--file")
    {
        using var file = WindowsFileMetadata.Open(args[0], args[2]);
        var before = file.Observe(cancellation.Token);
        if (before.IsDirectory)
            throw new ArgumentException("File comparison requires a regular file.");
        var storageBefore = file.ObserveStorage(cancellation.Token);
        using var volume = WindowsMftRecordSource.Open(args[0]);
        if (before.VolumeSerialNumber != (uint)(volume.Volume.SerialNumber & uint.MaxValue))
            throw new InvalidDataException("File and selected volume identifiers differ.");
        var result = await new LiveMftFileReader(
            volume.Volume.BytesPerCluster,
            maximumRecords: maximumRecords,
            totalClusters: volume.Volume.TotalClusters
        ).ReadAsync(volume, before.Reference.RecordIndex, before.Reference, cancellation.Token);
        if (result.Status != LiveFileReadStatus.ObservedUnchanged || result.File is null)
        {
            Console.WriteLine(
                $"File comparison unresolved: {result.Status}; record={result.UnresolvedRecordIndex}; attempts={result.Attempts}."
            );
            if (result.Error is { } reason)
                Console.WriteLine($"  Reason: {reason}");
            return 1;
        }
        var assembled = result.File;
        var storageAfter = file.ObserveStorage(cancellation.Token);
        var after = file.Observe(cancellation.Token);
        if (
            before != after
            || storageBefore != storageAfter
            || storageBefore.Standard.LogicalBytes != before.LogicalBytes
            || storageAfter.Standard.LogicalBytes != after.LogicalBytes
            || storageBefore.FileAttributes != before.FileAttributes
            || storageAfter.FileAttributes != after.FileAttributes
            || storageBefore.Standard.DeletePending
            || storageAfter.Standard.DeletePending
            || storageBefore.Standard.IsDirectory
            || storageAfter.Standard.IsDirectory
        )
        {
            Console.WriteLine("File comparison unresolved: metadata changed during observation.");
            return 1;
        }
        var stream = assembled.DataStreams.SingleOrDefault(s => s.Name.Length == 0);
        if (stream is null)
        {
            Console.WriteLine("File comparison unresolved: unnamed DATA stream was not resolved.");
            return 1;
        }
        bool logicalMatch = stream.LogicalBytes == after.LogicalBytes;
        bool allocatedMatch = stream.AllocatedBytes == storageAfter.AllocatedBytes;
        bool match = logicalMatch && allocatedMatch;
        Console.WriteLine(
            $"Record {before.Reference.RecordIndex}: NTFS-logical={stream.LogicalBytes}; Windows-logical={after.LogicalBytes}; logical-match={logicalMatch}; DATA-allocated={stream.AllocatedBytes}; Windows-allocated={storageAfter.AllocatedBytes}; allocation-source={(storageAfter.UsesCompressionInfo ? "FileCompressionInfo" : "FileStandardInfo")}; allocated-match={allocatedMatch}; match={match}"
        );
        Console.WriteLine("Optimistic size comparison; a mismatch may reflect concurrent changes.");
        return match ? 0 : 1;
    }
    ulong start = args.Length > 1 ? ulong.Parse(args[1]) : 0;
    int count = args.Length > 2 ? int.Parse(args[2]) : 16;
    if (
        count is < 1 or > 4096
        || start > FileReference.MaximumRecordIndex
        || (ulong)(count - 1) > FileReference.MaximumRecordIndex - start
    )
        throw new ArgumentException("Requested range is invalid or exceeds the diagnostic limit.");
    using var source = WindowsMftRecordSource.Open(args[0]);
    Console.WriteLine(
        $"Sector={source.Volume.BytesPerSector}; cluster={source.Volume.BytesPerCluster}; record={source.Volume.BytesPerRecord}; observed-slots={source.Volume.ObservedRecordCount}; maximum-records={maximumRecords}"
    );
    var reader = new LiveMftRecordReader();
    var fileReader = new LiveMftFileReader(
        source.Volume.BytesPerCluster,
        maximumRecords: maximumRecords,
        totalClusters: source.Volume.TotalClusters
    );
    int unresolved = 0;
    for (int n = 0; n < count; n++)
    {
        ulong index = start + (ulong)n;
        var result = await reader.ReadAsync(source, index, cancellationToken: cancellation.Token);
        if (result.Record is not { } record)
        {
            Console.WriteLine($"Record {index}: {result.Status}; attempts={result.Attempts}");
            if (result.Error is { } error)
                Console.WriteLine($"  Reason: {error.Message}");
            if (result.Status == LiveRecordReadStatus.Invalid)
                await RecordDiagnostics.PrintAsync(source, index, cancellation.Token);
            if (result.Status != LiveRecordReadStatus.Missing)
                unresolved++;
            continue;
        }
        // No filenames, dump, scan history, or partial capacity guesses are written.
        Console.WriteLine(
            $"Record {index}: {result.Status}; sequence={record.Reference.SequenceNumber}; directory={record.IsDirectory}; extension={record.IsExtension}; attributes={record.Attributes.Length}"
        );
        if (record.IsInUse && !record.IsExtension)
        {
            var fileResult = await fileReader.ReadAsync(
                source,
                index,
                record.Reference,
                cancellation.Token
            );
            if (fileResult.File is { } file)
            {
                foreach (var stream in file.DataStreams)
                    Console.WriteLine(
                        $"  DATA: named={stream.Name.Length != 0}; logical={stream.LogicalBytes}; allocated={stream.AllocatedBytes}"
                    );
            }
            else
            {
                Console.WriteLine(
                    $"  DATA capacity unresolved: {fileResult.Status}; record={fileResult.UnresolvedRecordIndex}; attempts={fileResult.Attempts}."
                );
                if (fileResult.Error is { } reason)
                    Console.WriteLine($"  Reason: {reason}");
                if (
                    fileResult.Status
                    is LiveFileReadStatus.InvalidRecord
                        or LiveFileReadStatus.InvalidAssembly
                )
                    await RecordDiagnostics.PrintAsync(
                        source,
                        fileResult.UnresolvedRecordIndex ?? index,
                        cancellation.Token
                    );
                unresolved++;
            }
        }
    }
    Console.WriteLine(
        "Optimistic metadata observations only; no point-in-time snapshot or complete disk usage estimate."
    );
    return unresolved == 0 ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception error) when (error is DbUpdateException or SqliteException)
{
    Console.Error.WriteLine(
        $"Diagnostic database operation failed: {error.InnerException?.Message ?? error.Message}"
    );
    return 2;
}
catch (Exception error)
    when (error is IOException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(error.Message);
    if (
        OperatingSystem.IsWindows() && error.InnerException is Win32Exception { NativeErrorCode: 5 }
    )
    {
        using var identity = WindowsIdentity.GetCurrent();
        bool administrator = new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator
        );
        Console.Error.WriteLine($"Current process has administrator privileges: {administrator}.");
        Console.Error.WriteLine(
            administrator
                ? "Access was denied despite administrator privileges; investigate volume access restrictions."
                : "Open PowerShell using Run as administrator, then rerun this command from the repository directory."
        );
        Console.Error.WriteLine(
            "Volume access requests read permission only; no write permission is requested."
        );
    }
    return 2;
}
