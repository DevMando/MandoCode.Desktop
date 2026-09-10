using MandoCode.Desktop.Services;
using MandoCode.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MandoCode.Desktop.Controls;

/// <summary>
/// The settings form, hosted twice: on the rail's Settings page bound to the global defaults, and
/// in the pane behind an agent's gear icon bound to that agent. <see cref="ISettingsScope"/> is the
/// only difference between the two — every control, validation rule and status message is shared,
/// which is the point of the control existing.
///
/// NOTHING HERE APPLIES AS YOU CHANGE IT. Every control edits <see cref="_draft"/>, a detached
/// clone of the target config; Save commits the whole draft through the scope, and abandoning the
/// form (closing the pane, leaving the page) simply drops it. Values are still VALIDATED as they
/// are entered, through ConfigKeySetter — the same engine the CLI's /config set uses — so a bad
/// value is rejected where it is typed rather than at save time.
///
/// The two app-wide settings ride along and appear on the defaults scope only: the Tavily key (a
/// secret, so it lives in exactly one file) and agent callsigns (not a config key at all, hence
/// <see cref="_draftCallsigns"/>).
/// </summary>
public sealed partial class SettingsForm : UserControl
{
    /// <summary>Guards the write-back: populating the controls fires their change events, and an
    /// unguarded population would stage every value it just read.</summary>
    private bool _loading;

    private ISettingsScope? _scope;

    /// <summary>The edit buffer. Cloned from the scope's config on every bind/reload, diffed
    /// against it to drive the Save button, and copied over it on Save.</summary>
    private MandoCodeConfig? _draft;

    /// <summary>Callsigns live in PanelState rather than in the config, so they can't ride in the
    /// draft — but they're on this form and must obey the same save-or-lose rule.</summary>
    private bool _draftCallsigns;

    /// <summary>The model the picker should be showing. Held separately because the list arrives
    /// asynchronously and can replace the ItemsSource after the selection was set.</summary>
    private string _modelComboTarget = "";

    /// <summary>Widest the form's content may get, matching the FormContentWidth XAML resource.
    /// Past this, lines get too long to scan comfortably on the full-width defaults page.</summary>
    private const double ContentWidthCap = 680;

    public SettingsForm()
    {
        InitializeComponent();
        SettingsTabs.SelectedItem = Tab_Model;   // the setup that matters most opens first
        ModelCombo.Loaded += (_, _) => ApplyModelComboTarget();
        SizeChanged += (_, _) => ApplyContentWidth();
    }

    /// <summary>
    /// Sizes the action panel to min(available, cap), left-aligned — which XAML alone can't express.
    /// Stretch plus MaxWidth centres the panel, and HorizontalAlignment Left makes a StackPanel
    /// shrink-wrap to its widest child, either of which stops Save from being the full-width primary
    /// it is meant to be. In the agent pane (narrower than the cap) this simply fills the pane.
    /// </summary>
    private void ApplyContentWidth()
    {
        if (ActualWidth <= 0) return;
        ActionPanel.Width = Math.Min(ActualWidth, ContentWidthCap);
    }

    /// <summary>Raised when the form wants its host to run the guided /setup wizard — it renders
    /// into a chat transcript, which is the host's business, not the form's.</summary>
    public event Action? SetupWizardRequested;

    /// <summary>Raised after a SAVE the host may need to reflect elsewhere (an agent's header, the
    /// tab strip). Not raised for staged edits — nothing has happened yet.</summary>
    public event Action? SettingsChanged;

    /// <summary>Raised after the app-wide Tavily key is saved, so the host can mirror it into every
    /// live agent's in-memory config.</summary>
    public event Action? SecretsChanged;

    /// <summary>True when the form is holding edits the user hasn't saved.</summary>
    public bool HasUnsavedChanges => SaveButton.IsEnabled;

    /// <summary>
    /// Points the form at what it should edit and repopulates it, discarding any draft in progress.
    /// Hosts re-bind whenever their surface opens, so the form always reflects the live config.
    /// </summary>
    public void Bind(ISettingsScope scope)
    {
        _scope = scope;

        var isAgent = scope is AgentSettingsScope;
        var appWide = scope.ShowsAppWideSettings;

        // App-wide settings appear once, on the page that owns app-wide things. On an agent's pane
        // they would read as per-agent settings they are not.
        S_AgentCallsigns.Visibility = appWide ? Visibility.Visible : Visibility.Collapsed;
        TavilySection.Visibility = appWide ? Visibility.Visible : Visibility.Collapsed;
        TavilyElsewhereNote.Visibility = appWide ? Visibility.Collapsed : Visibility.Visible;
        // The whole Connection block is defaults-only: an agent's endpoint is set at launch and its
        // model comes from its header dropdown, so neither belongs here. The guided wizard sits in
        // that block too — it configures the app (it ends with SaveDefaultsFrom).
        ConnectionSection.Visibility = appWide ? Visibility.Visible : Visibility.Collapsed;

        ApplyDefaultsButton.Visibility = isAgent ? Visibility.Visible : Visibility.Collapsed;
        SaveToDefaultsButton.Visibility = isAgent ? Visibility.Visible : Visibility.Collapsed;
        ResetFactoryButton.Visibility = isAgent ? Visibility.Collapsed : Visibility.Visible;

        ContextLengthNote.Text = isAgent
            ? "Sent with every request (num_ctx), so once saved it applies from this agent's next "
            + "message. Auto-sized to the model's hardware tier when you switch models; set it here to override."
            : "Sent with every request (num_ctx). New agents start here, and each one re-sizes it to "
            + "the model's hardware tier when its model changes.";

        Reload();
    }

    /// <summary>
    /// Fetches the pulled-model list. Deliberately NOT part of <see cref="Bind"/>: the defaults form
    /// is bound in the window constructor, and probing Ollama from there would put a network call on
    /// the startup path for a page the user may never open. Hosts call this when their surface
    /// actually becomes visible.
    /// </summary>
    public Task RefreshModelsAsync() => RefreshModelListAsync();

    /// <summary>Throws away any draft and repopulates every control from the live config.</summary>
    public void Reload()
    {
        if (_scope == null) return;

        _draft = ConfigCloning.DeepClone(_scope.Config);
        _draftCallsigns = AgentCallsigns.Enabled;
        ReloadControls();
        SettingsStatus.Text = "";
    }

    /// <summary>Populates every control from the draft. Split from <see cref="Reload"/> so a
    /// rejected keystroke can snap the controls back without discarding the rest of the draft.</summary>
    private void ReloadControls()
    {
        if (_draft is not { } cfg) return;

        _loading = true;
        try
        {
            EndpointBox.Text = cfg.OllamaEndpoint;
            _modelComboTarget = cfg.GetEffectiveModelName();
            ApplyModelComboTarget();
            S_ContextLength.Value = cfg.ContextLength;
            S_Temperature.Value = cfg.Temperature;
            S_TemperatureLabel.Text = cfg.Temperature.ToString("0.##");
            S_MaxTokens.Value = cfg.MaxTokens;
            S_Streaming.SelectedItem = cfg.ResponseStreaming;
            S_AgentCallsigns.IsOn = _draftCallsigns;
            S_TaskPlanning.IsOn = cfg.EnableTaskPlanning;
            S_DiffApprovals.IsOn = cfg.EnableDiffApprovals;
            S_AutoContinue.IsOn = cfg.EnableAutoContinuation;
            S_MaxContinuations.Value = cfg.MaxAutoContinuations;
            S_RequestTimeout.Value = cfg.RequestTimeoutMinutes;
            S_StallTimeout.Value = cfg.ModelResponseTimeoutSeconds;
            S_ToolBudget.Value = cfg.ToolResultCharBudget;
            S_RenderTimeout.Value = cfg.MarkdownRenderTimeoutSeconds;
            S_WebSearch.IsOn = cfg.EnableWebSearch;
            S_TavilyKey.Password = cfg.TavilyApiKey ?? "";
            S_TavilyKey.PasswordRevealMode = PasswordRevealMode.Hidden;
            TavilyViewButton.Content = "View";
            TavilyViewButton.IsEnabled = !string.IsNullOrEmpty(cfg.TavilyApiKey);

            RefreshScopeText();
        }
        finally
        {
            _loading = false;
        }

        RefreshDirtyState();
    }

    private void RefreshScopeText()
    {
        if (_scope != null) ScopeText.Text = _scope.ScopeDescription;
    }

    // ============================================================
    // Staging
    // ============================================================

    /// <summary>
    /// The single write path: validate against the DRAFT, never the live config. A rejected value
    /// snaps the controls back and says why; an accepted one just moves the unsaved-changes count.
    /// </summary>
    private void Stage(string key, string value)
    {
        if (_draft == null) return;

        var result = ConfigKeySetter.TrySet(_draft, key, value);
        if (!result.Ok)
        {
            SettingsStatus.Text = result.Message;
            ReloadControls();   // put the control back to the last good value
            return;
        }

        SettingsStatus.Text = "";
        RefreshDirtyState();
    }

    /// <summary>Drives the Save button and the pending count off a real diff, so undoing an edit by
    /// hand correctly returns the form to "nothing to save".</summary>
    private void RefreshDirtyState()
    {
        if (_scope == null || _draft == null) return;

        // Keys owned by another surface are never pending changes here, and the scopes re-source
        // them on commit — so a long-open form cannot write a stale value back over a change made
        // elsewhere while it sat there.
        var changed = ConfigCloning
            .DifferingKeys(_draft, _scope.Config, _scope.KeysOwnedElsewhere.ToArray())
            .Count;
        if (_draftCallsigns != AgentCallsigns.Enabled) changed++;

        SaveButton.IsEnabled = changed > 0;
        // Appended to the button's own label rather than standing alone, so the count reads
        // as part of the save it describes instead of as a caption under it.
        UnsavedText.Text = changed == 0
            ? ""
            : $"\u00b7 {changed} unsaved change{(changed == 1 ? "" : "s")}";
    }

    private void Setting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var toggle = (ToggleSwitch)sender;
        Stage((string)toggle.Tag, toggle.IsOn ? "true" : "false");
    }

    private void Setting_NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;

        // Clearing the box (its "X") or typing something invalid yields NaN. Don't stage it, and
        // don't leave the field empty/stuck — snap back to the last valid value so the spin buttons
        // keep working. If even the old value is gone, repopulate from the draft.
        if (double.IsNaN(args.NewValue))
        {
            if (!double.IsNaN(args.OldValue)) sender.Value = args.OldValue;
            else ReloadControls();
            return;
        }

        Stage((string)sender.Tag, ((long)args.NewValue).ToString());
    }

    private void Temperature_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        S_TemperatureLabel.Text = e.NewValue.ToString("0.##");
        Stage("temperature", e.NewValue.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Streaming_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || S_Streaming.SelectedItem is not string mode) return;
        Stage("streaming", mode);
    }

    /// <summary>Staged straight onto the draft rather than through ConfigKeySetter: that heals and
    /// validates URLs, which fights a half-typed one. The endpoint is probed for real on save.</summary>
    private void Endpoint_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || _draft == null) return;
        _draft.OllamaEndpoint = EndpointBox.Text.Trim();
        RefreshDirtyState();
    }

    private void ModelCombo_Changed(object sender, object e)
    {
        if (_loading || _draft == null) return;
        var picked = (ModelCombo.SelectedItem as string) ?? ModelCombo.Text;
        if (string.IsNullOrWhiteSpace(picked)) return;
        _draft.ModelName = picked.Trim();
        _draft.ModelPath = null;
        RefreshDirtyState();
    }

    /// <summary>App-wide naming style for new agents. Not a config key, so it's staged on its own
    /// field and committed alongside the draft.</summary>
    private void AgentCallsigns_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _draftCallsigns = S_AgentCallsigns.IsOn;
        RefreshDirtyState();
    }

    private void RunSetupWizard_Click(object sender, RoutedEventArgs e) => SetupWizardRequested?.Invoke();

    /// <summary>The wizard runs in a chat transcript, so it needs an agent to run in. The host keeps
    /// this in step with whether one is open.</summary>
    public bool SetupWizardEnabled
    {
        get => SetupWizardButton.IsEnabled;
        set
        {
            SetupWizardButton.IsEnabled = value;
            SetupWizardHint.Text = value
                ? "walks through connection + model, then saves them as the defaults"
                : "open an agent first — the wizard runs in a chat";
        }
    }

    // ============================================================
    // Saving
    // ============================================================

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_scope == null || _draft == null) return;

        var keyChanged = !string.Equals(_draft.TavilyApiKey, _scope.Config.TavilyApiKey, StringComparison.Ordinal);

        SaveButton.IsEnabled = false;
        SettingsStatus.Text = "Saving…";
        string message;
        try
        {
            message = await _scope.CommitAsync(_draft);
        }
        catch (Exception ex)
        {
            message = $"Couldn't save: {ex.Message}";
        }

        AgentCallsigns.Enabled = _draftCallsigns;
        if (keyChanged) SecretsChanged?.Invoke();

        // Re-clone from the now-updated live config: the draft is spent, and the commit may have
        // clamped a value or healed the endpoint, which the form should show.
        Reload();
        SettingsStatus.Text = message;

        SettingsChanged?.Invoke();
    }

    /// <summary>
    /// "Apply Global Defaults" — replaces this agent's settings with the saved defaults and puts it
    /// back to INHERITING them. Deliberately immediate rather than staged: it isn't an edit to one
    /// field, it discards the agent's whole config (any draft included) and re-points it.
    /// </summary>
    private async void ApplyDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (_scope is not AgentSettingsScope agentScope) return;

        var agent = agentScope.Session;
        ApplyDefaultsButton.IsEnabled = false;
        try
        {
            agent.ResetConfigToDefaults(agentScope.Configs);
            // The config changed underneath the live AI session — rebuild it so the new
            // endpoint/model/context take effect. History survives; see RefreshFromConfigAsync.
            await agent.Controller.RefreshFromConfigAsync();
        }
        finally
        {
            ApplyDefaultsButton.IsEnabled = true;
        }

        Reload();
        SettingsStatus.Text = $"{agent.Title} now matches the defaults for new agents, and will follow "
                            + "future changes to them until you save something here.";
        SettingsChanged?.Invoke();
    }

    /// <summary>
    /// "Save to Global Defaults" — makes this agent's settings the starting point for new agents.
    ///
    /// Works whatever state the form is in. Anything on screen but unsaved is committed to the
    /// agent FIRST, so the defaults can never end up holding values the agent they came from isn't
    /// actually running — the two always agree afterwards, and nothing is left pending.
    ///
    /// A side effect worth knowing: promoting leaves the agent identical to the defaults, so under
    /// the inheriting rule it drops its own saved file and follows later changes to them again.
    /// That is the honest outcome — it has no settings of its own left to keep — and the status
    /// line says so.
    /// </summary>
    private async void SaveToDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (_scope is not AgentSettingsScope agentScope || _draft == null) return;

        var session = agentScope.Session;
        var hadPendingEdits = HasUnsavedChanges;

        SaveToDefaultsButton.IsEnabled = false;
        SettingsStatus.Text = "Saving…";
        string message;
        try
        {
            if (hadPendingEdits)
            {
                await _scope.CommitAsync(_draft);
                AgentCallsigns.Enabled = _draftCallsigns;
            }

            session.Controller.SaveAsDefaults();
            // The agent now matches the defaults exactly, so this drops its own file: it is back to
            // inheriting rather than holding a private copy of what it just published.
            session.PersistConfigIfChanged();

            message = hadPendingEdits
                ? $"Saved to {session.Title} and copied to the defaults for new agents. "
                + "Agents already open keep their own."
                : $"Copied {session.Title}'s settings to the defaults for new agents. "
                + "Agents already open keep their own.";
        }
        catch (Exception ex)
        {
            message = $"Couldn't save to the defaults: {ex.Message}";
        }
        finally
        {
            SaveToDefaultsButton.IsEnabled = true;
        }

        Reload();
        SettingsStatus.Text = message;
        SettingsChanged?.Invoke();
    }

    /// <summary>
    /// Puts the visible tab back to the app's FACTORY values — the property initializers on
    /// MandoCodeConfig. Defaults scope only: an agent has the saved defaults to fall back on
    /// instead, which is what "Apply Global Defaults" is for. Staged like any other edit, so it
    /// needs a Save. Connection and the Tavily secret are left alone — not tunable knobs.
    /// </summary>
    private void ResetFactory_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;

        var d = new MandoCodeConfig();   // factory defaults (property initializers)
        var s = SettingsTabs.SelectedItem;

        static string Bool(bool b) => b ? "true" : "false";
        static string Num(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var resets = new List<(string Key, string Value)>();
        string tabName;

        if (s == Tab_Behavior)
        {
            tabName = "Behavior";
            resets.Add(("taskPlanning", Bool(d.EnableTaskPlanning)));
            resets.Add(("diffApprovals", Bool(d.EnableDiffApprovals)));
            resets.Add(("autoContinue", Bool(d.EnableAutoContinuation)));
            resets.Add(("maxContinuations", Num(d.MaxAutoContinuations)));
            resets.Add(("timeout", Num(d.RequestTimeoutMinutes)));
            resets.Add(("modelResponseTimeout", Num(d.ModelResponseTimeoutSeconds)));
            resets.Add(("toolBudget", Num(d.ToolResultCharBudget)));
            resets.Add(("renderTimeout", Num(d.MarkdownRenderTimeoutSeconds)));
        }
        else if (s == Tab_Integrations)
        {
            tabName = "Integrations";
            resets.Add(("webSearch", Bool(d.EnableWebSearch)));
        }
        else
        {
            tabName = "Model";
            resets.Add(("temperature", d.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
            resets.Add(("maxTokens", Num(d.MaxTokens)));
            resets.Add(("contextLength", Num(d.ContextLength)));
            resets.Add(("streaming", d.ResponseStreaming));
        }

        foreach (var (key, value) in resets) ConfigKeySetter.TrySet(_draft, key, value);

        ReloadControls();
        SettingsStatus.Text = $"{tabName} set to factory values — press Save to keep it.";
    }

    // ============================================================
    // Model picker
    // ============================================================

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RefreshModelListAsync();

    /// <summary>Fills the model picker without making the form wait on the network: the configured
    /// model is already known, so it shows selected on the first frame, then the installed-model
    /// list (a probe plus an Ollama /api/tags fetch, slow on cloud setups) fills in behind it.</summary>
    private async Task RefreshModelListAsync()
    {
        if (_scope == null) return;
        // No picker on this scope, so nothing to fill — and no reason to probe Ollama every time
        // the pane opens.
        if (ConnectionSection.Visibility != Visibility.Visible) return;

        // Instant: seed with the one model we already know, so the picker never sits empty. Only on
        // a first open — a manual refresh keeps the list it has until the new one arrives.
        var configured = (_draft ?? _scope.Config).GetEffectiveModelName();
        if (!string.IsNullOrEmpty(configured) &&
            (ModelCombo.ItemsSource is not IList<string> present || present.Count == 0))
        {
            _modelComboTarget = configured;
            SetModelItems(new List<string> { configured });
        }

        ModelListStatus.Text = "Fetching models…";
        var models = await _scope.ListModelsAsync();
        if (!string.IsNullOrEmpty(ModelCombo.Text)) _modelComboTarget = ModelCombo.Text;

        // A failed or empty fetch keeps whatever is already selectable. Replacing it with an empty
        // list would blank a picker that was showing the right answer a moment ago.
        if (models.Count == 0)
        {
            ModelListStatus.Text = "No models found — is Ollama running? (ollama serve, then ollama pull <model>)";
            return;
        }

        // A configured model the fetch doesn't list (a cloud model with nothing pulled locally)
        // still belongs in the picker — it is what the agent is actually using.
        if (!string.IsNullOrEmpty(_modelComboTarget) &&
            !models.Any(m => string.Equals(m, _modelComboTarget, StringComparison.OrdinalIgnoreCase)))
            models.Insert(0, _modelComboTarget);

        SetModelItems(ModelOrdering.Arrange(models));
        ModelListStatus.Text = $"{models.Count} model(s) available.";
    }

    /// <summary>Replaces the picker's items under the loading guard. Without it, re-projecting the
    /// list fires SelectionChanged and stages a "change" the user never made — lighting up Save on
    /// a page they only looked at.</summary>
    private void SetModelItems(IList<string> items)
    {
        _loading = true;
        try
        {
            ModelCombo.ItemsSource = items;
            ApplyModelComboTarget();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Selects the target model in the picker, falling back to the editable text when the
    /// list doesn't contain it. Selection is set by INDEX rather than by SelectedItem — an editable
    /// ComboBox drops a programmatic SelectedItem while its template isn't loaded.</summary>
    private void ApplyModelComboTarget()
    {
        if (string.IsNullOrEmpty(_modelComboTarget)) return;

        if (ModelCombo.ItemsSource is IList<string> items)
        {
            var idx = -1;
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i], _modelComboTarget, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx >= 0)
            {
                ModelCombo.SelectedIndex = idx;
                return;
            }
        }
        ModelCombo.Text = _modelComboTarget;
    }

    /// <summary>
    /// Pin toggle inside a dropdown row. Handled on Tapped rather than Click so the tap stops here
    /// instead of bubbling to the ComboBoxItem, which would otherwise treat pinning as picking the
    /// model and close the dropdown on the way out. Pinning is display order, not a setting — it
    /// applies immediately and is never part of the draft.
    /// </summary>
    private void ModelPin_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: string model } || string.IsNullOrWhiteSpace(model)) return;
        ModelOrdering.TogglePin(model);

        if (ModelCombo.ItemsSource is not IList<string> current) return;
        if (!string.IsNullOrEmpty(ModelCombo.Text)) _modelComboTarget = ModelCombo.Text;
        SetModelItems(ModelOrdering.Arrange(current));
    }

    // ============================================================
    // Tavily key (app-wide — defaults scope only)
    // ============================================================

    /// <summary>Staged like any other setting; the defaults scope's commit writes it to the shared
    /// config and fans it out to every live agent.</summary>
    private void TavilyKey_Changed(object sender, RoutedEventArgs e)
    {
        TavilyViewButton.IsEnabled = S_TavilyKey.Password.Length > 0;
        if (_loading || _draft == null) return;

        var typed = S_TavilyKey.Password.Trim();
        _draft.TavilyApiKey = typed.Length == 0 ? null : typed;
        RefreshDirtyState();
    }

    private void TavilyView_Click(object sender, RoutedEventArgs e)
    {
        var show = S_TavilyKey.PasswordRevealMode != PasswordRevealMode.Visible;
        S_TavilyKey.PasswordRevealMode = show ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        TavilyViewButton.Content = show ? "Hide" : "View";
    }

    // ============================================================
    // Tabs
    // ============================================================

    private void SettingsTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var s = sender.SelectedItem;
        TabPanel_Model.Visibility = s == Tab_Model ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Behavior.Visibility = s == Tab_Behavior ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Integrations.Visibility = s == Tab_Integrations ? Visibility.Visible : Visibility.Collapsed;

        // Reset acts on the visible tab, so its label names that tab.
        ResetFactoryText.Text = s == Tab_Behavior ? "Reset Behavior to Factory"
            : s == Tab_Integrations ? "Reset Integrations to Factory" : "Reset Model to Factory";
    }
}
