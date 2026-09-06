namespace MaddoxTasks.Web;

internal static class WebAssets
{
    // Keeping the app in source makes the UI available from a single-file and
    // Native AOT publish without relying on the process working directory.
    public const string IndexHtml = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <meta name="theme-color" content="#151922">
  <title>MaddoxTasks</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #11141b;
      --panel: #1a1f2a;
      --panel-raised: #222938;
      --panel-soft: #171b24;
      --text: #edf1f7;
      --muted: #9aa6b8;
      --line: #30394a;
      --accent: #f3a44f;
      --accent-strong: #ffc271;
      --danger: #f17e82;
      --success: #77d09c;
      --focus: #8dc8ff;
      --radius: 12px;
    }
    * { box-sizing: border-box; }
    html, body { min-height: 100%; }
    body {
      margin: 0;
      background: radial-gradient(circle at top right, #202a3b 0, var(--bg) 42rem);
      color: var(--text);
      font: 16px/1.45 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    button, input, select, textarea { font: inherit; }
    button, select, input[type="checkbox"] { cursor: pointer; }
    button, select, input, textarea { min-height: 44px; }
    button {
      border: 1px solid var(--line);
      border-radius: 9px;
      background: var(--panel-raised);
      color: var(--text);
      padding: .55rem .85rem;
      transition: border-color .15s ease, background .15s ease, transform .05s ease;
    }
    button:hover, button:focus-visible { border-color: var(--accent); background: #2a3344; }
    button:active { transform: translateY(1px); }
    button.primary { border-color: #d68735; background: #b96728; color: #fff8ef; }
    button.primary:hover, button.primary:focus-visible { background: #d17d31; }
    button.danger { color: #ffd4d4; border-color: #774249; }
    button.subtle { color: var(--muted); background: transparent; }
    button.icon { min-width: 44px; padding-inline: .55rem; }
    :focus-visible { outline: 3px solid var(--focus); outline-offset: 2px; }
    input, select, textarea {
      width: 100%;
      border: 1px solid var(--line);
      border-radius: 9px;
      background: #111720;
      color: var(--text);
      padding: .6rem .7rem;
    }
    textarea { resize: vertical; min-height: 100px; }
    label { display: grid; gap: .35rem; color: var(--muted); font-size: .9rem; }
    h1, h2, h3, p { margin-top: 0; }
    h1 { margin-bottom: 0; font-size: clamp(1.2rem, 3vw, 1.65rem); letter-spacing: .01em; }
    h2 { font-size: 1.25rem; }
    h3 { margin-bottom: .25rem; font-size: 1rem; }
    .app-shell { max-width: 1500px; margin: 0 auto; padding: 1rem; }
    .topbar {
      display: flex; align-items: center; justify-content: space-between; gap: .8rem;
      margin-bottom: 1rem;
    }
    .brand { display: flex; align-items: center; gap: .7rem; }
    .brand-mark { width: 13px; height: 13px; border-radius: 50%; background: var(--accent); box-shadow: 0 0 0 5px #3e2d1f; }
    .top-actions, .button-row, .filter-actions { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; }
    .button-row input { flex: 1 1 12rem; min-width: 0; }
    .toolbar {
      display: grid; grid-template-columns: minmax(170px, 2fr) repeat(2, minmax(130px, 1fr)) auto;
      gap: .65rem; align-items: end; margin-bottom: 1rem;
      padding: .75rem; border: 1px solid var(--line); border-radius: var(--radius); background: #161b24cc;
    }
    .toolbar .check { display: flex; align-items: center; gap: .45rem; min-height: 44px; white-space: nowrap; }
    .toolbar .check input { width: 20px; height: 20px; min-height: 20px; }
    .board {
      display: flex; gap: .65rem;
      align-items: flex-start; overflow-x: auto; padding-bottom: .35rem;
    }
    .column { flex: 1 1 180px; min-width: 180px; min-height: 8rem; border: 1px solid var(--line); border-radius: var(--radius); background: #161b24b8; }
    .column.empty-column { flex: 0 0 110px; min-width: 110px; }
    .column-header { display: flex; justify-content: space-between; align-items: center; gap: .5rem; padding: .65rem .7rem; border-bottom: 1px solid var(--line); }
    .column-header strong { font-size: .9rem; }
    .count { color: var(--muted); font-size: .8rem; }
    .column-items { display: grid; gap: .5rem; padding: .5rem; }
    .empty { padding: .8rem .55rem; color: var(--muted); font-size: .86rem; }
    .issue-card {
      display: grid; gap: .3rem; width: 100%; text-align: left; min-height: 96px;
      padding: .65rem; border: 1px solid var(--line); border-radius: 10px; background: var(--panel);
    }
    .issue-card.selected { border-color: var(--accent); box-shadow: inset 3px 0 var(--accent); }
    .card-top, .card-meta, .detail-heading, .modal-header { display: flex; align-items: center; justify-content: space-between; gap: .5rem; }
    .card-id, .card-meta, .muted { color: var(--muted); font-size: .82rem; }
    .card-title { font-weight: 650; overflow-wrap: anywhere; }
    .priority { color: var(--accent-strong); font-weight: 700; }
    .priority.p1 { color: #ff9690; }
    .priority.p2 { color: #ffc578; }
    .labels { display: flex; flex-wrap: wrap; gap: .35rem; }
    .tag { display: inline-flex; align-items: center; max-width: 100%; border: 1px solid #46536a; border-radius: 999px; padding: .14rem .45rem; color: #c4d6ec; background: #202b3d; font-size: .78rem; overflow-wrap: anywhere; }
    .tag.repository-tag { border-color: #9a6a35; color: #ffe0b3; background: #3b2b1c; }
    .tag button { min-width: 44px; min-height: 44px; margin: -.45rem -.35rem -.45rem .05rem; border: 0; padding: 0 .15rem; background: transparent; color: inherit; }
    .status-chip { display: inline-flex; border-radius: 999px; padding: .14rem .45rem; font-size: .75rem; font-weight: 650; }
    .status-active { color: #98edb6; background: #214b36; }
    .status-next { color: #a2d9ff; background: #214259; }
    .status-blocked { color: #ffc4a5; background: #5a302b; }
    .status-readyforreview { color: #ffe29e; background: #55421f; }
    .status-backlog { color: #ced4df; background: #343b49; }
    .status-done { color: #b7c1ce; background: #303641; }
    .status-rejected { color: #f3adb6; background: #4a2933; }
    .footer-help { margin-top: .85rem; color: var(--muted); font-size: .84rem; }
    .footer-help kbd { border: 1px solid var(--line); border-radius: 4px; padding: .1rem .3rem; color: var(--text); background: #202631; }
    .hidden { display: none !important; }
    .overlay { position: fixed; inset: 0; z-index: 10; display: grid; place-items: center; padding: 1rem; background: #080a0fcc; }
    .panel { width: min(700px, 100%); max-height: min(92vh, 900px); overflow: auto; border: 1px solid var(--line); border-radius: 15px; background: var(--panel); box-shadow: 0 20px 70px #000a; }
    .panel.wide { width: min(920px, 100%); }
    .modal-header { position: sticky; top: 0; padding: 1rem; border-bottom: 1px solid var(--line); background: var(--panel); z-index: 1; }
    .modal-body { display: grid; gap: 1rem; padding: 1rem; }
    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: .75rem; }
    .form-grid .full { grid-column: 1 / -1; }
    .detail-title { overflow-wrap: anywhere; }
    .detail-description { white-space: pre-wrap; overflow-wrap: anywhere; padding: .7rem; border-radius: 8px; background: var(--panel-soft); color: #dbe3ee; min-height: 2.5rem; }
    .section { display: grid; gap: .55rem; }
    .section-heading { color: var(--muted); font-size: .85rem; text-transform: uppercase; letter-spacing: .08em; }
    .history-item, .comment-item { padding: .55rem .65rem; border-left: 3px solid #46536a; background: var(--panel-soft); border-radius: 0 8px 8px 0; overflow-wrap: anywhere; }
    .lock-item { display: grid; gap: .4rem; padding: .7rem; border: 1px solid var(--line); border-radius: 9px; background: var(--panel-soft); }
    .history-item .meta, .comment-item .meta { color: var(--muted); font-size: .78rem; margin-bottom: .25rem; }
    .alert { padding: .7rem; border: 1px solid #75424b; border-radius: 8px; color: #ffd2d1; background: #351e25; }
    #toast { position: fixed; right: 1rem; bottom: max(1rem, env(safe-area-inset-bottom)); z-index: 30; max-width: min(420px, calc(100vw - 2rem)); padding: .75rem 1rem; border: 1px solid var(--line); border-radius: 9px; background: #202a38; box-shadow: 0 8px 30px #0008; }
    #toast.error { border-color: #874752; color: #ffd3d3; }
    #toast.success { border-color: #3f805b; color: #caffdc; }
    @media (max-width: 1100px) {
      .board { display: grid; grid-template-columns: repeat(4, minmax(210px, 1fr)); }
      .column { min-width: 0; }
      .column.empty-column { flex: 1 1 auto; min-width: 0; }
    }
    @media (max-width: 700px) {
      .app-shell { padding: .65rem; }
      .topbar { align-items: flex-start; }
      .top-actions button { min-width: 44px; }
      .toolbar { grid-template-columns: 1fr 1fr; }
      .toolbar label:first-child { grid-column: 1 / -1; }
      .toolbar .filter-actions { grid-column: 1 / -1; }
      .board { grid-template-columns: 1fr; overflow-x: visible; }
      .column { min-width: 0; min-height: 0; }
      .column-items { grid-template-columns: 1fr; }
      .form-grid { grid-template-columns: 1fr; }
      .form-grid .full { grid-column: auto; }
      .modal-body { padding: .8rem; }
    }
  </style>
</head>
<body>
  <main class="app-shell">
    <header class="topbar">
      <div class="brand"><span class="brand-mark" aria-hidden="true"></span><h1>MaddoxTasks</h1></div>
      <div class="top-actions">
        <button id="locks-button" type="button">Repository locks</button>
        <button id="refresh-button" class="icon" type="button" title="Refresh (r)" aria-label="Refresh">↻</button>
        <button id="help-button" class="icon" type="button" title="Keyboard shortcuts (?)" aria-label="Keyboard shortcuts">?</button>
        <button id="new-button" class="primary" type="button">New issue</button>
      </div>
    </header>
    <section class="toolbar" aria-label="Issue filters">
      <label>Search <input id="search" type="search" placeholder="Title, description, or label" autocomplete="off"></label>
      <label>Status <select id="status-filter"><option value="">All statuses</option></select></label>
      <label>Priority at most <select id="priority-filter"><option value="">Any priority</option><option value="1">1 - urgent</option><option value="2">2 - high</option><option value="3">3 - normal</option><option value="4">4 - low</option><option value="5">5 - someday</option></select></label>
      <div class="filter-actions"><button id="clear-filters" class="subtle" type="button">Clear</button><label class="check"><input id="include-done" type="checkbox"> Include done/rejected</label></div>
    </section>
    <div id="load-error" class="alert hidden" role="alert"></div>
    <section id="board" class="board" aria-live="polite"></section>
    <p class="footer-help"><kbd>↑</kbd>/<kbd>↓</kbd> or <kbd>j</kbd>/<kbd>k</kbd> navigate · <kbd>Enter</kbd> open · <kbd>n</kbd> new · <kbd>s</kbd> status · <kbd>p</kbd> priority · <kbd>d</kbd> done/description · <kbd>c</kbd> comment · <kbd>/</kbd> search · <kbd>?</kbd> help</p>
  </main>

  <div id="create-overlay" class="overlay hidden" role="dialog" aria-modal="true" aria-labelledby="create-heading">
    <form id="create-form" class="panel">
      <div class="modal-header"><h2 id="create-heading">New issue</h2><button class="icon subtle" type="button" data-close="create-overlay" aria-label="Close">×</button></div>
      <div class="modal-body">
        <div id="create-error" class="alert hidden" role="alert"></div>
        <details id="ai-entry">
          <summary>Fill with AI</summary>
          <label>Describe your task <textarea id="ai-prompt" rows="5" maxlength="16000" placeholder="What needs doing? Include any repository, priority, due date, or parent task."></textarea></label>
          <div class="button-row"><button id="generate-draft" type="button">Fill task details</button><span id="ai-progress" role="status"></span></div>
          <p class="muted">Review the filled fields below, then create the issue.</p>
        </details>
        <div class="form-grid">
          <label class="full">Title <input id="create-title" name="title" required maxlength="500" autofocus></label>
          <label class="full">Description <textarea id="create-description" name="description" rows="4"></textarea></label>
          <label>Initial status <select id="create-status" name="status"><option>Next</option><option>Backlog</option></select></label>
          <label>Priority <select id="create-priority" name="priority"><option value="1">1 - urgent</option><option value="2">2 - high</option><option value="3" selected>3 - normal</option><option value="4">4 - low</option><option value="5">5 - someday</option></select></label>
          <label>Parent issue token <input id="create-parent" name="parent" placeholder="Optional sequence or id"></label>
          <label>Due date <input id="create-due" name="due" type="date"></label>
          <label class="full">Labels (one per line; use repo:&lt;name&gt; for repositories) <textarea id="create-labels" rows="2"></textarea></label>
        </div>
        <div class="button-row"><button class="primary" type="submit">Create issue</button><button type="button" data-close="create-overlay">Cancel</button></div>
      </div>
    </form>
  </div>

  <div id="detail-overlay" class="overlay hidden" role="dialog" aria-modal="true" aria-labelledby="detail-heading">
    <section class="panel wide">
      <div class="modal-header"><h2 id="detail-heading" class="detail-title">Issue</h2><button class="icon subtle" type="button" data-close="detail-overlay" aria-label="Close">×</button></div>
      <div id="detail-body" class="modal-body"></div>
    </section>
  </div>

  <div id="help-overlay" class="overlay hidden" role="dialog" aria-modal="true" aria-labelledby="help-heading">
    <section class="panel">
      <div class="modal-header"><h2 id="help-heading">Keyboard shortcuts</h2><button class="icon subtle" type="button" data-close="help-overlay" aria-label="Close">×</button></div>
      <div class="modal-body">
        <p><kbd>↑</kbd>/<kbd>↓</kbd>, <kbd>j</kbd>/<kbd>k</kbd> move through issues. <kbd>Enter</kbd> opens the selected issue.</p>
        <p><kbd>n</kbd> creates an issue. In an issue, <kbd>s</kbd> focuses status, <kbd>p</kbd> priority, <kbd>t</kbd> labels, <kbd>d</kbd> description, and <kbd>c</kbd> comment.</p>
        <p><kbd>d</kbd> on the board marks the selected issue Done. <kbd>/</kbd> focuses search, <kbd>r</kbd> refreshes, <kbd>Esc</kbd> closes a panel, and <kbd>?</kbd> opens this help.</p>
        <button type="button" data-close="help-overlay">Close</button>
      </div>
    </section>
  </div>
  <div id="locks-overlay" class="overlay hidden" role="dialog" aria-modal="true" aria-labelledby="locks-heading">
    <section class="panel">
      <div class="modal-header"><h2 id="locks-heading">Repository locks</h2><button class="icon subtle" type="button" data-close="locks-overlay" aria-label="Close">×</button></div>
      <div id="locks-body" class="modal-body"><div class="muted">Loading repository locks...</div></div>
    </section>
  </div>
  <div id="toast" class="hidden" role="status" aria-live="polite"></div>

  <script>
  (() => {
    'use strict';
    const statuses = ['Active', 'Next', 'Blocked', 'ReadyForReview', 'Backlog', 'Done', 'Rejected'];
    const state = { issues: [], selected: -1, selectedId: null, detail: null, toastTimer: null, refreshTimer: null };
    const byId = id => document.getElementById(id);
    const board = byId('board');

    function escapeHtml(value) {
      return String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
    }
    function statusLabel(status) { return status === 'ReadyForReview' ? 'Ready for Review' : status; }
    function statusClass(status) { return 'status-' + String(status).toLowerCase(); }
    function compareIssuePriority(left, right) { return left.priority - right.priority || left.sequence - right.sequence; }
    function compareBoardOrder(left, right) { return statuses.indexOf(left.status) - statuses.indexOf(right.status) || compareIssuePriority(left, right); }
    function issueById(id) { return state.issues.find(issue => issue.id === id); }
    function repositoryName(label) { return String(label).toLowerCase().startsWith('repo:') ? String(label).slice(5) : null; }
    function visibleIssueLabels(labels) {
      const allLabels = labels || [];
      return allLabels.filter(label => repositoryName(label) !== null).concat(allLabels.filter(label => repositoryName(label) === null)).slice(0, 4);
    }
    function labelTag(label, removable = false) {
      const repository = repositoryName(label);
      const text = repository === null ? label : `Repository: ${repository}`;
      const removeButton = removable ? ` <button type="button" class="remove-label" data-label="${escapeHtml(label)}" aria-label="Remove ${escapeHtml(label)}">×</button>` : '';
      return `<span class="tag${repository === null ? '' : ' repository-tag'}">${escapeHtml(text)}${removeButton}</span>`;
    }
    function isTyping(target) { return target && (target.matches('input, textarea, select, [contenteditable="true"]') || target.isContentEditable); }
    function showToast(message, kind = 'success') {
      const toast = byId('toast');
      toast.textContent = message;
      toast.className = kind === 'error' ? 'error' : 'success';
      clearTimeout(state.toastTimer);
      state.toastTimer = setTimeout(() => toast.className = 'hidden', 4500);
    }
    function showError(message) {
      const element = byId('load-error');
      element.textContent = message;
      element.classList.toggle('hidden', !message);
    }
    async function api(path, options = {}) {
      const response = await fetch(path, { headers: { 'Accept': 'application/json', ...(options.body ? { 'Content-Type': 'application/json' } : {}) }, ...options });
      let payload;
      try { payload = await response.json(); } catch { payload = { success: false, error: 'The server returned an invalid response.' }; }
      if (!response.ok || payload.success === false) throw new Error(payload.error || payload.message || `Request failed (${response.status})`);
      return payload;
    }
    function queryString() {
      const params = new URLSearchParams();
      const search = byId('search').value.trim();
      const status = byId('status-filter').value;
      const priority = byId('priority-filter').value;
      if (search) params.set('search', search);
      if (status) params.set('status', status);
      if (priority) params.set('maxPriority', priority);
      if (byId('include-done').checked || status === 'Done' || status === 'Rejected') params.set('includeDone', 'true');
      return params.toString();
    }
    function captureDetailDraft() {
      if (!state.detail || byId('detail-overlay').classList.contains('hidden')) return null;
      const description = byId('edit-description');
      const label = byId('new-label');
      const comment = byId('new-comment');
      if (!description || !label || !comment) return null;
      const active = document.activeElement;
      return {
        issueId: state.detail.id,
        dirtyFields: {
          description: description.value !== (state.detail.description || ''),
          label: label.value !== '',
          comment: comment.value !== ''
        },
        values: { 'edit-description': description.value, 'new-label': label.value, 'new-comment': comment.value },
        activeId: active && active.id,
        selectionStart: active && 'selectionStart' in active ? active.selectionStart : null,
        selectionEnd: active && 'selectionEnd' in active ? active.selectionEnd : null,
        panelScrollTop: byId('detail-body').closest('.panel').scrollTop,
        documentScrollY: window.scrollY
      };
    }
    function restoreDetailDraft(draft) {
      if (!draft || !state.detail || draft.issueId !== state.detail.id) return;
      const fieldIds = { description: 'edit-description', label: 'new-label', comment: 'new-comment' };
      Object.entries(draft.dirtyFields).forEach(([field, dirty]) => {
        const id = fieldIds[field];
        const element = dirty && byId(id);
        if (element) element.value = draft.values[id];
      });
      const active = draft.activeId && byId(draft.activeId);
      if (active) {
        active.focus({ preventScroll: true });
        if (draft.selectionStart !== null && 'setSelectionRange' in active) {
          const end = Math.min(draft.selectionEnd, active.value.length);
          active.setSelectionRange(Math.min(draft.selectionStart, end), end);
        }
      }
      byId('detail-body').closest('.panel').scrollTop = draft.panelScrollTop;
      window.scrollTo({ top: draft.documentScrollY });
    }
    async function refresh(silent = false, preserveDetailDraft = true) {
      const draft = preserveDetailDraft ? captureDetailDraft() : null;
      try {
        const payload = await api('/api/issues' + (queryString() ? '?' + queryString() : ''));
        state.issues = (payload.issues || []).sort(compareBoardOrder);
        if (!state.issues.length) {
          state.selected = -1;
          state.selectedId = null;
        } else {
          const selectedIssueIndex = state.selectedId ? state.issues.findIndex(issue => issue.id === state.selectedId) : -1;
          state.selected = selectedIssueIndex >= 0 ? selectedIssueIndex : Math.min(Math.max(state.selected, 0), state.issues.length - 1);
          state.selectedId = state.issues[state.selected].id;
        }
        renderBoard();
        showError('');
        if (state.detail) await loadDetail(state.detail.id, true, draft);
      } catch (error) {
        showError(error.message);
        if (!silent) showToast(error.message, 'error');
      }
    }
    function renderBoard() {
      board.textContent = '';
      const selectedStatus = byId('status-filter').value;
      const showTerminal = byId('include-done').checked || selectedStatus === 'Done' || selectedStatus === 'Rejected';
      const boardStatuses = statuses.filter(status => showTerminal || (status !== 'Done' && status !== 'Rejected'));
      boardStatuses.forEach(status => {
        const issues = state.issues.filter(issue => issue.status === status).sort(compareIssuePriority);
        const column = document.createElement('section');
        column.className = 'column' + (issues.length === 0 ? ' empty-column' : '');
        column.innerHTML = `<div class="column-header"><strong>${escapeHtml(statusLabel(status))}</strong><span class="count">${issues.length}</span></div><div class="column-items"></div>`;
        const items = column.querySelector('.column-items');
        if (issues.length === 0) items.innerHTML = '<div class="empty">No issues</div>';
        issues.forEach(issue => items.appendChild(issueCard(issue)));
        board.appendChild(column);
      });
    }
    function issueCard(issue) {
      const card = document.createElement('button');
      card.type = 'button';
      card.className = 'issue-card' + (state.selectedId === issue.id ? ' selected' : '');
      card.dataset.id = issue.id;
      card.setAttribute('aria-label', `${issue.shortId} ${issue.title}, ${statusLabel(issue.status)}`);
      const tags = visibleIssueLabels(issue.labels).map(label => labelTag(label)).join('');
      const due = issue.dueDate ? `Due ${escapeHtml(new Date(issue.dueDate).toLocaleDateString())}` : '';
      card.innerHTML = `<div class="card-top"><span class="card-id">${escapeHtml(issue.shortId)} · ${escapeHtml(issue.id.slice(0, 8))}</span><span class="priority p${issue.priority}">P${issue.priority}</span></div><div class="card-title">${escapeHtml(issue.title)}</div><div class="card-meta"><span class="status-chip ${statusClass(issue.status)}">${escapeHtml(statusLabel(issue.status))}</span><span>${due}</span></div>${tags ? `<div class="labels">${tags}</div>` : ''}`;
      card.addEventListener('click', () => { selectIssue(state.issues.findIndex(candidate => candidate.id === issue.id)); openDetail(issue.id); });
      card.addEventListener('focus', () => state.selectedId = issue.id);
      return card;
    }
    function selectIssue(index) {
      if (!state.issues.length) return;
      state.selected = (index + state.issues.length) % state.issues.length;
      state.selectedId = state.issues[state.selected].id;
      renderBoard();
      const selectedCard = board.querySelector(`[data-id="${CSS.escape(state.selectedId)}"]`);
      if (selectedCard) selectedCard.focus({ preventScroll: true });
    }
    function closeOverlay(id) { byId(id).classList.add('hidden'); if (id === 'detail-overlay') state.detail = null; if (id === 'create-overlay') cancelDraft(); }
    function openOverlay(id) { byId(id).classList.remove('hidden'); }
    function openCreate() {
      cancelDraft();
      byId('create-form').reset();
      byId('create-error').classList.add('hidden');
      openOverlay('create-overlay');
      setTimeout(() => byId('create-title').focus(), 0);
    }
    async function submitCreate(event) {
      event.preventDefault();
      cancelDraft();
      const body = { title: byId('create-title').value, description: byId('create-description').value, status: byId('create-status').value, priority: Number(byId('create-priority').value) };
      const parent = byId('create-parent').value.trim();
      const due = byId('create-due').value;
      if (parent) body.parent = parent;
      if (due) body.due = due;
      body.labels = byId('create-labels').value.split('\n').map(value => value.trim()).filter(Boolean);
      try {
        const result = await api('/api/issues', { method: 'POST', body: JSON.stringify(body) });
        closeOverlay('create-overlay');
        showToast(result.message || 'Issue created.');
        await refresh(true);
        if (result.issueId) openDetail(result.issueId);
      } catch (error) {
        const box = byId('create-error'); box.textContent = error.message; box.classList.remove('hidden');
      }
    }
    let draftController = null;
    function cancelDraft() {
      if (draftController) draftController.abort();
      draftController = null;
      byId('generate-draft').disabled = false;
      byId('ai-progress').textContent = '';
    }
    async function generateDraft() {
      const prompt = byId('ai-prompt').value.trim();
      if (!prompt) { byId('ai-prompt').focus(); return; }
      cancelDraft();
      const controller = new AbortController();
      draftController = controller;
      byId('generate-draft').disabled = true;
      byId('ai-progress').textContent = 'Filling task details…';
      byId('create-error').classList.add('hidden');
      try {
        const result = await api('/api/issues/draft', { method: 'POST', body: JSON.stringify({ prompt }), signal: controller.signal });
        if (draftController !== controller) return;
        const draft = result.draft;
        for (const field of ['title', 'description', 'status', 'priority']) byId('create-' + field).value = draft[field];
        byId('create-parent').value = draft.parentId || '';
        byId('create-due').value = draft.dueDate || '';
        byId('create-labels').value = draft.labels.join('\n');
        byId('ai-progress').textContent = 'Details filled. Review and create your issue.';
        byId('create-title').focus();
      } catch (error) {
        if (controller.signal.aborted) return;
        const box = byId('create-error'); box.textContent = error.message; box.classList.remove('hidden');
        byId('ai-progress').textContent = '';
      } finally {
        if (draftController === controller) { draftController = null; byId('generate-draft').disabled = false; }
      }
    }
    async function loadDetail(id, silent = false, draft = null) {
      try {
        const payload = await api('/api/issues/' + encodeURIComponent(id));
        if (!state.detail || state.detail.id !== id) return;
        const currentDraft = draft ? captureDetailDraft() || draft : null;
        state.detail = payload.issue;
        renderDetail();
        restoreDetailDraft(currentDraft);
      } catch (error) {
        if (state.detail && state.detail.id === id && !silent) {
          byId('detail-heading').textContent = 'Unable to load issue';
          byId('detail-body').innerHTML = `<div class="alert" role="alert">${escapeHtml(error.message)}</div><div class="button-row"><button type="button" id="close-detail">Close</button></div>`;
          byId('close-detail').onclick = () => closeOverlay('detail-overlay');
        }
        if (!silent) showToast(error.message, 'error');
      }
    }
    async function openDetail(id) {
      state.detail = issueById(id) || { id };
      openOverlay('detail-overlay');
      byId('detail-heading').textContent = state.detail.title || 'Loading issue...';
      await loadDetail(id);
    }
    function renderDetail() {
      const issue = state.detail;
      if (!issue) return;
      byId('detail-heading').textContent = `${issue.shortId} · ${issue.title}`;
      const statusOptions = statuses.map(status => `<option value="${status}"${status === issue.status ? ' selected' : ''}>${escapeHtml(statusLabel(status))}</option>`).join('');
      const labelTags = (issue.labels || []).map(label => labelTag(label, true)).join('');
      const comments = (issue.comments || []).slice().reverse().map(comment => `<article class="comment-item"><div class="meta">${escapeHtml(comment.actor)} · ${escapeHtml(new Date(comment.timestamp).toLocaleString())}</div><div>${escapeHtml(comment.comment)}</div></article>`).join('') || '<div class="muted">No comments yet.</div>';
      const history = (issue.history || []).slice().reverse().map(item => `<article class="history-item"><div class="meta">${escapeHtml(new Date(item.timestamp).toLocaleString())} · ${escapeHtml(item.eventType)}</div><div>${historyText(item)}</div></article>`).join('') || '<div class="muted">No history.</div>';
      const parent = issue.parentId ? ` · parent ${escapeHtml(issue.parentId)}` : '';
      const due = issue.dueDate ? ` · due ${escapeHtml(new Date(issue.dueDate).toLocaleString())}` : '';
      byId('detail-body').innerHTML = `<div class="detail-heading"><span class="status-chip ${statusClass(issue.status)}">${escapeHtml(statusLabel(issue.status))}</span><button type="button" id="close-detail">Close</button></div><div class="section"><h3 class="detail-title">${escapeHtml(issue.title)}</h3><div class="muted">${escapeHtml(issue.id)}${parent}${due} · created ${escapeHtml(new Date(issue.createdAt).toLocaleString())} · updated ${escapeHtml(new Date(issue.updatedAt).toLocaleString())}</div></div><div class="form-grid"><label>Status <select id="edit-status">${statusOptions}</select></label><label>Priority <select id="edit-priority"><option value="1"${issue.priority === 1 ? ' selected' : ''}>1 - urgent</option><option value="2"${issue.priority === 2 ? ' selected' : ''}>2 - high</option><option value="3"${issue.priority === 3 ? ' selected' : ''}>3 - normal</option><option value="4"${issue.priority === 4 ? ' selected' : ''}>4 - low</option><option value="5"${issue.priority === 5 ? ' selected' : ''}>5 - someday</option></select></label></div><div class="section"><div class="section-heading">Description</div><textarea id="edit-description" rows="5">${escapeHtml(issue.description)}</textarea><div class="button-row"><button type="button" id="save-description" class="primary">Save description</button></div></div><div class="section"><div class="section-heading">Labels</div><div class="labels">${labelTags || '<span class="muted">No labels.</span>'}</div><div class="button-row"><input id="new-label" placeholder="Add label" aria-label="New label" aria-describedby="repository-label-help"><button type="button" id="add-label">Add label</button></div><div id="repository-label-help" class="muted">Use repo:&lt;name&gt; to identify and reserve a related repository.</div></div><div class="section"><div class="section-heading">Comments</div><textarea id="new-comment" rows="3" placeholder="Add a comment"></textarea><div class="button-row"><button type="button" id="add-comment" class="primary">Add comment</button></div><div class="section">${comments}</div></div><div class="section"><div class="section-heading">History</div><div class="section">${history}</div></div>`;
      byId('close-detail').onclick = () => closeOverlay('detail-overlay');
      byId('edit-status').onchange = event => mutateStatus(event.target.value);
      byId('edit-priority').onchange = event => mutatePriority(Number(event.target.value));
      byId('save-description').onclick = () => mutateDescription(byId('edit-description').value);
      byId('add-label').onclick = addLabel;
      byId('new-label').addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); addLabel(); } });
      byId('add-comment').onclick = addComment;
      byId('detail-body').querySelectorAll('.remove-label').forEach(button => button.onclick = () => removeLabel(button.dataset.label));
    }
    function historyText(item) {
      if (item.status) return 'Status → ' + statusLabel(item.status);
      if (item.priority) return 'Priority → ' + item.priority;
      if (item.label) return item.eventType === 'LabelAdded' ? 'Added label ' + item.label : 'Removed label ' + item.label;
      if (item.comment) return item.comment;
      if (item.description !== undefined) return 'Updated description';
      return item.eventType;
    }
    async function mutateStatus(status) {
      try { await api(`/api/issues/${encodeURIComponent(state.detail.id)}/status`, { method: 'PATCH', body: JSON.stringify({ status }) }); showToast('Status updated.'); await refresh(true); }
      catch (error) { showToast(error.message, 'error'); }
    }
    async function mutatePriority(priority) {
      try { await api(`/api/issues/${encodeURIComponent(state.detail.id)}/priority`, { method: 'PATCH', body: JSON.stringify({ priority }) }); showToast('Priority updated.'); await refresh(true); }
      catch (error) { showToast(error.message, 'error'); }
    }
    async function mutateDescription(description) {
      const issueId = state.detail.id;
      try { await api(`/api/issues/${encodeURIComponent(issueId)}/description`, { method: 'PATCH', body: JSON.stringify({ description }) }); if (state.detail && state.detail.id === issueId) state.detail.description = description; showToast('Description updated.'); await refresh(true); }
      catch (error) { showToast(error.message, 'error'); }
    }
    async function addLabel() {
      const input = byId('new-label'); const label = input.value.trim(); if (!label) return;
      try { await api(`/api/issues/${encodeURIComponent(state.detail.id)}/labels`, { method: 'POST', body: JSON.stringify({ label }) }); if (input.value.trim() === label) input.value = ''; showToast('Label added.'); await refresh(true); }
      catch (error) { showToast(error.message, 'error'); }
    }
    async function removeLabel(label) {
      try { await api(`/api/issues/${encodeURIComponent(state.detail.id)}/labels/${encodeURIComponent(label)}`, { method: 'DELETE' }); showToast('Label removed.'); await refresh(true); }
      catch (error) { showToast(error.message, 'error'); }
    }
    async function addComment() {
      const input = byId('new-comment'); const comment = input.value.trim(); if (!comment) return;
      try { await api(`/api/issues/${encodeURIComponent(state.detail.id)}/comments`, { method: 'POST', body: JSON.stringify({ comment }) }); if (input.value.trim() === comment) input.value = ''; showToast('Comment added.'); await refresh(true); }
      catch (error) { showToast(error.message, 'error'); }
    }
    async function openRepositoryLocks() {
      openOverlay('locks-overlay');
      byId('locks-body').innerHTML = '<div class="muted">Loading repository locks...</div>';
      try {
        const payload = await api('/api/repository-locks');
        const locks = payload.locks || [];
        byId('locks-body').innerHTML = locks.length ? locks.map(lock => `<article class="lock-item"><div>${labelTag('repo:' + lock.repository)} <span class="priority p${lock.priority}">P${lock.priority}</span></div><strong>${escapeHtml(lock.shortId)} · ${escapeHtml(lock.title)}</strong><span class="status-chip ${statusClass(lock.status)}">${escapeHtml(statusLabel(lock.status))}</span></article>`).join('') : '<div class="muted">No repositories are currently locked.</div>';
      } catch (error) {
        byId('locks-body').innerHTML = `<div class="alert" role="alert">${escapeHtml(error.message)}</div>`;
      }
    }
    function focusDetail(id) { const element = byId(id); if (element) element.focus(); }
    async function handleKey(event) {
      if (event.key === 'Escape') { ['create-overlay', 'detail-overlay', 'help-overlay', 'locks-overlay'].forEach(closeOverlay); return; }
      if (isTyping(event.target)) return;
      if (event.key === '?') { openOverlay('help-overlay'); return; }
      if (event.key === '/') { event.preventDefault(); byId('search').focus(); return; }
      const key = event.key.toLowerCase();
      if (key === 'r') { await refresh(); return; }
      if (key === 'n') { openCreate(); return; }
      if (state.detail && !byId('detail-overlay').classList.contains('hidden')) {
        if (key === 's') { focusDetail('edit-status'); return; }
        if (key === 'p') { focusDetail('edit-priority'); return; }
        if (key === 't') { focusDetail('new-label'); return; }
        if (key === 'd') { focusDetail('edit-description'); return; }
        if (key === 'c') { focusDetail('new-comment'); return; }
        return;
      }
      if (key === 'arrowdown' || key === 'j') { event.preventDefault(); selectIssue(state.selected + 1); return; }
      if (key === 'arrowup' || key === 'k') { event.preventDefault(); selectIssue(state.selected - 1); return; }
      if (key === 'enter' && state.selected >= 0) { event.preventDefault(); await openDetail(state.issues[state.selected].id); return; }
      if (key === 'd' && state.selected >= 0) { state.detail = state.issues[state.selected]; await mutateStatus(state.detail.status === 'Done' ? 'Next' : 'Done'); return; }
      if (key === 'c' && state.selected >= 0) { await openDetail(state.issues[state.selected].id); setTimeout(() => focusDetail('new-comment'), 0); }
      if (key === 's') {
        if (state.selected >= 0) { await openDetail(state.issues[state.selected].id); setTimeout(() => focusDetail('edit-status'), 0); }
        else byId('status-filter').focus();
        return;
      }
      if (key === 'p') {
        if (state.selected >= 0) { await openDetail(state.issues[state.selected].id); setTimeout(() => focusDetail('edit-priority'), 0); }
        else byId('priority-filter').focus();
        return;
      }
      if (key === 't') {
        if (state.selected >= 0) { await openDetail(state.issues[state.selected].id); setTimeout(() => focusDetail('new-label'), 0); }
        else byId('search').focus();
      }
    }
    statuses.forEach(status => byId('status-filter').insertAdjacentHTML('beforeend', `<option value="${status}">${escapeHtml(statusLabel(status))}</option>`));
    byId('new-button').onclick = openCreate;
    byId('locks-button').onclick = openRepositoryLocks;
    byId('refresh-button').onclick = () => refresh();
    byId('help-button').onclick = () => openOverlay('help-overlay');
    byId('create-form').addEventListener('submit', submitCreate);
    byId('generate-draft').addEventListener('click', generateDraft);
    ['search', 'status-filter', 'priority-filter', 'include-done'].forEach(id => byId(id).addEventListener('input', () => refresh(true)));
    byId('clear-filters').onclick = () => { byId('search').value = ''; byId('status-filter').value = ''; byId('priority-filter').value = ''; byId('include-done').checked = false; refresh(); };
    document.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', () => closeOverlay(button.dataset.close)));
    document.querySelectorAll('.overlay').forEach(overlay => overlay.addEventListener('click', event => { if (event.target === overlay) closeOverlay(overlay.id); }));
    document.addEventListener('keydown', event => { handleKey(event); });
    state.refreshTimer = setInterval(() => refresh(true), 10000);
    refresh();
  })();
  </script>
</body>
</html>
""";
}
