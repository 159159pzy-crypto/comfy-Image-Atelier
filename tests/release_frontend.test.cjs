const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
function functionSource(name) {
  let start = source.indexOf(`function ${name}(`);
  if (start < 0) return '';
  if (source.slice(start - 6, start) === 'async ') start -= 6;
  return source.slice(start, source.indexOf('\n}', start) + 2);
}
function load(ctx, ...names) { for (const name of names) vm.runInContext(functionSource(name), ctx); }
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
const plain = value => JSON.parse(JSON.stringify(value));

function normalizationContext() {
  const requests = [], changes = [];
  const ctx = vm.createContext({
    promptNormalizationEpoch: 0, promptNormalizationRequests: new Map(),
    draft: { fixed: {}, managedTriggers: [] }, clone: plain,
    ui: { extra_prompt: { value: 'old' }, dimensionList: { querySelector: () => null } },
    document: { getElementById: () => null },
    currentProfile: () => 'anima', singleWorkbenchState: { ready: true, mode: 'random' },
    request: () => { const gate = deferred(); requests.push(gate); return gate.promise; },
    setNormalizationUndo: value => changes.push(value), schedulePersist() {},
  });
  load(ctx, 'promptNormalizationContext', 'promptElementForField', 'invalidatePromptNormalization', 'applyPromptField', 'normalizePromptFields');
  return { ctx, requests, changes };
}
test('normalization cannot replace a newer edit or a different workbench draft', async () => {
  for (const change of ['edit', 'workbench', 'profile']) {
    const { ctx, requests } = normalizationContext();
    const pending = ctx.normalizePromptFields({ extra_prompt: 'old' });
    if (change === 'edit') ctx.ui.extra_prompt.value = 'new edit';
    if (change === 'workbench') ctx.singleWorkbenchState.mode = 'single';
    if (change === 'profile') ctx.currentProfile = () => 'krea2';
    requests[0].resolve({ fields: { extra_prompt: 'normalized old' }, changed: true });
    await pending;
    assert.equal(ctx.ui.extra_prompt.value, change === 'edit' ? 'new edit' : 'old', change);
  }
});
test('the latest normalization request wins and unchanged input still normalizes', async () => {
  const { ctx, requests } = normalizationContext();
  const old = ctx.normalizePromptFields({ extra_prompt: 'old' });
  const latest = ctx.normalizePromptFields({ extra_prompt: 'old' });
  requests[1].resolve({ fields: { extra_prompt: 'latest normalization' }, changed: true });
  await latest;
  requests[0].resolve({ fields: { extra_prompt: 'stale normalization' }, changed: true });
  await old;
  assert.equal(ctx.ui.extra_prompt.value, 'latest normalization');
});
test('input revisions reject an old normalization even when text changes back to the same value', async () => {
  const { ctx, requests } = normalizationContext();
  const pending = ctx.normalizePromptFields({ extra_prompt: 'old' });
  ctx.invalidatePromptNormalization();
  requests[0].resolve({ fields: { extra_prompt: 'obsolete result' }, changed: true });
  await pending;
  assert.equal(ctx.ui.extra_prompt.value, 'old');
});
test('single normalization writes its captured field and discards an obsolete workbench result', async () => {
  const { ctx, requests } = normalizationContext();
  const singleSource = fs.readFileSync(path.join(__dirname, '../static/single-workbench.js'), 'utf8');
  vm.runInContext(singleSource, ctx);
  const state = vm.runInContext('singleWorkbenchState', ctx);
  const target = { id: 'singleNegative', value: 'old single negative' };
  state.ready = true; state.mode = 'single'; state.promptTarget = target;
  ctx.toast = () => {};
  const valid = ctx.singleWorkbenchNormalizePrompt();
  requests[0].resolve({ fields: { negative_prompt: 'normalized single negative' }, changed: true });
  await valid;
  assert.equal(target.value, 'normalized single negative');
  const stale = ctx.singleWorkbenchNormalizePrompt();
  state.mode = 'random';
  requests[1].resolve({ fields: { negative_prompt: 'obsolete single negative' }, changed: true });
  await stale;
  assert.equal(target.value, 'normalized single negative');
});

function submitContext() {
  const gates = [], posts = []; let submit;
  const ctx = vm.createContext({
    ui: { settingsForm: { addEventListener: (_name, callback) => { submit = callback; } }, startButton: {} },
    submitInFlight: false, settings: { workbench: 'random', model_profile: 'anima', extra_prompt: 'original', loras: [{ filename: 'old' }] },
    clone: plain, singleWorkbenchActive: () => ctx.settings.workbench === 'single', singleWorkbenchInputBlocked: () => false,
    singleWorkbenchSeeds: () => ({ sample_seed: 777 }),
    currentPromptFields: () => ({}), readSettings: () => ctx.settings, persistNow() {},
    normalizePromptFields: () => { const gate = deferred(); gates.push(gate); return gate.promise; },
    normalizeSettingsPayload: settings => { const gate = deferred(); gates.push({ resolve: () => gate.resolve(settings) }); return gate.promise; },
    generationClassificationIssues: () => [], toast() {}, refreshConnection: async () => {}, renderBatchPayload() {}, updateGenerationAvailability() {},
    request: async (url, options) => { if (url === '/api/batches') { posts.push(JSON.parse(options.body)); return { status: 'running' }; } return {}; },
  });
  load(ctx, 'submitBatch');
  const start = source.indexOf('ui.settingsForm.addEventListener("submit"');
  vm.runInContext(source.slice(start, source.indexOf('ui.settingsForm.addEventListener("input"', start)), ctx);
  return { ctx, gates, posts, submit: () => submit({ preventDefault() {} }) };
}
async function drainSubmit(promise, gates) {
  let done = false; promise.finally(() => { done = true; });
  while (!done) { while (gates.length) gates.shift().resolve({ fields: {} }); await new Promise(setImmediate); }
  await promise;
}
test('submit locks before normalization and holds one immutable settings snapshot', async () => {
  const { ctx, gates, posts, submit } = submitContext();
  const first = submit();
  assert.equal(ctx.submitInFlight, true, 'the first asynchronous stage must already own the submit lock');
  const second = submit();
  ctx.settings.loras[0].filename = 'edited';
  ctx.settings = { workbench: 'single', model_profile: 'krea2', extra_prompt: 'different workbench' };
  await drainSubmit(first, gates); await drainSubmit(second, gates);
  assert.equal(posts.length, 1);
  assert.deepEqual(posts[0], { workbench: 'random', model_profile: 'anima', extra_prompt: 'original', loras: [{ filename: 'old' }] });
  assert.equal(ctx.submitInFlight, false);
});
test('fixed single seeds are captured with settings and a failed submit unlocks retry', async () => {
  const { ctx, gates, posts, submit } = submitContext();
  ctx.settings = { workbench: 'single', model_profile: 'anima' };
  await submit();
  assert.equal(posts[0].seeds.sample_seed, 777);
  ctx.settings = { workbench: 'random', model_profile: 'anima' };
  ctx.normalizeSettingsPayload = async () => { throw new Error('offline'); };
  await drainSubmit(submit(), gates);
  assert.equal(ctx.submitInFlight, false);
});
test('submission remains locked until its final status read settles', async () => {
  const { ctx, submit } = submitContext();
  const status = deferred(); let posts = 0;
  ctx.settings = { workbench: 'single', model_profile: 'anima' };
  ctx.request = async url => { if (url === '/api/batches') { posts++; return { status: 'running' }; } return status.promise; };
  const pending = submit();
  await new Promise(setImmediate);
  assert.equal(ctx.submitInFlight, true);
  await submit();
  assert.equal(posts, 1);
  status.reject(new Error('status temporarily unavailable'));
  await pending;
  assert.equal(ctx.submitInFlight, false);
});

function resourceContext() {
  const field = (value = '') => ({ value, disabled: false, replaceChildren(...options) { this.value = options[0]?.value || ''; } });
  const ui = Object.fromEntries(['model_profile', 'base_mode', 'sampler_name', 'scheduler', 'seedvr2_model_name', 'seedvr2_vae_name', 'model_name', 'latent_second_sampler_name', 'latent_second_scheduler', 'seedvr2_sampler_name', 'seedvr2_scheduler', 'face_sampler', 'face_scheduler', 'face_upscaler_model'].map(key => [key, field()]));
  const saved = { sampler_name: 'user_sampler', scheduler: 'user_scheduler', seedvr2_model_name: 'user_model', seedvr2_vae_name: 'user_vae' };
  for (const [key, value] of Object.entries(saved)) ui[key].value = value;
  const gates = [];
  const ctx = vm.createContext({
    ui, resourceRequest: 0, currentProfile: () => 'anima', defaults: {}, clone: value => JSON.parse(JSON.stringify(value)),
    profileConfig: () => ({ seedvr2_model: 'default_model', seedvr2_vae: 'default_vae' }),
    profileDefaults: () => ({ sampler_name: 'default_sampler', scheduler: 'default_scheduler' }),
    refreshCurrentModelSelection() {}, updateGenerationAvailability() {}, Option: function(_label, value) { this.value = value; },
    request: () => { const gate = deferred(); gates.push(gate); return gate.promise; },
    profileOptionsFromResources: () => [{ id: 'anima' }], ensureSelectValue: (element, value) => element.value = value,
    populateResourceSelect: (element, _choices, selected) => element.value = selected,
    resolveInventoryPath: value => value, controlValue: id => ui[id].value,
    applyNodeResourceControls() {}, updateProfileResources() {}, updateRepairControls() {},
  });
  load(ctx, 'loadResources');
  return { ctx, ui, gates, saved };
}
test('overlapping resource refreshes preserve saved selections throughout loading', async () => {
  const { ctx, ui, gates, saved } = resourceContext();
  const first = ctx.loadResources(), second = ctx.loadResources();
  assert.equal(gates.length, 1, 'overlapping inventory reads share a request');
  const duringLoad = Object.fromEntries(Object.keys(saved).map(key => [key, ui[key].value]));
  const payload = { models: ['model'], samplers: ['user_sampler'], schedulers: ['user_scheduler'], seedvr2_models: ['user_model'], seedvr2_vaes: ['user_vae'] };
  gates.forEach(gate => gate.resolve(payload)); await Promise.all([first, second]);
  assert.deepEqual(duringLoad, saved, 'autosave during loading must retain settings');
  assert.deepEqual(Object.fromEntries(Object.keys(saved).map(key => [key, ui[key].value])), saved);
});
test('an in-flight resource refresh preserves settings loaded into another workbench on the same profile', async () => {
  const { ctx, ui, gates } = resourceContext();
  const pending = ctx.loadResources();
  ui.sampler_name.value = 'new_workbench_sampler';
  ui.scheduler.value = 'new_workbench_scheduler';
  gates[0].resolve({ models: ['model'], samplers: ['new_workbench_sampler'], schedulers: ['new_workbench_scheduler'] });
  await pending;
  assert.equal(ui.sampler_name.value, 'new_workbench_sampler');
  assert.equal(ui.scheduler.value, 'new_workbench_scheduler');
});
test('resource loading keeps the img2img base-mode lock owned by its workbench', async () => {
  const { ctx, ui, gates } = resourceContext();
  ui.base_mode.disabled = true;
  const pending = ctx.loadResources();
  const duringLoad = ui.base_mode.disabled;
  gates[0].resolve({ models: ['model'], samplers: ['user_sampler'], schedulers: ['user_scheduler'] });
  await pending;
  assert.equal(duringLoad, true);
  assert.equal(ui.base_mode.disabled, true);
});

function previewContext() {
  const images = [], pending = [];
  const ctx = vm.createContext({
    imagePreview: { phase: 'open', context: {}, preloaded: new Map(), pointers: new Set(), token: 0, navigationRequest: 0 },
    currentPreviewRecord: () => ({ id: 1 }),
    Image: function() { this.decode = () => { const gate = deferred(); pending.push(gate); return gate.promise; }; images.push(this); },
    cancelImagePreviewAnimation() {},
    document: { documentElement: { classList: { remove() {} } } },
    ui: { imagePreviewVeil: { getAnimations: () => [] }, imagePreviewImage: { classList: { remove() {} }, removeAttribute() {} }, imagePreviewStatus: {}, imagePreviewAnnouncement: {}, imagePreviewDialog: { classList: { remove() {} }, dataset: {} } },
  });
  load(ctx, 'clearPreviewCache', 'trimPreviewCache', 'preloadRecord', 'finishImagePreviewClose');
  return { ctx, images, pending };
}
test('decoded preview cache holds at most five images and protects the current image', async () => {
  const { ctx, pending, images } = previewContext();
  for (let id = 1; id <= 12; id++) { const value = ctx.preloadRecord({ id }); pending.shift().resolve(); await value; }
  assert.equal(ctx.imagePreview.preloaded.size, 5);
  assert.ok(ctx.imagePreview.preloaded.has('1'));
  assert.ok(ctx.imagePreview.preloaded.has('12'));
  await ctx.preloadRecord({ id: 9 });
  const next = ctx.preloadRecord({ id: 13 }); pending.shift().resolve(); await next;
  assert.ok(ctx.imagePreview.preloaded.has('9'), 'reading a cached image updates its recency');
  assert.equal(ctx.imagePreview.preloaded.has('10'), false);
  assert.equal(images[1].src, '', 'eviction releases the decoded image reference');
  ctx.finishImagePreviewClose();
  assert.equal(ctx.imagePreview.preloaded.size, 0);
  assert.ok(images.every(image => image.src === ''));
});
test('closing a viewer invalidates a pending decode without deleting a newer same-id cache entry', async () => {
  const { ctx, pending } = previewContext();
  const old = ctx.preloadRecord({ id: 5 }), oldDecode = pending.shift();
  ctx.finishImagePreviewClose();
  ctx.imagePreview.context = {}; ctx.imagePreview.phase = 'open';
  const latest = ctx.preloadRecord({ id: 5 }), latestDecode = pending.shift();
  assert.ok(latestDecode, 'a new viewer must get its own decode');
  oldDecode.reject(new Error('old decode cancelled')); assert.equal(await old, null);
  assert.equal(ctx.imagePreview.preloaded.get('5'), latest);
  latestDecode.resolve(); assert.ok(await latest);
});
test('a successfully decoded image from a closed viewer cannot commit into the new context', async () => {
  const { ctx, pending } = previewContext();
  const stale = ctx.preloadRecord({ id: 7 }), decode = pending.shift();
  ctx.finishImagePreviewClose(); ctx.imagePreview.context = {};
  decode.resolve();
  assert.equal(await stale, null);
  assert.equal(ctx.imagePreview.preloaded.size, 0);
});
