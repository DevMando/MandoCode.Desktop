using System.Text;
using System.Text.RegularExpressions;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Desktop.Services;
using ModelContextProtocol.Client;

namespace MandoCode.Desktop.ViewModels;

/// <summary>
/// The WinUI orchestrator — a faithful port of App.razor's interactive loop, wired to
/// XAML events instead of a console read loop. Owns request lifecycle/cancellation,
/// slash-command dispatch, @file expansion, the plan handoff, and function-call
/// feedback. All output flows through TranscriptWriter; busy state through
/// BusyStateService; approvals through WinUiApprovalService.
/// </summary>
public sealed partial class ChatController
{
    private readonly AIService _ai;
    private readonly MandoCodeConfig _config;
    private readonly TokenTrackingService _tokenTracker;
    private readonly PlanHandoff _planHandoff;
    private readonly TaskPlannerService _taskPlanner;
    private readonly McpClientManager _mcpManager;
    private readonly McpApprovalGate _mcpGate;
    private readonly SkillLoader _skills;
    private readonly FileAutocompleteProvider _fileProvider;
    private readonly ProjectRootAccessor _projectRoot;
    private readonly MusicPlayerService _music;
    private readonly UiUpdateCheckService _updateCheck;
    private readonly WinUiApprovalService _approvals;
    private readonly TranscriptWriter _transcript;
    private readonly TranscriptHtmlBuilder _html;
    private readonly BusyStateService _busy;
    private readonly ShellRunner _shell;
    private readonly ApprovalPromptGate _promptGate;
    private readonly ConfigCoordinator _configs;
    private readonly McpCoordinator _mcp;
    private readonly SnapshotStore _snapshots;

    /// <summary>Recaps armed by "Import" — prepended (invisibly) to this agent's next message.
    /// There is no public harness API to inject a message into a fresh conversation, so we ride them
    /// along on the next send instead. Multiple imports ACCUMULATE (each is a distinct past
    /// conversation), and all ride along together on the next send.</summary>
    private readonly List<string> _armedContexts = new();
    private readonly HashSet<int> _armedSnapshotIds = new();   // dedupe: don't queue the same snapshot twice

    /// <summary>Emoji reactions clicked on transcript responses since the last send. Same
    /// ride-along mechanism as <see cref="_armedContexts"/>: folded into the next message's
    /// preamble and cleared once delivered. CardId is the transcript's per-response counter;
    /// the snippet tells the model WHICH response was reacted to.</summary>
    private readonly List<(string CardId, string Emoji, string Snippet)> _pendingReactions = new();

    /// <summary>Reaction chip toggled on in the transcript (re-clicking a different chip on
    /// the same response accumulates — multiple reactions per response are fine).</summary>
    public void AddReaction(string cardId, string emoji, string snippet)
    {
        _pendingReactions.RemoveAll(r => r.CardId == cardId && r.Emoji == emoji);
        _pendingReactions.Add((cardId, emoji, snippet));
    }

    /// <summary>Reaction chip toggled off. Only pending (undelivered) reactions can be
    /// retracted; once sent with a turn it's already part of the conversation.</summary>
    public void RemoveReaction(string cardId, string emoji) =>
        _pendingReactions.RemoveAll(r => r.CardId == cardId && r.Emoji == emoji);

    /// <summary>Workspace events the model can't see happen (the user discarding its edits
    /// via the undo button, files changing outside the app, external branch switches). Same
    /// ride-along mechanism as reactions: folded into the next message's preamble so the
    /// model's picture of the working tree stays true. Capped — if the queue somehow runs
    /// away, the oldest facts are the most likely to be stale anyway.</summary>
    private readonly List<string> _pendingWorkspaceNotes = new();

    /// <summary>Set by AgentSession: receives ("u"/"a", text) for every conversational turn,
    /// feeding the per-session ConversationLog that re-briefs the model after a restart.
    /// Only real dialogue is logged — /commands and !shell lines are not conversation.</summary>
    public Action<string, string>? ConversationLogger { get; set; }

    /// <summary>Arms a restored previous-session conversation to ride the next send as
    /// imported background — the automatic counterpart of importing a snapshot, used by
    /// session restore (tier 3).</summary>
    public void ArmRestoredConversation(string context) => _armedContexts.Add(context);

    public void NoteWorkspaceEvent(string note)
    {
        if (_pendingWorkspaceNotes.Count >= 30) _pendingWorkspaceNotes.RemoveAt(0);
        _pendingWorkspaceNotes.Add(note);
    }

    /// <summary>Queues a note describing a shell command the USER ran (`!cmd` / /command),
    /// output capped so a build log can't flood the preamble.</summary>
    private void NoteShellCommand(string cmd, bool failed, string output)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return;
        var snippet = output.Length > 400 ? output[..400] + "… [truncated]" : output;
        NoteWorkspaceEvent(
            $"The user ran a shell command themselves: `{cmd}` ({(failed ? "FAILED" : "succeeded")}). Output:\n{snippet}");
    }

    private CancellationTokenSource? _requestCts;
    private bool _isProcessing;

    // Operation tracking for contextual busy messages (port of App.razor fields)
    private int _recentReadCount;
    private readonly List<string> _recentReadFiles = new();
    private string? _lastOperationType;

    private string? _lastAiResponse;

    private const int MaxFileReferenceBudgetChars = 60_000;

    public bool IsConnected { get; private set; }
    public bool ModelError { get; private set; }
    public string? ModelWarning { get; private set; }
    public bool IsProcessing => _isProcessing;
    public string ModelName => _config.GetEffectiveModelName();
    public string ProjectRootPath => _projectRoot.ProjectRoot;
    public MandoCodeConfig Config => _config;

    /// <summary>Status bar refresh (connection, model, tokens). May fire on any thread.</summary>
    public event Action? StateChanged;

    /// <summary>Plan execution progress: (completedSteps, totalSteps, active).</summary>
    public event Action<int, int, bool>? PlanProgressChanged;

    /// <summary>The UI should open the Settings page (first run, not connected, or /config).</summary>
    public event Action? SetupNeeded;

    /// <summary>The UI should put this text on the clipboard (must marshal to UI thread).</summary>
    public event Action<string>? ClipboardCopyRequested;

    /// <summary>/exit — the UI should close the window.</summary>
    public event Action? ExitRequested;

    /// <summary>The UI should open the MCP server editor modal (null = add new,
    /// otherwise the name of the server to edit).</summary>
    public event Action<string?>? McpEditorRequested;

    public ChatController(
        AIService ai,
        MandoCodeConfig config,
        TokenTrackingService tokenTracker,
        PlanHandoff planHandoff,
        TaskPlannerService taskPlanner,
        McpClientManager mcpManager,
        McpApprovalGate mcpGate,
        SkillLoader skills,
        FileAutocompleteProvider fileProvider,
        ProjectRootAccessor projectRoot,
        MusicPlayerService music,
        UiUpdateCheckService updateCheck,
        WinUiApprovalService approvals,
        TranscriptWriter transcript,
        TranscriptHtmlBuilder html,
        BusyStateService busy,
        ShellRunner shell,
        ApprovalPromptGate promptGate,
        ConfigCoordinator configs,
        McpCoordinator mcp,
        SnapshotStore snapshots)
    {
        _ai = ai;
        _config = config;
        _tokenTracker = tokenTracker;
        _planHandoff = planHandoff;
        _taskPlanner = taskPlanner;
        _mcpManager = mcpManager;
        _mcpGate = mcpGate;
        _skills = skills;
        _fileProvider = fileProvider;
        _projectRoot = projectRoot;
        _music = music;
        _updateCheck = updateCheck;
        _approvals = approvals;
        _transcript = transcript;
        _html = html;
        _busy = busy;
        _shell = shell;
        _promptGate = promptGate;
        _configs = configs;
        _mcp = mcp;
        _snapshots = snapshots;

        // ---- Same wiring as App.razor OnInitialized ----
        // Every delegate below is SINGLE-ASSIGNMENT, not multicast. That's safe only because
        // _ai, _planHandoff, and _mcpGate are this tab's own instances (see AgentSession) —
        // on shared services the last tab constructed would silently steal every approval.
        _ai.OnFunctionInvoked += OnFunctionInvoked;
        _ai.OnFunctionCompleted += OnFunctionCompleted;
        _planHandoff.OnPlanRequested = HandleProposedPlanAsync;

        WireApprovalHandlers();

        _tokenTracker.OnTokensUpdated += _ => StateChanged?.Invoke();
    }

    /// <summary>
    /// Attaches or detaches this agent's approval handlers to match its own
    /// <c>EnableDiffApprovals</c>. The CLI marks that key "restart required" because it wires
    /// the delegates once at startup against a shared AIService; here every agent owns its own,
    /// so the toggle can apply live and independently per tab.
    /// </summary>
    private void WireApprovalHandlers()
    {
        if (_config.EnableDiffApprovals)
        {
            _ai.OnWriteApprovalRequested = _approvals.HandleWriteApprovalAsync;
            _ai.OnDeleteApprovalRequested = _approvals.HandleDeleteApprovalAsync;
            _ai.OnCommandApprovalRequested = _approvals.HandleCommandApprovalAsync;
            _mcpGate.OnApprovalRequested = _approvals.HandleMcpApprovalAsync;
        }
        else
        {
            _ai.OnWriteApprovalRequested = null;
            _ai.OnDeleteApprovalRequested = null;
            _ai.OnCommandApprovalRequested = null;
            _mcpGate.OnApprovalRequested = null;
        }
    }

    /// <summary>Applies a just-changed setting to THIS agent. Kernel-baked keys rebuild its
    /// kernel; history survives. Nothing touches other agents or the disk.</summary>
    private async Task ApplyAgentSettingScopeAsync(ConfigKeySetter.ApplyScope scope)
    {
        WireApprovalHandlers();

        // Turning MCP on for this agent has to be able to start the shared servers. They're only
        // started at connect time, and an agent that booted with MCP off never asked for them —
        // without this, the toggle would flip and attach nothing. Idempotent once running.
        if (_config.EnableMcp && _config.McpServers.Count > 0)
            await _mcp.EnsureStartedAsync();

        if (scope is ConfigKeySetter.ApplyScope.KernelRebuild or ConfigKeySetter.ApplyScope.AppRestart)
            await _ai.RefreshSettingsAsync(_config);
    }

    /// <summary>
    /// "Make Default for New Agents" — snapshots this agent's settings onto the saved defaults.
    /// Agents already open keep their own; only the next one you open inherits these.
    /// </summary>
    public void SaveAsDefaults() => _configs.SaveDefaultsFrom(_config);

    // ============================================================
    // Startup (port of InitializeConnectionAsync)
    // ============================================================

    public async Task InitializeAsync()
    {
        var probe = await OllamaSetupHelper.ProbeAsync(_config.OllamaEndpoint);
        if (probe.WasHealed)
        {
            // A trailing-slash heal is a correction, not a preference — fix this agent AND the
            // saved defaults, so every future agent starts on the working URL.
            _config.OllamaEndpoint = probe.NormalizedUrl;
            _configs.Defaults.OllamaEndpoint = probe.NormalizedUrl;
            _configs.SaveDefaults();
            _transcript.Append(_html.Dim($"Detected trailing slash on Ollama URL — using {probe.NormalizedUrl}"));
        }

        IsConnected = probe.Ok;

        var shouldSetup = MandoCodeConfig.IsFirstRun() || (!probe.Ok && !_config.HasCompletedOnboarding);
        if (!probe.Ok)
        {
            _transcript.Append(_html.Warn($"Can't reach Ollama at {_config.OllamaEndpoint}."));
            _transcript.Append(_html.Dim("Open Settings (gear icon) to set the endpoint and model, make sure 'ollama serve' is running, then hit Reconnect — or type /retry."));
        }
        if (shouldSetup) SetupNeeded?.Invoke();

        if (IsConnected)
        {
            await ValidateCurrentModelAsync();
            await InitializeMcpAsync();
            if (!ModelError)
                _transcript.Append(_html.StatusChip(ModelName, "ready", "ok"));
        }

        StateChanged?.Invoke();

        // Fire-and-forget update check against MandoCode.Desktop's own GitHub releases —
        // NOT the MandoCode CLI NuGet package (the two version independently).
        // Self-throttled to once/24h, fail-silent.
        _ = Task.Run(async () =>
        {
            try
            {
                var update = await _updateCheck.CheckForUpdateAsync();
                if (update != null)
                {
                    _transcript.Append(_html.Warn($"✨ MandoCode Desktop {update.LatestVersion} is available (you have {update.CurrentVersion})"));
                    _transcript.Append(_html.Link("Download the latest release", update.DownloadUrl));
                }
            }
            catch { /* never disrupt the session */ }
        });
    }

    private async Task ValidateCurrentModelAsync()
    {
        var (isValid, errorMessage) = await _ai.ValidateModelAsync();
        if (!isValid && errorMessage != null)
        {
            _transcript.Append(_html.Warn(errorMessage));
            if (errorMessage.Contains("Continue anyway?"))
            {
                // CLI asks a confirm; the desktop build proceeds in degraded mode with a
                // visible warning — the user can switch models any time via /model.
                ModelError = false;
                ModelWarning = errorMessage;
                _transcript.Append(_html.Dim("Continuing anyway — function calling may be unreliable with this model. Use /model to switch."));
            }
            else
            {
                ModelError = true;
                ModelWarning = errorMessage;
                _transcript.Append(_html.Dim("Fix: pull the model (ollama pull <model-name>), or use /model to select a different one."));
            }
        }
        else
        {
            ModelError = false;
            ModelWarning = null;
        }
        StateChanged?.Invoke();
    }

    private async Task InitializeMcpAsync()
    {
        if (!_config.EnableMcp || _config.McpServers.Count == 0) return;

        try
        {
            // The servers are process-wide and start once for the app; only the tool
            // registration is per-tab, so a newly opened tab attaches the already-running
            // clients to its own kernel.
            await _mcp.EnsureStartedAsync();
            await _ai.AttachMcpPluginsAsync();

            var active = _mcpManager.ActiveClients.Count;
            var failed = _mcpManager.StartupErrors.Count;
            if (active > 0 || failed > 0)
            {
                _transcript.Append(failed == 0
                    ? _html.StatusChip("MCP", $"{active} server{(active == 1 ? "" : "s")} connected", "ok")
                    : _html.StatusChip("MCP", $"{active} connected, {failed} failed — type /mcp for details", "warn"));
            }
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Warn($"MCP startup error: {ex.Message}"));
        }
    }

    // ============================================================
    // Input dispatch (port of RunInteractiveLoopAsync body)
    // ============================================================

    /// <summary>Handles one submitted input. Returns immediately if a request is running.</summary>
    public async Task SubmitAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || _isProcessing) return;

        _isProcessing = true;
        StateChanged?.Invoke();
        try
        {
            _transcript.Append(_html.UserEcho(input));

            // Shell escape: !<cmd>. The card renders only in the transcript, which the model
            // never sees — so queue a workspace note too, or a user-run `git commit` / build
            // is invisible to the agent (it would have to re-discover the state itself).
            if (input.TrimStart().StartsWith('!'))
            {
                var cmd = input.TrimStart()[1..].Trim();
                var (failed, output) = await _shell.RunAsync(cmd);
                NoteShellCommand(cmd, failed, output);
                return;
            }

            if (InputStateMachine.IsCommand(input))
            {
                await DispatchCommandAsync(input);
                return;
            }

            if (!IsConnected)
            {
                _transcript.Append(_html.Warn("Not connected to Ollama."));
                _transcript.Append(_html.Dim("Make sure 'ollama serve' is running, then type /retry — or open Settings to change the endpoint."));
                return;
            }

            if (ModelError)
            {
                _transcript.Append(_html.Warn("No model loaded. Use /model to select one, or pull one with: ollama pull <model-name>"));
                return;
            }

            ConversationLogger?.Invoke("u", input);

            // Plan heuristic BEFORE @file expansion so attachments don't inflate it.
            var needsPlanning = _taskPlanner.RequiresPlanning(input);
            var processedInput = ProcessFileReferences(input);

            // Imported snapshots (from "Import" in the Snapshots panel) ride along ONCE, as background
            // the model already knows — the user's echoed message stays their own text. Multiple
            // imports accumulate and are all sent together, each kept as a distinct recap.
            if (_armedContexts.Count > 0)
            {
                var noun = _armedContexts.Count == 1 ? "recap" : "recaps";
                processedInput =
                    $"[Imported context — {_armedContexts.Count} {noun} from previous conversations. " +
                    "Treat as background you already have; do not reply to it directly.]\n" +
                    string.Join("\n\n", _armedContexts) +
                    "\n\n[Current request:]\n" + processedInput;
                _armedContexts.Clear();
                _armedSnapshotIds.Clear();   // a new batch can re-import the same snapshots next time
            }

            // Emoji reactions ride along the same way — factual feedback the model can steer
            // on, clearly framed as metadata so it's never mistaken for typed text.
            if (_pendingReactions.Count > 0)
            {
                var lines = string.Join("\n", _pendingReactions.Select(r =>
                    $"- {r.Emoji} on your response beginning: “{r.Snippet}”"));
                processedInput =
                    "[The user reacted to earlier responses with emoji — a feedback signal, not text they typed:]\n" +
                    lines +
                    "\n\n[Current request:]\n" + processedInput;
                _pendingReactions.Clear();
            }

            // Workspace changes the model didn't make and can't see. Framed as facts (not
            // instructions) with an explicit staleness warning, so the model re-reads rather
            // than trusting its memory of file contents.
            if (_pendingWorkspaceNotes.Count > 0)
            {
                var notes = string.Join("\n", _pendingWorkspaceNotes.Select(n => "- " + n));
                processedInput =
                    "[Workspace changes since your last turn, made outside this conversation. " +
                    "Your memory of affected file contents may be stale — re-read before relying on it:]\n" +
                    notes +
                    "\n\n[Current request:]\n" + processedInput;
                _pendingWorkspaceNotes.Clear();
            }

            if (needsPlanning)
            {
                processedInput += "\n\n[system: this request looks multi-step. " +
                                  "Call propose_plan now with the breakdown before doing any work.]";
            }

            await ProcessDirectRequestAsync(processedInput);
        }
        finally
        {
            _isProcessing = false;
            _busy.Reset();
            StateChanged?.Invoke();
        }
    }

    public void CancelActiveRequest()
    {
        var cts = _requestCts;
        if (cts != null && !cts.IsCancellationRequested) cts.Cancel();
    }

    // ============================================================
    // Direct AI request (port of ProcessDirectRequestAsync)
    // ============================================================

    private async Task ProcessDirectRequestAsync(string input)
    {
        _recentReadCount = 0;
        _recentReadFiles.Clear();
        _lastOperationType = null;

        _requestCts = new CancellationTokenSource();
        var token = _requestCts.Token;

        try
        {
            var receivedFirstChunk = false;
            var enumerator = _ai.ChatStreamAsync(input, token).GetAsyncEnumerator(token);

            try
            {
                _busy.Start("Thinking...");

                if (await enumerator.MoveNextAsync())
                    receivedFirstChunk = true;

                if (!receivedFirstChunk)
                {
                    _busy.Stop();
                    _transcript.Append(_html.Warn("No response from model. The request may have exceeded the model's context window."));
                    _transcript.Append(_html.Dim("Try a smaller request, or switch to a model with a larger context window via /config set."));
                    return;
                }

                // Each element is one completed chat turn — auto-continuations and
                // post-approval turns arrive as additional elements. Flush every turn
                // to the transcript as it completes so its text lands next to the
                // approval/diff cards it belongs with, instead of every turn coalescing
                // into a single card after the final one.
                var segments = new List<string>();
                do
                {
                    var segment = enumerator.Current.Trim();
                    if (segment.Length > 0)
                    {
                        segments.Add(segment);
                        _transcript.Append(_html.AssistantCard(segment));
                        ConversationLogger?.Invoke("a", segment);
                    }
                } while (await enumerator.MoveNextAsync());

                _busy.Stop();

                if (segments.Count == 0)
                {
                    _transcript.Append(_html.Warn("Model returned an empty response. The context may be too large for this model."));
                    _transcript.Append(_html.Dim("Try a smaller request, or switch to a model with a larger context window."));
                }
                else
                {
                    var responseText = string.Join("\n\n", segments);
                    _lastAiResponse = responseText;

                    if (Looks401(responseText))
                    {
                        // 401 auto-recovery — same intent as the CLI's TryAutoSigninAfter401Async:
                        // offer the sign-in walkthrough right here instead of making the user
                        // type /setup and re-navigate to it.
                        await OfferCloudSigninAsync();
                    }

                    if (_config.EnableTokenTracking)
                    {
                        var lastOp = _tokenTracker.LastOperation;
                        if (lastOp != null && !lastOp.IsEstimate)
                        {
                            var tps = lastOp.TokensPerSecond.HasValue ? $": {lastOp.TokensPerSecond.Value:0.#} tok/s" : "";
                            _transcript.Append(_html.TokenSummary(
                                $"[~{TokenTrackingService.FormatTokenCount(lastOp.PromptTokens)} in, " +
                                $"{TokenTrackingService.FormatTokenCount(lastOp.CompletionTokens)} out{tps}]"));
                        }
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }
        catch (OperationCanceledException)
        {
            _transcript.Append(_html.Warn("Request cancelled."));
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Error($"Error: {ex.Message}"));
        }
        finally
        {
            _busy.Reset();
            var oldCts = Interlocked.Exchange(ref _requestCts, null);
            oldCts?.Dispose();
            StateChanged?.Invoke();
        }
    }

    private static bool Looks401(string responseText)
        => !string.IsNullOrEmpty(responseText)
           && responseText.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase);

    // ============================================================
    // @file references (port of ProcessFileReferences)
    // ============================================================

    private string ProcessFileReferences(string input)
    {
        var pattern = @"@([\w.\-/\\]+[\w.])";
        var matches = Regex.Matches(input, pattern);
        if (matches.Count == 0) return input;

        var contextBlocks = new StringBuilder();
        var referencedFiles = new HashSet<string>();
        int totalExpansionChars = 0;
        int skippedForBudget = 0;

        foreach (Match match in matches)
        {
            var filePath = match.Groups[1].Value;
            if (!referencedFiles.Add(filePath)) continue;

            if (totalExpansionChars >= MaxFileReferenceBudgetChars)
            {
                skippedForBudget++;
                continue;
            }

            var content = _fileProvider.ReadFileContent(filePath);
            if (content != null)
            {
                if (content.Length > 10_000)
                    content = content[..10_000] + "\n... [truncated]";

                contextBlocks.AppendLine($"File: {filePath}");
                contextBlocks.AppendLine("```");
                contextBlocks.AppendLine(content);
                contextBlocks.AppendLine("```");
                contextBlocks.AppendLine();
                totalExpansionChars += content.Length;

                if (_config.EnableTokenTracking)
                    _tokenTracker.RecordEstimatedUsage(content.Length, $"@{filePath}");

                _transcript.Append(_html.OperationCard(new OperationDisplayEvent
                {
                    OperationType = "Read",
                    FilePath = filePath,
                    LineCount = content.Split('\n').Length
                }));
            }
            else
            {
                var dirListing = _fileProvider.GetDirectoryListing(filePath);
                if (dirListing != null)
                {
                    if (dirListing.Length > 4_000)
                        dirListing = dirListing[..4_000] + "\n... [truncated]";

                    contextBlocks.AppendLine($"Directory: {filePath}/");
                    contextBlocks.AppendLine("Contents:");
                    contextBlocks.AppendLine(dirListing);
                    contextBlocks.AppendLine();
                    totalExpansionChars += dirListing.Length;

                    _transcript.Append(_html.Dim($"[Directory] {filePath}/"));
                }
                else
                {
                    _transcript.Append(_html.Warn($"[Not found] {filePath}"));
                }
            }
        }

        if (contextBlocks.Length == 0) return input;

        if (skippedForBudget > 0)
        {
            contextBlocks.AppendLine($"[Note: {skippedForBudget} additional @reference(s) were skipped — the total expansion budget of {MaxFileReferenceBudgetChars:N0} chars was reached. Re-reference specific files in follow-up messages if needed.]");
            contextBlocks.AppendLine();
            _transcript.Append(_html.Warn($"[Skipped] {skippedForBudget} @reference(s) beyond expansion budget ({MaxFileReferenceBudgetChars:N0} chars)"));
        }

        var fullContext = new StringBuilder();
        fullContext.AppendLine("--- Referenced Files ---");
        fullContext.Append(contextBlocks);
        fullContext.AppendLine("---");
        fullContext.AppendLine();
        fullContext.Append(input);
        return fullContext.ToString();
    }

    // ============================================================
    // Function call feedback (port of OnFunctionInvoked/Completed)
    // ============================================================

    private void OnFunctionInvoked(FunctionCall call)
    {
        _lastOperationType = call.FunctionName.Replace("FileSystem_", "").ToLowerInvariant();

        if (!call.FunctionName.StartsWith("FileSystem_", StringComparison.Ordinal))
            _transcript.Append(_html.ToolChip(DescribeToolCall(call)));

        _busy.Update(call.Description);
    }

    /// <summary>Label for a tool-call pill. For a skill load, surfaces WHICH skill (the useful bit)
    /// instead of the generic "Skills · load_skill"; everything else keeps its normal description.</summary>
    private static string DescribeToolCall(FunctionCall call)
    {
        if (call.FunctionName.EndsWith("load_skill", StringComparison.OrdinalIgnoreCase)
            && call.Arguments.TryGetValue("name", out var nameObj)
            && nameObj?.ToString() is { Length: > 0 } skillName)
        {
            return $"Skill: {skillName.Trim()}";
        }

        return call.Description;
    }

    private void OnFunctionCompleted(FunctionExecutionResult result)
    {
        if (result.OperationDisplay != null)
        {
            _transcript.Append(_html.OperationCard(result.OperationDisplay));

            if (result.OperationDisplay.OperationType == "Read")
            {
                _recentReadCount++;
                var fileName = Path.GetFileName(result.OperationDisplay.FilePath);
                if (!string.IsNullOrEmpty(fileName)) _recentReadFiles.Add(fileName);
            }
            else
            {
                _recentReadCount = 0;
                _recentReadFiles.Clear();
            }
        }
        else if (!result.Success)
        {
            // No completion pill for success (the invoke pill is the whole record, and recoloring it
            // in place is what broke things). A failure still surfaces its detail line.
            _transcript.Append(_html.Error($"[Error] {result.Result}"));
        }

        var fnLower = result.FunctionName.ToLowerInvariant();
        if (fnLower.Contains("write") || fnLower.Contains("delete") || fnLower.Contains("create") || fnLower.Contains("folder"))
            _fileProvider.RefreshCache();

        _busy.Update(GetContextualActivity(result));
    }

    private string? GetContextualActivity(FunctionExecutionResult result)
    {
        var opType = result.OperationDisplay?.OperationType ?? _lastOperationType ?? "";
        return opType switch
        {
            "Read" when _recentReadCount >= 2 => $"Analyzing {_recentReadCount} files...",
            "Read" => "Analyzing file...",
            "Write" or "Update" => "Applying changes...",
            "Search" => "Reviewing search results...",
            "Glob" => "Scanning project structure...",
            "WebSearch" => "Processing search results...",
            "WebFetch" => "Reading web content...",
            "Delete" => "Continuing...",
            "Command" => "Processing command output...",
            _ => null
        };
    }

    // ============================================================
    // Plan handoff (port of HandleProposedPlanAsync + HandleProgressEvent)
    // ============================================================

    private const string ExecutePlanLabel = "Execute plan";
    private const string RejectPlanLabel = "One-shot it";
    private const string CancelRequestLabel = "Cancel request";
    private const string SkipStepLabel = "Skip this step and continue";
    private const string CancelPlanLabel = "Cancel the plan";

    private async Task<string> HandleProposedPlanAsync(TaskPlan plan, CancellationToken ct)
    {
        var ui = _approvals.Ui ?? throw new InvalidOperationException("Approval UI not attached yet.");

        string choice;
        // Serialize against diff/command/MCP prompts, held ONLY around the prompt —
        // holding it across ExecutePlanAsync would deadlock the first step's approval.
        using (await _promptGate.AcquireAsync(ct))
        {
            _busy.Stop();
            _transcript.Append(_html.PlanCard(plan));

            choice = await ui.ShowApprovalAsync(new ApprovalRequest
            {
                Title = "The assistant proposes this plan. What would you like to do?",
                // Bottom bar, not the centered modal — the plan card above stays readable.
                BottomBar = true,
                ToastSummary = "Wants to run a proposed plan",
                Options = new[]
                {
                    new ApprovalOption(ExecutePlanLabel, ApprovalOptionKind.Proceed),
                    new ApprovalOption(RejectPlanLabel, ApprovalOptionKind.Redirect,
                        Description: "Skip the step-by-step plan — the model attempts the whole request in one shot."),
                    // Destructive (red) so the hard "stop" reads differently from "one-shot it".
                    new ApprovalOption(CancelRequestLabel, ApprovalOptionKind.Destructive)
                }
            }, ct);

            _transcript.Append(_html.UserEcho(choice));
        }

        if (choice == CancelRequestLabel)
        {
            _transcript.Append(_html.Dim("Request cancelled — stopping here."));
            // Cancel the request token so the turn mechanically unwinds — the return
            // string alone is a polite request small models ignore (see App.razor).
            _requestCts?.Cancel();
            return "User cancelled the request. Stop here and do not take further action.";
        }

        if (choice == RejectPlanLabel)
        {
            _transcript.Append(_html.Dim("Plan skipped — one-shotting your request."));
            return "User declined the step-by-step plan and wants you to one-shot it: attempt the full "
                 + "request in a single pass. Do not call propose_plan again.";
        }

        _transcript.Append(_html.Success("Executing plan..."));
        PlanProgressChanged?.Invoke(0, plan.Steps.Count, true);

        try
        {
            await foreach (var progressEvent in _taskPlanner.ExecutePlanAsync(plan, ct))
            {
                await HandleProgressEventAsync(progressEvent, plan, ui);
            }
        }
        catch (OperationCanceledException)
        {
            _busy.Stop();
            _taskPlanner.CancelPlan(plan);
        }
        finally
        {
            PlanProgressChanged?.Invoke(plan.CompletedStepsCount, plan.Steps.Count, false);
        }

        if (plan.Status == TaskPlanStatus.Completed)
            _transcript.Append(_html.Success("Plan completed successfully!"));
        else if (plan.Status == TaskPlanStatus.Cancelled)
            _transcript.Append(_html.Warn("Plan was cancelled."));
        else
            _transcript.Append(_html.Error("Plan completed with errors."));

        if (!string.IsNullOrEmpty(plan.ExecutionSummary))
            _transcript.Append(_html.Dim(plan.ExecutionSummary));

        var summary = plan.ExecutionSummary ?? $"Plan finished with status {plan.Status}.";
        return summary +
               "\n\nThe plan has finished. Respond to the user now with a brief summary of the outcome. " +
               "Do NOT create more files, call more tools, or propose another plan.";
    }

    private async Task HandleProgressEventAsync(TaskProgressEvent progressEvent, TaskPlan plan, IApprovalUi ui)
    {
        switch (progressEvent.ProgressType)
        {
            case TaskProgressType.StepStarted:
                _recentReadCount = 0;
                _recentReadFiles.Clear();
                _lastOperationType = null;
                PlanProgressChanged?.Invoke(progressEvent.CurrentStep - 1, progressEvent.TotalSteps, true);
                _transcript.Append(_html.StepStarted(progressEvent.CurrentStep, progressEvent.TotalSteps, progressEvent.StepDescription));
                _busy.Update("Working...");
                break;

            case TaskProgressType.StepCompleted:
                PlanProgressChanged?.Invoke(progressEvent.CurrentStep, progressEvent.TotalSteps, true);
                if (!string.IsNullOrEmpty(progressEvent.Message))
                {
                    _transcript.Append(_html.AssistantCard(progressEvent.Message));
                    ConversationLogger?.Invoke("a", progressEvent.Message!);
                }
                _transcript.Append(_html.Success($"Step {progressEvent.CurrentStep} completed."));
                break;

            case TaskProgressType.StepFailed:
                _busy.Stop();
                _transcript.Append(_html.Error($"Step {progressEvent.CurrentStep} failed: {progressEvent.Message ?? "Unknown error"}"));

                // Must resolve before enumeration continues — same semantics as the
                // CLI's blocking prompt: SkipStep/CancelPlan mutate plan.Status before
                // ExecutePlanAsync's next iteration reconciles.
                var failChoice = await ui.ShowApprovalAsync(new ApprovalRequest
                {
                    Title = "How would you like to proceed?",
                    Subtitle = $"Step {progressEvent.CurrentStep} failed",
                    Options = new[]
                    {
                        new ApprovalOption(SkipStepLabel, ApprovalOptionKind.Redirect),
                        new ApprovalOption(CancelPlanLabel, ApprovalOptionKind.Destructive)
                    }
                });

                if (failChoice == CancelPlanLabel)
                {
                    _taskPlanner.CancelPlan(plan);
                }
                else
                {
                    var failedStep = plan.Steps.FirstOrDefault(s => s.StepNumber == progressEvent.CurrentStep);
                    if (failedStep != null) _taskPlanner.SkipStep(plan, failedStep);
                }
                _busy.Start();
                break;

            case TaskProgressType.PlanCompleted:
            case TaskProgressType.PlanCancelled:
                _busy.Stop();
                break;
        }
    }

    // ============================================================
    // Slash commands (port of the RunInteractiveLoopAsync dispatch ladder)
    // ============================================================

    /// <summary>Slash-command suggestions for the input box autocomplete.</summary>
    public IReadOnlyList<(string Command, string Description)> GetCommandSuggestions(string prefix)
    {
        return SlashCommands.All
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    private async Task DispatchCommandAsync(string input)
    {
        var trimmed = input.TrimStart().TrimStart('/').Trim();
        var firstSpace = trimmed.IndexOf(' ');
        var command = (firstSpace < 0 ? trimmed : trimmed[..firstSpace]).ToLowerInvariant();
        var rawArgs = firstSpace < 0 ? "" : trimmed[(firstSpace + 1)..].Trim();

        switch (command)
        {
            case "exit":
                // Closing the window disposes the shared music player and every agent's WebView2.
                // Doing it here would tear down the audio device for agents still open if /exit
                // ever came to mean "close this tab".
                ExitRequested?.Invoke();
                return;

            case "clear":
                await _ai.ClearHistoryAsync();
                _lastAiResponse = null;
                _transcript.Clear();
                _transcript.Append(_html.Dim("Conversation cleared."));
                return;

            case "help":
                ShowHelp();
                return;

            case "config":
                await HandleConfigCommandAsync(rawArgs);
                return;

            case "model":
                await HandleModelCommandAsync(rawArgs);
                return;

            case "setup":
                await RunSetupWizardAsync();
                return;

            case "learn":
                await HandleLearnCommandAsync();
                return;

            case "retry":
                await HandleRetryCommandAsync();
                return;

            case "copy":
                if (string.IsNullOrEmpty(_lastAiResponse))
                    _transcript.Append(_html.Warn("Nothing to copy — no AI response yet."));
                else
                {
                    ClipboardCopyRequested?.Invoke(_lastAiResponse);
                    _transcript.Append(_html.StatusChip("Copied", $"{_lastAiResponse.Length} chars", "ok"));
                }
                return;

            case "copy-code":
                HandleCopyCodeCommand();
                return;

            case "music":            HandleMusicCommand("play"); return;
            case "music-stop":       HandleMusicCommand("stop"); return;
            case "music-pause":      HandleMusicCommand("pause"); return;
            case "music-next":       HandleMusicCommand("next"); return;
            case "music-list":       HandleMusicCommand("list"); return;
            case "music-playlist":   await RunMusicPlaylistPickerAsync(); return;
            case "music-lofi":       HandleMusicGenre("lofi"); return;
            case "music-synthwave":  HandleMusicGenre("synthwave"); return;
            case "music-vol":        HandleMusicVolume(rawArgs); return;

            case "command":
            {
                var (failed, output) = await _shell.RunAsync(rawArgs);
                NoteShellCommand(rawArgs, failed, output);
                return;
            }

            case "skills":
                ShowSkills();
                return;

            case "force-skill":
                await HandleForceSkillCommandAsync(rawArgs);
                return;

            case "mcp":
                await HandleMcpCommandAsync(rawArgs);
                return;

            case "mcp-reload":
                await HandleMcpReloadCommandAsync();
                return;

            default:
                _transcript.Append(_html.Error($"Unknown command: {input}"));
                _transcript.Append(_html.Dim("Type /help for available commands"));
                return;
        }
    }

    private void ShowHelp()
    {
        var rows = new List<(string, string)>
        {
            ("/help", "Show this help message"),
            ("/setup", "Reconnect to Ollama or pick a different model (guided wizard)"),
            ("/model", "Quick switch — pick a different model (/model <tag> skips the picker)"),
            ("/config", "Adjust settings — guided wizard (/config set <key> <value> inline)"),
            ("/retry", "Retry Ollama connection"),
            ("/learn", "Learn about LLMs and local AI models"),
            ("/music", "Play lofi/synthwave coding music"),
            ("/music-stop", "Stop music playback"),
            ("/music-pause", "Pause/resume music"),
            ("/music-next", "Skip to next track"),
            ("/music-vol <0-100>", "Set volume"),
            ("/music-playlist", "Pick a genre to play"),
            ("/music-list", "Show available tracks"),
            ("/command <cmd>", "Run a shell command (also: !<cmd>)"),
            ("/copy", "Copy last AI response to clipboard"),
            ("/copy-code", "Copy code blocks from last response"),
            ("/skills", "List installed skills (auto-invoked when relevant)"),
            ("/force-skill <name>", "Override: force a specific skill to run now"),
            ("/mcp", "List MCP servers with status and tool counts"),
            ("/mcp add", "Interactively add a new MCP server"),
            ("/mcp remove <name>", "Remove an MCP server from config"),
            ("/mcp tools [server]", "List tools exposed by connected MCP servers"),
            ("/mcp-reload", "Restart MCP servers and re-register their tools"),
            ("/clear", "Clear conversation history"),
            ("/exit", "Exit MandoCode")
        };
        _transcript.Append(_html.HelpCard(rows));
        _transcript.Append(_html.Dim("Examples: \"What files are in this project?\" · \"Refactor Main to async/await\" · \"@Program.cs explain this file\""));
    }

    private async Task HandleConfigCommandAsync(string rawArgs)
    {
        if (rawArgs.StartsWith("set", StringComparison.OrdinalIgnoreCase))
        {
            var setArgs = rawArgs[3..].Trim();
            var parts = setArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                _transcript.Append(_html.Info("Usage: /config set <key> <value>"));
                _transcript.Append(_html.Mono(ConfigKeySetter.DescribeKeys(_config)));
                return;
            }

            // Settings are per-agent: this edits THIS agent's config for this session only.
            // Nothing is written to disk — "Make Default for New Agents" on the Settings page
            // is the one action that does that.
            var result = ConfigKeySetter.TrySet(_config, parts[0], parts[1]);
            _transcript.Append(result.Ok ? _html.Success(result.Message) : _html.Error(result.Message));
            if (!result.Ok)
            {
                _transcript.Append(_html.Dim("Run /config set with no arguments to list keys and current values."));
                return;
            }

            await ApplyAgentSettingScopeAsync(result.Scope);

            if (result.PostSetValidation != null)
                _transcript.Append(_html.Dim(await result.PostSetValidation()));

            switch (result.Scope)
            {
                case ConfigKeySetter.ApplyScope.KernelRebuild:
                case ConfigKeySetter.ApplyScope.AppRestart:
                    _transcript.Append(_html.Dim("Applied to this agent — conversation history kept."));
                    break;
                case ConfigKeySetter.ApplyScope.Immediate:
                    _transcript.Append(_html.Dim("Takes effect on your next message."));
                    break;
                case ConfigKeySetter.ApplyScope.DaemonRestart:
                    _transcript.Append(_html.Dim("Applies when MandoCode next starts the Ollama daemon (one daemon serves every agent)."));
                    break;
            }
            _transcript.Append(_html.Dim("This agent only. Settings → \"Make Default for New Agents\" to keep it."));
            StateChanged?.Invoke();
            return;
        }

        if (rawArgs.Length > 0)
        {
            _transcript.Append(_html.Warn("Unknown /config subcommand."));
            _transcript.Append(_html.Dim("Usage: /config (opens the Settings page) or /config set <key> <value>"));
            return;
        }

        _transcript.Append(_html.StatusChip("Settings", "opening…"));
        SetupNeeded?.Invoke();
    }

    /// <summary>
    /// Applies one setting from the Settings page. Routed through ConfigKeySetter.TrySet —
    /// the same validation + apply-scope engine as the CLI and /config set — so the page
    /// can never accept a value the CLI would reject.
    /// </summary>
    public async Task<(bool Ok, string Message)> ApplyConfigKeyAsync(string key, string value)
    {
        // Per-agent, this session only. ConfigKeySetter is the same validation engine the CLI's
        // /config set uses, so the page can never accept a value the CLI would reject.
        var result = ConfigKeySetter.TrySet(_config, key, value);
        if (!result.Ok) return (false, result.Message);

        await ApplyAgentSettingScopeAsync(result.Scope);

        var message = result.Message;
        if (result.PostSetValidation != null)
            message += "  " + await result.PostSetValidation();

        switch (result.Scope)
        {
            case ConfigKeySetter.ApplyScope.KernelRebuild:
            case ConfigKeySetter.ApplyScope.AppRestart:
                message += "  (applied — conversation history kept)";
                break;
            case ConfigKeySetter.ApplyScope.Immediate:
                message += "  (takes effect on your next message)";
                break;
            case ConfigKeySetter.ApplyScope.DaemonRestart:
                message += "  (app-wide — applies when MandoCode next starts the Ollama daemon)";
                break;
        }

        StateChanged?.Invoke();
        return (true, message);
    }

    /// <summary>
    /// Opens the model picker for THIS tab — the same overlay wizard as /model. The pick is
    /// tab-local: it repins this agent's model without touching the saved default or any other
    /// open tab. Driven by the model button in the tab header.
    /// </summary>
    public Task ShowModelPickerAsync() => HandleModelCommandAsync("");

    /// <summary>Result of loading the pulled-model list for the header dropdown.</summary>
    public sealed record ModelListResult(bool Ok, string? Error, IReadOnlyList<string> Models);

    /// <summary>
    /// Loads pulled models for the header's quick-switch dropdown — cloud first, then alphabetical.
    /// Returns an inline error string (rather than writing to the transcript) so the dropdown can
    /// render it in place. The typed <c>/model</c> command keeps using the overlay wizard.
    /// </summary>
    public async Task<ModelListResult> LoadAvailableModelsAsync()
    {
        var probe = await OllamaSetupHelper.ProbeAsync(_config.OllamaEndpoint);
        if (!probe.Ok)
            return new ModelListResult(false, $"Can't reach Ollama at {_config.OllamaEndpoint}.", Array.Empty<string>());

        var fetched = await OllamaSetupHelper.ListModelsWithStatusAsync(probe.NormalizedUrl);
        if (!fetched.Ok)
            return new ModelListResult(false, fetched.Error ?? "Couldn't fetch your model list.", Array.Empty<string>());
        if (fetched.Models.Count == 0)
            return new ModelListResult(false, "No models pulled yet — try: ollama pull qwen2.5-coder:14b", Array.Empty<string>());

        var ordered = fetched.Models
            .OrderBy(m => MandoCodeConfig.IsCloudModel(m) ? 0 : 1)
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ModelListResult(true, null, ordered);
    }

    /// <summary>Applies a model chosen from the header dropdown (tab-local, same effect as <c>/model</c>).</summary>
    public async Task SelectModelAsync(string modelTag)
    {
        if (string.IsNullOrWhiteSpace(modelTag)) return;
        if (string.Equals(modelTag, _config.ModelName, StringComparison.OrdinalIgnoreCase)) return;
        await ApplyModelSwitchAsync(modelTag);
    }

    private async Task HandleModelCommandAsync(string rawArgs)
    {
        var probe = await OllamaSetupHelper.ProbeAsync(_config.OllamaEndpoint);
        if (!probe.Ok)
        {
            _transcript.Append(_html.Warn($"Can't reach Ollama at {_config.OllamaEndpoint}."));
            _transcript.Append(_html.Dim("Type /retry to reconnect, or open Settings."));
            return;
        }

        var fetched = await OllamaSetupHelper.ListModelsWithStatusAsync(probe.NormalizedUrl);
        if (!fetched.Ok)
        {
            _transcript.Append(_html.Warn("Couldn't fetch your model list."));
            _transcript.Append(_html.Dim($"Details: {fetched.Error ?? "unknown error"}"));
            return;
        }
        if (fetched.Models.Count == 0)
        {
            _transcript.Append(_html.Warn("No models pulled yet."));
            _transcript.Append(_html.Dim("Pull one with: ollama pull qwen2.5-coder:14b"));
            return;
        }

        var ordered = fetched.Models
            .OrderBy(m => MandoCodeConfig.IsCloudModel(m) ? 0 : 1)
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? modelTag;
        if (string.IsNullOrWhiteSpace(rawArgs))
        {
            // Same quick-switch picker as the CLI's /model — cloud bubbles to top.
            modelTag = await PickModelAsync(ordered);
            if (modelTag == null)
            {
                _transcript.Append(_html.Dim("Cancelled."));
                return;
            }
        }
        else
        {
            var requested = rawArgs.Split(' ')[0];
            modelTag = ordered.FirstOrDefault(m => m.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (modelTag == null)
            {
                _transcript.Append(_html.Warn($"'{requested}' isn't in your pulled models. Type /model to pick from the list."));
                return;
            }
        }

        await ApplyModelSwitchAsync(modelTag);
    }

    /// <summary>
    /// Sets this agent's model, sizes its context window, rebuilds its kernel, revalidates.
    /// Like every setting, it belongs to this agent for this session — two tabs can run different
    /// models side by side. Nothing is persisted; "Make Default for New Agents" does that.
    /// </summary>
    private async Task ApplyModelSwitchAsync(string modelTag)
    {
        // Buffer the outgoing conversation BEFORE anything clears it. ReinitializeAsync below wipes
        // the live history (a different model mid-history is a different conversation), so this is the
        // one chance to grab it. It is NOT auto-saved — we offer the user a snapshot (summarized by a
        // model of their choice) after the switch; if they ignore the offer, the buffer is discarded.
        var previousModel = _config.GetEffectiveModelName();
        _pending = await BufferConversationAsync(previousModel);
        // Full-fidelity carry: grab the verbatim history too (tool calls included), so the
        // user can choose "keep memory" — continue the SAME conversation on the new model —
        // instead of (or in addition to having the option of) a summarized snapshot.
        _pendingCarryJson = _pending == null ? null : _ai.ExportHistoryJson();

        _config.ModelName = modelTag;
        _config.ModelPath = null;

        var recommendedCtx = MandoCodeConfig.RecommendedContextLength(modelTag);
        if (recommendedCtx > 0 && recommendedCtx != _config.ContextLength)
        {
            _config.ContextLength = recommendedCtx;
            _transcript.Append(_html.Dim($"Context window sized to {recommendedCtx / 1024}k tokens for this model tier (takes effect when MandoCode starts Ollama)."));
        }

        _busy.Start("Switching model...");
        try
        {
            // ReinitializeAsync, not RefreshSettingsAsync: a different model mid-history is a
            // different conversation.
            await _ai.ReinitializeAsync(_config);
            await ValidateCurrentModelAsync();
        }
        finally
        {
            _busy.Reset();
        }

        _transcript.Append(_html.StatusChip(modelTag, "now active", "ok"));

        // Only mention the cleared context — and offer a snapshot — when there was actually a
        // conversation to clear. Switching an empty chat has nothing to salvage, so stay quiet.
        if (_pending != null)
        {
            _transcript.Append(_html.StatusChip("Context cleared", "keep memory, or snapshot it?", ""));
            SnapshotOfferChanged?.Invoke();
        }

        StateChanged?.Invoke();
    }

    /// <summary>The outgoing conversation buffered on a model switch (or the live one, for a manual
    /// snapshot), held only until the user creates a snapshot from it or ignores the offer. Pure
    /// opt-in: it is NOT auto-saved, and is discarded on the next switch or when the app closes.</summary>
    /// <summary><paramref name="IsManual"/> is true when the offer came from the "Take snapshot" tab
    /// action rather than a model switch — the user already decided, so the UI can skip the
    /// "snapshot available?" notification bar and open the name+model picker directly.</summary>
    public sealed record PendingSnapshot(string OriginModel, string RawHistory, int MessageCount, bool IsManual = false);

    private PendingSnapshot? _pending;

    /// <summary>The conversation currently on offer to snapshot, or null if there's nothing pending.</summary>
    public PendingSnapshot? PendingOffer => _pending;

    /// <summary>Raised when <see cref="PendingOffer"/> appears or clears, so the tab can show/hide its
    /// "create a snapshot?" card. Fires on the calling thread.</summary>
    public event Action? SnapshotOfferChanged;

    /// <summary>Buffers the outgoing conversation before a switch clears it. Returns null (nothing
    /// buffered) when there's nothing worth keeping. Never throws — must not block the switch.</summary>
    private async Task<PendingSnapshot?> BufferConversationAsync(string originModel, bool isManual = false)
    {
        try
        {
            var history = await _ai.GetHistoryAsync();
            if (!HistorySummarizer.HasContent(history)) return null;
            // Full (untruncated) — this is only ever fed to the summarizer, never stored on a snapshot.
            return new PendingSnapshot(originModel, HistorySummarizer.Full(history), history.Count - 1, isManual);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Offers to snapshot the CURRENT conversation on demand (the "Take snapshot" tab action),
    /// without switching models or clearing it. Shows the create card, or notes there's nothing to save.</summary>
    public async Task OfferManualSnapshotAsync()
    {
        _pendingCarryJson = null;   // nothing was cleared — "keep memory" doesn't apply here
        _pending = await BufferConversationAsync(ModelName, isManual: true);
        if (_pending == null)
        {
            _transcript.Append(_html.StatusChip("Nothing to snapshot", "start a conversation first", "warn"));
            return;
        }
        SnapshotOfferChanged?.Invoke();
    }

    /// <summary>Creates a snapshot from the pending conversation, summarized by
    /// <paramref name="summarizerModel"/> (the origin model, the user's favorite, or an explicit pick).
    /// Born summarized — there is no light/un-enhanced state. Returns null on success, else an error.</summary>
    public async Task<string?> CreateSnapshotAsync(string summarizerModel, string? name = null)
    {
        var pending = _pending;
        if (pending == null) return "Nothing to snapshot.";
        if (string.IsNullOrWhiteSpace(summarizerModel)) return "Pick a model to summarize with.";

        try
        {
            // The endpoint is shared (Ollama routes local and cloud models alike), so any installed
            // model summarizes fine regardless of which model the chat is on.
            var recap = await SnapshotEnhancer.SummarizeAsync(
                _config.OllamaEndpoint, summarizerModel, pending.RawHistory);

            if (string.IsNullOrWhiteSpace(recap))
                return "The model returned an empty recap.";

            // No name typed → let the model title it from the recap, then GUARANTEE the title is
            // unique against what's already in the store (the model is told the taken names to
            // avoid near-misses, but code enforces it). A typed name is kept exactly as entered.
            var finalName = name?.Trim();
            if (string.IsNullOrWhiteSpace(finalName))
            {
                var taken = _snapshots.Items.Select(s => s.DisplayTitle).ToList();
                var suggested = await SnapshotEnhancer.SuggestNameAsync(
                    _config.OllamaEndpoint, summarizerModel, recap, taken);
                if (!string.IsNullOrWhiteSpace(suggested))
                    finalName = SnapshotNaming.MakeUnique(suggested!, taken);
            }

            _snapshots.Add(pending.OriginModel, summarizerModel, recap, pending.MessageCount, finalName, ProjectRootPath);
            _pending = null;
            SnapshotOfferChanged?.Invoke();
            var label = string.IsNullOrWhiteSpace(finalName) ? $"summarized by {summarizerModel}" : $"\"{finalName}\"";
            _transcript.Append(_html.StatusChip("Snapshot saved", label, "ok"));
            return null;
        }
        catch (Exception ex)
        {
            return $"Snapshot failed: {ex.Message}";
        }
    }

    /// <summary>Declines the pending snapshot offer — pure opt-in, so the buffered conversation is
    /// discarded.</summary>
    public void DismissSnapshotOffer()
    {
        if (_pending == null) return;
        _pending = null;
        _pendingCarryJson = null;
        SnapshotOfferChanged?.Invoke();
    }

    /// <summary>Verbatim pre-switch history (AIService export JSON), held alongside
    /// <see cref="_pending"/> only for model switches — never for manual snapshot offers,
    /// where nothing was cleared. Same lifetime as the offer: consumed, dismissed, or
    /// replaced by the next switch.</summary>
    private string? _pendingCarryJson;

    /// <summary>Whether the current offer supports "keep memory" (model switches only).</summary>
    public bool CanCarryMemory => _pendingCarryJson != null;

    /// <summary>
    /// "Keep memory" on the model-switch offer: re-imports the pre-switch conversation
    /// VERBATIM into the new model — full fidelity, tool calls included; the same
    /// conversation simply continues under new management. On failure the offer stays up,
    /// so the summarized-snapshot path remains available as the fallback.
    /// </summary>
    public bool TryCarryMemoryAcrossSwitch()
    {
        var json = _pendingCarryJson;
        if (json == null) return false;

        var restored = _ai.TryRestoreHistoryJson(json);
        if (restored == 0)
        {
            _transcript.Append(_html.Warn(
                "Couldn't carry the conversation into the new model — you can still save it as a snapshot."));
            return false;
        }

        _pendingCarryJson = null;
        _pending = null;   // offer consumed — the conversation lives on, nothing to salvage
        _transcript.Append(_html.StatusChip("Memory carried", $"{restored} messages continue here", "ok"));
        SnapshotOfferChanged?.Invoke();
        return true;
    }

    /// <summary>Arms a snapshot's recap so it rides along (invisibly) with this agent's next message.</summary>
    public void ImportContext(ContextSnapshot snapshot)
    {
        // Skip a snapshot that's already queued for the next send, so a double-click (or re-import)
        // doesn't stack the same recap twice.
        if (!_armedSnapshotIds.Add(snapshot.Id))
        {
            _transcript.Append(_html.StatusChip("Already imported", $"{snapshot.DisplayTitle} is queued", ""));
            return;
        }

        // Accumulate — importing several snapshots stacks them, each labeled so the model can tell
        // the distinct past conversations apart. They all ride along on the next send.
        _armedContexts.Add($"From \"{snapshot.DisplayTitle}\":\n{snapshot.Recap}");
        // Show the snapshot's name when it has one, else the model it came from.
        _transcript.Append(_html.StatusChip("Context imported", $"from {snapshot.DisplayTitle}", ""));
    }

    private async Task HandleLearnCommandAsync()
    {
        if (!IsConnected)
        {
            _transcript.Append(_html.Warn("Connect to Ollama first (/retry or Settings), then try /learn again."));
            return;
        }

        await _ai.EnterLearnModeAsync();
        _transcript.Append(_html.Success("AI Educator mode active!"));
        _transcript.Append(_html.Dim("Ask me anything about local AI! Type /clear to return to normal mode."));
    }

    private async Task HandleRetryCommandAsync()
    {
        _transcript.Append(_html.Dim("Checking Ollama connection..."));

        var probe = await OllamaSetupHelper.ProbeAsync(_config.OllamaEndpoint);
        if (probe.WasHealed)
        {
            _config.OllamaEndpoint = probe.NormalizedUrl;
            _configs.Defaults.OllamaEndpoint = probe.NormalizedUrl;
            _configs.SaveDefaults();   // a correction, not a preference
        }

        IsConnected = probe.Ok;
        if (IsConnected)
        {
            _transcript.Append(_html.Success("✓ Connected to Ollama!"));
            await ValidateCurrentModelAsync();
        }
        else
        {
            _transcript.Append(_html.Error($"Unable to reach Ollama at {_config.OllamaEndpoint}."));
            _transcript.Append(_html.Dim("Make sure 'ollama serve' is running, pull a model, then type /retry — or open Settings."));
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Applies new connection settings from the Settings page and reconnects — for THIS agent.
    /// Another tab can stay pointed at a different endpoint or model.
    /// </summary>
    public async Task ApplyConnectionSettingsAsync(string endpoint, string? modelName)
    {
        if (!string.IsNullOrWhiteSpace(endpoint)) _config.OllamaEndpoint = endpoint.Trim();
        if (!string.IsNullOrWhiteSpace(modelName)) _config.ModelName = modelName.Trim();

        // Onboarding is an app-wide fact, not a preference: once the user has connected once,
        // the first-run wizard must not fire again for the next agent or the next launch.
        _config.HasCompletedOnboarding = true;
        _configs.Defaults.HasCompletedOnboarding = true;
        _configs.SaveDefaults();

        _busy.Start("Connecting...");
        try
        {
            var probe = await OllamaSetupHelper.ProbeAsync(_config.OllamaEndpoint);
            if (probe.WasHealed) _config.OllamaEndpoint = probe.NormalizedUrl;
            IsConnected = probe.Ok;

            if (IsConnected)
            {
                await _ai.ReinitializeAsync(_config);
                await ValidateCurrentModelAsync();
                await InitializeMcpAsync();
                if (!ModelError)
                    _transcript.Append(_html.StatusChip(ModelName, "ready", "ok"));
            }
            else
            {
                _transcript.Append(_html.Error($"Unable to reach Ollama at {_config.OllamaEndpoint}. ({probe.Error ?? "no detail"})"));
            }
        }
        finally
        {
            _busy.Reset();
            StateChanged?.Invoke();
        }
    }

    /// <summary>Fetches the pulled-model list for the settings pane dropdown.</summary>
    public async Task<List<string>> ListModelsAsync()
    {
        try
        {
            var probe = await OllamaSetupHelper.ProbeAsync(_config.OllamaEndpoint);
            if (!probe.Ok) return new List<string>();
            return await OllamaSetupHelper.ListModelsAsync(probe.NormalizedUrl);
        }
        catch
        {
            return new List<string>();
        }
    }

    private void HandleCopyCodeCommand()
    {
        if (string.IsNullOrEmpty(_lastAiResponse))
        {
            _transcript.Append(_html.Warn("Nothing to copy — no AI response yet."));
            return;
        }

        var matches = Regex.Matches(_lastAiResponse, @"```(?:\w*)\r?\n([\s\S]*?)```");
        if (matches.Count == 0)
        {
            _transcript.Append(_html.StatusChip("Copy code", "no code blocks in the last response"));
            return;
        }

        var codeContent = string.Join("\n\n", matches.Cast<Match>().Select(m => m.Groups[1].Value.TrimEnd()));
        ClipboardCopyRequested?.Invoke(codeContent);
        _transcript.Append(_html.StatusChip("Copied", $"{matches.Count} code block{(matches.Count == 1 ? "" : "s")} · {codeContent.Length} chars", "ok"));
    }

    private void ShowSkills()
    {
        var skills = _skills.GetAll();
        if (skills.Count == 0)
        {
            _transcript.Append(_html.Warn("No skills installed."));
            _transcript.Append(_html.Dim($"Create a skill by adding a folder with a SKILL.md file to:\n  • User skills: {_skills.ResolvedUserSkillsDirectory}\n  • Project skills: {_skills.ResolvedProjectSkillsDirectory}"));
            return;
        }

        _transcript.Append(_html.Success("✨ Skills are invoked automatically when the model matches a description to your request."));
        _transcript.Append(_html.HelpCard(skills.Select(s =>
            ($"{s.Name}  [{s.Source}]", string.IsNullOrWhiteSpace(s.Description) ? "(no description)" : s.Description))));
        _transcript.Append(_html.Dim("Use /force-skill <name> to override and run a specific one."));
    }

    private async Task HandleForceSkillCommandAsync(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            // Picker, matching the CLI's PromptForSkill flow.
            var picked = await PickSkillAsync();
            if (picked == null) return;
            skillName = picked;
        }

        var skill = _skills.GetByName(skillName);
        if (skill == null)
        {
            _transcript.Append(_html.Error($"No skill named {skillName} is installed."));
            _transcript.Append(_html.Dim("Type /skills to see available skills."));
            return;
        }

        if (!IsConnected || ModelError)
        {
            _transcript.Append(_html.Warn("Cannot run skill — no model is connected."));
            return;
        }

        _transcript.Append(_html.Success($"✓ Forcing skill: {skill.Name}"));
        if (!string.IsNullOrWhiteSpace(skill.Description))
            _transcript.Append(_html.Dim(skill.Description));

        var forcedPrompt =
            $"[system: the user has explicitly forced skill '{skill.Name}' via /force-skill. " +
            $"Follow these instructions exactly for this turn, even if they differ from your defaults.]\n\n" +
            $"# Skill: {skill.Name}\n\n{skill.Body}";

        await ProcessDirectRequestAsync(forcedPrompt);
    }

    private async Task HandleMcpCommandAsync(string rawArgs)
    {
        if (rawArgs.StartsWith("tools", StringComparison.OrdinalIgnoreCase))
        {
            var filter = rawArgs[5..].Trim();
            await HandleMcpToolsCommandAsync(string.IsNullOrWhiteSpace(filter) ? null : filter);
            return;
        }
        if (rawArgs.StartsWith("remove", StringComparison.OrdinalIgnoreCase))
        {
            await HandleMcpRemoveCommandAsync(rawArgs[6..].Trim());
            return;
        }
        if (rawArgs.StartsWith("add", StringComparison.OrdinalIgnoreCase))
        {
            _transcript.Append(_html.Dim("Opening the MCP server editor…"));
            McpEditorRequested?.Invoke(null);
            return;
        }

        // Plain /mcp — status listing
        if (!_config.EnableMcp)
        {
            _transcript.Append(_html.Warn("MCP is disabled. Enable with /config set mcp true."));
            return;
        }
        if (_config.McpServers.Count == 0)
        {
            _transcript.Append(_html.Warn("No MCP servers configured."));
            _transcript.Append(_html.Dim("Add one with /mcp add, or from the MCP page in the sidebar."));
            return;
        }

        var rows = await GetMcpStatusRowsAsync();
        _transcript.Append(_html.HelpCard(rows.Select(r => ($"{r.Name} ({r.Transport})", r.Status))));
    }

    /// <summary>
    /// Persists an added/edited MCP server, then reconnects — the write path for the
    /// MCP editor modal. When <paramref name="originalName"/> differs from
    /// <paramref name="name"/> the server was renamed and the old entry is dropped.
    /// Returns a short status line for the MCP page; details also land in the transcript.
    /// </summary>
    public async Task<(bool Ok, string Message)> SaveMcpServerAsync(string? originalName, string name, McpServerConfig server)
    {
        // MCP servers are app-wide — they're OS processes shared by every agent — so the edit
        // lands on the saved defaults and is persisted immediately, unlike per-agent settings.
        var servers = _configs.Defaults.McpServers;

        // Preserve fields the editor doesn't surface (e.g. autoApprove) across an edit.
        if (originalName != null && servers.TryGetValue(originalName, out var existing))
            server.AutoApprove = existing.AutoApprove;

        McpServerConfig? replaced = null;
        if (servers.TryGetValue(name, out var prior)) replaced = prior;

        if (originalName != null && originalName != name)
            servers.Remove(originalName);
        servers[name] = server;

        try
        {
            _configs.SaveDefaults();
            _configs.SyncMcpServersToAgents();
        }
        catch (Exception ex)
        {
            // Roll back the in-memory state so config and disk don't diverge.
            servers.Remove(name);
            if (replaced != null) servers[name] = replaced;
            if (originalName != null && originalName != name && replaced == null && servers.ContainsKey(originalName) == false)
                servers[originalName] = server;
            _transcript.Append(_html.Error($"Failed to save config: {ex.Message}"));
            return (false, $"Failed to save config: {ex.Message}");
        }

        _transcript.Append(_html.Dim(originalName == null
            ? $"MCP server '{name}' added. Connecting..."
            : $"MCP server '{name}' updated. Reconnecting..."));

        _busy.Start($"Connecting to '{name}'...");
        try
        {
            // Restarts the shared servers once and re-registers their tools on EVERY tab's
            // kernel — history kept. Other tabs would otherwise hold stale tool handles.
            await _mcp.ReloadAllAsync();
        }
        finally
        {
            _busy.Reset();
        }

        if (server.Disabled)
        {
            _transcript.Append(_html.Success($"✓ '{name}' saved (disabled)."));
            return (true, $"✓ '{name}' saved (disabled).");
        }
        if (_mcpManager.ActiveClients.TryGetValue(name, out var client))
        {
            var toolCount = "?";
            try { toolCount = (await client.ListToolsAsync()).Count.ToString(); } catch { }
            _transcript.Append(_html.Success($"✓ Server '{name}' connected ({toolCount} tool(s))."));
            return (true, $"✓ '{name}' connected ({toolCount} tool(s)).");
        }
        if (_mcpManager.StartupErrors.TryGetValue(name, out var err))
        {
            _transcript.Append(_html.Warn($"Server saved but failed to connect: {err}"));
            return (true, $"'{name}' saved but failed to connect: {err}");
        }
        _transcript.Append(_html.Warn($"'{name}' saved but did not appear in active clients."));
        return (true, $"'{name}' saved but did not appear in active clients.");
    }

    public sealed record McpToolInfo(string Name, string? Description);
    public sealed record McpTestResult(bool Ok, string Message, IReadOnlyList<McpToolInfo> Tools);

    /// <summary>
    /// Tests an MCP server config in isolation — spins up a throwaway client from the
    /// editor's form values, handshakes, lists tools, and tears it down. Never touches
    /// saved config or the running servers (unlike Save &amp; Connect, which commits and
    /// restarts everything). Returns the full tool list so the UI can preview the
    /// capabilities this server would add.
    /// </summary>
    public async Task<McpTestResult> TestMcpServerAsync(string name, McpServerConfig server)
    {
        // Generous ceiling: a first-ever npx/uvx stdio server downloads its package
        // before it can even handshake.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        McpClient? client = null;
        try
        {
            ModelContextProtocol.Client.IClientTransport transport;
            if (server.IsHttp)
            {
                var options = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(server.Url!),
                    Name = name
                };
                if (server.Headers.Count > 0)
                    options.AdditionalHeaders = server.Headers.ToDictionary(kv => kv.Key, kv => kv.Value);
                transport = new HttpClientTransport(options);
            }
            else
            {
                var options = new StdioClientTransportOptions
                {
                    Name = name,
                    Command = server.Command!,
                    Arguments = server.Args.ToArray()
                };
                if (server.Env.Count > 0)
                    options.EnvironmentVariables = server.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
                transport = new StdioClientTransport(options);
            }

            client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);
            var tools = await client.ListToolsAsync(cancellationToken: cts.Token);

            var toolInfos = tools
                .Select(t => new McpToolInfo(t.Name, string.IsNullOrWhiteSpace(t.Description) ? null : t.Description))
                .ToList();
            return new McpTestResult(true,
                toolInfos.Count == 0
                    ? "Connected — this server exposes no tools."
                    : $"These {toolInfos.Count} tool(s) will be available to the model:",
                toolInfos);
        }
        catch (OperationCanceledException)
        {
            return new McpTestResult(false,
                "Timed out after 60s. First runs of npx/uvx servers can be slow (package download) — try again.",
                Array.Empty<McpToolInfo>());
        }
        catch (Exception ex)
        {
            return new McpTestResult(false, ex.Message, Array.Empty<McpToolInfo>());
        }
        finally
        {
            if (client != null)
            {
                try { await client.DisposeAsync(); } catch { }
            }
        }
    }

    public sealed record McpStatusRow(string Name, string Transport, string Status, bool Connected, bool Disabled);

    /// <summary>Live MCP server status — feeds both the /mcp listing and the MCP page.</summary>
    public async Task<List<McpStatusRow>> GetMcpStatusRowsAsync()
    {
        var rows = new List<McpStatusRow>();
        foreach (var (name, serverCfg) in _config.McpServers)
        {
            var transport = serverCfg.IsHttp ? "http" : "stdio";
            string status;
            var connected = false;
            if (serverCfg.Disabled)
            {
                status = "disabled";
            }
            else if (_mcpManager.ActiveClients.TryGetValue(name, out var client))
            {
                connected = true;
                var toolCount = "?";
                try
                {
                    var tools = await client.ListToolsAsync();
                    toolCount = tools.Count.ToString();
                }
                catch { }
                status = $"connected · {toolCount} tool(s)";
            }
            else if (_mcpManager.StartupErrors.TryGetValue(name, out var err))
            {
                status = $"failed: {err}";
            }
            else
            {
                status = "not started";
            }
            rows.Add(new McpStatusRow(name, transport, status, connected, serverCfg.Disabled));
        }
        return rows;
    }

    private async Task HandleMcpToolsCommandAsync(string? serverFilter)
    {
        if (_mcpManager.ActiveClients.Count == 0)
        {
            _transcript.Append(_html.Warn("No MCP servers are connected."));
            _transcript.Append(_html.Dim("Type /mcp for status."));
            return;
        }

        if (serverFilter != null && !_mcpManager.ActiveClients.ContainsKey(serverFilter))
        {
            _transcript.Append(_html.Warn($"Server '{serverFilter}' is not connected."));
            _transcript.Append(_html.Dim($"Connected: {string.Join(", ", _mcpManager.ActiveClients.Keys)}"));
            return;
        }

        var clients = serverFilter != null
            ? new[] { (serverFilter, _mcpManager.ActiveClients[serverFilter]) }
            : _mcpManager.ActiveClients.Select(kv => (kv.Key, kv.Value)).ToArray();

        foreach (var (serverName, client) in clients)
        {
            try
            {
                var tools = await client.ListToolsAsync();
                _transcript.Append(_html.Info($"{serverName} ({tools.Count} tool{(tools.Count == 1 ? "" : "s")})"));
                _transcript.Append(_html.HelpCard(tools.Select(t =>
                    (t.Name, string.IsNullOrWhiteSpace(t.Description) ? "(no description)" : t.Description))));
            }
            catch (Exception ex)
            {
                _transcript.Append(_html.Error($"{serverName}: {ex.Message}"));
            }
        }
    }

    private async Task HandleMcpRemoveCommandAsync(string serverArg)
    {
        if (string.IsNullOrWhiteSpace(serverArg))
        {
            _transcript.Append(_html.Warn("Usage: /mcp remove <name>"));
            if (_config.McpServers.Count > 0)
                _transcript.Append(_html.Dim($"Configured: {string.Join(", ", _config.McpServers.Keys)}"));
            return;
        }

        if (!_config.McpServers.ContainsKey(serverArg))
        {
            _transcript.Append(_html.Warn($"No server named '{serverArg}' is configured."));
            if (_config.McpServers.Count > 0)
                _transcript.Append(_html.Dim($"Configured: {string.Join(", ", _config.McpServers.Keys)}"));
            return;
        }

        var ui = _approvals.Ui;
        if (ui != null)
        {
            var choice = await ui.ShowApprovalAsync(new ApprovalRequest
            {
                Title = $"Remove MCP server '{serverArg}'?",
                Options = new[]
                {
                    new ApprovalOption("Remove", ApprovalOptionKind.Destructive),
                    new ApprovalOption("Cancel", ApprovalOptionKind.Redirect)
                }
            });
            if (choice != "Remove")
            {
                _transcript.Append(_html.Dim("Cancelled."));
                return;
            }
        }

        _configs.Defaults.McpServers.Remove(serverArg);
        try
        {
            _configs.SaveDefaults();
            _configs.SyncMcpServersToAgents();
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Error($"Failed to save config: {ex.Message}"));
            return;
        }

        _transcript.Append(_html.Dim($"Removed '{serverArg}'. Reloading..."));
        await _mcp.ReloadAllAsync();   // every tab's kernel re-registers, history kept
        _transcript.Append(_html.Success($"✓ '{serverArg}' removed."));
    }

    private async Task HandleMcpReloadCommandAsync()
    {
        // Render reload progress as centered status pills — same StatusChip family as the top
        // "MCP · N connected" chip (a theme-aware dot, no emoji), rather than left-aligned text.
        _transcript.Append(_html.StatusChip("MCP", "restarting…"));

        await _mcp.ReloadAllAsync();   // every tab's kernel re-registers, history kept

        var active = _mcpManager.ActiveClients.Count;
        var failed = _mcpManager.StartupErrors.Count;
        _transcript.Append(failed == 0
            ? _html.StatusChip("MCP", $"{active} server{(active == 1 ? "" : "s")} connected", "ok")
            : _html.StatusChip("MCP", $"{active} connected, {failed} failed", "warn"));
    }

    // ============================================================
    // Music (port of HandleMusicCommand, minus the ANSI visualizer)
    // ============================================================

    private void HandleMusicCommand(string action)
    {
        switch (action)
        {
            case "play":
                _music.Play();
                ShowMusicStatus();
                break;
            case "stop":
                _music.Stop();
                _transcript.Append(_html.Dim("♪ Music stopped."));
                break;
            case "pause":
                _music.TogglePause();
                ShowMusicStatus();
                break;
            case "next":
                _music.NextTrack();
                ShowMusicStatus();
                break;
            case "list":
                var tracks = _music.GetAvailableTracks();
                if (tracks.Count == 0)
                {
                    _transcript.Append(_html.Warn("No tracks found."));
                    _transcript.Append(_html.Dim($"Add mp3 files to genre folders under: {_music.UserMusicPath}"));
                }
                else
                {
                    _transcript.Append(_html.Mono(string.Join("\n",
                        tracks.Select(t => $"♪ {t.Name}  [{t.Genre}]"))));
                }
                break;
        }
    }

    private void HandleMusicGenre(string genre)
    {
        _music.Play(genre);
        ShowMusicStatus();
    }

    private void HandleMusicVolume(string rawArgs)
    {
        if (int.TryParse(rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var vol))
        {
            vol = Math.Clamp(vol, 0, 100);
            _music.SetVolume(vol / 100f);
            _transcript.Append(_html.Dim($"♪ Volume set to {vol}%."));
        }
        else
        {
            _transcript.Append(_html.Dim("Usage: /music-vol <0-100>  ·  Example: /music-vol 70"));
        }
    }

    private void ShowMusicStatus()
    {
        if (!_music.AudioAvailable)
        {
            _transcript.Append(_html.Warn($"Audio unavailable: {_music.AudioError ?? "unknown error"}"));
            return;
        }

        var track = _music.CurrentTrack;
        if (track == null || (!_music.IsPlaying && !_music.IsPaused))
        {
            _transcript.Append(_html.Dim("♪ Nothing playing. /music to start."));
            return;
        }

        var playing = !_music.IsPaused;
        _transcript.Append(_html.StatusChip(
            playing ? "Now playing" : "Paused",
            $"{track.Name} · {track.Genre} · vol {(int)(_music.Volume * 100)}%",
            playing ? "ok" : ""));
    }
}
