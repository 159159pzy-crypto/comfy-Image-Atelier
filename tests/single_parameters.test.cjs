const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const workbenchSource = fs.readFileSync(path.join(__dirname, '../static/single-workbench.js'), 'utf8');
const compatibilitySource = fs.readFileSync(path.join(__dirname, '../static/single-compatibility.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));

function setup() {
  const removedControls = new Set(['singleClipName', 'singleVaeName', 'singleClipType', 'singlePromptSeed']);
  const elements = {
    singleNegative: { value: '' }, singleSeedMode: { value: 'fixed' }, singleSampleSeed: { value: '1234' },
    singleCompatibilityStatus: { textContent: '' },
    single_positive_prompt: { dataset: { singlePath: 'positive_prompt' }, value: '', type: 'textarea' },
    single_denoise: { dataset: { singlePath: 'denoise' }, value: '', type: 'number' },
  };
  const config = { single_defaults: {
    latent: { pipeline: 'secondary' }, face_pipeline: 'secondary',
    normal_pipeline: 'secondary', dual: { first_pipeline: 'secondary', second_pipeline: 'secondary' },
    loras_secondary: [{ filename: 'server-default.safetensors', enabled: true, strength: 1 }],
    secondary_managed_triggers: ['server trigger'], vae_decode: { tile_size: 1024 },
  } };
  const upscalers = {
    latent: { enabled: false, scale_by: 1.5, upscale_method: 'nearest-exact' },
    seedvr2: { enabled: false, weight_dtype: 'default', encode: { tile_size: 512, overlap: 64, temporal_size: 64, temporal_overlap: 8 }, decode: { tile_size: 512, overlap: 64, temporal_size: 64, temporal_overlap: 8 } },
    nvidia: { enabled: false, resize_type: 'scale by multiplier', scale: 2, quality: 'high' },
    face_refine: { enabled: false, steps: 20, cfg_scale: 4, sampler: 'euler', scheduler: 'normal', denoise_strength: .3, processing_resolution: 512, detection_confidence: .5, face_positive_prompt: '', offload_models_to_cpu: false },
  };
  let settings = {}, persisted = null, invalidations = 0;
  const ctx = vm.createContext({
    console, clearTimeout, setTimeout, config,
    document: {
      getElementById(id) {
        if (removedControls.has(id)) throw new Error(`Removed control accessed: ${id}`);
        return elements[id] || null;
      },
      querySelectorAll: selector => selector === '[data-single-path]' ? [elements.single_positive_prompt, elements.single_denoise] : [],
    },
    SECTIONS: ['character', 'clothing', 'pose', 'background', 'expression'],
    currentProfile: () => 'anima',
    profileConfig: () => ({ clip_name: 'default-clip', vae_name: 'default-vae', clip_type: 'default-type' }),
    profileSettingsDefaults: profile => ({ model_profile: profile, model_name: 'model.safetensors', count: 8, base_mode: 'dual_sampler', width: 1024, height: 1024, steps: 30, cfg: 4, loras: [], upscalers: plain(upscalers) }),
    profileDrafts: { anima: { model_name: 'draft-model' } },
    UPSCALER_CONTROL_IDS: { face_refine: Object.fromEntries(Object.keys(upscalers.face_refine).map(key => [key, `face_${key}`])) },
    singleWorkbenchCancelInput() {}, singleWorkbenchRestoreInput() {},
  });
  vm.runInContext(workbenchSource, ctx);
  vm.runInContext(compatibilitySource, ctx);
  const state = vm.runInContext('singleWorkbenchState', ctx);
  state.ready = true;
  state.mode = 'single';
  // View rendering is orthogonal to the serialization and compatibility contract.
  ctx.singleWorkbenchRenderCompatibility = () => {};
  ctx.singleWorkbenchRenderProcessed = () => {};
  ctx.singleWorkbenchRenderMode = () => {};
  ctx.singleWorkbenchPollPrompt = () => {};
  ctx.singleWorkbenchInvalidatePrompt = () => { invalidations++; state.single.prompt_job_id = ''; state.single.processed_prompt_override = null; };
  ctx.readSettings = () => ctx.singleWorkbenchRead(settings);
  ctx.applySettings = next => { settings = plain(next); ctx.singleWorkbenchApply(settings); };
  ctx.singleWorkbenchPersist = () => { persisted = plain(ctx.singleWorkbenchCapture()); };
  const defaults = ctx.singleWorkbenchDefaultSettings('anima');
  ctx.applySettings(defaults);
  return { ctx, state, elements, config, defaults, persisted: () => persisted, invalidations: () => invalidations };
}

test('new single drafts override server secondary pipeline defaults without changing the server config', () => {
  const { ctx, config } = setup();
  const before = plain(config);
  const fresh = ctx.singleWorkbenchDefaultSettings('anima');
  assert.deepEqual([fresh.single.normal_pipeline, fresh.single.dual.first_pipeline, fresh.single.dual.second_pipeline, fresh.single.latent.pipeline, fresh.single.face_pipeline], ['primary', 'primary', 'primary', 'primary', 'primary']);
  assert.deepEqual(plain(fresh.single.loras_secondary), []);
  assert.deepEqual(plain(fresh.single.secondary_managed_triggers), []);
  assert.equal(fresh.single.vae_decode.tile_size, 1024);
  assert.deepEqual(config, before);
});

test('loading a legacy single draft preserves secondary routing and low-frequency custom values through an edit and read', () => {
  const { ctx, state, elements, defaults } = setup();
  const legacy = ctx.singleWorkbenchMerge(defaults, { single: {
    positive_prompt: 'authored prompt', latent: { pipeline: 'secondary', model_format: 'SDXL' }, face_pipeline: 'secondary',
    normal_pipeline: 'secondary', dual: { first_pipeline: 'secondary', second_add_noise: false },
    loras_secondary: [{ filename: 'legacy.safetensors', enabled: true, strength: .65 }], secondary_managed_triggers: ['legacy trigger'],
    vae_encode: { tile_size: 1536, overlap: 96 }, weight_dtype: 'fp8_e4m3fn',
    prompt_processing: { expand: { seed: 987, ollama_auto_unload: false } },
  } });
  const original = plain(legacy);
  ctx.applySettings(legacy);
  elements.single_positive_prompt.value = 'edited prompt';
  elements.single_denoise.value = '.42';
  const saved = ctx.readSettings();
  assert.equal(saved.single.positive_prompt, 'edited prompt');
  assert.equal(saved.single.denoise, .42);
  assert.equal(saved.single.latent.pipeline, 'secondary');
  assert.equal(saved.single.face_pipeline, 'secondary');
  assert.equal(saved.single.normal_pipeline, 'secondary');
  assert.equal(saved.single.dual.first_pipeline, 'secondary');
  assert.equal(saved.single.dual.second_add_noise, false);
  assert.equal(saved.single.vae_encode.tile_size, 1536);
  assert.equal(saved.single.vae_encode.overlap, 96);
  assert.equal(saved.single.weight_dtype, 'fp8_e4m3fn');
  assert.equal(saved.single.prompt_processing.expand.seed, 987);
  assert.equal(saved.single.prompt_processing.expand.ollama_auto_unload, false);
  assert.deepEqual(plain(saved.single.loras_secondary), original.single.loras_secondary);
  saved.single.loras_secondary[0].strength = 9;
  assert.equal(state.single.loras_secondary[0].strength, .65);
  assert.deepEqual(plain(legacy), original);
});

test('encoder resources and prompt seed survive read and draft capture with their old DOM controls absent', () => {
  const { ctx, state, elements, defaults } = setup();
  ctx.applySettings({ ...defaults, clip_name: 'custom-clip', vae_name: 'custom-vae', clip_type: 'custom-type' });
  state.promptSeed = 8181;
  elements.singleSampleSeed.value = '456';
  const saved = ctx.singleWorkbenchRead({ clip_name: 'stale', vae_name: 'stale', clip_type: 'stale' });
  assert.deepEqual([saved.clip_name, saved.vae_name, saved.clip_type], ['custom-clip', 'custom-vae', 'custom-type']);
  assert.deepEqual(plain(ctx.singleWorkbenchSeeds()), { sample_seed: 456, prompt_seed: 8181 });
  const captured = ctx.singleWorkbenchCapture();
  assert.equal(captured.promptSeed, 8181);
  assert.equal(captured.seed, '456');
  assert.equal(captured.settings.clip_name, 'custom-clip');
  captured.settings.single.vae_decode.tile_size = 64;
  assert.equal(state.single.vae_decode.tile_size, 1024);
});

test('stored prompt seeds keep numeric validation after the prompt seed input is removed', () => {
  const { ctx, state, elements } = setup();
  state.promptSeed = 2147483647;
  assert.equal(ctx.singleWorkbenchSeeds().prompt_seed, 2147483647);
  for (const value of [-1, 2147483648, 1.5, 'invalid']) {
    state.promptSeed = value;
    assert.throws(() => ctx.singleWorkbenchSeeds(), /有效/);
  }
  elements.singleSeedMode.value = 'random';
  assert.equal(ctx.singleWorkbenchSeeds(), null);
});

test('compatibility lists hidden differences but excludes editable prompt, sampler and enhancement fields', () => {
  const { ctx, defaults } = setup();
  const customized = ctx.singleWorkbenchMerge(defaults, {
    clip_name: 'custom-clip', width: 768, cfg: 5, loras: [{ filename: 'common.safetensors', enabled: true, strength: .8 }],
    single: {
      positive_prompt: 'custom prompt', prefix: 'prefix', suffix: 'suffix', input_image_id: 'asset', denoise: .4, resize_mode: 'pad',
      dual: { second_cfg: 6, second_sampler_name: 'dpmpp_2m', first_add_noise: false },
      latent: { upscale_method: 'bilinear', pipeline: 'secondary' },
      dlss: { enabled: true, style: '自然', intensity: .4, output_mix: .5, gpu_gen: 'RTX 40' },
      krea_variance: { enabled: true, randomize_percent: 60, strength: 30, noise_distribution: 'gaussian' },
      prompt_processing: { expand: { enabled: true, rule: 'new rule', user_prompt: 'user', seed: 42 } },
    },
    upscalers: {
      seedvr2: { enabled: true, weight_dtype: 'fp16', encode: { tile_size: 1024 } },
      nvidia: { scale: 3, resize_type: 'resize to target' },
      face_refine: { enabled: true, steps: 30, cfg_scale: 6, sampler: 'dpmpp_2m', scheduler: 'simple', denoise_strength: .5, processing_resolution: 768, detection_confidence: .7 },
    },
  });
  const paths = plain(ctx.singleWorkbenchCompatibilityEntries(customized)).map(entry => entry.path).sort();
  assert.deepEqual(paths, [
    'clip_name', 'single.dual.first_add_noise', 'single.latent.pipeline', 'single.dlss.gpu_gen',
    'single.prompt_processing.expand.seed',
    'upscalers.seedvr2.weight_dtype', 'upscalers.seedvr2.encode.tile_size',
    'upscalers.nvidia.resize_type', 'upscalers.face_refine.detection_confidence',
  ].sort());
  assert.deepEqual(plain(ctx.singleWorkbenchCompatibilityEntries(defaults)), []);
});

test('resetting compatibility resets hidden differences and keeps authored prompts, reference, common LoRAs and visible parameters', () => {
  const { ctx, state, defaults, persisted, invalidations } = setup();
  const commonLoras = [{ filename: 'common.safetensors', enabled: true, strength: .7 }];
  const original = ctx.singleWorkbenchMerge(defaults, {
    generation_mode: 'img2img', clip_name: 'old-clip', loras: commonLoras, width: 768, steps: 42,
    single: {
      positive_prompt: 'keep this prompt', prefix: 'prefix', suffix: 'suffix', input_image_id: 'owned-reference', denoise: .27, resize_mode: 'pad',
      loras_secondary: [{ filename: 'old.safetensors', enabled: true, strength: 1 }], secondary_managed_triggers: ['old trigger'],
      face_pipeline: 'secondary', latent: { pipeline: 'secondary', upscale_method: 'bilinear' }, vae_decode: { tile_size: 1536 },
      prompt_processing: { expand: { enabled: true, rule: 'chosen rule', seed: 91 } },
      dual: { second_cfg: 7, first_add_noise: false },
      krea_variance: { enabled: true, randomize_percent: 72, strength: 33, noise_distribution: 'gaussian' },
    },
    upscalers: { face_refine: { sampler: 'dpmpp_2m', detection_confidence: .9 } },
  });
  ctx.applySettings(original);
  state.promptSeed = 123;
  ctx.singleWorkbenchResetCompatibility();
  const saved = ctx.readSettings();
  assert.equal(saved.single.positive_prompt, 'keep this prompt');
  assert.equal(saved.single.prefix, 'prefix');
  assert.equal(saved.single.suffix, 'suffix');
  assert.equal(saved.single.input_image_id, 'owned-reference');
  assert.equal(saved.generation_mode, 'img2img');
  assert.equal(saved.single.denoise, .27);
  assert.equal(saved.single.resize_mode, 'pad');
  assert.deepEqual(plain(saved.loras), commonLoras);
  assert.equal(saved.width, 768);
  assert.equal(saved.steps, 42);
  assert.equal(saved.single.dual.second_cfg, 7);
  assert.deepEqual(plain(saved.single.krea_variance), plain(original.single.krea_variance));
  assert.equal(saved.single.latent.upscale_method, 'bilinear');
  assert.equal(saved.single.prompt_processing.expand.rule, 'chosen rule');
  assert.equal(saved.single.prompt_processing.expand.enabled, true);
  assert.equal(saved.upscalers.face_refine.sampler, 'dpmpp_2m');
  assert.equal(saved.clip_name, defaults.clip_name);
  assert.equal(saved.single.face_pipeline, 'primary');
  assert.equal(saved.single.latent.pipeline, 'primary');
  assert.equal(saved.single.vae_decode.tile_size, defaults.single.vae_decode.tile_size);
  assert.equal(saved.single.dual.first_add_noise, defaults.single.dual.first_add_noise);
  assert.equal(saved.single.prompt_processing.expand.seed, 0);
  assert.equal(saved.upscalers.face_refine.detection_confidence, defaults.upscalers.face_refine.detection_confidence);
  assert.deepEqual(plain(saved.single.loras_secondary), []);
  assert.deepEqual(plain(saved.single.secondary_managed_triggers), []);
  assert.equal(state.promptSeed, 0);
  assert.equal(invalidations(), 1);
  assert.equal(persisted().settings.single.input_image_id, 'owned-reference');
  assert.deepEqual(persisted().settings.single.krea_variance, plain(original.single.krea_variance));
  assert.equal(persisted().promptSeed, 0);
  assert.deepEqual(plain(ctx.singleWorkbenchCompatibilityEntries(saved)), []);
});
