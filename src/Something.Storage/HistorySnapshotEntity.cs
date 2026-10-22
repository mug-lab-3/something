namespace Something.Storage;

internal sealed class HistorySnapshotEntity
{
    public Guid Id { get; set; }
    public string VolumeKey { get; set; } = "";
    public string Drive { get; set; } = "";
    public long CompletedUtcTicks { get; set; }
    public int LocalDay { get; set; }
    public int UtcOffsetMinutes { get; set; }
    public long ElapsedTicks { get; set; }
    public bool Retired { get; set; }
}
