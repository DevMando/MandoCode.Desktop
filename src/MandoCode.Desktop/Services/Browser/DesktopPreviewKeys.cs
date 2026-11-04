using System.Text.Json;

namespace MandoCode.Desktop.Services;

/// <summary>One keyboard key, described the way a browser key event describes it.</summary>
public sealed record BrowserKey(string Name, string Key, string Code, int VirtualKey, string Text = "", int Location = 0);

/// <summary>
/// Maps the key names an agent may ask for onto browser key-event fields. Only names in this table
/// are dispatchable: an unknown name is reported rather than guessed at, so a typo can never
/// quietly send some other key into the page.
/// </summary>
public static class DesktopPreviewKeys
{
    public const int Alt = 1, Control = 2, Meta = 4, Shift = 8;

    private static readonly Dictionary<string, BrowserKey> Named = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["return"] = "Enter", ["esc"] = "Escape", ["del"] = "Delete", ["spacebar"] = "Space",
        ["up"] = "ArrowUp", ["down"] = "ArrowDown", ["left"] = "ArrowLeft", ["right"] = "ArrowRight",
        ["ctrl"] = "Control", ["cmd"] = "Meta", ["command"] = "Meta", ["win"] = "Meta",
        ["pgup"] = "PageUp", ["pgdn"] = "PageDown", ["ins"] = "Insert",
    };

    // A shifted character carries the physical key's code and virtual key code, not its own.
    private const string Unshifted = "`-=[]\\;',./";
    private const string Shifted = "~_+{}|:\"<>?";
    private const string ShiftedDigits = ")!@#$%^&*(";
    private static readonly string[] PunctuationCodes =
        ["Backquote", "Minus", "Equal", "BracketLeft", "BracketRight", "Backslash", "Semicolon", "Quote", "Comma", "Period", "Slash"];
    private static readonly int[] PunctuationVirtualKeys = [192, 189, 187, 219, 221, 220, 186, 222, 188, 190, 191];

    static DesktopPreviewKeys()
    {
        void Add(string name, string key, string code, int virtualKey, string text = "", int location = 0) =>
            Named[name] = new BrowserKey(name, key, code, virtualKey, text, location);

        Add("Enter", "Enter", "Enter", 13, "\r");
        Add("Tab", "Tab", "Tab", 9, "\t");
        Add("Space", " ", "Space", 32, " ");
        Add("Escape", "Escape", "Escape", 27);
        Add("Backspace", "Backspace", "Backspace", 8);
        Add("Delete", "Delete", "Delete", 46);
        Add("Insert", "Insert", "Insert", 45);
        Add("ArrowUp", "ArrowUp", "ArrowUp", 38);
        Add("ArrowDown", "ArrowDown", "ArrowDown", 40);
        Add("ArrowLeft", "ArrowLeft", "ArrowLeft", 37);
        Add("ArrowRight", "ArrowRight", "ArrowRight", 39);
        Add("Home", "Home", "Home", 36);
        Add("End", "End", "End", 35);
        Add("PageUp", "PageUp", "PageUp", 33);
        Add("PageDown", "PageDown", "PageDown", 34);
        // Modifier keys are dispatchable in their own right so a game can observe a held Shift.
        Add("Shift", "Shift", "ShiftLeft", 16, "", 1);
        Add("Control", "Control", "ControlLeft", 17, "", 1);
        Add("Alt", "Alt", "AltLeft", 18, "", 1);
        Add("Meta", "Meta", "MetaLeft", 91, "", 1);
        for (var i = 1; i <= 12; i++) Add("F" + i, "F" + i, "F" + i, 111 + i);
    }

    /// <summary>Names listed to the agent when it asks for a key this table does not have.</summary>
    public static string KeyNames => string.Join(", ", Named.Keys);

    /// <summary>Resolves a key name or single printable character. Shifted characters report the Shift modifier.</summary>
    public static bool TryResolve(string? name, out BrowserKey key, out int modifiers, out string error)
    {
        key = null!;
        modifiers = 0;
        error = "";
        var requested = name?.Trim() ?? "";
        if (requested.Length == 0)
        {
            error = "A key is required, for example Enter, Tab, ArrowUp, or a single printable character.";
            return false;
        }
        if (Aliases.TryGetValue(requested, out var canonical)) requested = canonical;
        if (Named.TryGetValue(requested, out var named))
        {
            key = named;
            return true;
        }
        if (requested.Length == 1 && TryResolveCharacter(requested[0], out key, out modifiers)) return true;
        error = $"Unknown key '{(requested.Length > 40 ? requested[..40] : requested)}'. " +
            $"Use one printable character, or one of: {KeyNames}. To type a whole string, use fill_desktop_preview.";
        return false;
    }

    /// <summary>
    /// Builds the browser key-event payload for one key transition. A key that produces text is
    /// sent as a full key-down so the character reaches the page; everything else is a raw key-down,
    /// which is what makes Backspace delete and arrow keys move instead of inserting characters.
    /// </summary>
    public static string BuildKeyEvent(BrowserKey key, int modifiers, bool down)
    {
        // A Ctrl/Alt/Meta chord is a shortcut, not text entry; only plain and shifted keys insert text.
        var text = (modifiers & ~Shift) == 0 ? key.Text : "";
        return JsonSerializer.Serialize(new
        {
            type = down ? text.Length > 0 ? "keyDown" : "rawKeyDown" : "keyUp",
            modifiers,
            key = key.Key,
            code = key.Code,
            windowsVirtualKeyCode = key.VirtualKey,
            text,
            unmodifiedText = text,
            location = key.Location,
        });
    }

    /// <summary>Parses "ctrl", "ctrl+shift", or "ctrl, alt" into the browser modifier bitmask.</summary>
    public static bool TryParseModifiers(string? text, out int modifiers, out string error)
    {
        modifiers = 0;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.Length > 80)
        {
            error = "The modifier list is too long. Use ctrl, shift, alt, and meta.";
            return false;
        }
        foreach (var part in text.Split(['+', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bit = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Control,
                "shift" => Shift,
                "alt" or "option" => Alt,
                "meta" or "cmd" or "command" or "win" or "super" => Meta,
                _ => 0,
            };
            if (bit == 0)
            {
                error = $"Unknown modifier '{part}'. Use ctrl, shift, alt, or meta.";
                return false;
            }
            modifiers |= bit;
        }
        return true;
    }

    private static bool TryResolveCharacter(char c, out BrowserKey key, out int modifiers)
    {
        key = null!;
        modifiers = 0;
        var text = c.ToString();
        if (c is >= 'a' and <= 'z')
        {
            key = new(text, text, "Key" + char.ToUpperInvariant(c), char.ToUpperInvariant(c), text);
            return true;
        }
        if (c is >= 'A' and <= 'Z')
        {
            modifiers = Shift;
            key = new(text, text, "Key" + c, c, text);
            return true;
        }
        if (c is >= '0' and <= '9')
        {
            key = new(text, text, "Digit" + c, c, text);
            return true;
        }
        var shiftedDigit = ShiftedDigits.IndexOf(c);
        if (shiftedDigit >= 0)
        {
            modifiers = Shift;
            key = new(text, text, "Digit" + shiftedDigit, '0' + shiftedDigit, text);
            return true;
        }
        var plain = Unshifted.IndexOf(c);
        if (plain >= 0)
        {
            key = new(text, text, PunctuationCodes[plain], PunctuationVirtualKeys[plain], text);
            return true;
        }
        var shifted = Shifted.IndexOf(c);
        if (shifted >= 0)
        {
            modifiers = Shift;
            key = new(text, text, PunctuationCodes[shifted], PunctuationVirtualKeys[shifted], text);
            return true;
        }
        return false;
    }
}
