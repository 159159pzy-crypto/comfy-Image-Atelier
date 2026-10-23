const createFrontendContext = require('./helpers/frontend-context.cjs');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const plain = value => JSON.parse(JSON.stringify(value));
const sources = ['single-workbench.js', 'single-compatibility.js', 'single-layout.js']
  .map(file => fs.readFileSync(path.join(__dirname, '../static', file), 'utf8'));

function setup() {
  const elements = {}, notices = [], persisted = [];
  const document = { activeElement: null };
  class Element {
    constructor(tagName = 'div', id = '') {
      Object.assign(this, { tagName: tagName.toUpperCase(), id, dataset: {}, children: [], attributes: new Map(),
        hidden: false, disabled: false, isConnected: true, textContent: '', value: '', style: {},
        scrollLeft: 0, scrollTop: 0, clientWidth: 500, clientHeight: 200, offsetLeft: 0, offsetWidth: 100 });
      this.classList = { add() {}, remove() {}, contains: () => false };
      if (id) elements[id] = this;
    }
    append(...children) { for (const child of children) { child.parentElement = this; this.children.push(child); } }
    replaceChildren(...children) { this.children = []; this.append(...children); }
    get disabled() { return this._disabled || false; }
    set disabled(value) {
      this._disabled = Boolean(value);
      // Native disabled controls surrender focus before the layout can hide
      // their card; regressions must not rely on a stale activeElement.
      if (value && document.activeElement === this) document.activeElement = document.body;
    }
    setAttribute(key, value) { this.attributes.set(key, String(value)); }
    getAttribute(key) { return this.attributes.get(key) ?? null; }
    removeAttribute(key) { this.attributes.delete(key); }
    contains(node) { return node === this || this.children.some(child => child.contains(node)); }
    focus() { document.activeElement = this; }
    getClientRects() {
      for (let node = this; node; node = node.parentElement) if (node.hidden) return [];
      return [{}];
    }
    querySelectorAll(selector) {
      const descendants = this.children.flatMap(child => [child, ...child.querySelectorAll('*')]);
      if (selector === '*') return descendants;
      if (selector === '[data-single-category]') return descendants.filter(child => child.dataset.singleCategory);
      return descendants.filter(child => selector.split(',').map(tag => tag.trim().toUpperCase()).includes(child.tagName));
    }
    closest(selector) {
      for (let node = this; node; node = node.parentElement) {
        if (selector === '[hidden]' && node.hidden) return node;
        if (selector === '[data-single-group]' && node.dataset.singleGroup) return node;
      }
      return null;
    }
  }
  class HTMLInputElement extends Element {}
  class HTMLTextAreaElement extends Element {}
  document.body = new Element('body');
  document.activeElement = document.body;
  const form = new Element('form', 'settingsForm'), controls = new Element('div', 'singleWorkbenchControls');
  const tablist = new Element('div', 'singleParameterTabs');
  document.body.append(form); form.append(tablist, controls);
  const tabs = ['common', 'prompt', 'repair', 'more'].map((category, index) => {
    const button = new Element('button', `singleCategory_${category}`);
    button.dataset.singleCategory = category; button.offsetLeft = index * 100;
    tablist.append(button); return button;
  });
  const panels = ['common', 'prompt', 'repair', 'more'].map(category => {
    const card = new Element('section', `card_${category}`);
    card.dataset.singleGroup = category; controls.append(card); return card;
  });
  const compatibility = new Element('section', 'singleCompatibilitySection');
  Object.assign(compatibility.dataset, { singleGroup: 'more', singleWhen: 'compatibility' });
  compatibility.hidden = true; controls.append(compatibility); panels.push(compatibility);
  for (const [tag, id] of [['dl', 'singleCompatibilityValues'], ['span', 'singleCompatibilityCount'],
    ['button', 'singleResetCompatibility']]) compatibility.append(new Element(tag, id));
  for (const id of ['singleNegative', 'singleSeedMode', 'singleSampleSeed', 'randomArchive', 'singleCommandSlot', 'singlePreviewRail']) new Element('div', id);
  for (const id of ['single_dual_second_sampler_name', 'single_dual_second_scheduler']) {
    new Element('input', id); new Element('datalist', `${id}_options`);
  }
  elements.singleSeedMode.value = 'fixed'; elements.singleSampleSeed.value = '0';
  const galleryTab = new Element('button');
  document.getElementById = id => elements[id] || null;
  document.createElement = tag => new Element(tag);
  document.querySelectorAll = selector => {
    if (selector === '#singleParameterTabs [data-single-category]') return tabs;
    if (selector === '#settingsForm [data-single-group]') return panels;
    return [];
  };
  document.querySelector = selector => {
    if (selector === '[data-mobile-view="gallery"]') return galleryTab;
    const category = selector.match(/^#singleParameterTabs \[data-single-category="([^"]+)"\]$/)?.[1];
    return tabs.find(button => button.dataset.singleCategory === category) || null;
  };
  let settings = {}, profile = 'anima';
  const ctx = createFrontendContext({
    console, document, Element, HTMLElement: Element, HTMLInputElement, HTMLTextAreaElement, clearTimeout,
    window: { scrollX: 0, scrollY: 0, scrollTo() {} },
    config: { single_defaults: {} }, resources: { samplers: [], schedulers: [] }, profileDrafts: {},
    SECTIONS: ['character', 'clothing', 'pose', 'background', 'expression'],
    currentProfile: () => profile,
    profileConfig: value => ({ clip_name: `${value}-clip`, vae_name: `${value}-vae`, clip_type: `${value}-type` }),
    profileSettingsDefaults: value => ({ model_profile: value, loras: [], upscalers: {
      latent: { enabled: false }, seedvr2: { enabled: false, weight_dtype: 'default' },
      nvidia: { enabled: false, resize_type: 'scale by multiplier', quality: 'high' }, face_refine: { enabled: false },
    } }),
    ui: { base_mode: { value: 'normal' }, latent_upscale_enabled: { checked: false }, detailDialog: { close() {} } },
    toast: (...args) => notices.push(args), closeImagePreview() {},
    singleWorkbenchCancelInput() {}, singleWorkbenchRestoreInput() {},
  });
  for (const source of sources) vm.runInContext(source, ctx);
  const state = vm.runInContext('singleWorkbenchState', ctx);
  const layout = vm.runInContext('singleLayoutState', ctx);
  Object.assign(state, { ready: true, mode: 'single' });
  // The production initializer moves shared controls into these groups. This
  // fixture starts with that DOM in place so layout and visibility remain real.
  Object.assign(layout, { initialized: true, active: true, categoryPlacement: 'columns',
    singleFields: [elements.singleResetCompatibility], singleDisabled: new Map(),
    columnsMobile: { matches: false }, mobile: { matches: false } });
  ctx.readSettings = () => ctx.singleWorkbenchRead(settings);
  ctx.applySettings = next => {
    settings = plain(next); profile = settings.model_profile;
    ctx.singleWorkbenchApply(settings);
  };
  ctx.singleWorkbenchRenderMode = () => {
    controls.hidden = !ctx.singleWorkbenchActive();
    ctx.singleWorkbenchLayout();
  };
  ctx.singleWorkbenchRenderProcessed = () => {};
  ctx.singleWorkbenchInvalidatePrompt = () => {};
  ctx.singleWorkbenchLoadFavorites = () => {};
  ctx.singleWorkbenchShowSettings = () => {};
  ctx.singleWorkbenchPersist = () => {
    const draft = plain(ctx.singleWorkbenchCapture());
    state.drafts[state.mode][profile] = draft;
    persisted.push(draft);
  };
  ctx.applySettings(ctx.singleWorkbenchDefaultSettings(profile));
  return { ctx, state, layout, elements, document, notices, persisted, compatibility, tabs,
    values: () => elements.singleCompatibilityValues.children.map(node => node.textContent),
    load: overrides => ctx.applySettings(ctx.singleWorkbenchMerge(ctx.singleWorkbenchDefaultSettings(profile), overrides)),
  };
}

test('default compatibility remains absent through categories and desktop/mobile relayouts', () => {
  const { ctx, layout, compatibility, elements } = setup();
  for (const mobile of [false, true, false]) {
    layout.columnsMobile.matches = mobile; layout.mobile.matches = mobile;
    for (const category of ['more', 'common', 'repair', 'prompt', 'more']) {
      ctx.singleWorkbenchSelectCategory(category);
      assert.equal(compatibility.hidden, true);
      assert.equal(compatibility.dataset.singleUnavailable, 'true');
      assert.equal(elements.singleCompatibilityValues.children.length, 0);
      assert.equal(elements.singleResetCompatibility.disabled, true);
    }
  }
});

test('hidden legacy parameters appear only in presets and disappear after replacing the draft', () => {
  const { ctx, load, compatibility, values } = setup();
  load({ single: { normal_pipeline: 'secondary', weight_dtype: 'fp8_e4m3fn' } });
  assert.equal(compatibility.hidden, true, 'loading old values does not change the selected category');
  ctx.singleWorkbenchSelectCategory('more');
  assert.equal(compatibility.hidden, false);
  assert.ok(values().includes('旧副管线'));
  assert.ok(values().includes('fp8_e4m3fn'));
  ctx.singleWorkbenchSelectCategory('repair');
  assert.equal(compatibility.hidden, true);
  ctx.singleWorkbenchSelectCategory('more');
  assert.equal(compatibility.hidden, false);
  load({});
  assert.equal(compatibility.hidden, true);
  assert.deepEqual(values(), []);
});

test('reset hides the focused compatibility card, announces success, and persists defaults', () => {
  const { ctx, load, state, layout, compatibility, elements, document, notices, persisted } = setup();
  load({ single: { normal_pipeline: 'secondary' } }); state.promptSeed = 55;
  ctx.singleWorkbenchSelectCategory('more');
  elements.singleResetCompatibility.focus();
  // Capture/restore focus is also active when a breakpoint changes on reset.
  layout.columnsMobile.matches = true;
  ctx.singleWorkbenchResetCompatibility();
  assert.equal(compatibility.hidden, true);
  assert.equal(document.activeElement, elements.singleCategory_more);
  assert.equal(state.promptSeed, 0);
  assert.equal(elements.singleCompatibilityValues.children.length, 0);
  assert.equal(persisted.at(-1).settings.single.normal_pipeline, 'primary');
  assert.equal(persisted.at(-1).promptSeed, 0);
  assert.ok(notices.some(args => /恢复/.test(args[0])));
  ctx.singleWorkbenchSelectCategory('common'); ctx.singleWorkbenchSelectCategory('more');
  assert.equal(compatibility.hidden, true);
});

test('removing compatibility values does not steal focus from an unrelated tab', () => {
  const { ctx, load, state, compatibility, elements, document } = setup();
  state.promptSeed = 99; ctx.singleWorkbenchSelectCategory('more');
  assert.equal(compatibility.hidden, false);
  elements.singleCategory_common.focus();
  state.promptSeed = 0; load({});
  assert.equal(compatibility.hidden, true);
  assert.equal(document.activeElement, elements.singleCategory_common);
});

test('standalone resource refresh also updates compatibility card availability', () => {
  const { ctx, state, compatibility, values } = setup();
  ctx.singleWorkbenchSelectCategory('more');
  state.promptSeed = 125;
  ctx.singleWorkbenchResources();
  assert.equal(compatibility.hidden, false);
  assert.deepEqual(values(), ['提示词种子', '125']);
  state.promptSeed = 0;
  ctx.singleWorkbenchResources();
  assert.equal(compatibility.hidden, true);
  assert.deepEqual(values(), []);
});

test('both history entry points refresh prompt seed compatibility after applying their settings', async () => {
  for (const method of ['singleWorkbenchRestoreHistory', 'singleWorkbenchFromHistory']) {
    const { ctx, compatibility, values } = setup();
    ctx.singleWorkbenchSelectCategory('more');
    const record = { id: 7, sample_seed: 444, prompt_seed: 73, positive_prompt: 'saved prompt',
      settings: plain(ctx.singleWorkbenchDefaultSettings('anima')) };
    await ctx[method](record);
    assert.equal(compatibility.hidden, false, method);
    assert.deepEqual(values(), ['提示词种子', '73']);
    await ctx[method]({ ...record, prompt_seed: 0 });
    assert.equal(compatibility.hidden, true, `${method}: the next record clears the old seed`);
    assert.deepEqual(values(), []);
  }
});

test('profile and workbench switches render each draft compatibility without retaining the prior values', () => {
  const { ctx, state, compatibility, values, elements } = setup();
  const defaults = profile => plain(ctx.singleWorkbenchDefaultSettings(profile));
  state.drafts.single.anima = { settings: defaults('anima'), promptSeed: 81 };
  state.drafts.single.krea2 = { settings: defaults('krea2'), promptSeed: 0 };
  state.drafts.random.krea2 = { settings: { ...defaults('krea2'), workbench: 'random' }, promptSeed: 0 };
  ctx.singleWorkbenchSelectCategory('more');
  ctx.singleWorkbenchSwitch('single', 'anima', { save: false });
  assert.equal(compatibility.hidden, false); assert.deepEqual(values(), ['提示词种子', '81']);
  ctx.singleWorkbenchSwitch('single', 'krea2', { save: false });
  assert.equal(compatibility.hidden, true); assert.deepEqual(values(), []);
  ctx.singleWorkbenchSwitch('random', 'krea2', { save: false });
  assert.equal(elements.singleWorkbenchControls.hidden, true);
  ctx.singleWorkbenchSwitch('single', 'anima', { save: false });
  assert.equal(compatibility.hidden, false); assert.deepEqual(values(), ['提示词种子', '81']);
  ctx.singleWorkbenchSwitch('single', 'krea2', { save: false });
  assert.equal(compatibility.hidden, true); assert.deepEqual(values(), []);
});
