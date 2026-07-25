using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>Full-text matching and snippet extraction behind History's search.</summary>
public class ConversationSearchTests
{
    // ---- Flatten -----------------------------------------------------------

    [Fact]
    public void Flatten_joins_turn_text()
    {
        var turns = new[]
        {
            new ConversationTurn("u", "how do the dividers work"),
            new ConversationTurn("a", "each one repartitions its two panes"),
        };
        var text = ConversationSearch.Flatten(turns);
        Assert.Contains("how do the dividers work", text);
        Assert.Contains("each one repartitions its two panes", text);
    }

    [Fact]
    public void Flatten_drops_the_role_markers()
    {
        // Otherwise searching "a" or "u" would hit every conversation ever recorded.
        var turns = new[] { new ConversationTurn("u", "hello"), new ConversationTurn("a", "hi") };
        Assert.Equal("hello\nhi", ConversationSearch.Flatten(turns));
    }

    [Fact]
    public void Flatten_of_nothing_is_empty()
    {
        Assert.Equal("", ConversationSearch.Flatten(Array.Empty<ConversationTurn>()));
    }

    // ---- Snippet: matching -------------------------------------------------

    [Fact]
    public void Snippet_is_null_when_the_query_is_absent()
    {
        Assert.Null(ConversationSearch.Snippet("nothing relevant here", "dividers"));
    }

    [Fact]
    public void Snippet_matches_case_insensitively()
    {
        Assert.NotNull(ConversationSearch.Snippet("The Divider Math", "divider math"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Snippet_is_null_for_empty_text(string? text)
    {
        Assert.Null(ConversationSearch.Snippet(text, "anything"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Snippet_is_null_for_a_blank_query(string? query)
    {
        Assert.Null(ConversationSearch.Snippet("some conversation text", query));
    }

    // ---- Snippet: the window -----------------------------------------------

    [Fact]
    public void Snippet_returns_the_whole_text_unellipsised_when_it_fits()
    {
        // Nothing was truncated, so it should read as a complete quote.
        var snippet = ConversationSearch.Snippet("divider math", "divider", radius: 60);
        Assert.Equal("divider math", snippet);
    }

    [Fact]
    public void Snippet_ellipsises_only_the_ends_it_actually_truncated()
    {
        var text = new string('a', 200) + " NEEDLE " + new string('b', 200);

        var snippet = ConversationSearch.Snippet(text, "NEEDLE", radius: 10);
        Assert.NotNull(snippet);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Contains("NEEDLE", snippet);

        // A hit at the very start has nothing to its left to elide.
        var atStart = ConversationSearch.Snippet("NEEDLE" + new string('b', 200), "NEEDLE", radius: 10);
        Assert.NotNull(atStart);
        Assert.False(atStart!.StartsWith("…"), "no left truncation, so no leading ellipsis");
        Assert.EndsWith("…", atStart);
    }

    [Fact]
    public void Snippet_keeps_context_either_side_of_the_hit()
    {
        var snippet = ConversationSearch.Snippet("before the NEEDLE and after", "NEEDLE", radius: 6);
        Assert.NotNull(snippet);
        Assert.Contains("the NEEDLE and", snippet);
    }

    [Fact]
    public void Snippet_windows_the_first_hit()
    {
        var text = "first NEEDLE here" + new string('x', 500) + "second NEEDLE there";
        var snippet = ConversationSearch.Snippet(text, "NEEDLE", radius: 8);
        Assert.NotNull(snippet);
        Assert.Contains("first", snippet);
        Assert.DoesNotContain("second", snippet);
    }

    // ---- Snippet: single-line output ---------------------------------------

    [Fact]
    public void Snippet_collapses_newlines_so_it_renders_on_one_line()
    {
        var snippet = ConversationSearch.Snippet("line one\nNEEDLE\nline three", "NEEDLE");
        Assert.NotNull(snippet);
        Assert.DoesNotContain("\n", snippet);
        Assert.DoesNotContain("\r", snippet);
        Assert.Equal("line one NEEDLE line three", snippet);
    }

    [Fact]
    public void Snippet_collapses_whitespace_runs()
    {
        var snippet = ConversationSearch.Snippet("lots     of\t\tspace NEEDLE", "NEEDLE");
        Assert.Equal("lots of space NEEDLE", snippet);
    }

    [Fact]
    public void Snippet_does_not_start_or_end_with_stray_space()
    {
        // The window can cut mid-whitespace; that shouldn't show up as a padded quote.
        var snippet = ConversationSearch.Snippet("aaaa     NEEDLE     bbbb", "NEEDLE", radius: 4);
        Assert.NotNull(snippet);
        var inner = snippet!.Trim('…');
        Assert.Equal(inner.Trim(), inner);
    }

    // ---- IsSearchable ------------------------------------------------------

    [Theory]
    [InlineData("ab")]
    [InlineData("divider")]
    public void IsSearchable_accepts_queries_worth_scanning_for(string query)
    {
        Assert.True(ConversationSearch.IsSearchable(query));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    public void IsSearchable_rejects_queries_too_short_to_narrow_anything(string? query)
    {
        // One character would match nearly every conversation — not worth reading 60 log files.
        Assert.False(ConversationSearch.IsSearchable(query));
    }

    [Fact]
    public void IsSearchable_ignores_surrounding_whitespace()
    {
        Assert.False(ConversationSearch.IsSearchable(" a "));
        Assert.True(ConversationSearch.IsSearchable(" ab "));
    }
}
