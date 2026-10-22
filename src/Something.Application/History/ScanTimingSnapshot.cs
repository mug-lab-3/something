using System.Collections.Immutable;
using Something.Application.Scanning;

namespace Something.Application.History;

public sealed record ScanStepTiming(int Step, int Attempt, ScanWorkStage? Work, TimeSpan Elapsed);

/// <summary>Chronological, non-overlapping intervals measured by a monotonic clock.
/// Retried steps have separate rows. The final write of these diagnostics is excluded.</summary>
public sealed record ScanTimingSnapshot(
    int TotalSteps,
    TimeSpan TotalElapsed,
    ImmutableArray<ScanStepTiming> Steps
);
