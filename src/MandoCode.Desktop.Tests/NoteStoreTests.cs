using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// Tests for the jot pad against real temp folders. Notes are the one store in the app that IS the
/// filesystem — there's no JSON index to assert against, so these verify the actual claim: what's in
/// the pad folder is what the panel shows, and grouping is a subfolder rather than metadata.
/// </summary>
public sealed class NoteStoreTests : IDisposable
{
    private readonly string _root;
    private readonly NoteStore _store;

    public NoteStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mandocode-pad-" + Guid.NewGuid().ToString("N"));
        _store = new NoteStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Writes a note directly, as Notepad or a sync client would — bypassing the store.</summary>
    private string WriteNote(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- discovery ----

    [Fact]
    public void Discover_finds_a_note_written_outside_the_app()
    {
        WriteNote("ideas.txt", "first line\nsecond line");

        var note = Assert.Single(_store.Discover());

        Assert.Equal("ideas.txt", note.FileName);
        Assert.Equal("ideas", note.Title);
        Assert.Equal("", note.Group);
        Assert.Equal("Unfiled", note.GroupLabel);
        Assert.Equal("first line", note.Preview);
    }

    [Fact]
    public void Discover_is_empty_when_the_pad_folder_does_not_exist_yet()
    {
        // Nothing is pre-created — a fresh install has no pad until the first note.
        Assert.False(Directory.Exists(_root));
        Assert.Empty(_store.Discover());
    }

    [Fact]
    public void Discover_reads_one_level_of_subfolders_as_groups()
    {
        WriteNote("loose.txt", "unfiled");
        WriteNote(Path.Combine("MandoCode.Desktop", "panel.txt"), "filed under a project");

        var notes = _store.Discover();

        Assert.Equal(2, notes.Count);
        Assert.Equal("MandoCode.Desktop", notes.Single(n => n.FileName == "panel.txt").Group);
        Assert.Equal("", notes.Single(n => n.FileName == "loose.txt").Group);
    }

    [Fact]
    public void Discover_does_not_recurse_past_one_level()
    {
        // A jot pad with a folder hierarchy is a filing system; search is the better answer.
        WriteNote(Path.Combine("project", "deep", "buried.txt"), "too deep");

        Assert.Empty(_store.Discover());
    }

    [Fact]
    public void Discover_skips_dot_folders_and_non_note_files()
    {
        WriteNote("keep.txt", "text note");
        WriteNote("keep.md", "# markdown note");
        WriteNote("skip.png", "not a note");
        WriteNote(Path.Combine(".git", "config.txt"), "somebody else's business");

        var names = _store.Discover().Select(n => n.FileName).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "keep.md", "keep.txt" }, names);
    }

    [Fact]
    public void Discover_returns_newest_first_across_groups()
    {
        var old = WriteNote(Path.Combine("proj", "old.txt"), "old");
        var recent = WriteNote("new.txt", "new");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-3));
        File.SetLastWriteTime(recent, DateTime.Now);

        Assert.Equal(new[] { "new.txt", "old.txt" }, _store.Discover().Select(n => n.FileName).ToArray());
    }

    [Fact]
    public void Discover_caps_how_much_of_a_note_is_read()
    {
        WriteNote("big.txt", new string('x', NoteStore.MaxTextBytes + 5_000));

        var note = Assert.Single(_store.Discover());

        // The row still reports the true size; only the searchable/assistant-visible body is capped.
        Assert.Equal(NoteStore.MaxTextBytes, note.Text.Length);
        Assert.Equal(NoteStore.MaxTextBytes + 5_000, note.Bytes);
    }

    // ---- create ----

    [Fact]
    public void Create_makes_the_pad_folder_on_first_use()
    {
        var note = _store.Create();

        Assert.True(File.Exists(note.Path));
        Assert.Equal("Untitled.txt", note.FileName);
        Assert.Equal(_root, Path.GetDirectoryName(note.Path));
        Assert.Equal("", File.ReadAllText(note.Path));
        Assert.Equal("", note.Group);
    }

    [Fact]
    public void Create_files_a_note_under_its_group_folder()
    {
        var note = _store.Create(group: "MandoCode.Desktop");

        Assert.Equal("MandoCode.Desktop", note.Group);
        Assert.Equal(Path.Combine(_root, "MandoCode.Desktop"), Path.GetDirectoryName(note.Path));
        Assert.True(File.Exists(note.Path));
    }

    [Fact]
    public void Create_sanitizes_a_group_that_cannot_be_a_folder_name()
    {
        var note = _store.Create(group: "weird:name/here");

        Assert.Equal("weird name here", note.Group);
        Assert.True(File.Exists(note.Path));
    }

    [Fact]
    public void Create_uniquifies_within_its_folder_instead_of_overwriting()
    {
        var first = _store.Create();
        var second = _store.Create();
        var third = _store.Create(title: "Untitled");
        // Same name in a different group is a different file, so it keeps the plain name.
        var grouped = _store.Create(group: "proj");

        Assert.Equal("Untitled.txt", first.FileName);
        Assert.Equal("Untitled 2.txt", second.FileName);
        Assert.Equal("Untitled 3.txt", third.FileName);
        Assert.Equal("Untitled.txt", grouped.FileName);
    }

    [Fact]
    public void Create_falls_back_to_a_default_name_for_an_unusable_title()
    {
        Assert.Equal("Untitled.txt", _store.Create(title: "  ///  ").FileName);
    }

    [Fact]
    public void Create_raises_Changed()
    {
        var fired = 0;
        _store.Changed += () => fired++;

        _store.Create();

        Assert.Equal(1, fired);
    }

    // ---- rename ----

    [Fact]
    public void Rename_keeps_the_note_in_its_group_and_keeps_its_text()
    {
        var note = _store.Create(group: "proj");
        File.WriteAllText(note.Path, "body text");

        var moved = _store.Rename(note, "Q3 rollout");

        Assert.NotNull(moved);
        Assert.Equal("Q3 rollout.txt", moved!.FileName);
        Assert.Equal("proj", moved.Group);
        Assert.Equal(Path.Combine(_root, "proj"), Path.GetDirectoryName(moved.Path));
        Assert.False(File.Exists(note.Path));
        Assert.Equal("body text", File.ReadAllText(moved.Path));
    }

    [Fact]
    public void Rename_sanitizes_a_title_that_cannot_be_a_file_name()
    {
        var note = _store.Create();

        var moved = _store.Rename(note, "ideas: rollout/plan?");

        Assert.Equal("ideas rollout plan.txt", moved!.FileName);
    }

    [Fact]
    public void Rename_keeps_the_extension()
    {
        WriteNote("thoughts.md", "# hi");
        var note = Assert.Single(_store.Discover());

        var moved = _store.Rename(note, "thoughts v2");

        Assert.Equal("thoughts v2.md", moved!.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Untitled")]   // same title — a no-op, not a rename to "Untitled 2"
    public void Rename_declines_a_pointless_title(string title)
    {
        var note = _store.Create();

        Assert.Null(_store.Rename(note, title));
        Assert.True(File.Exists(note.Path));
    }

    // ---- delete / reread ----

    [Fact]
    public void Delete_removes_the_file()
    {
        var note = _store.Create();

        Assert.True(_store.Delete(note));
        Assert.False(File.Exists(note.Path));
    }

    [Fact]
    public void Delete_of_an_already_gone_note_still_succeeds()
    {
        var note = _store.Create();
        File.Delete(note.Path);

        Assert.True(_store.Delete(note));
    }

    [Fact]
    public void Reread_picks_up_an_edit_made_outside_the_app()
    {
        var note = _store.Create();
        File.WriteAllText(note.Path, "written in VS Code\nand this");

        var fresh = _store.Reread(note);

        Assert.NotNull(fresh);
        Assert.Equal("written in VS Code", fresh!.Preview);
        Assert.Equal(File.ReadAllText(note.Path).Length, (int)fresh.Bytes);
    }

    [Fact]
    public void Reread_reports_a_deleted_note_as_gone()
    {
        var note = _store.Create();
        File.Delete(note.Path);

        Assert.Null(_store.Reread(note));
    }

    // ---- pure helpers ----

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a b c d e f g h i j")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("...hidden", "hidden")]              // a leading dot hides the file (and dot-dirs are skipped)
    [InlineData("", "")]
    [InlineData("////", "")]
    public void SanitizeTitle_produces_a_legal_name(string input, string expected)
    {
        Assert.Equal(expected, NoteStore.SanitizeTitle(input));
    }

    [Fact]
    public void UniquePath_collides_case_insensitively()
    {
        // Windows won't hold both "ideas.txt" and "Ideas.txt"; silently overwriting one with the other
        // is the worst outcome a notes app can have.
        WriteNote("ideas.txt", "x");

        var path = NoteStore.UniquePath(_root, "IDEAS", ".txt");

        Assert.Equal("IDEAS 2.txt", Path.GetFileName(path));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   \n\n  ", "")]
    [InlineData("\n\n  the gist  \nmore", "the gist")]
    [InlineData("# Heading\nbody", "Heading")]
    [InlineData("- a bullet", "a bullet")]
    public void Preview_is_the_first_line_that_says_something(string text, string expected)
    {
        Assert.Equal(expected, NoteStore.Preview(text));
    }

    [Fact]
    public void Preview_caps_a_long_line_with_an_ellipsis()
    {
        var preview = NoteStore.Preview(new string('a', 400));

        Assert.EndsWith("…", preview);
        Assert.True(preview.Length < 200, $"preview was {preview.Length} chars");
    }

    [Fact]
    public void Matches_hits_on_name_group_and_body()
    {
        WriteNote(Path.Combine("widget-api", "ideas.txt"), "line one\nremember the rate limiter\nthree");
        var note = Assert.Single(_store.Discover());

        Assert.True(NoteStore.Matches(note, "ideas"));            // name
        Assert.True(NoteStore.Matches(note, "widget-api"));       // group
        Assert.True(NoteStore.Matches(note, "RATE LIMITER"));     // body, case-insensitive
        Assert.True(NoteStore.Matches(note, ""));                 // empty query shows everything
        Assert.False(NoteStore.Matches(note, "nonsense"));
    }

    [Fact]
    public void MatchSnippet_quotes_the_matching_body_line()
    {
        WriteNote("ideas.txt", "the gist\nremember the rate limiter");
        var note = Assert.Single(_store.Discover());

        Assert.Equal("remember the rate limiter", NoteStore.MatchSnippet(note, "rate limiter"));
    }

    [Fact]
    public void MatchSnippet_is_null_when_the_hit_is_already_on_the_card()
    {
        WriteNote("ideas.txt", "the gist\nmore text");
        var note = Assert.Single(_store.Discover());

        Assert.Null(NoteStore.MatchSnippet(note, "gist"));
        Assert.Null(NoteStore.MatchSnippet(note, "nonsense"));
    }

    [Theory]
    [InlineData(0, "empty")]
    [InlineData(412, "412 B")]
    [InlineData(3174, "3.1 KB")]
    public void SizeLabel_reads_like_a_file_size(long bytes, string expected)
    {
        var note = new NoteEntry
        {
            Path = Path.Combine(_root, "a.txt"),
            Group = "",
            ModifiedAt = DateTimeOffset.Now,
            Bytes = bytes,
            Preview = "",
            Text = "",
        };

        Assert.Equal(expected, note.SizeLabel);
    }

    [Fact]
    public void DefaultRoot_is_the_mandocode_folder()
    {
        // Beside the CLI's own config.json, not in LocalAppData: these are the user's files.
        Assert.EndsWith(Path.Combine(".mandocode", "notes"), NoteStore.DefaultRoot);
    }
}
