using System.Collections.Immutable;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Ntfs.Tests;

public sealed class FolderStatisticsTests
{
    private static readonly FileReference Root = Ref(5);
    private static readonly FileReference A = Ref(20);
    private static readonly FileReference B = Ref(21);
    private static readonly FileReference Child = Ref(22);

    private static FileReference Ref(ulong index, ushort sequence = 1) =>
        FileReference.Create(index, sequence);

    private static FolderObservation Folder(
        FileReference reference,
        params FileReference[] parents
    ) => new(reference, [.. parents]);

    private static FolderGraph Graph() =>
        FolderGraph.Build(
            Root,
            [Folder(Root, Root), Folder(A, Root), Folder(B, Root), Folder(Child, A)],
            TestContext.Current.CancellationToken
        );

    private static FileCapacityObservation File(
        ulong index,
        long bytes,
        params FileReference[] parents
    ) =>
        new(
            Ref(index),
            false,
            CapacityDomain.Ordinary,
            CapacityResolution.Resolved,
            [
                .. parents.Select(
                    (p, i) => new NtfsFileName(p, $"name-{i}", FileNameNamespace.Win32, 0)
                ),
            ],
            [new("", bytes, bytes)],
            true
        );

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 5)]
    [InlineData(2, 20)]
    [InlineData(3, 20)]
    public void CommonAncestor_NestedSiblingAndRepeatedParents_ReturnsNearest(
        int scenario,
        ulong expected
    )
    {
        FileReference[] parents = scenario switch
        {
            0 => [A, Child],
            1 => [Child, B],
            2 => [A, A],
            _ => [Child, A, A],
        };
        var graph = Graph();
        Assert.Equal(
            Ref(expected),
            graph.FindCommonAncestor(parents, TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            Ref(expected),
            graph.FindCommonAncestor(parents.Reverse(), TestContext.Current.CancellationToken)
        );
    }

    [Theory]
    [InlineData(0, FolderIssue.MissingParent)]
    [InlineData(1, FolderIssue.ReusedParent)]
    [InlineData(2, FolderIssue.AmbiguousParent)]
    [InlineData(3, FolderIssue.Cycle)]
    [InlineData(4, FolderIssue.Cycle)]
    [InlineData(5, FolderIssue.MissingParent)]
    public void Graph_InvalidParentOrCycle_PreservesProblemAndBlocksDescendants(
        int scenario,
        FolderIssue expected
    )
    {
        var a = scenario switch
        {
            0 => Folder(A, Ref(999)),
            1 => Folder(A, Ref(5, 2)),
            2 => Folder(A, Root, B),
            3 => Folder(A, A),
            4 => Folder(A, B),
            _ => Folder(A),
        };
        var graph = FolderGraph.Build(
            Root,
            [Folder(Child, A), a, Folder(B, scenario == 4 ? A : Root), Folder(Root, Root)],
            TestContext.Current.CancellationToken
        );
        Assert.Equal(expected, graph.Nodes[A].Issue);
        Assert.Equal(FolderIssue.InvalidAncestor, graph.Nodes[Child].Issue);
        Assert.Null(graph.FindCommonAncestor([A, Child], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, FolderIssue.MissingRoot)]
    [InlineData(true, FolderIssue.InvalidRoot)]
    public void Graph_MissingOrInvalidRoot_DoesNotInventHierarchy(
        bool includeRoot,
        FolderIssue expected
    )
    {
        FolderObservation[] folders = includeRoot
            ? [Folder(Root, A), Folder(A, Root)]
            : [Folder(A, Root)];
        var graph = FolderGraph.Build(Root, folders, TestContext.Current.CancellationToken);
        if (includeRoot)
            Assert.Equal(expected, graph.Nodes[Root].Issue);
        else
            Assert.Equal(expected, graph.Nodes[A].Issue);
        Assert.Null(graph.FindCommonAncestor([A], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Graph_DeepReverseOrderedTree_UsesIterativeTraversal()
    {
        const int depth = 10000;
        var observations = Enumerable
            .Range(100, depth)
            .Reverse()
            .Select(i => Folder(Ref((ulong)i), i == 100 ? Root : Ref((ulong)i - 1)))
            .Append(Folder(Root, Root));
        var graph = FolderGraph.Build(Root, observations, TestContext.Current.CancellationToken);
        Assert.Equal(depth, graph.Nodes[Ref(100 + depth - 1)].Depth);
        Assert.Equal(
            Root,
            graph.FindCommonAncestor(
                [Root, Ref(100 + depth - 1)],
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void Graph_DuplicateOrReusedRecordInGeneration_RejectsAmbiguousInput()
    {
        Assert.Throws<InvalidDataException>(() =>
            FolderGraph.Build(
                Root,
                [Folder(Root, Root), Folder(A, Root), Folder(Ref(20, 2), Root)],
                TestContext.Current.CancellationToken
            )
        );
        Assert.Throws<ArgumentException>(() =>
            FolderGraph.Build(Root, [new(Root, default)], TestContext.Current.CancellationToken)
        );
        Assert.Null(Graph().FindCommonAncestor([], TestContext.Current.CancellationToken));
        Assert.Null(Graph().FindCommonAncestor([Ref(999)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Aggregate_Shared100MiB_ChargesCommonAncestorOnceAndKeepsLeafZero()
    {
        const long bytes = 100 * 1024 * 1024;
        var observation = File(100, bytes, A, B);
        var graph = Graph();
        var attribution = new FileAttributionPolicy(graph).Attribute(
            observation,
            TestContext.Current.CancellationToken
        );
        var result = new FolderAggregator(graph).Aggregate(
            [observation],
            TestContext.Current.CancellationToken
        );
        Assert.Equal(AttributionKind.Shared, attribution.Kind);
        Assert.Equal(Root, attribution.Folder);
        Assert.Equal((UInt128)bytes, result.Folders[Root].SharedData.AllocatedBytes);
        Assert.Equal((UInt128)bytes, result.Folders[Root].SubtreeData.AllocatedBytes);
        Assert.Equal(default, result.Folders[A].SubtreeData);
        Assert.Equal(default, result.Folders[B].SubtreeData);
        Assert.Equal(1UL, result.Folders[Root].SubtreeFileCount);
        Assert.Equal(
            new CapacityAmount(checked((UInt128)bytes * 2), checked((UInt128)bytes * 2)),
            result.Folders[Root].Entries!.SubtreeData
        );
        Assert.Equal(
            new CapacityAmount((UInt128)bytes, (UInt128)bytes),
            result.Folders[A].Entries!.SubtreeData
        );
        Assert.Equal(
            new CapacityAmount((UInt128)bytes, (UInt128)bytes),
            result.Folders[B].Entries!.SubtreeData
        );
        Assert.False(result.HasUnknownAttribution);
        Assert.Equal(2, observation.Names.Length);
    }

    [Fact]
    public void Aggregate_SameParentNamesAndDosAliases_CountsOneFileAndAllStreams()
    {
        var observation = File(100, 5000, A, A) with
        {
            Names =
            [
                new(A, "alpha", FileNameNamespace.Win32AndDos, 0),
                new(A, "ALPHA~1", FileNameNamespace.Dos, 0),
                new(A, "Alpha", FileNameNamespace.Posix, 0),
            ],
            Streams = [new("", 5000, 4096), new("named", 100, 0)],
        };
        var result = new FolderAggregator(Graph()).Aggregate(
            [observation],
            TestContext.Current.CancellationToken
        );
        Assert.Equal(new CapacityAmount(5100, 4096), result.Folders[A].DirectData);
        Assert.Equal(1UL, result.Folders[A].SubtreeFileCount);
        Assert.Equal(result.Folders[A].SubtreeData, result.Folders[Root].SubtreeData);
        Assert.Equal(3, observation.Names.Length);
        Assert.Equal(new CapacityAmount(10000, 8192), result.Folders[A].Entries!.DirectData);
        Assert.Equal(2UL, result.Folders[A].Entries!.SubtreeFileCount);
    }

    [Theory]
    [InlineData(0, AttributionIssue.IncompleteLinks)]
    [InlineData(1, AttributionIssue.IncompleteLinks)]
    [InlineData(2, AttributionIssue.InvalidHierarchy)]
    [InlineData(3, AttributionIssue.InvalidNames)]
    [InlineData(4, AttributionIssue.UnknownDomain)]
    [InlineData(5, AttributionIssue.InvalidNames)]
    public void Attribute_UnknownLinksNamesOrParent_DoesNotGuessOwner(
        int scenario,
        AttributionIssue issue
    )
    {
        var observation = File(100, 123, A) with
        {
            LinkSetComplete = scenario != 0,
            Domain = scenario == 4 ? CapacityDomain.Unclassified : CapacityDomain.Ordinary,
        };
        observation = scenario switch
        {
            1 => observation with { Names = [new(A, "alias", FileNameNamespace.Dos, 0)] },
            2 => observation with { Names = [new(Ref(20, 2), "file", FileNameNamespace.Win32, 0)] },
            3 => observation with { Names = [new(A, "", FileNameNamespace.Win32, 0)] },
            5 => observation with { Names = [new(A, "file", (FileNameNamespace)99, 0)] },
            _ => observation,
        };
        var attribution = new FileAttributionPolicy(Graph()).Attribute(
            observation,
            TestContext.Current.CancellationToken
        );
        var result = new FolderAggregator(Graph()).Aggregate(
            [observation],
            TestContext.Current.CancellationToken
        );
        Assert.Equal(issue, attribution.Issue);
        Assert.Null(attribution.Folder);
        Assert.Equal(new CapacityAmount(123, 123), result.Unattributed.KnownData);
        Assert.True(result.HasUnknownAttribution);
        Assert.Equal(default, result.Folders[Root].SubtreeData);
    }

    [Fact]
    public void Aggregate_UnresolvedOrdinaryAndMetadata_AreSeparateFromKnownZero()
    {
        var unknown = File(100, 1, Child) with
        {
            Resolution = CapacityResolution.Unresolved,
            Streams = [],
        };
        var metadata = File(8, 1) with
        {
            Domain = CapacityDomain.NtfsMetadata,
            Resolution = CapacityResolution.Unresolved,
            Streams = [],
        };
        var result = new FolderAggregator(Graph()).Aggregate(
            [
                unknown,
                metadata,
                File(101, 0, A),
                File(0, 8192) with
                {
                    Domain = CapacityDomain.NtfsMetadata,
                },
            ],
            TestContext.Current.CancellationToken
        );
        Assert.Null(
            new FileAttributionPolicy(Graph())
                .Attribute(unknown, TestContext.Current.CancellationToken)
                .Capacity
        );
        Assert.Equal(1UL, result.Folders[Root].SubtreeUnresolvedCount);
        Assert.Equal(1UL, result.Folders[Child].DirectUnresolvedCount);
        Assert.Equal(1UL, result.Folders[A].SubtreeFileCount);
        Assert.Equal(1UL, result.Metadata.UnresolvedCount);
        Assert.Equal(new CapacityAmount(8192, 8192), result.Metadata.KnownData);
        Assert.Equal(default, result.Folders[Root].SubtreeData);
        Assert.False(result.HasUnknownAttribution);
    }

    [Fact]
    public void Aggregate_DirectoryDataAndUnlocalizedUnknown_PreserveDistinctAccounting()
    {
        var directory = File(20, 8) with { IsDirectory = true };
        var unknown = File(100, 1) with
        {
            Resolution = CapacityResolution.Unresolved,
            Streams = [],
            LinkSetComplete = false,
        };
        var result = new FolderAggregator(Graph()).Aggregate(
            [directory, unknown],
            TestContext.Current.CancellationToken
        );
        Assert.Equal(new CapacityAmount(8, 8), result.Folders[A].DirectoryData);
        Assert.Equal(0UL, result.Folders[Root].SubtreeFileCount);
        Assert.Equal(1UL, result.Unattributed.UnresolvedCount);
        Assert.True(result.HasUnknownAttribution);
        var broken = File(999, 10) with { IsDirectory = true };
        Assert.Equal(
            AttributionKind.Unattributed,
            new FileAttributionPolicy(Graph())
                .Attribute(broken, TestContext.Current.CancellationToken)
                .Kind
        );
    }

    [Fact]
    public void Aggregate_InputPermutationAndRandomSharing_PreserveUniqueTotalsAndAdditivity()
    {
        var random = new Random(20261013);
        var observations = Enumerable
            .Range(100, 200)
            .Select(i =>
                File(
                    (ulong)i,
                    random.Next(0, 100000),
                    Enumerable
                        .Range(0, random.Next(1, 5))
                        .Select(_ => new[] { Root, A, B, Child }[random.Next(4)])
                        .ToArray()
                )
            )
            .ToArray();
        var graph = Graph();
        var expected = observations.Aggregate(
            default(CapacityAmount),
            (sum, file) =>
                sum
                + new CapacityAmount(
                    (UInt128)file.Streams[0].LogicalBytes,
                    (UInt128)file.Streams[0].AllocatedBytes
                )
        );
        var result = new FolderAggregator(graph).Aggregate(
            observations,
            TestContext.Current.CancellationToken
        );
        var reordered = new FolderAggregator(
            FolderGraph.Build(
                Root,
                [Folder(Child, A), Folder(B, Root), Folder(A, Root), Folder(Root, Root)],
                TestContext.Current.CancellationToken
            )
        ).Aggregate(
            observations.OrderBy(_ => random.Next()),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(expected, result.Folders[Root].SubtreeData);
        foreach (var (reference, sample) in result.Folders)
        {
            Assert.Equal(sample, reordered.Folders[reference]);
            var children = graph.Nodes.Values.Where(n =>
                n.Reference != Root && n.Parent == reference
            );
            var computed = children.Aggregate(
                sample.DirectData + sample.SharedData + sample.DirectoryData,
                (sum, n) => sum + result.Folders[n.Reference].SubtreeData
            );
            Assert.Equal(computed, sample.SubtreeData);
        }
        Assert.Equal(200UL, result.Folders[Root].SubtreeFileCount);
    }

    [Fact]
    public void Capacity_ExceedsSigned64BitWithoutTruncation_AndRejectsUInt128Overflow()
    {
        var result = new FolderAggregator(Graph()).Aggregate(
            [File(100, long.MaxValue, A), File(101, long.MaxValue, A)],
            TestContext.Current.CancellationToken
        );
        Assert.Equal((UInt128)long.MaxValue * 2, result.Folders[Root].SubtreeData.AllocatedBytes);
        Assert.Throws<OverflowException>(() =>
            new CapacityAmount(UInt128.MaxValue, 0) + new CapacityAmount(1, 0)
        );
        Assert.Throws<OverflowException>(() =>
            new CapacityAmount(0, UInt128.MaxValue) + new CapacityAmount(0, 1)
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Attribute_InvalidObservationContract_RejectsPartialOrUninitializedData(int scenario)
    {
        var observation = File(100, 12, A);
        observation = scenario switch
        {
            0 => observation with { Resolution = CapacityResolution.Unresolved },
            1 => observation with { Names = default },
            2 => observation with { Streams = default },
            _ => observation with { Domain = (CapacityDomain)99 },
        };
        Assert.Throws<ArgumentException>(() =>
            new FileAttributionPolicy(Graph()).Attribute(
                observation,
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void Aggregate_DuplicateIdentityOrStream_RejectsDoubleAccounting()
    {
        var file = File(100, 100, A);
        Assert.Throws<InvalidDataException>(() =>
            new FolderAggregator(Graph()).Aggregate(
                [file, file],
                TestContext.Current.CancellationToken
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            new FolderAggregator(Graph()).Aggregate(
                [file, file with { Reference = Ref(100, 2) }],
                TestContext.Current.CancellationToken
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            new FolderAggregator(Graph()).Aggregate(
                [file with { Streams = [new("", 1, 1), new("", 2, 2)] }],
                TestContext.Current.CancellationToken
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamCapacity("", -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamCapacity("", 0, -1));
    }

    [Fact]
    public void Aggregate_MissingDirectoryCapacity_IsNotAssumedToBeKnownZero()
    {
        var graph = Graph();
        var incomplete = new FolderAggregator(graph).Aggregate(
            [File(100, 10, A)],
            TestContext.Current.CancellationToken
        );
        Assert.False(incomplete.Folders[A].DirectoryCapacityObserved);
        Assert.Equal(4UL, incomplete.Folders[Root].SubtreeUnobservedDirectoryCount);
        var directoryFacts = new[] { Root, A, B, Child }.Select(r =>
            File(r.RecordIndex, 0) with
            {
                IsDirectory = true,
            }
        );
        var complete = new FolderAggregator(graph).Aggregate(
            directoryFacts.Append(File(100, 10, A)),
            TestContext.Current.CancellationToken
        );
        Assert.True(complete.Folders[A].DirectoryCapacityObserved);
        Assert.Equal(0UL, complete.Folders[Root].SubtreeUnobservedDirectoryCount);
        Assert.Equal(incomplete.Folders[Root].SubtreeData, complete.Folders[Root].SubtreeData);
    }

    [Fact]
    public void Aggregate_EmptyGraphOrInvalidDirectory_ReportsUnknownAttribution()
    {
        var graph = FolderGraph.Build(Root, [], TestContext.Current.CancellationToken);
        var result = new FolderAggregator(graph).Aggregate(
            [],
            TestContext.Current.CancellationToken
        );
        Assert.Empty(result.Folders);
        Assert.True(result.HasUnknownAttribution);
        Assert.Throws<InvalidDataException>(() =>
            new FileAttributionPolicy(Graph()).Attribute(
                File(20, 10, Root),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void Aggregate_UnresolvedMetadataOnly_DoesNotMarkOrdinaryFolderCapacityUnknown()
    {
        var directories = new[] { Root, A, B, Child }.Select(r =>
            File(r.RecordIndex, 0) with
            {
                IsDirectory = true,
            }
        );
        var badClusters = File(8, 0) with
        {
            Domain = CapacityDomain.NtfsMetadata,
            Resolution = CapacityResolution.Unresolved,
            Streams = [],
        };
        var result = new FolderAggregator(Graph()).Aggregate(
            directories.Append(badClusters),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(1UL, result.Metadata.UnresolvedCount);
        Assert.Equal(0UL, result.Folders[Root].SubtreeUnresolvedCount);
        Assert.Equal(0UL, result.Folders[Root].SubtreeUnobservedDirectoryCount);
        Assert.False(result.HasUnknownAttribution);
    }

    [Fact]
    public void CancelledOperations_DoNotReturnPartialAggregatesOrAccessInputs()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            FolderGraph.Build(Root, [], cancellation.Token)
        );
        Assert.Throws<OperationCanceledException>(() =>
            Graph().FindCommonAncestor([], cancellation.Token)
        );
        Assert.Throws<OperationCanceledException>(() =>
            new FolderAggregator(Graph()).Aggregate([], cancellation.Token)
        );
        Assert.Throws<OperationCanceledException>(() =>
            new FileAttributionPolicy(Graph()).Attribute(File(100, 1, A), cancellation.Token)
        );
    }

    [Fact]
    public void Aggregate_CancellationDuringInput_DisposesEnumeratorAndReturnsNoResult()
    {
        using var cancellation = new CancellationTokenSource();
        bool disposed = false;
        IEnumerable<FileCapacityObservation> Observations()
        {
            try
            {
                yield return File(100, 1, A);
                cancellation.Cancel();
                yield return File(101, 1, B);
            }
            finally
            {
                disposed = true;
            }
        }
        Assert.Throws<OperationCanceledException>(() =>
            new FolderAggregator(Graph()).Aggregate(Observations(), cancellation.Token)
        );
        Assert.True(disposed);
    }
}
