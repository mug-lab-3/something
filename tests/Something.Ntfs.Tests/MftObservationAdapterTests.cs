using Something.Ntfs;
using Something.Statistics;

namespace Something.Ntfs.Tests;

public sealed class MftObservationAdapterTests
{
    private static FileReference Ref(ulong i, ushort seq = 1) => FileReference.Create(i, seq);

    private static NtfsFileName Name(
        ulong parent,
        string name = "test",
        FileNameNamespace ns = FileNameNamespace.Win32
    ) => new(Ref(parent), name, ns, 0);

    private static MftScanEntry Entry(ushort links = 1) =>
        new(
            42,
            MftScanEntryKind.ResolvedFile,
            LiveFileReadStatus.ObservedUnchanged,
            new(
                Ref(42),
                false,
                [Name(5), Name(5, "TEST~1", FileNameNamespace.Dos)],
                [new("", true, 4, 0, [])],
                []
            ),
            Evidence: new(Ref(42), false, links, NtfsMetadataKind.Unclassified)
        );

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void LinkCount_RequiresCompleteNonDosNames(ushort links, bool complete)
    {
        var result = MftObservationAdapter.Convert(
            Entry(links),
            TestContext.Current.CancellationToken
        )!;
        Assert.Equal(complete, result.LinkSetComplete);
        Assert.Equal(links, result.ObservedHardLinkCount);
        Assert.Equal(2, result.Names.Length);
        Assert.Equal(new StreamCapacity("", 4, 0), Assert.Single(result.Streams));
    }

    [Fact]
    public void EvidenceMismatch_AndUninitializedArrays_AreRejected()
    {
        var entry = Entry();
        Assert.Throws<InvalidDataException>(() =>
            MftObservationAdapter.Convert(
                entry with
                {
                    Evidence = entry.Evidence! with { Reference = Ref(42, 2) },
                },
                TestContext.Current.CancellationToken
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            MftObservationAdapter.Convert(
                entry with
                {
                    File = entry.File! with { Names = default },
                },
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void TypedUnresolvedMetadata_DoesNotDependOnErrorMessageOrInventZero()
    {
        var entry = new MftScanEntry(
            8,
            MftScanEntryKind.Unresolved,
            LiveFileReadStatus.Unsupported,
            Error: "arbitrary text",
            Evidence: new(Ref(8), false, 1, NtfsMetadataKind.BadClusters)
        );
        var result = MftObservationAdapter.Convert(entry, TestContext.Current.CancellationToken)!;
        Assert.Equal(CapacityDomain.NtfsMetadata, result.Domain);
        Assert.Equal(CapacityResolution.Unresolved, result.Resolution);
        Assert.Empty(result.Streams);
        Assert.False(result.LinkSetComplete);
        Assert.Null(
            MftObservationAdapter.Convert(
                entry with
                {
                    Evidence = null,
                },
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void Extension_IsNotAnotherFile_AndCancellationIsObserved()
    {
        Assert.Null(
            MftObservationAdapter.Convert(
                new(42, MftScanEntryKind.ExtensionRecord, LiveFileReadStatus.ObservedUnchanged),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            MftObservationAdapter.Convert(
                Entry() with
                {
                    Kind = MftScanEntryKind.ExtensionRecord,
                },
                TestContext.Current.CancellationToken
            )
        );
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            MftObservationAdapter.Convert(Entry(), cancel.Token)
        );
    }

    [Theory]
    [InlineData(11, CapacityDomain.NtfsMetadata)]
    [InlineData(20, CapacityDomain.Ordinary)]
    [InlineData(21, CapacityDomain.NtfsMetadata)]
    [InlineData(99, CapacityDomain.Unclassified)]
    public void MetadataAncestry_UsesReferencesInsteadOfDollarPrefix(
        ulong parent,
        CapacityDomain expected
    )
    {
        var graph = FolderGraph.Build(
            Ref(5),
            [
                new(Ref(5), [Ref(5)]),
                new(Ref(11), [Ref(5)]),
                new(Ref(20), [Ref(5)]),
                new(Ref(21), [Ref(11)]),
            ],
            TestContext.Current.CancellationToken
        );
        var observation = MftObservationAdapter.Convert(
            Entry(),
            TestContext.Current.CancellationToken
        )! with
        {
            Names = [Name(parent, "$ordinary-name")],
        };
        var resolver = new ScanDomainResolver(graph);
        Assert.Equal(
            expected,
            resolver.Resolve(observation, TestContext.Current.CancellationToken).Domain
        );
        Assert.Equal(
            CapacityDomain.Unclassified,
            resolver
                .Resolve(
                    observation with
                    {
                        Names = [new(Ref(parent, 2), "stale", FileNameNamespace.Win32, 0)],
                    },
                    TestContext.Current.CancellationToken
                )
                .Domain
        );
        Assert.Equal(
            CapacityDomain.Unclassified,
            resolver
                .Resolve(
                    observation with
                    {
                        Names = [Name(11), Name(20)],
                        LinkSetComplete = true,
                    },
                    TestContext.Current.CancellationToken
                )
                .Domain
        );
    }
}
