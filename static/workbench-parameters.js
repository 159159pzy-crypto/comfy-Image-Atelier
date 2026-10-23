/* Shared parameter behavior; stored workbench and sampling-mode drafts remain independent. */
const RANDOM_DUAL_KEYS = ["switch_step", "scale", "second_cfg", "second_sampler_name", "second_scheduler", "upscale_method", "model_format", "half_precision"];
const NVIDIA_DLSS_KEYS = ["style", "intensity", "local_tone", "local_struct", "output_mix"];
const sharedParameterState = { latent: { model_format: "Wan 2.1", half_precision: true }, dual: {}, krea_variance: {}, dlss: {}, modes: {}, activeMode: null, applying: false };
function sharedKreaDefaults() {
  return { enabled: false, randomize_percent: 50, auto_strength_factor: 1, strength: 20, noise_insert: "noise on beginning steps", steps_switchover_percent: 25, mask_starts_at: "beginning", mask_percent: 0, noise_distribution: "uniform", granularity: "values" };
}
function sharedKreaFromSettings(settings = {}) {
  const saved = settings.krea_variance || (settings.workbench === "single" ? settings.single?.krea_variance : null);
  return { ...sharedKreaDefaults(), ...(saved || { enabled: settings.workbench !== "single" && settings.model_profile === "krea2" }) };
}
function randomDualDefaults() {
  return { switch_step: 15, scale: 1, second_cfg: 8, second_sampler_name: "euler", second_scheduler: "simple", upscale_method: "sesqui", model_format: "Wan 2.1", half_precision: true };
}
function sharedSamplingDefaults(mode = "normal") {
  return { steps: mode === "dual_sampler" ? 30 : 20, cfg: 8, sampler_name: "euler", scheduler: "simple", base_denoise: 1, krea_variance: sharedKreaDefaults(), dual: randomDualDefaults(), seedMode: "fixed", seed: "0" };
}
function randomDualFromSettings(settings = {}) {
  const explicit = settings.workbench === "single" ? settings.single?.dual : settings.dual;
  if (explicit && typeof explicit === "object") return Object.fromEntries(RANDOM_DUAL_KEYS.map(key => [key, explicit[key] ?? randomDualDefaults()[key]]));
  return { ...randomDualDefaults(), switch_step: 15, scale: settings.upscalers?.latent?.scale_by ?? 1.5,
    second_cfg: settings.cfg ?? 4, second_sampler_name: settings.sampler_name || "er_sde",
    second_scheduler: settings.scheduler || "simple", upscale_method: settings.upscalers?.latent?.upscale_method || "bilinear" };
}
function randomWorkbenchDefaultSettings(profile = "anima") {
  const settings = JSON.parse(JSON.stringify(profileSettingsDefaults(profile)));
  const upscalers = normalizeUpscalers(settings.upscalers);
  Object.assign(upscalers.latent, { upscale_method: "sesqui", model_format: "Wan 2.1", half_precision: true, scale_by: 1.5 });
  Object.assign(upscalers.latent.second_sampler, { steps: 20, cfg: 8, sampler_name: "euler", scheduler: "simple", denoise: 1 });
  Object.assign(upscalers.face_refine, { steps: 8, cfg_scale: 1, sampler: "res_2m_ode", scheduler: "beta57", denoise_strength: .3 });
  const mode = settings.base_mode === "dual_sampler" ? "dual_sampler" : "normal";
  const sampling = sharedSamplingDefaults(mode);
  delete sampling.seedMode; delete sampling.seed;
  return { ...settings, ...sampling, base_mode: mode, model_profile: profile, workbench: "random", upscalers };
}
function sharedLatentFormat(value, fallback = "Wan 2.1") {
  return String(value || "").startsWith("legacy:") ? fallback : value || fallback;
}
function sharedLatentMethodChoice(element, method, format) {
  if (!element) return;
  for (const option of [...(element.options || [])]) if (option.value.startsWith("legacy:")) option.remove();
  const value = method && method !== "sesqui" ? `legacy:${method}` : format || "Wan 2.1";
  ensureSelectValue(element, value);
  if (value.startsWith("legacy:")) {
    const option = [...(element.options || [])].find(item => item.value === value);
    if (option) { option.textContent = `旧算法：${method}`; if (option.dataset) delete option.dataset.missing; }
  }
}
function setRandomDualControls(settings) {
  const values = randomDualFromSettings(settings);
  sharedParameterState.dual = { ...values };
  for (const key of RANDOM_DUAL_KEYS) {
    const element = document.getElementById(`random_dual_${key}`);
    if (!element) continue;
    if (element.type === "checkbox") element.checked = Boolean(values[key]);
    else if (element.tagName === "SELECT") ensureSelectValue(element, String(values[key]));
    else element.value = values[key];
  }
  sharedLatentMethodChoice(document.getElementById("random_dual_model_format"), values.upscale_method, values.model_format);
}
function readRandomDualControls() {
  return Object.fromEntries(RANDOM_DUAL_KEYS.map(key => {
    const element = document.getElementById(`random_dual_${key}`);
    if (key === "model_format") return [key, sharedLatentFormat(element?.value, sharedParameterState.dual.model_format || "Wan 2.1")];
    return [key, element ? (element.type === "checkbox" ? element.checked : element.type === "number" ? Number(element.value) : element.value) : sharedParameterState.dual[key] ?? randomDualDefaults()[key]];
  }));
}
function sharedParametersApply(settings, raw = settings) {
  sharedParameterState.applying = true;
  sharedParameterState.krea_variance = sharedKreaFromSettings({ ...raw, model_profile: settings.model_profile });
  if (ui.krea_variance_enabled) ui.krea_variance_enabled.checked = Boolean(sharedParameterState.krea_variance.enabled);
  if (ui.base_denoise) ui.base_denoise.value = settings.base_denoise ?? 1;
  const latent = raw?.workbench === "single" ? { ...settings.upscalers?.latent, ...raw.single?.latent } : settings.upscalers?.latent;
  if (latent) {
    sharedParameterState.latent = { model_format: latent.model_format || "Wan 2.1", half_precision: latent.half_precision ?? true };
    ensureSelectValue(ui.latent_upscale_method, latent.upscale_method || "sesqui");
    sharedLatentMethodChoice(ui.latent_model_format, latent.upscale_method, sharedParameterState.latent.model_format);
  }
  sharedParameterState.dlss = { ...defaultUpscalers().nvidia.dlss, ...(raw?.single?.dlss || {}), ...(raw?.upscalers?.nvidia?.dlss || {}) };
  delete sharedParameterState.dlss.enabled;
  for (const key of NVIDIA_DLSS_KEYS) setControl(`nvidia_dlss_${key}`, sharedParameterState.dlss[key]);
  sharedParameterState.activeMode = settings.base_mode || "normal";
  sharedParameterState.modes = {};
  sharedParameterState.applying = false;
}
function sharedSamplingCapture() {
  return { steps: Number(ui.steps.value), cfg: Number(ui.cfg.value), sampler_name: ui.sampler_name.value, scheduler: ui.scheduler.value,
    base_denoise: Number(ui.base_denoise?.value ?? 1), krea_variance: { ...sharedParameterState.krea_variance, enabled: Boolean(ui.krea_variance_enabled?.checked) },
    dual: readRandomDualControls(), seedMode: document.getElementById("singleSeedMode")?.value || "fixed", seed: document.getElementById("singleSampleSeed")?.value || "0" };
}
function sharedSamplingDrafts() {
  if (sharedParameterState.activeMode) sharedParameterState.modes[sharedParameterState.activeMode] = sharedSamplingCapture();
  return JSON.parse(JSON.stringify(sharedParameterState.modes));
}
function sharedSamplingRestoreDrafts(modes) {
  sharedParameterState.modes = JSON.parse(JSON.stringify(modes || {}));
  sharedParameterState.activeMode = ui.base_mode.value;
  sharedParameterState.modes[ui.base_mode.value] = sharedSamplingCapture();
}
function sharedSamplingModeChanged() {
  const mode = ui.base_mode.value;
  if (sharedParameterState.applying || mode === sharedParameterState.activeMode) return;
  if (sharedParameterState.activeMode) sharedParameterState.modes[sharedParameterState.activeMode] = sharedSamplingCapture();
  const next = sharedParameterState.modes[mode] || sharedSamplingDefaults(mode);
  for (const key of ["steps", "cfg", "sampler_name", "scheduler", "base_denoise"]) setControl(key, next[key]);
  sharedParameterState.krea_variance = { ...sharedKreaDefaults(), ...next.krea_variance };
  setControl("krea_variance_enabled", sharedParameterState.krea_variance.enabled);
  setRandomDualControls({ dual: next.dual });
  const seedMode = document.getElementById("singleSeedMode"), seed = document.getElementById("singleSampleSeed");
  if (seedMode) seedMode.value = next.seedMode || "fixed";
  if (seed) seed.value = next.seed ?? "0";
  sharedParameterState.activeMode = mode;
  sharedParameterVisibility(); updateRepairControls(); schedulePersist();
}
function sharedParameterVisibility() {
  const dual = ui.base_mode.value === "dual_sampler";
  const panel = document.getElementById("randomDualSection");
  if (panel) {
    panel.hidden = !dual;
    for (const field of panel.querySelectorAll("input, select")) field.disabled = !dual;
    const scale = document.getElementById("random_dual_scale");
    const sesqui = document.getElementById("random_dual_upscale_method").value === "sesqui";
    scale.min = "1"; scale.max = sesqui ? "2" : "8";
    document.getElementById("random_dual_switch_step").max = String(Math.max(0, Number(ui.steps.value) - 1));
  }
  const modeTitle = document.getElementById("baseSamplingTitle");
  if (modeTitle) modeTitle.textContent = dual ? "双采样" : "底图";
  const order = dual ? ["steps", "random_dual_switch_step", "random_dual_scale", "random_dual_model_format", "singleSeedControls", "cfg", "sampler_name", "scheduler", "krea_variance_enabled", "random_dual_second_cfg", "random_dual_second_sampler_name", "random_dual_second_scheduler"]
    : ["steps", "cfg", "sampler_name", "scheduler", "base_denoise", "singleSeedControls", "krea_variance_enabled"];
  for (const [index, id] of order.entries()) {
    const node = document.getElementById(id);
    const field = id === "singleSeedControls" ? node : node?.closest?.("label");
    if (field?.style) field.style.order = String(index);
  }
  for (const [id, title] of [["steps", dual ? "总步数" : "步数"], ["cfg", dual ? "CFG 1" : "CFG"], ["sampler_name", dual ? "采样器 1" : "采样器"], ["scheduler", dual ? "调度器 1" : "调度器"]]) {
    const label = ui[id]?.closest?.("label")?.querySelector("span"); if (label) label.textContent = title;
  }
  const kreaToggle = ui.krea_variance_enabled;
  if (kreaToggle && typeof currentProfile === "function") {
    const supported = currentProfile() === "krea2";
    kreaToggle.disabled = !supported && !kreaToggle.checked;
    kreaToggle.title = supported ? "" : "仅适用于 Krea 2";
    const label = kreaToggle.closest?.("label"); if (label) label.title = kreaToggle.title;
  }
  const denoise = ui.base_denoise?.closest?.("label"); if (denoise) denoise.hidden = dual;
  if (ui.base_denoise) ui.base_denoise.disabled = dual;
  const method = ui.latent_upscale_method.value;
  ui.latent_upscale_scale.min = method === "sesqui" ? "1" : "0.01";
  ui.latent_upscale_scale.max = method === "sesqui" ? "2" : "64";
  ui.latent_upscale_scale.title = method === "sesqui" ? "SesquiLSR 倍率为 1–2" : "潜空间放大倍率为 0.01–64";
  if (ui.latent_model_format) ui.latent_model_format.disabled = !ui.latent_upscale_enabled.checked;
  const seedMode = document.getElementById("singleSeedMode"), seed = document.getElementById("singleSampleSeed");
  if (seedMode) seedMode.disabled = false;
  if (seed) seed.disabled = seedMode?.value !== "fixed";
  const roll = document.getElementById("singleRollSeed"); if (roll) roll.disabled = false;
}
function sharedParameterResources() {
  for (const [key, choices] of [["second_sampler_name", resources.samplers], ["second_scheduler", resources.schedulers]]) {
    const element = document.getElementById(`random_dual_${key}`);
    if (element) populateResourceSelect(element, choices || [], element.value, "资源暂不可用");
  }
  const schema = resources.node_inputs?.SesquiLatentUpscale || {};
  const formatSpec = schema.model_format;
  for (const id of ["latent_model_format", "random_dual_model_format"]) {
    const element = document.getElementById(id);
    if (!element) continue;
    if (formatSpec) applyNodeInputControl(element, formatSpec);
    else {
      const selected = element.value || "Wan 2.1";
      populateSelect(element, ["SDXL", "Flux", "Flux2", "Ideogram 4", "Wan 2.1"], selected);
    }
    const dual = id === "random_dual_model_format";
    sharedLatentMethodChoice(element, dual ? document.getElementById("random_dual_upscale_method")?.value : ui.latent_upscale_method.value, dual ? sharedParameterState.dual.model_format : sharedParameterState.latent.model_format);
  }
  const dlss = resources.node_inputs?.DLSS5NeuralRender || {};
  for (const key of NVIDIA_DLSS_KEYS) applyNodeInputControl(ui[`nvidia_dlss_${key}`], dlss[key]);
}
function sharedParameterResourceIssues(settings) {
  const issues = [];
  if (settings.krea_variance?.enabled && settings.model_profile && settings.model_profile !== "krea2") issues.push("K2 多样性增强仅适用于 Krea 2，请关闭该选项");
  if (typeof resourcesLoaded === "undefined" || !resourcesLoaded) return issues;
  const face = settings.upscalers?.face_refine;
  if (face?.enabled) {
    const schema = resources.node_inputs?.ForbiddenVisionFaceProcessorIntegrated || {};
    const choices = (spec, fallback) => Array.isArray(spec?.[0]) ? spec[0] : fallback;
    if (!choices(schema.sampler, resources.samplers || []).includes(face.sampler)) issues.push(`脸部采样器不可用：${face.sampler}`);
    if (!choices(schema.scheduler, resources.schedulers || []).includes(face.scheduler)) issues.push(`脸部调度器不可用：${face.scheduler}`);
  }
  return issues;
}
function adaptPostprocessSettings(settings, source, targetWorkbench) {
  const sourceSingle = source?.workbench === "single";
  settings.base_denoise = source?.base_denoise ?? settings.base_denoise ?? 1;
  settings.krea_variance = sharedKreaFromSettings(source);
  if (targetWorkbench === "single" && !sourceSingle) {
    const latent = source?.upscalers?.latent;
    if (latent) Object.assign(settings.single.latent, { upscale_method: latent.upscale_method || "bilinear", model_format: latent.model_format || "Wan 2.1", half_precision: latent.half_precision ?? true });
    Object.assign(settings.single.dual, randomDualFromSettings(source));
  } else if (targetWorkbench === "random" && sourceSingle) {
    if (source.single?.latent) {
      settings.upscalers ||= {};
      settings.upscalers.latent = { ...settings.upscalers.latent, ...Object.fromEntries(["upscale_method", "model_format", "half_precision"].filter(key => source.single.latent[key] !== undefined).map(key => [key, source.single.latent[key]])) };
    }
    settings.dual = randomDualFromSettings(source);
  }
  if (source?.single?.dlss && !source?.upscalers?.nvidia?.dlss) {
    settings.upscalers ||= {}; settings.upscalers.nvidia ||= {};
    settings.upscalers.nvidia.dlss = { ...source.single.dlss }; delete settings.upscalers.nvidia.dlss.enabled;
  }
  return settings;
}
function initializeSharedParameters() {
  const seed = document.getElementById("singleSeedControls"), base = document.getElementById("baseSamplingFields");
  if (seed && base) base.append(seed);
  ui.base_mode.addEventListener("change", sharedSamplingModeChanged);
  ui.krea_variance_enabled?.addEventListener("change", () => { sharedParameterVisibility(); updateRepairControls(); schedulePersist(); });
  for (const key of RANDOM_DUAL_KEYS) document.getElementById(`random_dual_${key}`)?.addEventListener("change", () => { sharedParameterVisibility(); schedulePersist(); });
  for (const [id, target] of [["random_dual_model_format", "random_dual_upscale_method"], ["latent_model_format", "latent_upscale_method"]]) document.getElementById(id)?.addEventListener("change", () => {
    const selected = document.getElementById(id).value;
    ensureSelectValue(document.getElementById(target), selected.startsWith("legacy:") ? selected.slice(7) : "sesqui");
    if (!selected.startsWith("legacy:")) {
      if (target === "latent_upscale_method") sharedParameterState.latent.model_format = selected;
      else sharedParameterState.dual.model_format = selected;
    }
    sharedParameterVisibility(); schedulePersist();
  });
  ui.steps.addEventListener("input", () => { sharedParameterVisibility(); });
  sharedParameterResources(); sharedParameterVisibility();
}
