# MandoCode Desktop (WinUI 3)

The MandoCode AI coding agent with a native Windows desktop interface. Same engine
as the [MandoCode CLI](https://github.com/DevMando/MandoCode) — literally: the CLI
repo is pinned here as a **git submodule** (`/MandoCode`) and this app project-references
`MandoCode/src/MandoCode/MandoCode.csproj`, reusing the entire harness (AIService,
task planner, plugins, MCP, skills, config, approvals, token tracking). Only the
user interface is different: WinUI 3 instead of RazorConsole.

## Clone & build

```
git clone --recursive https://github.com/DevMando/MandoCode.Desktop.git
cd MandoCode.Desktop
dotnet build src/MandoCode.Desktop/MandoCode.Desktop.csproj
dotnet run --project src/MandoCode.Desktop
```

Already cloned without `--recursive`? The `MandoCode/` folder will be empty — run
`git submodule update --init` and build again.

Built exe: `src\MandoCode.Desktop\bin\Debug\net8.0-windows10.0.19041.0\win-x64\MandoCode.Desktop.exe`
- `MandoCode.Desktop.exe <folder>` — open with that folder as the project root
  (otherwise the current directory; changeable in-app via the folder button).

Requires the WebView2 runtime (preinstalled on Windows 11) and a reachable
Ollama (`ollama serve`). Uses the same config file as the CLI, so both apps
share endpoint/model/settings.

## The harness submodule

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

## Architecture

| Layer | CLI (MandoCode) | Desktop (this app) |
|---|---|---|
| Orchestrator | `Components/App.razor` interactive loop | `ViewModels/ChatController.cs` (faithful port) |
| Approvals | `DiffApprovalHandler` (Spectre panels) | `Services/WinUiApprovalService.cs` + XAML overlay (same labels, bypass state, `DiffApprovalResult` contract) |
| Transcript | ANSI scrollback + Spectre renderables | WebView2 + `TranscriptHtmlBuilder` (Markdig HTML, dark theme) |
| Busy/spinner | `SpinnerService` (ANSI) | `BusyStateService` → ProgressRing |
| Onboarding | `OnboardingFlow` terminal prompts | `/setup` wizard + Settings page |
| Everything else | `Services/`, `Plugins/`, `Models/` | **reused verbatim via project reference** |

Key seams the harness already provided (unchanged): `AIService.ChatStreamAsync`,
`OnWrite/Delete/CommandApprovalRequested` delegates, `PlanHandoff.OnPlanRequested`,
`McpApprovalGate.OnApprovalRequested`, `TaskPlannerService.ExecutePlanAsync`
progress events, `DiffService` diff models.

## Releasing

Push a tag (`git tag v0.2.0 && git push --tags`) and the Release workflow
publishes a self-contained win-x64 zip to GitHub Releases. The app's built-in
update checker watches those releases — older installs show an update notice
within 24 hours.

## What's in v0.1

- Chat with streaming turns, function-call operation cards, markdown transcript
- Write / command / delete / MCP approvals with native diff viewer (same
  approve / don't-ask-again / deny / new-instructions / cancel-plan semantics)
- propose_plan flow: plan table, execute/reject/cancel, per-step progress bar,
  step-failure skip/cancel
- Slash commands with autocomplete: /help /clear /model /config /retry /learn
  /copy /copy-code /skills /force-skill /mcp /mcp tools /mcp remove /mcp-reload
  /music* /command /exit — plus `!cmd` shell escape and `@file` references
- Token tracking in the status bar + per-response summaries
- Sidebar navigation: Chat, Settings, and MCP pages
  - Settings — the whole config as a native form (toggles, sliders, number boxes,
    grouped Connection/Generation/Behavior/Limits/Integrations); every change is
    validated and applied through the shared ConfigKeySetter, same as the CLI
  - MCP — live server list with status/tool counts; add/edit servers in a single
    form modal with a Test button (isolated connection check + tool table preview)
- Guided wizards, built on the approval-overlay select + text primitives:
  - `/setup` — probe/start Ollama, change endpoint, pull a starter model with live
    progress, model picker, cloud-auth check + sign-in walkthrough
  - `/model`, `/force-skill`, `/music-playlist` — pickers
  - 401 auto-recovery — a cloud 401 offers the `ollama signin` walkthrough inline
- Update check against this repo's GitHub Releases (24h throttle, fail-silent)

Not ported (yet): matrix easter eggs, terminal theme service (N/A).

## License

MIT — same as the MandoCode CLI.
