using Microsoft.UI.Xaml;
using Something.Application;

namespace Something.App;

public sealed partial class MainWindow
{
    private async Task VerifyScanAnimationAsync()
    {
        if (!demo || selectedCard is not { } card || scans.Count != 0)
            throw new InvalidOperationException("Scan animation smoke requires an idle demo.");
        var args = Environment.GetCommandLineArgs();
        string capture = args[Array.IndexOf(args, "--smoke-test") + 1];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var operation = new ScanOperation(cancellation);
        double tabsTop = WorkspaceTabs.TransformToVisual(RootLayout).TransformPoint(default).Y;
        scans.Add(card, operation);
        card.SetBusy(true);
        try
        {
            foreach (var phase in new[] { ScanPhase.Reading, ScanPhase.SavingHistory })
            {
                operation.Progress = new(phase, 100, .42, TimeSpan.FromSeconds(5));
                DisplayProgress(card, operation.Progress);
                await Task.Delay(1450, lifetime.Token);
                if (
                    RootLayout.FindName("ScanAnimation") is not null
                    || Math.Abs(
                        WorkspaceTabs.TransformToVisual(RootLayout).TransformPoint(default).Y
                            - tabsTop
                    ) > .5
                )
                    throw new InvalidOperationException(
                        "Scan introduced a second animation or shifted the workspace."
                    );
                var expected =
                    phase == ScanPhase.Reading
                        ? LoadingAnimationKind.ScanDisk
                        : LoadingAnimationKind.OptimizeDatabase;
                if (
                    ViewLoadingOverlay.Visibility != Visibility.Visible
                    || ViewLoadingAnimation.ActiveKind != expected
                    || ViewHost.IsEnabled
                )
                    throw new InvalidOperationException(
                        "Scan did not use the central loading overlay and the requested phase."
                    );
                ViewLoadingAnimation.PauseForPreview(phase == ScanPhase.Reading ? .54 : .24);
                try
                {
                    await Task.Delay(80, lifetime.Token);
                    RootLayout.UpdateLayout();
                    await CaptureStartupFrameAsync(
                        capture + (phase == ScanPhase.Reading ? ".scan-reading" : ".scan-saving")
                    );
                }
                finally
                {
                    ViewLoadingAnimation.ResumeAfterPreview();
                }
            }
            await SelectDriveAsync(cards[1]);
            if (
                ViewLoadingOverlay.Visibility != Visibility.Collapsed
                || ViewLoadingAnimation.IsPlaying
            )
                throw new InvalidOperationException("Another drive retained the scan animation.");
            await SelectDriveAsync(card);
            if (ViewLoadingOverlay.Visibility != Visibility.Visible)
                throw new InvalidOperationException(
                    "Returning to a scanning drive lost its animation."
                );
            operation.Finished = true;
            var publication = BeginViewUpdate("Publish completed scan");
            await publication
                .DisposeAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
            if (ViewLoadingOverlay.Visibility != Visibility.Visible || !HasScanPresentation)
                throw new InvalidOperationException(
                    "Publication lost the owning scan's animation."
                );
        }
        finally
        {
            scans.Remove(card);
            card.SetBusy(false);
            card.SetProgress(false);
            card.SetResult(sessions.GetValueOrDefault(card.Choice.Drive));
            UpdateScanActivity();
            await ViewLoadingAnimation.WaitForSettledAsync();
        }
        if (ViewLoadingOverlay.Visibility != Visibility.Collapsed || ViewLoadingAnimation.IsPlaying)
            throw new InvalidOperationException("Finished scan retained its progress area.");
        var reduced = new RecordLoadingAnimation();
        reduced.SetKind(LoadingAnimationKind.ScanDisk);
        reduced.SetBusy(true, animationsEnabled: false);
        reduced.SetBusy(false, animationsEnabled: false);
        if (
            reduced.ActiveKind != LoadingAnimationKind.ScanDisk
            || !reduced.WaitForSettledAsync().IsCompleted
        )
            throw new InvalidOperationException(
                "Reduced-motion scan introduced an animation wait."
            );
        var cancelled = new RecordLoadingAnimation();
        cancelled.SetKind(LoadingAnimationKind.ScanDisk);
        cancelled.SetBusy(true, animationsEnabled: true);
        var waiting = cancelled.WaitForSettledAsync();
        cancelled.Cancel();
        if (cancelled.IsPlaying || !waiting.IsCompleted)
            throw new InvalidOperationException("Cancelled scan animation left a waiter.");
    }
}
