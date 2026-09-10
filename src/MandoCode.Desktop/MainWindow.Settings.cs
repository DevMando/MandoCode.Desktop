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

    /// <summary>Points the rail's Settings page at the global defaults. Called on page open, so
    /// the form always reflects a default another surface may have changed (a healed endpoint, an
    /// agent promoted with "Make Default for New Agents").</summary>
    private void LoadSettings()
    {
        DefaultsSettingsForm.Reload();
        _ = DefaultsSettingsForm.RefreshModelsAsync();
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
