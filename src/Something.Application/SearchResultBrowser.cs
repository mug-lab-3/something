using System.Collections.Immutable;
using Something.Application.Search;
using Something.Ntfs;

namespace Something.Application;

public enum SearchColumn
{
    Name,
    Size,
    Delta,
}

/// <summary>Where the user is in one search result: the current folder, the browsing history and
/// the order of the list. The tree shows the folders of the result; the list shows the folders
/// and matched files directly below the current folder. Independent of any window.</summary>
public sealed class SearchResultBrowser
{
    /// <summary>Rows read at a time, as in the other tabs.</summary>
    public const int PageSize = 250;

    private readonly HistorySearchResult result;
    private readonly List<FileReference> back = [];
    private readonly List<FileReference> forward = [];
    private ImmutableArray<SearchItem>? sorted;

    public SearchResultBrowser(HistorySearchResult result)
    {
        this.result = result ?? throw new ArgumentNullException(nameof(result));
        Current = result.Root;
    }

    public HistorySearchResult Result => result;

    /// <summary>The folder whose entries the list shows; the volume root at first.</summary>
    public FileReference Current { get; private set; }

    public bool CanGoBack => back.Count != 0;
    public bool CanGoForward => forward.Count != 0;
    public bool CanGoUp => Current != result.Root;

    /// <summary>Display path of the current folder relative to the volume root; empty at the root.</summary>
    public string Path => result.PathOf(Current);

    public SearchColumn SortColumn { get; private set; } = SearchColumn.Name;
    public bool Descending { get; private set; }

    /// <summary>The folder a folder was last seen in, or null for the root and unknown folders.</summary>
    public FileReference? ParentOf(FileReference folder) =>
        folder == result.Root ? null : result.Folder(folder)?.Parent;

    /// <summary>Whether <paramref name="folder"/> is the root or a folder of the result.</summary>
    public bool Contains(FileReference folder) =>
        folder == result.Root || result.Folder(folder) is not null;

    /// <summary>The folders of the result below <paramref name="folder"/>, in the fixed order of the tree.</summary>
    public ImmutableArray<SearchItem> TreeChildren(FileReference folder) =>
        result.ChildFolders(folder);

    /// <summary>Opens a folder of the result and remembers where the user came from. Opening the
    /// current folder changes nothing.</summary>
    public void Open(FileReference folder)
    {
        if (!Contains(folder))
            throw new ArgumentException("The folder is not part of the result.", nameof(folder));
        if (folder == Current)
            return;
        back.Add(Current);
        forward.Clear();
        Move(folder);
    }

    public bool GoBack()
    {
        if (back.Count == 0)
            return false;
        forward.Add(Current);
        Move(Take(back));
        return true;
    }

    public bool GoForward()
    {
        if (forward.Count == 0)
            return false;
        back.Add(Current);
        Move(Take(forward));
        return true;
    }

    /// <summary>Opens the parent of the current folder as a new step of the history.</summary>
    public bool GoUp()
    {
        if (ParentOf(Current) is not { } parent)
            return false;
        Open(Contains(parent) ? parent : result.Root);
        return true;
    }

    /// <summary>Sorts by <paramref name="column"/>: a column already in use reverses its order,
    /// another starts with its natural one (names ascending, sizes and changes largest first).</summary>
    public void Sort(SearchColumn column)
    {
        if (!Enum.IsDefined(column))
            throw new ArgumentOutOfRangeException(nameof(column));
        if (column == SortColumn)
            Descending = !Descending;
        else
        {
            SortColumn = column;
            Descending = column != SearchColumn.Name;
        }
        sorted = null;
    }

    /// <summary>Every entry of the current folder: folders first, then in the order of the chosen
    /// column. Unknown sizes and changes come last in either direction.</summary>
    public ImmutableArray<SearchItem> Entries => sorted ??= Order(result.Entries(Current));

    public int Count => Entries.Length;

    /// <summary>One page of <see cref="Entries"/>; <paramref name="hasMore"/> tells whether the list
    /// should read another when it is scrolled to the end.</summary>
    public ImmutableArray<SearchItem> Page(int page, out bool hasMore)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        long start = (long)page * PageSize;
        var entries = Entries;
        if (start >= entries.Length)
        {
            hasMore = false;
            return [];
        }
        int count = (int)Math.Min(PageSize, entries.Length - start);
        hasMore = start + count < entries.Length;
        return entries.AsSpan((int)start, count).ToImmutableArray();
    }

    private void Move(FileReference folder)
    {
        Current = folder;
        sorted = null;
    }

    private static FileReference Take(List<FileReference> stack)
    {
        var folder = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return folder;
    }

    private ImmutableArray<SearchItem> Order(ImmutableArray<SearchItem> entries)
    {
        IOrderedEnumerable<SearchItem> folders = entries.OrderByDescending(item =>
            item.IsDirectory
        );
        var ordered = SortColumn switch
        {
            SearchColumn.Size => ByKnown(folders, item => result.ValuesOf(item).Size),
            SearchColumn.Delta => ByKnown(folders, item => result.ValuesOf(item).Delta),
            _ => Descending
                ? folders
                    .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(item => item.Name, StringComparer.Ordinal)
                : folders
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Name, StringComparer.Ordinal),
        };
        return
        [
            .. ordered
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Reference.RecordIndex),
        ];
    }

    private IOrderedEnumerable<SearchItem> ByKnown<T>(
        IOrderedEnumerable<SearchItem> folders,
        Func<SearchItem, T?> key
    )
        where T : struct, IComparable<T> =>
        Descending
            ? folders.ThenBy(item => key(item) is null).ThenByDescending(item => key(item))
            : folders.ThenBy(item => key(item) is null).ThenBy(item => key(item));
}
