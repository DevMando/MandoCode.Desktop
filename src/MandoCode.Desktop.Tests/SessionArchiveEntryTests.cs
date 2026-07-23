using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// Locks the pure display derivations used by the History and Snapshots panels — the project-label
/// path parsing (whose null / trailing-slash / root-only cases are the kind that silently regress)
/// and the preview placeholder. No store is constructed: these are property getters on the data
/// records, so there's no filesystem contact.
/// </summary>
public sealed class SessionArchiveEntryTests
{
    private static SessionArchiveEntry Entry(string project = @"C:\work\mando", string? preview = "hi") =>
        new()
        {
            Key = "k1",
            Title = "Agent 1",
            ProjectRoot = project,
            Model = "opus",
            ClosedAt = DateTimeOffset.Now,
            TurnCount = 3,
            Preview = preview,
        };

    [Fact]
    public void ProjectLabel_UsesLeafName()
        => Assert.Equal("mando", Entry(@"C:\work\mando").ProjectLabel);

    [Fact]
    public void ProjectLabel_IgnoresTrailingSeparator()
        => Assert.Equal("mando", Entry(@"C:\work\mando\").ProjectLabel);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ProjectLabel_FallsBackWhenBlank(string project)
        => Assert.Equal("Unknown project", Entry(project).ProjectLabel);

    [Fact]
    public void PreviewOrPlaceholder_UsesPreviewWhenPresent()
        => Assert.Equal("hi", Entry(preview: "hi").PreviewOrPlaceholder);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PreviewOrPlaceholder_PlaceholderWhenBlank(string? preview)
        => Assert.Equal("(no message text captured)", Entry(preview: preview).PreviewOrPlaceholder);

    // The same project-label rule backs snapshot grouping — a null root (snapshots taken before
    // project tracking existed) must land in one stable bucket, not throw.
    [Fact]
    public void Snapshot_ProjectLabel_HandlesNullRoot()
    {
        var snap = new ContextSnapshot
        {
            Id = 1,
            CapturedAt = DateTimeOffset.Now,
            OriginModel = "opus",
            SummarizerModel = "opus",
            Recap = "…",
            MessageCount = 2,
            ProjectRoot = null,
        };
        Assert.Equal("Unknown project", snap.ProjectLabel);
    }
}
