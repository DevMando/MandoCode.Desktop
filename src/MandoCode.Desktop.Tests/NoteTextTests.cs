using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// Regression tests for the note editor's newline handling. A WinUI TextBox holds every newline as a
/// bare CR, so a round trip through the editor has to restore the note's own convention. Two real
/// bugs came out of this, both caught by watching the app rather than the compiler: a CRLF note being
/// rewritten as CR-only (one endless line in Notepad), and every OPEN registering as an edit, which
/// autosaved untouched notes.
/// </summary>
public sealed class NoteTextTests
{
    [Fact]
    public void DetectNewline_prefers_CRLF_when_the_file_has_any()
    {
        Assert.Equal("\r\n", NoteText.DetectNewline("a\r\nb"));
        Assert.Equal("\r\n", NoteText.DetectNewline("a\nb\r\nc"));   // mixed: CRLF wins
    }

    [Fact]
    public void DetectNewline_keeps_LF_for_an_LF_only_file()
    {
        Assert.Equal("\n", NoteText.DetectNewline("a\nb\nc"));
    }

    [Fact]
    public void DetectNewline_falls_back_to_the_platform_default()
    {
        // Nothing to copy: a new or single-line note.
        Assert.Equal(Environment.NewLine, NoteText.DetectNewline(""));
        Assert.Equal(Environment.NewLine, NoteText.DetectNewline("one line, no newline"));
    }

    [Fact]
    public void ToFileText_restores_CRLF_from_the_editor_form()
    {
        // What the TextBox hands back after a two-line note is edited: bare CRs.
        Assert.Equal("a\r\nb\r\nc", NoteText.ToFileText("a\rb\rc", "\r\n"));
    }

    [Fact]
    public void ToFileText_restores_LF_for_an_LF_note()
    {
        Assert.Equal("a\nb", NoteText.ToFileText("a\rb", "\n"));
    }

    [Fact]
    public void ToFileText_never_leaves_a_bare_CR_behind()
    {
        var result = NoteText.ToFileText("a\rb\r\nc\nd", "\r\n");

        Assert.Equal("a\r\nb\r\nc\r\nd", result);
        // The failure mode this guards: a lone CR that Notepad renders as no break at all.
        Assert.DoesNotContain('\r', result.Replace("\r\n", ""));
    }

    [Fact]
    public void ToFileText_is_idempotent()
    {
        // Saving twice with no edit in between must not double up line endings.
        var once = NoteText.ToFileText("a\rb", "\r\n");
        Assert.Equal(once, NoteText.ToFileText(once, "\r\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no newlines at all")]
    public void ToFileText_leaves_newline_free_text_alone(string text)
    {
        Assert.Equal(text, NoteText.ToFileText(text, "\r\n"));
    }

    [Fact]
    public void A_round_trip_through_the_editor_form_preserves_the_file()
    {
        // The full path: file text in, editor normalization, file text back out. Equality here is
        // what makes "opening a note doesn't change it" true.
        foreach (var original in new[] { "a\r\nb\r\n", "a\nb\n", "single", "" })
        {
            var newline = NoteText.DetectNewline(original);
            var editorForm = original.Replace("\r\n", "\r").Replace('\n', '\r');   // what the TextBox does

            Assert.Equal(original, NoteText.ToFileText(editorForm, newline));
        }
    }
}
