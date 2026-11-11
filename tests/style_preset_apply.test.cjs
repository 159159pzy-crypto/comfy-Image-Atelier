const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../static/single-workbench.js'), 'utf8');
const app = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
function setup() {
  const draft = { pipeline_version: 5, workbench: 'single', generation_mode: 'img2img', model_profile: 'anima', cfg: 4,
    single: { positive_prompt: 'current scene', prefix: 'current prefix', input_image_id: 'reference', prompt_processing: { expand: { enabled: true, llm_service: 'current-service' } } } };
  const pending = [], applied = [], confirmations = [];
  const elements = { singleSampleSeed: { value: '123' }, singleSeedMode: { value: 'fixed' }, singleNegative: { value: '' } };
  const ctx = vm.createContext({ JSON, Object, Number, Set, Map, Date,
    document: { getElementById: id => elements[id], querySelectorAll: () => [] },
    currentProfile: () => draft.model_profile, readSettings: () => plain(draft),
    request: (url, options) => { const d = deferred(); pending.push({ url, body: JSON.parse(options.body), ...d }); return d.promise; },
    StylePresetManager: { confirm: options => { const d = deferred(); confirmations.push({ options, ...d }); return d.promise; } },
    applySettings: (value, options) => applied.push({ value: plain(value), options }),
    canonicalizeWorkflowSettings: () => { throw new Error('must not expand a sparse preset'); },
  });
  vm.runInContext(source, ctx);
  const state = vm.runInContext('singleWorkbenchState', ctx); state.mode = 'single'; state.ready = true; state.single = plain(draft.single);
  return { ctx, draft, state, elements, pending, applied, confirmations };
}
const item = { id: 'preset-A', model_profile: 'anima', settings: { cfg: 7 } };
const flush = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };

test('preset application sends complete current settings and commits only the server-resolved result', async () => {
  const h = setup(); const before = plain(h.draft); const task = h.ctx.singleWorkbenchApplyPreset(item);
  assert.equal(h.pending[0].url, '/api/style-presets/preset-A/resolve');
  assert.deepEqual(h.pending[0].body, { settings: before, model_profile: 'anima' });
  const result = { ...before, cfg: 7 };
  h.pending[0].resolve({ settings: result, version_change: null });
  assert.equal(await task, true); assert.deepEqual(h.applied[0].value, result);
  assert.equal(h.applied[0].options.preservePromptProcessing, true);
  assert.equal(h.applied[0].options.preservePoolDraft, true);
  assert.deepEqual(h.draft, before); assert.equal(h.confirmations.length, 0);
});

test('late resolution never overwrites edited settings, seeds, models or workbenches', async () => {
  for (const change of ['settings', 'seed', 'model', 'workbench', 'ai']) {
    const h = setup(); const task = h.ctx.singleWorkbenchApplyPreset(item);
    if (change === 'settings') h.draft.cfg = 9;
    if (change === 'seed') h.elements.singleSampleSeed.value = '999';
    if (change === 'model') h.draft.model_profile = 'krea2';
    if (change === 'workbench') h.state.mode = 'random';
    if (change === 'ai') h.state.single.prompt_processing.expand.enabled = false;
    h.pending[0].resolve({ settings: h.pending[0].body.settings });
    assert.equal(await task, false, change); assert.equal(h.applied.length, 0, change);
  }
});

test('the newest preset request wins independently of response order', async () => {
  const h = setup(); const first = h.ctx.singleWorkbenchApplyPreset(item);
  const second = h.ctx.singleWorkbenchApplyPreset({ ...item, id: 'preset-B' });
  h.pending[1].resolve({ settings: { ...h.draft, cfg: 8 } }); await second;
  h.pending[0].resolve({ settings: { ...h.draft, cfg: 7 } });
  assert.equal(await first, false); assert.equal(h.applied.length, 1); assert.equal(h.applied[0].value.cfg, 8);
});

test('repair version changes require confirmation and recheck the draft after confirmation', async () => {
  for (const outcome of ['accept', 'cancel', 'changed']) {
    const h = setup(); const task = h.ctx.singleWorkbenchApplyPreset(item);
    h.pending[0].resolve({ settings: { ...h.draft, pipeline_version: 2 }, version_change: { from: 5, to: 2 } });
    await flush(); assert.equal(h.applied.length, 0); assert.match(h.confirmations[0].options.message, /v5.*v2/);
    if (outcome === 'changed') h.draft.cfg = 10;
    h.confirmations[0].resolve(outcome !== 'cancel');
    assert.equal(await task, outcome === 'accept'); assert.equal(h.applied.length, outcome === 'accept' ? 1 : 0);
  }
});

test('failed resolution and wrong-model presets leave the editor intact', async () => {
  const h = setup(); const task = h.ctx.singleWorkbenchApplyPreset(item);
  h.pending[0].reject(new Error('incompatible scale'));
  await assert.rejects(task, /incompatible scale/); assert.equal(h.applied.length, 0);
  await assert.rejects(h.ctx.singleWorkbenchApplyPreset({ ...item, model_profile: 'krea2' }), /切换/);
  assert.equal(h.pending.length, 1);
});

test('one-time resource resolution notes stay outside the generation-settings API', async () => {
  const h = setup(); h.draft.resource_replacements = [{ field: 'model_name', from: 'missing', to: 'available' }];
  const task = h.ctx.singleWorkbenchApplyPreset(item);
  assert.equal(h.pending[0].body.settings.resource_replacements, undefined);
  h.pending[0].resolve({ settings: h.pending[0].body.settings });
  await task;
  assert.deepEqual(h.applied[0].value.resource_replacements, h.draft.resource_replacements);
});

test('single form population preserves AI switches for presets but history keeps its existing reset policy', () => {
  for (const preserve of [true, false]) {
    const h = setup();
    Object.assign(h.ctx, { config: {}, profileSettingsDefaults: () => ({}), profileConfig: () => ({}),
      singleWorkbenchCancelInput() {}, singleWorkbenchResourceValues: () => ({}), singleWorkbenchRenderProcessed() {},
      singleWorkbenchRestoreInput() {}, singleWorkbenchRenderMode() {},
    });
    h.ctx.singleWorkbenchApply(h.draft, { preservePromptProcessing: preserve });
    assert.equal(h.state.single.prompt_processing.expand.enabled, preserve);
    assert.equal(h.state.single.prompt_processing.expand.llm_service, 'current-service');
    assert.equal(h.state.single.prompt_job_id, ''); assert.equal(h.state.single.processed_prompt_override, null);
  }
});

function scrollHarness(count = 9) {
  const flags = new Set(), properties = {}, frames = [];
  let measurements = 0;
  const list = { children: Array.from({ length: count }, () => ({ getBoundingClientRect: () => { measurements++; return { height: 52 }; } })),
    style: { setProperty: (key, value) => { properties[key] = value; }, getPropertyValue: key => properties[key] || '', removeProperty: key => { delete properties[key]; } },
    scrollHeight: count * 59 - 7, clientHeight: 378, scrollTop: 0, offsetWidth: 360, clientWidth: 350 };
  const ctx = vm.createContext({ ui: { presetList: list, presetScroll: { classList: { toggle: (key, value) => value ? flags.add(key) : flags.delete(key) } } },
    getComputedStyle: () => ({ rowGap: '7px' }), requestAnimationFrame: callback => { frames.push(callback); return frames.length; } });
  const start = app.indexOf('let presetScrollObserver =');
  vm.runInContext(app.slice(start, app.indexOf('function stylePresetItemProfile(', start)), ctx);
  return { ctx, list, flags, properties, frames, measurements: () => measurements };
}
test('homepage keeps six complete rows plus a fading next-row preview and an opaque scrollbar', () => {
  const { ctx, list, flags, properties } = scrollHarness();
  ctx.updatePresetScroll(); assert.equal(list.style.maxHeight, '378px'); assert.deepEqual([...flags], ['can-scroll-down']);
  assert.equal(properties['--preset-fade-top'], '0px'); assert.equal(properties['--preset-fade-bottom'], '24px');
  assert.equal(properties['--preset-scrollbar-width'], '10px');
  list.scrollTop = 90; ctx.syncPresetScrollEdges(); assert.equal(flags.size, 2);
  list.scrollTop = 146; ctx.syncPresetScrollEdges(); assert.deepEqual([...flags], ['can-scroll-up']);
  assert.equal(properties['--preset-fade-bottom'], '0px');
  list.scrollHeight = list.clientHeight; ctx.syncPresetScrollEdges(); assert.equal(flags.size, 0); assert.equal(list.tabIndex, -1);
});
test('a non-overflowing preset list reserves no preview space or fade', () => {
  for (const count of [0, 1, 6]) {
    const { ctx, list, flags } = scrollHarness(count);
    list.clientHeight = list.scrollHeight = Math.max(0, count * 59 - 7);
    ctx.updatePresetScroll();
    assert.equal(list.style.maxHeight, count ? `${count * 59 - 7}px` : undefined);
    assert.equal(flags.size, 0); assert.equal(list.tabIndex, -1);
  }
});
test('overlay scrollbars get an unfaded lane without exposing an opaque content seam', () => {
  const { ctx, list, properties } = scrollHarness();
  list.clientWidth = list.offsetWidth;
  ctx.updatePresetScroll();
  assert.equal(properties['--preset-scrollbar-width'], '12px');
  assert.equal(properties['--preset-overlay-gutter'], '12px');
  list.scrollHeight = list.clientHeight;
  ctx.updatePresetScroll();
  assert.equal(properties['--preset-scrollbar-width'], '0px');
  assert.equal(properties['--preset-overlay-gutter'], '0px');
});
test('preset fades follow subpixel distance continuously and clamp overscroll', () => {
  const { ctx, list, properties } = scrollHarness();
  for (const [position, top, bottom] of [[-5, '0px', '24px'], [6.25, '6.25px', '24px'], [141.5, '24px', '4.5px'], [300, '24px', '0px']]) {
    list.scrollTop = position; ctx.syncPresetScrollEdges();
    assert.equal(properties['--preset-fade-top'], top); assert.equal(properties['--preset-fade-bottom'], bottom);
  }
});
test('scroll bursts coalesce to one frame without measuring rows or writing viewport height', () => {
  const h = scrollHarness(); h.ctx.updatePresetScroll();
  const measurements = h.measurements(), height = h.list.style.maxHeight;
  for (let i = 0; i < 20; i++) { h.list.scrollTop = i; h.ctx.schedulePresetScroll(); }
  assert.equal(h.frames.length, 1); h.frames.shift()();
  assert.equal(h.measurements(), measurements); assert.equal(h.list.style.maxHeight, height);
  assert.equal(h.properties['--preset-fade-top'], '19px');
  h.ctx.schedulePresetScroll(true); h.ctx.schedulePresetScroll(); h.frames.shift()();
  assert.equal(h.measurements(), measurements + 6);
});
