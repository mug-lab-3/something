using Something.Application.Memory;
using Something.Windows;

namespace Something.Ntfs.Tests;

public sealed class DiagnosticDatabaseSelectionTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private const ulong MiB = 1024 * 1024;

    [Theory]
    [InlineData(1UL)]
    [InlineData(1024UL * 1024 * 1024)]
    public void Automatic_SufficientCurrentHeadroomSelectsMemory(ulong estimate)
    {
        var available = new AvailableMemory(16 * GiB, 12 * GiB, 12 * GiB, 12 * GiB);

        var result = DiagnosticDatabaseSelector.Select(estimate, available);

        Assert.True(result.UseMemory);
        Assert.Equal(0UL, result.FileCacheBytes);
        Assert.Equal(DatabaseSelectionReason.SufficientHeadroom, result.Reason);
        Assert.Equal(estimate * 2 + 16 * GiB / 5, result.RequiredAdditionalBytes);
        Assert.Same(available, result.Available);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Automatic_AnyInsufficientResourceSelectsFile(int limited)
    {
        var available = new AvailableMemory(
            16 * GiB,
            limited == 0 ? GiB : 12 * GiB,
            limited == 1 ? GiB : 12 * GiB,
            limited == 2 ? GiB : 12 * GiB
        );

        var result = DiagnosticDatabaseSelector.Select(GiB, available);

        Assert.False(result.UseMemory);
        Assert.Equal(DatabaseSelectionReason.InsufficientHeadroom, result.Reason);
    }

    [Fact]
    public void Automatic_ExactThresholdFitsButOneByteLessDoesNot()
    {
        const ulong estimate = GiB;
        ulong threshold = 2 * estimate + GiB;
        var available = new AvailableMemory(4 * GiB, threshold, threshold, threshold);

        Assert.True(DiagnosticDatabaseSelector.Select(estimate, available).UseMemory);
        Assert.False(
            DiagnosticDatabaseSelector
                .Select(estimate, available with { PhysicalBytes = threshold - 1 })
                .UseMemory
        );
    }

    [Fact]
    public void Automatic_MissingOrInvalidInformationFallsBackToFile()
    {
        foreach (
            var available in new AvailableMemory?[]
            {
                null,
                new(0, 0, 0, 0),
                new(GiB, 2 * GiB, 2 * GiB, 2 * GiB),
            }
        )
        {
            var result = DiagnosticDatabaseSelector.Select(1, available);
            Assert.False(result.UseMemory);
            Assert.Equal(DatabaseSelectionReason.MemoryInfoUnavailable, result.Reason);
            Assert.Null(result.Available);
            Assert.Equal(64 * MiB, result.FileCacheBytes);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FileCache_UsesTheTightestResourceAndLeavesTheReserve(int limited)
    {
        var available = new AvailableMemory(
            16 * GiB,
            limited == 0 ? 6 * GiB : 12 * GiB,
            limited == 1 ? 6 * GiB : 12 * GiB,
            limited == 2 ? 6 * GiB : 12 * GiB
        );

        var result = DiagnosticDatabaseSelector.Select(4 * GiB, available);

        Assert.False(result.UseMemory);
        ulong expected = (6 * GiB - result.ReserveBytes) / 4 / 16384 * 16384;
        Assert.Equal(expected, result.FileCacheBytes);
        Assert.True(result.FileCacheBytes > 64 * MiB);
        Assert.True((UInt128)result.FileCacheBytes * 4 + result.ReserveBytes <= 6 * GiB);
    }

    [Fact]
    public void FileCache_ScalesWithAvailableHeadroomAndHonorsSizeBounds()
    {
        var available = new AvailableMemory(16 * GiB, 6 * GiB, 12 * GiB, 12 * GiB);
        var larger = DiagnosticDatabaseSelector.Select(4 * GiB, available);
        var smaller = DiagnosticDatabaseSelector.Select(
            4 * GiB,
            available with
            {
                PhysicalBytes = 4 * GiB,
            }
        );
        var minimum = DiagnosticDatabaseSelector.Select(
            4 * GiB,
            available with
            {
                PhysicalBytes = 0,
            }
        );
        var maximum = DiagnosticDatabaseSelector.Select(
            ulong.MaxValue,
            new(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)
        );
        var smallDatabase = DiagnosticDatabaseSelector.Select(
            64 * MiB,
            available,
            DiagnosticDatabasePreference.File
        );

        Assert.True(larger.FileCacheBytes > smaller.FileCacheBytes);
        Assert.Equal(64 * MiB, minimum.FileCacheBytes);
        Assert.Equal(2 * GiB, maximum.FileCacheBytes);
        Assert.Equal(64 * MiB, smallDatabase.FileCacheBytes);
        Assert.Equal(
            64 * MiB,
            DiagnosticDatabaseSelector
                .Select(1, available, DiagnosticDatabasePreference.File)
                .FileCacheBytes
        );
    }

    [Fact]
    public void Automatic_ReserveIsCappedAndArithmeticCannotWrapIntoAnApproval()
    {
        var available = new AvailableMemory(
            ulong.MaxValue,
            ulong.MaxValue,
            ulong.MaxValue,
            ulong.MaxValue
        );

        var result = DiagnosticDatabaseSelector.Select(ulong.MaxValue, available);

        Assert.False(result.UseMemory);
        Assert.Equal(4 * GiB, result.ReserveBytes);
        Assert.Equal(ulong.MaxValue, result.RequiredAdditionalBytes);
        Assert.Equal(DatabaseSelectionReason.InsufficientHeadroom, result.Reason);
    }

    [Fact]
    public void ExplicitDiagnosticPreferencesOverrideTheAutomaticDecision()
    {
        var forcedMemory = DiagnosticDatabaseSelector.Select(
            GiB,
            null,
            DiagnosticDatabasePreference.Memory
        );
        var forcedFile = DiagnosticDatabaseSelector.Select(
            1,
            new(16 * GiB, 12 * GiB, 12 * GiB, 12 * GiB),
            DiagnosticDatabasePreference.File
        );

        Assert.True(forcedMemory.UseMemory);
        Assert.Equal(DatabaseSelectionReason.ForcedMemory, forcedMemory.Reason);
        Assert.False(forcedFile.UseMemory);
        Assert.Equal(DatabaseSelectionReason.ForcedFile, forcedFile.Reason);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiagnosticDatabaseSelector.Select(0, null)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiagnosticDatabaseSelector.Select(1, null, (DiagnosticDatabasePreference)99)
        );
    }

    [Fact]
    public void WindowsMemoryProbe_ReturnsCurrentHeadroomWithoutElevation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var snapshot = WindowsMemoryAvailability.TryRead();

        Assert.NotNull(snapshot);
        Assert.True(snapshot.TotalPhysicalBytes > 0);
        Assert.True(snapshot.AvailablePhysicalBytes <= snapshot.TotalPhysicalBytes);
        Assert.True(snapshot.AvailableVirtualBytes > 0);
    }
}
