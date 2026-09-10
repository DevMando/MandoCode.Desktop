using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// Secondary text — timestamps, paths, status lines, the hint under a heading — is still text, and
/// half the shipped palettes authored it below the WCAG AA floor. The palettes stay as authored
/// (most are faithful to upstream schemes); UiTheme.ReadableDim is what makes them legible. The
/// point of the sweep below is the NEXT theme: one added with a too-faint Dim fails here instead of
/// shipping.
/// </summary>
public class ThemeReadabilityTests
{
    public static TheoryData<string> ThemeNames()
    {
        var data = new TheoryData<string>();
        foreach (var t in UiTheme.All) data.Add(t.Name);
        return data;
    }

    private static UiTheme Theme(string name) => UiTheme.All.Single(t => t.Name == name);

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void EveryThemesSecondaryTextClearsTheFloor(string name)
    {
        var t = Theme(name);
        var ratio = ColorMath.Contrast(t.ReadableDim, t.Background);
        // A CRT theme is read through its own overlay, so it is held to the higher floor.
        var floor = t.Crt ? ColorMath.AaNormalTextThroughGlass : ColorMath.AaNormalText;

        Assert.True(ratio >= floor,
            $"{name}: dim text is {ratio:F2}:1 against its background (floor {floor}:1)");
    }

    [Fact]
    public void OverlaidThemesAreHeldToAHigherFloorThanPlainOnes()
    {
        // The point of the CRT floor is that it BINDS — if the tube themes happened to clear the
        // ordinary floor anyway, this whole mechanism would be decoration.
        var overlaid = UiTheme.All.Where(t => t.Crt).ToList();
        Assert.NotEmpty(overlaid);

        foreach (var t in overlaid)
        {
            Assert.True(ColorMath.Contrast(t.ReadableDim, t.Background) >= ColorMath.AaNormalTextThroughGlass,
                $"{t.Name}: dim text does not clear the through-glass floor");
        }
        Assert.True(ColorMath.AaNormalTextThroughGlass > ColorMath.AaNormalText);
    }

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void EveryThemesBodyTextClearsTheFloor(string name)
    {
        // ReadableDim blends toward Text and stops there, so body text being readable is the
        // assumption that makes the lift terminate. Asserted rather than assumed.
        var t = Theme(name);
        var ratio = ColorMath.Contrast(t.Text, t.Background);

        Assert.True(ratio >= ColorMath.AaNormalText,
            $"{name}: body text is {ratio:F2}:1 against its background");
    }

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void AlreadyReadablePalettesAreLeftAlone(string name)
    {
        // The lift is a correction, not a restyle: a theme that authored a legible Dim must come
        // through byte-identical, or switching themes would quietly repaint palettes that were fine.
        var t = Theme(name);
        var floor = t.Crt ? ColorMath.AaNormalTextThroughGlass : ColorMath.AaNormalText;
        if (ColorMath.Contrast(t.Dim, t.Background) < floor) return;

        Assert.Equal(t.Dim, t.ReadableDim);
    }

    [Fact]
    public void TheLiftIsTheSmallestOneThatWorks()
    {
        // Walking to the first passing step keeps a nudged palette recognisably itself. A lift that
        // overshot would wash every faint theme toward the same grey.
        const string bg = "#282A36", text = "#F8F8F2", faint = "#6272A4";   // Dracula
        var lifted = ColorMath.Readable(faint, bg, text, ColorMath.AaNormalText);

        Assert.True(ColorMath.Contrast(lifted, bg) >= ColorMath.AaNormalText);
        Assert.True(ColorMath.Contrast(lifted, bg) < ColorMath.AaNormalText + 1.0,
            "overshot: the lift should stop at the first step that clears the floor");
        Assert.NotEqual(text, lifted);   // nudged toward the text colour, not replaced by it
    }

    [Fact]
    public void ContrastMatchesKnownWcagValues()
    {
        Assert.Equal(21.0, ColorMath.Contrast("#000000", "#FFFFFF"), 2);
        Assert.Equal(1.0, ColorMath.Contrast("#808080", "#808080"), 2);
    }
}
