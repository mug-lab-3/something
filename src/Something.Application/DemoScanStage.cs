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

    public static string Describe(DemoScanStage stage) =>
        stage switch
        {
            DemoScanStage.Initial => "初期状態",
            DemoScanStage.ResizedAndAdded => "サイズ増減・ファイル追加",
            DemoScanStage.RemovedAndMoved => "ファイル削除・移動",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
}
