const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = name => fs.readFileSync(path.join(__dirname, '../static', name), 'utf8');
const app = source('app.js');
const plain = value => JSON.parse(JSON.stringify(value));
function fn(name) {
  const start = app.indexOf(`function ${name}(`);
  return app.slice(start, app.indexOf('\n}', start) + 2);
}
function setup() {
  const elements = {};
  const ctx = vm.createContext({
    clone: plain, document: { getElementById: id => elements[id] || null },
    profileSettingsDefaults: () => ({ model_profile: 'anima', cfg: 4, sampler_name: 'er_sde', scheduler: 'simple', upscalers: { latent: { enabled: false, upscale_method: 'bilinear', scale_by: 1.5 } } }),
    ensureSelectValue: (element, value) => { element.value = value; },
    singleWorkbenchActive: () => false, ui: { base_mode: { value: 'normal' }, steps: { value: '30' }, latent_upscale_method: { value: 'sesqui' }, latent_upscale_scale: {} },
  });
  for (const name of ['defaultUpscalers', 'normalizeUpscalers']) vm.runInContext(fn(name), ctx);
  vm.runInContext(source('workbench-parameters.js'), ctx);
  for (const [key, value] of Object.entries(ctx.randomDualDefaults())) elements[`random_dual_${key}`] = { value: String(value), type: typeof value === 'number' ? 'number' : 'select-one', tagName: typeof value === 'number' ? 'INPUT' : 'SELECT', disabled: false };
  elements.randomDualSection = { hidden: false, querySelectorAll: () => Object.values(elements).filter(e => e.type) };
  return { ctx, elements };
}
test('new random defaults match single dual and latent controls without changing server defaults', () => {
  const { ctx } = setup();
  const before = plain(ctx.profileSettingsDefaults());
  const settings = ctx.randomWorkbenchDefaultSettings('anima');
  assert.deepEqual(plain(settings.dual), { switch_step: 15, scale: 1, second_cfg: 4, second_sampler_name: 'er_sde', second_scheduler: 'simple', upscale_method: 'sesqui' });
  assert.equal(settings.upscalers.latent.upscale_method, 'sesqui');
  assert.deepEqual(plain(ctx.profileSettingsDefaults()), before);
});
test('editing an old random record retains its actual dual sampler and latent scale', () => {
  const { ctx } = setup();
  const old = { workbench: 'random', dual: null, cfg: 2.5, sampler_name: 'euler', scheduler: 'karras', upscalers: { latent: { enabled: false, scale_by: 1.75, upscale_method: 'bicubic' } } };
  const before = plain(old);
  ctx.setRandomDualControls(old);
  assert.deepEqual(plain(ctx.readRandomDualControls()), { switch_step: 15, scale: 1.75, second_cfg: 2.5, second_sampler_name: 'euler', second_scheduler: 'karras', upscale_method: 'bicubic' });
  assert.deepEqual(old, before);
});
test('dual fields hide and disable on normal mode without losing values, then restore', () => {
  const { ctx, elements } = setup();
  elements.random_dual_switch_step.value = '7';
  elements.random_dual_second_cfg.value = '5.2';
  const values = plain(ctx.readRandomDualControls());
  ctx.sharedParameterVisibility();
  assert.equal(elements.randomDualSection.hidden, true);
  assert.equal(elements.random_dual_second_cfg.disabled, true);
  ctx.ui.base_mode.value = 'dual_sampler';
  ctx.sharedParameterVisibility();
  assert.equal(elements.randomDualSection.hidden, false);
  assert.equal(elements.random_dual_second_cfg.disabled, false);
  assert.deepEqual(plain(ctx.readRandomDualControls()), values);
  assert.equal(elements.random_dual_switch_step.max, '29');
  ctx.singleWorkbenchActive = () => true;
  ctx.sharedParameterVisibility();
  assert.equal(elements.randomDualSection.hidden, true);
  assert.equal(elements.random_dual_second_cfg.disabled, true);
});
test('Sesqui constraints follow each workbench algorithm and never clamp a stored value', () => {
  const { ctx, elements } = setup();
  ctx.ui.latent_upscale_scale.value = '3';
  ctx.sharedParameterVisibility();
  assert.deepEqual([ctx.ui.latent_upscale_scale.min, ctx.ui.latent_upscale_scale.max, ctx.ui.latent_upscale_scale.value], ['1', '2', '3']);
  ctx.ui.latent_upscale_method.value = 'bicubic';
  elements.random_dual_upscale_method.value = 'bilinear';
  ctx.sharedParameterVisibility();
  assert.deepEqual([ctx.ui.latent_upscale_scale.min, ctx.ui.latent_upscale_scale.max, elements.random_dual_scale.max], ['0.01', '64', '8']);
  ctx.singleWorkbenchActive = () => true;
  elements.single_latent_upscale_method = { value: 'sesqui' };
  ctx.sharedParameterVisibility();
  assert.equal(ctx.ui.latent_upscale_scale.max, '2');
});
test('cross-workbench imports transfer effective dual and latent parameters while keeping single routes', () => {
  const { ctx } = setup();
  const single = { workbench: 'single', single: { dual: { ...ctx.randomDualDefaults(), second_cfg: 3.5 }, latent: { upscale_method: 'sesqui', model_format: 'SDXL', half_precision: false, pipeline: 'secondary' } }, upscalers: { latent: { enabled: true, scale_by: 1.8, upscale_method: 'bilinear' } } };
  const random = plain(single);
  ctx.adaptPostprocessSettings(random, single, 'random');
  assert.equal(random.upscalers.latent.upscale_method, 'sesqui');
  assert.equal(random.upscalers.latent.model_format, 'SDXL');
  assert.equal(random.upscalers.latent.half_precision, false);
  assert.equal(random.upscalers.latent.pipeline, undefined);
  assert.equal(random.dual.second_cfg, 3.5);
  random.workbench = 'random';
  const target = { single: { dual: { first_pipeline: 'primary' }, latent: { pipeline: 'primary' } } };
  ctx.adaptPostprocessSettings(target, random, 'single');
  assert.equal(target.single.latent.model_format, 'SDXL');
  assert.equal(target.single.latent.pipeline, 'primary');
  assert.equal(target.single.dual.first_pipeline, 'primary');
  assert.equal(target.single.dual.second_cfg, 3.5);
});
