const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require('node:path').join(__dirname, '../static/app.js'), 'utf8');
function functionSource(name) {
  let start = source.indexOf(`function ${name}(`);
  assert.ok(start >= 0, name);
  if (source.slice(start - 6, start) === 'async ') start -= 6;
  const end = source.indexOf('\n}', start) + 2;
  return source.slice(start, end);
}
function context() {
  const ctx = vm.createContext({
    SECTIONS: ['character', 'clothing', 'pose', 'background', 'expression'],
    defaults: { profile_settings: { anima: {}, krea2: {} } },
    draft: {}, profileDrafts: {}, ui: {}, activeProfileId: 'anima',
    clone: x => JSON.parse(JSON.stringify(x)),
    normalizeProfileId: x => x === 'krea2' ? 'krea2' : 'anima',
    defaultPools: () => ({ pose: { ids: [] } }), defaultUpscalers: () => ({}),
    profileSettingsDefaults: () => ({ random_character: true, fixed_pose: '', pools: {}, loras: [] }),
    profileConfig: () => ({}),
  });
  for (const name of ['profileStateFrom', 'profileDraftFromDraft', 'restoreProfileDraft']) vm.runInContext(functionSource(name), ctx);
  return ctx;
}
function plain(value) { return JSON.parse(JSON.stringify(value)); }
test('history and legacy settings override defaults, including empty arrays', () => {
  const ctx = context();
  ctx.profileDrafts.anima = { modes: { pose: 'off' }, fixed: { pose: 'unrelated' }, pools: {}, loras: [] };
  ctx.restoreProfileDraft('anima', { fixed_pose: 'standing', pools: { pose: { ids: ['pose:one'] } }, loras: [{ filename: 'saved.safetensors' }] }, { preferStored: false });
  assert.equal(ctx.draft.modes.pose, 'fixed');
  assert.equal(ctx.draft.fixed.pose, 'standing');
  assert.deepEqual(plain(ctx.draft.pools.pose.ids), ['pose:one']);
  assert.equal(ctx.draft.loras[0].filename, 'saved.safetensors');
  ctx.restoreProfileDraft('anima', { loras: [], fixed_pose: '' }, { preferStored: false });
  assert.deepEqual(plain(ctx.draft.loras), []);
  assert.equal(ctx.draft.fixed.pose, '');
});
test('saved profile draft preserves inactive fixed text and model isolation', () => {
  const ctx = context();
  ctx.profileDrafts.anima = { modes: { pose: 'pool' }, fixed: { pose: 'draft standing' }, pools: {}, loras: [] };
  ctx.restoreProfileDraft('anima', { fixed_pose: 'history' });
  assert.equal(ctx.draft.fixed.pose, 'draft standing');
  assert.equal(ctx.draft.modes.pose, 'pool');
  ctx.restoreProfileDraft('krea2', { model_profile: 'krea2', loras: [{ filename: 'krea.safetensors' }] }, { preferStored: false });
  assert.equal(ctx.draft.loras[0].filename, 'krea.safetensors');
  ctx.restoreProfileDraft('anima');
  assert.equal(ctx.draft.fixed.pose, 'draft standing');
});
test('generation omits inactive fixed drafts while retaining pools and count', () => {
  const ctx = context();
  vm.runInContext(functionSource('readSettings'), ctx);
  ctx.saveCurrentProfileDraft = () => {};
  ctx.canonicalArtists = x => x;
  ctx.currentProfile = () => 'anima';
  ctx.readUpscalerControls = () => ({});
  ctx.readKreaPoolSelection = () => ({ ids: [] });
  const fields = ['count','female_count','male_count','character_detail','manual_artist','quality_prompt','extra_prompt','negative_prompt','width','height','steps','cfg','sampler_name','scheduler','model_name','base_mode','krea_prompt_pool_enabled'];
  ctx.ui = Object.fromEntries(fields.map(x => [x, { value: '1', checked: false }]));
  ctx.ui.count.value = '7';
  ctx.draft = { modes: { character: 'pool', clothing: 'off', pose: 'fixed' }, fixed: { character: 'hidden identity', clothing: 'hidden coat', pose: 'standing' }, counts: {}, pools: { pose: { ids: ['pose:one'] } }, loras: [] };
  const settings = ctx.readSettings();
  assert.equal(settings.fixed_character, '');
  assert.equal(settings.fixed_clothing, '');
  assert.equal(settings.fixed_pose, 'standing');
  assert.equal(settings.count, 7);
  assert.deepEqual(plain(settings.pools.pose.ids), ['pose:one']);
  assert.equal(ctx.draft.fixed_character, undefined);
  assert.equal(ctx.draft.fixed.character, 'hidden identity');
});
test('partial redraw requires selecting every non-preservable dimension', () => {
  const ctx = context();
  ctx.ui.confirmVariant = {};
  ctx.ui.variantStatus = { dataset: {} };
  ctx.selectedRegenerationMode = () => 'content_redraw';
  ctx.selectedRedrawSections = () => ['pose'];
  ctx.setRedrawSectionsVisible = () => {};
  ctx.escapeHtml = x => x;
  ctx.regenerationReasons = () => [];
  ctx.regenerationOptions = { modes: { content_redraw: { available: true } }, resourceIssues: [], sections: { character: { random: true, canPreserve: false, preserveReason: '角色身份与最终提示词冲突，请同时重抽角色' }, pose: { random: true, canPreserve: true } } };
  vm.runInContext(functionSource('updateVariantDrawer'), ctx);
  ctx.updateVariantDrawer();
  assert.equal(ctx.ui.confirmVariant.disabled, true);
  assert.match(ctx.ui.variantStatus.innerHTML, /同时重抽角色/);
  ctx.selectedRedrawSections = () => ['character', 'pose'];
  ctx.updateVariantDrawer();
  assert.equal(ctx.ui.confirmVariant.disabled, false);
  assert.equal(ctx.ui.variantStatus.dataset.state, 'ok');
});

function classificationContext() {
  const ctx = context();
  ctx.URL = URL;
  ctx.config = {};
  ctx.submitInFlight = false;
  ctx.currentProfile = () => ctx.activeProfileId;
  ctx.profileLabel = value => value === 'krea2' ? 'Krea 2' : 'Anima';
  ctx.resources = { models: ['anima.safetensors'], model_items: [] };
  ctx.resourcesLoaded = true;
  ctx.resourceInventoryProfile = 'anima';
  ctx.resourceError = '';
  ctx.loraInventory = [];
  ctx.loraClassificationItems = [];
  ctx.loraInventoryLoaded = true;
  ctx.loraInventoryProfile = 'anima';
  ctx.loraInventoryError = '';
  ctx.loraInventoryRequest = 0;
  ctx.draft = { loras: [] };
  const connection = { className: 'status-badge online', querySelector: () => ({}) };
  connection.classList = { contains: name => connection.className.split(' ').includes(name) };
  ctx.ui = { model_name: { value: 'anima.safetensors' }, startButton: {}, connection };
  for (const name of ['normalizedPath', 'loraItem', 'classificationItem', 'classificationReason', 'loraSelectionIssue', 'modelSelectionIssue', 'generationClassificationIssues', 'updateGenerationAvailability', 'replaceLoraInventoryItem', 'safeLoraManagerUrl']) {
    vm.runInContext(functionSource(name), ctx);
  }
  return ctx;
}

test('enabled incompatible LoRAs remain saved and block generation with the Manager reason', () => {
  const ctx = classificationContext();
  ctx.draft.loras = [{ filename: 'saved.safetensors', enabled: true, strength: 0.8 }];
  ctx.loraClassificationItems = [{ filename: 'saved.safetensors', model_profile: 'krea2', classification_status: 'recognized', base_model: 'Krea 2' }];
  ctx.updateGenerationAvailability();
  assert.equal(ctx.ui.startButton.disabled, true);
  assert.match(ctx.ui.startButton.title, /Krea 2/);
  assert.equal(ctx.draft.loras[0].filename, 'saved.safetensors');
  assert.equal(ctx.draft.loras[0].strength, 0.8);
  ctx.draft.loras[0].enabled = false;
  ctx.updateGenerationAvailability();
  assert.equal(ctx.ui.startButton.disabled, false);
});

test('unknown and unsupported classifications display the backend reason', () => {
  const ctx = classificationContext();
  ctx.resources.models = [];
  ctx.resources.model_items = [{ name: 'anima.safetensors', model_profile: null, classification_status: 'unknown', classification_reason: 'base_model 未填写' }];
  assert.match(ctx.modelSelectionIssue(), /base_model 未填写/);
  ctx.loraClassificationItems = [{ filename: 'sdxl.safetensors', model_profile: null, classification_status: 'unsupported', classification_reason: '不支持 SDXL' }];
  assert.match(ctx.loraSelectionIssue('sdxl.safetensors'), /不支持 SDXL/);
});

test('connection polling cannot enable generation while Manager classification is unavailable', async () => {
  const ctx = classificationContext();
  ctx.document = { hidden: false };
  ctx.request = async () => ({ online: true, version: 'test' });
  ctx.loraInventoryLoaded = false;
  ctx.loraInventoryError = 'LoRA Manager 不可用';
  vm.runInContext(functionSource('refreshConnection'), ctx);
  await ctx.refreshConnection();
  assert.equal(ctx.ui.startButton.disabled, true);
  assert.match(ctx.ui.startButton.title, /LoRA Manager 不可用/);
  ctx.loraInventoryLoaded = true;
  ctx.loraInventoryError = '';
  await ctx.refreshConnection();
  assert.equal(ctx.ui.startButton.disabled, false);
  ctx.config.storage_status = { history: { read_only: true, reason: '备份失败' } };
  await ctx.refreshConnection();
  assert.equal(ctx.ui.startButton.disabled, true);
});

test('trigger edits do not promote a reclassified item into the selectable inventory', () => {
  const ctx = classificationContext();
  ctx.loraInventory = [{ filename: 'saved.safetensors', model_profile: 'anima', classification_status: 'recognized' }];
  ctx.replaceLoraInventoryItem({ filename: 'saved.safetensors', model_profile: null, classification_status: 'unindexed', classification_reason: '插件未索引', trigger_words: ['saved'] });
  assert.equal(ctx.loraItem('saved.safetensors'), null);
  assert.equal(ctx.loraInventory.length, 0);
  assert.match(ctx.loraSelectionIssue('saved.safetensors'), /插件未索引/);
});

test('Manager links only allow local HTTP URLs without credentials', () => {
  const ctx = classificationContext();
  assert.equal(ctx.safeLoraManagerUrl('http://127.0.0.1:8188/loras'), 'http://127.0.0.1:8188/loras');
  assert.equal(ctx.safeLoraManagerUrl('http://[::1]:8188/loras'), 'http://[::1]:8188/loras');
  for (const value of ['javascript:alert(1)', 'https://evil.example/loras', 'http://127.0.0.1.evil.example/loras', 'http://user:pass@localhost:8188/loras']) assert.equal(ctx.safeLoraManagerUrl(value), '');
});

class TestOption {
  constructor(text, value) { this.text = text; this.value = value; this.dataset = {}; this.disabled = false; }
}
function modelSelect() {
  return {
    value: '', children: [],
    replaceChildren(...children) { this.children = children; },
    get options() { return this.children.flatMap(item => item.children || [item]); },
  };
}

test('restored and stale explicit model paths stay visible as disabled options', () => {
  const ctx = classificationContext();
  ctx.Option = TestOption;
  ctx.document = { createElement: () => ({ children: [], append(item) { this.children.push(item); } }) };
  ctx.ui.model_name = modelSelect();
  ctx.ui.model_name.value = 'old/anima.safetensors';
  ctx.resources.models = ['new/anima.safetensors'];
  ctx.resources.model_items = [{ name: 'new/anima.safetensors', model_profile: 'anima', classification_status: 'recognized' }];
  for (const name of ['resolveInventoryPath', 'populateModelSelect', 'refreshCurrentModelSelection']) vm.runInContext(functionSource(name), ctx);
  ctx.refreshCurrentModelSelection();
  assert.equal(ctx.ui.model_name.value, 'old/anima.safetensors');
  assert.equal(ctx.ui.model_name.options[0].disabled, true);
  assert.match(ctx.ui.model_name.options[0].text, /不可用/);
  assert.match(ctx.ui.model_name.options[0].text, /不在当前/);
  ctx.ui.model_name.value = 'anima.safetensors';
  ctx.refreshCurrentModelSelection();
  assert.equal(ctx.ui.model_name.value, 'new/anima.safetensors');
  assert.equal(ctx.ui.model_name.options[0].disabled, false);
});

test('a matching basename cannot replace an incompatible exact model or LoRA', () => {
  const ctx = classificationContext();
  ctx.Option = TestOption;
  ctx.document = { createElement: () => ({ children: [], append(item) { this.children.push(item); } }) };
  ctx.ui.model_name = modelSelect();
  ctx.ui.model_name.value = 'model.safetensors';
  ctx.resources.models = ['anima/model.safetensors'];
  ctx.resources.model_items = [
    { name: 'model.safetensors', model_profile: null, classification_status: 'unknown', classification_reason: 'base_model 未填写' },
    { name: 'anima/model.safetensors', model_profile: 'anima', classification_status: 'recognized' },
  ];
  for (const name of ['populateModelSelect', 'refreshCurrentModelSelection']) vm.runInContext(functionSource(name), ctx);
  ctx.refreshCurrentModelSelection();
  assert.equal(ctx.ui.model_name.value, 'model.safetensors');
  assert.equal(ctx.ui.model_name.options[0].disabled, true);
  assert.match(ctx.ui.model_name.options[0].text, /base_model 未填写/);
  ctx.loraInventory = [{ filename: 'anima/model.safetensors', model_profile: 'anima', classification_status: 'recognized' }];
  assert.equal(ctx.loraItem('model.safetensors'), null);
  assert.equal(ctx.loraItem('anima/model.safetensors').filename, 'anima/model.safetensors');
});

test('legacy model basename resolution requires a globally unique match across profiles', () => {
  const ctx = classificationContext();
  ctx.Option = TestOption;
  ctx.document = { createElement: () => ({ children: [], append(item) { this.children.push(item); } }) };
  ctx.ui.model_name = modelSelect();
  ctx.ui.model_name.value = 'model.safetensors';
  ctx.resources.models = ['anima/model.safetensors'];
  ctx.resources.model_items = [
    { name: 'krea/model.safetensors', model_profile: 'krea2', classification_status: 'recognized' },
    { name: 'anima/model.safetensors', model_profile: 'anima', classification_status: 'recognized' },
  ];
  for (const name of ['populateModelSelect', 'refreshCurrentModelSelection']) vm.runInContext(functionSource(name), ctx);
  ctx.refreshCurrentModelSelection();
  assert.equal(ctx.ui.model_name.value, 'model.safetensors');
  assert.equal(ctx.ui.model_name.options[0].disabled, true);
  assert.match(ctx.ui.model_name.options[0].text, /多个同名主模型/);
});

test('only the latest ownership request can choose the Manager page for the selected kind', async () => {
  const ctx = classificationContext();
  const pending = [];
  ctx.config.comfy_url = 'http://127.0.0.1:8188';
  ctx.ownershipRequest = 0;
  ctx.ownershipItems = [];
  ctx.ui.ownershipKind = { value: 'models' };
  ctx.ui.ownershipError = {};
  ctx.ui.ownershipSummary = {};
  ctx.ui.ownershipList = { replaceChildren() {} };
  ctx.ui.openLoraManager = { removeAttribute(name) { delete this[name]; } };
  ctx.renderResourceOwnership = () => {};
  ctx.request = url => new Promise(resolve => pending.push({ url, resolve }));
  for (const name of ['setLoraManagerUrl', 'loadResourceOwnership']) vm.runInContext(functionSource(name), ctx);
  const models = ctx.loadResourceOwnership({ refresh: true });
  assert.equal(ctx.ui.openLoraManager.href, 'http://127.0.0.1:8188/checkpoints');
  ctx.ui.ownershipKind.value = 'loras';
  const loras = ctx.loadResourceOwnership();
  pending[1].resolve({ items: [{ name: 'lora.safetensors' }], manager_url: 'http://127.0.0.1:8188/loras' });
  await loras;
  pending[0].resolve({ items: [{ name: 'model.safetensors' }], manager_url: 'http://127.0.0.1:8188/checkpoints' });
  await models;
  assert.equal(ctx.ui.openLoraManager.href, 'http://127.0.0.1:8188/loras');
  assert.equal(ctx.ownershipItems[0].name, 'lora.safetensors');
});

test('LoRA refresh preserves profile drafts and rejects a stale response after switching profiles', async () => {
  const ctx = classificationContext();
  const pending = [];
  ctx.request = url => new Promise(resolve => pending.push({ url, resolve }));
  ctx.renderLoras = ctx.renderLoraCatalog = ctx.updateRepairControls = ctx.setLoraManagerUrl = ctx.reconcileLoraTriggers = () => {};
  ctx.draft.loras = [{ filename: 'anima-saved.safetensors', enabled: true }];
  vm.runInContext(functionSource('loadLoraInventory'), ctx);
  const oldLoad = ctx.loadLoraInventory({ refresh: true });
  assert.match(pending[0].url, /refresh=1/);
  ctx.activeProfileId = 'krea2';
  ctx.draft.loras = [{ filename: 'krea-saved.safetensors', enabled: true }];
  const currentLoad = ctx.loadLoraInventory({ refresh: true });
  pending[1].resolve({ items: [{ filename: 'krea.safetensors', model_profile: 'krea2', classification_status: 'recognized' }], classification_items: [] });
  await currentLoad;
  pending[0].resolve({ items: [{ filename: 'anima.safetensors', model_profile: 'anima', classification_status: 'recognized' }] });
  await oldLoad;
  assert.equal(ctx.loraInventory[0].filename, 'krea.safetensors');
  assert.equal(ctx.loraInventoryProfile, 'krea2');
  assert.equal(ctx.draft.loras[0].filename, 'krea-saved.safetensors');
  assert.equal(ctx.loraInventoryLoaded, true);
});

const singleSource = fs.readFileSync(require('node:path').join(__dirname, '../static/single-workbench.js'), 'utf8');
function singleContext(extra = {}) {
  const elements = {};
  const ctx = vm.createContext({
    JSON, Number, Set, Date, clearTimeout, setTimeout, console,
    document: { getElementById: id => elements[id] ||= { value: '', hidden: false }, querySelectorAll: () => [] },
    SECTIONS: ['character', 'clothing', 'pose', 'background', 'expression'],
    config: {}, profileConfig: () => ({ clip_name: 'clip.safetensors', vae_name: 'vae.safetensors', clip_type: 'stable_diffusion' }),
    profileSettingsDefaults: profile => ({ model_profile: profile, count: 8, base_mode: 'dual_sampler', loras: [], negative_prompt: 'negative' }),
    ...extra,
  });
  vm.runInContext(singleSource, ctx);
  return { ctx, elements, run: code => vm.runInContext(code, ctx) };
}
test('single defaults use one normal sample and match safe image resize and dual defaults', () => {
  const { run } = singleContext();
  const value = plain(run('singleWorkbenchDefaultSettings("anima")'));
  assert.equal(value.count, 1);
  assert.equal(value.base_mode, 'normal');
  assert.equal(value.single.resize_mode, 'crop');
  assert.equal(value.single.dual.scale, 1);
  assert.equal(value.single.dual.second_pipeline, 'primary');
});
test('single serialization freezes manual composition and omits all random dimensions', () => {
  const { ctx, elements, run } = singleContext();
  run('singleWorkbenchState.ready = true; singleWorkbenchState.mode = "single"; singleWorkbenchState.single = singleWorkbenchDefaultSettings().single');
  elements.singleNegative = { value: 'manual negative' };
  run('singleWorkbenchState.resourceValues = {clip_name: "custom clip", vae_name: "custom vae", clip_type: "custom type"}');
  ctx.base = { count: 9, quality_prompt: 'random quality', extra_prompt: 'random body', manual_artist: '@artist', random_pose: true, fixed_pose: 'standing', loras: [{ filename: 'first', enabled: true, strength: 1 }] };
  const value = plain(run('singleWorkbenchRead(base)'));
  assert.equal(value.count, 1); assert.equal(value.workbench, 'single');
  assert.equal(value.extra_prompt, ''); assert.equal(value.quality_prompt, ''); assert.equal(value.manual_artist, '');
  assert.equal(value.random_pose, false); assert.equal(value.fixed_pose, '');
  assert.equal(value.negative_prompt, 'manual negative'); assert.equal(value.clip_name, 'custom clip');
  assert.equal(value.loras[0].filename, 'first');
  assert.equal(ctx.base.count, 9);
});
test('reading and saving single settings does not detach secondary LoRA edit handlers', () => {
  const { elements, run } = singleContext();
  for (const id of ['singleNegative', 'singleClipName', 'singleVaeName', 'singleClipType']) elements[id] = { value: '' };
  run('singleWorkbenchState.ready = true; singleWorkbenchState.mode = "single"; singleWorkbenchState.single = singleWorkbenchDefaultSettings().single; singleWorkbenchState.single.loras_secondary = [{filename:"second", enabled:true, strength:1}]; const renderedItem = singleWorkbenchState.single.loras_secondary[0]; const renderedList = singleWorkbenchState.single.loras_secondary;');
  run('singleWorkbenchRead({}); renderedItem.strength = 0.75');
  assert.equal(run('singleWorkbenchRead({}).single.loras_secondary[0].strength'), 0.75);
  run('const submitted = singleWorkbenchRead({}); submitted.single.loras_secondary[0].strength = 9');
  assert.equal(run('singleWorkbenchState.single.loras_secondary[0].strength'), 0.75);
  run('singleWorkbenchRead({}); renderedList.splice(0,1)');
  assert.equal(run('singleWorkbenchRead({}).single.loras_secondary.length'), 0);
});
test('single namespace merging preserves independent encoder, decoder and LoRA arrays', () => {
  const { run } = singleContext();
  assert.equal(run('const a = singleWorkbenchDefaultSettings(); a.single.vae_encode.tile_size = 1024; a.single.vae_decode.tile_size'), 512);
  assert.equal(run('const b = singleWorkbenchMerge(a.single, {loras_secondary:[{filename:"second"}]}); b.loras_secondary.push({filename:"third"}); a.single.loras_secondary.length'), 0);
});
test('single fixed seeds reject unsafe numeric precision and invalid prompt seeds', () => {
  const { elements, run } = singleContext();
  elements.singleSeedMode = { value: 'fixed' }; elements.singleSampleSeed = { value: '123456' }; run('singleWorkbenchState.promptSeed = 99');
  assert.deepEqual(plain(run('singleWorkbenchSeeds()')), { sample_seed: 123456, prompt_seed: 99 });
  elements.singleSampleSeed.value = '9007199254740992';
  assert.throws(() => run('singleWorkbenchSeeds()'), /有效/);
  elements.singleSampleSeed.value = String(2 ** 50 + 1);
  assert.throws(() => run('singleWorkbenchSeeds()'), /有效/);
  elements.singleSampleSeed.value = String(2 ** 50);
  assert.equal(run('singleWorkbenchSeeds().sample_seed'), 2 ** 50);
  elements.singleSeedMode.value = 'random';
  assert.equal(run('singleWorkbenchSeeds()'), null);
});

test('explicit history loading returns mobile navigation to settings and focuses after the detail dialog closes', () => {
  const workspace = { dataset: { mobileView: 'gallery' } }, frames = [], calls = [];
  const tabs = ['settings', 'gallery'].map(view => ({ dataset: { mobileView: view }, classList: { toggle(_name, active) { this.active = active; } } }));
  const prompt = { focus(options) { calls.push(['focus', options.preventScroll]); }, scrollIntoView(options) { calls.push(['scroll', options.block]); } };
  const detail = { open: true };
  const { run } = singleContext({
    document: { querySelector: () => workspace, querySelectorAll: () => tabs, getElementById: () => prompt },
    ui: { detailDialog: detail }, requestAnimationFrame: callback => frames.push(callback),
  });
  run('singleWorkbenchState.mode = "single"; singleWorkbenchShowSettings()');
  assert.equal(workspace.dataset.mobileView, 'settings');
  assert.equal(tabs[0].classList.active, true); assert.equal(tabs[1].classList.active, false);
  assert.equal(calls.length, 0);
  detail.open = false; frames[0]();
  assert.deepEqual(calls, [['focus', true], ['scroll', 'center']]);
});

test('legacy v4 drafts migrate inactive model profiles into the random workbench', () => {
  const { ctx, run } = singleContext();
  ctx.settings = { model_profile: 'anima', count: 13, width: 896, extra_prompt: 'anima', workbench: 'random' };
  ctx.profiles = { anima: { extra_prompt: 'anima', modes: { pose: 'pool' }, fixed: { pose: 'inactive' }, pools: { pose: { ids: ['one'] } } }, krea2: { model_name: 'krea.safetensors', extra_prompt: 'old Krea prose', loras: [], managedTriggers: ['hint'], modes: {}, kreaPromptPoolEnabled: true, kreaPromptPoolSelection: ['saved'] } };
  const migrated = plain(run('singleWorkbenchLegacyDrafts(settings, profiles)'));
  assert.equal(migrated.krea2.settings.model_profile, 'krea2');
  assert.equal(migrated.krea2.settings.workbench, 'random');
  assert.equal(migrated.krea2.settings.extra_prompt, 'old Krea prose');
  assert.equal(migrated.krea2.settings.count, 13);
  assert.deepEqual(migrated.krea2.settings.krea_prompt_pool.ids, ['saved']);
  assert.equal(migrated.anima.settings.random_pose, true);
  assert.equal(migrated.anima.settings.fixed_pose, '');
  assert.equal(migrated.anima.profileDrafts.anima.fixed.pose, 'inactive');
});
test('applying a single preset preserves input image and active workbench while invalidating old preview', () => {
  let applied;
  const current = { workbench: 'single', generation_mode: 'img2img', single: { input_image_id: 'asset-current', prompt_job_id: 'old-job', processed_prompt_override: 'old result', positive_prompt: 'current' } };
  const { ctx, run } = singleContext({ readSettings: () => current, applySettings: value => { applied = plain(value); } });
  run('singleWorkbenchState.ready = true; singleWorkbenchState.mode = "single"');
  ctx.preset = { settings: { workbench: 'single', generation_mode: 'txt2img', cfg: 5, single: { input_image_id: '', positive_prompt: 'preset prompt' } } };
  run('singleWorkbenchApplyPreset(preset)');
  assert.equal(applied.workbench, 'single'); assert.equal(applied.generation_mode, 'img2img');
  assert.equal(applied.single.input_image_id, 'asset-current');
  assert.equal(applied.single.positive_prompt, 'preset prompt');
  assert.equal(applied.single.prompt_job_id, ''); assert.equal(applied.single.processed_prompt_override, null);
});
test('random workbench does not accept single-only settings when applying a shared preset', () => {
  let applied;
  const { ctx, run } = singleContext({ readSettings: () => ({ workbench: 'random', count: 12 }), applySettings: value => { applied = plain(value); } });
  run('singleWorkbenchState.ready = true');
  ctx.preset = { settings: { workbench: 'single', generation_mode: 'img2img', single: { input_image_id: 'source' }, clip_name: 'override', cfg: 4 } };
  run('singleWorkbenchApplyPreset(preset)');
  assert.equal(applied.workbench, 'random'); assert.equal(applied.count, 12); assert.equal(applied.cfg, 4);
  assert.equal(applied.single, undefined); assert.equal(applied.clip_name, undefined);
});
test('single source text is not modified by the random LoRA trigger reconciler', () => {
  const ctx = vm.createContext({ singleWorkbenchActive: () => true });
  vm.runInContext(functionSource('reconcileLoraTriggers'), ctx);
  assert.doesNotThrow(() => ctx.reconcileLoraTriggers([{ filename: 'first' }]));
});
test('favorite reads cannot replace the active section with a stale response', async () => {
  const pending = [];
  const ctx = vm.createContext({ favoritesRequest: 0, activeSection: 'pose', favoritesData: {}, favoritesAvailable: false,
    request: path => new Promise(resolve => pending.push({ path, resolve })), updateFavoriteSelectionButton() {}, toast() {}, currentView: () => ({ filters: {} }) });
  vm.runInContext(functionSource('loadFavorites'), ctx);
  const first = ctx.loadFavorites('pose'); ctx.activeSection = 'character'; const second = ctx.loadFavorites('character');
  pending[1].resolve({ items: ['current'] }); await second;
  pending[0].resolve({ items: ['stale'] }); await first;
  assert.deepEqual(plain(ctx.favoritesData.items), ['current']);
});
test('prompt cancellation keeps polling while stopping and drops failed job references', async () => {
  let polls = 0;
  const { ctx, run } = singleContext({ setTimeout: () => { polls++; return 1; }, schedulePersist() {}, request: async () => ({ id: 'job', status: 'stopping' }) });
  run('singleWorkbenchState.single = {prompt_job_id:"job"}; singleWorkbenchRenderProcessed = () => {}');
  await run('singleWorkbenchPollPrompt("job")');
  assert.equal(polls, 1); assert.equal(run('singleWorkbenchState.single.prompt_job_id'), 'job');
  ctx.request = async () => ({ id: 'job', status: 'cancelled' });
  await run('singleWorkbenchPollPrompt("job")');
  assert.equal(run('singleWorkbenchState.single.prompt_job_id'), '');
});
test('single Sesqui controls enforce model limits and random mode restores its original range', () => {
  const ui = { latent_upscale_scale: {} };
  const { elements, run } = singleContext({ ui });
  run('singleWorkbenchState.ready=true;singleWorkbenchState.mode="single"');
  elements.single_dual_upscale_method = { value: 'sesqui' };
  elements.single_latent_upscale_method = { value: 'sesqui' };
  run('singleWorkbenchRefreshLimits()');
  assert.equal(elements.single_dual_scale.max, '2');
  assert.equal(ui.latent_upscale_scale.max, '2');
  elements.single_dual_upscale_method.value = 'bilinear';
  run('singleWorkbenchRefreshLimits()');
  assert.equal(elements.single_dual_scale.max, '8');
  run('singleWorkbenchState.mode="random";singleWorkbenchRefreshLimits()');
  assert.equal(ui.latent_upscale_scale.min, '0.01');
  assert.equal(ui.latent_upscale_scale.max, '64');
});

test('node resource controls retain unavailable selections and apply live numeric ranges', () => {
  const ctx = context();
  ctx.Option = class { constructor(text, value) { this.textContent = text; this.value = value; this.dataset = {}; } };
  for (const name of ['populateResourceSelect', 'ensureSelectValue', 'applyNodeInputControl']) vm.runInContext(functionSource(name), ctx);
  const select = { tagName: 'SELECT', dataset: {}, value: '90', options: [], replaceChildren(...options) { this.options = options; }, add(option) { this.options.push(option); } };
  ctx.applyNodeInputControl(select, [['None', '90° CW', '90° CCW', '180°'], { default: 'None' }]);
  assert.equal(select.value, '90');
  assert.match(select.options[0].textContent, /当前不可用/);
  assert.equal(select.options[0].dataset.missing, 'true');
  ctx.ensureSelectValue(select, '270');
  assert.match(select.options.at(-1).textContent, /当前不可用/);
  assert.equal(select.value, '270');
  const numeric = { type: 'number', value: '64' };
  ctx.applyNodeInputControl(numeric, ['FLOAT', { min: 1, max: 4, step: 0.01 }]);
  assert.deepEqual(numeric, { type: 'number', value: '64', min: '1', max: '4', step: '0.01' });
  const quality = { ...select, dataset: {}, value: 'ULTRA', options: [] };
  ctx.applyNodeInputControl(quality, ['COMBO', { options: ['LOW', 'MEDIUM', 'HIGH', 'ULTRA'] }]);
  assert.equal(quality.options.length, 4);
  assert.equal(quality.value, 'ULTRA');
});

const gallerySource = fs.readFileSync(require('node:path').join(__dirname, '../static/single-gallery.js'), 'utf8');
function galleryContext() {
  const elements = new Map();
  const element = id => {
    if (!elements.has(id)) elements.set(id, {
      id, hidden: false, disabled: false, isConnected: true, attributes: {},
      getAttribute(key) { return key === 'src' ? this.src || null : this.attributes[key]; },
      setAttribute(key, value) { this.attributes[key] = value; },
      removeAttribute(key) { if (key === 'src') delete this.src; else delete this.attributes[key]; },
      focus() { this.focused = true; }, addEventListener() {},
    });
    return elements.get(id);
  };
  const ctx = vm.createContext({
    console, URL, Map, Set, queueMicrotask,
    document: { getElementById: element }, elements,
    imagePreview: { phase: 'open', context: { kind: 'single', record: null }, navigationRequest: 0, token: 0, preloaded: new Map() },
    ui: { imagePreviewDialog: { classList: { add() {}, remove() {} } }, imagePreviewImage: { hidden: false, focus() {} }, imagePreviewStatus: {}, detailImage: element('detailImage'), detailDialog: { open: true, showModal() { this.open = true; } } },
    detailContext: { origin: 'single' }, selectedRecord: null, regenerationRequest: 0, regenerationOptions: {},
    closeRegenerationDrawers() {}, renderDetail() {},
    cancelImagePreviewAnimation() {}, updatePreviewNavigation() {}, announcePreview() {},
    closeImagePreview() {}, request: async () => { throw new Error('Unexpected request'); },
    historyPage: 7, historyItems: [{ id: 999, settings: { workbench: 'random' } }], historyCache: new Map([[7, { untouched: true }]]),
  });
  vm.runInContext(gallerySource, ctx);
  vm.runInContext(functionSource('updateDetailRecord'), ctx);
  ctx.state = vm.runInContext('singleGalleryState', ctx);
  ctx.singleGalleryDecode = async () => {};
  ctx.renderCanvas = ctx.singleGalleryCanvas;
  ctx.renderFeedback = ctx.singleGalleryRender;
  vm.runInContext('singleGalleryRender = () => {}; singleGalleryCanvas = () => {};', ctx);
  Object.assign(ctx.state, { ready: true, active: true });
  return ctx;
}
function singleRecord(id) { return { id, filename: `image-${id}.png`, settings: { workbench: 'single' } }; }
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }

test('single history cursor remains independent of random paging and prepended generations', async () => {
  const ctx = galleryContext(), urls = [];
  const responses = [
    { items: [singleRecord(30), singleRecord(20)], total: 4, next_before_id: 20, has_more: true },
    { items: [singleRecord(10), singleRecord(5)], total: 4, next_before_id: 5, has_more: false },
    { items: [singleRecord(40), singleRecord(30)], total: 5, next_before_id: 30, has_more: true },
  ];
  ctx.request = async url => { urls.push(url); return responses.shift(); };
  await ctx.singleGalleryLoad(); await ctx.singleGalleryLoad({ more: true }); await ctx.singleGalleryRefresh();
  assert.deepEqual(plain(ctx.state.items.map(item => item.id)), [40, 30, 20, 10, 5]);
  assert.equal(ctx.state.nextBefore, 5);
  assert.equal(ctx.state.hasMore, false);
  assert.match(urls[1], /workbench=single.*before_id=20/);
  assert.equal(ctx.historyPage, 7);
  assert.equal(ctx.historyItems[0].id, 999);
  assert.equal(ctx.historyCache.get(7).untouched, true);
});

test('a refresh with more than one page of new images resets a contiguous cursor without moving the viewed old image', async () => {
  const ctx = galleryContext();
  Object.assign(ctx.state, { loaded: true, viewerOpen: true, items: [singleRecord(20), singleRecord(10)], viewerRecord: singleRecord(10), nextBefore: 10, hasMore: false });
  ctx.request = async () => ({ items: [singleRecord(60), singleRecord(50)], total: 6, next_before_id: 50, has_more: true });
  await ctx.singleGalleryRefresh();
  assert.deepEqual(plain(ctx.state.items.map(record => record.id)), [60, 50]);
  assert.equal(ctx.state.viewerRecord.id, 10);
  assert.equal(ctx.state.nextBefore, 50); assert.equal(ctx.state.hasMore, true);
  const responses = [{ items: [singleRecord(40), singleRecord(30)], total: 6, next_before_id: 30, has_more: true }, { items: [singleRecord(20), singleRecord(10)], total: 6, next_before_id: 10, has_more: false }];
  ctx.request = async () => responses.shift();
  ctx.switchPreviewImage = async () => true;
  await ctx.singleGalleryNavigate(-1);
  assert.equal(ctx.state.viewerRecord.id, 20);
  assert.deepEqual(plain(ctx.state.items.map(record => record.id)), [60, 50, 40, 30, 20, 10]);
});

test('changing workbench invalidates a pending single history response', async () => {
  const ctx = galleryContext(), first = deferred();
  ctx.request = () => first.promise;
  const pending = ctx.singleGalleryLoad();
  ctx.singleGalleryInvalidate();
  first.resolve({ items: [singleRecord(20)], next_before_id: 20, total: 1, has_more: false });
  await pending;
  assert.equal(ctx.state.items.length, 0);
  assert.equal(ctx.state.loaded, false);
  assert.equal(ctx.state.loading, false);
});

test('returning to single refreshes cached history when a single batch completed while random was active', async () => {
  const ctx = galleryContext(), response = deferred(), urls = [];
  Object.assign(ctx.state, { active: false, loaded: true, items: [singleRecord(20)], lastCompleted: singleRecord(20), nextBefore: 20, hasMore: false });
  ctx.request = url => { urls.push(url); return response.promise; };
  ctx.singleGalleryBatch({ id: 8, settings: { workbench: 'single' }, status: 'completed', completed: 1 });
  await Promise.resolve();
  assert.equal(urls.length, 0);
  assert.equal(ctx.state.terminalKey, '8:completed:1');
  ctx.singleGalleryMode(true);
  const pending = ctx.state.finishingPromise;
  assert.equal(urls.length, 1);
  assert.equal(urls[0], '/api/history?workbench=single&limit=24');
  response.resolve({ items: [{ ...singleRecord(30), batch_id: 8 }, singleRecord(20)], total: 2, next_before_id: 20, has_more: false });
  await pending;
  assert.equal(ctx.state.lastCompleted.id, 30);
  assert.deepEqual(plain(ctx.state.items.map(record => record.id)), [30, 20]);
  assert.equal(ctx.historyPage, 7);
});

test('failed cursor reads retain loaded records and retry the same older cursor', async () => {
  const ctx = galleryContext();
  Object.assign(ctx.state, { loaded: true, items: [singleRecord(20)], nextBefore: 20, hasMore: true });
  const urls = [];
  ctx.request = async url => { urls.push(url); throw new Error('offline'); };
  await ctx.singleGalleryLoad({ more: true });
  assert.equal(ctx.state.error, 'offline'); assert.equal(ctx.state.items[0].id, 20);
  ctx.request = async url => { urls.push(url); return { items: [singleRecord(10)], next_before_id: 10, total: 2, has_more: false }; };
  await ctx.singleGalleryLoad({ more: true });
  assert.equal(urls[0], urls[1]); assert.equal(ctx.state.error, '');
  assert.deepEqual(plain(ctx.state.items.map(item => item.id)), [20, 10]);
});

test('an old backend without cursor fields reports restart instead of claiming history is complete', async () => {
  const ctx = galleryContext();
  ctx.request = async () => ({ items: [singleRecord(50)], page: 1, pages: 4, total: 90 });
  await ctx.singleGalleryLoad();
  assert.match(ctx.state.error, /重启后台服务/);
  assert.equal(ctx.state.loaded, false);
  assert.equal(ctx.state.loading, false);
});

test('invalidating an older-page read preserves its cursor and permits retry', async () => {
  const ctx = galleryContext(), first = deferred();
  Object.assign(ctx.state, { loaded: true, items: [singleRecord(20)], nextBefore: 20, hasMore: true });
  ctx.request = () => first.promise;
  const pending = ctx.singleGalleryLoad({ more: true });
  ctx.singleGalleryInvalidate();
  first.resolve({ items: [singleRecord(10)], next_before_id: 10, has_more: false, total: 2 });
  await pending;
  assert.equal(ctx.state.loading, false); assert.equal(ctx.state.nextBefore, 20); assert.equal(ctx.state.hasMore, true);
  ctx.request = async url => { assert.match(url, /before_id=20/); return { items: [singleRecord(10)], next_before_id: 10, has_more: false, total: 2 }; };
  await ctx.singleGalleryLoad({ more: true });
  assert.deepEqual(plain(ctx.state.items.map(item => item.id)), [20, 10]);
});

test('a late bitmap selection cannot replace the newest single viewer selection', async () => {
  const ctx = galleryContext(), one = deferred(), two = deferred();
  Object.assign(ctx.state, { viewerOpen: true, viewerRecord: singleRecord(30) });
  ctx.switchPreviewImage = record => record.id === 20 ? one.promise : two.promise;
  const first = ctx.singleGallerySelect(singleRecord(20));
  const second = ctx.singleGallerySelect(singleRecord(10));
  two.resolve(true); await second; one.resolve(true); await first;
  assert.equal(ctx.state.viewerRecord.id, 10);
  assert.equal(ctx.selectedRecord.id, 10);
  assert.equal(ctx.regenerationRequest, 1);
  assert.equal(ctx.regenerationOptions, null);
});

test('canvas opens single detail first while a random archive entry keeps random origin', () => {
  const ctx = galleryContext();
  vm.runInContext(functionSource('openDetail'), ctx);
  ctx.state.lastCompleted = singleRecord(20);
  ctx.ui.detailDialog.open = false;
  ctx.singleGalleryOpenDetail();
  assert.equal(ctx.ui.detailDialog.open, true);
  assert.equal(ctx.detailContext.origin, 'single');
  assert.equal(ctx.detailContext.returnFocus, ctx.elements.get('singlePreviewOpen'));
  assert.equal(ctx.selectedRecord.id, 20);
  assert.equal(ctx.state.viewerOpen, false);
  ctx.openDetail(singleRecord(10));
  assert.equal(ctx.detailContext.origin, 'random');
  assert.equal(ctx.selectedRecord.id, 10);
});

test('single fullscreen belongs to its detail and closes without cancelling canvas history refresh', async () => {
  const ctx = galleryContext(), response = deferred();
  Object.assign(ctx.state, { viewerOpen: true, viewerRecord: singleRecord(20) });
  const viewerContext = ctx.singleGalleryContext();
  assert.equal(viewerContext.sourceElement, ctx.ui.detailImage);
  assert.equal(viewerContext.sourceContainer, ctx.ui.detailDialog);
  assert.equal(viewerContext.returnFocus, ctx.ui.detailImage);
  assert.equal(viewerContext.isValid(), true);
  ctx.request = () => response.promise;
  const pending = ctx.singleGalleryLoad();
  viewerContext.onClose();
  response.resolve({ items: [singleRecord(30)], total: 1, next_before_id: 30, has_more: false });
  await pending;
  assert.equal(ctx.state.lastCompleted.id, 30);
  assert.equal(ctx.state.viewerRecord.id, 20);
  assert.equal(ctx.state.viewerOpen, false);
  ctx.detailContext = { origin: 'random' };
  assert.equal(viewerContext.isValid(), false);
});

test('running and stopping lock canvas even without a frame while old detail selection survives completion', async () => {
  const ctx = galleryContext(), bitmap = deferred();
  Object.assign(ctx.state, { loaded: true, viewerOpen: true, viewerRecord: singleRecord(30), lastCompleted: singleRecord(30) });
  ctx.singleGalleryCanvas = ctx.renderCanvas;
  ctx.openDetail = () => { throw new Error('Busy canvas opened a detail'); };
  for (const status of ['running', 'stopping']) {
    ctx.singleGalleryBatch({ id: 1, settings: { workbench: 'single' }, status });
    assert.equal(ctx.state.busy, true);
    assert.equal(ctx.elements.get('singlePreviewOpen').disabled, true);
    assert.match(ctx.elements.get('singlePreviewStatus').textContent, /等待采样预览/);
    ctx.singleGalleryOpenDetail();
  }
  ctx.switchPreviewImage = () => bitmap.promise;
  ctx.singleGalleryRefresh = async () => { ctx.state.items = [{ ...singleRecord(40), batch_id: 1 }]; };
  const selection = ctx.singleGallerySelect(singleRecord(20));
  ctx.singleGalleryBatch({ id: 1, settings: { workbench: 'single' }, status: 'completed', completed: 1 });
  await Promise.resolve();
  bitmap.resolve(true); await selection;
  await ctx.state.finishingPromise;
  assert.equal(ctx.state.viewerRecord.id, 20);
  assert.equal(ctx.state.lastCompleted.id, 40);
  assert.equal(ctx.state.busy, false);
  assert.equal(ctx.elements.get('singlePreviewOpen').disabled, false);
});

test('deleting current single record selects older neighbor, then newer, then empty without touching random state', async () => {
  const ctx = galleryContext();
  Object.assign(ctx.state, { items: [singleRecord(30), singleRecord(20), singleRecord(10)], total: 3, lastCompleted: singleRecord(30), viewerRecord: singleRecord(20) });
  assert.equal((await ctx.singleGalleryDeleted(20)).id, 10);
  assert.equal(ctx.state.viewerRecord.id, 10);
  assert.equal((await ctx.singleGalleryDeleted(10)).id, 30);
  assert.equal(ctx.state.viewerRecord.id, 30);
  assert.equal(await ctx.singleGalleryDeleted(30), null);
  assert.equal(ctx.state.viewerRecord, null); assert.equal(ctx.state.lastCompleted, null); assert.equal(ctx.state.total, 0);
  assert.equal(ctx.historyPage, 7); assert.equal(ctx.historyItems[0].id, 999);
});

test('deleting the loaded boundary fetches older history before choosing a fallback', async () => {
  const ctx = galleryContext();
  Object.assign(ctx.state, { loaded: true, items: [singleRecord(30), singleRecord(20)], nextBefore: 20, hasMore: true, total: 3, viewerRecord: singleRecord(20) });
  ctx.request = async url => { assert.match(url, /before_id=20/); return { items: [singleRecord(10)], total: 2, next_before_id: 10, has_more: false }; };
  assert.equal((await ctx.singleGalleryDeleted(20)).id, 10);
  assert.equal(ctx.state.viewerRecord.id, 10);
  assert.equal(ctx.state.total, 2);
});

test('terminal refresh waits for an in-flight older page and resolves only after newest history', async () => {
  const ctx = galleryContext(), older = deferred(), newest = deferred();
  Object.assign(ctx.state, { loaded: true, items: [singleRecord(20)], nextBefore: 20, hasMore: true });
  const urls = [];
  ctx.request = url => { urls.push(url); return url.includes('before_id') ? older.promise : newest.promise; };
  const pendingOlder = ctx.singleGalleryLoad({ more: true });
  let resolved = false;
  const pendingRefresh = ctx.singleGalleryRefresh().then(() => { resolved = true; });
  older.resolve({ items: [singleRecord(10)], next_before_id: 10, has_more: false, total: 2 });
  await pendingOlder; await Promise.resolve();
  assert.equal(resolved, false);
  newest.resolve({ items: [singleRecord(30), singleRecord(20)], next_before_id: 20, has_more: true, total: 3 });
  await pendingRefresh;
  assert.equal(ctx.state.lastCompleted.id, 30);
  assert.equal(urls.length, 2);
});

test('staging a fullscreen bitmap does not commit single detail before decode succeeds', () => {
  const ctx = vm.createContext({ imagePreview: { context: { kind: 'single', record: singleRecord(20) } }, selectedRecord: { id: 99 }, renderDetail() { throw new Error('Single browsing rendered random detail'); } });
  for (const name of ['currentPreviewRecord', 'setCurrentPreviewRecord']) vm.runInContext(functionSource(name), ctx);
  ctx.setCurrentPreviewRecord(singleRecord(10));
  assert.equal(ctx.currentPreviewRecord().id, 10); assert.equal(ctx.selectedRecord.id, 99);
  ctx.imagePreview.context = { kind: 'random' };
  ctx.renderDetail = record => { ctx.rendered = record.id; };
  ctx.setCurrentPreviewRecord({ id: 98 });
  assert.equal(ctx.selectedRecord.id, 98); assert.equal(ctx.rendered, 98);
});

test('a failed single bitmap preserves detail and supports retry without a sidebar', async () => {
  const ctx = galleryContext();
  Object.assign(ctx.state, { viewerOpen: true, viewerRecord: singleRecord(30) });
  ctx.selectedRecord = ctx.state.viewerRecord;
  ctx.switchPreviewImage = async () => false;
  assert.equal(await ctx.singleGallerySelect(singleRecord(20)), false);
  assert.equal(ctx.selectedRecord.id, 30);
  assert.equal(ctx.state.viewerRecord.id, 30);
  ctx.renderFeedback();
  assert.equal(ctx.elements.get('singleViewerFeedback').hidden, false);
  assert.equal(ctx.elements.get('singleViewerRetry').hidden, false);
  assert.match(ctx.elements.get('singleViewerStatus').textContent, /重试/);
  ctx.switchPreviewImage = async () => true;
  assert.equal(await ctx.singleGalleryRetryViewer(), true);
  assert.equal(ctx.selectedRecord.id, 20);
  assert.equal(ctx.state.imageError, null);
});

test('a deleted old detail cannot close or cancel a different image opened while DELETE awaited', async () => {
  const ctx = galleryContext(), response = deferred();
  vm.runInContext(functionSource('deleteSelectedHistoryRecord'), ctx);
  ctx.confirm = () => true;
  ctx.toast = message => { throw new Error(message); };
  ctx.request = () => response.promise;
  ctx.ui.deleteRecord = { disabled: false };
  ctx.ui.detailDialog.close = () => { throw new Error('Closed a newer detail'); };
  ctx.closeImagePreview = () => { throw new Error('Closed a newer fullscreen'); };
  ctx.loadHistory = () => { throw new Error('Changed random history'); };
  Object.assign(ctx.state, { loaded: true, items: [singleRecord(30), singleRecord(20)], total: 2, viewerRecord: singleRecord(20) });
  ctx.selectedRecord = ctx.state.viewerRecord;
  const deleting = ctx.deleteSelectedHistoryRecord();
  ctx.detailContext = { origin: 'single' };
  ctx.selectedRecord = singleRecord(30); ctx.state.viewerRecord = ctx.selectedRecord;
  ctx.state.selectionRequest = 8;
  response.resolve({ ok: true }); await deleting;
  assert.equal(ctx.selectedRecord.id, 30);
  assert.equal(ctx.state.viewerRecord.id, 30);
  assert.equal(ctx.state.selectionRequest, 8);
  assert.equal(ctx.historyDirty, true);
  assert.equal(ctx.ui.deleteRecord.disabled, false);
});

test('single delete updates its detail in place and closes only when no neighbor remains', async () => {
  const ctx = galleryContext();
  vm.runInContext(functionSource('deleteSelectedHistoryRecord'), ctx);
  ctx.confirm = () => true; ctx.toast = message => { throw new Error(message); };
  ctx.request = async () => ({ ok: true }); ctx.ui.deleteRecord = {};
  ctx.ui.detailDialog.close = () => { ctx.ui.detailDialog.open = false; };
  Object.assign(ctx.state, { loaded: true, items: [singleRecord(30), singleRecord(20)], total: 2, viewerRecord: singleRecord(30) });
  ctx.selectedRecord = ctx.state.viewerRecord;
  await ctx.deleteSelectedHistoryRecord();
  assert.equal(ctx.ui.detailDialog.open, true);
  assert.equal(ctx.selectedRecord.id, 20);
  assert.equal(ctx.ui.detailImage.focused, true);
  await ctx.deleteSelectedHistoryRecord();
  assert.equal(ctx.ui.detailDialog.open, false);
  assert.equal(ctx.selectedRecord, null);
});

test('a rejected random page request cannot overwrite a newly opened single fullscreen', async () => {
  const ctx = galleryContext(), page = deferred();
  vm.runInContext(functionSource('navigatePreview'), ctx);
  ctx.imagePreview.context = { kind: 'random' };
  ctx.selectedHistoryLocation = () => ({ page: 1, items: [], index: -1 });
  ctx.previewNeighbor = () => ({ page: 2 });
  ctx.loadHistoryPageForPreview = () => page.promise;
  let focused = false;
  const navigating = ctx.navigatePreview(1, { isConnected: true, focus() { focused = true; } });
  ctx.imagePreview.navigationRequest++;
  ctx.imagePreview.context = { kind: 'single' }; ctx.imagePreview.phase = 'loading';
  page.reject(new Error('old offline page')); await navigating;
  assert.equal(ctx.imagePreview.phase, 'loading');
  assert.equal(focused, false);
});

test('decoded single image commits its detail before entry animation and Esc cannot reopen a closing viewer', async () => {
  const ctx = galleryContext(), entry = deferred();
  for (const name of ['switchPreviewImage', 'currentPreviewRecord', 'setCurrentPreviewRecord', 'closeImagePreview']) vm.runInContext(functionSource(name), ctx);
  Object.assign(ctx.state, { viewerOpen: true, viewerRecord: singleRecord(30) });
  ctx.selectedRecord = ctx.state.viewerRecord;
  ctx.imagePreview.context = ctx.singleGalleryContext();
  Object.assign(ctx.ui.imagePreviewImage, { src: '/api/images/30', style: {}, decode: async () => {} });
  ctx.ui.imagePreviewTitle = {};
  ctx.preloadRecord = async () => ({}); ctx.liveImagePreviewFrame = () => ({});
  ctx.imagePreviewFrame = () => ({ transform: 'none' }); ctx.imagePreviewSourceFrame = () => ({});
  ctx.layoutImagePreview = () => {}; ctx.previewMotion = (_, value) => value;
  ctx.imagePreviewReducedMotion = () => true; ctx.previewPositionLabel = () => '';
  ctx.preloadPreviewNeighbors = () => {}; ctx.animateImagePreview = () => {}; ctx.animateImagePreviewVeil = () => {};
  let animations = 0;
  ctx.playPreviewAnimation = () => ++animations === 1 ? Promise.resolve(true) : entry.promise;
  const selecting = ctx.singleGallerySelect(singleRecord(20));
  await new Promise(setImmediate);
  assert.equal(ctx.imagePreview.phase, 'opening');
  assert.equal(ctx.selectedRecord.id, 20);
  assert.equal(ctx.regenerationRequest, 1);
  ctx.closeImagePreview();
  assert.equal(ctx.imagePreview.phase, 'closing');
  entry.resolve(false);
  assert.equal(await selecting, false);
  assert.equal(ctx.imagePreview.phase, 'closing');
  assert.equal(ctx.selectedRecord.id, 20);
});

test('closing single detail returns focus to canvas and never reopens fullscreen', () => {
  const ctx = galleryContext();
  vm.runInContext(functionSource('finishDetailClose'), ctx);
  const canvas = ctx.document.getElementById('singlePreviewOpen');
  ctx.detailContext = { origin: 'single', returnFocus: canvas };
  ctx.ui.detailDialog.open = false;
  ctx.openImagePreview = () => { throw new Error('Reopened fullscreen'); };
  ctx.finishDetailClose();
  assert.equal(canvas.focused, true);
  assert.equal(ctx.detailContext, null);
  assert.equal(ctx.state.viewerRecord, null);
});

test('disabled single canvas returns detail focus to its preview region unless settings loading suppresses it', () => {
  const ctx = galleryContext();
  vm.runInContext(functionSource('finishDetailClose'), ctx);
  const canvas = ctx.document.getElementById('singlePreviewOpen');
  const region = ctx.document.getElementById('singlePreview');
  canvas.disabled = true;
  ctx.ui.detailDialog.open = false;
  for (const busy of [true, false]) {
    ctx.state.busy = busy;
    region.focused = false;
    ctx.detailContext = { origin: 'single', returnFocus: canvas };
    ctx.finishDetailClose();
    assert.equal(region.focused, true);
    assert.notEqual(canvas.focused, true);
  }
  region.focused = false;
  ctx.detailContext = { origin: 'single', returnFocus: null };
  ctx.finishDetailClose();
  assert.equal(region.focused, false);
});

test('fullscreen restores focus after native close rendering without stealing a new dialog', () => {
  const ctx = galleryContext(), frames = [];
  for (const name of ['clearPreviewCache', 'finishImagePreviewClose', 'restoreImagePreviewCloseFocus']) vm.runInContext(functionSource(name), ctx);
  ctx.requestAnimationFrame = fn => frames.push(fn);
  ctx.document.documentElement = { classList: { remove() {} } };
  Object.assign(ctx.imagePreview, { pointers: new Set(), context: ctx.singleGalleryContext() });
  ctx.ui.imagePreviewVeil = { getAnimations: () => [] };
  ctx.ui.imagePreviewImage.classList = { remove() {} }; ctx.ui.imagePreviewImage.removeAttribute = () => {}; ctx.ui.imagePreviewAnnouncement = {};
  Object.assign(ctx.ui.imagePreviewDialog, { dataset: {}, open: true, close() { this.open = false; } });
  let focuses = 0;
  ctx.ui.detailImage.focus = () => { focuses++; };
  ctx.finishImagePreviewClose();
  assert.equal(focuses, 0); assert.equal(frames.length, 0);
  ctx.restoreImagePreviewCloseFocus();
  frames.shift()(); assert.equal(focuses, 0);
  frames.shift()(); assert.equal(focuses, 1);
  ctx.imagePreview.context = ctx.singleGalleryContext(); ctx.imagePreview.phase = 'open';
  ctx.finishImagePreviewClose();
  ctx.restoreImagePreviewCloseFocus();
  ctx.detailContext = { origin: 'random' };
  frames.shift()(); frames.shift()(); assert.equal(focuses, 1);
});
