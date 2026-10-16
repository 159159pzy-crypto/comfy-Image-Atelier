const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../static/single-input.js'), 'utf8');
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
function asset(id = 'image-a') { return { id, width: 120, height: 80, filename: `${id}.png`, image_url: `/api/input-images/${id}/image` }; }
function file(overrides = {}) { return { name: 'reference.png', type: 'image/png', size: 100, ...overrides }; }
function response(value, ok = true) { return { ok, json: async () => value }; }
function target() {
  const handlers = new Map(), attributes = new Map(), classes = new Set();
  return {
    hidden: false, value: '', files: [], textContent: '',
    classList: { toggle(name, on) { if (on) classes.add(name); else classes.delete(name); }, contains: name => classes.has(name) },
    addEventListener(name, handler) { if (!handlers.has(name)) handlers.set(name, []); handlers.get(name).push(handler); },
    emit(name, data = {}) {
      const event = { target: this, relatedTarget: {}, prevented: false, preventDefault() { this.prevented = true; }, ...data };
      for (const handler of handlers.get(name) || []) handler(event);
      return event;
    },
    setAttribute(name, value) { attributes.set(name, String(value)); },
    getAttribute(name) { return name === 'src' ? this.src ?? null : attributes.get(name) ?? null; },
    removeAttribute(name) { attributes.delete(name); if (name === 'src') delete this.src; },
  };
}
function setup(extra = {}) {
  const elements = Object.fromEntries(['singleInputSection', 'singleInputPreview', 'singleInputStatus', 'singleInputRemove', 'singleUseInputSize', 'singleInputFields', 'singleInputUpload'].map(id => [id, target()]));
  let modal = false, profile = 'anima';
  const document = Object.assign(target(), {
    hidden: false, documentElement: target(),
    getElementById: id => elements[id] || null,
    querySelector: () => modal ? {} : null,
    createElement: () => target(),
    body: { append(node) { elements[node.id] = node; } },
  });
  const window = target(), messages = [], counts = { persist: 0, mode: 0, repair: 0, availability: 0 };
  const state = { ready: true, mode: 'single', generationMode: 'txt2img', single: { input_image_id: '' }, inputImage: null, inputRequest: 0 };
  const ctx = vm.createContext({
    console, AbortController, document, window, singleWorkbenchState: state,
    imagePreview: { phase: 'closed' },
    ui: { base_mode: { value: 'dual_sampler' }, width: { value: 1024 }, height: { value: 1024 } },
    singleWorkbenchActive: () => state.ready && state.mode === 'single',
    currentProfile: () => profile,
    updateRepairControls: () => counts.repair++,
    updateGenerationAvailability: () => counts.availability++,
    singleWorkbenchRenderMode: () => counts.mode++,
    schedulePersist: () => counts.persist++,
    toast: value => messages.push(value),
    FormData: class { append(name, value) { this.name = name; this.file = value; } },
    fetch: async () => response(asset()), request: async () => asset(),
    ...extra,
  });
  vm.runInContext(source, ctx);
  ctx.initializeSingleInput();
  return { ctx, state, elements, document, window, messages, counts, setModal: value => { modal = value; }, setProfile: value => { profile = value; } };
}
const fileTransfer = files => ({ types: ['Files'], items: [{ kind: 'file' }], files });

test('upload commits the owned image and switches to normal img2img only after success', async () => {
  const pending = deferred();
  const { ctx, state, elements, counts } = setup({ fetch: () => pending.promise });
  const upload = ctx.singleWorkbenchUpload(file());
  assert.equal(state.generationMode, 'txt2img');
  assert.equal(ctx.ui.base_mode.value, 'dual_sampler');
  assert.equal(ctx.singleWorkbenchInputBlocked(), true);
  pending.resolve(response(asset()));
  assert.equal(await upload, true);
  assert.equal(state.generationMode, 'img2img');
  assert.equal(ctx.ui.base_mode.value, 'normal');
  assert.equal(state.single.input_image_id, 'image-a');
  assert.equal(elements.singleInputPreview.src, '/api/input-images/image-a/image');
  assert.equal(elements.singleInputFields.hidden, false);
  assert.equal(ctx.ui.width.value, 1024);
  assert.equal(ctx.singleWorkbenchInputBlocked(), false);
  assert.equal(counts.repair, 1);
});

test('failed replacement preserves the original image and removes the pending generation lock', async () => {
  const { ctx, state, elements } = setup({ fetch: async () => response({ error: '图片损坏' }, false) });
  ctx.singleWorkbenchSetInput(asset('original'));
  assert.equal(await ctx.singleWorkbenchUpload(file()), false);
  assert.equal(state.single.input_image_id, 'original');
  assert.equal(state.generationMode, 'img2img');
  assert.equal(elements.singleInputPreview.src, '/api/input-images/original/image');
  assert.equal(elements.singleInputStatus.textContent, '图片损坏');
  assert.equal(ctx.singleWorkbenchInputBlocked(), false);
});

test('removing during an upload aborts it and a late successful response cannot restore the image', async () => {
  const pending = deferred(); let signal;
  const { ctx, state, elements } = setup({ fetch: (_url, options) => { signal = options.signal; return pending.promise; } });
  ctx.singleWorkbenchSetInput(asset('original'));
  const upload = ctx.singleWorkbenchUpload(file());
  elements.singleInputRemove.emit('click');
  assert.equal(signal.aborted, true);
  assert.equal(state.generationMode, 'txt2img');
  assert.equal(ctx.singleWorkbenchInputBlocked(), false);
  pending.resolve(response(asset('late')));
  assert.equal(await upload, false);
  assert.equal(state.single.input_image_id, '');
  assert.equal(elements.singleInputPreview.hidden, true);
  assert.equal(elements.singleInputFields.hidden, true);
});

test('the newest upload wins and a late failure cannot replace its status', async () => {
  const first = deferred(), second = deferred(); let calls = 0;
  const { ctx, state, elements, messages } = setup({ fetch: () => (++calls === 1 ? first.promise : second.promise) });
  const older = ctx.singleWorkbenchUpload(file());
  const newer = ctx.singleWorkbenchUpload(file({ name: 'new.png' }));
  second.resolve(response(asset('newest')));
  await newer;
  first.reject(new Error('old failure'));
  await older;
  assert.equal(state.single.input_image_id, 'newest');
  assert.match(elements.singleInputStatus.textContent, /newest/);
  assert.equal(messages.includes('old failure'), false);
});

test('restoring a draft with no reference preserves its dual-sampler choice', async () => {
  const { ctx, state } = setup();
  state.inputImage = asset('previous');
  state.generationMode = 'img2img';
  await ctx.singleWorkbenchRestoreInput();
  assert.equal(state.inputImage, null);
  assert.equal(state.generationMode, 'txt2img');
  assert.equal(ctx.ui.base_mode.value, 'dual_sampler');
  assert.equal(ctx.singleWorkbenchInputBlocked(), false);
});

test('a missing saved reference remains removable and blocks generation until removed', async () => {
  const { ctx, state, elements } = setup({ request: async () => { throw new Error('图片不存在'); } });
  state.single.input_image_id = 'missing';
  await ctx.singleWorkbenchRestoreInput();
  assert.equal(state.single.input_image_id, 'missing');
  assert.equal(state.generationMode, 'img2img');
  assert.equal(elements.singleInputRemove.hidden, false);
  assert.match(elements.singleInputStatus.textContent, /图片不存在/);
  assert.equal(ctx.singleWorkbenchInputBlocked(), true);
  elements.singleInputRemove.emit('click');
  assert.equal(ctx.singleWorkbenchInputBlocked(), false);
  assert.equal(state.generationMode, 'txt2img');
});

test('restores are invalidated even if a different profile requests the same asset id', async () => {
  const first = deferred(), second = deferred(); let calls = 0;
  const { ctx, state, setProfile, elements } = setup({ request: () => (++calls === 1 ? first.promise : second.promise) });
  state.single.input_image_id = 'same';
  const older = ctx.singleWorkbenchRestoreInput();
  ctx.singleWorkbenchCancelInput({ render: false });
  setProfile('krea2');
  const newer = ctx.singleWorkbenchRestoreInput();
  second.resolve({ ...asset('same'), filename: 'current.png' });
  await newer;
  first.resolve({ ...asset('same'), filename: 'old.png' });
  await older;
  assert.match(elements.singleInputStatus.textContent, /current.png/);
  assert.equal(ctx.singleWorkbenchInputBlocked(), false);
});

test('single-file validation rejects multiple, unsupported, empty and oversized files without replacing a reference', async () => {
  let uploads = 0;
  const { ctx, state } = setup({ fetch: async () => { uploads++; return response(asset()); } });
  ctx.singleWorkbenchSetInput(asset('original'));
  for (const files of [[file(), file()], [file({ type: 'image/gif', name: 'test.gif' })], [file({ size: 0 })], [file({ size: 20 * 1024 * 1024 + 1 })]]) {
    assert.equal(await ctx.singleWorkbenchAcceptInput(files), false);
    assert.equal(state.single.input_image_id, 'original');
  }
  assert.equal(uploads, 0);
  assert.equal(await ctx.singleWorkbenchAcceptInput([file({ type: '', name: 'test.JPEG', size: 20 * 1024 * 1024 })]), true);
  assert.equal(uploads, 1);
});

test('nested file drags show a stable overlay and leaving, blurring or hiding clears it', () => {
  const { document, elements, window, state } = setup();
  const dataTransfer = fileTransfer([file()]);
  document.emit('dragenter', { dataTransfer });
  document.emit('dragenter', { dataTransfer });
  assert.equal(elements.singleDropOverlay.hidden, false);
  document.emit('dragleave');
  assert.equal(elements.singleDropOverlay.hidden, false);
  document.emit('dragleave');
  assert.equal(elements.singleDropOverlay.hidden, true);
  document.emit('dragover', { dataTransfer });
  assert.equal(dataTransfer.dropEffect, 'copy');
  window.emit('blur');
  assert.equal(elements.singleDropOverlay.hidden, true);
  document.emit('dragenter', { dataTransfer });
  document.hidden = true;
  document.emit('visibilitychange');
  assert.equal(elements.singleDropOverlay.hidden, true);
  assert.equal(state.inputDragDepth, 0);
});

test('text drags, random mode and open dialogs/viewers never accept workbench file input', () => {
  const { ctx, state, document, elements, setModal } = setup();
  const text = document.emit('dragenter', { dataTransfer: { types: ['text/plain'], items: [{ kind: 'string' }] } });
  assert.equal(text.prevented, false);
  assert.equal(elements.singleDropOverlay.hidden, true);
  const dataTransfer = fileTransfer([file()]);
  setModal(true);
  assert.equal(document.emit('dragover', { dataTransfer }).prevented, false);
  setModal(false);
  ctx.imagePreview.phase = 'open';
  assert.equal(document.emit('drop', { dataTransfer }).prevented, false);
  ctx.imagePreview.phase = 'closed';
  state.mode = 'random';
  assert.equal(document.emit('dragenter', { dataTransfer }).prevented, false);
  assert.equal(elements.singleDropOverlay.hidden, true);
});

test('drop, file picker and paste each route one image through the same upload entry', async () => {
  const { ctx, document, elements } = setup();
  const uploads = [];
  ctx.singleWorkbenchUpload = async image => { uploads.push(image); return true; };
  const dropped = file({ name: 'drop.png' }), selected = file({ name: 'select.png' }), pasted = file({ name: 'paste.png' });
  assert.equal(document.emit('drop', { dataTransfer: fileTransfer([dropped]) }).prevented, true);
  elements.singleInputUpload.value = 'C:\\fakepath\\select.png';
  elements.singleInputUpload.files = [selected];
  elements.singleInputUpload.emit('change');
  assert.equal(elements.singleInputUpload.value, '');
  assert.equal(document.emit('paste', { clipboardData: { items: [{ kind: 'file', type: 'image/png', getAsFile: () => pasted }] } }).prevented, true);
  assert.deepEqual(uploads, [dropped, selected, pasted]);
  assert.equal(elements.singleDropOverlay.hidden, true);
});

test('using the reference size is explicit, clamps dimensions and rounds to multiples of eight', () => {
  const { ctx, elements } = setup();
  ctx.singleWorkbenchSetInput({ ...asset(), width: 10001, height: 61 });
  assert.equal(ctx.ui.width.value, 1024);
  elements.singleUseInputSize.emit('click');
  assert.equal(ctx.ui.width.value, 4096);
  assert.equal(ctx.ui.height.value, 64);
});
