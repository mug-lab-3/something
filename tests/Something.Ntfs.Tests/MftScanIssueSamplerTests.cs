namespace Something.Ntfs.Tests;

public sealed class MftScanIssueSamplerTests
{
    [Fact]
    public void Add_ManyFailures_RetainsOnlyFirstThreePerStatus()
    {
        var sampler = new MftScanIssueSampler();
        foreach (ulong index in Enumerable.Range(0, 1000).Select(i => (ulong)i))
        {
            sampler.Add(
                new(
                    index,
                    MftScanEntryKind.Unresolved,
                    LiveFileReadStatus.LimitExceeded,
                    Error: "limit"
                )
            );
            sampler.Add(
                new(
                    index,
                    MftScanEntryKind.Unresolved,
                    LiveFileReadStatus.Unsupported,
                    Error: "unsupported"
                )
            );
        }

        var limits = sampler.GetSamples(LiveFileReadStatus.LimitExceeded);
        var unsupported = sampler.GetSamples(LiveFileReadStatus.Unsupported);

        Assert.Equal([0uL, 1uL, 2uL], limits.Select(s => s.RecordIndex));
        Assert.Equal([0uL, 1uL, 2uL], unsupported.Select(s => s.RecordIndex));
        Assert.All(limits, sample => Assert.Equal("limit", sample.Error));
    }

    [Fact]
    public void Add_ResolvedFilesAndExtensions_AreNotRetained()
    {
        var sampler = new MftScanIssueSampler();
        sampler.Add(new(42, MftScanEntryKind.ResolvedFile, LiveFileReadStatus.ObservedUnchanged));
        sampler.Add(
            new(43, MftScanEntryKind.ExtensionRecord, LiveFileReadStatus.ObservedUnchanged)
        );

        Assert.Empty(sampler.GetSamples(LiveFileReadStatus.ObservedUnchanged));
        Assert.Empty(sampler.GetSamples(LiveFileReadStatus.Missing));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void Constructor_UnboundedOrZeroLimit_Rejects(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MftScanIssueSampler(limit));

    [Fact]
    public void Add_UnknownOutcome_RejectsInsteadOfGrowingAnUnboundedDictionary()
    {
        var sampler = new MftScanIssueSampler();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            sampler.Add(new(42, MftScanEntryKind.Unresolved, (LiveFileReadStatus)1000))
        );
        Assert.Throws<ArgumentNullException>(() => sampler.Add(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("capacity unknown")]
    public void Add_MissingOrShortReason_PreservesIt(string? reason)
    {
        var sampler = new MftScanIssueSampler();
        sampler.Add(
            new(42, MftScanEntryKind.Unresolved, LiveFileReadStatus.InvalidAssembly, Error: reason)
        );

        Assert.Equal(
            reason,
            Assert.Single(sampler.GetSamples(LiveFileReadStatus.InvalidAssembly)).Error
        );
    }

    [Fact]
    public void GetSamples_ReturnsAnOwnedSnapshotAndBoundsLongReasons()
    {
        var sampler = new MftScanIssueSampler(16);
        sampler.Add(
            new(
                42,
                MftScanEntryKind.Unresolved,
                LiveFileReadStatus.InvalidRecord,
                Error: new string('x', 10000)
            )
        );
        var before = sampler.GetSamples(LiveFileReadStatus.InvalidRecord);
        sampler.Add(new(43, MftScanEntryKind.Unresolved, LiveFileReadStatus.InvalidRecord));
        var after = sampler.GetSamples(LiveFileReadStatus.InvalidRecord);

        Assert.Single(before);
        Assert.Equal(2, after.Length);
        Assert.Equal(MftScanIssueSampler.MaximumErrorCharacters, before[0].Error!.Length);
        Assert.EndsWith("…", before[0].Error);
    }
}
