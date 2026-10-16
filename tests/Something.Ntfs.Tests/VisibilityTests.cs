using System.Collections.Immutable;
using System.Numerics;
using Something.Application;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class VisibilityTests
{
    private static FileReference Ref(ulong index, ushort sequence = 1) =>
        FileReference.Create(index, sequence);

    private static readonly FileReference Root = Ref(5);
    private static readonly DateTimeOffset Started = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    private static FileCapacityObservation Fact(
        ulong index,
        ulong parent,
        uint? attributes = 0,
        long bytes = 0,
        bool directory = false
    ) =>
        new(
            Ref(index),
            directory,
            CapacityDomain.Ordinary,
            CapacityResolution.Resolved,
            [new(Ref(parent), $"item-{index}", FileNameNamespace.Win32AndDos, 6)],
            [new("", bytes, bytes)],
            true
        )
        {
            StandardFileAttributes = attributes,
        };

    private static FileCapacityObservation[] Facts(
        long ordinaryBytes = 10,
        long hiddenBytes = 20
    ) =>
        [
            Fact(5, 5, 6, directory: true),
            Fact(20, 5, directory: true),
            Fact(21, 20, 2, directory: true),
            Fact(22, 21, directory: true),
            Fact(23, 20, 4, directory: true),
            Fact(100, 20, bytes: ordinaryBytes),
            Fact(101, 20, 2, hiddenBytes),
            Fact(102, 20, 4, 30),
            Fact(103, 20, 6, 40),
            Fact(104, 22, bytes: 50),
            Fact(105, 23, bytes: 60),
            Fact(106, 20, null, 70),
            Fact(107, 20, bytes: 80) with
            {
                Names =
                [
                    new(Ref(20), "visible-link", FileNameNamespace.Win32AndDos, 0),
                    new(Ref(22), "excluded-link", FileNameNamespace.Win32, 0),
                    new(Ref(22), "EXCLUD~1", FileNameNamespace.Dos, 0),
                ],
                ObservedHardLinkCount = 2,
            },
        ];

    [Theory]
    [InlineData(true, true, 440)]
    [InlineData(false, true, 310)]
    [InlineData(true, false, 250)]
    [InlineData(false, false, 160)]
    public void AggregationFiltersAttributesAncestorsAndOnlyExcludedHardLinkNames(
        bool system,
        bool hidden,
        int expected
    )
    {
        var facts = Facts();
        var before = facts.ToImmutableArray();
        var result = new VisibleScan(
            Root,
            () => facts,
            new(system, hidden),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            (UInt128)expected,
            result.Aggregation.Folders[Root].Entries!.SubtreeData.AllocatedBytes
        );
        Assert.Equal(!hidden, result.ExcludedFolders.Contains(Ref(22)));
        Assert.Equal(!system, result.ExcludedFolders.Contains(Ref(23)));
        Assert.Equal(before, facts.ToImmutableArray());
        Assert.True(result.Aggregation.Folders[Root].DirectoryCapacityObserved);
        Assert.False(result.Aggregation.HasUnknownAttribution);
    }

    [Fact]
    public void UnknownAttributesAndUnresolvedCapacityRemainVisibleAndUncertain()
    {
        var facts = Facts()
            .Append(
                Fact(108, 20, null) with
                {
                    Resolution = CapacityResolution.Unresolved,
                    Streams = [],
                }
            )
            .ToArray();
        var result = new VisibleScan(
            Root,
            () => facts,
            new(false, false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1UL, result.Aggregation.Folders[Root].Entries!.SubtreeUnresolvedCount);
        Assert.Equal(
            (UInt128)160,
            result.Aggregation.Folders[Root].Entries!.SubtreeData.AllocatedBytes
        );
    }

    [Fact]
    public void ParentSequenceReuseIsNotMistakenForExcludedParent()
    {
        var facts = Facts()
            .Append(
                Fact(108, 21, bytes: 90) with
                {
                    Names = [new(Ref(21, 2), "reused", FileNameNamespace.Win32, 0)],
                }
            )
            .ToArray();
        var result = new VisibleScan(
            Root,
            () => facts,
            new(false, false),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Aggregation.HasUnknownAttribution);
        Assert.Equal(1UL, result.Aggregation.Unattributed.ResolvedCount);
    }

    [Fact]
    public void ProjectionHonorsCancellationBeforeReadingFacts()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new VisibleScan(
                Root,
                () => throw new InvalidOperationException("Must not read"),
                new(false, false),
                cancellation.Token
            )
        );
    }

    private static async Task<ScanSession> CreateSession(
        FileCapacityObservation[] facts,
        CancellationToken token
    )
    {
        var database = TemporaryScanDatabase.Create(token, inMemory: true);
        try
        {
            var id = database.Store.BeginScan(
                new("synthetic-visibility", Guid.NewGuid(), 1, new(0, 2000), Started),
                token
            );
            database.Store.StageBatch(
                id,
                facts
                    .Select(f => new StagedObservation(
                        f.Reference.RecordIndex,
                        f.Resolution == CapacityResolution.Resolved
                            ? MftScanEntryKind.ResolvedFile
                            : MftScanEntryKind.Unresolved,
                        f.Resolution == CapacityResolution.Resolved
                            ? LiveFileReadStatus.ObservedUnchanged
                            : LiveFileReadStatus.Unsupported,
                        f
                    ))
                    .ToArray(),
                token
            );
            database.Store.CompleteScan(id, Started.AddSeconds(1), token);
            var summary = database.Store.ReadSnapshotSummary(id, token);
            return new(
                database,
                id,
                "Z:",
                Root,
                summary.Root,
                summary.MetadataUnresolved,
                DiagnosticDatabaseSelector.Select(1, null),
                true,
                TimeSpan.FromSeconds(1)
            );
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    [Fact]
    public async Task BrowserAnalysisAndRootUseFilteredSizesAndRestoringAllNeedsNoRescan()
    {
        var token = TestContext.Current.CancellationToken;
        await using var session = await CreateSession(Facts(), token);
        await session.SetVisibilityAsync(new(false, false), token);
        var page = await session.ReadFolderAsync(Ref(20), 0, false, token, includeTotalCount: true);
        var analysis = await session.ReadFolderAnalysisAsync(new(LeafOnly: true), token);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(
            new ulong[] { 107, 106, 100 },
            page.Entries.Select(e => e.Reference.RecordIndex)
        );
        Assert.Equal("item-20", Assert.Single(analysis).RelativePath);
        Assert.Equal((UInt128)160, session.RootSample.Entries!.SubtreeData.AllocatedBytes);
        Assert.Equal((UInt128)160, analysis[0].Data.AllocatedBytes);
        Assert.Empty((await session.ReadFolderAsync(Ref(22), 0, false, token)).Entries);

        await session.SetVisibilityAsync(new(), token);
        Assert.Equal((UInt128)440, session.RootSample.Entries!.SubtreeData.AllocatedBytes);
        Assert.Equal(
            8,
            (
                await session.ReadFolderAsync(Ref(20), 0, false, token, includeTotalCount: true)
            ).TotalCount
        );
        Assert.Equal(4, (await session.ReadFolderAnalysisAsync(new(), token)).Length);
    }

    [Fact]
    public async Task FilterBeforePaginationAndSortingKeepsAllVisibleRowsWithoutDuplicates()
    {
        var token = TestContext.Current.CancellationToken;
        var facts = new[] { Fact(5, 5, directory: true) }
            .Concat(
                Enumerable.Range(100, 600).Select(i => Fact((ulong)i, 5, i % 2 == 0 ? 2u : 0u, i))
            )
            .ToArray();
        await using var session = await CreateSession(facts, token);
        await session.SetVisibilityAsync(new(true, false), token);
        var first = await session.ReadFolderAsync(
            Root,
            0,
            false,
            token,
            includeTotalCount: true,
            descending: false
        );
        var second = await session.ReadFolderAsync(Root, 1, false, token, descending: false);

        Assert.Equal(300, first.TotalCount);
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(250, first.Entries.Length);
        Assert.Equal(50, second.Entries.Length);
        var entries = first.Entries.Concat(second.Entries).ToArray();
        Assert.Equal(300, entries.Select(e => e.Reference).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(100, 600).Where(i => i % 2 != 0).Select(i => (ulong)i),
            entries.Select(e => e.Reference.RecordIndex)
        );
        var reverse = await session.ReadFolderAsync(Root, 0, false, token);
        Assert.Equal(699UL, reverse.Entries[0].Reference.RecordIndex);
    }

    [Fact]
    public async Task FilteredViewNeverChangesSavedFactsOrArchivedTotals()
    {
        var token = TestContext.Current.CancellationToken;
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"something-visibility-{Guid.NewGuid():N}"
        );
        try
        {
            var archive = new ScanHistoryArchive(directory);
            await using var source = await CreateSession(Facts(), token);
            await source.SetVisibilityAsync(new(false, false), token);
            var entry = await source.SaveHistoryAsync(archive, token);
            await using var restored = await ScanSession.OpenHistoryAsync(
                archive,
                entry.Id,
                "Z:",
                token
            );
            Assert.Equal((UInt128)440, restored.RootSample.Entries!.SubtreeData.AllocatedBytes);
            Assert.Equal(
                8,
                (
                    await restored.ReadFolderAsync(
                        Ref(20),
                        0,
                        false,
                        token,
                        includeTotalCount: true
                    )
                ).TotalCount
            );
            await restored.SetVisibilityAsync(new(false, false), token);
            Assert.Equal((UInt128)160, restored.RootSample.Entries!.SubtreeData.AllocatedBytes);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ComparisonFiltersBothEndpointsInsteadOfCountingHiddenGrowth()
    {
        var token = TestContext.Current.CancellationToken;
        await using var before = await CreateSession(Facts(), token);
        await using var after = await CreateSession(Facts(15, 2000), token);
        var report = new FolderChangeReport(
            "synthetic-visibility",
            FolderChangePeriod.Custom,
            Started.AddSeconds(2),
            Started,
            null,
            null,
            FolderComparisonAvailability.Available,
            []
        );

        var filtered = await ScanSession.ApplyVisibilityToComparisonAsync(
            report,
            before,
            after,
            new(false, false),
            token
        );

        Assert.Equal(new BigInteger(5), filtered.Root!.AllocatedDelta);
        Assert.Equal(new BigInteger(5), Assert.Single(filtered.Folders).AllocatedDelta);
        Assert.Equal((UInt128)165, after.RootSample.Entries!.SubtreeData.AllocatedBytes);
        Assert.Equal(FolderChangeStatus.Comparable, filtered.Root.Status);
    }

    [Fact]
    public async Task CancelledSettingsChangePreservesPreviousReadModel()
    {
        var token = TestContext.Current.CancellationToken;
        await using var session = await CreateSession(Facts(), token);
        await session.SetVisibilityAsync(new(false, false), token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.SetVisibilityAsync(new(), cancellation.Token)
        );
        Assert.Equal((UInt128)160, session.RootSample.Entries!.SubtreeData.AllocatedBytes);
    }

    [Fact]
    public void PreferencesPersistIndependentlyOfDatabaseAndMalformedJsonUsesDefaults()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"something-settings-{Guid.NewGuid():N}"
        );
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new DisplaySettingsStore(path);
            Assert.Equal(new(), store.Load());
            store.Save(new(false, true));
            Assert.Equal(new(false, true), new DisplaySettingsStore(path).Load());
            File.WriteAllText(path, "{broken");
            Assert.Equal(new(), store.Load());
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
