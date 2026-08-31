using System.ComponentModel;
using MandoCode.Services;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Desktop-only agent tools for the docked preview pane. They deliberately accept only project
/// files: opening arbitrary URLs is not an agent capability, and a browser-compatible file can
/// be rendered safely through the pane's project-local virtual host.
/// </summary>
public sealed class DesktopPreviewTools
{
    private static readonly HashSet<string> BrowserExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".svg"
    };

    private readonly ProjectRootAccessor _projectRoot;

    public DesktopPreviewTools(ProjectRootAccessor projectRoot) => _projectRoot = projectRoot;

    /// <summary>Raised on an agent worker thread; the view marshals it to its UI thread.</summary>
    public event Action<DesktopPreviewRequest>? Requested;

    [Description(
        "Opens a browser-compatible project file in the MandoCode Desktop preview pane. " +
        "After creating or updating an HTML, HTM, or SVG page, call this when the user would " +
        "benefit from seeing the result. Use a project-relative path only. This does not open " +
        "external websites or run a development server.")]
    public string OpenDesktopPreview(
        [Description("Project-relative path to an existing .html, .htm, or .svg page to show in the Desktop preview pane.")]
        string relativePath)
    {
        if (!TryResolveBrowserFile(relativePath, out var fullPath, out var error)) return error;

        Requested?.Invoke(DesktopPreviewRequest.Open(fullPath));
        return $"Opened {Path.GetRelativePath(_projectRoot.ProjectRoot, fullPath)} in the Desktop preview pane.";
    }

    [Description(
        "Refreshes the page currently open in the MandoCode Desktop preview pane. Call this " +
        "after finishing changes that affect an already open webpage, including its CSS, " +
        "JavaScript, or other local assets. Do not call it when no preview is open.")]
    public string RefreshDesktopPreview()
    {
        Requested?.Invoke(DesktopPreviewRequest.Refresh());
        return "Requested a refresh of the current Desktop preview.";
    }

    private bool TryResolveBrowserFile(string relativePath, out string fullPath, out string message)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            message = "A project-relative HTML, HTM, or SVG path is required.";
            return false;
        }
        if (Path.IsPathRooted(relativePath))
        {
            message = "Use a project-relative path, not an absolute path.";
            return false;
        }

        var root = Path.GetFullPath(_projectRoot.ProjectRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            message = "The requested preview file must stay inside the current project.";
            return false;
        }
        if (!File.Exists(candidate))
        {
            message = $"The file '{relativePath}' does not exist yet. Create it before opening a preview.";
            return false;
        }
        if (!BrowserExtensions.Contains(Path.GetExtension(candidate)))
        {
            message = "Desktop webpage preview supports .html, .htm, and .svg files.";
            return false;
        }

        fullPath = candidate;
        message = "";
        return true;
    }
}

public sealed record DesktopPreviewRequest(string? FullPath, bool ForceRefresh)
{
    public static DesktopPreviewRequest Open(string fullPath) => new(fullPath, ForceRefresh: false);
    public static DesktopPreviewRequest Refresh() => new(null, ForceRefresh: true);
}
