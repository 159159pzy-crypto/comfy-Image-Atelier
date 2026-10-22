const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../static/single-gallery.js'), 'utf8');
const appSource = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
const flush = () => new Promise(resolve => setImmediate(resolve));
function deferred() { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function record(id = 1, batchId = 'older') { return { id, batch_id: batchId, filename: `${id}.png`, sample_seed: 3, settings: { workbench: 'single' } }; }
function batch(overrides = {}) { return { id: 'batch-1', prompt_id: 'prompt-1', status: 'running', completed: 0, preview_id: 1, preview_epoch: 1, preview_node: 'sampler', settings: { workbench: 'single' }, ...overrides }; }
function history(items) { return { items, total: items.length, has_more: false, next_before_id: items.at(-1)?.id || null }; }
function response({ status = 200, batchId = 'batch-1', promptId = 'prompt-1', nodeId = 'sampler', seq = 1, epoch = 1, type = 'image/jpeg' } = {}) {
  const headers = { 'X-Preview-Batch-Id': batchId, 'X-Preview-Prompt-Id': promptId, 'X-Preview-Node-Id': nodeId, 'X-Preview-Id': String(seq), 'X-Preview-Epoch': String(epoch) };
  return { status, ok: status >= 200 && status < 300, headers: { get: key => headers[key] ?? null }, blob: async () => ({ type }) };
}
function element() {
  const attrs = new Map();
  return {
    hidden: false, disabled: false, textContent: '', writes: [],
    set src(value) { attrs.set('src', value); this.writes.push(value); }, get src() { return attrs.get('src'); },
    getAttribute: key => attrs.get(key) ?? null, setAttribute: (key, value) => attrs.set(key, String(value)),
    removeAttribute(key) { attrs.delete(key); this.writes.push(null); }, addEventListener() {},
  };
}
function setup(extra = {}) {
  const elements = new Map(), decodes = [], revoked = [], requests = [];
  let blobId = 0;
  const ctx = vm.createContext({
    console, URLSearchParams, document: { hidden: false, getElementById: id => { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); } },
    imagePreview: { context: null, phase: 'closed', preloaded: new Map() },
    URL: { createObjectURL: () => `blob:frame-${++blobId}`, revokeObjectURL: value => revoked.push(value) },
    Image: class { decode() { const pending = deferred(); decodes.push({ source: this.src, ...pending }); return pending.promise; } },
    request: async url => { requests.push(url); return history([record()]); },
    fetch: async () => response(),
    ...extra,
  });
  vm.runInContext(source, ctx);
  const state = vm.runInContext('singleGalleryState', ctx);
  Object.assign(state, { ready: true, active: true, loaded: true, items: [record()], total: 1, lastCompleted: record() });
  ctx.singleGalleryCanvas();
  const image = elements.get('singlePreviewImage');
  function running(value = batch()) { state.generationKey = `${value.id}:${value.prompt_id}`; state.frameEpoch = Number(value.preview_epoch || 0); state.batchSnapshot = value; state.busy = true; state.waitingBatch = value.id; return value; }
  function live(value = 'blob:previous') { state.live = { batchId: 'batch-1', promptId: 'prompt-1', epoch: 1, previewId: 0, src: value }; ctx.singleGalleryCanvas(); }
  return { ctx, state, elements, image, decodes, revoked, requests, running, live };
}

test('independent rewrite tasks leave the image canvas and history unchanged throughout their lifecycle', async () => {
  const h = setup();
  const previous = h.state.lastCompleted;
  for (const status of ['queued', 'running', 'stopping', 'completed', 'stopped', 'error']) {
    h.ctx.singleGalleryBatch(batch({ id: 'rewrite-job', kind: 'prompt_preview', stage: 'prompt_processing',
      status, completed: status === 'completed' ? 1 : 0, preview_id: undefined,
      settings: { kind: 'rewrite', workbench: 'single', model_profile: 'krea2' } }));
    await flush();
    assert.equal(h.state.lastCompleted, previous);
    assert.equal(h.image.src, '/api/images/1');
    assert.equal(h.state.busy, false);
    assert.equal(h.state.finishingBatch, null);
    assert.equal(h.state.error, '');
  }
  assert.equal(h.requests.length, 0);
  assert.equal(h.decodes.length, 0);
});

test('a rewrite following a generation resolves only the preceding image batch', async () => {
  const h = setup({ request: async () => history([record(2, 'batch-1'), record()]) });
  h.running(); h.live();
  h.ctx.singleGalleryBatch(batch({ id: 'rewrite-job', kind: 'prompt_preview', stage: 'prompt_processing',
    preview_id: undefined, settings: { kind: 'rewrite', workbench: 'single' } }));
  await flush();
  assert.equal(h.state.finishingBatch, 'batch-1');
  assert.equal(h.decodes[0].source, '/api/images/2');
  h.decodes[0].resolve(); await flush();
  h.ctx.singleGalleryBatch(batch({ id: 'rewrite-job', kind: 'prompt_preview', status: 'completed',
    completed: 1, preview_id: undefined, settings: { kind: 'rewrite', workbench: 'single' } }));
  await flush();
  assert.equal(h.image.src, '/api/images/2');
  assert.equal(h.state.finishingBatch, null);
  assert.equal(h.state.error, '');
  assert.equal(h.decodes.length, 1);
});

test('a preview replaces the existing frame only after successful decode', async () => {
  const h = setup(); const value = h.running(); h.live();
  const pending = h.ctx.singleGalleryFetchFrame(value);
  await flush();
  assert.equal(h.image.src, 'blob:previous');
  assert.equal(h.state.framePending, true);
  h.decodes[0].resolve(); await pending;
  assert.equal(h.image.src, 'blob:frame-1');
  assert.equal(h.state.live.previewId, 1);
  assert.deepEqual(h.revoked, ['blob:previous']);
});

test('late fetch/decode from the prior prompt cannot replace the newer task', async () => {
  let count = 0;
  const h = setup({ fetch: async () => response(++count === 1 ? {} : { batchId: 'batch-2', promptId: 'prompt-2', seq: 2 }) });
  const first = h.ctx.singleGalleryFetchFrame(h.running());
  await flush();
  h.ctx.singleGalleryBatch(batch({ id: 'batch-2', prompt_id: 'prompt-2', preview_id: 2 }));
  await flush();
  h.decodes[0].resolve(); await first;
  assert.equal(h.state.live, null);
  assert.equal(h.state.framePending, true);
  assert.ok(h.revoked.includes('blob:frame-1'));
  h.decodes[1].resolve(); await flush();
  assert.equal(h.state.live.promptId, 'prompt-2');
  assert.equal(h.image.src, 'blob:frame-2');
});

test('204, mismatched ownership and invalid content allow retry of the same frame', async () => {
  for (const failed of [response({ status: 204 }), response({ batchId: 'other' }), response({ nodeId: 'other' }), response({ epoch: 99 }), response({ type: 'text/html' })]) {
    let count = 0;
    const h = setup({ fetch: async () => ++count === 1 ? failed : response() });
    const value = h.running();
    await h.ctx.singleGalleryFetchFrame(value);
    assert.equal(h.state.requestedFrame, '');
    assert.equal(h.state.framePending, false);
    const retry = h.ctx.singleGalleryFetchFrame(value);
    await flush(); h.decodes[0].resolve(); await retry;
    assert.equal(h.state.live.previewId, 1);
  }
});

test('failed image decode preserves the last live frame and can recover', async () => {
  const h = setup(); const value = h.running(); h.live();
  const first = h.ctx.singleGalleryFetchFrame(value);
  await flush(); h.decodes[0].reject(new Error('decode failed')); await first;
  assert.equal(h.image.src, 'blob:previous');
  assert.ok(h.revoked.includes('blob:frame-1'));
  assert.equal(h.state.requestedFrame, '');
  const retry = h.ctx.singleGalleryFetchFrame(value);
  await flush(); h.decodes[1].resolve(); await retry;
  assert.equal(h.image.src, 'blob:frame-2');
});

test('a newer returned frame prevents repeated fetches for stale snapshot sequence numbers', async () => {
  let count = 0;
  const h = setup({ fetch: async () => { count++; return response({ seq: 3 }); } });
  const value = h.running();
  const pending = h.ctx.singleGalleryFetchFrame(value);
  await flush(); h.decodes[0].resolve(); await pending;
  assert.equal(h.state.live.previewId, 3);
  for (const seq of [1, 2, 3]) await h.ctx.singleGalleryFetchFrame(batch({ preview_id: seq }));
  assert.equal(count, 1);
});

test('a same-node stage transition keeps the displayed frame and rejects the previous decode', async () => {
  let count = 0;
  const h = setup({ fetch: async () => response(++count === 1 ? {} : { epoch: 2, seq: 2 }) });
  const value = h.running(); h.live();
  const first = h.ctx.singleGalleryFetchFrame(value);
  await flush();
  h.ctx.singleGalleryBatch(batch({ preview_epoch: 2, preview_id: undefined }));
  assert.equal(h.image.src, 'blob:previous');
  h.ctx.singleGalleryBatch(batch({ preview_epoch: 2, preview_id: 2 }));
  await flush();
  h.decodes[0].resolve(); await first;
  assert.equal(h.image.src, 'blob:previous');
  assert.equal(h.state.framePending, true);
  assert.ok(h.revoked.includes('blob:frame-1'));
  h.decodes[1].resolve(); await flush();
  assert.equal(h.image.src, 'blob:frame-2');
  assert.equal(h.state.live.epoch, 2);
});

test('a delayed previous-stage HTTP response cannot create a new image after a stage change', async () => {
  const pendingResponse = deferred(), urls = [];
  const h = setup({ fetch: url => { urls.push(url); return pendingResponse.promise; } });
  const value = h.running(); h.live();
  const first = h.ctx.singleGalleryFetchFrame(value);
  h.ctx.singleGalleryBatch(batch({ preview_epoch: 2, preview_id: undefined, progress_node: 'second' }));
  pendingResponse.resolve(response()); await first;
  assert.equal(h.decodes.length, 0);
  assert.equal(h.image.src, 'blob:previous');
  assert.equal(new URL(urls[0], 'http://localhost').searchParams.get('epoch'), '1');
});

test('stopping restores the previous finished image and discards a late frame', async () => {
  const h = setup(); const value = h.running(); h.live();
  const pending = h.ctx.singleGalleryFetchFrame(value);
  await flush();
  h.ctx.singleGalleryBatch(batch({ status: 'stopped', preview_id: undefined }));
  assert.equal(h.image.src, '/api/images/1');
  assert.equal(h.state.live, null);
  h.decodes[0].resolve(); await pending; await flush();
  assert.equal(h.image.src, '/api/images/1');
  assert.equal(h.state.busy, false);
});

test('completion preserves the live canvas until the matching final image decodes', async () => {
  const h = setup({ request: async () => history([record(2, 'batch-1'), record()]) });
  h.running(); h.live();
  h.ctx.singleGalleryBatch(batch({ status: 'completed', completed: 1, preview_id: undefined }));
  await flush();
  assert.equal(h.decodes[0].source, '/api/images/2');
  assert.equal(h.state.lastCompleted.id, 1);
  assert.equal(h.image.src, 'blob:previous');
  assert.equal(h.elements.get('singlePreviewOpen').disabled, true);
  h.decodes[0].resolve(); await flush();
  assert.equal(h.state.lastCompleted.id, 2);
  assert.equal(h.image.src, '/api/images/2');
  assert.equal(h.state.finishingBatch, null);
  assert.equal(h.elements.get('singlePreviewOpen').disabled, false);
  assert.ok(h.revoked.includes('blob:previous'));
});

test('completion without a live frame shows a placeholder until final decode', async () => {
  const h = setup({ request: async () => history([record(2, 'batch-1'), record()]) });
  h.ctx.singleGalleryBatch(batch({ preview_id: undefined }));
  assert.equal(h.image.getAttribute('src'), null);
  h.ctx.singleGalleryBatch(batch({ status: 'completed', completed: 1, preview_id: undefined }));
  await flush();
  assert.equal(h.state.lastCompleted.id, 1);
  assert.equal(h.image.getAttribute('src'), null);
  assert.equal(h.image.hidden, true);
  h.decodes[0].resolve(); await flush();
  assert.equal(h.image.src, '/api/images/2');
  assert.equal(h.image.hidden, false);
});

test('a missing completed record remains retryable instead of showing the old work', async () => {
  const h = setup(); h.running();
  h.ctx.singleGalleryBatch(batch({ status: 'completed', completed: 1, preview_id: undefined }));
  await flush();
  assert.equal(h.state.finishingBatch, 'batch-1');
  assert.match(h.state.error, /尚未读取到/);
  assert.equal(h.image.getAttribute('src'), null);
  assert.equal(h.elements.get('singlePreviewRetry').hidden, false);
  h.ctx.request = async () => history([record(2, 'batch-1'), record()]);
  const retry = h.ctx.singleGalleryFinishBatch('batch-1');
  await flush(); h.decodes[0].resolve(); await retry;
  assert.equal(h.image.src, '/api/images/2');
  assert.equal(h.state.error, '');
});

test('a failed final decode keeps its live frame and retries without exposing the undecoded image', async () => {
  const h = setup({ request: async () => history([record(2, 'batch-1'), record()]) });
  h.running(); h.live();
  h.ctx.singleGalleryBatch(batch({ status: 'completed', completed: 1, preview_id: undefined }));
  await flush(); h.decodes[0].reject(new Error('final decode failed')); await flush();
  assert.equal(h.image.src, 'blob:previous');
  assert.equal(h.state.lastCompleted.id, 1);
  assert.equal(h.state.finishingBatch, 'batch-1');
  const retry = h.ctx.singleGalleryFinishBatch('batch-1');
  await flush(); h.decodes[1].resolve(); await retry;
  assert.equal(h.image.src, '/api/images/2');
});

test('leaving and returning invalidates old preview decode without locking the retry', async () => {
  const h = setup(); const pending = h.ctx.singleGalleryFetchFrame(h.running());
  await flush();
  h.ctx.singleGalleryMode(false);
  h.ctx.singleGalleryMode(true);
  await flush();
  assert.equal(h.decodes.length, 2);
  h.decodes[0].resolve(); await pending;
  assert.equal(h.state.live, null);
  assert.equal(h.state.framePending, true);
  h.decodes[1].resolve(); await flush();
  assert.equal(h.image.src, 'blob:frame-2');
});

test('leaving during final decode and returning restarts final loading with new ownership', async () => {
  const h = setup({ request: async () => history([record(2, 'batch-1'), record()]) });
  h.running(); h.live();
  h.ctx.singleGalleryBatch(batch({ status: 'completed', completed: 1, preview_id: undefined }));
  await flush();
  h.ctx.singleGalleryMode(false); h.ctx.singleGalleryMode(true);
  await flush();
  assert.equal(h.decodes.length, 2);
  h.decodes[0].resolve(); await flush();
  assert.equal(h.image.getAttribute('src'), null);
  assert.ok(h.revoked.includes('blob:previous'), 'a hidden workbench releases its live frame');
  assert.equal(h.state.finishingBatch, 'batch-1');
  h.decodes[1].resolve(); await flush();
  assert.equal(h.image.src, '/api/images/2');
});

test('inactive single workbench releases its canvas and later updates cannot reattach an original', async () => {
  const h = setup();
  assert.equal(h.image.src, '/api/images/1');
  h.ctx.singleGalleryMode(false);
  assert.equal(h.image.getAttribute('src'), null);
  assert.equal(h.image.hidden, true);
  h.ctx.singleGalleryCanvas();
  h.ctx.singleGalleryBatch({ id: 'other', status: 'running', settings: { workbench: 'random' } });
  assert.equal(h.image.getAttribute('src'), null);
  h.ctx.singleGalleryMode(true);
  await flush();
  assert.equal(h.image.src, '/api/images/1');
  assert.equal(h.image.hidden, false);
});

test('a completion received while inactive loads its final image on return', async () => {
  const h = setup({ request: async () => history([record(2, 'batch-1'), record()]) });
  h.running(); h.ctx.singleGalleryMode(false);
  h.ctx.singleGalleryBatch(batch({ status: 'completed', completed: 1, preview_id: undefined }));
  await flush(); assert.equal(h.decodes.length, 0);
  h.ctx.singleGalleryMode(true);
  await flush(); h.decodes[0].resolve(); await flush();
  assert.equal(h.image.src, '/api/images/2');
});

test('an old final decode cannot replace a newly started generation', async () => {
  const h = setup({ request: async () => history([record(2, 'batch-1'), record()]) });
  h.running(); h.live();
  h.ctx.singleGalleryBatch(batch({ status: 'completed', completed: 1, preview_id: undefined }));
  await flush();
  h.ctx.singleGalleryBatch(batch({ id: 'batch-2', prompt_id: 'prompt-2', preview_id: undefined }));
  h.decodes[0].resolve(); await flush();
  assert.equal(h.state.busy, true);
  assert.equal(h.state.generationKey, 'batch-2:prompt-2');
  assert.equal(h.state.lastCompleted.id, 1);
  assert.equal(h.image.getAttribute('src'), null);
});

test('a queued random task with no preceding single output restores the last completed work', async () => {
  const h = setup(); h.running(); h.live();
  h.ctx.singleGalleryBatch({ id: 'random-next', status: 'running', settings: { workbench: 'random' } });
  await flush();
  assert.equal(h.state.finishingBatch, null);
  assert.equal(h.state.live, null);
  assert.equal(h.image.src, '/api/images/1');
});

function randomSetup(extra = {}) {
  const image = element(), decodes = [], revoked = [], urls = [];
  let blobId = 0;
  const ctx = vm.createContext({
    console, URLSearchParams, ui: { livePreview: image },
    URL: { createObjectURL: () => `blob:random-${++blobId}`, revokeObjectURL: value => revoked.push(value) },
    Image: class { decode() { const pending = deferred(); decodes.push({ source: this.src, ...pending }); return pending.promise; } },
    fetch: async url => { urls.push(url); return response(); }, ...extra,
  });
  vm.runInContext(appSource.slice(appSource.indexOf('const randomPreviewState ='), appSource.indexOf('let submitInFlight =')), ctx);
  const state = vm.runInContext('randomPreviewState', ctx);
  const render = overrides => ctx.renderRandomPreview(batch({ settings: { workbench: 'random' }, ...overrides }));
  return { ctx, state, image, decodes, revoked, urls, render };
}

test('random previews use owned uncached requests and replace only decoded frames', async () => {
  const h = randomSetup(); h.render();
  await flush();
  assert.equal(h.image.getAttribute('src'), null);
  h.decodes[0].resolve(); await flush();
  assert.equal(h.image.src, 'blob:random-1');
  const query = new URL(h.urls[0], 'http://localhost').searchParams;
  assert.equal(query.get('batch_id'), 'batch-1');
  assert.equal(query.get('prompt_id'), 'prompt-1');
  assert.equal(query.get('node_id'), 'sampler');
  assert.equal(query.get('epoch'), '1');
});

test('random same-node reentry cancels stale decode without blanking the last displayed frame', async () => {
  let count = 0;
  const h = randomSetup({ fetch: async () => response({ epoch: count < 2 ? 1 : 2, seq: ++count }) });
  h.render(); await flush(); h.decodes[0].resolve(); await flush();
  h.render({ preview_id: 2 }); await flush();
  h.render({ preview_epoch: 2, preview_id: undefined });
  assert.equal(h.image.src, 'blob:random-1');
  h.render({ preview_epoch: 2, preview_id: 3 }); await flush();
  h.decodes[2].resolve(); await flush();
  assert.equal(h.image.src, 'blob:random-3');
  h.decodes[1].resolve(); await flush();
  assert.equal(h.image.src, 'blob:random-3');
  assert.equal(h.state.pending, false);
  assert.ok(h.revoked.includes('blob:random-1'));
  assert.ok(h.revoked.includes('blob:random-2'));
});

test('random next prompt and terminal states clear the old frame and reject pending responses', async () => {
  for (const next of [{ prompt_id: 'prompt-2', preview_id: undefined }, { status: 'completed' }]) {
    const h = randomSetup();
    h.render(); await flush(); h.decodes[0].resolve(); await flush();
    const pendingResponse = deferred(); h.ctx.fetch = () => pendingResponse.promise;
    h.render({ preview_id: 2 });
    h.render(next);
    assert.equal(h.image.getAttribute('src'), null);
    assert.equal(h.image.hidden, true);
    pendingResponse.resolve(response({ seq: 2 })); await flush();
    assert.equal(h.image.getAttribute('src'), null);
    assert.equal(h.decodes.length, 1);
    assert.ok(h.revoked.includes('blob:random-1'));
  }
});

test('random failed fetch, ownership, and decode preserve the frame and allow retries', async () => {
  for (const failed of [response({ status: 204 }), response({ promptId: 'old' }), response({ epoch: 0 }), response({ type: 'text/html' }), new Error('offline')]) {
    const h = randomSetup(); h.render(); await flush(); h.decodes[0].resolve(); await flush();
    h.ctx.fetch = async () => { if (failed instanceof Error) throw failed; return failed; };
    h.render({ preview_id: 2 }); await flush();
    assert.equal(h.image.src, 'blob:random-1');
    assert.equal(h.state.pending, false);
    assert.equal(h.state.requested, '');
    h.ctx.fetch = async () => response({ seq: 2 });
    h.render({ preview_id: 2 }); await flush();
    h.decodes[1].reject(new Error('decode failed')); await flush();
    assert.equal(h.image.src, 'blob:random-1');
    h.render({ preview_id: 2 }); await flush(); h.decodes[2].resolve(); await flush();
    assert.equal(h.image.src, 'blob:random-3');
  }
});

test('random newer returned frames avoid repeated fetches from older poll snapshots', async () => {
  let count = 0;
  const h = randomSetup({ fetch: async () => { ++count; return response({ seq: 3 }); } });
  h.render(); await flush(); h.decodes[0].resolve(); await flush();
  for (const preview_id of [1, 2, 3]) { h.render({ preview_id }); await flush(); }
  assert.equal(count, 1);
});

function pollingSetup() {
  let now = 1000, visibleSingle = true;
  const pending = [], renders = [];
  const ctx = vm.createContext({
    AbortController, setTimeout, clearTimeout,
    document: { hidden: false }, currentBatch: batch(), Date: { now: () => now },
    singleWorkbenchActive: () => visibleSingle, toast() {},
    request() { const wait = deferred(); pending.push(wait); return wait.promise; },
    renderBatchPayload(value) { renders.push(value); ctx.currentBatch = value.batch; },
  });
  vm.runInContext(appSource.slice(appSource.indexOf('let pollErrorShown ='), appSource.indexOf('function card(record,')), ctx);
  return { ctx, pending, renders, time: value => { now = value; }, single: value => { visibleSingle = value; } };
}

test('batch polling never overlaps and respects single, random, idle and hidden intervals', async () => {
  const h = pollingSetup();
  const first = h.ctx.pollBatch(); h.time(1400); await h.ctx.pollBatch();
  assert.equal(h.pending.length, 1);
  h.pending[0].resolve({ batch: batch() }); await first;
  const second = h.ctx.pollBatch(); assert.equal(h.pending.length, 2);
  h.pending[1].resolve({ batch: batch() }); await second;
  h.single(false); h.time(1800); await h.ctx.pollBatch(); assert.equal(h.pending.length, 2);
  h.time(2600); const third = h.ctx.pollBatch(); assert.equal(h.pending.length, 3);
  h.pending[2].resolve({ batch: batch({ status: 'completed' }) }); await third;
  h.time(3000); await h.ctx.pollBatch(); assert.equal(h.pending.length, 3);
  h.time(7400); h.ctx.document.hidden = true; await h.ctx.pollBatch(); assert.equal(h.pending.length, 3);
  h.ctx.document.hidden = false; const fourth = h.ctx.pollBatch(); assert.equal(h.pending.length, 4);
  h.pending[3].resolve({ batch: null }); await fourth;
});

test('an old polling response cannot undo a newer submit or stop response', async () => {
  for (const replacement of [batch({ id: 'batch-2', prompt_id: 'prompt-2' }), batch({ status: 'stopping' })]) {
    const h = pollingSetup(); const pending = h.ctx.pollBatch();
    h.ctx.currentBatch = replacement;
    h.pending[0].resolve({ batch: batch() }); await pending;
    assert.equal(h.renders.length, 0);
    assert.equal(h.ctx.currentBatch, replacement);
    assert.equal(vm.runInContext('pollBatchPending', h.ctx), false);
  }
});
