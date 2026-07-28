using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public class CardPreviewTests
{
    // ---- Trim: the quoted user turns -------------------------------------------------

    [Fact]
    public void Trim_returns_null_for_nothing_to_show()
    {
        Assert.Null(CardPreview.Trim(null));
        Assert.Null(CardPreview.Trim(""));
        Assert.Null(CardPreview.Trim("   \n  "));
    }

    [Fact]
    public void Trim_keeps_a_short_message_whole_and_unellipsised()
        => Assert.Equal("checkout main and pull latest", CardPreview.Trim("  checkout main and pull latest  "));

    [Fact]
    public void Trim_caps_a_long_message_with_an_ellipsis()
    {
        var clipped = CardPreview.Trim(new string('x', CardPreview.UserChars + 50));
        Assert.Equal(CardPreview.UserChars + 1, clipped!.Length);   // the cap plus the ellipsis
        Assert.EndsWith("…", clipped);
    }

    // ---- ClipReply: the agent's answer ----------------------------------------------

    [Fact]
    public void ClipReply_returns_null_when_there_is_no_reply()
    {
        Assert.Null(CardPreview.ClipReply(null));
        Assert.Null(CardPreview.ClipReply("  "));
    }

    [Fact]
    public void ClipReply_keeps_the_first_two_sentences_and_drops_the_rest()
        => Assert.Equal(
            "Already on main. The pull failed.",
            CardPreview.ClipReply("Already on main. The pull failed. Git could not authenticate. Try gh."));

    [Fact]
    public void ClipReply_keeps_a_one_sentence_reply_whole()
        => Assert.Equal("Done — the branch is clean.", CardPreview.ClipReply("Done — the branch is clean."));

    [Fact]
    public void ClipReply_keeps_a_reply_with_no_terminator_at_all()
        => Assert.Equal("no trailing period here", CardPreview.ClipReply("no trailing period here"));

    [Fact]
    public void ClipReply_collapses_paragraphs_into_one_line()
        => Assert.Equal(
            "First line. Second line.",
            CardPreview.ClipReply("First line.\n\n   Second line.\n"));

    [Fact]
    public void ClipReply_drops_fenced_code_blocks()
        => Assert.Equal(
            "Here is the fix.",
            CardPreview.ClipReply("Here is the fix.\n\n```csharp\nvar x = 1;   // not card material\n```\n"));

    [Fact]
    public void ClipReply_drops_tilde_fences_too()
        => Assert.Equal("Ran it.", CardPreview.ClipReply("Ran it.\n~~~\ngit status\n~~~"));

    [Fact]
    public void ClipReply_returns_null_when_only_code_remains()
        => Assert.Null(CardPreview.ClipReply("```\ngit push --force\n```"));

    [Fact]
    public void ClipReply_strips_headings_quotes_and_bullets()
        => Assert.Equal(
            "Summary Pulled main. Synced the submodule.",
            CardPreview.ClipReply("## Summary\n\n- Pulled main.\n- Synced the submodule."));

    [Fact]
    public void ClipReply_strips_numbered_items_but_keeps_prose_that_starts_with_a_digit()
    {
        Assert.Equal("Fetch. Merge.", CardPreview.ClipReply("1. Fetch.\n2) Merge."));
        Assert.Equal("27 files changed.", CardPreview.ClipReply("27 files changed."));
    }

    [Fact]
    public void ClipReply_strips_task_list_checkboxes()
        => Assert.Equal("done thing", CardPreview.ClipReply("- [x] done thing"));

    [Fact]
    public void ClipReply_strips_emphasis_and_code_spans_but_keeps_underscores()
        => Assert.Equal(
            "The LastMessage field on session_archive is set.",
            CardPreview.ClipReply("The **LastMessage** field on `session_archive` is set."));

    [Fact]
    public void ClipReply_keeps_link_text_and_drops_the_target()
        => Assert.Equal(
            "See MainWindow.xaml for the template.",
            CardPreview.ClipReply("See [MainWindow.xaml](src/MandoCode.Desktop/MainWindow.xaml) for the template."));

    [Fact]
    public void ClipReply_drops_table_separator_rows()
        => Assert.Equal(
            "Results: | file | lines |",
            CardPreview.ClipReply("Results:\n\n| file | lines |\n|------|-------|"));

    [Fact]
    public void ClipReply_does_not_split_on_decimals_or_file_names()
        => Assert.Equal(
            "Bumped to v1.2 in MainWindow.xaml.cs today. Second sentence.",
            CardPreview.ClipReply("Bumped to v1.2 in MainWindow.xaml.cs today. Second sentence. Third."));

    [Fact]
    public void ClipReply_treats_a_terminator_cluster_as_one_sentence_end()
        => Assert.Equal("Wait, what?! It worked.", CardPreview.ClipReply("Wait, what?! It worked. Really."));

    [Fact]
    public void ClipReply_does_not_split_on_a_short_abbreviation()
        => Assert.Equal(
            "Use gh, e.g. gh auth login, to sign in. Then pull.",
            CardPreview.ClipReply("Use gh, e.g. gh auth login, to sign in. Then pull. And build."));

    [Fact]
    public void ClipReply_hard_caps_a_long_two_sentence_reply()
    {
        var wordy = new string('a', 200) + ". " + new string('b', 200) + ".";
        var clipped = CardPreview.ClipReply(wordy);
        Assert.Equal(CardPreview.ReplyChars + 1, clipped!.Length);   // the cap plus the ellipsis
        Assert.EndsWith("…", clipped);
    }
}
