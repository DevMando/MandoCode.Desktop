using MandoCode.Services;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Colors for the context meter in a tab's status strip. The fill takes the level's theme color
/// (green, gold, red) but is lifted toward body text until it clears two floors: the text floor
/// against the page, because the percentage is printed in this same color, and the non-text floor
/// against the empty track, because the bar's length is the reading, so the filled part has to
/// stand apart from the unfilled part. Measured across the shipped themes, the level colors alone
/// missed even 3:1 against the page in a few light and retro palettes, and a dim-colored track sat
/// within 1.0–1.5:1 of the fill in most of them.
/// </summary>
public static class ContextMeterColors
{
    /// <summary>WCAG 1.4.11: graphical objects need 3:1 against adjacent colors.</summary>
    public const double NonTextFloor = 3.0;

    /// <summary>WCAG 1.4.3: the percentage is small text in the fill color.</summary>
    public const double TextFloor = ColorMath.AaNormalText;

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
        var onPage = ColorMath.Readable(color, theme.Background, theme.Text, TextFloor);
        return ColorMath.Readable(onPage, Track(theme), theme.Text, NonTextFloor);
    }
}
