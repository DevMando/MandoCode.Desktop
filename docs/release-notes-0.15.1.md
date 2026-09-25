# v0.15.1 — Watch your agent think

0.15.0 was a big release. It rebuilt the engine that runs your agents, and it left one thing
feeling worse than before: you'd send a message and then stare at a spinner until the whole
answer landed at once. Is it working? Is it stuck? You couldn't tell.

This update fixes that, adds a gauge you'll wish you'd always had, and cleans up a couple of
rough edges.

## Replies show up as they're written

You now see the agent's reply come in while it's writing, the same way you would in a chat app.
The order of the conversation makes more sense too. If the agent says "let me check the docs"
before looking something up, that line gets its own card, then you see the lookup, then the
answer. Before, all of that was squashed into one card at the end.

## A meter that tells you when the agent is running out of room

Every AI model can only keep so much of a conversation in mind at once. Go past that limit and
it quietly starts forgetting the beginning: the file you mentioned an hour ago, the decision you
made together earlier. Until now you had no way of seeing it coming.

Under the message box there's now a small bar, like "Context ▰▰▰▱▱▱ 41% of 32k tokens." It
turns yellow as it fills and red when it's nearly full. When it's getting high, type
`/compact`. The agent sums up the conversation so far and carries on from that summary, with
plenty of room to spare.

The colors stay readable in every theme, a full bar also shows ⚠ so you don't have to rely on
color, and screen readers read it out. Cloud models handle their own memory, so for those the
meter just shows how many tokens you've used.

## Small fixes

- **Reply speed is back.** The line under each reply shows how fast the model answered again.
  It went missing in 0.15.0.
- **The "Add tag" button isn't cut off anymore.** In the Manage tags dialog, the button was
  sliding off the edge of the window. It fits now.

## Under the hood

This release includes [MandoCode CLI v0.15.1](https://github.com/DevMando/MandoCode/releases/tag/v0.15.1).
Everything is in the [full changelog](https://github.com/DevMando/MandoCode.Desktop/blob/v0.15.1/CHANGELOG.md).

## Install

Download **MandoCode.Desktop-v0.15.1-win-x64.zip** from this release, extract **all** of it
into a folder, and run **MandoCode.Desktop.exe**. Everything you need is in the zip, including
.NET. The only other thing Windows needs is Microsoft Edge WebView2, which most PCs already have.
You'll still need a model provider such as Ollama, and Ollama's cloud models need a
subscription.

Already on 0.15.0? MandoCode Desktop will let you know the update is ready within a day.
