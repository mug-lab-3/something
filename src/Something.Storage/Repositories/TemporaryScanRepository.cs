using Something.Application.Memory;
using Something.Application.Repositories;
using Something.Application.Scanning;

namespace Something.Storage;

internal sealed class TemporaryScanRepository : ITemporaryScanRepository
{
    public static TemporaryScanRepository Instance { get; } = new();

    public IScanSnapshot Publish(
        CompletedScan scan,
        DateTimeOffset endedUtc,
        IMemoryAllowance allowance,
        Action<ScanWorkProgress>? progress,
        CancellationToken token
    )
    {
        var (limit, grow, account) = DuckMemoryPolicy.Acquire(allowance);
        var database = TemporaryScanDatabase.Create(
            token,
            inMemory: false,
            boundedMemory: true,
            limit
        );
        try
        {
            var id = database.Store.PublishBufferedScan(
                scan,
                endedUtc,
                token,
                progress,
                grow,
                account
            );
            return new StoreScanSnapshot(database, database.Store, id);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }
}
