using Something.Application;
using Something.Application.Memory;
using Something.Storage;

namespace Something.Ntfs.Tests;

/// <summary>How reservations share the machine's memory, independent of any database engine.</summary>
public sealed class ScanMemoryBudgetTests
{
    private const ulong GiB = 1UL << 30;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Holds <paramref name="bytes"/> and reports what was available when deciding.</summary>
    private static AvailableMemory? Take(IMemoryAllowance allowance, ulong bytes) =>
        allowance.Charge<AvailableMemory?>(available => new(available, bytes));

    private static AvailableMemory? Peek(IMemoryAllowance allowance) => Take(allowance, 0);

    [Fact]
    public async Task ChargesOfOneReservationAreSubtractedFromTheOthers()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => new(32 * GiB, 20 * GiB, 20 * GiB, 20 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);

        // Act
        Take(first, 8 * GiB);

        // Assert: the first holds its 1 GiB buffer plus 8 GiB; the second holds 1 GiB.
        Assert.Equal(11 * GiB, Peek(second)!.PhysicalBytes);
        Assert.Equal(19 * GiB, Peek(first)!.PhysicalBytes);
    }

    [Fact]
    public async Task AReservationOnlyGrowsItsHold()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => new(32 * GiB, 20 * GiB, 20 * GiB, 20 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);

        // Act
        Take(first, 6 * GiB);
        Take(first, 2 * GiB);
        first.Hold(GiB);
        first.Hold(0);

        // Assert
        Assert.Equal(13 * GiB, Peek(second)!.PhysicalBytes);
        first.Hold(10 * GiB);
        Assert.Equal(9 * GiB, Peek(second)!.PhysicalBytes);
    }

    [Fact]
    public async Task SimultaneousChargesNeverTakeTheSameHeadroom()
    {
        // Arrange: 9 GiB free, 3.2 GiB for the OS, so only one 4 GiB hold fits beside the other buffer.
        var budget = new ScanMemoryBudget(() => new(16 * GiB, 9 * GiB, 9 * GiB, 9 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);
        bool Claim(IMemoryAllowance allowance) =>
            allowance.Charge<bool>(available =>
                MemoryHeadroom.Fits(available, 4 * GiB, MemoryHeadroom.OsReserve(available))
                    ? new(true, 4 * GiB)
                    : new(false, 0)
            );

        // Act
        bool[] won = await Task.WhenAll(
            Task.Run(() => Claim(first), Token),
            Task.Run(() => Claim(second), Token)
        );

        // Assert
        Assert.Single(won, claimed => claimed);
    }

    [Fact]
    public async Task MissingOrInconsistentInformationReachesTheDecisionAsUnknown()
    {
        // Arrange
        AvailableMemory? memory = null;
        var budget = new ScanMemoryBudget(() => memory);
        using var reservation = await budget.ReserveAsync(GiB, Token);

        // Act and assert
        Assert.Null(Peek(reservation));
        memory = new(8 * GiB, 9 * GiB, 9 * GiB, 9 * GiB);
        Assert.Null(Peek(reservation));
        memory = new(0, 0, 0, 0);
        Assert.Null(Peek(reservation));
    }

    [Fact]
    public async Task ADisposedReservationRefusesCharges()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => new(8 * GiB, 4 * GiB, 4 * GiB, 4 * GiB));
        var reservation = await budget.ReserveAsync(GiB, Token);
        reservation.Dispose();

        // Act and assert
        Assert.Throws<ObjectDisposedException>(() => Peek(reservation));
        Assert.Throws<ObjectDisposedException>(() => reservation.Hold(GiB));
    }

    [Fact]
    public async Task DisposingAReservationReturnsItsHold()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => new(32 * GiB, 20 * GiB, 20 * GiB, 20 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        var second = await budget.ReserveAsync(GiB, Token);
        Take(second, 5 * GiB);
        Assert.Equal(14 * GiB, Peek(first)!.PhysicalBytes);

        // Act
        second.Dispose();

        // Assert
        Assert.Equal(20 * GiB, Peek(first)!.PhysicalBytes);
    }

    [Fact]
    public async Task CompetingBudgetIsSubtractedFromAllAvailableResources()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => new(16 * GiB, 12 * GiB, 13 * GiB, 14 * GiB));
        using var first = await budget.ReserveAsync(2 * GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);

        // Act
        var available = Peek(second);

        // Assert
        Assert.Equal(new AvailableMemory(16 * GiB, 10 * GiB, 11 * GiB, 12 * GiB), available);
    }

    [Fact]
    public async Task WaitingScanStartsAfterBudgetReleased()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => new(4 * GiB, 3 * GiB, 3 * GiB, 3 * GiB));
        using var first = await budget.ReserveAsync(2 * GiB, Token);
        Task<ScanMemoryBudget.Reservation> waiting = budget.ReserveAsync(2 * GiB, Token);
        Assert.False(waiting.IsCompleted);

        // Act
        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert
        Assert.NotNull(second);
        Assert.Throws<ObjectDisposedException>(() => Peek(first));
    }

    [Fact]
    public async Task AChargeLetsAWaitingScanProceedOnlyWhenRoomRemains()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => new(16 * GiB, 12 * GiB, 12 * GiB, 12 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        Take(first, 9 * GiB);
        Task<ScanMemoryBudget.Reservation> waiting = budget.ReserveAsync(2 * GiB, Token);

        // Act
        await Task.Delay(50, Token);
        bool blocked = !waiting.IsCompleted;
        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert
        Assert.True(blocked);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task CancellingWaitDoesNotLeakReservationOrCancelOtherScan()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => new(4 * GiB, 3 * GiB, 3 * GiB, 3 * GiB));
        using var first = await budget.ReserveAsync(2 * GiB, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = budget.ReserveAsync(GiB, cancellation.Token);

        // Act
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(3 * GiB, Peek(first)!.PhysicalBytes);
    }

    [Fact]
    public async Task MissingMemoryInformationSerializesScans()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => null);
        using var first = await budget.ReserveAsync(ulong.MaxValue, Token);
        var waiting = budget.ReserveAsync(GiB, Token);

        // Act
        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert
        Assert.NotNull(second);
    }

    [Fact]
    public void UnsharedAllowanceReadsAvailabilityAndHoldsNothing()
    {
        // Arrange
        AvailableMemory? memory = new(16 * GiB, 9 * GiB, 9 * GiB, 9 * GiB);
        var allowance = new UnsharedMemoryAllowance(() => memory);

        // Act
        var first = Take(allowance, 100 * GiB);
        allowance.Hold(100 * GiB);
        var second = Peek(allowance);
        memory = new(16 * GiB, 20 * GiB, 20 * GiB, 20 * GiB);

        // Assert
        Assert.Equal(first, second);
        Assert.Null(Peek(allowance));
    }

    [Fact]
    public void HeadroomPolicyRejectsInconsistentSamples()
    {
        // Arrange
        var sample = new AvailableMemory(16 * GiB, 9 * GiB, 7 * GiB, 8 * GiB);

        // Act and assert
        Assert.Null(MemoryHeadroom.Validate(sample with { TotalPhysicalBytes = 0 }));
        Assert.Null(MemoryHeadroom.Validate(sample with { PhysicalBytes = 17 * GiB }));
        Assert.Null(MemoryHeadroom.Validate(null));
        Assert.Same(sample, MemoryHeadroom.Validate(sample));
        Assert.Equal(7 * GiB, MemoryHeadroom.Headroom(sample));
        Assert.Equal(16 * GiB / 5, MemoryHeadroom.OsReserve(sample));
        Assert.Equal(GiB, MemoryHeadroom.OsReserve(null));
        Assert.Equal(
            4 * GiB,
            MemoryHeadroom.OsReserve(sample with { TotalPhysicalBytes = 64 * GiB })
        );
        Assert.Equal(7 * GiB - 16 * GiB / 5, MemoryHeadroom.Surplus(sample));
        Assert.Equal(0UL, MemoryHeadroom.Surplus(sample with { PhysicalBytes = GiB }));
        Assert.True(MemoryHeadroom.Fits(sample, 6 * GiB, GiB));
        Assert.False(MemoryHeadroom.Fits(sample, 6 * GiB + 1, GiB));
        Assert.False(MemoryHeadroom.Fits(null, 0, 0));
        Assert.False(MemoryHeadroom.Fits(sample, ulong.MaxValue, 1));
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
