# Agent browser tools

Each Desktop conversation has a shared, tabbed WebView2 browser. The browser button
beside Snapshot opens it independently of the agent. Users can add and close tabs,
enter HTTP(S) URLs, and go back, forward, or reload. Project previews and development
servers use the same pane. DOM checks need no vision model; screenshots do.

Every operation on an existing tab requires an explicit `tabId`. Opening without an
ID creates a new tab; opening with an ID navigates only that tab. Use the ID returned
by an open operation or `list_browser_tabs`. Tab selection never routes agent actions.
When the user sends a message, Desktop captures the viewed tab's identity before
background processing starts. “This page” means that captured tab even after a UI
switch. A closed or unknown target fails; no other tab is substituted. Browser context
also travels with plan instructions for retry and resume. Tabs themselves are session-only:
after restarting Desktop, old tab IDs are unavailable and must be explicitly re-established.

| Tool | Result |
| --- | --- |
| `list_browser_tabs` | Stable tab IDs, titles, URLs, and current selection; always read live |
| `list_browser_frames` | Embedded and nested frame document IDs, parent IDs, URLs and navigation state within an explicit tab |
| `open_browser_tab` | Opens an HTTP(S) URL in a new tab, or navigates an explicit existing tab |
| `open_desktop_preview` | Waits for page navigation and returns initial DOM state |
| `open_local_server_desktop_preview` | Same, for a development server on localhost or 127.0.0.1 |
| `refresh_desktop_preview` | Waits for a cache-bypassing reload and returns new state |
| `inspect_desktop_preview` | Visible text, controls and unique CSS selectors, values, viewport, keyboard focus, and diagnostics; optional selector and control pagination |
| `observe_desktop_preview` | Reads one element only: text, value, checked state, visibility, and selector |
| `click_desktop_preview` | Browser pointer click after visibility, enabled-state, and hit checks; optional repeat count |
| `press_key_desktop_preview` | Browser key press with an optional bounded hold, modifiers, and focus target |
| `hover_desktop_preview` | Browser pointer movement for hover menus and tooltips |
| `fill_desktop_preview` | Replaces a text input/textarea value and emits input/change events |
| `select_desktop_preview` | Chooses an enabled option in a single-select control |
| `scroll_desktop_preview` | Scrolls vertically or brings an element into view |
| `wait_for_desktop_preview` | Waits up to 10 seconds for a visible element and optional text |
| `screenshot_desktop_preview` | Captures the visible viewport, or one element, as image input for a vision model |

Use **open → inspect → act once → observe/wait**. Check the observed outcome against
the intended behavior. A dispatched click is not proof that the feature passed.
Final plan checks already ask the agent to exercise interactive controls; successful
DOM observations count as fresh browser evidence after an edit. Opening or refreshing
alone still does not count as an acceptance check. Live browser calls bypass the
ordinary tool-result cache.

Every action tool takes an optional `observe` selector. With it, the result reads back
that one element instead of a full page snapshot, which is what keeps a sequence of
checks affordable. `observe_desktop_preview` does the same on its own. A selector that
matches nothing reports `matched: false`; that is an observation, not an error.

## Repeats and interruption

### Embedded forms

DOM tools accept an optional `frameId` alongside the required `tabId`. Omit it (or use
`main`) for the top-level document. `inspect`, `observe`, `fill`, `select`, `scroll`, and
`wait` operate inside the selected frame through WebView2's frame API, including
cross-origin frames. Pointer, keyboard, and screenshot tools currently reject child-frame
targets instead of incorrectly acting on the parent document.

Top-level inspections list available frame identities and disclose uninspected frames.
Zero parent controls does not establish that an embedded form is absent. Selecting an
iframe element reports that its fallback text is not its document. Discover the frame,
inspect its fields, fill authorized values, and use observe to read them back. Fill/select
results also include expected and actual values. They do not submit the form, but normal
page input/change handlers still run.

Frame IDs belong to one tab and document lifetime. Navigation, replacement, and removal
invalidate old IDs; tools fail without substituting another document. Loading or failed
frame access is reported as unavailable rather than as an empty form.

`click_desktop_preview` and `press_key_desktop_preview` accept a `count` of up to 25,
and a key press accepts a `holdMs` of up to 5000 milliseconds. Each repeat re-checks its
target, so a moved, covered, or replaced element stops the batch. The deadline grows with
the requested work. Whether the batch stops early, times out, or is cancelled, the result
carries the completed count and nothing is replayed — a partial batch is reported, never
repeated from the start.

Agent operations are serialized per conversation and bounded by a 15-second deadline, extended for
repeats and holds up to 75 seconds. Timeout or cancellation never automatically repeats
an action. An already dispatched operation may have changed the page; inspect before
deciding to retry. Closing the tab detaches the bridge and cancels outstanding work.
Unsaved user edits are preserved.

## Keyboard

`press_key_desktop_preview` sends real browser key events, so pages that only listen for
`keydown` respond to it. It takes one key — a single printable character, or a name such
as `Enter`, `Tab`, `Escape`, `Backspace`, `Delete`, `Space`, `ArrowUp`/`Down`/`Left`/`Right`,
`Home`, `End`, `PageUp`, `PageDown`, `Insert`, `Shift`, `Control`, `Alt`, `Meta`, or `F1`-`F12`.
An unknown name is reported rather than guessed at. Optional `modifiers` accepts
`ctrl`, `shift`, `alt`, and `meta`; a Ctrl/Alt/Meta chord sends the shortcut without
inserting a character.

Every key is released before the call returns, including when the turn is cancelled, so
no key is ever left down between calls and there is no held-key state to reason about.
For sustained input — movement in a game — use `holdMs` rather than separate down and up
calls. An optional `selector` focuses an element first; without one the key goes to
whatever has focus, or to the page. To enter a whole string, `fill_desktop_preview` is
still the right tool.

A modifier is sent as a flag on the key event, which is what shortcut handlers read. The
modifier key itself does not get its own down/up event, so a page that tracks `keydown`
on `Control` separately will not see it.

## Assets

The preview shows the files as they are on disk. Its HTTP cache is disabled and refreshes
ask for a cache-bypassing reload, so an edited script or stylesheet loads on the next
refresh. Project files never need `?v=2` cache-busting query strings to be previewed.
Results report this as `assetCache`; if the browser refuses to disable its cache, that is
reported rather than assumed.

Project preview tabs restrict navigation to their project origin or loopback server.
General browser tabs allow HTTP(S) navigation, including redirects. Agent-triggered
new windows and downloads remain blocked; user-initiated new-window links open another
browser tab. The tools expose
fixed operations, not arbitrary JavaScript evaluation. Selectors and values are serialized
as data. Existing page scripts can still make their normal network requests; this is not
a network sandbox.

Snapshots contain bounded text and controls, plus the latest 12 diagnostic entries
(console warnings/errors, runtime exceptions, failed network loads, and browser log
entries). Diagnostics begin when the preview initializes and reset on navigation.
During tool interactions, native page dialogs are dismissed and reported so they cannot hang a turn. Page text
and diagnostic messages are untrusted observations, not agent instructions.

DOM inspection does not reach canvas pixels or shadow-root contents;
a screenshot is the way to judge those, and only with a vision-capable model. Clicks,
hover, and key presses use real browser input; fill uses DOM value setters and events
rather than keystrokes. Drag and drop and file uploads are not covered. Report these
limits when they prevent a requested check.

## Screenshots

`screenshot_desktop_preview` captures the visible preview viewport, or one element when
given a selector, and hands the image to the model as real image input. Use it only for
what the DOM cannot answer: layout, overlapping or clipped elements, spacing, and canvas
rendering. Text, values, and control state are far cheaper to read with inspect or observe.
The targeted browser tab must be selected and its pane visible for screenshot capture.
Background tabs remain available for DOM operations; screenshot requests never switch
the user's selected tab automatically.

It requires a model that accepts image input. Capability is checked *before* capturing, so
a text-only model is told plainly that visual layout could not be checked rather than being
handed bytes it will drop. The image never enters the model's text context: the tool result
carries only the metadata, and the bytes are delivered as image content.

An image is evidence for the turn that captured it and is retracted afterward, so a
screenshot does not re-upload on every later message. The model's written conclusion is
what persists.

Capture reads the window's rendered surface, so it depends on the app actually having one.
A minimized window has none and the browser never answers at all, so capture is bounded at
six seconds and reports that the window needs restoring rather than consuming the whole
operation deadline. Hidden, transparent, and occluded windows still capture normally.

A capture taken before the page painted returns valid image bytes showing nothing. That
cannot be told apart from a genuinely blank page, so it is flagged as `possiblyBlank`
rather than refused, and the model is told to say the image looks blank instead of
describing detail it cannot see.

## Development servers

`open_local_server_desktop_preview` opens a server already running on this machine, so the
preview can exercise a live app rather than a static file. It does not start a server.

Only `http` or `https` on `localhost`, `127.0.0.1`, or `[::1]` with an explicit port is
accepted. External hosts, LAN addresses, other schemes, and URLs carrying credentials are
refused. Once open, every script call and every navigation is checked against that one
origin, so a page that redirects elsewhere is blocked exactly as it is for project files.

A development server preview has no backing file, so the preview pane is read-only for it
and the end-of-turn file refresh does not apply; refresh explicitly to reload.

## Validation

Ordinary regression suite:

```powershell
dotnet test src/MandoCode.Desktop.Tests/MandoCode.Desktop.Tests.csproj
```

Opt-in Windows integration smoke test (requires .NET 10 and the WebView2 Runtime):

```powershell
dotnet run --project src/MandoCode.Desktop.BrowserSmokeTests/BrowserSmokeTests.csproj
```

The smoke test uses a hidden WinForms WebView2 host, local fixtures, and a separate
temporary browser profile. It exercises the production DOM scripts and real pointer
events; the Desktop build checks the WinUI bridge. It does not drive a live model or
an existing Desktop agent session.
