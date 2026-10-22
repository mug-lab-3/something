using System.Collections.Immutable;
using Something.Statistics;

namespace Something.Ntfs.Tests;

public sealed class FolderAccountingDiagnosticsTests
{
    private static FileReference Ref(ulong index) => FileReference.Create(index, 1);

    private static NtfsFileName Name(
        ulong parent,
        string name,
        FileNameNamespace ns = FileNameNamespace.Win32
    ) => new(Ref(parent), name, ns, 0);

    private static FolderGraph Graph() =>
        FolderGraph.Build(
            Ref(5),
            [
                new(Ref(5), [Ref(5)]),
                new(Ref(20), [Ref(5)]),
                new(Ref(21), [Ref(5)]),
                new(Ref(22), [Ref(20)]),
            ],
            TestContext.Current.CancellationToken
        );

    private static FileCapacityObservation File(
        ulong index,
        ImmutableArray<NtfsFileName> names,
        ImmutableArray<StreamCapacity> streams
    ) =>
        new(
            Ref(index),
            false,
            CapacityDomain.Ordinary,
            CapacityResolution.Resolved,
            names,
            streams,
            true
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Analyze_SeparatesDuplicateNamesCrossBoundaryStreamsAndOverlappingTraits(
        bool reverse
    )
    {
        var graph = Graph();
        FileCapacityObservation[] files =
        [
            File(20, [], [new("directory-metadata", 7, 0)]) with
            {
                IsDirectory = true,
            },
            File(22, [], []) with
            {
                IsDirectory = true,
            },
            File(
                100,
                [Name(20, "first"), Name(20, "FIRST~1", FileNameNamespace.Dos), Name(22, "second")],
                [new("", 100, 80), new("ads", 3, 0)]
            ),
            File(
                101,
                [Name(20, "inside"), Name(21, "outside")],
                [new("", 40, 32), new("ads", 5, 4)]
            ),
            File(102, [Name(21, "unrelated")], [new("", 1000, 1000)]),
            File(103, [Name(22, "resident")], [new("", 10, 0)]),
            File(104, [Name(20, "metadata")], [new("", 10000, 10000)]) with
            {
                Domain = CapacityDomain.NtfsMetadata,
            },
        ];
        var traits = new Dictionary<FileReference, FileStorageTraits>
        {
            [Ref(22)] = FileStorageTraits.ReparsePoint,
            [Ref(100)] = FileStorageTraits.CompressedData | FileStorageTraits.SparseData,
            [Ref(101)] = FileStorageTraits.ReparsePoint,
            [Ref(103)] = FileStorageTraits.ResidentData,
        };

        var result = FolderAccountingDiagnostics.Analyze(
            graph,
            Ref(20),
            reverse ? files.Reverse() : files,
            traits,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new CapacityAmount(120, 80), result.AssignedData);
        Assert.Equal(new CapacityAmount(3, 0), result.AssignedNamedStreams);
        Assert.Equal(new CapacityAmount(7, 0), result.AssignedDirectoryData);
        Assert.Equal(new CapacityAmount(150, 112), result.VisibleUniqueUnnamedData);
        Assert.Equal(new CapacityAmount(250, 192), result.PerVisibleNameUnnamedData);
        Assert.Equal(new CapacityAmount(100, 80), result.DuplicateNameExtraUnnamedData);
        Assert.Equal(new CapacityAmount(40, 32), result.CrossBoundaryUnnamedData);
        Assert.Equal(3UL, result.VisibleFiles);
        Assert.Equal(4UL, result.VisibleNames);
        Assert.Equal(1UL, result.MultipleNameFiles);
        Assert.Equal(1UL, result.DosAliases);
        Assert.Equal(1UL, result.DescendantDirectories);
        Assert.Equal(1UL, result.ReparseDirectories);
        Assert.Equal(
            new CapacityBucket(new(103, 80), 1, 0),
            result.SpecialFiles[FileStorageTraits.CompressedData]
        );
        Assert.Equal(
            result.SpecialFiles[FileStorageTraits.CompressedData],
            result.SpecialFiles[FileStorageTraits.SparseData]
        );
        Assert.Equal(
            new CapacityBucket(new(45, 36), 1, 0),
            result.SpecialFiles[FileStorageTraits.ReparsePoint]
        );
        Assert.Equal(
            new CapacityBucket(new(10, 0), 1, 0),
            result.SpecialFiles[FileStorageTraits.ResidentData]
        );
        var aggregation = new FolderAggregator(graph).Aggregate(
            files,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(result.AssignedData, aggregation.Folders[Ref(20)].SubtreeData);
        Assert.Equal(
            result.PerVisibleNameUnnamedData,
            aggregation.Folders[Ref(20)].Entries!.SubtreeData
        );
        Assert.Equal(result.VisibleNames, aggregation.Folders[Ref(20)].Entries!.SubtreeFileCount);
    }

    [Fact]
    public void Analyze_PreservesUncertaintyWithoutCountingKnownPartialNames()
    {
        FileCapacityObservation[] files =
        [
            File(100, [Name(20, "unresolved")], []) with
            {
                Resolution = CapacityResolution.Unresolved,
            },
            File(101, [Name(20, "incomplete")], [new("", 90, 80)]) with
            {
                LinkSetComplete = false,
            },
            File(102, [Name(20, "known"), Name(999, "missing-parent")], [new("", 30, 20)]),
        ];
        var traits = new Dictionary<FileReference, FileStorageTraits>
        {
            [Ref(100)] = FileStorageTraits.CompressedData,
        };

        var result = FolderAccountingDiagnostics.Analyze(
            Graph(),
            Ref(20),
            files,
            traits,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(default, result.AssignedData);
        Assert.Equal(default, result.PerVisibleNameUnnamedData);
        Assert.Equal(3UL, result.VisibleFiles);
        Assert.Equal(1UL, result.IncompleteNameSets);
        Assert.Equal(1UL, result.UnattributedVisibleFiles);
        Assert.Equal(1UL, result.UnresolvedVisibleFiles);
        Assert.Equal(
            new CapacityBucket(default, 0, 1),
            result.SpecialFiles[FileStorageTraits.CompressedData]
        );
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Analyze_RejectsDuplicateIdentityAndUnknownTraits(int scenario)
    {
        var file = File(100, [Name(20, "file")], [new("", 1, 1)]);
        Assert.Throws<InvalidDataException>(() =>
            FolderAccountingDiagnostics.Analyze(
                Graph(),
                Ref(20),
                scenario == 1 ? [file, file] : [file],
                new Dictionary<FileReference, FileStorageTraits>
                {
                    [Ref(100)] = scenario == 2 ? (FileStorageTraits)16 : FileStorageTraits.None,
                },
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void Analyze_RejectsMissingScope() =>
        Assert.Throws<ArgumentException>(() =>
            FolderAccountingDiagnostics.Analyze(
                Graph(),
                Ref(999),
                [],
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

    [Fact]
    public void Analyze_CancellationDuringEnumeration_DoesNotReturnPartialTotals()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        IEnumerable<FileCapacityObservation> Observations()
        {
            yield return File(100, [Name(20, "first")], [new("", 1, 1)]);
            cancellation.Cancel();
            yield return File(101, [Name(20, "second")], [new("", 1, 1)]);
        }
        Assert.Throws<OperationCanceledException>(() =>
            FolderAccountingDiagnostics.Analyze(
                Graph(),
                Ref(20),
                Observations(),
                cancellationToken: cancellation.Token
            )
        );
    }

    [Fact]
    public void Classify_UsesDataStorageAndReparseAttributesRatherThanCachedNameFlags()
    {
        var file = new NtfsFile(
            Ref(100),
            false,
            [new(Ref(20), "test", FileNameNamespace.Win32, uint.MaxValue)],
            [
                new("", false, 100, 80, [])
                {
                    Storage = new(NtfsAttributeFlags.Compressed | NtfsAttributeFlags.Sparse, 4),
                },
                new("ads", true, 1, 0, []),
            ],
            [new ResidentAttribute(NtfsAttributeTypes.ReparsePoint, 0, "", 0, [])]
        );
        Assert.Equal(
            FileStorageTraits.CompressedData
                | FileStorageTraits.SparseData
                | FileStorageTraits.ResidentData
                | FileStorageTraits.ReparsePoint,
            FolderAccountingDiagnostics.Classify(file)
        );
        Assert.Equal(
            FileStorageTraits.None,
            FolderAccountingDiagnostics.Classify(file with { DataStreams = [], Attributes = [] })
        );
    }
}
