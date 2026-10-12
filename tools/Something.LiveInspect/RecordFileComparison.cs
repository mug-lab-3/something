using System.Runtime.Versioning;
using Something.Ntfs;
using Something.Windows;

[SupportedOSPlatform("windows")]
internal static class RecordFileComparison
{
    public static async Task<int> RunAsync(
        string drive,
        ulong index,
        int maximumRecords,
        CancellationToken token
    )
    {
        using var source = WindowsMftRecordSource.Open(drive);
        var observed = await new LiveMftRecordReader().ReadAsync(
            source,
            index,
            cancellationToken: token
        );
        if (observed.Record is not { IsInUse: true, IsExtension: false, IsDirectory: false } record)
        {
            Console.WriteLine(
                $"Record file comparison unresolved: record={index}; status={observed.Status}; regular-base-file-required=True."
            );
            return 1;
        }
        using var file = WindowsFileMetadata.OpenByReference(drive, record.Reference, token);
        var before = file.Observe(token);
        var storageBefore = file.ObserveStorage(token);
        var result = await new LiveMftFileReader(
            source.Volume.BytesPerCluster,
            maximumRecords: maximumRecords,
            totalClusters: source.Volume.TotalClusters
        ).ReadAsync(source, index, record.Reference, token);
        var storageAfter = file.ObserveStorage(token);
        var after = file.Observe(token);
        if (
            !WindowsFileObservationConsistency.IsStable(
                record.Reference,
                (uint)(source.Volume.SerialNumber & uint.MaxValue),
                before,
                storageBefore,
                after,
                storageAfter
            )
        )
        {
            Console.WriteLine(
                "Record file comparison unresolved: identity or metadata changed or differs from the selected volume."
            );
            return 1;
        }
        Console.WriteLine(
            $"Record {index}: sequence={after.Reference.SequenceNumber}; Windows-logical={after.LogicalBytes}; Windows-allocated={storageAfter.AllocatedBytes}; allocation-source={(storageAfter.UsesCompressionInfo ? "FileCompressionInfo" : "FileStandardInfo")}; compressed={(after.FileAttributes & 0x800u) != 0}; sparse={(after.FileAttributes & 0x200u) != 0}; compression-format={storageAfter.Compression.CompressionFormat}"
        );
        if (result.File is not { } resolved)
        {
            Console.WriteLine(
                $"NTFS comparison unresolved: {result.Status}; attempts={result.Attempts}; reason={result.Error ?? "not available"}"
            );
            Console.WriteLine(
                "Windows observations only; NTFS capacity is unresolved and has not been independently verified."
            );
            return 1;
        }
        var stream = resolved.DataStreams.SingleOrDefault(s => s.Name.Length == 0);
        if (stream is null)
        {
            Console.WriteLine("NTFS comparison unresolved: unnamed DATA stream was not resolved.");
            return 1;
        }
        bool logicalMatch = stream.LogicalBytes == after.LogicalBytes;
        bool allocatedMatch = stream.AllocatedBytes == storageAfter.AllocatedBytes;
        Console.WriteLine(
            $"NTFS-logical={stream.LogicalBytes}; DATA-allocated={stream.AllocatedBytes}; logical-match={logicalMatch}; allocated-match={allocatedMatch}; match={logicalMatch && allocatedMatch}"
        );
        Console.WriteLine(
            "Optimistic unnamed-stream comparison; no point-in-time snapshot guarantee."
        );
        return logicalMatch && allocatedMatch ? 0 : 1;
    }
}
