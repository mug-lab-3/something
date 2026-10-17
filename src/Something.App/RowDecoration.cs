using Windows.UI.Text;

namespace Something.App;

public static class RowDecoration
{
    public static TextDecorations FromRemoved(bool removed) =>
        removed ? TextDecorations.Strikethrough : TextDecorations.None;
}
