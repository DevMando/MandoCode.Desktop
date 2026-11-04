using System.Diagnostics;
using System.IO.Compression;
using MandoCode.Models;

namespace MandoCode.Desktop.Services;

/// <summary>
/// App-global owner of the user's (global) skills — the folders under
/// <c>~/.mandocode/skills</c> (or the configured override). Skills are just directories
/// each holding a <c>SKILL.md</c>; this service is the UI's create / edit / delete /
/// enable / install surface for them, plus the fan-out that makes changes reach every
/// open agent.
///
/// Every agent has its OWN <see cref="SkillLoader"/> and its "Available Skills" block is
/// baked into that agent's system prompt when its kernel is built. So, exactly like
/// <see cref="McpCoordinator"/> does for MCP tools, a change to the skill set on disk only
/// reaches a live conversation once we reload that agent's loader and rebuild its prompt —
/// which is what <see cref="ReloadAllAsync"/> does across every session.
///
/// PROJECT skills (a repo's <c>.mandocode/skills</c>) are deliberately out of scope here:
/// they belong to a checkout, not the app, so they aren't managed from this global surface.
///
/// DISABLE without an engine change: the loader skips any folder lacking a <c>SKILL.md</c>,
/// so disabling renames <c>SKILL.md</c> → <c>SKILL.md.disabled</c> and enabling renames it
/// back. Reversible, and invisible to the engine.
/// </summary>
public sealed class SkillCoordinator
{
    private const string SkillFile = "SKILL.md";
    private const string DisabledSkillFile = "SKILL.md.disabled";

    private readonly MandoCodeConfig _defaults;

    /// <summary>Set by <see cref="SessionManager"/>, same as the other coordinators.</summary>
    public Func<IEnumerable<AgentSession>> SessionsAccessor { get; set; } = Array.Empty<AgentSession>;

    public SkillCoordinator(MandoCodeConfig defaults)
    {
        _defaults = defaults;
    }

    /// <summary>The app-wide user-skills directory every agent scans.</summary>
    public string UserSkillsDirectory => _defaults.GetEffectiveUserSkillsDirectory();

    // ---------------------------------------------------------------- read

    /// <summary>
    /// Lists the global skills as management rows — including DISABLED ones, which the
    /// engine's loader hides. Ordered by name. Malformed folders are skipped silently.
    /// </summary>
    public IReadOnlyList<SkillEntry> ListGlobalSkills()
    {
        var dir = UserSkillsDirectory;
        var rows = new List<SkillEntry>();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return rows;

        foreach (var folder in Directory.EnumerateDirectories(dir))
        {
            var enabledPath = Path.Combine(folder, SkillFile);
            var disabledPath = Path.Combine(folder, DisabledSkillFile);

            var enabled = File.Exists(enabledPath);
            var path = enabled ? enabledPath : (File.Exists(disabledPath) ? disabledPath : null);
            if (path == null) continue;   // a draft folder with neither file

            var skill = SkillParser.ParseFile(path, SkillSource.User, out _);
            var folderName = Path.GetFileName(folder);
            rows.Add(new SkillEntry
            {
                Name = skill?.Name ?? folderName,
                Description = skill?.Description ?? "",
                Body = skill?.Body ?? "",
                FolderPath = folder,
                Enabled = enabled,
            });
        }

        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- CRUD

    /// <summary>
    /// Writes a skill's <c>SKILL.md</c>. <paramref name="originalFolder"/> is the folder being
    /// edited (null for a new skill); when the name changes on edit, the folder is renamed to
    /// match so the two stay in step. Returns the skill's folder path.
    /// </summary>
    public string SaveSkill(string? originalFolder, string name, string description, string body)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A skill needs a name.");

        var slug = Slug(name);
        var root = UserSkillsDirectory;
        Directory.CreateDirectory(root);

        var targetFolder = Path.Combine(root, slug);

        // Editing and the name (hence slug) changed: move the existing folder rather than
        // orphaning it. Guard against clobbering a different existing skill.
        if (originalFolder != null &&
            !string.Equals(originalFolder, targetFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(targetFolder))
                throw new InvalidOperationException($"A skill folder named '{slug}' already exists.");
            Directory.Move(originalFolder, targetFolder);
        }
        else if (originalFolder == null && Directory.Exists(targetFolder))
        {
            throw new InvalidOperationException($"A skill named '{name}' already exists.");
        }

        Directory.CreateDirectory(targetFolder);

        // Preserve the enabled/disabled state of the file we're rewriting.
        var wasDisabled = originalFolder != null &&
                          !File.Exists(Path.Combine(targetFolder, SkillFile)) &&
                          File.Exists(Path.Combine(targetFolder, DisabledSkillFile));
        var fileName = wasDisabled ? DisabledSkillFile : SkillFile;

        File.WriteAllText(Path.Combine(targetFolder, fileName), BuildSkillMarkdown(name, description, body));
        return targetFolder;
    }

    /// <summary>Deletes a skill's folder outright.</summary>
    public void DeleteSkill(string folderPath)
    {
        if (Directory.Exists(folderPath))
            Directory.Delete(folderPath, recursive: true);
    }

    /// <summary>Toggles a skill on/off by renaming its SKILL.md — see the class remarks.</summary>
    public void SetEnabled(string folderPath, bool enabled)
    {
        var enabledPath = Path.Combine(folderPath, SkillFile);
        var disabledPath = Path.Combine(folderPath, DisabledSkillFile);

        if (enabled && File.Exists(disabledPath))
        {
            if (File.Exists(enabledPath)) File.Delete(disabledPath);   // shouldn't happen; prefer the live file
            else File.Move(disabledPath, enabledPath);
        }
        else if (!enabled && File.Exists(enabledPath))
        {
            if (File.Exists(disabledPath)) File.Delete(disabledPath);
            File.Move(enabledPath, disabledPath);
        }
    }

    // -------------------------------------------------------------- install

    /// <summary>
    /// Installs skills from a source into the user-skills directory. The source is auto-detected:
    /// a git URL (cloned), a local <c>.zip</c> (extracted), or a local folder (copied). A source
    /// may hold ONE skill (a <c>SKILL.md</c> at its root) or MANY (subfolders each with one).
    /// Returns the names installed; throws with a human message on failure.
    /// </summary>
    public InstallResult InstallFrom(string source)
    {
        source = source.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Enter a git URL, a .zip path, or a folder path.");

        var staging = Path.Combine(Path.GetTempPath(), "mandocode-skill-" + Guid.NewGuid().ToString("N"));
        try
        {
            var searchRoot = MaterializeSource(source, staging);
            var skillFolders = DiscoverSkillFolders(searchRoot);
            // Nothing found isn't an error — it's user input to correct. Return an empty result and
            // let the caller show a friendly "no SKILL.md here" message instead of throwing.
            if (skillFolders.Count == 0)
                return new InstallResult(Array.Empty<string>(), Array.Empty<string>());

            var root = UserSkillsDirectory;
            Directory.CreateDirectory(root);

            var installed = new List<string>();
            var skipped = new List<string>();
            foreach (var src in skillFolders)
            {
                var slug = Slug(Path.GetFileName(src));
                var dest = Path.Combine(root, slug);
                if (Directory.Exists(dest)) { skipped.Add(slug); continue; }
                CopyDirectory(src, dest);
                installed.Add(slug);
            }

            return new InstallResult(installed, skipped);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Puts the source's contents on disk under <paramref name="staging"/> and returns the
    /// directory to scan for skills.</summary>
    private static string MaterializeSource(string source, string staging)
    {
        if (LooksLikeGitUrl(source))
        {
            Directory.CreateDirectory(staging);
            RunGitClone(source, staging);
            return staging;
        }

        if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(source))
        {
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(source, staging);
            return staging;
        }

        if (Directory.Exists(source))
            return source;   // copied straight from here; no staging needed

        throw new InvalidOperationException(
            "Source not recognized. Give a git URL (https/git/ssh), a path to a .zip, or a folder that exists.");
    }

    private static bool LooksLikeGitUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("git://", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".git", StringComparison.OrdinalIgnoreCase);

    private static void RunGitClone(string url, string dest)
    {
        var psi = new ProcessStartInfo("git", $"clone --depth 1 \"{url}\" \"{dest}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "git isn't available on this machine — install Git, or use a .zip / folder instead. (" + ex.Message + ")");
        }

        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("git clone failed: " + stderr.Trim());
    }

    // Folders that never hold a skill but are common in repos/archives — skipped while searching so
    // a big source doesn't waste time walking them (and can't surface a vendored example SKILL.md).
    private static readonly HashSet<string> IgnoredScanDirs =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs", ".idea" };

    private const int MaxScanDepth = 6;

    /// <summary>
    /// Finds every skill folder at or under <paramref name="root"/> — a directory that DIRECTLY
    /// contains a SKILL.md. Searches recursively (bounded depth) so it handles nested layouts: a
    /// single skill at the root, a <c>skills/&lt;name&gt;/</c> subtree, a GitHub <c>.zip</c>'s
    /// <c>repo-main/</c> wrapper folder, or a whole repo of them.
    ///
    /// Once a folder is identified as a skill it is NOT descended into — a skill is a unit, and this
    /// stops a skill that ships example SKILL.md files from fragmenting into several installs.
    /// </summary>
    private static List<string> DiscoverSkillFolders(string root)
    {
        var found = new List<string>();
        Walk(root, 0, found);
        return found;
    }

    private static void Walk(string dir, int depth, List<string> found)
    {
        if (depth > MaxScanDepth) return;

        if (File.Exists(Path.Combine(dir, SkillFile)))
        {
            found.Add(dir);
            return;   // a skill folder is a leaf unit — don't descend into its own contents
        }

        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(dir); }
        catch (Exception ex) { Debug.WriteLine($"[SkillCoordinator] scan skipped {dir}: {ex.Message}"); return; }

        foreach (var sub in subs)
        {
            if (IgnoredScanDirs.Contains(Path.GetFileName(sub))) continue;
            Walk(sub, depth + 1, found);
        }
    }

    // -------------------------------------------------------------- fan-out

    /// <summary>
    /// Re-scans the skill set on EVERY open agent and rebuilds each one's system prompt so the
    /// change is live in existing conversations. History is preserved (RefreshSettingsAsync swaps
    /// system message 0 in place) — see <see cref="McpCoordinator.ReloadAllAsync"/> for the twin.
    /// </summary>
    public async Task ReloadAllAsync()
    {
        foreach (var session in SessionsAccessor().ToList())
        {
            session.Skills.Reload();
            await session.Ai.RefreshSettingsAsync(session.Config);
        }
    }

    // -------------------------------------------------------------- helpers

    private static string BuildSkillMarkdown(string name, string description, string body)
    {
        // Frontmatter mirrors SkillParser's expected shape: a --- fenced YAML head, then body.
        var sb = new System.Text.StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(name).Append('\n');
        if (!string.IsNullOrWhiteSpace(description))
            sb.Append("description: ").Append(description.Replace("\r", "").Replace("\n", " ").Trim()).Append('\n');
        sb.Append("---\n\n");
        sb.Append(body.Replace("\r\n", "\n").TrimEnd());
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Folder-safe name: lowercase, spaces→hyphens, strip anything else.</summary>
    private static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : (c is ' ' or '_' or '-' ? '-' : '\0'))
            .Where(c => c != '\0')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "skill" : slug;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(source))
        {
            // Skip a .git folder so cloned skills don't drag a repo into the skills dir.
            if (string.Equals(Path.GetFileName(sub), ".git", StringComparison.OrdinalIgnoreCase)) continue;
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"[SkillCoordinator] temp cleanup failed: {ex.Message}"); }
    }
}

/// <summary>A global skill as shown in the management list (includes disabled ones).</summary>
public sealed class SkillEntry
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Body { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public bool Enabled { get; init; }
}

/// <summary>Outcome of an install: what was added and what was skipped as already-present.</summary>
public sealed record InstallResult(IReadOnlyList<string> Installed, IReadOnlyList<string> Skipped);
