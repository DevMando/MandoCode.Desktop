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

    /// <summary>When true, the transcript suppresses all motion — no fade-in on new
    /// blocks, no hover transitions, no smooth scroll — for the still, instant repaint
    /// of an e-reader page. Gated in the WebView via an html[data-flat] attribute.</summary>
    public bool FlatMotion { get; init; }

    /// <summary>When true, the transcript wears a CRT-tube overlay (scanlines, aperture
    /// grille, Trinitron damper wires, vignette, phosphor bloom) — all STATIC gradients,
    /// no animation. Gated in the WebView via an html[data-crt] attribute.</summary>
    public bool Crt { get; init; }

    /// <summary>When true, the transcript wears Windows-98 chrome: square corners, two-tone
    /// 3D bevels lit from the top-left, navy title-bar gradients on panels, Tahoma, classic
    /// chunky scrollbars. Gated in the WebView via an html[data-win98] attribute.</summary>
    public bool Win98 { get; init; }

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
            // Cathode Ray — a Trinitron-style aperture-grille CRT: a deep, near-black picture
            // tube with vivid, saturated phosphors that glow. Crt=true drapes the transcript in
            // the tube overlay (scanlines + aperture grille + the two damper wires + vignette +
            // bloom), all static gradients. The palette is punchy on purpose — CRT phosphors are
            // high-saturation and the scanlines darken everything, so colors need headroom.
            // Positioned as the last of the dark themes.
            Name = "Cathode Ray (CRT)",
            Description = "Deep-black picture tube with glowing phosphors and cathode-ray scanlines. 📺",
            Crt = true,
            Background = "#0B0B0D", Panel = "#131318", Border = "#2A2A33",
            Text = "#E9EEEC", Dim = "#7C8A86",
            Accent = "#33CCFF", Gold = "#FFC747", Sky = "#6FD3FF",
            Green = "#43E37A", Red = "#FF5B54", DiffAdd = "#43E37A",
        },
        new UiTheme
        {
            // Warm paper + black ink, fully grayscale — reads like a Kindle page. Positioned as
            // the first of the light themes. Every accent collapses to a shade of warm ink (the
            // desaturation is what sells "e-ink," more than the cream background does), and
            // FlatMotion strips the transcript's animation so pages repaint still and instant
            // like an e-reader. Diffs read as a paper-edit metaphor: changed lines are dark ink
            // over faded context; the +/- glyphs (not hue) carry add-vs-remove.
            Name = "E-Ink Paper",
            Description = "Warm paper, black ink, grayscale, no motion — reads like a Kindle. 📖",
            IsLight = true,
            FlatMotion = true,
            Background = "#E9E5DB", Panel = "#DED9CC", Border = "#C6BFAE",
            Text = "#211C16", Dim = "#726B5B",
            Accent = "#3B342A", Gold = "#6A5E48", Sky = "#4C4636",
            Green = "#5A5240", Red = "#2E251E", DiffAdd = "#3B342A",
        },
        new UiTheme
        {
            // The whole palette is era-authentic: 3D-face silver surfaces, white sunken
            // content wells, the 16-color navy/olive/teal-adjacent accents (hyperlink blue
            // for links), black text. FlatMotion is period-correct — nothing animated in
            // 1998. The real costume is the data-win98 CSS in TranscriptHtmlBuilder:
            // square corners, two-tone bevels, and navy title-bar gradients on every panel.
            Name = "W98 - Y2K",
            Description = "Silver bevels, navy title bars, teal desktop. Party like it's 1998. 🖥️",
            IsLight = true,
            FlatMotion = true,
            Win98 = true,
            Background = "#C0C0C0", Panel = "#FFFFFF", Border = "#808080",
            Text = "#000000", Dim = "#5A5A5A",
            Accent = "#000080", Gold = "#806000", Sky = "#0000CC",
            Green = "#008000", Red = "#B00000", DiffAdd = "#008000",
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

    /// <summary>Full path of the chat background image, or null when none is set. The picked
    /// file is COPIED into <see cref="UserDataFolder"/> (as chat-bg.&lt;ext&gt;) so the setting
    /// survives the original moving; transcripts load it via the mandocode.userdata host.</summary>
    public static string? ChatBackgroundFile { get; private set; }

    /// <summary>Opacity of the chat background image layer only (0.05–1.0). Text never
    /// fades — the slider dims the picture, not the conversation.</summary>
    public static double ChatBackgroundOpacity { get; private set; } = 0.30;

    /// <summary>Boxed messages: each prompt/response renders on its own frosted card in the
    /// transcript (hard boundaries, easier long-session scanning) instead of the flat
    /// terminal look. ON by default — cards are the universal chat idiom and the better
    /// first impression; the flat-density crowd knows where settings live. Theme-agnostic —
    /// the CSS uses only theme variables. W98 ignores this: its message windows are bespoke.</summary>
    public static bool BoxedMessages { get; private set; } = true;

    public static void SetBoxedMessages(bool on)
    {
        BoxedMessages = on;
        Save();
        // Caller re-applies to tabs (same contract as SetChatBackgroundOpacity).
    }

    /// <summary>Raised after a theme is applied so the window can retheme the WebView.</summary>
    public static event Action? ThemeChanged;

    // The theme choice lives in a desktop-local file, NOT the shared MandoCode config —
    // the pinned CLI doesn't know this key and its config loader must never see it.
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "ui-settings.json");

    /// <summary>Folder each tab's WebView2 serves as https://mandocode.userdata/ — holds the
    /// copied chat background image (and the settings file itself, which is never requested).</summary>
    public static string UserDataFolder => Path.GetDirectoryName(SettingsPath)!;

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
                // Null = setting predates the feature (or fresh file): take the current default.
                BoxedMessages = saved?.Boxed ?? true;
                if (saved?.ChatBgOpacity is > 0) ChatBackgroundOpacity = Math.Clamp(saved.ChatBgOpacity, 0.05, 1.0);
                if (!string.IsNullOrEmpty(saved?.ChatBackground))
                {
                    var bg = Path.Combine(UserDataFolder, saved.ChatBackground);
                    if (File.Exists(bg)) ChatBackgroundFile = bg;
                }
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

    /// <summary>Copies the picked image into <see cref="UserDataFolder"/> and remembers it,
    /// or clears the background when <paramref name="sourcePath"/> is null. The caller
    /// re-scripts open transcripts (MainWindow.ApplyThemeToAllTabs).</summary>
    public static void SetChatBackground(string? sourcePath)
    {
        try
        {
            if (Directory.Exists(UserDataFolder))
                foreach (var old in Directory.GetFiles(UserDataFolder, "chat-bg.*"))
                    File.Delete(old);
        }
        catch { /* an open WebView may briefly hold the old file — stale copies are harmless */ }

        ChatBackgroundFile = null;
        if (sourcePath != null)
        {
            try
            {
                Directory.CreateDirectory(UserDataFolder);
                var dest = Path.Combine(UserDataFolder,
                    "chat-bg" + Path.GetExtension(sourcePath).ToLowerInvariant());
                File.Copy(sourcePath, dest, overwrite: true);
                ChatBackgroundFile = dest;
            }
            catch { /* unreadable source — behave as if cleared */ }
        }
        Save();
    }

    public static void SetChatBackgroundOpacity(double value)
    {
        ChatBackgroundOpacity = Math.Clamp(value, 0.05, 1.0);
        Save();
    }

    /// <summary>CSS value for the transcript's --chat-bg-image variable: a cache-busted
    /// virtual-host URL, or 'none' when no background is set.</summary>
    public static string ChatBackgroundCssValue()
    {
        if (ChatBackgroundFile == null || !File.Exists(ChatBackgroundFile)) return "none";
        var v = File.GetLastWriteTimeUtc(ChatBackgroundFile).Ticks;
        return $"url(\"https://mandocode.userdata/{Path.GetFileName(ChatBackgroundFile)}?v={v}\")";
    }

    /// <summary>Invariant-culture string for --chat-bg-opacity (a comma decimal would be
    /// silently invalid CSS on some locales).</summary>
    public static string ChatBackgroundOpacityCss() =>
        ChatBackgroundOpacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                new UiSettings
                {
                    Theme = Current.Name,
                    Opacity = WindowOpacity,
                    ChatBackground = ChatBackgroundFile == null ? null : Path.GetFileName(ChatBackgroundFile),
                    ChatBgOpacity = ChatBackgroundOpacity,
                    Boxed = BoxedMessages,
                }));
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
        $"s.setProperty('--chat-bg-image','{ChatBackgroundCssValue()}');" +
        $"s.setProperty('--chat-bg-opacity','{ChatBackgroundOpacityCss()}');" +
        (t.FlatMotion
            ? "document.documentElement.setAttribute('data-flat','1');"
            : "document.documentElement.removeAttribute('data-flat');") +
        (t.Crt
            ? "document.documentElement.setAttribute('data-crt','1');"
            : "document.documentElement.removeAttribute('data-crt');") +
        (t.Win98
            ? "document.documentElement.setAttribute('data-win98','1');"
            : "document.documentElement.removeAttribute('data-win98');") +
        (BoxedMessages
            ? "document.documentElement.setAttribute('data-cards','1');"
            : "document.documentElement.removeAttribute('data-cards');") +
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
        public string? ChatBackground { get; set; }      // file name inside UserDataFolder
        public double ChatBgOpacity { get; set; } = 0.30;
        /// <summary>Nullable on purpose: absent (pre-feature settings file) means "use the
        /// current default", so changing the default never fights a user's explicit choice.</summary>
        public bool? Boxed { get; set; }
    }
}
