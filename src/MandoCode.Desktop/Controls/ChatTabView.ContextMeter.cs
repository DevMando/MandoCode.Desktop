using MandoCode.Desktop.Services;
using MandoCode.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MandoCode.Desktop;

public sealed partial class ChatTabView
{
    // ============================================================
    // Context meter (status strip, left of the git chip)
    // ============================================================

    /// <summary>
    /// Shows how full this agent's context window is. "Used" is the last request's prompt tokens:
    /// every request resends the whole conversation, so that's what the next one starts from. Local
    /// models get a bar against the configured context length; a cloud model manages its window
    /// server-side, so it gets the count alone. Runs on every header refresh (token updates raise
    /// one) and on theme changes, since the fill color is derived from the theme.
    /// </summary>
    private void UpdateContextMeter()
    {
        var lastOp = Session.Tokens.LastOperation;
        if (!Session.Config.EnableTokenTracking || lastOp == null || lastOp.PromptTokens <= 0)
        {
            ContextMeterPanel.Visibility = Visibility.Collapsed;
            UpdateStatusStripVisibility();
            return;
        }

        var window = ContextMeter.KnownWindow(Session.Config.GetEffectiveModelName(), Session.Config.ContextLength);
        var reading = ContextMeter.Read(lastOp.PromptTokens, window);
        var theme = ThemeManager.Current;

        var hasBar = window > 0;
        ContextMeterTrack.Visibility = hasBar ? Visibility.Visible : Visibility.Collapsed;
        if (hasBar)
        {
            ContextMeterTrack.Background = new SolidColorBrush(ThemeManager.C(ContextMeterColors.Track(theme)));
            ContextMeterFill.Background = new SolidColorBrush(ThemeManager.C(ContextMeterColors.Fill(theme, reading.Level)));
            var fraction = Math.Clamp((double)lastOp.PromptTokens / window, 0, 1);
            ContextMeterFill.Width = ContextMeterTrack.Width * fraction;
        }

        // A full window also says so in words, so the warning never rests on color alone.
        ContextMeterText.Text = reading.Level == ContextMeter.Level.Full ? $"⚠ {reading.Label}" : reading.Label;
        ToolTipService.SetToolTip(ContextMeterPanel, hasBar
            ? $"Context window: the last request used {reading.Label}. "
              + "Every request resends the conversation, so this is where the next one starts. "
              + "Near full, /compact shrinks it."
            : $"{reading.Label}. This cloud model manages its context window server-side.");

        ContextMeterPanel.Visibility = Visibility.Visible;
        UpdateStatusStripVisibility();
    }

    /// <summary>The strip shows when either of its parts has something to show.</summary>
    private void UpdateStatusStripVisibility() =>
        StatusStrip.Visibility =
            ContextMeterPanel.Visibility == Visibility.Visible || GitStatusPanel.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
}
