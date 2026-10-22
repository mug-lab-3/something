using System.Numerics;
using Something.Application;
using Something.Application.Search;

namespace Something.Ntfs.Tests;

/// <summary>Browsing one search result: folder, history and list order, without a window.</summary>
public sealed class SearchResultBrowserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly FileReference Root = FileReference.Create(5, 5);

    private static FileReference Ref(ulong index) => FileReference.Create(index, 1);

    private static SearchItem Folder(ulong index, string name, FileReference? parent = null) =>
        new(Ref(index), parent ?? Root, name, true, 0, 0, 0, 1, null, null, null, null, false)
        {
            Matched = true,
        };

    private static SearchItem File(
        ulong index,
        string name,
        FileReference? parent = null,
        ulong? size = 1,
        ulong? first = 1
    ) =>
        new(Ref(index), parent ?? Root, name, false, 0, 0, 0, 1, first, size, size, size, false)
        {
            Matched = true,
        };

    private static SearchResultBrowser Browser(
        IEnumerable<SearchItem> folders,
        IEnumerable<SearchItem> files
    ) =>
        new(
            new(
                "volume:A",
                new HistorySearchCriteria(Now.AddDays(-1), Now),
                HistorySearchStatus.Completed,
                [],
                Root,
                folders,
                files,
                0
            )
        );

    /// <summary>Root: folders "beta" and "Alpha"; files; "Alpha" holds "Sub" and a file.</summary>
    private static SearchResultBrowser Sample() =>
        Browser(
            [Folder(20, "beta"), Folder(21, "Alpha"), Folder(22, "Sub", Ref(21))],
            [
                File(30, "b.txt", size: 300, first: 100),
                File(31, "A.txt", size: 50, first: 80),
                File(32, "unknown.bin", size: null, first: null),
                File(33, "inner.txt", Ref(21), size: 5, first: 5),
            ]
        );

    private static string[] Names(SearchResultBrowser browser) =>
        [.. browser.Entries.Select(item => item.Name)];

    [Fact]
    public void ItStartsAtTheRootWithNoHistory()
    {
        // Act
        var browser = Sample();

        // Assert
        Assert.Equal(Root, browser.Current);
        Assert.Equal("", browser.Path);
        Assert.False(browser.CanGoBack);
        Assert.False(browser.CanGoForward);
        Assert.False(browser.CanGoUp);
        Assert.Null(browser.ParentOf(Root));
        Assert.Equal(["Alpha", "beta", "A.txt", "b.txt", "unknown.bin"], Names(browser));
    }

    [Fact]
    public void FoldersComeFirstThenFilesByNameWithoutRegardToCase()
    {
        // Arrange
        var browser = Browser(
            [Folder(20, "b"), Folder(21, "A")],
            [File(30, "z"), File(31, "Y"), File(32, "x")]
        );

        // Assert
        Assert.Equal(["A", "b", "x", "Y", "z"], Names(browser));
        browser.Sort(SearchColumn.Name);
        Assert.Equal(["b", "A", "z", "Y", "x"], Names(browser));
    }

    [Fact]
    public void OpeningAFolderListsItsEntriesAndKeepsTheHistory()
    {
        // Arrange
        var browser = Sample();

        // Act
        browser.Open(Ref(21));
        var inside = Names(browser);
        browser.Open(Ref(22));

        // Assert
        Assert.Equal(["Sub", "inner.txt"], inside);
        Assert.Equal("Alpha\\Sub", browser.Path);
        Assert.Empty(browser.Entries);
        Assert.Equal(Ref(21), browser.ParentOf(Ref(22)));
        Assert.True(browser.CanGoBack);
        Assert.True(browser.CanGoUp);
    }

    [Fact]
    public void BackForwardAndUpFollowTheOrderTheFoldersWereOpened()
    {
        // Arrange
        var browser = Sample();
        browser.Open(Ref(21));
        browser.Open(Ref(22));

        // Act and assert
        Assert.True(browser.GoBack());
        Assert.Equal(Ref(21), browser.Current);
        Assert.True(browser.CanGoForward);
        Assert.True(browser.GoUp());
        Assert.Equal(Root, browser.Current);
        Assert.False(browser.CanGoUp);
        Assert.False(browser.GoUp());
        Assert.True(browser.GoBack());
        Assert.Equal(Ref(21), browser.Current);
        Assert.True(browser.GoBack());
        Assert.Equal(Root, browser.Current);
        Assert.False(browser.GoBack());
        // The history reads Root, Alpha, Root: going forward twice ends at the last Root.
        Assert.True(browser.GoForward());
        Assert.Equal(Ref(21), browser.Current);
        Assert.True(browser.GoForward());
        Assert.Equal(Root, browser.Current);
        Assert.False(browser.GoForward());
    }

    [Fact]
    public void OpeningAnotherFolderDropsTheForwardHistory()
    {
        // Arrange
        var browser = Sample();
        browser.Open(Ref(21));
        browser.GoBack();

        // Act
        browser.Open(Ref(20));

        // Assert
        Assert.False(browser.CanGoForward);
        Assert.False(browser.GoForward());
        Assert.Equal(Ref(20), browser.Current);
    }

    [Fact]
    public void OpeningTheCurrentFolderAgainChangesNothing()
    {
        // Arrange
        var browser = Sample();
        browser.Open(Ref(21));
        browser.Open(Ref(21));
        browser.Open(Root);
        browser.Open(Root);

        // Act
        browser.GoBack();

        // Assert: Root -> Alpha -> Root, so going back once returns to Alpha.
        Assert.Equal(Ref(21), browser.Current);
    }

    [Fact]
    public void FoldersOutsideTheResultCannotBeOpened()
    {
        // Arrange
        var browser = Sample();

        // Act and assert
        Assert.Throws<ArgumentException>(() => browser.Open(Ref(999)));
        Assert.False(browser.Contains(Ref(999)));
        Assert.True(browser.Contains(Root));
        Assert.True(browser.Contains(Ref(22)));
        Assert.Equal(Root, browser.Current);
    }

    [Fact]
    public void TheTreeListsTheFoldersOfAFolderInTheFixedOrder()
    {
        // Arrange
        var browser = Sample();

        // Act
        string[] root = [.. browser.TreeChildren(Root).Select(item => item.Name)];
        string[] alpha = [.. browser.TreeChildren(Ref(21)).Select(item => item.Name)];

        // Assert
        Assert.Equal(["Alpha", "beta"], root);
        Assert.Equal(["Sub"], alpha);
        Assert.Empty(browser.TreeChildren(Ref(22)));
    }

    [Fact]
    public void SizeSortsLargestFirstAndUnknownSizesComeLastInBothDirections()
    {
        // Arrange
        var browser = Sample();

        // Act
        browser.Sort(SearchColumn.Size);
        string[] descending = Names(browser);
        browser.Sort(SearchColumn.Size);
        string[] ascending = Names(browser);

        // Assert
        Assert.Equal(["Alpha", "beta", "b.txt", "A.txt", "unknown.bin"], descending);
        Assert.Equal(["Alpha", "beta", "A.txt", "b.txt", "unknown.bin"], ascending);
        Assert.True(browser.Descending == false);
    }

    [Fact]
    public void ChangeSortsByTheDifferenceAndPutsMissingChangesLast()
    {
        // Arrange: b.txt grew by 200, A.txt by -30; unknown.bin has no change.
        var browser = Sample();

        // Act
        browser.Sort(SearchColumn.Delta);
        string[] descending = Names(browser);
        browser.Sort(SearchColumn.Delta);
        string[] ascending = Names(browser);

        // Assert
        Assert.Equal(["Alpha", "beta", "b.txt", "A.txt", "unknown.bin"], descending);
        Assert.Equal(["Alpha", "beta", "A.txt", "b.txt", "unknown.bin"], ascending);
        Assert.Equal(
            new BigInteger(200),
            browser.Entries.Single(item => item.Name == "b.txt").Delta
        );
    }

    [Fact]
    public void FoldersSortByTheSumOfWhatMatchedBelowThem()
    {
        // Arrange: A holds a small match, B a large one; the folders' own sizes are unknown.
        var browser = Browser(
            [Folder(20, "A") with { Matched = false }, Folder(21, "B") with { Matched = false }],
            [
                File(30, "a.bin", Ref(20), size: 5, first: 5),
                File(31, "b.bin", Ref(21), size: 900, first: 100),
            ]
        );

        // Act
        browser.Sort(SearchColumn.Size);
        string[] bySize = Names(browser);
        browser.Sort(SearchColumn.Delta);
        string[] byChange = Names(browser);
        browser.Sort(SearchColumn.Delta);
        string[] reversed = Names(browser);

        // Assert
        Assert.Equal(["B", "A"], bySize);
        Assert.Equal(["B", "A"], byChange);
        Assert.Equal(["A", "B"], reversed);
    }

    [Fact]
    public void ANewColumnStartsWithItsNaturalOrderAndTheSameColumnReverses()
    {
        // Arrange
        var browser = Sample();

        // Act and assert
        Assert.Equal((SearchColumn.Name, false), (browser.SortColumn, browser.Descending));
        browser.Sort(SearchColumn.Size);
        Assert.Equal((SearchColumn.Size, true), (browser.SortColumn, browser.Descending));
        browser.Sort(SearchColumn.Name);
        Assert.Equal((SearchColumn.Name, false), (browser.SortColumn, browser.Descending));
        browser.Sort(SearchColumn.Name);
        Assert.True(browser.Descending);
        Assert.Throws<ArgumentOutOfRangeException>(() => browser.Sort((SearchColumn)99));
    }

    [Fact]
    public void TheOrderIsKeptWhenAnotherFolderIsOpened()
    {
        // Arrange
        var browser = Browser(
            [Folder(20, "d")],
            [File(30, "small", Ref(20), size: 1), File(31, "big", Ref(20), size: 9)]
        );
        browser.Sort(SearchColumn.Size);

        // Act
        browser.Open(Ref(20));

        // Assert
        Assert.Equal(["big", "small"], Names(browser));
    }

    [Fact]
    public void PagesCoverEveryEntryOnceAndTellWhetherMoreFollow()
    {
        // Arrange
        int count = SearchResultBrowser.PageSize * 2 + 7;
        var browser = Browser(
            [],
            Enumerable.Range(0, count).Select(i => File((ulong)(100 + i), $"f{i:D4}"))
        );

        // Act
        var first = browser.Page(0, out bool more0);
        var second = browser.Page(1, out bool more1);
        var third = browser.Page(2, out bool more2);
        var beyond = browser.Page(3, out bool more3);

        // Assert
        Assert.Equal(count, browser.Count);
        Assert.Equal(
            (SearchResultBrowser.PageSize, SearchResultBrowser.PageSize, 7, 0),
            (first.Length, second.Length, third.Length, beyond.Length)
        );
        Assert.Equal((true, true, false, false), (more0, more1, more2, more3));
        Assert.Equal(browser.Entries, first.Concat(second).Concat(third));
        Assert.Throws<ArgumentOutOfRangeException>(() => browser.Page(-1, out _));
    }

    [Fact]
    public void AnEmptyResultHasOnlyTheRoot()
    {
        // Arrange
        var browser = Browser([], []);

        // Assert
        Assert.Empty(browser.Entries);
        Assert.Equal(0, browser.Count);
        Assert.Empty(browser.Page(0, out bool more));
        Assert.False(more);
        Assert.Empty(browser.TreeChildren(Root));
    }

    [Fact]
    public void ARootWithoutScansIsStillBrowsable()
    {
        // Arrange
        var result = HistorySearchResult.Unsearched(
            "volume:A",
            new HistorySearchCriteria(Now.AddDays(-1), Now),
            HistorySearchStatus.NoScans,
            []
        );

        // Act
        var browser = new SearchResultBrowser(result);

        // Assert
        Assert.Empty(browser.Entries);
        Assert.False(browser.CanGoUp);
        Assert.True(browser.Contains(browser.Current));
    }
}
