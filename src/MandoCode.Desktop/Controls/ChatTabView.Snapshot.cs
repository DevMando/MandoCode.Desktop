using System.Collections.ObjectModel;
using System.Text.Json;
using MandoCode.Models;
using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MandoCode.Desktop;

public sealed partial class ChatTabView
{
    // ============================================================
    // Create-snapshot offer card — shown when the controller buffers a conversation (on a model
    // switch or "Take snapshot"). The user picks a summarizer model and creates, or dismisses to
    // discard. Snapshots are born summarized; there is no light/un-enhanced state.
    // ============================================================

    /// <summary>Shows or hides the top offer to match the controller's pending buffer. Stage 1 is the
    /// thin notification bar; the full picker is built only when the user clicks Create on it.</summary>
    private void RefreshSnapshotOffer()
    {
        var offer = _controller.PendingOffer;
        if (offer == null)
        {
            SnapshotOfferRoot.Visibility = Visibility.Collapsed;
            return;
        }

        // A manual "Take snapshot" is an explicit decision — skip the notification bar and open the
        // name+model picker straight away. Model switches keep stage 1, since there the user may
        // just want to keep working (or "keep memory") rather than snapshot at all.
        if (offer.IsManual)
        {
            ShowSnapshotPickerCard(offer);
            return;
        }

        // Stage 1: notification bar. Non-blocking — the user can ignore it and keep prompting.
        // "Keep memory" only appears when a switch actually cleared a conversation.
        SnapshotKeepMemoryButton.Visibility = _controller.CanCarryMemory
            ? Visibility.Visible : Visibility.Collapsed;
        SnapshotNotifyText.Text = _controller.CanCarryMemory
            ? $"Keep the {offer.OriginModel} conversation going, or save it as a snapshot?"
            : $"Snapshot available — save the {offer.OriginModel} conversation.";
        SnapshotNotifyBar.Visibility = Visibility.Visible;
        SnapshotOfferCard.Visibility = Visibility.Collapsed;
        SnapshotOfferRoot.Visibility = Visibility.Visible;
        SlideSnapshotOfferIn();
    }

    /// <summary>Stage 2: expand into the full name + model picker. Reached either from the
    /// notification bar's Create (model switches) or directly for a manual "Take snapshot". Hangs at
    /// the top until the user creates or dismisses.</summary>
    private void ShowSnapshotPickerCard(ChatController.PendingSnapshot offer)
    {
        SnapshotOfferSubtitle.Text =
            $"{offer.MessageCount} message{(offer.MessageCount == 1 ? "" : "s")} from {offer.OriginModel} — "
            + "name it (or leave blank and the summarizer will), pick a model, and create.";
        SnapshotCreateButton.Content = "Create";
        SnapshotNameBox.Text = "";   // a fresh offer starts unnamed
        // Reset any leftover busy state from a prior, interrupted attempt.
        SnapshotBusyPanel.Visibility = Visibility.Collapsed;
        SnapshotBusyRing.IsActive = false;
        SnapshotOfferContent.Opacity = 1;
        SnapshotOfferContent.IsHitTestVisible = true;
        SnapshotNotifyBar.Visibility = Visibility.Collapsed;
        SnapshotOfferCard.Visibility = Visibility.Visible;
        SnapshotOfferRoot.Visibility = Visibility.Visible;
        SlideSnapshotOfferIn();
        _ = LoadSnapshotModelsAsync(offer.OriginModel);
    }

    private void SnapshotNotifyCreate_Click(object sender, RoutedEventArgs e)
    {
        var offer = _controller.PendingOffer;
        if (offer != null) ShowSnapshotPickerCard(offer);
    }

    /// <summary>Drops the offer down from the top of the transcript with a short fade.</summary>
    private void SlideSnapshotOfferIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation
        {
            From = -18, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(slide, SnapshotOfferTransform);
        Storyboard.SetTargetProperty(slide, "Y");
        var fade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, SnapshotOfferRoot);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Children.Add(fade);
        sb.Begin();
    }

    /// <summary>Populates the model picker without making the card wait on a network round-trip: the
    /// model that had the conversation is shown selected instantly, then the full installed-model list
    /// (an Ollama /api/tags fetch, slow on cloud setups) streams in behind it for "pick another."</summary>
    private async Task LoadSnapshotModelsAsync(string originModel)
    {
        // Instant: seed with just the current model so the card is usable with zero lag.
        var current = new ModelChoice(originModel, MandoCodeConfig.IsCloudModel(originModel));
        SnapshotModelCombo.ItemsSource = new List<ModelChoice> { current };
        SnapshotModelCombo.SelectedIndex = 0;
        SnapshotModelCombo.IsEnabled = true;
        SnapshotCreateButton.IsEnabled = true;

        // Background: fetch the rest so the dropdown fills in for choosing another model.
        var result = await _controller.LoadAvailableModelsAsync();
        if (!result.Ok || result.Models.Count == 0) return;   // keep the single current entry

        // Guard against a race: if a newer offer/switch swapped models while we were fetching, don't
        // clobber its selection with this stale list.
        if ((SnapshotModelCombo.SelectedItem as ModelChoice)?.Name != originModel) return;

        var choices = result.Models
            .Select(m => new ModelChoice(m, MandoCodeConfig.IsCloudModel(m)))
            .ToList();
        if (!choices.Any(c => string.Equals(c.Name, originModel, StringComparison.OrdinalIgnoreCase)))
            choices.Insert(0, current);   // keep the current model even if the list omits it

        SnapshotModelCombo.ItemsSource = choices;
        SnapshotModelCombo.SelectedItem =
            choices.First(c => string.Equals(c.Name, originModel, StringComparison.OrdinalIgnoreCase));
    }

    private async void SnapshotCreate_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotModelCombo.SelectedItem is not ModelChoice choice)
        {
            _transcript.Append(_html.Warn("Pick a model to summarize with first."));
            return;
        }

        // Summarizing can take a while (especially a cloud model), so show a clear busy state:
        // fade the controls out and spin, with the snapshot's name in the message when it has one.
        var name = SnapshotNameBox.Text?.Trim() ?? "";
        SnapshotBusyText.Text = string.IsNullOrEmpty(name)
            ? "Creating snapshot…"
            : $"Creating “{name}” snapshot…";
        SetSnapshotBusy(true);

        var error = await _controller.CreateSnapshotAsync(choice.Name, name);
        if (error != null)
        {
            SetSnapshotBusy(false);
            _transcript.Append(_html.Warn(error));
        }
        // On success the controller clears the offer → SnapshotOfferChanged → RefreshSnapshotOffer
        // hides the whole thing, and a "Snapshot saved" chip lands in the transcript.
    }

    /// <summary>Toggles the create card's busy state: fades the inputs out (and blocks them) while a
    /// centered spinner + "Creating…" text shows.</summary>
    private void SetSnapshotBusy(bool busy)
    {
        SnapshotBusyRing.IsActive = busy;
        SnapshotBusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SnapshotOfferContent.IsHitTestVisible = !busy;

        var fade = new DoubleAnimation
        {
            To = busy ? 0.25 : 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, SnapshotOfferContent);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Begin();
    }

    private void SnapshotOfferDismiss_Click(object sender, RoutedEventArgs e)
        => _controller.DismissSnapshotOffer();

    /// <summary>"Keep memory": verbatim continuation on the new model. On success the offer
    /// clears itself (SnapshotOfferChanged → RefreshSnapshotOffer); on failure the bar stays
    /// so Snapshot remains available as the salvage path.</summary>
    private void SnapshotKeepMemory_Click(object sender, RoutedEventArgs e)
        => _controller.TryCarryMemoryAcrossSwitch();

    /// <summary>Saves this tab's transcript as a standalone HTML page. Shared by the header save
    /// button and the tab's options menu.</summary>
    public async Task ExportTranscriptAsync()
    {
        var core = CanScript ? TranscriptView.CoreWebView2 : null;   // captured: see AppendRawAsync
        if (core == null) return;
        try
        {
            var json = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
            var html = JsonSerializer.Deserialize<string>(json) ?? "";

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            picker.FileTypeChoices.Add("HTML page", new List<string> { ".html" });
            picker.SuggestedFileName = $"mandocode-transcript-{DateTime.Now:yyyy-MM-dd-HHmm}";
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            await Windows.Storage.FileIO.WriteTextAsync(file, "<!DOCTYPE html>\n" + html);
            _transcript.Append(_html.Success($"Transcript saved to {file.Path}"));
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Warn($"Couldn't save transcript: {ex.Message}"));
        }
    }

    /// <summary>Opens a clicked transcript path with its default app (folders open in Explorer).
    /// Relative paths — how operation cards display them — resolve against THIS tab's root.</summary>
    private void OpenTranscriptPath(string raw)
    {
        try
        {
            var path = raw.Trim();
            if (!Path.IsPathRooted(path)) path = Path.Combine(Session.ProjectRoot.ProjectRoot, path);
            path = Path.GetFullPath(path);
            if (File.Exists(path) || Directory.Exists(path))
            {
                if (ShellOpen.Try(path) is { } ex1)
                    _transcript.Append(_html.Warn($"Couldn't open file: {ex1.Message}"));
            }
            else
                _transcript.Append(_html.Warn($"Can't open — no longer exists: {path}"));
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Warn($"Couldn't open file: {ex.Message}"));
        }
    }

    private static void OpenInBrowser(string url)
        => ShellOpen.Try(url);   // a dead link must not crash the app — the launch failure is swallowed

}
