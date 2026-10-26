const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const app = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
function install(ctx, name) {
  let start = app.indexOf(`function ${name}(`);
  if (app.slice(start - 6, start) === 'async ') start -= 6;
  vm.runInContext(app.slice(start, app.indexOf('\n}', start) + 2), ctx);
}
function deferred() { let resolve; const promise = new Promise(yes => { resolve = yes; }); return { resolve, promise }; }

test('an old manager page cannot replace the restored home page', async () => {
  let key = 'manager:1', params = '&folder_id=saved';
  const pending = [], shown = [];
  const ctx = vm.createContext({ historyPage: 1, historyRequest: 0,
    artworkHistoryKey: () => key, artworkHistoryParams: () => params,
    request: url => { const item = deferred(); pending.push({ ...item, url }); return item.promise; },
    renderHistoryPage: data => shown.push(data), toast() {} });
  install(ctx, 'historyViewState'); install(ctx, 'loadHistory');
  const old = ctx.loadHistory(2);
  key = 'home:2'; params = '';
  const current = ctx.loadHistory(1);
  pending[1].resolve({ page: 1, items: [{ id: 10 }] }); await current;
  pending[0].resolve({ page: 2, items: [{ id: 99 }] }); await old;
  assert.equal(shown.length, 1); assert.equal(shown[0].items[0].id, 10);
  assert.match(pending[0].url, /folder_id=saved/); assert.doesNotMatch(pending[1].url, /folder_id/);
});

test('a pending full-size neighbor read cannot poison another filter cache', async () => {
  let key = 'folder-a'; const pending = deferred();
  const ctx = vm.createContext({ historyCache: new Map(), imagePreview: {}, artworkHistoryKey: () => key,
    artworkHistoryParams: () => '&folder_id=a', request: () => pending.promise });
  install(ctx, 'historyViewState'); install(ctx, 'previewHistoryView'); install(ctx, 'cacheHistoryPage'); install(ctx, 'loadHistoryPageForPreview');
  const old = ctx.loadHistoryPageForPreview(2); key = 'folder-b';
  pending.resolve({ page: 2, items: [{ id: 99 }] });
  assert.equal(await old, null); assert.equal(ctx.historyCache.size, 0);
});

test('home and manager requests have independent owners and a closed manager cannot render a late page', async () => {
  const pending = [], shown = [];
  const ctx = vm.createContext({ historyPage: 3, historyRequest: 0,
    artworkHistoryKey: view => `${view.kind}:${view.queryEpoch}`,
    artworkHistoryParams: view => view.kind === 'library' ? '&workbench=single&folder_id=trip' : '&workbench=single',
    request: url => { const item = deferred(); pending.push({ ...item, url }); return item.promise; },
    renderHistoryPage: (data, view) => shown.push({ id: data.items[0].id, owner: view.kind }), toast() {} });
  install(ctx, 'historyViewState'); install(ctx, 'loadHistory');
  const manager = { kind: 'library', page: 1, queryEpoch: 0, requestId: 0, closed: false };
  const managerRead = ctx.loadHistory(2, manager), homeRead = ctx.loadHistory(3);
  pending[1].resolve({ page: 3, items: [{ id: 10 }] }); await homeRead;
  pending[0].resolve({ page: 2, items: [{ id: 20 }] }); await managerRead;
  assert.deepEqual(shown, [{ id: 10, owner: 'home' }, { id: 20, owner: 'library' }]);
  assert.match(pending[0].url, /folder_id=trip/); assert.doesNotMatch(pending[1].url, /folder_id/);
  const late = ctx.loadHistory(3, manager); manager.closed = true;
  pending[2].resolve({ page: 3, items: [{ id: 30 }] }); await late;
  assert.equal(shown.length, 2);
});

test('manager preview paging reads and caches only its folder while the home page stays unchanged', async () => {
  const urls = [], homeCache = new Map([[3, { page: 3, items: [{ id: 90 }] }]]);
  const manager = { kind: 'library', queryEpoch: 2, cache: new Map(), page: 1, pages: 2, items: [{ id: 1 }], elements: { historyCount: { textContent: '30 张图片' } } };
  const ctx = vm.createContext({ imagePreview: { context: { historyView: manager } }, historyPage: 3, historyItems: [{ id: 90 }], historyCache: homeCache,
    selectedRecord: { id: 1 }, artworkHistoryKey: view => `${view.kind}:${view.queryEpoch}`,
    artworkHistoryParams: view => view === manager ? '&folder_id=trip' : '',
    request: async url => { urls.push(url); return { page: 2, items: [{ id: 2 }] }; } });
  for (const name of ['historyViewState', 'previewHistoryView', 'previewRecordIndex', 'selectedHistoryLocation', 'previewNeighbor', 'cacheHistoryPage', 'loadHistoryPageForPreview']) install(ctx, name);
  assert.equal(ctx.selectedHistoryLocation().items[0].id, 1);
  assert.equal(ctx.previewNeighbor(1).page, 2);
  const data = await ctx.loadHistoryPageForPreview(2);
  assert.equal(data.items[0].id, 2); assert.match(urls[0], /folder_id=trip/);
  assert.equal(manager.cache.get(2), data); assert.equal(homeCache.size, 1); assert.equal(ctx.historyPage, 3); assert.equal(ctx.historyItems[0].id, 90);
});

test('one detail action group is exposed at a time and closes without hiding historical data', () => {
  let closes = 0, focusInPanel = null;
  const names = ['use', 'generate', 'more'];
  const buttons = names.map(name => ({ name, expanded: 'false', getAttribute() { return this.expanded; }, setAttribute(_, value) { this.expanded = value; }, focus() { this.focused = true; } }));
  const panels = names.map(name => ({ name, hidden: true, querySelector: () => ({ focus() { focusInPanel = name; } }) }));
  const ctx = vm.createContext({ ui: {}, artworkDetailVisible: () => true, closeRegenerationDrawers() { closes++; },
    document: { querySelectorAll: selector => selector.includes('data-detail-action') ? buttons : panels,
      querySelector: selector => (selector.includes('data-detail-action') ? buttons : panels).find(item => selector.includes(`"${item.name}"`)) } });
  for (const name of ['setDetailActionPanelVisible', 'closeDetailActionPanels', 'toggleDetailActionPanel']) install(ctx, name);
  ctx.toggleDetailActionPanel('generate');
  assert.deepEqual(panels.map(panel => panel.hidden), [true, false, true]);
  assert.equal(focusInPanel, null, 'opening actions keeps the current reading focus');
  ctx.toggleDetailActionPanel('use');
  assert.deepEqual(panels.map(panel => panel.hidden), [false, true, true]);
  assert.equal(focusInPanel, null);
  assert.equal(ctx.closeDetailActionPanels({ restoreFocus: true }), true);
  assert.equal(buttons[0].focused, true); assert.ok(panels.every(panel => panel.hidden));
  assert.equal(closes, 2);
});

function installDismissal(ctx) {
  install(ctx, 'invalidateHistoryTransfer');
  for (const marker of ['ui.closeDialog.addEventListener("click",', 'ui.detailDialog.addEventListener("click",', 'ui.detailDialog.addEventListener("cancel",']) {
    const start = app.indexOf(marker);
    assert.ok(start >= 0, marker);
    vm.runInContext(app.slice(start, app.indexOf('\n});', start) + 4), ctx);
  }
}
function dismissalContext(extra = {}) {
  const events = {}, calls = [];
  const host = { open: true, addEventListener: (name, callback) => { events[name] = callback; } };
  const ctx = vm.createContext({
    ui: { detailDialog: host, closeDialog: { addEventListener: (_, callback) => { events.x = callback; } }, replayDrawer: { hidden: true }, variantDrawer: { hidden: true } },
    artworkLibraryState: { busy: false }, singleWorkbenchState: { historyTransferRequest: 0 },
    closeImagePreview: () => calls.push('preview'), closeRegenerationDrawers: () => calls.push('drawers'),
    closeDetailActionPanels: () => false, artworkDetailVisible: () => true, artworkBack: () => false,
    // Keep the host open: its actual close event arrives after an animation.
    motionDialog: () => calls.push('close-intent'), ...extra,
  });
  installDismissal(ctx);
  return { ctx, events, calls, host };
}

test('X, backdrop and final Escape invalidate a pending import before the close animation finishes', async () => {
  const workbench = fs.readFileSync(path.join(__dirname, '../static/single-workbench.js'), 'utf8');
  const start = workbench.indexOf('async function singleWorkbenchFromHistory('), end = workbench.indexOf('\n}', start) + 2;
  for (const close of ['x', 'click', 'cancel']) {
    const pending = deferred(), switches = [], image = { id: 32, settings: { model_profile: 'anima' } };
    const trigger = { setAttribute() {}, removeAttribute() {} };
    const h = dismissalContext({ document: { getElementById: () => trigger }, currentProfile: () => 'anima',
      detailContext: { origin: 'random' }, selectedRecord: image, request: () => pending.promise,
      singleWorkbenchSwitch: () => switches.push('switch'), toast() {},
    });
    vm.runInContext(workbench.slice(start, end), h.ctx);
    const importing = h.ctx.singleWorkbenchFromHistory(image, true);
    assert.equal(h.ctx.singleWorkbenchState.historyTransferRequest, 1);
    const event = { target: h.host, prevented: false, preventDefault() { this.prevented = true; } };
    h.events[close](event);
    assert.equal(h.ctx.singleWorkbenchState.historyTransferRequest, 2, close);
    assert.equal(h.host.open, true, 'native close has not fired yet');
    assert.ok(h.calls.includes('close-intent'));
    pending.resolve({ id: 'imported-reference' }); await importing;
    assert.deepEqual(switches, []); assert.equal(h.ctx.singleWorkbenchState.historyTransferBusy, false);
    if (close !== 'x') assert.equal(event.prevented, true);
  }
});

test('a busy deletion consumes backdrop and Escape without dismissing or invalidating its owner', () => {
  for (const close of ['x', 'click', 'cancel']) {
    const h = dismissalContext({ artworkLibraryState: { busy: true }, artworkBack: () => { throw new Error('must not navigate during write'); } });
    const event = { target: h.host, prevented: false, preventDefault() { this.prevented = true; } };
    h.events[close](event);
    assert.deepEqual(h.calls, [], close); assert.equal(h.ctx.singleWorkbenchState.historyTransferRequest, 0);
    if (close !== 'x') assert.equal(event.prevented, true, 'must suppress native or generic backdrop dismissal');
  }
});

test('Escape in an internal panel ignores hidden detail drawers and returns only one level', () => {
  let backs = 0, actions = 0;
  const h = dismissalContext({ artworkDetailVisible: () => false, artworkBack: () => { backs++; return true; }, closeDetailActionPanels: () => { actions++; return true; } });
  h.ctx.ui.replayDrawer.hidden = false;
  const event = { preventDefault() {} }; h.events.cancel(event);
  assert.equal(backs, 1); assert.equal(actions, 0); assert.deepEqual(h.calls, []);
  assert.equal(h.ctx.singleWorkbenchState.historyTransferRequest, 0);
});

test('Tab stays in the active artwork view and excludes controls inside closed disclosures', () => {
  let focused = null;
  const host = { open: true };
  const element = name => ({ name, tabIndex: 0, parentElement: host, matches: () => false, getClientRects: () => [{}], closest: () => null, focus() { focused = name; } });
  const first = element('close'), last = element('advanced'), hiddenSummary = element('cleanup');
  hiddenSummary.parentElement = { tagName: 'DETAILS', open: false, parentElement: host, querySelector: () => ({ contains: () => false }) };
  host.querySelectorAll = () => [first, last, hiddenSummary];
  const ctx = vm.createContext({ ui: { detailDialog: host }, document: { activeElement: last }, getComputedStyle: () => ({ visibility: 'visible' }) });
  install(ctx, 'containArtworkFocus'); let prevented = false;
  ctx.containArtworkFocus({ key: 'Tab', shiftKey: false, preventDefault() { prevented = true; } });
  assert.equal(prevented, true); assert.equal(focused, 'close');
  ctx.document.activeElement = first;
  ctx.containArtworkFocus({ key: 'Tab', shiftKey: true, preventDefault() {} });
  assert.equal(focused, 'advanced');
});

test('Escape ignores a drawer already exiting and returns the next active level', () => {
  let backs = 0;
  const h = dismissalContext({ artworkBack: () => { backs++; return true; } });
  h.ctx.ui.replayDrawer.hidden = false;
  h.ctx.ui.replayDrawer.dataset = { motionState: 'closing' };
  h.events.cancel({ preventDefault() {} });
  assert.equal(backs, 1); assert.deepEqual(h.calls, []);
});

test('clicking detail metadata folds all actions without restoring the old trigger focus', () => {
  const closes = [];
  const h = dismissalContext({ closeRegenerationDrawers: restoreFocus => closes.push(['generation', restoreFocus]),
    closeDetailActionPanels: options => closes.push(['more', options]) });
  h.events.click({ target: { closest: () => null } });
  assert.deepEqual(closes, [['generation', false], ['more', undefined]]);
  closes.length = 0;
  h.events.click({ target: { closest: () => ({}) } });
  assert.deepEqual(closes, [], 'interacting inside the fold leaves it open');
});
