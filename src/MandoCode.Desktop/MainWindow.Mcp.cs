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
    // MCP page
    // ============================================================

    private async void McpRefresh_Click(object sender, RoutedEventArgs e) => await RefreshMcpListAsync();

    // Full unfiltered set; the list shows what matches the search box (see ApplyMcpFilter).
    private List<McpRow> _allMcpRows = new();
    private bool _loadingMcp;

    private async Task RefreshMcpListAsync()
    {
        // Servers are shared across agents and enabled/disabled per-server now, so MCP is always on
        // at the agent level. Make sure the active agent actually attaches tools (new agents inherit
        // EnableMcp=true from defaults; this only fires for an agent someone turned off previously).
        if (!_controller.Config.EnableMcp)
            await ApplySettingAsync("mcp", "true");

        McpPageStatus.Text = "Checking server status…";
        var rows = await Task.Run(_controller.GetMcpStatusRowsAsync);

        var green = (SolidColorBrush)Application.Current.Resources["MandoGreenBrush"];
        var gold = (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"];
        _allMcpRows = rows.Select(r => new McpRow
        {
            Name = r.Name,
            Transport = r.Transport,
            Status = r.Status,
            StatusBrush = r.Connected ? green : gold,
            Enabled = !r.Disabled,
        }).ToList();

        ApplyMcpFilter();
    }

    private void McpSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ApplyMcpFilter();

    private string _mcpFilter = "all";

    private void McpFilter_Click(object sender, RoutedEventArgs e)
    {
        _mcpFilter = (string)((FrameworkElement)sender).Tag;
        McpFilterAll.IsChecked = _mcpFilter == "all";
        McpFilterEnabled.IsChecked = _mcpFilter == "enabled";
        McpFilterDisabled.IsChecked = _mcpFilter == "disabled";
        McpFilterFailed.IsChecked = _mcpFilter == "failed";
        ApplyMcpFilter();
    }

    /// <summary>Applies search + active chip, then groups into Enabled/Disabled sections. The
    /// programmatic ItemsSource set realizes rows (firing each toggle), guarded in McpEnabled_Toggled.</summary>
    private void ApplyMcpFilter()
    {
        var q = McpSearchBox.Text?.Trim() ?? "";
        IEnumerable<McpRow> filtered = _allMcpRows;
        if (!string.IsNullOrEmpty(q))
            filtered = filtered.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Transport.Contains(q, StringComparison.OrdinalIgnoreCase));
        filtered = _mcpFilter switch
        {
            "enabled" => filtered.Where(r => r.Enabled),
            "disabled" => filtered.Where(r => !r.Enabled),
            "failed" => filtered.Where(r => r.Status.StartsWith("failed", StringComparison.OrdinalIgnoreCase)),
            _ => filtered,
        };
        var shown = filtered.ToList();

        var groups = new List<McpRowGroup>();
        var en = shown.Where(r => r.Enabled).ToList();
        var dis = shown.Where(r => !r.Enabled).ToList();
        if (en.Count > 0) groups.Add(new McpRowGroup($"Enabled ({en.Count})", en));
        if (dis.Count > 0) groups.Add(new McpRowGroup($"Disabled ({dis.Count})", dis));

        var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = groups };
        _loadingMcp = true;
        McpList.ItemsSource = cvs.View;
        _loadingMcp = false;

        McpEditButton.IsEnabled = false;
        McpRemoveButton.IsEnabled = false;

        var total = _allMcpRows.Count;
        var enabledTotal = _allMcpRows.Count(r => r.Enabled);
        var active = q.Length > 0 || _mcpFilter != "all";
        if (total == 0)
            McpPageStatus.Text = "No MCP servers configured yet — “Add MCP Server” to connect one.";
        else if (active)
            McpPageStatus.Text = $"{shown.Count} of {total} shown  ·  {enabledTotal} enabled";
        else
            McpPageStatus.Text = $"{total} server{(total == 1 ? "" : "s")}, {enabledTotal} enabled";
    }

    /// <summary>Per-server on/off. Flips the shared config's Disabled flag and saves, which restarts
    /// the servers and re-registers tools on every agent (SaveMcpServerAsync → coordinator reload).</summary>
    private async void McpEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        // Fires while the list realizes rows and binds IsOn — ignore those (state already matches).
        if (_loadingMcp) return;
        if (sender is not ToggleSwitch sw || sw.DataContext is not McpRow row) return;
        if (sw.IsOn == row.Enabled) return;

        // Edit the canonical defaults entry (what SaveMcpServerAsync persists), flip Disabled, save.
        if (!_configs.Defaults.McpServers.TryGetValue(row.Name, out var server)) return;
        server.Disabled = !sw.IsOn;

        McpPageStatus.Text = sw.IsOn ? $"Enabling “{row.Name}”…" : $"Disabling “{row.Name}”…";
        await Task.Run(() => _controller.SaveMcpServerAsync(row.Name, row.Name, server));

        // Do not replace the grouped ItemsSource for a single toggle. Recreating the list makes
        // every row animate back into place and moves this server between groups while the user is
        // still looking at it. The durable config and live agent tools are already updated above;
        // the current view is reconciled when the page is opened again, filtered, or refreshed.
        row.Enabled = sw.IsOn;
        McpPageStatus.Text = sw.IsOn ? $"Enabled “{row.Name}”." : $"Disabled “{row.Name}”.";
    }

    /// <summary>Runs a slash command through the normal pipeline (transcript echo, wizard
    /// overlays, busy state all included), then refreshes the server list.</summary>
    private async Task RunMcpCommandAsync(string command)
    {
        if (_controller.IsProcessing)
        {
            McpPageStatus.Text = "Busy — wait for the current request to finish.";
            return;
        }
        await Task.Run(() => _controller.SubmitAsync(command));
        await RefreshMcpListAsync();
    }

    private void McpAdd_Click(object sender, RoutedEventArgs e) => OpenMcpEditor(null);

    private void McpEdit_Click(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpRow row)
        {
            McpPageStatus.Text = "Select a server to edit first.";
            return;
        }
        OpenMcpEditor(row.Name);
    }

    private void McpList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = McpList.SelectedItem is McpRow;
        McpEditButton.IsEnabled = hasSelection;
        McpRemoveButton.IsEnabled = hasSelection;
    }

    private void McpList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (McpList.SelectedItem is McpRow row) OpenMcpEditor(row.Name);
    }

    private async void McpRemove_Click(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpRow row)
        {
            McpPageStatus.Text = "Select a server to remove first.";
            return;
        }
        await RunMcpCommandAsync($"/mcp remove {row.Name}");
    }

    private async void McpReload_Click(object sender, RoutedEventArgs e) =>
        await RunMcpCommandAsync("/mcp-reload");

    // ============================================================
    // MCP server editor modal (add + edit)
    // ============================================================

    private string? _mcpEditOriginalName;

    private void OpenMcpEditor(string? serverName)
    {
        _mcpEditOriginalName = serverName;
        M_StatusBar.IsOpen = false;
        M_TestToolsTable.Visibility = Visibility.Collapsed;
        M_TestSpin.Visibility = Visibility.Collapsed;
        McpEditorTestButton.IsEnabled = true;
        McpEditorSaveButton.IsEnabled = true;

        if (serverName != null && _controller.Config.McpServers.TryGetValue(serverName, out var cfg))
        {
            McpEditorTitle.Text = $"Edit MCP server — {serverName}";
            McpEditorSaveButton.Content = "Save & Reconnect";
            M_Name.Text = serverName;
            M_Transport.SelectedIndex = cfg.IsHttp ? 1 : 0;
            M_Command.Text = cfg.Command ?? "";
            M_Args.Text = string.Join(" ", cfg.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            M_Env.Text = string.Join("\n", cfg.Env.Select(kv => $"{kv.Key}={kv.Value}"));
            M_Url.Text = cfg.Url ?? "";
            M_Headers.Text = string.Join("\n", cfg.Headers.Select(kv => $"{kv.Key}={kv.Value}"));
            M_Disabled.IsOn = cfg.Disabled;
        }
        else
        {
            McpEditorTitle.Text = "Add MCP server";
            McpEditorSaveButton.Content = "Save & Connect";
            M_Name.Text = "";
            M_Transport.SelectedIndex = 0;
            M_Command.Text = "";
            M_Args.Text = "";
            M_Env.Text = "";
            M_Url.Text = "";
            M_Headers.Text = "";
            M_Disabled.IsOn = false;
        }

        UpdateMcpTransportPanels();
        McpEditorOverlay.Visibility = Visibility.Visible;
        M_Name.Focus(FocusState.Programmatic);
    }

    private void M_Transport_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateMcpTransportPanels();

    private void UpdateMcpTransportPanels()
    {
        // Guard: fires during InitializeComponent before panels exist.
        if (M_StdioPanel == null || M_HttpPanel == null) return;
        var isHttp = M_Transport.SelectedIndex == 1;
        M_HttpPanel.Visibility = isHttp ? Visibility.Visible : Visibility.Collapsed;
        M_StdioPanel.Visibility = isHttp ? Visibility.Collapsed : Visibility.Visible;
    }

    private void McpEditorCancel_Click(object sender, RoutedEventArgs e) =>
        McpEditorOverlay.Visibility = Visibility.Collapsed;

    private void ShowMcpEditorError(string message)
    {
        M_TestSpin.IsActive = false;
        M_TestSpin.Visibility = Visibility.Collapsed;
        M_TestToolsTable.Visibility = Visibility.Collapsed;
        M_StatusBar.Severity = InfoBarSeverity.Error;
        M_StatusBar.Title = "Check the form";
        M_StatusBar.Message = message;
        M_StatusBar.IsOpen = true;
    }

    /// <summary>Parses "KEY=value" lines. Returns null (with an error shown) on a bad line.</summary>
    private Dictionary<string, string>? ParseKeyValueLines(string text, string label)
    {
        var dict = new Dictionary<string, string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                ShowMcpEditorError($"{label}: '{line}' isn't KEY=value.");
                return null;
            }
            dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return dict;
    }

    /// <summary>Shared validate-and-build for Test and Save. Shows the error inline and
    /// returns false when the form isn't valid.</summary>
    private bool TryBuildServerFromForm(bool checkNameCollision, out string name, out MandoCode.Models.McpServerConfig server)
    {
        M_StatusBar.IsOpen = false;
        server = new MandoCode.Models.McpServerConfig { Disabled = M_Disabled.IsOn };

        // Lowercased — servers are referenced by name in tool prefixes (mcp_<server>).
        name = M_Name.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) { ShowMcpEditorError("Name cannot be empty."); return false; }
        if (name.Contains(' ')) { ShowMcpEditorError("Name cannot contain spaces."); return false; }
        if (checkNameCollision && _mcpEditOriginalName == null && _controller.Config.McpServers.ContainsKey(name))
        {
            ShowMcpEditorError($"A server named '{name}' already exists — edit it instead, or pick another name.");
            return false;
        }

        if (M_Transport.SelectedIndex == 1)   // http
        {
            var url = M_Url.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                ShowMcpEditorError("URL must be absolute (e.g. https://mcp.example.com/mcp).");
                return false;
            }
            server.Url = url;
            server.Transport = "http";

            var headers = ParseKeyValueLines(M_Headers.Text, "Headers");
            if (headers == null) return false;
            server.Headers = headers;
        }
        else                                   // stdio
        {
            var command = M_Command.Text.Trim();
            if (string.IsNullOrWhiteSpace(command)) { ShowMcpEditorError("Command cannot be empty."); return false; }
            server.Command = command;
            server.Args = ChatController.ParseShellLikeArgs(M_Args.Text.Trim());

            var env = ParseKeyValueLines(M_Env.Text, "Environment variables");
            if (env == null) return false;
            server.Env = env;
        }

        return true;
    }

    private async void McpEditorTest_Click(object sender, RoutedEventArgs e)
    {
        // No collision check — testing an existing name is fine, nothing is written.
        if (!TryBuildServerFromForm(checkNameCollision: false, out var name, out var server)) return;

        M_StatusBar.Severity = InfoBarSeverity.Informational;
        M_StatusBar.Title = "Testing connection…";
        M_StatusBar.Message = "Connecting with these values — nothing is saved, running servers aren't touched.";
        M_StatusBar.IsOpen = true;
        M_TestToolsTable.Visibility = Visibility.Collapsed;
        M_TestSpin.Visibility = Visibility.Visible;
        M_TestSpin.IsActive = true;
        McpEditorTestButton.IsEnabled = false;
        McpEditorSaveButton.IsEnabled = false;

        try
        {
            var result = await Task.Run(() => _controller.TestMcpServerAsync(name, server));

            M_TestSpin.IsActive = false;
            M_TestSpin.Visibility = Visibility.Collapsed;

            if (result.Ok)
            {
                M_StatusBar.Severity = InfoBarSeverity.Success;
                M_StatusBar.Title = $"Connected — {result.Tools.Count} tool(s)";
                M_StatusBar.Message = result.Message;
                if (result.Tools.Count > 0)
                {
                    M_TestTools.ItemsSource = result.Tools
                        .Select(t => new ToolChip { Name = t.Name, Description = t.Description ?? "(no description)" })
                        .ToList();
                    M_TestToolsTable.Visibility = Visibility.Visible;
                }
            }
            else
            {
                M_StatusBar.Severity = InfoBarSeverity.Error;
                M_StatusBar.Title = "Connection failed";
                M_StatusBar.Message = result.Message;
            }
        }
        finally
        {
            M_TestSpin.IsActive = false;
            McpEditorTestButton.IsEnabled = true;
            McpEditorSaveButton.IsEnabled = true;
        }
    }

    private async void McpEditorSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildServerFromForm(checkNameCollision: true, out var name, out var server)) return;

        McpEditorOverlay.Visibility = Visibility.Collapsed;
        SwitchPage("mcp");
        McpPageStatus.Text = $"Saving '{name}' and connecting…";

        var originalName = _mcpEditOriginalName;
        var (_, message) = await Task.Run(() => _controller.SaveMcpServerAsync(originalName, name, server));
        McpPageStatus.Text = message;
        await RefreshMcpListAsync();
        McpPageStatus.Text = message;
    }

}
