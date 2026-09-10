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
    // Sidebar navigation — Settings and MCP are full-screen pages, not tabs. They act on
    // whichever agent is selected, so switching pages never changes which agent that is.
    // ============================================================

    private string _currentPage = "chat";

    private void NavChat_Click(object sender, RoutedEventArgs e) => SwitchPage("chat");

    // Settings/MCP act as toggles: clicking the one you're already on closes it and returns to the
    // last active agent, rather than reloading the page in place.
    private void NavSettings_Click(object sender, RoutedEventArgs e)
        => SwitchPage(_currentPage == "settings" ? "chat" : "settings");
    private void NavMcp_Click(object sender, RoutedEventArgs e)
        => SwitchPage(_currentPage == "mcp" ? "chat" : "mcp");
    private void NavSkills_Click(object sender, RoutedEventArgs e)
        => SwitchPage(_currentPage == "skills" ? "chat" : "skills");
    private void NavAppearance_Click(object sender, RoutedEventArgs e)
        => SwitchPage(_currentPage == "appearance" ? "chat" : "appearance");

    private void SwitchPage(string page)
    {
        // Settings and MCP act on the selected agent — with none open there's nothing to edit, so
        // fall back to the (empty) chat. Skills and Appearance are app-global and stay reachable.
        if ((page == "settings" || page == "mcp") && _sessions.Active == null) page = "chat";

        _currentPage = page;
        var showingChat = page == "chat";

        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        McpPage.Visibility = page == "mcp" ? Visibility.Visible : Visibility.Collapsed;
        SkillsPage.Visibility = page == "skills" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = page == "appearance" ? Visibility.Visible : Visibility.Collapsed;

        // Glide the full-screen page in from the rail side (translate + fade). Both run on the
        // composition thread, so the whole page slides smoothly regardless of how much it holds.
        if (page == "settings") SlideInPage(SettingsPage, SettingsPageTransform);
        else if (page == "mcp") SlideInPage(McpPage, McpPageTransform);
        else if (page == "skills") SlideInPage(SkillsPage, SkillsPageTransform);
        else if (page == "appearance") SlideInPage(AppearancePage, AppearancePageTransform);

        // Every agent view stays loaded; only the visible one(s) show, and only on the chat page.
        // Collapsing rather than removing is what keeps each WebView2's transcript alive. In split
        // mode 2–4 views show at once (the pane set, _splitPanes, in pane order).
        ApplyPaneLayout();

        // The empty-state background shows only on the chat page with no agents left.
        EmptyAgentsState.Visibility = showingChat && _tabs.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        _ = RefreshEmptyBackgroundAsync();

        RefreshNavIcons();
        // Re-evaluate the approval toast for the new page — leaving the chat can newly "hide" the
        // selected agent's approval, which should now raise the toast (and returning clears it).
        RefreshTabStrip();

        switch (page)
        {
            case "settings":
                LoadSettings();   // SettingsForm.Reload — it fetches its own model list
                break;
            case "mcp":
                _ = RefreshMcpListAsync();
                break;
            case "skills":
                RefreshSkillsList();
                break;
            default:
                ActiveChat?.FocusInput();
                break;
        }
    }

    /// <summary>Slides a full-screen page (Settings/MCP) into view from the rail side, with a short
    /// fade. Translate and Opacity are independent animations, so this stays smooth on the
    /// composition thread no matter how much the page contains.</summary>
    private static void SlideInPage(UIElement page, TranslateTransform transform)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slide = new DoubleAnimation
        {
            From = -48,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Children.Add(fade);
        sb.Begin();
    }

    private void RefreshNavIcons()
    {
        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];
        var normal = (SolidColorBrush)Application.Current.Resources["MandoDimBrush"];
        var gold = (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"];

        // An approval waiting in ANY agent while you're on Settings/MCP: the agents icon goes gold,
        // because from here you can't see which tab is badged.
        var approvalPending = _currentPage != "chat" && _tabs.Any(t => t.View.IsApprovalOpen);

        NavChatIcon.Foreground = _currentPage == "chat" ? accent : (approvalPending ? gold : normal);
        NavSettingsIcon.Foreground = _currentPage == "settings" ? accent : normal;
        NavMcpIcon.Foreground = _currentPage == "mcp" ? accent : normal;
        NavSkillsIcon.Foreground = _currentPage == "skills" ? accent : normal;
        NavAppearanceIcon.Foreground = _currentPage == "appearance" ? accent : normal;
        NavSnapshotsIcon.Foreground = SnapshotsPanelOpen ? accent : normal;
        NavHistoryIcon.Foreground = HistoryPanelOpen ? accent : normal;
        NavNotesIcon.Foreground = NotesPanelOpen ? accent : normal;
        NavTerminalIcon.Foreground = _terminalOpen ? accent : normal;

        // Unread agent output is cleared the moment the user is actually looking at an agent's
        // tab — not merely when the panel is open, since it may well be open on a shell.
        if (_terminalOpen && (_terminal?.ActiveTabIsAgentOutput ?? false)) _agentOutputUnread = false;
        var showBadge = _agentOutputUnread;
        NavTerminalBadge.Visibility = showBadge ? Visibility.Visible : Visibility.Collapsed;

        // Motion means "a command is running right now"; a still dot means "output is waiting".
        // FlatMotion themes opt out of animation entirely, so they get the still dot in both cases
        // rather than a pulse the theme has said it doesn't want.
        var pulse = showBadge && AnyAgentCommandRunning && !ThemeManager.Current.FlatMotion;
        if (pulse)
        {
            if (!_navTerminalBadgePulsing) { _navTerminalBadgePulsing = true; NavTerminalBadgePulse.Begin(); }
        }
        else if (_navTerminalBadgePulsing)
        {
            // Stopped, not just hidden: an animation left running behind a collapsed element keeps
            // the compositor busy for no visible benefit.
            _navTerminalBadgePulsing = false;
            NavTerminalBadgePulse.Stop();
            NavTerminalBadge.Opacity = 1.0;
        }

        ToolTipService.SetToolTip(NavTerminal, showBadge
            ? (AnyAgentCommandRunning
                ? "Terminal — agent is running a command (Ctrl+`)"
                : "Terminal — agent output waiting (Ctrl+`)")
            : "Terminal — open a shell, or watch agent commands (Ctrl+`)");

        // MCP acts on the selected agent, so it needs one open. Settings no longer does — it edits
        // the defaults for FUTURE agents, which is exactly the thing you might want to set up before
        // opening any. (An agent's own settings are behind its gear icon instead.)
        NavMcp.IsEnabled = _sessions.Active != null;
        // The guided wizard renders into a chat transcript, so it needs one open even though what
        // it configures is app-wide.
        DefaultsSettingsForm.SetupWizardEnabled = _sessions.Active != null;
        ToolTipService.SetToolTip(NavChat, approvalPending ? "Agents — approval waiting" : "Agents");
    }

}
