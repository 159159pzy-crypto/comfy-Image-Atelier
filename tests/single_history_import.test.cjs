const createFrontendContext = require('./helpers/frontend-context.cjs');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../static/single-workbench.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));

function setup() {
  const elements = {
    singleSeedMode: { value: 'random' },
    singleSampleSeed: { value: '0', disabled: true },
    singleRollSeed: { addEventListener(_name, callback) { this.click = callback; } },
  };
  const imported = [], layouts = [], saved = [];
  const ctx = createFrontendContext({
    document: { getElementById: id => elements[id] },
    profileSettingsDefaults: profile => ({ model_profile: profile, loras: [], negative_prompt: '' }),
    randomWorkbenchDefaultSettings: profile => ({ model_profile: profile, count: 4 }),
    SECTIONS: ['character', 'clothing', 'pose', 'background', 'expression'],
    profileStateFrom: settings => ({ modes: {}, counts: {}, fixed: {}, pools: plain(settings.pools || {}) }),
    profileConfig: () => ({}), currentProfile: () => 'anima',
    // The server must retain secondary defaults for historical compatibility.
    config: { single_defaults: { latent: { pipeline: 'secondary' }, face_pipeline: 'secondary' } },
    ui: { detailDialog: { close() {} } }, closeImagePreview() {}, toast() {},
    crypto: { getRandomValues(array) { array[0] = 9090; return array; } },
    schedulePersist() {},
  });
  vm.runInContext(source, ctx);
  const state = vm.runInContext('singleWorkbenchState', ctx);
  state.ready = true;
  state.mode = 'single';
  ctx.singleWorkbenchCapture = () => ({ settings: { count: 1 }, profileDrafts: {} });
  ctx.singleWorkbenchSwitch = (mode, profile, options) => {
    state.mode = mode;
    state.single = plain(options.settings.single || {});
    // The initial draft is random-seeded; loading history then selects fixed.
    elements.singleSeedMode.value = 'random';
    elements.singleSampleSeed.disabled = true;
    imported.push({ mode, profile, settings: plain(options.settings) });
  };
  ctx.singleWorkbenchLayout = () => {
    layouts.push({ mode: elements.singleSeedMode.value, seed: elements.singleSampleSeed.value });
    elements.singleSampleSeed.disabled = elements.singleSeedMode.value !== 'fixed';
  };
  ctx.singleWorkbenchPersist = () => saved.push({ seed: elements.singleSampleSeed.value, disabled: elements.singleSampleSeed.disabled });
  ctx.singleWorkbenchShowSettings = () => {};
  return { ctx, state, elements, imported, layouts, saved };
}

function historyRecord(workbench) {
  return {
    id: 12, sample_seed: 4444, prompt_seed: 77, positive_prompt: 'saved final prompt',
    negative_prompt: 'saved negative',
    single_runtime: { positive: 'saved authored prompt' },
    settings: {
      workbench, model_profile: 'anima',
      loras: [{ filename: 'primary.safetensors', enabled: true, strength: 0.7 }],
      single: {
        normal_pipeline: 'secondary',
        dual: { first_pipeline: 'secondary', second_pipeline: 'secondary' },
        latent: { pipeline: 'secondary' }, face_pipeline: 'secondary',
        loras_secondary: [{ filename: 'legacy.safetensors', enabled: true, strength: 0.4 }],
        secondary_managed_triggers: ['legacy trigger'],
        vae_encode: { tile_size: 1536 }, weight_dtype: 'fp8_e4m3fn',
      },
    },
  };
}

test('sending random history into single uses the unified defaults and keeps its image parameters', async () => {
  const { ctx, imported, layouts, elements, saved } = setup();
  const record = historyRecord('random');
  const before = plain(record);
  await ctx.singleWorkbenchFromHistory(record);
  const settings = imported[0].settings;
  const single = settings.single;
  assert.equal(settings.workbench, 'single');
  assert.deepEqual(settings.loras, record.settings.loras);
  assert.deepEqual([single.normal_pipeline, single.dual.first_pipeline, single.dual.second_pipeline,
    single.latent.pipeline, single.face_pipeline], Array(5).fill('primary'));
  assert.deepEqual(single.loras_secondary, []);
  assert.deepEqual(single.secondary_managed_triggers, []);
  assert.equal(single.vae_encode.tile_size, 512);
  assert.equal(single.weight_dtype, 'default');
  assert.equal(single.positive_prompt, 'saved final prompt');
  assert.deepEqual(record, before);
  assert.deepEqual(layouts, [{ mode: 'fixed', seed: 4444 }]);
  assert.equal(elements.singleSampleSeed.disabled, false);
  assert.equal(saved.at(-1).disabled, false);
});

test('sending legacy single history preserves secondary routes and low frequency parameters', async () => {
  const { ctx, imported, state, elements } = setup();
  const record = historyRecord('single');
  await ctx.singleWorkbenchFromHistory(record);
  const single = imported[0].settings.single;
  assert.deepEqual([single.normal_pipeline, single.dual.first_pipeline, single.dual.second_pipeline,
    single.latent.pipeline, single.face_pipeline], Array(5).fill('secondary'));
  assert.deepEqual(single.loras_secondary, record.settings.single.loras_secondary);
  assert.deepEqual(single.secondary_managed_triggers, ['legacy trigger']);
  assert.equal(single.vae_encode.tile_size, 1536);
  assert.equal(single.weight_dtype, 'fp8_e4m3fn');
  assert.equal(single.positive_prompt, 'saved authored prompt');
  assert.equal(state.promptSeed, 77);
  assert.equal(elements.singleSampleSeed.disabled, false);
});

test('a failed reference import leaves the current workbench and detail intact', async () => {
  const { ctx, imported, saved } = setup(); const messages = [];
  ctx.request = async () => { throw new Error('原图不可用'); };
  ctx.toast = message => messages.push(message);
  let closed = 0; ctx.motionDialog = () => closed++;
  await ctx.singleWorkbenchFromHistory(historyRecord('random'), true);
  assert.equal(imported.length, 0); assert.equal(saved.length, 0); assert.equal(closed, 0);
  assert.match(messages[0], /原图不可用/);
});

test('closing or changing the detail during reference import prevents a late workbench switch', async () => {
  const { ctx, imported, state } = setup(); let resolve;
  ctx.request = () => new Promise(yes => { resolve = yes; });
  const record = historyRecord('random'); ctx.selectedRecord = record; ctx.detailContext = { origin: 'random' };
  const importing = ctx.singleWorkbenchFromHistory(record, true);
  ctx.detailContext = { origin: 'single' }; state.historyTransferRequest++;
  resolve({ id: 'new-managed-reference' }); await importing;
  assert.equal(imported.length, 0); assert.equal(state.historyTransferBusy, false);
});

test('reference import finishes before committing the destination workbench', async () => {
  const { ctx, imported } = setup(); let resolve; const events = [];
  ctx.request = () => new Promise(yes => { resolve = yes; });
  ctx.singleWorkbenchSetInput = image => events.push(`input:${image.id}`);
  ctx.artworkRevealWorkbench = () => events.push('reveal');
  const importing = ctx.singleWorkbenchFromHistory(historyRecord('random'), true);
  assert.equal(imported.length, 0);
  resolve({ id: 'managed-reference' }); await importing;
  assert.equal(imported.length, 1); assert.deepEqual(events, ['input:managed-reference', 'reveal']);
});

test('restoring single history makes the fixed seed editable before persisting', () => {
  const { ctx, state, elements, layouts, saved } = setup();
  ctx.singleWorkbenchRestoreHistory(historyRecord('single'));
  assert.equal(elements.singleSeedMode.value, 'fixed');
  assert.equal(elements.singleSampleSeed.value, 4444);
  assert.equal(elements.singleSampleSeed.disabled, false);
  assert.equal(state.promptSeed, 77);
  assert.deepEqual(layouts, [{ mode: 'fixed', seed: 4444 }]);
  assert.deepEqual(saved, [{ seed: 4444, disabled: false }]);
});

test('rolling a new seed changes random mode to an editable fixed seed', () => {
  const { ctx, elements, layouts } = setup();
  const registration = source.split('\n').find(line => line.includes('getElementById("singleRollSeed").addEventListener'));
  assert.ok(registration, 'roll-seed click handler registration');
  vm.runInContext(registration, ctx);
  elements.singleRollSeed.click();
  assert.equal(elements.singleSeedMode.value, 'fixed');
  assert.equal(elements.singleSampleSeed.value, 9090);
  assert.equal(elements.singleSampleSeed.disabled, false);
  assert.deepEqual(layouts, [{ mode: 'fixed', seed: 9090 }]);
});

test('random presets change shared style while retaining the current single prompt, routes and custom resources', () => {
  const { ctx } = setup();
  const current = plain(ctx.singleWorkbenchDefaultSettings('anima'));
  Object.assign(current, {
    generation_mode: 'img2img', clip_name: 'custom-clip', vae_name: 'custom-vae', clip_type: 'custom-type', cfg: 3,
  });
  Object.assign(current.single, {
    positive_prompt: 'my unfinished scene', prefix: 'my prefix', suffix: 'my suffix',
    input_image_id: 'current-reference', prompt_job_id: 'old-job', processed_prompt_override: 'old result',
  });
  const before = plain(current);
  let applied;
  ctx.readSettings = () => current;
  ctx.applySettings = value => { applied = plain(value); };
  for (const workbench of ['random', undefined]) {
    const preset = { settings: {
      ...historyRecord('random').settings, cfg: 6, width: 896,
      clip_name: 'profile-clip', vae_name: 'profile-vae', clip_type: 'profile-type',
    } };
    if (workbench === undefined) delete preset.settings.workbench;
    preset.settings.single.positive_prompt = '';
    const presetBefore = plain(preset);
    ctx.singleWorkbenchApplyPreset(preset);
    assert.equal(applied.cfg, 6);
    assert.equal(applied.width, 896);
    assert.deepEqual(applied.loras, preset.settings.loras);
    assert.equal(applied.workbench, 'single');
    assert.equal(applied.generation_mode, 'img2img');
    assert.equal(applied.single.positive_prompt, 'my unfinished scene');
    assert.equal(applied.single.prefix, 'my prefix');
    assert.equal(applied.single.suffix, 'my suffix');
    assert.equal(applied.single.input_image_id, 'current-reference');
    assert.equal(applied.single.prompt_job_id, '');
    assert.equal(applied.single.processed_prompt_override, null);
    assert.deepEqual([applied.single.normal_pipeline, applied.single.dual.first_pipeline,
      applied.single.dual.second_pipeline, applied.single.latent.pipeline, applied.single.face_pipeline], Array(5).fill('primary'));
    assert.deepEqual(applied.single.loras_secondary, []);
    assert.deepEqual([applied.clip_name, applied.vae_name, applied.clip_type], ['custom-clip', 'custom-vae', 'custom-type']);
    assert.deepEqual(current, before);
    assert.deepEqual(preset, presetBefore);
  }
});

test('single presets still load their saved prompt, secondary routes and resources while retaining the current reference', () => {
  const { ctx } = setup();
  const current = plain(ctx.singleWorkbenchDefaultSettings('anima'));
  current.generation_mode = 'img2img';
  current.single.input_image_id = 'current-reference';
  let applied;
  ctx.readSettings = () => current;
  ctx.applySettings = value => { applied = plain(value); };
  const preset = { settings: {
    ...historyRecord('single').settings, clip_name: 'saved-clip', vae_name: 'saved-vae', clip_type: 'saved-type',
    generation_mode: 'txt2img',
  } };
  Object.assign(preset.settings.single, {
    positive_prompt: 'saved single scene', input_image_id: '', prompt_job_id: 'stale-job', processed_prompt_override: 'stale result',
  });
  const before = plain(preset);
  ctx.singleWorkbenchApplyPreset(preset);
  assert.equal(applied.workbench, 'single');
  assert.equal(applied.generation_mode, 'img2img');
  assert.equal(applied.single.positive_prompt, 'saved single scene');
  assert.deepEqual([applied.single.normal_pipeline, applied.single.dual.first_pipeline,
    applied.single.dual.second_pipeline, applied.single.latent.pipeline, applied.single.face_pipeline], Array(5).fill('secondary'));
  assert.deepEqual(applied.single.loras_secondary, preset.settings.single.loras_secondary);
  assert.equal(applied.single.vae_encode.tile_size, 1536);
  assert.deepEqual([applied.clip_name, applied.vae_name, applied.clip_type], ['saved-clip', 'saved-vae', 'saved-type']);
  assert.equal(applied.single.input_image_id, 'current-reference');
  assert.equal(applied.single.prompt_job_id, '');
  assert.equal(applied.single.processed_prompt_override, null);
  assert.deepEqual(preset, before);
});

test('changing the main model cancels an in-flight upload but preserves reference restoration and random mode', () => {
  const { ctx, state } = setup();
  let change, cancellations = 0;
  ctx.ui.model_name = { addEventListener(name, callback) { assert.equal(name, 'change'); change = callback; } };
  ctx.singleWorkbenchCancelInput = () => { cancellations++; state.inputBusy = ''; };
  const registration = source.split('\n').find(line => line.includes('ui.model_name.addEventListener("change"'));
  assert.ok(registration, 'main-model change handler registration');
  vm.runInContext('const state = singleWorkbenchState;', ctx);
  vm.runInContext(registration, ctx);
  state.single.input_image_id = 'existing-reference';
  state.inputBusy = 'upload';
  change();
  assert.equal(cancellations, 1);
  assert.equal(state.inputBusy, '');
  assert.equal(state.single.input_image_id, 'existing-reference');
  state.inputBusy = 'restore';
  change();
  assert.equal(cancellations, 1);
  assert.equal(state.inputBusy, 'restore');
  state.inputBusy = '';
  change();
  assert.equal(cancellations, 1);
  state.mode = 'random';
  state.inputBusy = 'upload';
  change();
  assert.equal(cancellations, 1);
});


test('restoring random history loads its image seed without freezing future random prompts', () => {
  const { ctx, state, elements } = setup();
  ctx.singleWorkbenchRestoreHistory({ ...historyRecord('random'), sample_seed: 0 });
  assert.equal(elements.singleSeedMode.value, 'fixed'); assert.equal(elements.singleSampleSeed.value, 0);
  assert.equal(state.mode, 'random');
  assert.deepEqual(plain(ctx.singleWorkbenchSeeds()), { sample_seed: 0 });
});
