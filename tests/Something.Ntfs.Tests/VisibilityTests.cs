using System.Collections.Immutable;
using System.Numerics;
using Something.Application;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class VisibilityTests
{
    [Fact]
    public void CompactDirectoryFactsAvoidASecondFullObservationRead()
    {
        var facts = Facts();
        int fullReads = 0;
        var directories = facts
            .Where(f => f.IsDirectory)
            .Select(f => new FolderVisibilityObservation(
                f.Reference,
                [
                    .. f
                        .Names.Where(n => n.Namespace != FileNameNamespace.Dos)
                        .Select(n => n.Parent)
                        .Distinct(),
                ],
                f.StandardFileAttributes
            ));
        var projection = new VisibleScan(
            Root,
            () =>
            {
                fullReads++;
                return facts;
            },
            new(false, false),
            TestContext.Current.CancellationToken,
            directories
        );

        Assert.Equal(1, fullReads);
        Assert.Equal(
            (UInt128)160,
            projection.Aggregation.Folders[Root].Entries!.SubtreeData.AllocatedBytes
        );
    }

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
            var entry = await source.SaveHistoryAsync(HistoryService.ForArchive(archive), token);
            await using var restored = await HistoryService
                .ForArchive(archive)
                .OpenAsync(entry.Id, "Z:", token);
            Assert.Equal(9, (await restored.ReadFileAnalysisAsync(token)).Length);
            await restored.SetVisibilityAsync(new(false, false), token);
            Assert.Equal(3, (await restored.ReadFileAnalysisAsync(token)).Length);
            await restored.SetVisibilityAsync(new(), token);
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
    public async Task ReturningToEachSettingReusesItsProjectionRootListingAndAnalysis()
    {
        var token = TestContext.Current.CancellationToken;
        await using var session = await CreateSession(Facts(), token);
        (FileVisibilityOptions Options, UInt128 Bytes)[] choices =
        [
            (new(), 440),
            (new(false, true), 310),
            (new(true, false), 250),
            (new(false, false), 160),
        ];
        var cached =
            new Dictionary<
                FileVisibilityOptions,
                (FolderCapacitySample Root, FolderEntryPage Page, RankedFolder[] Analysis)
            >();
        foreach (var (options, bytes) in choices)
        {
            await session.SetVisibilityAsync(options, token);
            var page = await session.ReadFolderAsync(
                Root,
                0,
                false,
                token,
                includeTotalCount: true
            );
            var analysis = await session.ReadFolderAnalysisAsync(new(), token);
            Assert.Equal(bytes, session.RootSample.Entries!.SubtreeData.AllocatedBytes);
            cached.Add(options, (session.RootSample, page, analysis));
        }
        foreach (var (options, bytes) in choices)
        {
            await session.SetVisibilityAsync(options, token);
            Assert.Same(cached[options].Root, session.RootSample);
            Assert.Equal(bytes, session.RootSample.Entries!.SubtreeData.AllocatedBytes);
            Assert.Same(
                cached[options].Page,
                await session.ReadFolderAsync(Root, 0, false, token, includeTotalCount: true)
            );
            var analysis = await session.ReadFolderAnalysisAsync(new(), token);
            Assert.NotSame(cached[options].Analysis, analysis);
            Assert.All(
                cached[options].Analysis.Zip(analysis),
                pair => Assert.Same(pair.First, pair.Second)
            );
        }
    }

    [Fact]
    public async Task DistinctScanSessionsNeverReuseAnotherScansCachedTotals()
    {
        var token = TestContext.Current.CancellationToken;
        await using var first = await CreateSession(Facts(), token);
        await using var second = await CreateSession(Facts(20, 40), token);
        await first.SetVisibilityAsync(new(false, false), token);
        var originalRoot = first.RootSample;
        var originalPage = await first.ReadFolderAsync(Root, 0, false, token);
        var originalAnalysis = await first.ReadFolderAnalysisAsync(new(), token);
        await second.SetVisibilityAsync(new(false, false), token);
        Assert.NotEqual(first.ScanId, second.ScanId);
        Assert.NotSame(originalRoot, second.RootSample);
        Assert.Equal((UInt128)170, second.RootSample.Entries!.SubtreeData.AllocatedBytes);
        Assert.Equal(
            (UInt128)170,
            Assert.Single(await second.ReadFolderAnalysisAsync(new(), token)).Data.AllocatedBytes
        );
        Assert.Same(originalRoot, first.RootSample);
        Assert.Same(originalPage, await first.ReadFolderAsync(Root, 0, false, token));
        Assert.Same(
            originalAnalysis[0],
            Assert.Single(await first.ReadFolderAnalysisAsync(new(), token))
        );
        Assert.Equal((UInt128)160, first.RootSample.Entries!.SubtreeData.AllocatedBytes);
    }

    [Fact]
    public async Task CachedAnalysisStillAppliesDepthLeafAndSortAndDoesNotExposeItsArray()
    {
        var token = TestContext.Current.CancellationToken;
        await using var session = await CreateSession(Facts(), token);
        var all = await session.ReadFolderAnalysisAsync(new(), token);
        Array.Reverse(all);
        Assert.Equal(
            Ref(20),
            (await session.ReadFolderAnalysisAsync(new(), token))[0].Sample.Reference
        );
        var leaves = await session.ReadFolderAnalysisAsync(new(LeafOnly: true), token);
        Assert.Equal(
            new[] { Ref(22), Ref(23) },
            leaves.Select(r => r.Sample.Reference).OrderBy(r => r.RecordIndex)
        );
        Assert.Single(await session.ReadFolderAnalysisAsync(new(Depth: 1), token));
        await session.SetVisibilityAsync(new(false, false), token);
        Assert.Single(await session.ReadFolderAnalysisAsync(new(Depth: 1, LeafOnly: true), token));
        Assert.Empty(await session.ReadFolderAnalysisAsync(new(Depth: 2), token));
        await session.SetVisibilityAsync(new(), token);
        var reversed = await session.ReadFolderAsync(Root, 0, false, token, descending: false);
        Assert.Equal(Ref(20), Assert.Single(reversed.Entries).Reference);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ReadFolderAsync(Root, 0, false, cancellation.Token)
        );
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.ReadFolderAsync(Root, 0, false, token)
        );
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.ReadFolderAnalysisAsync(new(), token)
        );
    }

    [Theory]
    [InlineData(true, true, 9, 440)]
    [InlineData(false, true, 6, 310)]
    [InlineData(true, false, 5, 250)]
    [InlineData(false, false, 3, 160)]
    public async Task RecursiveFileAnalysisRetainsVisibleHardLinkNamesAndMatchesPerNameAccounting(
        bool system,
        bool hidden,
        int count,
        int bytes
    )
    {
        var token = TestContext.Current.CancellationToken;
        await using var session = await CreateSession(Facts(), token);
        var original = await session.ReadFileAnalysisAsync(token);
        await session.SetVisibilityAsync(new(system, hidden), token);
        var files = await session.ReadFileAnalysisAsync(token);
        Assert.Equal(count, files.Length);
        Assert.Equal(
            (UInt128)bytes,
            files.Aggregate((UInt128)0, (total, f) => total + f.Entry.Data!.Value.AllocatedBytes)
        );
        Assert.Equal((UInt128)bytes, session.RootSample.Entries!.SubtreeData.AllocatedBytes);
        Assert.All(files, file => Assert.False(file.Entry.IsDirectory));
        Assert.Equal(hidden ? 2 : 1, files.Count(file => file.Entry.Reference == Ref(107)));
        Assert.DoesNotContain(
            files,
            file => file.RelativePath.Contains("EXCLUD~1", StringComparison.Ordinal)
        );
        Array.Reverse(original);
        await session.SetVisibilityAsync(new(), token);
        var restored = await session.ReadFileAnalysisAsync(token);
        Assert.Equal(9, restored.Length);
        Assert.Same(
            original.Single(f => f.Entry.Reference == Ref(100)),
            restored.Single(f => f.Entry.Reference == Ref(100))
        );
    }

    [Fact]
    public async Task FileAnalysisRejectsReusedParentSequenceAndPreservesUnresolvedSize()
    {
        var token = TestContext.Current.CancellationToken;
        var facts = Facts()
            .Append(
                Fact(108, 20) with
                {
                    Names = [new(Ref(20, 2), "reused", FileNameNamespace.Win32, 0)],
                }
            )
            .Append(Fact(109, 20) with { Resolution = CapacityResolution.Unresolved, Streams = [] })
            .ToArray();
        await using var session = await CreateSession(facts, token);
        var files = await session.ReadFileAnalysisAsync(token);
        Assert.DoesNotContain(files, file => file.Entry.Reference == Ref(108));
        var unknown = Assert.Single(files, file => file.Entry.Reference == Ref(109));
        Assert.Null(unknown.Entry.Data);
        Assert.True(unknown.Entry.CapacityIncomplete);
        Assert.Null(CapacityAnalysisItem.FromFile(unknown).AllocatedBytes);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.ReadFileAnalysisAsync(token)
        );
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
