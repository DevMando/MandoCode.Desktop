using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The deterministic guards around LLM-suggested snapshot titles: cleaning the model's raw output
/// (which loves to add quotes, a "Title:" preface, or a second line of reasoning) and enforcing
/// uniqueness the model can't be trusted to.
/// </summary>
public sealed class SnapshotNamingTests
{
    [Theory]
    [InlineData("\"Auth Refactor\"", "Auth Refactor")]           // surrounding quotes
    [InlineData("Auth Refactor.", "Auth Refactor")]              // trailing period
    [InlineData("`Auth Refactor`", "Auth Refactor")]             // backticks
    [InlineData("  Auth   Refactor  ", "Auth Refactor")]         // collapsed whitespace
    public void Clean_StripsDecoration(string raw, string expected)
        => Assert.Equal(expected, SnapshotNaming.Clean(raw));

    [Fact]
    public void Clean_KeepsFirstLineOnly()
        => Assert.Equal("Auth Refactor", SnapshotNaming.Clean("Auth Refactor\nreasoning here"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Clean_ReturnsNullWhenNothingUsable(string? raw)
        => Assert.Null(SnapshotNaming.Clean(raw));

    [Fact]
    public void Clean_CapsLength()
    {
        var result = SnapshotNaming.Clean(new string('a', 200))!;
        Assert.True(result.Length <= 61);   // 60 chars + the ellipsis
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void MakeUnique_LeavesDistinctNameUntouched()
        => Assert.Equal("Auth Refactor", SnapshotNaming.MakeUnique("Auth Refactor", new[] { "Other Thing" }));

    [Fact]
    public void MakeUnique_AppendsCounterOnClash()
        => Assert.Equal("Auth Refactor (2)", SnapshotNaming.MakeUnique("Auth Refactor", new[] { "Auth Refactor" }));

    [Fact]
    public void MakeUnique_SkipsToFirstFreeCounter()
    {
        var taken = new[] { "Auth Refactor", "Auth Refactor (2)", "Auth Refactor (3)" };
        Assert.Equal("Auth Refactor (4)", SnapshotNaming.MakeUnique("Auth Refactor", taken));
    }

    [Fact]
    public void MakeUnique_ClashIsCaseInsensitive()
        => Assert.Equal("Auth Refactor (2)", SnapshotNaming.MakeUnique("Auth Refactor", new[] { "auth refactor" }));
}
