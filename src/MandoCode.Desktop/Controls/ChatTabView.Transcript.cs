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
    // Transcript
    // ============================================================

    private bool _journalRestored;

    /// <summary>Replays this session's journaled transcript into the fresh WebView — via
    /// ExecuteScript directly, NOT through TranscriptWriter (that would re-journal every
    /// block). Chunked so a long history is a few script calls, not a thousand.</summary>
    private async Task RestoreJournaledTranscriptAsync()
    {
        if (_journalRestored) return;
        _journalRestored = true;
        try
        {
            var blocks = TranscriptJournal.Load(Session.PersistKey)
                .Where(b => !TranscriptHtmlBuilder.IsEphemeralStatus(b))
                .ToList();
            if (blocks.Count == 0) return;

            var chunk = new System.Text.StringBuilder();
            foreach (var block in blocks)
            {
                chunk.Append(block);
                if (chunk.Length > 400_000)
                {
                    await AppendRawAsync(chunk.ToString());
                    chunk.Clear();
                }
            }
            if (chunk.Length > 0) await AppendRawAsync(chunk.ToString());

            // Divider goes through AppendRawAsync too — journaling it would stack one
            // divider per relaunch. Memory restore happens LATER (RestoreConversationMemoryAsync,
            // called by MainWindow after any saved model is re-selected — a model switch clears
            // history, so restoring memory here would risk it being wiped moments later).
            _replayedBlockCount = blocks.Count;
            await AppendRawAsync(_html.StatusCard(
                "Previous session restored",
                "This transcript was restored from your previous session.",
                "success"));
        }
        catch { /* a failed replay must never block a fresh conversation */ }
    }

    private int _replayedBlockCount;

    /// <summary>
    /// Gives the restored session its memory back, best fidelity first. Called by MainWindow
    /// AFTER the tab's harness is initialized and any saved model re-selected. Cascade:
    /// 1) full-fidelity harness history (the agent genuinely remembers, tool calls included);
    /// 2) plain-text tail armed as imported background (briefed, not remembering);
    /// 3) an honest amnesia note, so the model never has to guess about the replayed pixels.
    /// Fresh tabs have none of the files and fall straight through as a no-op.
    /// </summary>
    public async Task RestoreConversationMemoryAsync()
    {
        try
        {
            // 1) Full fidelity: rehydrate the harness's chat history verbatim.
            var historyJson = SessionHistoryStore.Load(Session.PersistKey);
            if (historyJson != null)
            {
                var restored = await Task.Run(() => Session.Ai.TryRestoreHistoryJson(historyJson));
                if (restored > 0)
                {
                    await AppendRawAsync(_html.StatusCard(
                        "Conversation memory restored",
                        $"The agent remembers this session ({restored} messages).",
                        "success"));
                    return;
                }
            }

            // 2) Tail-brief: bounded verbatim excerpt rides the next send as imported background.
            var turns = ConversationLog.Load(Session.PersistKey);
            if (turns.Count > 0)
            {
                const int budget = 12_000;
                var picked = new List<ConversationTurn>();
                var used = 0;
                for (var i = turns.Count - 1; i >= 0; i--)
                {
                    if (picked.Count > 0 && used + turns[i].T.Length > budget) break;
                    picked.Add(turns[i]);
                    used += turns[i].T.Length;
                }
                picked.Reverse();

                var sb = new System.Text.StringBuilder();
                if (picked.Count < turns.Count)
                    sb.Append($"(Older turns omitted — this is the most recent {picked.Count} of {turns.Count}.)\n\n");
                foreach (var turn in picked)
                    sb.Append(turn.R == "u" ? "User: " : "Assistant: ").Append(turn.T).Append("\n\n");

                _controller.ArmRestoredConversation(
                    "From \"your previous session in this tab\" (verbatim excerpt, not a recap):\n" +
                    sb.ToString().TrimEnd());
                await AppendRawAsync(_html.Dim(
                    "Context re-armed — the agent will be briefed on this conversation with your next message."));
                return;
            }

            // 3) Transcript was replayed but no memory of any kind exists — say so to the model.
            if (_replayedBlockCount > 0)
                _controller.NoteWorkspaceEvent(
                    "This tab was restored from a previous session. The transcript the user sees above is a replay " +
                    "for their benefit; it is NOT in your context and you have no memory of it. If the user refers " +
                    "to earlier work, say so honestly and re-read files instead of guessing.");
        }
        catch { /* memory restore is best-effort; a fresh conversation always works */ }
    }

    /// <summary>
    /// Writes a fragment straight into the transcript document, bypassing the <c>_pendingHtml</c>
    /// queue on purpose: this runs during journal replay, while <c>_webViewReady</c> is still false so
    /// that live blocks keep queueing BEHIND the restored history. That's also why it can't use
    /// <see cref="CanScript"/> — which tests <c>_webViewReady</c> and would send the replay to the
    /// queue it's meant to precede.
    ///
    /// The core is captured into a local instead of being dereferenced off the property twice: replay
    /// awaits between chunks, and closing (or unparenting) the tab in that window unloads the WebView
    /// and nulls <c>CoreWebView2</c> — which is a NullReferenceException on the next chunk. The catch
    /// below always swallowed it, so it never crashed anything, but it broke into the debugger on
    /// every occurrence and abandoned the rest of the replay on an exception path.
    /// </summary>
    private async Task AppendRawAsync(string html)
    {
        if (_shutDown) return;

        var core = TranscriptView.CoreWebView2;
        if (core == null) return;   // WebView gone (tab closed mid-replay) — nothing to render into

        try
        {
            await core.ExecuteScriptAsync($"window.__append({JsonSerializer.Serialize(html)})");
        }
        catch { /* transient during navigation/teardown — a fragment failing to render is not fatal */ }
    }

    private async void AppendHtml(string html)
    {
        if (_shutDown) return;

        // Capture once — CanScript reads the property, and re-reading it at the call site is a race
        // against the WebView unloading. A block that arrives with no live core is queued, not
        // dropped, so it still renders if the WebView comes back.
        var core = CanScript ? TranscriptView.CoreWebView2 : null;
        if (core == null)
        {
            _pendingHtml.Enqueue(html);
            return;
        }

        try
        {
            await core.ExecuteScriptAsync($"window.__append({JsonSerializer.Serialize(html)})");
        }
        catch
        {
            // A fragment failing to render must never take the app down.
        }
    }

    private async void ClearTranscript()
    {
        var core = CanScript ? TranscriptView.CoreWebView2 : null;
        if (core == null) return;
        try { await core.ExecuteScriptAsync("window.__clear()"); }
        catch { /* transient during navigation/teardown — clearing a gone WebView is a no-op */ }
    }

    /// <summary>Offer to snapshot this tab's conversation (the "Take snapshot" tab action) — pops the
    /// opt-in create card so the user can pick a summarizer model.</summary>
    public void TakeSnapshotManually() => _ = _controller.OfferManualSnapshotAsync();

    /// <summary>The header's camera button — the same offer as the tab menu's "Take snapshot", one
    /// click away instead of two. No guard for an empty conversation: the offer itself answers that
    /// with a "Nothing to snapshot" chip in the transcript, which teaches more than a dead button
    /// would.</summary>
    private void SnapshotButton_Click(object sender, RoutedEventArgs e) => TakeSnapshotManually();

}
