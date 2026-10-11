using System.Security.Cryptography;
using System.Text.Json;
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
        }
    }
}
