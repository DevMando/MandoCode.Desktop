# MainWindow

The XAML and startup/shutdown code-behind stay together at this folder's root.
Partial files are grouped by responsibility:

| Folder | Responsibility |
| --- | --- |
| Agents | Agent tabs, split layouts, and workspaces |
| Navigation | Sidebar and page navigation |
| Panels | History, snapshots, notes, and terminal panels |
| Settings | Settings, appearance, skills, MCP, tags, and music |
| Shared | Shared helpers and window-level view models |

These files retain their existing namespaces and class names. MainWindow partial
files still form the same window; this folder organization does not introduce
separate controls or change ownership of state.
