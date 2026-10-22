namespace Something.App;

public sealed partial class SearchConditionsView
{
    internal void TypeNameForSmoke(string text) => NameBox.Text = text;

    internal string RangePreviewForSmoke => RangePreviewText.Text;
    internal bool SearchEnabledForSmoke => SearchButton.IsEnabled;
}
