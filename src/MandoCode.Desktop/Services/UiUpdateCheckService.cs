using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandoCode.Desktop.Services;

public sealed record UiUpdateInfo(string CurrentVersion, string LatestVersion, string DownloadUrl);

/// <summary>
/// MandoCode.Desktop's own "newer version available" check — the desktop counterpart of the
/// CLI's UpdateCheckService, pointed at this app's GitHub Releases instead of the
/// MandoCode NuGet package (the two products version independently).
///
/// Same manners as the CLI checker: self-throttles to once per 24h via a small state
/// file, and fails silent on everything — no repo yet, no releases yet, offline, rate
/// limited — an update nag must never disrupt the session. Ships safe today: until a
/// GitHub release exists it simply finds nothing.
/// </summary>
public sealed class UiUpdateCheckService
{
    // Releases of the desktop app. Publish a release tagged v0.2.0 (or 0.2.0) and
    // older installs start nagging within a day.
    private const string ReleasesApiUrl = "https://api.github.com/repos/DevMando/MandoCode.Desktop/releases/latest";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static string StateFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "update-check.json");

    /// <summary>
    /// Numeric version, for comparing against published releases. Never carries a prerelease tag —
    /// see <see cref="DisplayVersion"/> for what a human should be shown.
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// Version for display, including any prerelease tag (e.g. "v0.16.0-rc.1").
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="CurrentVersion"/>, which feeds version comparison and must stay
    /// numeric. The window title used the numeric one, so a tagged test build was indistinguishable
    /// from the release it was cut from — exactly the confusion that makes a stale binary hard to
    /// spot. Shares the CLI's formatting so both products label builds the same way.
    /// </remarks>
    public static string DisplayVersion =>
        MandoCode.Services.VersionLabel.ForAssembly(Assembly.GetExecutingAssembly());

    public async Task<UiUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var state = LoadState();

            string? latestTag = state?.LatestVersion;
            string? latestUrl = state?.DownloadUrl;

            var due = state == null || DateTime.UtcNow - state.LastCheckUtc >= CheckInterval;
            if (due)
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                // GitHub's API rejects requests without a User-Agent.
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"MandoCode.Desktop/{CurrentVersion}");

                var release = await http.GetFromJsonAsync<GitHubRelease>(ReleasesApiUrl, cancellationToken);
                if (release?.TagName != null)
                {
                    latestTag = release.TagName;
                    latestUrl = release.HtmlUrl;
                }

                // Record the check even when nothing was found (404 throws and skips this,
                // which is fine — the state file just stays absent until a release exists).
                SaveState(new CheckState
                {
                    LastCheckUtc = DateTime.UtcNow,
                    LatestVersion = latestTag ?? "",
                    DownloadUrl = latestUrl ?? ""
                });
            }

            if (string.IsNullOrEmpty(latestTag)) return null;

            var latest = ParseVersion(latestTag);
            var current = ParseVersion(CurrentVersion);
            if (latest == null || current == null || latest <= current) return null;

            return new UiUpdateInfo(
                CurrentVersion,
                latestTag.TrimStart('v', 'V'),
                string.IsNullOrEmpty(latestUrl) ? "https://github.com/DevMando/MandoCode.Desktop/releases" : latestUrl!);
        }
        catch
        {
            return null; // see class summary — always fail silent
        }
    }

    private static Version? ParseVersion(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        // Normalize "0.2" → "0.2.0" so Version.TryParse accepts both styles.
        if (text.Count(c => c == '.') == 1) text += ".0";
        return Version.TryParse(text, out var v) ? v : null;
    }

    private static CheckState? LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return null;
            return JsonSerializer.Deserialize<CheckState>(File.ReadAllText(StateFilePath));
        }
        catch { return null; }
    }

    private static void SaveState(CheckState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state));
        }
        catch { /* throttle state is best-effort */ }
    }

    private sealed class CheckState
    {
        public DateTime LastCheckUtc { get; set; }
        public string LatestVersion { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    }
}
