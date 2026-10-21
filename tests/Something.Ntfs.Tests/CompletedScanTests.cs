using Something.Application.Scanning;
using Something.Statistics;

namespace Something.Ntfs.Tests;

/// <summary>Completion of a buffered scan, without a database.</summary>
public sealed class CompletedScanTests
{
    private static readonly FileReference Root = FileReference.Create(5, 5);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static StagedObservation Entry(
        ulong index,
        bool directory,
        string name,
        long bytes = 0,
        FileReference? parent = null
    )
    {
        var reference = index == 5 ? Root : FileReference.Create(index, 1);
        return new(
            index,
            MftScanEntryKind.ResolvedFile,
            LiveFileReadStatus.ObservedUnchanged,
            new FileCapacityObservation(
                reference,
                directory,
                CapacityDomain.Ordinary,
                CapacityResolution.Resolved,
                [new(parent ?? Root, name, FileNameNamespace.Win32, 0)],
                directory ? [] : [new("", bytes, bytes)],
                true
            )
        );
    }

    private static BufferedScan Buffer(params StagedObservation[] entries)
    {
        var buffer = new BufferedScan(
            new("volume:A", Guid.NewGuid(), 1, new(0, 1000), DateTimeOffset.UtcNow)
        );
        foreach (var entry in entries)
            buffer.Add(entry, Token);
        return buffer;
    }

    [Fact]
    public void CompletionAggregatesOnceAndFreezesTheBuffer()
    {
        // Arrange
        var buffer = Buffer(
            Entry(5, true, "."),
            Entry(20, true, "Data"),
            Entry(100, false, "a.bin", 4096, FileReference.Create(20, 1))
        );

        // Act
        var completed = buffer.Complete(Token);

        // Assert
        Assert.Same(completed, buffer.Complete(Token));
        Assert.Equal(Root, completed.RootReference);
        Assert.Equal(new CapacityAmount(4096, 4096), completed.Root.SubtreeData);
        Assert.Equal([5UL, 20UL, 100UL], completed.Observations.Select(o => o.RecordIndex));
        Assert.Empty(completed.Diagnostics);
        Assert.Throws<InvalidOperationException>(() => buffer.Add(Entry(101, false, "b"), Token));
        Assert.Equal(3, completed.Observations.Count);
    }

    [Fact]
    public void AccountingDiagnosticsAssignTheSubtreeOfAFolder()
    {
        // Arrange
        var completed = Buffer(
                Entry(5, true, "."),
                Entry(20, true, "Data"),
                Entry(100, false, "a.bin", 4096, FileReference.Create(20, 1)),
                Entry(101, false, "b.bin", 8192)
            )
            .Complete(Token);

        // Act
        var data = completed.ReadAccountingDiagnostics(
            FileReference.Create(20, 1),
            cancellationToken: Token
        );
        var root = completed.ReadAccountingDiagnostics(Root, cancellationToken: Token);

        // Assert
        Assert.Equal(new CapacityAmount(4096, 4096), data.AssignedData);
        Assert.Equal(completed.Root.SubtreeData, root.AssignedData);
    }

    [Fact]
    public void TheDatabaseEstimateGrowsWithEntriesAndIsPageAligned()
    {
        // Arrange
        var small = Buffer(Entry(5, true, "."), Entry(100, false, "a", 1)).Complete(Token);
        var many = Buffer([
                Entry(5, true, "."),
                .. Enumerable.Range(100, 40).Select(i => Entry((ulong)i, false, $"file-{i}", 1)),
            ])
            .Complete(Token);

        // Act
        ulong smaller = small.EstimateDatabaseBytes(Token);
        ulong larger = many.EstimateDatabaseBytes(Token);

        // Assert
        Assert.True(smaller >= 64 * 1024);
        Assert.Equal(0UL, smaller % 16384);
        Assert.True(larger > smaller);
    }

    [Fact]
    public void CompletionNeedsTheRootAndHonorsCancellation()
    {
        // Arrange
        var rootless = Buffer(Entry(100, false, "a", 1));
        var buffer = Buffer(Entry(5, true, "."));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act and assert
        Assert.Throws<InvalidDataException>(() => rootless.Complete(Token));
        Assert.Throws<OperationCanceledException>(() => buffer.Complete(cancelled.Token));
        buffer.Add(Entry(100, false, "a", 1), Token);
        Assert.Equal(new CapacityAmount(1, 1), buffer.Complete(Token).Root.SubtreeData);
    }
}
