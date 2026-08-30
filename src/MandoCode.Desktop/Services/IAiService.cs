using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;

namespace MandoCode.Desktop.Services;

/// <summary>
/// The slice of the harness <see cref="AIService"/> that <see cref="ViewModels.ChatController"/>
/// actually depends on — its streaming loop, approval wiring, function-call events, and history.
///
/// <para>Why this exists: <c>AIService</c> is a concrete type in the pinned, read-only harness
/// submodule, so we can't put an interface on it directly. Depending on this abstraction instead
/// (via <see cref="AiServiceAdapter"/>) does two things:</para>
/// <list type="bullet">
///   <item>absorbs harness API drift in one place — when the pin rolls forward and a signature
///   moves, the adapter breaks, not the controller's guts (the approval-wiring seam the README
///   flags as highest-risk); and</item>
///   <item>lets the request loop be driven by a fake in tests, without a live Ollama.</item>
/// </list>
///
/// The three approval callbacks are single-assignment <c>Func</c> properties (not events): they are
/// set to this tab's handlers and nulled when diff approvals are off. That's safe only because each
/// agent owns its own AIService — see AgentSession.
/// </summary>
public interface IAiService
{
    event Action<FunctionCall>? OnFunctionInvoked;
    event Action<FunctionExecutionResult>? OnFunctionCompleted;

    Func<string, string?, string, Task<DiffApprovalResult>>? OnWriteApprovalRequested { get; set; }
    Func<string, string?, Task<DiffApprovalResult>>? OnDeleteApprovalRequested { get; set; }
    Func<string, Task<DiffApprovalResult>>? OnCommandApprovalRequested { get; set; }

    Task ReinitializeAsync(MandoCodeConfig config);
    Task RefreshSettingsAsync(MandoCodeConfig config);
    Task AttachMcpPluginsAsync(CancellationToken cancellationToken = default);
    Task<(bool IsValid, string? ErrorMessage)> ValidateModelAsync();
    IAsyncEnumerable<string> ChatStreamAsync(string userMessage, CancellationToken cancellationToken = default);
    Task<GeneratedPlan> GeneratePlanAsync(
        string request,
        string? revisionContext = null,
        CancellationToken cancellationToken = default);
    string? ExportHistoryJson();

    /// <summary>Restores the authoritative request used by isolated plan-step histories.</summary>
    void SetRequestContext(string? request) { }

    /// <summary>
    /// Appends an assistant message without calling the model. Used to record a completed plan's
    /// manifest, which must land in history without giving the model an open turn to redo the work in.
    /// </summary>
    void AppendAssistantNote(string text);
    int TryRestoreHistoryJson(string json);
    Task EnterLearnModeAsync();
    Task ClearHistoryAsync();
    Task<IReadOnlyList<ChatMessage>> GetHistoryAsync();
}
