using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The bundled-background gallery is discovered from a folder rather than declared in code, so the
/// file-naming convention IS the contract — these pin the rules documented in
/// Assets/images/backgrounds/README.md, which is what a future release adds images against.
/// </summary>
public class BuiltInBackgroundsTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mandocode-bg-tests-" + Guid.NewGuid().ToString("N"));

    public BuiltInBackgroundsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private void Add(string fileName) => File.WriteAllText(Path.Combine(_folder, fileName), "x");

    // ---- display names ---------------------------------------------------------------

    [Theory]
    [InlineData("01-nebula-drift.jpg", "Nebula Drift")]
    [InlineData("02-violet-haze.png", "Violet Haze")]
    [InlineData("10_deep_space.webp", "Deep Space")]
    [InlineData("aurora.jpg", "Aurora")]
    [InlineData("two words.png", "Two Words")]
    public void DisplayNameFor_strips_the_ordering_prefix_and_titlecases(string file, string expected)
        => Assert.Equal(expected, BuiltInBackgrounds.DisplayNameFor(file));

    [Fact]
    public void DisplayNameFor_keeps_a_leading_number_that_is_part_of_the_name()
    {
        // No separator after the digits, so "1999" is the name — not an ordering prefix.
        Assert.Equal("1999 Skyline", BuiltInBackgrounds.DisplayNameFor("1999 skyline.jpg"));
        // A prefix with nothing after it must not clip the whole name away.
        Assert.Equal("01", BuiltInBackgrounds.DisplayNameFor("01.jpg"));
    }

    // ---- discovery -------------------------------------------------------------------

    [Fact]
    public void DiscoverIn_returns_empty_for_a_missing_folder()
        => Assert.Empty(BuiltInBackgrounds.DiscoverIn(Path.Combine(_folder, "nope")));

    [Fact]
    public void DiscoverIn_returns_empty_when_the_folder_has_no_images()
    {
        Add("README.md");
        Assert.Empty(BuiltInBackgrounds.DiscoverIn(_folder));
    }

    [Fact]
    public void DiscoverIn_orders_by_file_name_so_the_numeric_prefix_controls_the_gallery()
    {
        Add("03-third.jpg");
        Add("01-first.jpg");
        Add("02-second.jpg");

        Assert.Equal(
            new[] { "First", "Second", "Third" },
            BuiltInBackgrounds.DiscoverIn(_folder).Select(b => b.DisplayName));
    }

    [Fact]
    public void DiscoverIn_takes_only_decodable_image_extensions()
    {
        Add("01-keep.jpg");
        Add("02-keep.jpeg");
        Add("03-keep.png");
        Add("04-keep.webp");
        Add("05-skip.txt");
        Add("06-skip.bmp");     // the picker accepts it; BitmapImage thumbnails don't
        Add("README.md");

        var found = BuiltInBackgrounds.DiscoverIn(_folder);
        Assert.Equal(4, found.Count);
        Assert.All(found, b => Assert.StartsWith("Keep", b.DisplayName));
    }

    [Fact]
    public void DiscoverIn_is_case_insensitive_about_extensions()
    {
        Add("01-shouty.JPG");
        Assert.Single(BuiltInBackgrounds.DiscoverIn(_folder));
    }

    [Fact]
    public void DiscoverIn_carries_the_file_name_as_the_durable_identity()
    {
        Add("01-nebula-drift.jpg");
        var only = Assert.Single(BuiltInBackgrounds.DiscoverIn(_folder));

        // The FILE NAME is what's persisted to mark the active tile — not the display name, which
        // is derived and would change if the labeling rules ever did.
        Assert.Equal("01-nebula-drift.jpg", only.FileName);
        Assert.Equal(Path.Combine(_folder, "01-nebula-drift.jpg"), only.FullPath);
    }
}
