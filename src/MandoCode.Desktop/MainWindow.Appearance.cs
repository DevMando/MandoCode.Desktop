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

    private void MediaBackground_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_appearanceReady) return;   // see _appearanceReady — a Save() here wipes settings
        ThemeManager.SetMediaBackground(MediaBackgroundToggle.IsOn);
        ApplyThemeToAllTabs();   // live — the filter attaches to the existing #bg layer
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

    /// <summary>Applies a theme picked from the Appearance page. Guarded on _appearanceReady like
    /// the opacity sliders: the constructor selects the live theme in this list, and that selection
    /// must not be mistaken for the user choosing one. No status line — the whole window repaints,
    /// and the header value beside the list already names the new theme.</summary>
    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_appearanceReady || ThemeList.SelectedItem is not ThemeVm vm) return;
        ThemeManager.Apply(vm.Theme, Root);
        ThemeHeaderValue.Text = vm.Theme.Name;
    }
}
