const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const createFrontendContext = require('./helpers/frontend-context.cjs');

const appSource = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
const workbenchSource = fs.readFileSync(path.join(__dirname, '../static/single-workbench.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
const sections = ['character', 'clothing', 'pose', 'background', 'expression'];
const emptyPools = () => Object.fromEntries(sections.map(section => [section, { mode: 'include', ids: [], excluded_ids: [] }]));
function defaults(profile = 'anima', workbench = 'random') {
  return { model_profile: profile, workbench, count: workbench === 'single' ? 1 : 4,
    model_name: `${profile}-default`, steps: 20, cfg: 8, base_mode: 'normal',
    loras: [], lora_managed_triggers: [], pools: emptyPools(), upscalers: {},
    extra_prompt: '', negative_prompt: '', krea_prompt_pool: { enabled: false, ids: [] } };
}
function appFunction(name) {
  const start = appSource.indexOf(`function ${name}(`);
  assert.ok(start >= 0, name);
  return appSource.slice(start, appSource.indexOf('\n}', start) + 2);
}
function setup(storage = new Map()) {
  const elements = {
    singleSeedMode: { value: 'random' }, singleSampleSeed: { value: '0' },
  };
  const controls = ['count', 'female_count', 'male_count', 'character_detail', 'manual_artist', 'quality_prompt',
    'extra_prompt', 'negative_prompt', 'width', 'height', 'steps', 'cfg', 'sampler_name', 'scheduler',
    'model_profile', 'model_name', 'base_mode', 'base_denoise', 'krea_prompt_pool_enabled'];
  const ui = Object.fromEntries(controls.map(name => [name, { value: '', checked: false }]));
  ui.draftStatus = {};
  let invalidations = 0;
  const ctx = createFrontendContext({
    SECTIONS: sections, defaults: defaults(), draft: {}, profileDrafts: {}, activeProfileId: 'anima',
    kreaPromptPoolSelection: [], initialized: false, loraInventoryLoaded: false,
    ui, document: { getElementById: id => elements[id] },
    clone: plain, normalizeProfileId: profile => profile === 'krea2' ? 'krea2' : 'anima',
    normalizeSettings: raw => ({ ...defaults(raw.model_profile), ...plain(raw) }),
    profileSettingsDefaults: profile => defaults(profile), profileConfig: () => ({}),
    defaultPools: emptyPools, defaultUpscalers: () => ({}), normalizeUpscalers: value => value || {},
    canonicalArtists: value => value, ensureSelectValue: (element, value) => { element.value = value; },
    invalidatePromptNormalization() {}, clearNormalizationUndo() {},
    renderDimensions() {}, renderLoras() {}, renderKreaPromptPool() {}, updatePeopleTotal() {},
    updatePoolOverview() {}, updateRepairControls() {},
    localStorage: { setItem: (key, value) => storage.set(key, value), getItem: key => storage.get(key) || null },
    VIEW_KEY: 'test:views', activeSection: 'character', poolViews: {}, formatSavedTime: () => 'now',
    clearTimeout() {}, invalidatePoolContext: () => { invalidations++; },
  });
  ctx.currentProfile = () => ctx.activeProfileId;
  ctx.setUpscalerControls = value => { ctx.upscalers = plain(value); };
  ctx.readUpscalerControls = () => plain(ctx.upscalers || {});
  ctx.readKreaPoolSelection = () => ({ ids: [...ctx.kreaPromptPoolSelection] });
  for (const name of ['profileStateFrom', 'profileDraftFromDraft', 'profileSamplingState', 'saveCurrentProfileDraft',
    'restoreProfileDraft', 'applySettings', 'readSettings']) vm.runInContext(appFunction(name), ctx);
  vm.runInContext(workbenchSource, ctx);
  const state = vm.runInContext('singleWorkbenchState', ctx);
  state.ready = true;
  ctx.singleWorkbenchDefaultSettings = profile => defaults(profile, 'single');
  ctx.randomWorkbenchDefaultSettings = profile => defaults(profile);
  ctx.singleWorkbenchApply = settings => { state.mode = settings.workbench || 'random'; };
  ctx.singleWorkbenchRead = settings => ({ ...settings, workbench: state.mode,
    ...(state.mode === 'single' ? { count: 1, krea_prompt_pool: { enabled: false, ids: [] } } : {}) });
  ctx.singleWorkbenchCancelInput = () => {};
  ctx.singleWorkbenchRenderMode = () => { if (state.mode === 'single') ui.count.value = 1; };
  ctx.singleWorkbenchLoadFavorites = async () => {};
  ctx.singleWorkbenchLayout = () => {};
  ctx.singleWorkbenchShowSettings = () => {};
  function setCurrent(settings, composition = {}) {
    ctx.applySettings({ ...defaults(settings.model_profile, settings.workbench), ...plain(settings) });
    Object.assign(ctx.draft, plain(composition));
  }
  return { ctx, state, storage, elements, setCurrent, invalidations: () => invalidations };
}
function record(profile = 'anima', workbench = 'random') {
  return { id: 10, sample_seed: 0, prompt_seed: 12, settings: { ...defaults(profile, workbench), count: 99,
    steps: 31, cfg: 4.5, extra_prompt: 'historical prompt',
    loras: [{ filename: 'history.safetensors', enabled: true, strength: .7 }], lora_managed_triggers: ['history trigger'],
    random_character: true, fixed_pose: 'historical fixed', random_character_count: 1,
    pools: { ...emptyPools(), character: { mode: 'include', ids: ['history-only'], excluded_ids: [] } },
    krea_prompt_pool: { enabled: true, ids: ['history-krea'] } } };
}
function composition() {
  return { modes: { character: 'pool', clothing: 'fixed', pose: 'off', background: 'pool', expression: 'off' },
    counts: { character: 3, clothing: 2, pose: 4, background: 2, expression: 1 },
    fixed: { character: 'hidden identity', clothing: 'current clothing', pose: 'hidden pose', background: '', expression: 'hidden smile' },
    pools: { ...emptyPools(), character: { mode: 'include', ids: ['current-character'], excluded_ids: [] },
      background: { mode: 'all', ids: [], excluded_ids: ['excluded-background'] } } };
}

test('loading history retains live count and full pool editor state while importing image parameters', () => {
  const h = setup();
  const pool = composition();
  h.setCurrent({ model_profile: 'anima', workbench: 'random', count: 7, cfg: 2,
    loras: [{ filename: 'current.safetensors' }], lora_managed_triggers: ['current trigger'] }, pool);
  const history = record(), before = plain(history);
  const previousInvalidations = h.invalidations();
  h.ctx.singleWorkbenchRestoreHistory(history);
  const settings = h.ctx.readSettings();
  assert.equal(settings.count, 7);
  assert.deepEqual(Object.fromEntries(['modes', 'counts', 'fixed', 'pools'].map(key => [key, plain(h.ctx.draft[key])])), pool);
  assert.equal(settings.random_character, true);
  assert.equal(settings.random_pose, false);
  assert.equal(settings.fixed_pose, '');
  assert.equal(settings.fixed_clothing, 'current clothing');
  assert.equal(settings.random_character_count, 3);
  assert.equal(settings.cfg, 4.5); assert.equal(settings.steps, 31);
  assert.equal(settings.extra_prompt, 'historical prompt');
  assert.deepEqual(plain(settings.loras), history.settings.loras);
  assert.deepEqual(plain(settings.lora_managed_triggers), history.settings.lora_managed_triggers);
  assert.equal(h.elements.singleSeedMode.value, 'fixed'); assert.equal(h.elements.singleSampleSeed.value, 0);
  assert.equal(h.state.promptSeed, 12);
  assert.deepEqual(history, before);
  assert.ok(h.invalidations() > previousInvalidations);
});

test('cross-workbench and cross-model history retains the target draft and leaves the source draft intact', () => {
  const h = setup();
  h.setCurrent({ model_profile: 'krea2', workbench: 'random', count: 11,
    krea_prompt_pool: { enabled: false, ids: ['target-krea'] } }, composition());
  h.ctx.singleWorkbenchPersist();
  const targetPools = plain(h.ctx.draft.pools);
  h.ctx.singleWorkbenchSwitch('single', 'anima');
  h.setCurrent({ model_profile: 'anima', workbench: 'single', cfg: 2, extra_prompt: 'source work in progress' });
  const source = plain(h.ctx.singleWorkbenchCapture());
  h.ctx.singleWorkbenchRestoreHistory(record('krea2'));
  const settings = h.ctx.readSettings();
  assert.equal(h.state.mode, 'random'); assert.equal(h.ctx.currentProfile(), 'krea2');
  assert.equal(settings.count, 11);
  assert.deepEqual(plain(settings.krea_prompt_pool), { enabled: false, ids: ['target-krea'] });
  assert.deepEqual(plain(settings.pools), targetPools);
  assert.equal(settings.cfg, 4.5);
  assert.deepEqual(plain(h.state.drafts.single.anima), source);
  h.ctx.singleWorkbenchSwitch('single', 'anima');
  assert.equal(h.ctx.readSettings().cfg, 2);
  assert.equal(h.ctx.readSettings().extra_prompt, 'source work in progress');
});

test('history for a new target uses that target defaults instead of source or historical selections', () => {
  const h = setup();
  h.setCurrent({ model_profile: 'anima', workbench: 'random', count: 17 }, composition());
  h.ctx.singleWorkbenchRestoreHistory(record('krea2'));
  assert.equal(h.ctx.readSettings().count, 4);
  assert.deepEqual(plain(h.ctx.readSettings().pools), emptyPools());
  assert.deepEqual(plain(h.ctx.readSettings().krea_prompt_pool), { enabled: false, ids: [] });
  assert.equal(h.state.drafts.random.anima.settings.count, 17);
  h.ctx.singleWorkbenchRestoreHistory(record('anima', 'single'));
  assert.equal(h.ctx.readSettings().count, 1);
  assert.equal(h.state.mode, 'single');
});

test('enabled Krea pool survives history loading and round-trip persistence without restoring historical LoRAs', () => {
  const h = setup();
  h.setCurrent({ model_profile: 'krea2', workbench: 'random', count: 8,
    krea_prompt_pool: { enabled: true, ids: ['kept-krea'] } }, composition());
  h.ctx.singleWorkbenchRestoreHistory(record('krea2'));
  const saved = JSON.parse(h.storage.get('anima-studio:workbenches:v1'));
  const loaded = setup(h.storage);
  loaded.state.drafts = saved.drafts;
  loaded.ctx.singleWorkbenchSwitch(saved.mode, saved.profile, { save: false });
  assert.equal(loaded.ctx.readSettings().count, 8);
  assert.deepEqual(plain(loaded.ctx.readSettings().krea_prompt_pool), { enabled: true, ids: ['kept-krea'] });
  assert.deepEqual(plain(loaded.ctx.draft.fixed), composition().fixed);
  assert.deepEqual(plain(loaded.ctx.draft.pools), composition().pools);
  assert.deepEqual(plain(loaded.ctx.readSettings().loras), record('krea2').settings.loras);
  assert.equal(loaded.ctx.readSettings().cfg, 4.5);
  assert.equal(Number(loaded.elements.singleSampleSeed.value), 0);
});

test('single-workbench history and refresh retain hidden Krea selection without enabling random generation', () => {
  const h = setup();
  h.setCurrent({ model_profile: 'krea2', workbench: 'single',
    krea_prompt_pool: { enabled: true, ids: ['hidden-krea'] } }, composition());
  h.ctx.singleWorkbenchRestoreHistory(record('krea2', 'single'));
  assert.equal(h.ctx.readSettings().count, 1);
  assert.deepEqual(plain(h.ctx.readSettings().krea_prompt_pool), { enabled: false, ids: [] });
  const saved = JSON.parse(h.storage.get('anima-studio:workbenches:v1'));
  const loaded = setup(h.storage);
  loaded.state.drafts = saved.drafts;
  loaded.ctx.singleWorkbenchSwitch(saved.mode, saved.profile, { save: false });
  assert.equal(loaded.ctx.ui.krea_prompt_pool_enabled.checked, true);
  assert.deepEqual(plain(loaded.ctx.kreaPromptPoolSelection), ['hidden-krea']);
  assert.deepEqual(plain(loaded.ctx.draft.fixed), composition().fixed);
  assert.deepEqual(plain(loaded.ctx.readSettings().krea_prompt_pool), { enabled: false, ids: [] });
});
