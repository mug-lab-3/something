namespace Something.Application.History;

public sealed record HistoryCompactionResult(long BeforeBytes, long AfterBytes, bool Replaced)
{
    public long ReclaimedBytes => Math.Max(0, BeforeBytes - AfterBytes);
}
