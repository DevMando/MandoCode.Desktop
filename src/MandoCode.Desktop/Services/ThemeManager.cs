using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MandoCode.Desktop.Services;

/// <summary>
/// One selectable UI theme. Hex values (#RRGGBB) are used verbatim as the transcript's
/// CSS variables and parsed into XAML brushes, so the WebView2 chat and the native
/// pages always agree on every surface color.
/// </summary>
public sealed record UiTheme
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool IsLight { get; init; }
    public required string Background { get; init; }
    public required string Panel { get; init; }
    public required string Border { get; init; }
    public required string Text { get; init; }
    public required string Dim { get; init; }
    public required string Accent { get; init; }
    public required string Gold { get; init; }
    public required string Sky { get; init; }
    public required string Green { get; init; }
    public required string Red { get; init; }
    public required string DiffAdd { get; init; }

    public static readonly IReadOnlyList<UiTheme> All = new[]
    {
        // First entry is the default for fresh installs (ThemeManager falls back to All[0]).
        new UiTheme
        {
            Name = "Visual Studio Dark",
            Description = "The classic VS / VS Code dark — charcoal and signature blue.",
            Background = "#1E1E1E", Panel = "#252526", Border = "#3F3F46",
            Text = "#D4D4D4", Dim = "#858585",
            Accent = "#007ACC", Gold = "#DCDCAA", Sky = "#9CDCFE",
            Green = "#89D185", Red = "#F44747", DiffAdd = "#4EC9B0",
        },
        new UiTheme
        {
            Name = "Mando Dark",
            Description = "The classic MandoCode look — deep purple, gold, and sky.",
            Background = "#16121F", Panel = "#201A2E", Border = "#362C4D",
            Text = "#E6E1F0", Dim = "#8F87A3",
            Accent = "#C864FF", Gold = "#FFC850", Sky = "#38B6FF",
            Green = "#4EC94E", Red = "#E05252", DiffAdd = "#87CEFA",
        },
        new UiTheme
        {
            Name = "Midnight Ocean",
            Description = "Deep navy and cyan — calm, cool, focused.",
            Background = "#0B1622", Panel = "#132335", Border = "#27405A",
            Text = "#DCE9F5", Dim = "#7C93AB",
            Accent = "#22D3EE", Gold = "#FFB454", Sky = "#7AA7FF",
            Green = "#34D399", Red = "#F0566A", DiffAdd = "#6EE7B7",
        },
        new UiTheme
        {
            Name = "Phosphor Fwog",
            Description = "Tree-fwog green on a dark pond — Phosphor's terminal roots, but froggier. 🐸",
            Background = "#0B1410", Panel = "#15211A", Border = "#294A34",
            Text = "#D8F3CF", Dim = "#7FA383",
            Accent = "#63D94B", Gold = "#FFCB47", Sky = "#4FD6C2",
            Green = "#63D94B", Red = "#FF6F5B", DiffAdd = "#A7E86A",
        },
        new UiTheme
        {
            Name = "Sunset Ember",
            Description = "Warm charcoal with amber and orange heat.",
            Background = "#1B120D", Panel = "#281A12", Border = "#4A3222",
            Text = "#F5E9DE", Dim = "#AA8F7C",
            Accent = "#FF8C42", Gold = "#FFC850", Sky = "#5CB3FF",
            Green = "#58C97C", Red = "#E8524A", DiffAdd = "#FFD08A",
        },
        new UiTheme
        {
            Name = "One Dark Pro",
            Description = "Atom's One Dark — the most-installed VS Code theme.",
            Background = "#282C34", Panel = "#21252B", Border = "#3E4451",
            Text = "#ABB2BF", Dim = "#5C6370",
            Accent = "#61AFEF", Gold = "#E5C07B", Sky = "#56B6C2",
            Green = "#98C379", Red = "#E06C75", DiffAdd = "#98C379",
        },
        new UiTheme
        {
            Name = "Dracula",
            Description = "The famous purple-tinted vampire palette.",
            Background = "#282A36", Panel = "#343746", Border = "#44475A",
            Text = "#F8F8F2", Dim = "#6272A4",
            Accent = "#BD93F9", Gold = "#F1FA8C", Sky = "#8BE9FD",
            Green = "#50FA7B", Red = "#FF5555", DiffAdd = "#50FA7B",
        },
        new UiTheme
        {
            Name = "Monokai",
            Description = "Sublime's legendary pink-and-lime classic.",
            Background = "#272822", Panel = "#3E3D32", Border = "#49483E",
            Text = "#F8F8F2", Dim = "#75715E",
            Accent = "#F92672", Gold = "#E6DB74", Sky = "#66D9EF",
            Green = "#A6E22E", Red = "#FF6159", DiffAdd = "#A6E22E",
        },
        new UiTheme
        {
            Name = "Tokyo Night",
            Description = "Moody indigo night with neon pastels.",
            Background = "#1A1B26", Panel = "#24283B", Border = "#3B4261",
            Text = "#C0CAF5", Dim = "#565F89",
            Accent = "#7AA2F7", Gold = "#E0AF68", Sky = "#7DCFFF",
            Green = "#9ECE6A", Red = "#F7768E", DiffAdd = "#9ECE6A",
        },
        new UiTheme
        {
            Name = "Paper Light",
            Description = "A clean light theme with royal purple accents.",
            IsLight = true,
            Background = "#F6F4FA", Panel = "#FFFFFF", Border = "#DCD4EA",
            Text = "#241C33", Dim = "#6E6584",
            Accent = "#7C2FE0", Gold = "#A87400", Sky = "#0069C2",
            Green = "#1D8A3C", Red = "#C43333", DiffAdd = "#0069C2",
        },
        new UiTheme
        {
            Name = "Solarized Light",
            Description = "The warm, low-contrast cream classic.",
            IsLight = true,
            Background = "#FDF6E3", Panel = "#EEE8D5", Border = "#D9CFB0",
            Text = "#586E75", Dim = "#93A1A1",
            Accent = "#268BD2", Gold = "#B58900", Sky = "#2AA198",
            Green = "#859900", Red = "#DC322F", DiffAdd = "#859900",
        },
        new UiTheme
        {
            Name = "GitHub Light",
            Description = "Bright and familiar — straight from github.com.",
            IsLight = true,
            Background = "#FFFFFF", Panel = "#F6F8FA", Border = "#D0D7DE",
            Text = "#1F2328", Dim = "#656D76",
            Accent = "#0969DA", Gold = "#9A6700", Sky = "#0550AE",
            Green = "#1A7F37", Red = "#CF222E", DiffAdd = "#1A7F37",
        },
        new UiTheme
        {
            Name = "One Light",
            Description = "Atom's gentle gray-on-white counterpart to One Dark.",
            IsLight = true,
            Background = "#FAFAFA", Panel = "#EAEAEB", Border = "#DBDBDC",
            Text = "#383A42", Dim = "#A0A1A7",
            Accent = "#4078F2", Gold = "#986801", Sky = "#0184BC",
            Green = "#50A14F", Red = "#E45649", DiffAdd = "#50A14F",
        },
    };
}

/// <summary>
/// Applies a UiTheme across the whole app: the shared Mando* brushes (mutated in
/// place, so every StaticResource reference recolors live), the system accent family
/// (accent buttons, toggles, sliders), the root light/dark element theme, and — via
/// a script the window injects — the transcript's CSS variables.
/// </summary>
public static class ThemeManager
{
    public static UiTheme Current { get; private set; } = UiTheme.All[0];

    /// <summary>Whole-window opacity, 0.3–1.0. 1.0 = solid (default). The window applies
    /// it via the Win32 layered-window alpha; this just owns the value and persistence.</summary>
    public static double WindowOpacity { get; private set; } = 1.0;

    /// <summary>Raised after a theme is applied so the window can retheme the WebView.</summary>
    public static event Action? ThemeChanged;

    // The theme choice lives in a desktop-local file, NOT the shared MandoCode config —
    // the pinned CLI doesn't know this key and its config loader must never see it.
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "ui-settings.json");

    /// <summary>Loads the saved theme (or the default) and applies it. Call once from
    /// the window constructor, before first render.</summary>
    public static void Initialize(FrameworkElement root)
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var saved = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath));
                Current = UiTheme.All.FirstOrDefault(t => t.Name == saved?.Theme) ?? Current;
                if (saved?.Opacity is > 0) WindowOpacity = Math.Clamp(saved.Opacity, 0.3, 1.0);
            }
        }
        catch { /* unreadable settings file — fall back to the defaults */ }
        ApplyCore(Current, root);
        Save();   // first run (or corrupt file): materialize the defaults on disk
    }

    public static void Apply(UiTheme theme, FrameworkElement root)
    {
        Current = theme;
        ApplyCore(theme, root);
        Save();
        ThemeChanged?.Invoke();
    }

    public static void SetWindowOpacity(double value)
    {
        WindowOpacity = Math.Clamp(value, 0.3, 1.0);
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                new UiSettings { Theme = Current.Name, Opacity = WindowOpacity }));
        }
        catch { /* persistence is best-effort; the setting is still applied */ }
    }

    private static void ApplyCore(UiTheme t, FrameworkElement root)
    {
        var res = Application.Current.Resources;

        SetBrush(res, "MandoAccentBrush", t.Accent);
        SetBrush(res, "MandoGoldBrush", t.Gold);
        SetBrush(res, "MandoSkyBrush", t.Sky);
        SetBrush(res, "MandoGreenBrush", t.Green);
        SetBrush(res, "MandoRedBrush", t.Red);
        SetBrush(res, "MandoBackgroundBrush", t.Background);
        SetBrush(res, "MandoPanelBrush", t.Panel);
        SetBrush(res, "MandoBorderBrush", t.Border);
        SetBrush(res, "MandoTextBrush", t.Text);
        SetBrush(res, "MandoDimBrush", t.Dim);

        // Accent family: Light2 feeds accent fills in dark themes, Dark1 in light
        // themes — both pinned to the exact brand accent so fills never drift.
        var accent = C(t.Accent);
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorLight1"] = Mix(accent, white: true, 0.15);
        res["SystemAccentColorLight2"] = accent;
        res["SystemAccentColorLight3"] = Mix(accent, white: true, 0.45);
        res["SystemAccentColorDark1"] = accent;
        res["SystemAccentColorDark2"] = Mix(accent, white: false, 0.30);
        res["SystemAccentColorDark3"] = Mix(accent, white: false, 0.45);
        res["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
        res["AccentFillColorSecondaryBrush"] = new SolidColorBrush(WithAlpha(accent, 0xE6));
        res["AccentFillColorTertiaryBrush"] = new SolidColorBrush(WithAlpha(accent, 0xCC));
        var accentText = Mix(accent, white: !t.IsLight, 0.20);
        res["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(accentText);
        res["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(accentText);
        res["AccentTextFillColorTertiaryBrush"] = new SolidColorBrush(accent);

        // Flip through the opposite theme so every {ThemeResource} in the tree
        // re-resolves and picks up the overrides above, even dark→dark.
        var target = t.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = target == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = target;
    }

    /// <summary>JS that re-points the transcript's CSS variables at this theme —
    /// keeps existing chat content intact while recoloring it live.</summary>
    public static string BuildTranscriptScript(UiTheme t) =>
        "(function(){var s=document.documentElement.style;" +
        $"s.setProperty('--bg','{t.Background}');" +
        $"s.setProperty('--fg','{t.Text}');" +
        $"s.setProperty('--dim','{t.Dim}');" +
        $"s.setProperty('--accent','{t.Accent}');" +
        $"s.setProperty('--gold','{t.Gold}');" +
        $"s.setProperty('--sky','{t.Sky}');" +
        $"s.setProperty('--green','{t.Green}');" +
        $"s.setProperty('--red','{t.Red}');" +
        $"s.setProperty('--panel','{t.Panel}');" +
        $"s.setProperty('--border','{t.Border}');" +
        $"s.setProperty('--diffadd','{t.DiffAdd}');" +
        "})();";

    private static void SetBrush(ResourceDictionary res, string key, string hex) =>
        ((SolidColorBrush)res[key]).Color = C(hex);

    public static Color C(string hex) => Color.FromArgb(
        255,
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    private static Color Mix(Color c, bool white, double amount)
    {
        byte target = white ? (byte)255 : (byte)0;
        byte Ch(byte v) => (byte)(v + (target - v) * amount);
        return Color.FromArgb(255, Ch(c.R), Ch(c.G), Ch(c.B));
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private sealed class UiSettings
    {
        public string? Theme { get; set; }
        public double Opacity { get; set; } = 1.0;
    }
}
