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
    // Appearance-page live preview — a real miniature transcript
    // ============================================================
    // Same shell + theme script as the tabs, so it shows EXACTLY what they show (background
    // image, boxed messages, E-Ink dithering, W98 chrome). Failures never break settings —
    // the preview is a luxury.

    private bool _previewWebReady;

    private async void InitBgPreview()
    {
        try
        {
            await BgPreviewWeb.EnsureCoreWebView2Async();
            var core = BgPreviewWeb.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;

            // Same virtual hosts the tabs map: assets (highlight.js) + userdata (bg image).
            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    "mandocode.assets",
                    System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "web"),
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { /* already mapped / missing assets — preview still renders */ }
            try
            {
                Directory.CreateDirectory(ThemeManager.UserDataFolder);
                core.SetVirtualHostNameToFolderMapping(
                    "mandocode.userdata", ThemeManager.UserDataFolder,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { /* already mapped / no user-data folder — preview still renders */ }

            core.NavigationCompleted += (_, _) =>
            {
                if (_previewWebReady) return;
                _previewWebReady = true;
                _ = SeedBgPreviewAsync();
            };
            core.NavigateToString(TranscriptHtmlBuilder.BaseDocument(ThemeManager.Current));
        }
        catch { /* no preview — settings still fully functional */ }
    }

    private async Task SeedBgPreviewAsync()
    {
        try
        {
            var blocks =
                _html.UserEcho("how does this look?") +
                _html.AssistantCard(
                    "Like this — the image fades, the text never does.\n\n" +
                    "Inline `code` and a block, to judge every surface:\n\n" +
                    "```csharp\nvar vibe = \"immaculate\";\n```");
            await BgPreviewWeb.CoreWebView2.ExecuteScriptAsync(
                "window.__append(" + JsonSerializer.Serialize(blocks) + ");");
            await BgPreviewWeb.CoreWebView2.ExecuteScriptAsync(
                ThemeManager.BuildTranscriptScript(ThemeManager.Current));
        }
        catch { /* preview seeding is cosmetic; the real transcript is unaffected */ }
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { ActiveChat?.HandleEscape(); return; }

        // Ctrl+`  toggles the terminal;  Ctrl+Shift+`  opens a new shell tab (VS Code parity).
        // 192 == VK_OEM_3 (backtick/tilde). Handled here rather than via a KeyboardAccelerator:
        // WinUI fast-fails natively when an accelerator is registered on an OEM key.
        if (e.Key == (VirtualKey)192 && IsDown(VirtualKey.Control))
        {
            e.Handled = true;
            if (IsDown(VirtualKey.Shift)) { OpenTerminalPanel(); _terminal!.NewTerminalTab(); }
            else ToggleTerminal();
        }
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void ApplyThemeToAllTabs()
    {
        foreach (var tab in _tabs) tab.View.ApplyTheme();
        // The appearance preview is a transcript too — it re-themes with everyone else.
        if (_previewWebReady && BgPreviewWeb.CoreWebView2 != null)
            _ = BgPreviewWeb.CoreWebView2.ExecuteScriptAsync(
                ThemeManager.BuildTranscriptScript(ThemeManager.Current));
    }

    private void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

}
