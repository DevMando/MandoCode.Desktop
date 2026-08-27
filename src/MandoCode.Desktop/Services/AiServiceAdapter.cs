using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Forwards <see cref="IAiService"/> straight through to a harness <see cref="AIService"/>. This is
/// the ONLY place in the Desktop app that names the concrete AIService's method surface for the
/// controller's sake, so a harness API change on the pin roll surfaces here — one file to fix —
/// rather than scattered across ChatController's request loop and approval wiring.
///
/// The wrapped instance stays owned by AgentSession; other consumers (memory restore, MCP/skill
/// refresh) keep using it directly. Only ChatController receives it through this adapter.
/// </summary>
public sealed class AiServiceAdapter : IAiService
{
    private readonly AIService _ai;

    public AiServiceAdapter(AIService ai) => _ai = ai;

    public event Action<FunctionCall>? OnFunctionInvoked
    {
        add => _ai.OnFunctionInvoked += value;
        remove => _ai.OnFunctionInvoked -= value;
    }

    public event Action<FunctionExecutionResult>? OnFunctionCompleted
    {
        add => _ai.OnFunctionCompleted += value;
        remove => _ai.OnFunctionCompleted -= value;
    }

    public Func<string, string?, string, Task<DiffApprovalResult>>? OnWriteApprovalRequested
    {
        get => _ai.OnWriteApprovalRequested;
        set => _ai.OnWriteApprovalRequested = value;
    }

    public Func<string, string?, Task<DiffApprovalResult>>? OnDeleteApprovalRequested
    {
        get => _ai.OnDeleteApprovalRequested;
        set => _ai.OnDeleteApprovalRequested = value;
    }

    public Func<string, Task<DiffApprovalResult>>? OnCommandApprovalRequested
    {
        get => _ai.OnCommandApprovalRequested;
        set => _ai.OnCommandApprovalRequested = value;
    }

    public Task ReinitializeAsync(MandoCodeConfig config) => _ai.ReinitializeAsync(config);
    public Task RefreshSettingsAsync(MandoCodeConfig config) => _ai.RefreshSettingsAsync(config);
    public Task AttachMcpPluginsAsync(CancellationToken cancellationToken = default) => _ai.AttachMcpPluginsAsync(cancellationToken);
    public Task<(bool IsValid, string? ErrorMessage)> ValidateModelAsync() => _ai.ValidateModelAsync();
    public IAsyncEnumerable<string> ChatStreamAsync(string userMessage, CancellationToken cancellationToken = default) => _ai.ChatStreamAsync(userMessage, cancellationToken);
    public string? ExportHistoryJson() => _ai.ExportHistoryJson();

    public void AppendAssistantNote(string text) => _ai.AppendAssistantNote(text);
    public int TryRestoreHistoryJson(string json) => _ai.TryRestoreHistoryJson(json);
    public Task EnterLearnModeAsync() => _ai.EnterLearnModeAsync();
    public Task ClearHistoryAsync() => _ai.ClearHistoryAsync();
    public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync() => _ai.GetHistoryAsync();
}
