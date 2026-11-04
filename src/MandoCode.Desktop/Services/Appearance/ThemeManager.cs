using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MandoCode.Desktop.Services;

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

    /// <summary>File name of the shipped background currently in use (see
    /// <see cref="BuiltInBackgrounds"/>), or null when the background is the user's own file or
    /// none. Identity only — the image itself is copied like any picked file, and that copy is
    /// RENAMED to chat-bg.&lt;ext&gt;, so without this the gallery couldn't mark which tile is
    /// active after a restart.</summary>
    public static string? ChatBackgroundBuiltIn { get; private set; }

    /// <summary>Opacity of the chat background image layer only (0.05–1.0). Text never
    /// fades — the slider dims the picture, not the conversation.</summary>
    public static double ChatBackgroundOpacity { get; private set; } = 0.30;

    /// <summary>Boxed messages: each prompt/response renders on its own frosted card in the
    /// transcript (hard boundaries, easier long-session scanning) instead of the flat
    /// terminal look. ON by default — cards are the universal chat idiom and the better
    /// first impression; the flat-density crowd knows where settings live. Theme-agnostic —
    /// the CSS uses only theme variables. W98 ignores this: its message windows are bespoke.</summary>
    public static bool BoxedMessages { get; private set; } = true;

    /// <summary>
    /// When on, a chat background image is processed to match themes that simulate a display or a
    /// printing process — quantised to a passive-matrix panel's four shades, laid down as riso
    /// halftone, and so on. ON by default: these themes are chosen for the illusion, and a
    /// full-colour photograph behind one is the thing that breaks it. Someone who wants the picture
    /// untouched turns it off, which is a discoverable one-click reversal; someone who would have
    /// loved the effect would never have gone looking for a switch to enable it.
    ///
    /// <para>Only affects the themes that simulate a medium, and only when a background image is
    /// set, so for most users and most themes this setting does nothing at all.</para>
    ///
    /// <para>E-Ink is not governed by this — its picture is grayscale-dithered unconditionally,
    /// because there the halftone is not an effect applied to the theme, it IS the theme.</para>
    /// </summary>
    public static bool MediaBackground { get; private set; } = true;

    public static void SetMediaBackground(bool on)
    {
        MediaBackground = on;
        Save();
    }

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
        // A MISSING settings file is the fresh-install signal, deliberately narrower than "we ended
        // up on the defaults": a file that exists but won't parse belongs to someone who has used
        // the app, and handing them a background they never chose would read as the corruption
        // doing something rather than the app recovering from it.
        var freshInstall = !File.Exists(SettingsPath);

        try
        {
            if (!freshInstall)
            {
                var saved = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath));
                Current = UiTheme.All.FirstOrDefault(t => t.Name == saved?.Theme) ?? Current;
                if (saved?.Opacity is > 0) WindowOpacity = Math.Clamp(saved.Opacity, 0.3, 1.0);
                // Null = setting predates the feature (or fresh file): take the current default.
                BoxedMessages = saved?.Boxed ?? true;
                MediaBackground = saved?.MediaBg ?? true;
                if (saved?.ChatBgOpacity is > 0) ChatBackgroundOpacity = Math.Clamp(saved.ChatBgOpacity, 0.05, 1.0);
                if (!string.IsNullOrEmpty(saved?.ChatBackground))
                {
                    var bg = Path.Combine(UserDataFolder, saved.ChatBackground);
                    if (File.Exists(bg)) ChatBackgroundFile = bg;
                    // Only meaningful while that copy is still on disk — otherwise the gallery would
                    // mark a tile as active with no background actually showing.
                    if (ChatBackgroundFile != null) ChatBackgroundBuiltIn = saved.ChatBgBuiltIn;
                }
            }
        }
        catch { /* unreadable settings file — fall back to the defaults */ }

        if (freshInstall) ApplyFirstRunBackground();

        ApplyCore(Current, root);
        Save();   // first run (or corrupt file): materialize the defaults on disk
    }

    /// <summary>
    /// Opens a brand-new install on the first bundled background instead of a bare theme, so the app
    /// has a look out of the box and the gallery isn't a feature only the curious ever find.
    /// <see cref="ChatBackgroundOpacity"/> is left at its 30% default — the same value a
    /// user-picked image gets.
    ///
    /// "First" means first in gallery order, so the <c>01-</c> prefix picks the first-run image as
    /// well as the tile order: a future release changes the default by renumbering, not by editing
    /// this. A build that ships no backgrounds is a no-op and opens on the flat theme, exactly as
    /// before.
    ///
    /// Runs on the first launch ONLY. Any later change — including removing the background — writes
    /// a settings file, and this never runs again, so a user's "no background" choice sticks.
    /// </summary>
    private static void ApplyFirstRunBackground()
    {
        if (BuiltInBackgrounds.All.FirstOrDefault() is not { } first) return;
        SetChatBackground(first.FullPath, first.FileName);
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
    /// re-scripts open transcripts (MainWindow.ApplyThemeToAllTabs).
    ///
    /// <paramref name="builtInName"/> is the shipped image's file name when the source came from
    /// the gallery, and null for a file the user picked — the copy is renamed on the way in, so
    /// this is the only record of which tile is active. Copying rather than referencing the install
    /// folder is deliberate: the background survives the app being reinstalled or updated
    /// underneath it, and every downstream consumer keeps working off one path.</summary>
    public static void SetChatBackground(string? sourcePath, string? builtInName = null)
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
                // File.Copy keeps the SOURCE's timestamp, and the shipped images all share one
                // (git writes them in a single checkout) — so the ?v cache-buster wouldn't change
                // between gallery picks and the WebView would keep serving the old image. Stamp
                // the copy time to make every pick a fresh URL.
                File.SetLastWriteTimeUtc(dest, DateTime.UtcNow);
                ChatBackgroundFile = dest;
            }
            catch { /* unreadable source — behave as if cleared */ }
        }

        // Tied to the copy actually landing: a source that failed to copy leaves no background, so
        // claiming a gallery tile is active would mark a tile that isn't showing anything.
        ChatBackgroundBuiltIn = ChatBackgroundFile == null ? null : builtInName;
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
                    ChatBgBuiltIn = ChatBackgroundBuiltIn,
                    ChatBgOpacity = ChatBackgroundOpacity,
                    Boxed = BoxedMessages,
                    MediaBg = MediaBackground,
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
        SetBrush(res, "MandoDimBrush", t.ReadableDim);
        SetBrush(res, "MandoRaisedBrush", Raised(t));
        // Panel with alpha, for native surfaces painted over the chat background image.
        // 0xE6 rather than the transcript's 82%, because XAML has no backdrop blur to
        // soften what shows through — the extra opacity does that job instead.
        SetBrush(res, "MandoGlassBrush", WithAlpha(C(t.Panel), 0xE6));

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
        $"s.setProperty('--dim','{t.ReadableDim}');" +
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
        (t.Monochrome
            ? "document.documentElement.setAttribute('data-mono','1');"
            : "document.documentElement.removeAttribute('data-mono');") +
        (t.Fanfold
            ? "document.documentElement.setAttribute('data-fanfold','1');"
            : "document.documentElement.removeAttribute('data-fanfold');") +
        (t.Lcd
            ? "document.documentElement.setAttribute('data-lcd','1');"
            : "document.documentElement.removeAttribute('data-lcd');") +
        (t.Riso
            ? "document.documentElement.setAttribute('data-riso','1');"
            : "document.documentElement.removeAttribute('data-riso');") +
        (t.Vfd
            ? "document.documentElement.setAttribute('data-vfd','1');"
            : "document.documentElement.removeAttribute('data-vfd');") +
        (t.SplitFlap
            ? "document.documentElement.setAttribute('data-splitflap','1');"
            : "document.documentElement.removeAttribute('data-splitflap');") +
        (t.Fiche
            ? "document.documentElement.setAttribute('data-fiche','1');"
            : "document.documentElement.removeAttribute('data-fiche');") +
        (t.Cyanotype
            ? "document.documentElement.setAttribute('data-cyano','1');"
            : "document.documentElement.removeAttribute('data-cyano');") +
        (t.Vector
            ? "document.documentElement.setAttribute('data-vector','1');"
            : "document.documentElement.removeAttribute('data-vector');") +
        (BoxedMessages
            ? "document.documentElement.setAttribute('data-cards','1');"
            : "document.documentElement.removeAttribute('data-cards');") +
        (MediaBackground
            ? "document.documentElement.setAttribute('data-mediabg','1');"
            : "document.documentElement.removeAttribute('data-mediabg');") +
        "})();";

    /// <summary>Accent tint used for the raised surface. The strong value is what nearly every theme
    /// gets; the gentle one is the floor a low-contrast theme backs off to (see
    /// <see cref="Raised"/>).</summary>
    private const double RaisedTintStrong = 0.18;
    private const double RaisedTintGentle = 0.08;

    /// <summary>Text-on-card contrast the tint must preserve where the theme allows it — the WCAG AA
    /// floor for body text.</summary>
    private const double RaisedTextContrastFloor = 4.5;

    /// <summary>Panel-vs-Background separation at which a theme's Panel already reads as a raised
    /// surface on its own, and tinting it would do more harm than good.</summary>
    private const double RaisedAlreadySeparated = 1.5;

    /// <summary>
    /// The raised-surface color for a theme: <see cref="UiTheme.Panel"/> blended toward that theme's
    /// own <see cref="UiTheme.Accent"/>. Used by transient cards that float OVER the transcript (the
    /// snapshot offer), which need to read as ON TOP OF the conversation rather than part of it —
    /// and Panel can't do that job, because Panel and Background are only a few points apart in most
    /// themes (Visual Studio Dark is #252526 on #1E1E1E).
    ///
    /// Derived rather than a hand-authored hex per theme for two reasons. It's one less value to keep
    /// in sync every time a theme is added or retuned; and blending toward the theme's OWN accent
    /// means each theme gets a shade that belongs to it instead of a generic gray — the tint stays
    /// grayscale in E-Ink Paper (whose accent is desaturated ink), goes navy in W98, and goes
    /// phosphor green in Phosphor Fwog, all for free.
    ///
    /// Deliberately NOT a lift toward white/black: W98's Panel is pure white on a silver Background,
    /// so darkening it would push the card TOWARD the background it needs to separate from.
    ///
    /// The tint backs off when a full-strength one would hurt legibility. Solarized Light is the case
    /// that needs it: its Text/Panel pair is famously low-contrast and already sits below AA, so the
    /// strong tint would take a marginal theme to genuinely hard to read. It lands on the gentle tint
    /// instead, which still separates from its background about as well as W98 does at full strength.
    /// </summary>
    private static Color Raised(UiTheme t)
    {
        var panel = C(t.Panel);
        var accent = C(t.Accent);
        var text = C(t.Text);

        // Some themes already put real distance between Panel and Background, and tinting those
        // makes things worse rather than better. W98 is the case: its white content wells sit
        // 1.82:1 off the silver desktop — already the most separated surface of any theme — and
        // tinting turned a period-correct white dialog into a pale lavender one belonging to no
        // era, while REDUCING the separation to 1.21:1. Every other theme is at most 1.36:1, so
        // this only ever exempts a theme whose own palette has done the job.
        if (Contrast(panel, C(t.Background)) >= RaisedAlreadySeparated) return panel;

        for (var tint = RaisedTintStrong; tint > RaisedTintGentle; tint -= 0.02)
        {
            var candidate = MixToward(panel, accent, tint);
            if (Contrast(text, candidate) >= RaisedTextContrastFloor) return candidate;
        }

        // No tint in range clears the floor (the theme starts below it) — take the gentlest, which
        // costs the least contrast while still being a distinct surface.
        return MixToward(panel, accent, RaisedTintGentle);
    }

    /// <summary>WCAG contrast ratio between two colors. Thin wrapper over <see cref="ColorMath"/>,
    /// which owns the arithmetic so the palette can be checked without a UI type in the way.</summary>
    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (ColorMath.Luminance(a.R, a.G, a.B), ColorMath.Luminance(b.R, b.G, b.B));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex) =>
        ((SolidColorBrush)res[key]).Color = C(hex);

    private static void SetBrush(ResourceDictionary res, string key, Color color) =>
        ((SolidColorBrush)res[key]).Color = color;

    /// <summary>Blends <paramref name="from"/> toward <paramref name="to"/> by
    /// <paramref name="amount"/> (0–1). The result always lands between the two channels, so the
    /// byte casts can't overflow.</summary>
    private static Color MixToward(Color from, Color to, double amount)
    {
        byte Ch(byte a, byte b) => (byte)(a + (b - a) * amount);
        return Color.FromArgb(255, Ch(from.R, to.R), Ch(from.G, to.G), Ch(from.B, to.B));
    }

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
        /// <summary>Shipped image this came from, or null for the user's own file. Absent in a
        /// settings file written before the gallery existed, which reads as "your own file" — the
        /// safe answer, since it only means no tile is marked.</summary>
        public string? ChatBgBuiltIn { get; set; }
        public double ChatBgOpacity { get; set; } = 0.30;
        /// <summary>Nullable on purpose: absent (pre-feature settings file) means "use the
        /// current default", so changing the default never fights a user's explicit choice.</summary>
        public bool? Boxed { get; set; }
        /// <summary>Nullable for the same reason as <see cref="Boxed"/>: absent means the current
        /// default rather than a stored "off", so an existing settings file written before this
        /// setting existed picks the default up instead of being pinned to the old behaviour.</summary>
        public bool? MediaBg { get; set; }
    }
}
