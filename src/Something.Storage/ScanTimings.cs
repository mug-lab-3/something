using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

public sealed record ScanStepTiming(int Step, int Attempt, ScanWorkStage? Work, TimeSpan Elapsed);

/// <summary>Chronological, non-overlapping intervals measured by a monotonic clock.
/// Retried steps have separate rows. The final write of these diagnostics is excluded.</summary>
public sealed record ScanTimingSnapshot(
    int TotalSteps,
    TimeSpan TotalElapsed,
    ImmutableArray<ScanStepTiming> Steps
);

internal sealed class ScanTimingEntity
{
    public Guid ScanId { get; set; }
    public int PlanVersion { get; set; } = 1;
    public int TotalSteps { get; set; }
    public long TotalTicks { get; set; }
}

internal sealed class ScanStepTimingEntity
{
    public Guid ScanId { get; set; }
    public int Ordinal { get; set; }
    public int Step { get; set; }
    public int Attempt { get; set; }
    public ScanWorkStage? Work { get; set; }
    public long ElapsedTicks { get; set; }
}

public sealed partial class ScanStore
{
    public void SaveTimings(
        Guid scanId,
        ScanTimingSnapshot timing,
        CancellationToken token = default
    )
    {
        ArgumentNullException.ThrowIfNull(timing);
        if (
            timing.TotalSteps is < 1 or > 100
            || timing.TotalElapsed < TimeSpan.Zero
            || timing.Steps.IsDefaultOrEmpty
        )
            throw new ArgumentException("A complete timing snapshot is required.", nameof(timing));
        long sum = 0;
        foreach (var step in timing.Steps)
        {
            if (
                step.Step < 0
                || step.Step > timing.TotalSteps
                || step.Attempt < 0
                || step.Elapsed < TimeSpan.Zero
                || step.Work is { } work && !Enum.IsDefined(work)
            )
                throw new ArgumentException("Invalid step timing.", nameof(timing));
            sum = checked(sum + step.Elapsed.Ticks);
        }
        if (sum != timing.TotalElapsed.Ticks)
            throw new ArgumentException(
                "Step durations do not conserve total elapsed time.",
                nameof(timing)
            );
        Write(
            () =>
            {
                if (!context.Scans.Any(s => s.Id == scanId && s.State == StoredScanState.Completed))
                    throw new InvalidOperationException(
                        "Only completed scans can have timing diagnostics."
                    );
                context.StepTimings.Where(row => row.ScanId == scanId).ExecuteDelete();
                context.Timings.Where(row => row.ScanId == scanId).ExecuteDelete();
                context.Timings.Add(
                    new()
                    {
                        ScanId = scanId,
                        TotalSteps = timing.TotalSteps,
                        TotalTicks = timing.TotalElapsed.Ticks,
                    }
                );
                for (int i = 0; i < timing.Steps.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var step = timing.Steps[i];
                    context.StepTimings.Add(
                        new()
                        {
                            ScanId = scanId,
                            Ordinal = i,
                            Step = step.Step,
                            Attempt = step.Attempt,
                            Work = step.Work,
                            ElapsedTicks = step.Elapsed.Ticks,
                        }
                    );
                }
                var history = context.HistorySnapshots.SingleOrDefault(row => row.Id == scanId);
                if (history is not null)
                    history.ElapsedTicks = timing.TotalElapsed.Ticks;
            },
            token
        );
    }

    public ScanTimingSnapshot? ReadTimings(Guid scanId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var total = context.Timings.AsNoTracking().SingleOrDefault(row => row.ScanId == scanId);
        if (total is null)
            return null;
        var rows = context
            .StepTimings.AsNoTracking()
            .Where(row => row.ScanId == scanId)
            .OrderBy(row => row.Ordinal)
            .ToArray();
        token.ThrowIfCancellationRequested();
        return new(
            total.TotalSteps,
            TimeSpan.FromTicks(total.TotalTicks),
            [
                .. rows.Select(row => new ScanStepTiming(
                    row.Step,
                    row.Attempt,
                    row.Work,
                    TimeSpan.FromTicks(row.ElapsedTicks)
                )),
            ]
        );
    }
}
