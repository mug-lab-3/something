namespace Something.Ntfs.Tests;

public sealed class NtfsMetadataClassifierTests
{
    [Theory]
    [InlineData("$BadClus", "$Bad")]
    [InlineData("$BADCLUS", "$BAD")]
    public async Task ReadAsync_BadClusterMetadata_ReportsUnsupportedRatherThanInventingCapacity(
        string name,
        string stream
    )
    {
        byte[] bytes = Fixture(name: name, stream: stream);
        var source = new Source(bytes);
        var result = await new LiveMftFileReader(4096).ReadAsync(
            source,
            8,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveFileReadStatus.Unsupported, result.Status);
        Assert.Null(result.File);
        Assert.Contains("$BadClus", result.Error);
        Assert.Equal(2, source.Calls);
    }

    [Theory]
    [InlineData(42, "$BadClus", "$Bad", 5, FileNameNamespace.Win32)]
    [InlineData(8, "ordinary.bin", "$Bad", 5, FileNameNamespace.Win32)]
    [InlineData(8, "$BadClus", "ordinary", 5, FileNameNamespace.Win32)]
    [InlineData(8, "$BadClus", "$Bad", 42, FileNameNamespace.Win32)]
    [InlineData(8, "$BadClus", "$Bad", 5, FileNameNamespace.Dos)]
    public void Classify_NameOrIndexAlone_DoesNotRelaxNormalCapacityValidation(
        uint index,
        string name,
        string stream,
        ulong parentIndex,
        FileNameNamespace nameSpace
    )
    {
        byte[] bytes = Fixture(index, name, stream, parentIndex, nameSpace);
        var record = new MftRecordParser().Parse(bytes, index);
        Assert.Equal(NtfsMetadataKind.Unclassified, NtfsMetadataClassifier.Classify(record));
        Assert.Throws<NtfsAssemblyException>(() => new MftFileAssembler(4096).Assemble(record, []));
    }

    [Fact]
    public async Task ReadAsync_BadClusterMetadataWithTornRecord_StillRejectsParserInput()
    {
        byte[] bytes = Fixture();
        bytes[510] ^= 1;
        var result = await new LiveMftFileReader(4096).ReadAsync(
            new Source(bytes),
            8,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveFileReadStatus.InvalidRecord, result.Status);
        Assert.Null(result.File);
    }

    [Fact]
    public void Classify_ExtensionOrDirectoryOrUnusedRecord_DoesNotIdentifyBadClusters()
    {
        var record = new MftRecordParser().Parse(Fixture(), 8);
        Assert.Equal(
            NtfsMetadataKind.Unclassified,
            NtfsMetadataClassifier.Classify(
                record with
                {
                    BaseReference = FileReference.Create(42, 3),
                }
            )
        );
        Assert.Equal(
            NtfsMetadataKind.Unclassified,
            NtfsMetadataClassifier.Classify(
                record with
                {
                    Flags = record.Flags | MftRecordFlags.Directory,
                }
            )
        );
        Assert.Equal(
            NtfsMetadataKind.Unclassified,
            NtfsMetadataClassifier.Classify(record with { Flags = 0 })
        );
    }

    [Fact]
    public void Classify_MissingBadStream_DoesNotIdentifyFromNameOnly()
    {
        var record = new MftRecordParser().Parse(
            RecordFixture.Record(
                [
                    RecordFixture.Resident(0x30, 0, RecordFixture.FileName("$BadClus")),
                    RecordFixture.NonResident(),
                ],
                index: 8
            ),
            8
        );
        Assert.Equal(NtfsMetadataKind.Unclassified, NtfsMetadataClassifier.Classify(record));
    }

    private static byte[] Fixture(
        uint index = 8,
        string name = "$BadClus",
        string stream = "$Bad",
        ulong parentIndex = 5,
        FileNameNamespace nameSpace = FileNameNamespace.Win32
    ) =>
        RecordFixture.Record(
            [
                RecordFixture.Resident(
                    0x30,
                    0,
                    RecordFixture.FileName(
                        name,
                        nameSpace,
                        FileReference.Create(parentIndex, 5).Value
                    )
                ),
                RecordFixture.Resident(0x80, 1, []),
                // Deliberately no Sparse flag: management data can describe holes differently.
                // Parsing preserves the fields; ordinary assembly must still reject this mismatch.
                RecordFixture.NonResident(
                    id: 2,
                    name: stream,
                    runs: [0x01, 2, 0],
                    allocated: 8192,
                    logical: 8192
                ),
            ],
            index: index
        );

    private sealed class Source(byte[] bytes) : IRawMftRecordSource
    {
        public int Calls { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
            ulong recordIndex,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(bytes.AsMemory());
        }
    }
}
