namespace Something.Application.History;

public sealed record HistoryDatabaseInfo(string Path, long DatabaseBytes, long WalBytes)
{
    public long TotalBytes => checked(DatabaseBytes + WalBytes);
}
