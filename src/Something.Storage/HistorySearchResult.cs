using System.Collections.Immutable;
using System.Numerics;
using Something.Ntfs;

namespace Something.Storage;

public enum HistorySearchStatus
{
    Completed,
    NoScans,
    ChangesNeedTwoScans,
}

/// <summary>One visible name observed in the scans of a search. Sizes use the criteria's size kind.
/// Scan positions index <see cref="HistorySearchResult.Scans"/>; unresolved sizes stay null.</summary>
public sealed record SearchItem(
    FileReference Reference,
    FileReference Parent,
    string Name,
    bool IsDirectory,
    uint? StandardFileAttributes,
    int FirstObserved,
    int LastObserved,
    int ObservedCount,
    UInt128? FirstSize,
    UInt128? Size,
    UInt128? MinimumKnownSize,
    UInt128? MaximumKnownSize,
    bool HasUnresolvedSize
)
{
    /// <summary>False for folders shown only because a match lies below them.</summary>
    public bool Matched { get; init; }

    /// <summary>A condition could not be decided because a size was unresolved.</summary>
    public bool Uncertain { get; init; }

    /// <summary>True when the item is the only visible name of its identity, so it follows
    /// renames and moves; false for a hard link name, which continues only in place.</summary>
    internal bool FollowsIdentity { get; init; }

    public bool InFirstScan => FirstObserved == 0;

    /// <summary>Size at the last observation minus size at the first scan of the range.</summary>
    public BigInteger? Delta =>
        InFirstScan && FirstSize is { } first && Size is { } last
            ? (BigInteger)last - (BigInteger)first
            : null;
}

/// <summary>A size observation of one item in one scan of a search.</summary>
public sealed record SearchSizeObservation(
    int ScanIndex,
    FileReference? Parent,
    string? Name,
    CapacityObservationState State,
    UInt128? AllocatedBytes,
    UInt128? LogicalBytes
);

public enum CapacityObservationState
{
    Known,
    Unresolved,
    NotObserved,
}

/// <summary>Owns leases on every scan of the range until disposed.</summary>
public sealed class HistorySearchResult : IDisposable
{
    private readonly Dictionary<FileReference, SearchItem> folders;
    private readonly Dictionary<FileReference, List<FileReference>> childFolders;
    private readonly Dictionary<FileReference, List<SearchItem>> files;
    private readonly Action release;
    private bool disposed;

    internal HistorySearchResult(
        string volumeKey,
        HistorySearchCriteria criteria,
        HistorySearchStatus status,
        ImmutableArray<ScanHistoryEntry> scans,
        ImmutableArray<int> generations,
        SearchTree tree,
        Action release
    )
    {
        VolumeKey = volumeKey;
        Criteria = criteria;
        Status = status;
        Scans = scans;
        Generations = generations;
        Root = tree.Root;
        folders = tree.Folders;
        childFolders = tree.ChildFolders;
        files = tree.Files;
        MatchedFolders = tree.MatchedFolders;
        MatchedFiles = tree.MatchedFiles;
        UncertainCount = tree.UncertainCount;
        UnknownAttributeCount = tree.UnknownAttributeCount;
        this.release = release;
    }

    public string VolumeKey { get; }
    public HistorySearchCriteria Criteria { get; }
    public HistorySearchStatus Status { get; }

    /// <summary>Retained scans of the range, oldest first. The first is F and the last is L.</summary>
    public ImmutableArray<ScanHistoryEntry> Scans { get; }
    internal ImmutableArray<int> Generations { get; }

    /// <summary>The volume root as observed in the last scan; it is never an item itself.</summary>
    public FileReference Root { get; }
    public long MatchedFolders { get; }
    public long MatchedFiles { get; }

    /// <summary>Matches whose size, change or trend could not be decided; they are kept and marked.</summary>
    public long UncertainCount { get; }

    /// <summary>Items excluded only because their attributes were unknown.</summary>
    public long UnknownAttributeCount { get; }

    public SearchItem? Folder(FileReference reference) => folders.GetValueOrDefault(reference);

    /// <summary>Folders below <paramref name="parent"/> that are matched or contain a match, by name.</summary>
    public ImmutableArray<SearchItem> ChildFolders(FileReference parent) =>
        [
            .. childFolders
                .GetValueOrDefault(parent, [])
                .Select(reference => folders[reference])
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Reference.RecordIndex),
        ];

    /// <summary>The tree folders and matched files directly below <paramref name="parent"/>.</summary>
    public ImmutableArray<SearchItem> Entries(FileReference parent) =>
        [
            .. ChildFolders(parent),
            .. files
                .GetValueOrDefault(parent, [])
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Reference.RecordIndex),
        ];

    /// <summary>Display path relative to the volume root; empty for the root.</summary>
    public string PathOf(FileReference folder)
    {
        var names = new List<string>();
        for (
            var current = folder;
            current != Root && folders.TryGetValue(current, out var item);
            current = item.Parent
        )
            names.Add(item.Name);
        names.Reverse();
        return string.Join('\\', names);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        release();
    }
}

internal sealed class SearchTree
{
    internal FileReference Root { get; set; }
    internal Dictionary<FileReference, SearchItem> Folders { get; } = [];
    internal Dictionary<FileReference, List<FileReference>> ChildFolders { get; } = [];
    internal Dictionary<FileReference, List<SearchItem>> Files { get; } = [];
    internal long MatchedFolders { get; set; }
    internal long MatchedFiles { get; set; }
    internal long UncertainCount { get; set; }
    internal long UnknownAttributeCount { get; set; }
}
