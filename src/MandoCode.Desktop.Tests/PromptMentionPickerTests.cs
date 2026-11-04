using MandoCode.Desktop.Services;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public class PromptMentionPickerTests
{
    [Fact]
    public void FilesAreDefault_AndSwitchingKeepsTheTypedQuery()
    {
        var picker = new PromptMentionPicker();
        Assert.True(picker.Update("ask @Ni", 7));
        Assert.Equal(MentionPickerTarget.Files, picker.Target);
        Assert.Equal("Files — Press [Tab] for Agents", picker.Title);
        Assert.True(picker.Toggle());
        Assert.Equal("Agents — Press [Tab] for Files", picker.Title);
        Assert.True(picker.Update("ask @Nin", 8));
        Assert.Equal(MentionPickerTarget.Agents, picker.Target);
        Assert.Equal("Nin", picker.Fragment!.Query);
        Assert.True(picker.Toggle());
        Assert.Equal(MentionPickerTarget.Files, picker.Target);
    }

    [Theory]
    [InlineData("email@example.com")]
    [InlineData("a completed @Ninja ")]
    [InlineData("plain text")]
    [InlineData("@\"finished folder/\"")]
    public void CompletedTokensAndEmails_DoNotOpenPicker(string text)
    {
        var picker = new PromptMentionPicker();
        Assert.False(picker.Update(text, text.Length));
        Assert.False(picker.Toggle());
    }

    [Fact]
    public void DismissalSurvivesFileNotifications_ButEditingOpensAgain()
    {
        var picker = new PromptMentionPicker();
        picker.Update("@", 1);
        picker.Toggle();
        picker.Dismiss();
        Assert.False(picker.Update("@", 1));
        Assert.True(picker.Update("@N", 2));
        Assert.Equal(MentionPickerTarget.Files, picker.Target);
        var revision = picker.Revision;
        picker.Update("@Na", 3);
        Assert.True(picker.Revision > revision);
    }

    [Fact]
    public void CaretTokenPreservesFollowingText_AndAllowsQuotedFolderDrillDown()
    {
        var picker = new PromptMentionPicker();
        Assert.True(picker.Update("inspect @src next", 12));
        Assert.Equal(new MentionFragment(8, 12, "src"), picker.Fragment);
        const string quoted = "@\"My Folder/sub";
        Assert.True(picker.Update(quoted, quoted.Length));
        Assert.Equal("My Folder/sub", picker.Fragment!.Query);
    }

    [Fact]
    public void QuotedFolderPrefixDecodesJsonEscapesForLookup()
    {
        var picker = new PromptMentionPicker();
        var reference = FileReferenceToken.Format("My & Folder/");
        var prefix = reference[..^1];
        Assert.True(picker.Update(prefix, prefix.Length));
        Assert.Equal("My & Folder/", picker.Fragment!.Query);
    }

    [Theory]
    [InlineData("Ninja", true, "./Ninja")]
    [InlineData("src/Ninja", false, "src/Ninja")]
    [InlineData("My Folder/a b.txt", false, "My Folder/a b.txt")]
    public void FileInsertionRemainsAFile_AndRoundTripsSpaces(string path, bool collision, string expected)
        => Assert.Equal(expected, Assert.Single(FileReferenceToken.Paths(PromptMentionPicker.FileReference(path, collision))));
}
