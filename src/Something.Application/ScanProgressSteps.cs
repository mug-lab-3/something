using Something.Storage;

namespace Something.Application;

public readonly record struct ScanStep(int Number, int Total);

/// <summary>Ordered steps for the native file-storage workflow. Step position is not
/// an estimate of elapsed time; percentage still belongs to the current step.</summary>
internal static class ScanProgressSteps
{
    private static readonly (ScanPhase Phase, ScanWorkStage? Work)[] Steps =
    [
        (ScanPhase.Reading, null),
        (ScanPhase.Aggregating, ScanWorkStage.CollectingFolders),
        (ScanPhase.Aggregating, ScanWorkStage.BuildingHierarchy),
        (ScanPhase.Aggregating, ScanWorkStage.AggregatingFiles),
        (ScanPhase.Aggregating, ScanWorkStage.RollingUpFolders),
        (ScanPhase.Aggregating, ScanWorkStage.ValidatingAggregation),
        (ScanPhase.Saving, ScanWorkStage.WritingFiles),
        (ScanPhase.Saving, ScanWorkStage.WritingFolders),
        (ScanPhase.Saving, ScanWorkStage.WritingFolderNames),
        (ScanPhase.Saving, ScanWorkStage.FlushingRows),
        (ScanPhase.Saving, ScanWorkStage.ValidatingRelationships),
        (ScanPhase.Saving, ScanWorkStage.BuildingFolderList),
        (ScanPhase.Saving, ScanWorkStage.Committing),
        (ScanPhase.SavingHistory, ScanWorkStage.MaintainingHistory),
    ];

    internal static ScanStep Locate(ScanPhase phase, ScanWorkStage? work, bool savesHistory)
    {
        int total = Steps.Length - (savesHistory ? 0 : 1);
        if (phase == ScanPhase.WaitingForMemory)
            return new(0, total);
        if (phase is ScanPhase.Completed or ScanPhase.WritingSnapshot)
            return new(total, total);
        if (phase == ScanPhase.SavingHistory && work != ScanWorkStage.MaintainingHistory)
            phase = ScanPhase.Saving;
        for (int i = 0; i < total; i++)
            if (Steps[i].Phase == phase && (work is null || Steps[i].Work == work))
                return new(i + 1, total);
        throw new InvalidOperationException("Progress stage is outside the scan plan.");
    }
}
