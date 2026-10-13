using Something.Application;

namespace Something.Ntfs.Tests;

public sealed class ScanSessionTests
{
    [Fact]
    public async Task DemoSession_BrowsesRanksAndSerializesQueriesWithoutVolumeAccess()
    {
        var token = TestContext.Current.CancellationToken;
        await using var session = await new DriveScanner().CreateDemoAsync(null, token);

        var queries = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(_ => session.ReadFolderAsync(session.Root, 0, false, token))
        );
        var ranking = await session.ReadLargestAsync(new(1), token);

        Assert.True(session.IsDemo);
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
            new DriveScanner().CreateDemoAsync(null, cancellation.Token)
        );
    }
}
