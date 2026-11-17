(function (global) {
  'use strict';
  const instances = new WeakMap(), PAGE = 24;
  const node = (tag, cls, text) => { const n = document.createElement(tag); if (cls) n.className = cls; if (text !== undefined) n.textContent = String(text); return n; };
  const fmt = n => Number(n || 0).toLocaleString(), enc = encodeURIComponent;
  const short = value => String(value || '').slice(0, 12);
  function destroy(host) { instances.get(host)?.destroy(); }
  async function render(host, options) {
    destroy(host); host.replaceChildren();
    let disposed = false, generation = 0, captureGeneration = 0, connectionGeneration = 0, connectionState = null, scene = null, offset = 0, captureOffset = 0, rows = [], termRows = [], selectedTerm = null, occurrenceOffset = 0, occurrenceGeneration = 0;
    const aborters = new Set(), cleanup = [];
    const on = (target, event, fn) => { target.addEventListener(event, fn); cleanup.push(() => target.removeEventListener(event, fn)); };
    const request = async path => { const controller = new AbortController(); aborters.add(controller); try { return await options.api(path, { signal: controller.signal }); } finally { aborters.delete(controller); } };
    const root = node('div', 'sh-root'), intro = node('div', 'sh-intro');
    intro.append(node('span', 'sh-tag', 'SOURCE / REVISION / WORD / SHAPE'), node('h2', '', 'Words with a place in time.'),
      node('p', '', 'Each word is anchored to retained source bytes. Stack captured repositories, rotate the view, and follow an exact term back to every indexed occurrence.'));
    const status = node('p', 'sh-status', 'Loading captured source…'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
    const connection = node('section', 'sh-connection'), connectionCopy = node('div', 'sh-connection-copy'), connectionTitle = node('h3', '', 'Source connection'), connectionDetail = node('p', 'sh-muted', 'Reading the active source selection…'), connectionHash = node('code', 'sh-connection-hash'), connectionNotice = node('p', 'sh-connection-notice'), refresh = node('button', 'button secondary', 'Refresh source data'); refresh.type = 'button'; connection.setAttribute('aria-label', 'Active source connection'); connectionNotice.setAttribute('role', 'status');
    connectionCopy.append(node('span', 'sh-tag', 'ACTIVE SOURCE'), connectionTitle, connectionDetail, connectionHash, connectionNotice); connection.append(connectionCopy, refresh);
    const connectionPaths = node('details', 'sh-connect-help'), workspacePathLabel = node('p', '', 'Source corpus'), workspacePath = node('code'), indexPath = node('code'); connectionPaths.append(node('summary', '', 'Connection paths'), workspacePathLabel, workspacePath, node('p', '', 'Query index'), indexPath); connectionPaths.hidden = true; connectionCopy.append(connectionPaths);
    const connectHelp = node('details', 'sh-connect-help'); connectHelp.append(node('summary', '', 'Connect a retained source corpus'), node('p', '', 'Save a corpus selection with the native CLI, then restart FileMapper to activate it. A compact selection is pinned to one verified snapshot.'), node('code', '', 'filemapper harvest connect --service-data "<FileMapper data directory>" --workspace "<source corpus>" --compact-index "<sealed snapshot.db>"'), node('p', '', 'Omit --compact-index to read the ingestion index. Refresh updates the displayed data within the active selection.')); connectionCopy.append(connectHelp);
    const controls = node('form', 'sh-controls'), layers = [];
    for (let i = 0; i < 3; i++) { const label = node('label', 'sh-field'), select = node('select'); select.setAttribute('aria-label', 'Source capture layer ' + (i + 1)); label.append(node('span', '', 'LAYER ' + (i + 1)), select); controls.append(label); layers.push(select); on(select, 'change', () => { offset = 0; void loadTerms(); }); }
    const searchLabel = node('label', 'sh-field sh-search'), search = node('input'); search.type = 'search'; search.maxLength = 512; search.placeholder = 'Search source words…'; searchLabel.append(node('span', '', 'WORD PATTERN'), search);
    const apply = node('button', 'button primary', 'Map words'); apply.type = 'submit'; controls.append(searchLabel, apply);
    const metrics = node('div', 'sh-metrics'), stage = node('div', 'sh-stage'), pages = node('div', 'sh-pages'), previous = node('button', 'button secondary', 'Previous words'), next = node('button', 'button secondary', 'Next words'), pageInfo = node('span');
    pages.append(previous, pageInfo, next);
    const capturesPage = node('div', 'sh-pages'), capturesPrev = node('button', 'text-button', 'Previous captures'), capturesNext = node('button', 'text-button', 'Next captures'), capturesInfo = node('span'); capturesPage.append(capturesPrev, capturesInfo, capturesNext);
    const evidence = node('section', 'sh-evidence'), evidenceHeading = node('h3', '', 'Select a word to inspect its source'), evidenceNote = node('p', 'sh-muted', 'Exact repository path, byte interval, line, column, and source hash.'), occurrenceList = node('div', 'sh-occurrences'), occurrenceNav = node('div', 'sh-pages'), occurrencePrev = node('button', 'button secondary', 'Previous occurrences'), occurrenceNext = node('button', 'button secondary', 'Next occurrences'), occurrenceInfo = node('span'); occurrenceNav.append(occurrencePrev, occurrenceInfo, occurrenceNext); occurrenceNav.hidden = true; evidence.append(evidenceHeading, evidenceNote, occurrenceList, occurrenceNav);
    const note = node('details', 'sh-scope'); note.append(node('summary', '', 'What this source view establishes'), node('p', '', 'Layers are selected captures with actual capture timestamps. Vertical spacing is ordinal, not elapsed time. Links mean exact normalized-term equality across captured source documents. Shapes and colors encode lexical features and can collide. They do not establish semantic equivalence, translations, runtime behavior, or learning improvement. Only the displayed page is rendered; search and paging query the full indexed vocabulary.'));
    root.append(intro, connection, status, controls, capturesPage, metrics, stage, pages, evidence, note); host.append(root);
    const instance = { destroy() { disposed = true; generation++; captureGeneration++; connectionGeneration++; occurrenceGeneration++; for (const c of aborters) c.abort(); scene?.destroy(); cleanup.forEach(fn => fn()); root.remove(); instances.delete(host); }, getState: () => ({ connection: connectionState, layers: layers.map(n => n.value), offset, selectedTerm, camera: scene?.getState().camera }) }; instances.set(host, instance);
    function message(text, error = false) { if (!disposed) { status.textContent = text; status.classList.toggle('error', error); } }
    async function loadConnection() {
      const current = ++connectionGeneration;
      try {
        const active = await request('/api/harvest/connection'); if (disposed || current !== connectionGeneration) return; connectionState = active;
        connectionTitle.textContent = active.workspaceName || 'Source corpus';
        const mode = active.mode === 'compact' ? 'Pinned compact snapshot' : 'Ingestion index';
        const origin = active.origin === 'saved' ? 'saved connection' : active.origin === 'explicit' ? 'startup override' : 'default workspace';
        connectionDetail.textContent = `${mode} · ${origin} · ${active.indexAvailable ? active.indexName : 'index unavailable'}`;
        connectionHash.textContent = active.compactSha256 ? 'SHA-256 ' + active.compactSha256 : active.connectionId ? 'Connection ' + active.connectionId : ''; connectionHash.hidden = !active.compactSha256 && !active.connectionId;
        workspacePath.textContent = active.workspacePath || ''; indexPath.textContent = active.indexPath || ''; connectionPaths.hidden = !active.workspacePath && !active.indexPath;
        workspacePathLabel.textContent = active.workspaceAvailable === false ? 'Source corpus unavailable; queries use retained index records' : 'Source corpus';
        connectionNotice.classList.toggle('pending', active.restartRequired || active.savedState === 'invalid');
        connectionNotice.textContent = active.savedIgnoredByOverride ? 'Startup arguments override the saved selection. Remove the override and restart to use the saved connection.'
          : active.savedState === 'invalid' ? 'The saved selection is invalid. This session keeps its active source; correct the saved selection before restarting.'
          : active.restartRequired && active.savedState === 'absent' ? 'The saved selection was removed. Restart FileMapper to use the default source workspace.'
          : active.restartRequired ? 'A different source selection is saved. Restart FileMapper to activate it.'
          : active.configurationChanged && active.savedMatchesActive ? 'The saved configuration points to this active source. No data-source change is pending.'
          : active.connectedAt ? 'Connected ' + new Date(active.connectedAt).toLocaleString() + ' · active for this service session.'
          : 'Source selection stays fixed for this service session.';
      } catch (error) {
        if (disposed || current !== connectionGeneration || error.name === 'AbortError') return;
        connectionState = null; connectionTitle.textContent = 'Source connection'; connectionDetail.textContent = 'Connection details are unavailable from this service.'; connectionHash.hidden = true; connectionPaths.hidden = true; connectionNotice.textContent = 'Source queries below report their own availability.'; connectionNotice.classList.add('pending');
      }
    }
    async function loadCaptures() {
      const current = ++captureGeneration; refresh.disabled = true; void loadConnection();
      try {
        const page = await request('/api/harvest/captures?offset=' + captureOffset + '&limit=100'); if (disposed || current !== captureGeneration) return; rows = page.items;
        for (let i = 0; i < layers.length; i++) { const old = layers[i].value; layers[i].replaceChildren(); layers[i].append(new Option(i ? 'No additional layer' : 'Choose a capture', '')); for (const row of rows) layers[i].append(new Option(row.displayName + ' · ' + short(row.commit) + ' · ' + (row.extractionHash ? 'indexed' : 'bytes retained'), row.captureId)); if (rows.some(r => r.captureId === old)) layers[i].value = old; }
        if (!layers[0].value) layers[0].value = rows.find(r => r.extractionHash)?.captureId || rows[0]?.captureId || '';
        capturesInfo.textContent = page.total ? `${captureOffset + 1}–${captureOffset + rows.length} of ${fmt(page.total)} captures` : 'No source captures yet'; capturesPrev.disabled = captureOffset === 0; capturesNext.disabled = captureOffset + rows.length >= page.total;
        if (!rows.length) { generation++; occurrenceGeneration++; selectedTerm = null; termRows = []; scene?.destroy(); scene = null; metrics.replaceChildren(); occurrenceList.replaceChildren(); occurrenceNav.hidden = true; occurrenceInfo.textContent = ''; pageInfo.textContent = ''; evidenceHeading.textContent = 'Select a word to inspect its source'; evidenceNote.textContent = 'Exact repository path, byte interval, line, column, and source hash.'; message('No pinned source corpus is indexed in this workspace yet.'); stage.replaceChildren(node('p', 'sh-empty', 'Connect a retained source corpus above, or use the native harvest capture and extract commands to create source evidence.')); previous.disabled = next.disabled = true; apply.disabled = false; return; }
        await loadTerms();
      } catch (e) { if (!disposed && current === captureGeneration && e.name !== 'AbortError') message(e.message, true); }
      finally { if (!disposed && current === captureGeneration) refresh.disabled = false; }
    }
    async function loadTerms() {
      const current = ++generation; occurrenceGeneration++; selectedTerm = null; occurrenceNav.hidden = true; occurrenceList.replaceChildren(); occurrenceInfo.textContent = ''; pageInfo.textContent = ''; evidenceHeading.textContent = 'Select a word to inspect its source'; evidenceNote.textContent = 'Exact repository path, byte interval, line, column, and source hash.'; scene?.destroy(); scene = null; termRows = []; metrics.replaceChildren(); stage.replaceChildren(); previous.disabled = next.disabled = true;
      const ids = [...new Set(layers.map(n => n.value).filter(Boolean))]; if (!ids.length) { apply.disabled = false; message('Choose a source capture.'); return; }
      apply.disabled = true; message('Reading retained source vocabulary…');
      try {
        const data = await Promise.all(ids.map(async id => ({ capture: rows.find(r => r.captureId === id), counts: await request('/api/harvest/captures/' + enc(id) + '/counts'), terms: await request('/api/harvest/captures/' + enc(id) + '/terms?offset=' + offset + '&limit=' + PAGE + '&search=' + enc(search.value)) })));
        if (disposed || current !== generation) return;
        metrics.replaceChildren(); termRows = []; const objects = [], edges = [], byTerm = new Map();
        for (let layer = 0; layer < data.length; layer++) {
          const { capture, counts, terms } = data[layer], card = node('article', 'sh-metric');
          card.append(node('span', 'sh-tag', 'LAYER ' + (layer + 1)), node('h3', '', capture.displayName), node('strong', '', fmt(counts.wordOccurrences) + ' word occurrences'), node('p', '', `${fmt(counts.processedDocuments)} / ${fmt(counts.capturedDocuments)} captured documents processed · ${fmt(counts.distinctTerms)} terms`), node('p', 'sh-muted', `${fmt(counts.lexicallyCompleteDocuments)} lexically complete · ${fmt(counts.incompleteLexicalDocuments)} partial · ${fmt(counts.unavailableEntries)} unavailable`), node('small', '', (capture.startedAt ? new Date(capture.startedAt).toLocaleString() : 'Capture time unavailable') + ' · ' + capture.state)); metrics.append(card);
          for (const word of terms.items) {
            const somId = word.somId + ':' + capture.captureId, evidenceId = capture.captureId + ':' + word.source.termId;
            const item = { ...word, capture, layer, instanceId: somId }; termRows.push(item);
            const object = { somId, label: word.source.term, arm: capture.displayName, status: counts.extractionSealed ? 'Sealed extraction' : 'Extraction in progress', some_type: 'source-word', shape_type: word.shape_type, shape_color: word.shape_color, somshape: { shape: word.shape_type, axis: layer % 3, rotation: 0 }, layerIndex: layer, layer: layer + 1, observedAt: capture.startedAt, sessionId: capture.captureId, schemaPath: capture.displayName + '@' + capture.commit, value: { amount: word.source.occurrences, unit: 'occurrences' }, evidenceRefs: [{ id: evidenceId, field: 'source.termId', value: word.source.termId }], valueSource: { evidenceRef: evidenceId, field: 'source.occurrences', value: { amount: word.source.occurrences, unit: 'occurrences' } } };
            objects.push(object); for (const other of byTerm.get(word.source.termId) || []) edges.push({ from: other, to: somId, kind: 'equal-normalized-term', label: 'Equal normalized term; distinct captures' }); if (!byTerm.has(word.source.termId)) byTerm.set(word.source.termId, []); byTerm.get(word.source.termId).push(somId);
          }
        }
        scene?.destroy(); stage.replaceChildren();
        scene = global.FileMapperSpatial.mount(stage, { objects, edges, layout: 'snapshot-layers', sampled: true, onSelect: object => { const selected = termRows.find(t => t.instanceId === object.somId); if (selected) { selectedTerm = selected; occurrenceOffset = 0; void loadOccurrences(); } } });
        stage.querySelector('.fms-heading h3').textContent = 'A vocabulary you can orbit.'; stage.querySelector('.fms-snapshot').textContent = data.length + ' pinned source layer' + (data.length === 1 ? '' : 's');
        pageInfo.textContent = data.map(d => d.capture.displayName + ': ' + fmt(d.terms.items.length) + ' of ' + fmt(d.terms.total)).join(' · '); previous.disabled = offset === 0; next.disabled = !data.some(d => offset + PAGE < d.terms.total);
        message(`${objects.length} word shapes loaded · ${edges.length} exact-term links across captures · page starts at ${offset + 1}.`);
      } catch (e) { if (!disposed && current === generation && e.name !== 'AbortError') message(e.message, true); }
      finally { if (!disposed && current === generation) apply.disabled = false; }
    }
    async function loadOccurrences() {
      if (!selectedTerm) return; const selected = selectedTerm, current = ++occurrenceGeneration;
      try {
        const page = await request('/api/harvest/captures/' + enc(selected.capture.captureId) + '/occurrences?term=' + enc(selected.source.termId) + '&offset=' + occurrenceOffset + '&limit=50'); if (disposed || current !== occurrenceGeneration) return;
        evidenceHeading.textContent = selected.source.term + ' · ' + selected.capture.displayName; evidenceNote.textContent = selected.source.termId + ' · signature ' + selected.signatureBits + ' · ' + selected.model; occurrenceList.replaceChildren();
        for (const row of page.items) { const card = node('article', 'sh-occurrence'), unit = row.unit; card.append(node('strong', '', row.repositoryPath), node('code', '', unit.text), node('p', '', `Line ${unit.startLine}, column ${unit.startColumn} (${unit.columnUnit}) · bytes [${unit.byteStart}, ${unit.byteStart + unit.byteLength})`), node('small', '', row.lexicalStatus + ' · ' + row.documentId), node('small', '', 'Source SHA-256 ' + row.sourceSha256)); occurrenceList.append(card); }
        occurrenceInfo.textContent = page.total ? `${occurrenceOffset + 1}–${occurrenceOffset + page.items.length} of ${fmt(page.total)}` : 'No indexed occurrences'; occurrenceNav.hidden = false; occurrencePrev.disabled = occurrenceOffset === 0; occurrenceNext.disabled = occurrenceOffset + page.items.length >= page.total;
      } catch (e) { if (!disposed && current === occurrenceGeneration && e.name !== 'AbortError') message(e.message, true); }
    }
    on(controls, 'submit', e => { e.preventDefault(); offset = 0; void loadTerms(); }); on(previous, 'click', () => { offset = Math.max(0, offset - PAGE); void loadTerms(); }); on(next, 'click', () => { offset += PAGE; void loadTerms(); });
    on(refresh, 'click', () => { void loadCaptures(); });
    on(capturesPrev, 'click', () => { captureOffset = Math.max(0, captureOffset - 100); void loadCaptures(); }); on(capturesNext, 'click', () => { captureOffset += 100; void loadCaptures(); });
    on(occurrencePrev, 'click', () => { occurrenceOffset = Math.max(0, occurrenceOffset - 50); void loadOccurrences(); }); on(occurrenceNext, 'click', () => { occurrenceOffset += 50; void loadOccurrences(); });
    await loadCaptures(); return instance;
  }
  global.FileMapperSourceHarvest = Object.freeze({ render, destroy, getState: host => instances.get(host)?.getState() });
})(window);
