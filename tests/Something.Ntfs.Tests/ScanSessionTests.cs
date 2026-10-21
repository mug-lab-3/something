using Something.Application;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class ScanSessionTests
{
    [Fact]
    public async Task DemoForwardsAggregationAndSavingProgressBeforeCompletion()
    {
        var token = TestContext.Current.CancellationToken;
        var updates = new List<ScanProgress>();

        await using var session = await new DriveScanner(
            StorageRepositories.TemporaryScans
        ).CreateDemoAsync(new SynchronousProgress(updates.Add), token);

        Assert.Contains(updates, p => p.Phase == ScanPhase.Aggregating && p.Fraction is > 0);
        Assert.Contains(updates, p => p.Phase == ScanPhase.Saving && p.Fraction is > 0);
        Assert.Contains(
            updates,
            p =>
                p.Work?.Stage == Something.Application.Scanning.ScanWorkStage.Committing
                && p.Fraction is null
        );
        Assert.Equal(ScanPhase.Completed, updates[^1].Phase);
        Assert.Equal(1, updates[^1].Fraction);
        Assert.All(updates, p => Assert.Equal(13, p.Step.Total));
        Assert.Contains(updates, p => p.Step.Number == 4 && p.Phase == ScanPhase.Aggregating);
        Assert.Contains(updates, p => p.Step.Number == 7 && p.Phase == ScanPhase.Saving);
        Assert.Equal(new ScanStep(13, 13), updates[^1].Step);
        Assert.True(
            updates
                .Zip(updates.Skip(1))
                .All(pair => pair.First.Step.Number <= pair.Second.Step.Number)
        );
    }

    [Fact]
    public void HistoryPlanIncludesRetentionWithoutRestartingStepNumbersAtEachPhase()
    {
        var collection = new ScanProgress(ScanPhase.Aggregating, 100, 0.5, TimeSpan.Zero)
        {
            SavesHistory = true,
            Work = new(Something.Application.Scanning.ScanWorkStage.CollectingFolders, 50, 100),
        };
        var writing = collection with
        {
            Phase = ScanPhase.SavingHistory,
            Work = new(Something.Application.Scanning.ScanWorkStage.WritingFolders, 50, 100),
        };
        var native = writing with
        {
            Fraction = null,
            Work = new(Something.Application.Scanning.ScanWorkStage.BuildingFolderList, 0, null),
        };
        var retention = native with
        {
            Work = new(Something.Application.Scanning.ScanWorkStage.MaintainingHistory, 0, null),
        };

        Assert.Equal(new ScanStep(2, 14), collection.Step);
        Assert.Equal(new ScanStep(8, 14), writing.Step);
        Assert.Equal(0.5, writing.Fraction);
        Assert.Equal(new ScanStep(12, 14), native.Step);
        Assert.Null(native.Fraction);
        Assert.Equal(new ScanStep(14, 14), retention.Step);
        Assert.Null(retention.Fraction);
        Assert.Equal(new ScanStep(14, 14), (retention with { Phase = ScanPhase.Completed }).Step);
        Assert.Equal(
            new ScanStep(0, 14),
            (collection with { Phase = ScanPhase.WaitingForMemory }).Step
        );
    }

    private sealed class SynchronousProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

    [Fact]
    public async Task DemoSession_BrowsesRanksAndSerializesQueriesWithoutVolumeAccess()
    {
        var token = TestContext.Current.CancellationToken;
        var beforeScan = DateTimeOffset.UtcNow;
        await using var session = await new DriveScanner(
            StorageRepositories.TemporaryScans
        ).CreateDemoAsync(null, token);

        var queries = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(_ => session.ReadFolderAsync(session.Root, 0, false, token))
        );
        var ranking = await session.ReadFolderAnalysisAsync(new(1), token);

        Assert.True(session.IsDemo);
        Assert.InRange(
            session.CompletedLocal!.Value.ToUniversalTime(),
            beforeScan,
            DateTimeOffset.UtcNow
        );
        Assert.All(
            queries,
            q =>
            {
                Assert.Equal(3, q.Entries.Length);
                Assert.False(q.HasMore);
            }
        );
        Assert.Equal(2, ranking.Length);
        Assert.Equal("Media", ranking[0].RelativePath);
        var projects = queries[0].Entries.Single(e => e.Name == "Projects");
        Assert.True(projects.CanBrowse);
        var children = await session.ReadFolderAsync(projects.Reference, 0, false, token);
        Assert.Contains(children.Entries, e => e.Name == "preview.png" && e.Data is not null);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.ReadFolderAsync(session.Root, 0, false, token)
        );
    }

    [Fact]
    public async Task DemoSession_CancellationDoesNotReturnAnIncompleteGeneration()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DriveScanner(StorageRepositories.TemporaryScans).CreateDemoAsync(
                null,
                cancellation.Token
            )
        );
    }
}
