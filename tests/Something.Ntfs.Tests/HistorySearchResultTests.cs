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
