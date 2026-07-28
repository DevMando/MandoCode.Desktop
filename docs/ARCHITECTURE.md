# Architecture

This is the internals doc for contributors — how MandoCode Desktop is put together, how it relates
to the [MandoCode CLI](https://github.com/DevMando/MandoCode), and the reasoning behind the less
obvious decisions. If you just want to use the app, see the [README](../README.md) instead.

## The harness submodule

The MandoCode AI coding agent with a native Windows desktop interface. Same engine
as the [MandoCode CLI](https://github.com/DevMando/MandoCode) — literally: the CLI
repo is pinned here as a **git submodule** (`/MandoCode`) and this app project-references
`MandoCode/src/MandoCode/MandoCode.csproj`, reusing the entire harness (AIService,
task planner, plugins, MCP, skills, config, approvals, token tracking). Only the
user interface is different: WinUI 3 instead of RazorConsole.

`/MandoCode` is pinned at a specific CLI commit — radical changes in the CLI repo
can never break this app until the pin is deliberately moved. To roll the harness
forward (do this against CLI release tags, not random commits):

```
cd MandoCode
git fetch && git checkout v0.14.0        # or origin/main
cd ..
dotnet build src/MandoCode.Desktop/MandoCode.Desktop.csproj   # fix what the compiler flags
git add MandoCode && git commit -m "Roll harness to v0.14.0"
```

When rolling, also eyeball the three *ported* seams for behavioral drift (they
compile independently of the CLI's originals):

| Desktop port | CLI original |
|---|---|
| `ViewModels/ChatController.cs` | `Components/App.razor` interactive loop |
| `Services/WinUiApprovalService.cs` | `Services/Approval/DiffApprovalHandler.cs` |
| `Services/TranscriptHtmlBuilder.cs` | `MarkdownHtmlRenderer` / `OperationDisplayRenderer` |

The harness is safe to instantiate once per agent — its statics are pure functions and readonly
`Regex`, and `AIService` takes every collaborator by constructor. Rolling the pin forward, watch
for that changing.

## Layer map

| Layer | CLI (MandoCode) | Desktop (this app) |
|---|---|---|
| Orchestrator | `Components/App.razor` interactive loop | `ViewModels/ChatController.cs` (faithful port), one per agent |
| Approvals | `DiffApprovalHandler` (Spectre panels) | `Services/WinUiApprovalService.cs` + each agent's own XAML overlay (same labels, bypass state, `DiffApprovalResult` contract) |
| Transcript | ANSI scrollback + Spectre renderables | WebView2 + `TranscriptHtmlBuilder` (Markdig HTML, themed) |
| Busy/spinner | `SpinnerService` (ANSI) | `BusyStateService` → ProgressRing |
| Onboarding | `OnboardingFlow` terminal prompts | In-chat guided wizard (auto on first launch, `/setup` after) + Settings page |
| Everything else | `Services/`, `Plugins/`, `Models/` | **reused verbatim via project reference** |

Key seams the harness already provided (unchanged): `AIService.ChatStreamAsync`,
`OnWrite/Delete/CommandApprovalRequested` delegates, `PlanHandoff.OnPlanRequested`,
`McpApprovalGate.OnApprovalRequested`, `TaskPlannerService.ExecutePlanAsync`
progress events, `DiffService` diff models.

## Agents

Each tab is an independent agent. `Services/AgentSession.cs` hand-constructs one agent's object
graph; `SessionManager` owns the set of them. The split matters:

| Per agent | App-wide |
|---|---|
| `AIService` (its conversation, its model), `ChatController`, `TaskPlannerService` | The `MandoCodeConfig` on disk — the **defaults** a new agent starts on |
| `MandoCodeConfig` clone, `ProjectRootAccessor`, `SkillLoader`, `FileAutocompleteProvider` | `McpClientManager` (one set of server processes) |
| `TokenTrackingService`, `PlanHandoff`, `TranscriptWriter`, `BusyStateService`, `ShellRunner` | `MusicPlayerService`, `ThemeManager` (static), `TranscriptHtmlBuilder`, `SpinnerService` |
| `WinUiApprovalService`, `ApprovalPromptGate`, `McpApprovalGate` | `ConfigCoordinator`, `McpCoordinator`, `SkillCoordinator`, `SessionManager`, `SnapshotStore`, `SessionArchiveStore`, `NoteStore`, `UiUpdateCheckService` |

Tabs default to `Agent 1`, `Agent 2`, … (the folder shows in the header); the number reuses the
lowest free slot, and a rename or folder change never overwrites it. Each tab's `⋯` options menu
carries Rename, Take snapshot, Export transcript, and Close. The model in each header opens a
quick-switch dropdown (cloud first, `cloud`/`local` badges) rather than a full-screen picker.

Closing the **last** agent is allowed: it leaves a clean empty state (showing the chat background)
with a one-click way to start a new agent. Actions that need an agent to act on — the Settings and
MCP pages, and snapshot Import — disable while none is open, then re-enable when you open one.

### Split view (2–4 agents at once)

The **Split** button puts two agents side by side in a resizable view, and further panes are added
from the split bar's **Add pane** button or a tab's **Add to split view** (its `⋯` menu, or
right-click). **Add pane** is a `SplitButton`: clicking it panes the next agent that isn't shown
yet, while its chevron lists the agents still available so you can pick a specific one — the same
shape as the terminal's shell picker. The layout follows the pane count: two side by side, three
across, four as a 2×2 —
past three, columns alone leave each pane too narrow for a transcript plus an input box. Every
divider is draggable, and each one repartitions only the two panes either side of it, so adjusting
one split never nudges a third pane.

It's called *split view* rather than *compare* because comparing two models on the same prompt is
only one of the things it's for: with three or four panes open you're usually watching agents work
in parallel on different folders, not comparing their output.

The pane set is an explicit, remembered choice — never set by plain-clicking a tab. Clicking a paned
agent's tab shows the split; clicking any other agent shows it normally while the set waits.
Dropping below two panes turns the split off and leaves you on the agent that survived. The set and
its divider positions persist across restarts (in `workspace.json`, keyed by each agent's durable
persist-key so a skipped project folder drops one pane rather than shifting all of them).

Panes are ordinary agent views moved between grid cells with `Grid.SetColumn`/`Grid.SetRow` —
**never re-parented**, so every WebView (and its live transcript) survives the switch, which is the
whole reason the tab surface is built the way it is (see below). The row and column tracks are
rebuilt in code per pane count, interleaving a divider track between adjacent panes; track
definitions and dividers are the only things that change, so no agent view ever leaves the tree.
The geometry and divider math live in `Services/PaneLayout.cs`, kept free of WinUI types so they're
unit tested directly.

The split bar uses chips with `MenuFlyout` pickers rather than `ComboBox`es on purpose: rebuilding
ComboBox items as containers makes WinUI throw `COMException 0x80070490` on the next selection.

The three approval services are per-agent for **correctness**, not tidiness. Shared, they break
in ways that are invisible until a second tab exists: `WinUiApprovalService` holds the
"don't ask again" bypass set, so one agent's blanket approval would auto-approve writes in every
other; `ApprovalPromptGate` is a `SemaphoreSlim(1,1)` built for one console, so an unanswered
approval in one agent would stop another's from ever rendering; and `ChatController` **assigns**
(not `+=`) five approval delegates, so on shared services the last agent constructed silently
steals every approval.

One setting can't be per-agent and is labelled app-wide in the UI: **Appearance** is a property
of the window (and lives outside the shared config). **Context window** used to share that label —
it was applied as `OLLAMA_CONTEXT_LENGTH` at daemon start, one daemon, one window — but it now
rides on every chat request as `num_ctx`, so each agent's own value governs its own conversations,
like any other per-agent setting.

### Settings and the config file

`~/.mandocode/config.json` is not "the current settings". It is the **defaults a new agent starts
on**. Editing Settings changes the selected agent, live, for that session; **Make Default for New
Agents** is the only action that writes the file (plus corrections like a healed endpoint URL, the
onboarding flag, and the app-wide MCP server list).

`ConfigCoordinator` is the only code that calls `MandoCodeConfig.Save()`. The rule can't be
enforced by the type system — `Save()` is public and non-virtual on a harness type — so the
`MANDO001` build target fails the build if `ChatController` calls `_config.Save()` on its clone,
which would publish one agent's model as everybody's default. Cloning is a JSON round-trip
**followed by `ValidateAndClamp()`**: `System.Text.Json` rebuilds `McpServers` with a
case-sensitive comparer, and without the clamp every MCP lookup in the clone silently misses on
a casing difference.

### Context snapshots

A snapshot is a portable, AI-written recap of a conversation — save the gist of one agent's
context and carry it into another model or a fresh agent. Snapshots are offered when switching a
model would clear the conversation, and on demand via `Take snapshot` (tab `⋯` menu), which goes
straight to the save step. The recap is generated by `SnapshotEnhancer` (a bare, tool-less Ollama
kernel, map-reduce over the full history so nothing is truncated) using a summarizer model you
pick; a snapshot is therefore always born with a real recap — there is no "light"/un-enhanced state.
Leave the name blank and the summarizer proposes a short title, which is then made unique against
existing titles in code (`SnapshotNaming`) — an LLM can't be trusted to guarantee that itself.

The **Snapshots** rail icon opens a global panel (the `SnapshotStore` is app-wide, one list for
every tab, **persisted** to `snapshots.json`). Cards **group by project** and are **searchable**,
and each project group is a collapsible `Expander` whose fold state is remembered
(`PanelState` → `panel-state.json`). **Import** arms a snapshot's recap to ride along, invisibly,
with the active agent's next message — carrying context into any model. The rail badge is an
**unread count** (snapshots captured since you last opened the panel), not a running total, and
clears when you open it.

### Session history (reopen closed conversations)

Closing an agent no longer discards its conversation — it **archives** it. `SessionArchiveStore`
keeps an app-wide index (`sessions.json`) of closed conversations; the transcript, model memory, and
conversation-log journals stay on disk (see [session-persistence.md](session-persistence.md)).
The **History** rail panel lists them (grouped by project, searchable, collapsible), and **Open**
reopens one as a fresh tab on its original persist-key so the normal restore cascade replays the
transcript and — when the model supports it — rehydrates the full memory. `/clear` still forgets a
conversation for good; only *closing* softened from "gone" to "recoverable." The archive is capped
(newest 60); evicting a row deletes its journals so the on-disk stores stay bounded.

### Notes (the jot pad)

The **Notes** rail panel is an always-there scratchpad: plain text files under
`~\.mandocode\notes`, beside the config file the CLI shares. **New** creates one and opens an editor docked next to the
chat — autosaved on a 1.2s debounce (plus Ctrl+S, leaving the note, closing the panel, closing the
window), renameable in place, with **Show in Explorer**.

Notes are **app-wide**, the same call as snapshots and session history. A note is something you want
to write down *now*, which is often between projects or before an agent is even open, so nothing here
needs one. What survives of "which project was this about" is a plain **subfolder**: a new note is
filed under the active agent's folder name when there is one, and sits loose at the top when there
isn't. Grouping therefore costs no metadata, can't drift, and is corrected by dragging files around
in Explorer.

**The filesystem is the store** — no index, no JSON, which is also why the pad lives in
`~\.mandocode` rather than LocalAppData: these are your files, meant to be greppable, syncable, and
openable in any editor. Discovery walks one folder plus its immediate subfolders (one level only — a
jot pad with a hierarchy is a filing system, and the search box is a better answer to "where did I
put it"). In exchange, no row can ever point at a file that isn't there: a note added in Notepad
shows up, and one deleted outside the app disappears. Search matches note **bodies** and quotes the
matching line on the card; only search and the assistant see a body truncated, capped at
`NoteStore.MaxTextBytes` (128 KB).

#### The prompt bar

Both surfaces — the open note and the list — carry a prompt at the bottom. It's chat-shaped, but the
document above it is your note rather than a transcript, so replies land in the bar's own strip and
reach a note only through **Insert** (at the cursor, replacing the selection if there is one) or
**Replace note**. With text selected, the question is about the selection.

The note's text travels **in the message**, taken from the live editor buffer — so the model always
sees the note as it is right now, including keystrokes autosave hasn't written yet, with nothing to
fall out of sync. Only the current message carries the note; earlier turns in the thread keep just
their words, so a long back-and-forth doesn't ship five stale copies. On the list, the question is
about the pad: every note's title and first line, plus the full text of whatever the search box is
currently matching, and the bar states what it was given (`12 notes listed · 3 read in full`) because
a capped read that looks total is the one thing an "ask about all my notes" box must not do.

`Services/NoteAssistant.cs` builds a bare Ollama kernel with **no plugins, filters, or tools** — the
same shape as `SnapshotEnhancer`. That's the design, not an optimization: with no file tools, "nothing
writes your note but you" is true by construction rather than by policy, and no approval machinery is
needed because the only route from a reply into a note is a button you pressed. It also isn't an
`AIService` agent, because those exist to change your files (the opposite of what a notepad wants)
and are scoped to a project root the pad deliberately sits outside of. Its model is picked from the
chip under the prompt, defaults to the app-wide default model, and is remembered; the thread is
per-note, in memory, and cleared when you switch notes — notes aren't conversations.

The editor is not the only thing that writes these files, and doesn't assume it is. A
`FileSystemWatcher` compares the file against what the editor last wrote: identical means the write
was ours; changed-while-clean is adopted silently; changed-while-you-were-typing raises a conflict you
resolve — *use the version on disk* or *keep what I typed*. A note deleted from under unsaved edits
offers to save it back. `Services/NoteText.cs` handles the newline round trip, because a WinUI
`TextBox` normalizes every newline to a bare CR — write that straight back out and a Notepad-authored
CRLF note becomes one endless line. Both of those are why `NoteStore`, `NoteEntry`, and `NoteText` are
WinUI-free with an injectable pad root: the tests drive them against real temp folders.

Notes deliberately lack the **Delete all *n*** group button that Snapshots and History carry: those
are artifacts the app generated, a note is something you wrote by hand, and one button that deletes a
folder's worth of writing is a different class of risk.

Undo/Redo for the assistant's inserts and replacements is a single-level **toggle** rather than a
history stack: the editor remembers the buffer right before and right after the last assistant edit,
and Undo/Redo just flip between those two exact states. Typing anything that lands outside both
retires the toggle — the same rule a full undo stack uses to clear its redo branch on a new edit,
just with only one edit remembered.

### Why the tab strip isn't a `TabView`

WinUI's `TabView` hosts only the selected item's content, which detaches the previous tab and
closes its `CoreWebView2`. `TranscriptWriter` retains nothing — the WebView2 DOM is the only copy
of a conversation — so that would destroy the transcript on every tab switch. The strip carries
headers only; content lives in `Visibility`-toggled sibling panels and is never re-parented.

## Full feature inventory (v0.1)

- Chat with streaming turns, function-call operation cards, markdown transcript
- Write / command / delete / MCP approvals with native diff viewer (same
  approve / don't-ask-again / deny / new-instructions / cancel-plan semantics)
- propose_plan flow: plan table, execute/reject/cancel, per-step progress bar,
  step-failure skip/cancel
- Slash commands with autocomplete: /help /setup /clear /model /config /retry /learn
  /copy /copy-code /skills /force-skill /mcp /mcp tools /mcp remove /mcp-reload
  /music* /command /exit — plus `!cmd` shell escape and `@file` references
- Token tracking + per-response summaries, per agent
- Agent tabs — `+` opens another agent (`Agent 1`, `Agent 2`, …) with its own
  conversation, project folder, model, and settings; an approval waiting in a
  background agent badges its tab and the toast names it. Each tab's `⋯` menu:
  Rename, Take snapshot, Export transcript, Close. The header model opens a
  quick-switch dropdown (cloud first, `cloud`/`local` badges). Closing the last
  agent is allowed and leaves an empty state that shows the chat background
- Split view — the **Split** button shows two agents side by side, and **Add pane**
  (or a tab's **Add to split view**) grows that to three across or four as a 2×2, every
  divider draggable; the pane set is a remembered, explicit choice, so clicking
  other tabs navigates without disturbing it, and it survives a restart
- Session history — closing an agent archives its conversation instead of deleting
  it; the **History** panel reopens any past conversation as a new tab (with its
  transcript, and full memory when the model supports it), grouped by project and
  searchable. `/clear` still forgets for good
- History cards show both ends of a conversation — the opening message (what it was
  about, since rows are titled by agent name) and a dimmer **last ·** line with your
  most recent message (whether it's worth resuming)
- Full-text history search — the search box reads each archived conversation's whole
  text, not just its title and preview, and quotes the matching line on the card so
  every hit explains itself. Debounced and off the UI thread behind a per-session
  cache, so typing never waits on file IO
- Bulk cleanup — opening a project group of two or more in History or Snapshots
  reveals a **Delete all *n*** button at the top of it, clearing the whole group
  after one confirmation, batched into a single store write rather than one per item
- Context snapshots — save an AI-written recap of a conversation (summarized by a
  model you pick) and Import it into another model or a fresh agent; a global
  left-rail panel lists them, **persisted**, grouped by project, searchable, with
  collapsible groups. Unnamed snapshots get an auto-generated, unique title
- Notes — a **Notes** rail panel that jots into the project itself: **New** writes
  a plain text file under `~\.mandocode\notes` and opens an editor beside the chat
  (autosave, rename in place, Show in Explorer). App-wide like snapshots, so a note never
  needs an agent open; new notes are filed under the folder you're working in when there is
  one, which is what the panel groups by. The filesystem is the store — no index, so a note
  written in Notepad appears and one deleted outside the app is gone. Searchable across note
  bodies with the matching line quoted
- A prompt bar under both notes surfaces — ask about the open note (it's sent the live
  buffer, so the model always sees what you see) or about the whole pad, with the bar
  stating what it was given. Replies land in their own strip and reach a note only via
  **Insert** / **Replace note**; the assistant has no tools at all, so "nothing writes your
  note but you" is structural. External edits are adopted into the open editor, while an
  external change landing on unsaved typing raises a conflict you resolve — never a silent
  overwrite. Undo/Redo toggles the assistant's last insert/replace
- Rail badges on History and Snapshots are unread counts that clear when you open
  the panel (persisted), not running totals
- Integrated terminal — a sliding panel (Ctrl+` / Ctrl+Shift+` to maximize) running a
  real shell via ConPTY, rendered with xterm.js in WebView2; a shell picker chooses the
  shell and it opens in the active agent's project folder
- Per-agent file explorer with git awareness — a live file tree (FileSystemWatcher) plus a
  Changes tab (GitQuickStatus): branch chip, add/modify/delete status, dirty badges, inline
  diff cards, one-click commit and per-file undo; tree items drag into the input as `@`-refs.
  WorkspaceDeltaTracker notes commits/reverts/branch switches made outside the conversation
- Sidebar: Settings, MCP, and Skills as full-screen pages, acting on the selected agent
  - Settings — the whole config as a native form (toggles, sliders, number boxes,
    grouped Appearance/Connection/Generation/Behavior/Limits/Integrations); every
    change is validated through the shared ConfigKeySetter, same as the CLI, and
    applies to that agent alone. "Make Default for New Agents" saves it to disk
  - MCP — live server list with status/tool counts; add/edit servers in a single
    form modal with a Test button (isolated connection check + tool table preview).
    Servers are one app-wide set; each agent chooses whether to attach their tools
  - Skills — list installed skills (searchable, filterable, enabled per agent), install
    from a folder or zip, and an editor that can generate or refine a skill body with a
    model you pick (`SkillAuthor`); `SkillCoordinator` fans changes to every open agent
- Guided wizards, built on the approval-overlay select + text primitives:
  - First-run setup — fires automatically in the chat on a fresh install: probe/start
    Ollama (offering a winget install when the CLI is missing), change endpoint, pick a
    starter model from a curated list (cloud recommended, or local tiers with size and
    hardware hints), pull it with live progress, cloud-auth check + sign-in walkthrough.
    Re-run any time via `/setup` or Settings → Run guided setup
  - `/model`, `/force-skill`, `/music-playlist` — pickers
  - 401 auto-recovery — a cloud 401 offers the `ollama signin` walkthrough inline
- Branded application icon across the exe, taskbar, and window title bar
- Update check against this repo's GitHub Releases (24h throttle, fail-silent)

Not ported: matrix easter eggs; the CLI's ANSI terminal-theme service (N/A — the app ships
its own integrated terminal instead, see above).
