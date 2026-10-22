using System.Collections.Immutable;
using Something.Application;
using Something.Application.Repositories;
using Something.Application.Search;

namespace Something.Ntfs.Tests;

/// <summary>Search results built from items, and the search session wrapper, without a database.</summary>
public sealed class HistorySearchResultTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly HistorySearchCriteria Everything = new(Now.AddDays(-30), Now);
    private static readonly FileReference Root = FileReference.Create(5, 5);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static FileReference Ref(ulong index) => FileReference.Create(index, 1);

    private static SearchItem Folder(
        ulong index,
        string name,
        FileReference? parent = null,
        bool matched = false,
        bool uncertain = false
    ) =>
        new(Ref(index), parent ?? Root, name, true, 0, 0, 0, 1, null, null, null, null, false)
        {
            Matched = matched,
            Uncertain = uncertain,
        };

    private static SearchItem File(
        ulong index,
        string name,
        FileReference? parent = null,
        bool uncertain = false
    ) =>
        new(Ref(index), parent ?? Root, name, false, 0, 0, 0, 1, 1, 1, 1, 1, false)
        {
            Matched = true,
            Uncertain = uncertain,
        };

    private static HistorySearchResult Result(
        IEnumerable<SearchItem> folders,
        IEnumerable<SearchItem> files,
        long unknown = 0
    ) =>
        new(
            "volume:A",
            Everything,
            HistorySearchStatus.Completed,
            [],
            Root,
            folders,
            files,
            unknown
        );

    [Fact]
    public void EntriesListFoldersFirstThenFilesByName()
    {
        // Arrange
        var result = Result(
            [Folder(20, "beta"), Folder(21, "Alpha"), Folder(22, "alpha")],
            [File(30, "b.txt"), File(31, "A.txt"), File(32, "a.txt", Ref(20))]
        );

        // Act
        string[] names = [.. result.Entries(Root).Select(item => item.Name)];
        string[] folders = [.. result.ChildFolders(Root).Select(item => item.Name)];
        var inside = Assert.Single(result.Entries(Ref(20)));

        // Assert
        Assert.Equal(["Alpha", "alpha", "beta", "A.txt", "b.txt"], names);
        Assert.Equal(["Alpha", "alpha", "beta"], folders);
        Assert.Equal("a.txt", inside.Name);
        Assert.Empty(result.Entries(Ref(30)));
    }

    [Fact]
    public void PathsFollowTheFoldersUpToTheRoot()
    {
        // Arrange
        var result = Result(
            [Folder(20, "Data"), Folder(21, "Logs", Ref(20)), Folder(22, "Old", Ref(21))],
            []
        );

        // Act
        string[] paths = [result.PathOf(Root), result.PathOf(Ref(20)), result.PathOf(Ref(22))];

        // Assert
        Assert.Equal(["", "Data", "Data\\Logs\\Old"], paths);
        Assert.Equal("Logs", result.Folder(Ref(21))!.Name);
        Assert.Null(result.Folder(Ref(99)));
    }

    [Fact]
    public void CountsIncludeOnlyMatchedItems()
    {
        // Arrange: an unmatched folder that leads to matches is never counted, even if uncertain.
        var result = Result(
            [
                Folder(20, "lead", uncertain: true),
                Folder(21, "hit", Ref(20), matched: true),
                Folder(22, "doubt", Ref(20), matched: true, uncertain: true),
            ],
            [File(30, "a", Ref(21)), File(31, "b", Ref(21), uncertain: true)],
            unknown: 4
        );

        // Act
        long[] counts =
        [
            result.MatchedFolders,
            result.MatchedFiles,
            result.UncertainCount,
            result.UnknownAttributeCount,
        ];

        // Assert
        Assert.Equal([2L, 2L, 2L, 4L], counts);
    }

    public static TheoryData<string, SearchItem[], SearchItem[]> Malformed =>
        new()
        {
            { "duplicate folder", [Folder(20, "a"), Folder(20, "b")], [] },
            { "missing parent", [Folder(20, "a", Ref(21))], [] },
            { "cycle", [Folder(20, "a", Ref(21)), Folder(21, "b", Ref(20))], [] },
            { "self parent", [Folder(20, "a", Ref(20))], [] },
            { "root as folder", [Folder(5, "root") with { Reference = Root }], [] },
            { "file as folder", [File(30, "a")], [] },
            { "file outside", [], [File(30, "a", Ref(20))] },
            { "folder as file", [], [Folder(20, "a", matched: true)] },
            { "unmatched file", [], [File(30, "a") with { Matched = false }] },
        };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedTreesAreRejected(string reason, SearchItem[] folders, SearchItem[] files)
    {
        // Act
        var error = Record.Exception(() => Result(folders, files));

        // Assert
        Assert.True(error is ArgumentException, reason);
    }

    [Fact]
    public void InvalidArgumentsAreRejected()
    {
        // Act and assert
        Assert.Throws<ArgumentOutOfRangeException>(() => Result([], [], unknown: -1));
        Assert.Throws<ArgumentException>(() =>
            new HistorySearchResult(
                " ",
                Everything,
                HistorySearchStatus.Completed,
                [],
                Root,
                [],
                [],
                0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HistorySearchResult.Unsearched(
                "volume:A",
                Everything,
                HistorySearchStatus.Completed,
                []
            )
        );
    }

    [Fact]
    public void AnUnsearchedResultIsEmpty()
    {
        // Act
        var result = HistorySearchResult.Unsearched(
            "volume:A",
            Everything,
            HistorySearchStatus.NoScans,
            default
        );

        // Assert
        Assert.Equal(HistorySearchStatus.NoScans, result.Status);
        Assert.Empty(result.Scans);
        Assert.Empty(result.Entries(result.Root));
        Assert.Equal(0, result.MatchedFolders + result.MatchedFiles + result.UncertainCount);
    }

    // A file with a size, and a change when it was in the first scan of the range.
    private static SearchItem Sized(
        ulong index,
        string name,
        FileReference parent,
        ulong? size,
        ulong? first = null
    ) =>
        new(Ref(index), parent, name, false, 0, 0, 0, 1, first, size, size, size, false)
        {
            Matched = true,
        };

    [Fact]
    public void AFolderOnTheWayToMatchesShowsTheSumOfWhatMatchedBelowIt()
    {
        // Arrange: Data holds a.bin (100, was 40) and Logs; Logs holds b.bin (50, was 70).
        var result = Result(
            [Folder(20, "Data"), Folder(21, "Logs", Ref(20))],
            [
                Sized(30, "a.bin", Ref(20), 100, 40),
                Sized(31, "b.bin", Ref(21), 50, 70),
                Sized(32, "c.bin", Root, 9, 9),
            ]
        );

        // Act
        var data = result.ValuesOf(result.Folder(Ref(20))!);
        var logs = result.ValuesOf(result.Folder(Ref(21))!);

        // Assert
        Assert.Equal(new SearchValues(150, 40, false, false), data);
        Assert.Equal(new SearchValues(50, -20, false, false), logs);
    }

    [Fact]
    public void AMatchedFolderShowsItsWholeSizeAndIsCountedOnceAboveIt()
    {
        // Arrange: Data (not matched) holds Big, a matched folder of 1000 whose matched file x is 10.
        var big = Folder(21, "Big", Ref(20), matched: true) with
        {
            Size = 1000,
            FirstSize = 600,
        };
        var result = Result(
            [Folder(20, "Data"), big],
            [Sized(30, "x.bin", Ref(21), 10, 10), Sized(31, "y.bin", Ref(20), 5, 5)]
        );

        // Act
        var data = result.ValuesOf(result.Folder(Ref(20))!);
        var own = result.ValuesOf(result.Folder(Ref(21))!);

        // Assert: Big counts once, with its whole size; x inside it is not added again.
        Assert.Equal(new SearchValues(1005, 400, false, false), data);
        Assert.Equal(new SearchValues(1000, 400, false, false), own);
    }

    [Fact]
    public void AnUnmatchedFolderInsideAMatchedFolderSumsItsOwnMatches()
    {
        // Arrange: Outer is matched; Inner is only on the way to z.bin inside it.
        var result = Result(
            [Folder(20, "Outer", matched: true), Folder(21, "Inner", Ref(20))],
            [Sized(30, "z.bin", Ref(21), 7, 7)]
        );

        // Act
        var inner = result.ValuesOf(result.Folder(Ref(21))!);

        // Assert
        Assert.Equal(new SearchValues(7, 0, false, false), inner);
    }

    [Fact]
    public void UnknownValuesMarkASumIncompleteWithoutBeingTakenAsZero()
    {
        // Arrange: one file with no known change (it appeared later), one without a size.
        var result = Result(
            [Folder(20, "Data"), Folder(21, "Empty", Ref(20)), Folder(22, "Unknown", Root)],
            [
                Sized(30, "new.bin", Ref(20), 100),
                Sized(31, "a.bin", Ref(20), 10, 4),
                Sized(32, "gone.bin", Ref(22), null),
            ]
        );

        // Act
        var data = result.ValuesOf(result.Folder(Ref(20))!);
        var unknown = result.ValuesOf(result.Folder(Ref(22))!);
        var empty = result.ValuesOf(result.Folder(Ref(21))!);

        // Assert
        Assert.Equal(new SearchValues(110, 6, false, true), data);
        Assert.Equal(new SearchValues(null, null, true, true), unknown);
        Assert.Equal(new SearchValues(null, null, false, false), empty);
    }

    [Fact]
    public void FilesKeepTheirOwnValuesAndUncertaintyMarksThemIncomplete()
    {
        // Arrange
        var file = Sized(30, "a.bin", Root, 100, 40) with
        {
            Uncertain = true,
        };
        var result = Result([], [file]);

        // Act
        var values = result.ValuesOf(file);

        // Assert
        Assert.Equal(new SearchValues(100, 60, true, true), values);
        Assert.Throws<ArgumentNullException>(() => result.ValuesOf(null!));
    }

    [Fact]
    public void ADeepTreeIsSummedWithoutRecursion()
    {
        // Arrange: a chain of 20000 folders with one file at the bottom.
        const int Depth = 20_000;
        var folders = Enumerable
            .Range(0, Depth)
            .Select(i => Folder((ulong)(1000 + i), $"d{i}", i == 0 ? Root : Ref((ulong)(999 + i))));
        var result = Result(folders, [Sized(1, "f.bin", Ref((ulong)(999 + Depth)), 5, 5)]);

        // Act
        var top = result.ValuesOf(result.Folder(Ref(1000))!);

        // Assert
        Assert.Equal(new SearchValues(5, 0, false, false), top);
    }

    [Fact]
    public async Task SeriesReadsRunOneAtATime()
    {
        // Arrange
        var session = new FakeSession(Result([], [File(30, "a")]));
        await using var search = new HistorySearch(session);
        var item = Assert.Single(search.Result.Entries(Root));

        // Act
        var first = search.ReadSeriesAsync(item, Token);
        session.Entered.Wait(Token);
        var second = search.ReadSeriesAsync(item, Token);
        bool waited = !second.IsCompleted && session.Calls == 1;
        session.Proceed.Set();
        await Task.WhenAll(first, second);

        // Assert
        Assert.True(waited);
        Assert.Equal((2, 1), (session.Calls, session.MostAtOnce));
        Assert.Equal(CapacityObservationState.Known, Assert.Single(await first).State);
    }

    [Fact]
    public async Task DisposingReleasesTheSessionOnceAndStopsReads()
    {
        // Arrange
        var session = new FakeSession(Result([], [File(30, "a")])) { Proceeding = true };
        var search = new HistorySearch(session);
        var item = Assert.Single(search.Result.Entries(Root));

        // Act
        await search.DisposeAsync();
        await search.DisposeAsync();

        // Assert
        Assert.Equal(1, session.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            search.ReadSeriesAsync(item, Token)
        );
        Assert.Equal(0, session.Calls);
    }

    [Fact]
    public async Task ACancelledReadNeverReachesTheSession()
    {
        // Arrange
        var session = new FakeSession(Result([], [File(30, "a")])) { Proceeding = true };
        await using var search = new HistorySearch(session);
        var item = Assert.Single(search.Result.Entries(Root));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Act and assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            search.ReadSeriesAsync(item, cancelled.Token)
        );
        Assert.Equal(0, session.Calls);
    }

    /// <summary>Reports one known size per call; blocks each call until <see cref="Proceed"/> is set.</summary>
    private sealed class FakeSession(HistorySearchResult result) : IHistorySearchSession
    {
        private int active;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Proceed { get; } = new();
        public bool Proceeding
        {
            init
            {
                if (value)
                    Proceed.Set();
            }
        }
        public int Calls { get; private set; }
        public int MostAtOnce { get; private set; }
        public int Disposals { get; private set; }
        public HistorySearchResult Result => result;

        public ImmutableArray<SearchSizeObservation> ReadSeries(
            SearchItem item,
            CancellationToken token
        )
        {
            int now = Interlocked.Increment(ref active);
            MostAtOnce = Math.Max(MostAtOnce, now);
            Calls++;
            Entered.Set();
            Proceed.Wait(token);
            Interlocked.Decrement(ref active);
            return [new(0, item.Parent, item.Name, CapacityObservationState.Known, 1, 1)];
        }

        public void Dispose() => Disposals++;
    }
}
