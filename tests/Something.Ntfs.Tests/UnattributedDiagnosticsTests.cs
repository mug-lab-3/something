using Something.Ntfs;
using Something.Statistics;

namespace Something.Ntfs.Tests;

public sealed class UnattributedDiagnosticsTests
{
    private static FileReference Ref(ulong index, ushort sequence = 1) =>
        FileReference.Create(index, sequence);

    private static FolderGraph Graph() =>
        FolderGraph.Build(
            Ref(5),
            [
                new(Ref(5), [Ref(5)]),
                new(Ref(20), [Ref(5)]),
                new(Ref(11), [Ref(5)]),
                new(Ref(21), [Ref(999)]),
                new(Ref(22), [Ref(23)]),
                new(Ref(23), [Ref(22)]),
            ],
            TestContext.Current.CancellationToken
        );

    private static FileCapacityObservation File(
        ulong index,
        FileReference parent,
        long size = 10
    ) =>
        new(
            Ref(index),
            false,
            CapacityDomain.Ordinary,
            CapacityResolution.Resolved,
            [new(parent, "synthetic", FileNameNamespace.Win32, 0)],
            [new("", size, size)],
            true
        )
        {
            ObservedHardLinkCount = 1,
        };

    [Theory]
    [InlineData(0, UnattributedReason.IncompleteLinks)]
    [InlineData(1, UnattributedReason.NoVisibleNames)]
    [InlineData(2, UnattributedReason.MissingParent)]
    [InlineData(3, UnattributedReason.ReusedParent)]
    [InlineData(4, UnattributedReason.InvalidHierarchy)]
    [InlineData(5, UnattributedReason.MixedDomains)]
    [InlineData(6, UnattributedReason.UnknownDomain)]
    [InlineData(7, UnattributedReason.InvalidNames)]
    [InlineData(8, UnattributedReason.InvalidHierarchy)]
    [InlineData(9, UnattributedReason.InvalidHierarchy)]
    [InlineData(10, UnattributedReason.DuplicateNames)]
    [InlineData(11, UnattributedReason.OrphanDosAlias)]
    [InlineData(12, UnattributedReason.IncompleteLinks)]
    public void ExplainsExclusionsWithoutChangingKnownCapacity(
        int scenario,
        UnattributedReason expected
    )
    {
        var graph = Graph();
        var collector = new UnattributedDiagnostics(graph);
        var file = File(100, Ref(20));
        file = scenario switch
        {
            0 => file with { LinkSetComplete = false, ObservedHardLinkCount = 2 },
            1 => file with { Names = [] },
            2 => File(100, Ref(999)),
            3 => File(100, Ref(20, 2)),
            4 => File(100, Ref(21)),
            5 => file with
            {
                Names = [.. file.Names, new(Ref(11), "metadata-link", FileNameNamespace.Win32, 0)],
            },
            6 => file with { Domain = CapacityDomain.Unclassified },
            7 => file with { Names = [new(Ref(20), "", FileNameNamespace.Win32, 0)] },
            8 => file with { Reference = Ref(21), IsDirectory = true },
            9 => File(100, Ref(22)),
            10 => file with
            {
                LinkSetComplete = false,
                ObservedHardLinkCount = 2,
                Names = [file.Names[0], file.Names[0]],
            },
            11 => file with
            {
                LinkSetComplete = false,
                ObservedHardLinkCount = 2,
                Names = [.. file.Names, new(Ref(21), "DOS~1", FileNameNamespace.Dos, 0)],
            },
            _ => file with { LinkSetComplete = false, ObservedHardLinkCount = null },
        };
        var resolved = new ScanDomainResolver(graph).Resolve(
            file,
            TestContext.Current.CancellationToken
        );
        var attribution = new FileAttributionPolicy(graph).Attribute(
            resolved,
            TestContext.Current.CancellationToken
        );
        // Invalid names can still have a known domain; request the precise policy result.
        if (scenario == 7)
            attribution = new FileAttributionPolicy(graph).Attribute(
                file,
                TestContext.Current.CancellationToken
            );
        collector.Add(resolved, attribution);
        var summary = Assert.Single(collector.Snapshot());
        Assert.Equal(expected, summary.Reason);
        Assert.Equal(new CapacityAmount(10, 10), summary.KnownData);
        Assert.Equal(1UL, summary.ResolvedCount);
        Assert.Equal(file.Reference, Assert.Single(summary.Samples).Reference);
    }

    [Fact]
    public void BoundedSamples_KeepLargestFilesWithDeterministicTiesAndPreserveUnknown()
    {
        var graph = Graph();
        var collector = new UnattributedDiagnostics(graph);
        foreach (var index in new ulong[] { 103, 101, 102, 104 })
        {
            var file = File(index, Ref(999), index == 104 ? 1 : 20);
            collector.Add(
                file,
                new FileAttributionPolicy(graph).Attribute(
                    file,
                    TestContext.Current.CancellationToken
                )
            );
        }
        var unknown = File(105, Ref(999)) with
        {
            Resolution = CapacityResolution.Unresolved,
            Streams = [],
        };
        collector.Add(
            unknown,
            new FileAttributionPolicy(graph).Attribute(
                unknown,
                TestContext.Current.CancellationToken
            )
        );
        var summary = Assert.Single(collector.Snapshot());
        Assert.Equal(new CapacityAmount(61, 61), summary.KnownData);
        Assert.Equal(4UL, summary.ResolvedCount);
        Assert.Equal(1UL, summary.UnresolvedCount);
        Assert.Equal(
            new ulong[] { 101, 102 },
            summary.Samples.Select(s => s.Reference.RecordIndex).ToArray()
        );
        Assert.Throws<ArgumentException>(() =>
            collector.Add(
                unknown,
                new(
                    Ref(999),
                    AttributionKind.Unattributed,
                    null,
                    null,
                    AttributionIssue.InvalidHierarchy
                )
            )
        );
    }
}
