  const log = document.getElementById('log');
  if (window.hljs) hljs.configure({ ignoreUnescapedHTML: true });

  // --- append pipeline: group consecutive op cards, stamp timestamps ---
  function groupSummary(d) {
    const n = d.querySelectorAll(':scope > .op').length;
    d.querySelector('summary').textContent = '⚙ ' + n + ' operation' + (n === 1 ? '' : 's');
  }
  function placeChild(c) {
    if (c.nodeType !== 1) { log.appendChild(c); return; }
    if (c.classList.contains('op')) {
      const prev = log.lastElementChild;
      if (prev && prev.tagName === 'DETAILS' && prev.classList.contains('op-group') && prev.hasAttribute('open')) {
        prev.appendChild(c);
        groupSummary(prev);
        return;
      }
      if (prev && prev.classList.contains('op')) {
        const d = document.createElement('details');
        d.className = 'op-group';
        d.setAttribute('open', '');
        d.appendChild(document.createElement('summary'));
        log.insertBefore(d, prev);
        d.appendChild(prev);
        d.appendChild(c);
        groupSummary(d);
        return;
      }
      log.appendChild(c);
      return;
    }
    const last = log.lastElementChild;
    if (last && last.tagName === 'DETAILS' && last.classList.contains('op-group'))
      last.removeAttribute('open');   // run over — collapse the group
    if (c.classList.contains('assistant') || c.classList.contains('user-echo'))
      c.title = new Date().toLocaleTimeString();
    log.appendChild(c);
  }

  // --- syntax highlighting + copy chips, applied to new nodes only ---
  function highlightNew() {
    if (!window.hljs) return;
    log.querySelectorAll('.md pre code:not([data-hl])').forEach(function (c) {
      c.setAttribute('data-hl', '1');
      try { hljs.highlightElement(c); } catch (err) { }
    });
  }
  function doCopy(text, chip) {
    // In the app, the host writes the clipboard (copy: message). In an EXPORTED transcript
    // there is no webview bridge, so fall back to the browser clipboard API — file:// pages
    // are a secure context in Chromium/Firefox, and this runs on a user gesture.
    if (window.chrome && window.chrome.webview)
      window.chrome.webview.postMessage('copy:' + text);
    else if (navigator.clipboard)
      navigator.clipboard.writeText(text).catch(function () { });
    chip.classList.add('copied');
    setTimeout(function () { chip.classList.remove('copied'); }, 1400);
  }
  function addCopyChips() {
    log.querySelectorAll('.md pre:not([data-copy])').forEach(function (pre) {
      pre.setAttribute('data-copy', '1');
      const chip = document.createElement('button');
      chip.className = 'copy-chip';
      pre.appendChild(chip);      // click is handled by the delegated .copy-chip handler
    });
    log.querySelectorAll('.assistant:not([data-copy])').forEach(function (card) {
      card.setAttribute('data-copy', '1');
      if (!card.querySelector('.md')) return;
      const chip = document.createElement('button');
      chip.className = 'copy-chip';
      card.appendChild(chip);
    });
  }
  // Delegated so copy still works in exported transcripts (see the toggle handlers below).
  document.addEventListener('click', function (e) {
    const chip = e.target.closest('.copy-chip');
    if (!chip) return;
    e.stopPropagation();
    const pre = chip.closest('pre');
    let text = '';
    if (pre) {
      const code = pre.querySelector('code');
      text = code ? code.innerText : pre.innerText;
    } else {
      const card = chip.closest('.assistant');
      const md = card && card.querySelector('.md');
      if (md) text = md.innerText;
    }
    doCopy(text, chip);
  });

  // --- emoji reactions on assistant responses: hover ghost → picker card → pills ---
  // Toggling posts react:/unreact: with a JSON payload; the snippet lets the preamble
  // on the user's next turn say WHICH response was reacted to.
  const RX_QUICK = ['👍', '👎', '❤️', '🔥', '🎉', '🤔', '😂'];
  const RX_MORE = ['😀', '😄', '😊', '😉', '😍', '🥰', '😎', '🤓', '🙃', '😅', '😬', '😭',
    '🥳', '🤯', '😴', '🙄', '😤', '😱', '🫠', '🤗', '🫡', '👌', '🙏', '👏', '💪', '🤝',
    '✌️', '🤞', '👀', '🧠', '💯', '✨', '🚀', '🎯', '💡', '⚡', '⭐', '💔', '✅', '❌',
    '⚠️', '❓', '❗', '💬', '🐛', '🔧', '🔒', '🔑', '📝', '📌', '📁', '🖥️', '☕', '🍕',
    '🎮', '🤖'];
  let rxSeq = 0;
  let rxCard = null;   // the card the open picker targets

  const rxPop = document.createElement('div');
  rxPop.id = 'rx-pop';
  document.body.appendChild(rxPop);

  function rxSnippet(card) {
    const md = card.querySelector('.md');
    return (md ? md.innerText : '').trim().replace(/\s+/g, ' ').slice(0, 80);
  }
  function rxPillFor(card, emoji) {
    const tray = card.querySelector('.rx-tray');
    if (!tray) return null;
    return Array.prototype.find.call(tray.children, function (p) { return p.textContent === emoji; });
  }
  // Toggle a reaction on a card: pill tray + picker highlight + postMessage, all in one place.
  function rxToggle(card, emoji) {
    const existing = rxPillFor(card, emoji);
    if (existing) {
      existing.remove();
      const tray = card.querySelector('.rx-tray');
      if (tray && !tray.children.length) tray.remove();
      window.chrome.webview.postMessage('unreact:' +
        JSON.stringify({ id: card.dataset.rxId, emoji: emoji, snippet: '' }));
    } else {
      let tray = card.querySelector('.rx-tray');
      if (!tray) {
        tray = document.createElement('div');
        tray.className = 'rx-tray';
        card.appendChild(tray);
      }
      const pill = document.createElement('button');
      pill.className = 'rx-pill';
      pill.textContent = emoji;
      pill.title = 'Click to remove reaction';
      pill.addEventListener('click', function (ev) {
        ev.stopPropagation();
        rxToggle(card, emoji);
      });
      tray.appendChild(pill);
      window.chrome.webview.postMessage('react:' +
        JSON.stringify({ id: card.dataset.rxId, emoji: emoji, snippet: rxSnippet(card) }));
    }
    // Picking (or un-picking) from the open picker dismisses it — one-shot action,
    // like Teams/Slack. Multiple reactions = reopen; chosen ones show highlighted.
    if (rxPop.style.display === 'block' && rxCard === card) closeRxPop();
  }
  function rxChip(parent, emoji) {
    const b = document.createElement('button');
    b.className = 'rx' + (rxCard && rxPillFor(rxCard, emoji) ? ' on' : '');
    b.textContent = emoji;
    b.addEventListener('click', function (ev) {
      ev.stopPropagation();
      rxToggle(rxCard, emoji);
    });
    parent.appendChild(b);
  }
  function openRxPop(card, anchor) {
    rxCard = card;
    rxPop.innerHTML = '';
    const quick = document.createElement('div');
    quick.className = 'rx-quick';
    RX_QUICK.forEach(function (e) { rxChip(quick, e); });
    const moreBtn = document.createElement('button');
    moreBtn.className = 'rx-more-btn';
    moreBtn.textContent = 'More ▾';
    quick.appendChild(moreBtn);
    rxPop.appendChild(quick);
    const grid = document.createElement('div');
    grid.className = 'rx-grid';
    RX_MORE.forEach(function (e) { rxChip(grid, e); });
    rxPop.appendChild(grid);
    moreBtn.addEventListener('click', function (ev) {
      ev.stopPropagation();
      const opening = grid.style.display !== 'flex';
      grid.style.display = opening ? 'flex' : 'none';
      moreBtn.textContent = opening ? 'Less ▴' : 'More ▾';
    });
    // Anchor under the ghost button, right-aligned; flip above when near the viewport bottom.
    rxPop.style.display = 'block';
    const r = anchor.getBoundingClientRect();
    const pw = rxPop.offsetWidth, ph = rxPop.offsetHeight;
    const left = Math.max(8, Math.min(r.right - pw, window.innerWidth - pw - 8)) + window.scrollX;
    let top = r.bottom + 6 + window.scrollY;
    if (r.bottom + ph + 12 > window.innerHeight) top = Math.max(window.scrollY + 8, r.top - ph - 6 + window.scrollY);
    rxPop.style.left = left + 'px';
    rxPop.style.top = top + 'px';
  }
  function closeRxPop() { rxPop.style.display = 'none'; rxCard = null; }
  document.addEventListener('click', function (e) {
    if (rxPop.style.display === 'block' && !rxPop.contains(e.target)) closeRxPop();
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') closeRxPop();
  });

  function addReactionGhosts() {
    log.querySelectorAll('.assistant:not([data-rx])').forEach(function (card) {
      card.setAttribute('data-rx', '1');
      card.dataset.rxId = String(++rxSeq);
      const ghost = document.createElement('button');
      ghost.className = 'react-ghost';
      ghost.textContent = '🙂+';
      ghost.title = 'React to this response';
      ghost.addEventListener('click', function (ev) {
        ev.stopPropagation();
        if (rxPop.style.display === 'block' && rxCard === card) { closeRxPop(); return; }
        openRxPop(card, ghost);
      });
      card.appendChild(ghost);
    });
  }

  // --- collapse long diff/output panels to a preview; corner buttons maximize/minimize ---
  // Only panel-hosted <pre> blocks (diffs, command output, folder-delete listings) taller than
  // ~22% of the window get collapsed. A matching Expand/Collapse button is placed in the top-RIGHT
  // and bottom-RIGHT corners so it's reachable from the top or — after expanding down — the bottom.
  function setCollapsed(pre, collapsed) {
    pre.classList.toggle('collapsed', collapsed);
    const panel = pre.closest('.panel');
    if (!panel) return;
    const fade = panel.querySelector('.collapse-fade');
    if (fade) fade.style.display = collapsed ? 'block' : 'none';
    panel.querySelectorAll('.expand-btn').forEach(function (b) {
      b.textContent = collapsed ? '⤢ Expand' : '⤡ Collapse';
    });
  }
  function addCollapsers() {
    log.querySelectorAll('pre.diff:not([data-collapse]), pre.cmd-out:not([data-collapse])').forEach(function (pre) {
      pre.setAttribute('data-collapse', '1');
      const panel = pre.closest('.panel');
      if (!panel) return;                                               // only panel-hosted blocks
      if (pre.scrollHeight <= window.innerHeight * 0.22 + 40) return;   // short enough already
      pre.classList.add('collapsible');
      panel.classList.add('collapsible-panel');
      const fade = document.createElement('div');
      fade.className = 'collapse-fade';
      pre.appendChild(fade);
      ['right', 'right bottom'].forEach(function (side) {
        const b = document.createElement('button');
        b.className = 'expand-btn ' + side;
        b.title = 'Maximize / minimize this block';
        panel.appendChild(b);   // click is handled by the delegated .expand-btn handler
      });
      setCollapsed(pre, true);                                          // start minimized
    });
  }

  // --- clamp long user prompts: a big pasted prompt (log, file contents) otherwise
  // dominates the scrollback, so echoes taller than ~9 lines clamp to ~8 with a toggle ---
  function addEchoClamps() {
    log.querySelectorAll('.user-echo:not([data-clamp])').forEach(function (echo) {
      echo.setAttribute('data-clamp', '1');
      const lh = parseFloat(getComputedStyle(echo).lineHeight) || 20;
      if (echo.scrollHeight <= lh * 9 + 6) return;   // short enough — no chrome
      // Count hidden lines while the echo is still unclamped; ~8 lines stay visible.
      const hidden = Math.max(1, Math.round(echo.scrollHeight / lh) - 8);
      echo.classList.add('clamped');
      const btn = document.createElement('button');
      btn.className = 'ue-toggle';
      // The expanded label lives in a data attribute (not a closure) so it survives
      // outerHTML serialization when the transcript is exported.
      btn.dataset.more = 'Show more (' + hidden + ' more line' + (hidden === 1 ? '' : 's') + ')';
      btn.textContent = btn.dataset.more;
      echo.after(btn);
    });
  }
  // Toggles are DELEGATED document handlers, not per-button listeners: exporting the
  // transcript serializes outerHTML, which keeps the buttons but drops bound listeners.
  // These handlers re-register when the exported page runs this script on load, so
  // clamped prompts and collapsed panels stay expandable in the saved file.
  document.addEventListener('click', function (e) {
    const btn = e.target.closest('.ue-toggle');
    if (!btn) return;
    const echo = btn.previousElementSibling;
    if (!echo || !echo.classList.contains('user-echo')) return;
    const clamped = echo.classList.toggle('clamped');
    btn.textContent = clamped ? btn.dataset.more : 'Show less';
  });
  document.addEventListener('click', function (e) {
    const b = e.target.closest('.expand-btn:not(.web-collapse)');
    if (!b) return;
    const panel = b.closest('.panel');
    const pre = panel && panel.querySelector('pre.collapsible');
    if (pre) setCollapsed(pre, !pre.classList.contains('collapsed'));
  });

  window.__append = function (html) {
    const nearBottom = (window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 60);
    const wrap = document.createElement('div');
    wrap.innerHTML = html;
    while (wrap.firstChild) placeChild(wrap.firstChild);
    highlightNew();
    addCopyChips();
    addReactionGhosts();
    addCollapsers();
    addEchoClamps();
    if (nearBottom) window.scrollTo(0, document.body.scrollHeight);
    updatePill();
  };
  window.__clear = function () { log.innerHTML = ''; updatePill(); };

  document.addEventListener('click', function (e) {
    const link = e.target.closest('a[data-file]');
    if (!link) return;
    e.preventDefault();
    window.chrome.webview.postMessage('open-file:' + link.getAttribute('data-file'));
  });

  // Interactive diff-card chips (delegated — survives transcript export, like the toggles).
  // Clear just deletes the card from the DOM; Undo asks the host, which confirms before
  // discarding anything. In an exported page Undo is a harmless no-op (no webview bridge).
  document.addEventListener('click', function (e) {
    const clear = e.target.closest('.dv-clear');
    if (clear) {
      const panel = clear.closest('.panel');
      if (panel) panel.remove();
      return;
    }
    const undo = e.target.closest('.dv-undo');
    if (undo && window.chrome && window.chrome.webview)
      window.chrome.webview.postMessage('undo-file:' + undo.getAttribute('data-file'));
  });

  // --- drag hand-off: Chromium owns drags over the transcript surface, so XAML never sees
  // them. On dragenter we alert the host, which mounts its drop overlay over this WebView;
  // the OS then retargets the drag (and the drop, with real file paths) to that overlay.
  // preventDefault on dragover/drop is the safety net for a drop that lands in the instant
  // before the overlay mounts — without it the browser would navigate to the dropped file.
  window.addEventListener('dragenter', function (e) {
    e.preventDefault();
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage('drag-enter');
  });
  window.addEventListener('dragover', function (e) { e.preventDefault(); });
  window.addEventListener('drop', function (e) { e.preventDefault(); });

  // Web fetch/search preview toggle: the inline chip opens the hidden detail box; the box's own
  // Collapse button (and the chip again) closes it. Chip label and box visibility stay in sync.
  document.addEventListener('click', function (e) {
    const t = e.target.closest('.web-toggle, .web-collapse');
    if (!t) return;
    e.stopPropagation();
    const op = t.closest('.op');
    if (!op) return;
    const detail = op.querySelector('.web-detail');
    const toggle = op.querySelector('.web-toggle');
    if (!detail || !toggle) return;
    const open = t.classList.contains('web-collapse') ? false : detail.hasAttribute('hidden');
    detail.toggleAttribute('hidden', !open);
    toggle.textContent = open ? '⤡ Collapse' : '⤢ Expand';
  });

  // --- jump-to-bottom pill ---
  const pill = document.createElement('div');
  pill.id = 'jump-pill';
  pill.textContent = '↓ Latest';
  document.body.appendChild(pill);
  pill.addEventListener('click', function () {
    window.scrollTo({ top: document.body.scrollHeight,
      behavior: document.documentElement.hasAttribute('data-flat') ? 'auto' : 'smooth' });
  });
  function updatePill() {
    const nb = (window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 80);
    pill.style.display = nb ? 'none' : 'block';
  }
  window.addEventListener('scroll', updatePill);

  // --- in-chat find (Ctrl+F) ---
  let findBar = null, findHits = [], findIdx = -1;
  function ensureFindBar() {
    if (findBar) return;
    findBar = document.createElement('div');
    findBar.id = 'findbar';
    findBar.innerHTML = '<input type="text" placeholder="Find in chat"/><span class="find-count"></span>' +
      '<button data-act="prev">▲</button><button data-act="next">▼</button><button data-act="close">✕</button>';
    document.body.appendChild(findBar);
    const input = findBar.querySelector('input');
    let deb = null;
    input.addEventListener('input', function () {
      clearTimeout(deb);
      deb = setTimeout(function () { runFind(input.value); }, 150);
    });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); stepFind(e.shiftKey ? -1 : 1); }
    });
    findBar.addEventListener('click', function (e) {
      const b = e.target.closest('button');
      if (!b) return;
      if (b.dataset.act === 'prev') stepFind(-1);
      else if (b.dataset.act === 'next') stepFind(1);
      else closeFind();
    });
  }
  function setCount() {
    if (!findBar) return;
    findBar.querySelector('.find-count').textContent = findHits.length ? (findIdx + 1) + '/' + findHits.length : '';
  }
  function clearFind() {
    findHits.forEach(function (m) {
      const p = m.parentNode;
      if (!p) return;
      p.replaceChild(document.createTextNode(m.textContent), m);
      p.normalize();
    });
    findHits = [];
    findIdx = -1;
    setCount();
  }
  function runFind(q) {
    clearFind();
    if (!q || q.length < 2) return;
    const needle = q.toLowerCase();
    const walker = document.createTreeWalker(log, NodeFilter.SHOW_TEXT, null);
    const nodes = [];
    let n;
    while ((n = walker.nextNode())) {
      if (n.textContent.toLowerCase().includes(needle)) nodes.push(n);
    }
    nodes.forEach(function (node) {
      let text = node, idx;
      while ((idx = text.textContent.toLowerCase().indexOf(needle)) >= 0) {
        const hit = text.splitText(idx);
        const rest = hit.splitText(q.length);
        const m = document.createElement('mark');
        m.className = 'find-hit';
        hit.parentNode.replaceChild(m, hit);
        m.appendChild(hit);
        findHits.push(m);
        text = rest;
      }
    });
    if (findHits.length) { findIdx = 0; focusHit(); }
    setCount();
  }
  function stepFind(dir) {
    if (!findHits.length) return;
    findHits[findIdx].classList.remove('find-current');
    findIdx = (findIdx + dir + findHits.length) % findHits.length;
    focusHit();
    setCount();
  }
  function focusHit() {
    const m = findHits[findIdx];
    m.classList.add('find-current');
    m.scrollIntoView({ block: 'center' });
  }
  function closeFind() {
    clearFind();
    if (findBar) findBar.style.display = 'none';
  }
  document.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && (e.key === 'f' || e.key === 'F')) {
      e.preventDefault();
      ensureFindBar();
      findBar.style.display = 'flex';
      const input = findBar.querySelector('input');
      input.focus();
      input.select();
    }
    else if (e.key === 'Escape' && findBar && findBar.style.display !== 'none') {
      closeFind();
    }
  });
