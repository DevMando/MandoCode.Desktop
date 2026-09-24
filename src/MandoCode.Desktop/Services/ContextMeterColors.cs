using MandoCode.Services;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Colors for the context meter in a tab's status strip. The fill takes the level's theme color
/// (green, gold, red) but is lifted toward body text until it clears the WCAG non-text floor against
/// both the page and the empty track: the bar's length is the reading, so the filled part has to
/// stand apart from the unfilled part, not just from the page. Measured across the shipped themes,
/// the level colors alone missed 3:1 against the page in a few light and retro palettes, and a
/// dim-colored track sat within 1.0–1.5:1 of the fill in most of them.
/// </summary>
public static class ContextMeterColors
{
    /// <summary>WCAG 1.4.11: graphical objects need 3:1 against adjacent colors.</summary>
    public const double NonTextFloor = 3.0;

    /// <summary>The empty track: the theme's border color, a quiet outline of the capacity.</summary>
    public static string Track(UiTheme theme) => theme.Border;

    public static string Fill(UiTheme theme, ContextMeter.Level level)
    {
        var color = level switch
        {
            ContextMeter.Level.Full => theme.Red,
            ContextMeter.Level.Warm => theme.Gold,
            _ => theme.Green,
        };
        var onPage = ColorMath.Readable(color, theme.Background, theme.Text, NonTextFloor);
        return ColorMath.Readable(onPage, Track(theme), theme.Text, NonTextFloor);
    }
}
