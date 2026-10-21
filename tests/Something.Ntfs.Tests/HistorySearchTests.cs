using System.Collections.Immutable;
using System.Numerics;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class HistorySearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly HistorySearchCriteria Everything = new(Now.AddDays(-30), Now);

    /// <summary>A directory or file with one name, or two names when <see cref="Link"/> is set.</summary>
    private sealed record Node(
        ulong Index,
        string Name,
        ulong Parent = 5,
        long? Size = 0,
        bool Directory = false,
        uint? Attributes = 0,
        (ulong Parent, string Name)? Link = null,
        bool NamedStream = false
    );

    [Fact]
    public void ValueConditionsUseTheLastScanAndChangesStartAtTheFirst()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Save(
            Now.AddDays(-40),
            [new(20, "Videos", Directory: true), new(30, "a.mp4", 20, 1)]
        );
        fixture.Save(
            Now.AddDays(-3),
            [
                new(20, "Videos", Directory: true),
                new(21, "Docs", Directory: true),
                new(30, "a.mp4", 20, 100),
                new(31, "b.txt", 21, 900),
            ]
        );
        fixture.Save(
            Now.AddDays(-1),
            [
                new(20, "Videos", Directory: true),
                new(21, "Docs", Directory: true),
                new(30, "a.mp4", 20, 700),
                new(31, "b.txt", 21, 950),
            ]
        );

        // Act
        using var result = fixture.Archive.Search(
            "volume:A",
            Everything with
            {
                Target = SearchTarget.Files,
                MinimumDelta = 500,
            },
            Token
        );

        // Assert
        Assert.Equal(HistorySearchStatus.Completed, result.Status);
        Assert.Equal(2, result.Scans.Length);
        var file = Assert.Single(result.Entries(result.Folder(Ref(20))!.Reference));
        Assert.Equal("a.mp4", file.Name);
        Assert.Equal((UInt128)100, file.FirstSize);
        Assert.Equal((UInt128)700, file.Size);
        Assert.Equal(new BigInteger(600), file.Delta);
        Assert.True(file.Matched);
        var top = Assert.Single(result.ChildFolders(result.Root));
        Assert.Equal("Videos", top.Name);
        Assert.False(top.Matched);
        Assert.Equal(1, result.MatchedFiles);
        Assert.Equal(0, result.MatchedFolders);
        Assert.Equal("Videos", result.PathOf(top.Reference));
    }

    [Fact]
    public void FoldersMatchOnSubtreeSizesAndAncestorsAreListed()
    {
        // Arrange
        using var fixture = new Fixture();
        Node[] tree =
        [
            new(20, "Users", Directory: true),
            new(21, "mug", 20, Directory: true),
            new(22, "Cache", 21, Directory: true),
            new(23, "Small", 21, Directory: true),
            new(30, "blob", 22, 5000),
            new(31, "note", 23, 10),
        ];
        fixture.Save(Now.AddDays(-2), tree);
        fixture.Save(Now.AddDays(-1), tree);

        // Act
        using var result = fixture.Archive.Search(
            "volume:A",
            Everything with
            {
                Target = SearchTarget.Folders,
                MinimumSize = 1000,
                MinimumDepth = 3,
            },
            Token
        );

        // Assert
        var cache = result.Folder(Ref(22))!;
        Assert.True(cache.Matched);
        Assert.Equal((UInt128)5000, cache.Size);
        Assert.False(result.Folder(Ref(21))!.Matched);
        Assert.Null(result.Folder(Ref(23)));
        Assert.Equal("Users\\mug\\Cache", result.PathOf(cache.Reference));
        Assert.Equal(["Cache"], result.Entries(Ref(21)).Select(e => e.Name));
        Assert.Equal(1, result.MatchedFolders);
    }

    [Fact]
    public void PresenceSeparatesAppearedDisappearedAndPersistentItems()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Save(
            Now.AddDays(-3),
            [new(20, "Old", Directory: true), new(30, "gone.log", 20, 10), new(31, "kept", 5, 1)]
        );
        fixture.Save(Now.AddDays(-2), [new(31, "kept", 5, 2), new(32, "new", 5, 3)]);
        fixture.Save(Now.AddDays(-1), [new(31, "kept", 5, 3), new(32, "new", 5, 4)]);

        // Act
        using var all = Search(fixture, Everything with { Presence = SearchPresence.InAllScans });
        using var appeared = Search(
            fixture,
            Everything with
            {
                Presence = SearchPresence.Appeared,
            }
        );
        using var gone = Search(
            fixture,
            Everything with
            {
                Presence = SearchPresence.Disappeared,
                Target = SearchTarget.Files,
            }
        );

        // Assert
        Assert.Equal(["kept"], all.Entries(all.Root).Select(e => e.Name));
        Assert.Equal(["new"], appeared.Entries(appeared.Root).Select(e => e.Name));
        var old = Assert.Single(gone.ChildFolders(gone.Root));
        Assert.Equal("Old", old.Name);
        var log = Assert.Single(gone.Entries(old.Reference));
        Assert.Equal(0, log.LastObserved);
        Assert.Equal((UInt128)10, log.Size);
    }

    [Fact]
    public void UnresolvedSizesAreKeptAsUncertainButNeverBecomeZero()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Save(
            Now.AddDays(-2),
            [
                new(30, "unknown-now", 5, 100),
                new(31, "unknown-before", 5, null),
                new(32, "zero", 5, 0),
            ]
        );
        fixture.Save(
            Now.AddDays(-1),
            [
                new(30, "unknown-now", 5, null),
                new(31, "unknown-before", 5, 900),
                new(32, "zero", 5, 50),
            ]
        );

        // Act
        using var large = Search(fixture, Everything with { MinimumSize = 500 });
        using var rate = Search(fixture, Everything with { MinimumDeltaPercent = 10 });
        using var delta = Search(fixture, Everything with { MinimumDelta = 10 });

        // Assert
        var entries = large.Entries(large.Root).ToDictionary(e => e.Name);
        Assert.True(entries["unknown-now"].Uncertain);
        Assert.Null(entries["unknown-now"].Size);
        Assert.False(entries["unknown-before"].Uncertain);
        Assert.False(entries.ContainsKey("zero"));
        Assert.Equal(1, large.UncertainCount);
        // A rate needs a known nonzero first size, so zero and unresolved first sizes never match.
        Assert.Equal(["unknown-now"], rate.Entries(rate.Root).Select(e => e.Name));
        Assert.Equal(
            ["unknown-before", "unknown-now", "zero"],
            delta.Entries(delta.Root).Select(e => e.Name)
        );
        Assert.True(delta.Entries(delta.Root).Single(e => e.Name == "unknown-before").Uncertain);
    }

    [Fact]
    public void RenamedSingleNamesContinueWhileHardLinkNamesDoNot()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Save(
            Now.AddDays(-2),
            [
                new(20, "A", Directory: true),
                new(30, "before.txt", 5, 10),
                new(31, "x", 5, 7, Link: (20, "y")),
            ]
        );
        fixture.Save(
            Now.AddDays(-1),
            [
                new(20, "A", Directory: true),
                new(30, "after.txt", 20, 20),
                new(31, "x", 5, 9, Link: (20, "z")),
            ]
        );

        // Act
        using var result = Search(fixture, Everything with { Target = SearchTarget.Files });

        // Assert
        var moved = Assert.Single(result.Entries(Ref(20)), e => e.Reference == Ref(30));
        Assert.Equal("after.txt", moved.Name);
        Assert.Equal(new BigInteger(10), moved.Delta);
        Assert.Equal(2, moved.ObservedCount);
        var links = result
            .Entries(result.Root)
            .Concat(result.Entries(Ref(20)))
            .Where(e => e.Reference == Ref(31))
            .ToDictionary(e => e.Name);
        Assert.Equal(["x", "y", "z"], links.Keys.Order());
        Assert.Equal(new BigInteger(2), links["x"].Delta);
        Assert.Equal(0, links["y"].LastObserved);
        Assert.False(links["z"].InFirstScan);
    }

    [Fact]
    public void AttributeConditionsExcludeUnknownAttributesAndOverrideVisibility()
    {
        // Arrange
        using var fixture = new Fixture();
        Node[] nodes =
        [
            new(20, "Hidden", Directory: true, Attributes: 0x2),
            new(30, "inside", 20, 5),
            new(31, "plain", 5, 5),
            new(32, "unknown", 5, 5, Attributes: null),
            new(33, "system", 5, 5, Attributes: 0x6),
            new(34, "zone", 5, 5, NamedStream: true),
        ];
        fixture.Save(Now.AddDays(-2), nodes);
        fixture.Save(Now.AddDays(-1), nodes);
        var hideHidden = new FileVisibilityOptions(IncludeHidden: false);

        // Act
        using var hidden = Search(
            fixture,
            Everything with
            {
                Attributes = new(Hidden: AttributeRequirement.Set),
                Visibility = hideHidden,
            }
        );
        using var visible = Search(
            fixture,
            Everything with
            {
                Target = SearchTarget.Files,
                Visibility = hideHidden,
            }
        );
        using var streams = Search(
            fixture,
            Everything with
            {
                Attributes = new(NamedStreams: AttributeRequirement.Set),
            }
        );

        // Assert
        Assert.Equal(["Hidden", "system"], hidden.Entries(hidden.Root).Select(e => e.Name));
        Assert.Equal(1, hidden.UnknownAttributeCount);
        Assert.Equal(
            ["plain", "unknown", "zone"],
            visible.Entries(visible.Root).Select(e => e.Name)
        );
        Assert.Null(visible.Folder(Ref(20)));
        Assert.Equal(["zone"], streams.Entries(streams.Root).Select(e => e.Name));
    }

    [Theory]
    [InlineData(NameMatchMode.Contains, "REPORT", new[] { "report.pdf", "Report-2026.docx" })]
    [InlineData(NameMatchMode.Wildcard, "*.pdf", new[] { "report.pdf" })]
    [InlineData(NameMatchMode.RegularExpression, @"^report-\d+", new[] { "Report-2026.docx" })]
    public void NamesMatchWithoutRegardToCase(NameMatchMode mode, string pattern, string[] expected)
    {
        // Arrange
        using var fixture = new Fixture();
        Node[] nodes =
        [
            new(30, "report.pdf", 5, 1),
            new(31, "Report-2026.docx", 5, 1),
            new(32, "notes.txt", 5, 1),
        ];
        fixture.Save(Now.AddDays(-2), nodes);
        fixture.Save(Now.AddDays(-1), nodes);

        // Act
        using var result = Search(
            fixture,
            Everything with
            {
                NamePattern = pattern,
                NameMode = mode,
            }
        );

        // Assert
        Assert.Equal(expected.Order(), result.Entries(result.Root).Select(e => e.Name).Order());
    }

    [Fact]
    public void ExtensionsBaseFolderAndDepthRestrictPaths()
    {
        // Arrange
        using var fixture = new Fixture();
        Node[] nodes =
        [
            new(20, "Work", Directory: true),
            new(21, "Deep", 20, Directory: true),
            new(22, "Other", Directory: true),
            new(30, "a.tar.gz", 20, 1),
            new(31, "b.GZ", 21, 1),
            new(32, "c.gz", 22, 1),
            new(33, "gz", 20, 1),
        ];
        fixture.Save(Now.AddDays(-2), nodes);
        fixture.Save(Now.AddDays(-1), nodes);

        // Act
        using var underWork = Search(
            fixture,
            Everything with
            {
                Extensions = ["gz"],
                BaseFolder = "work\\",
            }
        );
        using var shallow = Search(
            fixture,
            Everything with
            {
                Extensions = ["gz"],
                BaseFolder = "Work",
                MaximumDepth = 2,
            }
        );

        // Assert
        Assert.Equal(2, underWork.MatchedFiles);
        Assert.Equal(
            ["a.tar.gz"],
            underWork.Entries(Ref(20)).Where(e => !e.IsDirectory).Select(e => e.Name)
        );
        Assert.Equal(["b.GZ"], underWork.Entries(Ref(21)).Select(e => e.Name));
        Assert.Equal(1, shallow.MatchedFiles);
        Assert.Null(shallow.Folder(Ref(21)));
    }

    [Fact]
    public void TrendsCompareConsecutiveKnownSizes()
    {
        // Arrange
        using var fixture = new Fixture();
        (string Name, long?[] Sizes)[] series =
        [
            ("rising", [1, 2, 3]),
            ("flat", [4, 4, 4]),
            ("falling", [5, 4, 3]),
            ("zigzag", [1, 3, 2]),
            ("gap-between-equal", [1, null, 1]),
            ("gap-rising", [1, null, 2]),
            ("unknown-last", [7, 7, null]),
        ];
        for (int scan = 0; scan < 3; scan++)
            fixture.Save(
                Now.AddDays(scan - 3),
                [.. series.Select((s, i) => new Node((ulong)(30 + i), s.Name, 5, s.Sizes[scan]))]
            );

        // Act
        using var increasing = Search(fixture, Everything with { Trend = SizeTrend.Increasing });
        using var decreasing = Search(fixture, Everything with { Trend = SizeTrend.Decreasing });

        // Assert
        var rising = increasing.Entries(increasing.Root).ToDictionary(e => e.Name);
        Assert.Equal(["gap-rising", "rising", "unknown-last"], rising.Keys);
        Assert.False(rising["rising"].Uncertain);
        Assert.True(rising["gap-rising"].Uncertain);
        Assert.True(rising["unknown-last"].Uncertain);
        Assert.Equal(
            ["falling", "unknown-last"],
            decreasing.Entries(decreasing.Root).Select(e => e.Name)
        );
    }

    [Fact]
    public void SeriesReportsEveryScanWithoutInventingZeroSizes()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Save(Now.AddDays(-3), [new(30, "first", 5, 10)]);
        fixture.Save(Now.AddDays(-2), [new(31, "other", 5, 1)]);
        fixture.Save(Now.AddDays(-1), [new(30, "first", 5, null)]);
        fixture.Save(Now.AddHours(-1), [new(30, "renamed", 5, 30)]);
        using var result = Search(fixture, Everything);
        var item = result.Entries(result.Root).Single(e => e.Reference == Ref(30));

        // Act
        var series = fixture.Archive.ReadSearchSeries(result, item, Token);

        // Assert
        Assert.Equal(
            [
                CapacityObservationState.Known,
                CapacityObservationState.NotObserved,
                CapacityObservationState.Unresolved,
                CapacityObservationState.Known,
            ],
            series.Select(o => o.State)
        );
        Assert.Equal((UInt128)10, series[0].AllocatedBytes);
        Assert.Null(series[1].AllocatedBytes);
        Assert.Equal("renamed", series[3].Name);
        Assert.Equal((UInt128)30, series[3].LogicalBytes);
    }

    [Fact]
    public void EmptyRangesAndSingleScansAreReportedInsteadOfSearched()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Save(Now.AddDays(-1), [new(30, "only", 5, 1)]);

        // Act
        using var none = Search(fixture, new(Now.AddDays(-30), Now.AddDays(-20)));
        using var single = Search(fixture, Everything with { MinimumDelta = 1 });
        using var values = Search(fixture, Everything with { MinimumSize = 1 });

        // Assert
        Assert.Equal(HistorySearchStatus.NoScans, none.Status);
        Assert.Equal(HistorySearchStatus.ChangesNeedTwoScans, single.Status);
        Assert.Empty(single.Entries(single.Root));
        Assert.Equal(["only"], values.Entries(values.Root).Select(e => e.Name));
    }

    [Fact]
    public void ContradictoryCriteriaAndCancellationAreRejected()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Save(Now.AddDays(-1), [new(30, "only", 5, 1)]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act and assert
        Assert.Throws<ArgumentException>(() =>
            Search(fixture, Everything with { MinimumSize = 2, MaximumSize = 1 })
        );
        Assert.Throws<ArgumentException>(() =>
            Search(
                fixture,
                Everything with
                {
                    NamePattern = "(",
                    NameMode = NameMatchMode.RegularExpression,
                }
            )
        );
        Assert.Throws<ArgumentException>(() =>
            Search(fixture, Everything with { MinimumDepth = 0 })
        );
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Archive.Search("volume:A", Everything, cancelled.Token)
        );
    }

    [Fact]
    public void ResultsLeaseTheirScansUntilDisposed()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Save(Now.AddDays(-6).AddHours(-12), [new(30, "file", 5, 1)]);
        fixture.Save(Now.AddDays(-6).AddHours(-11), [new(30, "file", 5, 2)]);
        var result = Search(fixture, Everything);
        var item = Assert.Single(result.Entries(result.Root));

        // Act: two days later the first scan of that day is retired, but still leased.
        fixture.Clock.Now = Now.AddDays(2);
        fixture.Archive.Maintain(Token);
        int listed = fixture.Archive.List("volume:A", Token).Length;
        var series = fixture.Archive.ReadSearchSeries(result, item, Token);
        result.Dispose();
        fixture.Archive.Maintain(Token);

        // Assert
        Assert.Equal(1, listed);
        Assert.All(series, o => Assert.Equal(CapacityObservationState.Known, o.State));
        using var after = Search(fixture, Everything);
        Assert.Single(after.Scans);
    }

    private static HistorySearchResult Search(Fixture fixture, HistorySearchCriteria criteria) =>
        fixture.Archive.Search("volume:A", criteria, Token);

    private static FileReference Ref(ulong index) => FileReference.Create(index, 1);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = HistorySearchTests.Now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } =
            Path.Combine(
                Path.GetTempPath(),
                "something-search-test-" + Guid.NewGuid().ToString("N")
            );
        public Clock Clock { get; } = new();
        public ScanHistoryArchive Archive { get; }

        public Fixture() => Archive = new(Root, Clock, TimeZoneInfo.Utc);

        public ScanHistoryEntry Save(DateTimeOffset ended, Node[] nodes)
        {
            var buffer = new BufferedScan(
                new("volume:A", Guid.NewGuid(), 1, new(0, 1000), ended.AddSeconds(-1))
            );
            var root = Ref(5);
            Add(
                new FileCapacityObservation(
                    root,
                    true,
                    CapacityDomain.Ordinary,
                    CapacityResolution.Resolved,
                    [new(root, "root", FileNameNamespace.Win32, 0)],
                    [],
                    true
                )
                {
                    StandardFileAttributes = 0x16,
                }
            );
            foreach (var node in nodes)
            {
                ImmutableArray<NtfsFileName> names =
                [
                    new(Ref(node.Parent), node.Name, FileNameNamespace.Win32, 0),
                    .. node.Link is { } link
                        ?
                        [
                            new NtfsFileName(
                                Ref(link.Parent),
                                link.Name,
                                FileNameNamespace.Win32,
                                0
                            ),
                        ]
                        : Array.Empty<NtfsFileName>(),
                ];
                bool resolved = node.Directory || node.Size is not null;
                ImmutableArray<StreamCapacity> streams =
                    node.Directory || !resolved
                        ? []
                        :
                        [
                            new("", node.Size!.Value, node.Size.Value),
                            .. node.NamedStream
                                ? [new StreamCapacity("Zone.Identifier", 26, 26)]
                                : Array.Empty<StreamCapacity>(),
                        ];
                Add(
                    new FileCapacityObservation(
                        Ref(node.Index),
                        node.Directory,
                        CapacityDomain.Ordinary,
                        resolved ? CapacityResolution.Resolved : CapacityResolution.Unresolved,
                        names,
                        streams,
                        true
                    )
                    {
                        StandardFileAttributes = node.Attributes,
                    }
                );
            }
            buffer.Complete(Token);
            return Archive.Publish(buffer, ended, "C:", TimeSpan.FromSeconds(1), 64UL << 20, Token);

            void Add(FileCapacityObservation file) =>
                buffer.Add(
                    new StagedObservation(
                        file.Reference.RecordIndex,
                        file.Resolution == CapacityResolution.Resolved
                            ? MftScanEntryKind.ResolvedFile
                            : MftScanEntryKind.Unresolved,
                        file.Resolution == CapacityResolution.Resolved
                            ? LiveFileReadStatus.ObservedUnchanged
                            : LiveFileReadStatus.Unsupported,
                        file
                    ),
                    Token
                );
        }

        public void Dispose()
        {
            string owned = Path.GetFullPath(Root);
            if (
                !Path.GetFileName(owned)
                    .StartsWith("something-search-test-", StringComparison.Ordinal)
            )
                throw new InvalidOperationException("Invalid cleanup path.");
            if (Directory.Exists(owned))
                Directory.Delete(owned, recursive: true);
        }
    }
}
