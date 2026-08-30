using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Faithful port of the CLI's DiffApprovalHandler with the Spectre rendering swapped
/// for the XAML approval overlay (via <see cref="IApprovalUi"/>) and transcript HTML
/// cards. The decision logic — option labels, session bypass state, prompt-gate
/// serialization, the "Cancel the plan" option only while a plan executes, and the
/// DiffApprovalResult contract consumed by FunctionInvocationFilter — is unchanged.
/// </summary>
public sealed class WinUiApprovalService
{
    private readonly ApprovalPromptGate _promptGate;
    private readonly BusyStateService _busy;
    private readonly PlanHandoff _planHandoff;
    private readonly TranscriptWriter _transcript;
    private readonly TranscriptHtmlBuilder _html;

    private bool _globalWriteBypass;
    private readonly HashSet<string> _approvedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set by MainWindow once the window exists. Approvals auto-approve are
    /// impossible before that because no AI request can start without the window.</summary>
    public IApprovalUi? Ui { get; set; }

    // Segoe Fluent icon glyphs for the approval option buttons.
    private const string GlyphApprove = "";        // check mark
    private const string GlyphApproveNoAsk = "";   // completed (check in circle)
    private const string GlyphDeny = "";           // cancel (X)
    private const string GlyphInstructions = "";   // edit (pencil)
    private const string GlyphCancelPlan = "";     // stop

    // Same labels as the CLI so behavior (and muscle memory) match.
    private const string ApproveLabel = "Approve";
    private const string ApproveDeletionLabel = "Approve deletion";
    private const string ApproveNoAskWriteLabel = "Approve - okay to write & modify files don't ask me again";
    private const string ApproveNoAskRunLabel = "Approve - okay to run commands don't ask me again";
    private const string ApproveNoAskDeleteLabel = "Approve - okay to write, modify & delete files don't ask me again";
    private const string DenyLabel = "Deny";
    private const string ProvideInstructionsLabel = "Provide new instructions";
    private const string CancelPlanLabel = "Cancel the plan";

    public WinUiApprovalService(
        ApprovalPromptGate promptGate,
        BusyStateService busy,
        PlanHandoff planHandoff,
        TranscriptWriter transcript,
        TranscriptHtmlBuilder html)
    {
        _promptGate = promptGate;
        _busy = busy;
        _planHandoff = planHandoff;
        _transcript = transcript;
        _html = html;
    }

    public async Task<DiffApprovalResult> HandleWriteApprovalAsync(string relativePath, string? oldContent, string newContent)
    {
        using var promptHold = await _promptGate.AcquireAsync();
        _busy.Stop();

        var diffLines = DiffService.ComputeDiff(oldContent, newContent);
        var isNewFile = oldContent == null;
        var displayLines = DiffService.CollapseContext(diffLines, 3);
        var additions = diffLines.Count(l => l.LineType == DiffLineType.Added);
        var deletions = diffLines.Count(l => l.LineType == DiffLineType.Removed);
        var fileName = Path.GetFileName(relativePath);
        var summary = $"{deletions} deletion(s), {additions} addition(s){(isNewFile ? " (new file)" : "")}";

        // Always show the diff in the transcript so the user can see what changed,
        // even when auto-approved (mirrors the CLI's scrollback panel).
        _transcript.Append(_html.DiffCard(relativePath, displayLines, summary));

        if (_globalWriteBypass || _approvedFiles.Contains(relativePath))
        {
            _transcript.Append(_html.StatusCard("Auto-approved", $"{fileName} was already approved for this session.", "success"));
            _busy.Start();
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var noAskLabel = isNewFile
            ? ApproveNoAskWriteLabel
            : $"Approve - Don't ask again to modify {fileName}";

        var options = new List<ApprovalOption>
        {
            new(ApproveLabel, ApprovalOptionKind.Proceed, GlyphApprove),
            new(noAskLabel, ApprovalOptionKind.Proceed, GlyphApproveNoAsk),
            new(DenyLabel, ApprovalOptionKind.Redirect, GlyphDeny),
            new(ProvideInstructionsLabel, ApprovalOptionKind.Redirect, GlyphInstructions)
        };
        if (_planHandoff.IsExecuting) options.Add(new(CancelPlanLabel, ApprovalOptionKind.Destructive, GlyphCancelPlan));

        var request = new ApprovalRequest
        {
            Title = "Apply these changes?",
            Subtitle = relativePath,
            DiffLines = displayLines,
            DiffSummary = summary,
            ToastSummary = isNewFile ? $"Wants to create {fileName}" : $"Wants to edit {fileName}",
            Options = options
        };

        var (choice, instructions) = await PromptAllowingInstructionCancelAsync(request);

        DiffApprovalResult result;
        if (choice == ApproveLabel)
        {
            _transcript.Append(_html.StatusCard("Changes approved", $"{fileName} can be updated.", "success"));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }
        else if (choice == noAskLabel)
        {
            _transcript.Append(_html.StatusCard("Changes approved", $"{fileName} can be updated.", "success"));
            if (isNewFile)
            {
                _globalWriteBypass = true;
                _transcript.Append(_html.StatusCard("Auto-approval enabled", "All future writes will be auto-approved for this session.", "warning"));
            }
            else
            {
                _approvedFiles.Add(relativePath);
                _transcript.Append(_html.StatusCard("Auto-approval enabled", $"Future modifications to {fileName} will be auto-approved.", "warning"));
            }
            result = new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain };
        }
        else if (choice == DenyLabel)
        {
            _transcript.Append(_html.Error("Changes denied."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Denied };
        }
        else if (choice == CancelPlanLabel)
        {
            _transcript.Append(_html.Error("Plan cancellation requested."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.CancelPlan };
        }
        else // Provide new instructions
        {
            _transcript.Append(_html.Warn("Redirecting with new instructions..."));
            result = new DiffApprovalResult
            {
                Response = DiffApprovalResponse.NewInstructions,
                UserMessage = instructions
            };
        }

        _busy.Start();
        return result;
    }

    public async Task<DiffApprovalResult> HandleCommandApprovalAsync(string command)
    {
        using var promptHold = await _promptGate.AcquireAsync();
        _busy.Stop();

        _transcript.Append(_html.CommandCard(command));

        if (_globalWriteBypass)
        {
            _transcript.Append(_html.StatusCard("Command auto-approved", "Commands are currently auto-approved for this session.", "success"));
            _busy.Start();
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var options = new List<ApprovalOption>
        {
            new(ApproveLabel, ApprovalOptionKind.Proceed, GlyphApprove),
            new(ApproveNoAskRunLabel, ApprovalOptionKind.Proceed, GlyphApproveNoAsk),
            new(DenyLabel, ApprovalOptionKind.Redirect, GlyphDeny),
            new(ProvideInstructionsLabel, ApprovalOptionKind.Redirect, GlyphInstructions)
        };
        if (_planHandoff.IsExecuting) options.Add(new(CancelPlanLabel, ApprovalOptionKind.Destructive, GlyphCancelPlan));

        var request = new ApprovalRequest
        {
            Title = "Run this command?",
            CommandText = command,
            ToastSummary = $"Wants to run: {(command.Length > 48 ? command[..48] + "…" : command)}",
            Options = options,
            // Non-covering bottom bar (like plan approval): the transcript stays readable —
            // often the context right above IS why the agent wants to run this command.
            BottomBar = true
        };

        var (choice, instructions) = await PromptAllowingInstructionCancelAsync(request);

        DiffApprovalResult result;
        if (choice == ApproveLabel)
        {
            _transcript.Append(_html.StatusCard("Command approved", "The command can now run.", "success"));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }
        else if (choice == ApproveNoAskRunLabel)
        {
            _transcript.Append(_html.StatusCard("Command approved", "The command can now run.", "success"));
            _globalWriteBypass = true;
            _transcript.Append(_html.StatusCard("Auto-approval enabled", "All future writes, deletions, and commands will be auto-approved for this session.", "warning"));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain };
        }
        else if (choice == DenyLabel)
        {
            _transcript.Append(_html.Error("Command denied."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Denied };
        }
        else if (choice == CancelPlanLabel)
        {
            _transcript.Append(_html.Error("Plan cancellation requested."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.CancelPlan };
        }
        else
        {
            _transcript.Append(_html.Warn("Redirecting with new instructions..."));
            result = new DiffApprovalResult
            {
                Response = DiffApprovalResponse.NewInstructions,
                UserMessage = instructions
            };
        }

        _busy.Start();
        return result;
    }

    public async Task<DiffApprovalResult> HandleDeleteApprovalAsync(string relativePath, string? existingContent)
    {
        using var promptHold = await _promptGate.AcquireAsync();
        _busy.Stop();

        var isFolder = existingContent != null && existingContent.StartsWith("Folder:");
        IReadOnlyList<DiffLine>? displayLines = null;
        string warning;

        if (isFolder)
        {
            _transcript.Append(_html.FolderDeleteCard(relativePath, existingContent!));
            warning = $"This will DELETE the folder and ALL its contents: {relativePath}/";
        }
        else
        {
            var diffLines = new List<DiffLine>();
            if (!string.IsNullOrEmpty(existingContent))
            {
                var lines = existingContent.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    diffLines.Add(new DiffLine
                    {
                        LineType = DiffLineType.Removed,
                        Content = lines[i].TrimEnd('\r'),
                        OldLineNumber = i + 1
                    });
                }
            }
            displayLines = DiffService.CollapseContext(diffLines, 3);
            _transcript.Append(_html.DiffCard(relativePath, displayLines, $"{diffLines.Count} deletion(s)"));
            warning = $"This will DELETE the file: {relativePath}";
        }
        _transcript.Append(_html.Error(warning));

        if (_globalWriteBypass)
        {
            _transcript.Append(_html.StatusCard("Deletion auto-approved", "Deletions are currently auto-approved for this session.", "success"));
            _busy.Start();
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var options = new List<ApprovalOption>
        {
            new(ApproveDeletionLabel, ApprovalOptionKind.Proceed, GlyphApprove),
            new(ApproveNoAskDeleteLabel, ApprovalOptionKind.Proceed, GlyphApproveNoAsk),
            new(DenyLabel, ApprovalOptionKind.Redirect, GlyphDeny),
            new(ProvideInstructionsLabel, ApprovalOptionKind.Redirect, GlyphInstructions)
        };
        if (_planHandoff.IsExecuting) options.Add(new(CancelPlanLabel, ApprovalOptionKind.Destructive, GlyphCancelPlan));

        var request = new ApprovalRequest
        {
            Title = "Delete this file?",
            Subtitle = relativePath,
            Detail = warning,
            DiffLines = displayLines,
            ToastSummary = isFolder
                ? $"Wants to delete folder {Path.GetFileName(relativePath)}/"
                : $"Wants to delete {Path.GetFileName(relativePath)}",
            Options = options
        };

        var (choice, instructions) = await PromptAllowingInstructionCancelAsync(request);

        DiffApprovalResult result;
        if (choice == ApproveDeletionLabel)
        {
            _transcript.Append(_html.StatusCard("Deletion approved", "The deletion can now run.", "success"));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }
        else if (choice == ApproveNoAskDeleteLabel)
        {
            _transcript.Append(_html.StatusCard("Deletion approved", "The deletion can now run.", "success"));
            _globalWriteBypass = true;
            _transcript.Append(_html.StatusCard("Auto-approval enabled", "All future writes and deletions will be auto-approved for this session.", "warning"));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain };
        }
        else if (choice == DenyLabel)
        {
            _transcript.Append(_html.Error("Deletion denied."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Denied };
        }
        else if (choice == CancelPlanLabel)
        {
            _transcript.Append(_html.Error("Plan cancellation requested."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.CancelPlan };
        }
        else
        {
            _transcript.Append(_html.Warn("Redirecting with new instructions..."));
            result = new DiffApprovalResult
            {
                Response = DiffApprovalResponse.NewInstructions,
                UserMessage = instructions
            };
        }

        _busy.Start();
        return result;
    }

    public async Task<DiffApprovalResult> HandleMcpApprovalAsync(string serverName, string toolName, string? description)
    {
        using var promptHold = await _promptGate.AcquireAsync();
        _busy.Stop();

        _transcript.Append(_html.Info($"MCP tool request: {toolName} (from {serverName})"));

        if (_globalWriteBypass)
        {
            _transcript.Append(_html.StatusCard("MCP tool auto-approved", "MCP tools are currently auto-approved for this session.", "success"));
            _busy.Start();
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var noAskMcpLabel = $"Approve - don't ask again for {toolName} this session";
        var options = new List<ApprovalOption>
        {
            new(ApproveLabel, ApprovalOptionKind.Proceed, GlyphApprove),
            new(noAskMcpLabel, ApprovalOptionKind.Proceed, GlyphApproveNoAsk),
            new(DenyLabel, ApprovalOptionKind.Redirect, GlyphDeny)
        };
        if (_planHandoff.IsExecuting) options.Add(new(CancelPlanLabel, ApprovalOptionKind.Destructive, GlyphCancelPlan));

        var request = new ApprovalRequest
        {
            Title = $"Allow MCP tool \"{toolName}\" from \"{serverName}\"?",
            Detail = description,
            ToastSummary = $"Wants to run tool \"{toolName}\"",
            Options = options
        };

        var choice = await RequireUi().ShowApprovalAsync(request);

        DiffApprovalResult result;
        if (choice == ApproveLabel)
        {
            _transcript.Append(_html.Success("Approved."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }
        else if (choice == noAskMcpLabel)
        {
            _transcript.Append(_html.Success("Approved for session."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain };
        }
        else if (choice == CancelPlanLabel)
        {
            _transcript.Append(_html.Error("Plan cancellation requested."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.CancelPlan };
        }
        else
        {
            _transcript.Append(_html.Error("Denied."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Denied };
        }

        _busy.Start();
        return result;
    }

    /// <summary>
    /// Shows the approval prompt; when the user picks "Provide new instructions" the
    /// text input opens with a Cancel button, and cancelling returns to the original
    /// approval prompt (misclick recovery) instead of committing to the redirect.
    /// Desktop-only affordance — the CLI's flow has no equivalent back-step.
    /// </summary>
    private async Task<(string Choice, string? Instructions)> PromptAllowingInstructionCancelAsync(ApprovalRequest request)
    {
        while (true)
        {
            var choice = await RequireUi().ShowApprovalAsync(request);
            if (choice != ProvideInstructionsLabel) return (choice, null);

            var instructions = await RequireUi().ShowInstructionInputAsync(
                "Enter your instructions:", allowCancel: true);
            if (instructions != ApprovalSignals.Cancelled) return (choice, instructions);
        }
    }

    private IApprovalUi RequireUi() =>
        Ui ?? throw new InvalidOperationException("Approval UI not attached yet.");
}
