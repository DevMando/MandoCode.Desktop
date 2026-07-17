using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MandoCode.Desktop.Controls;

/// <summary>
/// A thin horizontal grip that shows the vertical-resize (↕) cursor on hover, so the
/// terminal's drag splitter reads as draggable. Subclasses <see cref="Grid"/> (a Panel,
/// which renders its Background and is hit-testable) because <c>Thumb</c> is sealed and
/// the cursor requires the protected <c>ProtectedCursor</c> member, reachable only from a
/// derived type. Dragging itself is driven by pointer events in the host (MainWindow).
/// </summary>
public sealed class ResizeGrip : Grid
{
    public ResizeGrip()
    {
        // ProtectedCursor MUST be set after the element is in the visual tree — assigning it
        // in the constructor (during InitializeComponent) fast-fails WinUI natively
        // (STATUS_STOWED_EXCEPTION 0xC000027B). Loaded is the safe point.
        Loaded += (_, _) =>
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}
