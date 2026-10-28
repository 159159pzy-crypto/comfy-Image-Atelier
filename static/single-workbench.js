/* Single and random workbenches share resources and favorites.
 * Only composition drafts belong to an individual workbench. */
const WORKBENCH_DRAFT_KEY = "anima-studio:workbenches:v1";
const singleWorkbenchState = {
  ready: false, mode: "random", drafts: { random: {}, single: {} }, single: {},
  generationMode: "txt2img", promptTarget: null,
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
    dual: { switch_step: 15, scale: 1.0, upscale_method: "sesqui", model_format: "Wan 2.1", half_precision: true, first_pipeline: "primary", second_pipeline: "primary", second_cfg: 8, second_sampler_name: "euler", second_scheduler: "simple", first_add_noise: true, second_add_noise: true, first_leftover_noise: true, second_leftover_noise: false },
    zero_negative: false, secondary_zero_negative: false,
    krea_variance: { enabled: false, randomize_percent: 50, auto_strength_factor: 1, strength: 20, noise_insert: "noise on beginning steps", steps_switchover_percent: 25, mask_starts_at: "beginning", mask_percent: 0, noise_distribution: "uniform", granularity: "values" },
    prompt_processing: { expand: processing, oc: { ...processing, custom_rule: true, character: "", features: "" } },
    prompt_job_id: "", processed_prompt_override: null,
    latent: { upscale_method: "sesqui", model_format: "Wan 2.1", half_precision: true, pipeline: "secondary" }, face_pipeline: "secondary",
    dlss: { enabled: false, style: "电影", intensity: 1, local_tone: 1, local_struct: 1, output_view: "处理", output_mix: 1, gpu_gen: "Auto" },
  };
  return { ...settings, ...(typeof sharedSamplingDefaults === "function" ? (() => { const value = sharedSamplingDefaults(); delete value.seedMode; delete value.seed; return value; })() : {}), workbench: "single", generation_mode: "txt2img", model_profile: profile, count: 1, base_mode: "normal",
    clip_name: settings.clip_name || profileConfig(profile).clip_name || "", vae_name: settings.vae_name || profileConfig(profile).vae_name || "", clip_type: settings.clip_type || profileConfig(profile).clip_type || "",
    single: singleWorkbenchUnifiedDefaults(singleWorkbenchMerge(single, typeof config !== "undefined" ? config.single_defaults || config.defaults?.single : {})) };
}
function singleWorkbenchUnifiedDefaults(single) {
  single.normal_pipeline = single.dual.first_pipeline = single.dual.second_pipeline = "primary";
  single.latent.pipeline = single.face_pipeline = "primary";
  single.loras_secondary = []; single.secondary_managed_triggers = [];
  for (const stage of Object.values(single.prompt_processing || {})) stage.enabled = false;
  single.prompt_job_id = ""; single.processed_prompt_override = null;
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
    '<div id="singleInputDropZone" class="image-input-zone" tabindex="0" role="group" aria-label="图生图参考图识别框" aria-describedby="singleInputStatus"><div class="single-reference-heading"><label class="button ghost compact single-upload-button">＋ 添加参考图<input id="singleInputUpload" class="sr-only" type="file" accept="image/png,image/jpeg,image/webp"></label><span id="singleInputStatus" class="field-hint" role="status">将图片拖入此框，或点击此框后粘贴，自动切换图生图</span></div>' +
    '<img id="singleInputPreview" alt="图生图参考图片" hidden></div>' +
    '<div class="single-inline-actions"><button id="singleUseInputSize" type="button" class="button ghost compact" hidden>使用原图尺寸</button><button id="singleInputRemove" type="button" class="button ghost compact" hidden>移除参考图</button></div>' +
    '<div id="singleInputFields" class="single-advanced-grid" hidden>' + number("denoise", "重绘强度", 0, 1, .01) + field("resize_mode", "尺寸适配", "text", [["crop", "填满并裁切"], ["pad", "适应并留边"], ["stretch", "拉伸"]]) + '<p class="field-hint wide">强度越低越保留原图；较高强度和后续二次采样会进一步改变画面。强度为 0 时基础阶段仅重建原图，后处理仍按配置执行。</p></div></section>' +
    '<section class="control-section single-prompt-section"><div class="section-heading"><h2>提示词</h2><span>描述你的下一张作品</span></div>' +
    '<label class="field single-prompt-main"><span>画面描述</span><textarea id="single_positive_prompt" data-single-path="positive_prompt" data-single-prompt rows="5" placeholder="描述主体、场景、动作与画面细节…"></textarea></label>' +
    '<label class="field"><span>负面提示词</span><textarea id="singleNegative" data-single-prompt rows="3"></textarea></label>' +
    '<label class="field"><span>前缀</span><textarea id="single_prefix" data-single-path="prefix" data-single-prompt rows="2" placeholder="风格、画面基调…"></textarea></label>' +
    '<label class="field"><span>后缀</span><textarea id="single_suffix" data-single-path="suffix" data-single-prompt rows="2" placeholder="补充细节、质量词…"></textarea></label>' +
    '<div class="single-prompt-tools"><button id="singleFavoriteOpen" type="button" class="button ghost compact">♡ 共享收藏</button><button id="singleFavoriteSave" type="button" class="button ghost compact">收藏提示词</button><button id="singleNormalize" type="button" class="button ghost compact">规范当前提示词框</button><button id="singleManagePromptRules" type="button" class="button ghost compact">管理规范与替换</button><button id="singleUndoNormalization" type="button" class="button ghost compact" hidden>撤销上次规范</button></div><p id="singleNormalizationStatus" class="field-hint" role="status"></p>' +
    sharedFavoritesPanelMarkup("single") + '</section>' +
    '<div id="singleParameterTabs" role="tablist" aria-label="参数分类"><span id="singleTabIndicator" aria-hidden="true"></span>' +
    [["common","常用参数"],["prompt","提示词处理"],["repair","放大修复"],["more","风格预设"]].map(([id,label]) => '<button id="singleCategory_' + id + '" type="button" role="tab" data-single-category="' + id + '" aria-selected="' + (id === "common") + '" tabindex="' + (id === "common" ? 0 : -1) + '">' + label + '</button>').join("") + '</div>' +
    '<section id="singleSeedSection" hidden><div id="singleSeedControls"><label class="field"><span>随机种</span><div class="shared-seed-controls"><input id="singleSampleSeed" aria-label="随机种" type="number" min="0" max="1125899906842624" step="1" value="0"><select id="singleSeedMode" aria-label="种子方式"><option value="fixed">固定种子</option><option value="random">每次随机</option></select><button id="singleRollSeed" type="button" class="button ghost compact">换一个</button></div></label></div></section>';
  html += promptAssistantMarkup();
  html += singleWorkbenchSection("双采样细项", number("dual.switch_step", "第二段起始步", 0, 149, 1) + number("dual.scale", "中间放大倍率", 1, 8, .05) + number("dual.second_cfg", "第二段 CFG", 0, 30, .1) + field("dual.second_sampler_name", "第二段采样器") + field("dual.second_scheduler", "第二段调度器") + field("dual.upscale_method", "中间放大算法", "text", upscalers) + '<p class="field-hint wide">第一段完整采样，第二段从指定步继续至总步数。</p>', "common", "singleDualSection");
  html += '<div id="singleLatentMethodField">' + field("latent.upscale_method", "放大算法", "text", upscalers) + '</div>';
  html += '<section id="singleCompatibilitySection" class="control-section" data-single-group="more" data-single-when="compatibility" hidden><div class="section-heading"><h2>兼容设置</h2><span id="singleCompatibilityCount"></span></div><p class="field-hint">旧配置中的特殊参数仍按原值生效。恢复默认仅重置已精简的参数，并让各阶段共用当前 LoRA 列表。</p><dl id="singleCompatibilityValues"></dl><button id="singleResetCompatibility" type="button" class="button ghost compact">恢复精简参数默认值</button></section>';
  document.getElementById("singleWorkbenchControls").innerHTML = html;
  document.getElementById("singleDualSection").hidden = true;
  document.getElementById("singleDualSection").dataset.singleWhen = "legacy";
  document.getElementById("singleLatentMethodField").hidden = true;
}

function singleWorkbenchRead(settings) {
  if (!singleWorkbenchActive() || singleWorkbenchState.reading) return singleWorkbenchState.ready ? { ...settings, workbench: "random", generation_mode: "txt2img" } : settings;
  const single = singleWorkbenchState.single;
  document.querySelectorAll("[data-single-path]").forEach(element => singleWorkbenchPath(single, element.dataset.singlePath, element.type === "checkbox" ? element.checked : element.type === "number" ? Number(element.value) : element.value));
  const result = { ...settings, workbench: "single", generation_mode: singleWorkbenchState.generationMode, count: 1, single: singleWorkbenchClone(single), negative_prompt: document.getElementById("singleNegative").value,
    ...singleWorkbenchResourceValues({ ...settings, single, ...singleWorkbenchState.resourceValues }),
    extra_prompt: "", quality_prompt: "", manual_artist: "", krea_prompt_pool: { enabled: false, ids: [] } };
  if (typeof readRandomDualControls === "function") Object.assign(result.single.dual, readRandomDualControls());
  if (typeof sharedParameterState !== "undefined" && settings.upscalers?.latent) Object.assign(result.single.latent, { upscale_method: settings.upscalers.latent.upscale_method, model_format: settings.upscalers.latent.model_format, half_precision: settings.upscalers.latent.half_precision });
  if (settings.krea_variance) result.single.krea_variance = singleWorkbenchClone(settings.krea_variance);
  for (const stage of Object.values(result.single.prompt_processing || {})) stage.enabled = false;
  result.single.prompt_job_id = ""; result.single.processed_prompt_override = null;
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
    if (typeof promptAssistantMigrateLegacy === "function") promptAssistantMigrateLegacy(singleWorkbenchState.single, raw.model_profile || currentProfile());
    for (const stage of Object.values(singleWorkbenchState.single.prompt_processing || {})) stage.enabled = false;
    singleWorkbenchState.single.prompt_job_id = ""; singleWorkbenchState.single.processed_prompt_override = null;
    singleWorkbenchState.generationMode = raw.generation_mode === "img2img" ? "img2img" : "txt2img";
    document.querySelectorAll("[data-single-path]").forEach(element => { const value = singleWorkbenchPath(singleWorkbenchState.single, element.dataset.singlePath); if (element.type === "checkbox") element.checked = Boolean(value); else if (element.tagName === "SELECT") ensureSelectValue(element, String(value ?? "")); else element.value = value ?? ""; });
    document.getElementById("singleNegative").value = raw.negative_prompt || "";
    singleWorkbenchState.resourceValues = singleWorkbenchResourceValues({ ...full, ...raw });
    singleWorkbenchRenderProcessed();
    void singleWorkbenchRestoreInput();
  } else if (raw.workbench === "random") singleWorkbenchState.mode = "random";
  if (typeof promptAssistantRender === "function") promptAssistantRender();
  singleWorkbenchRenderMode();
}
function singleWorkbenchCapture() {
  const settings = readSettings();
  return { settings: singleWorkbenchClone(settings), profileDrafts: singleWorkbenchClone(profileDrafts), seedMode: document.getElementById("singleSeedMode").value, seed: document.getElementById("singleSampleSeed").value, promptSeed: singleWorkbenchState.promptSeed, samplingModes: typeof sharedSamplingDrafts === "function" ? sharedSamplingDrafts() : {} };
}
function singleWorkbenchLegacyDrafts(settings, legacyProfiles) {
  const drafts = {};
  for (const [profile, stored] of Object.entries(legacyProfiles || {})) {
    if (!["anima", "krea2"].includes(profile) || !stored || typeof stored !== "object") continue;
    const restored = { ...singleWorkbenchClone(settings), model_profile: profile, workbench: "random", generation_mode: "txt2img" };
    for (const key of ["model_name", "base_mode", "base_denoise", "krea_variance", "dual", "steps", "cfg", "sampler_name", "scheduler", "upscalers", "extra_prompt", "loras", "pools"]) if (stored[key] !== undefined) restored[key] = singleWorkbenchClone(stored[key]);
    restored.base_denoise = stored.base_denoise ?? 1;
    restored.krea_variance = typeof sharedKreaFromSettings === "function" ? sharedKreaFromSettings({ ...stored, workbench: "random", model_profile: profile }) : { enabled: profile === "krea2", ...(stored.krea_variance || {}) };
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
function singleWorkbenchSwitch(mode, profile = currentProfile(), { save = true, settings = null, poolDraft = null } = {}) {
  if (!singleWorkbenchState.ready) return;
  if (save) singleWorkbenchPersist();
  if (typeof invalidatePoolContext === "function") invalidatePoolContext();
  const state = singleWorkbenchState;
  const animateMode = save && state.mode !== mode;
  state.promptRevision++;
  singleWorkbenchCancelInput({ render: false });
  clearTimeout(state.promptPoll);
  state.promptJob = null;
  if (document.getElementById("singleProcessPrompt")) document.getElementById("singleProcessPrompt").disabled = false;
  if (document.getElementById("singleCancelPrompt")) document.getElementById("singleCancelPrompt").hidden = true;
  state.promptTarget = null;
  const target = state.drafts[mode]?.[profile];
  state.mode = mode;
  state.restoring = true;
  profileDrafts = singleWorkbenchClone(target?.profileDrafts || {});
  const next = settings || target?.settings || (mode === "single" ? singleWorkbenchDefaultSettings(profile) : randomWorkbenchDefaultSettings(profile));
  applySettings({ ...next, workbench: mode, model_profile: profile });
  // Inactive fixed text is deliberately omitted from generation settings. Keep
  // the complete editor state when restoring a draft or importing history.
  const preservedPools = poolDraft || (!settings ? target?.profileDrafts?.[profile] : null);
  if (preservedPools) {
    for (const key of ["modes", "counts", "fixed", "pools"]) {
      if (preservedPools[key] !== undefined) draft[key] = singleWorkbenchClone(preservedPools[key]);
    }
    if (preservedPools.kreaPromptPoolEnabled !== undefined) ui.krea_prompt_pool_enabled.checked = Boolean(preservedPools.kreaPromptPoolEnabled);
    if (preservedPools.kreaPromptPoolSelection !== undefined) kreaPromptPoolSelection = singleWorkbenchClone(preservedPools.kreaPromptPoolSelection);
    renderDimensions();
    renderKreaPromptPool();
    updatePoolOverview();
  }
  document.getElementById("singleSeedMode").value = target?.seedMode || (target ? "random" : "fixed");
  document.getElementById("singleSampleSeed").value = target?.seed || "0";
  state.promptSeed = Number(target?.promptSeed || 0);
  if (typeof sharedSamplingRestoreDrafts === "function") sharedSamplingRestoreDrafts(settings ? null : target?.samplingModes);
  state.restoring = false;
  singleWorkbenchRenderMode({ animate: animateMode });
  singleWorkbenchPersist();
  if (mode === "single") void singleWorkbenchLoadFavorites();
  else if (typeof historyDirty !== "undefined" && historyDirty) void loadHistory(historyPage);
  // applySettings refreshes a changed model profile; same-profile workbench
  // changes reuse its inventory. Startup performs its own initial refresh.
}
function singleWorkbenchPositionIndicator({ immediate = false } = {}) {
  if (typeof animateStudioIndicator !== "function" || singleWorkbenchState.restoring) return;
  const indicator = document.querySelector("#workbenchSwitch .workbench-switch-thumb");
  const button = document.querySelector(`#workbenchSwitch [data-workbench="${singleWorkbenchState.mode}"]`);
  if (!indicator || !button) return;
  animateStudioIndicator(indicator, { x: button.offsetLeft - indicator.offsetLeft, width: button.offsetWidth }, { immediate });
}
function singleWorkbenchRenderMode({ animate = false } = {}) {
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
  ui.latent_upscale_method.closest("label").hidden = true;
  singleWorkbenchRefreshLimits();
  updateGenerationAvailability();
  if (typeof singleWorkbenchLayout === "function") singleWorkbenchLayout();
  if (typeof singleGalleryMode === "function") singleGalleryMode(active);
  if (typeof sharedParameterVisibility === "function") sharedParameterVisibility();
  if (typeof promptAssistantRender === "function") promptAssistantRender();
  // Applying a draft renders once while restoring. Preserve the old indicator
  // until the final render knows whether this was a user switch or a restore.
  if (!singleWorkbenchState.restoring) {
    const previous = singleWorkbenchState.motionMode;
    const changed = previous !== singleWorkbenchState.mode;
    singleWorkbenchPositionIndicator({ immediate: previous === undefined || changed && !animate });
    if (changed && animate && typeof StudioMotion !== "undefined") {
      const workspace = document.querySelector(".workspace");
      if (workspace) StudioMotion.presence(workspace, true, {
        from: { opacity: .72 }, response: .24, damping: 1, restart: true, manageInert: false,
        render: values => { workspace.style.opacity = String(values.opacity); },
      });
    }
    singleWorkbenchState.motionMode = singleWorkbenchState.mode;
  }
}
function singleWorkbenchRefreshLimits() {
  if (typeof sharedParameterVisibility === "function") sharedParameterVisibility();
}

function singleWorkbenchSeeds() {
  if (document.getElementById("singleSeedMode").value !== "fixed") return null;
  const sample_seed = Number(document.getElementById("singleSampleSeed").value), prompt_seed = Number(singleWorkbenchState.promptSeed || 0);
  if (!Number.isSafeInteger(sample_seed) || sample_seed < 0 || sample_seed > 2 ** 50 || singleWorkbenchActive() && (!Number.isSafeInteger(prompt_seed) || prompt_seed < 0 || prompt_seed > 2147483647)) throw new Error("请输入有效的非负整数种子");
  return singleWorkbenchActive() ? { sample_seed, prompt_seed } : { sample_seed };
}
function singleWorkbenchInvalidatePrompt() {
  if (!singleWorkbenchActive()) return;
  singleWorkbenchState.promptRevision++;
  singleWorkbenchState.single.prompt_job_id = "";
  singleWorkbenchState.single.processed_prompt_override = null;
  singleWorkbenchState.promptJob = null;
  clearTimeout(singleWorkbenchState.promptPoll);
  if (document.getElementById("singleProcessPrompt")) document.getElementById("singleProcessPrompt").disabled = false;
  if (document.getElementById("singleCancelPrompt")) document.getElementById("singleCancelPrompt").hidden = true;
  if (document.getElementById("singlePromptJobStatus")) document.getElementById("singlePromptJobStatus").textContent = "输入已更改，请重新处理提示词。";
  singleWorkbenchRenderProcessed();
}
function singleWorkbenchRenderProcessed() {
  if (!document.getElementById("singleProcessedPrompt")) return;
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
    const before = target.value;
    const result = await normalizePromptFields({ [field]: before }, { recordUndo: false, elements: { [field]: target } });
    if (!result.appliedFields.includes(field)) return;
    const changed = target.value !== before;
    if (changed && typeof setNormalizationUndo === "function") setNormalizationUndo([{ field, before, after: target.value }], { [field]: target });
    if (changed && target.id !== "singleNegative") singleWorkbenchInvalidatePrompt();
    const status = document.getElementById("singleNormalizationStatus");
    if (status) status.textContent = changed ? "已规范当前提示词框，可撤销。" : "当前提示词无需调整。";
    schedulePersist();
  } catch (error) {
    if (context !== promptNormalizationContext()) return;
    const status = document.getElementById("singleNormalizationStatus");
    if (status) status.textContent = `规范失败：${error.message}`;
    toast(error.message);
  }
}
async function singleWorkbenchLoadFavorites() {
  if (typeof sharedFavoritesLoad === "function") return sharedFavoritesLoad();
}
function singleWorkbenchRenderFavorites() {
  if (typeof sharedFavoritesRender === "function") sharedFavoritesRender();
}


function singleWorkbenchResources() {
  if (typeof sharedParameterResources === "function") sharedParameterResources();
  if (!singleWorkbenchState.ready) return;
  for (const [id, values] of [["single_dual_second_sampler_name", resources.samplers], ["single_dual_second_scheduler", resources.schedulers]]) {
    const input = document.getElementById(id), listId = `${id}_options`;
    let list = document.getElementById(listId);
    if (!list) { list = document.createElement("datalist"); list.id = listId; input.after(list); input.setAttribute("list", listId); }
    list.replaceChildren(...(values || []).map(value => new Option(value, value)));
  }
  if (typeof singleWorkbenchLayout === "function") singleWorkbenchLayout();
}
function singleWorkbenchFavoritesChanged(context) {
  if (typeof sharedFavoritesChanged === "function") sharedFavoritesChanged(context);
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
  const profile = record.settings?.model_profile || "anima";
  const target = singleWorkbenchState.mode === mode && currentProfile() === profile
    ? singleWorkbenchCapture() : singleWorkbenchState.drafts[mode]?.[profile];
  const fallback = mode === "single" ? singleWorkbenchDefaultSettings(profile) : randomWorkbenchDefaultSettings(profile);
  const current = target?.settings || fallback;
  const poolDraft = profileStateFrom(current);
  const storedPools = target?.profileDrafts?.[profile];
  for (const key of ["modes", "counts", "fixed", "pools"]) {
    if (storedPools?.[key] !== undefined) poolDraft[key] = singleWorkbenchClone(storedPools[key]);
  }
  poolDraft.kreaPromptPoolEnabled = Boolean(storedPools?.kreaPromptPoolEnabled ?? current.krea_prompt_pool?.enabled);
  poolDraft.kreaPromptPoolSelection = singleWorkbenchClone(storedPools?.kreaPromptPoolSelection ?? current.krea_prompt_pool?.ids ?? []);
  const settings = { ...singleWorkbenchClone(record.settings || {}),
    count: mode === "single" ? 1 : current.count ?? fallback.count,
    pools: singleWorkbenchClone(poolDraft.pools),
    krea_prompt_pool: {
      enabled: poolDraft.kreaPromptPoolEnabled,
      ids: singleWorkbenchClone(poolDraft.kreaPromptPoolSelection),
    },
  };
  for (const section of SECTIONS) {
    settings[`random_${section}`] = poolDraft.modes[section] === "pool";
    settings[`random_${section}_count`] = Number(poolDraft.counts[section] || 1);
    settings[`fixed_${section}`] = poolDraft.modes[section] === "fixed" ? poolDraft.fixed[section] || "" : "";
  }
  singleWorkbenchSwitch(mode, profile, { settings, poolDraft });
  document.getElementById("singleSeedMode").value = "fixed";
  document.getElementById("singleSampleSeed").value = record.sample_seed ?? 0;
  singleWorkbenchState.promptSeed = record.prompt_seed ?? 0;
  singleWorkbenchLayout();
  singleWorkbenchPersist();
  singleWorkbenchShowSettings();
}
async function singleWorkbenchFromHistory(record, asImage = false) {
  if (!record || singleWorkbenchState.historyTransferBusy) return;
  // Resolve the managed reference before changing the user's current draft.
  // A failed or abandoned import must leave the detail and its list in place.
  let importedInput = null;
  if (asImage) {
    const token = singleWorkbenchState.historyTransferRequest = (singleWorkbenchState.historyTransferRequest || 0) + 1;
    const owner = typeof detailContext !== "undefined" ? detailContext : null;
    const sourceProfile = currentProfile();
    const trigger = document.getElementById("useAsInputImage");
    singleWorkbenchState.historyTransferBusy = true;
    if (trigger) { trigger.disabled = true; trigger.setAttribute("aria-busy", "true"); }
    try {
      importedInput = await request("/api/input-images/from-history", { method: "POST", body: JSON.stringify({ image_id: record.id }) });
      if (token !== singleWorkbenchState.historyTransferRequest || sourceProfile !== currentProfile()
        || (owner && (owner !== detailContext || selectedRecord !== record))) return;
    } catch (error) {
      if (token === singleWorkbenchState.historyTransferRequest && (!owner || owner === detailContext)) toast(`参考图导入失败：${error.message}`);
      return;
    } finally {
      singleWorkbenchState.historyTransferBusy = false;
      if (trigger) { trigger.disabled = false; trigger.removeAttribute("aria-busy"); }
    }
  }
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
  if (importedInput) singleWorkbenchSetInput(importedInput);
  if (typeof suppressDetailReturnFocus === "function") suppressDetailReturnFocus();
  closeImagePreview({ immediate: true, restoreFocus: false }); motionDialog(ui.detailDialog, false);
  singleWorkbenchShowSettings();
  singleWorkbenchPersist();
  if (typeof revealHistoryWorkbench === "function") revealHistoryWorkbench();
  toast(asImage ? "已送入单图工作台作为参考图" : "已载入最终提示词和生成参数");
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
  const modes = typeof sharedSamplingDrafts === "function" ? sharedSamplingDrafts() : null;
  applySettings({ ...current, ...snapshot, workbench: singleWorkbenchState.mode });
  if (typeof sharedSamplingRestoreDrafts === "function") sharedSamplingRestoreDrafts(modes);
}
function initializeSingleWorkbench({ legacyRestored = false } = {}) {
  singleWorkbenchBuild();
  const state = singleWorkbenchState;
  state.ready = true;
  if (typeof initializePromptAssistant === "function") initializePromptAssistant();
  if (legacyRestored) document.getElementById("singleSeedMode").value = "random";
  const legacy = singleWorkbenchCapture();
  state.drafts.random = singleWorkbenchLegacyDrafts(legacy.settings, legacy.profileDrafts);
  state.drafts.random[currentProfile()] = legacy;
  let saved = null;
  try { const value = JSON.parse(localStorage.getItem(WORKBENCH_DRAFT_KEY) || "null"); if (value?.version === 1 && value.drafts?.random && value.drafts?.single) saved = value; } catch { /* The old draft remains available for recovery. */ }
  if (saved) { state.drafts = saved.drafts; singleWorkbenchSwitch(saved.mode === "single" ? "single" : "random", normalizeProfileId(saved.profile), { save: false }); }
  else singleWorkbenchRenderMode();
  document.querySelectorAll("[data-workbench]").forEach(button => { if (button.tagName === "BUTTON") button.addEventListener("click", () => singleWorkbenchSwitch(button.dataset.workbench)); });
  document.getElementById("workbenchSwitch").addEventListener("keydown", event => { if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return; event.preventDefault(); const mode = ["ArrowLeft", "Home"].includes(event.key) ? "single" : "random"; singleWorkbenchSwitch(mode); document.querySelector(`#workbenchSwitch [data-workbench="${mode}"]`).focus({ preventScroll: true }); });
  if (typeof ResizeObserver !== "undefined") {
    state.switchObserver = new ResizeObserver(() => singleWorkbenchPositionIndicator({ immediate: true }));
    state.switchObserver.observe(document.getElementById("workbenchSwitch"));
  }
  document.querySelectorAll("[data-single-prompt]").forEach(element => { element.addEventListener("focus", () => state.promptTarget = element); if (element.id !== "singleNegative") element.addEventListener("input", singleWorkbenchInvalidatePrompt); });
  for (const id of ["single_dual_upscale_method", "single_latent_upscale_method"]) document.getElementById(id).addEventListener("change", singleWorkbenchRefreshLimits);
  ui.settingsForm.addEventListener("change", () => singleWorkbenchLayout());
  document.getElementById("singleResetCompatibility").addEventListener("click", singleWorkbenchResetCompatibility);
  if (typeof initializeSharedParameters === "function") initializeSharedParameters();
  initializeSingleInput();
  ui.model_name.addEventListener("change", () => { if (singleWorkbenchActive() && state.inputBusy === "upload") singleWorkbenchCancelInput(); });
  document.getElementById("singleRollSeed").addEventListener("click", () => { document.getElementById("singleSampleSeed").value = crypto.getRandomValues(new Uint32Array(1))[0]; document.getElementById("singleSeedMode").value = "fixed"; singleWorkbenchLayout(); schedulePersist(); });
  initializeSharedFavorites();
  document.getElementById("singleNormalize").addEventListener("click", singleWorkbenchNormalizePrompt);
  document.getElementById("singleManagePromptRules").addEventListener("click", openPromptRuleDialog);
  document.getElementById("singleUndoNormalization").addEventListener("click", undoPromptNormalization);
  if (typeof clearNormalizationUndo === "function") clearNormalizationUndo();
  document.getElementById("sendToSingle").addEventListener("click", () => singleWorkbenchFromHistory(selectedRecord));
  document.getElementById("useAsInputImage").addEventListener("click", () => singleWorkbenchFromHistory(selectedRecord, true));
  window.addEventListener("pagehide", singleWorkbenchPersist);
  singleWorkbenchResources();
  if (typeof initializeSingleGallery === "function") initializeSingleGallery();
}
