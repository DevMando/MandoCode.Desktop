# Changelog

All notable changes to MandoCode Desktop are documented here.
Versioning is independent of the MandoCode CLI; the pinned harness commit is
recorded by the `MandoCode` submodule.

## [Unreleased]

Multiple agents, one window. Each tab is an independent agent with its own conversation,
project folder, model, and settings — and the config file stops being "the current settings"
and becomes "the defaults a new agent starts on."

### Why this matters

One conversation at a time is the wrong shape for real work. You want a cloud model planning
in one tab while a local one grinds through a refactor in another, each pointed at a different
folder. Everything below exists to make that safe rather than merely possible: three of the
fixes are for bugs that would have silently corrupted one agent from another, and none of them
are visible until you actually open a second tab.

### Added
- **Agent tabs.** A `+` button opens another agent — its own conversation, project folder,
  model, and settings. Each tab carries its own header: connection dot, model switcher, token
  count, project folder path, save-transcript and open-folder buttons. Closing the last agent
  is refused (Settings and MCP have no agent to act on without one).
- **Per-agent settings.** The Settings page acts on the selected agent and says whose settings
  you're editing. Changes apply live to that agent alone, for that session. Nothing is written
  to disk until you press **Make Default for New Agents**, which snapshots that agent's settings
  into `~/.mandocode/config.json`. Agents already open keep their own.
- **Cross-agent approval routing.** An approval raised in a background agent badges its tab in
  gold and the toast names the agent (`Click to review in "MandoCode 2"`) — with several agents
  running, "an approval is waiting" is useless without saying where.
- **Per-agent MCP.** Servers are one app-wide set (they're OS processes), but each agent decides
  whether to attach their tools. Enabling MCP for an agent starts the shared servers if they
  aren't running yet.
- **Context snapshots (history points).** Switching a model clears the conversation, so the instant
  before it clears, the outgoing conversation is captured as a snapshot — origin model, timestamp,
  and a compact recap. A **Snapshots** icon on the left rail (with a count badge) opens a global
  management panel — docked left at ~37% width so the active chat stays visible — listing every
  tab's snapshots. **Import** arms a snapshot so its recap rides along, invisibly, with the *active*
  agent's next message, carrying the context into any model. The store is app-wide, so a snapshot
  taken in one tab imports into a brand-new tab on a capable model. **Take snapshot** (tab options
  menu) captures on demand without switching. The recap is written by a summarizer model you pick
  (`SnapshotEnhancer`, a tool-less Ollama kernel that map-reduces over the full history so nothing is
  truncated), so a snapshot is always born with a real recap — there is no "light"/un-enhanced state.
- **Per-tab options menu.** The tab's `⋯` menu carries Rename, Take snapshot, Export transcript,
  and Close. It replaces the bare close button — which, on the last remaining agent, was an `X`
  you were not allowed to use; Close is now simply greyed out there.
- **Model quick-switch dropdown.** Clicking the model in a tab's header drops a list anchored to the
  button (cloud models first, `cloud`/`local` badges, current one preselected) instead of a
  full-screen modal. It opens instantly with a loading spinner while the model list is fetched, and
  shows connection/empty-list errors inline. The typed `/model` command still uses the overlay wizard.
- **History panel — reopen a closed conversation.** A new rail icon (with a count badge) opens a
  docked panel, sharing the Snapshots column, that lists every conversation you've closed — title,
  project, model, when, turn count, and the first thing you said. **Open** brings one back as a
  fresh tab through the existing restore cascade: the transcript replays and, when the model can
  take it, the full memory rehydrates. **Delete** forgets one for good. Search filters by title,
  project, model, or that first message. The archive is app-wide, persisted, and capped at the
  newest 60 — evicting an old row deletes its journals so the on-disk stores stay bounded.
- **Snapshots panel — grouping, search, and a cleaner import.** Snapshot cards now group by the
  project they were taken in (freshest project first), a search box filters by title/recap/model/
  project, and Import closes the panel and focuses the chat so the "context armed" confirmation is
  the thing you see.
- **Collapsible project groups, in both panels.** Each project group in Snapshots and History is an
  `Expander` you can fold — the answer to "10–100 projects." Which groups you've collapsed is
  remembered across launches (`PanelState` → `panel-state.json`).
- **Compare view — two agents side by side.** A **Split** button pairs two agents into a resizable
  side-by-side view. The pair is an explicit, remembered choice (set by the button or the compare
  bar's pickers, never by clicking a tab): clicking a paired agent's tab shows the split, clicking
  any other agent shows it normally while the pair waits. The panes are ordinary agent views moved
  between grid columns via `Grid.SetColumn` — never re-parented — so both WebViews and their live
  transcripts survive the switch.
- **AI-named snapshots.** Saving a snapshot without a name now asks the summarizer for a short,
  descriptive title from the recap; uniqueness against existing titles is then guaranteed in code
  (`SnapshotNaming`), so two snapshots can't share a name.
- **Unread badges.** The History and Snapshots rail badges are now unread counts — items newer than
  the last time you opened that panel — and clear when you open it, rather than showing a running
  total. The "last seen" marks persist across launches.
- **Integrated terminal.** A sliding terminal panel (Ctrl+` toggles it, Ctrl+Shift+` maximizes)
  runs a real shell through ConPTY, rendered with xterm.js inside WebView2 — no new native
  dependencies. A shell picker (`ShellCatalog`) selects PowerShell/cmd/etc., and the terminal
  opens in the active agent's project folder.
- **File explorer with git awareness.** Each agent has a collapsible file tree, kept live by a
  `FileSystemWatcher`, alongside a **Changes** tab driven by `GitQuickStatus`: a branch chip,
  per-file add/modify/delete status with dirty badges on files and folders in the tree, inline
  diff cards, a one-click **commit**, and per-file **undo** (with confirmation). Tree items drag
  into the input as `@`-references, and paths can be dropped onto the chat.
- **External-change awareness.** `WorkspaceDeltaTracker` notices when the working tree changed
  outside the conversation — a commit, a revert, or a branch switch between your turns — and notes
  it to the agent so its next reply reflects the repo as it actually is, not a stale picture.
- **Skills page + AI-assisted authoring.** A **Skills** sidebar page lists installed skills
  (searchable, filterable, enabled per agent), installs new ones from a folder or a zip, and its
  editor can **generate or refine** a skill body with a model you pick (`SkillAuthor`).
  `SkillCoordinator` fans skill changes out to every open agent, mirroring `McpCoordinator`.
- **Branded app icon** across the exe, taskbar, and window title bar, plus a lightweight
  unhandled-exception logger (`crash.log`) to speed up diagnosing native/COM failures.

### Changed
- **Closing the last agent is allowed.** The app no longer forces at least one agent open — closing
  the final one leaves an empty state (with the chat background) and a one-click New agent. Settings,
  MCP, and snapshot Import disable while no agent is open and re-enable when one exists.
- **"Take snapshot" goes straight to the picker.** The manual capture (tab `⋯` menu) skips the
  "snapshot available?" notification bar and opens the name + summarizer-model picker directly — a
  model switch keeps the bar, since snapshotting isn't a foregone conclusion there.
- **Closing a tab archives it; `/clear` still forgets.** Closing used to delete a conversation's
  journals outright ("closed tab = conversation gone"). Now it files the conversation into the
  History archive instead, so it can be reopened later; only `/clear` (and eviction past the
  archive cap) deletes the files. A session that never had a real turn is still dropped on close —
  there's nothing to reopen. "Cleared means cleared" is unchanged; only *closing* softens from
  "gone" to "recoverable."
- **`/model` is an agent-local switch** and no longer writes to disk; the model button in each
  agent's header opens the same picker. `/setup` and the Settings page still set the app-wide
  default, because they configure the app rather than one agent.
- **`enableDiffApprovals` applies live, per agent.** The CLI marks it "restart required" because
  it wires the approval delegates once at startup against a shared `AIService`; each agent now
  owns its own, so the toggle attaches and detaches them on the spot.
- **`/exit` no longer disposes the music player.** Shared resources belong to the window: closing
  it (by any route) now disposes the audio device and every agent's WebView2.
- **Chat moved out of `MainWindow`** into a `ChatTabView` user control (`MainWindow.xaml.cs`:
  1368 → 930 lines; the chat surface plus tab plumbing is now its own 867-line control). It
  implements `IApprovalUi` against its *own* overlay, which is what makes concurrent approvals
  safe rather than a race.
- Settings and MCP stay full-screen sidebar pages, not tabs; selecting an agent returns to chat.
- Two settings are now labelled app-wide, because they are: **Appearance** (a property of the
  window, stored outside the shared config) and **Context window** (applied as
  `OLLAMA_CONTEXT_LENGTH` when MandoCode starts the daemon — one daemon, one context window).
- **Agents are named `Agent 1`, `Agent 2`, …** by default, not the folder's leaf name (the folder
  shows in the header already). Numbers fill the lowest free slot, so closing `Agent 2` and opening
  a new tab gives `Agent 2` again rather than an ever-climbing count. Renaming a tab (options menu)
  or changing its folder no longer overwrites the label — it persists across both.
- **Session status and events render as chips.** Startup, model-switch, MCP-connected, context
  cleared, context imported, and snapshot-saved lines are now status **chips** — a themed CSS status
  dot instead of an emoji, so they recolor with the theme and render identically everywhere. The dot
  carries meaning: **green** = healthy/ready (connected, ready, now active, snapshot saved), **grey**
  = an informational event (context cleared, context imported), **gold** = a soft warning. A switch
  clears the live context but leaves the visible transcript, so the `Context cleared` chip is what
  makes the reset explicit rather than silent.

### Fixed
- **"Approve — don't ask again" leaked across agents.** `WinUiApprovalService` held the bypass set
  and approved-file list as singleton state, so a blanket approval in one chat silently
  auto-approved writes in every other. It is now per-agent.
- **An unanswered approval in one agent blocked every other agent's approval from rendering.**
  `ApprovalPromptGate` is a `SemaphoreSlim(1,1)` built to serialize prompts on one console; shared
  across agents, tab B simply looked hung. It is now per-agent.
- **The last agent constructed stole every approval.** `ChatController` assigns (not `+=`) five
  handlers — `PlanHandoff.OnPlanRequested`, `AIService.On{Write,Delete,Command}ApprovalRequested`,
  `McpApprovalGate.OnApprovalRequested`. Single-assignment delegates on shared services mean last
  writer wins. Each agent now owns those services, so there is exactly one writer per graph.
- **MCP could never start if the saved default had it off.** `McpClientManager` gates
  `StartAllAsync` on `EnableMcp`, which is now a per-agent setting — so `"enableMcp": false` in
  the defaults starved every agent that turned MCP on, with no error. `McpCoordinator` owns the
  manager and runs it on a host config that always has MCP enabled; the per-agent flag controls
  only whether that agent attaches the tools.
- **`/mcp-reload` only refreshed the agent that ran it.** Other agents kept stale tool handles.
  Reload now resets each agent's MCP session approvals, restarts the shared servers once, and
  re-registers tools on every agent's kernel (history preserved).
- **`NullReferenceException` opening and closing tabs.** `ChatTabView` subscribed to nine harness
  events and unsubscribed from none, and `Shutdown()` closed the `CoreWebView2` while leaving the
  ready flag set — so a closing agent's unwinding turn drove transcript writes into a null
  `CoreWebView2`. On open, `EnsureCoreWebView2Async()` ran before the control was `Loaded`,
  leaving `CoreWebView2` null. Subscriptions are now symmetric, initialization waits for `Loaded`
  and null-checks, every script path is guarded, and a tab is shut down before it is unparented.
- **WebView2 was never disposed.** Closing an agent left its browser processes running for the
  rest of the session, and closing the window left them orphaned. Both now reap.
- **Screen readers saw unnamed buttons.** The tab close buttons and the MCP page's action buttons
  wrap an icon in a panel, exposing no accessible name. All are named now.

### Guardrails
- `ConfigCoordinator` is the only code in the app that calls `MandoCodeConfig.Save()`. That can't
  be enforced by the type system — `Save()` is public and non-virtual on a type in the read-only
  harness submodule — so a build target (`MANDO001`) fails the build if `ChatController` ever
  calls `_config.Save()` on its per-agent clone, which would publish one agent's model as
  everybody's default.
- Cloning the config is a JSON round-trip followed by a mandatory `ValidateAndClamp()`.
  `System.Text.Json` rebuilds `McpServers` with the default case-sensitive comparer; without the
  clamp, every MCP lookup in the clone silently misses on a casing difference.

### Not done
- Each agent holds a live WebView2 (tens of MB). A retained transcript log would let background
  agents defer creating one until first shown.
- Agent settings are session-scoped by design and are not restored on launch.
- **Summarize-at-restore.** The tail-brief restore fallback still excerpts the stored dialogue
  verbatim rather than running `HistorySummarizer` over it — better coverage of long sessions is a
  follow-up, at the cost of one LLM call on restore.

## [0.1.0] — 2026-07-07

The first MandoCode Desktop — the MandoCode AI coding agent with a native
WinUI 3 interface, sharing its entire engine with the CLI via project reference.

### Added
- Chat with the full MandoCode harness: Semantic Kernel + Ollama (local or
  cloud), file/web/planning/skills plugins, MCP servers, token tracking
- WebView2 transcript with markdown rendering, operation cards, and diff cards
- Native approval overlays for file writes, deletions, shell commands, and MCP
  tools — same labels, session-bypass rules, and semantics as the CLI
- propose_plan flow: plan table, execute/reject/cancel, per-step progress bar,
  step-failure skip/cancel
- Sidebar navigation: Chat, Settings, and MCP pages
  - Settings: the whole config as a native form (toggles, sliders, number
    boxes), validated and applied through the CLI-shared ConfigKeySetter
  - MCP: live server list; add/edit servers in a single form modal with a
    Test button (isolated connection check + tool table preview)
- Guided /setup wizard: probe/start Ollama, pull a starter model with live
  progress, model picker, cloud-auth check and `ollama signin` walkthrough
- 401 auto-recovery: cloud auth errors offer the sign-in walkthrough inline
- Slash commands with autocomplete, `@file` references with drill-down
  file picker, `!cmd` shell escape
- Update check against this repo's GitHub Releases (24h throttle, fail-silent)
