/* Single and random workbenches share resources and favorites.
 * Only composition drafts belong to an individual workbench. */
const WORKBENCH_DRAFT_KEY = "anima-studio:workbenches:v1";
const singleWorkbenchState = {
  ready: false, mode: "random", drafts: { random: {}, single: {} }, single: {},
  generationMode: "txt2img", favoriteSection: "character", favoriteRequest: 0,
  favoriteData: null, favoriteItems: [], favoriteGroup: "__all__", promptTarget: null,
  inputImage: null, previewRecord: null, promptJob: null, promptPoll: null, promptRevision: 0, inputRequest: 0,
  reading: false, restoring: false, resourceValues: {}, promptSeed: 0,
};
function singleWorkbenchActive() { return singleWorkbenchState.ready && singleWorkbenchState.mode === "single"; }
function singleWorkbenchClone(value) { return JSON.parse(JSON.stringify(value)); }
function singleWorkbenchMerge(base, value) {
  const result = singleWorkbenchClone(base || {});
  for (const [key, entry] of Object.entries(value || {})) {
    result[key] = entry && typeof entry === "object" && !Array.isArray(entry)
      ? singleWorkbenchMerge(result[key] && typeof result[key] === "object" ? result[key] : {}, entry)
      : singleWorkbenchClone(entry);
  }
  return result;
}
function singleWorkbenchDefaultSettings(profile = "anima") {
  const settings = typeof profileSettingsDefaults === "function" ? singleWorkbenchClone(profileSettingsDefaults(profile)) : {};
  const encode = { tiled: true, tile_size: 512, overlap: 64, temporal_size: 64, temporal_overlap: 8 };
  const processing = { enabled: false, rule: "扩写-通用", custom_rule: false, custom_rule_content: "", user_prompt: "", llm_service: "", ollama_auto_unload: true, seed: 0 };
  const single = {
    positive_prompt: "", prefix: "", suffix: "", input_image_id: "", denoise: .65, resize_mode: "crop", unlock_resources: true, weight_dtype: "default",
    vae_encode: encode, vae_decode: encode, loras_secondary: [], secondary_managed_triggers: [], normal_pipeline: "primary",
    dual: { switch_step: 15, scale: 1.0, upscale_method: "sesqui", model_format: "Wan 2.1", half_precision: true, first_pipeline: "primary", second_pipeline: "primary", second_cfg: 4, second_sampler_name: "er_sde", second_scheduler: "simple", first_add_noise: true, second_add_noise: true, first_leftover_noise: true, second_leftover_noise: false },
    zero_negative: false, secondary_zero_negative: false,
    krea_variance: { enabled: false, randomize_percent: 50, auto_strength_factor: 1, strength: 20, noise_insert: "noise on beginning steps", steps_switchover_percent: 25, mask_starts_at: "beginning", mask_percent: 0, noise_distribution: "uniform", granularity: "values" },
    prompt_processing: { expand: processing, oc: { ...processing, custom_rule: true, character: "", features: "" } },
    prompt_job_id: "", processed_prompt_override: null,
    latent: { upscale_method: "sesqui", model_format: "Wan 2.1", half_precision: true, pipeline: "secondary" }, face_pipeline: "secondary",
    dlss: { enabled: false, style: "电影", intensity: 1, local_tone: 1, local_struct: 1, output_view: "处理", output_mix: 1, gpu_gen: "Auto" },
  };
  return { ...settings, workbench: "single", generation_mode: "txt2img", model_profile: profile, count: 1, base_mode: "normal",
    clip_name: settings.clip_name || profileConfig(profile).clip_name || "", vae_name: settings.vae_name || profileConfig(profile).vae_name || "", clip_type: settings.clip_type || profileConfig(profile).clip_type || "",
    single: singleWorkbenchUnifiedDefaults(singleWorkbenchMerge(single, typeof config !== "undefined" ? config.single_defaults || config.defaults?.single : {})) };
}
function singleWorkbenchUnifiedDefaults(single) {
  single.normal_pipeline = single.dual.first_pipeline = single.dual.second_pipeline = "primary";
  single.latent.pipeline = single.face_pipeline = "primary";
  single.loras_secondary = []; single.secondary_managed_triggers = [];
  return single;
}
function singleWorkbenchPath(value, path, next) {
  const parts = path.split("."); let current = value;
  for (const key of parts.slice(0, -1)) { if (!current[key] || typeof current[key] !== "object") current[key] = {}; current = current[key]; }
  if (arguments.length === 3) current[parts.at(-1)] = next;
  return current[parts.at(-1)];
}
function singleWorkbenchField(path, label, type = "text", options = null, attrs = "") {
  const id = `single_${path.replaceAll(".", "_")}`;
  const control = options
    ? `<select id="${id}" data-single-path="${path}">${options.map((option) => { const [value, title] = Array.isArray(option) ? option : [option, option]; return `<option value="${escapeHtml(value)}">${escapeHtml(title)}</option>`; }).join("")}</select>`
    : type === "textarea" ? `<textarea id="${id}" data-single-path="${path}" rows="3" ${attrs}></textarea>`
      : `<input id="${id}" data-single-path="${path}" type="${type}" ${attrs}>`;
  return type === "checkbox" ? `<label class="switch-row"><span>${label}</span>${control}</label>` : `<label class="field${type === "textarea" ? " wide" : ""}"><span>${label}</span>${control}</label>`;
}
function singleWorkbenchSection(title, content, group, id = "") {
  return '<section class="control-section" data-single-group="' + group + '"' + (id ? ' id="' + id + '"' : "") + '><div class="section-heading"><h2>' + title + '</h2></div><div class="single-advanced-grid">' + content + '</div></section>';
}
function singleWorkbenchToggleSection(title, path, content, group, id) {
  const inputId = `single_${path.replaceAll(".", "_")}`;
  return `<section id="${id}" class="control-section single-toggle-card" data-single-group="${group}"><label class="switch-row single-card-header"><span class="single-card-title" role="heading" aria-level="2">${title}</span><input id="${inputId}" data-single-path="${path}" type="checkbox" aria-controls="${id}Fields"></label><div id="${id}Fields" class="single-advanced-grid single-card-fields" data-single-enabled="${path}">${content}</div></section>`;
}

function singleWorkbenchBuild() {
  const field = singleWorkbenchField;
  const upscalers = ["sesqui", "bilinear", "bislerp", "nearest-exact", "area", "bicubic"];
  const number = (path, label, min = 0, max = 100, step = .1) => field(path, label, "number", null, 'min="' + min + '" max="' + max + '" step="' + step + '"');
  const check = (path, label) => field(path, label, "checkbox");
  const processing = (stage, label) => singleWorkbenchToggleSection(label, "prompt_processing." + stage + ".enabled",
    field("prompt_processing." + stage + ".rule", "规则") + field("prompt_processing." + stage + ".llm_service", "LLM 服务") +
    check("prompt_processing." + stage + ".custom_rule", "使用自定义规则") +
    field("prompt_processing." + stage + ".custom_rule_content", "自定义规则", "textarea") +
    field("prompt_processing." + stage + ".user_prompt", "附加指令", "textarea") +
    (stage === "oc" ? field("prompt_processing.oc.character", "角色") + field("prompt_processing.oc.features", "角色特征", "textarea") : ""), "prompt", stage === "oc" ? "singleOcSection" : "singleExpandSection");
  let html = '<section id="singleInputSection" class="control-section single-reference">' +
    '<div class="single-reference-heading"><label class="button ghost compact single-upload-button">＋ 添加参考图<input id="singleInputUpload" class="sr-only" type="file" accept="image/png,image/jpeg,image/webp"></label><span id="singleInputStatus" class="field-hint" role="status">拖入或粘贴图片，自动切换图生图</span></div>' +
    '<img id="singleInputPreview" alt="图生图参考图片" hidden>' +
    '<div class="single-inline-actions"><button id="singleUseInputSize" type="button" class="button ghost compact" hidden>使用原图尺寸</button><button id="singleInputRemove" type="button" class="button ghost compact" hidden>移除参考图</button></div>' +
    '<div id="singleInputFields" class="single-advanced-grid" hidden>' + number("denoise", "重绘强度", 0, 1, .01) + field("resize_mode", "尺寸适配", "text", [["crop", "填满并裁切"], ["pad", "适应并留边"], ["stretch", "拉伸"]]) + '<p class="field-hint wide">强度越低越保留原图；较高强度和后续二次采样会进一步改变画面。强度为 0 时基础阶段仅重建原图，后处理仍按配置执行。</p></div></section>' +
    '<section class="control-section single-prompt-section"><div class="section-heading"><h2>提示词</h2><span>描述你的下一张作品</span></div>' +
    '<label class="field single-prompt-main"><span>画面描述</span><textarea id="single_positive_prompt" data-single-path="positive_prompt" data-single-prompt rows="5" placeholder="描述主体、场景、动作与画面细节…"></textarea></label>' +
    '<label class="field"><span>负面提示词</span><textarea id="singleNegative" data-single-prompt rows="3"></textarea></label>' +
    '<label class="field"><span>前缀</span><textarea id="single_prefix" data-single-path="prefix" data-single-prompt rows="2" placeholder="风格、画面基调…"></textarea></label>' +
    '<label class="field"><span>后缀</span><textarea id="single_suffix" data-single-path="suffix" data-single-prompt rows="2" placeholder="补充细节、质量词…"></textarea></label>' +
    '<div class="single-prompt-tools"><button id="singleFavoriteOpen" type="button" class="button ghost compact">♡ 共享收藏</button><button id="singleNormalize" type="button" class="button ghost compact">规范提示词</button></div>' +
    '<div id="singleFavoritePanel" hidden><div class="single-favorite-tabs">' +
    [...SECTIONS, "artist"].map(section => '<button type="button" class="button ghost compact" data-single-favorite-section="' + section + '" aria-pressed="' + (section === "character") + '">' + (SECTION_META[section]?.label || "画师") + '</button>').join("") +
    '</div><label class="field"><span>收藏分组</span><select id="singleFavoriteGroup"><option value="__all__">全部收藏</option></select></label><div class="single-inline-actions"><button id="singleFavoriteManage" type="button" class="button ghost compact">管理收藏</button><button id="singleFavoriteRefresh" type="button" class="button ghost compact">刷新</button></div><p class="field-hint">点击词条，插入最近编辑位置。</p><div id="singleFavoriteList" class="single-favorite-list"></div><p id="singleFavoriteStatus" class="field-hint" role="status"></p></div></section>' +
    '<div id="singleParameterTabs" role="tablist" aria-label="参数分类"><span id="singleTabIndicator" aria-hidden="true"></span>' +
    [["common","常用参数"],["prompt","提示词处理"],["repair","放大修复"],["more","更多设置"]].map(([id,label]) => '<button id="singleCategory_' + id + '" type="button" role="tab" data-single-category="' + id + '" aria-selected="' + (id === "common") + '" tabindex="' + (id === "common" ? 0 : -1) + '">' + label + '</button>').join("") + '</div>' +
    '<section id="singleSeedSection" class="control-section" data-single-group="common"><div id="singleSeedControls"><div class="section-heading"><h2>随机种子</h2><button id="singleRollSeed" type="button" class="button ghost compact">换一个</button></div><div class="single-advanced-grid"><label class="field"><span>种子方式</span><select id="singleSeedMode"><option value="random">每次随机</option><option value="fixed">固定种子</option></select></label><label class="field"><span>图像种子</span><input id="singleSampleSeed" type="number" min="0" max="1125899906842624" step="1" value="0"></label></div></div></section>';
  html += processing("expand", "提示词扩写") + processing("oc", "OC 提示词处理");
  html += '<section class="control-section" data-single-group="prompt" id="singleProcessedSection"><div class="single-inline-actions"><button id="singleProcessPrompt" type="button" class="button primary compact">处理并预览提示词</button><button id="singleCancelPrompt" type="button" class="button ghost compact" hidden>取消处理</button></div><p id="singlePromptJobStatus" class="single-job-status" role="status">可先预览、编辑处理结果，再生成。</p><label id="singleProcessedField" class="field" hidden><span>处理后的提示词 · 可编辑</span><textarea id="singleProcessedPrompt" rows="6"></textarea></label><button id="singleDiscardProcessed" type="button" class="button ghost compact" hidden>清除处理结果</button></section>';
  html += singleWorkbenchSection("双采样细项", number("dual.switch_step", "第二段起始步", 0, 149, 1) + number("dual.scale", "中间放大倍率", 1, 8, .05) + number("dual.second_cfg", "第二段 CFG", 0, 30, .1) + field("dual.second_sampler_name", "第二段采样器") + field("dual.second_scheduler", "第二段调度器") + field("dual.upscale_method", "中间放大算法", "text", upscalers) + '<p class="field-hint wide">第一段完整采样，第二段从指定步继续至总步数。</p>', "common", "singleDualSection");
  html += '<div id="singleLatentMethodField">' + field("latent.upscale_method", "放大算法", "text", upscalers) + '</div>';
  html += singleWorkbenchToggleSection("DLSS 色彩处理", "dlss.enabled", field("dlss.style", "风格", "text", ["默认", "自然", "电影", "风格3"]) + number("dlss.intensity", "强度", 0, 1, .05) + number("dlss.output_mix", "输出混合", 0, 1, .01), "repair", "singleDlssSection");
  html += '<section id="singleCompatibilitySection" class="control-section" data-single-group="more"><div class="section-heading"><h2>兼容设置</h2><span id="singleCompatibilityCount"></span></div><p class="field-hint">旧配置中的特殊参数仍按原值生效。恢复默认仅重置已精简的参数，并让各阶段共用当前 LoRA 列表。</p><dl id="singleCompatibilityValues"></dl><button id="singleResetCompatibility" type="button" class="button ghost compact">恢复精简参数默认值</button><p id="singleCompatibilityStatus" class="field-hint" role="status"></p></section>';
  document.getElementById("singleWorkbenchControls").innerHTML = html;
  document.getElementById("singleDualSection").dataset.singleWhen = "dual";
}

function singleWorkbenchRead(settings) {
  if (!singleWorkbenchActive() || singleWorkbenchState.reading) return singleWorkbenchState.ready ? { ...settings, workbench: "random", generation_mode: "txt2img" } : settings;
  const single = singleWorkbenchState.single;
  document.querySelectorAll("[data-single-path]").forEach(element => singleWorkbenchPath(single, element.dataset.singlePath, element.type === "checkbox" ? element.checked : element.type === "number" ? Number(element.value) : element.value));
  const result = { ...settings, workbench: "single", generation_mode: singleWorkbenchState.generationMode, count: 1, single: singleWorkbenchClone(single), negative_prompt: document.getElementById("singleNegative").value,
    ...singleWorkbenchResourceValues({ ...settings, single, ...singleWorkbenchState.resourceValues }),
    extra_prompt: "", quality_prompt: "", manual_artist: "", krea_prompt_pool: { enabled: false, ids: [] } };
  for (const section of SECTIONS) { result[`random_${section}`] = false; result[`fixed_${section}`] = ""; }
  return result;
}
function singleWorkbenchResourceValues(settings) {
  const profile = settings.model_profile || currentProfile();
  const bound = profileConfig(profile);
  const others = ["anima", "krea2"].filter(id => id !== profile).map(profileConfig);
  return Object.fromEntries(["clip_name", "vae_name", "clip_type"].map(key => {
    let value = settings[key] || bound[key] || "";
    const normalized = String(value).replaceAll("\\", "/").split("/").at(-1).toLowerCase();
    const stale = others.some(other => other[key] !== bound[key] && other[key]?.toLowerCase() === normalized);
    if (bound[key] && (stale || settings.single?.unlock_resources === false)) value = bound[key];
    return [key, value];
  }));
}
function singleWorkbenchApply(raw) {
  if (!singleWorkbenchState.ready) return;
  if (raw.workbench === "single") {
    singleWorkbenchCancelInput({ render: false });
    singleWorkbenchState.mode = "single";
    const full = singleWorkbenchDefaultSettings(raw.model_profile || currentProfile());
    singleWorkbenchState.single = singleWorkbenchMerge(full.single, raw.single || {});
    singleWorkbenchState.generationMode = raw.generation_mode === "img2img" ? "img2img" : "txt2img";
    document.querySelectorAll("[data-single-path]").forEach(element => { const value = singleWorkbenchPath(singleWorkbenchState.single, element.dataset.singlePath); if (element.type === "checkbox") element.checked = Boolean(value); else if (element.tagName === "SELECT") ensureSelectValue(element, String(value ?? "")); else element.value = value ?? ""; });
    document.getElementById("singleNegative").value = raw.negative_prompt || "";
    singleWorkbenchState.resourceValues = singleWorkbenchResourceValues({ ...full, ...raw });
    singleWorkbenchRenderCompatibility();
    singleWorkbenchRenderProcessed();
    void singleWorkbenchRestoreInput();
    if (singleWorkbenchState.single.prompt_job_id) void singleWorkbenchPollPrompt(singleWorkbenchState.single.prompt_job_id);
  } else if (raw.workbench === "random") singleWorkbenchState.mode = "random";
  singleWorkbenchRenderMode();
}
function singleWorkbenchCapture() {
  const settings = readSettings();
  return { settings: singleWorkbenchClone(settings), profileDrafts: singleWorkbenchClone(profileDrafts), seedMode: document.getElementById("singleSeedMode").value, seed: document.getElementById("singleSampleSeed").value, promptSeed: singleWorkbenchState.promptSeed };
}
function singleWorkbenchLegacyDrafts(settings, legacyProfiles) {
  const drafts = {};
  for (const [profile, stored] of Object.entries(legacyProfiles || {})) {
    if (!["anima", "krea2"].includes(profile) || !stored || typeof stored !== "object") continue;
    const restored = { ...singleWorkbenchClone(settings), model_profile: profile, workbench: "random", generation_mode: "txt2img" };
    for (const key of ["model_name", "base_mode", "dual", "steps", "cfg", "sampler_name", "scheduler", "upscalers", "extra_prompt", "loras", "pools"]) if (stored[key] !== undefined) restored[key] = singleWorkbenchClone(stored[key]);
    restored.lora_managed_triggers = singleWorkbenchClone(stored.managedTriggers || []);
    restored.krea_prompt_pool = { enabled: Boolean(stored.kreaPromptPoolEnabled), ids: singleWorkbenchClone(stored.kreaPromptPoolSelection || []) };
    for (const section of SECTIONS) { restored[`random_${section}`] = stored.modes?.[section] === "pool"; restored[`fixed_${section}`] = stored.modes?.[section] === "fixed" ? stored.fixed?.[section] || "" : ""; restored[`random_${section}_count`] = Number(stored.counts?.[section] || 1); }
    drafts[profile] = { settings: restored, profileDrafts: singleWorkbenchClone(legacyProfiles), seedMode: "random", seed: "0", promptSeed: "0" };
  }
  return drafts;
}
function singleWorkbenchPersist() {
  if (!singleWorkbenchState.ready) return false;
  if (singleWorkbenchState.restoring) return true;
  const state = singleWorkbenchState;
  state.drafts[state.mode][currentProfile()] = singleWorkbenchCapture();
  try {
    const savedAt = new Date().toISOString();
    localStorage.setItem(WORKBENCH_DRAFT_KEY, JSON.stringify({ version: 1, mode: state.mode, profile: currentProfile(), drafts: state.drafts, savedAt }));
    localStorage.setItem(VIEW_KEY, JSON.stringify({ version: 1, activeSection, views: poolViews }));
    ui.draftStatus.textContent = `本地草稿已保存 ${formatSavedTime(savedAt)}`;
  } catch { ui.draftStatus.textContent = "浏览器未允许保存草稿"; }
  return true;
}
function singleWorkbenchSwitch(mode, profile = currentProfile(), { save = true, settings = null } = {}) {
  if (!singleWorkbenchState.ready) return;
  if (save) singleWorkbenchPersist();
  const state = singleWorkbenchState;
  state.promptRevision++;
  singleWorkbenchCancelInput({ render: false });
  clearTimeout(state.promptPoll);
  state.promptJob = null;
  document.getElementById("singleProcessPrompt").disabled = false;
  document.getElementById("singleCancelPrompt").hidden = true;
  const target = state.drafts[mode]?.[profile];
  state.mode = mode;
  state.restoring = true;
  profileDrafts = singleWorkbenchClone(target?.profileDrafts || {});
  const next = settings || target?.settings || (mode === "single" ? singleWorkbenchDefaultSettings(profile) : randomWorkbenchDefaultSettings(profile));
  applySettings({ ...next, workbench: mode, model_profile: profile });
  document.getElementById("singleSeedMode").value = target?.seedMode || "random";
  document.getElementById("singleSampleSeed").value = target?.seed || "0";
  state.promptSeed = Number(target?.promptSeed || 0);
  state.restoring = false;
  singleWorkbenchRenderMode();
  singleWorkbenchPersist();
  if (mode === "single") void singleWorkbenchLoadFavorites();
  else if (typeof historyDirty !== "undefined" && historyDirty) void loadHistory(historyPage);
  // applySettings refreshes a changed model profile; same-profile workbench
  // changes reuse its inventory. Startup performs its own initial refresh.
}
function singleWorkbenchRenderMode() {
  const active = singleWorkbenchActive();
  document.body.dataset.workbench = active ? "single" : "random";
  document.getElementById("singleWorkbenchControls").hidden = !active;
  document.getElementById("singlePreview").hidden = !active;
  document.querySelectorAll("[data-workbench]").forEach(button => { if (button.tagName !== "BUTTON") return; const selected = button.dataset.workbench === singleWorkbenchState.mode; button.setAttribute("aria-selected", String(selected)); button.tabIndex = selected ? 0 : -1; });
  singleWorkbenchRenderInput();
  document.querySelector(".panel-title h1").textContent = active ? "单图创作" : "生成设置";
  document.querySelector(".panel-title .kicker").textContent = active ? "ADVANCED WORKBENCH" : "COMPOSITION";
  if (active) ui.count.value = 1;
  ui.base_mode.disabled = active && singleWorkbenchState.generationMode === "img2img";
  ui.latent_upscale_method.closest("label").hidden = active;
  singleWorkbenchRefreshLimits();
  updateGenerationAvailability();
  if (typeof singleWorkbenchLayout === "function") singleWorkbenchLayout();
  if (typeof singleGalleryMode === "function") singleGalleryMode(active);
  if (typeof sharedParameterVisibility === "function") sharedParameterVisibility();
}
function singleWorkbenchRefreshLimits() {
  if (!singleWorkbenchState.ready) return;
  const dualSesqui = document.getElementById("single_dual_upscale_method").value === "sesqui";
  const dualScale = document.getElementById("single_dual_scale");
  dualScale.max = dualSesqui ? "2" : "8";
  dualScale.title = dualSesqui ? "SesquiLSR 倍率为 1–2" : "倍率为 1–8";
  const switchStep = document.getElementById("single_dual_switch_step");
  if (switchStep && ui.steps) switchStep.max = String(Math.max(0, Number(ui.steps.value) - 1));
  const latentSesqui = (singleWorkbenchActive() ? document.getElementById("single_latent_upscale_method").value : ui.latent_upscale_method?.value) === "sesqui";
  ui.latent_upscale_scale.min = latentSesqui ? "1" : "0.01";
  ui.latent_upscale_scale.max = latentSesqui ? "2" : "64";
  ui.latent_upscale_scale.title = latentSesqui ? "SesquiLSR 倍率为 1–2；算法在单图的后处理管线中选择" : "潜空间放大倍率";
  if (typeof sharedParameterVisibility === "function") sharedParameterVisibility();
}
function singleWorkbenchSeeds() {
  if (document.getElementById("singleSeedMode").value !== "fixed") return null;
  const sample_seed = Number(document.getElementById("singleSampleSeed").value), prompt_seed = Number(singleWorkbenchState.promptSeed || 0);
  if (!Number.isSafeInteger(sample_seed) || sample_seed < 0 || sample_seed > 2 ** 50 || !Number.isSafeInteger(prompt_seed) || prompt_seed < 0 || prompt_seed > 2147483647) throw new Error("请输入有效的非负整数种子");
  return { sample_seed, prompt_seed };
}
function singleWorkbenchInvalidatePrompt() {
  if (!singleWorkbenchActive()) return;
  singleWorkbenchState.promptRevision++;
  singleWorkbenchState.single.prompt_job_id = "";
  singleWorkbenchState.single.processed_prompt_override = null;
  singleWorkbenchState.promptJob = null;
  clearTimeout(singleWorkbenchState.promptPoll);
  document.getElementById("singleProcessPrompt").disabled = false;
  document.getElementById("singleCancelPrompt").hidden = true;
  document.getElementById("singlePromptJobStatus").textContent = "输入已更改，请重新处理提示词。";
  singleWorkbenchRenderProcessed();
}
function singleWorkbenchRenderProcessed() {
  const value = singleWorkbenchState.single.processed_prompt_override;
  const text = value ?? singleWorkbenchState.promptJob?.result?.text;
  const visible = typeof text === "string";
  document.getElementById("singleProcessedField").hidden = !visible;
  document.getElementById("singleDiscardProcessed").hidden = !visible;
  document.getElementById("singleProcessedPrompt").value = text || "";
}
async function singleWorkbenchProcessPrompt() {
  const button = document.getElementById("singleProcessPrompt");
  button.disabled = true;
  document.getElementById("singleCancelPrompt").hidden = false;
  const revision = singleWorkbenchState.promptRevision;
  try {
    const job = await request("/api/prompt-jobs", { method: "POST", body: JSON.stringify({ settings: readSettings(), force: true }) });
    if (revision !== singleWorkbenchState.promptRevision || !singleWorkbenchActive()) return;
    singleWorkbenchState.promptJob = job;
    singleWorkbenchState.single.prompt_job_id = job.id;
    singleWorkbenchState.single.processed_prompt_override = null;
    await singleWorkbenchPollPrompt(job.id);
  } catch (error) { if (revision !== singleWorkbenchState.promptRevision) return; document.getElementById("singlePromptJobStatus").textContent = error.message; button.disabled = false; document.getElementById("singleCancelPrompt").hidden = true; }
}
async function singleWorkbenchPollPrompt(id) {
  try {
    const job = await request(`/api/prompt-jobs/${encodeURIComponent(id)}`);
    if (singleWorkbenchState.single.prompt_job_id !== id) return;
    singleWorkbenchState.promptJob = job;
    const pending = ["queued", "running", "stopping"].includes(job.status);
    document.getElementById("singleProcessPrompt").disabled = pending;
    document.getElementById("singleCancelPrompt").hidden = !pending;
    document.getElementById("singlePromptJobStatus").textContent = job.error || ({ queued: "提示词任务排队中…", running: "正在处理提示词…", stopping: "正在取消处理…", completed: "处理完成，可以编辑结果后生成。", cancelled: "处理已取消", interrupted: "处理已中断", error: "提示词处理失败" }[job.status] || job.status);
    if (["queued", "running", "stopping"].includes(job.status)) singleWorkbenchState.promptPoll = setTimeout(() => singleWorkbenchPollPrompt(id), 1200);
    else { if (job.status !== "completed") singleWorkbenchState.single.prompt_job_id = ""; document.getElementById("singleProcessPrompt").disabled = false; document.getElementById("singleCancelPrompt").hidden = true; singleWorkbenchRenderProcessed(); schedulePersist(); }
  } catch (error) { if (singleWorkbenchState.single.prompt_job_id !== id) return; if (error.status === 404) { singleWorkbenchState.single.prompt_job_id = ""; singleWorkbenchState.promptJob = null; singleWorkbenchRenderProcessed(); schedulePersist(); } document.getElementById("singlePromptJobStatus").textContent = error.message; document.getElementById("singleProcessPrompt").disabled = false; }
}






function singleWorkbenchInsert(text) {
  const target = singleWorkbenchState.promptTarget || document.getElementById("single_positive_prompt");
  const start = target.selectionStart ?? target.value.length, end = target.selectionEnd ?? start;
  const before = target.value.slice(0, start), after = target.value.slice(end);
  const prefix = before && !/[\s,，]$/.test(before) ? ", " : "";
  const suffix = after && !/^[\s,，]/.test(after) ? ", " : "";
  target.setRangeText(`${prefix}${text}${suffix}`, start, end, "end"); target.focus();
  target.dispatchEvent(new Event("input", { bubbles: true }));
}
async function singleWorkbenchNormalizePrompt() {
  const target = singleWorkbenchState.promptTarget || document.getElementById("single_positive_prompt");
  const field = target.id === "singleNegative" ? "negative_prompt" : target.id;
  const context = promptNormalizationContext();
  try {
    const result = await normalizePromptFields({ [field]: target.value }, { recordUndo: false, elements: { [field]: target } });
    if (!result.appliedFields.includes(field)) return;
    if (target.id !== "singleNegative") singleWorkbenchInvalidatePrompt();
    schedulePersist();
  } catch (error) { if (context === promptNormalizationContext()) toast(error.message); }
}
async function singleWorkbenchLoadFavorites() {
  const state = singleWorkbenchState, section = state.favoriteSection, token = ++state.favoriteRequest;
  document.getElementById("singleFavoriteStatus").textContent = "正在读取共享收藏…";
  try {
    const data = await request(`/api/favorites/${section}`);
    if (token !== state.favoriteRequest || section !== state.favoriteSection) return;
    state.favoriteData = data;
    const select = document.getElementById("singleFavoriteGroup");
    select.replaceChildren(new Option("全部收藏", "__all__"), ...(data.groups || []).map(group => new Option(`${group.name} · ${group.totalCount ?? group.directCount ?? ""}`, group.id)));
    if (![...select.options].some(option => option.value === state.favoriteGroup)) state.favoriteGroup = "__all__";
    select.value = state.favoriteGroup;
    if (section === "artist") state.favoriteItems = (data.items || []).filter(item => item.groupIds?.length).map(item => ({ ...item, text: `@${item.name}`, title: `@${item.name}`, favorite_key: item.name }));
    else {
      const items = []; let page = 1, pages = 1;
      do {
        const result = await request(`/api/pools/${section}?collection=__all__&limit=200&page=${page}&model_profile=anima`);
        if (token !== state.favoriteRequest || section !== state.favoriteSection) return;
        items.push(...(result.items || [])); pages = Number(result.pages || 1); page++;
      } while (page <= pages);
      state.favoriteItems = items.map(item => ({ ...item, text: item.prompt || (item.trigger || []).join(", ") || item.name || item.title }));
    }
    singleWorkbenchRenderFavorites();
  } catch (error) { if (token === state.favoriteRequest) document.getElementById("singleFavoriteStatus").textContent = `收藏暂不可用：${error.message}`; }
}
function singleWorkbenchRenderFavorites() {
  const state = singleWorkbenchState, section = state.favoriteSection, groups = state.favoriteData?.groups || [];
  const groupIds = new Set([state.favoriteGroup]);
  for (let changed = true; changed;) { changed = false; for (const group of groups) if (groupIds.has(group.parentId) && !groupIds.has(group.id)) { groupIds.add(group.id); changed = true; } }
  const allowed = new Set((state.favoriteData?.items || []).filter(item => state.favoriteGroup === "__all__" ? item.groupIds?.length : item.groupIds?.some(id => groupIds.has(id))).map(item => String(section === "character" || section === "artist" ? item.name || item.id : item.id || item.name)));
  const items = state.favoriteItems.filter(item => allowed.has(String(item.favorite_key)));
  document.getElementById("singleFavoriteList").replaceChildren(...items.map(item => {
    const row = document.createElement("span"); row.className = "single-favorite-chip";
    const insert = document.createElement("button"); insert.type = "button"; insert.textContent = item.title || item.name; insert.title = item.text; insert.addEventListener("click", () => singleWorkbenchInsert(item.text));
    const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "♥"; remove.setAttribute("aria-label", `取消收藏 ${item.title || item.name}`);
    remove.addEventListener("click", async () => { remove.disabled = true; try { await request(`/api/favorites/${section}/item`, { method: "PUT", body: JSON.stringify({ ...(section === "artist" ? { name: item.name } : { id: item.id }), favorite: false }) }); await singleWorkbenchLoadFavorites(); if (section === activeSection) await loadFavorites(section); if (section === "artist") await loadArtistFavorites(); } catch (error) { toast(error.message); remove.disabled = false; } });
    row.append(insert, remove); return row;
  }));
  document.getElementById("singleFavoriteStatus").textContent = items.length ? `${items.length} 项收藏 · 与随机工作台同步` : "当前分组暂无收藏。";
}


function singleWorkbenchResources() {
  if (typeof sharedParameterResources === "function") sharedParameterResources();
  if (!singleWorkbenchState.ready) return;
  for (const stage of ["expand", "oc"]) {
    for (const [field, values] of [["rule", resources.prompt_processing?.rules || []], ["llm_service", resources.prompt_processing?.services || []]]) {
      const input = document.getElementById(`single_prompt_processing_${stage}_${field}`);
      const id = `${input.id}_options`;
      let list = document.getElementById(id);
      if (!list) { list = document.createElement("datalist"); list.id = id; input.after(list); input.setAttribute("list", id); }
      list.replaceChildren(...values.map(value => new Option(value, value)));
    }
  }
  for (const [id, values] of [["single_dual_second_sampler_name", resources.samplers], ["single_dual_second_scheduler", resources.schedulers]]) {
    const input = document.getElementById(id), listId = `${id}_options`;
    let list = document.getElementById(listId);
    if (!list) { list = document.createElement("datalist"); list.id = listId; input.after(list); input.setAttribute("list", listId); }
    list.replaceChildren(...(values || []).map(value => new Option(value, value)));
  }
  singleWorkbenchRenderCompatibility();
  if (typeof singleWorkbenchLayout === "function") singleWorkbenchLayout();
}
function singleWorkbenchFavoritesChanged() {
  if (!singleWorkbenchState.ready) return;
  try { localStorage.setItem("anima-studio:favorites-updated", String(Date.now())); } catch { /* Same-page updates still work. */ }
  if (singleWorkbenchActive() && !document.getElementById("singleFavoritePanel").hidden) void singleWorkbenchLoadFavorites();
}
function singleWorkbenchHistory(data) {
  if (typeof singleGalleryRefresh === "function") return singleGalleryRefresh();
}
function singleWorkbenchBatch(batch) {
  if (typeof singleGalleryBatch === "function") singleGalleryBatch(batch);
}
function singleWorkbenchShowSettings() {
  const workspace = document.querySelector(".workspace");
  workspace.dataset.mobileView = "settings";
  document.querySelectorAll(".mobile-tabs button[data-mobile-view]").forEach(button => button.classList.toggle("active", button.dataset.mobileView === "settings"));
  const mode = singleWorkbenchState.mode;
  requestAnimationFrame(() => {
    if (singleWorkbenchState.mode !== mode || ui.detailDialog.open) return;
    const target = mode === "single" ? document.getElementById("single_positive_prompt") : ui.model_profile;
    target?.focus({ preventScroll: true });
    target?.scrollIntoView({ block: "center", behavior: "auto" });
  });
}
function singleWorkbenchRestoreHistory(record) {
  const mode = record.settings?.workbench === "single" ? "single" : "random";
  singleWorkbenchSwitch(mode, record.settings?.model_profile || "anima", { settings: record.settings });
  if (mode === "single") {
    document.getElementById("singleSeedMode").value = "fixed";
    document.getElementById("singleSampleSeed").value = record.sample_seed;
    singleWorkbenchState.promptSeed = record.prompt_seed;
    singleWorkbenchLayout();
    singleWorkbenchPersist();
  }
  singleWorkbenchShowSettings();
}
async function singleWorkbenchFromHistory(record, asImage = false) {
  if (!record) return;
  if (typeof suppressDetailReturnFocus === "function") suppressDetailReturnFocus();
  const profile = record.settings?.model_profile || "anima";
  const settings = singleWorkbenchMerge(singleWorkbenchDefaultSettings(profile), record.settings || {});
  settings.workbench = "single"; settings.count = 1; settings.generation_mode = "txt2img";
  settings.single = singleWorkbenchMerge(singleWorkbenchDefaultSettings(profile).single, record.settings?.workbench === "single" ? record.settings.single || {} : {});
  if (typeof adaptPostprocessSettings === "function") adaptPostprocessSettings(settings, record.settings || {}, "single");
  settings.single.prefix = ""; settings.single.suffix = "";
  settings.single.positive_prompt = (record.settings?.workbench === "single" ? record.single_runtime?.positive : "") || record.positive_prompt || record.resolved_prompt || "";
  settings.single.prompt_processing.expand.enabled = false; settings.single.prompt_processing.oc.enabled = false;
  settings.single.prompt_job_id = ""; settings.single.processed_prompt_override = null; settings.single.input_image_id = "";
  settings.negative_prompt = record.negative_prompt || settings.negative_prompt || "";
  settings.lora_managed_triggers = [];
  singleWorkbenchSwitch("single", profile, { settings });
  document.getElementById("singleSampleSeed").value = record.sample_seed || 0;
  singleWorkbenchState.promptSeed = record.prompt_seed || 0;
  document.getElementById("singleSeedMode").value = "fixed";
  singleWorkbenchLayout();
  closeImagePreview({ immediate: true, restoreFocus: false }); ui.detailDialog.close();
  singleWorkbenchShowSettings();
  if (asImage) {
    singleWorkbenchCancelInput({ render: false });
    const token = singleWorkbenchState.inputRequest;
    const current = () => token === singleWorkbenchState.inputRequest && profile === currentProfile() && singleWorkbenchActive();
    singleWorkbenchState.inputBusy = "upload"; singleWorkbenchRenderInput();
    try {
      const input = await request("/api/input-images/from-history", { method: "POST", body: JSON.stringify({ image_id: record.id }) });
      if (!current()) return;
      singleWorkbenchSetInput(input);
    } catch (error) {
      if (!current()) return;
      singleWorkbenchState.inputError = error.message; toast(error.message);
      singleWorkbenchState.inputBusy = ""; singleWorkbenchRenderInput();
      return;
    }
  }
  singleWorkbenchPersist(); toast(asImage ? "已送入单图工作台作为参考图" : "已载入最终提示词和生成参数");
}
function singleWorkbenchApplyPreset(item) {
  const current = readSettings(); const snapshot = singleWorkbenchClone(item.settings || {});
  const sourceWorkbench = snapshot.workbench;
  delete snapshot.workbench; delete snapshot.generation_mode;
  if (singleWorkbenchActive()) {
    snapshot.single = singleWorkbenchMerge(current.single, sourceWorkbench === "single" ? snapshot.single || {} : {});
    if (typeof adaptPostprocessSettings === "function") adaptPostprocessSettings(snapshot, item.settings || {}, "single");
    if (sourceWorkbench !== "single" && (!snapshot.model_profile || snapshot.model_profile === current.model_profile)) {
      for (const key of ["clip_name", "vae_name", "clip_type"]) delete snapshot[key];
    }
    snapshot.single.input_image_id = current.single.input_image_id;
    snapshot.single.prompt_job_id = ""; snapshot.single.processed_prompt_override = null;
  } else {
    if (typeof adaptPostprocessSettings === "function") adaptPostprocessSettings(snapshot, item.settings || {}, "random");
    // Legacy random snapshots must initialize dual controls from their own
    // effective values, rather than inheriting an unrelated current draft.
    if (sourceWorkbench !== "single" && typeof randomDualFromSettings === "function") snapshot.dual = randomDualFromSettings(item.settings || {});
    delete snapshot.single; delete snapshot.clip_name; delete snapshot.vae_name; delete snapshot.clip_type;
  }
  applySettings({ ...current, ...snapshot, workbench: singleWorkbenchState.mode });
}
function initializeSingleWorkbench() {
  singleWorkbenchBuild();
  const state = singleWorkbenchState;
  state.ready = true;
  const legacy = singleWorkbenchCapture();
  state.drafts.random = singleWorkbenchLegacyDrafts(legacy.settings, legacy.profileDrafts);
  state.drafts.random[currentProfile()] = legacy;
  let saved = null;
  try { const value = JSON.parse(localStorage.getItem(WORKBENCH_DRAFT_KEY) || "null"); if (value?.version === 1 && value.drafts?.random && value.drafts?.single) saved = value; } catch { /* The old draft remains available for recovery. */ }
  if (saved) { state.drafts = saved.drafts; singleWorkbenchSwitch(saved.mode === "single" ? "single" : "random", normalizeProfileId(saved.profile), { save: false }); }
  else singleWorkbenchRenderMode();
  document.querySelectorAll("[data-workbench]").forEach(button => { if (button.tagName === "BUTTON") button.addEventListener("click", () => singleWorkbenchSwitch(button.dataset.workbench)); });
  document.getElementById("workbenchSwitch").addEventListener("keydown", event => { if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return; event.preventDefault(); const mode = ["ArrowLeft", "Home"].includes(event.key) ? "single" : "random"; singleWorkbenchSwitch(mode); document.querySelector(`#workbenchSwitch [data-workbench="${mode}"]`).focus(); });
  document.querySelectorAll("[data-single-prompt]").forEach(element => { element.addEventListener("focus", () => state.promptTarget = element); if (element.id !== "singleNegative") element.addEventListener("input", singleWorkbenchInvalidatePrompt); });
  document.querySelectorAll('[data-single-path^="prompt_processing."]').forEach(element => element.addEventListener("input", singleWorkbenchInvalidatePrompt));
  for (const id of ["single_dual_upscale_method", "single_latent_upscale_method"]) document.getElementById(id).addEventListener("change", singleWorkbenchRefreshLimits);
  ui.settingsForm.addEventListener("change", () => { singleWorkbenchLayout(); if (singleWorkbenchActive()) singleWorkbenchRenderCompatibility(); });
  document.getElementById("singleResetCompatibility").addEventListener("click", singleWorkbenchResetCompatibility);
  if (typeof initializeSharedParameters === "function") initializeSharedParameters();
  document.getElementById("singleProcessedPrompt").addEventListener("input", event => { state.single.processed_prompt_override = event.target.value; schedulePersist(); });
  document.getElementById("singleDiscardProcessed").addEventListener("click", () => { singleWorkbenchInvalidatePrompt(); schedulePersist(); });
  document.getElementById("singleProcessPrompt").addEventListener("click", singleWorkbenchProcessPrompt);
  document.getElementById("singleCancelPrompt").addEventListener("click", async () => { const id = state.single.prompt_job_id; if (!id) return; try { await request(`/api/prompt-jobs/${encodeURIComponent(id)}/cancel`, { method: "POST" }); await singleWorkbenchPollPrompt(id); } catch (error) { toast(error.message); } });
  initializeSingleInput();
  ui.model_name.addEventListener("change", () => { if (singleWorkbenchActive() && state.inputBusy === "upload") singleWorkbenchCancelInput(); });
  document.getElementById("singleRollSeed").addEventListener("click", () => { document.getElementById("singleSampleSeed").value = crypto.getRandomValues(new Uint32Array(1))[0]; document.getElementById("singleSeedMode").value = "fixed"; singleWorkbenchLayout(); schedulePersist(); });
  document.getElementById("singleFavoriteOpen").addEventListener("click", () => { const panel = document.getElementById("singleFavoritePanel"); panel.hidden = !panel.hidden; if (!panel.hidden) void singleWorkbenchLoadFavorites(); });
  document.querySelectorAll("[data-single-favorite-section]").forEach(button => button.addEventListener("click", () => { state.favoriteSection = button.dataset.singleFavoriteSection; state.favoriteGroup = "__all__"; document.querySelectorAll("[data-single-favorite-section]").forEach(item => item.setAttribute("aria-pressed", String(item === button))); void singleWorkbenchLoadFavorites(); }));
  document.getElementById("singleFavoriteRefresh").addEventListener("click", singleWorkbenchLoadFavorites);
  document.getElementById("singleFavoriteGroup").addEventListener("change", event => { state.favoriteGroup = event.target.value; singleWorkbenchRenderFavorites(); });
  document.getElementById("singleFavoriteManage").addEventListener("click", () => state.favoriteSection === "artist" ? ui.manageArtists.click() : openPool(state.favoriteSection));
  document.getElementById("singleNormalize").addEventListener("click", singleWorkbenchNormalizePrompt);
  document.getElementById("historyWorkbenchFilter").addEventListener("change", () => { historyCache.clear(); void loadHistory(1); });
  document.getElementById("sendToSingle").addEventListener("click", () => singleWorkbenchFromHistory(selectedRecord));
  document.getElementById("useAsInputImage").addEventListener("click", () => singleWorkbenchFromHistory(selectedRecord, true));
  window.addEventListener("focus", () => { if (singleWorkbenchActive() && !document.getElementById("singleFavoritePanel").hidden) void singleWorkbenchLoadFavorites(); });
  window.addEventListener("storage", event => { if (event.key === "anima-studio:favorites-updated" && singleWorkbenchActive()) void singleWorkbenchLoadFavorites(); });
  window.addEventListener("pagehide", singleWorkbenchPersist);
  singleWorkbenchResources();
  if (typeof initializeSingleGallery === "function") initializeSingleGallery();
}
