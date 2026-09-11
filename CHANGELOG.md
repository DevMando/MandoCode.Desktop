# Changelog

All notable changes to MandoCode Desktop are documented here.
Desktop shares its major.minor version with the MandoCode engine generation it
ships (the engine drives 0.14 → 0.15; each product's patch number advances
independently). The exact pinned engine commit is recorded by the `MandoCode`
submodule.

## [Unreleased]

## [0.15.0] — 2026-09-10

**A new AI foundation, plans you can resume, and more ways to work alongside your agents.**
This release moves MandoCode Desktop from Semantic Kernel to **Microsoft Agent Framework (MAF)**.
MAF now coordinates the engine's conversations, tools, and plan execution. Desktop's notes
assistant, snapshot summaries, and skill author also move off Semantic Kernel, using
Microsoft.Extensions.AI. Your existing models, providers, skills, MCP servers, and approval
controls continue to work with the new foundation.

### Release highlights
- **Review a plan, change it, and pick it up later.** Approved plans save their progress. Edit
  steps before work starts, resume unfinished work after restarting, and approve a revised plan
  when the original approach fails. Completed steps stay completed.
- **A built-in browser—for you and your agents.** Open websites in browser tabs, enter URLs, and use
  familiar navigation controls. “Explain this page” refers to the tab you were viewing when you
  sent the message; agent actions target explicit tabs, including forms inside embedded frames.
- **See the work as it happens.** A read-only terminal tab shows each agent's commands and live
  output. Preview project files, edit text, open PDFs, and inspect local development sites without
  leaving Desktop. The command view displays captured output; it is not a persistent agent shell.
- **Coordinate multiple agents.** Mention another open agent with `@` to ask a question or hand
  off work. Named split panes and drag-and-drop make it easier to arrange their conversations.
- **Make each agent your own.** Save settings per agent, keep global defaults for new agents,
  and pin frequently used models. Nine new themes, themed backgrounds, and readability fixes
  expand the appearance options.
- **Spend less time recovering your place.** Model choices survive restarts, changing folders
  keeps the conversation, repeated restore notices are removed, and deleting notes, snapshots,
  or history no longer makes the remaining list slide back into place.

**Included engine:** [MandoCode CLI v0.15.0](https://github.com/DevMando/MandoCode/releases/tag/v0.15.0),
pinned to release commit `e67578251c5716a6ede192a2e3e82e7dfda7c8f0`.
Desktop advances from 0.14.1 to 0.15.0 to match the engine generation. The Windows download
remains self-contained; users do not need to install .NET separately.

### Added
- **Split view tells you which agent is which, and you rearrange it by dragging.** Each pane now
  carries its own header naming the agent shown beneath it, so you read the name where the agent is
  instead of matching it up from a strip along the top. Agents already on screen drop out of the tab
  strip, leaving it as a list of what is *not* currently visible. Drag a tab onto a pane to put that
  agent there, or drag one pane's header onto another to swap the two — each target says what the
  drop will do before you release. With a single agent open, dragging another onto the chat area
  starts a split, and which half you hover decides which side the dragged agent takes. The per-pane
  dropdowns are gone; the tab menu still offers "Add to split view" for anyone who would rather not
  drag.
- **Agents can talk to each other.** Typing `@` now offers the other open agents before project
  files, so you can address one by name from another's conversation. An agent can check what another
  is doing, read its conversation, ask it a question and get a real answer back, or hand it a whole
  job. Handing over a job does not block: your agent replies immediately and stays available while
  the other works, tells you how far along it is whenever you ask, and announces the result in the
  conversation when it finishes. Progress is summarised from what the other agent is actually doing
  — the step it is on, the commands it has run — rather than by copying its conversation across, so
  a job that runs for ten minutes costs no more to keep track of than one that runs for ten seconds.
  Questions relayed between agents are labelled as such, so an agent always knows whether it is
  talking to you or to another agent, and treats what another agent tells it as a claim rather than
  a fact.
- **Nine themes that imitate a physical medium, not just a colour scheme.** Alongside the existing
  e-ink and CRT looks, MandoCode Desktop now ships a monochrome amber terminal, a vacuum-fluorescent
  panel, a vector scope, a passive-matrix LCD, a Solari split-flap board, a cyanotype blueprint, a
  two-ink risograph print, continuous-feed printer paper, and microfiche. Each reproduces how its
  medium actually made an image rather than only borrowing its colours — the LCD ghosts instead of
  glowing, the split-flap board can only show uppercase because a flap carries one printed
  character, and the cyanotype is a negative, so emphasis is a whiter stroke rather than a heavier
  one. The theme list is ordered so neighbours share a mechanism, which keeps it skimmable now that
  there are twenty-five entries.
- **Your chat background can match the theme.** Themes that imitate a screen or a printing process
  also process your background image the way that medium would have reproduced it — quantised to a
  panel's few shades, rendered as amber phosphor, or laid down as two-ink halftone. On by default,
  with a switch in Settings → Appearance to keep the picture in full colour. It affects only those
  themes, and only when a background is set.
- **A new background in the box, and it is what a fresh install opens on.** Settings → Appearance
  gains **Midnight Ramen** — a rain-slicked night shot outside a 24-hour ramen shop on the Hakone
  road — and it now leads the gallery, so someone opening MandoCode for the first time lands on it
  instead of Golden Gate. The three existing backgrounds are all still there, one place further
  down the list.
- **The MandoCode wordmark has its own typeface.** The name in the top-left is now set in Permanent
  Marker, bundled with the app rather than fetched from a font service, so it renders identically
  offline. It takes each theme's accent colour, so it re-inks itself as you switch themes.
- **Watch the agent's shell commands run.** The terminal panel gains a read-only tab per agent
  showing every command that agent runs — the command and the folder it runs in, its output line by
  line as it arrives, and whether it finished, failed, or was killed for taking too long. A long
  build is no longer ninety seconds of silence. The tab is deliberately read-only: the agent's
  commands still run through captured pipes rather than a terminal, so nothing about what the model
  receives, how commands are timed out, or how they report their exit code changes. The view keeps
  more scrollback than the model is given, so the tail of a long build is visible even though the
  model's copy is truncated. Output is recorded from launch, so opening the panel after a build
  still shows it. A tab never steals focus while you are working in a shell — its title accents
  instead — and closing one discards that agent's recorded output. The terminal button on the rail
  carries a dot when an agent has produced output you have not seen, so the tab is discoverable
  without opening the panel to find it: the dot pulses while a command is actually running and goes
  still once it finishes, and following it opens the panel directly on that agent's output rather
  than on an empty shell. Themes that switch off motion get the still dot in both cases.
- **PDFs open in the preview pane.** Selecting a PDF in the Explorer shows it in the browser's own
  viewer — scroll, zoom, search, print — instead of only offering to open it in another
  application. This is for reading: a PDF's text and structure are not reachable through the page
  DOM, so the assistant cannot read one. It is told that plainly, and told to judge the document
  from a screenshot on a vision-capable model, rather than being handed an empty page it might
  report as a blank document.
- **Pinned and recently used models rise to the top of the model picker.** A pin on each row in
  Settings keeps the models you actually use at the top; below them sit the models you most
  recently switched to, then everything else alphabetically. Pins and recent use are remembered
  across launches and apply to every agent. The tail stays alphabetical on purpose, so a long
  list does not reshuffle every time a model is pulled.
- **An interactive browser preview the agent can drive.** The agent reads a page's text,
  controls, values, and browser errors, then clicks, hovers, presses keys, fills fields, selects
  options, scrolls, and waits for elements. Pointer and keyboard input are dispatched as genuine
  browser events, so a page cannot tell them from a person. The preview's cache is bypassed and
  refreshes ask for a cache-bypassing reload, so an edited script or stylesheet is never reviewed
  against its previous version and project files need no cache-busting query strings.
- **Screenshots for the questions the page's structure cannot answer.** Visual layout, overlapping
  or clipped elements, spacing, and canvas rendering. The capture is handed to the model as real
  image input rather than text, and the model's ability to accept images is checked *before*
  capturing — a text-only model is told plainly that visual layout could not be checked instead of
  being handed bytes it would silently drop. Screenshots are also delivered during plan execution.
- **Development server previews.** The agent can exercise a running app instead of a static file.
  Only HTTP or HTTPS on loopback with an explicit port is accepted; external hosts, LAN addresses,
  other schemes, and URLs carrying credentials are refused.
- **Embedded form DOM support.** Browser tools discover cross-origin and nested frames
  and can inspect, fill, select, scroll, wait, and read back fields using explicit tab and
  frame IDs. Navigated or removed frame targets fail without falling back to the parent.
  Inspection distinguishes uninspected frame content from missing form fields.
- **A built-in browser with shared tabs.** The browser button beside Snapshot opens the existing pane
  with tabs, a new-tab button, an address bar, and back/forward/reload controls. User
  websites and agent previews coexist. Agent actions require explicit tab IDs; references
  to “this page” retain the tab viewed when the message was sent, even after switching tabs.
  Closing a targeted tab reports failure rather than redirecting the agent to another tab.
- **Docked file previews from the Explorer.** Selecting a file opens a resizable, read-only
  preview between the chat and file tree. Code, text, configuration, documentation, and common
  image formats open in Desktop; unsupported or large files offer the existing external-open
  action. An open preview reloads once after an agent turn so it shows the final saved file state.
  Text previews can enter an explicit edit mode and save with the button or `Ctrl+S`; unsaved
  edits are protected from agent refreshes and saving warns before replacing a newer disk version.
  HTML, SVG, and local page assets render in a project-local browser mode with an in-pane reload
  control; browser navigation cannot replace the preview with an external site. The agent can open
  a finished HTML, HTM, or SVG page in that pane and refresh an open page through its normal tool
  calls, while the pane also refreshes once automatically when a turn changes the open file.
  Opening a file never adds it to the agent's context—use the explicit `@` button to attach it.
- **Separate tags for Skills and MCP servers.** Use the new gear-icon **Tags** button on each row
  to assign tags from a checkbox menu; assigned tags appear as colored chips beside that button.
  The `+` button in Filters opens a tag-management dialog where new tags receive a chosen color.
  Skill tags and MCP tags remain separate, and tags do not alter shared server configuration or
  portable SKILL.md files.
- **Filtered bulk enable/disable.** The Filters group now includes an action that reads **Disable
  all** whenever any matching item is active, or **Enable all** when every matching item is disabled.
  It applies only to the current search, status, and tag result. The visible rows stay in place
  while their switches update; MCP servers are saved and reloaded once for the full filtered set.
- **Responsive management filters.** The Filters group stays right-aligned beside the action buttons
  when both fit. On a narrow window, it moves as a complete second row below those actions instead
  of overlapping or partially wrapping.
- **An Unfinished Plan card appears when an agent has checkpointed work.** Resume continues at the
  first unsettled step; Discard forgets the saved run. The card reflects current checkpoint state
  rather than transcript history, so an obsolete Resume button cannot come back after restart.
- **`/plan <goal>` forces a reviewable plan.** This gives short but cross-cutting work the same
  planning path as a long request, without depending on a message-length heuristic. A one-step plan
  can still be sent straight through with One-shot it.
- **Failed work can produce a revised remaining plan.** Completed steps stay settled, the proposed
  replacement is shown for review, and execution resumes only after approval.
- **Settings are split into global defaults and per-agent settings.** The rail's Settings page is
  now Default Settings: the starting point every new agent is seeded from, and reachable with no
  agent open. A gear in an agent's header, between the snapshot and folder icons, opens that
  agent's own settings in a docked pane beside its conversation. Both surfaces are the same form
  bound to different targets, so the two can never drift apart.
- **An agent keeps its own settings after it is closed and reopened.** Per-agent settings used to
  live only in memory and were lost with the process. An agent now becomes independent the first
  time its settings are saved, and is restored on those settings whether it comes back from a
  relaunch or from the History panel. An agent that has never been configured keeps following the
  defaults, so raising a default still reaches every agent you never touched. API keys are never
  written to a per-agent file; they stay in the shared configuration and are supplied to each agent
  in memory.
- **Settings apply when saved, not as you type.** Every control edits a pending copy, a Save button
  reports how many changes are waiting, and closing the page or pane discards anything unsaved.
  Values are still checked as they are entered, so a rejected number is refused where it is typed
  rather than at save time. Two further actions on an agent's pane move settings between the two
  scopes: Apply Global Defaults replaces an agent's settings with the defaults and lets it follow
  them again, and Save to Global Defaults makes an agent's settings the starting point for new ones.
- **`/compact` condenses a long conversation on demand.** A session filling its context window can
  be compacted deliberately instead of waiting to be forced into it, and the compacted context is
  persisted, so it survives a restart rather than being rebuilt on the next launch. `/clear` now
  states plainly that it wipes context completely, so the two are not mistaken for each other.

### Changed
- **The bundled backgrounds are renumbered to put the new default first.** Golden Gate, Sequoia
  Trail, and Pismo Beach are unchanged and still in the gallery, but each has moved down one
  position. If you had picked one of them, your background keeps working exactly as before — the
  image lives in your own data folder — but its tile may no longer show as the selected one in
  Appearance. Re-picking it restores the highlight.
- **Completed turns now keep routine activity out of the conversation flow.** File operations,
  tool calls, and routine connection progress remain visible while work is running,
  then fold into an expandable Activity section when it completes. Assistant replies, warnings,
  errors, pending approvals, plans, update notices, and session-restore notices remain visible.
  Restored activity starts collapsed so old operational detail does not look like fresh work.
- **Long completed changes now have one Work completed rollup.** Related tool activity, diffs,
  commands, and approval outcomes collapse together after a turn ends, with the number of files
  changed, line totals, distinct approval states, and commands shown in the summary. Expanding it
  preserves the original sequence and individual controls. Auto-approved deletions and MCP tool
  requests stay part of that routine sequence; destructive warnings remain visible only while a
  manual decision is needed.
- **Completed approval notices collapse into a compact state card.** Successful approvals and
  auto-approval notices keep their full existing appearance while work is active, then summarize
  each distinct state once (for example, `✅ ⚠️`) with the original cards available on expand.
  Approval and tool-activity summaries share the same compact card and rotating chevron. Pending
  prompts, denials, and approval errors remain visible.
- **Restored Desktop sessions no longer repeat the success message for conversation memory.** The
  restored transcript is already visible, so full memory recovery is silent. Desktop calls out only
  reduced-memory restores and unavailable conversation memory.
- **`@` directory references now render as a normal List operation.** Missing references use a
  clear warning instead of raw `[Directory]` or `[Not found]` parser-style labels.
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
- **The engine now runs on Microsoft Agent Framework.** Semantic Kernel is no longer part of the
  runtime. Chat uses the framework's agent and tool abstractions, while plan execution uses its
  workflow runtime for progress, retry, and recovery. Streaming, tool approval prompts, and model
  switching continue to use the same product flows on the new foundation.
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
- **Released engine pin: `e675782`** (tag `v0.15.0`). This is the exact CLI release that ships with this
  release. It includes workflow planning as the default plan runner, manual conversation
  compaction, automatic planning based on task shape, the large-root context guard verified
  through Desktop against a real `@directory` request, image content counted toward the context
  estimate, browser tab and frame listings always read live rather than answered from the
  recent-call cache, host-supplied agent tools, the command output sink behind the agent's
  read-only terminal tab, and executor-owned step completion in place of per-step verification by
  a second model.

- **The Settings model picker no longer waits on the network to show your model.** The configured
  model appears selected immediately and the installed-model list fills in behind it. A failed or
  empty fetch now keeps the picker as it was rather than blanking it, and a configured model the
  fetch does not return — a cloud model with nothing pulled locally — stays listed.
- **The preview pane shows a globe when it is showing the browser.** It previously showed a
  document icon whether the pane held a file or a live web page.
- **Model status is now one line instead of several cards.** The active model, its image
  capability, and whether it runs in the cloud appear together as `model · active · text-only ·
  cloud`, replacing the separate capability notice and status pill. Routine model switches no
  longer show the cloud subscription caveat; subscription failures still receive an explanation.
- **Screenshots stay honest when the window is not on screen.** Capture reads the window's
  rendered surface, so it depends on the app having one. A minimized window is now reported as
  needing to be restored, within a bounded wait, rather than consuming the whole operation
  deadline and surfacing as a vague timeout. A capture that comes back nearly uniform is flagged
  as possibly blank, so the model says the image looks empty instead of describing detail it
  cannot see. Hidden, transparent, and occluded windows were measured and capture normally.
- **A blocked click now names what is covering the target.** Instead of reporting only that an
  element is covered, the result identifies the element sitting on top of it — usually an overlay,
  a sticky header, or the suggestion list a field opens when it is filled.

### Fixed
- **Leaving split view restores the agent tabs.** Agents hidden from the tab strip while visible
  in split panes return when you exit the split, so you can switch between them normally again.
- **Restoring a conversation no longer repeats context-window notices.** Old sizing notices are
  omitted from restored history, and initialization does not announce intermediate model settings.
  The restored conversation and final active model remain visible.
- **Switching to a cloud model no longer repeats the subscription notice.** Picking a cloud model
  announced that cloud models need an ollama.com subscription. It was meant to say so once, but the
  "already said it" memory belonged to a single agent rather than the app, so every agent you
  switched said it again — and a tab restored onto a cloud model marked the notice as shown without
  displaying it, suppressing it for the rest of that session. The notice is gone entirely rather
  than repaired: the model chip already marks a model as `cloud`, and if a cloud request actually
  fails you now get the only message that was ever actionable — that the account is signed in but
  has no active subscription. The setup wizard still explains cloud versus local while you are
  choosing, and still offers to sign you in when you are not.
- **Deleting a snapshot, past conversation, or note no longer resets the list.** These panels
  rebuilt themselves after every deletion, so the surviving cards slid back up and expanded project
  groups collapsed — losing your place in the middle of tidying up. The lists now update in place:
  the deleted card goes, everything else stays where it was, and group counts and "Delete all"
  labels update immediately.
- **Status, plan, approval, and recovery notices stay readable over a busy background.** These are
  now drawn as high-contrast cards rather than plain text, so they survive a chat background set to
  a high image opacity. Connection and status pills keep their existing coloured-dot treatment.
- **The per-turn token count is readable in every theme.** It was faint text trailing at the right
  edge, which put the smallest, palest thing on screen in the exact corner where the themes that
  imitate a screen are darkest — measured at well under half the contrast it needed, and effectively
  invisible on the CRT and amber terminal themes. It now sits in a small pill with its own
  background and border, so it reads as a deliberate element rather than trailing text and survives
  a busy chat wallpaper too. The corner shading on those themes was also too heavy in general and
  has been eased, which helps ordinary text near the edges of the pane as well.
- **Secondary text is readable in every theme.** Timestamps, file paths, status lines and the hints
  under headings were below the accessibility contrast floor in half the shipped themes — several
  faithfully so, since palettes like Dracula and One Dark ship famously faint comment colours
  upstream. Those palettes are unchanged; the text is now lifted just far enough to clear the floor,
  blended toward each theme's own text colour so it stays that theme's shade rather than drifting
  grey. A theme that was already readable is left exactly as it was. Themes viewed through an
  overlay, such as the CRT tube, are held to a higher bar, because scanlines take contrast the
  palette's own numbers do not account for.
- **The CRT theme's phosphor glow follows the text it surrounds.** Every glyph glowed the same
  blue regardless of its colour, so red errors carried a blue halo. On a real tube the glow is the
  phosphor being excited, so error text now glows red and success green. Code blocks keep a tighter
  glow, since a full halo turns syntax highlighting into a smear.
- **The "no agents open" message stays readable over a chat background image.** With a background
  image set, closing the last agent left the message painted straight onto the picture, where a
  busy or light image could make it hard to read. The message now sits in a translucent card — the
  same treatment the chat's own message bubbles use — so the image still shows through while the
  text keeps a predictable surface behind it. The explanatory line under the heading is also less
  dimmed, since dimming costs contrast that a background image has already spent.
- **The preview pane's open and attach buttons now work on web pages.** Both acted only on a
  project file, so on a website they did nothing at all and gave no reason why. Open now hands the
  page to the system's default browser and the attach button puts its address into the prompt,
  while a preview of a project file still opens that file in its default application. A file
  preview is served through an address that only resolves inside the app, so the two cases stay
  deliberately distinct. Only ordinary web addresses are handed to the system.
- **Per-tab model choices survive a restart.** Restoring a session initialized every tab at once,
  and a tab boots on the default model before moving onto its own saved one. Any workspace write
  during that window recorded the default over a tab's real model, so an agent you had switched
  could come back on the default — permanently, since the saved value was gone. Workspace writes
  are now held until every tab has settled, and a tab whose restore does not finish keeps its
  saved model instead of having the fallback written over it.
- **A restored tab no longer announces two different models at startup.** Restoring a session
  announced the default model, then immediately switched to the tab's saved model and announced
  that one as well. The first notice was obsolete the moment it appeared, and could advertise
  image support on a model that was never used.
- **The token total now reflects what the provider actually processed.** Desktop no longer adds
  rough character-based estimates for reads, searches, web results, writes, or attachments on top
  of the provider's prompt and completion counts. File reads still show their line counts.
- **Partial completion is no longer called a full success.** A plan that reaches the end after
  skipped or failed work says how many steps completed and reports “completed with issues.”
- **Cancelling a plan no longer produces a second, contradictory error path.** Desktop stops at the
  user's decision instead of showing retry choices or reporting an unexpected failure afterward.
- **Approval and recovery cards stay out of persisted transcript history.** They are live controls,
  not conversation messages, so stale actions are not replayed into a restored session.
- **Changing an agent's project folder no longer erases the conversation.** Pointing an agent at a
  different folder rebuilt its session from scratch and cleared everything said up to that moment,
  so a folder change part-way through a task lost all context. The conversation is now kept. The
  agent still repoints its tools, system prompt, and project skills at the new folder, and is told
  the folder moved so it re-reads files rather than reusing paths from before the change. Only
  folder changes from here on benefit — conversations already cleared cannot be recovered.

### Test coverage
Host-level regression coverage exercises deferred plan execution, instruction
editing, dependent-step revision, checkpoint cards, Resume/Discard actions, semantic step outcomes,
and truthful completion status. Browser coverage adds explicit tab targeting, frame identity, and
plan-card review content. Settings coverage pins the rules a saved agent depends on: that a stored
agent configuration never contains an API key, that it is unaffected by app-wide changes made
elsewhere, and that an agent matching the defaults is treated as still following them. The same workflows were also exercised with real models, including
closing the process between steps and resuming from the saved cursor.

An opt-in smoke test drives a real WebView2 browser end to end: DOM reads, pointer and keyboard
input, cross-origin and nested frame discovery, filling and reading back embedded form fields
without submitting, background-tab isolation, and rejection of stale or removed frame targets.

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
