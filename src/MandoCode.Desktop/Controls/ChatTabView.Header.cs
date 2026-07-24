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
    // Header / busy / plan progress
    // ============================================================

    public void UpdateHeader()
    {
        ModelText.Text = _controller.ModelName;
        ProjectRootText.Text = _controller.ProjectRootPath;
        ConnectionDot.Fill = new SolidColorBrush(
            _controller.ModelError ? Colors.Orange
            : _controller.IsConnected ? Colors.LimeGreen
            : Colors.Gray);

        var tracker = Session.Tokens;
        TokenText.Text = tracker.TotalSessionTokens > 0
            ? $"{MandoCode.Services.TokenTrackingService.FormatTokenCount(tracker.TotalSessionTokens)} tokens"
            : "";

        var processing = _controller.IsProcessing;
        SendIcon.Glyph = processing ? "" : "";   // stop vs send
        SendLabel.Text = processing ? "Stop" : "Send";
        ModelButton.IsEnabled = !processing;   // no model switch mid-turn

        RefreshBranchChip();

        HeaderChanged?.Invoke(this);
    }

    private void UpdateBusy(bool busy, string? activity)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        if (busy) BusyText.Text = string.IsNullOrWhiteSpace(activity) ? "Working..." : activity;
        else
        {
            // Turn just ended: refresh git state and snapshot it as the baseline for
            // detecting OUTSIDE-the-conversation changes before the next send.
            _wsTracker.MarkCapturePending();
            RefreshBranchChip(force: true);

            // Persist the model's full memory as of this turn (tier-3 full fidelity).
            // Off-thread: serialization of a long history shouldn't touch UI latency.
            var key = Session.PersistKey;
            var ai = Session.Ai;
            _ = Task.Run(() => SessionHistoryStore.Save(key, ai.ExportHistoryJson()));
        }
    }

    // ============================================================
    // Workspace-change notes for the model
    // ============================================================
    // The model only knows what happened inside the conversation. Anything else — the undo
    // button discarding its edits, files changed in another editor, external branch switches
    // — is invisible to it and leaves its picture of the working tree stale. The decision
    // logic lives in WorkspaceDeltaTracker (pure, unit-testable); this class only feeds it:
    // turn end → MarkCapturePending, git refresh → CaptureBaselineIfPending, watcher touch →
    // RecordTouch, send → EmitDelta. Notes queue on the controller (same pattern as reactions).

    private GitBranchInfo? _lastGitInfo;
    private readonly WorkspaceDeltaTracker _wsTracker = new();

    /// <summary>Called at send time: queues notes for whatever changed outside the
    /// conversation since the last turn ended, then re-baselines.</summary>
    private void EmitWorkspaceDelta()
    {
        foreach (var note in _wsTracker.EmitDelta(_lastGitInfo))
            _controller.NoteWorkspaceEvent(note);
    }

}
