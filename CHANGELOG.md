# Changelog

All notable changes to MandoCode Desktop are documented here.
Versioning is independent of the MandoCode CLI; the pinned harness commit is
recorded by the `MandoCode` submodule.

## [Unreleased]

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
