# v0.15.0 — A new AI foundation and a more connected workspace

MandoCode Desktop now runs on **Microsoft Agent Framework (MAF)**, replacing Semantic Kernel
in the engine. The migration brings conversations, tools, and approved plans onto a shared
agent and workflow foundation. Desktop's notes assistant, snapshot summaries, and skill author
also move off Semantic Kernel. Your existing models, providers, skills, MCP servers, and
approval controls continue to work.

## Plans you can steer and resume

Review and edit steps before execution, resume unfinished plans after restarting, and choose
whether to retry, skip, cancel, or approve a revised approach when work fails. Completed steps
stay settled. Use `/plan <goal>` when you want a plan explicitly; automatic planning now looks
at the shape of the task rather than just the length of your message. Steps carry acceptance
criteria and plans include final testing, replacing the separate model verifier for every step.

## A built-in browser—for you and your agents

Browse websites directly inside MandoCode Desktop. Open tabs, enter URLs, and navigate with
back, forward, and reload controls. Ask the agent to explain the page you are viewing or help
fill out a form, including forms embedded in another page. Your request stays tied to that tab,
even if you switch tabs while the agent works.

## Watch commands run and preview the results

- **Live command output:** each agent gets a read-only output tab showing commands, working
  folders, output, and completion status. A terminal indicator points you to unseen activity.
  This displays captured command output; it does not introduce a persistent agent shell.
- **File and app previews:** inspect files beside the conversation, edit text with save
  protection, read PDFs, and preview local development sites. Vision-capable models can receive
  screenshots; text-only models are told when they cannot inspect an image.
## Let your agents work together

Mention another open agent with `@` to ask questions or hand off work. You can keep talking
with your agent while the other works, ask for progress, and receive the result when it finishes.
Named split panes and drag-and-drop help you arrange their conversations.

## A workspace that remembers your preferences

Save settings independently for each agent while keeping global defaults for new ones. Pin
frequently used models, organize Skills and MCP servers with tags and filtered bulk actions,
and compact a long conversation with `/compact`. Completed work folds into expandable activity
summaries. Nine new themes, themed backgrounds, and the Midnight Ramen default background join
improvements to text and status-card readability.

## Fixes you will notice

- Deleting notes, snapshots, or history keeps the rest of the list in place.
- Restored conversations retain their model choices without repeated context-window notices.
- Changing an agent's project folder keeps its conversation.
- Exiting split view brings the agent tabs back.
- Routine cloud model switches no longer repeat subscription notices; failures still explain
  when a subscription is required.
- Token totals use provider-reported usage, and plans with skipped or failed steps report
  partial completion instead of an unqualified success.

See the [full changelog](https://github.com/DevMando/MandoCode.Desktop/blob/v0.15.0/CHANGELOG.md)
for the complete list. This release pins the engine to
[MandoCode CLI v0.15.0](https://github.com/DevMando/MandoCode/releases/tag/v0.15.0),
commit `e67578251c5716a6ede192a2e3e82e7dfda7c8f0`.

## Install

Download **MandoCode.Desktop-v0.15.0-win-x64.zip** from this release, extract **all** its contents
into a folder, and run **MandoCode.Desktop.exe**. The Windows x64 package includes .NET;
Microsoft Edge WebView2 Runtime is the only separate system runtime dependency. Model access
still requires a configured provider, such as Ollama; Ollama cloud models require an active
cloud subscription.
