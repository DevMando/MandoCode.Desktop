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

    /// <summary>
    /// When true, the transcript has ONE hue available and syntax highlighting must carry meaning
    /// through brightness and weight instead of colour — the discipline a single-phosphor terminal
    /// forces on you. Pairs with <see cref="Crt"/> rather than replacing it: an amber tube is still
    /// a tube, so it keeps the scanlines and the bloom. Gated via html[data-mono].
    /// </summary>
    public bool Monochrome { get; init; }

    /// <summary>
    /// When true, the transcript is continuous-feed printer paper: alternating pale bands locked to
    /// the text's own line grid, and sprocket-hole margins down both edges. Gated via
    /// html[data-fanfold].
    /// </summary>
    public bool Fanfold { get; init; }

    /// <summary>
    /// When true, the transcript is a passive-matrix LCD: a visible pixel grid and a smeared ghost
    /// instead of a glow. The deliberate opposite of <see cref="Crt"/> — liquid crystal does not
    /// emit light, it twists it, so nothing blooms and nothing is ever truly black. Gated via
    /// html[data-lcd].
    /// </summary>
    public bool Lcd { get; init; }

    /// <summary>
    /// When true, the transcript is a two-ink risograph print: toothy paper, mottled ink coverage,
    /// and a one-pixel misregistration between the two drums. Gated via html[data-riso].
    /// </summary>
    public bool Riso { get; init; }

    /// <summary>
    /// When true, the transcript is a vacuum-fluorescent display — the cyan-green of a VCR clock.
    /// Emissive like a tube but with none of a tube's geometry: no scanlines, no grille, no
    /// curvature. A segment either lights or it doesn't, so the glow has a hard edge rather than a
    /// swept beam's halo. Gated via html[data-vfd].
    /// </summary>
    public bool Vfd { get; init; }

    /// <summary>
    /// When true, the transcript is a Solari split-flap board. The only MECHANICAL surface here:
    /// each character is a printed flap, so text is forced to uppercase and monospace — a flap
    /// carries one glyph and the machine has no others. Gated via html[data-splitflap].
    /// </summary>
    public bool SplitFlap { get; init; }

    /// <summary>
    /// When true, the transcript is film in a microfiche reader: cool, soft-focus, heavily
    /// vignetted by the lens. The tell is that dust and scratches sit on the READER'S GLASS, not on
    /// the film — so they stay put while the page scrolls beneath them. Gated via html[data-fiche].
    /// </summary>
    public bool Fiche { get; init; }

    /// <summary>
    /// When true, the transcript is a cyanotype. The one NEGATIVE surface: glyphs are where the
    /// light did not reach, so text is unexposed paper on Prussian blue and "bolder" means less
    /// exposure, not more ink. Gated via html[data-cyano].
    /// </summary>
    public bool Cyanotype { get; init; }

    /// <summary>
    /// When true, the transcript is drawn by an electrostatically deflected beam on a long-
    /// persistence scope: no raster at all, so no scanlines — just a bright trace and the fading
    /// ghost of where the beam has already been. Gated via html[data-vector].
    /// </summary>
    public bool Vector { get; init; }

    /// <summary>
    /// <see cref="Dim"/>, raised if necessary until it is actually readable on this theme's own
    /// background. Every consumer should use this rather than <see cref="Dim"/> directly.
    ///
    /// <para>Why: secondary text — timestamps, file paths, status lines, the hint under a heading —
    /// is still text, and half the shipped palettes put Dim below the WCAG AA floor of 4.5:1. Most
    /// of those are faithful to their upstream schemes (Dracula and One Dark ship famously faint
    /// comment colours), so the palettes are kept as authored and corrected here instead. A theme
    /// already above the floor is returned untouched.</para>
    ///
    /// <para>Blended toward this theme's own <see cref="Text"/>, so a lifted Dim stays recognisably
    /// that theme's colour instead of drifting toward grey.</para>
    /// </summary>
    public string ReadableDim => ColorMath.Readable(Dim, Background, Text, DimContrastFloor);

    /// <summary>
    /// The contrast this theme's secondary text has to clear. Normally the WCAG AA floor, but
    /// <see cref="Crt"/> themes are held higher.
    ///
    /// <para>Why: every other contrast figure in this app compares a colour against the colour
    /// BEHIND it. The CRT overlay is painted in FRONT — a 22%-black scanline covering half the rows,
    /// plus an edge vignette — so a CRT palette's measured contrast is not the contrast a reader
    /// actually gets. Body text has enormous headroom there and never notices; dim text is the
    /// thinnest thing on the screen and does. The overlay's attenuation is fixed and known, so the
    /// floor can simply account for it rather than pretending the glass isn't there.</para>
    /// </summary>
    private double DimContrastFloor => Crt ? ColorMath.AaNormalTextThroughGlass : ColorMath.AaNormalText;

    public static readonly IReadOnlyList<UiTheme> All = new[]
    {
        // Ordered dark → light, and within each half: ordinary colour schemes first, then the
        // themes that imitate a physical medium. The media run is sequenced by HOW THE IMAGE IS
        // MADE rather than by how it looks — swept beam, steered beam, lit segment, moving part,
        // chemical process, then the transmissive and printed surfaces. Neighbours in this list
        // are therefore neighbours in mechanism, which is what makes the picker skimmable once
        // there are two dozen entries: Cathode Ray sits beside the other tubes and not beside the
        // other green things.
        //
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
            Name = "Phosphor Fwog",
            Description = "Tree-fwog green on a dark pond — Phosphor's terminal roots, but froggier. 🐸",
            Background = "#0B1410", Panel = "#15211A", Border = "#294A34",
            Text = "#D8F3CF", Dim = "#7FA383",
            Accent = "#63D94B", Gold = "#FFCB47", Sky = "#4FD6C2",
            Green = "#63D94B", Red = "#FF6F5B", DiffAdd = "#A7E86A",
        },
        new UiTheme
        {
            // Cathode Ray — a Trinitron-style aperture-grille CRT: a deep, near-black picture
            // tube with vivid, saturated phosphors that glow. Crt=true drapes the transcript in
            // the tube overlay (scanlines + aperture grille + the two damper wires + vignette +
            // bloom), all static gradients. The palette is punchy on purpose — CRT phosphors are
            // high-saturation and the scanlines darken everything, so colors need headroom.
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
            // Pheteven Phosphor — a single-gun monochrome terminal. Crt=true because an amber tube is
            // still a tube (scanlines, grille, damper wires, bloom all apply); Monochrome=true adds
            // the constraint the hardware imposes: one phosphor, so syntax highlighting carries
            // meaning in brightness and weight instead of hue. That constraint IS the theme —
            // amber over a colour scheme would just be a filter.
            //
            // #FFB000 is the P3 amber those tubes actually used. P3 has a noticeably longer decay
            // than the P1 green next door, which is why the bloom reads wider and warmer here even
            // though both themes share the CRT block. Every "accent" is a different amber rather
            // than a different colour, for the same reason E-Ink collapses its accents to ink.
            // Sits beside Sunset Ember, the warm dark theme it shares a temperature with.
            // The name is a nod, and it hides in plain sight: anyone who doesn't know Pheteven reads
            // it as another Ph- pun in the Phosphor family and moves on. Sits well beside Phosphor
            // Fwog, which set that precedent.
            Name = "Pheteven Phosphor",
            Description = "One-gun amber tube — brightness carries meaning, because hue can't. 🟠",
            Crt = true,
            Monochrome = true,
            Background = "#100A02", Panel = "#1A1206", Border = "#3A2A0C",
            Text = "#FFB000", Dim = "#B07704",
            Accent = "#FFD07A", Gold = "#FFC340", Sky = "#FFCE6B",
            Green = "#FFC340", Red = "#FF8A3D", DiffAdd = "#FFD07A",
        },
        new UiTheme
        {
            // Vector Scope — an electrostatically deflected beam on a long-persistence phosphor.
            // The one emissive theme with NO scanlines, because there is no raster: the beam is
            // steered straight to where the stroke goes. What it leaves is persistence, the fading
            // ghost of where it has already been, which the CSS offsets slightly up and left.
            //
            // Built deliberately without the round aperture and the scintillation noise a real
            // scope has. Both are period-correct and both are hostile to prose — a circular mask
            // wastes a rectangular pane and the noise fights the text. This keeps the phosphor,
            // which is the part that reads well.
            Name = "Vector Scope",
            Description = "Steered beam on long-persistence phosphor — no raster, all trace. 📡",
            Vector = true,
            // Softened from the first pass, which was accurate and unreadable. Neon green on
            // near-pure black, glowing, is genuinely what a scope looks like and genuinely painful
            // to read for more than a minute. The background lifts off absolute black and the
            // phosphor comes down off full saturation — still unmistakably a scope, no longer a
            // light source pointed at you.
            Background = "#060F09", Panel = "#0C1A11", Border = "#1B4227",
            Text = "#79D992", Dim = "#3C7A4F",
            Accent = "#A9E070", Gold = "#CFE07E", Sky = "#79D9BE",
            Green = "#79D992", Red = "#D9907C", DiffAdd = "#A9E070",
        },
        new UiTheme
        {
            // VFD — a vacuum-fluorescent display, the cyan-green of a VCR clock or a car stereo.
            // Fills the gap between the tubes and the panel: emissive like a CRT, but with none of
            // a tube's geometry, because there is no beam being swept. A segment is energised or it
            // is not, so the CSS gives it a hard-edged glow rather than a phosphor halo.
            //
            // The unlit field is nearly black on purpose — a VFD sits behind a smoked filter, so
            // the panel reads as a hole cut into the device rather than a dark grey screen.
            Name = "VFD",
            Description = "Vacuum-fluorescent cyan behind smoked glass — the VCR clock look. 📟",
            Vfd = true,
            Background = "#05080A", Panel = "#0B1114", Border = "#1B2B30",
            Text = "#7FFFE4", Dim = "#3F8F82",
            Accent = "#5BE8FF", Gold = "#FFD86B", Sky = "#5BE8FF",
            Green = "#7FFFE4", Red = "#FF7A6B", DiffAdd = "#7FFFE4",
        },
        new UiTheme
        {
            // Split-Flap — a Solari departure board, and the only mechanical surface in the set.
            // Its constraint is about which GLYPHS exist rather than which colours do: a flap
            // carries one printed character, so the board can only show the set it was built with.
            // The CSS forces uppercase and monospace for that reason — the machine's real limit,
            // not a styling choice — and draws the hinge seam through the waist of every cell.
            //
            // Source code is the one place that breaks: uppercasing it would corrupt it, so code
            // keeps its own case. A board that had to print a stack trace would have the same
            // problem and no way to solve it.
            Name = "Split-Flap",
            Description = "Solari departure board — hinged flaps, all caps, mechanical clatter. 🛫",
            SplitFlap = true,
            Background = "#101014", Panel = "#191920", Border = "#2E2E38",
            Text = "#F5EAC8", Dim = "#8E866F",
            Accent = "#FFD24A", Gold = "#FFD24A", Sky = "#EDE3C4",
            Green = "#BFE08A", Red = "#E8836E", DiffAdd = "#BFE08A",
        },
        new UiTheme
        {
            // Cyanotype — a contact print in a tray, and the only NEGATIVE surface here. The
            // process exposes everywhere light reaches and washes out everywhere it does not, so
            // the glyphs are unexposed paper and the field is Prussian blue. That inverts what
            // emphasis means: bolder is LESS exposure, so the CSS renders weight as a whiter
            // stroke rather than a heavier one.
            //
            // No glow of any kind. Every other dark theme in this set emitted light; this one was
            // developed in water and hung up to dry.
            Name = "Cyanotype",
            Description = "Blueprint negative — unexposed paper on Prussian blue, brushed on. 📐",
            Cyanotype = true,
            Background = "#0E3A5F", Panel = "#13456D", Border = "#2A6389",
            Text = "#EDF4F8", Dim = "#9DBED4",
            Accent = "#FFFFFF", Gold = "#E6D9A8", Sky = "#C9E2F0",
            Green = "#C4E5D2", Red = "#F3B5A8", DiffAdd = "#C4E5D2",
        },
        new UiTheme
        {
            // Matrix LCD — a passive-matrix panel, and the deliberate opposite of Cathode Ray.
            // Liquid crystal doesn't emit light, it twists a backlight through a polarizer, so
            // nothing blooms. What a passive matrix does instead is SMEAR: row/column addressing
            // can't switch a cell cleanly, so the previous frame lingers a moment behind the new
            // one. The CSS renders that as a hard ghost offset one pixel down-right.
            //
            // Nothing here is true black — "off" is the polarizer at its darkest, which on these
            // panels is a muddy olive, and the background is the backlight leaking through. Getting
            // that wrong (pure black, bright green) is what makes most LCD themes look like a
            // colour scheme rather than a panel.
            Name = "Matrix LCD",
            Description = "Passive-matrix panel — pixel grid, ghosting smear, no glow at all. 🎮",
            IsLight = true,
            Lcd = true,
            Background = "#C4CFA1", Panel = "#B7C393", Border = "#8A9A6B",
            Text = "#1B2410", Dim = "#4A5730",
            Accent = "#33421C", Gold = "#3E4D22", Sky = "#33421C",
            Green = "#33421C", Red = "#4F3A18", DiffAdd = "#33421C",
        },
        new UiTheme
        {
            // Warm paper + black ink, fully grayscale — reads like a Kindle page. Every accent collapses to a shade of warm ink (the
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
            // Risograph — a two-drum stencil duplicator. One spot ink per drum, one pass each, and
            // the paper shifts a hair between passes, so the two never line up. The CSS puts that
            // misregistration one pixel off in the second ink; it's the entire signature, and no
            // amount of paper texture reads as riso without it.
            //
            // Two drums means exactly two inks, so the palette has two and the highlighter is
            // re-mapped rather than recoloured — a third hue would need a third pass the machine
            // doesn't have loaded. Federal Blue and Fluorescent Pink are the two most-printed riso
            // inks, which is why the combination looks instantly familiar.
            Name = "Risograph",
            Description = "Two-ink stencil print — mottled coverage and a pixel of misregistration. 🎨",
            IsLight = true,
            Riso = true,
            Background = "#F3EFE6", Panel = "#E7E1D3", Border = "#C0B7A4",
            Text = "#241F1B", Dim = "#6E6558",
            Accent = "#1A4A8F", Gold = "#8A5A1C", Sky = "#1A4A8F",
            Green = "#1A4A8F", Red = "#C42A63", DiffAdd = "#1A4A8F",
        },
        new UiTheme
        {
            // Green-Bar Fanfold — continuous-feed printer paper. FlatMotion because paper doesn't
            // animate, and the CSS locks the pale bands to the text's own line grid (six lines per
            // band) rather than to pixels: bands that drift across the lines read as wallpaper,
            // bands that contain exactly six lines read as fanfold. Sprocket margins and the
            // tear-off perforations run down both edges.
            //
            // The palette is deliberately ink-on-paper rather than colourful: a line printer had
            // one ribbon, so "colour" here means how hard the hammer struck. Red is the one
            // exception, and it earns it — two-colour ribbons (black over red) were real, and
            // errors are exactly what the red half was for.
            Name = "Green-Bar Fanfold",
            Description = "Tractor-feed printer paper — green bars, sprocket holes, ribbon ink. 🖨️",
            IsLight = true,
            FlatMotion = true,
            Fanfold = true,
            Background = "#F4F1E4", Panel = "#EAE6D5", Border = "#C3BFA8",
            Text = "#2A2822", Dim = "#6B6656",
            Accent = "#3E6B47", Gold = "#8A6A22", Sky = "#3E6B47",
            Green = "#3E6B47", Red = "#9E3227", DiffAdd = "#3E6B47",
        },
        new UiTheme
        {
            // Microfiche — a projected positive in a reader: cool, soft, and vignetted hard by the
            // lens. Cheapest of these to build, because the tell was already available: dust and
            // scratches belong to the reader's GLASS, not to the film, so they hold still while the
            // page scrolls beneath them. The overlay was already fixed; only the interpretation is
            // new.
            //
            // Light, because a fiche reader projects a positive onto a lit screen — making this
            // dark would be a photographic negative, which is a different object entirely.
            Name = "Microfiche",
            Description = "Film in a reader — soft focus, lens vignette, dust on the glass. 🔍",
            IsLight = true,
            Fiche = true,
            Background = "#D9DFE2", Panel = "#CBD3D7", Border = "#A5B0B6",
            Text = "#16212A", Dim = "#556570",
            Accent = "#2C5468", Gold = "#6B5A33", Sky = "#2C5468",
            Green = "#2F5A4A", Red = "#7A3229", DiffAdd = "#2F5A4A",
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
