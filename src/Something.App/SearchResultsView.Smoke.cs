using Microsoft.UI.Xaml.Controls;
using Something.Application;

namespace Something.App;

public sealed partial class SearchResultsView
{
    internal TreeView FolderTree => Folders;
    internal ListView EntryList => Entries;
    internal SearchResultBrowser? Browser => browser;
    internal string CountsTextForSmoke => CountsText.Text;
    internal int ChipCount => Chips.Children.Count;
    internal string PathTextForSmoke => PathText.Text;

    internal void SortForSmoke(SearchColumn column)
    {
        browser!.Sort(column);
        RefreshList();
    }
}
