namespace MandoCode.Desktop.Services;

/// <summary>Exact tag identity; a pulled family member never marks sibling variants pulled.</summary>
public sealed class OllamaModelInventory
{
    private readonly HashSet<string> _tags;
    public OllamaModelInventory(IEnumerable<string> names) =>
        _tags = names.Select(Normalize).ToHashSet(StringComparer.Ordinal);

    public static string Normalize(string name)
    {
        var tag = name.Trim();
        if (tag.StartsWith("library/", StringComparison.Ordinal)) tag = tag[8..];
        return tag.LastIndexOf(':') > tag.LastIndexOf('/') ? tag : tag + ":latest";
    }

    public bool Contains(string name) => _tags.Contains(Normalize(name));
    public int CountFamily(string name, bool cloudOnly = false)
    {
        var family = Normalize(name);
        family = family[..family.LastIndexOf(':')];
        return _tags.Count(tag => tag[..tag.LastIndexOf(':')] == family
            && (!cloudOnly || MandoCode.Models.MandoCodeConfig.IsCloudModel(tag)));
    }
}
