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
            _transcript.Append(_html.Success("✓ Auto-approved"));
            _busy.Start();
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var noAskLabel = isNewFile
            ? ApproveNoAskWriteLabel
            : $"Approve - Don't ask again to modify {fileName}";

        var options = new List<ApprovalOption>
        {
            new(ApproveLabel, ApprovalOptionKind.Proceed),
            new(noAskLabel, ApprovalOptionKind.Proceed),
            new(DenyLabel, ApprovalOptionKind.Redirect),
            new(ProvideInstructionsLabel, ApprovalOptionKind.Redirect)
        };
        if (_planHandoff.IsExecuting) options.Add(new(CancelPlanLabel, ApprovalOptionKind.Destructive));

        var request = new ApprovalRequest
        {
            Title = "Apply these changes?",
            Subtitle = relativePath,
            DiffLines = displayLines,
            DiffSummary = summary,
            Options = options
        };

        var choice = await RequireUi().ShowApprovalAsync(request);

        DiffApprovalResult result;
        if (choice == ApproveLabel)
        {
            _transcript.Append(_html.Success("Changes approved."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }
        else if (choice == noAskLabel)
        {
            _transcript.Append(_html.Success("Changes approved."));
            if (isNewFile)
            {
                _globalWriteBypass = true;
                _transcript.Append(_html.Dim("All future writes will be auto-approved for this session."));
            }
            else
            {
                _approvedFiles.Add(relativePath);
                _transcript.Append(_html.Dim($"Future modifications to {fileName} will be auto-approved."));
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
            var instructions = await RequireUi().ShowInstructionInputAsync("Enter your instructions:");
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
            _transcript.Append(_html.Success("✓ Auto-approved command"));
            _busy.Start();
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var options = new List<ApprovalOption>
        {
            new(ApproveLabel, ApprovalOptionKind.Proceed),
            new(ApproveNoAskRunLabel, ApprovalOptionKind.Proceed),
            new(DenyLabel, ApprovalOptionKind.Redirect),
            new(ProvideInstructionsLabel, ApprovalOptionKind.Redirect)
        };
        if (_planHandoff.IsExecuting) options.Add(new(CancelPlanLabel, ApprovalOptionKind.Destructive));

        var request = new ApprovalRequest
        {
            Title = "Run this command?",
            CommandText = command,
            Options = options
        };

        var choice = await RequireUi().ShowApprovalAsync(request);

        DiffApprovalResult result;
        if (choice == ApproveLabel)
        {
            _transcript.Append(_html.Success("Command approved."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }
        else if (choice == ApproveNoAskRunLabel)
        {
            _transcript.Append(_html.Success("Command approved."));
            _globalWriteBypass = true;
            _transcript.Append(_html.Dim("All future writes, deletions, and commands will be auto-approved for this session."));
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
            var instructions = await RequireUi().ShowInstructionInputAsync("Enter your instructions:");
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
            _transcript.Append(_html.Success("✓ Auto-approved deletion"));
            _busy.Start();
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var options = new List<ApprovalOption>
        {
            new(ApproveDeletionLabel, ApprovalOptionKind.Proceed),
            new(ApproveNoAskDeleteLabel, ApprovalOptionKind.Proceed),
            new(DenyLabel, ApprovalOptionKind.Redirect),
            new(ProvideInstructionsLabel, ApprovalOptionKind.Redirect)
        };
        if (_planHandoff.IsExecuting) options.Add(new(CancelPlanLabel, ApprovalOptionKind.Destructive));

        var request = new ApprovalRequest
        {
            Title = "Delete this file?",
            Subtitle = relativePath,
            Detail = warning,
            DiffLines = displayLines,
            Options = options
        };

        var choice = await RequireUi().ShowApprovalAsync(request);

        DiffApprovalResult result;
        if (choice == ApproveDeletionLabel)
        {
            _transcript.Append(_html.Success("Deletion approved."));
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }
        else if (choice == ApproveNoAskDeleteLabel)
        {
            _transcript.Append(_html.Success("Deletion approved."));
            _globalWriteBypass = true;
            _transcript.Append(_html.Dim("All future writes and deletions will be auto-approved for this session."));
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
            var instructions = await RequireUi().ShowInstructionInputAsync("Enter your instructions:");
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
            _transcript.Append(_html.Success("✓ Auto-approved MCP tool"));
            _busy.Start();
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var noAskMcpLabel = $"Approve - don't ask again for {toolName} this session";
        var options = new List<ApprovalOption>
        {
            new(ApproveLabel, ApprovalOptionKind.Proceed),
            new(noAskMcpLabel, ApprovalOptionKind.Proceed),
            new(DenyLabel, ApprovalOptionKind.Redirect)
        };
        if (_planHandoff.IsExecuting) options.Add(new(CancelPlanLabel, ApprovalOptionKind.Destructive));

        var request = new ApprovalRequest
        {
            Title = $"Allow MCP tool \"{toolName}\" from \"{serverName}\"?",
            Detail = description,
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

    private IApprovalUi RequireUi() =>
        Ui ?? throw new InvalidOperationException("Approval UI not attached yet.");
}
