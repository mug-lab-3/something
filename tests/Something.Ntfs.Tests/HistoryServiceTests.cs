using Something.Application;
using Something.Application.History;
using Something.Application.Memory;
using Something.Application.Repositories;

namespace Something.Ntfs.Tests;

/// <summary>Services run against in-memory repositories, without a database.</summary>
public sealed class HistoryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MoveSwitchesListingsAndSearchesToTheNewLocation()
    {
        // Arrange
        var moved = new FakeHistory("moved", [Entry(Now.AddDays(-1)), Entry(Now)]);
        var original = new FakeHistory("original", [Entry(Now.AddDays(-2))]) { Target = moved };
        var history = new HistoryService(original.Repositories);
        var search = new HistorySearchService(history, new(() => null));
        string? saved = null;

        // Act
        await history.MoveAsync("moved", location => saved = location, Token);
        var listed = await history.ListAsync("volume:A", Token);
        var preview = await search.PreviewAsync("volume:A", Now.AddDays(-7), Now, Token);

        // Assert
        Assert.Equal(("moved", "moved"), (saved, history.DirectoryPath));
        Assert.Equal([moved.Entries[1], moved.Entries[0]], listed);
        Assert.Equal(
            (2, moved.Entries[0], moved.Entries[1]),
            (preview.ScanCount, preview.First, preview.Last)
        );
    }

    private static ScanHistoryEntry Entry(DateTimeOffset completedUtc) =>
        new(
            Guid.NewGuid(),
            "volume:A",
            "A:",
            completedUtc,
            DateOnly.FromDateTime(completedUtc.UtcDateTime),
            0,
            TimeSpan.FromSeconds(1)
        );

    /// <summary>Lists fixed entries and relocates to <see cref="Target"/>; everything else is unused.</summary>
    private sealed class FakeHistory(string directory, ScanHistoryEntry[] entries)
        : IScanHistoryRepository,
            IHistoryDatabase
    {
        public ScanHistoryEntry[] Entries => entries;
        public FakeHistory? Target { get; init; }
        public HistoryRepositories Repositories => new(this, null!, null!, null!, this);

        public ScanHistoryEntry[] List(string volumeKey, CancellationToken token) =>
            [.. entries.OrderByDescending(entry => entry.CompletedUtc)];

        public HistoryRepositories Relocate(
            string directory,
            Action<string> saveLocation,
            CancellationToken token
        )
        {
            saveLocation(directory);
            return Target!.Repositories;
        }

        public string DatabasePath => Path.Combine(directory, "history.duckdb");
        public string DirectoryPath => directory;
        public string? MaintenanceError => null;

        public ScanHistorySummary[] ListSummaries(string volumeKey, CancellationToken token) =>
            throw new NotSupportedException();

        public IReadOnlyDictionary<Guid, long> ReadRecordCounts(
            IReadOnlyCollection<Guid> scanIds,
            CancellationToken token
        ) => throw new NotSupportedException();

        public IHistorySnapshot Open(Guid id, CancellationToken token) =>
            throw new NotSupportedException();

        public Guid[] Delete(IReadOnlyCollection<Guid> ids, CancellationToken token) =>
            throw new NotSupportedException();

        public int CollectRetired(CancellationToken token) => throw new NotSupportedException();

        public HistoryDatabaseInfo ReadInfo() => throw new NotSupportedException();

        public void Checkpoint(CancellationToken token) => throw new NotSupportedException();

        public HistoryCompactionResult Compact(
            IMemoryAllowance allowance,
            Action<HistoryCompactionStage>? progress,
            CancellationToken token
        ) => throw new NotSupportedException();

        public void DeleteDatabase(CancellationToken token) => throw new NotSupportedException();
    }
}
