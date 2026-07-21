using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MandoCode.Desktop.Controls;

/// <summary>
/// A thin grip that shows a resize cursor on hover, so a drag splitter reads as draggable.
/// The default (Horizontal) grip is a horizontal bar showing the vertical-resize (↕) cursor,
/// as used by the terminal splitter; a Vertical grip is a vertical bar showing ↔, as used by
/// the file-explorer splitter. Subclasses <see cref="Grid"/> (a Panel, which renders its
/// Background and is hit-testable) because <c>Thumb</c> is sealed and the cursor requires
/// the protected <c>ProtectedCursor</c> member, reachable only from a derived type.
/// Dragging itself is driven by pointer events in the host (MainWindow / ChatTabView).
/// </summary>
public sealed class ResizeGrip : Grid
{
    /// <summary>The grip bar's own orientation: a Vertical bar resizes horizontally (↔).
    /// Set from XAML; read when Loaded fires, so markup order doesn't matter.</summary>
    public Orientation GripOrientation { get; set; } = Orientation.Horizontal;

    public ResizeGrip()
    {
        // ProtectedCursor MUST be set after the element is in the visual tree — assigning it
        // in the constructor (during InitializeComponent) fast-fails WinUI natively
        // (STATUS_STOWED_EXCEPTION 0xC000027B). Loaded is the safe point.
        Loaded += (_, _) =>
            ProtectedCursor = InputSystemCursor.Create(
                GripOrientation == Orientation.Horizontal
                    ? InputSystemCursorShape.SizeNorthSouth
                    : InputSystemCursorShape.SizeWestEast);
    }
}
