using System.ComponentModel;
using System.Security.Principal;
using Something.Ntfs;
using Something.Windows;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This diagnostic requires Windows and a local NTFS volume.");
    return 2;
}
if (
    args.Length is < 1 or > 3
    || (args.Length > 1 && args[1] != "--file" && !ulong.TryParse(args[1], out _))
    || (args.Length > 1 && args[1] == "--file" && args.Length != 3)
    || (args.Length > 2 && args[1] != "--file" && !int.TryParse(args[2], out _))
)
{
    Console.Error.WriteLine(
        "Usage: Something.LiveInspect <drive, e.g. C:> [start-index=0] [count=16, max=4096]"
    );
    Console.Error.WriteLine("   or: Something.LiveInspect <drive> --file <absolute-file-path>");
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
    if (args.Length == 3 && args[1] == "--file")
    {
        using var file = WindowsFileMetadata.Open(args[0], args[2]);
        var before = file.Observe(cancellation.Token);
        if (before.IsDirectory)
            throw new ArgumentException("File comparison requires a regular file.");
        using var volume = WindowsMftRecordSource.Open(args[0]);
        if (before.VolumeSerialNumber != (uint)(volume.Volume.SerialNumber & uint.MaxValue))
            throw new InvalidDataException("File and selected volume identifiers differ.");
        var result = await new LiveMftFileReader(volume.Volume.BytesPerCluster).ReadAsync(
            volume,
            before.Reference.RecordIndex,
            before.Reference,
            cancellation.Token
        );
        if (result.Status != LiveFileReadStatus.ObservedUnchanged || result.File is null)
        {
            Console.WriteLine(
                $"File comparison unresolved: {result.Status}; record={result.UnresolvedRecordIndex}; attempts={result.Attempts}."
            );
            return 1;
        }
        var assembled = result.File;
        var after = file.Observe(cancellation.Token);
        if (before != after)
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
        bool match = stream.LogicalBytes == after.LogicalBytes;
        Console.WriteLine(
            $"Record {before.Reference.RecordIndex}: NTFS-logical={stream.LogicalBytes}; Windows-logical={after.LogicalBytes}; DATA-allocated={stream.AllocatedBytes}; match={match}"
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
        $"Sector={source.Volume.BytesPerSector}; cluster={source.Volume.BytesPerCluster}; record={source.Volume.BytesPerRecord}; observed-slots={source.Volume.ObservedRecordCount}"
    );
    var reader = new LiveMftRecordReader();
    var fileReader = new LiveMftFileReader(source.Volume.BytesPerCluster);
    int unresolved = 0;
    for (int n = 0; n < count; n++)
    {
        ulong index = start + (ulong)n;
        var result = await reader.ReadAsync(source, index, cancellationToken: cancellation.Token);
        if (result.Record is not { } record)
        {
            Console.WriteLine($"Record {index}: {result.Status}; attempts={result.Attempts}");
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
