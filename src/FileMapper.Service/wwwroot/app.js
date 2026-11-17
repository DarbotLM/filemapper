'use strict';

const $ = (id) => document.getElementById(id);
const state = { token: sessionStorage.getItem('filemapper.token') || '', snapshots: [], snapshot: null, view: 'spatial', parent: null, search: '', offset: 0, nodeOffset: 0, selected: null, rank: [], entries: [], request: 0, inspectorRequest: 0, connected: false, polling: false, busy: false };
const PAGE = 50;
state.capabilities = [];
state.role = '';
state.duplicateOffset = 0;
state.breakdownOffset = 0;
const canScan = () => state.capabilities.includes('scan');
const palette = ['#77e2f2', '#e7c98d', '#607daa', '#427e98', '#8190b6', '#497e74', '#8f7972', '#4c647e'];
const views = {
  'source-semantics': ['Source semantics', 'RETAINED SOURCE / NATIVE SEMANTIC EVIDENCE', 'Source, shape and exact evidence', 'Analyze captured bytes, rotate native objects, and follow recorded spans, relationships and time.'],
  'word-library': ['Word library bank', 'PUBLIC / PINNED EDITIONS', 'Explore observed source patterns', 'Search immutable public editions and inspect exact source provenance.'],
  practice: ['Edition practice', 'PUBLIC EDITION / WHOLE-SHAPE PRACTICE', 'Play the observed shapes', 'Freeze a pinned edition and explore native legal placements with source evidence.'],
  sources: ['Source word atlas', 'PINNED REPOSITORIES / SOURCE WORD SHAPES', 'Trace every word to its source', 'Stack captured source layers, compare exact word patterns, and inspect immutable byte positions.'],
  learning: ['Learning arena', 'NATIVE / FROZEN MODELS & WHOLE SHAPES', 'Put your patterns in play', 'Observe real sources, inspect learning, and replay a three-dimensional arena.'],
  overview: ['Overview', 'YOUR FILESYSTEM, IN FOCUS', 'Explore your territory', 'Storage, structure, and source. One connected map.'],
  semantic: ['Semantic model', '9BIT / NATIVE OBJECTS, PATTERNS & REPLAY', 'Read the shape of your evidence', 'Inspect stable identity and word features, compare observed snapshots, and replay grounded challenges.'],
  spatial: ['Spatial atlas', '9BIT / SPATIAL WORKSPACE', 'Your files. Another dimension', 'Orbit your objects. Flip the evidence. Follow observed time.'],
  robocopy: ['Robocopy tools', 'NATIVE TRANSFERS / REVIEWABLE PLANS', 'Build your transfer', 'Browse the complete installed option catalog and preview the effects.'],
  explorer: ['File explorer', 'EVERY LOCATION HAS A STORY', 'Follow the hierarchy', 'Browse locations, inspect identity, and trace the supporting evidence.'],
  graph: ['Semantic graph', 'STRUCTURE BECOMES UNDERSTANDING', 'Connect the dots', 'Navigate symbols, calls, dependencies, and their observed relationships.'],
  findings: ['Findings', 'FROM OBSERVATION TO INSIGHT', 'See what needs attention', 'Review evidence-backed conditions with explicit confidence and scope.'],
  duplicates: ['Duplicates', 'SAME CONTENT, DIFFERENT PLACES', 'Understand the overlap', 'Content equality and physical reclaimability are separate measurements.'],
  activity: ['Scan activity', 'A HISTORY OF YOUR TERRITORY', 'Watch your map evolve', 'Snapshots preserve what was observed, when, and how completely.']
};

// The address identifies a view and a recorded snapshot; credentials stay in session storage.
function readRoute() {
  const query = new URLSearchParams(window.location.search);
  const view = query.get('view');
  return { view: Object.hasOwn(views, view) ? view : 'spatial', snapshotId: query.get('snapshot') || null, invalidView: view !== null && !Object.hasOwn(views, view) };
}
function writeRoute(mode = 'replace') {
  const url = new URL(window.location.href);
  url.searchParams.set('view', state.view);
  if (state.snapshot) url.searchParams.set('snapshot', state.snapshot.id);
  else url.searchParams.delete('snapshot');
  if (url.href !== window.location.href) window.history[mode === 'push' ? 'pushState' : 'replaceState'](null, '', url);
}

function el(tag, className, text) { const n = document.createElement(tag); if (className) n.className = className; if (text !== undefined) n.textContent = String(text); return n; }
function clear(n) { n.replaceChildren(); return n; }
function number(v) { return Number(v || 0).toLocaleString(); }
function bytes(v) { if (v === null || v === undefined || !Number.isFinite(Number(v))) return 'Unknown'; const n = Number(v); if (n === 0) return '0 B'; const i = Math.min(5, Math.floor(Math.log(Math.abs(n)) / Math.log(1024))); return `${(n / 1024 ** i).toLocaleString(undefined, { maximumFractionDigits: i ? 1 : 0 })} ${['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'][Math.max(i, 0)]}`; }
function date(v) { return v ? new Date(v).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }) : 'In progress'; }
function badge(value) { const s = String(value || 'unknown'); const c = /complete|success|finished/i.test(s) && !/incomplete/i.test(s) ? '' : /error|fail|critical|high/i.test(s) ? ' error' : /partial|incomplete|cancel|warning|medium/i.test(s) ? ' warning' : ' neutral'; return el('span', 'badge' + c, s); }
function notice(text, error = false) { $('notice-text').textContent = text; $('notice').classList.toggle('error', error); $('notice').hidden = false; $('announcer').textContent = text; }
function empty(target, title, text, action, loading = false) { const n = clear(target); const box = el('div', 'empty-state' + (loading ? ' loading' : '')); box.append(el('span', 'empty-icon', loading ? '◌' : '◈'), el('strong', '', title), el('p', '', text)); if (action) { const b = el('button', 'button secondary', action.text); b.addEventListener('click', action.run); box.append(b); } n.append(box); }
function connected(value) { state.connected = value; $('connection-label').textContent = value ? `Connected · ${state.role}` : 'Connect workspace'; $('connection-dot').classList.toggle('connected', value); $('new-scan-button').disabled = value && !canScan(); if (!value) { window.FileMapperEditionPractice?.destroy($('live-edition-practice')); window.FileMapperWordLibrary?.destroy($('live-word-library')); window.FileMapperHashing?.destroy($('live-hashing')); window.FileMapperSourceSemantics?.destroy($('live-source-semantics')); window.FileMapperSemanticModel?.destroy($('live-semantic-model')); window.FileMapperLearningArena?.destroy($('live-learning-arena')); } }
function endpoint(part) { return `/api/snapshots/${encodeURIComponent(state.snapshot.id)}/${part}`; }
async function api(url, options = {}) {
  const headers = new Headers(options.headers); if (state.token) headers.set('Authorization', `Bearer ${state.token}`);
  if (options.body && typeof options.body !== 'string') { headers.set('Content-Type', 'application/json'); options.body = JSON.stringify(options.body); }
  const response = await fetch(url, { ...options, headers, credentials: 'same-origin', cache: 'no-store' });
  if (!response.ok) { let detail; try { detail = await response.json(); } catch { detail = {}; } if (response.status === 401) connected(false); throw new Error(detail.detail || detail.title || detail.error || `Request failed (${response.status})`); }
  if (options.raw) return response;
  if (response.status === 204) return null;
  return response.json();
}
async function refreshBuild() {
  const ui = document.documentElement.dataset.uiVersion || 'unknown';
  try {
    const health = await api('/api/health', { signal: AbortSignal.timeout(5000) });
    if (health.product !== 'FileMapper' || typeof health.version !== 'string' || !/^\d+\.\d+\.\d+$/.test(health.version)) throw new Error('Build unavailable');
    const matching = health.version === ui;
    $('service-build').textContent = matching ? `v${health.version}` : 'Build mismatch';
    $('service-build').title = `Running service ${health.version} · UI ${ui}${matching ? '' : ' · Reload to load the installed UI'}`;
    $('build-detail').textContent = `SERVICE ${health.version} · UI ${ui} · DEVELOPMENT`;
  } catch {
    $('service-build').textContent = 'Build unavailable';
    $('service-build').title = 'The running service version could not be verified.';
    $('build-detail').textContent = `UI ${ui} · SERVICE UNVERIFIED`;
  }
}
function showConnection() { $('access-token').value = state.token; $('connect-error').textContent = ''; $('connect-dialog').showModal(); $('access-token').focus(); }
function showScan() { if (!state.connected) { showConnection(); return; } if (!canScan()) { notice('This credential is read-only. Connect with a scanner or operator credential to map a folder.'); return; } $('scan-error').textContent = ''; if (state.snapshot && !$('scan-root').value) $('scan-root').value = state.snapshot.root; $('scan-dialog').showModal(); $('scan-root').focus(); }
function fillSnapshotSelect() {
  clear($('snapshot-select'));
  if (!state.snapshots.length) { const option = el('option', '', 'No snapshots yet'); option.value = ''; $('snapshot-select').append(option); }
  for (const snapshot of state.snapshots) { const option = el('option', '', `${date(snapshot.startedAt)} · ${snapshot.id.slice(0, 8)}`); option.value = snapshot.id; $('snapshot-select').append(option); }
  $('snapshot-select').value = state.snapshot?.id || '';
}
function fillScope() {
  const s = state.snapshot;
  $('scope-path').textContent = s ? s.root : state.connected ? 'Your workspace is ready. Map your first folder.' : 'Connect your workspace to begin';
  const b = badge(s?.completeness || 'No snapshot'); b.id = 'scope-completeness'; $('scope-completeness').replaceWith(b);
  $('scope-time').textContent = s ? `${s.id.slice(0, 8)} · ${date(s.startedAt)}` : '';
  $('analyze-button').disabled = !s || s.status !== 'completed' || !canScan();
  $('graph-analyze-button').disabled = $('analyze-button').disabled;
  for (const id of ['root-button', 'up-button', 'export-button', 'export-format', 'node-kind']) $(id).disabled = !s;
  $('metric-logical').textContent = s ? bytes(s.logicalBytes) : '—';
  $('metric-allocated').textContent = s ? bytes(s.allocatedBytes) : '—';
  $('allocated-note').textContent = s?.allocatedBytes == null ? 'Unknown until allocation is verified' : 'Unique physical allocation';
  $('metric-files').textContent = s ? number(s.fileCount) : '—';
  $('metric-entries-note').textContent = s ? `${number(s.entryCount)} locations · ${number(s.directoryCount)} directories` : 'Stable identities, linked locations';
  $('metric-errors').textContent = s ? number(s.errorCount) : '—';
}
async function refresh(preserve = true) {
  void refreshBuild();
  try {
    const [session, snapshots] = await Promise.all([api('/api/session'), api('/api/snapshots')]);
    state.capabilities = session.capabilities; state.role = session.role; state.snapshots = snapshots; connected(true);
    const prior = state.snapshot?.id;
    const requested = preserve ? readRoute().snapshotId : null;
    state.snapshot = (preserve && state.snapshots.find(s => s.id === prior)) ||
      (requested ? state.snapshots.find(s => s.id === requested) || null : state.snapshots[0] || null);
    if (prior !== state.snapshot?.id) resetSelection();
    if (requested && !state.snapshot) notice('The snapshot in this link is unavailable in this workspace. Choose an available snapshot to continue.', true);
    else writeRoute();
    fillSnapshotSelect(); fillScope(); await renderView();
  } catch (e) { notice(e.message, true); if (!state.connected) renderDisconnected(); }
}
function resetSelection() { window.FileMapperHashing?.destroy($('live-hashing'));  window.FileMapperLearningArena?.destroy($('live-learning-arena')); window.FileMapperSemanticModel?.destroy($('live-semantic-model')); state.parent = state.snapshot?.root || null; state.duplicateOffset = 0; state.breakdownOffset = 0; state.offset = 0; state.nodeOffset = 0; state.search = ''; state.selected = null; state.rank = []; $('search-input').value = ''; closeInspector(); }
function renderDisconnected() { window.FileMapperEditionPractice?.destroy($('live-edition-practice')); empty($('live-edition-practice'), 'Connect to edition practice', 'Play whole shapes from a saved public edition arena.', { text: 'Connect workspace', run: showConnection }); window.FileMapperWordLibrary?.destroy($('live-word-library')); empty($('live-word-library'), 'Connect to the public word library', 'Search pinned public editions and inspect their source observations.', { text: 'Connect workspace', run: showConnection }); window.FileMapperHashing?.destroy($('live-hashing')); empty($('live-hashing'), 'Connect to content analysis', 'Inspect staged reads, content matches and their shape evidence.');  window.FileMapperSourceSemantics?.destroy($('live-source-semantics')); empty($('live-source-semantics'), 'Connect to retained-source semantics', 'Analyze captured source and inspect native objects with exact evidence.', { text: 'Connect workspace', run: showConnection }); window.FileMapperSourceHarvest?.destroy($('live-source-harvest')); empty($('live-source-harvest'), 'Connect to source words', 'Explore pinned repository content and immutable word positions.', { text: 'Connect workspace', run: showConnection }); window.FileMapperLearningArena?.destroy($('live-learning-arena')); empty($('live-learning-arena'), 'Connect to the learning arena', 'Inspect native learners and replay whole shapes against frozen evidence.', { text: 'Connect workspace', run: showConnection }); window.FileMapperSemanticModel?.destroy(document.getElementById('live-semantic-model')); empty(document.getElementById('live-semantic-model'), 'Connect to semantic evidence', 'Inspect native objects, compare snapshots, and replay grounded 9bit challenges.', { text: 'Connect workspace', run: showConnection }); window.FileMapperRobocopy?.destroy(document.getElementById('live-robocopy')); empty(document.getElementById('live-robocopy'), 'Connect to Robocopy tools', 'Use a local role credential to inspect transfer options.', { text: 'Connect workspace', run: showConnection }); window.FileMapperSpatial?.destroy(document.getElementById('live-spatial')); empty(document.getElementById('live-spatial'), 'Connect to your spatial workspace', 'Your shapes are generated from actual snapshot evidence.', { text: 'Connect workspace', run: showConnection }); fillScope(); for (const id of ['treemap', 'largest-files', 'composition', 'file-table', 'node-list', 'graph-neighborhood', 'findings-list', 'duplicate-list', 'snapshot-list', 'jobs-list']) empty($(id), 'A map starts with a connection', 'Connect to your local FileMapper service to explore real filesystem evidence.', { text: 'Connect workspace', run: showConnection }); }
function renderNoSnapshot() { window.FileMapperHashing?.destroy($('live-hashing')); empty($('live-hashing'), 'Choose a baseline snapshot', 'Create a filesystem scan to analyze current content with recorded evidence.');  window.FileMapperSemanticModel?.destroy(document.getElementById('live-semantic-model')); empty(document.getElementById('live-semantic-model'), 'No semantic snapshot selected', 'Choose an observed snapshot to inspect identities, word shapes, patterns, and replay challenges.'); window.FileMapperSpatial?.destroy(document.getElementById('live-spatial')); empty(document.getElementById('live-spatial'), 'No observed layers yet', 'Create a snapshot to map files into semantic shapes.'); for (const id of ['treemap', 'largest-files', 'composition', 'file-table', 'node-list', 'graph-neighborhood', 'findings-list', 'duplicate-list']) empty($(id), 'Unmapped territory', 'Choose a folder to create your first evidence-backed snapshot.', { text: 'Start your first scan', run: showScan }); $('treemap-caption').textContent = 'No filesystem data has been collected yet.'; }
async function switchView(view, routeMode = 'push') {
  if (!Object.hasOwn(views, view)) return;
  if (state.view === 'duplicates' && view !== 'duplicates') window.FileMapperHashing?.destroy($('live-hashing'));
  if (state.view === 'source-semantics' && view !== 'source-semantics') window.FileMapperSourceSemantics?.destroy($('live-source-semantics'));
  if (state.view === 'word-library' && view !== 'word-library') window.FileMapperWordLibrary?.destroy($('live-word-library'));
  if (state.view === 'sources' && view !== 'sources') window.FileMapperSourceHarvest?.destroy($('live-source-harvest'));
  if (state.view === 'practice' && view !== 'practice') window.FileMapperEditionPractice?.destroy($('live-edition-practice'));
  if (state.view === 'learning' && view !== 'learning') window.FileMapperLearningArena?.destroy($('live-learning-arena'));
  if (state.view === 'spatial' && view !== 'spatial') window.FileMapperSpatial?.destroy(document.getElementById('live-spatial'));
  state.view = view;
  document.body.dataset.view = view;
  document.title = `FileMapper · ${views[view][0]}`;
  if (routeMode) writeRoute(routeMode);
  closeInspector();
  for (const [key, config] of Object.entries(views)) {
    $(`view-${key}`).hidden = key !== view;
    const button = document.querySelector(`#navigation button[data-view="${key}"]`);
    button.classList.toggle('active', key === view);
    if (key === view) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
    if (key === view) {
      $('breadcrumb-view').textContent = config[0]; $('page-eyebrow').textContent = config[1];
      clear($('page-title')).append(document.createTextNode(config[2]), el('span', '', '.'));
      $('page-subtitle').textContent = config[3];
    }
  }
  window.scrollTo({top: 0, left: 0, behavior: 'instant'});
  await renderView();
}
async function renderView() {
  const generation = ++state.request;
  if (!state.connected) { renderDisconnected(); return; }
  if (!state.snapshot && !['activity','robocopy','learning','sources','source-semantics','word-library','practice'].includes(state.view)) { renderNoSnapshot(); return; }
  try {
    if (state.view === 'overview') await renderOverview(generation);
    else if (state.view === 'explorer') await renderExplorer(generation);
    else if (state.view === 'graph') await renderNodes(generation);
    else if (state.view === 'findings') await renderFindings(generation);
    else if (state.view === 'duplicates') await renderDuplicates(generation);
    else if (state.view === 'activity') await renderActivity(generation);
    else if (state.view === 'spatial') await FileMapperSpatial.render($('live-spatial'), {snapshot: state.snapshot, snapshots: state.snapshots, api, endpoint, inspectEntry, inspectNode});
    else if (state.view === 'semantic') await FileMapperSemanticModel.render($('live-semantic-model'), {api, capabilities: state.capabilities, snapshot: state.snapshot, snapshots: state.snapshots});
    else if (state.view === 'learning') await FileMapperLearningArena.render($('live-learning-arena'), {api, capabilities: state.capabilities, snapshot: state.snapshot, snapshots: state.snapshots});
    else if (state.view === 'practice') await FileMapperEditionPractice.render($('live-edition-practice'), {api, capabilities: state.capabilities, initialEdition: state.practiceEdition, initialEditionOffset: state.practiceEditionOffset});
    else if (state.view === 'word-library') await FileMapperWordLibrary.render($('live-word-library'), {api, capabilities: state.capabilities, openPractice: (sha,offset) => {state.practiceEdition=sha;state.practiceEditionOffset=Math.floor(offset/20)*20; switchView('practice');}});
    else if (state.view === 'sources') await FileMapperSourceHarvest.render($('live-source-harvest'), {api});
    else if (state.view === 'source-semantics') await FileMapperSourceSemantics.render($('live-source-semantics'), {api, capabilities: state.capabilities});
    else if (state.view === 'robocopy') await FileMapperRobocopy.render($('live-robocopy'), {api, capabilities: state.capabilities});
  } catch (e) { if (generation === state.request) notice(e.message, true); }
}
function fileTable(target, entries, compact = false) {
  clear(target); if (!entries.length) { empty(target, 'No files in this view', 'Try another folder, adjust your search, or refresh the snapshot.'); return; }
  const table = el('table', 'file-table'); const head = el('thead'); const hr = el('tr'); ['Name / location', 'Logical size', 'Allocated', compact ? 'Type' : 'Modified'].forEach((text, i) => { const th = el('th', i === 2 || i === 3 ? 'optional-column' : '', text); th.scope = 'col'; hr.append(th); }); head.append(hr); table.append(head); const body = el('tbody');
  for (const entry of entries) {
    const tr = el('tr'); tr.dataset.nodeId = entry.nodeId; if (state.selected === entry.nodeId) tr.classList.add('selected');
    const nameCell = el('td'); const b = el('button', 'file-link'); b.title = entry.path; const isDirectory = /directory/i.test(entry.kind); b.append(el('span', '', isDirectory ? '▰' : '▱')); const name = el('span'); name.append(el('strong', '', entry.name), el('small', '', entry.path)); b.append(name); b.addEventListener('click', () => inspectEntry(entry)); nameCell.append(b);
    const logicalCell = el('td', 'mono', isDirectory ? (entry.directoryMetrics ? bytes(entry.directoryMetrics.recursiveLogicalBytes) : 'Not aggregated') : bytes(entry.logicalBytes));
    const allocatedCell = el('td', 'mono optional-column', isDirectory ? (entry.directoryMetrics ? bytes(entry.directoryMetrics.recursiveAllocatedBytes) : 'Not aggregated') : bytes(entry.allocatedBytes));
    if (isDirectory) { logicalCell.title = entry.directoryMetrics ? `Distinct descendant identities · ${entry.directoryMetrics.completeness}` : 'Recursive metrics unavailable in this search projection.'; allocatedCell.title = entry.directoryMetrics ? `Allocation counted once per identity within this subtree · ${entry.directoryMetrics.completeness}` : 'Recursive metrics unavailable in this search projection.'; }
    tr.append(nameCell, logicalCell, allocatedCell, el('td', 'optional-column', compact ? entry.kind : date(entry.modifiedAt)));
    if (isDirectory) { const open = el('button', 'text-button', 'Open →'); open.setAttribute('aria-label', `Open ${entry.name}`); open.addEventListener('click', () => navigateFolder(entry.path)); nameCell.append(open); }
    body.append(tr);
  }
  table.append(body); target.append(table);
}
async function renderOverview(generation) {
  empty($('treemap'), 'Reading the landscape', 'Loading ranked filesystem measurements…', null, true);
  const [result, breakdown] = await Promise.all([api(endpoint(`rank?by=${$('metric-select').value}&limit=50`)), api(endpoint(`breakdown?by=${$('breakdown-select').value}&offset=${state.breakdownOffset}&limit=5`))]); if (generation !== state.request) return;
  state.rank = [...new Map((result.items || result || []).map(row => row.item || row).filter(row => /file/i.test(row.kind)).map(row => [row.nodeId, row])).values()];
  renderTreemap(); renderComposition(breakdown); fileTable($('largest-files'), state.rank.slice(0, 7), true);
  const represented = state.rank.length;
  $('treemap-caption').textContent = `${Math.min(24, represented)} ranked identities shown · ${$('metric-select').value} bytes · ${result.truncated ? 'bounded projection' : 'ranked projection'}`;
}
function renderTreemap() {
  const container = clear($('treemap')); const metric = $('metric-select').value === 'allocated' ? 'allocatedBytes' : 'logicalBytes'; const items = state.rank.filter(x => Number(x[metric]) > 0).slice(0, 24); const total = items.reduce((a, x) => a + Number(x[metric]), 0);
  if (!items.length) { empty(container, 'No measured bytes to display', 'Empty files and unknown allocation have no area. Select logical bytes or scan another folder.'); return; }
  function split(rows, x, y, w, h) {
    if (rows.length === 1) { const entry = rows[0]; const index = items.indexOf(entry); const tile = el('button', 'treemap-tile'); tile.dataset.nodeId = entry.nodeId; tile.title = `${entry.path}\n${bytes(entry[metric])} ${metric === 'logicalBytes' ? 'logical' : 'allocated'}`; tile.setAttribute('aria-label', tile.title.replace('\n', ', ')); Object.assign(tile.style, { left: `${x}%`, top: `${y}%`, width: `${w}%`, height: `${h}%`, background: palette[index % palette.length], color: index % palette.length < 2 ? '#182810' : '#eff9e8' }); tile.append(el('strong', '', entry.name)); if (w > 13 && h > 19) tile.append(el('span', '', bytes(entry[metric]))); if (w > 18 && h > 35) tile.append(el('em', '', `${(Number(entry[metric]) / total * 100).toFixed(1)}% of this view`)); if (state.selected === entry.nodeId) tile.classList.add('selected'); tile.addEventListener('click', () => inspectEntry(entry)); container.append(tile); return; }
    const sum = rows.reduce((a, r) => a + Number(r[metric]), 0); let left = Number(rows[0][metric]), pivot = 1; while (pivot < rows.length - 1 && left < sum / 2) { left += Number(rows[pivot][metric]); pivot++; } const ratio = left / sum; const horizontal = w * (container.clientWidth || 500) > h * (container.clientHeight || 248);
    if (horizontal) { split(rows.slice(0, pivot), x, y, w * ratio, h); split(rows.slice(pivot), x + w * ratio, y, w * (1 - ratio), h); }
    else { split(rows.slice(0, pivot), x, y, w, h * ratio); split(rows.slice(pivot), x, y + h * ratio, w, h * (1 - ratio)); }
  }
  split(items, 0, 0, 100, 100);
}
function renderComposition(result) {
  const target = clear($('composition')); const metric = $('metric-select').value === 'allocated' ? 'allocatedBytes' : 'logicalBytes';
  const page = result.page; const total = page.items.reduce((sum, row) => sum + Number(row[metric] || 0), 0);
  $('breakdown-prev').disabled = state.breakdownOffset === 0;
  $('breakdown-next').disabled = state.breakdownOffset + page.items.length >= page.total;
  $('breakdown-page').textContent = `${number(page.total)} groups`;
  if (!page.items.length) { empty(target, 'No measured files', 'This scope contains no file identities for this breakdown.'); return; }
  page.items.forEach((group, i) => {
    const row = el('div', 'composition-row'); row.title = `${number(group.uniqueFiles)} files · ${number(group.streamIdentities)} streams · ${number(group.pathEntries)} locations · ${group.completeness}`;
    const swatch = el('span', 'composition-swatch'); swatch.style.background = palette[i];
    const bar = el('div', 'composition-bar'); const fill = el('span'); fill.style.width = `${total ? Number(group[metric] || 0) / total * 100 : 0}%`; fill.style.background = palette[i]; bar.append(fill);
    row.append(swatch, el('span', 'composition-name', group.label), bar, el('span', 'composition-bytes', bytes(group[metric]))); target.append(row);
  });
  target.append(el('p', 'composition-subtitle', `Entire snapshot · ${result.completeness}. Bars compare measured bytes on this page. ${result.groupingStrategy}`));
}
async function renderExplorer(generation) {
  $('explorer-path').textContent = state.search ? `Search: ${state.search} · entire snapshot` : state.parent || state.snapshot.root;
  empty($('file-table'), 'Reading locations', 'Loading a bounded page of entries…', null, true);
  const query = new URLSearchParams({ offset: state.offset, limit: PAGE }); if (state.search) query.set('search', state.search); else if (state.parent) query.set('parent', state.parent);
  const envelope = await api(endpoint(`${state.search ? 'entries' : 'hierarchy'}?${query}`)); if (generation !== state.request) return;
  const result = state.search ? envelope : {...envelope.page, items: envelope.page.items.map(row => ({...row.item, directoryMetrics:row.metrics}))};
  if (!state.search && envelope.scope) $('explorer-path').textContent += ` · ${bytes(envelope.scope.recursiveLogicalBytes)} logical · ${bytes(envelope.scope.recursiveAllocatedBytes)} allocated · ${envelope.scope.completeness}`;
  state.entries = result.items; fileTable($('file-table'), result.items); $('file-page-info').textContent = result.total ? `${number(state.offset + 1)}–${number(Math.min(state.offset + result.items.length, result.total))} of ${number(result.total)} locations` : '0 locations'; $('file-prev').disabled = state.offset === 0; $('file-next').disabled = state.offset + result.items.length >= result.total; $('up-button').disabled = !!state.search || !state.parent || state.parent === state.snapshot.root;
}
function navigateFolder(path) { state.parent = path; state.search = ''; state.offset = 0; $('search-input').value = ''; switchView('explorer'); }
function select(id) { state.selected = id; document.querySelectorAll('[data-node-id]').forEach(n => n.classList.toggle('selected', n.dataset.nodeId === id)); }
let inspectorReturnFocus = null;
function openInspector() {
  if ($('inspector').hidden) inspectorReturnFocus = document.activeElement;
  document.getElementById('inspector').hidden = false;
  $('close-inspector').focus({preventScroll:true});
}
function closeInspector() {
  const wasOpen = !$('inspector').hidden;
  $('inspector').hidden = true; state.inspectorRequest++;
  if (wasOpen && inspectorReturnFocus?.isConnected) inspectorReturnFocus.focus({preventScroll:true});
  inspectorReturnFocus = null;
}
function detail(target, label, value, mono = false) { target.append(el('div', 'detail-label', label), el('div', 'detail-value' + (mono ? ' mono' : ''), value == null ? 'Unknown' : value)); }
async function inspectEntry(entry) {
  select(entry.nodeId); const generation = ++state.inspectorRequest; $('inspector-title').textContent = entry.name; openInspector(); const target = clear($('inspector-content')); detail(target, 'Location', entry.path, true); detail(target, 'Stable identity', entry.nodeId, true); detail(target, 'Kind', entry.kind); detail(target, 'Logical bytes', /directory/i.test(entry.kind) ? (entry.directoryMetrics ? bytes(entry.directoryMetrics.recursiveLogicalBytes) + ' · recursive' : 'Not aggregated in this projection') : bytes(entry.logicalBytes)); detail(target, 'Allocated bytes', /directory/i.test(entry.kind) ? (entry.directoryMetrics ? bytes(entry.directoryMetrics.recursiveAllocatedBytes) + ' · recursive' : 'Not aggregated in this projection') : bytes(entry.allocatedBytes)); detail(target, 'Hardlink names on filesystem', entry.linkCount); detail(target, 'Modified', date(entry.modifiedAt)); detail(target, 'Attributes', entry.attributes); if (entry.owner) detail(target, 'Owner', entry.owner); if (entry.hash) detail(target, 'Content hash', entry.hash, true); target.append(el('hr', 'detail-separator')); detail(target, 'Snapshot', `${state.snapshot.id} · ${state.snapshot.completeness}`, true); if (/directory/i.test(entry.kind)) { const b = el('button', 'button secondary', 'Open this folder →'); b.style.marginTop = '18px'; b.addEventListener('click', () => { closeInspector(); navigateFolder(entry.path); }); target.append(b); }
  if (entry.directoryMetrics) { detail(target, 'Recursive logical bytes', bytes(entry.directoryMetrics.recursiveLogicalBytes)); detail(target, 'Recursive allocated bytes', bytes(entry.directoryMetrics.recursiveAllocatedBytes)); detail(target, 'Exclusive allocation within scope', bytes(entry.directoryMetrics.exclusiveAllocatedBytes)); detail(target, 'Scope completeness', entry.directoryMetrics.completeness); detail(target, 'Unique descendant files', entry.directoryMetrics.uniqueFileCount); for (const note of entry.directoryMetrics.limitations || []) detail(target, 'Measurement note', note); }
  await appendEvidence(target, entry.nodeId, generation);
}
async function appendEvidence(target, id, generation) {
  try { const rows = await api(endpoint(`observations?subjectId=${encodeURIComponent(id)}`)); if (generation !== state.inspectorRequest) return; target.append(el('hr', 'detail-separator'), el('h3', '', 'Supporting observations')); if (!rows.length) target.append(el('p', 'detail-value', 'No direct observations were returned for this identity.')); for (const evidence of rows) { const card = el('div', 'evidence-card'); card.append(el('strong', '', evidence.predicate), el('p', '', evidence.value), el('small', '', `${evidence.sourceKind} · ${Math.round(evidence.confidence * 100)}% confidence · ${evidence.completeness}`), el('small', '', evidence.sourceUri)); if (evidence.errorDetail) card.append(el('p', '', evidence.errorDetail)); target.append(card); } } catch (e) { if (generation === state.inspectorRequest) target.append(el('p', 'detail-value', `Evidence unavailable: ${e.message}`)); }
}
async function renderNodes(generation) {
  empty($('node-list'), 'Reading the graph', 'Loading symbols and entities…', null, true);
  const query = new URLSearchParams({ offset: state.nodeOffset, limit: PAGE }); if ($('node-kind').value) query.set('kind', $('node-kind').value); if ($('node-search').value.trim()) query.set('search', $('node-search').value.trim());
  const result = await api(endpoint(`nodes?${query}`)); if (generation !== state.request) return;
  const target = clear($('node-list')); $('node-total').textContent = `${number(result.total)} nodes`; $('node-prev').disabled = state.nodeOffset === 0; $('node-next').disabled = state.nodeOffset + result.items.length >= result.total;
  if (!result.items.length) empty(target, 'No matching graph nodes', 'Run semantic analysis or change your search to discover related entities.');
  for (const node of result.items) { const b = el('button', 'node-button'); b.dataset.nodeId = node.id; b.append(el('span', 'node-icon', node.kind.slice(0, 2))); const copy = el('span', 'node-copy'); copy.append(el('strong', '', node.name), el('small', '', `${node.kind} · ${node.path || node.id}`)); b.append(copy); b.addEventListener('click', () => inspectNode(node)); target.append(b); }
  if (!state.selected) empty($('graph-neighborhood'), 'Pick a point on the map', 'Select a node to explore its observed relationships and evidence.');
}
async function inspectNode(node) {
  select(node.id); const generation = ++state.inspectorRequest; const neighborhood = clear($('graph-neighborhood')); const center = el('div', 'graph-center'); center.append(badge(node.kind), el('strong', '', node.name)); neighborhood.append(center);
  $('inspector-title').textContent = node.name; openInspector(); const target = clear($('inspector-content')); detail(target, 'Node identity', node.id, true); detail(target, 'Kind', node.kind); if (node.path) detail(target, 'Location', node.path, true); for (const [key, value] of Object.entries(node.properties || {})) detail(target, key, typeof value === 'object' ? JSON.stringify(value, null, 2) : value, true);
  try { const edges = await api(endpoint(`edges?nodeId=${encodeURIComponent(node.id)}`)); if (generation !== state.inspectorRequest) return; if (!edges.length) neighborhood.append(el('p', 'detail-value', 'No adjacent edges were returned for this node.')); for (const edge of edges) { const outgoing = edge.fromId === node.id; const b = el('button', 'graph-edge'); const other = outgoing ? edge.toId : edge.fromId; b.append(el('span', 'edge-kind', `${outgoing ? '→' : '←'} ${edge.kind}`), el('span', 'edge-id', other)); b.title = `${edge.kind}: ${edge.fromId} → ${edge.toId}`; b.addEventListener('click', () => inspectEdge(edge)); neighborhood.append(b); } neighborhood.append(el('p', 'composition-subtitle', `${edges.length} observed adjacent relationships shown. Edge direction and confidence are retained in evidence.`)); await appendEvidence(target, node.id, generation); } catch (e) { notice(e.message, true); }
}
function inspectEdge(edge) { state.inspectorRequest++; $('inspector-title').textContent = edge.kind; openInspector(); const target = clear($('inspector-content')); detail(target, 'From', edge.fromId, true); detail(target, 'To', edge.toId, true); detail(target, 'Evidence ID', edge.evidenceId, true); detail(target, 'Confidence', `${Math.round(edge.confidence * 100)}%`); detail(target, 'Properties', JSON.stringify(edge.properties || {}, null, 2), true); }
async function renderFindings(generation) {
  empty($('findings-list'), 'Reading findings', 'Loading analysis results…', null, true); const result = await api(endpoint('findings')); if (generation !== state.request) return;
  const rows = result.items || result; const target = clear($('findings-list')); $('findings-count').textContent = number(rows.length); $('findings-count').hidden = rows.length === 0;
  if (!rows.length) { empty(target, 'No findings recorded', 'Run semantic analysis to collect supported checks. An empty finding list does not establish that the source is safe.', { text: 'Analyze snapshot', run: analyze }); return; }
  for (const finding of rows) {
    const card = el('article', 'finding-card'); card.append(badge(finding.severity)); const body = el('div', 'finding-main');
    body.append(el('h3', '', finding.title), el('p', '', finding.explanation), el('p', 'finding-meta', `${finding.ruleId} · ${finding.path}:${finding.line}${finding.cwe ? ' · ' + finding.cwe : ''}`));
    body.append(el('p', 'finding-meta', `${finding.status || 'review-candidate'} · ${finding.modelVersion || 'legacy-candidate-v1'} · ${finding.completeness || 'partial'} coverage${finding.truncated ? ' · witness truncated' : ''}`));
    card.append(body); const actions = el('div', 'finding-actions'); actions.append(badge(`${Math.round(finding.confidence * 100)}% confidence`));
    const inspect = el('button', 'button secondary compact', finding.route?.length ? `Trace ${number(finding.route.length)} steps ↗` : 'Evidence ↗');
    inspect.addEventListener('click', () => inspectFinding(finding)); actions.append(inspect); card.append(actions); target.append(card);
  }
}
async function inspectFinding(finding) {
  const generation = ++state.inspectorRequest; $('inspector-title').textContent = finding.title; openInspector();
  const target = clear($('inspector-content'));
  detail(target, 'Assessment', finding.status || 'review-candidate'); detail(target, 'Model', finding.modelVersion || 'legacy-candidate-v1');
  detail(target, 'Coverage', finding.completeness || 'partial'); detail(target, 'Explanation', finding.explanation);
  if (finding.truncated) detail(target, 'Truncation', finding.truncationReason || 'The witness is incomplete.');
  if (finding.assumptions?.length) { const list = el('ul', 'finding-assumptions'); for (const item of finding.assumptions) list.append(el('li', '', item)); target.append(el('h3', 'witness-heading', 'Model assumptions'), list); }
  try {
    const result = await api(endpoint(`findings/${encodeURIComponent(finding.id)}`)); if (generation !== state.inspectorRequest) return;
    target.append(el('h3', 'witness-heading', 'Ordered witness'));
    if (!result.steps.length) target.append(el('p', 'detail-value', 'This finding has supporting observations but no saved flow witness.'));
    else {
      const route = el('ol', 'witness-route');
      for (const step of result.steps) {
        const item = el('li', 'witness-step'); item.append(el('strong', '', step.location.reason), el('p', 'witness-location', `${step.location.path}:${step.location.line}:${step.location.column}`));
        if (step.via) item.append(el('p', 'finding-meta', `${step.via.kind} · ${Math.round(step.via.confidence * 100)}% confidence`));
        const inspect = el('button', 'text-button', `Inspect ${step.node.kind}`); inspect.addEventListener('click', () => inspectNode(step.node)); item.append(inspect); route.append(item);
      }
      target.append(route, el('p', 'detail-value', 'This is a modeled witness. Runtime feasibility and exploitability require review.'));
    }
    const evidence = el('details', 'witness-evidence'); evidence.append(el('summary', '', `${number(result.observations.length)} supporting observations`));
    for (const observation of result.observations) { const block = el('div', 'witness-observation'); detail(block, observation.predicate, observation.value); detail(block, 'Source', observation.sourceUri, true); detail(block, 'Evidence ID', observation.id, true); detail(block, 'Coverage', observation.completeness); evidence.append(block); }
    target.append(evidence);
  } catch (e) { if (generation === state.inspectorRequest) target.append(el('p', 'detail-value', e.message)); }
}
async function renderDuplicates(generation) {
  await window.FileMapperHashing?.render($('live-hashing'), { api, snapshot: state.snapshot, capabilities: state.capabilities });
  if (generation !== state.request) return;
  empty($('duplicate-list'), 'Comparing content identities', 'Reading confirmed hash groups…', null, true); const result = await api(endpoint(`duplicates?offset=${state.duplicateOffset}&limit=${PAGE}`)); if (generation !== state.request) return;
  const target = clear($('duplicate-list')); $('duplicate-page-info').textContent = `${number(result.totalGroups)} groups · page ${1 + state.duplicateOffset/PAGE}`; $('duplicate-prev').disabled = state.duplicateOffset === 0; $('duplicate-next').disabled = state.duplicateOffset + result.groups.length >= result.totalGroups; if (!result.groups?.length) { empty(target, 'No duplicate groups recorded', `${number(result.hashedFiles)} hashed files in the available projection. Enable content hashing for a new scan to discover exact duplicates.`); return; }
  target.append(el('p', 'path-trail', `${number(result.groups.length)} content groups on this page · ${number(result.hashedFiles)} hashed files · ${result.completeness}`));
  for (const group of result.groups) {
    const section = el('section', 'panel duplicate-group'); const heading = el('div', 'panel-heading'); const intro = el('div');
    intro.append(el('h2', '', `${number(group.pathCount)} matching locations · ${number(group.uniqueIdentities)} identities`), el('p', 'duplicate-title', group.hash)); heading.append(intro, badge(group.confidence || 'confirmed')); section.append(heading);
    const summary = el('div', 'panel-footer'); summary.append(el('span', '', `Content: ${bytes(group.logicalBytes)}`), el('span', '', `Estimated reclaimable: ${bytes(group.reclaimableBytes)}${group.savingsLimitation ? ' · ' + group.savingsLimitation : ''}`)); section.append(summary);
    const members = el('div'); section.append(members);
    const appendMembers = rows => { for (const entry of rows) { const b = el('button', 'duplicate-path text-button', entry.path); b.addEventListener('click', () => inspectEntry(entry)); members.append(b); } };
    appendMembers(group.items);
    if (group.itemsTruncated) {
      let offset = group.items.length; const more = el('button', 'button secondary compact', 'Load more locations'); section.append(more);
      more.addEventListener('click', async () => {
        more.disabled = true;
        try {
          const page = await api(endpoint(`duplicates/members?hash=${encodeURIComponent(group.hash)}&logicalBytes=${group.logicalBytes}&offset=${offset}&limit=1000`));
          if (generation !== state.request) return;
          appendMembers(page.page.items); offset += page.page.items.length; more.hidden = offset >= page.page.total;
        } catch (e) { notice(e.message, true); } finally { more.disabled = false; }
      });
    }
    target.append(section);
  }
}
async function renderActivity(generation) {
  const target = clear($('snapshot-list')); if (!state.snapshots.length) empty(target, 'No observation history yet', 'Your first scan creates the first point in this timeline.', { text: 'Start a scan', run: showScan });
  for (const snapshot of state.snapshots) { const row = el('div', 'snapshot-row'); const info = el('div'); info.append(el('strong', '', snapshot.root), el('small', '', `${date(snapshot.startedAt)} · ${number(snapshot.entryCount)} locations · ${bytes(snapshot.logicalBytes)} logical`)); const open = el('button', 'button secondary compact', 'Explore →'); open.addEventListener('click', () => { state.snapshot = snapshot; resetSelection(); fillSnapshotSelect(); fillScope(); switchView('overview'); }); row.append(info, badge(snapshot.status), badge(snapshot.completeness), open); target.append(row); }
  const jobs = await api('/api/jobs'); if (generation !== state.request) return; renderJobs(jobs.items || jobs);
}
function renderJobs(jobs) {
  const target = clear($('jobs-list'));
  if (!jobs.length) { empty(target, 'No recorded jobs', 'Start a scan or semantic analysis to see its durable progress here.'); return; }
  for (const job of jobs) {
    const row = el('div', 'job-row'); const info = el('div');
    info.append(el('strong', '', `${job.kind || 'Scan'} · ${job.id.slice(0, 8)}`), el('small', '', `${date(job.startedAt)} · ${number(job.entries)} entries${job.error ? ' · ' + job.error : ''}`));
    row.append(info, badge(job.status));
    const controls = el('div', 'job-controls');
    const control = (action, label) => {
      const button = el('button', 'button secondary compact', label); button.disabled = !canScan();
      button.addEventListener('click', async () => { button.disabled = true; try { await api(`/api/jobs/${encodeURIComponent(job.id)}/${action}`, {method:'POST'}); notice(`${label} requested. Snapshot completeness follows its durable scan state.`); await refresh(); } catch (e) { notice(e.message, true); button.disabled = !canScan(); } });
      controls.append(button);
    };
    if (job.status === 'running') { if (job.kind === 'scan') control('pause', 'Pause'); control('cancel', 'Cancel'); }
    if (job.kind === 'scan' && ['paused','interrupted','failed','cancelled'].includes(job.status)) control('resume', 'Resume');
    row.append(controls); target.append(row);
  }
}
async function analyze() { if (!state.snapshot || !canScan()) return; if (state.busy) return; state.busy = true; $('analyze-button').disabled = true; $('graph-analyze-button').disabled = true; try { const result = await api(endpoint('analyze'), { method: 'POST' }); notice(`Semantic analysis started · ${String(result.id || result.jobId || '').slice(0, 8)}. Source is analyzed without executing repository code.`); await switchView('activity'); } catch (e) { notice(e.message, true); } finally { state.busy = false; fillScope(); } }
async function exportSnapshot() { if (!state.snapshot) return; const button = $('export-button'); button.disabled = true; try { const format = $('export-format').value; const response = await api(endpoint(`export?format=${format}`), { raw: true }); const blob = await response.blob(); const url = URL.createObjectURL(blob); const link = el('a'); link.href = url; link.download = `filemapper-${state.snapshot.id}.${format === 'sarif' ? 'sarif.json' : format}`; document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); notice(`Export downloaded: ${format.toUpperCase()}.`); } catch (e) { notice(e.message, true); } finally { button.disabled = false; } }

document.querySelectorAll('#navigation button[data-view]').forEach(b => b.addEventListener('click', () => switchView(b.dataset.view)));
document.querySelectorAll('[data-goto]').forEach(b => b.addEventListener('click', () => switchView(b.dataset.goto)));
$('connection-button').addEventListener('click', showConnection);
$('new-scan-button').addEventListener('click', showScan);
$('cancel-connect').addEventListener('click', () => $('connect-dialog').close());
$('cancel-scan').addEventListener('click', () => $('scan-dialog').close());
$('dismiss-notice').addEventListener('click', () => $('notice').hidden = true);
$('close-inspector').addEventListener('click', closeInspector);
$('refresh-button').addEventListener('click', () => refresh());
$('activity-refresh').addEventListener('click', () => refresh());
$('analyze-button').addEventListener('click', analyze);
$('graph-analyze-button').addEventListener('click', analyze);
$('export-button').addEventListener('click', exportSnapshot);
$('metric-select').addEventListener('change', () => renderView());
$('snapshot-select').addEventListener('change', () => { state.snapshot = state.snapshots.find(s => s.id === $('snapshot-select').value) || null; resetSelection(); fillScope(); writeRoute('push'); renderView(); });
$('connect-form').addEventListener('submit', async e => { e.preventDefault(); const previous = state.token; state.token = $('access-token').value.trim(); const submit = e.submitter; submit.disabled = true; try { await api('/api/snapshots'); sessionStorage.setItem('filemapper.token', state.token); connected(true); $('connect-dialog').close(); notice('Connected to your local workspace.'); await refresh(); } catch (error) { state.token = previous; $('connect-error').textContent = error.message; } finally { submit.disabled = false; } });
$('scan-form').addEventListener('submit', async e => { e.preventDefault(); $('scan-submit').disabled = true; $('scan-error').textContent = ''; try { const result = await api('/api/scans', { method: 'POST', body: { root: $('scan-root').value.trim(), hashFiles: $('scan-hash').checked, reparsePolicy:$('scan-reparse').value, excludes:$('scan-excludes').value.split(',').map(s => s.trim()).filter(Boolean) } }); $('scan-dialog').close(); notice(`Mapping started · ${String(result.snapshotId || result.jobId).slice(0, 8)}. You can follow progress in Scan activity.`); await refresh(false); await switchView('activity'); } catch (error) { $('scan-error').textContent = error.message; } finally { $('scan-submit').disabled = false; } });
$('search-form').addEventListener('submit', e => { e.preventDefault(); state.search = $('search-input').value.trim(); state.offset = 0; renderView(); });
$('root-button').addEventListener('click', () => { if (state.snapshot) navigateFolder(state.snapshot.root); });
$('up-button').addEventListener('click', () => { const path = state.parent.replace(/[\\/]+$/, ''); const index = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/')); if (index >= 0) { const parent = path.slice(0, index) + (index === 2 && path[1] === ':' ? '\\' : ''); navigateFolder(parent.length < state.snapshot.root.length ? state.snapshot.root : parent); } });
$('breakdown-select').addEventListener('change', () => { state.breakdownOffset = 0; renderView(); });
$('breakdown-prev').addEventListener('click', () => { state.breakdownOffset = Math.max(0, state.breakdownOffset - 5); renderView(); });
$('breakdown-next').addEventListener('click', () => { state.breakdownOffset += 5; renderView(); });
$('duplicate-prev').addEventListener('click', () => { state.duplicateOffset = Math.max(0,state.duplicateOffset-PAGE); renderView(); });
$('duplicate-next').addEventListener('click', () => { state.duplicateOffset += PAGE; renderView(); });
$('file-prev').addEventListener('click', () => { state.offset = Math.max(0, state.offset - PAGE); renderView(); });
$('file-next').addEventListener('click', () => { state.offset += PAGE; renderView(); });
$('node-search-form').addEventListener('submit', e => { e.preventDefault(); state.nodeOffset = 0; renderView(); });
$('node-kind').addEventListener('change', () => { state.nodeOffset = 0; renderView(); });
$('node-prev').addEventListener('click', () => { state.nodeOffset = Math.max(0, state.nodeOffset - PAGE); renderView(); });
$('node-next').addEventListener('click', () => { state.nodeOffset += PAGE; renderView(); });
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeInspector(); });
let resizeTimer; window.addEventListener('resize', () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(() => { if (state.view === 'overview' && state.rank.length) renderTreemap(); }, 150); });
setInterval(async () => { if (!state.connected || state.polling || document.hidden) return; state.polling = true; try { const rows = await api('/api/jobs'); const jobs = rows.items || rows; if (state.view === 'activity') renderJobs(jobs); const snapshots = await api('/api/snapshots'); const old = state.snapshot; state.snapshots = snapshots; const current = snapshots.find(s => s.id === old?.id); if (current) { const changed = current.entryCount !== old.entryCount || current.status !== old.status || current.errorCount !== old.errorCount; state.snapshot = current; fillSnapshotSelect(); fillScope(); if (changed && !/running|scanning|pending/i.test(current.status)) { notice(`Snapshot ${current.id.slice(0, 8)} is ${current.status} · ${current.completeness}.`); await renderView(); } } } catch (e) { notice(e.message, true); } finally { state.polling = false; } }, 5000);

function initializeLiveTiles() {
  const definitions = [
    ['logicalBytes', 'Logical size counts distinct file identities. Directory and volume overhead are outside this measurement.'],
    ['allocatedBytes', 'Measured allocation for file identities. A missing measurement stays unknown; this is not total volume usage.'],
    ['fileCount', 'File count and location count answer different questions. The same identity can have more than one path.'],
    ['errorCount', 'Diagnostics include collection and semantic analysis messages. A diagnostic count is not a count of verified defects.']
  ];
  document.querySelectorAll('#metrics .metric-card').forEach((card, index) => {
    const [field, explanation] = definitions[index];
    const inner = el('div', 'live-tile-inner');
    const front = el('div', 'live-tile-face');
    while (card.firstChild) front.append(card.firstChild);
    const back = el('div', 'live-tile-face live-tile-back');
    back.append(el('strong', '', `snapshot.${field}`), el('p', '', explanation));
    back.setAttribute('aria-hidden', 'true'); back.inert = true;
    inner.append(front, back);
    const flip = el('button', 'tile-flip');
    const label = front.querySelector('.metric-title').textContent.trim();
    flip.append(el('span', '', 'FLIP TO EVIDENCE'), el('span', '', '↻'));
    flip.setAttribute('aria-label', `Flip ${label} tile`); flip.setAttribute('aria-pressed', 'false');
    flip.addEventListener('click', () => {
      const active = card.classList.toggle('is-flipped');
      flip.setAttribute('aria-pressed', String(active));
      flip.firstElementChild.textContent = active ? 'BACK TO METRIC' : 'FLIP TO EVIDENCE';
      front.setAttribute('aria-hidden', String(active)); front.inert = active;
      back.setAttribute('aria-hidden', String(!active)); back.inert = !active;
    });
    card.append(inner, flip);
  });
}
initializeLiveTiles();
window.addEventListener('popstate', () => {
  const route = readRoute();
  if (route.invalidView) notice('This view is unavailable. Showing the Spatial atlas.');
  const previous = state.snapshot?.id;
  state.snapshot = route.snapshotId ? state.snapshots.find(s => s.id === route.snapshotId) || null : state.snapshots[0] || null;
  if (previous !== state.snapshot?.id) resetSelection();
  if (route.snapshotId && state.connected && !state.snapshot) notice('The snapshot in this link is unavailable in this workspace. Choose an available snapshot to continue.', true);
  fillSnapshotSelect(); fillScope(); switchView(route.view, false);
});
const initialRoute = readRoute();
if (initialRoute.invalidView) notice('This view is unavailable. Showing the Spatial atlas.');
switchView(initialRoute.view, false);
if (state.token) refresh(); else { void refreshBuild(); renderDisconnected(); fillScope(); }
