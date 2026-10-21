using System.Numerics;
using Something.Application;
using Something.Application.Browsing;
using Something.Application.Comparison;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class BrowserOrderingTests
{
    [Fact]
    public void MixedAnalysisSortsFilesAndFoldersTogetherWithUnknownCapacityLast()
    {
        CapacityAnalysisItem[] items =
        [
            new(FileReference.Create(30, 1), "folder", true, 100, 0),
            new(FileReference.Create(31, 1), "large-file", false, 1000, 2),
            new(FileReference.Create(32, 1), "unknown", false, null, null),
        ];
        Assert.Equal(
            new[] { "large-file", "folder", "unknown" },
            BrowserOrdering
                .Analysis(items, new(BrowserColumn.Size, true))
                .Select(row => row.RelativePath)
        );
        Assert.Equal(
            new[] { "folder", "large-file", "unknown" },
            BrowserOrdering
                .Analysis(items, new(BrowserColumn.Size, false))
                .Select(row => row.RelativePath)
        );
        var unresolved = CapacityAnalysisItem.FromFileChange(
            new(Entry(40, @"a\same-name", 100), null)
        );
        Assert.Null(unresolved.AllocatedBytes);
        Assert.Null(unresolved.Delta);
        Assert.Equal(FolderChangeStatus.NotObservedAfter, unresolved.Status);
        var reused = EntryChanges.Compare(
            [Entry(40, @"a\same-name", 100)],
            [Entry(40, @"b\same-name", 110), Entry(41, @"a\same-name", 50)],
            TestContext.Current.CancellationToken
        );
        var changed = reused.Select(CapacityAnalysisItem.FromFileChange).ToArray();
        Assert.Contains(changed, row => row.Reference.RecordIndex == 40 && row.Delta == 10);
        Assert.Contains(changed, row => row.Reference.RecordIndex == 41 && row.Delta is null);
    }

    private static FolderEntry Entry(
        ulong index,
        string name,
        UInt128? size,
        bool directory = false
    ) =>
        new(
            FileReference.Create(index, 1),
            0,
            name,
            directory,
            size is { } bytes ? new CapacityAmount(bytes, bytes) : null,
            true,
            directory,
            size is null
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileSizesSortNumericallyAndKeepUnknownLastAndDirectoriesFirst(bool descending)
    {
        FolderEntry[] rows =
        [
            Entry(4, "unknown", null),
            Entry(2, "medium", 2048),
            Entry(1, "small", 512),
            Entry(3, "large", (UInt128)1 << 100),
            Entry(5, "folder", 0, true),
        ];

        var result = BrowserOrdering.Entries(rows, new(BrowserColumn.Size, descending));

        Assert.Equal(
            descending ? new ulong[] { 5, 3, 2, 1, 4 } : [5, 1, 2, 3, 4],
            result.Select(row => row.Reference.RecordIndex)
        );
    }

    [Fact]
    public void NameOrderIsOrdinalIgnoreCaseAndPreservesUnicodeAndDeterministicTies()
    {
        FolderEntry[] rows =
        [
            Entry(1, "あ", 1),
            Entry(4, "a", 1),
            Entry(2, "b", 1),
            Entry(3, "A", 1),
        ];

        Assert.Equal(
            new[] { "A", "a", "b", "あ" },
            BrowserOrdering.Entries(rows, new(BrowserColumn.Name, false)).Select(row => row.Name)
        );
        Assert.Equal(
            new[] { "あ", "b", "A", "a" },
            BrowserOrdering.Entries(rows, new(BrowserColumn.Name, true)).Select(row => row.Name)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignedChangesIncludeBothDirectionsZeroAndUnknownWithoutTreatingUnknownAsZero(
        bool descending
    )
    {
        EntryChange[] rows =
        [
            new(Entry(1, "decreased", 1000), Entry(1, "decreased", 1)),
            new(Entry(2, "same", 4), Entry(2, "same", 4)),
            new(Entry(3, "grew", 1), Entry(3, "grew", (UInt128)1 << 100)),
            new(null, Entry(4, "unknown", 5)),
        ];
        var expected = descending ? new ulong[] { 3, 2, 1, 4 } : [1, 2, 3, 4];

        Assert.Equal(
            expected,
            BrowserOrdering
                .EntryChanges(rows, new(BrowserColumn.Change, descending))
                .Select(row => row.Entry.Reference.RecordIndex)
        );
        var folders = rows.Select(row => new FolderChange(
            row.Entry.Reference,
            row.Before?.Name,
            row.After?.Name,
            row.BeforeBytes,
            row.AfterBytes,
            row.Delta,
            row.Status
        ));
        Assert.Equal(
            expected,
            BrowserOrdering
                .FolderChanges(folders, new(BrowserColumn.Change, descending))
                .Select(row => row.Reference.RecordIndex)
        );
        Assert.Null(rows[^1].Delta);
        Assert.True(rows[2].Delta > (BigInteger)long.MaxValue);
    }

    [Fact]
    public void ColumnSwitchUsesTextAscendingNumericDescendingAndRepeatedClickReverses()
    {
        var initial = new BrowserOrder(BrowserColumn.Size, true);
        Assert.Equal(new(BrowserColumn.Size, false), initial.Toggle(BrowserColumn.Size));
        Assert.Equal(new(BrowserColumn.Name, false), initial.Toggle(BrowserColumn.Name));
        Assert.Equal(new(BrowserColumn.Change, true), initial.Toggle(BrowserColumn.Change));
    }
}
