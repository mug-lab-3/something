using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class PublicCorpusTests
{
    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    [InlineData("04")]
    [InlineData("11-mft")]
    [InlineData("11-compress")]
    [InlineData("17")]
    public async Task NistCorpus_VerifiedBytes_ParseAllRecordsAndAssembleUserFiles(string id)
    {
        string? root = Environment.GetEnvironmentVariable("SOMETHING_NIST_CORPUS");
        if (string.IsNullOrWhiteSpace(root))
            Assert.Skip("Opt-in corpus: run scripts/test-public-corpus.ps1 first.");
        var token = TestContext.Current.CancellationToken;
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "nist-corpus.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, token));
        var expected = manifest
            .RootElement.GetProperty("Cases")
            .EnumerateArray()
            .Single(c => c.GetProperty("Id").GetString() == id);
        await using var stream = File.OpenRead(Path.Combine(root, id, "mft.raw"));
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        Assert.Equal(expected.GetProperty("MftSha256").GetString(), hash);
        stream.Position = 0;
        var records = new List<MftRecord>();
        long active = 0,
            inactive = 0,
            empty = 0;
        await foreach (
            var result in new MftRecordReader(
                expected.GetProperty("RecordSize").GetInt32()
            ).ReadAsync(stream, cancellationToken: token)
        )
        {
            Assert.True(
                result.Status != MftRecordReadStatus.Invalid,
                $"{id}, record {result.RecordIndex}: {result.Error?.Message}"
            );
            if (result.Status == MftRecordReadStatus.Uninitialized)
            {
                empty++;
                continue;
            }
            var record = Assert.IsType<MftRecord>(result.Record);
            if (record.IsInUse)
            {
                active++;
                records.Add(record);
            }
            else
                inactive++;
        }
        Assert.Equal(expected.GetProperty("Active").GetInt64(), active);
        Assert.Equal(expected.GetProperty("Inactive").GetInt64(), inactive);
        Assert.Equal(expected.GetProperty("Uninitialized").GetInt64(), empty);
        Assert.Contains(records, r => r.Reference.RecordIndex == 5 && r.IsDirectory);
        var assembler = new MftFileAssembler(expected.GetProperty("BytesPerCluster").GetInt32());
        var files = new Dictionary<FileReference, NtfsFile>();
        // Reserved NTFS metadata (0..15), especially $BadClus, has accounting
        // semantics distinct from user files. Its records are still parsed above.
        foreach (var record in records.Where(r => !r.IsExtension && r.Reference.RecordIndex >= 16))
        {
            // These selected corpora must not require external ATTRIBUTE_LIST payloads.
            Assert.DoesNotContain(
                record.Attributes,
                a => a is NonResidentAttribute && a.Type == NtfsAttributeTypes.AttributeList
            );
            NtfsFile file;
            try
            {
                file = assembler.Assemble(
                    record,
                    records.Where(r => r.IsExtension && r.BaseReference == record.Reference)
                );
            }
            catch (NtfsAssemblyException e)
            {
                Assert.Fail(
                    $"{id}, record {record.Reference.RecordIndex}: {e.Message}; "
                        + JsonSerializer.Serialize(record.Attributes.OfType<NonResidentAttribute>())
                );
                throw;
            }
            Assert.Equal(record.Reference, file.Reference);
            Assert.All(
                file.DataStreams,
                data => Assert.True(data.LogicalBytes >= 0 && data.AllocatedBytes >= 0)
            );
            files.Add(file.Reference, file);
        }

        // Independent directory lookup and physical-cluster decoding, from a hash-verified image.
        // Opening the image read-only prevents the oracle from changing corpus bytes.
        if (!OperatingSystem.IsWindows())
            return;
        await using var image = File.OpenRead(Path.Combine(root, id, "image.dd"));
        Assert.Equal(
            expected.GetProperty("ImageSha256").GetString(),
            Convert.ToHexStringLower(await SHA256.HashDataAsync(image, token))
        );
        image.Position = 0;
        var partitions = new BiosPartitionTable(image, BiosPartitionTable.DetectGeometry(image));
        using var volume = partitions.Partitions.Single(p => p.BiosType == 7).Open();
        using var oracle = new NtfsFileSystem(volume);
        Assert.False(oracle.CanWrite);
        Assert.Equal(expected.GetProperty("BytesPerCluster").GetInt32(), oracle.ClusterSize);
        oracle.NtfsOptions.FileLengthFromDirectoryEntries = false;
        oracle.NtfsOptions.HideHiddenFiles = false;
        oracle.NtfsOptions.HideSystemFiles = false;
        oracle.NtfsOptions.HideMetafiles = false;
        var compared = new HashSet<(FileReference Reference, string Name)>();
        int compressed = 0;
        int sparse = 0;
        foreach (string path in EnumerateOracleFiles(oracle, token))
        {
            token.ThrowIfCancellationRequested();
            var reference = new FileReference(unchecked((ulong)oracle.GetFileId(path)));
            if (reference.RecordIndex < 16)
                continue;
            Assert.True(
                files.TryGetValue(reference, out var file),
                "Oracle identity missing from assembled records."
            );
            foreach (var data in file!.DataStreams)
            {
                if (!compared.Add((reference, data.Name)))
                    continue;
                string streamPath = data.Name.Length == 0 ? path : path + ":" + data.Name;
                long oraclePhysical = 0;
                foreach (var range in oracle.PathToClusters(streamPath))
                    oraclePhysical = checked(
                        oraclePhysical + checked(range.Count * oracle.ClusterSize)
                    );
                Assert.Equal(oracle.GetFileLength(streamPath), data.LogicalBytes);
                Assert.Equal(oraclePhysical, data.AllocatedBytes);
                if (data.Storage is { Flags: var flags })
                {
                    if ((flags & NtfsAttributeFlags.Compressed) != 0)
                        compressed++;
                    if ((flags & NtfsAttributeFlags.Sparse) != 0)
                        sparse++;
                }
            }
        }
        Assert.Equal(expected.GetProperty("IndependentStreams").GetInt32(), compared.Count);
        Assert.Equal(expected.GetProperty("CompressedStreams").GetInt32(), compressed);
        Assert.Equal(expected.GetProperty("SparseStreams").GetInt32(), sparse);
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"{id}: independently compared streams={compared.Count}; compressed={compressed}; sparse={sparse}."
        );
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> EnumerateOracleFiles(
        NtfsFileSystem oracle,
        CancellationToken token
    )
    {
        var pending = new Stack<string>();
        var visited = new HashSet<long>();
        pending.Push(@"\");
        while (pending.TryPop(out string? directory))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(oracle.GetFileId(directory)))
                continue;
            Assert.True(
                visited.Count <= 4096 && directory.Length <= 4096,
                "Oracle traversal exceeded its bounded corpus limits."
            );
            foreach (
                string path in oracle.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            )
                yield return path;
            foreach (
                string path in oracle.GetDirectories(
                    directory,
                    "*.*",
                    SearchOption.TopDirectoryOnly
                )
            )
            {
                token.ThrowIfCancellationRequested();
                if ((oracle.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    pending.Push(path);
            }
        }
    }
}
