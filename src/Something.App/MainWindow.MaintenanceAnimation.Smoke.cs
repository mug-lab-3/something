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
                LoadingAnimationKind.Export,
            }
        )
        {
            bool animated;
            await using (
                BeginViewUpdate(
                    kind switch
                    {
                        LoadingAnimationKind.DeleteHistory => Strings.Get(
                            "HistoryManagement_Deleting"
                        ),
                        LoadingAnimationKind.Export => "Something-Demo.csv",
                        _ => Strings.Get("Database_Optimizing"),
                    },
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
                await Task.Delay(
                    RecordLoadingAnimation.DefaultAppearDelay + TimeSpan.FromMilliseconds(100),
                    lifetime.Token
                );
                ViewLoadingAnimation.PauseForPreview(
                    kind switch
                    {
                        LoadingAnimationKind.DeleteHistory => 1.5,
                        LoadingAnimationKind.Export => .95,
                        _ => .7,
                    }
                );
                try
                {
                    await Task.Delay(80, lifetime.Token);
                    RootLayout.UpdateLayout();
                    if (kind == LoadingAnimationKind.Export)
                    {
                        if (
                            ViewLoadingTitle.Text != Strings.Get("Export_Writing")
                            || ViewLoadingText.Text != "Something-Demo.csv"
                        )
                            throw new InvalidOperationException(
                                "Export did not title its progress with the file name."
                            );
                        ViewLoadingAnimation.VerifyExportPose(.95);
                    }
                    await CaptureStartupFrameAsync(
                        capture
                            + kind switch
                            {
                                LoadingAnimationKind.DeleteHistory => ".history-delete",
                                LoadingAnimationKind.Export => ".export-animation",
                                _ => ".history-optimize",
                            }
                    );
                }
                finally
                {
                    ViewLoadingAnimation.ResumeAfterPreview();
                }
            }
            if (
                ViewLoadingAnimation.IsPlaying
                || (
                    animated
                    && ViewLoadingAnimation.LastPresentedFor < RecordLoadingAnimation.MinimumVisible
                )
            )
                throw new InvalidOperationException(
                    "Maintenance ended before its minimum display time."
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
                // The first cycle starts after the appearance delay.
                await Task.Delay(
                    TimeSpan.FromMilliseconds(3200) + RecordLoadingAnimation.DefaultAppearDelay,
                    lifetime.Token
                );
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
