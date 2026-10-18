namespace Something.App;

public sealed partial class MainWindow
{
    private async Task VerifyMaintenanceAnimationsAsync()
    {
        await ViewLoadingAnimation.WaitForSettledAsync();
        var args = Environment.GetCommandLineArgs();
        string capture = args[Array.IndexOf(args, "--smoke-test") + 1];
        foreach (
            var kind in new[]
            {
                LoadingAnimationKind.DeleteHistory,
                LoadingAnimationKind.OptimizeDatabase,
            }
        )
        {
            int cycles = ViewLoadingAnimation.CompletedCycles;
            bool animated;
            await using (
                BeginViewUpdate(
                    kind == LoadingAnimationKind.DeleteHistory
                        ? "スキャン履歴を削除しています…"
                        : "DBを最適化しています…",
                    kind
                )
            )
            {
                animated = ViewLoadingAnimation.IsPlaying;
                using (BeginViewUpdate("Nested history refresh"))
                {
                    if (ViewLoadingAnimation.ActiveKind != kind)
                        throw new InvalidOperationException(
                            "Nested refresh replaced the maintenance animation."
                        );
                }
                ViewLoadingAnimation.PauseForPreview(.7);
                try
                {
                    await Task.Delay(80, lifetime.Token);
                    RootLayout.UpdateLayout();
                    await CaptureStartupFrameAsync(
                        capture
                            + (
                                kind == LoadingAnimationKind.DeleteHistory
                                    ? ".history-delete"
                                    : ".history-optimize"
                            )
                    );
                }
                finally
                {
                    ViewLoadingAnimation.ResumeAfterPreview();
                }
            }
            if (
                ViewLoadingAnimation.IsPlaying
                || (animated && ViewLoadingAnimation.CompletedCycles != cycles + 1)
            )
                throw new InvalidOperationException(
                    "Maintenance did not finish at its cycle boundary."
                );

            var reduced = new RecordLoadingAnimation();
            reduced.SetKind(kind);
            reduced.SetBusy(true, animationsEnabled: false);
            reduced.SetBusy(false, animationsEnabled: false);
            if (reduced.ActiveKind != kind || !reduced.WaitForSettledAsync().IsCompleted)
                throw new InvalidOperationException(
                    "Reduced-motion maintenance introduced a wait."
                );
            var cancelled = new RecordLoadingAnimation();
            cancelled.SetKind(kind);
            cancelled.SetBusy(true, animationsEnabled: true);
            var waiter = cancelled.WaitForSettledAsync();
            cancelled.Cancel();
            if (cancelled.IsPlaying || !waiter.IsCompleted)
                throw new InvalidOperationException(
                    "Cancelled maintenance left an animation waiter."
                );
        }
        // Change task at a clean cycle boundary, rather than resetting a chick mid-walk.
        await using (BeginViewUpdate("Delete", LoadingAnimationKind.DeleteHistory))
        {
            await using (BeginViewUpdate("Optimize", LoadingAnimationKind.OptimizeDatabase))
            {
                await Task.Delay(1500, lifetime.Token);
                if (
                    ViewLoadingAnimation.IsPlaying
                    && ViewLoadingAnimation.ActiveKind != LoadingAnimationKind.OptimizeDatabase
                )
                    throw new InvalidOperationException(
                        "Maintenance did not switch after a full cycle."
                    );
            }
        }
    }
}
