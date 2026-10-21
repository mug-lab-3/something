using System.Collections.Immutable;
using System.Numerics;
using Something.Application.History;
using Something.Ntfs;

namespace Something.Application.Search;

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
    public bool FollowsIdentity { get; init; }

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

/// <summary>The folders and files a search placed, as a tree below the volume root: matched items
/// and the folders that lead to them. Immutable; the scans stay open through the search session
/// that produced it, not through this data.</summary>
public sealed class HistorySearchResult
{
    private readonly Dictionary<FileReference, SearchItem> folders = [];
    private readonly Dictionary<FileReference, List<SearchItem>> childFolders = [];
    private readonly Dictionary<FileReference, List<SearchItem>> files = [];

    /// <summary>Checks that every folder and file hangs below <paramref name="root"/>: each parent
    /// is the root or one of <paramref name="folders"/>, folders form no cycle, and no folder
    /// appears twice.</summary>
    /// <param name="volumeKey">The volume the scans belong to.</param>
    /// <param name="criteria">The criteria the search ran with.</param>
    /// <param name="status">Whether the search ran.</param>
    /// <param name="scans">Retained scans of the range, oldest first.</param>
    /// <param name="root">The volume root of the last scan; files and folders hang below it.</param>
    /// <param name="folders">Matched folders and the unmatched folders that lead to matches.</param>
    /// <param name="files">Matched files; several names of one hard-linked file may appear.</param>
    /// <param name="unknownAttributeCount">Items left out only because their attributes were unknown.</param>
    public HistorySearchResult(
        string volumeKey,
        HistorySearchCriteria criteria,
        HistorySearchStatus status,
        ImmutableArray<ScanHistoryEntry> scans,
        FileReference root,
        IEnumerable<SearchItem> folders,
        IEnumerable<SearchItem> files,
        long unknownAttributeCount
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeKey);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentOutOfRangeException.ThrowIfNegative(unknownAttributeCount);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        VolumeKey = volumeKey;
        Criteria = criteria;
        Status = status;
        Scans = scans.IsDefault ? [] : scans;
        Root = root;
        UnknownAttributeCount = unknownAttributeCount;
        foreach (var folder in folders)
        {
            if (!folder.IsDirectory || folder.Reference == root)
                throw new ArgumentException("A folder entry is not a folder below the root.");
            if (!this.folders.TryAdd(folder.Reference, folder))
                throw new ArgumentException("A folder appears twice.", nameof(folders));
            if (folder.Matched)
                MatchedFolders++;
            if (folder.Matched && folder.Uncertain)
                UncertainCount++;
        }
        foreach (var folder in this.folders.Values)
            Add(childFolders, folder);
        foreach (var file in files)
        {
            if (file.IsDirectory || !file.Matched)
                throw new ArgumentException("A file entry is not a matched file.", nameof(files));
            Add(this.files, file);
            MatchedFiles++;
            if (file.Uncertain)
                UncertainCount++;
        }
        ValidateTree();
    }

    /// <summary>A search that did not run, such as one without scans in its range.</summary>
    public static HistorySearchResult Unsearched(
        string volumeKey,
        HistorySearchCriteria criteria,
        HistorySearchStatus status,
        ImmutableArray<ScanHistoryEntry> scans
    ) =>
        status == HistorySearchStatus.Completed
            ? throw new ArgumentOutOfRangeException(nameof(status))
            : new(volumeKey, criteria, status, scans, default, [], [], 0);

    public string VolumeKey { get; }
    public HistorySearchCriteria Criteria { get; }
    public HistorySearchStatus Status { get; }

    /// <summary>Retained scans of the range, oldest first. The first is F and the last is L.</summary>
    public ImmutableArray<ScanHistoryEntry> Scans { get; }

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
        [.. ByName(childFolders.GetValueOrDefault(parent, []))];

    /// <summary>The tree folders and matched files directly below <paramref name="parent"/>.</summary>
    public ImmutableArray<SearchItem> Entries(FileReference parent) =>
        [.. ChildFolders(parent), .. ByName(files.GetValueOrDefault(parent, []))];

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

    private static IEnumerable<SearchItem> ByName(IEnumerable<SearchItem> items) =>
        items
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.RecordIndex);

    private static void Add(Dictionary<FileReference, List<SearchItem>> groups, SearchItem item)
    {
        if (!groups.TryGetValue(item.Parent, out var children))
            groups.Add(item.Parent, children = []);
        children.Add(item);
    }

    // Every parent chain must end at the root without revisiting a folder.
    private void ValidateTree()
    {
        var reaches = new HashSet<FileReference>();
        var path = new HashSet<FileReference>();
        foreach (var start in folders.Keys)
        {
            path.Clear();
            for (var current = start; current != Root && !reaches.Contains(current); )
            {
                if (!path.Add(current))
                    throw new ArgumentException("The folders form a cycle.", "folders");
                current = folders.TryGetValue(current, out var folder)
                    ? folder.Parent
                    : throw new ArgumentException(
                        "A folder's parent is neither the root nor a folder of the result.",
                        "folders"
                    );
            }
            reaches.UnionWith(path);
        }
        foreach (var parent in files.Keys)
            if (parent != Root && !folders.ContainsKey(parent))
                throw new ArgumentException(
                    "A file's parent is neither the root nor a folder of the result.",
                    "files"
                );
    }
}
