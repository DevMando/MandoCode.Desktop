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
    // Skills page — global (user) skills. All file work lives in SkillCoordinator; this is just
    // the UI + the fan-out call that makes a change land in every open agent's prompt.
    // ============================================================

    private string? _editingSkillFolder;

    // Full unfiltered set; the ListView shows whatever matches the search box (see ApplySkillFilter).
    private List<SkillRow> _allSkillRows = new();
    private string? _skillTagFilter;

    private void RefreshSkillsList()
    {
        _allSkillRows = _skillCoordinator.ListGlobalSkills().Select(s => new SkillRow
        {
            Name = s.Name,
            Description = s.Description,
            Body = s.Body,
            FolderPath = s.FolderPath,
            Enabled = s.Enabled,
            Tags = _itemTags.GetItemTags(TagScope.Skills, s.FolderPath),
        }).ToList();

        PopulateSkillTagFilter();
        ApplySkillFilter();
    }

    private void SkillSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ApplySkillFilter();

    private string _skillFilter = "all";

    private void PopulateSkillTagFilter()
    {
        var choices = new List<TagFilterOption> { new() };
        choices.AddRange(_itemTags.GetTags(TagScope.Skills).Select(tag => new TagFilterOption { Label = tag, Tag = tag }));
        SkillTagFilter.ItemsSource = choices;
        SkillTagFilter.SelectedItem = choices.FirstOrDefault(choice =>
            string.Equals(choice.Tag, _skillTagFilter, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    }

    private void SkillTagFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _skillTagFilter = (SkillTagFilter.SelectedItem as TagFilterOption)?.Tag;
        ApplySkillFilter();
    }

    private void SkillFilter_Click(object sender, RoutedEventArgs e)
    {
        _skillFilter = (string)((FrameworkElement)sender).Tag;
        // Single-select: light the chosen chip, clear the rest.
        SkillFilterAll.IsChecked = _skillFilter == "all";
        SkillFilterEnabled.IsChecked = _skillFilter == "enabled";
        SkillFilterDisabled.IsChecked = _skillFilter == "disabled";
        SkillFilterLarge.IsChecked = _skillFilter == "large";
        ApplySkillFilter();
    }

    /// <summary>Applies the search text + active chip, then groups the result into Enabled/Disabled
    /// sections. Runs on every refresh and keystroke, so filters survive enable/install/delete.</summary>
    private void ApplySkillFilter()
    {
        var q = SkillSearchBox.Text?.Trim() ?? "";
        IEnumerable<SkillRow> filtered = _allSkillRows;
        if (!string.IsNullOrEmpty(q))
            filtered = filtered.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Description.Contains(q, StringComparison.OrdinalIgnoreCase));
        filtered = _skillFilter switch
        {
            "enabled" => filtered.Where(r => r.Enabled),
            "disabled" => filtered.Where(r => !r.Enabled),
            "large" => filtered.Where(r => r.IsLarge),
            _ => filtered,
        };
        if (!string.IsNullOrWhiteSpace(_skillTagFilter))
            filtered = filtered.Where(row => row.Tags.Contains(_skillTagFilter, StringComparer.OrdinalIgnoreCase));
        var shown = filtered.ToList();

        // Group by state — Enabled first, Disabled below; empty sections omitted.
        var groups = new List<SkillRowGroup>();
        var en = shown.Where(r => r.Enabled).ToList();
        var dis = shown.Where(r => !r.Enabled).ToList();
        if (en.Count > 0) groups.Add(new SkillRowGroup($"Enabled ({en.Count})", en));
        if (dis.Count > 0) groups.Add(new SkillRowGroup($"Disabled ({dis.Count})", dis));

        var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = groups };
        SkillsList.ItemsSource = cvs.View;

        // Resetting ItemsSource clears the selection, so the selection-scoped buttons go with it.
        SkillEditButton.IsEnabled = false;
        SkillDeleteButton.IsEnabled = false;
        SkillBulkToggleButton.IsEnabled = shown.Count > 0;
        SkillBulkToggleButton.Content = shown.Any(row => row.Enabled) ? "Disable all" : "Enable all";

        var total = _allSkillRows.Count;
        var enabledTotal = _allSkillRows.Count(r => r.Enabled);
        var active = q.Length > 0 || _skillFilter != "all" || !string.IsNullOrWhiteSpace(_skillTagFilter);
        if (total == 0)
            SkillsPageStatus.Text = $"No global skills yet — “New Skill” or “Install from…” to add one.  ({_skillCoordinator.UserSkillsDirectory})";
        else if (active)
            SkillsPageStatus.Text = $"{shown.Count} of {total} shown  ·  {enabledTotal} enabled";
        else
            SkillsPageStatus.Text = $"{total} skill{(total == 1 ? "" : "s")}, {enabledTotal} enabled  ·  {_skillCoordinator.UserSkillsDirectory}";
    }

    private async void SkillManageTags_Click(object sender, RoutedEventArgs e)
    {
        await ShowTagManagerAsync(TagScope.Skills, "Skill tags");
        RefreshSkillsList();
    }

    private async void SkillBulkToggle_Click(object sender, RoutedEventArgs e)
    {
        var targets = FilteredSkillRows();
        var enable = !targets.Any(row => row.Enabled);
        foreach (var row in targets)
            _skillCoordinator.SetEnabled(row.FolderPath, enable);

        await _skillCoordinator.ReloadAllAsync();
        RefreshSkillsList();
        SkillsPageStatus.Text = enable ? $"Enabled {targets.Count} filtered skill(s)." : $"Disabled {targets.Count} filtered skill(s).";
    }

    private List<SkillRow> FilteredSkillRows()
    {
        var q = SkillSearchBox.Text?.Trim() ?? "";
        IEnumerable<SkillRow> rows = _allSkillRows;
        if (q.Length > 0)
            rows = rows.Where(row => row.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                     row.Description.Contains(q, StringComparison.OrdinalIgnoreCase));
        rows = _skillFilter switch
        {
            "enabled" => rows.Where(row => row.Enabled),
            "disabled" => rows.Where(row => !row.Enabled),
            "large" => rows.Where(row => row.IsLarge),
            _ => rows,
        };
        return string.IsNullOrWhiteSpace(_skillTagFilter)
            ? rows.ToList()
            : rows.Where(row => row.Tags.Contains(_skillTagFilter, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Reload every agent's skill set + prompt, then re-render the list and report.</summary>
    private async Task ApplySkillChangeAsync(string status)
    {
        await _skillCoordinator.ReloadAllAsync();
        RefreshSkillsList();
        SkillsPageStatus.Text = status;
    }

    private void SkillRefresh_Click(object sender, RoutedEventArgs e) => RefreshSkillsList();

    private void SkillsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var has = SkillsList.SelectedItem is SkillRow;
        SkillEditButton.IsEnabled = has;
        SkillDeleteButton.IsEnabled = has;
    }

    private void SkillsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SkillsList.SelectedItem is SkillRow row) OpenSkillEditor(row);
    }

    private async void SkillEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        // Toggled also fires while the list realizes rows and binds IsOn from the row. In that case
        // the new state equals the row's stored state — a no-op we must ignore, or realizing the
        // list would rewrite files. A real user flip makes the two differ.
        if (sender is not ToggleSwitch sw || sw.DataContext is not SkillRow row) return;
        if (sw.IsOn == row.Enabled) return;

        try
        {
            _skillCoordinator.SetEnabled(row.FolderPath, sw.IsOn);
            await ApplySkillChangeAsync(sw.IsOn ? $"Enabled “{row.Name}”." : $"Disabled “{row.Name}”.");
        }
        catch (Exception ex)
        {
            SkillsPageStatus.Text = ex.Message;
        }
    }

    private void SkillNew_Click(object sender, RoutedEventArgs e) => OpenSkillEditor(null);

    private void SkillEdit_Click(object sender, RoutedEventArgs e)
    {
        if (SkillsList.SelectedItem is SkillRow row) OpenSkillEditor(row);
    }

    private async void SkillDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SkillsList.SelectedItem is not SkillRow row) return;

        var dialog = new ContentDialog
        {
            Title = "Delete skill",
            Content = $"Delete “{row.Name}”? This removes its folder from disk and can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _skillCoordinator.DeleteSkill(row.FolderPath);
            await ApplySkillChangeAsync($"Deleted “{row.Name}”.");
        }
        catch (Exception ex)
        {
            SkillsPageStatus.Text = ex.Message;
        }
    }

    private void SkillOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _skillCoordinator.UserSkillsDirectory;
        try { System.IO.Directory.CreateDirectory(dir); }
        catch (Exception ex) { SkillsPageStatus.Text = ex.Message; return; }
        if (ShellOpen.Try(dir) is { } err) SkillsPageStatus.Text = err.Message;
    }

    // ---- Skill editor modal ----

    private void OpenSkillEditor(SkillRow? row)
    {
        Sk_StatusBar.IsOpen = false;
        if (row == null)
        {
            _editingSkillFolder = null;
            SkillEditorTitle.Text = "New skill";
            Sk_Name.Text = "";
            Sk_Description.Text = "";
            Sk_Body.Text = "";
            Sk_Tags.Text = "";
        }
        else
        {
            _editingSkillFolder = row.FolderPath;
            SkillEditorTitle.Text = "Edit skill";
            Sk_Name.Text = row.Name;
            Sk_Description.Text = row.Description;
            Sk_Body.Text = row.Body;
            Sk_Tags.Text = string.Join(", ", row.Tags);
        }

        // Reset the AI panel and default its model to the active agent's (still changeable).
        Sk_AiIntent.Text = "";
        SetSkillAiBusy(false, "");
        _ = LoadSkillAuthorModelsAsync(_sessions.Active?.Controller.ModelName ?? "");

        UpdateSkillBodySize();   // explicit: setting Text="" above won't fire TextChanged if already empty
        SkillEditorOverlay.Visibility = Visibility.Visible;
        Sk_Name.Focus(FocusState.Programmatic);
    }

    private void Sk_Body_TextChanged(object sender, TextChangedEventArgs e) => UpdateSkillBodySize();

    /// <summary>Live size readout for the instructions body — approximate tokens, gold when large,
    /// matching the size column in the skills list.</summary>
    private void UpdateSkillBodySize()
    {
        var chars = Sk_Body.Text?.Length ?? 0;
        var tokens = (chars + 3) / 4;
        var large = tokens >= 2000;
        Sk_BodySize.Text = (tokens >= 1000 ? $"≈{tokens / 1000.0:0.0}k tokens" : $"≈{tokens} tokens")
            + (large ? " · large — heavy on local models" : "");
        Sk_BodySize.Foreground = new SolidColorBrush(
            ThemeManager.C(large ? ThemeManager.Current.Gold : ThemeManager.Current.Dim));
    }

    /// <summary>Fills the AI model dropdown: the active agent's model shown selected instantly, then
    /// the full installed-model list streamed in behind it. Mirrors the snapshot picker.</summary>
    private async Task LoadSkillAuthorModelsAsync(string activeModel)
    {
        if (string.IsNullOrWhiteSpace(activeModel))
        {
            Sk_AiModel.ItemsSource = null;
            return;
        }

        var current = new ModelChoice(activeModel, MandoCodeConfig.IsCloudModel(activeModel));
        Sk_AiModel.ItemsSource = new List<ModelChoice> { current };
        Sk_AiModel.SelectedIndex = 0;

        var result = await _controller.LoadAvailableModelsAsync();
        if (!result.Ok || result.Models.Count == 0) return;

        // If the user already picked another model while the list loaded, don't clobber it.
        if ((Sk_AiModel.SelectedItem as ModelChoice)?.Name != activeModel) return;

        var choices = result.Models
            .Select(m => new ModelChoice(m, MandoCodeConfig.IsCloudModel(m)))
            .ToList();
        if (!choices.Any(c => string.Equals(c.Name, activeModel, StringComparison.OrdinalIgnoreCase)))
            choices.Insert(0, current);

        Sk_AiModel.ItemsSource = choices;
        Sk_AiModel.SelectedItem =
            choices.First(c => string.Equals(c.Name, activeModel, StringComparison.OrdinalIgnoreCase));
    }

    private void SetSkillAiBusy(bool busy, string status)
    {
        Sk_AiSpin.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Sk_AiSpin.IsActive = busy;
        Sk_GenerateButton.IsEnabled = !busy;
        Sk_RefineButton.IsEnabled = !busy;
        Sk_AiStatus.Text = status;
    }

    private async void SkillGenerate_Click(object sender, RoutedEventArgs e)
    {
        var intent = Sk_AiIntent.Text.Trim();
        if (intent.Length == 0) { Sk_AiStatus.Text = "Describe what the skill should do first."; return; }
        if (Sk_AiModel.SelectedItem is not ModelChoice model) { Sk_AiStatus.Text = "Pick a model first."; return; }

        Sk_StatusBar.IsOpen = false;
        SetSkillAiBusy(true, "Drafting…");
        try
        {
            var endpoint = _sessions.Active!.Config.OllamaEndpoint;
            var draft = await SkillAuthor.GenerateAsync(endpoint, model.Name, intent);
            if (!string.IsNullOrWhiteSpace(draft.Name)) Sk_Name.Text = draft.Name;
            if (!string.IsNullOrWhiteSpace(draft.Description)) Sk_Description.Text = draft.Description;
            if (!string.IsNullOrWhiteSpace(draft.Body)) Sk_Body.Text = draft.Body;
            SetSkillAiBusy(false, "Draft ready — review and edit before saving.");
        }
        catch (Exception ex)
        {
            SetSkillAiBusy(false, "");
            ShowSkillEditorError($"AI draft failed: {ex.Message}");
        }
    }

    private async void SkillRefine_Click(object sender, RoutedEventArgs e)
    {
        var instruction = Sk_AiIntent.Text.Trim();
        if (instruction.Length == 0) { Sk_AiStatus.Text = "Type what to change in the box above."; return; }
        if (Sk_Body.Text.Trim().Length == 0) { Sk_AiStatus.Text = "Nothing to refine yet — write or generate instructions first."; return; }
        if (Sk_AiModel.SelectedItem is not ModelChoice model) { Sk_AiStatus.Text = "Pick a model first."; return; }

        Sk_StatusBar.IsOpen = false;
        SetSkillAiBusy(true, "Refining…");
        try
        {
            var endpoint = _sessions.Active!.Config.OllamaEndpoint;
            var body = await SkillAuthor.RefineAsync(endpoint, model.Name, Sk_Body.Text, instruction);
            if (!string.IsNullOrWhiteSpace(body)) Sk_Body.Text = body;
            SetSkillAiBusy(false, "Instructions updated.");
        }
        catch (Exception ex)
        {
            SetSkillAiBusy(false, "");
            ShowSkillEditorError($"AI refine failed: {ex.Message}");
        }
    }

    private void SkillEditorCancel_Click(object sender, RoutedEventArgs e) =>
        SkillEditorOverlay.Visibility = Visibility.Collapsed;

    private void ShowSkillEditorError(string message)
    {
        Sk_StatusBar.Title = "Check the form";
        Sk_StatusBar.Message = message;
        Sk_StatusBar.IsOpen = true;
    }

    private async void SkillEditorSave_Click(object sender, RoutedEventArgs e)
    {
        var name = Sk_Name.Text.Trim();
        if (name.Length == 0) { ShowSkillEditorError("Give the skill a name."); return; }
        if (Sk_Body.Text.Trim().Length == 0) { ShowSkillEditorError("The instructions can't be empty."); return; }

        try
        {
            var folder = _skillCoordinator.SaveSkill(_editingSkillFolder, name, Sk_Description.Text, Sk_Body.Text);
            if (!string.IsNullOrWhiteSpace(_editingSkillFolder))
                _itemTags.RenameItem(TagScope.Skills, _editingSkillFolder, folder);
            _itemTags.SetItemTags(TagScope.Skills, folder, SplitTags(Sk_Tags.Text));
            SkillEditorOverlay.Visibility = Visibility.Collapsed;
            await ApplySkillChangeAsync($"Saved “{name}”.");
        }
        catch (Exception ex)
        {
            ShowSkillEditorError(ex.Message);
        }
    }

    // ---- Skill install modal ----

    private void SkillInstall_Click(object sender, RoutedEventArgs e)
    {
        Sk_InstallMode.SelectedIndex = 0;   // Git by default
        Sk_InstallGitPanel.Visibility = Visibility.Visible;
        Sk_InstallLocalPanel.Visibility = Visibility.Collapsed;
        Sk_InstallGitUrl.Text = "";
        Sk_InstallLocalPath.Text = "";
        Sk_InstallStatus.Text = "";
        Sk_InstallError.IsOpen = false;
        Sk_InstallSpin.IsActive = false;
        Sk_InstallSpin.Visibility = Visibility.Collapsed;
        SkillInstallConfirmButton.IsEnabled = true;
        SkillInstallOverlay.Visibility = Visibility.Visible;
        Sk_InstallGitUrl.Focus(FocusState.Programmatic);
    }

    private void SkillInstallMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent before the panels exist.
        if (Sk_InstallGitPanel == null || Sk_InstallLocalPanel == null) return;
        var local = Sk_InstallMode.SelectedIndex == 1;
        Sk_InstallGitPanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        Sk_InstallLocalPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SkillBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        // SettingsIdentifier keeps this picker's "last visited folder" separate from every
        // other picker in the app — see ChatTabView.Explorer.cs's OpenFolderButton_Click.
        var picker = new Windows.Storage.Pickers.FolderPicker { SettingsIdentifier = "SkillInstallFolder" };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) Sk_InstallLocalPath.Text = folder.Path;
    }

    private async void SkillBrowseZip_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SettingsIdentifier = "SkillInstallZip" };
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file != null) Sk_InstallLocalPath.Text = file.Path;
    }

    private void SkillInstallCancel_Click(object sender, RoutedEventArgs e) =>
        SkillInstallOverlay.Visibility = Visibility.Collapsed;

    private async void SkillInstallConfirm_Click(object sender, RoutedEventArgs e)
    {
        var source = (Sk_InstallMode.SelectedIndex == 1 ? Sk_InstallLocalPath.Text : Sk_InstallGitUrl.Text).Trim();
        if (source.Length == 0)
        {
            Sk_InstallError.Title = "Nothing to install";
            Sk_InstallError.Message = "Enter a git URL, a .zip path, or a folder path.";
            Sk_InstallError.IsOpen = true;
            return;
        }

        Sk_InstallError.IsOpen = false;
        Sk_InstallSpin.Visibility = Visibility.Visible;
        Sk_InstallSpin.IsActive = true;
        Sk_InstallStatus.Text = "Fetching…";
        SkillInstallConfirmButton.IsEnabled = false;

        try
        {
            // Clone / extract / copy can block; keep it off the UI thread.
            var result = await Task.Run(() => _skillCoordinator.InstallFrom(source));

            // Nothing found: keep the modal open so the user can fix the source, and say what a
            // valid source looks like. (finally still resets the spinner/button below.)
            if (result.Installed.Count == 0 && result.Skipped.Count == 0)
            {
                Sk_InstallError.Title = "No skills found";
                Sk_InstallError.Message = "That source has no SKILL.md. A skill is a folder containing a SKILL.md file — point at one, or at a folder/repo/.zip that holds them (nested is fine).";
                Sk_InstallError.IsOpen = true;
                return;
            }

            SkillInstallOverlay.Visibility = Visibility.Collapsed;
            await _skillCoordinator.ReloadAllAsync();
            RefreshSkillsList();

            var parts = new List<string>();
            if (result.Installed.Count > 0) parts.Add($"installed {string.Join(", ", result.Installed)}");
            if (result.Skipped.Count > 0) parts.Add($"skipped (already present): {string.Join(", ", result.Skipped)}");
            SkillsPageStatus.Text = string.Join("  ·  ", parts);
        }
        catch (Exception ex)
        {
            Sk_InstallError.Title = "Install failed";
            Sk_InstallError.Message = ex.Message;
            Sk_InstallError.IsOpen = true;
        }
        finally
        {
            Sk_InstallSpin.IsActive = false;
            Sk_InstallSpin.Visibility = Visibility.Collapsed;
            Sk_InstallStatus.Text = "";
            SkillInstallConfirmButton.IsEnabled = true;
        }
    }

}
