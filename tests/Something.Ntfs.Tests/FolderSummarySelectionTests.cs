using System.Numerics;
using Something.Application.Export;

namespace Something.Ntfs.Tests;

public sealed class FolderSummarySelectionTests
{
    private sealed record Folder(string Path, UInt128? Size, BigInteger? Delta = null);

    private static readonly Folder[] Folders =
    [
        new("", 1000),
        new("Projects", 600, 50),
        new("Projects\\App", 400, -300),
        new("Projects\\App\\bin", 300, 10),
        new("Projects2", 350, 200),
        new("Media", 100, null),
        new("Unknown", null, 5),
    ];

    private static (Folder Folder, int Depth)[] Select(string start, FolderSummaryLimits limits) =>
        FolderSummarySelection.Select(
            Folders,
            start,
            limits,
            folder => folder.Path,
            folder => folder.Size,
            folder => folder.Delta
        );

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("Projects", "", 1)]
    [InlineData("Projects\\App\\bin", "", 3)]
    [InlineData("Projects", "Projects", 0)]
    [InlineData("Projects\\App", "Projects", 1)]
    [InlineData("Projects\\App\\bin", "Projects", 2)]
    public void Depth_CountsLevelsBelowTheStart(string path, string start, int expected) =>
        Assert.Equal(expected, FolderSummarySelection.Depth(path, start));

    [Theory]
    [InlineData("Projects2", "Projects")]
    [InlineData("Projects", "Projects\\App")]
    [InlineData("Media", "Projects")]
    [InlineData("", "Projects")]
    public void Depth_ExcludesSiblingsWithACommonPrefixAndAncestors(string path, string start) =>
        Assert.Null(FolderSummarySelection.Depth(path, start));

    [Fact]
    public void Select_WithoutLimits_KeepsEveryFolderBelowTheStartLargestFirst()
    {
        // Act
        var rows = Select("Projects", new());

        // Assert
        Assert.Equal(
            ["Projects", "Projects\\App", "Projects\\App\\bin"],
            rows.Select(row => row.Folder.Path)
        );
        Assert.Equal([0, 1, 2], rows.Select(row => row.Depth));
    }

    [Fact]
    public void Select_FromTheRoot_AppliesDepthAndPutsUnknownSizesLast()
    {
        // Act
        var rows = Select("", new(MaximumDepth: 1));

        // Assert
        Assert.Equal(
            ["", "Projects", "Projects2", "Media", "Unknown"],
            rows.Select(row => row.Folder.Path)
        );
    }

    [Fact]
    public void Select_MinimumSize_IsInclusiveAndExcludesUnknownSizes()
    {
        // Act
        var rows = Select("", new(MinimumBytes: 350));

        // Assert
        Assert.Equal(
            ["", "Projects", "Projects\\App", "Projects2"],
            rows.Select(row => row.Folder.Path)
        );
    }

    [Fact]
    public void Select_ByChange_OrdersByMagnitudeAndAppliesTheLimitAfterSorting()
    {
        // Act
        var rows = Select("", new(Limit: 3, SortByChange: true));

        // Assert
        Assert.Equal(
            ["Projects\\App", "Projects2", "Projects"],
            rows.Select(row => row.Folder.Path)
        );
    }

    [Fact]
    public void Select_DepthZero_KeepsOnlyTheStartFolder()
    {
        // Act
        var rows = Select("Projects\\App", new(MaximumDepth: 0));

        // Assert
        var row = Assert.Single(rows);
        Assert.Equal(("Projects\\App", 0), (row.Folder.Path, row.Depth));
    }

    [Fact]
    public void Select_EqualSizes_KeepInputOrder()
    {
        // Arrange
        Folder[] folders = [new("A", 5), new("B", 5), new("C", 5)];

        // Act
        var rows = FolderSummarySelection.Select(
            folders,
            "",
            new(),
            folder => folder.Path,
            folder => folder.Size,
            folder => folder.Delta
        );

        // Assert
        Assert.Equal(["A", "B", "C"], rows.Select(row => row.Folder.Path));
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    public void Select_RejectsNegativeDepthAndEmptyLimit(int? depth, int? limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Select("", new(MaximumDepth: depth, Limit: limit))
        );
}
