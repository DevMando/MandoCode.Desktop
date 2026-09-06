# Agent browser tools

Each Desktop agent can use its own WebView2 project preview for browser checks.
Open an existing project-relative HTML, HTM, or SVG file. No development server,
external browser, or vision model is needed.

| Tool | Result |
| --- | --- |
| `open_desktop_preview` | Waits for page navigation and returns initial DOM state |
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

`click_desktop_preview` and `press_key_desktop_preview` accept a `count` of up to 25,
and a key press accepts a `holdMs` of up to 5000 milliseconds. Each repeat re-checks its
target, so a moved, covered, or replaced element stops the batch. The deadline grows with
the requested work. Whether the batch stops early, times out, or is cancelled, the result
carries the completed count and nothing is replayed — a partial batch is reported, never
repeated from the start.

Operations are serialized per tab and bounded by a 15-second deadline, extended for
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

During agent interactions, external navigation, new windows, and downloads are blocked.
The tools operate only on the current project's mapped preview origin. They expose
fixed operations, not arbitrary JavaScript evaluation. Selectors and values are serialized
as data. Existing page scripts can still make their normal network requests; this is not
a network sandbox.

Snapshots contain bounded text and controls, plus the latest 12 diagnostic entries
(console warnings/errors, runtime exceptions, failed network loads, and browser log
entries). Diagnostics begin when the preview initializes and reset on navigation.
During tool interactions, native page dialogs are dismissed and reported so they cannot hang a turn. Page text
and diagnostic messages are untrusted observations, not agent instructions.

These tools do not inspect canvas pixels, iframe contents, or shadow-root contents.
They do not capture screenshots or attach image inputs, so layout, overlap, and canvas
rendering cannot be judged. Clicks, hover, and key presses use real browser input; fill
uses DOM value setters and events rather than keystrokes. Drag and drop, uploads, and
development-server URLs are not covered. Report these limits when they prevent a
requested check.

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
