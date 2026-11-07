const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
const single = fs.readFileSync(path.join(__dirname, '../static/single-workbench.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));

function loadFunctions(ctx, names) {
  for (const name of names) {
    let start = app.indexOf(`function ${name}(`);
    assert.ok(start >= 0, name);
    if (app.slice(start - 6, start) === 'async ') start -= 6;
    vm.runInContext(app.slice(start, app.indexOf('\n}', start) + 2), ctx);
  }
}

function setup({ prefix = '', positive = 'portrait', suffix = '', managed = [], inventory = [] } = {}) {
  const events = [], saved = new Map();
  let ctx;
  const field = (id, value, singlePath) => ({
    id, value, type: 'textarea', tagName: 'TEXTAREA', dataset: singlePath ? { singlePath } : {},
    dispatchEvent(event) {
      events.push({ id, type: event.type, bubbles: event.bubbles });
      if (id === 'single_prefix' && event.type === 'input') ctx.singleWorkbenchForgetRemovedTriggers();
      return true;
    },
  });
  const elements = {
    single_prefix: field('single_prefix', prefix, 'prefix'),
    single_positive_prompt: field('single_positive_prompt', positive, 'positive_prompt'),
    single_suffix: field('single_suffix', suffix, 'suffix'),
    singleNegative: field('singleNegative', 'negative'),
    singleSeedMode: field('singleSeedMode', 'fixed'),
    singleSampleSeed: field('singleSampleSeed', '17'),
  };
  const ui = {
    extra_prompt: field('extra_prompt', 'random draft'),
    loraSearch: { value: '' }, loraCatalogEmpty: {}, loraCatalogMeta: {},
    loraCatalog: { children: [], replaceChildren(...children) { this.children = children; } },
    model_profile: { value: 'anima' }, model_name: { value: 'base' }, base_mode: { value: 'normal' },
    sampler_name: { value: 'euler' }, scheduler: { value: 'simple' }, krea_prompt_pool_enabled: {}, draftStatus: {},
  };
  const config = { model_profile: 'anima', model_name: 'base', base_mode: 'normal', loras: [], single: {} };
  ctx = vm.createContext({
    Event, Set, Map, Date, clearTimeout, setTimeout, document: {
      getElementById: id => elements[id] || null,
      querySelectorAll: selector => selector === '[data-single-path]' ? Object.values(elements).filter(item => item.dataset.singlePath) : [],
      createElement: () => ({
        handlers: {}, querySelector: () => null,
        addEventListener(name, callback) { this.handlers[name] = callback; },
      }),
    },
    ui, draft: { loras: [], managedTriggers: ['random managed'] },
    loraInventoryLoaded: true, loraInventoryProfile: 'anima', loraInventoryError: '', loraInventory: plain(inventory),
    loraClassificationItems: [], activeProfileId: 'anima', profileDrafts: {}, initialized: false,
    SECTIONS: [], defaults: config, config: {}, activeSection: 'character', poolViews: {}, VIEW_KEY: 'view',
    clone: plain, profileConfig: () => ({}), profileSettingsDefaults: profile => ({ ...plain(config), model_profile: profile }),
    randomWorkbenchDefaultSettings: profile => ({ ...plain(config), model_profile: profile, workbench: 'random', extra_prompt: 'random draft' }),
    escapeHtml: String, loraPresentation: item => ({ label: item.filename, basename: item.filename, path: item.filename, folder: '' }),
    schedulePersist() {}, renderLoras() {}, invalidatePromptNormalization() {}, clearNormalizationUndo() {},
    normalizeSettings: settings => ({ ...plain(config), ...plain(settings) }), normalizeUpscalers: value => value || {},
    setUpscalerControls() {}, renderDimensions() {}, renderKreaPromptPool() {}, updatePeopleTotal() {}, updatePoolOverview() {}, updateRepairControls() {},
    ensureSelectValue(element, value) { element.value = value; },
    formatSavedTime: () => 'now', localStorage: { setItem: (key, value) => saved.set(key, value) },
  });
  ctx.currentProfile = () => ctx.activeProfileId;
  ctx.replaceLoraInventoryItem = item => {
    ctx.loraInventory = ctx.loraInventory.filter(old => old.filename !== item.filename).concat([item]);
  };
  ctx.restoreProfileDraft = (_profile, settings) => {
    ctx.draft = { loras: plain(settings.loras || []), managedTriggers: plain(settings.lora_managed_triggers || []) };
  };
  vm.runInContext(single, ctx);
  const state = vm.runInContext('singleWorkbenchState', ctx);
  state.ready = true;
  state.mode = 'single';
  state.single = plain(ctx.singleWorkbenchDefaultSettings('anima').single);
  state.single.lora_prefix_managed_triggers = managed;
  Object.assign(ctx, {
    singleWorkbenchCancelInput() {}, singleWorkbenchRenderProcessed() {}, singleWorkbenchRestoreInput() {},
    singleWorkbenchRenderMode() {}, singleWorkbenchLoadFavorites() {}, singleWorkbenchLayout() {},
  });
  loadFunctions(ctx, ['normalizeTriggerWords', 'promptTokens', 'triggerIdentity', 'normalizedPath', 'loraItem',
    'reconcileLoraTriggers', 'forgetRemovedManagedTriggers', 'addManagedLoras', 'removeManagedLoras', 'renderLoraCatalog', 'applySettings']);
  ctx.readSettings = () => ctx.singleWorkbenchRead({
    workbench: state.mode, model_profile: ctx.currentProfile(), loras: plain(ctx.draft.loras),
    lora_managed_triggers: plain(ctx.draft.managedTriggers), extra_prompt: ui.extra_prompt.value,
  });
  return { ctx, state, elements, ui, events, saved };
}

const lora = (filename, trigger_words, options = {}) => ({
  filename, trigger_words, model_profile: 'anima', classification_status: 'recognized', default_strength: .8, ...options,
});
const ownership = state => plain(state.single.lora_prefix_managed_triggers);

test('quick catalog clicks and manager bulk additions both write missing triggers into the single prefix', () => {
  const first = lora('ink.safetensors', ['blue ink']);
  const second = lora('paper.safetensors', ['paper grain']);
  const { ctx, state, ui, elements, events } = setup({ inventory: [first] });
  ctx.renderLoraCatalog();
  ui.loraCatalog.children[0].handlers.click();
  assert.equal(elements.single_prefix.value, 'blue ink');
  assert.equal(ctx.draft.loras[0].strength, .8);
  const result = ctx.addManagedLoras([second]);
  assert.equal(result.added.length, 1);
  assert.equal(elements.single_prefix.value, 'blue ink, paper grain');
  assert.deepEqual(ownership(state), ['blue ink', 'paper grain']);
  assert.equal(elements.single_positive_prompt.value, 'portrait');
  assert.equal(elements.singleNegative.value, 'negative');
  assert.equal(ui.extra_prompt.value, 'random draft');
  assert.deepEqual(plain(ctx.draft.managedTriggers), ['random managed']);
  assert.deepEqual(events, [{ id: 'single_prefix', type: 'input', bubbles: true }, { id: 'single_prefix', type: 'input', bubbles: true }]);
});

test('single additions deduplicate across all positive fields without taking ownership of hand-written words', () => {
  const { ctx, state, elements } = setup({ prefix: 'BLUE INK', positive: 'portrait, paper grain', suffix: 'soft fur' });
  ctx.addManagedLoras([lora('one', ['blue ink', 'PAPER GRAIN', 'Soft Fur', 'new word'])]);
  assert.equal(elements.single_prefix.value, 'BLUE INK, new word');
  assert.deepEqual(ownership(state), ['new word']);
  ctx.removeManagedLoras(['one']);
  assert.equal(elements.single_prefix.value, 'BLUE INK');
  assert.equal(elements.single_positive_prompt.value, 'portrait, paper grain');
  assert.equal(elements.single_suffix.value, 'soft fur');
  assert.deepEqual(ownership(state), []);
});

test('disabling and removing shared triggers cleans only words no remaining enabled LoRA needs', () => {
  const { ctx, state, elements } = setup({ prefix: 'hand written' });
  const { added } = ctx.addManagedLoras([lora('first', ['shared', 'first only']), lora('second', ['SHARED', 'second only'])]);
  added[0].enabled = false;
  ctx.reconcileLoraTriggers();
  assert.equal(elements.single_prefix.value, 'hand written, shared, second only');
  assert.deepEqual(ownership(state), ['shared', 'second only']);
  added[0].enabled = true;
  ctx.reconcileLoraTriggers([added[0]]);
  assert.equal(elements.single_prefix.value, 'hand written, shared, second only, first only');
  ctx.removeManagedLoras(['second']);
  assert.equal(elements.single_prefix.value, 'hand written, shared, first only');
  ctx.removeManagedLoras(['first']);
  assert.equal(elements.single_prefix.value, 'hand written');
});

test('effective trigger edits, empty overrides and reset reconcile owned prefix words', () => {
  const { ctx, state, elements } = setup({ prefix: 'manual' });
  const { added } = ctx.addManagedLoras([lora('one', ['old word'])]);
  ctx.replaceLoraInventoryItem(lora('one', ['new word'], { trigger_override: true }));
  ctx.reconcileLoraTriggers(added);
  assert.equal(elements.single_prefix.value, 'manual, new word');
  assert.deepEqual(ownership(state), ['new word']);
  ctx.replaceLoraInventoryItem(lora('one', [], { trigger_override: true }));
  ctx.reconcileLoraTriggers(added);
  assert.equal(elements.single_prefix.value, 'manual');
  assert.deepEqual(ownership(state), []);
  ctx.replaceLoraInventoryItem(lora('one', ['old word'], { trigger_override: false }));
  ctx.reconcileLoraTriggers(added);
  assert.equal(elements.single_prefix.value, 'manual, old word');
});

test('missing inventory and unknown trigger metadata preserve existing automatic prefix words', () => {
  const { ctx, state, elements } = setup({ prefix: 'manual' });
  const { added } = ctx.addManagedLoras([lora('one', ['keep'])]);
  ctx.loraInventoryLoaded = false;
  ctx.loraInventory = [];
  ctx.reconcileLoraTriggers(added);
  assert.equal(elements.single_prefix.value, 'manual, keep');
  ctx.loraInventoryLoaded = true;
  ctx.reconcileLoraTriggers(added);
  assert.equal(elements.single_prefix.value, 'manual, keep');
  ctx.loraInventory = [lora('one', [], { trigger_metadata_available: false })];
  ctx.reconcileLoraTriggers(added);
  assert.equal(elements.single_prefix.value, 'manual, keep');
  assert.deepEqual(ownership(state), ['keep']);
  ctx.loraInventory = [lora('one', [])];
  ctx.reconcileLoraTriggers(added);
  assert.equal(elements.single_prefix.value, 'manual');
});

test('manual prefix edits release ownership and retyped words are protected from later removal', () => {
  const { ctx, state, elements } = setup();
  const { added } = ctx.addManagedLoras([lora('one', ['blue ink'])]);
  elements.single_prefix.value = 'my edited ink';
  ctx.singleWorkbenchForgetRemovedTriggers();
  assert.deepEqual(ownership(state), []);
  elements.single_prefix.value = 'my edited ink, blue ink';
  ctx.singleWorkbenchForgetRemovedTriggers();
  ctx.reconcileLoraTriggers(added);
  assert.deepEqual(ownership(state), []);
  ctx.removeManagedLoras(['one']);
  assert.equal(elements.single_prefix.value, 'my edited ink, blue ink');
});

test('removing and re-enabling after a manual deletion adds the missing trigger again', () => {
  const { ctx, state, elements } = setup();
  const { added } = ctx.addManagedLoras([lora('one', ['blue ink'])]);
  elements.single_prefix.value = '';
  ctx.singleWorkbenchForgetRemovedTriggers();
  assert.deepEqual(ownership(state), []);
  added[0].enabled = false;
  ctx.reconcileLoraTriggers();
  assert.equal(elements.single_prefix.value, '');
  added[0].enabled = true;
  ctx.reconcileLoraTriggers(added);
  assert.equal(elements.single_prefix.value, 'blue ink');
  assert.deepEqual(ownership(state), ['blue ink']);
});

test('trigger operations preserve multiline and weighted hand-written prompt formatting', () => {
  const prefix = '  warm light,\n(detail, texture:1.2),\nsoft focus';
  const { ctx, elements } = setup({ prefix });
  ctx.addManagedLoras([lora('one', ['(detail, texture:1.2)', 'blue ink'])]);
  assert.equal(elements.single_prefix.value, `${prefix}, blue ink`);
  ctx.reconcileLoraTriggers();
  assert.equal(elements.single_prefix.value, `${prefix}, blue ink`);
  ctx.removeManagedLoras(['one']);
  assert.equal(elements.single_prefix.value, prefix);
});

for (const prefix of ["a girl's portrait", "don't change this style", "artists' style"]) {
  test(`natural-language apostrophes preserve trigger ownership: ${prefix}`, () => {
    const { ctx, state, elements } = setup({ prefix });
    const { added } = ctx.addManagedLoras([lora('one', ['blue ink'])]);
    assert.equal(elements.single_prefix.value, `${prefix}, blue ink`);
    assert.deepEqual(ownership(state), ['blue ink']);
    ctx.reconcileLoraTriggers(added);
    assert.equal(elements.single_prefix.value, `${prefix}, blue ink`);
    added[0].enabled = false;
    ctx.reconcileLoraTriggers();
    assert.equal(elements.single_prefix.value, prefix);
    assert.deepEqual(ownership(state), []);
  });
}

test('paired quotes and escaped commas remain intact during trigger deduplication and cleanup', () => {
  const prefix = '"grain, ink",\n\'fine, paper\', blue\\, ink';
  const { ctx, state, elements } = setup({ prefix });
  ctx.addManagedLoras([lora('one', ['"grain, ink"', "'fine, paper'", 'blue\\, ink', 'new word'])]);
  assert.equal(elements.single_prefix.value, `${prefix}, new word`);
  assert.deepEqual(ownership(state), ['new word']);
  ctx.removeManagedLoras(['one']);
  assert.equal(elements.single_prefix.value, prefix);
});

test('restoring a single draft reconciles its restored prefix instead of overwriting the previous form', () => {
  const item = lora('one', ['blue ink']);
  const { ctx, state, elements } = setup({ prefix: 'previous form', inventory: [item] });
  ctx.applySettings({
    workbench: 'single', model_profile: 'anima', loras: [{ filename: item.filename, enabled: true, strength: 1 }],
    lora_managed_triggers: ['hand written'],
    single: { prefix: 'loaded prefix', positive_prompt: 'loaded portrait', suffix: 'loaded suffix', lora_prefix_managed_triggers: [] },
  });
  assert.equal(elements.single_prefix.value, 'loaded prefix, blue ink');
  assert.equal(elements.single_positive_prompt.value, 'loaded portrait');
  assert.equal(elements.single_suffix.value, 'loaded suffix');
  assert.deepEqual(ownership(state), ['blue ink']);
  const saved = plain(ctx.readSettings());
  assert.equal(saved.single.prefix, 'loaded prefix, blue ink');
  assert.deepEqual(saved.single.lora_prefix_managed_triggers, ['blue ink']);
  saved.lora_managed_triggers = ['loaded prefix', 'blue ink'];
  ctx.applySettings(saved);
  ctx.removeManagedLoras(['one']);
  assert.equal(elements.single_prefix.value, 'loaded prefix');
});

test('legacy saved settings do not claim hand-written prefix words from backend runtime triggers', () => {
  const item = lora('one', ['blue ink']);
  const { ctx, state, elements } = setup({ inventory: [item] });
  ctx.applySettings({
    workbench: 'single', model_profile: 'anima', loras: [{ filename: item.filename, enabled: true, strength: 1 }],
    lora_managed_triggers: ['blue ink'], single: { prefix: 'blue ink', positive_prompt: 'portrait' },
  });
  assert.deepEqual(ownership(state), []);
  ctx.removeManagedLoras(['one']);
  assert.equal(elements.single_prefix.value, 'blue ink');
});

test('manual prefix normalization keeps automatic trigger ownership through read and disable', async () => {
  const { ctx, state, elements } = setup({ prefix: 'manual  ' });
  const { added } = ctx.addManagedLoras([lora('one', ['My_Trigger'])]);
  loadFunctions(ctx, ['promptNormalizationContext', 'promptElementForField', 'applyPromptField', 'normalizePromptFields']);
  ctx.promptNormalizationEpoch = 0;
  ctx.promptNormalizationRequests = new Map();
  const requests = [];
  ctx.request = async (url, options) => {
    requests.push({ url, body: JSON.parse(options.body) });
    return { fields: { single_prefix: 'manual, My_Trigger' }, changed: true, changes: [] };
  };
  state.promptTarget = elements.single_prefix;
  await ctx.singleWorkbenchNormalizePrompt();
  assert.equal(requests[0].url, '/api/prompts/normalize');
  assert.deepEqual(requests[0].body.managedTriggers, ['My_Trigger']);
  assert.equal(elements.single_prefix.value, 'manual, My_Trigger');
  assert.deepEqual(plain(ctx.readSettings().single.lora_prefix_managed_triggers), ['My_Trigger']);
  added[0].enabled = false;
  ctx.reconcileLoraTriggers();
  assert.equal(elements.single_prefix.value, 'manual');
  assert.deepEqual(ownership(state), []);
});

test('switching workbenches persists single ownership and leaves the random draft independent', () => {
  const item = lora('one', ['blue ink']);
  const { ctx, state, elements, ui, saved } = setup({ prefix: 'manual', inventory: [item] });
  ctx.addManagedLoras([item]);
  ctx.singleWorkbenchSwitch('random');
  assert.equal(ui.extra_prompt.value, 'random draft');
  const snapshot = JSON.parse(saved.get('anima-studio:workbenches:v1'));
  assert.deepEqual(snapshot.drafts.single.anima.settings.single.lora_prefix_managed_triggers, ['blue ink']);
  assert.equal(snapshot.drafts.single.anima.settings.single.prefix, 'manual, blue ink');
  ctx.singleWorkbenchSwitch('single');
  assert.equal(elements.single_prefix.value, 'manual, blue ink');
  assert.deepEqual(ownership(state), ['blue ink']);
  ctx.removeManagedLoras(['one']);
  assert.equal(elements.single_prefix.value, 'manual');
});

test('random workbench retains its existing extra-prompt trigger ownership behavior', () => {
  const { ctx, state, ui, elements } = setup({ prefix: 'single unchanged' });
  state.mode = 'random';
  ctx.draft.managedTriggers = [];
  ctx.addManagedLoras([lora('one', ['blue ink'])]);
  assert.equal(ui.extra_prompt.value, 'random draft, blue ink');
  assert.deepEqual(plain(ctx.draft.managedTriggers), ['blue ink']);
  assert.equal(elements.single_prefix.value, 'single unchanged');
  ctx.removeManagedLoras(['one']);
  assert.equal(ui.extra_prompt.value, 'random draft');
});

test('model profile switching keeps automatic prefix words and ownership in their own single drafts', () => {
  const first = lora('anima', ['anima word']);
  const second = lora('krea', ['krea word'], { model_profile: 'krea2' });
  const { ctx, state, elements } = setup({ prefix: 'anima manual', inventory: [first, second] });
  ctx.addManagedLoras([first]);
  ctx.singleWorkbenchSwitch('single', 'krea2');
  ctx.loraInventoryProfile = 'krea2';
  elements.single_prefix.value = 'krea manual';
  ctx.addManagedLoras([second]);
  assert.equal(elements.single_prefix.value, 'krea manual, krea word');
  ctx.singleWorkbenchSwitch('single', 'anima');
  ctx.loraInventoryProfile = 'anima';
  ctx.reconcileLoraTriggers(ctx.draft.loras);
  assert.equal(elements.single_prefix.value, 'anima manual, anima word');
  assert.deepEqual(ownership(state), ['anima word']);
  ctx.removeManagedLoras(['anima']);
  assert.equal(elements.single_prefix.value, 'anima manual');
  ctx.singleWorkbenchSwitch('single', 'krea2');
  ctx.loraInventoryProfile = 'krea2';
  ctx.reconcileLoraTriggers(ctx.draft.loras);
  assert.equal(elements.single_prefix.value, 'krea manual, krea word');
  assert.deepEqual(ownership(state), ['krea word']);
  ctx.removeManagedLoras(['krea']);
  assert.equal(elements.single_prefix.value, 'krea manual');
});
