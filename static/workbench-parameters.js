/* Shared parameter behavior; stored workbench drafts remain independent. */
const RANDOM_DUAL_KEYS = ["switch_step", "scale", "second_cfg", "second_sampler_name", "second_scheduler", "upscale_method"];
const sharedParameterState = { latent: { model_format: "Wan 2.1", half_precision: true } };
function randomDualDefaults() {
  return { switch_step: 15, scale: 1, second_cfg: 4, second_sampler_name: "er_sde", second_scheduler: "simple", upscale_method: "sesqui" };
}
function randomDualFromSettings(settings = {}) {
  const explicit = settings.workbench === "single" ? settings.single?.dual : settings.dual;
  if (explicit && typeof explicit === "object") return Object.fromEntries(RANDOM_DUAL_KEYS.map(key => [key, explicit[key] ?? randomDualDefaults()[key]]));
  // Before editable dual settings, the template starts its second pass at 15,
  // shares main sampling controls, and resizes with the latent-stage settings.
  return { switch_step: 15, scale: settings.upscalers?.latent?.scale_by ?? 1.5,
    second_cfg: settings.cfg ?? 4, second_sampler_name: settings.sampler_name || "er_sde",
    second_scheduler: settings.scheduler || "simple", upscale_method: settings.upscalers?.latent?.upscale_method || "bilinear" };
}
function randomWorkbenchDefaultSettings(profile = "anima") {
  const settings = JSON.parse(JSON.stringify(profileSettingsDefaults(profile)));
  const upscalers = normalizeUpscalers(settings.upscalers);
  Object.assign(upscalers.latent, { upscale_method: "sesqui", model_format: "Wan 2.1", half_precision: true });
  return { ...settings, model_profile: profile, workbench: "random", dual: randomDualDefaults(), upscalers };
}
function setRandomDualControls(settings) {
  const values = randomDualFromSettings(settings);
  for (const key of RANDOM_DUAL_KEYS) {
    const element = document.getElementById(`random_dual_${key}`);
    if (!element) continue;
    if (element.tagName === "SELECT") ensureSelectValue(element, String(values[key]));
    else element.value = values[key];
  }
}
function readRandomDualControls() {
  return Object.fromEntries(RANDOM_DUAL_KEYS.map(key => {
    const element = document.getElementById(`random_dual_${key}`);
    return [key, element ? (element.type === "number" ? Number(element.value) : element.value) : randomDualDefaults()[key]];
  }));
}
function sharedParameterVisibility() {
  const single = typeof singleWorkbenchActive === "function" && singleWorkbenchActive();
  const panel = document.getElementById("randomDualSection");
  const available = !single && ui.base_mode.value === "dual_sampler";
  if (panel) {
    panel.hidden = !available;
    for (const field of panel.querySelectorAll("input, select")) field.disabled = !available;
    const scale = document.getElementById("random_dual_scale");
    const sesqui = document.getElementById("random_dual_upscale_method").value === "sesqui";
    scale.min = "1"; scale.max = sesqui ? "2" : "8";
    document.getElementById("random_dual_switch_step").max = String(Math.max(0, Number(ui.steps.value) - 1));
  }
  const method = single ? document.getElementById("single_latent_upscale_method")?.value : ui.latent_upscale_method.value;
  ui.latent_upscale_scale.min = method === "sesqui" ? "1" : "0.01";
  ui.latent_upscale_scale.max = method === "sesqui" ? "2" : "64";
  ui.latent_upscale_scale.title = method === "sesqui" ? "SesquiLSR 倍率为 1–2" : "潜空间放大倍率为 0.01–64";
}
function sharedParameterResources() {
  for (const [key, choices] of [["second_sampler_name", resources.samplers], ["second_scheduler", resources.schedulers]]) {
    const element = document.getElementById(`random_dual_${key}`);
    if (element) populateResourceSelect(element, choices || [], element.value, "资源暂不可用");
  }
}
function adaptPostprocessSettings(settings, source, targetWorkbench) {
  const sourceSingle = source?.workbench === "single";
  if (targetWorkbench === "single" && !sourceSingle) {
    const latent = source?.upscalers?.latent;
    if (latent) Object.assign(settings.single.latent, {
      upscale_method: latent.upscale_method || "bilinear", model_format: latent.model_format || "Wan 2.1",
      half_precision: latent.half_precision ?? true,
    });
    Object.assign(settings.single.dual, randomDualFromSettings(source));
  } else if (targetWorkbench === "random" && sourceSingle) {
    if (source.single?.latent) {
      settings.upscalers ||= {};
      settings.upscalers.latent = { ...settings.upscalers.latent, ...Object.fromEntries(
        ["upscale_method", "model_format", "half_precision"].filter(key => source.single.latent[key] !== undefined).map(key => [key, source.single.latent[key]])) };
    }
    settings.dual = randomDualFromSettings(source);
  }
  return settings;
}
function initializeSharedParameters() {
  for (const key of RANDOM_DUAL_KEYS) document.getElementById(`random_dual_${key}`)?.addEventListener("change", () => {
    sharedParameterVisibility(); schedulePersist();
  });
  ui.steps.addEventListener("input", () => {
    sharedParameterVisibility();
    if (typeof singleWorkbenchRefreshLimits === "function") singleWorkbenchRefreshLimits();
  });
  sharedParameterResources();
  sharedParameterVisibility();
}
