using System.Text.Json;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Desktop.Services;
using ModelContextProtocol.Client;

namespace MandoCode.Desktop.ViewModels;

/// <summary>
/// The desktop wizards — ports of the CLI's interactive flows (/setup onboarding,
/// /mcp add, /config wizard, model/skill/genre pickers, 401 cloud sign-in), built on
/// the same two primitives the CLI wizards use: a select prompt (the approval overlay)
/// and a text prompt (the instruction input). The heavy lifting — probing, starting
/// the daemon, pulling models, `ollama signin` — is the harness's OllamaSetupHelper,
/// reused verbatim.
/// </summary>
public sealed partial class ChatController
{
    // ============================================================
    // Wizard primitives (desktop equivalents of WizardPromptSelect/Text/Confirm)
    // ============================================================

    private IApprovalUi WizardUi =>
        _approvals.Ui ?? throw new InvalidOperationException("Approval UI not attached yet.");

    private async Task<string> WizardSelectAsync(string title, IReadOnlyList<ApprovalOption> options)
    {
        var choice = await WizardUi.ShowApprovalAsync(new ApprovalRequest { Title = title, Options = options });
        // Persist the answer in the transcript, like the CLI's gold `>` echo.
        _transcript.Append(_html.UserEcho(choice));
        return choice;
    }

    private async Task<bool> WizardConfirmAsync(string title, bool defaultYes = false)
    {
        var options = defaultYes
            ? new[] { new ApprovalOption("Yes", ApprovalOptionKind.Proceed), new ApprovalOption("No", ApprovalOptionKind.Redirect) }
            : new[] { new ApprovalOption("No", ApprovalOptionKind.Redirect), new ApprovalOption("Yes", ApprovalOptionKind.Proceed) };
        return await WizardSelectAsync(title, options) == "Yes";
    }

    /// <summary>
    /// Cancelable, validated text prompt. Returns null when the user cancels (Esc or
    /// the Cancel button). Re-prompts with the validation error appended until valid.
    /// </summary>
    private async Task<string?> WizardTextAsync(string title, string placeholder = "", Func<string, string?>? validate = null)
    {
        var prompt = title;
        while (true)
        {
            var value = await WizardUi.ShowInstructionInputAsync(prompt, placeholder, allowCancel: true);
            if (value == ApprovalSignals.Cancelled) return null;

            var error = validate?.Invoke(value);
            if (error == null)
            {
                _transcript.Append(_html.UserEcho(value.Length == 0 ? "(empty)" : value));
                return value;
            }
            prompt = $"{title}\n⚠ {error}";
        }
    }

    private void WizardCancelled() => _transcript.Append(_html.Dim("Cancelled."));

    // ============================================================
    // Pickers
    // ============================================================

    private const string PickerCancelLabel = "(cancel)";

    /// <summary>Model picker — cloud models on top, badge per row (mirrors the CLI's /model picker).</summary>
    private async Task<string?> PickModelAsync(IReadOnlyList<string> models)
    {
        var options = models
            .OrderBy(m => MandoCodeConfig.IsCloudModel(m) ? 0 : 1)
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .Select(m => new ApprovalOption($"{m}   ({(MandoCodeConfig.IsCloudModel(m) ? "cloud" : "local")})", ApprovalOptionKind.Proceed))
            .Append(new ApprovalOption(PickerCancelLabel, ApprovalOptionKind.Redirect))
            .ToList();

        var picked = await WizardSelectAsync("Select a model:", options);
        return picked == PickerCancelLabel ? null : picked.Split(' ')[0];
    }

    /// <summary>Skill picker for /force-skill with no argument (mirrors the CLI's PromptForSkill).</summary>
    private async Task<string?> PickSkillAsync()
    {
        var skills = _skills.GetAll();
        if (skills.Count == 0)
        {
            _transcript.Append(_html.Warn("No skills installed. Type /skills for setup instructions."));
            return null;
        }

        const string separator = "  —  ";
        var options = skills
            .Select(s => new ApprovalOption(
                string.IsNullOrWhiteSpace(s.Description) ? s.Name : s.Name + separator + s.Description,
                ApprovalOptionKind.Proceed))
            .Append(new ApprovalOption(PickerCancelLabel, ApprovalOptionKind.Redirect))
            .ToList();

        var picked = await WizardSelectAsync("Select a skill to force:", options);
        if (picked == PickerCancelLabel) return null;

        var dashIdx = picked.IndexOf(separator, StringComparison.Ordinal);
        return dashIdx > 0 ? picked[..dashIdx] : picked;
    }

    /// <summary>Genre picker for /music-playlist (mirrors the CLI's HandleMusicPlaySelection).</summary>
    private async Task RunMusicPlaylistPickerAsync()
    {
        var genres = _music.GetAvailableGenres();
        if (genres.Count == 0)
        {
            _transcript.Append(_html.Warn("No genres found."));
            _transcript.Append(_html.Dim($"Add mp3 files to genre folders under: {_music.UserMusicPath}"));
            return;
        }

        if (genres.Count == 1)
        {
            _music.Play(genres[0]);
            ShowMusicStatus();
            return;
        }

        const string shuffleAll = "Shuffle All";
        var options = new List<ApprovalOption> { new(shuffleAll, ApprovalOptionKind.Proceed) };
        options.AddRange(genres.Select(g =>
            new ApprovalOption(char.ToUpper(g[0]) + g[1..], ApprovalOptionKind.Proceed)));
        options.Add(new ApprovalOption(PickerCancelLabel, ApprovalOptionKind.Redirect));

        var picked = await WizardSelectAsync("Select a genre:", options);
        if (picked == PickerCancelLabel) return;

        _music.Play(picked == shuffleAll ? null : picked.ToLowerInvariant());
        ShowMusicStatus();
    }

    // ============================================================
    // /setup — guided connection wizard (desktop OnboardingFlow)
    // ============================================================

    private async Task RunSetupWizardAsync()
    {
        _transcript.Append(_html.Info("Connection setup"));

        // ---- Step 1: reach the daemon ----
        while (true)
        {
            _busy.Start("Checking Ollama...");
            OllamaSetupHelper.ProbeResult probe;
            try { probe = await OllamaSetupHelper.ProbeAsync(_config.OllamaEndpoint); }
            finally { _busy.Reset(); }

            if (probe.Ok)
            {
                if (probe.WasHealed)
                {
                    _config.OllamaEndpoint = probe.NormalizedUrl;
                    _configs.Defaults.OllamaEndpoint = probe.NormalizedUrl;
                    _configs.SaveDefaults();
                    _transcript.Append(_html.Dim($"Healed trailing slash — using {probe.NormalizedUrl}"));
                }
                IsConnected = true;
                _transcript.Append(_html.Success($"✓ Ollama reachable at {_config.OllamaEndpoint}"));
                break;
            }

            IsConnected = false;
            var cliInstalled = OllamaSetupHelper.IsOllamaCliInstalled();
            var options = new List<ApprovalOption>();
            if (cliInstalled) options.Add(new("Start Ollama now", ApprovalOptionKind.Proceed));
            options.Add(new("Change endpoint URL", ApprovalOptionKind.Proceed));
            if (!cliInstalled) options.Add(new("Open the Ollama download page", ApprovalOptionKind.Proceed));
            options.Add(new("Retry", ApprovalOptionKind.Proceed));
            options.Add(new("Cancel setup", ApprovalOptionKind.Redirect));

            var choice = await WizardSelectAsync(
                $"Can't reach Ollama at {_config.OllamaEndpoint}. ({probe.Error ?? "no detail"})", options);

            if (choice == "Cancel setup") { WizardCancelled(); StateChanged?.Invoke(); return; }
            if (choice == "Retry") continue;

            if (choice == "Start Ollama now")
            {
                _busy.Start("Starting Ollama...");
                try
                {
                    var started = await OllamaSetupHelper.StartOllamaServeAsync(_config.OllamaEndpoint, _config.ContextLength);
                    _transcript.Append(started
                        ? _html.Success("✓ Ollama started.")
                        : _html.Warn("Couldn't start Ollama automatically — start it manually with 'ollama serve'."));
                }
                catch (Exception ex)
                {
                    _transcript.Append(_html.Warn($"Couldn't start Ollama: {ex.Message}"));
                }
                finally { _busy.Reset(); }
                continue;
            }

            if (choice == "Open the Ollama download page")
            {
                OllamaSetupHelper.OpenInBrowser("https://ollama.com/download");
                _transcript.Append(_html.Dim("Install Ollama, then pick Retry."));
                continue;
            }

            // Change endpoint URL
            var url = await WizardTextAsync(
                "Ollama endpoint URL:", _config.OllamaEndpoint,
                u => Uri.TryCreate(u, UriKind.Absolute, out var parsed) && (parsed.Scheme == "http" || parsed.Scheme == "https")
                    ? null : "Must be an absolute http(s) URL");
            if (url == null) { WizardCancelled(); StateChanged?.Invoke(); return; }
            _config.OllamaEndpoint = url.Trim();
        }

        // ---- Step 2: have a model ----
        _busy.Start("Fetching models...");
        OllamaSetupHelper.ListModelsResult fetched;
        try { fetched = await OllamaSetupHelper.ListModelsWithStatusAsync(_config.OllamaEndpoint); }
        finally { _busy.Reset(); }

        var models = fetched.Ok ? fetched.Models : new List<string>();
        if (models.Count == 0)
        {
            _transcript.Append(_html.Warn("No models pulled yet."));
            if (await WizardConfirmAsync("Pull a model now?", defaultYes: true))
            {
                var tag = await WizardTextAsync(
                    "Model tag to pull:", "e.g. qwen2.5-coder:7b",
                    t => string.IsNullOrWhiteSpace(t) ? "Enter a model tag" : null);
                if (tag != null && await PullModelWithProgressAsync(tag.Trim()))
                    models = new List<string> { tag.Trim() };
            }
            if (models.Count == 0)
            {
                _transcript.Append(_html.Dim("Setup incomplete — pull a model (ollama pull <tag>) and run /setup again."));
                StateChanged?.Invoke();
                return;
            }
        }

        // ---- Step 3: pick the model ----
        var modelTag = await PickModelAsync(models);
        if (modelTag == null) { WizardCancelled(); StateChanged?.Invoke(); return; }

        // ---- Step 4: cloud auth check ----
        if (MandoCodeConfig.IsCloudModel(modelTag))
        {
            _busy.Start("Checking cloud sign-in...");
            OllamaSetupHelper.AuthTestResult auth;
            try { auth = await OllamaSetupHelper.TestCloudAuthAsync(_config.OllamaEndpoint, modelTag); }
            finally { _busy.Reset(); }

            if (auth.Unauthorized)
            {
                _transcript.Append(_html.Warn("Cloud models need an Ollama sign-in."));
                if (await WizardConfirmAsync("Sign in to Ollama cloud now?", defaultYes: true))
                    await RunCloudSigninWalkthroughAsync();
            }
        }

        // ---- Step 5: apply ----
        await ApplyModelSwitchAsync(modelTag);

        // /setup configures the app, not one agent: what you just picked becomes the default new
        // agents start on, and marks onboarding done so the wizard never re-fires. Agents already
        // open keep whatever they were on.
        _config.HasCompletedOnboarding = true;
        _configs.SaveDefaultsFrom(_config);

        if (!ModelError)
        {
            _transcript.Append(_html.Success("Setup complete — you're ready to go."));
            _transcript.Append(_html.Dim("Saved as the default for new agents."));
        }
        StateChanged?.Invoke();
    }

    /// <summary>Pulls a model via the harness's AutoPullAsync, streaming progress into the busy line.</summary>
    private async Task<bool> PullModelWithProgressAsync(string tag)
    {
        _transcript.Append(_html.Dim($"Pulling {tag} — this can take a few minutes..."));
        _busy.Start($"Pulling {tag}...");
        try
        {
            var ok = await OllamaSetupHelper.AutoPullAsync(
                tag,
                new Progress<string>(line => _busy.Update($"Pulling {tag}: {line}")),
                CancellationToken.None);
            _transcript.Append(ok
                ? _html.Success($"✓ Pulled {tag}.")
                : _html.Error($"Pull failed for {tag} — check the tag name (see ollama.com/library)."));
            return ok;
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Error($"Pull failed: {ex.Message}"));
            return false;
        }
        finally
        {
            _busy.Reset();
        }
    }

    // ============================================================
    // 401 auto-recovery + cloud sign-in walkthrough
    // ============================================================

    private async Task OfferCloudSigninAsync()
    {
        _transcript.Append(_html.Warn("Ollama cloud sign-in required (401 Unauthorized)."));

        if (!OllamaSetupHelper.IsOllamaCliInstalled())
        {
            _transcript.Append(_html.Dim("The Ollama CLI wasn't found on PATH — install it from ollama.com/download, run 'ollama signin', then re-send your message."));
            return;
        }

        if (!await WizardConfirmAsync("Sign in to Ollama cloud now?", defaultYes: true))
        {
            _transcript.Append(_html.Dim("Run 'ollama signin' in a terminal when ready, then re-send your message."));
            return;
        }

        await RunCloudSigninWalkthroughAsync();
    }

    private async Task RunCloudSigninWalkthroughAsync()
    {
        _transcript.Append(_html.Info("Launching Ollama cloud sign-in — a browser window may open. Complete the sign-in there."));
        _busy.Start("Waiting for sign-in to complete...");
        int exitCode;
        try
        {
            exitCode = await OllamaSetupHelper.RunOllamaSigninAsync(
                new Progress<string>(line =>
                {
                    if (!string.IsNullOrWhiteSpace(line)) _transcript.Append(_html.Dim(line));
                }),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _busy.Reset();
            _transcript.Append(_html.Error($"Sign-in failed to launch: {ex.Message}"));
            return;
        }
        finally
        {
            _busy.Reset();
        }

        if (exitCode == 0)
        {
            // Rebuild the kernel against the now-authenticated daemon (same as the CLI).
            await _ai.ReinitializeAsync(_config);
            _transcript.Append(_html.Success("✓ Signed in. Re-send your message to try again."));
        }
        else
        {
            _transcript.Append(_html.Warn($"Sign-in did not complete (exit code {exitCode}). Try 'ollama signin' in a terminal."));
        }
    }

    /// <summary>
    /// Split a command-line-style argument string, honoring double-quoted segments
    /// (same as the CLI — matches how Claude Desktop / Cursor treat these args).
    /// </summary>
    public static List<string> ParseShellLikeArgs(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var ch in input)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
