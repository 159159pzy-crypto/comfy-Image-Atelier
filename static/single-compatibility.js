/* Preserve saved values even when their controls are absent from the compact UI. */
const SINGLE_VISIBLE_PATHS = new Set([
  "positive_prompt", "prefix", "suffix", "input_image_id", "denoise", "resize_mode",
  "prompt_job_id", "processed_prompt_override", "dual.switch_step", "dual.scale",
  "dual.second_cfg", "dual.second_sampler_name", "dual.second_scheduler", "dual.upscale_method",
  "latent.upscale_method", "dlss.enabled", "dlss.style", "dlss.intensity", "dlss.output_mix",
  ...["expand", "oc"].flatMap(stage => ["enabled", "rule", "llm_service", "custom_rule", "custom_rule_content", "user_prompt", "character", "features"].map(key => `prompt_processing.${stage}.${key}`)),
]);
const SINGLE_COMPATIBILITY_LABELS = {
  unlock_resources: "自选编码资源", weight_dtype: "模型权重类型", loras_secondary: "旧副管线 LoRA",
  secondary_managed_triggers: "旧副管线触发词", normal_pipeline: "普通采样 LoRA 来源",
  zero_negative: "主管线负向归零", secondary_zero_negative: "副管线负向归零", face_pipeline: "脸部细化 LoRA 来源",
  vae_encode: "VAE 编码", vae_decode: "VAE 解码", dual: "双采样", latent: "潜空间", dlss: "DLSS",
  krea_variance: "Krea 变化", prompt_processing: "提示词处理", expand: "扩写", oc: "OC",
  tiled: "分块", tile_size: "分块尺寸", overlap: "分块重叠", temporal_size: "时间分块尺寸", temporal_overlap: "时间分块重叠",
  first_pipeline: "第一段 LoRA 来源", second_pipeline: "第二段 LoRA 来源", pipeline: "LoRA 来源",
  first_add_noise: "第一段添加噪声", second_add_noise: "第二段添加噪声", first_leftover_noise: "第一段剩余噪声", second_leftover_noise: "第二段剩余噪声",
  model_format: "模型格式", half_precision: "半精度", seed: "处理种子", ollama_auto_unload: "完成后卸载模型",
  local_tone: "局部色调", local_struct: "局部结构", output_view: "输出视图", gpu_gen: "显卡代际",
  auto_strength_factor: "自动强度系数", noise_insert: "噪声插入", steps_switchover_percent: "切换比例",
  mask_starts_at: "遮罩起点", mask_percent: "遮罩比例", noise_distribution: "噪声分布", granularity: "颗粒度",
};
function singleCompatibilityGet(object, path) {
  return path.split(".").reduce((value, key) => value?.[key], object);
}
function singleCompatibilityLeaves(value, prefix = "") {
  return Object.entries(value || {}).flatMap(([key, entry]) => {
    const path = prefix ? `${prefix}.${key}` : key;
    return entry && typeof entry === "object" && !Array.isArray(entry)
      ? singleCompatibilityLeaves(entry, path) : [[path, entry]];
  });
}
function singleWorkbenchCompatibilityEntries(settings) {
  const baseline = singleWorkbenchDefaultSettings(settings.model_profile || currentProfile());
  const entries = [];
  const add = (path, label) => {
    const value = singleCompatibilityGet(settings, path), normal = singleCompatibilityGet(baseline, path);
    if (value !== undefined && normal !== undefined && JSON.stringify(value) !== JSON.stringify(normal)) entries.push({ path, label, value, normal });
  };
  for (const [path] of singleCompatibilityLeaves(baseline.single)) {
    // The Krea controls were removed, not their saved execution settings.
    // Exclude the entire group from both this display and its reset action.
    if (path.startsWith("krea_variance.")) continue;
    if (!SINGLE_VISIBLE_PATHS.has(path)) add(`single.${path}`, path.split(".").map(part => SINGLE_COMPATIBILITY_LABELS[part] || part).join(" · "));
  }
  for (const [key, label] of [["clip_name", "CLIP"], ["vae_name", "VAE"], ["clip_type", "编码类型"]]) add(key, label);
  entries.push(...postprocessCompatibilityEntries(settings, baseline));
  return entries;
}
function postprocessCompatibilityEntries(settings, baseline) {
  const entries = [];
  const add = (path, label) => {
    const value = singleCompatibilityGet(settings, path), normal = singleCompatibilityGet(baseline, path);
    if (value !== undefined && normal !== undefined && JSON.stringify(value) !== JSON.stringify(normal)) entries.push({ path, label, value, normal });
  };
  add("upscalers.seedvr2.weight_dtype", "SeedVR2 · 权重类型");
  for (const group of ["encode", "decode"]) for (const key of ["tile_size", "overlap", "temporal_size", "temporal_overlap"]) {
    add(`upscalers.seedvr2.${group}.${key}`, `SeedVR2 · ${group === "encode" ? "编码" : "解码"} · ${SINGLE_COMPATIBILITY_LABELS[key]}`);
  }
  add("upscalers.nvidia.resize_type", "NVIDIA VSR · 缩放方式");
  const faceVisible = new Set(["enabled", "steps", "cfg_scale", "sampler", "scheduler", "denoise_strength", "processing_resolution"]);
  if (typeof UPSCALER_CONTROL_IDS !== "undefined") for (const [key, id] of Object.entries(UPSCALER_CONTROL_IDS.face_refine)) {
    if (faceVisible.has(key)) continue;
    const label = document.getElementById(id)?.closest("label")?.querySelector("span")?.textContent || key;
    add(`upscalers.face_refine.${key}`, `脸部细化 · ${label}`);
  }
  return entries;
}
function singleCompatibilityDisplay(value) {
  if (typeof value === "boolean") return value ? "开启" : "关闭";
  if (value === "primary") return "统一 LoRA 列表";
  if (value === "secondary") return "旧副管线";
  if (Array.isArray(value)) return value.map(item => typeof item === "object" ? `${item.filename} · ${item.enabled ? "启用" : "停用"} · 强度 ${item.strength}` : String(item)).join("；") || "空";
  return String(value);
}
function singleWorkbenchRenderCompatibility() {
  const container = document.getElementById("singleCompatibilityValues");
  if (!container || !singleWorkbenchActive()) return;
  const entries = singleWorkbenchCompatibilityEntries(readSettings());
  if (singleWorkbenchState.promptSeed) entries.push({ label: "提示词种子", value: singleWorkbenchState.promptSeed });
  container.replaceChildren(...entries.flatMap(entry => {
    const term = document.createElement("dt"), description = document.createElement("dd");
    term.textContent = entry.label; description.textContent = singleCompatibilityDisplay(entry.value);
    return [term, description];
  }));
  document.getElementById("singleCompatibilityCount").textContent = entries.length ? `${entries.length} 项保留值` : "使用默认值";
  document.getElementById("singleResetCompatibility").disabled = entries.length === 0;
}
function singleWorkbenchResetCompatibility() {
  const settings = readSettings();
  for (const entry of singleWorkbenchCompatibilityEntries(settings)) singleWorkbenchPath(settings, entry.path, singleWorkbenchClone(entry.normal));
  singleWorkbenchState.promptSeed = 0;
  applySettings(settings);
  singleWorkbenchInvalidatePrompt();
  singleWorkbenchPersist();
  singleWorkbenchRenderCompatibility();
  document.getElementById("singleCompatibilityStatus").textContent = "已恢复精简参数默认值，所有采样与修复阶段共用当前 LoRA 列表。";
}
