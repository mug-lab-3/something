namespace Something.Windows;

/// <summary>Recycle failures that the user interface describes in the user's language.</summary>
public enum RecycleFailure
{
    UnsafePath,
    UnsafeName,
    DeviceName,
    Unconfirmed,
    LinkedPath,
    NotRecyclable,
    PathChanged,
    Failed,
}

/// <summary>
/// Attaches a <see cref="RecycleFailure"/> to an exception without changing its type, so callers
/// keep catching by type while the user interface translates the failure instead of the message.
/// </summary>
public static class RecycleFailures
{
    private const string Key = "Something.Windows.RecycleFailure";

    public static TException WithFailure<TException>(this TException error, RecycleFailure failure)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(error);
        error.Data[Key] = failure;
        return error;
    }

    public static RecycleFailure? Of(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Data[Key] as RecycleFailure?;
    }
}
