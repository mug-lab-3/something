using System.Collections.Immutable;

namespace Something.Ntfs;

/// <summary>A bounded diagnostic sample, not a retained file observation or scan history.</summary>
public sealed record MftScanIssueSample(ulong RecordIndex, string? Error);

/// <summary>
/// Keeps only a small number of unresolved examples per known outcome.
/// The caller serializes access; resolved records and their metadata are never retained.
/// </summary>
public sealed class MftScanIssueSampler(int maximumSamplesPerStatus = 3)
{
    public const int MaximumErrorCharacters = 1024;
    private readonly int sampleLimit = ValidateLimit(maximumSamplesPerStatus);
    private readonly Dictionary<LiveFileReadStatus, List<MftScanIssueSample>> samples = [];

    private static int ValidateLimit(int limit)
    {
        if (limit is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(maximumSamplesPerStatus));
        return limit;
    }

    public void Add(MftScanEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind != MftScanEntryKind.Unresolved)
            return;
        if (!Enum.IsDefined(entry.Status))
            throw new ArgumentOutOfRangeException(nameof(entry));
        if (!samples.TryGetValue(entry.Status, out var group))
        {
            group = new(sampleLimit);
            samples.Add(entry.Status, group);
        }
        if (group.Count == sampleLimit)
            return;
        string? error = entry.Error;
        if (error is { Length: > MaximumErrorCharacters })
            error = string.Concat(error.AsSpan(0, MaximumErrorCharacters - 1), "…");
        group.Add(new(entry.RecordIndex, error));
    }

    public ImmutableArray<MftScanIssueSample> GetSamples(LiveFileReadStatus status) =>
        samples.TryGetValue(status, out var group) ? [.. group] : [];
}
