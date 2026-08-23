# Session Persistence & Snapshots

How MandoCode Desktop remembers — the concepts, the architecture, and where it goes next.

## The two concepts

- **History JSON is *memory*** — verbatim, heavy, machine-format, tied to one conversation's
  continuation. It answers *"resume exactly where I was."*
- **A snapshot is a *knowledge artifact*** — distilled by an LLM, named by you, human-readable,
  cheap to inject anywhere. It answers *"carry what we learned somewhere else."*

> **Memory doesn't transfer between minds; knowledge does.**

That line is the design rule. Anything that continues *the same* conversation (relaunch, model
switch on the same tab) should use memory. Anything that moves context *between* conversations
(another agent, another project, a fresh start) should use knowledge — a snapshot. When a new
feature needs "the agent should know about X," ask which side of the line X lives on.

## The persistence tiers

Each tier ships independently and degrades gracefully into the one below it.

| Tier | What survives | Store | Mechanism |
|------|---------------|-------|-----------|
| 1 | Workspace shape: tabs, titles, folders, models, active tab | `workspace.json` | Saved on every structural change + close; restored at launch |
| 1 | Snapshots | `snapshots.json` | Rewritten on add/remove; loaded at construction |
| 1 | Closed-conversation index (History) | `sessions.json` | Rewritten on close/reopen/delete; points at the retained per-key journals below |
| 1 | Panel UI prefs: collapsed groups + per-panel "last seen" unread marks | `panel-state.json` | Rewritten on fold/unfold and when a panel is opened |
| 2 | The visible transcript | `transcripts/<key>.jsonl` | Append-on-write journal of every HTML block; replayed into the WebView on restore |
| 3 | The model's memory | `histories/<key>.json` | `AIService.ExportHistoryJson()` at every turn end (write-then-rename); `TryRestoreHistoryJson()` on restore |
| 3 fallback | A plain-text tail of the dialogue | `conversations/<key>.jsonl` | Armed as imported background on the next send when full fidelity can't apply |

All stores live under `%LOCALAPPDATA%\MandoCode.Desktop\`, keyed by each session's durable
`PersistKey` (a GUID that survives relaunches, unlike the process-local session Id). All writes
are best-effort and append-or-atomic: a crash loses at most the in-flight block. Caps are
enforced on the **write** side, not just at load — an app that never restarts must still have
bounded files.

### The restore cascade (per tab, in order)

1. **Full fidelity** — rehydrate the harness `ChatHistory` verbatim, tool calls included.
   The agent genuinely *remembers*. Runs only **after** any saved model is re-selected,
   because model selection clears history.
2. **Tail-brief** — a bounded verbatim excerpt of the dialogue rides the next send as
   imported background. The agent is *briefed*, not remembering.
3. **Honest amnesia** — if a transcript was replayed but no memory exists, the model is told
   exactly that, so it never has to guess about pixels it can't see.

Cleanup is symmetrical: `/clear`, closing a tab, and the startup orphan sweep remove all of a
session's files together. Cleared means cleared.

## Model switches

A switch clears the live history ("a different model mid-history is a different conversation"
was the original stance — from before any serialization existed). The offer bar now presents
both sides of the concept line:

- **Keep memory** — the pre-switch history is re-imported verbatim; the same conversation
  continues under the new model. Right choice cloud↔cloud or when moving to a *bigger* model.
- **Snapshot** — the conversation is summarized into a named, portable recap. Right choice
  when **downsizing** (a small local model may not fit the verbatim history) or when you want
  a clean slate plus the lessons.

"Keep memory" appears only for switch offers, never for manual "Take snapshot" offers (nothing
was cleared, there is nothing to carry). If the verbatim import fails, the offer stays up and
the snapshot path remains as salvage.

## The History archive — reopening closed conversations

The per-key journals turned out to support more than restoring the tabs open at close: they back a
**History panel** that reopens *any* conversation you've closed. This required one deliberate change
to the retention model.

Closing a tab used to delete its journals outright — "closed tab = conversation gone." That made
the memory/knowledge split lopsided: the only way context survived was to still be open at launch.
Now closing **archives** instead:

- `SessionArchiveStore` keeps an app-wide index (`sessions.json`) of closed conversations — the
  cheap metadata (title, project, model, closed-at, turn count, first message), not the heavy
  parts. The transcript/log/history journals it points at are the same per-key stores a live tab
  uses; they simply aren't deleted on close anymore.
- **Reopen** recreates a tab on the archived persist-key and lets the normal restore cascade run —
  so a reopened conversation replays its transcript and, when the model can take it, rehydrates its
  full memory. The row leaves the archive (it's live again) and re-files itself on the next close.
- The archive is capped at the newest 60; evicting a row deletes its journals, so the on-disk
  stores stay bounded even for someone who never runs `/clear`.
- The startup orphan sweep now keeps *archived* keys alongside *open* ones — only genuinely
  orphaned journals (crash leftovers, pruned folders) are swept.

The design rule held: `/clear` still forgets (deletes the files, never archives). Only the meaning
of *closing* softened from "gone" to "recoverable." A session that never had a real turn is dropped
on close regardless — there's nothing worth reopening.

Both panels grew the same shape at the same time: cards **group by project**, a **search** box
filters them, and each project group is a **collapsible** `Expander` whose fold state persists
(`panel-state.json`). Their rail badges became **unread counts** — items newer than the last time
you opened that panel — which clear on open and whose "last seen" marks also persist. Snapshots
gained **AI-generated titles** (unique-checked in code) when saved unnamed, and Import now gets out
of the way so the chat's "context armed" confirmation is what you see.

## Future building

In rough order of value:

- **Summarize-at-restore upgrade** — the tail-brief fallback could run `HistorySummarizer`
  over the stored dialogue instead of excerpting it, trading an LLM call for better coverage
  of long sessions.
- **CLI `--continue`** — `ExportHistoryJson`/`TryRestoreHistoryJson` live in the harness
  precisely so the CLI can grow its own resume without new plumbing.
- **Cross-provider carry verification** — verbatim history with function-call content moving
  between Ollama and cloud connectors should map cleanly through Microsoft.Extensions.AI's
  generic content types; it deserves a deliberate test before "Keep memory" is treated as
  guaranteed across providers (the graceful fallback already handles failure).
