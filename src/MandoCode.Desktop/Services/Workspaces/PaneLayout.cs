namespace MandoCode.Desktop.Services;

/// <summary>
/// Pure geometry for the split view's pane grid — how many rows and columns a given pane count
/// occupies, which cell each pane lands in, and where a dragged divider leaves the track sizes.
/// Deliberately free of WinUI types so it can be unit tested; <c>MainWindow.Split.cs</c> owns the
/// visual-tree side (building tracks, creating dividers, moving views between cells).
/// </summary>
public static class PaneLayout
{
    /// <summary>Past four panes a transcript plus its input box stops being usable at any split.</summary>
    public const int MaxPanes = 4;

    /// <summary>No pane may be dragged below this share of its axis.</summary>
    public const double MinFraction = 0.15;

    /// <summary>Grid shape for a pane count: 2 side by side, 3 across, 4 as a 2×2. Three is the most
    /// that stays readable in a single row, so four wraps rather than shrinking further.</summary>
    public static (int Rows, int Cols) Shape(int count) => count switch
    {
        2 => (1, 2),
        3 => (1, 3),
        4 => (2, 2),
        _ => (1, 1),
    };

    /// <summary>Pane index → its cell in the pane grid, filled row-major.</summary>
    public static (int Row, int Col) Cell(int index, int count)
    {
        var (_, cols) = Shape(count);
        return (index / cols, index % cols);
    }

    /// <summary>An even split across <paramref name="count"/> tracks.</summary>
    public static List<double> EqualFractions(int count)
    {
        if (count < 1) count = 1;
        return Enumerable.Repeat(1.0 / count, count).ToList();
    }

    /// <summary>Fractions valid for <paramref name="count"/> tracks: keeps the current list when it
    /// already describes that many tracks (so a user's dragged positions survive), otherwise starts
    /// over from an even split.</summary>
    public static List<double> Fit(List<double>? current, int count) =>
        current is { } c && c.Count == count && c.Sum() > 0 ? c : EqualFractions(count);

    /// <summary>
    /// Moves the divider at <paramref name="index"/> to <paramref name="pointerFraction"/> (0–1
    /// along the axis, measured from the pane area's leading edge). The two adjacent panes' COMBINED
    /// share is held constant, so dragging one divider never nudges a pane further along the axis.
    /// Clamped so neither side of the divider drops below <see cref="MinFraction"/>.
    /// </summary>
    public static void Repartition(IList<double> fractions, int index, double pointerFraction)
    {
        if (index < 0 || index + 1 >= fractions.Count) return;

        double before = 0;
        for (int i = 0; i < index; i++) before += fractions[i];

        double pair = fractions[index] + fractions[index + 1];

        // When the pair is too small to honour the minimum on BOTH sides, [Min, pair - Min] is an
        // empty range: clamping into it would throw, and pinning one side to Min would drive the
        // other negative. Halving is the most balanced feasible answer and keeps both non-negative.
        double first = pair <= 2 * MinFraction
            ? pair / 2
            : Math.Clamp(pointerFraction - before, MinFraction, pair - MinFraction);

        fractions[index] = first;
        fractions[index + 1] = pair - first;
    }
}
