using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

internal static class DuckMemoryRecovery
{
    internal const int MaximumGrowthAttempts = 3;

    // The failed Write has disposed its transaction and cleared tracking before retry.
    // Retry completed facts, never reacquire a live volume or publish a partial generation.
    // Memory is preferred: grow the budget first; spill to disk only when growth is exhausted.
    // A failure during commit is retried only after re-reading the database proves the
    // attempt was not published; a publication that did commit is returned, never repeated.
    internal static T Run<T>(
        DuckScanDbContext context,
        Func<Action<ScanWorkProgress>?, T> publish,
        Func<ulong, ulong?>? reserveGrowth,
        Action<ScanWorkProgress>? progress,
        CancellationToken token,
        Action<ulong>? accountMemoryLimit = null,
        Func<(bool Committed, T Result)>? readCommitted = null
    )
    {
        lock (DuckDatabaseBootstrap.Gate(context.Database.GetDbConnection().DataSource))
        {
            int attempt = 0,
                growth = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ulong limit = DuckDatabaseBootstrap.ReadMemoryLimit(context);
                bool spilling = DuckDatabaseBootstrap.IsSpilling(context);
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
                                SpillingToDisk = spilling,
                            }
                        );
                    });
                }
                catch (Exception error) when (IsOutOfMemory(error))
                {
                    if (stage == ScanWorkStage.Committing)
                    {
                        if (readCommitted is null)
                            throw;
                        var (committed, result) = readCommitted();
                        if (committed)
                            return result;
                    }
                    token.ThrowIfCancellationRequested();
                    if (
                        growth < MaximumGrowthAttempts
                        && reserveGrowth?.Invoke(limit) is { } next
                        && next > limit
                    )
                    {
                        DuckDatabaseBootstrap.IncreaseMemoryLimit(context, next);
                        growth++;
                    }
                    else if (!spilling)
                        DuckDatabaseBootstrap.EnableSpill(context);
                    else
                        throw;
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
                && (
                    current.Message.Contains("Out of Memory Error:", StringComparison.Ordinal)
                    || current.Message.Contains(
                        "failed to allocate data of size",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || current.Message.Contains(
                        "could not allocate block of size",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || current.Message.Contains(
                        "failed to pin block of size",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
                return true;
        return false;
    }
}
