namespace Something.Application;

public enum DemoScanStage
{
    Initial,
    ResizedAndAdded,
    RemovedAndMoved,
}

public static class DemoScanScenario
{
    public static DemoScanStage Next(DemoScanStage stage) =>
        stage switch
        {
            DemoScanStage.Initial => DemoScanStage.ResizedAndAdded,
            DemoScanStage.ResizedAndAdded => DemoScanStage.RemovedAndMoved,
            DemoScanStage.RemovedAndMoved => DemoScanStage.Initial,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
}
