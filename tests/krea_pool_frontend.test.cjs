const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../static/krea-pool.js'), 'utf8');
const app = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
const html = fs.readFileSync(path.join(__dirname, '../static/index.html'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
function element(tag = 'div', id = '') {
  const handlers = {}, attributes = {}, classes = new Set();
  return { tagName: tag.toUpperCase(), id, value: '', textContent: '', hidden: false, disabled: false, checked: false, children: [], dataset: {}, files: [], open: false,
    classList: { add: (...names) => names.forEach(name => classes.add(name)), remove: (...names) => names.forEach(name => classes.delete(name)), contains: name => classes.has(name), toggle(name, on) { if (on ?? !classes.has(name)) classes.add(name); else classes.delete(name); } },
    style: { setProperty() {}, removeProperty() {} },
    setAttribute(name, value) { attributes[name] = String(value); }, removeAttribute(name) { delete attributes[name]; }, getAttribute(name) { return attributes[name]; },
    toggleAttribute(name, force) { if (force ?? !(name in attributes)) attributes[name] = ''; else delete attributes[name]; },
    append(...nodes) { for (const node of nodes) { this.children.push(node); node.parentNode = this; } },
    replaceChildren(...nodes) { this.children = []; this.append(...nodes); },
    querySelectorAll(selector) { const all = this.children.flatMap(node => [node, ...(node.querySelectorAll?.('*') || [])]); return selector === 'input:checked' ? all.filter(node => node.tagName === 'INPUT' && node.checked) : all; },
    closest() { return this.fieldset ||= { disabled: false }; }, focus() { this.focused = true; },
    addEventListener(name, fn) { (handlers[name] ||= []).push(fn); },
    async emit(name) { await Promise.all((handlers[name] || []).map(fn => fn({ target: this, preventDefault() {} }))); },
    click() { if (this.tagName === 'INPUT') { this.checked = !this.checked; return this.emit('change'); } return this.emit('click'); },
  };
}
function setup({ request, initialize = true } = {}) {
  let profile = 'krea2', persists = 0;
  const elements = {}, calls = [], messages = [], dialogs = [], timers = [];
  const get = id => elements[id] ||= element('div', id);
  const ui = new Proxy({}, { get: (target, id) => target[id] ||= get(id), set: (target, id, value) => { target[id] = value; return true; } });
  const ctx = vm.createContext({
    ui, console, currentProfile: () => profile, sharedFavoritesState: { epoch: 0 },
    kreaPromptPool: { items: [], count: 0 }, kreaPromptPoolSelection: [], kreaPromptPoolRequest: 0,
    kreaPoolPage: 1, kreaPoolPages: 0, kreaPoolQueryCount: 0, kreaPoolTotalCount: 0, kreaPoolSearchTimer: null,
    editingKreaPromptItem: null, kreaImportRequest: 0, kreaImportPreview: null,
    document: { createElement: tag => element(tag), createDocumentFragment: () => element('fragment'), getElementById: get },
    Option: function(text, value) { return Object.assign(element('option'), { value, textContent: text }); },
    matchMedia: () => ({ matches: false, addEventListener() {} }),
    setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout() {}, confirm: () => true,
    toast: text => messages.push(text), schedulePersist: () => persists++, updatePoolOverview() {}, applyStorageProtection() {}, animatePoolSurface() {},
    motionDialog: (dialog, open) => { dialog.open = open; dialogs.push({ id: dialog.id, open }); },
    sharedFavoritesChanged() {}, sharedFavoriteOpen() {},
    request: async (url, init) => {
      const call = { url, method: init?.method || 'GET', body: init?.body ? JSON.parse(init.body) : null }; calls.push(call);
      if (request) { const result = request(call, ctx); if (result !== undefined) return result; }
      if (url === '/api/prompt-pools/query') return { items: [], groups: [], count: 0, pool_count: 0, page: 1, pages: 0 };
      if (url.includes('/groups')) return { groups: [] };
      if (url.startsWith('/api/favorites/')) return { groups: [{ id: 'default', name: '我的收藏' }], items: [] };
      return {};
    },
  });
  vm.runInContext(source, ctx);
  if (initialize) ctx.initializeKreaPromptPool();
  const run = code => vm.runInContext(code, ctx);
  return { ctx, ui, get, calls, messages, dialogs, timers, run, persists: () => persists,
    profile(value) { profile = value; ctx.sharedFavoritesState.epoch++; },
    selection(ids) { ctx.kreaPromptPoolSelection = ids; },
  };
}
function importDraft(ui, mode = 'append') {
  ui.kreaPromptPoolText.value = JSON.stringify({ version: 1, items: [{ section: 'prompt', title: '雨中街道', subtitle: '霓虹', prompt: 'A rainy street.' }] });
  ui.kreaPromptImportFormat.value = 'auto'; ui.kreaPromptImportMode.value = mode; ui.kreaPromptImportBundle.value = '城市';
}
const previewRows = () => ({ rows: [{ row: 1, status: 'new', action: 'create', item: { title: '雨中街道', subtitle: '霓虹', prompt: 'A rainy street.' }, groups: [] }], summary: { new: 1, conflict: 0, error: 0 } });

test('Krea UI references resolve to real DOM controls and app initializes the module once', () => {
  const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]));
  const refs = new Set([...source.matchAll(/\bui\.([A-Za-z][A-Za-z0-9_]*)/g)].map(match => match[1]));
  for (const match of source.matchAll(/\bon\("([^"]+)"/g)) refs.add(match[1]);
  for (const id of refs) assert.ok(ids.has(id), `Missing #${id}`);
  assert.equal((app.match(/initializeKreaPromptPool\(\)/g) || []).length, 1);
  assert.ok(html.indexOf('/static/krea-pool.js') < html.indexOf('/static/app.js'));
  for (const match of source.matchAll(/^(?:async )?function (\w+)\(/gm)) assert.ok(!app.includes(`function ${match[1]}(`), `app.js shadows ${match[1]}`);
});

test('filtered queries send complete selections via POST and all-mode clears every filter', async () => {
  const ui = setup(); ui.selection(Array.from({ length: 1200 }, (_, i) => `id-${i}`));
  ui.ui.kreaPoolSearch.value = ' city '; ui.ui.kreaPoolSort.value = 'favorite-first'; ui.ui.kreaPoolSelectedOnly.checked = true;
  ui.run('kreaPoolView.collection="favorites"; kreaPoolView.customGroup="custom"');
  await ui.ctx.queryKreaPool(ui.ctx.kreaQuery());
  assert.equal(ui.calls[0].method, 'POST'); assert.equal(ui.calls[0].body.selected_ids.length, 1200);
  assert.deepEqual([ui.calls[0].body.q, ui.calls[0].body.collection, ui.calls[0].body.custom_group], ['city', 'favorites', 'custom']);
  const all = plain(ui.ctx.kreaQuery({ all: true }));
  assert.deepEqual([all.q, all.collection, all.custom_group, all.sort, all.selected_only], ['', '', '', '', false]);
});

test('late pool reads cannot populate another profile or replace a newer query', async () => {
  const gates = [];
  const ui = setup({ request: call => { if (call.url === '/api/prompt-pools/query') { const gate = deferred(); gates.push(gate); return gate.promise; } } });
  const first = ui.ctx.loadKreaPromptPool(); ui.profile('anima');
  gates[0].resolve({ items: [{ id: 'old' }], count: 1, pool_count: 1, pages: 1 }); await first;
  assert.equal(ui.ctx.kreaPromptPool.count, 0);
  ui.profile('krea2'); const stale = ui.ctx.loadKreaPromptPool(), fresh = ui.ctx.loadKreaPromptPool();
  gates[2].resolve({ items: [], count: 7, pool_count: 7, pages: 1 }); await fresh;
  gates[1].resolve({ items: [], count: 99, pool_count: 99, pages: 1 }); await stale;
  assert.equal(ui.ctx.kreaPromptPool.count, 7);
});

test('failed legacy-favorite ensure does not select any batch item or enable generation', async () => {
  const ui = setup({ request: call => { if (call.url.endsWith('/ensure-many')) throw new Error('保存失败'); } });
  ui.selection(['existing']); ui.ui.krea_prompt_pool_enabled.checked = false;
  await ui.ctx.selectKreaItems([{ id: 'new', title: '名称', subtitle: '说明', text: 'New prose.', in_pool: false }, { id: 'already-pooled', in_pool: true }]);
  assert.deepEqual(plain(ui.ctx.kreaPromptPoolSelection), ['existing']); assert.equal(ui.ui.krea_prompt_pool_enabled.checked, false);
  assert.equal(ui.persists(), 0); assert.match(ui.messages[0], /保存失败/); assert.equal(ui.run('kreaPoolView.busy'), false);
});

test('legacy ensure completion after leaving and returning cannot select the new profile draft', async () => {
  const gate = deferred(), ui = setup({ request: call => call.url.endsWith('/ensure-many') ? gate.promise : undefined });
  ui.selection(['before']); const pending = ui.ctx.selectKreaItems([{ id: 'legacy', title: '名称', subtitle: '说明', text: 'Legacy prose.', in_pool: false }]);
  ui.profile('anima'); ui.profile('krea2'); ui.selection(['restored']); ui.ui.krea_prompt_pool_enabled.checked = false;
  gate.resolve({ items: [{ id: 'legacy' }] }); await pending;
  assert.deepEqual(plain(ui.ctx.kreaPromptPoolSelection), ['restored']); assert.equal(ui.ui.krea_prompt_pool_enabled.checked, false);
});

test('editing only display metadata preserves selection and the enabled switch', async () => {
  const ui = setup({ request: call => call.url === '/api/prompt-pools/items' ? { id: 'stable' } : undefined });
  ui.selection(['stable', 'other']); ui.ctx.editingKreaPromptItem = { id: 'stable', text: 'Original prose.' };
  ui.ui.kreaPromptItemName.value = '新名称'; ui.ui.kreaPromptItemSubtitle.value = '新说明'; ui.ui.kreaPromptItemText.value = 'Original prose.';
  ui.ui.krea_prompt_pool_enabled.checked = false;
  await ui.ctx.saveKreaPromptItem({ preventDefault() {} });
  assert.deepEqual(plain(ui.ctx.kreaPromptPoolSelection), ['stable', 'other']); assert.equal(ui.ui.krea_prompt_pool_enabled.checked, false);
  const save = ui.calls.find(call => call.url === '/api/prompt-pools/items');
  assert.equal(save.method, 'PUT'); assert.deepEqual([save.body.title, save.body.subtitle, save.body.text], ['新名称', '新说明', 'Original prose.']);
});

test('new custom prompt is saved without being selected or enabling the pool', async () => {
  const ui = setup({ request: call => call.url === '/api/prompt-pools/items' ? { id: 'new' } : undefined });
  ui.selection(['existing']); ui.ui.kreaPromptItemName.value = '新名称'; ui.ui.kreaPromptItemText.value = 'New prose.';
  await ui.ctx.saveKreaPromptItem({ preventDefault() {} });
  assert.deepEqual(plain(ui.ctx.kreaPromptPoolSelection), ['existing']); assert.equal(ui.ui.krea_prompt_pool_enabled.checked, false);
});

test('changing import content invalidates preview and ignores its late response', async () => {
  const gate = deferred(), ui = setup({ request: call => call.url.endsWith('/import/preview') ? gate.promise : undefined }); importDraft(ui.ui);
  const pending = ui.ctx.previewKreaPromptImport(); ui.ui.kreaPromptPoolText.value = 'different'; await ui.ui.kreaPromptPoolText.emit('input');
  gate.resolve(previewRows()); await pending;
  assert.equal(ui.ctx.kreaImportPreview, null); assert.equal(ui.ui.kreaPromptImportCommit.disabled, true);
});

test('commit with a changed fingerprint only previews again instead of writing', async () => {
  const ui = setup({ request: call => call.url.endsWith('/import/preview') ? previewRows() : undefined }); importDraft(ui.ui);
  await ui.ctx.previewKreaPromptImport(); ui.ui.kreaPromptImportBundle.value = '另一个大项';
  await ui.ctx.commitKreaPromptImport();
  assert.equal(ui.calls.filter(call => call.url.endsWith('/import/preview')).length, 2);
  assert.equal(ui.calls.filter(call => call.url.endsWith('/import')).length, 0);
});

test('replacement keeps only surviving selections and never selects imported ids', async () => {
  const ui = setup({ request: call => call.url.endsWith('/import/preview') ? previewRows() : call.url.endsWith('/import') ? { valid_ids: ['survives', 'new'], imported: 1, imported_ids: ['new'] } : undefined });
  importDraft(ui.ui, 'replace'); ui.selection(['survives', 'removed']); ui.ui.krea_prompt_pool_enabled.checked = true;
  await ui.ctx.previewKreaPromptImport(); await ui.ctx.commitKreaPromptImport();
  assert.deepEqual(plain(ui.ctx.kreaPromptPoolSelection), ['survives']); assert.equal(ui.ui.krea_prompt_pool_enabled.checked, true);
  assert.deepEqual(ui.calls.find(call => call.url.endsWith('/import')).body.targetGroupIds, []);
  await ui.ctx.reconcileKreaSelection([]); assert.deepEqual(plain(ui.ctx.kreaPromptPoolSelection), []); assert.equal(ui.ui.krea_prompt_pool_enabled.checked, false);
});

test('selection reconciliation cannot modify Anima after its pool read completes', async () => {
  const gate = deferred(), ui = setup({ request: call => call.url === '/api/prompt-pools/query' ? gate.promise : undefined });
  ui.selection(['krea']); const pending = ui.ctx.reconcileKreaSelection();
  ui.profile('anima'); ui.selection(['anima-draft']); ui.ui.krea_prompt_pool_enabled.checked = true;
  gate.resolve({ items: [], count: 0, pool_count: 0, pages: 0 }); await pending;
  assert.deepEqual(plain(ui.ctx.kreaPromptPoolSelection), ['anima-draft']); assert.equal(ui.ui.krea_prompt_pool_enabled.checked, true);
});

test('every import option change requires a new preview and replace disables target groups', async () => {
  const ui = setup({ request: call => call.url.endsWith('/import/preview') ? previewRows() : undefined }); importDraft(ui.ui);
  for (const [id, event] of [['kreaPromptImportBundle', 'input'], ['kreaPromptImportFormat', 'change'], ['kreaPromptImportGroups', 'change'], ['kreaPromptImportMode', 'change']]) {
    await ui.ctx.previewKreaPromptImport(); assert.ok(ui.ctx.kreaImportPreview);
    await ui.ui[id].emit(event); assert.equal(ui.ctx.kreaImportPreview, null, id); assert.equal(ui.ui.kreaPromptImportCommit.disabled, true, id);
  }
  ui.ui.kreaPromptImportMode.value = 'replace'; await ui.ui.kreaPromptImportMode.emit('change');
  assert.equal(ui.ui.kreaPromptImportGroups.closest('fieldset').disabled, true);
});

test('bulk selection ignores a delayed result from a profile that has been left', async () => {
  const gate = deferred(), ui = setup({ request: call => call.url === '/api/prompt-pools/query' ? gate.promise : undefined });
  const pending = ui.ui.kreaPoolSelectAll.emit('click');
  ui.profile('anima'); ui.profile('krea2'); ui.selection(['fresh']);
  gate.resolve({ items: [{ id: 'old-result', in_pool: true }], count: 1, pool_count: 1, pages: 1 }); await pending;
  assert.deepEqual(plain(ui.ctx.kreaPromptPoolSelection), ['fresh']);
});

test('a completed item save cannot close a newer item editor in the same profile', async () => {
  const gate = deferred(), ui = setup({ request: call => call.url === '/api/prompt-pools/items' ? gate.promise : undefined });
  ui.ctx.openKreaPromptItemDialog({ id: 'first', title: '第一个', subtitle: '说明', text: 'First prose.' });
  const pending = ui.ctx.saveKreaPromptItem({ preventDefault() {} });
  await ui.ui.kreaPromptItemCancel.emit('click');
  ui.ctx.openKreaPromptItemDialog({ id: 'second', title: '第二个', subtitle: '说明', text: 'Second prose.' });
  gate.resolve({ id: 'first' }); await pending;
  assert.equal(ui.ctx.editingKreaPromptItem?.id, 'second'); assert.equal(ui.ui.kreaPromptItemDialog.open, true);
});

test('reading a newly selected import file immediately revokes the old preview', async () => {
  const gate = deferred(), ui = setup({ request: call => call.url.endsWith('/import/preview') ? previewRows() : undefined }); importDraft(ui.ui);
  await ui.ctx.previewKreaPromptImport(); assert.equal(ui.ui.kreaPromptImportCommit.disabled, false);
  ui.ui.kreaPromptPoolFile.files = [{ name: 'next.json', text: () => gate.promise }];
  const pending = ui.ui.kreaPromptPoolFile.emit('change');
  assert.equal(ui.ctx.kreaImportPreview, null); assert.equal(ui.ui.kreaPromptImportCommit.disabled, true);
  gate.resolve('{"items":[]}'); await pending;
});

test('file read errors are shown in the import dialog and cannot leave a writable old preview', async () => {
  const ui = setup({ request: call => call.url.endsWith('/import/preview') ? previewRows() : undefined }); importDraft(ui.ui);
  await ui.ctx.previewKreaPromptImport();
  ui.ui.kreaPromptPoolFile.files = [{ name: 'unreadable.json', text: async () => { throw new Error('文件无法读取'); } }];
  await ui.ui.kreaPromptPoolFile.emit('change');
  assert.equal(ui.ctx.kreaImportPreview, null); assert.equal(ui.ui.kreaPromptImportCommit.disabled, true);
  assert.match(ui.ui.kreaPromptImportHint.textContent, /文件无法读取/);
});

test('late file reads cannot replace inputs after leaving and returning to Krea', async () => {
  const gate = deferred(), ui = setup({ request: call => call.url.endsWith('/import/preview') ? previewRows() : undefined });
  ui.ui.kreaPromptPoolFile.files = [{ name: 'old.json', text: () => gate.promise }];
  const pending = ui.ui.kreaPromptPoolFile.emit('change');
  ui.profile('anima'); ui.profile('krea2'); ui.ui.kreaPromptPoolText.value = 'new input';
  gate.resolve('old file input'); await pending;
  assert.equal(ui.ui.kreaPromptPoolText.value, 'new input');
});

test('old import completion preserves a newly opened import dialog and its preview', async () => {
  const gate = deferred(), ui = setup({ request: call => call.url.endsWith('/import/preview') ? previewRows() : call.url.endsWith('/import') ? gate.promise : undefined });
  ui.ctx.openKreaPromptImportDialog(); importDraft(ui.ui); await ui.ctx.previewKreaPromptImport();
  const pending = ui.ctx.commitKreaPromptImport();
  await ui.ui.kreaPromptImportCancel.emit('click'); ui.ctx.openKreaPromptImportDialog(); importDraft(ui.ui); ui.ui.kreaPromptImportBundle.value = '新文件';
  await ui.ctx.previewKreaPromptImport(); const newerPreview = ui.ctx.kreaImportPreview;
  gate.resolve({ imported: 1 }); await pending;
  assert.equal(ui.ctx.kreaImportPreview, newerPreview); assert.equal(ui.ui.kreaPromptImportDialog.open, true);
});

test('import conflict actions operate on the actual preview row and errors block confirmation', async () => {
  const result = previewRows(); result.rows[0].status = 'conflict'; result.rows[0].action = 'skip'; result.summary = { conflict: 1, new: 0, error: 0 };
  const ui = setup({ request: call => call.url.endsWith('/import/preview') ? result : undefined }); importDraft(ui.ui);
  await ui.ctx.previewKreaPromptImport(); assert.equal(ui.ui.kreaPromptImportCommit.disabled, true);
  const action = ui.ui.kreaPromptImportRows.children[0].children[2]; action.value = 'overwrite'; await action.emit('change');
  assert.equal(ui.ctx.kreaImportPreview.rows[0].action, 'overwrite'); assert.equal(ui.ui.kreaPromptImportCommit.disabled, false);
  action.value = 'skip'; await action.emit('change'); assert.equal(ui.ui.kreaPromptImportCommit.disabled, true);
  result.rows.push({ status: 'error', action: 'skip', item: {}, error: '正文缺失' }); result.summary.error = 1;
  await ui.ctx.previewKreaPromptImport(); assert.equal(ui.ui.kreaPromptImportCommit.disabled, true);
  assert.match(ui.ui.kreaPromptImportHint.textContent, /修正错误/);
});
