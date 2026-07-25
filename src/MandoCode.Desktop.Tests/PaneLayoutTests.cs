using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>Geometry and divider math for the compare view's 2–4 pane grid.</summary>
public class PaneLayoutTests
{
    // ---- Shape -------------------------------------------------------------

    [Theory]
    [InlineData(2, 1, 2)]   // side by side
    [InlineData(3, 1, 3)]   // across
    [InlineData(4, 2, 2)]   // wraps rather than shrinking to a fourth of the width
    public void Shape_matches_the_pane_count(int count, int rows, int cols)
    {
        Assert.Equal((rows, cols), PaneLayout.Shape(count));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Shape_collapses_to_a_single_cell_below_two_panes(int count)
    {
        // Single view has to be one */* cell so pages and the empty state fill it.
        Assert.Equal((1, 1), PaneLayout.Shape(count));
    }

    // ---- Cell --------------------------------------------------------------

    [Fact]
    public void Cell_lays_two_panes_out_in_one_row()
    {
        Assert.Equal((0, 0), PaneLayout.Cell(0, 2));
        Assert.Equal((0, 1), PaneLayout.Cell(1, 2));
    }

    [Fact]
    public void Cell_lays_three_panes_out_in_one_row()
    {
        Assert.Equal((0, 0), PaneLayout.Cell(0, 3));
        Assert.Equal((0, 1), PaneLayout.Cell(1, 3));
        Assert.Equal((0, 2), PaneLayout.Cell(2, 3));
    }

    [Fact]
    public void Cell_wraps_four_panes_into_a_two_by_two()
    {
        Assert.Equal((0, 0), PaneLayout.Cell(0, 4));
        Assert.Equal((0, 1), PaneLayout.Cell(1, 4));
        Assert.Equal((1, 0), PaneLayout.Cell(2, 4));
        Assert.Equal((1, 1), PaneLayout.Cell(3, 4));
    }

    [Fact]
    public void Cell_assigns_every_pane_a_distinct_cell()
    {
        for (int count = 2; count <= PaneLayout.MaxPanes; count++)
        {
            var cells = Enumerable.Range(0, count).Select(i => PaneLayout.Cell(i, count)).ToList();
            Assert.Equal(count, cells.Distinct().Count());
        }
    }

    [Fact]
    public void Cell_stays_inside_the_shape_it_reports()
    {
        for (int count = 2; count <= PaneLayout.MaxPanes; count++)
        {
            var (rows, cols) = PaneLayout.Shape(count);
            for (int i = 0; i < count; i++)
            {
                var (row, col) = PaneLayout.Cell(i, count);
                Assert.InRange(row, 0, rows - 1);
                Assert.InRange(col, 0, cols - 1);
            }
        }
    }

    // ---- EqualFractions / Fit ---------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EqualFractions_splits_the_axis_evenly_and_sums_to_one(int count)
    {
        var f = PaneLayout.EqualFractions(count);
        Assert.Equal(count, f.Count);
        Assert.Equal(1.0, f.Sum(), 10);
        Assert.All(f, v => Assert.Equal(1.0 / count, v, 10));
    }

    [Fact]
    public void Fit_keeps_dragged_positions_when_the_track_count_is_unchanged()
    {
        // The whole point: visiting Settings and coming back must not re-centre the divider.
        var dragged = new List<double> { 0.7, 0.3 };
        Assert.Same(dragged, PaneLayout.Fit(dragged, 2));
    }

    [Fact]
    public void Fit_resets_to_an_even_split_when_the_track_count_changes()
    {
        var dragged = new List<double> { 0.7, 0.3 };
        var fitted = PaneLayout.Fit(dragged, 3);
        Assert.Equal(3, fitted.Count);
        Assert.All(fitted, v => Assert.Equal(1.0 / 3, v, 10));
    }

    [Fact]
    public void Fit_replaces_a_null_or_degenerate_list()
    {
        Assert.Equal(2, PaneLayout.Fit(null, 2).Count);
        // A list of zeroes would collapse every pane, so it counts as unusable.
        Assert.Equal(1.0, PaneLayout.Fit(new List<double> { 0, 0 }, 2).Sum(), 10);
    }

    // ---- Repartition -------------------------------------------------------

    [Fact]
    public void Repartition_moves_the_divider_to_the_pointer()
    {
        var f = new List<double> { 0.5, 0.5 };
        PaneLayout.Repartition(f, 0, 0.3);
        Assert.Equal(0.3, f[0], 10);
        Assert.Equal(0.7, f[1], 10);
    }

    [Fact]
    public void Repartition_preserves_the_total()
    {
        var f = new List<double> { 1.0 / 3, 1.0 / 3, 1.0 / 3 };
        PaneLayout.Repartition(f, 1, 0.5);
        Assert.Equal(1.0, f.Sum(), 10);
    }

    [Fact]
    public void Repartition_leaves_non_adjacent_panes_untouched()
    {
        // Dragging one divider must not nudge a pane further along the axis.
        var f = new List<double> { 0.2, 0.4, 0.4 };
        PaneLayout.Repartition(f, 1, 0.4);
        Assert.Equal(0.2, f[0], 10);                 // pane 0 is not adjacent to divider 1
        Assert.Equal(0.8, f[1] + f[2], 10);          // the adjacent pair keeps its combined share
    }

    [Fact]
    public void Repartition_measures_the_pointer_from_the_axis_start_not_the_pair()
    {
        // Divider 1 sits at 0.2 + f[1]; a pointer at 0.5 should leave pane 1 with 0.3.
        var f = new List<double> { 0.2, 0.4, 0.4 };
        PaneLayout.Repartition(f, 1, 0.5);
        Assert.Equal(0.3, f[1], 10);
        Assert.Equal(0.5, f[2], 10);
    }

    [Fact]
    public void Repartition_clamps_so_neither_side_collapses()
    {
        var f = new List<double> { 0.5, 0.5 };

        PaneLayout.Repartition(f, 0, -5.0);          // dragged far past the left edge
        Assert.Equal(PaneLayout.MinFraction, f[0], 10);
        Assert.Equal(1.0, f.Sum(), 10);

        f = new List<double> { 0.5, 0.5 };
        PaneLayout.Repartition(f, 0, 5.0);           // and far past the right edge
        Assert.Equal(PaneLayout.MinFraction, f[1], 10);
        Assert.Equal(1.0, f.Sum(), 10);
    }

    [Fact]
    public void Repartition_honours_the_minimum_for_a_middle_divider()
    {
        var f = new List<double> { 1.0 / 3, 1.0 / 3, 1.0 / 3 };
        PaneLayout.Repartition(f, 1, 5.0);
        Assert.Equal(PaneLayout.MinFraction, f[2], 10);
        Assert.Equal(1.0 / 3, f[0], 10);
        Assert.Equal(1.0, f.Sum(), 10);
    }

    [Fact]
    public void Repartition_halves_a_pair_too_small_for_the_minimum()
    {
        // [Min, pair - Min] is empty here: clamping into it would throw, and pinning one side to
        // Min would drive the other negative. Both sides get half the pair instead.
        var f = new List<double> { 0.9, 0.05, 0.05 };
        PaneLayout.Repartition(f, 1, 0.95);
        Assert.Equal(0.05, f[1], 10);
        Assert.Equal(0.05, f[2], 10);
        Assert.Equal(1.0, f.Sum(), 10);
        Assert.All(f, v => Assert.True(v >= 0, "no track may go negative"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]     // divider 1 needs a track at index 2
    [InlineData(99)]
    public void Repartition_ignores_an_out_of_range_divider(int index)
    {
        var f = new List<double> { 0.5, 0.5 };
        PaneLayout.Repartition(f, index, 0.3);
        Assert.Equal(new List<double> { 0.5, 0.5 }, f);
    }
}
