const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const read = name => fs.readFileSync(path.join(__dirname, '../static', name), 'utf8');

class Events {
  constructor() { this.listeners = new Map(); }
  addEventListener(name, handler) { if (!this.listeners.has(name)) this.listeners.set(name, []); this.listeners.get(name).push(handler); }
  dispatch(name, values = {}) {
    const event = { target: this, preventDefault() { this.defaultPrevented = true; }, ...values };
    for (const handler of this.listeners.get(name) || []) handler(event);
    return event;
  }
}
function harness() {
  let time = 0, frameId = 0;
  const frames = new Map(), elements = new Map(), loads = [], focusLog = [];
  const document = new Events(), media = new Events(); media.matches = false;
  const node = (tag = 'div', id = '') => {
    const attributes = new Map(), classes = new Set();
    const result = Object.assign(new Events(), {
      tagName: tag.toUpperCase(), id, children: [], dataset: {}, hidden: false, inert: false, disabled: false, scrollTop: 0,
      style: { setProperty(key, value) { this[key] = value; }, removeProperty(key) { delete this[key]; } },
      classList: { add: (...names) => names.forEach(name => classes.add(name)), remove: (...names) => names.forEach(name => classes.delete(name)), contains: name => classes.has(name), toggle(name, yes) { yes = yes ?? !classes.has(name); yes ? classes.add(name) : classes.delete(name); return yes; } },
      setAttribute(name, value) { attributes.set(name, String(value)); }, getAttribute: name => attributes.get(name), removeAttribute: name => attributes.delete(name),
      matches(selector) {
        if (selector.includes(',')) return selector.split(',').some(item => this.matches(item.trim()));
        if (selector === '[hidden]') return this.hidden;
        if (selector === '[inert]') return this.inert;
        if (selector.startsWith('.')) return classes.has(selector.slice(1));
        const attribute = selector.match(/^\[data-([a-z-]+)(?:="([^"]*)")?\]$/);
        if (attribute) { const key = attribute[1].replace(/-([a-z])/g, (_, c) => c.toUpperCase()); return key in this.dataset && (attribute[2] === undefined || this.dataset[key] === attribute[2]); }
        return selector.toUpperCase() === this.tagName;
      },
      closest(selector) { return this.matches(selector) ? this : this.parentElement?.closest(selector) || null; },
      contains(child) { return child === this || this.children.some(item => item.contains(child)); },
      querySelectorAll(selector) { return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]); },
      querySelector(selector) { return this.querySelectorAll(selector)[0] || null; },
      append(...children) { children.forEach(child => this.insertBefore(child, null)); },
      insertBefore(child, before) { child.remove(); const index = before ? this.children.indexOf(before) : this.children.length; this.children.splice(index, 0, child); child.parentElement = this; },
      replaceChildren(...children) { for (const child of [...this.children]) child.remove(); this.append(...children); },
      remove() { if (this.parentElement) this.parentElement.children.splice(this.parentElement.children.indexOf(this), 1); this.parentElement = null; },
      before(other) { this.parentElement.insertBefore(other, this); },
      replaceWith(other) { this.before(other); this.remove(); },
      focus() { document.activeElement = this; focusLog.push(this.id); },
      getClientRects() { return this.closest('[hidden], [inert]') ? [] : [{}]; },
      getBoundingClientRect() { return { left: 0, top: 0, right: 300, bottom: 400, width: 300, height: 400 }; },
      showModal() { this.open = true; }, close() { this.open = false; this.dispatch('close'); },
    });
    Object.defineProperty(result, 'className', { get: () => [...classes].join(' '), set: value => { classes.clear(); value.split(/\s+/).filter(Boolean).forEach(name => classes.add(name)); } });
    Object.defineProperty(result, 'isConnected', { get: () => result === document.body || Boolean(result.parentElement?.isConnected) });
    if (id) elements.set(id, result);
    return result;
  };
  document.body = node('body'); document.documentElement = node('html');
  document.hidden = false; document.getElementById = id => elements.get(id);
  document.createElement = tag => node(tag); document.createComment = () => node('#comment'); document.createTextNode = text => Object.assign(node('#text'), { textContent: text });
  document.querySelector = selector => document.body.querySelector(selector); document.querySelectorAll = selector => document.body.querySelectorAll(selector);
  const host = node('dialog', 'detailDialog'); host.open = true; document.body.append(host);
  const title = node('h2', 'artworkViewTitle'), back = node('button', 'artworkBack'); host.append(title, back);
  const ids = { library: 'artworkLibraryView', detail: 'artworkDetailView', membership: 'artworkMembershipPanel', delete: 'artworkDeletePanel', cleanup: 'artworkCleanupPanel' };
  const views = Object.fromEntries(Object.entries(ids).map(([name, id]) => { const view = node('section', id); view.hidden = name !== 'library'; host.append(view); return [name, view]; }));
  const library = views.library;
  for (const id of ['artworkLibraryBody', 'artworkSelectionToggle', 'artworkBatchToolbar', 'artworkBatchActions', 'artworkFolderForm', 'artworkFolderFilter', 'artworkFolderList', 'artworkFolderEdit', 'artworkFolderSave', 'artworkFolderNew', 'artworkFolderCancel', 'artworkFolderName', 'artworkFolderError', 'artworkFolderDeleteConfirm', 'artworkFolderDeleteSummary', 'artworkFolderDeleteCancel', 'artworkFolderDeleteSubmit', 'artworkCleanupOpen', 'artworkSourceSummary']) library.append(node('div', id));
  for (const id of ['artworkBatchToolbar', 'artworkBatchActions', 'artworkFolderForm', 'artworkFolderDeleteConfirm']) elements.get(id).hidden = true;
  const archive = node('div', 'randomArchive'); document.body.append(archive);
  const group = node('div', 'historyWorkbenchFilter'); archive.append(group);
  const sources = ['', 'single', 'random'].map((source, index) => { const button = node('button', `source-${source || 'all'}`); button.dataset.artworkSource = source; button.offsetLeft = index * 100; button.offsetWidth = 100; group.append(button); return button; });
  const thumb = node('span'); thumb.className = 'artwork-source-thumb'; thumb.offsetLeft = 0; group.append(thumb);
  const detailStar = node('button', 'detailArtworkFavorite'); views.detail.append(detailStar);
  const loadsCleanup = [], notices = [], window = { scrollX: 0, scrollY: 480, scrollTo(value) { this.scrollX = value.left; this.scrollY = value.top; } };
  const ctx = vm.createContext({ document, window, config: {}, historyItems: [], historyPage: 3, historyPages: 1, historyRequest: 0, historyDirty: false, historyCache: new Map(), selectedRecord: null,
    ui: { gallery: { children: [], querySelector: () => null } },
    loadHistory: async page => loads.push(page), renderHistoryPage: (data, view) => { view.items = data.items; }, request: async () => ({ folders: [] }), toast: value => notices.push(value),
    releaseDetailForLibrary() {}, imageCleanupOpen: () => loadsCleanup.push(true),
    performance: { now: () => time }, matchMedia: () => media,
    requestAnimationFrame(callback) { frames.set(++frameId, callback); return frameId; }, cancelAnimationFrame(id) { frames.delete(id); },
    Option: function (text, value) { return Object.assign(node('option'), { text, value }); },
  });
  vm.runInContext(read('motion.js'), ctx);
  const app = read('app.js'), historyStart = app.indexOf('function historyViewState(');
  vm.runInContext(app.slice(historyStart, app.indexOf('\n}', historyStart) + 2), ctx);
  vm.runInContext(read('artwork-library.js'), ctx);
  const layout = read('single-layout.js'), start = layout.indexOf('function animateStudioIndicator(');
  vm.runInContext(layout.slice(start, layout.indexOf('\n}', start) + 2), ctx);
  ctx.motionDialog = (dialog, open, options) => ctx.StudioMotion.dialog(dialog, open, options);
  const state = vm.runInContext('artworkLibraryState', ctx);
  const manager = () => ({ kind: 'library', query: { ...state.homeQuery }, queryEpoch: 0, requestId: 0, cache: new Map(), items: [], page: 1, pages: 1, elements: ctx.ui });
  state.view = 'library'; state.mount = manager();
  const step = (ms = 1000 / 60) => { time += ms; const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(callback => callback(time)); };
  const drain = () => { let limit = 500; while (frames.size && limit--) step(); assert.ok(limit > 0, 'all motion must settle'); };
  return { ctx, state, elements, node, document, media, host, views, sources, group, thumb, frames, step, drain, loads, loadsCleanup, focusLog, notices, window, manager };
}
const plain = value => JSON.parse(JSON.stringify(value));

test('home source changes keep the latest radiogroup value while manager folder changes retain that source', () => {
  const h = harness(); h.state.mount = null;
  h.ctx.artworkSetFilter({ source: 'single' }); h.step(55);
  h.ctx.artworkSetFilter({ source: 'random' });
  const before = h.ctx.StudioMotion.snapshot(h.thumb, 'indicator');
  h.step(40); h.ctx.artworkSetFilter({ source: '' });
  assert.equal(h.sources[0].getAttribute('aria-checked'), 'true'); assert.equal(h.sources[0].tabIndex, 0);
  assert.equal(h.sources[2].getAttribute('aria-checked'), 'false'); assert.ok(before.active);
  h.ctx.artworkSetFilter({ folder: 'ignored' }); assert.equal(h.state.query.folder, '');
  h.ctx.artworkSetFilter({ source: 'single' }); h.state.mount = h.manager();
  h.ctx.artworkSetFilter({ source: 'random' }); assert.equal(h.state.query.source, 'single');
  h.ctx.artworkSetFilter({ folder: 'trip' });
  assert.deepEqual(plain(h.state.query), { source: 'single', folder: 'trip' });
  assert.equal(h.elements.get('artworkSourceSummary').textContent, '范围：单图工作台');
  assert.equal(h.ctx.artworkHistoryParams(), '&workbench=single&folder_id=trip');
  h.drain(); assert.equal(h.thumb.style.transform, 'translateX(100px)'); assert.deepEqual(h.loads, [1, 1, 1, 1, 1]);
});

test('view reversals retain both presentation and velocity and only the latest view stays interactive', () => {
  const h = harness(), card = h.node('button', 'card'); h.views.library.append(card); card.focus();
  h.ctx.artworkSetView('detail'); h.step(70);
  h.ctx.artworkSetView('library', { returning: true }); h.step(40);
  const before = h.ctx.StudioMotion.snapshot(h.views.detail, 'presence');
  h.ctx.artworkSetView('detail');
  const after = h.ctx.StudioMotion.snapshot(h.views.detail, 'presence');
  assert.deepEqual(plain(after.value), plain(before.value)); assert.deepEqual(plain(after.velocity), plain(before.velocity));
  assert.equal(h.views.library.inert, true); assert.equal(h.views.detail.inert, false); assert.equal(h.state.busy, false);
  assert.equal(h.document.activeElement.id, 'artworkViewTitle');
  h.drain();
  for (const [name, view] of Object.entries(h.views)) { assert.equal(view.hidden, name !== 'detail'); assert.equal(view.classList.contains('artwork-view-exiting'), false); assert.equal(view.style.transform, ''); }
  assert.equal(h.frames.size, 0);
});

test('cleanup returns to its exact library trigger and preserves library scroll', () => {
  const h = harness(), opener = h.elements.get('artworkCleanupOpen');
  h.elements.get('artworkLibraryBody').scrollTop = 360; opener.focus(); h.ctx.artworkOpenCleanup();
  assert.equal(h.state.view, 'cleanup'); assert.equal(h.loadsCleanup.length, 1); assert.equal(h.document.activeElement.id, 'artworkViewTitle');
  h.step(65); assert.equal(h.ctx.artworkBack(), true); h.drain();
  assert.equal(h.state.view, 'library'); assert.equal(h.document.activeElement, opener); assert.equal(h.elements.get('artworkLibraryBody').scrollTop, 360);
});

test('returning from an internal action restores More when its original item was hidden', () => {
  const h = harness(); h.ctx.artworkSetView('detail', { immediate: true });
  const more = h.node('button', 'more'), panel = h.node('div', 'more-panel'), item = h.node('button', 'delete-item');
  more.dataset.detailAction = 'more'; panel.dataset.detailPanel = 'more'; panel.append(item); h.views.detail.append(more, panel);
  h.ctx.selectedRecord = { id: 1 }; h.ctx.closeDetailActionPanels = () => { panel.hidden = true; }; item.focus();
  h.state.panelReturn = 'detail'; h.ctx.artworkSetView('delete'); h.step(50); h.ctx.artworkBack();
  assert.equal(h.document.activeElement, more); h.drain(); assert.equal(h.document.activeElement, more);
});

test('closing during a view transition finishes every view and inline track without late focus', () => {
  const h = harness(); h.state.mount = null;
  h.ctx.artworkSetView('detail'); h.ctx.artworkPresence(h.elements.get('artworkFolderForm'), true); h.step(60);
  h.host.open = false; h.ctx.artworkCloseLibrary({ restoreFocus: false }); const focused = h.document.activeElement;
  assert.equal(h.frames.size, 0); h.drain(); assert.equal(h.document.activeElement, focused);
  assert.equal(h.elements.get('artworkFolderForm').hidden, true); assert.equal(h.views.library.hidden, true);
  for (const view of Object.values(h.views)) assert.equal(view.classList.contains('artwork-view-exiting'), false);
});

test('reduced motion and hidden pages settle the current view without residual frames or temporary transforms', () => {
  for (const reduce of [true, false]) {
    const h = harness(); h.ctx.artworkSetView('detail'); h.step(60); h.ctx.artworkSetView('library', { returning: true });
    if (reduce) { h.media.matches = true; h.media.dispatch('change'); } else { h.document.hidden = true; h.document.dispatch('visibilitychange'); }
    assert.equal(h.frames.size, 0); assert.equal(h.views.library.hidden, false); assert.equal(h.views.detail.hidden, true);
    for (const view of Object.values(h.views)) { assert.equal(view.style.transform, ''); assert.equal(view.style.willChange, ''); }
  }
});

test('folder refresh and filtering retain focused nodes and handlers read renamed data', () => {
  const h = harness(); h.state.folderEditing = true; h.state.folders = [{ id: 'trip', name: '旅行', count: 3 }];
  h.ctx.artworkRenderFolders(); h.drain();
  const row = h.state.folderNodes.get('trip'), { link, edit } = row.artworkParts; link.focus();
  h.ctx.artworkSetFilter({ folder: 'trip' }); assert.equal(h.state.folderNodes.get('trip'), row); assert.equal(h.document.activeElement, link);
  h.state.folders = [{ id: 'trip', name: '秋日旅行', count: 4 }]; h.ctx.artworkRenderFolders();
  assert.equal(h.state.folderNodes.get('trip').artworkParts.link, link); assert.equal(h.document.activeElement, link); assert.equal(row.artworkParts.count.textContent, '4');
  edit.dispatch('click'); assert.equal(h.elements.get('artworkFolderName').value, '秋日旅行'); assert.equal(h.document.activeElement.id, 'artworkFolderName');
  assert.equal(h.loads.length, 1);
  edit.focus(); h.state.folderEditing = false; h.ctx.artworkRenderFolders();
  assert.equal(h.document.activeElement.id, 'artworkFolderEdit');
});

test('inline selection and editor surfaces reverse from their current values', () => {
  const h = harness(), toolbar = h.elements.get('artworkBatchToolbar');
  h.ctx.artworkPresence(toolbar, true); h.step(60); h.ctx.artworkPresence(toolbar, false); h.step(30);
  const before = h.ctx.StudioMotion.snapshot(toolbar, 'presence'); h.ctx.artworkPresence(toolbar, true);
  const after = h.ctx.StudioMotion.snapshot(toolbar, 'presence');
  assert.deepEqual(plain(after.value), plain(before.value)); assert.deepEqual(plain(after.velocity), plain(before.velocity));
  assert.equal(toolbar.inert, false); h.drain(); assert.equal(toolbar.hidden, false); assert.equal(toolbar.style.transform, '');
});

test('manager inherits home source while home nodes, page and scroll never leave the page', async () => {
  const h = harness(); h.host.open = false; h.state.mount = null; h.state.query = { source: 'single', folder: '' };
  const opener = h.node('button', 'organize'); h.document.body.append(opener); opener.focus();
  await h.ctx.artworkOpenLibrary({ currentTarget: opener });
  assert.equal(h.state.query.source, 'single'); assert.equal(h.ctx.historyPage, 3);
  assert.equal(h.elements.get('randomArchive').parentElement, h.document.body);
  h.ctx.artworkSetFilter({ folder: 'trip' });
  const reads = h.loads.length;
  h.ctx.StudioMotion.dialog(h.host, false, { immediate: true }); h.ctx.artworkCloseLibrary(); await Promise.resolve();
  assert.deepEqual(plain(h.state.query), { source: 'single', folder: '' }); assert.equal(h.elements.get('randomArchive').parentElement, h.document.body);
  assert.equal(h.loads.length, reads); assert.equal(h.ctx.historyPage, 3); assert.equal(h.window.scrollY, 480); assert.equal(h.document.activeElement, opener);
});

test('source radiogroup supports wrapping arrows and Home/End without changing the workbench', async () => {
  const h = harness(); h.state.mount = null; h.host.open = false;
  for (const match of read('artwork-library.js').matchAll(/artworkElement\("([^"]+)"\)/g)) {
    if (!h.elements.has(match[1])) h.document.body.append(h.node('div', match[1]));
  }
  h.ctx.initializeArtworkLibrary(); await Promise.resolve();
  for (const [key, expected] of [['ArrowLeft', 'random'], ['ArrowRight', ''], ['End', 'random'], ['Home', '']]) {
    const event = h.group.dispatch('keydown', { key });
    assert.equal(event.defaultPrevented, true); assert.equal(h.state.query.source, expected);
    assert.equal(h.document.activeElement.dataset.artworkSource, expected);
    assert.equal(h.sources.filter(button => button.tabIndex === 0).length, 1);
  }
  h.state.mount = h.manager(); const event = h.group.dispatch('keydown', { key: 'End' });
  assert.equal(event.defaultPrevented, undefined); assert.equal(h.state.query.source, '');
  h.drain();
});

test('successful favorite feedback belongs only to the star and failure starts no confirmation motion', async () => {
  for (const fail of [false, true]) {
    const h = harness(), star = h.node('button', 'card-star'), record = { id: 7, folder_ids: [] };
    h.document.body.append(star); h.ctx.ui.gallery.querySelector = () => star;
    h.ctx.artworkRenderFolders = () => {};
    h.ctx.artworkMutateMembership = async () => { if (fail) throw new Error('offline'); record.folder_ids = ['default']; };
    await h.ctx.artworkToggleFavorite(record);
    assert.equal(Boolean(h.ctx.StudioMotion.snapshot(star, 'favorite')?.active), !fail);
    assert.equal(h.ctx.StudioMotion.snapshot(h.ctx.ui.gallery, 'presence'), null);
    assert.equal(h.state.busy, false); h.drain(); assert.equal(star.style.scale, undefined);
    if (fail) assert.deepEqual(h.notices, ['offline']);
  }
});

test('a delayed membership read cannot reopen its panel after leaving and returning to the same view', async () => {
  const h = harness(); let resolve;
  h.ctx.artworkSetView('detail', { immediate: true });
  h.ctx.artworkLoadFolders = () => new Promise(yes => { resolve = yes; });
  const opening = h.ctx.artworkOpenMembership([{ id: 1 }]);
  h.ctx.artworkSetView('library', { returning: true }); h.ctx.artworkSetView('detail');
  resolve(); await opening; h.drain();
  assert.equal(h.state.view, 'detail'); assert.equal(h.views.membership.hidden, true);
});

test('only the newest membership click may open a panel or report its load error', async () => {
  for (const staleFailure of [false, true]) {
    const h = harness(), pending = [];
    for (const id of ['artworkMembershipHeading', 'artworkMembershipHint', 'artworkMembershipError', 'artworkMembershipChoices']) h.views.membership.append(h.node('div', id));
    h.ctx.request = () => new Promise((resolve, reject) => pending.push({ resolve, reject }));
    const first = h.ctx.artworkOpenMembership([{ id: 1 }]), latest = h.ctx.artworkOpenMembership([{ id: 2 }]);
    if (!staleFailure) {
      pending[0].resolve({ folders: [{ id: 'old', name: '旧收藏夹' }] }); await first;
      assert.equal(h.state.view, 'library', 'the ignored older folder response cannot expose stale choices');
    }
    pending[1].resolve({ folders: [{ id: 'current', name: '当前收藏夹' }] }); await latest;
    if (staleFailure) { pending[0].reject(new Error('stale offline')); await first; }
    assert.equal(h.state.view, 'membership'); assert.deepEqual(plain(h.state.membershipRecords), [{ id: 2 }]);
    assert.deepEqual(h.elements.get('artworkMembershipChoices').querySelectorAll('input').map(input => input.value), ['current']);
    assert.equal(h.notices.length, 0); h.drain();
  }
});

test('reopening during dialog exit retains the mounted library and prevents stale home restoration', async () => {
  const h = harness(); h.host.open = false; h.state.mount = null;
  h.host.addEventListener('close', () => h.ctx.artworkCloseLibrary());
  await h.ctx.artworkOpenLibrary(); h.step(80);
  const mount = h.state.mount; h.ctx.motionDialog(h.host, false); h.step(45);
  const before = h.ctx.StudioMotion.snapshot(h.host, 'presence'); await h.ctx.artworkOpenLibrary();
  const after = h.ctx.StudioMotion.snapshot(h.host, 'presence');
  assert.deepEqual(plain(after.value), plain(before.value)); assert.deepEqual(plain(after.velocity), plain(before.velocity));
  h.drain();
  assert.equal(h.host.open, true); assert.equal(h.state.mount, mount); assert.equal(h.elements.get('randomArchive').parentElement, h.document.body);
  assert.equal(h.loads.filter(page => page === 3).length, 0);
});

test('a source control revealed after being hidden lands on the current source', () => {
  const h = harness(); h.state.mount = null;
  h.sources.forEach(button => { button.offsetWidth = 0; });
  h.ctx.artworkSetFilter({ source: 'random' });
  assert.equal(h.sources[2].getAttribute('aria-checked'), 'true'); assert.equal(h.ctx.StudioMotion.snapshot(h.thumb, 'indicator'), null);
  h.sources.forEach(button => { button.offsetWidth = 100; }); h.ctx.artworkRenderSource({ immediate: true });
  assert.equal(h.thumb.style.transform, 'translateX(200px)'); assert.equal(h.thumb.style.width, '100px'); assert.equal(h.frames.size, 0);
});
