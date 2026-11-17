(function (global) {
  'use strict';
  const instances = new WeakMap(), API = '/api/source-semantics', PAGE = 24, RELATED = 20;
  const enc = encodeURIComponent, shown = value => value == null ? 'Unknown' : String(value);
  const number = value => value == null ? 'Unknown' : Number(value).toLocaleString();
  const time = value => value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString() + ' · ' + value : 'Unknown / not recorded';
  const short = value => String(value || '').slice(0, 12);
  const make = (tag, cls, value) => { const item = document.createElement(tag); if (cls) item.className = cls; if (value !== undefined) item.textContent = String(value); return item; };
  const field = (label, control) => { const item = make('label', 'ss-field'); item.append(make('span', '', label), control); return item; };
  const bytes = value => value == null ? 'Unknown' : Number(value).toLocaleString() + ' bytes';
  const activeJob = job => ['running', 'cancelling'].includes(job.status);
  function raw(title, value) { const item = make('details', 'ss-raw'), text = JSON.stringify(value, null, 2) ?? 'null'; item.append(make('summary', '', title), make('pre', '', text.slice(0, 24000))); if (text.length > 24000) item.append(make('p', 'ss-note', 'Display limited to 24,000 characters. Download the sealed artifact for the complete record.')); return item; }
  function pairs(target, values) { const list = make('dl', 'ss-facts'); for (const [key, value] of values) list.append(make('dt', '', key), make('dd', '', shown(value))); target.append(list); }
  function message(target, value, error = false) { target.textContent = value; target.classList.toggle('ss-error', error); }
  function destroy(host) { instances.get(host)?.destroy(); }

  async function render(host, options) {
    destroy(host); host.replaceChildren();
    const canWrite = options.capabilities?.includes('scan') === true, root = make('div', 'ss-root');
    let disposed = false, scene = null, pollTimer = null, mutationBusy = false, connection = null, refreshing = false, recoveryDeferred = false;
    let captures = [], capture = null, analysis = null, selectedNode = null, currentJobs = [], lastJobSignature = null;
    let captureOffset = 0, documentOffset = 0, analysisOffset = 0, objectOffset = 0;
    let documentPage = null, analysisPage = null, objectPage = null;
    const analysisHistory = [], objectHistory = [];
    const selectedDocuments = new Map(), versions = new Map(), requests = new Map(), cleanup = [], urls = new Set();
    const initialRoute = new URL(global.location.href).searchParams;
    let wantedCapture = initialRoute.get('sourceCapture') || '', wantedAnalysis = initialRoute.get('sourceAnalysis') || '', wantedNode = initialRoute.get('sourceNode') || '';
    const on = (target, event, fn) => { target.addEventListener(event, fn); cleanup.push(() => target.removeEventListener(event, fn)); };
    const button = (label, fn, cls = 'button secondary') => { const item = make('button', cls, label); item.type = 'button'; item.addEventListener('click', fn); return item; };
    const begin = key => { requests.get(key)?.abort(); const version = (versions.get(key) || 0) + 1; versions.set(key, version); return version; };
    const current = (key, version) => !disposed && versions.get(key) === version;
    function abortRelated() { for (const key of [...requests.keys()]) if (key.startsWith('related-') || key === 'scene-edges') { requests.get(key)?.abort(); requests.delete(key); } }
    async function request(key, path, settings = {}, consume) {
      requests.get(key)?.abort(); const controller = new AbortController(); requests.set(key, controller);
      try { const result = await options.api(path, { ...settings, signal: controller.signal }); return consume ? await consume(result, controller.signal) : result; }
      finally { if (requests.get(key) === controller) requests.delete(key); }
    }
    function route() { if (disposed) return; const url = new URL(global.location.href); for (const [key, value] of [['sourceCapture', capture?.captureId || wantedCapture], ['sourceAnalysis', analysis?.analysisId || wantedAnalysis], ['sourceNode', selectedNode || wantedNode]]) { if (value) url.searchParams.set(key, value); else url.searchParams.delete(key); } global.history.replaceState(null, '', url); }
    const status = make('p', 'ss-status', 'Loading retained-source workspace…'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
    const intro = make('section', 'ss-intro'), emblem = make('div', 'ss-nine'); emblem.setAttribute('aria-hidden', 'true'); for (let i = 0; i < 9; i++) emblem.append(make('i'));
    const introText = make('div'); introText.append(make('p', 'ss-kicker', 'RETAINED SOURCE / NATIVE EVIDENCE'), make('h2', '', 'Read the source. Follow the shape.'), make('p', '', 'Analyze captured C# and PowerShell bytes, orbit native objects, and inspect each relationship at its recorded source span.')); intro.append(introText, emblem);

    const connectionPanel = make('section', 'ss-panel ss-connection'), connectionBody = make('div'), connectionTitle = make('h3', '', 'Active source selection'), connectionText = make('p', 'ss-note'), connectionRaw = make('div');
    const refresh = button('Refresh source workspace', () => { void refreshAll(); }); connectionBody.append(make('p', 'ss-kicker', 'FIXED FOR THIS SERVICE SESSION'), connectionTitle, connectionText, connectionRaw); connectionPanel.append(connectionBody, refresh);

    const inputPanel = make('details', 'ss-panel ss-input'); inputPanel.open = true; inputPanel.append(make('summary', '', 'Analyze retained documents'));
    const captureSelect = make('select'), captureFacts = make('div'), captureNav = make('div', 'ss-pagination'), captureInfo = make('span');
    const capturePrev = button('Previous captures', () => { captureOffset = Math.max(0, captureOffset - 100); void loadCaptures(false); }), captureNext = button('Next captures', () => { captureOffset += 100; void loadCaptures(false); }); captureNav.append(capturePrev, captureInfo, captureNext);
    const scope = make('select'); scope.append(new Option('Whole capture · bounded supported inputs', 'capture'), new Option('Only explicitly selected documents', 'selection'));
    const sourceControls = make('div', 'ss-form-grid'); sourceControls.append(field('Capture from active source', captureSelect), field('Analysis scope', scope));
    const documentForm = make('form', 'ss-toolbar'), documentSearch = make('input'); documentSearch.type = 'search'; documentSearch.maxLength = 512; documentSearch.placeholder = 'Repository path…';
    const supportedOnly = make('input'); supportedOnly.type = 'checkbox'; supportedOnly.checked = true;
    const docSearchButton = make('button', 'button secondary', 'Find documents'); docSearchButton.type = 'submit'; documentForm.append(field('Find captured documents', documentSearch), field('C# / PowerShell candidates only', supportedOnly), docSearchButton);
    const documents = make('div', 'ss-documents'), documentNav = make('div', 'ss-pagination'), documentInfo = make('span'), selectionInfo = make('p', 'ss-note');
    const documentPrev = button('Previous documents', () => { documentOffset = Math.max(0, documentOffset - PAGE); void loadDocuments(); }), documentNext = button('Next documents', () => { documentOffset += PAGE; void loadDocuments(); }); documentNav.append(documentPrev, documentInfo, documentNext);
    const clearDocuments = button('Clear selected documents', () => { selectedDocuments.clear(); renderDocuments(); });
    const limits = make('details', 'ss-limits'); limits.append(make('summary', '', 'Analysis bounds'), make('p', 'ss-note', 'These limits bound admitted input, output, graph records and lexical joins. They are not a wall-time or process-memory guarantee. Unsupported, rejected and capped inputs remain in the coverage denominator.'));
    const limitFields = {}, limitGrid = make('div', 'ss-limit-grid');
    for (const [key, label, value, min, max] of [
      ['maximumDocuments', 'Documents', 256, 1, 2000], ['maximumDocumentBytes', 'Bytes per document', 2097152, 1, 16777216], ['maximumTotalBytes', 'Total input bytes', 33554432, 1, 67108864], ['maximumOutputBytes', 'Sealed output bytes', 536870912, 1024, 4294967296], ['maximumLexicalLinks', 'Lexical links', 500000, 0, 5000000], ['maximumJoinWordsPerDocument', 'Join words per document', 250000, 1, 1000000], ['maximumGraphRecords', 'Graph records', 500000, 1, 1000000]
    ]) { const input = make('input'); input.type = 'number'; input.step = '1'; input.min = String(min); input.max = String(max); input.value = String(value); input.required = true; input.disabled = !canWrite; limitFields[key] = input; limitGrid.append(field(label, input)); } limits.append(limitGrid);
    const start = button('Start retained-source analysis', () => { void startAnalysis(); }, 'button primary'), mutationNote = make('p', 'ss-note', canWrite ? 'A new durable job parses retained bytes without executing repository code. Existing analyses remain available.' : 'Read-only credential. Connect with a scanner or operator credential to start, import, or cancel source analysis.');
    const startRow = make('div', 'ss-actions'); startRow.append(start, clearDocuments); inputPanel.append(sourceControls, captureNav, captureFacts, documentForm, documents, documentNav, selectionInfo, limits, startRow, mutationNote);

    const imports = make('details', 'ss-panel'); imports.append(make('summary', '', 'Import an existing sealed analysis'));
    const importForm = make('form', 'ss-form-grid'), importDirectory = make('input'), importId = make('input'); importDirectory.placeholder = 'Absolute local analysis directory'; importDirectory.required = true; importDirectory.maxLength = 4096; importId.placeholder = '64 lowercase hexadecimal characters'; importId.pattern = '[a-f0-9]{64}'; importId.maxLength = 64; importId.required = true; importId.spellcheck = false; importDirectory.spellcheck = false;
    const importButton = make('button', 'button secondary', 'Verify and import sealed analysis'); importButton.type = 'submit'; importForm.append(field('Existing sealed analysis directory', importDirectory), field('Expected manifest SHA-256 / analysis ID', importId), importButton); imports.append(make('p', 'ss-note', 'Import verifies the supplied identity and artifacts, then retains a managed copy. The original analysis time stays separate from the import time.'), importForm);

    const jobsPanel = make('details', 'ss-panel'), jobsHeading = make('div', 'ss-section-heading'), jobsStatus = make('p', 'ss-note'), jobsList = make('div', 'ss-jobs'), jobsSummary = make('summary', '', 'Source analysis jobs'); jobsHeading.append(make('h3', '', 'Durable source job history'), button('Refresh jobs', () => { void loadJobs(); })); jobsPanel.append(jobsSummary, jobsHeading, jobsStatus, jobsList);

    const browser = make('section', 'ss-panel ss-browser'), analysisSelect = make('select'), analysisNav = make('div', 'ss-pagination'), analysisInfo = make('span');
    const analysisPrev = button('Previous analyses', () => { analysisOffset = analysisHistory.pop() ?? 0; void loadAnalyses(false); }), analysisNext = button('Next analyses', () => { analysisHistory.push(analysisOffset); analysisOffset = analysisPage?.nextOffset ?? analysisOffset + PAGE; void loadAnalyses(false); }); analysisNav.append(analysisPrev, analysisInfo, analysisNext);
    const analysisHeader = make('div', 'ss-section-heading'); analysisHeader.append(make('h3', '', 'Persisted source analyses'), field('Sealed analysis', analysisSelect));
    const manifestPanel = make('div', 'ss-manifest'), coveragePanel = make('div', 'ss-coverage'), artifactPanel = make('details', 'ss-artifacts'); artifactPanel.append(make('summary', '', 'Verified artifacts and authenticated downloads'));
    const objectForm = make('form', 'ss-toolbar'), objectSearch = make('input'), kind = make('input'), browseMode = make('select'); objectSearch.type = 'search'; objectSearch.maxLength = 512; objectSearch.placeholder = 'Native label, name or identity'; kind.type = 'text'; kind.maxLength = 128; kind.placeholder = 'All native kinds'; browseMode.append(new Option('Native SOM objects · 3D', 'objects'), new Option('Native nodes · index', 'nodes'));
    const objectApply = make('button', 'button primary', 'Find native records'); objectApply.type = 'submit'; objectForm.append(field('Record view', browseMode), field('Search persisted records', objectSearch), field('Exact native kind', kind), objectApply);
    const pageStatus = make('p', 'ss-note'), stage = make('div', 'ss-stage'), nativeList = make('div', 'ss-native-list'), objectNav = make('div', 'ss-pagination'), objectInfo = make('span');
    const objectPrevious = button('Previous records', () => { objectOffset = objectHistory.pop() ?? 0; void loadObjects(); }), objectNext = button('Next records', () => { objectHistory.push(objectOffset); objectOffset = objectPage?.nextOffset ?? objectOffset + PAGE; void loadObjects(); }); objectNav.append(objectPrevious, objectInfo, objectNext);
    const inspector = make('section', 'ss-inspector'); inspector.setAttribute('aria-label', 'Selected native node evidence'); inspector.append(make('h3', '', 'Choose a native object or node'), make('p', 'ss-note', 'Inspect exact spans, direct subject observations, lexical containment and adjacent edges.'));
    const allEvidence = make('details', 'ss-all-evidence'), allEvidenceBody = make('div'); allEvidence.append(make('summary', '', 'All observations, including context-only evidence'), make('p', 'ss-note', 'This collection includes observations that have no native node subject. It is independent of the selected node.'), allEvidenceBody);
    browser.append(analysisHeader, analysisNav, manifestPanel, coveragePanel, artifactPanel, objectForm, pageStatus, stage, nativeList, objectNav, inspector, allEvidence);
    const scopeNote = make('details', 'ss-panel'); scopeNote.append(make('summary', '', 'Meaning and limits of this view'), make('p', 'ss-note', 'Native SOM geometry and color encode categories. Camera position, card order and scene spacing are presentation only. Nine-bit lexical signatures can collide; matching shapes never merge native identities. Lexical containment is not resolved symbol binding. Analysis remains partial static evidence; capture, extraction, original-source, analysis and import times retain their own meaning.')); root.append(intro, connectionPanel, status, inputPanel, imports, jobsPanel, browser, scopeNote); host.append(root);

    const instance = { destroy() { if (disposed) return; disposed = true; global.clearTimeout(pollTimer); for (const c of requests.values()) c.abort(); requests.clear(); scene?.destroy(); cleanup.forEach(fn => fn()); urls.forEach(url => URL.revokeObjectURL(url)); urls.clear(); root.remove(); if (instances.get(host) === instance) instances.delete(host); }, getState: () => ({ captureId: capture?.captureId, analysisId: analysis?.analysisId, nodeId: selectedNode, documentSelection: [...selectedDocuments.keys()], objectOffset, camera: scene?.getState().camera }) };
    instances.set(host, instance);
    function fail(error, target = status) { if (!disposed && error.name !== 'AbortError') message(target, error.message, true); }
    function updateMutations() { start.disabled = !canWrite || mutationBusy || recoveryDeferred || !capture?.extractionHash || connection?.workspaceAvailable === false || (scope.value === 'selection' && !selectedDocuments.size); importButton.disabled = importDirectory.disabled = importId.disabled = !canWrite || mutationBusy || recoveryDeferred; for (const input of Object.values(limitFields)) input.disabled = !canWrite || mutationBusy; }
    function describePage(page, name) { const count = page?.items?.length || 0; return `${count ? page.offset + 1 : 0}–${page.offset + count} of ${number(page.total)} ${name}${page.byteLimitReached ? ' · response byte limit reached' : ''}`; }
    function setPaging(prev, next, info, page, name, legacy = false) { info.textContent = describePage(page, name); prev.disabled = page.offset === 0; next.disabled = legacy ? page.offset + page.items.length >= page.total : page.nextOffset == null; }
    function analysisLabel(row) {
      const source = captures.find(item => item.captureId === row.manifest.captureId);
      return `${source?.displayName || 'Capture ' + short(row.manifest.captureId)} · commit ${short(row.manifest.commit)} · analysis ${short(row.analysisId)}`;
    }
    function refreshAnalysisLabels() {
      // Relabel existing options after the independent capture request; keep the selected value and focus.
      for (const option of analysisSelect.options) {
        const row = analysisPage?.items.find(item => item.analysisId === option.value) || (analysis?.analysisId === option.value ? analysis : null);
        if (row) option.textContent = analysisLabel(row);
      }
    }

    async function loadConnection() {
      const version = begin('connection');
      try { const value = await request('connection', '/api/harvest/connection'); if (!current('connection', version)) return; connection = value; connectionTitle.textContent = value.workspaceName || 'Active source selection'; connectionText.textContent = `${value.mode === 'compact' ? 'Pinned compact index' : 'Ingestion index'} · ${value.indexAvailable ? 'index available' : 'index unavailable'} · ${value.workspaceAvailable ? 'raw workspace available' : 'raw workspace unavailable; new analysis cannot read retained bytes'}${value.restartRequired ? ' · saved selection differs; restart required to activate it' : ''}`; connectionRaw.replaceChildren(raw('Active source identity and paths', value)); updateMutations(); }
      catch (error) { if (current('connection', version)) { connection = null; message(connectionText, error.message, true); } }
    }
    async function loadCaptures(choose = true) {
      const version = begin('captures'); capturePrev.disabled = captureNext.disabled = true;
      try { const page = await request('captures', '/api/harvest/captures?' + new URLSearchParams({ offset: captureOffset, limit: 100 })); if (!current('captures', version)) return; captures = page.items; refreshAnalysisLabels(); const prior = capture; captureSelect.replaceChildren(new Option('Choose a capture', '')); if (prior && !captures.some(row => row.captureId === prior.captureId)) captureSelect.append(new Option(`${prior.displayName} · selected capture`, prior.captureId)); for (const row of captures) captureSelect.append(new Option(`${row.displayName} · ${short(row.commit)} · ${row.extractionHash ? 'lexical extraction sealed' : 'no sealed extraction'}`, row.captureId)); captureSelect.value = prior?.captureId || ''; setPaging(capturePrev, captureNext, captureInfo, page, 'captures', true); if (!capture && choose) { const value = captures.find(row => row.captureId === wantedCapture) || (!wantedCapture ? captures.find(row => row.extractionHash) || captures[0] : null); if (value) await selectCapture(value); else if (wantedCapture) message(captureInfo, 'The linked capture is not on this page. Browse captures to locate it; no replacement source was selected.', true); } }
      catch (error) { if (current('captures', version)) fail(error); }
    }
    async function selectCapture(value) {
      capture = value; wantedCapture = ''; captureSelect.value = value?.captureId || ''; selectedDocuments.clear(); documentOffset = 0; captureFacts.replaceChildren(); documents.replaceChildren(); if (value) pairs(captureFacts, [['Capture ID', value.captureId], ['Repository / revision', `${value.displayName} / ${value.commit}`], ['Sealed lexical extraction ID', value.extractionHash], ['Capture started', time(value.startedAt)], ['Capture finished', time(value.finishedAt)], ['Capture state', value.state]]); route(); updateMutations(); await loadDocuments();
    }
    async function loadDocuments() {
      const version = begin('documents'), id = capture?.captureId; documentPrev.disabled = documentNext.disabled = true; if (!id) { documentPage = null; renderDocuments(); return; }
      try { const page = await request('documents', '/api/harvest/captures/' + enc(id) + '/documents?' + new URLSearchParams({ offset: documentOffset, limit: PAGE, search: documentSearch.value.trim(), supportedOnly: supportedOnly.checked })); if (!current('documents', version) || capture?.captureId !== id) return; documentPage = page; renderDocuments(); }
      catch (error) { if (current('documents', version)) fail(error); }
    }
    function renderDocuments() {
      documents.replaceChildren(); if (!documentPage?.items.length) documents.append(make('p', 'ss-note', capture ? 'No captured documents match this page and filter.' : 'Choose an active capture to inspect retained documents.'));
      for (const row of documentPage?.items || []) { const item = make('label', 'ss-document'), checkbox = make('input'), body = make('span'); checkbox.type = 'checkbox'; checkbox.checked = selectedDocuments.has(row.entryId); checkbox.disabled = scope.value !== 'selection'; checkbox.addEventListener('change', () => { if (checkbox.checked) selectedDocuments.set(row.entryId, row); else selectedDocuments.delete(row.entryId); updateSelection(); }); body.append(make('strong', '', row.repositoryPath), make('small', '', `${shown(row.language)} · ${bytes(row.sourceBytes)} · capture: ${shown(row.captureStatus)} · lexical: ${shown(row.lexicalStatus)} · ${row.lexicallyComplete ? 'lexically complete' : 'lexical completeness unavailable or partial'}`), make('code', '', 'Source SHA-256 ' + shown(row.sourceSha256)), make('small', 'ss-note', row.sourceCandidate ? 'Supported-source candidate; parser admission is checked by the job.' : 'Not a supported-source candidate; rejection remains recorded in the job coverage.')); item.append(checkbox, body); documents.append(item); }
      if (documentPage) setPaging(documentPrev, documentNext, documentInfo, documentPage, 'matching captured documents', true); updateSelection();
    }
    function updateSelection() { selectionInfo.textContent = scope.value === 'selection' ? `${selectedDocuments.size} explicit document(s) selected across pages. Search and paging do not clear the selection.` : 'Whole-capture scope ignores the current document filter. The job records unsupported and limited entries as well as admitted documents.'; clearDocuments.disabled = !selectedDocuments.size; updateMutations(); }
    async function startAnalysis() {
      if (start.disabled || !capture) return; const values = {}; for (const [key, input] of Object.entries(limitFields)) { if (!input.reportValidity()) return; values[key] = Number(input.value); }
      const body = { captureId: capture.captureId, extractionId: capture.extractionHash, options: values }; if (scope.value === 'selection') body.entryIds = [...selectedDocuments.keys()]; mutationBusy = true; jobsPanel.open = true; updateMutations(); message(status, 'Submitting bounded retained-source analysis…');
      try { const job = await request('mutation', API + '/jobs', { method: 'POST', body }); if (disposed) return; message(status, `Job ${job.id} accepted · ${job.status}. Follow the durable output state below.`); await loadJobs(); }
      catch (error) { fail(error); } finally { mutationBusy = false; if (!disposed) updateMutations(); }
    }
    async function importAnalysis() { if (importButton.disabled || !importForm.reportValidity()) return; mutationBusy = true; jobsPanel.open = true; updateMutations(); message(status, 'Submitting sealed-analysis verification and import…'); try { const job = await request('mutation', API + '/imports', { method: 'POST', body: { directory: importDirectory.value.trim(), analysisId: importId.value.trim() } }); if (!disposed) { message(status, `Import job ${job.id} accepted. Existing analysis timestamps will be preserved.`); await loadJobs(); } } catch (error) { fail(error); } finally { mutationBusy = false; if (!disposed) updateMutations(); } }

    async function loadJobs() {
      const version = begin('jobs');
      try {
        const result = await request('jobs', API + '/jobs'); if (!current('jobs', version)) return;
        currentJobs = Array.isArray(result) ? result : result.items; recoveryDeferred = result.recoveryDeferred === true; updateMutations();
        if (!Array.isArray(currentJobs)) throw new Error('Source jobs response is missing its records.');
        const signature = currentJobs.map(job => `${job.id}:${job.status}:${job.progressSequence}:${job.outputState}`).join('|');
        if (signature !== lastJobSignature) {
          const registeredChanged = lastJobSignature !== null && currentJobs.some(job => job.outputState === 'registered' && !lastJobSignature.includes(`${job.id}:${job.status}:`));
          if (lastJobSignature === null && currentJobs.some(activeJob)) jobsPanel.open = true;
          renderJobs(); if (registeredChanged && !refreshing) void loadAnalyses(false); lastJobSignature = signature;
        }
        jobsSummary.textContent = `Source analysis jobs · ${currentJobs.filter(activeJob).length} active · ${currentJobs.length} retained`;
        message(jobsStatus, recoveryDeferred ? 'Source job recovery is deferred during service maintenance. New analysis and import are unavailable until recovery completes.' : `${currentJobs.filter(activeJob).length} active · ${currentJobs.length} retained job(s). ${currentJobs.some(activeJob) ? 'Progress polls every 3 seconds while this view is visible.' : 'The journal preserves terminal and interrupted outcomes.'}`);
      }
      catch (error) { if (current('jobs', version)) fail(error, jobsStatus); }
    }
    function renderJobs() {
      const focused = jobsList.contains(document.activeElement) ? document.activeElement : null;
      const focusedJob = focused?.closest('.ss-job')?.dataset.jobId, focusedText = focused?.textContent;
      const openDetails = new Map([...jobsList.querySelectorAll('.ss-job')].map(card => [card.dataset.jobId, [...card.querySelectorAll('details')].map(item => item.open)]));
      jobsList.replaceChildren(); if (!currentJobs.length) jobsList.append(make('p', 'ss-note', 'No retained-source jobs have been recorded.'));
      for (const job of currentJobs) {
        const card = make('article', 'ss-job'), head = make('div', 'ss-section-heading'); card.dataset.jobId = job.id; card.tabIndex = -1;
        head.append(make('strong', '', `${job.kind} · ${job.status}`), make('span', 'ss-chip', job.phase)); card.append(head, make('code', '', job.id));
        pairs(card, [['Output', job.outputState], ['Created', time(job.createdAt)], ['Started', time(job.startedAt)], ['Finished', time(job.finishedAt)], ['Capture', job.captureId], ['Analysis', job.analysisId]]);
        if (job.error || job.stopReason) card.append(make('p', 'ss-error', job.error || job.stopReason));
        if (job.progress) card.append(raw('Recorded progress · sequence ' + job.progressSequence, job.progress));
        if (job.coverage) card.append(raw('Terminal coverage', job.coverage)); card.append(raw('Job provenance', job));
        if (activeJob(job)) {
          const cancel = button(job.status === 'cancelling' ? 'Cancellation requested' : 'Request cancellation', async () => {
            cancel.disabled = true;
            try { await request('cancel-' + job.id, API + '/jobs/' + enc(job.id) + '/cancel', { method: 'POST' }); if (!disposed) { message(status, 'Cancellation requested. Publication may already have completed; the durable output state is authoritative.'); await loadJobs(); } }
            catch (error) { fail(error); if (!disposed) cancel.disabled = !canWrite; }
          }); cancel.disabled = !canWrite || job.status === 'cancelling'; card.append(cancel);
        }
        if (job.analysisId && job.outputState === 'registered') card.append(button('Open persisted analysis', () => { void selectAnalysis(job.analysisId); }));
        [...card.querySelectorAll('details')].forEach((item, index) => { item.open = openDetails.get(job.id)?.[index] === true; }); jobsList.append(card);
        if (focusedJob === job.id) ([...card.querySelectorAll('button,summary')].find(item => item.textContent === focusedText && !item.disabled) || card).focus({ preventScroll: true });
      }
    }
    async function loadAnalyses(choose = true) {
      const version = begin('analyses'); analysisPrev.disabled = analysisNext.disabled = true;
      try { const page = await request('analyses', API + '/analyses?' + new URLSearchParams({ offset: analysisOffset, limit: PAGE })); if (!current('analyses', version)) return; analysisPage = page; analysisSelect.replaceChildren(new Option('Choose a sealed analysis', '')); if (analysis && !page.items.some(row => row.analysisId === analysis.analysisId)) analysisSelect.append(new Option(analysisLabel(analysis), analysis.analysisId)); for (const row of page.items) analysisSelect.append(new Option(analysisLabel(row), row.analysisId)); analysisSelect.value = analysis?.analysisId || ''; setPaging(analysisPrev, analysisNext, analysisInfo, page, 'persisted analyses'); if (!analysis && choose) { const id = wantedAnalysis || page.items[0]?.analysisId; if (id) await selectAnalysis(id); else { manifestPanel.replaceChildren(make('p', 'ss-note', 'No sealed source analysis is registered. Analyze retained bytes or import a verified analysis above.')); } } }
      catch (error) { if (current('analyses', version)) fail(error); }
    }
    async function selectAnalysis(id) {
      wantedAnalysis = id || '';
      const version = begin('analysis'); begin('objects'); begin('node'); begin('all-observations'); abortRelated(); scene?.destroy(); scene = null; stage.replaceChildren(); nativeList.replaceChildren(); inspector.replaceChildren(make('p', 'ss-note', 'Reading sealed analysis…')); manifestPanel.replaceChildren(); coveragePanel.replaceChildren(); artifactPanel.replaceChildren(make('summary', '', 'Verified artifacts and authenticated downloads')); analysis = null; selectedNode = null; objectOffset = 0; objectHistory.length = 0; allEvidenceBody.replaceChildren();
      if (!id) { route(); return; }
      try { const value = await request('analysis', API + '/analyses/' + enc(id)); if (!current('analysis', version)) return; analysis = value; wantedAnalysis = ''; inputPanel.open = false; if (![...analysisSelect.options].some(option => option.value === value.analysisId)) analysisSelect.append(new Option(analysisLabel(value), value.analysisId)); analysisSelect.value = value.analysisId; refreshAnalysisLabels(); renderManifest(); route(); await loadObjects(); if (!current('analysis', version)) return; if (wantedNode) { const nodeId = wantedNode; wantedNode = ''; await selectNode(nodeId); } if (allEvidence.open) loadAllObservations(); }
      catch (error) { if (current('analysis', version)) { fail(error); inspector.replaceChildren(make('p', 'ss-error', 'Analysis unavailable. No replacement analysis was selected.')); } }
    }
    function renderManifest() {
      const m = analysis.manifest, c = m.coverage, provenance = make('details', 'ss-provenance'); provenance.append(make('summary', '', 'Source, session and time provenance')); const fullScope = make('details', 'ss-provenance'); fullScope.append(make('summary', '', 'Full analysis scope and limits'), make('p', 'ss-note', m.scope)); manifestPanel.replaceChildren(make('p', 'ss-chip', m.state), make('p', 'ss-note', 'Partial static evidence from retained source bytes. Select a native object to inspect its source span and supporting records.')); pairs(provenance, [['Analysis / manifest SHA-256', analysis.analysisId], ['Analysis run / context', m.runId + ' / ' + m.contextId], ['Capture ID', m.captureId], ['Lexical extraction ID', m.extractionId], ['Repository / commit', m.repositoryId + ' / ' + m.commit], ['Capture interval', time(m.captureStartedAt) + ' → ' + time(m.captureFinishedAt)], ['Analysis started', time(m.startedAt)], ['Parsing completed', time(m.parsedAt)], ['Analysis sealed', time(m.finishedAt)], ['Original source observed', time(m.originalSourceObservedAt)], ['Managed catalog import', time(analysis.importedAt)]]); provenance.append(raw('Full manifest · policies, implementation and source commitments', m)); manifestPanel.append(fullScope, provenance); coveragePanel.replaceChildren();
      for (const [label, value, note] of [['Admitted documents', `${number(c.admittedDocuments)} / ${number(c.supportedDocuments)}`, 'Parser admitted / supported selected inputs'], ['Selected capture entries', `${number(c.selectedEntries)} / ${number(c.captureEntries)}`, `${number(c.unsupportedEntries)} unsupported · ${number(c.rejectedOrLimitedDocuments)} rejected or limited`], ['Exact native spans', `${number(c.exactNodeSpans)} / ${number(c.nodes)}`, `${number(c.unavailableNodeSpans)} unavailable spans`], ['Observations', number(c.observations), `${number(c.partialObservations)} partial · includes context-only evidence`], ['Lexical containment links', number(c.lexicalLinks), `${c.lexicalLinksTruncated ? 'Link cap reached' : 'Link cap not reached'} · ${number(c.joinWordLimitedDocuments)} documents hit word-join limit`], ['Native edges / findings', `${number(c.edges)} / ${number(c.findings)}`, `${number(c.edgesWithMissingEndpoints)} missing endpoints · ${number(c.edgesWithMissingEvidence)} missing edge evidence`]]) { const card = make('article', 'ss-metric'); card.append(make('span', '', label), make('strong', '', value), make('small', '', note)); coveragePanel.append(card); } coveragePanel.append(raw('Input admission, parser admission and all denominators', c));
      const manifestArtifact = { path: 'manifest.json', sha256: analysis.analysisId, records: 1, byteLength: analysis.sealedBytes - m.artifacts.reduce((sum, item) => sum + item.byteLength, 0) };
      for (const artifact of [manifestArtifact, ...m.artifacts]) { const row = make('div', 'ss-artifact'), download = button('Download', () => { void downloadArtifact(artifact, download, analysis.analysisId); }); row.append(make('strong', '', artifact.path), make('span', '', `${number(artifact.records)} records · ${bytes(artifact.byteLength)}`), make('code', '', artifact.sha256), download); artifactPanel.append(row); }
    }
    async function downloadArtifact(artifact, control, id) {
      control.disabled = true; const key = 'download-' + id + '-' + artifact.path;
      try { let file = null; if (artifact.byteLength > 64 * 1024 * 1024) { if (!global.showSaveFilePicker) throw new Error('This artifact exceeds the 64 MiB in-memory download limit. Use a browser with streaming file saves or the native authenticated API.'); file = await global.showSaveFilePicker({ suggestedName: artifact.path }); } if (disposed) return;
        await request(key, API + '/analyses/' + enc(id) + '/artifacts/' + enc(artifact.path), { raw: true }, async (response, signal) => { if (file) { if (!response.body) throw new Error('Download stream unavailable.'); const output = await file.createWritable(); await response.body.pipeTo(output, { signal }); } else { const blob = await response.blob(); if (disposed || signal.aborted) return; const url = URL.createObjectURL(blob); urls.add(url); const link = make('a'); link.href = url; link.download = artifact.path.replace(/[^a-zA-Z0-9._-]/g, '_'); document.body.append(link); link.click(); link.remove(); global.setTimeout(() => { URL.revokeObjectURL(url); urls.delete(url); }, 1000); } }); if (!disposed) message(status, 'Downloaded verified artifact ' + artifact.path + '.');
      } catch (error) { fail(error); } finally { if (!disposed) control.disabled = false; }
    }
    async function loadObjects() {
      const version = begin('objects'), id = analysis?.analysisId; begin('node'); abortRelated(); selectedNode = null; route(); scene?.destroy(); scene = null; stage.replaceChildren(); nativeList.replaceChildren(); inspector.replaceChildren(make('h3', '', 'Choose a native object or node'), make('p', 'ss-note', 'Evidence is scoped to the selected analysis and native identity.')); objectPrevious.disabled = objectNext.disabled = true; if (!id) return; objectApply.disabled = true; message(pageStatus, 'Reading persisted native records…');
      const mode = browseMode.value, query = new URLSearchParams({ offset: objectOffset, limit: PAGE });
      if (objectSearch.value.trim()) query.set('search', objectSearch.value.trim());
      if (kind.value.trim()) query.set('kind', kind.value.trim());
      try { const page = await request('objects', API + '/analyses/' + enc(id) + '/' + mode + '?' + query); if (!current('objects', version) || analysis?.analysisId !== id) return; objectPage = page; setPaging(objectPrevious, objectNext, objectInfo, page, mode); if (!page.items.length) nativeList.append(make('p', 'ss-note', 'No native records match this page and filter.'));
        if (mode === 'nodes') { for (const node of page.items) { const item = button(node.name || node.id, () => { void selectNode(node.id); }, 'ss-native-node'); item.append(make('small', '', `${shown(node.kind)} · ${node.id}`)); nativeList.append(item); } message(pageStatus, describePage(page, 'native nodes') + '. Source identity is not replaced by a shape code.'); }
        else { const objects = page.items.map(object => ({ ...object, arm: object.some_type, status: object.snapshotWindow?.completeness || analysis.manifest.state, sessionId: analysis.manifest.runId, observedAt: object.observedWindow?.from ?? null, value: null })); scene = global.FileMapperSpatial.mount(stage, { objects, edges: [], sampled: true, onSelect: object => { if (object.sourceReference?.nodeId) void selectNode(object.sourceReference.nodeId); } }); stage.querySelector('.fms-heading h3').textContent = 'Native source objects, in three dimensions.'; stage.querySelector('.fms-snapshot').textContent = 'Sealed source analysis · ' + short(id); message(pageStatus, describePage(page, 'native SOM objects') + '. Geometry is copied from native SOM records. Relationships load from the persisted edge page.');
          const edges = await request('scene-edges', API + '/analyses/' + enc(id) + '/edges?offset=0&limit=200'); if (!current('objects', version) || analysis?.analysisId !== id) return; const ids = new Map(objects.map(object => [object.sourceReference.nodeId, object.somId])), included = edges.items.filter(edge => ids.has(edge.fromId) && ids.has(edge.toId)).map(edge => ({ from: ids.get(edge.fromId), to: ids.get(edge.toId), kind: edge.kind, label: `${edge.kind} · ${edge.id}`, evidenceRefs: [{ id: edge.evidenceId }] })); scene.setObjects(objects, included); message(pageStatus, `${describePage(page, 'native SOM objects')} · ${included.length} connecting edges from ${edges.items.length} / ${number(edges.total)} loaded native edges${edges.byteLimitReached ? ' · edge byte limit reached' : ''}. Only edges with both endpoints on this page are drawn; select a node for its adjacent evidence.`);
        }
      } catch (error) { if (current('objects', version)) fail(error, pageStatus); } finally { if (current('objects', version)) objectApply.disabled = false; }
    }
    async function selectNode(id) {
      const version = begin('node'), analysisId = analysis?.analysisId; if (!analysisId || !id) return; for (const key of [...requests.keys()]) if (key.startsWith('related-')) { requests.get(key)?.abort(); requests.delete(key); } selectedNode = id; route(); inspector.replaceChildren(make('h3', '', 'Reading native node evidence…'));
      try { const detail = await request('node', API + '/analyses/' + enc(analysisId) + '/node?' + new URLSearchParams({ nodeId: id, relatedLimit: RELATED })); if (!current('node', version) || analysis?.analysisId !== analysisId) return; renderNode(detail, analysisId, id, version); }
      catch (error) { if (current('node', version)) inspector.replaceChildren(make('p', 'ss-error', 'Selected node unavailable: ' + error.message)); }
    }
    function renderNode(detail, analysisId, id, version) {
      inspector.replaceChildren(make('p', 'ss-kicker', 'SELECTED NATIVE IDENTITY / EXACT EVIDENCE'), make('h3', '', detail.node.name || id)); const object = detail.object, span = detail.span;
      pairs(inspector, [['Analysis ID', analysisId], ['Native node ID', detail.node.id], ['Native kind', detail.node.kind], ['SOM ID', object?.somId], ['SOM observation ID', object?.observationId], ['Source path / logical URI', object?.sourceReference?.path], ['Span status', span?.status], ['Document ID', span?.documentId], ['Source SHA-256', span?.sourceSha256], ['Byte start / length', span?.byteStart == null ? 'Unavailable' : `${span.byteStart} / ${span.byteLength}`], ['UTF-16 start / length', span?.utf16Start == null ? 'Unavailable' : `${span.utf16Start} / ${span.utf16Length}`], ['Native evidence observation interval', object ? `${time(object.observedWindow?.from)} → ${time(object.observedWindow?.to)} · ${shown(object.observedWindow?.basis)}` : 'Unknown'], ['Analysis session', analysis.manifest.runId], ['Original source time', time(analysis.manifest.originalSourceObservedAt)]]);
      inspector.append(raw('Native node · original record', detail.node), raw('Exact source span', span), raw('Native SOM projection and source references', object));
      if (object?.some_shapes?.length) { const words = make('details', 'ss-word-shapes'); words.append(make('summary', '', `${object.some_shapes.length} retained lexical feature shapes`), make('p', 'ss-note', 'These are lexical features of the native object. Equal nine-bit signatures can collide. Exact source-word containment is listed separately below.')); const deck = make('div', 'ss-word-grid'); for (const word of object.some_shapes.slice(0, 24)) { const card = make('article', 'ss-word'); card.append(make('strong', '', word.token), make('small', '', `${shown(word.shape_type)} · ${number(word.count)} occurrence(s)`)); if (Number.isInteger(word.signature9) && word.signature9 >= 0 && word.signature9 < 512) { const bits = make('div', 'ss-bits'); bits.setAttribute('role', 'img'); bits.setAttribute('aria-label', 'Nine-bit lexical signature ' + word.signature9 + '; collisions allowed'); for (const bit of word.signature9.toString(2).padStart(9, '0')) bits.append(make('i', bit === '1' ? 'on' : '')); card.append(bits); } card.append(raw('Word identity and supplied geometry', word)); deck.append(card); } words.append(deck, make('p', 'ss-note', `${Math.min(24, object.some_shapes.length)} / ${object.some_shapes.length} retained word features displayed. Full features remain in the native object record and artifact.`)); inspector.append(words); }
      relatedPanel('Direct subject evidence', 'observations', detail.subjectEvidence, { subjectId: id }, renderObservation);
      relatedPanel('Adjacent native edges', 'edges', detail.edges, { nodeId: id }, renderEdge);
      relatedPanel('Lexical words contained in the exact source span', 'word-links', detail.wordLinks, { nodeId: id }, renderWord);
      const edgeEvidence = make('details', 'ss-related'); edgeEvidence.append(make('summary', '', 'Evidence referenced by adjacent edges'), make('p', 'ss-note', describePage(detail.relatedEdgeEvidence, 'edge evidence records') + '. These observations support edges; they are not attached to the selected node as direct subject evidence.')); for (const item of detail.relatedEdgeEvidence.items) edgeEvidence.append(renderObservation(item)); if (detail.relatedEdgeEvidence.nextOffset != null) edgeEvidence.append(make('p', 'ss-note', 'More edge evidence exists beyond this bounded detail. Page through the all-observations collection or download observations.ndjson and look up the exact edge evidence ID.')); inspector.append(edgeEvidence);
      function relatedPanel(label, endpoint, initial, filters, renderItem) {
        let page = initial; const history = [], section = make('section', 'ss-related'), content = make('div'), note = make('p', 'ss-note'), nav = make('div', 'ss-pagination');
        const previous = button('Previous', () => { void load(history.at(-1) ?? 0, false); }), next = button('Next', () => { void load(page.nextOffset, true); });
        nav.append(previous, note, next); section.append(make('h4', '', label), content, nav); inspector.append(section); paint();
        function paint() { content.replaceChildren(); for (const item of page.items) content.append(renderItem(item)); if (!page.items.length) content.append(make('p', 'ss-note', 'No records returned for this subject in the sealed analysis.')); setPaging(previous, next, note, page, 'records'); }
        async function load(offset, forward) {
          if (offset == null) return; previous.disabled = next.disabled = true;
          try { const value = await request('related-' + endpoint, API + '/analyses/' + enc(analysisId) + '/' + endpoint + '?' + new URLSearchParams({ ...filters, offset, limit: RELATED })); if (!current('node', version) || selectedNode !== id) return; if (forward) history.push(page.offset); else history.pop(); page = value; paint(); }
          catch (error) { if (current('node', version)) { setPaging(previous, next, note, page, 'records'); fail(error, note); } }
        }
      }
    }
    function renderObservation(item) { const card = make('article', 'ss-evidence'); card.append(make('strong', '', shown(item.predicate)), make('p', 'ss-evidence-value', shown(item.value).slice(0, 12000))); if (String(item.value || '').length > 12000) card.append(make('p', 'ss-note', 'Value display limited to 12,000 characters; download observations.ndjson for the complete value.')); pairs(card, [['Evidence ID', item.id], ['Subject', item.subjectId], ['Recorded at', time(item.observedAt)], ['Source', item.sourceUri], ['Coverage / confidence', `${shown(item.completeness)} / ${shown(item.confidence)}`]]); card.append(raw('Complete observation envelope', item)); return card; }
    function renderEdge(edge) { const card = make('article', 'ss-edge'); card.append(make('strong', '', edge.kind), make('code', '', edge.id), make('p', '', edge.fromId + ' → ' + edge.toId), make('p', 'ss-note', 'Edge evidence ID: ' + shown(edge.evidenceId))); const other = edge.fromId === selectedNode ? edge.toId : edge.fromId; card.append(button('Inspect adjacent node', () => { void selectNode(other); }), raw('Native edge record', edge)); return card; }
    function renderWord(item) { const card = make('article', 'ss-word-link'), link = item.link, occurrence = item.occurrence; card.append(make('code', '', shown(occurrence.text)), make('p', 'ss-note', link.relation)); pairs(card, [['Occurrence ID', link.occurrenceId], ['Normalized term / term ID', shown(occurrence.normalizedTerm) + ' / ' + shown(link.termId)], ['Line / column', `${shown(occurrence.startLine)} / ${shown(occurrence.startColumn)}`], ['Word byte start / length', `${link.wordByteStart} / ${link.wordByteLength}`], ['Document', link.documentId], ['Source SHA-256', link.sourceSha256]]); card.append(raw('Retained occurrence and containment link', item)); return card; }
    function loadAllObservations() {
      if (!analysis) return; const id = analysis.analysisId, version = begin('all-observations'), history = []; let page = null;
      const content = make('div'), note = make('p', 'ss-note'), nav = make('div', 'ss-pagination');
      const prev = button('Previous observations', () => { void load(history.at(-1) ?? 0, false); }), next = button('Next observations', () => { void load(page?.nextOffset, true); }); nav.append(prev, note, next); allEvidenceBody.replaceChildren(content, nav); void load(0, null);
      async function load(offset, forward) {
        if (offset == null) return; prev.disabled = next.disabled = true;
        try { const value = await request('all-observations', API + '/analyses/' + enc(id) + '/observations?' + new URLSearchParams({ offset, limit: RELATED })); if (!current('all-observations', version) || analysis?.analysisId !== id) return; if (forward === true) history.push(page.offset); else if (forward === false) history.pop(); page = value; content.replaceChildren(); for (const item of page.items) content.append(renderObservation(item)); setPaging(prev, next, note, page, 'observations'); }
        catch (error) { if (current('all-observations', version)) { if (page) setPaging(prev, next, note, page, 'observations'); fail(error, note); } }
      }
    }
    function poll() { if (disposed) return; global.clearTimeout(pollTimer); pollTimer = global.setTimeout(async () => { if (disposed) return; if (!document.hidden) await loadJobs(); poll(); }, 3000); }
    async function refreshAll() { refresh.disabled = true; refreshing = true; message(status, 'Refreshing source connection, captures, analyses and jobs…'); await Promise.allSettled([loadConnection(), loadCaptures(), loadAnalyses(), loadJobs()]); if (!disposed) { refresh.disabled = false; refreshing = false; if (!status.classList.contains('ss-error')) message(status, 'Source jobs and sealed analyses are independent of filesystem snapshots. Availability and bounded coverage appear in each section.'); } }
    on(captureSelect, 'change', () => { const row = captures.find(item => item.captureId === captureSelect.value) || (capture?.captureId === captureSelect.value ? capture : null); documentPage = null; void selectCapture(row); });
    on(scope, 'change', renderDocuments); on(documentForm, 'submit', event => { event.preventDefault(); documentOffset = 0; void loadDocuments(); }); on(supportedOnly, 'change', () => { documentOffset = 0; void loadDocuments(); }); on(importForm, 'submit', event => { event.preventDefault(); void importAnalysis(); }); on(analysisSelect, 'change', () => { wantedNode = ''; void selectAnalysis(analysisSelect.value); }); on(objectForm, 'submit', event => { event.preventDefault(); objectOffset = 0; objectHistory.length = 0; void loadObjects(); }); on(browseMode, 'change', () => { objectOffset = 0; objectHistory.length = 0; void loadObjects(); }); on(allEvidence, 'toggle', () => { if (allEvidence.open && !allEvidenceBody.childElementCount) loadAllObservations(); }); on(document, 'visibilitychange', () => { if (!document.hidden && !disposed) void loadJobs(); });
    updateMutations(); await refreshAll(); poll(); return instance;
  }
  global.FileMapperSourceSemantics = Object.freeze({ render, destroy, getState: host => instances.get(host)?.getState() });
})(window);
