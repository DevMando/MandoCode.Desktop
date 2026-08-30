# Changelog

All notable changes to MandoCode Desktop are documented here.
Desktop shares its major.minor version with the MandoCode engine generation it
ships (the engine drives 0.14 → 0.15; each product's patch number advances
independently). The exact pinned engine commit is recorded by the `MandoCode`
submodule.

## [Unreleased]

A new engine underneath, and a planner users can actually steer. Desktop moves onto engine
generation 0.15 and Microsoft's Agent Framework, then uses that foundation to make plans durable:
review the work before it starts, edit a step, recover after a restart, and change course when a
failure proves the remaining plan wrong. The workflow planner stays opt-in for this release while
the long-running model soak and local-model token measurements finish. Desktop's version follows
the engine generation, so it moves 0.14.1 → 0.15.0.

### Added
- **Separate tags for Skills and MCP servers.** Add tags in each item's editor, then use the new
  tag dropdown alongside the status filters to narrow the list. The `+` button opens a tag-management
  dialog for that surface, so a Skill tag never appears among MCP tags (or vice versa). Tags are
  Desktop organization metadata and do not alter shared server configuration or portable SKILL.md
  files.
- **Filtered bulk enable/disable.** The Filters group now includes an action that reads **Disable
  all** whenever any matching item is active, or **Enable all** when every matching item is disabled.
  It applies only to the current search, status, and tag result.
- **An Unfinished Plan card appears when an agent has checkpointed work.** Resume continues at the
  first unsettled step; Discard forgets the saved run. The card reflects current checkpoint state
  rather than transcript history, so an obsolete Resume button cannot come back after restart.
- **`/plan <goal>` forces a reviewable plan.** This gives short but cross-cutting work the same
  planning path as a long request, without depending on a message-length heuristic. A one-step plan
  can still be sent straight through with One-shot it.
- **Failed work can produce a revised remaining plan.** Completed steps stay settled, the proposed
  replacement is shown for review, and execution resumes only after approval.

### Changed
- **Automatic plans now start for the work that actually benefits from them.** Desktop recognizes
  explicit checklists, cross-cutting changes, and multiple deliverables instead of treating a long
  message as complex. Questions, research, explanations, and narrow edits stay conversational, and
  the transcript says why planning started. `/plan <goal>` still forces a plan at any time.
- **User messages remain the user's own words.** Automatic planning is routed directly by the host;
  rejected-plan follow-ups and forced skills carry separate, temporary system guidance instead of
  appending hidden `[system: ...]` text to a user-role message.
- **Plan review shows what every step will actually do.** Selecting Edit a step opens a prefilled
  editor. When an early step changes a file name, value, or expectation, Desktop refreshes only the
  dependent steps and shows the complete plan again before execution.
- **Step failures offer clear decisions.** Retry, revise the remaining plan, skip, and cancel are
  separate choices. Retried instructions stay attached to the failed step rather than becoming a
  new chat request.
- **Plan progress and recovery use the engine's durable workflow cursor.** Restarting does not rerun
  completed steps, and the agent is briefed with the restored plan context before it continues.
- **The engine now runs on Microsoft Agent Framework.** Semantic Kernel is gone from the
  codebase entirely; chat history moved onto the new framework's own types. The new path was
  built alongside the old one and verified against real models before the cutover, and the old
  one was only deleted once nothing depended on it. Because this touches every chat turn, it is
  the thing to watch after updating — streaming, tool approval prompts, and model switching all
  ride on it now.
- **The engine builds for .NET 10 and .NET 8 side by side.** Desktop targets .NET 10 and ships
  self-contained, so this changes nothing for anyone running the app; it matters if you build
  Desktop from source, where the engine project now resolves its .NET 10 build.
- **Engine dependencies moved to current releases**, including Model Context Protocol 2.2.0 and
  YamlDotNet 18.1.0.
- **Desktop moved off Semantic Kernel too.** The notes assistant, the snapshot summarizer, and
  the skill author each opened their own connection to Ollama through Semantic Kernel; they now
  use the same Microsoft.Extensions.AI client the engine standardized on. Same prompts, same
  temperatures, same behavior — but Desktop no longer depends on a framework the engine has
  removed. Snapshot recaps and note replies are the surfaces to sanity-check.
- **Engine safety pin: `d084bbb`** (engine 0.15.0), the merge commit from MandoCode CLI PR #80.
  This includes the full planner from PR #79 plus the large-root context guard verified through
  Desktop against a real `@directory` request.

### Fixed
- **The token total now reflects what the provider actually processed.** Desktop no longer adds
  rough character-based estimates for reads, searches, web results, writes, or attachments on top
  of the provider's prompt and completion counts. File reads still show their line counts.
- **Partial completion is no longer called a full success.** A plan that reaches the end after
  skipped or failed work says how many steps completed and reports “completed with issues.”
- **Cancelling a plan no longer produces a second, contradictory error path.** Desktop stops at the
  user's decision instead of showing retry choices or reporting an unexpected failure afterward.
- **Approval and recovery cards stay out of persisted transcript history.** They are live controls,
  not conversation messages, so stale actions are not replayed into a restored session.

### Test coverage
239 Desktop tests pass. New host-level coverage exercises deferred plan execution, instruction
editing, dependent-step revision, checkpoint cards, Resume/Discard actions, semantic step outcomes,
and truthful completion status. The same workflows were also exercised with real models, including
closing the process between steps and resuming from the saved cursor.

## [0.14.1] — 2026-07-28

First-five-minutes polish from watching 0.14.0's fresh-machine debut, plus honest guidance
about cloud model pricing.

### Changed
- **New agents get callsigns by default.** Tabs now open as Morphy, Kernel, Cloud — the
  500-name deck — instead of Agent 1, Agent 2. Numbered naming is still one Settings toggle
  away, and anyone who had explicitly chosen it keeps their choice (only the default flipped).
- **The "can't reach Ollama" screen puts the likely fix first.** On a machine without Ollama,
  the options now read: Install Ollama for me → Open the Ollama Download Page (I'll install it
  myself) → Change the Endpoint URL (Not Recommended) → Retry → Cancel Setup. The endpoint
  override — the option a fresh machine never needs — used to lead the list. The message now
  opens with a plain-language diagnosis ("Ollama isn't running on this computer — it may not
  be installed yet") instead of a raw socket error; the technical detail still shows, dimmed.

### Added
- **Cloud subscription awareness.** Cloud models on ollama.com now require an account with an
  active cloud subscription — without one, requests fail with 403 Forbidden, which previously
  surfaced as a raw error that looked like the app breaking. The subscription requirement is
  now stated everywhere a cloud model is chosen (the starter picker, the sign-in prompt, and
  on every model switch to a `:cloud` tag), and a 403 response gets its own explanation card
  naming the real cause and the two real exits (subscribe, or `/model` to a free local model).
  Deliberately distinct from the 401 path: 403 does *not* trigger the sign-in walkthrough,
  which cannot fix it and would loop.

## [0.14.0] — 2026-07-28

**The first public release of MandoCode Desktop** — the WinUI 3 desktop home for the MandoCode
engine (paired with engine/CLI 0.14.3). Everything below accumulated between the internal 0.1.0
milestone and today: multi-agent tabs on independent models, session persistence and restore,
context snapshots that carry a conversation across models, a notes workspace with an AI assistant,
MCP server support, themes with chat backgrounds, a music player, and the reliability work that
made small local models genuinely usable — a per-request context window, pre-flight compaction,
and a 16k floor.

### Changed
- **The context-window floor is now 16k** (harness 0.14.3). Live testing on a small local model
  showed 8k is unusable in practice: the system prompt and tool definitions consume most of it
  before the conversation starts, so the model lived in a permanent compaction cycle and filled
  the gaps by making things up. The default and the auto-sizing tier for local models under 7B
  both move to 16k (7B+ stays at 32k), and the compaction safety margin was widened so a web
  search landing mid-turn can no longer overflow the window. Existing agents pick the new size
  up on their next model switch; a smaller window can still be set explicitly.
- **Assistant text always starts on its own line.** Inserting a reply at the cursor used to glue it
  onto the tail of whatever line you were mid-way through. It now opens a new line first — unless
  the cursor already sits at the start of one, so an empty note doesn't gain a blank first line.
  Replacing a highlighted selection is unchanged: there you aimed at a specific span, and pushing
  the replacement onto its own line would orphan the rest of that line.
- **The snapshot offer now reads as a card floating over the chat.** It was painted with the same
  panel shade as the docked chrome, which sits within a few points of the transcript background in
  most themes (Visual Studio Dark is `#252526` on `#1E1E1E`), so it blended into the conversation.
  Both stages — the thin bar and the full name + model picker — now use a new raised surface plus an
  accent edge. The shade is derived per theme from that theme's own accent rather than hand-picked,
  so it carries the theme's character (grayscale in E-Ink Paper, navy in W98, phosphor green in
  Phosphor Fwog) and new themes get one automatically. The tint eases off on a theme whose text
  contrast can't afford it — Solarized Light, which already sat below AA on its own panel — and is
  skipped entirely on a theme whose panel already reads as raised, which keeps W98's card the
  period-correct white dialog on the silver desktop.

### Fixed
- **The context window setting now actually reaches the model.** It was exported as
  `OLLAMA_CONTEXT_LENGTH` only when MandoCode launched the Ollama daemon itself — anyone whose
  daemon was already running (the tray app, most commonly) silently got the daemon's own default
  instead, making the Settings field, the "context window sized to Nk tokens" line, and the
  per-model auto-sizing all cosmetic. The window now rides on every chat request as `num_ctx`,
  which outranks the tray app's slider and the daemon default, applies from the next message with
  no restart, and — as a bonus the old design could never offer — is genuinely per-agent: two tabs
  can run different windows against the same daemon. The Settings caption and README stop calling
  it app-wide, and `0` still means "let Ollama decide."
- **W98 chat prompts are readable again.** Your own prompts rendered in the theme's gold, which
  resolves to a dark mustard `#806000` — 3.21:1 on a silver window, under the accessibility floor
  and hard going for anyone with less-than-perfect sight. W98 prompts now use black window text
  (11.5:1), which is the era-correct answer anyway; the silver bevelled frame already marks whose
  turn it is. The "Show more" toggle on a clamped prompt got the same treatment: it sits on the teal
  desktop rather than in the window, where the dim gray it used was 1.44:1 — effectively invisible —
  and is now white underlined at 4.77:1. Other themes are untouched.

### Added
- **Conversations compact themselves before the context window overflows** (pinned harness
  update). Local Ollama never rejects an oversized prompt — it silently drops the oldest tokens,
  system prompt first, which surfaced as "Model returned an empty response" at the end of a
  tool-heavy turn on a small model. The harness now estimates each outgoing prompt (history plus
  every tool schema riding along, MCP servers included) before sending, and when it nears the
  window it folds older history into a recap first and says so in the reply — leaving thinking
  models the generation headroom they spend reasoning before any visible answer appears.
- **Undo for the notes assistant.** A gold undo arrow appears in the note header after the assistant
  inserts or replaces text, putting the note back exactly as it was. Ctrl+Z can't do this job —
  assigning the editor's text resets the TextBox's own undo history, so the one edit you *didn't*
  type by hand was the one the control couldn't reverse, and a Replace could take a whole note with
  it. The offer covers the assistant's last edit only and retires the moment you type, since
  restoring the earlier buffer would otherwise discard whatever you'd written on top of it.
- **Chat backgrounds included in the box.** Settings → Appearance now offers a gallery of three
  backgrounds that ship with MandoCode — **Golden Gate**, **Sequoia Trail**, and **Pismo Beach** —
  so a fresh install has something to pick without hunting for a file. Click a tile to use it, click it again to turn it off; the active one is ringed and
  named. Choosing your own image works exactly as before, and the two are interchangeable — a
  tile is just a starting point, not a mode. The gallery is read from the release's
  `Assets/images/backgrounds` folder at startup rather than listed in code, so a future release
  adds one by dropping the file in. A **fresh install now opens on "Golden Gate"** at the usual 30%
  opacity instead of a bare theme — first launch only, so nobody who has already set (or cleared) a
  background is re-skinned by an update.
- **One-click snapshot from the tab header.** A camera button joins the folder and explorer icons
  at the right of each agent's header, taking the same snapshot offer that lived two clicks deep
  in the tab's "…" menu (which stays). It sits with the header's other *actions* rather than
  beside the model label it captures, so it keeps a fixed position instead of sliding whenever
  the model name changes length. On an empty conversation it answers with the usual "Nothing to
  snapshot" chip rather than presenting a dead button.
- **History cards quote the agent's last reply.** A card showed the opening prompt and the last
  thing you typed; it now adds the last thing the agent *said*, which is usually what you
  actually remember a conversation by. The reply is flattened out of markdown (code fences,
  headings, bullets and tables dropped; link text kept) and clipped to its first couple of
  sentences, so the card doesn't grow — the opening line gives up a third row of wrapping to pay
  for it. The two closing quotes are now labeled **you** and **reply** so it's clear which voice
  is which. Conversations archived before this fill in on the first History open, alongside the
  existing last-message backfill (one file read for both).
- **Agent callsigns.** A Settings → Behavior toggle (app-wide) names new agents from a
  curated 500+ pool of handles — construct-crew, phreak, and cypher energy ("Morphy",
  "Crunch", "Blazor", "Kaos") — drawn from a shuffled deck that doesn't repeat until it runs
  dry. Off (the default) keeps "Agent 1, Agent 2, …"; renaming a tab works either way.
- **Agents know their own name.** The tab's name is the agent's spoken identity: reply cards
  are labeled with it, and the system prompt introduces the model as "{name}, a local AI
  coding assistant running on MandoCode" — so saying "hi" to Blazor gets Blazor, not a
  confused MandoCode. Renaming a tab tells the live conversation. (Engine support is
  null-safe: the CLI keeps its classic MandoCode identity untouched.)
- **Version in the title bar.** The window title reads "MandoCode Desktop v{version}",
  sourced from the same assembly version the update checker compares against releases.
- **Music player.** A music icon on the left rail opens a compact player: play/pause, next,
  stop, volume, and a playlist picker. While music plays the rail icon becomes an animated
  gold equalizer, and hovering it names the current track. **Add playlist** points at any
  folder of MP3s on a local disk (a junction under `~\.mandocode\music` — nothing is copied,
  and the CLI sees the same playlists); **Remove** deletes only the pointer, never the files,
  and only ever offers itself on playlists added this way. Tracks auto-advance through the
  playlist (an engine fix that also benefits the CLI — see the MandoCode changelog).
- **First-run guided setup.** A fresh install now walks through everything in the chat itself:
  reach Ollama (with a one-click winget install when it's missing), start the daemon, and pick a
  starter model from a curated list — cloud recommended, or local tiers with size and hardware
  hints. Setup stays discoverable afterward via `/setup` and a **Run guided setup** button in
  Settings → Connection. Previously a fresh install landed on the raw Settings page.
- **Open-in-Explorer buttons in the file explorer.** Every row gains an open icon next to the `@`
  tag: folders open in Windows File Explorer, files in their default app. (Double-click on
  folders couldn't do this — it fights the expand/collapse toggle.)

### Fixed
- **The app no longer freezes while the notes assistant streams a reply.** Fast models (small or
  thinking models especially) could emit tokens quicker than the reply strip repainted, starving
  the UI thread for the whole response. Streaming now runs off the UI thread and repaints are
  batched on a 100 ms clock, so generation speed no longer affects app responsiveness. Closing
  or switching notes also cancels the in-flight request instead of leaving it generating
  invisibly.

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
- **History cards say where you left off, not just how you started.** A card carried only the first
  thing you said. Since rows are titled by agent name ("Agent 3") unless renamed, that opening line is
  the only thing identifying a conversation — so rather than replacing it, cards now show both: the
  opening message as the topic, and a dimmer **last ·** line with your most recent message. Both are
  the user's own words (symmetric, and your instruction rather than a long formatted reply). The last
  line is hidden for single-turn conversations, where it would just repeat the first. Existing rows are
  backfilled from their conversation logs on the first History open — off the UI thread, one write —
  so old and new cards look the same instead of only new ones carrying the line.
- **History search now reads the conversations, not just their labels.** The search box previously
  matched title/project/model and the 140-character preview, so "find where we worked out the divider
  math" missed unless those words happened to open the conversation — while Snapshots search covered
  the whole recap, making History the inconsistent one. It now searches each archived conversation's
  full text and shows the matching line as a quoted snippet on the card, so a hit whose title and
  preview don't contain the term still explains itself. Metadata matching stays instant and
  synchronous; the body scan is debounced ~220ms and runs off the UI thread behind
  `ConversationTextCache` (lazily loaded per session, revalidated on the log's last-write time), so
  typing never waits on file IO and 60 logs aren't re-read per keystroke. Each scan carries a
  generation stamp so a slower earlier scan can't overwrite a later keystroke's results, and queries
  under two characters don't trigger one at all. Matching and snippet extraction live in
  `Services/ConversationSearch.cs`, kept pure and unit tested (+24 tests).
- **Snapshots panel — grouping, search, and a cleaner import.** Snapshot cards now group by the
  project they were taken in (freshest project first), a search box filters by title/recap/model/
  project, and Import closes the panel and focuses the chat so the "context armed" confirmation is
  the thing you see.
- **Collapsible project groups, in both panels.** Each project group in Snapshots and History is an
  `Expander` you can fold — the answer to "10–100 projects." Which groups you've collapsed is
  remembered across launches (`PanelState` → `panel-state.json`).
- **Delete a whole project group at once.** Opening a project group in Snapshots or History reveals a
  **Delete all *n*** button at the top of the group, clearing it in one action instead of a card at a
  time. It lives in the group's content, not its header, so it only exists while the group is open —
  never crowding the collapse chevron — and it can state the exact count. A single-item group doesn't
  get one at all, since that card's own Delete already does the same job. It confirms first, and
  because a group holds exactly what the panel is *showing*, deleting with a search active removes
  only the matches, which the prompt says explicitly rather than claiming "all". Backed by batched
  `RemoveAll` methods on both stores: one store-file write and one panel rebuild for the whole set,
  where looping the single-item Remove did both once per item.
- **Split view — 2 to 4 agents at once.** A **Split** button puts two agents side by side in a
  resizable view; **Add pane** in the split bar, or **Add to split view** on a tab (its `⋯` menu or
  right-click), grows it to three across or four as a 2×2. **Add pane** is a `SplitButton` — clicking
  it panes the next agent not yet shown, its chevron picks a specific one from those still available
  (the same shape as the terminal's shell picker), so a third pane is never an arbitrary guess.
  Past three, columns alone leave each pane
  too narrow for a transcript plus an input box, so four wraps instead of shrinking further. Every
  divider is draggable and repartitions only the two panes either side of it, so adjusting one split
  never nudges a third pane. The pane set is an explicit, remembered choice (never set by
  plain-clicking a tab): clicking a paned agent's tab shows the split, clicking any other agent shows
  it normally while the set waits, and dropping below two panes turns the split off and leaves you on
  the agent that survived. The set and its divider positions persist across restarts, keyed by each
  agent's durable persist-key so a project folder that's gone drops one pane rather than shifting all
  of them. Panes are ordinary agent views moved between grid cells via `Grid.SetColumn`/`Grid.SetRow`
  — never re-parented — so every WebView and its live transcript survives the switch; the row and
  column tracks are rebuilt in code per pane count, and track definitions plus dividers are the only
  things that change. Geometry and divider math live in `Services/PaneLayout.cs`, free of WinUI types
  and unit tested. The split bar uses chips with `MenuFlyout` pickers rather than `ComboBox`es,
  which sidesteps the `COMException 0x80070490` that rebuilding ComboBox item containers triggers.
  Named *split view* rather than *compare* because comparing two models on one prompt is only one of
  its uses — at three or four panes you're usually watching agents work in parallel, not comparing.
- **AI-named snapshots.** Saving a snapshot without a name now asks the summarizer for a short,
  descriptive title from the recap; uniqueness against existing titles is then guaranteed in code
  (`SnapshotNaming`), so two snapshots can't share a name.
- **Unread badges.** The History and Snapshots rail badges are now unread counts — items newer than
  the last time you opened that panel — and clear when you open it, rather than showing a running
  total. The "last seen" marks persist across launches.
- **Integrated terminal.** A sliding terminal panel (Ctrl+` toggles it, Ctrl+Shift+` maximizes)
  runs a real shell through ConPTY, rendered with xterm.js inside WebView2 — no new native
  dependencies. A shell picker (`ShellCatalog`) selects PowerShell/cmd/etc., and the terminal
  opens in the active agent's project folder. The terminal glyph at the left of the panel's tab
  strip collapses the panel, matching the chevron on the far right — so the icon that opened the
  terminal from the rail is also an icon that closes it.
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
- **Notes - a jot pad with a prompt attached.** A **Notes** rail panel for writing things down without
  leaving the app. **New** creates a plain text file under `~/.mandocode/notes` (beside the config file
  the CLI shares) and opens an editor docked next to the chat: autosave on a 1.2s debounce plus Ctrl+S,
  rename in place, Show in Explorer. Notes are **app-wide**, the same call as snapshots and session
  history - a note is something you want to write down *now*, often between projects or before an agent
  is even open, so nothing here needs one. What survives of "which project was this about" is a plain
  SUBFOLDER: a new note is filed under the active agent's folder name when there is one, and sits loose
  at the top when there isn't. Grouping therefore costs no metadata and cannot drift - you re-file a
  note by dragging it in Explorer.
  - **The filesystem is the store.** No notes index, no JSON, which is also why the pad lives in
    `~/.mandocode` rather than LocalAppData: these are your files, meant to be greppable, syncable, and
    openable in any editor. Discovery walks one folder plus its immediate subfolders (one level only -
    a jot pad with a hierarchy is a filing system, and search is the better answer to "where did I put
    it"). In exchange no row can point at a file that isn't there: a note written in Notepad shows up,
    one deleted outside the app disappears. Search matches note BODIES and quotes the matching line.
  - **A prompt bar under both surfaces.** Chat-shaped, but the document above it is your note rather
    than a transcript: replies land in the bar's own strip and reach a note only through **Insert** (at
    the cursor, replacing the selection if there is one) or **Replace note**. On an open note the
    question carries the LIVE editor buffer, so the model always sees the note as it is right now -
    including keystrokes autosave hasn't written yet - and only the current message carries it, so a
    long thread doesn't ship stale copies. On the list the question is about the pad: every note's
    title and first line plus the full text of whatever the search box is matching, with the bar
    stating what it was given (`12 notes listed - 3 read in full`), because a capped read that looks
    total is the one thing an "ask about all my notes" box must not do.
  - **The assistant has no tools, by design.** `NoteAssistant` builds a bare Ollama kernel with no
    plugins, filters, or tools - the same shape as `SnapshotEnhancer`. With no file access, "nothing
    writes your note but you" is true by construction rather than by policy, so no approval machinery
    is needed: the only route from a reply into a note is a button you pressed. Its model comes from
    the chip under the prompt (defaulting to the app-wide default) and is remembered; the thread is
    per-note, in memory, and cleared when you switch notes - notes aren't conversations.
  - **The editor is not the only writer, and doesn't assume it is.** These are plain files, so Notepad,
    VS Code, a sync client, or git can change one under you. A `FileSystemWatcher` compares the file
    against what the editor last wrote: identical means the write was ours, changed-while-clean is
    adopted silently, and changed-while-you-were-typing raises a conflict you resolve - *use the
    version on disk* or *keep what I typed*. A note deleted from under unsaved edits offers to save it
    back. No path silently discards typing.
  - `NoteText` owns the newline round trip: a WinUI `TextBox` normalizes every newline to a bare CR, so
    writing `Editor.Text` straight back out would turn a Notepad-authored CRLF note into one endless
    line - and comparing the loaded file text against `Editor.Text` made merely OPENING a note look
    like an edit, which autosaved untouched files. Both are covered by tests.

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
