using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.StorageCompare;

internal static class SyntheticData
{
    internal static readonly DateTimeOffset End = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    internal static readonly FileReference Root = FileReference.Create(5, 1);
    internal const string Volume = "synthetic-storage-comparison";

    internal static BufferedScan Create(int count, int generation, CancellationToken token)
    {
        var ended = End.AddDays(generation == 0 ? -7 : 0);
        var buffer = new BufferedScan(
            new(
                Volume,
                Guid.Parse($"00000000-0000-0000-0000-{generation + 1:D12}"),
                1,
                new(0, (ulong)count + 32),
                ended.AddMinutes(-1)
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
            ulong index = (ulong)i + 32;
            var reference = FileReference.Create(index, (ushort)(i % 20 + 1));
            bool directory = i % 10 == 0;
            var parent = directory
                ? Root
                : FileReference.Create((ulong)(i / 10 * 10) + 32, (ushort)(i / 10 * 10 % 20 + 1));
            bool unresolved = !directory && i % 97 == 0;
            var names = new List<NtfsFileName>
            {
                new(parent, $"synthetic-{i}", FileNameNamespace.Win32, 0),
                new(parent, $"SHORT~{i}", FileNameNamespace.Dos, 0),
            };
            if (!directory && i % 17 == 0)
                names.Add(new(Root, $"link-{i}", FileNameNamespace.Win32AndDos, 0));
            long allocated = 4096L * (1 + i % 100 + (generation == 1 ? i % 5 : 0));
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
        buffer.Complete(token);
        return buffer;

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
}
