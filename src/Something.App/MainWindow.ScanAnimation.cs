using Something.Application;

namespace Something.App;

public sealed partial class MainWindow
{
    private DriveCard? animatedScanCard;

    // Include publication: nested view scopes must not wait for the owning scan to finish.
    private bool HasScanPresentation => selectedCard is { } card && scans.ContainsKey(card);

    private LoadingAnimationKind ScanPresentationKind =>
        !HasScanPresentation ? LoadingAnimationKind.Inspect
        : scans[selectedCard!].Finished ? LoadingAnimationKind.OptimizeDatabase
        : scans[selectedCard!].Progress?.Phase switch
        {
            ScanPhase.Aggregating
            or ScanPhase.Saving
            or ScanPhase.WritingSnapshot
            or ScanPhase.SavingHistory => LoadingAnimationKind.OptimizeDatabase,
            _ => LoadingAnimationKind.ScanDisk,
        };

    private void UpdateScanActivity()
    {
        if (closed)
            return;
        // Reset the previous drive cycle before presenting a newly selected drive.
        if (animatedScanCard != selectedCard)
        {
            animatedScanCard = selectedCard;
            ViewLoadingAnimation.Cancel();
        }
        UpdateViewLoadingPresentation();
    }
}
