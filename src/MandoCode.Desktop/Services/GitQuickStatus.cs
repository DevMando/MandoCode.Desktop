using System.Diagnostics;
using MandoCode.Models;

namespace MandoCode.Desktop.Services;

/// <summary>A parsed working-tree diff for one file, ready for TranscriptHtmlBuilder.DiffCard.
/// <paramref name="Truncated"/> when the diff blew past the render cap.</summary>
public sealed record GitFileDiff(IReadOnlyList<DiffLine> Lines, string Summary, bool Truncated);

/// <summary>One working-tree change. <paramref name="Kind"/> is a display letter:
/// "M" modified, "A" added/staged-new, "D" deleted, "R" renamed, "U" untracked,
/// "!" merge conflict.</summary>
public sealed record GitChangeEntry(string RelPath, string Kind);

/// <summary>Local git state for the status strip and the explorer's Changes tab.
/// <paramref name="Branch"/> is the short SHA when <paramref name="Detached"/>.
/// <paramref name="Conflicted"/> means unmerged paths exist (mid-merge/rebase) — a louder
/// state than plain <paramref name="Dirty"/>.</summary>
/// <summary><paramref name="Oid"/> is the full HEAD commit hash — comparing it across
/// snapshots distinguishes "changes were committed" (HEAD moved) from "changes were
/// reverted" (HEAD didn't). <paramref name="RepoRoot"/> is the repository's toplevel, which
/// may be an ANCESTOR of the queried folder (git resolves repos by walking up); when it is,
/// <paramref name="Changes"/> is scoped to the queried subtree with subtree-relative paths.</summary>
public sealed record GitBranchInfo(
    string Branch, bool Dirty, bool Conflicted, int Ahead, int Behind, bool Detached,
    IReadOnlyList<GitChangeEntry> Changes, string Oid, string RepoRoot = "");

/// <summary>
/// Reads branch/dirty/ahead-behind state by shelling out to git — one
/// <c>git status --porcelain=v2 --branch</c> call carries all of it. Local-only: no network,
/// no GitHub API. Any failure (no git on PATH, not a repo, timeout) returns null and the
/// caller hides the chip; this must never surface an error to the user.
/// </summary>
public static class GitQuickStatus
{
    public static GitBranchInfo? TryGet(string root)
    {
        try
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

            // Git resolves a repo by walking UP the tree, so the repo may be an ancestor of
            // root (a tab opened on a subfolder — or a stray .git in Desktop/home catching
            // everything under it). The toplevel is surfaced so the UI can disclose it.
            var toplevel = RunGitLine(root, "rev-parse", "--show-toplevel");
            if (string.IsNullOrEmpty(toplevel)) return null;
            var repoRoot = Path.GetFullPath(toplevel.Replace('/', Path.DirectorySeparatorChar));

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // relativePaths=true pinned explicitly: porcelain-v2 paths then come relative to
            // the CWD (our project root) — files elsewhere in an ancestor repo arrive as
            // "../..." and are filtered below. Verified empirically; a user's global config
            // must not be able to flip this under us.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("status.relativePaths=true");
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain=v2");
            psi.ArgumentList.Add("--branch");

            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            if (p.ExitCode != 0) return null;

            string branch = "", oid = "";
            bool dirty = false, conflicted = false, detached = false;
            int ahead = 0, behind = 0;
            var changes = new List<GitChangeEntry>();

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
                {
                    branch = line["# branch.head ".Length..].Trim();
                    detached = branch == "(detached)";
                }
                else if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
                {
                    oid = line["# branch.oid ".Length..].Trim();
                }
                else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    foreach (var t in line["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (t[0] == '+') int.TryParse(t[1..], out ahead);
                        else if (t[0] == '-') int.TryParse(t[1..], out behind);
                    }
                }
                else if (line.Length > 0 && line[0] != '#')
                {
                    var entry = ParseChangeLine(line);
                    if (entry == null) continue;
                    // Scope to the project-root subtree: paths are CWD-relative (pinned
                    // above), so anything outside arrives as "../…". Churn elsewhere in an
                    // ancestor repo is not this tab's business — dirty/conflicted included.
                    if (entry.RelPath.StartsWith("../", StringComparison.Ordinal)) continue;
                    dirty = true;
                    if (entry.Kind == "!") conflicted = true;
                    changes.Add(entry);
                }
            }

            if (detached)
                branch = oid.Length >= 7 ? oid[..7] : oid;   // no branch — show the commit instead

            // Conflicts float to the top; everything else alphabetical.
            var ordered = changes
                .OrderBy(c => c.Kind == "!" ? 0 : 1)
                .ThenBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return branch.Length == 0 ? null : new GitBranchInfo(branch, dirty, conflicted, ahead, behind, detached, ordered, oid, repoRoot);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses one non-header porcelain-v2 entry into a display row. Formats:
    /// "1 XY ..... path" (ordinary), "2 XY ..... path\torig" (rename/copy),
    /// "u XY ..... path" (unmerged), "? path" (untracked). Unknown shapes → null.</summary>
    private static GitChangeEntry? ParseChangeLine(string line)
    {
        try
        {
            switch (line[0])
            {
                case '1':
                {
                    var parts = line.Split(' ', 9);
                    return parts.Length == 9 ? new GitChangeEntry(parts[8], KindFromXY(parts[1])) : null;
                }
                case '2':
                {
                    var parts = line.Split(' ', 10);
                    if (parts.Length != 10) return null;
                    var path = parts[9].Split('\t')[0];   // "newPath\toldPath" — show the new one
                    return new GitChangeEntry(path, "R");
                }
                case 'u':
                {
                    var parts = line.Split(' ', 11);
                    return parts.Length == 11 ? new GitChangeEntry(parts[10], "!") : null;
                }
                case '?':
                    return new GitChangeEntry(line[2..], "U");
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Collapses the two-character staged/unstaged state into one display letter,
    /// preferring the working-tree side when both are set.</summary>
    private static string KindFromXY(string xy)
    {
        var c = xy.Length == 2 && xy[1] != '.' ? xy[1] : (xy.Length >= 1 ? xy[0] : 'M');
        return c switch { 'A' => "A", 'D' => "D", 'R' => "R", _ => "M" };
    }

    /// <summary>One-line git query (e.g. rev-parse). Null on any failure.</summary>
    private static string? RunGitLine(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return null;
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(4000))
        {
            try { p.Kill(); } catch { }
            return null;
        }
        return p.ExitCode == 0 ? output.Trim() : null;
    }

    /// <summary>Discards a file's uncommitted changes: <c>git checkout HEAD -- path</c>
    /// restores index + worktree to the last commit (also resurrects a deleted file).
    /// DESTRUCTIVE and unrecoverable — callers must confirm with the user first.</summary>
    public static bool TryUndoChanges(string root, string relPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("checkout");
            psi.ArgumentList.Add("HEAD");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(relPath);

            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private const int MaxDiffLines = 4000;   // render cap — a transcript card, not a diff IDE

    /// <summary>Working-tree diff of one file vs HEAD (staged + unstaged combined), parsed
    /// into DiffCard's model. Untracked files render as all-additions (they have no HEAD
    /// side). Null on any failure; empty Lines when the file is binary or unchanged.</summary>
    public static GitFileDiff? TryGetDiff(string root, string relPath, bool untracked)
    {
        try
        {
            if (untracked) return DiffForUntracked(root, relPath);

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("diff");
            psi.ArgumentList.Add("--no-color");
            psi.ArgumentList.Add("HEAD");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(relPath);

            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            if (p.ExitCode != 0) return null;

            if (output.Contains("Binary files ", StringComparison.Ordinal))
                return new GitFileDiff(Array.Empty<DiffLine>(), "binary file — no text diff", false);

            return ParseUnifiedDiff(output);
        }
        catch
        {
            return null;
        }
    }

    private static GitFileDiff? DiffForUntracked(string root, string relPath)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return null;

        // Cheap binary sniff: NUL in the first 8k.
        using (var fs = File.OpenRead(full))
        {
            var probe = new byte[Math.Min(8192, fs.Length)];
            fs.ReadExactly(probe);
            if (Array.IndexOf(probe, (byte)0) >= 0)
                return new GitFileDiff(Array.Empty<DiffLine>(), "new binary file — no text diff", false);
        }

        var lines = new List<DiffLine>();
        var truncated = false;
        var n = 0;
        foreach (var text in File.ReadLines(full))
        {
            if (++n > MaxDiffLines) { truncated = true; break; }
            lines.Add(new DiffLine { LineType = DiffLineType.Added, Content = text, NewLineNumber = n });
        }
        var summary = $"new file — {lines.Count} addition(s)" + (truncated ? $" (showing first {MaxDiffLines} lines)" : "");
        return new GitFileDiff(lines, summary, truncated);
    }

    /// <summary>Unified-diff hunks → DiffLine rows. Header lines before the first @@ are
    /// skipped; "\ No newline at end of file" markers are ignored.</summary>
    private static GitFileDiff ParseUnifiedDiff(string output)
    {
        var lines = new List<DiffLine>();
        int oldNum = 0, newNum = 0, adds = 0, removes = 0;
        var inHunk = false;
        var truncated = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                // "@@ -12,5 +14,6 @@ optional section" — starting line numbers per side.
                var marks = line.Split(' ');
                if (marks.Length >= 3 &&
                    TryParseHunkStart(marks[1], out oldNum) &&
                    TryParseHunkStart(marks[2], out newNum))
                {
                    inHunk = true;
                    if (lines.Count > 0)   // visual separator between hunks
                        lines.Add(new DiffLine { LineType = DiffLineType.Unchanged, Content = "⋯" });
                }
                continue;
            }
            // Real context lines are " " + content (never empty) — a zero-length line here is
            // only the artifact of splitting after the final newline.
            if (!inHunk || line.Length == 0) continue;
            if (lines.Count >= MaxDiffLines) { truncated = true; break; }

            switch (line[0])
            {
                case '+':
                    lines.Add(new DiffLine { LineType = DiffLineType.Added, Content = line[1..], NewLineNumber = newNum++ });
                    adds++;
                    break;
                case '-':
                    lines.Add(new DiffLine { LineType = DiffLineType.Removed, Content = line[1..], OldLineNumber = oldNum++ });
                    removes++;
                    break;
                case '\\':
                    break;   // "\ No newline at end of file"
                default:
                    lines.Add(new DiffLine
                    {
                        LineType = DiffLineType.Unchanged,
                        Content = line[1..],
                        OldLineNumber = oldNum++,
                        NewLineNumber = newNum++,
                    });
                    break;
            }
        }

        var summary = $"{removes} deletion(s), {adds} addition(s)"
            + (truncated ? $" (showing first {MaxDiffLines} lines)" : "");
        return new GitFileDiff(lines, summary, truncated);

        static bool TryParseHunkStart(string mark, out int start)
        {
            // "-12,5" or "+14" → 12 / 14
            start = 0;
            var body = mark.TrimStart('-', '+');
            var comma = body.IndexOf(',');
            return int.TryParse(comma >= 0 ? body[..comma] : body, out start);
        }
    }
}
