# v0.16.0: Workspaces, images, and model discovery

Keep separate projects organized, bring screenshots into a conversation, and find your next model without leaving MandoCode Desktop.

## Give each project its own workspace

A new workspace row sits above your agent tabs. Put your Website agents in one workspace and your Backend agents in another. Each workspace remembers its selected agent and split layout, while background agents keep working when you switch away.

Existing agents move into **Main**. Workspaces reopen on launch, and badges show where agents are working or waiting for approval. Create, rename, and close workspaces directly from their tabs. **Split** and **New agent** apply to the workspace you are viewing.

## Paste screenshots and attach multiple images

Paste a screenshot or copied image file, choose **Attach image**, or drop image files into chat. Review numbered previews and remove individual images before sending.

You can include up to four PNG, JPEG, GIF, or WebP images per message, each up to 4 MiB and 16 million pixels. The selected model must have confirmed vision support. If a model cannot accept an attachment, Desktop keeps your draft so you can select a compatible model.

Try attaching two screenshots and asking your agent what changed between them.

## Discover and pull Ollama models inside Desktop

Type `/ollama-pull` or choose **Browse models…** from the model dropdown. Search the library, sort by Newest, Popular, or Name, and compare tags and sizes. Badges identify which exact variants are already available on your agent's Ollama server.

Selecting a model chooses its default tag. **Cloud only** filters both families and their variants, while still letting you choose between multiple cloud tags. Changing the sort or cloud filter clears the previous selection.

Pull with progress and cancellation, or enter an exact tag if the public catalog is unavailable. Downloads do not switch your model automatically. Choose **Use in this agent** when you are ready. Installed badges show availability, not whether a newer revision exists; **Pull again** refreshes a model.

## Clearer mentions and more agent collaboration

Typing `@` opens Files first. Press Tab to switch to Agents. Agent results include workspace and project information, and file lookup uses background indexing to stay responsive on large projects.

Agents can ask for immediate or background answers, send information, request a review, and hand off work. Expandable transcript entries identify the agents and their messages. Background jobs can be updated or cancelled individually.

## Other improvements

- Structured plan mode starts through `/plan`, keeping ordinary requests on the direct workflow.
- Unsupported decision-model chat requests and retired Ollama models receive clearer guidance.
- Incoming peer turns can be stopped from the receiving agent's tab.
- Desktop source files are grouped by feature to make contribution easier.

## Included engine

This release bundles [MandoCode CLI v0.16.2](https://github.com/DevMando/MandoCode/releases/tag/v0.16.2), pinned at `1087b8a`. It includes the shared 0.16 engine improvements and the CLI startup/focus rendering fix. Desktop continues to use its native WinUI conversation interface.

See the [full Desktop changelog](https://github.com/DevMando/MandoCode.Desktop/blob/v0.16.0/CHANGELOG.md) for details.

## Install

Download **MandoCode.Desktop-v0.16.0-win-x64.zip**, extract **all** its contents into a folder, and run **MandoCode.Desktop.exe**. The package includes .NET. Microsoft Edge WebView2 is also required and is already present on most Windows PCs.

You still need Ollama configured for your chosen models. Cloud models use your Ollama account and its applicable access requirements.

Existing installations will show the update through the built-in update checker.
