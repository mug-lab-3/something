namespace Something.Application.History;

/// <summary>History failures that the user interface describes in the user's language.</summary>
public enum HistoryFailure
{
    DatabaseInUse,
    UnexpectedCompactionBackupWal,
    CompactionRecoveryPending,
    InsufficientCompactionSpace,
    CompactedTablesMismatch,
    CompactedContentMismatch,
    MissingDatabaseWithWal,
    WalNotCheckpointedForDelete,
    WalNotCheckpointedForMove,
    RelativeLocation,
    LinkedLocation,
    DestinationOccupied,
    StartAfterEnd,
    InvalidLocalTime,
}

/// <summary>Steps reported while compacting the shared history database.</summary>
public enum HistoryCompactionStage
{
    CollectingUnreferenced,
    Rebuilding,
    ValidatingCopy,
    Switching,
}

/// <summary>
/// Attaches a <see cref="HistoryFailure"/> to an exception without changing its type, so callers
/// keep catching by type while the user interface translates the failure instead of the message.
/// </summary>
public static class HistoryFailures
{
    private const string Key = "Something.Application.HistoryFailure";

    public static TException WithFailure<TException>(this TException error, HistoryFailure failure)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(error);
        error.Data[Key] = failure;
        return error;
    }

    public static HistoryFailure? Of(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Data[Key] as HistoryFailure?;
    }
}
