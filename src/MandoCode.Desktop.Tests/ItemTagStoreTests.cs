using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public sealed class ItemTagStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mandocode-tags-" + Guid.NewGuid().ToString("N"));
    private readonly ItemTagStore _store;

    public ItemTagStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new ItemTagStore(Path.Combine(_directory, "tags.json"));
    }

    [Fact]
    public void Tags_are_separate_for_skills_and_mcps()
    {
        _store.SetItemTags(TagScope.Skills, "C:\\skills\\review", ["quality"]);
        _store.SetItemTags(TagScope.Mcps, "database", ["production"]);

        Assert.Equal(["quality"], _store.GetTags(TagScope.Skills));
        Assert.Equal(["production"], _store.GetTags(TagScope.Mcps));
        Assert.Equal(["quality"], _store.GetItemTags(TagScope.Skills, "C:\\skills\\review"));
        Assert.DoesNotContain("quality", _store.GetItemTags(TagScope.Mcps, "database"));
    }

    [Fact]
    public void Rename_moves_an_items_tags()
    {
        _store.SetItemTags(TagScope.Mcps, "old-name", ["local", "utility"]);

        _store.RenameItem(TagScope.Mcps, "old-name", "new-name");

        Assert.Empty(_store.GetItemTags(TagScope.Mcps, "old-name"));
        Assert.Equal(["local", "utility"], _store.GetItemTags(TagScope.Mcps, "new-name"));
    }

    [Fact]
    public void Deleting_a_tag_removes_it_from_all_assignments()
    {
        _store.SetItemTags(TagScope.Skills, "one", ["review", "shared"]);
        _store.SetItemTags(TagScope.Skills, "two", ["shared"]);

        _store.DeleteTag(TagScope.Skills, "shared");

        Assert.Equal(["review"], _store.GetItemTags(TagScope.Skills, "one"));
        Assert.Empty(_store.GetItemTags(TagScope.Skills, "two"));
        Assert.Equal(["review"], _store.GetTags(TagScope.Skills));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
