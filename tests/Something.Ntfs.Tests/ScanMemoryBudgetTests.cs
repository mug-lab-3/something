using Something.Application;
using Something.Application.Memory;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class ScanMemoryBudgetTests
{
    private const ulong GiB = 1UL << 30;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExistingSharedEngineLimitIsAccountedBeforeAnotherDriveSelectsBudget()
    {
        var budget = new ScanMemoryBudget(() => new(32 * GiB, 20 * GiB, 20 * GiB, 20 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);
        first.SelectDuckDatabase(1);

        first.AccountDuckDatabaseLimit(8 * GiB);

        Assert.Equal(3 * GiB, second.SelectDuckDatabase(1).Available!.PhysicalBytes);
    }

    [Fact]
    public async Task DuckGrowthReservesIncrementAndSubtractsCompetingDriveBudget()
    {
        var budget = new ScanMemoryBudget(() => new(32 * GiB, 20 * GiB, 20 * GiB, 20 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);
        Assert.Equal(19 * GiB / 2, first.TryGrowDuckDatabase(2 * GiB));
        var competing = second.SelectDuckDatabase(1);
        Assert.Equal(0UL, competing.Available!.PhysicalBytes);
    }

    [Fact]
    public void HeadroomPolicyRejectsInconsistentSamplesAndAlignsWithinTheNativeRange()
    {
        // Arrange
        var sample = new AvailableMemory(16 * GiB, 9 * GiB, 7 * GiB, 8 * GiB);

        // Act and assert
        Assert.Null(MemoryHeadroom.Validate(sample with { TotalPhysicalBytes = 0 }));
        Assert.Null(MemoryHeadroom.Validate(sample with { PhysicalBytes = 17 * GiB }));
        Assert.Equal(7 * GiB, MemoryHeadroom.Headroom(sample));
        Assert.Equal(16 * GiB / 5, MemoryHeadroom.OsReserve(sample));
        Assert.Equal(GiB, MemoryHeadroom.OsReserve(null));
        Assert.Equal(
            4 * GiB,
            MemoryHeadroom.OsReserve(sample with { TotalPhysicalBytes = 64 * GiB })
        );
        Assert.True(MemoryHeadroom.Fits(sample, 6 * GiB, GiB));
        Assert.False(MemoryHeadroom.Fits(sample, 6 * GiB + 1, GiB));
        Assert.False(MemoryHeadroom.Fits(null, 0, 0));
        Assert.Equal(16384UL, MemoryHeadroom.Align(16384 * 2 - 1));
        Assert.Equal(MemoryHeadroom.MaximumDuckLimit, MemoryHeadroom.Align(UInt128.MaxValue));
        Assert.Equal(GiB, MemoryHeadroom.DuckLimit(sample with { PhysicalBytes = GiB }));
    }

    [Fact]
    public async Task InitialDuckBudgetIsHalfOfAvailableHeadroomAndNeverBelowTheFloor()
    {
        // Arrange
        AvailableMemory? memory = new(32 * GiB, 20 * GiB, 20 * GiB, 20 * GiB);
        var budget = new ScanMemoryBudget(() => memory);
        using var search = await budget.ReserveAsync(GiB, Token);
        using var other = await budget.ReserveAsync(GiB, Token);

        // Act: 20 GiB, minus the other 1 GiB and a 4 GiB OS reserve, halved.
        ulong limit = search.SizeDuckDatabase();

        // Assert
        Assert.Equal(15 * GiB / 2, limit);
        Assert.Equal(4 * GiB, other.SelectDuckDatabase(1).Available!.PhysicalBytes);
        memory = new(8 * GiB, GiB, GiB, GiB);
        Assert.Equal(GiB, other.SizeDuckDatabase());
        memory = null;
        Assert.Equal(GiB, other.SizeDuckDatabase());
    }

    [Fact]
    public async Task DuckGrowthDoesNotIncreaseBudgetWhenAvailableRamIsInsufficientOrUnknown()
    {
        AvailableMemory? memory = new(8 * GiB, GiB, GiB, GiB);
        var budget = new ScanMemoryBudget(() => memory);
        using var reservation = await budget.ReserveAsync(GiB, Token);

        Assert.Null(reservation.TryGrowDuckDatabase(GiB));
        memory = null;
        Assert.Null(reservation.TryGrowDuckDatabase(GiB));
        reservation.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reservation.TryGrowDuckDatabase(GiB));
    }

    [Fact]
    public async Task SmallDuckScanReservesEngineWorkspaceInsteadOfSmallSqliteCache()
    {
        var budget = new ScanMemoryBudget(() => new(16 * GiB, 12 * GiB, 12 * GiB, 12 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);

        var selection = first.SelectDuckDatabase(1024);
        var competing = second.SelectDuckDatabase(1024);

        Assert.False(selection.UseMemory);
        Assert.True(selection.FileCacheBytes > 2 * GiB);
        Assert.Equal(
            12 * GiB - GiB - 2 * selection.FileCacheBytes,
            competing.Available!.PhysicalBytes
        );
        Assert.Equal(GiB, competing.FileCacheBytes);
    }

    [Fact]
    public void DuckEngineBudgetDoesNotDependOnNewDriveDatabaseEstimate()
    {
        var available = new AvailableMemory(16 * GiB, 12 * GiB, 12 * GiB, 12 * GiB);

        var small = DiagnosticDatabaseSelector.SelectDuckDb(1, available);
        var large = DiagnosticDatabaseSelector.SelectDuckDb(8 * GiB, available);

        Assert.True(small.FileCacheBytes > 2 * GiB);
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
    public async Task DuckGrowthCanUseAvailableMemoryBeyondEightGiB()
    {
        AvailableMemory? memory = new(64 * GiB, 40 * GiB, 40 * GiB, 40 * GiB);
        var budget = new ScanMemoryBudget(() => memory);
        using var reservation = await budget.ReserveAsync(GiB, Token);
        Assert.Equal(18 * GiB, reservation.SelectDuckDatabase(1).FileCacheBytes);
        Assert.Equal(26 * GiB, reservation.TryGrowDuckDatabase(8 * GiB));
        memory = new(64 * GiB, 4 * GiB, 4 * GiB, 4 * GiB);
        Assert.Null(reservation.TryGrowDuckDatabase(26 * GiB));
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
        var scanner = new DriveScanner(StorageRepositories.TemporaryScans);
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
