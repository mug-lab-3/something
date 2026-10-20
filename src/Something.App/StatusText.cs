using Something.Application;
using Something.Storage;
using Something.Windows;

namespace Something.App;

/// <summary>
/// Display text for codes that lower layers report instead of user-facing messages. Exceptions
/// without a code keep their diagnostic message.
/// </summary>
internal static class StatusText
{
    public static string Describe(Exception error) =>
        HistoryFailures.Of(error) is { } history ? Describe(history)
        : RecycleFailures.Of(error) is { } recycle ? Describe(recycle)
        : error.Message;

    public static string Describe(HistoryFailure failure) =>
        failure switch
        {
            HistoryFailure.DatabaseInUse => Strings.Get("HistoryFailure_DatabaseInUse"),
            HistoryFailure.UnexpectedCompactionBackupWal => Strings.Get(
                "HistoryFailure_UnexpectedCompactionBackupWal"
            ),
            HistoryFailure.CompactionRecoveryPending => Strings.Get(
                "HistoryFailure_CompactionRecoveryPending"
            ),
            HistoryFailure.InsufficientCompactionSpace => Strings.Get(
                "HistoryFailure_InsufficientCompactionSpace"
            ),
            HistoryFailure.CompactedTablesMismatch => Strings.Get(
                "HistoryFailure_CompactedTablesMismatch"
            ),
            HistoryFailure.CompactedContentMismatch => Strings.Get(
                "HistoryFailure_CompactedContentMismatch"
            ),
            HistoryFailure.MissingDatabaseWithWal => Strings.Get(
                "HistoryFailure_MissingDatabaseWithWal"
            ),
            HistoryFailure.WalNotCheckpointedForDelete => Strings.Get(
                "HistoryFailure_WalNotCheckpointedForDelete"
            ),
            HistoryFailure.WalNotCheckpointedForMove => Strings.Get(
                "HistoryFailure_WalNotCheckpointedForMove"
            ),
            HistoryFailure.RelativeLocation => Strings.Get("HistoryFailure_RelativeLocation"),
            HistoryFailure.LinkedLocation => Strings.Get("HistoryFailure_LinkedLocation"),
            HistoryFailure.DestinationOccupied => Strings.Get("HistoryFailure_DestinationOccupied"),
            HistoryFailure.StartAfterEnd => Strings.Get("HistoryFailure_StartAfterEnd"),
            HistoryFailure.InvalidLocalTime => Strings.Get("HistoryFailure_InvalidLocalTime"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

    public static string Describe(RecycleFailure failure) =>
        failure switch
        {
            RecycleFailure.UnsafePath => Strings.Get("RecycleFailure_UnsafePath"),
            RecycleFailure.UnsafeName => Strings.Get("RecycleFailure_UnsafeName"),
            RecycleFailure.DeviceName => Strings.Get("RecycleFailure_DeviceName"),
            RecycleFailure.Unconfirmed => Strings.Get("RecycleFailure_Unconfirmed"),
            RecycleFailure.LinkedPath => Strings.Get("RecycleFailure_LinkedPath"),
            RecycleFailure.NotRecyclable => Strings.Get("RecycleFailure_NotRecyclable"),
            RecycleFailure.PathChanged => Strings.Get("RecycleFailure_PathChanged"),
            RecycleFailure.Failed => Strings.Get("RecycleFailure_Failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

    public static string Describe(HistoryCompactionStage stage) =>
        stage switch
        {
            HistoryCompactionStage.CollectingUnreferenced => Strings.Get(
                "Compaction_CollectingUnreferenced"
            ),
            HistoryCompactionStage.Rebuilding => Strings.Get("Compaction_Rebuilding"),
            HistoryCompactionStage.ValidatingCopy => Strings.Get("Compaction_ValidatingCopy"),
            HistoryCompactionStage.Switching => Strings.Get("Compaction_Switching"),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

    public static string Describe(DemoScanStage stage) =>
        stage switch
        {
            DemoScanStage.Initial => Strings.Get("DemoStage_Initial"),
            DemoScanStage.ResizedAndAdded => Strings.Get("DemoStage_ResizedAndAdded"),
            DemoScanStage.RemovedAndMoved => Strings.Get("DemoStage_RemovedAndMoved"),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
}
