namespace MandoCode.Desktop.Services;

/// <summary>
/// WCAG contrast arithmetic over #RRGGBB strings. Deliberately free of any UI color type so the
/// theme palette can be checked without a UI thread — the shipped themes are asserted against the
/// readability floor in tests, which is what stops a new theme landing below it.
/// </summary>
public static class ColorMath
{
    /// <summary>WCAG AA for normal-size text. Secondary/"dim" text is still text.</summary>
    public const double AaNormalText = 4.5;

    /// <summary>
    /// The floor for text read THROUGH a darkening overlay — currently the CRT tube's scanlines and
    /// vignette. Raised by a third rather than computed from the overlay's exact alpha: the
    /// attenuation varies by scanline row and by distance from the tube's centre, so a single
    /// precise number would be false precision. This is a margin, and it is honest about being one.
    /// </summary>
    public const double AaNormalTextThroughGlass = 6.0;

    public static (int R, int G, int B) Parse(string hex)
    {
        var h = hex.TrimStart('#');
        return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
    }

    public static string ToHex(int r, int g, int b) => $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>WCAG relative luminance of an sRGB color.</summary>
    public static double Luminance(int r, int g, int b)
    {
        static double Ch(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(r) + 0.7152 * Ch(g) + 0.0722 * Ch(b);
    }

    /// <summary>WCAG contrast ratio, 1.0 (identical) to 21.0 (black on white).</summary>
    public static double Contrast(string aHex, string bHex)
    {
        var (ar, ag, ab) = Parse(aHex);
        var (br, bg, bb) = Parse(bHex);
        var (la, lb) = (Luminance(ar, ag, ab), Luminance(br, bg, bb));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Blends <paramref name="fromHex"/> toward <paramref name="toHex"/> by 0–1.</summary>
    public static string MixToward(string fromHex, string toHex, double amount)
    {
        var (fr, fg, fb) = Parse(fromHex);
        var (tr, tg, tb) = Parse(toHex);
        int Ch(int a, int b) => (int)Math.Round(a + (b - a) * amount);
        return ToHex(Ch(fr, tr), Ch(fg, tg), Ch(fb, tb));
    }

    /// <summary>
    /// Lifts <paramref name="colorHex"/> until it clears <paramref name="minContrast"/> against
    /// <paramref name="backgroundHex"/>, blending toward <paramref name="towardHex"/> — normally the
    /// theme's own body text, so the result stays that theme's colour rather than drifting to grey.
    ///
    /// <para>Returns the color untouched when it already clears, so a theme whose palette is already
    /// readable is never repainted. The walk is in small steps and takes the FIRST value that
    /// clears, which is the least change that fixes the problem — a palette should be nudged into
    /// legibility, not flattened into it.</para>
    ///
    /// <para>Terminates because <paramref name="towardHex"/> is body text, which every theme already
    /// keeps readable against its own background; the loop's final candidate is that color exactly.</para>
    /// </summary>
    public static string Readable(string colorHex, string backgroundHex, string towardHex, double minContrast)
    {
        if (Contrast(colorHex, backgroundHex) >= minContrast) return colorHex;

        for (var amount = 0.05; amount < 1.0; amount += 0.05)
        {
            var candidate = MixToward(colorHex, towardHex, amount);
            if (Contrast(candidate, backgroundHex) >= minContrast) return candidate;
        }
        return towardHex;
    }
}
