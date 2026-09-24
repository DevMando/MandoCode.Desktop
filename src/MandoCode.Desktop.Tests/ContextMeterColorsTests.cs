using MandoCode.Desktop.Services;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The context meter's bar is read by its length, and its percentage is printed in the fill color.
/// So in every theme the fill must clear the text floor against the page and the non-text floor
/// against the empty track. A theme added with a faint green, gold, or red fails here instead of
/// shipping an unreadable percentage, or a bar that looks full, or empty, when it isn't.
/// </summary>
public class ContextMeterColorsTests
{
    public static TheoryData<string, ContextMeter.Level> ThemesAndLevels()
    {
        var data = new TheoryData<string, ContextMeter.Level>();
        foreach (var t in UiTheme.All)
            foreach (var level in Enum.GetValues<ContextMeter.Level>())
                data.Add(t.Name, level);
        return data;
    }

    [Theory]
    [MemberData(nameof(ThemesAndLevels))]
    public void FillReadsAsTextOnThePage_AndStandsApartFromTheTrack(string name, ContextMeter.Level level)
    {
        var theme = UiTheme.All.Single(t => t.Name == name);
        var fill = ContextMeterColors.Fill(theme, level);

        var onPage = ColorMath.Contrast(fill, theme.Background);
        var onTrack = ColorMath.Contrast(fill, ContextMeterColors.Track(theme));
        Assert.True(onPage >= ContextMeterColors.TextFloor, $"{name}/{level}: fill is {onPage:F2}:1 against the page");
        Assert.True(onTrack >= ContextMeterColors.NonTextFloor, $"{name}/{level}: fill is {onTrack:F2}:1 against the track");
    }

    [Fact]
    public void AColorThatAlreadyClearsTheFloorIsLeftAlone()
    {
        // Lifting is a last resort: where the palette's own green already works, it's what shows.
        var theme = UiTheme.All.Single(t => t.Name == "Dracula");
        Assert.Equal(theme.Green, ContextMeterColors.Fill(theme, ContextMeter.Level.Ok));
    }
}
