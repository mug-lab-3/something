using System.Runtime.InteropServices;

namespace Something.App;

// Work around WinUI #8829: opening a popup can reuse a stale system cursor.
// Only reset the cursor when our menu opens; don't override normal pointer events.
internal static class MenuCursor
{
    private static readonly nint arrow = LoadCursor(0, 32512); // IDC_ARROW, shared system cursor.

    internal static void RestoreArrow()
    {
        if (arrow != 0)
            SetCursor(arrow);
    }

    // These handle-only calls require no buffer marshalling or unsafe code.
    [DllImport("user32.dll", EntryPoint = "LoadCursorW", ExactSpelling = true)]
    private static extern nint LoadCursor(nint instance, nint cursorName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetCursor(nint cursor);
}
