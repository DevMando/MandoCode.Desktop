# Desktop services

Services are grouped by feature, while retaining the shared
`MandoCode.Desktop.Services` namespace.

- **Agents**: `AgentCallsigns`, `AgentDirectory`, `AgentDirectoryTools`, `AgentDirectoryTools.Interactions`, `AgentInbox`, `AgentInteractionRunner`, `AgentNaming`, `AgentPeer`, `Delegations`, `PeerMessageEnvelope`, `PeerTurnPolicy`, `SessionAgentPeer`.
- **Ai**: `AiServiceAdapter`, `IAiService`, `ModelOrdering`.
- **Approval**: `ApprovalModels`, `WinUiApprovalService`.
- **Browser**: `AgentImageSink`, `BrowserFrames`, `BrowserRequestContext`, `DesktopPreviewKeys`, `DesktopPreviewScripts`, `DesktopPreviewTools`.
- **Configuration**: `AgentConfigStore`, `ConfigCloning`, `ConfigCoordinator`, `ItemTagStore`, `MandoCliDetector`, `McpCoordinator`, `SettingsScope`, `SkillAuthor`, `SkillCoordinator`.
- **Input**: `CommandSuggestion`, `PromptFileLookup`, `PromptMentionPicker`.
- **Notes**: `NoteAssistant`, `NoteEntry`, `NoteStore`, `NoteText`.
- **Project**: `GitQuickStatus`, `ProjectDisplay`, `ShellOpen`, `WorkspaceDeltaTracker`.
- **Rendering**: `BusyStateService`, `ITranscriptHtml`, `ModelNoticeReplay`, `PlanCardHtml`, `StableCollection`, `TranscriptHtmlBuilder`, `TranscriptWriter`.
- **Sessions**: `AgentSession`, `CardPreview`, `ConversationLog`, `ConversationSearch`, `ConversationTextCache`, `HistorySummarizer`, `SessionArchiveStore`, `SessionHistoryStore`, `SessionManager`, `TranscriptJournal`.
- **Snapshots**: `ContextSnapshot`, `SnapshotEnhancer`, `SnapshotNaming`, `SnapshotStore`.
- **Terminal**: `AgentCommandFormat`, `AgentCommandLog`, `PseudoConsole`, `ShellRunner`, `TerminalSession`.
- **Appearance**: `BuiltInBackgrounds`, `ColorMath`, `ContextMeterColors`, `PinnedOpacityConverter`, `ThemeManager`, `UiTheme`.
- **Workspaces**: `PaneLayout`, `PanelState`, `WorkspaceState`.
- **Music**: `MusicPlaylists`.
- **Diagnostics**: `CrashLog`, `UiUpdateCheckService`.

When moving a source file, update direct compile links in the Desktop test and
browser smoke-test projects, along with current documentation paths.
