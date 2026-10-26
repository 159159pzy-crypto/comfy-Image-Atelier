const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../static/image-cleanup.js'), 'utf8');
const id = character => character.repeat(64);
const plain = value => JSON.parse(JSON.stringify(value));
function setup(extra = {}) {
  const stored = new Map(), errors = { textContent: '' };
  const ctx = vm.createContext({ localStorage: { getItem: key => stored.get(key) || null }, document: { getElementById: () => errors }, setTimeout, clearTimeout, ...extra });
  vm.runInContext(source, ctx);
  return { ctx, stored, errors };
}
test('leases include both model drafts plus current image and running reverse input', () => {
  const h = setup({ singleWorkbenchState: { ready: true, single: { input_image_id: id('c') }, drafts: { single: { anima: { settings: { single: { input_image_id: id('a') } } }, krea2: { settings: { single: { input_image_id: id('b') } } } }, random: {} } }, promptReverseState: { ready: true, drafts: { anima: { imageId: id('d'), busy: true, jobImageId: id('e') }, krea2: { imageId: id('f') } } } });
  assert.deepEqual(plain(h.ctx.imageCleanupCollectReferences()), { input_image_ids: [id('a'), id('b'), id('c')], prompt_image_ids: [id('d'), id('e'), id('f')] });
});
test('before initialization leases read saved drafts and reject malformed asset IDs', () => {
  const h = setup({ singleWorkbenchState: { ready: false }, promptReverseState: { ready: false } });
  h.stored.set('anima-studio:workbenches:v1', JSON.stringify({ drafts: { single: { anima: { settings: { single: { input_image_id: id('a') } } }, krea2: { settings: { single: { input_image_id: '../file' } } } } } }));
  h.stored.set('anima-studio:prompt-reverse:v2', JSON.stringify({ drafts: { anima: { imageId: id('b') } } }));
  assert.deepEqual(plain(h.ctx.imageCleanupCollectReferences()), { input_image_ids: [id('a')], prompt_image_ids: [id('b')] });
  h.stored.set('anima-studio:workbenches:v1', '{broken'); assert.deepEqual(plain(h.ctx.imageCleanupCollectReferences().input_image_ids), []);
});
test('clearing workbench drafts in random mode does not lease stale single editor memory forever', () => {
  const h = setup({ singleWorkbenchState: { ready: true, mode: 'random', drafts: { single: {}, random: {} }, single: { input_image_id: id('a') } } });
  assert.deepEqual(plain(h.ctx.imageCleanupCollectReferences().input_image_ids), []);
});
test('complete lease replacements serialize and read newest references when actually sent', async () => {
  let resolve; const pending = new Promise(yes => { resolve = yes; }), calls = [];
  const state = { ready: true, drafts: {}, single: { input_image_id: id('a') } };
  const h = setup({ singleWorkbenchState: state, request: async (url, options) => { calls.push({ url, body: JSON.parse(options.body) }); if (calls.length === 1) return pending; return {}; } });
  const first = h.ctx.imageCleanupSyncLeases(); await Promise.resolve();
  state.single.input_image_id = id('b'); const second = h.ctx.imageCleanupSyncLeases(); await Promise.resolve();
  assert.equal(calls.length, 1); resolve({}); await first; await second;
  assert.equal(calls.length, 2); assert.deepEqual(calls[0].body.input_image_ids, [id('a')]); assert.deepEqual(calls[1].body.input_image_ids, [id('b')]);
  assert.equal(calls[0].body.session_id, calls[1].body.session_id);
  assert.equal(calls[0].url, '/api/image-cleanup/leases');
});
test('failed lease synchronization remains retryable and never clears user text or image IDs', async () => {
  let failing = true; const state = { ready: true, single: { input_image_id: id('a') }, drafts: {} };
  const h = setup({ singleWorkbenchState: state, request: async () => { if (failing) throw new Error('offline'); return { accepted: true }; } });
  assert.equal(await h.ctx.imageCleanupSyncLeases(), null); assert.equal(h.errors.textContent, '', 'background failure never exposes homepage housekeeping');
  assert.match(vm.runInContext('imageCleanupState.leaseError', h.ctx), /offline/); assert.equal(state.single.input_image_id, id('a'));
  failing = false; assert.deepEqual(plain(await h.ctx.imageCleanupSyncLeases()), { accepted: true });
  assert.equal(vm.runInContext('imageCleanupState.leaseError', h.ctx), '');
});
test('cleanup result separates concise summary from on-demand technical details', () => {
  const h = setup(); const report = { finished_at: 'now', deleted_files: 3, freed_bytes: 1048576, skipped_files: 5, comfy_status: '远程 ComfyUI 已跳过', errors: ['文件占用'] };
  const result = h.ctx.imageCleanupRenderResult(report), details = h.ctx.imageCleanupRenderDetails(report);
  assert.match(result, /1.0 MiB/); assert.doesNotMatch(result, /跳过|ComfyUI|文件占用/);
  assert.match(details, /跳过 5/); assert.match(details, /远程 ComfyUI/); assert.match(details, /文件占用/);
  const timestamp = 1793000000;
  assert.ok(h.ctx.imageCleanupRenderResult({ finished_at: timestamp }).includes(new Date(timestamp * 1000).toLocaleString()));
});
test('manual cleanup aborts before destructive endpoint when draft lease sync fails', async () => {
  const controls = new Map(); const get = id => { if (!controls.has(id)) controls.set(id, { textContent: '', disabled: false }); return controls.get(id); };
  const calls = []; const h = setup({ artworkElement: get, request: async url => { calls.push(url); throw new Error('offline'); }, document: { getElementById: get } });
  vm.runInContext('imageCleanupState.status = { read_only: false }', h.ctx);
  await h.ctx.imageCleanupRun();
  assert.deepEqual(calls, ['/api/image-cleanup/leases']);
  assert.match(get('imageCleanupError').textContent, /未执行清理/);
  assert.equal(get('imageCleanupRun').disabled, false);
});

function browserHarness({ withView = true, request: send } = {}) {
  const elements = new Map(), intervals = [], windowEvents = new Map(), documentEvents = new Map(), calls = [];
  const get = id => {
    if (!withView) return null;
    if (!elements.has(id)) elements.set(id, { textContent: '', disabled: false, open: false, hidden: true, events: new Map(), addEventListener(name, handler) { this.events.set(name, handler); } });
    return elements.get(id);
  };
  const status = { enabled: true, retention_days: 7, read_only: false, counts: { input: 1, prompt: 2, comfy_input: 3 }, bytes: { total: 1048576 } };
  const h = setup({ document: { hidden: false, getElementById: get, addEventListener: (event, handler) => documentEvents.set(event, handler) },
    window: { addEventListener: (event, handler) => windowEvents.set(event, handler) },
    setInterval: (handler, milliseconds) => { intervals.push({ handler, milliseconds }); return intervals.length; },
    request: async (url, options) => { calls.push({ url, options }); return send ? send(url, options) : url.endsWith('/leases') ? {} : status; } });
  return { ...h, get, elements, intervals, windowEvents, documentEvents, calls, status };
}
const settled = () => new Promise(resolve => setImmediate(resolve));

test('closed cleanup settings never fetch housekeeping status while draft protection renews', async () => {
  const h = browserHarness(); h.ctx.initializeImageCleanup(); h.ctx.initializeImageCleanup(); await settled();
  assert.equal(h.intervals.length, 1); assert.equal(h.intervals[0].milliseconds, 60000);
  assert.deepEqual(h.calls.map(item => item.url), ['/api/image-cleanup/leases']);
  await h.intervals[0].handler(); await h.windowEvents.get('focus')(); await h.windowEvents.get('pageshow')();
  h.documentEvents.get('visibilitychange')(); await settled();
  assert.equal(h.calls.length, 5); assert.ok(h.calls.every(item => item.url.endsWith('/leases')));
  assert.equal(h.get('imageCleanupSection').open, false); assert.equal(h.get('imageCleanupRun').disabled, true);
});

test('missing cleanup view does not prevent initialization or minute and focus renewals', async () => {
  const h = browserHarness({ withView: false }); h.ctx.initializeImageCleanup(); await settled();
  await h.intervals[0].handler(); await h.windowEvents.get('focus')();
  assert.equal(h.calls.length, 3); assert.ok(h.calls.every(item => item.url === '/api/image-cleanup/leases'));
  assert.equal(vm.runInContext('imageCleanupState.ready', h.ctx), true);
  assert.equal(vm.runInContext('imageCleanupState.viewReady', h.ctx), false);
});

test('cleanup page entry explicitly loads status without a disclosure toggle listener', async () => {
  const h = browserHarness(); h.ctx.initializeImageCleanup(); await settled();
  const section = h.get('imageCleanupSection');
  assert.equal(section.events.has('toggle'), false);
  await h.ctx.imageCleanupOpen();
  assert.deepEqual(h.calls.map(item => item.url), ['/api/image-cleanup/leases', '/api/image-cleanup']);
  assert.equal(h.get('imageCleanupDays').value, 7); assert.equal(h.get('imageCleanupRun').disabled, false);
  assert.match(h.get('imageCleanupStats').textContent, /6 个副本/);
  section.hidden = true; await settled();
  assert.equal(h.calls.length, 2);
});

test('initial status failure keeps cleanup controls disabled without interfering with renewals', async () => {
  const h = browserHarness({ request: async url => { if (url.endsWith('/leases')) return {}; throw new Error('offline'); } });
  h.ctx.initializeImageCleanup(); await settled(); await h.ctx.imageCleanupOpen();
  assert.match(h.get('imageCleanupError').textContent, /offline/); assert.equal(h.get('imageCleanupRun').disabled, true);
  await h.intervals[0].handler(); assert.equal(h.calls.at(-1).url, '/api/image-cleanup/leases');
});

test('read-only cleanup view disables writes and keeps detailed reasons folded', async () => {
  const h = browserHarness(); h.ctx.imageCleanupRender({ ...h.status, read_only: true, reason: '历史存储受到保护', last_run: { skipped_files: 2, comfy_status: '远程目录', errors: ['无法验证目录'] } });
  assert.equal(h.get('imageCleanupRun').disabled, true); assert.equal(h.get('imageCleanupDetails').hidden, false); assert.equal(h.get('imageCleanupDetails').open, false);
  assert.doesNotMatch(h.get('imageCleanupResult').textContent, /远程目录|无法验证/);
  assert.match(h.get('imageCleanupDetailsText').textContent, /远程目录|无法验证/);
  await h.ctx.imageCleanupRun(); await h.ctx.imageCleanupSave({ preventDefault() {} }); assert.equal(h.calls.length, 0);
});

test('cleanup completion survives a failed space-statistics refresh', async () => {
  const h = browserHarness({ request: async url => {
    if (url.endsWith('/leases')) return {};
    if (url.endsWith('/run')) return { deleted_files: 4, freed_bytes: 1048576, errors: ['一个副本被占用'] };
    throw new Error('offline');
  } });
  h.ctx.imageCleanupRender(h.status); await h.ctx.imageCleanupRun();
  assert.match(h.get('imageCleanupResult').textContent, /删除 4/); assert.match(h.get('imageCleanupResult').textContent, /1.0 MiB/);
  assert.match(h.get('imageCleanupError').textContent, /清理已完成/); assert.match(h.get('imageCleanupDetailsText').textContent, /被占用/);
  assert.equal(h.get('imageCleanupRun').disabled, false);
});
