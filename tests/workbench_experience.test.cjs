const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function setup() {
  const elements = {};
  const field = (id, value = '') => {
    const title = {textContent: ''};
    const label = {hidden: false, style: {}, querySelector: () => title};
    return elements[id] = {value, disabled: false, title, label, closest: () => label};
  };
  for (const id of ['steps', 'cfg', 'sampler_name', 'scheduler', 'base_denoise', 'single_denoise',
    'random_dual_switch_step', 'random_dual_scale', 'random_dual_upscale_method', 'random_dual_model_format',
    'latent_upscale_scale', 'latent_upscale_method', 'singleSeedMode', 'singleSampleSeed']) field(id);
  Object.assign(elements, {randomDualSection: {querySelectorAll: () => []},
    dualSamplingHint: {}, sampleSeedHint: {}, baseSamplingTitle: {}});
  elements.steps.value = '30'; elements.random_dual_switch_step.value = '15';
  elements.random_dual_upscale_method.value = elements.latent_upscale_method.value = 'sesqui';
  elements.random_dual_model_format.value = 'Wan 2.1';
  elements.base_denoise.value = '.2'; elements.single_denoise.value = '.65';
  const context = vm.createContext({active: true, ui: {...elements, base_mode: {value: 'normal'}},
    singleWorkbenchState: {generationMode: 'txt2img'}, document: {getElementById: id => elements[id] || null}});
  context.singleWorkbenchActive = () => context.active;
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../static/workbench-parameters.js'), 'utf8'), context);
  return {context, elements, render: () => context.sharedParameterVisibility()};
}

test('only the effective denoise control is enabled, and mode changes preserve both values', () => {
  const {context, elements, render} = setup();
  for (const [active, mode, base, hidden, inputDisabled] of [
    [true, 'txt2img', 'normal', false, true],
    [true, 'img2img', 'normal', true, false],
    [false, 'img2img', 'normal', false, true],
    [true, 'img2img', 'normal', true, false],
    [true, 'txt2img', 'dual_sampler', true, true],
    [true, 'txt2img', 'normal', false, true],
  ]) {
    context.active = active; context.singleWorkbenchState.generationMode = mode;
    context.ui.base_mode.value = base; render();
    assert.equal(elements.base_denoise.label.hidden, hidden);
    assert.equal(elements.base_denoise.disabled, hidden);
    assert.equal(elements.single_denoise.disabled, inputDisabled);
    assert.deepEqual([elements.base_denoise.value, elements.single_denoise.value], ['.2', '.65']);
    assert.equal(elements.sampleSeedHint.hidden, active);
  }
});

test('dual sampling explains full first pass and remaining second pass, rejecting invalid ranges', () => {
  const {context, elements, render} = setup();
  context.ui.base_mode.value = 'dual_sampler'; render();
  assert.equal(elements.steps.title.textContent, '第一轮步数');
  assert.match(elements.dualSamplingHint.textContent, /30＋15＝45/);
  elements.steps.value = '12'; elements.random_dual_switch_step.value = '6'; render();
  assert.match(elements.dualSamplingHint.textContent, /12＋6＝18/);
  for (const start of ['12', '13', '-1', '', '1.5']) {
    elements.random_dual_switch_step.value = start; render();
    assert.match(elements.dualSamplingHint.textContent, /请填写有效步数/);
  }
  context.ui.base_mode.value = 'normal'; render();
  assert.equal(elements.dualSamplingHint.hidden, true);
  assert.equal(elements.steps.title.textContent, '步数');
});

test('legacy resize algorithms are not presented as latent formats', () => {
  const {elements, render} = setup(); render();
  assert.equal(elements.random_dual_model_format.title.textContent, '潜空间格式');
  elements.random_dual_model_format.value = 'legacy:bicubic'; render();
  assert.equal(elements.random_dual_model_format.title.textContent, '旧放大算法');
});
