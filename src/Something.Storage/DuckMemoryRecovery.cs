using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

internal static class DuckMemoryRecovery
{
    // The failed Write has disposed its transaction and cleared tracking before retry.
    // Retry completed facts, never reacquire a live volume or publish a partial generation.
    internal static T Run<T>(
        DuckScanDbContext context,
        Func<Action<ScanWorkProgress>?, T> publish,
        Func<ulong, ulong?>? reserveGrowth,
        Action<ScanWorkProgress>? progress,
        CancellationToken token,
        Action<ulong>? accountMemoryLimit = null
    )
    {
        lock (DuckDatabaseBootstrap.Gate(context.Database.GetDbConnection().DataSource))
        {
            int attempt = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ulong limit = DuckDatabaseBootstrap.ReadMemoryLimit(context);
                accountMemoryLimit?.Invoke(limit);
                ScanWorkStage? stage = null;
                try
                {
                    return publish(value =>
                    {
                        stage = value.Stage;
                        progress?.Invoke(
                            value with
                            {
                                RetryAttempt = attempt,
                                MemoryLimitBytes = limit,
                            }
                        );
                    });
                }
                catch (Exception error) when (IsOutOfMemory(error))
                {
                    token.ThrowIfCancellationRequested();
                    if (
                        stage == ScanWorkStage.Committing
                        || attempt >= 3
                        || reserveGrowth?.Invoke(limit) is not { } next
                        || next <= limit
                    )
                        throw;
                    DuckDatabaseBootstrap.IncreaseMemoryLimit(context, next);
                    attempt++;
                }
            }
        }
    }

    internal static bool IsOutOfMemory(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (
                current is DuckDBException
                && current.Message.Contains("Out of Memory Error:", StringComparison.Ordinal)
            )
                return true;
        return false;
    }
}
