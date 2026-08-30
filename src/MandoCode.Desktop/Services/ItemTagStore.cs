using System.Text.Json;

namespace MandoCode.Desktop.Services;

/// <summary>Which management surface owns a tag catalog. Tags deliberately do not cross this
/// boundary: a skill category is not implicitly an MCP-server category.</summary>
public enum TagScope { Skills, Mcps }

/// <summary>
/// Desktop-owned tags for the Skills and MCP management pages. The shared engine config and a
/// skill's portable SKILL.md remain unchanged; this is organization metadata for this Desktop
/// install. Keys are stable item identifiers (skill folder path or MCP server name).
/// </summary>
public sealed class ItemTagStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public ItemTagStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MandoCode.Desktop", "item-tags.json");
    }

    public IReadOnlyList<string> GetTags(TagScope scope)
    {
        lock (_gate) return Catalog(Load(), scope).Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> GetItemTags(TagScope scope, string itemKey)
    {
        lock (_gate)
        {
            var catalog = Catalog(Load(), scope);
            return catalog.Assignments.TryGetValue(itemKey, out var tags)
                ? tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
    }

    public void SetItemTags(TagScope scope, string itemKey, IEnumerable<string> tags)
    {
        lock (_gate)
        {
            var state = Load();
            var catalog = Catalog(state, scope);
            var normalized = Normalize(tags);
            foreach (var tag in normalized)
                AddIfMissing(catalog.Tags, tag);

            if (normalized.Count == 0) catalog.Assignments.Remove(itemKey);
            else catalog.Assignments[itemKey] = normalized;
            Save(state);
        }
    }

    public void AddTag(TagScope scope, string tag)
    {
        lock (_gate)
        {
            var normalized = Normalize([tag]);
            if (normalized.Count == 0) return;
            var state = Load();
            AddIfMissing(Catalog(state, scope).Tags, normalized[0]);
            Save(state);
        }
    }

    public void DeleteTag(TagScope scope, string tag)
    {
        lock (_gate)
        {
            var state = Load();
            var catalog = Catalog(state, scope);
            catalog.Tags.RemoveAll(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase));
            foreach (var (key, assigned) in catalog.Assignments.ToList())
            {
                assigned.RemoveAll(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase));
                if (assigned.Count == 0) catalog.Assignments.Remove(key);
            }
            Save(state);
        }
    }

    public void RenameItem(TagScope scope, string oldKey, string newKey)
    {
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return;
        lock (_gate)
        {
            var state = Load();
            var assignments = Catalog(state, scope).Assignments;
            if (assignments.Remove(oldKey, out var tags)) assignments[newKey] = tags;
            Save(state);
        }
    }

    public void DeleteItem(TagScope scope, string itemKey)
    {
        lock (_gate)
        {
            var state = Load();
            Catalog(state, scope).Assignments.Remove(itemKey);
            Save(state);
        }
    }

    private ItemTagState Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<ItemTagState>(File.ReadAllText(_path))?.Normalize() ?? new ItemTagState();
        }
        catch { }
        return new ItemTagState();
    }

    private void Save(ItemTagState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    private static ItemTagCatalog Catalog(ItemTagState state, TagScope scope) =>
        scope == TagScope.Skills ? state.Skills : state.Mcps;

    private static List<string> Normalize(IEnumerable<string> tags) => tags
        .Select(tag => tag.Trim())
        .Where(tag => tag.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static void AddIfMissing(List<string> tags, string tag)
    {
        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
    }

    private sealed class ItemTagState
    {
        public ItemTagCatalog Skills { get; set; } = new();
        public ItemTagCatalog Mcps { get; set; } = new();

        public ItemTagState Normalize()
        {
            Skills ??= new();
            Mcps ??= new();
            Skills.Normalize();
            Mcps.Normalize();
            return this;
        }
    }

    private sealed class ItemTagCatalog
    {
        public List<string> Tags { get; set; } = [];
        public Dictionary<string, List<string>> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public void Normalize()
        {
            Tags ??= [];
            Assignments ??= new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
