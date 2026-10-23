const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = name => fs.readFileSync(path.join(__dirname, '../static', name), 'utf8');
function harness() {
  const springs = [], appearances = [], elements = {};
  const document = { activeElement: null, getElementById: id => elements[id] || null };
  function element(id, values = {}) {
    const result = { id, dataset: {}, style: {}, hidden: false, inert: false,
      offsetLeft: 0, offsetWidth: 100, scrollLeft: 0, scrollTop: 0, clientWidth: 400,
      value: '', selectionStart: 2, selectionEnd: 5,
      focus(options) { document.activeElement = this; this.focusOptions = options; },
      classList: { toggle() {} }, ...values };
    elements[id] = result;
    return result;
  }
  const StudioMotion = {
    spring(owner, channel, target, options) { springs.push({ owner, channel, target, options }); options.onUpdate(options.initial || target); },
    presence(owner, visible, options) {
      appearances.push({ owner, visible, options });
      owner.dataset.motionState = visible ? 'open' : 'closed';
      owner.hidden = !visible;
      if (options.manageInert !== false) owner.inert = !visible;
    },
  };
  const ctx = vm.createContext({ document, StudioMotion, singleWorkbenchActive: () => true });
  vm.runInContext(source('single-layout.js'), ctx);
  return { ctx, element, elements, document, springs, appearances, StudioMotion };
}

test('indicator reuses its spring on reversal and ignores unchanged background renders', () => {
  const { ctx, element, springs } = harness();
  const thumb = element('thumb');
  ctx.animateStudioIndicator(thumb, { x: 0, width: 80 });
  ctx.animateStudioIndicator(thumb, { x: 240, width: 110 });
  springs.at(-1).options.onUpdate({ x: 120, width: 95 });
  assert.equal(thumb.style.transform, 'translateX(120px)');
  ctx.animateStudioIndicator(thumb, { x: 0, width: 80 });
  assert.equal(springs.at(-1).owner, thumb);
  assert.equal(springs.at(-1).channel, springs[0].channel);
  assert.equal(springs.at(-1).options.immediate, false);
  const calls = springs.length;
  ctx.animateStudioIndicator(thumb, { x: 0, width: 80 });
  assert.equal(springs.length, calls, 'rendering an unchanged tab must not reset its motion');
  ctx.animateStudioIndicator(thumb, { x: 0, width: 80 }, { immediate: true });
  assert.equal(springs.at(-1).options.immediate, true, 'resize settles to the new measured geometry');
  ctx.animateStudioIndicator(thumb, { x: 0, width: 0 });
  assert.equal(springs.length, calls + 1, 'hidden tabs cannot erase their last measured geometry');
});

test('real indicator springs animate after settling and preserve velocity when the destination reverses', () => {
  let now = 0, nextFrame = 0;
  const frames = new Map();
  const ctx = vm.createContext({
    document: { hidden: false, addEventListener() {}, documentElement: { removeAttribute() {} } },
    performance: { now: () => now }, matchMedia: () => ({ matches: false, addEventListener() {} }),
    requestAnimationFrame(callback) { frames.set(++nextFrame, callback); return nextFrame; },
    cancelAnimationFrame(id) { frames.delete(id); },
  });
  vm.runInContext(source('motion.js'), ctx);
  vm.runInContext(source('single-layout.js'), ctx);
  const thumb = { style: {}, dataset: {} };
  const step = (elapsed = 1000 / 60) => {
    now += elapsed;
    const callbacks = [...frames.values()]; frames.clear();
    for (const callback of callbacks) callback(now);
  };
  ctx.animateStudioIndicator(thumb, { x: 0, width: 100 });
  assert.equal(frames.size, 0, 'initial positioning has no entrance');
  ctx.animateStudioIndicator(thumb, { x: 200, width: 120 });
  assert.equal(ctx.StudioMotion.snapshot(thumb, 'indicator').value.x, 0);
  assert.equal(frames.size, 1, 'a selection after rest must start a spring, not snap to its target');
  step(80);
  const before = ctx.StudioMotion.snapshot(thumb, 'indicator');
  assert.ok(before.value.x > 0 && before.value.x < 200); assert.ok(before.velocity.x > 0);
  ctx.animateStudioIndicator(thumb, { x: 0, width: 100 });
  const reversed = ctx.StudioMotion.snapshot(thumb, 'indicator');
  assert.equal(reversed.value.x, before.value.x); assert.equal(reversed.velocity.x, before.velocity.x);
  let remaining = 300;
  while (frames.size && remaining--) step();
  assert.ok(remaining > 0); assert.equal(ctx.StudioMotion.snapshot(thumb, 'indicator').value.x, 0);
  ctx.animateStudioIndicator(thumb, { x: 200, width: 120 });
  assert.equal(ctx.StudioMotion.snapshot(thumb, 'indicator').value.x, 0);
  assert.equal(ctx.StudioMotion.snapshot(thumb, 'indicator').active, true, 'subsequent settled changes still animate');
  ctx.animateStudioIndicator(thumb, { x: 240, width: 120 }, { immediate: true });
  assert.equal(ctx.StudioMotion.snapshot(thumb, 'indicator').value.x, 240); assert.equal(frames.size, 0);
});

test('loading both workbench scripts keeps parameter and workbench indicators independent', () => {
  const h = harness();
  vm.runInContext(source('single-workbench.js'), h.ctx);
  Object.assign(vm.runInContext('singleWorkbenchState', h.ctx), { ready: true, mode: 'single' });
  vm.runInContext('singleLayoutState.category = "repair"', h.ctx);
  const parameterThumb = h.element('singleTabIndicator');
  const workbenchThumb = h.element('workbenchThumb', { offsetLeft: 4 });
  h.document.querySelector = selector => {
    if (selector === '#singleParameterTabs [data-single-category="repair"]') return { offsetLeft: 230, offsetWidth: 96 };
    if (selector === '#workbenchSwitch .workbench-switch-thumb') return workbenchThumb;
    if (selector === '#workbenchSwitch [data-workbench="single"]') return { offsetLeft: 4, offsetWidth: 138 };
    return null;
  };
  h.ctx.singleParameterPositionIndicator();
  h.ctx.singleWorkbenchPositionIndicator();
  assert.equal(h.springs[0].owner, parameterThumb); assert.equal(h.springs[0].target.x, 230);
  assert.equal(h.springs[1].owner, workbenchThumb); assert.equal(h.springs[1].target.x, 0);
});

function categoryHarness() {
  const h = harness();
  const tabs = h.element('singleParameterTabs');
  const categories = ['common', 'prompt', 'repair', 'more'];
  const buttons = categories.map((category, index) => h.element(`tab-${category}`, {
    dataset: { singleCategory: category }, offsetLeft: index * 100, parentElement: tabs,
  }));
  const panels = categories.map(category => h.element(`panel-${category}`, {
    dataset: { singleGroup: category }, hidden: category !== 'common', value: `${category} draft`, scrollTop: 36,
  }));
  h.document.querySelector = selector => buttons.find(button => selector.includes(`"${button.dataset.singleCategory}"`));
  h.document.querySelectorAll = () => panels;
  h.ctx.singleWorkbenchLayout = () => {
    const category = vm.runInContext('singleLayoutState.category', h.ctx);
    for (const panel of panels) panel.hidden = panel.dataset.singleGroup !== category;
  };
  return { ...h, tabs, buttons, panels };
}

test('rapid user category switches dismiss old motion and preserve controls, drafts and scroll', () => {
  const { ctx, panels, buttons, appearances, document } = categoryHarness();
  ctx.singleWorkbenchSelectCategory('prompt', { focus: true, animate: true });
  ctx.singleWorkbenchSelectCategory('repair', { focus: true, animate: true });
  ctx.singleWorkbenchSelectCategory('prompt', { focus: true, animate: true });
  assert.equal(document.activeElement, buttons[1]);
  assert.equal(buttons[1].focusOptions.preventScroll, true);
  assert.equal(panels[1].hidden, false); assert.equal(panels[1].inert, false);
  assert.equal(panels[2].hidden, true); assert.equal(panels[2].inert, false, 'hidden isolates tabs without leaking inert onto shared controls');
  assert.equal(panels[1].value, 'prompt draft'); assert.equal(panels[1].selectionStart, 2);
  assert.equal(panels[1].scrollTop, 36);
  assert.ok(appearances.some(call => call.owner === panels[2] && !call.visible && call.options.immediate));
  const calls = appearances.length;
  ctx.singleWorkbenchSelectCategory('prompt', { animate: true });
  assert.equal(appearances.length, calls, 'clicking the selected category does not replay an entrance');
});

test('programmatic category reveal restores an inert panel without starting an entrance', () => {
  const { ctx, panels, appearances } = categoryHarness();
  ctx.singleWorkbenchSelectCategory('prompt', { animate: true });
  ctx.singleWorkbenchSelectCategory('repair', { animate: true });
  ctx.singleWorkbenchSelectCategory('prompt');
  assert.equal(panels[1].hidden, false); assert.equal(panels[1].inert, false);
  assert.equal(appearances.at(-1).options.immediate, true);
});

test('entering fixed-open single mode settles a pending collapse and restores its requested state on return', () => {
  const h = harness(), calls = [];
  let active = true;
  const classes = new Set();
  const details = h.element('shared-details', { open: true, dataset: { motionTargetOpen: 'false' },
    classList: { add: name => classes.add(name), remove: name => classes.delete(name) }, removeAttribute() {} });
  const summary = { getAttribute: () => null, setAttribute() {}, removeAttribute() {} };
  h.StudioMotion.disclosure = (node, open, options) => { calls.push({ node, open, options }); node.open = open; node.dataset.motionTargetOpen = String(open); node.dataset.motionState = open ? 'open' : 'closed'; };
  h.ctx.singleWorkbenchActive = () => active;
  h.ctx.singleWorkbenchRenderCompatibility = () => false;
  h.ctx.singleWorkbenchCaptureLayoutFocus = () => null;
  h.ctx.singleWorkbenchRestoreLayoutFocus = () => {};
  h.ctx.singleWorkbenchCompactFields = () => {};
  h.ctx.singleWorkbenchPlaceCategoryCards = () => {};
  h.ctx.singleParameterPositionIndicator = () => {};
  h.document.querySelectorAll = () => [];
  h.document.querySelector = () => ({ textContent: '' });
  h.element('settingsForm'); h.element('randomArchive');
  const state = vm.runInContext('singleLayoutState', h.ctx);
  Object.assign(state, { initialized: true, active: false, singleDisabled: new Map(), singleFields: [],
    categoryPlacement: 'columns', columnsMobile: { matches: false }, mobile: { matches: true }, shared: [{ node: details, summary }] });
  h.ctx.singleWorkbenchLayout();
  assert.equal(details.open, true); assert.equal(classes.has('single-fixed-open'), true);
  assert.equal(calls[0].options.immediate, true);
  assert.equal(state.shared[0].open, false, 'save the requested closed state, not the still-visible closing DOM');
  const layout = h.ctx.singleWorkbenchLayout;
  h.ctx.singleWorkbenchLayout = () => { details.hidden = true; };
  h.document.querySelectorAll = () => [details];
  h.document.querySelector = () => null;
  h.ctx.singleWorkbenchSelectCategory('repair', { animate: true });
  assert.equal(details.inert, false, 'switching category cannot leave shared random-workbench controls inert');
  h.ctx.singleWorkbenchLayout = layout;
  h.document.querySelectorAll = () => [];
  h.document.querySelector = () => ({ textContent: '' });
  active = false;
  h.ctx.singleWorkbenchLayout();
  assert.equal(details.open, false); assert.equal(classes.has('single-fixed-open'), false);
  assert.equal(details.hidden, false); assert.equal(details.inert, false);
  assert.equal(h.appearances.at(-1).options.immediate, true, 'returning settles any old presence before restoring the shared card');
  assert.equal(calls.at(-1).options.immediate, true);
});

test('invalid-field reveal replaces a pending disclosure close instead of setting open behind its callback', () => {
  const h = harness(), calls = [];
  h.ctx.Element = Object;
  const form = { id: 'settingsForm' };
  const details = { id: 'advanced', tagName: 'DETAILS', open: true, parentElement: form };
  const field = { parentElement: details, closest: () => null };
  h.StudioMotion.disclosure = (node, open, options) => calls.push({ node, open, options });
  h.document.querySelector = () => null;
  h.ctx.singleWorkbenchRevealField(field);
  assert.equal(calls.length, 1); assert.equal(calls[0].node, details);
  assert.equal(calls[0].open, true); assert.equal(calls[0].options.immediate, true);
});

test('historical groups delegate exclusivity to the motion runtime without racing native toggles', () => {
  const calls = [], container = { querySelectorAll() { throw new Error('native toggle listeners must not compete with motion'); } };
  const ctx = vm.createContext({ StudioMotion: { bindDisclosures: root => calls.push(root) } });
  vm.runInContext(source('single-details.js'), ctx);
  ctx.bindSingleDetailGroups(container);
  assert.deepEqual(calls, [container]);
});

test('workbench switch waits for draft restoration before retargeting, and saved-mode restores settle immediately', () => {
  const h = harness();
  h.document.body = { dataset: {} };
  const thumb = h.element('workbench-thumb', { offsetLeft: 4 });
  const workspace = h.element('workspace');
  const buttons = ['single', 'random'].map((mode, index) => h.element(`${mode}-tab`, {
    tagName: 'BUTTON', dataset: { workbench: mode }, offsetLeft: 4 + index * 140, offsetWidth: 140, setAttribute() {},
  }));
  h.document.querySelector = selector => selector === '#workbenchSwitch .workbench-switch-thumb' ? thumb
    : selector === '.workspace' ? workspace : selector.startsWith('#workbenchSwitch [data-workbench=')
      ? buttons.find(button => selector.includes(`"${button.dataset.workbench}"`)) : { textContent: '' };
  h.document.querySelectorAll = selector => selector === '[data-workbench]' ? buttons : [];
  for (const id of ['singleWorkbenchControls', 'singlePreview', 'singleSeedMode', 'singleSampleSeed']) h.element(id);
  Object.assign(h.ctx, { currentProfile: () => 'anima', clearTimeout() {}, profileDrafts: {},
    ui: { count: {}, base_mode: {}, latent_upscale_method: { closest: () => ({ hidden: false }) } },
    updateGenerationAvailability() {} });
  vm.runInContext(source('single-workbench.js'), h.ctx);
  for (const name of ['singleWorkbenchPersist', 'singleWorkbenchCancelInput', 'singleWorkbenchRenderInput',
    'singleWorkbenchRefreshLimits', 'singleWorkbenchLayout', 'singleWorkbenchLoadFavorites']) h.ctx[name] = () => {};
  const state = vm.runInContext('singleWorkbenchState', h.ctx);
  state.ready = true;
  h.ctx.singleWorkbenchRenderMode();
  assert.equal(h.springs[0].options.immediate, true);
  const intermediate = [];
  h.ctx.applySettings = () => { h.ctx.singleWorkbenchRenderMode(); intermediate.push(h.springs.length); };
  h.ctx.singleWorkbenchSwitch('single', 'anima', { settings: {} });
  assert.equal(intermediate[0], 1, 'nested restoring render cannot move the indicator to the destination early');
  assert.equal(h.springs.length, 2); assert.equal(h.springs.at(-1).options.immediate, false);
  assert.equal(h.appearances.length, 1); assert.equal(h.appearances[0].owner, workspace);
  h.ctx.singleWorkbenchRenderMode();
  assert.equal(h.springs.length, 2, 'background render keeps the active spring untouched');
  h.ctx.singleWorkbenchSwitch('random', 'anima', { save: false, settings: {} });
  assert.equal(h.springs.at(-1).options.immediate, true);
  assert.equal(h.appearances.length, 1, 'saved mode restoration does not replay the content entrance');
});

test('desk tabs keep saved text and focus while reversing the selected surface', () => {
  const h = harness();
  const rewrite = h.element('promptRewritePanel', { value: 'unfinished rewrite', scrollTop: 27 });
  const reverse = h.element('promptReversePanel', { hidden: true, value: 'recognized text' });
  const thumb = h.element('desk-thumb', { offsetLeft: 3, offsetWidth: 78 });
  h.element('promptDeskTabs', { querySelector: () => thumb });
  h.element('promptDeskRewrite', { offsetLeft: 3, offsetWidth: 90 });
  const reverseTab = h.element('promptDeskReverse', { offsetLeft: 93, offsetWidth: 90 });
  h.element('reverseInputDropZone');
  vm.runInContext(source('prompt-reverse.js'), h.ctx);
  h.ctx.promptReversePersist = () => {};
  h.ctx.promptReverseRender = () => {
    const mode = vm.runInContext('promptReverseState.mode', h.ctx);
    rewrite.hidden = mode === 'reverse'; reverse.hidden = mode !== 'reverse';
    h.ctx.promptDeskPositionIndicator();
  };
  h.ctx.promptReverseSetMode('reverse', true);
  assert.equal(h.document.activeElement, reverseTab);
  assert.equal(reverseTab.focusOptions.preventScroll, true);
  h.ctx.promptReverseSetMode('rewrite');
  assert.equal(rewrite.value, 'unfinished rewrite'); assert.equal(rewrite.scrollTop, 27);
  assert.equal(rewrite.hidden, false); assert.equal(rewrite.inert, false);
  assert.equal(reverse.hidden, true); assert.equal(reverse.inert, true);
  const calls = h.appearances.length;
  h.ctx.promptReverseSetMode('rewrite');
  assert.equal(h.appearances.length, calls);
});
