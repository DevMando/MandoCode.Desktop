// Terminal front-end: hosts one xterm.js instance per shell tab inside a single
// WebView2, and bridges keystrokes/output/resize to the C# TerminalPanel. Output
// bytes arrive base64-encoded (raw VT from ConPTY) and are handed to xterm untouched.
//
// Design note (see the tool-call-pill history in project memory): nothing here may
// repaint indefinitely. cursorBlink is OFF, so xterm only repaints on actual output.
(function () {
  "use strict";

  const TerminalCtor = window.Terminal;
  const FitAddonNS = window.FitAddon;
  const host = document.getElementById("host");
  const terms = Object.create(null); // id -> { term, fit, el }

  function post(obj) {
    try { window.chrome.webview.postMessage(JSON.stringify(obj)); } catch (e) { }
  }

  function makeTheme(t) {
    return t || {
      background: "#0b0b12",
      foreground: "#e6e6ef",
      cursor: "#f2c14e",
      cursorAccent: "#0b0b12",
      selectionBackground: "rgba(242,193,78,0.30)",
      black: "#1e1e28", red: "#ff6b6b", green: "#7ee787", yellow: "#f2c14e",
      blue: "#79c0ff", magenta: "#d2a8ff", cyan: "#56d4dd", white: "#d0d0e0",
      brightBlack: "#6a6a80", brightRed: "#ffa198", brightGreen: "#aff5b4",
      brightYellow: "#ffd479", brightBlue: "#a5d6ff", brightMagenta: "#e2c5ff",
      brightCyan: "#b3f0f5", brightWhite: "#ffffff"
    };
  }

  function create(id, cols, rows, theme) {
    if (terms[id]) return;

    const el = document.createElement("div");
    el.className = "term";
    el.style.display = "none";
    host.appendChild(el);

    const term = new TerminalCtor({
      cols: cols || 80,
      rows: rows || 24,
      cursorBlink: false,                 // no perpetual repaint loop
      cursorStyle: "bar",
      fontFamily: "Cascadia Mono, Consolas, 'Courier New', monospace",
      fontSize: 13,
      lineHeight: 1.1,
      theme: makeTheme(theme),
      scrollback: 5000,
      allowProposedApi: true
    });

    const fit = new FitAddonNS.FitAddon();
    term.loadAddon(fit);
    term.open(el);

    // Keystrokes / pasted text -> C# -> shell stdin.
    term.onData(d => post({ type: "data", id: id, data: d }));
    term.onBinary(d => post({ type: "data", id: id, data: d }));

    terms[id] = { term: term, fit: fit, el: el };
  }

  function write(id, b64) {
    const t = terms[id];
    if (!t) return;
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    t.term.write(bytes);
  }

  function fit(id) {
    const t = terms[id];
    if (!t || t.el.style.display === "none") return;
    try {
      t.fit.fit();
      post({ type: "resize", id: id, cols: t.term.cols, rows: t.term.rows });
    } catch (e) { }
  }

  function show(id) {
    for (const k in terms) terms[k].el.style.display = (k === id) ? "block" : "none";
    fit(id);
    focus(id);
  }

  function focus(id) {
    const t = terms[id];
    if (t) setTimeout(() => { try { t.term.focus(); } catch (e) { } }, 0);
  }

  function dispose(id) {
    const t = terms[id];
    if (!t) return;
    try { t.term.dispose(); } catch (e) { }
    t.el.remove();
    delete terms[id];
  }

  function exited(id, message) {
    const t = terms[id];
    if (!t) return;
    t.term.write("\r\n\x1b[38;5;244m" + (message || "[process exited]") + "\x1b[0m\r\n");
  }

  function setTheme(theme) {
    for (const k in terms) terms[k].term.options.theme = makeTheme(theme);
  }

  // C# -> JS (host.PostWebMessageAsJson -> parsed object on e.data).
  window.chrome.webview.addEventListener("message", function (e) {
    const m = e.data;
    if (!m || !m.type) return;
    switch (m.type) {
      case "create": create(m.id, m.cols, m.rows, m.theme); break;
      case "write": write(m.id, m.data); break;
      case "show": show(m.id); break;
      case "fit": fit(m.id); break;
      case "focus": focus(m.id); break;
      case "dispose": dispose(m.id); break;
      case "exited": exited(m.id, m.message); break;
      case "theme": setTheme(m.theme); break;
    }
  });

  // Refit the visible terminal when the panel is resized (fires only on change).
  const ro = new ResizeObserver(function () {
    for (const k in terms) if (terms[k].el.style.display !== "none") fit(k);
  });
  ro.observe(host);

  post({ type: "ready" });
})();
