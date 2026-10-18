using System.Numerics;
using Something.Application;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class DemoScenarioTests
{
    [Fact]
    public async Task SavedStagesPreserveGrowthShrinkageAdditionDeletionAndMoveIdentity()
    {
        var token = TestContext.Current.CancellationToken;
        string root = Path.Combine(
            Path.GetTempPath(),
            "something-demo-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            var archive = new ScanHistoryArchive(root);
            var scanner = new DriveScanner();
            await using var first = await scanner.CreateDemoAsync(null, token);
            await using var second = await scanner.CreateDemoAsync(
                null,
                token,
                stage: DemoScanStage.ResizedAndAdded
            );
            await using var third = await scanner.CreateDemoAsync(
                null,
                token,
                stage: DemoScanStage.RemovedAndMoved
            );
            var initial = await first.ReadFileAnalysisAsync(token);
            var resized = await second.ReadFileAnalysisAsync(token);
            var moved = await third.ReadFileAnalysisAsync(token);

            Assert.Equal(
                (UInt128)1_073_741_824,
                resized.Single(f => f.Entry.Name == "capture.mp4").Entry.Data!.Value.AllocatedBytes
                    - initial
                        .Single(f => f.Entry.Name == "capture.mp4")
                        .Entry.Data!.Value.AllocatedBytes
            );
            Assert.True(
                resized.Single(f => f.Entry.Name == "source.zip").Entry.Data!.Value.AllocatedBytes
                    < initial
                        .Single(f => f.Entry.Name == "source.zip")
                        .Entry.Data!.Value.AllocatedBytes
            );
            Assert.DoesNotContain(initial, f => f.Entry.Name == "render.mov");
            Assert.Contains(resized, f => f.Entry.Name == "render.mov");
            Assert.DoesNotContain(moved, f => f.Entry.Name == "preview.png");
            var oldZip = resized.Single(f => f.Entry.Name == "source.zip");
            var newZip = moved.Single(f => f.Entry.Name == "source.zip");
            Assert.Equal(oldZip.Entry.Reference, newZip.Entry.Reference);
            Assert.Equal(@"Projects\Archive\source.zip", oldZip.RelativePath);
            Assert.Equal(@"Media\source.zip", newZip.RelativePath);

            var before = await first.SaveHistoryAsync(archive, token);
            var after = await second.SaveHistoryAsync(archive, token);
            var reorganized = await third.SaveHistoryAsync(archive, token);
            var growth = archive.ReadFolderChanges(first.VolumeKey, before.Id, after.Id, token);
            var removal = archive.ReadFolderChanges(
                first.VolumeKey,
                after.Id,
                reorganized.Id,
                token
            );
            Assert.Equal(new BigInteger(805_306_368), growth.Root!.AllocatedDelta);
            Assert.Contains(growth.ChangedFolders, row => row.AllocatedDelta > 0);
            Assert.Contains(growth.ChangedFolders, row => row.AllocatedDelta < 0);
            Assert.Equal(new BigInteger(-12_582_912), removal.Root!.AllocatedDelta);
            await using var restored = await ScanSession.OpenHistoryAsync(
                archive,
                reorganized.Id,
                "Demo",
                token
            );
            var restoredFiles = await restored.ReadFileAnalysisAsync(token);
            Assert.Contains(restoredFiles, f => f.RelativePath == @"Media\source.zip");
            Assert.DoesNotContain(restoredFiles, f => f.Entry.Name == "preview.png");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExplicitStagesRemainReproducibleAndOtherDrivesStayIndependent()
    {
        var token = TestContext.Current.CancellationToken;
        var scanner = new DriveScanner();
        await using var changed = await scanner.CreateDemoAsync(
            null,
            token,
            "DemoA",
            DemoScanStage.ResizedAndAdded
        );
        await using var other = await scanner.CreateDemoAsync(null, token, "DemoB");
        await using var reset = await scanner.CreateDemoAsync(
            null,
            token,
            "DemoA",
            DemoScanScenario.Next(DemoScanStage.RemovedAndMoved)
        );
        var changedFiles = await changed.ReadFileAnalysisAsync(token);
        var otherFiles = await other.ReadFileAnalysisAsync(token);
        var resetFiles = await reset.ReadFileAnalysisAsync(token);

        Assert.Equal(5, changedFiles.Length);
        Assert.Equal(4, otherFiles.Length);
        Assert.Equal(
            otherFiles.Select(f => (f.RelativePath, f.Entry.Data)),
            resetFiles.Select(f => (f.RelativePath, f.Entry.Data))
        );
        Assert.NotEqual(changed.VolumeKey, other.VolumeKey);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            scanner.CreateDemoAsync(null, token, stage: (DemoScanStage)99)
        );
    }
}
