const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const app = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
function load(ctx, name) {
  const start = app.indexOf(`function ${name}(`);
  assert.ok(start >= 0, name);
  vm.runInContext(app.slice(start, app.indexOf('\n}', start) + 2), ctx);
}
function context() {
  const ctx = vm.createContext({ clone: plain, ui: {}, pipelineContractState: { version: 5, source: {} }, currentPipelineVersion: () => 5, currentProfile: () => 'krea2', applyNodeResourceControls() {}, ensureSelectValue: (node, value) => { node.value = value; } });
  for (const name of ['defaultUpscalers', 'normalizeUpscalers']) load(ctx, name);
  return ctx;
}
test('legacy controls retain nested enabled even when latent is disabled and bind DLSS to VSR', () => {
  const ctx = context();
  for (const latent of [false, true]) for (const second of [false, true]) for (const nvidia of [false, true]) {
    const old = { latent: { enabled: latent, second_sampler: { enabled: second, steps: 23, cfg: 2.4, denoise: .42 } }, nvidia: { enabled: nvidia, dlss: { style: '自然', output_mix: .4 } } };
    const before = plain(old), result = ctx.normalizeUpscalers(old, null, 'anima', 2);
    assert.equal(result.post_sampler.enabled, second);
    assert.equal(result.post_sampler.steps, 23); assert.equal(result.post_sampler.denoise, .42);
    assert.equal(result.dlss.enabled, nvidia); assert.equal(result.dlss.output_mix, .4);
    assert.equal(result.pixel_hires.enabled, false);
    assert.deepEqual(old, before);
  }
});
test('v3 post sampling and DLSS remain independent of latent and VSR flags', () => {
  const ctx = context();
  const value = ctx.normalizeUpscalers({ latent: { enabled: false }, nvidia: { enabled: false }, post_sampler: { enabled: true, steps: 7 }, dlss: { enabled: true } }, null, 'krea2', 3);
  assert.equal(value.post_sampler.enabled, true); assert.equal(value.post_sampler.steps, 7);
  assert.equal(value.dlss.enabled, true); assert.equal(value.nvidia.enabled, false);
  const off = ctx.normalizeUpscalers({ latent: { enabled: true, second_sampler: { enabled: true } }, nvidia: { enabled: true }, post_sampler: { enabled: false }, dlss: { enabled: false } }, null, 'anima', 3);
  assert.equal(off.post_sampler.enabled, false); assert.equal(off.dlss.enabled, false);
});
test('partial legacy sampler settings retain their actual defaults during migration', () => {
  const ctx = context(), result = ctx.normalizeUpscalers({ latent: { enabled: true, second_sampler: { enabled: false } } }, null, 'anima', 2);
  assert.equal(result.post_sampler.enabled, false); assert.equal(result.post_sampler.steps, 20);
  assert.equal(result.post_sampler.cfg, 8); assert.equal(result.post_sampler.denoise, 1);
});
test('Anima and Krea defaults have separate pixel and post sampling values and independent objects', () => {
  const ctx = context(), anima = ctx.defaultUpscalers('anima'), krea = ctx.defaultUpscalers('krea2');
  assert.deepEqual([anima.post_sampler.steps, anima.post_sampler.cfg, anima.post_sampler.denoise], [16, 4, .2]);
  assert.deepEqual([krea.post_sampler.steps, krea.post_sampler.cfg, krea.post_sampler.denoise], [10, 1, .3]);
  assert.equal(anima.pixel_hires.model_name, '4x_foolhardy_Remacri.pth');
  assert.equal(krea.pixel_hires.model_name, '4x-AnimeSharp.pth');
  anima.pixel_hires.percent = 25;
  assert.equal(krea.pixel_hires.percent, 50);
  assert.deepEqual(plain(anima.latent.sampler), { steps: 16, cfg: 4, denoise: .2, sampler_name: 'er_sde', scheduler: 'simple' });
  assert.deepEqual(plain(krea.latent.sampler), { steps: 10, cfg: 1, denoise: .3, sampler_name: 'er_sde', scheduler: 'simple' });
  anima.latent.sampler.steps = 31;
  assert.equal(anima.post_sampler.steps, 16); assert.equal(krea.latent.sampler.steps, 10);
});
test('controls round trip canonical settings including disabled shrink and independent post sampler', () => {
  const ctx = context();
  vm.runInContext(app.slice(app.indexOf('const UPSCALER_CONTROL_FIELDS'), app.indexOf('\nfunction refreshProfileData')), ctx);
  vm.runInContext('const NVIDIA_DLSS_KEYS = ["style", "intensity", "local_tone", "local_struct", "output_mix"]; const sharedParameterState = {latent:{}, dlss:{}};', ctx);
  const ids = vm.runInContext('[...Object.values(UPSCALER_CONTROL_IDS).flatMap(Object.values), ...Object.values(POST_SAMPLER_IDS), ...Object.values(LATENT_SAMPLER_IDS), "latent_second_sampler_enabled", ...Object.values(SEED_SAMPLER_IDS), ...Object.values(SEED_TILE_IDS.encode), ...Object.values(SEED_TILE_IDS.decode), ...NVIDIA_DLSS_KEYS.map(key => `nvidia_dlss_${key}`)]', ctx);
  for (const id of ids) ctx.ui[id] = { type: id.endsWith('_enabled') ? 'checkbox' : /steps|cfg|denoise|percent|scale|size|overlap|tone|struct|mix|intensity/.test(id) ? 'number' : 'text', tagName: 'INPUT', value: '', checked: false };
  Object.assign(ctx.ui.latent_upscale_method, { type: 'select-one', tagName: 'SELECT' });
  const settings = ctx.normalizeUpscalers({ pixel_hires: { enabled: true, percent: 37 }, latent: { enabled: false, upscale_method: 'bilinear', sampler: { steps: 29, denoise: .41 } }, post_sampler: { enabled: true, denoise: .33 }, dlss: { enabled: true, output_mix: .6 } }, null, 'krea2');
  ctx.setUpscalerControls(settings);
  ctx.pipelineContractState.source = { upscalers: settings };
  const result = ctx.readUpscalerControls();
  assert.equal(result.pixel_hires.percent, 37); assert.equal(result.post_sampler.enabled, true);
  assert.equal(result.post_sampler.denoise, .33); assert.equal(result.dlss.enabled, true); assert.equal(result.dlss.output_mix, .6);
  assert.equal(result.latent.enabled, false); assert.equal(result.latent.upscale_method, 'bilinear');
  assert.deepEqual(plain(result.latent.sampler), { steps: 29, cfg: 1, denoise: .41, sampler_name: 'er_sde', scheduler: 'simple' });
  assert.deepEqual(plain(result.latent.second_sampler), plain(settings.latent.second_sampler)); assert.deepEqual(plain(result.nvidia.dlss), plain(settings.nvidia.dlss));
});
test('v3 display normalization preserves dormant sampling values without upgrading its contract', () => {
  const ctx = context();
  for (const profile of ['anima', 'krea2']) for (const enabled of [false, true]) {
    const old = { latent: { enabled, upscale_method: 'bilinear', scale_by: 3.5, second_sampler: { enabled: true, steps: 99, cfg: 22, denoise: 1 } },
      post_sampler: { enabled: false, steps: 27, cfg: 2.8, denoise: .37 }, nvidia: { enabled: true }, dlss: { enabled: false } };
    const before = plain(old), result = ctx.normalizeUpscalers(old, { latent: { sampler: { steps: 88 } } }, profile, 3);
    assert.equal(result.latent.enabled, enabled); assert.equal(result.latent.upscale_method, 'bilinear'); assert.equal(result.latent.scale_by, 3.5);
    assert.deepEqual(plain(result.latent.sampler), { ...plain(ctx.defaultUpscalers(profile).latent.sampler), steps: 88 });
    assert.equal(result.post_sampler.enabled, false); assert.equal(result.post_sampler.steps, 27); assert.equal(result.post_sampler.denoise, .37);
    assert.equal(result.nvidia.enabled, true); assert.equal(result.dlss.enabled, false); assert.deepEqual(old, before);
  }
});
test('cross-workbench v4 preset imports preserve independently edited latent and post samplers', () => {
  const ctx = context();
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../static/workbench-parameters.js'), 'utf8'), ctx);
  for (const version of [3, 4]) {
    const source = { pipeline_version: version, model_profile: 'krea2', workbench: 'single', single: { latent: { upscale_method: 'bilinear', model_format: 'Wan 2.1' } },
      upscalers: { latent: { enabled: true, scale_by: 1.7, sampler: { steps: 34, cfg: 2.2, denoise: .44, sampler_name: 'heun', scheduler: 'karras' } }, post_sampler: { enabled: true, steps: 21, cfg: 3.1, denoise: .25 } } };
    const destination = plain(source); ctx.adaptPostprocessSettings(destination, source, 'random');
    assert.equal(destination.pipeline_version, version); assert.equal(destination.upscalers.latent.upscale_method, 'bilinear');
    assert.equal(destination.upscalers.latent.sampler.steps, 34);
    assert.equal(destination.upscalers.post_sampler.steps, 21); assert.equal(destination.upscalers.post_sampler.enabled, true);
  }
});
test('legacy history retains its nested contract when merged into another workbench', () => {
  const ctx = context();
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../static/workbench-parameters.js'), 'utf8'), ctx);
  const old = { model_profile: 'anima', workbench: 'random', upscalers: { latent: { enabled: true, second_sampler: { enabled: true, steps: 27 } }, nvidia: { enabled: true, dlss: { output_mix: .45 } } } };
  const destination = { upscalers: { ...ctx.defaultUpscalers(), ...old.upscalers }, single: { dual: {}, latent: {} } };
  ctx.adaptPostprocessSettings(destination, old, 'single');
  assert.equal(destination.pipeline_version, 2); assert.equal(destination.upscalers.latent.second_sampler.enabled, true);
  assert.equal(destination.upscalers.latent.second_sampler.steps, 27); assert.equal(destination.upscalers.nvidia.enabled, true);
  assert.equal(destination.upscalers.nvidia.dlss.output_mix, .45);
  assert.equal(destination.upscalers.latent.sampler, undefined);
});
test('capability checks distinguish VSR and DLSS and require pixel models only when enabled', () => {
  const ctx = context();
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../static/workbench-parameters.js'), 'utf8'), ctx);
  ctx.resourcesLoaded = true;
  ctx.resources = { node_types: ['DLSS5NeuralRender', 'KSampler'], samplers: ['er_sde'], schedulers: ['simple'], upscale_models: [] };
  assert.deepEqual(plain(ctx.sharedParameterResourceIssues({ pipeline_version: 5, upscalers: { dlss: { enabled: true } } })), []);
  assert.match(ctx.sharedParameterResourceIssues({ pipeline_version: 5, upscalers: { nvidia: { enabled: true } } }).join(), /RTXVideoSuperResolution/);
  assert.match(ctx.sharedParameterResourceIssues({ pipeline_version: 5, upscalers: { pixel_hires: { enabled: true, model_name: 'missing.pth' } } }).join(), /missing.pth/);
});
test('inactive v4 profile drafts retain their source version before workbench migration', () => {
  const ctx = context();
  ctx.SECTIONS = []; ctx.singleWorkbenchClone = plain;
  const source = fs.readFileSync(path.join(__dirname, '../static/single-workbench.js'), 'utf8');
  const start = source.indexOf('function singleWorkbenchLegacyDrafts(');
  vm.runInContext(source.slice(start, source.indexOf('\n}', start) + 2), ctx);
  const active = { pipeline_version: 3, model_profile: 'anima', upscalers: ctx.defaultUpscalers('anima') };
  const inactive = { krea2: { upscalers: { latent: { enabled: true, second_sampler: { enabled: true, steps: 19 } }, nvidia: { enabled: true } } } };
  const restored = ctx.singleWorkbenchLegacyDrafts(active, inactive).krea2.settings;
  assert.equal(restored.pipeline_version, 2);
  const upgraded = ctx.normalizeUpscalers(restored.upscalers, null, 'krea2', restored.pipeline_version);
  assert.equal(upgraded.post_sampler.enabled, true); assert.equal(upgraded.post_sampler.steps, 19);
  assert.equal(upgraded.dlss.enabled, true);
});
test('shared markup has visible independent toggles in the fixed pipeline order', () => {
  const html = fs.readFileSync(path.join(__dirname, '../static/index.html'), 'utf8');
  const order = [...html.matchAll(/class="sampling-stage sampling-stage-(pixel_hires|nvidia|seedvr2|latent|post_sampler|face|dlss)"/g)].map(match => match[1]);
  assert.deepEqual(order, ['pixel_hires', 'latent', 'post_sampler', 'face', 'seedvr2', 'nvidia', 'dlss']);
  assert.match(html, /class="switch-row"><span><strong>整图后采样/);
  assert.match(html, /id="pixelHiresResizeHint"/);
});
