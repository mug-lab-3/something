using Something.Ntfs;

internal static class RecordDiagnostics
{
    public static async Task PrintAsync(
        IMftRecordSource source,
        ulong index,
        CancellationToken token
    )
    {
        // Separate, bounded diagnostic rereads; these do not alter the scan or capacity result.
        var first = await source.ReadAsync(index, token);
        if (first is null)
        {
            Console.WriteLine("  Diagnostic reread: record unavailable.");
            return;
        }
        if (first.Value.Length > MftRecordParser.MaximumRecordSize)
        {
            Console.WriteLine("  Diagnostic reread: oversized record.");
            return;
        }
        byte[] owned = first.Value.ToArray();
        var second = await source.ReadAsync(index, token);
        token.ThrowIfCancellationRequested();
        bool equal = second is { } current && owned.AsSpan().SequenceEqual(current.Span);
        Console.WriteLine(
            $"  Diagnostic reread: bytes-equal={equal}; declared-encoding={source.Encoding}"
        );
        if (MftFixupDiagnostics.Inspect(owned) is { } summary)
            Console.WriteLine(
                $"  Fixup summary: sectors={summary.SectorCount}; protected-matches={summary.ProtectedTailMatches}; saved-matches={summary.SavedTailMatches}; other-tails={summary.OtherTails}; saved-mismatch-inside-used={summary.SavedMismatchesWithinUsedBytes}; saved-mismatch-outside-used={summary.SavedMismatchesOutsideUsedBytes}"
            );
        else
            Console.WriteLine("  Fixup summary unavailable: invalid diagnostic header.");

        // No filename, stream name, LCN, resident contents, or raw bytes are printed.
        if (new MftRecordParser().TryParse(owned, index, out var record, out _, source.Encoding))
        {
            int count = 0;
            foreach (
                var attribute in record!.Attributes.Where(a => a.Type == NtfsAttributeTypes.Data)
            )
            {
                if (count++ == 8)
                {
                    Console.WriteLine("  More record DATA attributes omitted.");
                    break;
                }
                if (attribute is NonResidentAttribute extent)
                    Console.WriteLine(
                        $"  Record DATA: named={extent.Name.Length != 0}; lowest-vcn={extent.LowestVcn}; highest-vcn={extent.HighestVcn}; flags={extent.Flags}; compression-unit={extent.CompressionUnit}; runs={extent.Runs.Length}"
                    );
                else
                    Console.WriteLine(
                        $"  Record DATA: named={attribute.Name.Length != 0}; resident=True"
                    );
            }
            Console.WriteLine(
                "  Other record segments are not included in this diagnostic reread."
            );
        }
    }
}
