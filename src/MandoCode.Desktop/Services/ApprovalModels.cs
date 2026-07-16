using MandoCode.Models;

namespace MandoCode.Desktop.Services;

/// <summary>Visual weight of an approval option (maps to button styling).</summary>
public enum ApprovalOptionKind
{
    /// <summary>Green — proceed (Approve / Approve-don't-ask).</summary>
    Proceed,
    /// <summary>Gold — deny / redirect (Deny / Provide new instructions).</summary>
    Redirect,
    /// <summary>Red — destructive (Cancel the plan).</summary>
    Destructive
}

/// <summary>Glyph is an optional Segoe Fluent icon rendered before the label; Description, when set,
/// is shown as a hover tooltip explaining what the option does.</summary>
public sealed record ApprovalOption(string Label, ApprovalOptionKind Kind, string? Glyph = null, string? Description = null);

/// <summary>
/// Everything the approval overlay needs to render one approval prompt.
/// UI-agnostic: built from the same DiffService/DiffLine models the CLI uses.
/// </summary>
public sealed class ApprovalRequest
{
    /// <summary>e.g. "Apply these changes?", "Run this command?", "Delete this file?"</summary>
    public required string Title { get; init; }

    /// <summary>Subtitle line — file path, server/tool name, etc.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Extra context (MCP tool description, delete warning). Plain text.</summary>
    public string? Detail { get; init; }

    /// <summary>Shell command text, for command approvals. Rendered monospace.</summary>
    public string? CommandText { get; init; }

    /// <summary>Collapsed diff lines, for write/delete approvals.</summary>
    public IReadOnlyList<DiffLine>? DiffLines { get; init; }

    /// <summary>e.g. "3 deletion(s), 12 addition(s) (new file)".</summary>
    public string? DiffSummary { get; init; }

    public required IReadOnlyList<ApprovalOption> Options { get; init; }

    /// <summary>Render as a non-covering bottom bar rather than the centered modal. Used for plan
    /// approval so the plan card stays readable in the transcript while the user decides.</summary>
    public bool BottomBar { get; init; }

    /// <summary>Short, specific line for the cross-agent "Approval required" toast — describes WHAT
    /// is waiting (e.g. "Wants to edit Program.cs"), not the modal's question. Falls back to
    /// <see cref="Title"/> when unset.</summary>
    public string? ToastSummary { get; init; }
}

/// <summary>
/// Implemented by MainWindow. Both methods are called from harness background threads;
/// the implementation marshals to the UI thread, shows the overlay, and completes when
/// the user chooses. The returned string is the chosen option's Label verbatim
/// (mirrors ApprovalSelectCoordinator's contract in the CLI).
/// </summary>
public interface IApprovalUi
{
    Task<string> ShowApprovalAsync(ApprovalRequest request, CancellationToken ct = default);

    /// <summary>
    /// Free-text input — used by the "Provide new instructions" approval path and by the
    /// wizard flows. When <paramref name="allowCancel"/> is true a Cancel button (and Esc)
    /// resolves to <see cref="ApprovalSignals.Cancelled"/>.
    /// </summary>
    Task<string> ShowInstructionInputAsync(string prompt, string placeholder = "", bool allowCancel = false, CancellationToken ct = default);
}

/// <summary>Sentinel values passed back through the string-based prompt contract.</summary>
public static class ApprovalSignals
{
    /// <summary>Returned by a cancelable text prompt when the user hits Cancel/Esc.
    /// ASCII CAN control char — can never collide with typed input.</summary>
    public const string Cancelled = "\u0018";
}
