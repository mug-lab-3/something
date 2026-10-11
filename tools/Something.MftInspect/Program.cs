using Something.Ntfs;

if (args.Length is < 1 or > 2 || (args.Length == 2 && !int.TryParse(args[1], out _)))
{
    Console.Error.WriteLine("Usage: Something.MftInspect <raw-$MFT-dump> [record-size-in-bytes]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
try
{
    int size = args.Length == 2 ? int.Parse(args[1]) : 1024;
    var reader = new MftRecordReader(size);
    await using var stream = new FileStream(
        args[0],
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1024 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan
    );
    long active = 0,
        deleted = 0,
        empty = 0,
        invalid = 0,
        extensions = 0;
    await foreach (var result in reader.ReadAsync(stream, cancellationToken: cancellation.Token))
    {
        switch (result.Status)
        {
            case MftRecordReadStatus.Uninitialized:
                empty++;
                break;
            case MftRecordReadStatus.Invalid:
                invalid++;
                if (invalid <= 20)
                    Console.Error.WriteLine(
                        $"Record {result.RecordIndex}: {result.Error!.Message}"
                    );
                break;
            case MftRecordReadStatus.Parsed:
                if (result.Record!.IsInUse)
                    active++;
                else
                    deleted++;
                if (result.Record.IsInUse && result.Record.IsExtension)
                    extensions++;
                break;
        }
    }
    Console.WriteLine(
        $"Active={active:N0}; deleted={deleted:N0}; uninitialized={empty:N0}; invalid={invalid:N0}; extensions={extensions:N0}"
    );
    return invalid == 0 ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception error)
    when (error is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
