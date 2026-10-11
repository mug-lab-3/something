using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using Something.Ntfs;

// Developer-only corpus check. DiscUtils extracts the raw stream independently
// of Something's parser. No volume mounting, elevated handles or writes to source.
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This DiscUtils corpus tool requires Windows.");
    return 2;
}
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Something.CorpusCheck <NIST-dd.bz2> <output-directory>");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
var token = cancellation.Token;
try
{
    string source = Path.GetFullPath(args[0]);
    string output = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(output);
    // CreateNew prevents reuse of a partially extracted or stale previous result.
    string imagePath = Path.Combine(output, "image.dd");
    await using (var input = File.OpenRead(source))
    await using (var imageOutput = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write))
    using (
        var decompressor = BZip2Stream.Create(
            input,
            CompressionMode.Decompress,
            decompressConcatenated: true
        )
    )
    {
        byte[] buffer = new byte[1024 * 1024];
        long total = 0;
        int count;
        while ((count = await decompressor.ReadAsync(buffer, token)) != 0)
        {
            total = checked(total + count);
            if (total > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("NIST image exceeds the 4 GiB tooling limit.");
            await imageOutput.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    await using var image = File.OpenRead(imagePath);
    var partitions = new BiosPartitionTable(image, BiosPartitionTable.DetectGeometry(image));
    var partition = partitions.Partitions.Single(p => p.BiosType == 7);
    using var volume = partition.Open();
    byte[] boot = new byte[512];
    volume.ReadExactly(boot);
    int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
    int bytesPerCluster = checked(bytesPerSector * boot[13]);
    int encoding = unchecked((sbyte)boot[64]);
    if (encoding is < -16 or > 127 || encoding == 0)
        throw new InvalidDataException("Unsupported boot record size encoding.");
    int recordSize = encoding < 0 ? 1 << -encoding : checked(encoding * bytesPerCluster);
    volume.Position = 0;
    using var ntfs = new NtfsFileSystem(volume);
    string mftPath = Path.Combine(output, "mft.raw");
    using (var mft = ntfs.OpenFile("$MFT", FileMode.Open, FileAccess.Read))
    await using (var raw = new FileStream(mftPath, FileMode.CreateNew, FileAccess.Write))
        await mft.CopyToAsync(raw, token);

    long active = 0,
        inactive = 0,
        empty = 0,
        invalid = 0;
    var errors = new List<object>();
    await using var records = File.OpenRead(mftPath);
    await foreach (
        var result in new MftRecordReader(recordSize).ReadAsync(records, cancellationToken: token)
    )
    {
        switch (result.Status)
        {
            case MftRecordReadStatus.Uninitialized:
                empty++;
                break;
            case MftRecordReadStatus.Invalid:
                invalid++;
                if (errors.Count < 20)
                    errors.Add(
                        new
                        {
                            result.RecordIndex,
                            result.Error!.Offset,
                            result.Error.Message,
                        }
                    );
                break;
            case MftRecordReadStatus.Parsed:
                if (result.Record!.IsInUse)
                    active++;
                else
                    inactive++;
                break;
        }
    }
    async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
    }
    var report = new
    {
        Source = Path.GetFileName(source),
        CompressedSha256 = await HashAsync(source),
        ImageSha256 = await HashAsync(imagePath),
        MftSha256 = await HashAsync(mftPath),
        PartitionStartSector = partition.FirstSector,
        BytesPerCluster = bytesPerCluster,
        RecordSize = recordSize,
        Active = active,
        Inactive = inactive,
        Uninitialized = empty,
        Invalid = invalid,
        Errors = errors,
    };
    string json = JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions { WriteIndented = true }
    );
    await File.WriteAllTextAsync(Path.Combine(output, "report.json"), json, token);
    Console.WriteLine(json);
    return invalid == 0 ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled; use a fresh output directory to retry.");
    return 130;
}
catch (Exception e)
    when (e
            is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException
    )
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
