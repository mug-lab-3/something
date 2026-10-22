using System.Collections.Immutable;
using System.Numerics;
using Something.Application;
using Something.Application.History;
using Something.Application.Repositories;
using Something.Application.Scanning;
using Something.Application.Search;
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
        var result = fixture.Search(
            Everything with
            {
                Target = SearchTarget.Files,
                MinimumDelta = 500,
            }
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
        var result = fixture.Search(
            Everything with
            {
                Target = SearchTarget.Folders,
                MinimumSize = 1000,
                MinimumDepth = 3,
            }
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
        var all = Search(fixture, Everything with { Presence = SearchPresence.InAllScans });
        var appeared = Search(fixture, Everything with { Presence = SearchPresence.Appeared });
        var gone = Search(
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
        var large = Search(fixture, Everything with { MinimumSize = 500 });
        var rate = Search(fixture, Everything with { MinimumDeltaPercent = 10 });
        var delta = Search(fixture, Everything with { MinimumDelta = 10 });

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
        var result = Search(fixture, Everything with { Target = SearchTarget.Files });

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
    public void HardLinksInOneFolderStaySeparateNames()
    {
        // Arrange: both names share the record, its observation state and the parent.
        using var fixture = new Fixture();
        fixture.Save(Now.AddDays(-2), [new(30, "first", 5, 4, Link: (5, "second"))]);
        fixture.Save(Now.AddDays(-1), [new(30, "first", 5, 6, Link: (5, "second"))]);

        // Act
        var result = Search(fixture, Everything with { MinimumDelta = 2 });

        // Assert
        var names = result.Entries(result.Root);
        Assert.Equal(["first", "second"], names.Select(e => e.Name));
        Assert.All(names, e => Assert.Equal(new BigInteger(2), e.Delta));
        Assert.All(names, e => Assert.Equal(2, e.ObservedCount));
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
        var hidden = Search(
            fixture,
            Everything with
            {
                Attributes = new(Hidden: AttributeRequirement.Set),
                Visibility = hideHidden,
            }
        );
        var visible = Search(
            fixture,
            Everything with
            {
                Target = SearchTarget.Files,
                Visibility = hideHidden,
            }
        );
        var streams = Search(
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
        var result = Search(fixture, Everything with { NamePattern = pattern, NameMode = mode });

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
        var underWork = Search(
            fixture,
            Everything with
            {
                Extensions = ["gz"],
                BaseFolder = "work\\",
            }
        );
        var shallow = Search(
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
    public void AnItemThatCameLaterIsCountedFromZeroInFilesAndFolders()
    {
        // Arrange: a folder with a file at the start, a new file and a new folder with a file later
        using var fixture = new Fixture();
        fixture.Save(
            Now.AddDays(-2),
            [new(20, "Pics", Directory: true), new(30, "old.png", 20, 100)]
        );
        fixture.Save(
            Now.AddDays(-1),
            [
                new(20, "Pics", Directory: true),
                new(21, "New", 20, Directory: true),
                new(30, "old.png", 20, 100),
                new(31, "new.png", 20, 40),
                new(32, "inner.png", 21, 60),
            ]
        );

        // Act: everything that grew by at least 1 byte
        var result = Search(fixture, Everything with { MinimumDelta = 1 });

        // Assert: the new file and folder count from zero; the old file did not change
        var names = result.Entries(Ref(20)).Select(e => e.Name).Order().ToArray();
        Assert.Equal(["New", "new.png"], names);
        Assert.Equal(
            40UL,
            (ulong)result.Entries(Ref(20)).Single(e => e.Name == "new.png").Delta!.Value
        );
        Assert.Equal(60UL, (ulong)result.Folder(Ref(21))!.Delta!.Value);
        Assert.Equal(["inner.png"], result.Entries(Ref(21)).Select(e => e.Name));
        Assert.Equal(100UL, (ulong)result.Folder(Ref(20))!.Delta!.Value);
    }

    [Fact]
    public void SwingCountsZeroAtTheEndWhereAnItemWasMissing()
    {
        // Arrange: one file stays, one comes in the last scan, one goes after the first
        using var fixture = new Fixture();
        fixture.Save(
            Now.AddDays(-3),
            [
                new(20, "Dir", Directory: true),
                new(30, "stays.png", 20, 100),
                new(31, "gone.png", 20, 400),
            ]
        );
        fixture.Save(
            Now.AddDays(-2),
            [
                new(20, "Dir", Directory: true),
                new(30, "stays.png", 20, 100),
                new(32, "new.png", 20, 250),
            ]
        );
        fixture.Save(
            Now.AddDays(-1),
            [
                new(20, "Dir", Directory: true),
                new(30, "stays.png", 20, 100),
                new(32, "new.png", 20, 250),
            ]
        );

        // Act
        var result = Search(
            fixture,
            Everything with
            {
                MinimumSwing = 1,
                Target = SearchTarget.Files,
            }
        );

        // Assert: the unchanged file has no swing; the others swung between zero and their size
        Assert.Equal(["gone.png", "new.png"], result.Entries(Ref(20)).Select(e => e.Name).Order());
        Assert.Equal(
            [(UInt128)400, 250],
            result.Entries(Ref(20)).OrderBy(e => e.Name).Select(e => e.Swing!.Value)
        );
    }

    [Fact]
    public void AnItemThatWentAwayIsCountedDownToZeroInFilesAndFolders()
    {
        // Arrange: a folder with two files, one of which is deleted before the last scan
        using var fixture = new Fixture();
        fixture.Save(
            Now.AddDays(-2),
            [
                new(20, "Pics", Directory: true),
                new(30, "keep.png", 20, 100),
                new(31, "gone.png", 20, 400),
            ]
        );
        fixture.Save(
            Now.AddDays(-1),
            [new(20, "Pics", Directory: true), new(30, "keep.png", 20, 100)]
        );

        // Act: everything that shrank by at least 1 byte
        var result = Search(fixture, Everything with { MaximumDelta = -1 });

        // Assert: the deleted file lost its whole size, and so did the folder; the kept one did not
        var gone = result.Entries(Ref(20)).Single();
        Assert.Equal("gone.png", gone.Name);
        Assert.Equal(-400, gone.Delta);
        Assert.Equal(-400, result.Folder(Ref(20))!.Delta);
    }

    [Fact]
    public void ANameOnThePathSelectsEverythingUnderAMatchingFolder()
    {
        // Arrange
        using var fixture = new Fixture();
        Node[] nodes =
        [
            new(20, "Users", Directory: true),
            new(21, "mug", 20, Directory: true),
            new(22, "Docs", 21, Directory: true),
            new(23, "other", 20, Directory: true),
            new(30, "a.txt", 22, 1),
            new(31, "b.txt", 21, 1),
            new(32, "c.txt", 23, 1),
            new(33, "mug.log", 23, 1),
        ];
        fixture.Save(Now.AddDays(-2), nodes);
        fixture.Save(Now.AddDays(-1), nodes);

        // Act
        var byName = Search(fixture, Everything with { NamePattern = "mug" });
        var byPath = Search(
            fixture,
            Everything with
            {
                NamePattern = "mug",
                NameIncludesPath = true,
            }
        );

        // Assert: by name only the folder and the file called mug; by path all that lies below it
        Assert.Equal((1, 1), (byName.MatchedFolders, byName.MatchedFiles));
        Assert.Equal((2, 3), (byPath.MatchedFolders, byPath.MatchedFiles));
        Assert.Equal(
            ["a.txt"],
            byPath.Entries(Ref(22)).Where(e => !e.IsDirectory).Select(e => e.Name)
        );
        Assert.Null(byPath.Folder(Ref(23))?.Matched is true ? byPath.Folder(Ref(23)) : null);
    }

    [Fact]
    public void ANameOnThePathUsesThePathOfTheLastObservation()
    {
        // Arrange: the file moved out of the matching folder between the scans
        using var fixture = new Fixture();
        fixture.Save(
            Now.AddDays(-2),
            [
                new(21, "mug", Directory: true),
                new(22, "Other", Directory: true),
                new(30, "a.txt", 21, 1),
            ]
        );
        fixture.Save(
            Now.AddDays(-1),
            [
                new(21, "mug", Directory: true),
                new(22, "Other", Directory: true),
                new(30, "a.txt", 22, 1),
            ]
        );

        // Act
        var result = Search(
            fixture,
            Everything with
            {
                NamePattern = "mug",
                NameIncludesPath = true,
                Target = SearchTarget.Files,
            }
        );

        // Assert
        Assert.Equal(0, result.MatchedFiles);
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
        var increasing = Search(fixture, Everything with { Trend = SizeTrend.Increasing });
        var decreasing = Search(fixture, Everything with { Trend = SizeTrend.Decreasing });

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
    public void FolderTrendsFollowTheSubtreeSizesNotTheListings()
    {
        // Arrange: a folder's own listing entry has no size, so only its samples can decide.
        using var fixture = new Fixture();
        (string Name, long?[] Sizes)[] series =
        [
            ("rising", [1, 2, 3]),
            ("flat", [4, 4, 4]),
            ("falling", [5, 4, 3]),
            ("unresolved", [1, null, 2]),
        ];
        for (int scan = 0; scan < 3; scan++)
            fixture.Save(
                Now.AddDays(scan - 3),
                [
                    .. series.SelectMany(
                        (s, i) =>
                            new Node[]
                            {
                                new((ulong)(20 + i), s.Name, 5, Directory: true),
                                new((ulong)(40 + i), "content", (ulong)(20 + i), s.Sizes[scan]),
                            }
                    ),
                ]
            );
        var folders = Everything with { Target = SearchTarget.Folders };

        // Act
        var increasing = Search(fixture, folders with { Trend = SizeTrend.Increasing });
        var decreasing = Search(fixture, folders with { Trend = SizeTrend.Decreasing });

        // Assert
        var rising = increasing.Entries(increasing.Root).ToDictionary(e => e.Name);
        Assert.Equal(["rising", "unresolved"], rising.Keys);
        Assert.False(rising["rising"].Uncertain);
        Assert.True(rising["unresolved"].Uncertain);
        // Known sizes of "unresolved" rise, which rules a decrease out.
        Assert.Equal(["falling"], decreasing.Entries(decreasing.Root).Select(e => e.Name));
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
        var search = fixture.Open(Everything);
        var item = search.Result.Entries(search.Result.Root).Single(e => e.Reference == Ref(30));

        // Act
        var series = search.ReadSeries(item, Token);

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
        var none = Search(fixture, new(Now.AddDays(-30), Now.AddDays(-20)));
        var single = Search(fixture, Everything with { MinimumDelta = 1 });
        var values = Search(fixture, Everything with { MinimumSize = 1 });

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
        var search = fixture.Open(Everything);
        var item = Assert.Single(search.Result.Entries(search.Result.Root));

        // Act: two days later the first scan of that day is retired, but still leased.
        fixture.Clock.Now = Now.AddDays(2);
        fixture.Archive.Maintain(Token);
        int listed = fixture.Archive.List("volume:A", Token).Length;
        var series = search.ReadSeries(item, Token);
        search.Dispose();
        fixture.Archive.Maintain(Token);

        // Assert
        Assert.Equal(1, listed);
        Assert.All(series, o => Assert.Equal(CapacityObservationState.Known, o.State));
        var after = Search(fixture, Everything);
        Assert.Single(after.Scans);
    }

    [Fact]
    public void CriteriaCheckReportsEveryIssueForTheScreen()
    {
        // Arrange
        var broken = new HistorySearchCriteria(Now, Now.AddDays(-1))
        {
            NamePattern = @"(a)\1",
            NameMode = NameMatchMode.RegularExpression,
            Extensions = ["", "a\\b"],
            MinimumDepth = 3,
            MaximumDepth = 2,
            MinimumSize = 2,
            MaximumSize = 1,
            MinimumDelta = 5,
            MaximumDelta = -5,
            MinimumDeltaPercent = double.NaN,
        };
        var changes = Everything with { Trend = SizeTrend.Increasing };

        // Act
        var all = HistorySearchCriteriaCheck.Check(broken, null);
        var empty = HistorySearchCriteriaCheck.Check(Everything, new(0, null, null));
        var single = HistorySearchCriteriaCheck.Check(changes, new(1, null, null));
        var fine = HistorySearchCriteriaCheck.Check(changes, new(2, null, null));

        // Assert
        Assert.Equal(
            [
                SearchCriteriaIssue.RangeReversed,
                SearchCriteriaIssue.InvalidRegularExpression,
                SearchCriteriaIssue.InvalidExtension,
                SearchCriteriaIssue.DepthRangeInvalid,
                SearchCriteriaIssue.SizeRangeReversed,
                SearchCriteriaIssue.DeltaRangeReversed,
                SearchCriteriaIssue.RateRangeInvalid,
            ],
            all
        );
        Assert.Equal([SearchCriteriaIssue.NoScansInRange], empty);
        Assert.Equal([SearchCriteriaIssue.ChangesNeedTwoScans], single);
        Assert.Empty(fine);
        Assert.Throws<ArgumentException>(() =>
            (broken with { StartUtc = Now.AddDays(-2) }).Validate()
        );
    }

    [Fact]
    public async Task ServicePreviewsTheRangeAndSummarizesOnlySpecifiedConditions()
    {
        // Arrange
        using var fixture = new Fixture();
        var first = fixture.Save(Now.AddDays(-2), [new(30, "a.log", 5, 1)]);
        var last = fixture.Save(Now.AddDays(-1), [new(30, "a.log", 5, 3)]);
        var budget = new ScanMemoryBudget(() => new(16UL << 30, 8UL << 30, 8UL << 30, 8UL << 30));
        var service = new HistorySearchService(TestHistory.For(fixture.Archive), budget);
        var criteria = Everything with
        {
            Extensions = ["log"],
            Attributes = new(Hidden: AttributeRequirement.Clear),
            MinimumDelta = 1,
            SizeKind = SearchSizeKind.Logical,
            MinimumSize = 1,
        };

        // Act
        var preview = await service.PreviewAsync("volume:A", Now.AddDays(-30), Now, Token);
        await using var search = await service.SearchAsync("volume:A", criteria, Token);
        var result = search.Result;
        var series = await search.ReadSeriesAsync(
            Assert.Single(result.Entries(result.Root)),
            Token
        );
        var parts = HistorySearchSummary.Describe(result);

        // Assert
        Assert.Equal(
            (2, first.Id, last.Id),
            (preview.ScanCount, preview.First!.Id, preview.Last!.Id)
        );
        Assert.Equal(2, series.Length);
        Assert.Equal(
            [
                new SearchRangePart(first.CompletedUtc, last.CompletedUtc, 2),
                new SearchExtensionsPart(["log"]),
                new SearchAttributePart(SearchAttributeKind.Hidden, AttributeRequirement.Clear),
                new SearchSizePart(SearchSizeKind.Logical, 1, null),
                new SearchDeltaPart(1, null),
            ],
            parts,
            (left, right) =>
                left is SearchExtensionsPart a && right is SearchExtensionsPart b
                    ? a.Extensions.SequenceEqual(b.Extensions)
                    : Equals(left, right)
        );
    }

    private static HistorySearchResult Search(Fixture fixture, HistorySearchCriteria criteria) =>
        fixture.Search(criteria);

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

        private readonly List<IHistorySearchSession> sessions = [];

        public Fixture() => Archive = new(Root, Clock, TimeZoneInfo.Utc);

        /// <summary>The result of a search of volume A; the session is released with the fixture.</summary>
        public HistorySearchResult Search(HistorySearchCriteria criteria) => Open(criteria).Result;

        public IHistorySearchSession Open(HistorySearchCriteria criteria)
        {
            var session = Archive.Search("volume:A", criteria, Token);
            sessions.Add(session);
            return session;
        }

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
            return Archive.Publish(
                buffer.Complete(TestContext.Current.CancellationToken),
                ended,
                "C:",
                TimeSpan.FromSeconds(1),
                64UL << 20,
                Token
            );

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
            foreach (var session in sessions)
                session.Dispose();
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
