using System.Collections.Immutable;
using Something.Application;
using Something.Application.History;
using Something.Application.Search;

namespace Something.Ntfs.Tests;

/// <summary>The points and axes of a size graph, without a window.</summary>
public sealed class SearchSizeSeriesTests
{
    private const ulong KiB = 1UL << 10;
    private const ulong MiB = 1UL << 20;
    private const ulong GiB = 1UL << 30;
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly FileReference Root = FileReference.Create(5, 5);

    private static FileReference Ref(ulong index) => FileReference.Create(index, 1);

    private static ScanHistoryEntry Scan(int daysAgo) =>
        new(
            Guid.NewGuid(),
            "volume:A",
            "A:",
            Now.AddDays(-daysAgo),
            DateOnly.FromDateTime(Now.AddDays(-daysAgo).UtcDateTime),
            0,
            TimeSpan.FromSeconds(1)
        );

    private static SearchItem Folder(ulong index, string name, FileReference parent) =>
        new(Ref(index), parent, name, true, 0, 0, 0, 1, null, null, null, null, false)
        {
            Matched = true,
        };

    private static HistorySearchResult Result(int scans, params SearchItem[] folders) =>
        new(
            "volume:A",
            new HistorySearchCriteria(Now.AddDays(-30), Now),
            HistorySearchStatus.Completed,
            [.. Enumerable.Range(0, scans).Select(i => Scan(scans - i))],
            Root,
            folders,
            [],
            0
        );

    private static SearchSizeObservation Known(
        int index,
        FileReference parent,
        string name,
        ulong allocated,
        ulong logical
    ) => new(index, parent, name, CapacityObservationState.Known, allocated, logical);

    [Fact]
    public void KnownSizesFollowTheChosenKindAndNothingIsInventedForTheRest()
    {
        // Arrange
        var result = Result(3);
        ImmutableArray<SearchSizeObservation> series =
        [
            Known(0, Root, "a.bin", 4096, 100),
            new(1, Root, "a.bin", CapacityObservationState.Unresolved, null, null),
            new(2, null, null, CapacityObservationState.NotObserved, null, null),
        ];

        // Act
        var allocated = SearchSizeSeries.Build(result, series, SearchSizeKind.Allocated);
        var logical = SearchSizeSeries.Build(result, series, SearchSizeKind.Logical);

        // Assert
        Assert.Equal([(UInt128)4096, null, null], allocated.Select(point => point.Bytes));
        Assert.Equal([(UInt128)100, null, null], logical.Select(point => point.Bytes));
        Assert.Equal(
            [
                CapacityObservationState.Known,
                CapacityObservationState.Unresolved,
                CapacityObservationState.NotObserved,
            ],
            allocated.Select(point => point.State)
        );
        Assert.Equal(
            result.Scans.Select(scan => scan.CompletedUtc),
            allocated.Select(point => point.CompletedUtc)
        );
        Assert.True(allocated[0].CompletedUtc < allocated[2].CompletedUtc);
    }

    [Fact]
    public void PathsComeFromThatScansParentAndName()
    {
        // Arrange: the item sat in Old (a folder the result no longer lists) and then in Data\Logs.
        var result = Result(4, Folder(20, "Data", Root), Folder(21, "Logs", Ref(20)));
        ImmutableArray<SearchSizeObservation> series =
        [
            Known(0, Ref(99), "a.log", 1, 1),
            Known(1, Ref(21), "b.log", 1, 1),
            Known(2, Root, "c.log", 1, 1),
            new(3, null, null, CapacityObservationState.NotObserved, null, null),
        ];

        // Act
        var points = SearchSizeSeries.Build(result, series, SearchSizeKind.Allocated);

        // Assert
        Assert.Equal(
            ["…\\a.log", "Data\\Logs\\b.log", "c.log", null],
            points.Select(point => point.Path)
        );
    }

    [Fact]
    public void AnUnresolvedSizeStillHasItsPath()
    {
        // Arrange
        var result = Result(1, Folder(20, "Data", Root));
        ImmutableArray<SearchSizeObservation> series =
        [
            new(0, Ref(20), "a.bin", CapacityObservationState.Unresolved, null, null),
        ];

        // Act
        var point = Assert.Single(SearchSizeSeries.Build(result, series, SearchSizeKind.Allocated));

        // Assert
        Assert.Equal("Data\\a.bin", point.Path);
        Assert.Null(point.Bytes);
    }

    [Fact]
    public void ASeriesThatDoesNotMatchTheResultIsRejected()
    {
        // Arrange
        var result = Result(2);
        ImmutableArray<SearchSizeObservation> tooShort = [Known(0, Root, "a", 1, 1)];
        ImmutableArray<SearchSizeObservation> outOfOrder =
        [
            Known(1, Root, "a", 1, 1),
            Known(0, Root, "a", 1, 1),
        ];

        // Act and assert
        Assert.Throws<ArgumentException>(() =>
            SearchSizeSeries.Build(result, tooShort, SearchSizeKind.Allocated)
        );
        Assert.Throws<ArgumentException>(() =>
            SearchSizeSeries.Build(result, outOfOrder, SearchSizeKind.Allocated)
        );
        Assert.Throws<ArgumentException>(() =>
            SearchSizeSeries.Build(result, default, SearchSizeKind.Allocated)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SearchSizeSeries.Build(result, [], (SearchSizeKind)9)
        );
    }

    [Theory]
    [InlineData(0UL, 1UL, 1UL, 1UL)]
    [InlineData(5UL, 1UL, 1UL, 5UL)]
    [InlineData(7UL, 1UL, 2UL, 8UL)]
    [InlineData(KiB, KiB, KiB / 4, KiB)]
    [InlineData(900 * MiB, MiB, 256 * MiB, 1024 * MiB)]
    [InlineData(GiB, GiB, GiB / 4, GiB)]
    public void ScalesUseBinaryUnitsAndRoundSteps(ulong maximum, ulong unit, ulong step, ulong top)
    {
        // Act
        var scale = SearchSizeSeries.ScaleFor(maximum);

        // Assert
        Assert.Equal(new SizeAxisScale(unit, step, top), scale);
    }

    [Fact]
    public void AGraphOfAboutFourteenGibticksInFourGibSteps()
    {
        // Act
        var scale = SearchSizeSeries.ScaleFor((UInt128)(13.97 * GiB));

        // Assert
        Assert.Equal(new SizeAxisScale(GiB, 4 * GiB, 16 * GiB), scale);
        Assert.Equal([(UInt128)0, 4 * GiB, 8 * GiB, 12 * GiB, 16 * GiB], scale.Ticks());
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(3UL)]
    [InlineData(1023UL)]
    [InlineData(1025UL)]
    [InlineData(123_456_789UL)]
    [InlineData(ulong.MaxValue)]
    public void EveryScaleCoversTheMaximumWithAtMostSixSteps(ulong maximum)
    {
        // Act
        var scale = SearchSizeSeries.ScaleFor(maximum);

        // Assert
        Assert.True(scale.Top >= maximum);
        Assert.True(scale.Top / scale.Step <= 6);
        Assert.Equal(UInt128.Zero, scale.Top % scale.Step);
        Assert.Equal(UInt128.Zero, scale.Step * 4 % scale.Unit);
        Assert.Equal(scale.Top / scale.Step + 1, (UInt128)scale.Ticks().Count());
    }

    [Fact]
    public void SizesBeyondTheLargestUnitDoNotOverflow()
    {
        // Act
        var scale = SearchSizeSeries.ScaleFor(UInt128.MaxValue);

        // Assert
        Assert.True(scale.Top >= UInt128.MaxValue / 2);
        Assert.True(scale.Step > 0);
    }
}
