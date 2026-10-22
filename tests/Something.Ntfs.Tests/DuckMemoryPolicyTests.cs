using Something.Application;
using Something.Application.Memory;
using Something.Storage;

namespace Something.Ntfs.Tests;

/// <summary>How a DuckDB engine sizes and grows its limit, without a database.</summary>
public sealed class DuckMemoryPolicyTests
{
    private const ulong GiB = 1UL << 30;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void TheInitialLimitIsHalfOfTheSurplusAndNeverBelowTheFloor()
    {
        // Arrange: 20 GiB free of 32 GiB, with a 4 GiB OS reserve.
        var plenty = new AvailableMemory(32 * GiB, 20 * GiB, 20 * GiB, 20 * GiB);

        // Act and assert
        Assert.Equal(8 * GiB, DuckMemoryPolicy.InitialLimit(plenty));
        Assert.Equal(GiB, DuckMemoryPolicy.InitialLimit(plenty with { PhysicalBytes = GiB }));
        Assert.Equal(GiB, DuckMemoryPolicy.InitialLimit(null));
    }

    [Fact]
    public void TheLimitHasNoFixedCeilingAndFollowsAvailability()
    {
        // Arrange
        var large = new AvailableMemory(256 * GiB, 200 * GiB, 200 * GiB, 200 * GiB);

        // Act
        ulong limit = DuckMemoryPolicy.InitialLimit(large);

        // Assert: (200 - 4) / 2
        Assert.Equal(98 * GiB, limit);
        Assert.Equal(0UL, limit % DuckMemoryPolicy.Granularity);
    }

    [Fact]
    public void AlignRoundsDownWithinTheNativeRange()
    {
        // Act and assert
        Assert.Equal(16384UL, DuckMemoryPolicy.Align(16384 * 2 - 1));
        Assert.Equal(0UL, DuckMemoryPolicy.Align(16383));
        Assert.Equal(DuckMemoryPolicy.MaximumLimit, DuckMemoryPolicy.Align(UInt128.MaxValue));
        Assert.True(DuckMemoryPolicy.MaximumLimit <= long.MaxValue);
    }

    [Fact]
    public void GrowthAddsHalfOfTheSurplusWhenItsDoubleFits()
    {
        // Arrange: surplus 16 GiB, so the engine may grow by 8 GiB, which holds 16 GiB.
        var plenty = new AvailableMemory(32 * GiB, 20 * GiB, 20 * GiB, 20 * GiB);

        // Act and assert
        Assert.Equal(10 * GiB, DuckMemoryPolicy.Grow(plenty, 2 * GiB));
        Assert.Equal(48 * GiB, DuckMemoryPolicy.Grow(plenty, 40 * GiB));
    }

    [Fact]
    public void GrowthIsRefusedWithoutRoomOrInformation()
    {
        // Arrange
        var scarce = new AvailableMemory(8 * GiB, GiB, GiB, GiB);
        var plenty = new AvailableMemory(32 * GiB, 20 * GiB, 20 * GiB, 20 * GiB);

        // Act and assert
        Assert.Null(DuckMemoryPolicy.Grow(null, GiB));
        Assert.Null(DuckMemoryPolicy.Grow(scarce, GiB));
        Assert.Null(DuckMemoryPolicy.Grow(plenty, DuckMemoryPolicy.MaximumLimit));
    }

    [Fact]
    public void AnEngineLimitIsChargedTwice()
    {
        // Act and assert
        Assert.Equal(6 * GiB, DuckMemoryPolicy.Charge(3 * GiB));
        Assert.Equal(ulong.MaxValue, DuckMemoryPolicy.Charge(ulong.MaxValue));
    }

    [Fact]
    public async Task AcquireChargesTheInitialLimitAndGrowthAgainstOtherReservations()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => new(32 * GiB, 20 * GiB, 20 * GiB, 20 * GiB));
        using var first = await budget.ReserveAsync(GiB, Token);
        using var second = await budget.ReserveAsync(GiB, Token);

        // Act: the other reservation holds 1 GiB, so (20 - 1 - 4) / 2 = 7.5 GiB.
        var (limit, grow, account) = DuckMemoryPolicy.Acquire(first);
        ulong? next = grow(limit);
        ulong? secondLimit = DuckMemoryPolicy.Acquire(second).Limit;

        // Assert
        Assert.Equal(15 * GiB / 2, limit);
        Assert.Equal(15 * GiB, next);
        Assert.True(secondLimit >= GiB);
        account(limit);
    }

    [Fact]
    public async Task AcquireNeverBlocksWhenNothingIsAvailable()
    {
        // Arrange
        var budget = new ScanMemoryBudget(() => null);
        using var reservation = await budget.ReserveAsync(GiB, Token);

        // Act
        var (limit, grow, _) = DuckMemoryPolicy.Acquire(reservation);

        // Assert
        Assert.Equal(GiB, limit);
        Assert.Null(grow(limit));
    }
}
