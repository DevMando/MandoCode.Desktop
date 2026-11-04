using MandoCode.Services;
using System.Text.Json;

namespace MandoCode.Desktop.Services;

public enum MentionPickerTarget { Files, Agents }
public sealed record MentionFragment(int Start, int End, string Query);

/// <summary>Caret-local mention state, independent of WinUI and background lookup timing.</summary>
public sealed class PromptMentionPicker
{
    private string? _prefix, _dismissedPrefix;
    public MentionFragment? Fragment { get; private set; }
    public MentionPickerTarget Target { get; private set; }
    public long Revision { get; private set; }
    public string Title => Target == MentionPickerTarget.Agents
        ? "Agents — Press [Tab] for Files" : "Files — Press [Tab] for Agents";

    public bool Update(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var prefix = text[..caret];
        var start = prefix.LastIndexOf('@');
        var query = start < 0 ? "" : prefix[(start + 1)..];
        var quoted = query.StartsWith('"');
        var valid = start >= 0 && (start == 0 || char.IsWhiteSpace(text[start - 1]))
            && (quoted ? !query[1..].Contains('"') : !query.Any(char.IsWhiteSpace));
        if (!valid || prefix == _dismissedPrefix)
        {
            if (Fragment is not null) Revision++;
            Fragment = null; Target = MentionPickerTarget.Files; _prefix = null;
            if (!valid) _dismissedPrefix = null;
            return false;
        }
        if (prefix != _prefix) Revision++;
        if (Fragment is null || Fragment.Start != start) Target = MentionPickerTarget.Files;
        var fragment = quoted ? query[1..] : query;
        if (quoted)
        {
            try { fragment = JsonSerializer.Deserialize<string>(query + "\"") ?? fragment; }
            catch (JsonException) { } // A partially typed escape is still editable.
        }
        Fragment = new(start, caret, fragment);
        _prefix = prefix; _dismissedPrefix = null;
        return true;
    }

    public bool Toggle()
    {
        if (Fragment is null) return false;
        Target = Target == MentionPickerTarget.Files ? MentionPickerTarget.Agents : MentionPickerTarget.Files;
        Revision++;
        return true;
    }

    public void Dismiss()
    {
        _dismissedPrefix = _prefix; _prefix = null; Fragment = null;
        Target = MentionPickerTarget.Files; Revision++;
    }

    public static string FileReference(string path, bool collidesWithAgent)
        => FileReferenceToken.Format(collidesWithAgent && !path.Contains('/') && !path.Contains('\\') ? "./" + path : path);
}
