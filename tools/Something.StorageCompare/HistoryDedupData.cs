using Something.Application.Scanning;
using Something.Ntfs;
using Something.Statistics;

namespace Something.StorageCompare;

internal static class HistoryDedupData
{
    internal static readonly FileReference Root = FileReference.Create(5, 1);
    internal static readonly DateTimeOffset End = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    // 1 is identical; 2 changes about 1%, 3 about 10%, 4 returns to the original, 5 changes one file.
    internal static CompletedScan Create(
        int count,
        int generation,
        string shape,
        CancellationToken token
    )
    {
        var buffer = new BufferedScan(
            new(
                "synthetic-dedup",
                Guid.Parse($"00000000-0000-0000-0000-{generation + 1:D12}"),
                1,
                new(0, (ulong)count + 32),
                End.AddMinutes(generation)
            )
        );
        Add(
            new(
                Root,
                true,
                CapacityDomain.Ordinary,
                CapacityResolution.Resolved,
                [new(Root, "root", FileNameNamespace.Win32AndDos, 0)],
                [],
                true
            )
        );
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            bool directory = i % 10 == 0;
            var reference = FileReference.Create((ulong)i + 32, (ushort)(i % 20 + 1));
            var parent = directory
                ? shape == "deep" && i != 0
                    ? Directory((i / 10 - 1) / 8 * 10)
                    : Root
                : shape == "wide"
                    ? FileReference.Create(32, 1)
                    : Directory(i / 10 * 10);
            int rate = (generation % 6) switch
            {
                2 => 100,
                3 => 1000,
                _ => 0,
            };
            bool changed =
                !directory && ((generation % 6 == 5 && i == 1) || (long)i * 104729 % 10000 < rate);
            if (changed && i % 127 == 0)
                continue; // Not observed in this generation: never fill from the old state.
            if (changed && i % 113 == 0)
            {
                buffer.Add(
                    new StagedObservation(
                        reference.RecordIndex,
                        MftScanEntryKind.Unresolved,
                        LiveFileReadStatus.Unsupported,
                        null
                    ),
                    token
                );
                continue;
            }
            if (changed && i % 109 == 0)
                reference = FileReference.Create(
                    reference.RecordIndex,
                    (ushort)(reference.SequenceNumber + 1)
                );
            bool unresolved = !directory && i % 97 == 0;
            string name = $"synthetic-{i}" + (i % 1009 == 0 ? "-\ud800\0" : "");
            if (changed && i % 19 == 0)
                name += "-renamed";
            if (changed && i % 23 == 0)
                parent = Root;
            var names = new List<NtfsFileName>
            {
                new(parent, name, FileNameNamespace.Win32, 0),
                new(parent, $"SHORT~{i}", FileNameNamespace.Dos, 0),
            };
            if (!directory && i % 17 == 0)
                names.Add(new(Root, $"link-{i}", FileNameNamespace.Win32AndDos, 0));
            long allocated = 4096L * (1 + i % 100 + (changed ? generation + 1 : 0));
            Add(
                new(
                    reference,
                    directory,
                    CapacityDomain.Ordinary,
                    unresolved ? CapacityResolution.Unresolved : CapacityResolution.Resolved,
                    [.. names],
                    directory || unresolved
                        ? []
                        : [new("", allocated - i % 100, allocated), new("extra", 32, 0)],
                    true
                )
                {
                    ObservedHardLinkCount = (ushort)names.Count,
                }
            );
        }
        return buffer.Complete(token);

        // "deep" nests directories eight per parent, so paths have realistic depth.
        static FileReference Directory(int i) =>
            FileReference.Create((ulong)i + 32, (ushort)(i % 20 + 1));

        void Add(FileCapacityObservation file) =>
            buffer.Add(
                new StagedObservation(
                    file.Reference.RecordIndex,
                    file.Resolution == CapacityResolution.Resolved
                        ? MftScanEntryKind.ResolvedFile
                        : MftScanEntryKind.Unresolved,
                    file.Resolution == CapacityResolution.Resolved
                        ? LiveFileReadStatus.ObservedUnchanged
                        : LiveFileReadStatus.Unsupported,
                    file
                ),
                token
            );
    }

    internal static bool Equal(StagedObservation left, StagedObservation right)
    {
        if (
            left.RecordIndex != right.RecordIndex
            || left.Kind != right.Kind
            || left.Status != right.Status
        )
            return false;
        if (left.File is not { } a || right.File is not { } b)
            return left.File is null && right.File is null;
        return a.Reference == b.Reference
            && a.IsDirectory == b.IsDirectory
            && a.Domain == b.Domain
            && a.Resolution == b.Resolution
            && a.LinkSetComplete == b.LinkSetComplete
            && a.ObservedHardLinkCount == b.ObservedHardLinkCount
            && a.Names.AsSpan().SequenceEqual(b.Names.AsSpan())
            && a.Streams.AsSpan().SequenceEqual(b.Streams.AsSpan());
    }
}
