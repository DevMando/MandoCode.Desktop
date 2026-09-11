# Agent mentions — design

Status: **built**, except `review_agent_work`. See the build order below.

## What it is

Typing `@Knuckles` in one agent's input addresses another open agent. The addressed agent can be
asked what it is doing, have its work reviewed, have its transcript read, or — at the far end — be
asked a genuine question that costs it a turn.

## The insight the design turns on

The obvious implementation is "pull Knuckles' transcript into Sonic's context." That is the wrong
default, for two reasons:

1. **It is the expensive option.** A transcript is thousands of tokens, copied into Sonic's window
   and paid for on every subsequent turn of Sonic's conversation. Asking Knuckles a question costs
   one turn and returns a few hundred tokens of *answer*.
2. **It is the lossy option.** Sonic has to work out which parts of a long transcript matter.
   Knuckles already knows.

So the transcript is the *escalation*, not the starting point.

The second insight is that **most of what you want does not require waking the other agent at all**:

| Tool | Wakes the target's model | Works while target is busy | Answers |
|---|---|---|---|
| `agent_status` | no | **yes** | "are you done", "what are you on" |
| `review_agent_work` | no | **yes** | "is the work any good" |
| `read_agent_transcript` | no | **yes** | "what exactly was said" |
| `ask_agent` | **yes** (in the background) | no | judgment, explanation, intent |

Three of four are pure observation. They carry no loop risk, no concurrency problem and no approval
question, because nothing runs. All the hazard is concentrated in the fourth.

`ask_agent` wakes the target but does not block the caller — it hands off and returns at once, the
same as `delegate_to_agent`. See "Delegation, the inbox, and the rolling digest" below.

## Build order

Each step is useful alone and complexity rises monotonically. Stopping after any of them leaves a
coherent feature.

1. `agent_status` — **built**, as `list_agents` and `get_agent_status`
2. `review_agent_work` — still proposed
3. `read_agent_transcript` — **built**
4. `ask_agent` — **built**

## The tools

Registered per agent through `AIService.SetHostTools`, the same seam the browser tools use
(`AgentSession` already calls it with `AIFunctionFactory.Create(...)` for each preview tool). The
host owns them, so they can see every open session while each agent's own file tools stay bounded to
its own root.

### 1. `agent_status(name)`

Returns host-observable facts. No model call anywhere.

Everything needed already exists:

- `BusyStateService.IsBusy` — working or idle
- `ChatController.PlanProgressChanged` — already emits `CurrentStep` / `TotalSteps`
- `AgentCommandLog.IsRunning` and its scrollback — the commands that agent has run
- the session's project root, model and title

This is the tool that makes "have you finished the X task?" answerable *at the moment you would
actually ask it* — which is while the other agent is still working. An earlier draft of this design
had the busy case refuse; that would have failed the feature's most common question.

### 2. `review_agent_work(name)`

Returns the target's working-tree diff so the *asking* agent can form its own judgment.

Self-assessment is the weakest form of review — asking an agent whether its own work is good is
nearly worthless. Handing the reviewer the artifact is a real review.

The artifact already exists: `GitQuickStatus` yields `GitChangeEntry` (path + change kind) and
`GitFileDiff` (parsed diff lines, with a `Truncated` flag for oversized diffs). Pair it with the
target's `AgentCommandLog` and the reviewer sees both what changed and what was run to produce it.

**Torn reads.** A working tree that is actively being written gives a half-finished picture. The
mitigation is already in hand: this tool checks `IsBusy` first and prefixes its result with a
warning when the target is mid-turn, rather than silently returning a diff that is about to change.

### 3. `read_agent_transcript(name, ...)`

The escalation. Expensive and explicit, reached for when an answer or a diff was not enough.
`IAiService.ExportHistoryJson()` already serialises a conversation.

The model chooses between this and `ask_agent` on its own — describing both honestly is what
implements "pull the transcript only when more context is needed." There is no condition to detect.

### 4. `ask_agent(name, question)`

Runs a real turn on the target's model and returns its answer.

`AIService.ChatStreamWithHostInstructionAsync` is the right primitive. Its guidance is carried as
"a real, transient system-role message… available for this turn and its continuations but removed
afterward, so it cannot masquerade as user-authored text or affect later turns" — exactly what a
delegated turn needs. Knuckles is told "Sonic is asking you this" for one turn only, with no
contamination of its own later conversation.

Open problems, all of which belong to this tool alone:

- **Loops — settled: `AgentCallChain`, an AsyncLocal chain with two limits.** A key set catches a
  true cycle (an agent already in this chain being asked again); a depth cap of 2 catches a chain
  that never repeats anyone but keeps going. AsyncLocal rather than a field, because the chain
  belongs to one call sequence — two conversations asking questions at once must not consume each
  other's budget.
- **Concurrency — settled: refuse, and route the caller to the read tools.** If the target is
  mid-turn, `ask_agent` returns its status plus a pointer to `read_agent_transcript`, so the busy
  case degrades to reading rather than dead-ending. This matters because busy is the COMMON case:
  "have you finished X?" is asked precisely when the answer might be no. Queueing was rejected — a
  caller that waited would stall its own turn behind work of unknown length. (Since 2026-09-10 this
  check runs BEFORE the hand-off rather than on the awaited result; see the delegation section.)
- **Acting vs answering — settled: a delegated turn is a FULL turn.** The target answers with all
  of its own tools, exactly as it would answer the user, because the point of asking a colleague is
  that they can go and look. A read-only delegated turn would make the feature useless for what it
  is for.

  The containment is that the target's own approval gates still stand, and they raise their dialogs
  in the target's own tab, where the user can see who is being asked to do what. The host
  instruction that frames the turn ("another agent is asking you this") is a *framing, not a
  sandbox* — a model can wander past prompt-level guidance, so it is not relied on for safety.

## The `@` picker

`ChatTabView.Input.cs` already implements `@` for project files: it walks back from the caret to the
token start, and if the token opens with `@` it filters through `FileAutocompleteProvider` and calls
`ShowSuggestions(SuggestMode.File, ...)`.

Agents join that same picker and **rank above files**. Rationale:

- Open agents are a small, closed, known set; project files are thousands. A short list on top costs
  the file case almost nothing.
- Callsigns are capitalised single words, so genuine collisions with real filenames are rare, and
  the picker disambiguates the rare ones visually.
- `@` already means "a participant" to anyone who has used Slack. A second sigil would be a thing to
  learn for no benefit.

Implementation notes:

- A new `SuggestMode.Agent`, because `AcceptSuggestion` branches on the mode and an agent mention
  substitutes differently from a file path — no trailing `/` drill-in behaviour, and the accepted
  text should be the callsign, not a path.
- The agent's own tab must be excluded from its own picker.
- Suggestion rows want a distinguishing glyph and a subtitle (folder name, or busy/idle), so an
  agent row never reads as a file row.
- **The host must also learn about mentions, not just the picker.** An earlier draft of this doc
  claimed the picker was an affordance and no host-side parsing was needed. That was wrong, and
  testing found it immediately: `ChatController.ProcessFileReferences` expands every `@token` at
  send time and warns `Couldn't find the referenced file or folder: Ninja` when the lookup misses.
  A mention never reached the model at all. Agent names must be resolved *before* the file lookup
  and skipped by it.
- Agents win over files on a name clash. A callsign is a deliberate act of addressing someone; a
  same-named file is a coincidence, and that file stays reachable by any path carrying a separator
  or an extension.

## Who am I talking to

A receiving agent must be able to tell a relayed question from something the user typed, and it must
be able to tell *structurally* rather than by reading the content.

The first implementation put the attribution only in the host instruction. That was not enough, and
testing showed why within minutes: the instruction is transient by design — the engine removes it
once the turn ends — so the peer's question stayed in the receiving agent's history as an ordinary
user turn. Asked afterwards who it had been talking to, the agent answered that the last message
"claimed" to be from another agent. It was reasoning from content, because content was all it had.

Worse, one agent told another "the person you're talking to is Mando", and the receiver had no way
to weigh that. A claim inside a relayed message had the same standing as a fact.

So:

- **Every agent-to-agent message is wrapped in a host-applied envelope** (`PeerMessageEnvelope`)
  naming the sender and stating it is not from the user. This is part of the message, so it persists
  in history rather than evaporating with the turn.
- **A message without an envelope is from the user.** That is the rule the framing states, and it is
  the only rule needed, because the host is the only thing that can add one.
- **`Wrap` strips any envelope already present in the payload.** Without that, "only MandoCode adds
  this" would be a claim the code did not keep — one agent could relay a message that appeared to
  come from a third, and an agent innocently quoting the marker while discussing this feature would
  produce the same confusion.
- **Claims inside a relayed message are that agent's assertions, not facts.** The framing says so
  explicitly, including claims about who the user is.

## Visibility

An exchange between agents appears in **both** transcripts — the target's tab showing that it was
asked, and by whom. That is the audit trail, and it also makes the feature legible: you can watch
your agents talk instead of wondering what they said.

`agent_status`, `review_agent_work` and `read_agent_transcript` are reads and need no entry in the
target's transcript; `ask_agent` produces a real turn there and must be attributed.

## Delegation, the inbox, and the rolling digest

**Superseded 2026-09-10 — `ask_agent` no longer blocks.** The original reasoning below is kept
because it is why `delegate_to_agent` exists, and the distinction it draws turned out to be wrong
in a way worth recording.

> `ask_agent` blocks. For a question that is right; for a job — "build a website" — it holds the
> asking agent's turn gate for minutes, and messages to that agent are dropped while it waits. Two
> separate problems: the asker is locked, and agents cannot wake up to report anything.

The error was treating "question" and "job" as different in kind. They are not: both run one full
turn on the target, through the same `AskAsync`. The only difference was whether the caller awaited
it — and that made the model responsible for predicting, before asking, whether a question would
turn out to be quick. That is the judgement it is worst at, and the cost of getting it wrong was
the user locked out of their own agent with no way to convert or cancel.

**Both now hand off and return at once.** `ask_agent` opens a `Delegation` exactly as
`delegate_to_agent` does, marked `DelegationKind.Question`, and the kind changes only the WORDING of
the result — "Ninja replied" rather than "Ninja finished", and a larger slice of the reply on the
card, because for a question the reply is the deliverable rather than a note about work that lives
in the files. The asking agent's turn ends immediately in both cases.

Two consequences worth stating plainly:

- **The busy check moved earlier.** While asking blocked, the target's atomic claim decided and the
  tool only worded the refusal. With nobody waiting to hear that, a busy target must be caught
  before the hand-off — otherwise the model is told its question is on its way and learns otherwise
  from a failure card much later. The claim inside `AskAsync` is still the authority; a race between
  the two now completes the question as unanswered rather than returning a refusal.
- **The loop guard now crosses a thread boundary.** `AgentCallChain` is an `AsyncLocal`, and the
  chain reaches the target's turn only because `Task.Run` captures `ExecutionContext`. If that ever
  stopped holding, the guard would fail silently — so it is pinned by a test that asserts the chain
  is visible on the far side of the hand-off, rather than left to inspection.

**Each agent has an inbox** (`AgentInbox`). Anything that happens while an agent is idle waits
there and is folded into the preamble of its next turn — the same ride-along `_armedContexts`
already uses for imported snapshots. Agents are turn-based, so this is the only moment an idle
agent can take delivery of anything.

**Progress is a rolling digest, not an event log.** This is the load-bearing decision. A delegation
posts under ONE inbox id for its whole life, so each report REPLACES the last: a job that runs for
ten minutes costs the same context as one that runs for ten seconds. An append-only feed would grow
with the other agent's work and be paid for on every subsequent turn — the transcript-copying
problem arriving in instalments.

**The digest is assembled, never written by a model.** Every field comes from state the host already
keeps: the turn gate, plan progress, `AgentCommandLog`, the directory entry. So keeping it current
costs string formatting rather than a turn, it is always accurate, and it cannot invent progress. A
model-written précis would cost a call per update and could report a job as nearly done because it
read that way.

**Notification and knowing are separate, and each is cheap.** A finished job appends a card to the
*delegating* agent's transcript — that is the "tell me when it's done", and it costs no model turn
at all. The agent itself learns from its inbox on its next turn. Neither requires waking anything.

Rejected along the way:

- **A publish/subscribe broker.** It answers "who gets the message", but the real blocker is that an
  idle agent cannot act on one — so a broker would sit on top of the same two delivery mechanisms
  and leave the original problem intact. The routing is also already known: A asked B.
- **Streaming B's actions to A.** The progressive idea was right; the delivery was not. Raw actions
  are a firehose, and "A unsubscribes when it knows enough" cannot work — A only decides while
  running, so the feed would accumulate unbounded until the user next happened to speak to it.
- **Queueing a job for a busy agent.** It would report work as accepted while nothing had started.
  Refusing names the reason and the current state instead.

## Deliberately out of scope

- Cross-agent *delegation* ("Knuckles, go fix the auth module"). Blocked on the approval question
  above, and worth having mentions in hand before deciding it.
- Mentioning a closed agent or a saved session. Snapshots already cover recovering an old
  conversation as context.
- Agents mentioning each other unprompted. Every cross-agent call in this design begins with
  something the user typed.

## Future: reaching Desktop agents from the CLI

Investigated, deliberately not built. Recorded because the findings are the expensive part.

- **`read_agent_transcript` is already cross-process.** It reads `ConversationLog.Load(key)` from
  disk, not from memory, so a separate process could read a Desktop agent's conversation today if it
  knew the key.
- **Discovery is the only missing piece for a read-only bridge.** `RefreshAgentDirectory` already
  runs on every tab-strip refresh; writing that snapshot to a file would give a CLI `@`-completion,
  status, and transcripts with no IPC and no protocol. It needs a PID and a heartbeat, or the CLI
  would confidently list agents belonging to a Desktop that has since exited. The two apps also use
  different roots today (`LocalApplicationData/MandoCode.Desktop` versus `~/.mandocode`), which is an
  agreement rather than an obstacle.
- **Agent Framework does not hand you remoting, but it does not fight it.** The packages in use
  (`Microsoft.Agents.AI` / `.Workflows`) expose no remote, host, proxy or transport types — the
  surface is entirely in-process. What they *do* expose is `AIAgent.DeserializeSessionAsync` with a
  serializable `AgentSession`, so conversation state already has a wire format; and
  `DelegatingAIAgent`, which is exactly the seam for a proxy that forwards `RunCoreAsync` over IPC.
  A remote agent would satisfy `IAgentPeer` and none of the four tools would know the difference.
- **Every hard problem here is product-shaped, not framework-shaped.** Where does an approval dialog
  appear when the CLI makes a Desktop agent write a file? How does a local endpoint prove the caller
  is the user rather than any process on the machine? Where does a completion go when the CLI has
  exited mid-job? And the addressing is asymmetric: Desktop has N named agents, the CLI is one
  unnamed conversation. A transport answers none of these.

## The one boundary this widens

Every agent's file access is bounded to its own `ProjectRootAccessor` by design.
`review_agent_work` deliberately reaches past that so the reviewer can see the target's folder. It
is read-only and scoped to another *open agent's* root, never to arbitrary paths — but it is a real
widening of what an agent can see, and it should be a decision rather than a discovery. If both
agents share a root, nothing is crossed at all.
