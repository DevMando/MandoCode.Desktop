using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace MandoCode.Desktop.Controls;

/// <summary>
/// A <see cref="Grid"/> that shows the move cursor on hover, so something you can pick up and drop
/// elsewhere reads that way before you try it. Used as the content of agent tabs and pane headers,
/// both of which can be dragged into a split-view pane.
///
/// <para>Windows has no grab/grabbing cursor the way CSS does. <c>SizeAll</c> — the four-way arrow —
/// is its convention for "this object can be moved", and it is the closer match here:
/// <c>Hand</c> means "this is a link", which would say the wrong thing about a header whose click
/// selects rather than navigates.</para>
///
/// <para>A Grid rather than a Border, for the same reason <see cref="ResizeGrip"/> is: Border is
/// sealed, and <c>ProtectedCursor</c> is reachable only from a derived type. Since both headers
/// already hold their content in a Grid, the cursor rides on the content and covers everything but
/// the surrounding padding.</para>
///
/// <para>Carries the same hazard as ResizeGrip: assigning the cursor before the element is in the
/// visual tree fast-fails WinUI natively (STATUS_STOWED_EXCEPTION 0xC000027B). Loaded is the only
/// safe moment.</para>
/// </summary>
public sealed class DragHandleGrid : Grid
{
    public DragHandleGrid()
    {
        Loaded += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }
}
