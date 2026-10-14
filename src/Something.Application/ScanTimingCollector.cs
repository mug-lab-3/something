using Something.Storage;

namespace Something.Application;

/// <summary>Worker-owned monotonic interval accounting. Repeated reports do not create rows.</summary>
internal sealed class ScanTimingCollector(bool savesHistory) : IProgress<ScanProgress>
{
    private readonly List<ScanStepTiming> steps = [];
    private int currentStep;
    private int currentAttempt;
    private ScanWorkStage? currentWork;
    private TimeSpan intervalStart;
    private TimeSpan latest;
    private bool completed;
    internal IProgress<ScanProgress>? Target { get; init; }

    public void Report(ScanProgress value)
    {
        if (completed && value.Phase != ScanPhase.Completed)
            throw new InvalidOperationException("Timing measurement already completed.");
        value = value with { SavesHistory = savesHistory };
        if (value.Elapsed < latest)
            throw new InvalidOperationException("Scan elapsed time must be monotonic.");
        latest = value.Elapsed;
        if (value.Phase != ScanPhase.Completed)
        {
            int number = value.Step.Number;
            int attempt = value.Work?.RetryAttempt ?? 0;
            if (number != currentStep || attempt != currentAttempt)
            {
                CloseInterval(value.Elapsed);
                currentStep = number;
                currentAttempt = attempt;
                currentWork = value.Work?.Stage;
            }
            else if (value.Work is { } work)
                currentWork = work.Stage;
        }
        Target?.Report(value);
    }

    internal ScanTimingSnapshot Complete(TimeSpan elapsed)
    {
        if (completed)
            throw new InvalidOperationException("Timing measurement already completed.");
        if (elapsed < latest)
            throw new InvalidOperationException("Scan elapsed time must be monotonic.");
        CloseInterval(elapsed);
        completed = true;
        return new(savesHistory ? 14 : 13, elapsed, [.. steps]);
    }

    private void CloseInterval(TimeSpan elapsed)
    {
        if (elapsed > intervalStart)
            steps.Add(new(currentStep, currentAttempt, currentWork, elapsed - intervalStart));
        intervalStart = elapsed;
    }
}
