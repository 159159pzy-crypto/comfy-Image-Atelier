const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../static/artwork-library.js'), 'utf8');
const app = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
function setup(extra = {}) {
  const elements = new Map();
  const ctx = vm.createContext({ document: { getElementById: id => elements.get(id) }, config: {}, historyItems: [], historyCache: new Map(), historyPage: 1, historyPages: 1, historyRequest: 0, historyDirty: false, ui: { gallery: { children: [], querySelector: () => null } }, toast() {}, selectedRecord: null, ...extra });
  const start = app.indexOf('function historyViewState('); vm.runInContext(app.slice(start, app.indexOf('\n}', start) + 2), ctx);
  vm.runInContext(source, ctx);
  const state = vm.runInContext('artworkLibraryState', ctx);
  const manager = () => ({ kind: 'library', query: { ...state.homeQuery }, queryEpoch: 0, requestId: 0, cache: new Map(), items: [...ctx.historyItems], page: 1, pages: 1, total: 0, elements: ctx.ui });
  return { ctx, elements, state, manager };
}
const record = (id, folder_ids = []) => ({ id, filename: `${id}.png`, folder_ids });
const plain = value => JSON.parse(JSON.stringify(value));
function element(extra = {}) {
  const classes = new Set(), attributes = {}, events = {};
  return { hidden: false, disabled: false, scrollTop: 0, dataset: {}, attributes, events,
    classList: { add: (...items) => items.forEach(item => classes.add(item)), remove: (...items) => items.forEach(item => classes.delete(item)), toggle: (item, enabled) => enabled ? classes.add(item) : classes.delete(item), contains: item => classes.has(item) },
    setAttribute(name, value) { attributes[name] = value; }, removeAttribute(name) { delete attributes[name]; }, addEventListener(name, fn) { events[name] = fn; }, focus() { this.focused = true; },
    ...extra,
  };
}
function surface(h) {
  for (const id of ['detailDialog', 'artworkLibraryView', 'artworkDetailView', 'artworkMembershipPanel', 'artworkDeletePanel', 'artworkBack', 'artworkLibraryBody', 'artworkSelectionToggle']) h.elements.set(id, element());
  h.elements.get('detailDialog').open = true;
  return h.elements.get('detailDialog');
}

test('favorites filter keeps query encoding and selection is bounded across pages', () => {
  const h = setup(); h.elements.set('artworkFolderFilter', { value: 'ignored-dom-value' });
  assert.equal(h.ctx.artworkHistoryQuery(), '');
  h.state.query.folder = '__all__'; assert.equal(h.ctx.artworkHistoryQuery(), '&favorites_only=true');
  h.state.query.folder = '夹 / ?'; assert.equal(h.ctx.artworkHistoryQuery(), '&folder_id=%E5%A4%B9%20%2F%20%3F');
  for (let id = 1; id <= 200; id++) assert.equal(h.ctx.artworkSelectionToggle(record(id), true), true);
  assert.equal(h.ctx.artworkSelectionToggle(record(201), true), false);
  assert.equal(h.ctx.artworkSelectionToggle(record(1), true), true, 'already selected remains usable at limit');
  h.ctx.artworkSelectionToggle(record(1), false); assert.equal(h.ctx.artworkSelectionToggle(record(201), true), true);
  h.ctx.historyCache.set(2, { items: [record(2)] }); h.ctx.artworkFilterChanged();
  assert.equal(h.state.selected.size, 0); assert.equal(h.ctx.historyCache.size, 0);
});

test('membership responses update both gallery caches, selection and open details', () => {
  const main = record(4), single = record(4), detail = record(4), selection = record(4), other = record(8, ['old']);
  const h = setup({ historyItems: [main, other], selectedRecord: detail, singleGalleryState: { items: [single], lastCompleted: single, viewerRecord: single } });
  h.state.mount = h.manager(); const managerItem = record(4), managerCached = record(4);
  h.state.mount.items = [managerItem]; h.state.mount.cache.set(1, { items: [managerCached] });
  h.state.selected.set('4', selection); const cached = record(4); h.ctx.historyCache.set(2, { items: [cached] });
  h.ctx.artworkApplyMemberships([{ id: 4, folder_ids: ['default', 'travel'] }]);
  for (const item of [main, single, detail, selection, cached, managerItem, managerCached]) assert.deepEqual(plain(item.folder_ids), ['default', 'travel']);
  assert.deepEqual(other.folder_ids, ['old']);
});

test('partial deletion only completes removed records or already-missing records', () => {
  const h = setup(); const outcomes = h.ctx.artworkDeletionOutcome([
    { id: 1, record_deleted: true, file_deleted: false },
    { id: 2, record_deleted: false, code: 'already_missing' },
    { id: 3, record_deleted: false, file_deleted: true, error: 'commit failed' },
    { id: 4, record_deleted: false, code: 'file_shared' },
  ]);
  assert.deepEqual(plain(outcomes.completed.map(item => item.id)), [1, 2]);
  assert.deepEqual(plain(outcomes.failed.map(item => item.id)), [3, 4]);
});

test('bulk invalidation removes only successful selection and preserves failed records', async () => {
  let invalidated = 0, loads = 0; const h = setup({ historyPage: 2, historyRequest: 0, historyDirty: false, detailContext: null, loadHistory: async () => { loads++; }, singleGalleryDeletedMany: async ids => { invalidated++; assert.deepEqual(plain(ids), [1, 2]); return null; } });
  h.ctx.historyItems = [record(3)]; h.state.selected.set('1', record(1)); h.state.selected.set('3', record(3)); h.ctx.historyCache.set(2, {});
  await h.ctx.artworkApplyDeleted([1, 2]);
  assert.deepEqual([...h.state.selected.keys()], ['3']); assert.equal(invalidated, 1); assert.equal(loads, 1); assert.equal(h.ctx.historyCache.size, 0);
});

test('a detail opened while bulk cache refresh waits is not closed by the older deletion', async () => {
  let resolve, closed = 0; const pending = new Promise(yes => { resolve = yes; });
  const h = setup({ selectedRecord: record(1), detailContext: { origin: 'single' }, historyPage: 1, historyRequest: 0,
    ui: { detailDialog: { open: true } }, closeImagePreview() {}, singleGalleryDeletedMany: () => pending,
    motionDialog: () => { closed++; }, loadHistory: async () => {}, updateDetailRecord: () => { throw new Error('must not replace new selection'); } });
  h.ctx.historyItems = [record(2)]; const deletion = h.ctx.artworkApplyDeleted([1]);
  h.ctx.selectedRecord = record(99); h.ctx.detailContext = { origin: 'random' }; resolve(record(2)); await deletion;
  assert.equal(h.ctx.selectedRecord.id, 99); assert.equal(closed, 0);
});

test('single gallery bulk deletion invalidates once and chooses surviving neighbor', async () => {
  const gallerySource = fs.readFileSync(path.join(__dirname, '../static/single-gallery.js'), 'utf8');
  const start = gallerySource.indexOf('async function singleGalleryDeletedMany('), end = gallerySource.indexOf('\n}', start) + 2;
  const records = [record(9), record(8), record(7), record(6)]; let invalidations = 0, refreshes = 0;
  const state = { items: records, viewerRecord: records[1], lastCompleted: records[0], total: 4, failedImages: new Set(['8']), active: true, viewerOpen: true };
  const ctx = vm.createContext({ singleGalleryState: state, imagePreview: { preloaded: new Map([['8', {}]]) }, singleGalleryInvalidate: () => { invalidations++; }, singleGalleryLoad: async () => { refreshes++; }, singleGalleryCanvas() {}, singleGalleryRender() {} });
  vm.runInContext(gallerySource.slice(start, end), ctx);
  const next = await ctx.singleGalleryDeletedMany([9, 8]);
  assert.equal(invalidations, 1); assert.equal(refreshes, 1); assert.equal(next.id, 7); assert.equal(state.lastCompleted.id, 7); assert.equal(state.viewerRecord.id, 7);
  assert.deepEqual(plain(state.items.map(item => item.id)), [7, 6]); assert.equal(state.total, 2); assert.equal(state.failedImages.size, 0);
});
test('folder refresh failure never hides successful or partially destructive deletion outcomes', async () => {
  for (const partial of [false, true]) {
    const notices = [], closed = [];
    const results = [{ id: 1, record_deleted: true, file_deleted: true }, ...(partial ? [{ id: 2, record_deleted: false, file_deleted: true, error: '数据库提交失败' }] : [])];
    const h = setup({ request: async () => ({ results }), motionDialog: (_, open) => closed.push(open), toast: text => notices.push(text) });
    for (const name of ['artworkDeleteMode', 'artworkDeleteResults', 'artworkDeleteDialog']) h.elements.set(name, { value: 'file', textContent: '' });
    h.ctx.artworkRenderDelete = h.ctx.artworkRenderSelection = h.ctx.artworkRenderDetail = () => {};
    h.ctx.artworkApplyDeleted = async () => {};
    h.ctx.artworkLoadFolders = async () => { throw new Error('离线'); };
    h.state.deleteRecords = [record(1), ...(partial ? [record(2)] : [])];
    await h.ctx.artworkConfirmDelete({ preventDefault() {} });
    const text = h.elements.get('artworkDeleteResults').textContent;
    assert.match(text, /已完成 1 张/); assert.match(text, /收藏夹数量刷新失败/);
    if (partial) { assert.match(text, /原文件已删除，记录未删除/); assert.deepEqual(plain(h.state.deleteRecords.map(item => item.id)), [2]); assert.deepEqual(closed, []); }
    else { assert.deepEqual(closed, [false]); assert.equal(h.state.deleteRecords.length, 0); assert.match(notices[0], /已删除 1 张/); }
  }
});

test('query state is independent of hidden controls and changes invalidate navigation', () => {
  let cleared = 0;
  const h = setup({ historyRequest: 4, imagePreview: { navigationRequest: 10 }, clearPreviewCache: () => cleared++ });
  h.elements.set('artworkFolderFilter', { value: '__all__' });
  h.elements.set('historyWorkbenchFilter', { value: 'single' });
  assert.equal(h.ctx.artworkHistoryParams(), '');
  h.state.query = { source: 'random', folder: '夹 / ?' };
  assert.equal(h.ctx.artworkHistoryParams(), '&workbench=random&folder_id=%E5%A4%B9%20%2F%20%3F');
  const oldKey = h.ctx.artworkHistoryKey();
  h.ctx.artworkInvalidateHistory();
  assert.notEqual(h.ctx.artworkHistoryKey(), oldKey);
  assert.equal(h.ctx.historyRequest, 5); assert.equal(h.ctx.imagePreview.navigationRequest, 11); assert.equal(cleared, 1);
  h.state.mount = h.manager(); assert.match(h.ctx.artworkHistoryKey(), /^library:/);
});

test('selection mode is explicit and selection clicks never open a detail', () => {
  const opened = [], notices = [];
  const h = setup({ openDetail: item => opened.push(item.id), toast: text => notices.push(text) });
  h.ctx.artworkCardActivate(record(1)); assert.deepEqual(opened, [1]);
  h.state.mount = h.manager(); h.state.selecting = true;
  h.ctx.artworkCardActivate(record(2), h.state.mount); assert.deepEqual(opened, [1]); assert.equal(h.state.selected.has('2'), true);
  h.ctx.artworkCardActivate(record(2), h.state.mount); assert.equal(h.state.selected.size, 0);
  for (let id = 1; id <= 200; id++) h.ctx.artworkSelectionToggle(record(id), true);
  h.ctx.artworkCardActivate(record(201), h.state.mount); assert.equal(h.state.selected.size, 200); assert.match(notices[0], /200/);
  h.ctx.config = { storage_status: { history: { read_only: true } } };
  h.ctx.artworkCardActivate(record(2), h.state.mount); assert.equal(h.state.selected.has('2'), true, 'readonly cannot change a selection');
});

test('card has only its star in browsing and only selection control in selection mode', () => {
  const h = setup(), checkbox = element(), label = element(), star = element(), open = element();
  const controls = { querySelector: selector => ({ input: checkbox, '.artwork-check': label, '.artwork-star': star })[selector], querySelectorAll: () => [checkbox, star] };
  const article = element({ record: record(8, ['default']), querySelector: selector => selector === '.artwork-card-tools' ? controls : open });
  h.state.ready = true;
  h.ctx.artworkDecorateCard(article);
  assert.equal(star.hidden, false); assert.equal(label.hidden, true); assert.equal(star.textContent, '★');
  assert.equal(star.attributes['aria-pressed'], 'true'); assert.match(open.attributes['aria-label'], /查看/);
  h.state.mount = h.manager(); h.state.selecting = true; h.state.selected.set('8', article.record);
  article.historyView = h.state.mount;
  h.ctx.artworkDecorateCard(article);
  assert.equal(star.hidden, true); assert.equal(label.hidden, false); assert.equal(checkbox.checked, true);
  assert.equal(open.attributes['aria-pressed'], 'true'); assert.match(open.attributes['aria-label'], /选择/);
  h.ctx.config = { storage_status: { history: { read_only: true } } }; h.ctx.artworkDecorateCard(article);
  assert.equal(checkbox.disabled, true); assert.equal(star.disabled, true);
  h.state.mount = null; h.ctx.artworkDecorateCard(article);
  assert.equal(star.hidden, false); assert.equal(label.hidden, true); assert.equal(open.attributes['aria-pressed'], undefined);
});

test('one host switches views and restores scroll without reopening a modal', () => {
  let released = 0;
  const h = setup({ releaseDetailForLibrary: () => released++, ui: { gallery: { querySelector: () => null } } });
  const host = surface(h); h.state.mount = h.manager(); h.state.view = 'library';
  host.scrollTop = 9; h.elements.get('artworkLibraryBody').scrollTop = 340;
  h.ctx.artworkShowDetail();
  assert.equal(host.open, true); assert.equal(host.dataset.artworkView, 'detail'); assert.equal(h.ctx.artworkDetailVisible(), true);
  assert.equal(h.elements.get('artworkLibraryView').hidden, true);
  assert.equal(h.elements.get('artworkBack').hidden, false);
  assert.equal(h.ctx.artworkBack(), true);
  assert.equal(released, 1); assert.equal(host.dataset.artworkView, 'library'); assert.equal(h.ctx.artworkDetailVisible(), false);
  assert.equal(h.elements.get('artworkLibraryBody').scrollTop, 340); assert.equal(host.scrollTop, 9);
  assert.equal(h.elements.get('artworkBack').hidden, true);
});

test('internal cancel restores its owner and busy operations cannot be dismissed', () => {
  const h = setup({ selectedRecord: record(3), renderDetail: () => {} }); const host = surface(h);
  h.state.view = 'delete'; h.state.panelReturn = 'detail'; h.state.busy = true;
  assert.equal(h.ctx.artworkBack(), true); assert.equal(h.state.view, 'delete');
  h.state.busy = false; assert.equal(h.ctx.artworkBack(), true);
  assert.equal(host.dataset.artworkView, 'detail'); assert.equal(h.elements.get('artworkDeletePanel').hidden, true);
  assert.equal(h.ctx.artworkBack(), false, 'top-level detail now asks caller to close the host');
});

test('successfully transferring leaves home query and nodes intact and suppresses stale focus restoration', () => {
  const loads = [], closed = [], h = setup({ singleWorkbenchActive: () => true, loadHistory: page => loads.push(page) });
  const host = surface(h), archive = element({ hidden: false }), opener = element({ isConnected: true });
  h.ctx.motionDialog = (dialog, open, options) => { closed.push(options); dialog.open = open; };
  h.elements.set('randomArchive', archive);
  h.state.opener = opener; h.state.mount = h.manager();
  h.state.query = { source: 'random', folder: 'travel' };
  h.ctx.artworkRevealWorkbench();
  assert.equal(h.elements.get('randomArchive'), archive); assert.equal(archive.hidden, false); assert.equal(host.open, false);
  assert.deepEqual(plain(h.state.query), { source: '', folder: '' }); assert.deepEqual(loads, []); assert.equal(opener.focused, undefined);
  assert.equal(closed[0].restoreFocus, false); assert.equal(closed[0].immediate, true);
  h.ctx.artworkCloseLibrary(); assert.deepEqual(loads, [], 'late native close event is harmless');
});

test('direct detail transfer closes the same host without a mounted library', () => {
  const h = setup(); const host = surface(h);
  h.ctx.motionDialog = (dialog, open) => { dialog.open = open; };
  h.ctx.artworkRevealWorkbench(); assert.equal(host.open, false); assert.equal(h.state.mount, null);
});

test('single selected artwork is added without clearing existing folders', async () => {
  const calls = [], h = setup({ toast() {} }); surface(h);
  h.elements.set('artworkMembershipChoices', { querySelectorAll: () => [{ value: 'new' }] });
  h.elements.set('artworkMembershipSave', element()); h.elements.set('artworkMembershipError', element());
  h.state.membershipRecords = [record(1, ['old'])];
  h.ctx.artworkMutateMembership = async (records, folders, action) => { calls.push({ ids: records.map(item => item.id), folders, action }); };
  h.ctx.artworkBack = () => true;
  await h.ctx.artworkSaveMembership({ preventDefault() {} });
  assert.deepEqual(plain(calls), [{ ids: [1], folders: ['new'], action: 'add' }]);
});

test('failed deletion keeps its record selected and retry uses only failed ids', async () => {
  const requests = [], h = setup({ toast() {}, request: async (_, opts) => {
    requests.push(JSON.parse(opts.body));
    return { results: requests.length === 1 ? [{ id: 1, record_deleted: true }, { id: 2, record_deleted: false, code: 'file_shared' }] : [{ id: 2, record_deleted: true }] };
  } });
  surface(h);
  for (const id of ['artworkDeleteMode', 'artworkDeleteResults']) h.elements.set(id, element({ value: 'file', textContent: '' }));
  h.ctx.artworkRenderDelete = h.ctx.artworkRenderSelection = h.ctx.artworkRenderDetail = () => {};
  h.ctx.artworkApplyDeleted = async ids => ids.forEach(id => h.state.selected.delete(String(id)));
  h.ctx.artworkLoadFolders = async () => {}; h.ctx.artworkBack = () => true;
  h.state.deleteRecords = [record(1), record(2)]; h.state.deleteRecords.forEach(item => h.state.selected.set(String(item.id), item));
  await h.ctx.artworkConfirmDelete({ preventDefault() {} });
  assert.deepEqual([...h.state.selected.keys()], ['2']); assert.deepEqual(plain(h.state.deleteRecords.map(item => item.id)), [2]);
  await h.ctx.artworkConfirmDelete({ preventDefault() {} });
  assert.deepEqual(requests.map(item => item.image_ids), [[1, 2], [2]]); assert.equal(h.state.selected.size, 0);
});

test('leaving detail for deletion invalidates an in-flight reference import', () => {
  let invalidated = 0, menusClosed = 0;
  const h = setup({ invalidateHistoryTransfer: () => invalidated++, closeDetailActionPanels: () => menusClosed++ });
  surface(h); h.state.view = 'detail';
  h.ctx.artworkSetView('delete');
  assert.equal(invalidated, 1); assert.equal(menusClosed, 1);
  h.ctx.artworkSetView('library'); assert.equal(invalidated, 1, 'non-detail navigation has no pending import owner');
});

test('folder deletion locks its editor, updates counts and moves the active filter to remaining favorites', async () => {
  let resolve; const loads = [], original = record(1, ['trip']), other = record(2, ['trip', 'default']);
  const h = setup({ historyItems: [original, other], loadHistory: async page => loads.push(page),
    request: () => new Promise(yes => { resolve = yes; }), Option: function(text, value) { return { text, value }; },
  });
  const node = () => element({ children: [], append(...children) { for (const child of children) { child.parentElement = this; this.children.push(child); } }, replaceChildren(...children) { this.children = children; },
    insertBefore(child, before) { const old = this.children.indexOf(child); if (old >= 0) this.children.splice(old, 1); const index = before ? this.children.indexOf(before) : this.children.length; this.children.splice(index, 0, child); child.parentElement = this; },
    remove() { if (this.parentElement) this.parentElement.children.splice(this.parentElement.children.indexOf(this), 1); this.parentElement = null; },
  });
  h.ctx.document.createElement = node;
  for (const id of ['artworkFolderFilter', 'historyWorkbenchFilter', 'artworkSourceSummary', 'artworkFolderList', 'artworkFolderForm', 'artworkFolderEdit', 'artworkFolderSave', 'artworkFolderNew', 'artworkFolderCancel', 'artworkFolderName', 'artworkFolderDeleteSubmit', 'artworkFolderDeleteCancel', 'artworkFolderDeleteConfirm', 'artworkFolderError']) h.elements.set(id, node());
  h.state.folders = [{ id: 'default', name: '我的收藏', count: 1 }, { id: 'trip', name: '旅行', count: 2 }];
  h.state.mount = h.manager(); h.state.query = { source: 'random', folder: 'trip' };
  h.state.folderEditing = true; h.state.pendingFolderDelete = h.state.folders[1]; h.state.selected.set('1', original);
  const deleting = h.ctx.artworkDeleteFolder();
  assert.equal(h.state.busy, true);
  for (const id of ['artworkFolderName', 'artworkFolderSave', 'artworkFolderDeleteSubmit', 'artworkFolderDeleteCancel']) assert.equal(h.elements.get(id).disabled, true, id);
  resolve({ folders: [{ id: 'default', name: '我的收藏', count: 2 }] }); await deleting;
  assert.equal(h.state.busy, false); assert.equal(h.elements.get('artworkFolderDeleteCancel').disabled, false);
  assert.deepEqual(plain(h.state.query), { source: 'random', folder: '__all__' });
  assert.equal(h.elements.get('artworkFolderFilter').value, '__all__'); assert.equal(h.elements.get('artworkSourceSummary').textContent, '范围：随机工作台');
  assert.deepEqual(plain(original.folder_ids), ['default']); assert.deepEqual(plain(other.folder_ids), ['default']);
  assert.equal(h.state.folders[0].count, 2); assert.equal(h.state.selected.size, 0); assert.deepEqual(loads, [1, 1]);
  assert.equal(h.elements.get('artworkFolderDeleteConfirm').hidden, true);
});

test('partial permanent deletion stays in its inline view with only failures selected', async () => {
  const items = [record(1), record(2)], h = setup({ historyPage: 1, historyRequest: 0, historyDirty: false, detailContext: null,
    historyItems: items, singleGalleryDeletedMany: async () => null, loadHistory: async () => {},
    request: async () => ({ results: [{ id: 1, record_deleted: true, file_deleted: true }, { id: 2, record_deleted: false, file_deleted: true, error: '提交失败' }] }),
  });
  const host = surface(h);
  for (const id of ['artworkDeleteSummary', 'artworkDeleteMode', 'artworkDeleteResults', 'artworkDeleteWarning', 'artworkDeleteSubmit']) h.elements.set(id, element({ value: 'file', textContent: '' }));
  h.ctx.artworkLoadFolders = async () => { throw new Error('暂时离线'); };
  h.state.mount = h.manager(); h.state.panelReturn = 'library'; h.state.deleteRecords = items;
  items.forEach(item => h.state.selected.set(String(item.id), item));
  h.ctx.artworkSetView('delete'); await h.ctx.artworkConfirmDelete({ preventDefault() {} });
  assert.equal(host.open, true); assert.equal(host.dataset.artworkView, 'delete');
  assert.equal(h.elements.get('artworkDeletePanel').hidden, false); assert.equal(h.elements.get('artworkLibraryView').hidden, true);
  assert.deepEqual([...h.state.selected.keys()], ['2']); assert.deepEqual(plain(h.state.deleteRecords.map(item => item.id)), [2]);
  assert.match(h.elements.get('artworkDeleteResults').textContent, /原文件已删除，记录未删除/);
  assert.match(h.elements.get('artworkDeleteResults').textContent, /收藏夹数量刷新失败/);
  assert.equal(h.elements.get('artworkDeleteSubmit').disabled, false); assert.equal(h.state.busy, false);
});

test('committed records leave local history and counts even when every refresh fails', async () => {
  const a = record(1, ['trip']), b = record(2, ['trip']), c = record(3, ['trip']);
  const removed = [], children = [a, b, c].map(item => ({ dataset: { recordId: String(item.id) }, remove() { removed.push(item.id); } }));
  const h = setup({ historyItems: [a, b, c], historyPage: 1, historyPages: 2, historyRequest: 0, historyDirty: false, detailContext: null,
    ui: { gallery: { children }, historyCount: { textContent: '28 张图片' }, pageLabel: {}, prevPage: {}, nextPage: {}, emptyState: {} },
    singleGalleryDeletedMany: async () => { throw new Error('single offline'); }, loadHistory: async () => { throw new Error('history offline'); },
  });
  h.state.query = { source: 'random', folder: 'trip' };
  h.state.selected.set('1', a); h.state.selected.set('2', b); h.state.selected.set('25', record(25, ['trip']));
  h.ctx.historyCache.set(1, { page: 1, total: 28, items: [a, b, c] });
  await assert.rejects(h.ctx.artworkApplyDeleted([1, 25]), /single offline/);
  assert.deepEqual(plain(h.ctx.historyItems.map(item => item.id)), [2, 3]);
  assert.deepEqual(removed, [1]); assert.deepEqual([...h.state.selected.keys()], ['2']);
  assert.equal(h.ctx.ui.historyCount.textContent, '26 张图片'); assert.equal(h.ctx.historyCache.size, 0);
  assert.equal(h.ctx.historyDirty, true);
});

test('mutation completion re-enables folder editing while keeping readonly writes disabled', () => {
  const h = setup(), navigate = element(), edit = element();
  navigate.classList.add('artwork-folder-link');
  h.elements.set('artworkFolderList', { querySelectorAll: () => [navigate, edit] });
  for (const id of ['artworkFolderEdit', 'artworkFolderSave', 'artworkFolderNew', 'artworkFolderCancel', 'artworkFolderName', 'artworkFolderDeleteSubmit', 'artworkFolderDeleteCancel']) h.elements.set(id, element());
  for (const id of ['artworkBatchToolbar', 'artworkBatchActions', 'artworkSelectionToggle', 'artworkSelectionCount', 'artworkBatchFavorite', 'artworkBatchDelete', 'artworkSelectionClear', 'artworkBatchRemove', 'artworkSelectPage', 'historyWorkbenchFilter', 'artworkFolderFilter']) h.elements.set(id, element());
  h.ctx.ui = { gallery: { children: [] } }; h.state.ready = true;
  h.state.busy = true; h.ctx.artworkRenderSelection();
  assert.equal(h.elements.get('artworkFolderEdit').disabled, true); assert.equal(navigate.disabled, true); assert.equal(edit.disabled, true);
  h.state.busy = false; h.ctx.artworkRenderSelection();
  assert.equal(h.elements.get('artworkFolderEdit').disabled, false); assert.equal(navigate.disabled, false); assert.equal(edit.disabled, false);
  h.ctx.config = { storage_status: { history: { read_only: true } } }; h.ctx.artworkRenderSelection();
  assert.equal(h.elements.get('artworkFolderEdit').disabled, true); assert.equal(navigate.disabled, false); assert.equal(edit.disabled, true);
});
