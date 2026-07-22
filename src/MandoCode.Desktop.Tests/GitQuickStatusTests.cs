using System.Diagnostics;
using MandoCode.Desktop.Services;
using MandoCode.Models;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>Integration tests against real throwaway git repos — verifies the porcelain
/// parsing, diff parsing, and undo behavior against whatever git actually outputs.</summary>
public sealed class GitQuickStatusTests
{
    /// <summary>Throwaway git repo in the temp dir, deleted on dispose.</summary>
    private sealed class TempRepo : IDisposable
    {
        public string Root { get; }

        public TempRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), "mandocode-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Git("init", "-b", "main");
            Git("config", "user.email", "test@test.local");
            Git("config", "user.name", "MandoCode Tests");
            Git("config", "commit.gpgsign", "false");
            // Byte-faithful checkouts: Git for Windows defaults to autocrlf=true, which
            // would restore "x\n" as "x\r\n" and fail exact-content assertions.
            Git("config", "core.autocrlf", "false");
        }

        public void Write(string rel, string content) =>
            File.WriteAllText(Path.Combine(Root, rel), content);

        public string Read(string rel) => File.ReadAllText(Path.Combine(Root, rel));

        public void CommitAll(string message = "commit")
        {
            Git("add", "-A");
            Git("commit", "-m", message);
        }

        public string Git(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
            return stdout;
        }

        public void Dispose()
        {
            try
            {
                // .git objects are read-only on Windows; strip attributes or Delete throws.
                foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Root, recursive: true);
            }
            catch { /* leftover temp dirs are tolerable; failing the test run is not */ }
        }
    }

    [Fact]
    public void NonRepo_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mandocode-tests-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(GitQuickStatus.TryGet(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CleanRepo_ReportsBranchAndCleanTree()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.CommitAll();

        var info = GitQuickStatus.TryGet(repo.Root);
        Assert.NotNull(info);
        Assert.Equal("main", info!.Branch);
        Assert.False(info.Dirty);
        Assert.Empty(info.Changes);
        Assert.Equal(40, info.Oid.Length);   // full SHA-1 — the delta tracker compares these
    }

    [Fact]
    public void ModifiedUntrackedAndDeleted_GetTheirKinds()
    {
        using var repo = new TempRepo();
        repo.Write("mod.txt", "one\n");
        repo.Write("del.txt", "bye\n");
        repo.CommitAll();

        repo.Write("mod.txt", "two\n");
        repo.Write("new.txt", "hi\n");
        File.Delete(Path.Combine(repo.Root, "del.txt"));

        var info = GitQuickStatus.TryGet(repo.Root)!;
        Assert.True(info.Dirty);
        var kinds = info.Changes.ToDictionary(c => c.RelPath, c => c.Kind);
        Assert.Equal("M", kinds["mod.txt"]);
        Assert.Equal("U", kinds["new.txt"]);
        Assert.Equal("D", kinds["del.txt"]);
    }

    // Git resolves repos by walking UP the tree (a tab on a repo subfolder, or a stray .git
    // in Desktop/home catching everything beneath it). Results must be scoped to the queried
    // subtree with subtree-relative paths — porcelain's repo-root-relative paths would
    // otherwise break every downstream path join, undo pathspec, and @token.
    [Fact]
    public void SubfolderRoot_ScopesChangesToSubtree()
    {
        using var repo = new TempRepo();
        Directory.CreateDirectory(Path.Combine(repo.Root, "sub"));
        repo.Write("outside.txt", "o\n");
        repo.Write(Path.Combine("sub", "inside.txt"), "i\n");
        repo.CommitAll();
        repo.Write("outside.txt", "o2\n");
        repo.Write(Path.Combine("sub", "inside.txt"), "i2\n");

        var info = GitQuickStatus.TryGet(Path.Combine(repo.Root, "sub"));
        Assert.NotNull(info);
        Assert.Equal("main", info!.Branch);
        var entry = Assert.Single(info.Changes);
        Assert.Equal("inside.txt", entry.RelPath);   // subtree-relative, not "sub/inside.txt"
        Assert.Equal("M", entry.Kind);
        Assert.True(info.RepoRoot.Length > 0);       // callers can disclose the ancestor repo
    }

    [Fact]
    public void CommitMovesOid()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.CommitAll();
        var before = GitQuickStatus.TryGet(repo.Root)!.Oid;

        repo.Write("a.txt", "two\n");
        repo.CommitAll("second");
        var after = GitQuickStatus.TryGet(repo.Root)!.Oid;

        Assert.NotEqual(before, after);   // this is what distinguishes COMMITTED from REVERTED
    }

    [Fact]
    public void Diff_ModifiedFile_ParsesAddsAndRemoves()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.CommitAll();
        repo.Write("a.txt", "two\n");

        var diff = GitQuickStatus.TryGetDiff(repo.Root, "a.txt", untracked: false);
        Assert.NotNull(diff);
        Assert.Contains(diff!.Lines, l => l.LineType == DiffLineType.Removed && l.Content == "one");
        Assert.Contains(diff.Lines, l => l.LineType == DiffLineType.Added && l.Content == "two");
        Assert.Equal("1 deletion(s), 1 addition(s)", diff.Summary);
    }

    [Fact]
    public void Diff_UntrackedFile_IsAllAdditions()
    {
        using var repo = new TempRepo();
        repo.Write("seed.txt", "x\n");
        repo.CommitAll();
        repo.Write("new.txt", "l1\nl2\nl3\n");

        var diff = GitQuickStatus.TryGetDiff(repo.Root, "new.txt", untracked: true);
        Assert.NotNull(diff);
        Assert.Equal(3, diff!.Lines.Count);
        Assert.All(diff.Lines, l => Assert.Equal(DiffLineType.Added, l.LineType));
        Assert.Contains("new file", diff.Summary);
    }

    [Fact]
    public void UndoChanges_RestoresModifiedContent()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "original\n");
        repo.CommitAll();
        repo.Write("a.txt", "mangled\n");

        Assert.True(GitQuickStatus.TryUndoChanges(repo.Root, "a.txt"));
        Assert.Equal("original\n", repo.Read("a.txt"));
    }

    [Fact]
    public void UndoChanges_ResurrectsDeletedFile()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "keep me\n");
        repo.CommitAll();
        File.Delete(Path.Combine(repo.Root, "a.txt"));

        Assert.True(GitQuickStatus.TryUndoChanges(repo.Root, "a.txt"));
        Assert.Equal("keep me\n", repo.Read("a.txt"));
    }

    [Fact]
    public void UndoChanges_FailsForUntrackedFile()
    {
        using var repo = new TempRepo();
        repo.Write("seed.txt", "x\n");
        repo.CommitAll();
        repo.Write("floating.txt", "no HEAD side\n");

        Assert.False(GitQuickStatus.TryUndoChanges(repo.Root, "floating.txt"));
    }
}
