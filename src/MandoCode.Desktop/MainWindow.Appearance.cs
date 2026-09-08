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
    // Chat background image (Appearance page)
    // ============================================================

    private async void BgChoose_Click(object sender, RoutedEventArgs e)
    {
        // SettingsIdentifier keeps this picker's "last visited folder" separate from every
        // other picker in the app — see ChatTabView.Explorer.cs's OpenFolderButton_Click.
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SettingsIdentifier = "ChatBackground" };
        // Desktop apps must marry the picker to an HWND before use.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" })
            picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        ThemeManager.SetChatBackground(file.Path);
        UpdateBgControls();
        ApplyThemeToAllTabs();
    }

    /// <summary>Picks one of the backgrounds that shipped with the app. It goes through the same
    /// copy-and-serve path as a file the user chose — the only extra is recording WHICH shipped image
    /// it was, so the gallery can mark it after a restart. Clicking the active tile turns it off
    /// again, so a tile is a toggle rather than a one-way trip.</summary>
    private void BgBuiltIn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BackgroundChoiceVm choice) return;

        if (choice.IsSelected) ThemeManager.SetChatBackground(null);
        else ThemeManager.SetChatBackground(choice.Item.FullPath, choice.Item.FileName);

        UpdateBgControls();
        ApplyThemeToAllTabs();
    }

    private void BgClear_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.SetChatBackground(null);
        UpdateBgControls();
        ApplyThemeToAllTabs();
    }

    private void BoxedMessages_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_appearanceReady) return;   // see _appearanceReady — a Save() here wipes settings
        ThemeManager.SetBoxedMessages(BoxedMessagesToggle.IsOn);
        ApplyThemeToAllTabs();   // live — existing messages re-skin instantly
    }

    private void BgOpacity_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_appearanceReady) return;   // see _appearanceReady — a Save() here wipes settings
        S_BgOpacityLabel.Text = $"{(int)e.NewValue}%";
        ThemeManager.SetChatBackgroundOpacity(e.NewValue / 100.0);
        ApplyThemeToAllTabs();   // live preview while dragging — the script is tiny
    }

    private void UpdateBgControls()
    {
        var hasImage = ThemeManager.ChatBackgroundFile != null;
        var builtIn = BuiltInBackgrounds.Find(ThemeManager.ChatBackgroundBuiltIn);

        // Naming the shipped image beats "Image set ✓" — with a gallery on the page, the label is
        // what tells you whether you're on one of ours or your own file.
        BgFileLabel.Text = builtIn != null ? builtIn.DisplayName
                         : hasImage ? "Your own image ✓"
                         : "No image set";
        BgClearButton.IsEnabled = hasImage;
        S_BgOpacity.IsEnabled = hasImage;

        // Rebuilt wholesale so the selection ring re-evaluates — the tiles bind OneTime.
        var shipped = BuiltInBackgrounds.All;
        BgBuiltInPanel.Visibility = shipped.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BgBuiltInList.ItemsSource = shipped
            .Select(b => new BackgroundChoiceVm
            {
                Item = b,
                IsSelected = b.FileName.Equals(ThemeManager.ChatBackgroundBuiltIn, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();

        // The preview WebView renders the image itself (via the userdata host + theme
        // script), so there is no XAML image to update here anymore.
    }

    // WinUI has no Window.Opacity — whole-window translucency is a Win32 layered-window
    // attribute on the HWND. At 100% the layered style is removed entirely so the
    // compositor does no extra work for the default solid window.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 0x2;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    private void ApplyWindowOpacity(double opacity)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (opacity >= 0.995)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle & ~(nint)WS_EX_LAYERED);
        }
        else
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle | (nint)WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(opacity * 255), LWA_ALPHA);
        }
    }

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ThemeList.SelectedItem is not ThemeVm vm) return;
        ThemeManager.Apply(vm.Theme, Root);
        ThemeHeaderValue.Text = vm.Theme.Name;
        SettingsStatus.Text = $"Theme set to {vm.Theme.Name}.";
    }

    private string _modelComboTarget = "";

    /// <summary>An editable ComboBox drops programmatic Text while its template isn't
    /// loaded (the Settings page starts collapsed) — so the intended model name is kept
    /// here and re-applied on the combo's Loaded event. Selecting the matching pulled
    /// model when one exists also marks it in the dropdown.</summary>
    private void ApplyModelComboTarget()
    {
        if (_modelComboTarget.Length == 0) return;
        if (ModelCombo.ItemsSource is IList<string> models)
        {
            var idx = models.IndexOf(_modelComboTarget);
            if (idx >= 0)
            {
                ModelCombo.SelectedIndex = idx;
                return;
            }
        }
        ModelCombo.Text = _modelComboTarget;
    }

    /// <summary>One write path for the whole page: ConfigKeySetter via the controller.</summary>
    private async Task ApplySettingAsync(string key, string value)
    {
        var (ok, message) = await _controller.ApplyConfigKeyAsync(key, value);
        SettingsStatus.Text = message;
        if (!ok) LoadSettings();   // revert the control to the real value
    }

    private async void Setting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var toggle = (ToggleSwitch)sender;
        await ApplySettingAsync((string)toggle.Tag, toggle.IsOn ? "true" : "false");
    }

    private async void Setting_NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingSettings) return;

        // Clearing the box (its "X") or typing something invalid yields NaN. Don't apply it, and
        // don't leave the field empty/stuck — snap back to the last valid value so the spin buttons
        // keep working. If even the old value is gone, reload the whole form from config.
        if (double.IsNaN(args.NewValue))
        {
            if (!double.IsNaN(args.OldValue)) sender.Value = args.OldValue;
            else LoadSettings();
            return;
        }

        await ApplySettingAsync((string)sender.Tag, ((long)args.NewValue).ToString());
    }

    private async void Temperature_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loadingSettings) return;
        S_TemperatureLabel.Text = e.NewValue.ToString("0.##");
        await ApplySettingAsync("temperature", e.NewValue.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    private async void Streaming_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || S_Streaming.SelectedItem is not string mode) return;
        await ApplySettingAsync("streaming", mode);
    }

    /// <summary>Enables View as soon as there's anything to reveal (saved key or fresh typing).</summary>
    private void TavilyKey_Changed(object sender, RoutedEventArgs e) =>
        TavilyViewButton.IsEnabled = S_TavilyKey.Password.Length > 0;

    private void TavilyView_Click(object sender, RoutedEventArgs e)
    {
        var show = S_TavilyKey.PasswordRevealMode != PasswordRevealMode.Visible;
        S_TavilyKey.PasswordRevealMode = show ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        TavilyViewButton.Content = show ? "Hide" : "View";
    }

    private async void TavilySave_Click(object sender, RoutedEventArgs e)
    {
        var key = S_TavilyKey.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            SettingsStatus.Text = "Enter a key first (or type 'clear' to remove the saved one).";
            return;
        }
        await ApplySettingAsync("tavilyKey", key.Trim());
        LoadSettings();
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) =>
        await RefreshModelListAsync();

    /// <summary>
    /// Pin toggle inside a dropdown row. Handled on Tapped rather than Click so the tap stops here
    /// instead of bubbling to the ComboBoxItem, which would otherwise treat pinning as picking the
    /// model and close the dropdown on the way out.
    /// </summary>
    private void ModelPin_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: string model } || string.IsNullOrWhiteSpace(model)) return;
        ModelOrdering.TogglePin(model);

        // Re-project the same names so the row's glyph re-renders and the pinned model moves up.
        if (ModelCombo.ItemsSource is not IList<string> current) return;
        if (!string.IsNullOrEmpty(ModelCombo.Text)) _modelComboTarget = ModelCombo.Text;
        ModelCombo.ItemsSource = ModelOrdering.Arrange(current);
        ApplyModelComboTarget();
    }

    /// <summary>Fills the model picker without making the page wait on the network: the configured
    /// model is already known, so it is shown selected on the first frame, then the installed-model
    /// list (a probe plus an Ollama /api/tags fetch, slow on cloud setups) fills in behind it for
    /// "pick another". Same approach as LoadSnapshotModelsAsync.</summary>
    private async Task RefreshModelListAsync()
    {
        // Instant: seed with the one model we already know, so the picker never sits empty. Only on
        // a first open — a manual refresh keeps the list it has until the new one arrives.
        var configured = _controller.Config.GetEffectiveModelName();
        if (!string.IsNullOrEmpty(configured) &&
            (ModelCombo.ItemsSource is not IList<string> present || present.Count == 0))
        {
            _modelComboTarget = configured;
            ModelCombo.ItemsSource = new List<string> { configured };
            ApplyModelComboTarget();
        }

        ModelListStatus.Text = "Fetching models…";
        var models = await Task.Run(_controller.ListModelsAsync);
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

        ModelCombo.ItemsSource = ModelOrdering.Arrange(models);
        ApplyModelComboTarget();
        ModelListStatus.Text = $"{models.Count} model(s) available.";
    }

    private async void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = EndpointBox.Text;
        var model = ModelCombo.Text;
        SettingsStatus.Text = "Connecting… (details land in the chat transcript)";
        await Task.Run(() => _controller.ApplyConnectionSettingsAsync(endpoint, model));
        SettingsStatus.Text = _controller.IsConnected
            ? $"✓ Connected — {_controller.ModelName}"
            : "Couldn't connect — see the chat transcript for details.";
        LoadSettings();
    }

}
