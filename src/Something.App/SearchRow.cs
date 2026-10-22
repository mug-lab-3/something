using System.Numerics;
using Microsoft.UI.Xaml;
using Something.Application;
using Something.Application.Search;
using Something.Ntfs;

namespace Something.App;

/// <summary>One row of the search result list: a folder of the result or a matched file.</summary>
public sealed class SearchRow(SearchItem item, string drive, string parentPath)
{
    public SearchItem Item { get; } = item;

    /// <summary>Path of the folder the item was last seen in, relative to the drive root.</summary>
    public string ParentPath { get; } = parentPath;

    public FileReference Reference => Item.Reference;
    public bool IsDirectory => Item.IsDirectory;
    public FileRemovalState Removal { get; internal set; } = new();
    public Visibility RecycleVisibility { get; internal set; } = Visibility.Collapsed;
    public string Name => DisplayText.Escape(Item.Name);
    public bool AttributesKnown => Item.StandardFileAttributes is not null;
    public bool IsHidden =>
        AttributesKnown
        && ((FileAttributes)Item.StandardFileAttributes!).HasFlag(FileAttributes.Hidden);
    public bool IsSystem =>
        AttributesKnown
        && ((FileAttributes)Item.StandardFileAttributes!).HasFlag(FileAttributes.System);

    /// <summary>The path as last observed, for the tooltip and the file actions.</summary>
    public string Path =>
        ParentPath.Length == 0
            ? $"{drive}\\{DisplayText.Escape(Item.Name)}"
            : $"{drive}\\{DisplayText.Escape(ParentPath)}\\{DisplayText.Escape(Item.Name)}";

    /// <summary>The size in the last scan the item was seen in; an asterisk marks a value that
    /// is incomplete or that a condition could not be decided on.</summary>
    public string Size =>
        DisplayText.Bytes(Item.Size) + (Item.Size is not null && IsApproximate ? " *" : "");

    public string Delta => Item.Delta is BigInteger delta ? DisplayText.Change(delta, null) : "—";

    public string SizeDescription =>
        Strings.Get("Search_SizeDescription")
        + (IsApproximate ? Strings.Get("Search_UncertainNote") : "");

    private bool IsApproximate => Item.Uncertain || Item.HasUnresolvedSize;
}
