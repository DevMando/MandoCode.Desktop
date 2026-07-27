using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using MandoCode.Models;
using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MandoCode.Desktop;

public sealed partial class MainWindow
{
    // ============================================================
    // Settings page
    // ============================================================

    private bool _loadingSettings;

    /// <summary>Populates every control from the live config. Guarded so control-change
    /// events fired during population don't write back.</summary>
    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            // The SELECTED agent's config, not the saved defaults. Switch agents and this page
            // shows different values.
            var cfg = _controller.Config;
            SettingsAgentChip.Text = _sessions.Active?.Title ?? "";
            EndpointBox.Text = cfg.OllamaEndpoint;
            _modelComboTarget = cfg.GetEffectiveModelName();
            ApplyModelComboTarget();
            S_ContextLength.Value = cfg.ContextLength;
            S_Temperature.Value = cfg.Temperature;
            S_TemperatureLabel.Text = cfg.Temperature.ToString("0.##");
            S_MaxTokens.Value = cfg.MaxTokens;
            S_Streaming.SelectedItem = cfg.ResponseStreaming;
            S_AgentCallsigns.IsOn = AgentCallsigns.Enabled;   // app-wide, not from the agent's config
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
            for (int i = 0; i < UiTheme.All.Count; i++)
                if (UiTheme.All[i] == ThemeManager.Current) ThemeList.SelectedIndex = i;
            SettingsStatus.Text = "";
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    /// <summary>App-wide naming style for new agents — applies immediately (like Appearance),
    /// not through the agent config / Make Default flow the rest of the page uses.</summary>
    private void AgentCallsigns_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        AgentCallsigns.Enabled = S_AgentCallsigns.IsOn;
        SavePanelState();
    }

    /// <summary>Runs the guided /setup wizard in the active agent's chat — the same flow that
    /// fires on first launch. Routed through SubmitAsync so it gets the standard command echo
    /// and the is-processing guard.</summary>
    private void RunSetupWizard_Click(object sender, RoutedEventArgs e)
    {
        SwitchPage("chat");
        _ = Task.Run(() => _controller.SubmitAsync("/setup"));
    }

    private void SettingsTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var s = sender.SelectedItem;
        TabPanel_Model.Visibility = s == Tab_Model ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Behavior.Visibility = s == Tab_Behavior ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Integrations.Visibility = s == Tab_Integrations ? Visibility.Visible : Visibility.Collapsed;

        // "Reset" acts on the visible tab, so its label names that tab.
        ResetTabButtonText.Text = s == Tab_Behavior ? "Reset Behavior"
            : s == Tab_Integrations ? "Reset Integrations" : "Reset Model";
        // Every remaining tab is per-agent now (Appearance moved to its own rail page), so
        // "Make Default for New Agents" always applies.
    }

    /// <summary>False until the constructor has loaded persisted appearance settings into the
    /// sliders. The sliders' XAML default Values fire ValueChanged during InitializeComponent —
    /// BEFORE ThemeManager.Initialize reads ui-settings.json — and a Save() in that window
    /// overwrites the file with defaults (that bug ate users' saved background image).</summary>
    private bool _appearanceReady;

    private void WindowOpacity_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_appearanceReady) return;
        S_WindowOpacityLabel.Text = $"{(int)e.NewValue}%";
        ThemeManager.SetWindowOpacity(e.NewValue / 100.0);
        ApplyWindowOpacity(ThemeManager.WindowOpacity);
    }

}
