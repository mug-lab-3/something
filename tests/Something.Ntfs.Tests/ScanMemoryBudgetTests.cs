using Something.Application;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class ScanMemoryBudgetTests
{
    private const ulong GiB = 1UL << 30;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SmallDuckScanReservesEngineWorkspaceInsteadOfSmallSqliteCache()
    {
        var budget = new ScanMemoryBudget(() => new(16 * GiB, 12 * GiB, 12 * GiB, 12 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);

        var selection = first.SelectDuckDatabase(1024);
        var competing = second.SelectDuckDatabase(1024);

        Assert.False(selection.UseMemory);
        Assert.InRange(selection.FileCacheBytes, GiB, 2 * GiB);
        Assert.Equal(
            12 * GiB - GiB - 2 * selection.FileCacheBytes,
            competing.Available!.PhysicalBytes
        );
        Assert.InRange(competing.FileCacheBytes, GiB, 2 * GiB);
    }

    [Fact]
    public void DuckEngineBudgetDoesNotDependOnNewDriveDatabaseEstimate()
    {
        var available = new AvailableMemory(16 * GiB, 12 * GiB, 12 * GiB, 12 * GiB);

        var small = DiagnosticDatabaseSelector.SelectDuckDb(1, available);
        var large = DiagnosticDatabaseSelector.SelectDuckDb(8 * GiB, available);

        Assert.Equal(2 * GiB, small.FileCacheBytes);
        Assert.Equal(small.FileCacheBytes, large.FileCacheBytes);
    }

    [Fact]
    public void DuckEngineBudgetHasBoundedFallbackForUnknownOrLowHeadroom()
    {
        Assert.Equal(GiB, DiagnosticDatabaseSelector.SelectDuckDb(1, null).FileCacheBytes);
        Assert.Equal(
            GiB,
            DiagnosticDatabaseSelector.SelectDuckDb(1, new(4 * GiB, 1, 1, 1)).FileCacheBytes
        );
    }

    [Fact]
    public async Task CompetingScanBudgetIsSubtractedFromAllAvailableResources()
    {
        var budget = new ScanMemoryBudget(() => new(16 * GiB, 12 * GiB, 13 * GiB, 14 * GiB));
        using var first = await budget.ReserveAsync(2 * GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);

        var selection = second.SelectDatabase(GiB);

        Assert.Equal(
            new AvailableMemory(16 * GiB, 10 * GiB, 11 * GiB, 12 * GiB),
            selection.Available
        );
        Assert.True(selection.UseMemory);
    }

    [Fact]
    public async Task DatabaseReservationPreventsOtherScanFromReusingSameHeadroom()
    {
        var budget = new ScanMemoryBudget(() => new(16 * GiB, 9 * GiB, 9 * GiB, 9 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);

        var firstSelection = first.SelectDatabase(2 * GiB);
        var secondSelection = second.SelectDatabase(2 * GiB);

        Assert.True(firstSelection.UseMemory);
        Assert.False(secondSelection.UseMemory);
        Assert.Equal(4 * GiB, secondSelection.Available!.PhysicalBytes);
        Assert.InRange(secondSelection.FileCacheBytes, 64UL << 20, 2 * GiB);
    }

    [Fact]
    public async Task SimultaneousDatabaseSelectionsReserveHeadroomAtomically()
    {
        var budget = new ScanMemoryBudget(() => new(16 * GiB, 9 * GiB, 9 * GiB, 9 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);

        var selections = await Task.WhenAll(
            Task.Run(() => first.SelectDatabase(2 * GiB), Token),
            Task.Run(() => second.SelectDatabase(2 * GiB), Token)
        );

        Assert.Single(selections, selection => selection.UseMemory);
        Assert.Single(selections, selection => !selection.UseMemory);
    }

    [Fact]
    public async Task WaitingScanStartsAfterBudgetReleased()
    {
        var budget = new ScanMemoryBudget(() => new(4 * GiB, 3 * GiB, 3 * GiB, 3 * GiB));
        using var first = await budget.ReserveAsync(2 * GiB, Token);
        Task<ScanMemoryBudget.Reservation> waiting = budget.ReserveAsync(2 * GiB, Token);
        Assert.False(waiting.IsCompleted);

        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.NotNull(second);
        Assert.Throws<ObjectDisposedException>(() => first.SelectDatabase(GiB));
    }

    [Fact]
    public async Task CancellingWaitDoesNotLeakReservationOrCancelOtherScan()
    {
        var budget = new ScanMemoryBudget(() => new(4 * GiB, 3 * GiB, 3 * GiB, 3 * GiB));
        using var first = await budget.ReserveAsync(2 * GiB, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = budget.ReserveAsync(GiB, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(3 * GiB, first.SelectDatabase(1).Available!.PhysicalBytes);
    }

    [Fact]
    public async Task MissingMemoryInformationSerializesScansAndUsesMinimumFileCache()
    {
        var budget = new ScanMemoryBudget(() => null);
        using var first = await budget.ReserveAsync(ulong.MaxValue, Token);
        var waiting = budget.ReserveAsync(GiB, Token);
        var selection = first.SelectDatabase(ulong.MaxValue);
        Assert.False(waiting.IsCompleted);
        Assert.False(selection.UseMemory);
        Assert.Equal(64UL << 20, selection.FileCacheBytes);

        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task ConcurrentDemoScansPublishIndependentBrowsableDatabases()
    {
        var scanner = new DriveScanner();
        var firstTask = scanner.CreateDemoAsync(null, Token, "DemoA");
        var secondTask = scanner.CreateDemoAsync(null, Token, "DemoB");
        await Task.WhenAll(firstTask, secondTask);
        await using var first = await firstTask;
        await using var second = await secondTask;

        var firstRows = await first.ReadFolderAsync(first.Root, 0, false, Token);
        var secondRows = await second.ReadFolderAsync(second.Root, 0, false, Token);

        Assert.NotEqual(first.ScanId, second.ScanId);
        Assert.Equal("DemoA", first.Drive);
        Assert.Equal("DemoB", second.Drive);
        Assert.Equal(3, firstRows.Entries.Length);
        Assert.Equal(firstRows.Entries.ToArray(), secondRows.Entries.ToArray());
        Assert.Equal(first.RootSample, second.RootSample);
        await first.DisposeAsync();
        Assert.Equal(
            3,
            (await second.ReadFolderAsync(second.Root, 0, false, Token)).Entries.Length
        );
    }
}
