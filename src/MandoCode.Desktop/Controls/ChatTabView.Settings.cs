using MandoCode.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

// ChatTabView lives in MandoCode.Desktop (not .Controls) despite its file sitting under Controls/ —
// match its other partials or this one silently becomes a separate, unused class.
namespace MandoCode.Desktop;

/// <summary>
/// The per-agent settings pane — the gear in this tab's header, between the snapshot and folder
/// buttons. Hosts the SAME <see cref="SettingsForm"/> the rail's Settings page does, bound to this
/// agent instead of to the global defaults, so the two surfaces can never drift apart.
///
/// Docked rather than overlaid, like the file explorer: opening it narrows the transcript instead
/// of covering the conversation you are changing settings for.
/// </summary>
public sealed partial class ChatTabView
{
    private bool _agentSettingsOpen;

    /// <summary>The form is created with the tab but only subscribed to on first open — and only
    /// once. Re-subscribing on every open would stack duplicate handlers and fire UpdateHeader N
    /// times per edit.</summary>
    private bool _agentSettingsWired;

    /// <summary>Whether this tab's settings pane is showing. The state is per-tab, so switching
    /// tabs doesn't carry one agent's open pane onto another.</summary>
    public bool AgentSettingsOpen => _agentSettingsOpen;

    private void AgentSettingsButton_Click(object sender, RoutedEventArgs e) =>
        ToggleAgentSettings(!_agentSettingsOpen);

    private void AgentSettingsClose_Click(object sender, RoutedEventArgs e) => ToggleAgentSettings(false);

    private void ToggleAgentSettings(bool open)
    {
        if (open == _agentSettingsOpen) return;
        _agentSettingsOpen = open;

        AgentSettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AgentSettingsSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (open)
        {
            // Re-bind on every open rather than once at construction: the agent's config moves
            // underneath this pane (a model switch from the header, /config set in the chat, "Match
            // Global Defaults"), and the form must show what is true now.
            if (!_agentSettingsWired)
            {
                _agentSettingsWired = true;
                // A model or endpoint change from in here has to reach the header, which shows both.
                // (The guided wizard is NOT wired here — it configures the app, so it lives on the
                // rail's Default Settings page.)
                AgentSettingsForm.SettingsChanged += UpdateHeader;
            }

            AgentSettingsForm.Bind(new AgentSettingsScope(
                Session, App.Services.GetRequiredService<ConfigCoordinator>()));
            AgentSettingsTitle.Text = $"{Session.Title} — settings";
            _ = AgentSettingsForm.RefreshModelsAsync();
            SizeAgentSettings();
        }

        UpdateHeaderButtonStates();
    }

    /// <summary>Keeps the pane a sensible share of the tab, clamped so the form stays usable on a
    /// small window and doesn't eat half a wide one. A width the user has dragged to wins.</summary>
    private void SizeAgentSettings()
    {
        var w = ChatRoot.ActualWidth;
        if (w <= 0) return;
        var target = _agentSettingsUserWidth ?? Math.Clamp(w * 0.30, 340, 520);
        AgentSettingsPanel.Width = Math.Clamp(target, MinAgentSettingsWidth, MaxAgentSettingsWidth());
    }

    private const double MinAgentSettingsWidth = 320;

    /// <summary>Leaves room for the transcript and for whatever else is already docked, so opening
    /// the explorer and this pane together can't squeeze the conversation to nothing.</summary>
    private double MaxAgentSettingsWidth() => Math.Max(MinAgentSettingsWidth,
        ChatRoot.ActualWidth
        - (_explorerOpen ? ExplorerPanel.ActualWidth : 0)
        - (_previewOpen ? PreviewPanel.ActualWidth : 0)
        - 320);

    // --- splitter drag (same pointer-capture pattern as the explorer's) ---

    private double? _agentSettingsUserWidth;   // set on first drag; SizeAgentSettings defers to it
    private bool _draggingAgentSettings;
    private double _agentSettingsDragStartWidth;
    private double _agentSettingsDragStartX;

    private void AgentSettingsSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingAgentSettings = true;
        _agentSettingsDragStartWidth = AgentSettingsPanel.ActualWidth;
        _agentSettingsDragStartX = e.GetCurrentPoint(ChatRoot).Position.X;   // stable frame while the grip moves
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void AgentSettingsSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingAgentSettings) return;
        // Dragging left grows the pane; right shrinks it.
        var delta = e.GetCurrentPoint(ChatRoot).Position.X - _agentSettingsDragStartX;
        var next = Math.Clamp(_agentSettingsDragStartWidth - delta, MinAgentSettingsWidth, MaxAgentSettingsWidth());
        AgentSettingsPanel.Width = next;
        _agentSettingsUserWidth = next;
    }

    private void AgentSettingsSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingAgentSettings) return;
        _draggingAgentSettings = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    /// <summary>Tints the gear while its pane is open, the same signal the explorer button gives.</summary>
    private void UpdateHeaderButtonStates()
    {
        AgentSettingsButton.Foreground = _agentSettingsOpen
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MandoAccentBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MandoTextBrush"];
    }
}
