const SECTION_META = {
  character: { label: "角色", kicker: "CHARACTER", singular: "角色" },
  clothing: { label: "服装", kicker: "CLOTHING", singular: "服装" },
  pose: { label: "姿势", kicker: "POSE", singular: "姿势" },
  background: { label: "背景", kicker: "BACKGROUND", singular: "背景" },
  expression: { label: "表情", kicker: "EXPRESSION", singular: "表情" },
};
const NATIVE_FACET_LABELS = {
  clothing: { categories: "服装分类", traits: "服装特征" },
  pose: { categories: "姿势分类", traits: "姿势特征" },
  background: { categories: "场景分类", traits: "场景特征" },
  expression: { categories: "表情分类", traits: "表情特征" },
};
const SECTIONS = Object.keys(SECTION_META);
const DRAFT_KEY = "anima-random-studio:draft:v4";
const LEGACY_DRAFT_KEYS = ["anima-random-studio:draft:v3", "anima-random-studio:draft:v2", "anima-random-studio:draft:v1"];
const DRAFT_BACKUP_KEY_PREFIX = "anima-random-studio:draft:backup:v";
const VIEW_KEY = "anima-random-studio:pool-view:v1";
const THEME_KEY = "anima-random-studio:theme:v1";
const GROUP_TREE_KEY = "anima-random-studio:favorite-tree:v1";
const CUSTOM_GROUP_TREE_KEY = "anima-random-studio:custom-group-tree:v1";
const INTERNAL_HIRES_PERCENT = 45;
const LEGACY_DEFAULT_LORAS = [
  { filename: "anima-highres-aesthetic-boost.safetensors", enabled: true, strength: 0.75 },
  { filename: "BlueArchiveStyleB1.safetensors", enabled: true, strength: 0.95 },
  { filename: "Cunnyfunkyv3.safetensors", enabled: true, strength: 0.75 },
];
const ui = Object.fromEntries(
  [
    "connection",
    "startButton",
    "stopButton",
    "batchState",
    "progressCount",
    "progressFill",
    "batchError",
    "livePreview",
    "sampleProgress",
    "sampleFill",
    "queueBadge",
    "queuePanel",
    "reproduceImage",
    "variantButton",
    "variantCount",
    "replayDrawer",
    "variantDrawer",
    "closeReplayDrawer",
    "closeVariantDrawer",
    "replaySeedSummary",
    "replayStatus",
    "variantStatus",
    "confirmReplay",
    "confirmVariant",
    "variantModes",
    "redrawSections",
    "variantDrawerCount",
    "settingsForm",
    "count",
    "female_count",
    "male_count",
    "peopleTotal",
    "dimensionList",
    "animaPromptPoolControls",
    "kreaPromptPoolControls",
    "krea_prompt_pool_enabled",
    "kreaPromptPoolFile",
    "kreaPromptPoolText",
    "kreaPromptImportFormat",
    "kreaPromptImportMode",
    "kreaPromptImportDialog",
    "kreaPromptImportForm",
    "kreaPromptImportClose",
    "kreaPromptImportCancel",
    "kreaPromptImportPreviewButton",
    "kreaPromptImportHint",
    "kreaPromptImportSummary",
    "kreaPromptImportRows",
    "kreaPromptImportCommit",
    "kreaPromptPoolCount",
    "kreaPromptPoolList",
    "openKreaPromptPool",
    "kreaPoolDrawer",
    "kreaPoolBackdrop",
    "closeKreaPool",
    "confirmKreaPool",
    "kreaPoolMeta",
    "kreaPoolSearch",
    "kreaPoolSelectedOnly",
    "kreaPoolSelectPage",
    "kreaPoolSelectAll",
    "kreaPoolDeleteSelection",
    "kreaPoolAddItem",
    "kreaPoolOpenImport",
    "kreaPoolPrev",
    "kreaPoolNext",
    "kreaPoolPageLabel",
    "clearKreaPoolSelection",
    "kreaPoolSelectionLabel",
    "kreaPromptItemDialog",
    "kreaPromptItemForm",
    "kreaPromptItemTitle",
    "kreaPromptItemText",
    "kreaPromptItemClose",
    "kreaPromptItemCancel",
    "kreaPromptItemDelete",
    "poolOverview",
    "character_detail",
    "manual_artist",
    "manageArtists",
    "artistFavorites",
    "quality_prompt",
    "extra_prompt",
    "negative_prompt",
    "width",
    "height",
    "steps",
    "cfg",
    "gallery",
    "emptyState",
    "historyCount",
    "prevPage",
    "nextPage",
    "pageLabel",
    "detailDialog",
    "closeDialog",
    "detailImage",
    "imagePreviewDialog",
    "closeImagePreview",
    "imagePreviewPrevious",
    "imagePreviewNext",
    "imagePreviewStage",
    "imagePreviewTitle",
    "imagePreviewImage",
    "imagePreviewVeil",
    "imagePreviewStatus",
    "imagePreviewAnnouncement",
    "detailMeta",
    "detailStats",
    "detailSelection",
    "detailPositive",
    "detailNegative",
    "restoreSettings",
    "deleteRecord",
    "toast",
    "poolDrawer",
    "drawerBackdrop",
    "closePool",
    "poolTitle",
    "poolKicker",
    "poolMeta",
    "poolSearch",
    "poolSort",
    "poolLanguage",
    "togglePoolSidebar",
    "selectedOnly",
    "clearFilters",
    "selectPage",
    "selectAllPool",
    "clearPool",
    "favoriteSelection",
    "addCustom",
    "importCustom",
    "poolSidebar",
    "poolGrid",
    "poolEmpty",
    "poseConflict",
    "poolSelectionLabel",
    "poolPrev",
    "poolNext",
    "poolPageLabel",
    "confirmPool",
    "customDialog",
    "customForm",
    "closeCustom",
    "cancelCustom",
    "customDialogTitle",
    "customId",
    "customTitle",
    "customSubtitle",
    "customGenderField",
    "customGender",
    "customCharacterMeta",
    "customHair",
    "customEye",
    "customSeries",
    "customPoseMeta",
    "customCategory",
    "customTraits",
    "customPrompt",
    "customGroupChecks",
    "deleteCustom",
    "loraSummary",
    "loraList",
    "loraEmpty",
    "addLora",
    "resetLoras",
    "loraDialog",
    "closeLora",
    "cancelLora",
    "loraSearch",
    "loraCatalog",
    "loraCatalogEmpty",
    "loraCatalogMeta",
    "loraTriggerDialog",
    "loraTriggerForm",
    "loraTriggerTitle",
    "loraTriggerFilename",
    "loraTriggerWords",
    "loraSourceTriggerWords",
    "closeLoraTriggers",
    "cancelLoraTriggers",
    "resetLoraTriggers",
    "managePromptRules",
    "undoPromptNormalization",
    "promptNormalizationStatus",
    "promptRuleDialog",
    "promptRuleForm",
    "promptRuleList",
    "promptRuleId",
    "promptRuleFrom",
    "promptRuleTo",
    "promptRulePositive",
    "promptRuleNegative",
    "promptRuleLora",
    "promptRuleError",
    "deletePromptRule",
    "clearPromptRule",
    "closePromptRules",
    "cancelPromptRules",
    "draftStatus",
    "resetSettings",
    "clearDraft",
    "favoriteDialog",
    "favoriteForm",
    "favoriteTitle",
    "favoriteGroups",
    "favoriteNickname",
    "removeFavorite",
    "closeFavorite",
    "cancelFavorite",
    "favoriteSelectionDialog",
    "favoriteSelectionForm",
    "favoriteSelectionTitle",
    "favoriteSelectionSummary",
    "favoriteSelectionGroups",
    "favoriteSelectionGroupName",
    "createFavoriteSelectionGroup",
    "favoriteSelectionError",
    "closeFavoriteSelection",
    "cancelFavoriteSelection",
    "confirmFavoriteSelection",
    "groupDialog",
    "groupForm",
    "groupDialogTitle",
    "groupId",
    "groupName",
    "groupNameField",
    "deleteGroup",
    "closeGroup",
    "cancelGroup",
    "saveGroupButton",
    "importChildGroup",
    "childGroupDialog",
    "childGroupForm",
    "childGroupTitle",
    "childGroupHint",
    "childGroupSearch",
    "childGroupList",
    "childGroupEmpty",
    "childGroupError",
    "closeChildGroup",
    "cancelChildGroup",
    "confirmChildGroup",
    "deleteGroupDialog",
    "deleteGroupForm",
    "deleteGroupTitle",
    "deleteGroupSummary",
    "deleteGroupStats",
    "deleteGroupKeep",
    "deleteGroupKeepHint",
    "deleteGroupItemsOption",
    "deleteGroupItems",
    "deleteGroupItemsTitle",
    "deleteGroupItemsHint",
    "deleteGroupAllOption",
    "deleteGroupAll",
    "deleteGroupAllHint",
    "deleteGroupError",
    "closeDeleteGroup",
    "cancelDeleteGroup",
    "confirmDeleteGroup",
    "customGroupDialog",
    "customGroupForm",
    "customGroupDialogTitle",
    "customGroupId",
    "customGroupName",
    "deleteCustomGroup",
    "closeCustomGroup",
    "cancelCustomGroup",
    "importDialog",
    "importDialogTitle",
    "importFile",
    "importBundleName",
    "importJsonTemplate",
    "importCsvTemplate",
    "importTargetGroups",
    "closeImport",
    "cancelImport",
    "importHint",
    "importSummary",
    "importRows",
    "commitImport",
    "artistDialog",
    "closeArtists",
    "cancelArtists",
    "saveCurrentArtists",
    "artistFavoriteList",
    "artistFavoriteCount",
    "presetSummary",
    "presetName",
    "savePreset",
    "presetError",
    "presetList",
    "presetEmpty",
    "model_name",
    "model_profile",
    "modelProfileHint",
    "manageResourceOwnership",
    "resourceOwnershipDialog",
    "closeResourceOwnership",
    "closeResourceOwnershipFooter",
    "ownershipKind",
    "ownershipSearch",
    "ownershipSummary",
    "ownershipList",
    "ownershipError",
    "openLoraManager",
    "refreshResourceClassification",
    "base_mode",
    "sampler_name",
    "scheduler",
    "latent_upscale_enabled",
    "latent_upscale_scale",
    "latent_upscale_method",
    "latentUpscaleFields",
    "latent_second_sampler_enabled",
    "latentSecondSamplerFields",
    "latent_second_sampler_steps",
    "latent_second_sampler_cfg",
    "latent_second_sampler_name",
    "latent_second_scheduler",
    "latent_second_sampler_denoise",
    "seedvr2_enabled",
    "seedvr2_model_name",
    "seedvr2_vae_name",
    "seedvr2_weight_dtype",
    "seedvr2_scale_by",
    "seedvr2_color_correction_method",
    "seedvr2_sampler_steps",
    "seedvr2_sampler_cfg",
    "seedvr2_sampler_name",
    "seedvr2_scheduler",
    "seedvr2_sampler_denoise",
    "seedvr2_encode_tile_size",
    "seedvr2_encode_overlap",
    "seedvr2_encode_temporal_size",
    "seedvr2_encode_temporal_overlap",
    "seedvr2_decode_tile_size",
    "seedvr2_decode_overlap",
    "seedvr2_decode_temporal_size",
    "seedvr2_decode_temporal_overlap",
    "seedvr2Fields",
    "nvidia_upscale_enabled",
    "nvidia_resize_type",
    "nvidia_scale",
    "nvidia_quality",
    "nvidiaUpscaleFields",
    "face_refine_enabled",
    "faceRefineFields",
    "face_steps",
    "face_cfg_scale",
    "face_sampler",
    "face_scheduler",
    "face_denoise_strength",
    "face_selection",
    "face_detection_confidence",
    "face_manual_rotation",
    "face_processing_resolution",
    "face_upscaler_model",
    "face_crop_padding",
    "face_blend_softness",
    "face_mask_expansion",
    "face_sampling_mask_blur_size",
    "face_sampling_mask_blur_strength",
    "face_positive_prompt",
    "face_negative_prompt",
    "face_exclusions",
    "face_enable_pre_upscale",
    "face_replace_positive_prompt",
    "face_replace_negative_prompt",
    "face_enable_color_correction",
    "face_enable_segmentation",
    "face_enable_differential_diffusion",
    "face_enable_lightness_rescue",
    "face_enable_final_refinement",
    "face_offload_models_to_cpu",
    "repairSummary",
    "resourceWarning",
    "themeToggle",
    "appError",
  ].map((id) => [id, document.getElementById(id)]),
);

// 0.2.0 新增控件按可选处理:旧版 index.html 缺少它们时只隐藏对应功能,不阻断整个界面。
const OPTIONAL_CONTROLS = new Set([
  "livePreview",
  "sampleProgress",
  "sampleFill",
  "queueBadge",
  "queuePanel",
  "reproduceImage",
  "variantButton",
  "variantCount",
  "replayDrawer",
  "variantDrawer",
  "closeReplayDrawer",
  "closeVariantDrawer",
  "replaySeedSummary",
  "replayStatus",
  "variantStatus",
  "confirmReplay",
  "confirmVariant",
  "variantModes",
  "redrawSections",
  "variantDrawerCount",
]);
const missingUi = Object.entries(ui)
  .filter(([id, element]) => !element && !OPTIONAL_CONTROLS.has(id))
  .map(([id]) => id);
if (missingUi.length) {
  const message = `界面资源版本不一致，缺少控件：${missingUi.join("、")}。请刷新页面。`;
  const errorPanel = document.getElementById("appError");
  if (errorPanel) {
    errorPanel.textContent = message;
    errorPanel.hidden = false;
  }
  throw new Error(message);
}

let defaults = null;
let config = { catalog: { counts: {} } };
let currentBatch = null;
let selectedRecord = null;
let detailContext = null;
const imagePreview = {
  phase: "closed", token: 0, zoom: 1, x: 0, y: 0, width: 0, height: 0,
  animation: null, pointers: new Map(), gesture: null, tap: null, backdropPointer: null,
  navigationRequest: 0, preloaded: new Map(), swipe: null,
  context: null,
};
let regenerationOptions = null;
let regenerationRequest = 0;
let regenerationTrigger = null;
let historyPage = 1;
let historyPages = 1;
let historyItems = [];
const historyCache = new Map();
let historyDirty = false;
let lastTerminalBatch = "";
let toastTimer = null;
let persistTimer = null;
let initialized = false;
let draft = { modes: {}, counts: {}, fixed: {}, pools: {}, loras: [], managedTriggers: [] };
// 模型档案分别保存随机池、LoRA 与触发词草稿；仅写入浏览器草稿，不发送给后端。
let profileDrafts = {};
let profileResources = {};
let activeProfileId = "anima";
let kreaPromptPool = { model_profile: "krea2", items: [], count: 0, page: 1, pages: 0 };
let kreaPromptPoolSelection = [];
let kreaPoolPage = 1;
let kreaPoolPages = 0;
let kreaPoolQueryCount = 0;
let kreaPoolTotalCount = 0;
let kreaSelectedViewItems = null;
let kreaSelectedViewQuery = "";
let kreaPoolSearchTimer = null;
let editingKreaPromptItem = null;
let kreaImportPreview = null;
let kreaImportRequest = 0;
let activeSection = "character";
let poolPage = 1;
let poolPages = 1;
let poolItems = [];
let poolFacets = {};
let poolRequest = 0;
let editingCustom = null;
let editingCustomGroup = null;
let editingFavoriteItem = null;
let loraInventory = [];
let loraClassificationItems = [];
let loraInventoryLoaded = false;
let loraInventoryError = "";
let editingLoraTrigger = null;
let promptRules = [];
let editingPromptRule = null;
let normalizationUndo = null;
let promptNormalizationEpoch = 0;
const promptNormalizationRequests = new Map();
let favoritesData = { groups: [], items: [], favorite_keys: [] };
let favoritesAvailable = false;
let customGroups = [];
let artistFavoritesData = { groups: [], items: [] };
let stylePresets = [];
let stylePresetRequest = 0;
let stylePresetProfile = "";
let resources = { models: [], seedvr2_models: [], upscale_models: [], samplers: [], schedulers: [], model_profiles: {}, nodes: {}, plugins: {} };
let resourceRequest = 0;
let resourceInventoryProfile = "";
let resourceError = "";
let resourcesLoaded = false;
let loraInventoryRequest = 0;
let loraInventoryProfile = "";
let kreaPromptPoolRequest = 0;
let customGroupsRequest = 0;
let kreaPoolBackdropTimer = null;
let importPreview = null;
let editingGroup = null;
let childGroupParent = null;
let selectedChildSource = "";
let pendingGroupDelete = null;
let collapsedFavoriteGroups = (() => {
  try {
    const value = JSON.parse(localStorage.getItem(GROUP_TREE_KEY) || "{}");
    return Object.fromEntries(SECTIONS.map((section) => [section, new Set(value[section] || [])]));
  } catch {
    return Object.fromEntries(SECTIONS.map((section) => [section, new Set()]));
  }
})();
const customTreeInitialized = new Set();
let collapsedCustomGroups = (() => {
  try {
    const value = JSON.parse(localStorage.getItem(CUSTOM_GROUP_TREE_KEY) || "{}");
    Object.keys(value).forEach((section) => customTreeInitialized.add(section));
    return Object.fromEntries(SECTIONS.map((section) => [section, new Set(value[section] || [])]));
  } catch {
    return Object.fromEntries(SECTIONS.map((section) => [section, new Set()]));
  }
})();
const poseItemCache = new Map();

function defaultView() {
  return {
    query: "",
    sort: "",
    language: "zh",
    selectedOnly: false,
    scrollTop: 0,
    filters: { gender: "", hair: "", eye: "", series: "", categories: [], traits: [], collection: "", customGroup: "" },
  };
}
let poolViews = Object.fromEntries(SECTIONS.map((section) => [section, defaultView()]));

async function request(path, options = {}) {
  const response = await fetch(path, {
    headers: options.body ? { "Content-Type": "application/json" } : undefined,
    ...options,
  });
  let payload = null;
  try {
    payload = await response.json();
  } catch {
    payload = {};
  }
  if (!response.ok) {
    if (payload.code === "storage_read_only" && payload.store) {
      config.storage_status ||= {};
      config.storage_status[payload.store] = { read_only: true, reason: payload.error, path: payload.path };
      renderStorageStatus();
    }
    const error = new Error(payload.error || `请求失败 (${response.status})`);
    error.status = response.status;
    error.code = payload.code;
    throw error;
  }
  if (path.startsWith("/api/favorites/") && options.method && options.method !== "GET") {
    if (typeof singleWorkbenchFavoritesChanged === "function") singleWorkbenchFavoritesChanged();
  }
  return payload;
}

async function requestFavorites(path, options = {}) {
  const section = path.match(/\/api\/favorites\/([^/?]+)/)?.[1];
  const value = await request(path, options);
  return section === activeSection ? value : favoritesData;
}

// Protection lasts until restart. Observe new controls and later enable attempts
// so asynchronous resource/history refreshes cannot re-enable protected writes.
const STORAGE_CONTROLS = {
  history: "#startButton, #confirmReplay, #confirmVariant, #deleteRecord",
  style_presets: "#savePreset, .preset-actions button",
  custom_prompts: '#customForm button[type="submit"], #deleteCustom, #customGroupForm button[type="submit"], #deleteCustomGroup, #commitImport, [data-storage-write="custom_prompts"]',
  lora_triggers: '#loraTriggerForm button[type="submit"], #resetLoraTriggers',
  prompt_rules: '#promptRuleForm button[type="submit"], #deletePromptRule, .prompt-rule-actions input',
  prompt_pools: '#kreaPromptItemForm button[type="submit"], #kreaPromptItemDelete, #kreaPoolDeleteSelection, #kreaPromptImportCommit, .krea-prompt-remove',
};
function protectStorageControls() {
  for (const [store, selector] of Object.entries(STORAGE_CONTROLS)) {
    const status = config.storage_status?.[store];
    if (!status?.read_only) continue;
    for (const control of document.querySelectorAll(selector)) {
      if (!control.disabled) control.disabled = true;
      control.title = status.reason || "此存储已受保护，请修复文件后重启";
    }
  }
}
function renderStorageStatus() {
  const protectedStores = Object.values(config.storage_status || {}).filter((item) => item.read_only);
  const messages = protectedStores.map((item) => `${item.path || item.store}：${item.reason}`);
  const diagnostic = config.catalog?.discovery;
  if (diagnostic?.message) messages.push(diagnostic.message);
  const panel = document.getElementById("storageWarning");
  panel.hidden = messages.length === 0;
  panel.textContent = messages.join("\n");
  for (const dialog of document.querySelectorAll("dialog")) {
    let note = dialog.querySelector(".storage-protection-note");
    if (!note && protectedStores.length) {
      note = document.createElement("p");
      note.className = "storage-protection-note";
      note.setAttribute("role", "status");
      (dialog.querySelector("form") || dialog).prepend(note);
    }
    if (note) note.textContent = protectedStores.map((item) => `${item.path || item.store}：${item.reason}`).join("\n");
  }
  protectStorageControls();
}
new MutationObserver(protectStorageControls).observe(document.body, {
  childList: true, subtree: true, attributes: true, attributeFilter: ["disabled"],
});

function escapeHtml(value) {
  const span = document.createElement("span");
  span.textContent = value == null ? "" : String(value);
  return span.innerHTML.replaceAll('"', "&quot;").replaceAll("'", "&#39;");
}

function toast(message) {
  ui.toast.textContent = message;
  ui.toast.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => ui.toast.classList.remove("show"), 3200);
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}
const PROMPT_FIELD_NAMES = [
  "quality_prompt",
  "manual_artist",
  "fixed_character",
  "fixed_clothing",
  "fixed_pose",
  "fixed_background",
  "fixed_expression",
  "extra_prompt",
  "negative_prompt",
];
const PROMPT_FIELD_LABELS = {
  quality_prompt: "质量词",
  manual_artist: "画师",
  fixed_character: "固定角色",
  fixed_clothing: "固定服装",
  fixed_pose: "固定姿势",
  fixed_background: "固定背景",
  fixed_expression: "固定表情",
  extra_prompt: "额外提示词",
  negative_prompt: "负面词",
  custom_prompt: "自定义提示词",
};
function promptFieldForElement(element) {
  if (!element) return "";
  if (element.dataset?.fixed) return `fixed_${element.dataset.fixed}`;
  if (element === ui.customPrompt) return "custom_prompt";
  return PROMPT_FIELD_NAMES.includes(element.id) ? element.id : "";
}
function currentPromptFields() {
  return {
    quality_prompt: ui.quality_prompt.value,
    manual_artist: ui.manual_artist.value,
    ...Object.fromEntries(SECTIONS.map((section) => [`fixed_${section}`, draft.fixed[section] || ""])),
    extra_prompt: ui.extra_prompt.value,
    negative_prompt: ui.negative_prompt.value,
  };
}
function applyPromptField(field, value) {
  if (field.startsWith("fixed_")) {
    const section = field.slice(6);
    draft.fixed[section] = value;
    const input = ui.dimensionList.querySelector(`[data-fixed="${section}"]`);
    if (input) input.value = value;
    return;
  }
  if (field === "custom_prompt") {
    ui.customPrompt.value = value;
    return;
  }
  if (ui[field]) ui[field].value = value;
}
function setNormalizationUndo(changes) {
  if (!changes.length) return;
  normalizationUndo = Object.fromEntries(changes.map((item) => [item.field, item.before]));
  ui.undoPromptNormalization.hidden = false;
  const labels = changes.map((item) => PROMPT_FIELD_LABELS[item.field] || item.field);
  ui.promptNormalizationStatus.textContent = `已规范 ${changes.length} 个字段：${labels.join("、")}`;
}
function clearNormalizationUndo(message = "编辑结束后自动按 Anima Tag 规范整理。") {
  normalizationUndo = null;
  ui.undoPromptNormalization.hidden = true;
  ui.promptNormalizationStatus.textContent = message;
}
function promptNormalizationContext() {
  const mode = typeof singleWorkbenchState !== "undefined" && singleWorkbenchState.ready ? singleWorkbenchState.mode : "random";
  return `${promptNormalizationEpoch}:${currentProfile()}:${mode}`;
}
function invalidatePromptNormalization() {
  promptNormalizationEpoch++;
  promptNormalizationRequests.clear();
}
function promptElementForField(field) {
  if (field.startsWith("fixed_")) return ui.dimensionList.querySelector(`[data-fixed="${field.slice(6)}"]`);
  if (field === "custom_prompt") return ui.customPrompt;
  return ui[field] || document.getElementById(field);
}
async function normalizePromptFields(fields, { apply = true, recordUndo = true, elements = {} } = {}) {
  const context = promptNormalizationContext();
  const captured = Object.fromEntries(Object.entries(fields).map(([field, value]) => {
    const revision = (promptNormalizationRequests.get(field) || 0) + 1;
    promptNormalizationRequests.set(field, revision);
    return [field, { value, revision, element: elements[field] || promptElementForField(field) }];
  }));
  const payload = await request("/api/prompts/normalize", {
    method: "POST",
    body: JSON.stringify({ fields, managedTriggers: clone(draft.managedTriggers || []), model_profile: currentProfile() }),
  });
  const appliedFields = [];
  if (apply && context === promptNormalizationContext()) {
    for (const [field, value] of Object.entries(payload.fields || {})) {
      const owner = captured[field];
      const currentValue = owner?.element?.value ?? (field.startsWith("fixed_") ? draft.fixed[field.slice(6)] : undefined);
      if (!owner || owner.revision !== promptNormalizationRequests.get(field) || currentValue !== owner.value) continue;
      if (elements[field]) owner.element.value = value;
      else applyPromptField(field, value);
      appliedFields.push(field);
    }
    const changes = (payload.changes || []).filter(change => appliedFields.includes(change.field));
    if (changes.length && recordUndo) setNormalizationUndo(changes);
    if (payload.changed && appliedFields.length) schedulePersist();
  }
  return { ...payload, appliedFields };
}
async function normalizePromptElement(element) {
  const field = promptFieldForElement(element);
  if (!field) return;
  const context = promptNormalizationContext();
  try {
    await normalizePromptFields({ [field]: element.value });
  } catch (error) {
    if (context !== promptNormalizationContext()) return;
    ui.promptNormalizationStatus.textContent = `规范失败：${error.message}`;
    toast(error.message);
  }
}
async function normalizeSettingsPayload(settings) {
  const fields = Object.fromEntries(
    PROMPT_FIELD_NAMES.filter((field) => typeof settings[field] === "string").map((field) => [field, settings[field]]),
  );
  const payload = await request("/api/prompts/normalize", {
    method: "POST",
    body: JSON.stringify({ fields, managedTriggers: settings.lora_managed_triggers || [], model_profile: settings.model_profile || currentProfile() }),
  });
  return { ...settings, ...(payload.fields || {}) };
}
function defaultPools() {
  return Object.fromEntries(SECTIONS.map((section) => [section, { mode: "include", ids: [], excluded_ids: [] }]));
}
function currentView() {
  return poolViews[activeSection] || (poolViews[activeSection] = defaultView());
}

function applyTheme(theme, persist = false) {
  const next = theme === "dark" ? "dark" : "light";
  document.documentElement.dataset.theme = next;
  document.querySelector('meta[name="theme-color"]').content = next === "dark" ? "#1c1c1e" : "#f5f5f7";
  ui.themeToggle.title = next === "dark" ? "切换浅色主题" : "切换深色主题";
  ui.themeToggle.setAttribute("aria-label", ui.themeToggle.title);
  ui.themeToggle.setAttribute("aria-pressed", String(next === "dark"));
  ui.themeToggle.querySelector("span").textContent = next === "dark" ? "☀" : "☾";
  if (persist) localStorage.setItem(THEME_KEY, next);
}

function initialTheme() {
  const saved = localStorage.getItem(THEME_KEY);
  if (saved === "light" || saved === "dark") return saved;
  return matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function normalizeProfileId(value) {
  const normalized = String(value || "").trim().toLowerCase();
  return normalized === "krea2" || normalized.includes("krea2") ? "krea2" : "anima";
}
function defaultUpscalers() {
  return {
    latent: { enabled: false, scale_by: 1.5, upscale_method: "bilinear", model_format: "Wan 2.1", half_precision: true, second_sampler: { enabled: true, steps: 20, cfg: 4, sampler_name: "er_sde", scheduler: "simple", denoise: 1 } },
    seedvr2: { enabled: false, model_name: "", vae_name: "", scale_by: 2, weight_dtype: "default", color_correction_method: "lab", sampler: { steps: 1, cfg: 1, sampler_name: "euler", scheduler: "simple", denoise: 1 }, encode: { tile_size: 512, overlap: 64, temporal_size: 64, temporal_overlap: 8 }, decode: { tile_size: 512, overlap: 128, temporal_size: 4096, temporal_overlap: 8 } },
    nvidia: { enabled: false, resize_type: "scale by multiplier", scale: 2, quality: "ULTRA" },
    face_refine: { enabled: false, steps: 8, cfg_scale: 1, sampler: "er_sde", scheduler: "simple", denoise_strength: 0.3, face_selection: 0, detection_confidence: 0.75, manual_rotation: "None", processing_resolution: 1024, enable_pre_upscale: true, upscaler_model: "Fast 4x (Lanczos)", crop_padding: 1.6, face_positive_prompt: "", replace_positive_prompt: false, face_negative_prompt: "", replace_negative_prompt: false, exclusions: "", blend_softness: 8, mask_expansion: 2, sampling_mask_blur_size: 21, sampling_mask_blur_strength: 1, enable_color_correction: true, enable_segmentation: true, enable_differential_diffusion: true, enable_lightness_rescue: true, enable_final_refinement: true, offload_models_to_cpu: true },
  };
}
function normalizeUpscalers(value, fallback = null) {
  const base = clone(fallback || defaultUpscalers());
  const raw = value && typeof value === "object" ? value : {};
  for (const key of Object.keys(base)) {
    if (raw[key] && typeof raw[key] === "object") {
      base[key] = { ...base[key], ...raw[key] };
      for (const nested of ["second_sampler", "sampler", "encode", "decode"]) {
        if (base[key][nested] && typeof base[key][nested] === "object" && raw[key][nested] && typeof raw[key][nested] === "object")
          base[key][nested] = { ...base[key][nested], ...raw[key][nested] };
      }
    }
  }
  base.latent.enabled = Boolean(base.latent.enabled);
  base.latent.scale_by = Number.isFinite(Number(base.latent.scale_by))
    ? Math.max(0.01, Math.min(64, Number(base.latent.scale_by)))
    : 1.5;
  base.seedvr2.enabled = Boolean(base.seedvr2.enabled);
  base.seedvr2.model_name = String(base.seedvr2.model_name || "");
  base.seedvr2.vae_name = String(base.seedvr2.vae_name || "");
  base.seedvr2.scale_by = Number.isFinite(Number(base.seedvr2.scale_by)) ? Math.max(0.01, Math.min(64, Number(base.seedvr2.scale_by))) : 2;
  base.nvidia.enabled = Boolean(base.nvidia.enabled);
  // Ignore the deprecated v1 upscale-model choice. RTXVideoSuperResolution
  // uses the installed GPU/plugin directly and has no model-file input.
  delete base.nvidia.model_name;
  base.face_refine.enabled = Boolean(base.face_refine.enabled);
  return base;
}
function profileDefaults(profile) {
  const id = normalizeProfileId(profile);
  const value = resources.model_profiles?.[id] || resources.profiles?.[id] || profileResources?.[id];
  return value?.defaults && typeof value.defaults === "object" ? value.defaults : {};
}
function profileSettingsDefaults(profile) {
  const id = normalizeProfileId(profile);
  return defaults?.profile_settings?.[id] || defaults?.profiles?.[id]?.settings || (id === "krea2" ? { ...defaults, loras: [], pools: defaultPools() } : defaults || {});
}
function defaultLorasForProfile(profile) {
  return clone(profileSettingsDefaults(profile)?.loras || []);
}
function profileConfig(profile) {
  const id = normalizeProfileId(profile);
  return resources.model_profiles?.[id] || resources.profiles?.[id] || profileResources?.[id] || { id };
}
function profileLabel(profile) {
  const value = profileConfig(profile);
  return value.label || (normalizeProfileId(profile) === "krea2" ? "Krea 2" : "Anima");
}
function profileStateFrom(settings) {
  return {
    modes: Object.fromEntries(SECTIONS.map((section) => [section, settings[`random_${section}`] ? "pool" : settings[`fixed_${section}`] ? "fixed" : "off"])),
    counts: Object.fromEntries(SECTIONS.map((section) => [section, Number(settings[`random_${section}_count`] || 1)])),
    fixed: Object.fromEntries(SECTIONS.map((section) => [section, settings[`fixed_${section}`] || ""])),
    pools: clone(settings.pools || defaultPools()),
    loras: clone(settings.loras || []),
    managedTriggers: clone(settings.lora_managed_triggers || []),
  };
}
function currentProfile() {
  return normalizeProfileId(activeProfileId || ui.model_profile?.value || defaults?.model_profile || "anima");
}
function saveCurrentProfileDraft() {
  if (!ui.model_profile) return;
  profileDrafts[currentProfile()] = {
    ...clone(profileDraftFromDraft()),
    kreaPromptPoolEnabled: Boolean(currentProfile() === "krea2" && ui.krea_prompt_pool_enabled?.checked),
    kreaPromptPoolSelection: [...kreaPromptPoolSelection],
    extra_prompt: ui.extra_prompt?.value || "",
    ...profileSamplingState(),
  };
}
function profileSamplingState() {
  return {
    model_name: ui.model_name?.value || "",
    base_mode: ui.base_mode?.value || "normal",
    dual: typeof readRandomDualControls === "function" ? readRandomDualControls() : null,
    steps: Number(ui.steps?.value || 0),
    cfg: Number(ui.cfg?.value || 0),
    sampler_name: ui.sampler_name?.value || "",
    scheduler: ui.scheduler?.value || "",
    upscalers: readUpscalerControls(),
  };
}
function profileDraftFromDraft() {
  return {
    modes: draft.modes || {}, counts: draft.counts || {}, fixed: draft.fixed || {}, pools: draft.pools || defaultPools(),
    loras: draft.loras || [], managedTriggers: draft.managedTriggers || [],
  };
}
function restoreProfileDraft(profile, settings = {}, { preferStored = true } = {}) {
  const stored = profileDrafts[normalizeProfileId(profile)];
  const targetProfile = normalizeProfileId(profile);
  const sourceProfile = normalizeProfileId(settings?.model_profile || targetProfile);
  const profileDefault = profileSettingsDefaults(profile);
  const source = (preferStored ? stored : null) || (settings && settings.modes ? settings : profileStateFrom({ ...profileDefault, ...settings }));
  draft = {
    modes: clone(source.modes || {}), counts: clone(source.counts || {}), fixed: clone(source.fixed || {}),
    pools: clone(source.pools || defaultPools()), loras: clone(source.loras || []), managedTriggers: clone(source.managedTriggers || []),
  };
  if (!stored && sourceProfile !== targetProfile && normalizeProfileId(profile) === "krea2" && !defaults?.profile_settings?.krea2 && !defaults?.profiles?.krea2?.settings) {
    draft.pools = defaultPools();
    draft.loras = [];
    draft.managedTriggers = [];
    draft.modes = Object.fromEntries(SECTIONS.map((section) => [section, "off"]));
  }
  profileDrafts[normalizeProfileId(profile)] = {
    ...clone(profileDraftFromDraft()),
    kreaPromptPoolEnabled: Boolean((preferStored ? stored?.kreaPromptPoolEnabled : undefined) ?? settings.krea_prompt_pool?.enabled),
    kreaPromptPoolSelection: [...((preferStored ? stored?.kreaPromptPoolSelection : null) || settings.krea_prompt_pool?.ids || [])],
    extra_prompt: (preferStored ? stored?.extra_prompt : undefined) ?? settings.extra_prompt ?? "",
    model_name: (preferStored ? stored?.model_name : undefined) ?? (sourceProfile === targetProfile ? settings.model_name : null) ?? profileConfig(profile).model_name ?? "",
    base_mode: (preferStored ? stored?.base_mode : undefined) ?? settings.base_mode,
    dual: clone((preferStored ? stored?.dual : undefined) ?? settings.dual ?? null),
    steps: (preferStored ? stored?.steps : undefined) ?? settings.steps,
    cfg: (preferStored ? stored?.cfg : undefined) ?? settings.cfg,
    sampler_name: (preferStored ? stored?.sampler_name : undefined) ?? settings.sampler_name,
    scheduler: (preferStored ? stored?.scheduler : undefined) ?? settings.scheduler,
    upscalers: clone((preferStored ? stored?.upscalers : null) || settings.upscalers || defaults?.upscalers || defaultUpscalers()),
  };
}
function switchProfileDraft(nextProfile, { applyDefaults = true } = {}) {
  const next = normalizeProfileId(nextProfile);
  const previous = currentProfile();
  if (previous !== next) saveCurrentProfileDraft();
  activeProfileId = next;
  if (ui.model_profile) ui.model_profile.value = next;
  const state = profileDrafts[next];
  if (state) {
    restoreProfileDraft(next, state);
    kreaPromptPoolSelection = [...(state.kreaPromptPoolSelection || [])];
    ui.krea_prompt_pool_enabled.checked = Boolean(state.kreaPromptPoolEnabled);
    if (typeof state.extra_prompt === "string") ui.extra_prompt.value = state.extra_prompt;
  } else {
    const source = typeof randomWorkbenchDefaultSettings === "function" ? randomWorkbenchDefaultSettings(next) : profileSettingsDefaults(next);
    restoreProfileDraft(next, source);
    ui.extra_prompt.value = source.extra_prompt || "";
  }
  const selectedState = profileDrafts[next] || profileSettingsDefaults(next);
  ensureSelectValue(ui.model_name, selectedState.model_name || profileConfig(next).model_name || "");
  if (applyDefaults) {
    const values = profileDefaults(next);
    const profileSettings = selectedState;
    ui.base_mode.value = profileSettings.base_mode || values.base_mode || "normal";
    for (const field of ["steps", "cfg", "sampler_name", "scheduler"]) {
      const value = profileSettings[field] ?? values[field];
      if (value !== undefined && ui[field]) setControl(field, value);
    }
    const repair = profileSettings.upscalers || profileSettingsDefaults(next).upscalers || defaults?.upscalers || defaultUpscalers();
    const normalized = normalizeUpscalers(repair);
    normalized.seedvr2.model_name = normalized.seedvr2.model_name || profileConfig(next).seedvr2_model || "";
    normalized.seedvr2.vae_name = normalized.seedvr2.vae_name || profileConfig(next).seedvr2_vae || "";
    setUpscalerControls(normalized);
    if (typeof setRandomDualControls === "function") setRandomDualControls(profileSettings);
  }
  updateProfileResources();
  renderDimensions();
  renderLoras();
  updatePoolOverview();
  renderKreaPromptPool();
  updateRepairControls();
  schedulePersist();
}
function normalizeSettings(raw) {
  const merged = { ...defaults, ...raw };
  const legacyModelProfile = /krea2/i.test(String(raw?.model_name || "")) ? "krea2" : null;
  merged.model_profile = normalizeProfileId(raw?.model_profile ?? legacyModelProfile ?? defaults?.model_profile ?? "anima");
  const settingsProfile = normalizeProfileId(raw?.model_profile ?? defaults?.model_profile ?? "anima");
  merged.model_name = String(raw?.model_name ?? (settingsProfile === merged.model_profile ? defaults?.model_name : null) ?? profileConfig(merged.model_profile).model_name ?? "");
  const kreaRaw = raw?.krea_prompt_pool || defaults?.krea_prompt_pool || {};
  merged.krea_prompt_pool = {
    enabled: Boolean(kreaRaw.enabled),
    ids: Array.isArray(kreaRaw.ids) ? [...new Set(kreaRaw.ids.map((value) => String(value)))] : [],
  };
  merged.base_mode = raw?.base_mode === "dual_sampler"
    ? "dual_sampler"
    : raw?.base_mode === "normal"
      ? "normal"
      : raw?.second_sampler?.enabled
        ? "dual_sampler"
        : (defaults?.base_mode === "dual_sampler" ? "dual_sampler" : "normal");
  const hasPools = raw && raw.pools && typeof raw.pools === "object";
  merged.pools = hasPools ? clone(raw.pools) : defaultPools();
  for (const section of SECTIONS) {
    if (!merged.pools[section]) merged.pools[section] = { mode: "include", ids: [], excluded_ids: [] };
    if (!hasPools && raw?.[`random_${section}`]) merged.pools[section].mode = "all";
  }
  const hasLoras = raw && Object.prototype.hasOwnProperty.call(raw, "loras");
  merged.loras = clone(hasLoras ? raw.loras || [] : defaultLorasForProfile(merged.model_profile));
  merged.lora_managed_triggers = normalizeTriggerWords(raw?.lora_managed_triggers || defaults?.lora_managed_triggers || []);
  // 旧设置仅用于读取迁移；提交时 readSettings 只会序列化 upscalers。
  const oldHires = raw?.hires || null;
  const defaultRepair = normalizeUpscalers(defaults?.upscalers, defaultUpscalers());
  const migratedRepair = oldHires
    // v1 applied the percentage after a nominal 4x pixel model.  A direct
    // latent multiplier therefore preserves the resulting geometry as
    // percent / 100 * 4, matching the server migration contract.
    ? { ...defaultRepair, latent: { ...defaultRepair.latent, enabled: Boolean(oldHires.enabled), scale_by: Number(oldHires.percent || 45) / 100 * 4 } }
    : defaultRepair;
  merged.upscalers = normalizeUpscalers(raw?.upscalers, migratedRepair);
  if (raw?.detailers?.face && !raw?.upscalers?.face_refine) merged.upscalers.face_refine.enabled = true;
  const profile = profileConfig(merged.model_profile);
  if (!merged.upscalers.seedvr2.model_name) merged.upscalers.seedvr2.model_name = profile.seedvr2_model || "";
  if (!merged.upscalers.seedvr2.vae_name) merged.upscalers.seedvr2.vae_name = profile.seedvr2_vae || "";
  merged.manual_artist = canonicalArtists(merged.manual_artist);
  return merged;
}

const UPSCALER_CONTROL_FIELDS = {
  latent: ["enabled", "scale_by", "upscale_method"],
  seedvr2: ["enabled", "model_name", "vae_name", "weight_dtype", "scale_by", "color_correction_method"],
  nvidia: ["enabled", "resize_type", "scale", "quality"],
  face_refine: ["enabled", "steps", "cfg_scale", "sampler", "scheduler", "denoise_strength", "face_selection", "detection_confidence", "manual_rotation", "processing_resolution", "upscaler_model", "crop_padding", "blend_softness", "mask_expansion", "sampling_mask_blur_size", "sampling_mask_blur_strength", "face_positive_prompt", "replace_positive_prompt", "face_negative_prompt", "replace_negative_prompt", "exclusions", "enable_pre_upscale", "enable_color_correction", "enable_segmentation", "enable_differential_diffusion", "enable_lightness_rescue", "enable_final_refinement", "offload_models_to_cpu"],
};
const UPSCALER_CONTROL_IDS = {
  latent: { enabled: "latent_upscale_enabled", scale_by: "latent_upscale_scale", upscale_method: "latent_upscale_method" },
  seedvr2: { enabled: "seedvr2_enabled", model_name: "seedvr2_model_name", vae_name: "seedvr2_vae_name", weight_dtype: "seedvr2_weight_dtype", scale_by: "seedvr2_scale_by", color_correction_method: "seedvr2_color_correction_method" },
  nvidia: { enabled: "nvidia_upscale_enabled", resize_type: "nvidia_resize_type", scale: "nvidia_scale", quality: "nvidia_quality" },
  face_refine: { enabled: "face_refine_enabled", steps: "face_steps", cfg_scale: "face_cfg_scale", sampler: "face_sampler", scheduler: "face_scheduler", denoise_strength: "face_denoise_strength", face_selection: "face_selection", detection_confidence: "face_detection_confidence", manual_rotation: "face_manual_rotation", processing_resolution: "face_processing_resolution", upscaler_model: "face_upscaler_model", crop_padding: "face_crop_padding", blend_softness: "face_blend_softness", mask_expansion: "face_mask_expansion", sampling_mask_blur_size: "face_sampling_mask_blur_size", sampling_mask_blur_strength: "face_sampling_mask_blur_strength", face_positive_prompt: "face_positive_prompt", replace_positive_prompt: "face_replace_positive_prompt", face_negative_prompt: "face_negative_prompt", replace_negative_prompt: "face_replace_negative_prompt", exclusions: "face_exclusions", enable_pre_upscale: "face_enable_pre_upscale", enable_color_correction: "face_enable_color_correction", enable_segmentation: "face_enable_segmentation", enable_differential_diffusion: "face_enable_differential_diffusion", enable_lightness_rescue: "face_enable_lightness_rescue", enable_final_refinement: "face_enable_final_refinement", offload_models_to_cpu: "face_offload_models_to_cpu" },
};
const POST_SAMPLER_IDS = { steps: "latent_second_sampler_steps", cfg: "latent_second_sampler_cfg", sampler_name: "latent_second_sampler_name", scheduler: "latent_second_scheduler", denoise: "latent_second_sampler_denoise" };
const SEED_SAMPLER_IDS = { steps: "seedvr2_sampler_steps", cfg: "seedvr2_sampler_cfg", sampler_name: "seedvr2_sampler_name", scheduler: "seedvr2_scheduler", denoise: "seedvr2_sampler_denoise" };
const SEED_TILE_IDS = { encode: { tile_size: "seedvr2_encode_tile_size", overlap: "seedvr2_encode_overlap", temporal_size: "seedvr2_encode_temporal_size", temporal_overlap: "seedvr2_encode_temporal_overlap" }, decode: { tile_size: "seedvr2_decode_tile_size", overlap: "seedvr2_decode_overlap", temporal_size: "seedvr2_decode_temporal_size", temporal_overlap: "seedvr2_decode_temporal_overlap" } };

function controlValue(id, fallback = "") {
  const element = ui[id];
  if (!element) return fallback;
  if (element.type === "checkbox") return Boolean(element.checked);
  if (element.type === "number") return Number(element.value);
  return element.value;
}
function setControl(id, value) {
  const element = ui[id];
  if (!element || value === undefined || value === null) return;
  if (element.type === "checkbox") element.checked = Boolean(value);
  else if (element.tagName === "SELECT") ensureSelectValue(element, String(value));
  else element.value = value;
}
function setSamplerValues(ids, values) { for (const [key, id] of Object.entries(ids)) setControl(id, values?.[key]); }
function readSamplerValues(ids) { return Object.fromEntries(Object.entries(ids).map(([key, id]) => [key, controlValue(id)])); }
function setTileValues(values) { for (const [group, ids] of Object.entries(SEED_TILE_IDS)) for (const [key, id] of Object.entries(ids)) setControl(id, values?.[group]?.[key]); }
function readTileValues() { return Object.fromEntries(Object.entries(SEED_TILE_IDS).map(([group, ids]) => [group, Object.fromEntries(Object.entries(ids).map(([key, id]) => [key, controlValue(id)]))])); }
function setUpscalerControls(upscalers) {
  if (typeof sharedParameterState !== "undefined") sharedParameterState.latent = {
    model_format: upscalers?.latent?.model_format || "Wan 2.1", half_precision: upscalers?.latent?.half_precision ?? true,
  };
  for (const [stage, fields] of Object.entries(UPSCALER_CONTROL_FIELDS)) for (const field of fields) setControl(UPSCALER_CONTROL_IDS[stage][field], upscalers?.[stage]?.[field]);
  setSamplerValues(POST_SAMPLER_IDS, upscalers?.latent?.second_sampler);
  setControl("latent_second_sampler_enabled", upscalers?.latent?.second_sampler?.enabled);
  setSamplerValues(SEED_SAMPLER_IDS, upscalers?.seedvr2?.sampler);
  setTileValues(upscalers?.seedvr2);
  applyNodeResourceControls();
}
function readUpscalerControls() {
  const result = {};
  for (const [stage, fields] of Object.entries(UPSCALER_CONTROL_FIELDS)) result[stage] = Object.fromEntries(fields.map((field) => [field, controlValue(UPSCALER_CONTROL_IDS[stage][field])]));
  result.latent.second_sampler = { enabled: controlValue("latent_second_sampler_enabled"), ...readSamplerValues(POST_SAMPLER_IDS) };
  if (typeof sharedParameterState !== "undefined") Object.assign(result.latent, sharedParameterState.latent);
  result.seedvr2.sampler = readSamplerValues(SEED_SAMPLER_IDS);
  Object.assign(result.seedvr2, readTileValues());
  return result;
}

function refreshProfileData() {
  const section = activeSection;
  ++poolRequest;
  return Promise.allSettled([
    loadResources(),
    loadLoraInventory(),
    loadStylePresets(),
    loadCustomGroups(section).then(() => loadPool()),
  ]);
}

function applySettings(raw, { useProfileDraft = false } = {}) {
  invalidatePromptNormalization();
  clearNormalizationUndo();
  const settings = normalizeSettings(raw || defaults || {});
  const previousProfile = currentProfile();
  activeProfileId = settings.model_profile;
  if (ui.model_profile) ui.model_profile.value = settings.model_profile;
  ui.krea_prompt_pool_enabled.checked = Boolean(settings.krea_prompt_pool?.enabled);
  kreaPromptPoolSelection = [...(settings.krea_prompt_pool?.ids || [])];
  ensureSelectValue(ui.model_name, settings.model_name);
  ensureSelectValue(ui.base_mode, settings.base_mode);
  for (const [name, value] of Object.entries(settings)) {
    const element = ui[name];
    if (element && !["object", "undefined"].includes(typeof value)) element.value = value;
  }
  ensureSelectValue(ui.sampler_name, settings.sampler_name);
  ensureSelectValue(ui.scheduler, settings.scheduler);
  const upscalers = normalizeUpscalers(settings.upscalers);
  setUpscalerControls(upscalers);
  if (typeof setRandomDualControls === "function") setRandomDualControls(settings);
  restoreProfileDraft(settings.model_profile, useProfileDraft ? (profileDrafts[settings.model_profile] || settings) : settings, { preferStored: useProfileDraft });
  const storedProfileState = profileDrafts[settings.model_profile];
  if (useProfileDraft && storedProfileState) {
    ui.krea_prompt_pool_enabled.checked = Boolean(storedProfileState.kreaPromptPoolEnabled);
    kreaPromptPoolSelection = [...(storedProfileState.kreaPromptPoolSelection || [])];
  }
  const storedExtraPrompt = useProfileDraft ? storedProfileState?.extra_prompt : null;
  if (typeof storedExtraPrompt === "string") ui.extra_prompt.value = storedExtraPrompt;
  else if (typeof settings.extra_prompt === "string") ui.extra_prompt.value = settings.extra_prompt;
  if (loraInventoryLoaded) reconcileLoraTriggers(draft.loras.filter((item) => item.enabled));
  renderDimensions();
  renderLoras();
  renderKreaPromptPool();
  updatePeopleTotal();
  updatePoolOverview();
  updateRepairControls();
  if (typeof singleWorkbenchApply === "function") singleWorkbenchApply(raw || {});
  if (initialized && previousProfile !== settings.model_profile) void refreshProfileData();
}

function readSettings() {
  saveCurrentProfileDraft();
  const normalizedArtist = canonicalArtists(ui.manual_artist.value);
  if (ui.manual_artist.value !== normalizedArtist) ui.manual_artist.value = normalizedArtist;
  const settings = {
    count: Number(ui.count.value), female_count: Number(ui.female_count.value), male_count: Number(ui.male_count.value),
    character_detail: ui.character_detail.value, manual_artist: normalizedArtist, quality_prompt: ui.quality_prompt.value,
    extra_prompt: ui.extra_prompt.value, negative_prompt: ui.negative_prompt.value, width: Number(ui.width.value), height: Number(ui.height.value),
    steps: Number(ui.steps.value), cfg: Number(ui.cfg.value), sampler_name: ui.sampler_name.value, scheduler: ui.scheduler.value,
    model_profile: currentProfile(), model_name: ui.model_name.value,
    krea_prompt_pool: {
      enabled: currentProfile() === "krea2" && ui.krea_prompt_pool_enabled.checked,
      ids: currentProfile() === "krea2" ? [...(readKreaPoolSelection().ids || [])] : [],
    },
    base_mode: ui.base_mode.value === "dual_sampler" ? "dual_sampler" : "normal",
    dual: typeof readRandomDualControls === "function" ? readRandomDualControls() : null,
    pools: clone(draft.pools), loras: clone(draft.loras), lora_managed_triggers: clone(draft.managedTriggers || []),
    upscalers: readUpscalerControls(),
  };
  for (const section of SECTIONS) {
    settings[`random_${section}`] = draft.modes[section] === "pool";
    settings[`random_${section}_count`] = Number(draft.counts[section] || 1);
    settings[`fixed_${section}`] = draft.modes[section] === "fixed" ? draft.fixed[section] || "" : "";
  }
  return typeof singleWorkbenchRead === "function" ? singleWorkbenchRead(settings) : settings;
}

function formatSavedTime(timestamp) {
  try {
    return new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(
      new Date(timestamp),
    );
  } catch {
    return "刚刚";
  }
}

function persistNow() {
  if (!initialized) return;
  if (typeof singleWorkbenchPersist === "function" && singleWorkbenchPersist()) return;
  const savedAt = new Date().toISOString();
  try {
    localStorage.setItem(DRAFT_KEY, JSON.stringify({ version: 4, savedAt, settings: readSettings(), profileDrafts: clone(profileDrafts) }));
    for (const key of LEGACY_DRAFT_KEYS) localStorage.removeItem(key);
    localStorage.setItem(VIEW_KEY, JSON.stringify({ version: 1, activeSection, views: poolViews }));
    ui.draftStatus.textContent = `本地草稿已保存 ${formatSavedTime(savedAt)}`;
  } catch {
    ui.draftStatus.textContent = "浏览器未允许保存草稿";
  }
}

function schedulePersist() {
  clearTimeout(persistTimer);
  persistTimer = setTimeout(persistNow, 180);
}

function loadStoredView() {
  try {
    const payload = JSON.parse(localStorage.getItem(VIEW_KEY) || "null");
    if (payload?.version !== 1 || typeof payload.views !== "object") return;
    for (const section of SECTIONS)
      poolViews[section] = {
        ...defaultView(),
        ...(payload.views[section] || {}),
        filters: { ...defaultView().filters, ...(payload.views[section]?.filters || {}) },
      };
    if (SECTIONS.includes(payload.activeSection)) activeSection = payload.activeSection;
  } catch {
    localStorage.removeItem(VIEW_KEY);
  }
}

function loadStoredDraft() {
  try {
    const current = JSON.parse(localStorage.getItem(DRAFT_KEY) || "null");
    const legacy = current
      ? null
      : LEGACY_DRAFT_KEYS.map((key) => JSON.parse(localStorage.getItem(key) || "null")).find(Boolean);
    const payload = current || legacy;
    if (![1, 2, 3, 4].includes(payload?.version) || !payload.settings || typeof payload.settings !== "object") return false;
    const settings = clone(payload.settings);
    if (payload.version < 4) {
      try {
        localStorage.setItem(`${DRAFT_BACKUP_KEY_PREFIX}${payload.version}`, JSON.stringify(payload));
      } catch {
        // Keep startup available if browser storage is full; the old source key remains until migration writes.
      }
    }
    if (payload.version === 1 && isLegacyDefaultLoras(settings.loras)) settings.loras = [];
    profileDrafts = payload.profileDrafts && typeof payload.profileDrafts === "object" ? clone(payload.profileDrafts) : {};
    applySettings(settings, { useProfileDraft: true });
    if (payload.version !== 4) {
      localStorage.setItem(DRAFT_KEY, JSON.stringify({ version: 4, savedAt: payload.savedAt, settings, profileDrafts: clone(profileDrafts) }));
      for (const key of LEGACY_DRAFT_KEYS) localStorage.removeItem(key);
    }
    ui.draftStatus.textContent = `已恢复草稿 ${formatSavedTime(payload.savedAt)}`;
    return true;
  } catch {
    localStorage.removeItem(DRAFT_KEY);
    for (const key of LEGACY_DRAFT_KEYS) localStorage.removeItem(key);
    return false;
  }
}

function isLegacyDefaultLoras(value) {
  if (!Array.isArray(value) || value.length !== LEGACY_DEFAULT_LORAS.length) return false;
  return value.every((item, index) => {
    const expected = LEGACY_DEFAULT_LORAS[index];
    return (
      normalizedPath(item?.filename) === normalizedPath(expected.filename) &&
      Boolean(item?.enabled) === expected.enabled &&
      Number(item?.strength) === expected.strength
    );
  });
}

function selectedCount(section) {
  const selection = draft.pools[section] || { mode: "include", ids: [], excluded_ids: [] };
  return selection.mode === "all"
    ? Math.max(0, Number(config.catalog?.counts?.[section] || 0) - selection.excluded_ids.length)
    : selection.ids.length;
}

function renderDimensions() {
  ui.dimensionList.replaceChildren(
    ...SECTIONS.map((section) => {
      const meta = SECTION_META[section];
      const mode = draft.modes[section] || "off";
      const count = draft.counts[section] || 1;
      const selected = selectedCount(section);
      const article = document.createElement("article");
      article.className = `dimension-row mode-${mode}`;
      article.dataset.section = section;
      article.innerHTML = `<div class="dimension-heading"><div><span class="dimension-icon">${meta.label.slice(0, 1)}</span><strong>${meta.label}</strong><small>${selected} 项已选</small></div><button class="manage-pool" type="button" data-manage="${section}">管理池 <span aria-hidden="true">→</span></button></div>
      <div class="dimension-mode" role="group" aria-label="${meta.label}模式"><button type="button" data-mode="off" class="${mode === "off" ? "active" : ""}">关闭</button><button type="button" data-mode="pool" class="${mode === "pool" ? "active" : ""}">随机池</button><button type="button" data-mode="fixed" class="${mode === "fixed" ? "active" : ""}">固定</button></div>
      <div class="dimension-body"><label class="draw-count"><span>抽取数量</span><input type="number" min="1" max="${section === "expression" ? 1 : 5}" step="1" value="${section === "expression" ? 1 : count}" data-count="${section}" ${mode !== "pool" || section === "expression" ? "disabled" : ""}></label><span class="mode-note">${mode === "pool" ? `${selected} 项候选` : mode === "fixed" ? "使用固定提示词" : "不加入提示词"}</span></div>
      <label class="field dimension-fixed"><span>固定提示词</span><input type="text" value="${escapeHtml(draft.fixed[section] || "")}" data-fixed="${section}" placeholder="留空则不加入" ${mode !== "fixed" ? "disabled" : ""}></label>`;
      article.querySelectorAll("[data-mode]").forEach((button) =>
        button.addEventListener("click", () => {
          draft.modes[section] = button.dataset.mode;
          renderDimensions();
          schedulePersist();
        }),
      );
      article.querySelector("[data-count]").addEventListener("input", (event) => {
        draft.counts[section] = Math.max(1, Math.min(5, Number(event.target.value || 1)));
        schedulePersist();
        updatePoseConflict();
      });
      const fixedInput = article.querySelector("[data-fixed]");
      fixedInput.addEventListener("input", (event) => {
        draft.fixed[section] = event.target.value;
        schedulePersist();
      });
      fixedInput.addEventListener("blur", () => normalizePromptElement(fixedInput));
      article.querySelector("[data-manage]").addEventListener("click", () => openPool(section));
      return article;
    }),
  );
}

function updatePeopleTotal() {
  const total = Number(ui.female_count.value || 0) + Number(ui.male_count.value || 0);
  ui.peopleTotal.textContent = total ? `共 ${total} 人` : "不限人数";
}
function updatePoolOverview() {
  ui.poolOverview.textContent = currentProfile() === "krea2"
    ? `${kreaPromptPool.count || 0} 项词条 · ${kreaPromptPoolSelection.length} 项已选`
    : `${SECTIONS.reduce((sum, section) => sum + selectedCount(section), 0)} 项已选`;
}

function normalizedPath(value) {
  return String(value || "")
    .replaceAll("\\", "/")
    .toLowerCase();
}
function updateFavoriteSelectionButton() {
  const count = selectedCount(activeSection);
  ui.favoriteSelection.textContent = count ? `收藏本次选择 · ${count}` : "收藏本次选择";
  ui.favoriteSelection.disabled = !favoritesAvailable || count === 0;
}
function normalizeTriggerWords(values) {
  const result = [];
  const seen = new Set();
  for (const value of Array.isArray(values) ? values : []) {
    const word = String(value || "").trim();
    const identity = word.toLocaleLowerCase();
    if (word && !seen.has(identity)) {
      result.push(word);
      seen.add(identity);
    }
  }
  return result;
}
function promptTokens(value) {
  return String(value || "")
    .replaceAll("\r", ",")
    .replaceAll("\n", ",")
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}
function triggerIdentity(value) {
  return String(value || "").trim().toLocaleLowerCase();
}
function forgetRemovedManagedTriggers() {
  const present = new Set(promptTokens(ui.extra_prompt.value).map(triggerIdentity));
  draft.managedTriggers = normalizeTriggerWords(
    (draft.managedTriggers || []).filter((word) => present.has(triggerIdentity(word))),
  );
}
function reconcileLoraTriggers(addFor = []) {
  if (typeof singleWorkbenchActive === "function" && singleWorkbenchActive()) return;
  if (!loraInventoryLoaded) return;
  const enabledItems = draft.loras.filter((item) => item.enabled);
  const metadataUnknown = enabledItems.some((item) => {
    const inventoryItem = loraItem(item.filename);
    return !inventoryItem || inventoryItem.trigger_metadata_available === false;
  });
  const current = promptTokens(ui.extra_prompt.value);
  const currentIds = new Set(current.map(triggerIdentity));
  const managed = new Map((draft.managedTriggers || []).map((word) => [triggerIdentity(word), word]));
  const required = new Set();
  for (const item of enabledItems) {
    const inventoryItem = loraItem(item.filename);
    for (const word of normalizeTriggerWords(inventoryItem?.trigger_words || [])) required.add(triggerIdentity(word));
  }
  for (const item of addFor) {
    if (!item.enabled) continue;
    const inventoryItem = loraItem(item.filename);
    for (const word of normalizeTriggerWords(inventoryItem?.trigger_words || [])) {
      const identity = triggerIdentity(word);
      required.add(identity);
      if (!currentIds.has(identity)) {
        current.push(word);
        currentIds.add(identity);
        managed.set(identity, word);
      }
    }
  }
  const keptManaged = [];
  for (const [identity, word] of managed) {
    if (required.has(identity) || metadataUnknown) keptManaged.push(word);
  }
  if (!metadataUnknown) {
    const removed = new Set(
      [...managed.keys()].filter((identity) => !required.has(identity)),
    );
    if (removed.size) {
      for (let index = current.length - 1; index >= 0; index--) {
        if (removed.has(triggerIdentity(current[index]))) current.splice(index, 1);
      }
    }
  }
  draft.managedTriggers = normalizeTriggerWords(keptManaged);
  const nextPrompt = current.join(", ");
  if (ui.extra_prompt.value !== nextPrompt) ui.extra_prompt.value = nextPrompt;
}
function loraItem(filename) {
  if (loraInventoryProfile !== currentProfile()) return null;
  const identity = normalizedPath(filename);
  const candidates = loraInventory.filter((item) => item.model_profile === currentProfile() && item.classification_status === "recognized");
  return candidates.find((item) => normalizedPath(item.filename) === identity) || null;
}
function classificationItem(items, filename) {
  const identity = normalizedPath(filename);
  const matches = (items || []).filter((item) => normalizedPath(item.name || item.filename) === identity);
  if (matches.length === 1) return matches[0];
  if (identity.includes("/")) return null;
  const basenameMatches = (items || []).filter((item) => normalizedPath(item.name || item.filename).split("/").pop() === identity);
  return basenameMatches.length === 1 ? basenameMatches[0] : null;
}
function classificationReason(item, fallback) {
  if (item?.model_profile && item.model_profile !== currentProfile()) {
    return `LoRA Manager 识别为 ${profileLabel(item.model_profile)}，与当前 ${profileLabel(currentProfile())} 档案不匹配`;
  }
  return item?.classification_reason || fallback;
}
function loraSelectionIssue(filename) {
  if (!loraInventoryLoaded || loraInventoryProfile !== currentProfile()) return loraInventoryError || "正在读取 LoRA Manager 分类";
  if (loraItem(filename)) return "";
  return classificationReason(classificationItem(loraClassificationItems, filename), "文件不在当前 ComfyUI LoRA 清单中");
}
function modelSelectionIssue(filename = ui.model_name?.value || "") {
  if (!resourcesLoaded || resourceInventoryProfile !== currentProfile()) return resourceError || "正在读取 LoRA Manager 分类";
  if (resources.models.includes(filename)) return "";
  if (!normalizedPath(filename).includes("/")) {
    const names = (resources.model_items || []).map((item) => item.name || item.filename);
    const exact = names.some((name) => normalizedPath(name) === normalizedPath(filename));
    if (!exact && names.filter((name) => normalizedPath(name).split("/").pop() === normalizedPath(filename)).length > 1) return "存在多个同名主模型，请选择完整路径";
  }
  return classificationReason(classificationItem(resources.model_items, filename), filename ? "文件不在当前 ComfyUI 主模型清单中" : "没有识别为当前档案的主模型");
}
function generationClassificationIssues(settings = null) {
  const issues = [];
  const modelName = settings?.model_name ?? ui.model_name?.value ?? "";
  const modelIssue = modelSelectionIssue(modelName);
  if (modelIssue) issues.push(`主模型：${modelIssue}`);
  if (!loraInventoryLoaded || loraInventoryProfile !== currentProfile()) issues.push(loraInventoryError || "正在读取 LoRA Manager 分类");
  else for (const item of [...(settings?.loras || draft.loras || []), ...(settings?.single?.loras_secondary || (typeof singleWorkbenchActive === "function" && singleWorkbenchActive() ? singleWorkbenchState.single.loras_secondary || [] : []))]) {
    if (!item.enabled) continue;
    const reason = loraSelectionIssue(item.filename);
    if (reason) issues.push(`${item.filename}：${reason}`);
  }
  return [...new Set(issues)];
}
function updateGenerationAvailability() {
  if (!ui.startButton) return;
  const issues = generationClassificationIssues();
  if (typeof singleWorkbenchInputBlocked === "function" && singleWorkbenchInputBlocked()) issues.push("参考图正在读取或不可用，请等待、重新添加或移除");
  const storage = config.storage_status?.history;
  ui.startButton.disabled = submitInFlight || !ui.connection.classList.contains("online") || issues.length > 0 || Boolean(storage?.read_only);
  ui.startButton.title = storage?.read_only ? storage.reason || "历史存储已受保护" : issues.join("；");
}
function loraPresentation(inventoryItem, fallback) {
  const path = String(inventoryItem?.normalized_path || fallback || "").replaceAll("\\", "/");
  const parts = path.split("/").filter(Boolean);
  const basename = inventoryItem?.basename || parts.pop() || "未命名 LoRA";
  const folder = inventoryItem?.folder || parts.join("/") || "根目录";
  return { path, basename, folder, label: `${folder} / ${basename}` };
}
function renderLoras() {
  const loras = Array.isArray(draft.loras) ? draft.loras : [];
  const enabled = loras.filter((item) => item.enabled).length;
  ui.loraSummary.textContent = `${enabled} 启用 / ${loras.length} 配置`;
  ui.loraEmpty.hidden = loras.length > 0;
  ui.loraList.replaceChildren(
    ...loras.map((item, index) => {
      const selectionIssue = loraSelectionIssue(item.filename);
      const available = !selectionIssue;
      const high = Math.abs(Number(item.strength)) > 2;
      const row = document.createElement("div");
      row.className = `lora-row${available ? "" : " missing"}`;
      const inventoryItem = loraItem(item.filename);
      const display = loraPresentation(inventoryItem, item.filename);
      const triggers = normalizeTriggerWords(inventoryItem?.trigger_words || []);
      const triggerLabel = inventoryItem?.trigger_override ? "触发词（已覆盖）" : "触发词";
      row.innerHTML = `<input class="lora-toggle" type="checkbox" ${item.enabled ? "checked" : ""} title="启用 LoRA" aria-label="启用 ${escapeHtml(item.filename)}"><div class="lora-name"><strong title="${escapeHtml(display.path)}">${escapeHtml(display.label)}</strong><small class="${available && !high ? "" : "lora-warning"}">${available ? (high ? "高强度 · 模型 + CLIP" : "模型 + CLIP") : escapeHtml(selectionIssue)}</small>${triggers.length || inventoryItem?.trigger_override ? `<small class="lora-trigger-line" title="自动触发词">${triggerLabel}：${triggers.length ? escapeHtml(triggers.join(", ")) : "已禁用"}</small>` : ""}</div><input class="lora-strength" type="number" min="-100" max="100" step="0.05" value="${Number(item.strength)}" title="LoRA strength" aria-label="${escapeHtml(item.filename)} 强度"><div class="lora-actions"><button class="button ghost compact lora-trigger-edit" type="button" data-lora-triggers ${inventoryItem ? "" : "disabled"}>触发词</button><button class="icon-button dark" type="button" data-lora-up title="上移" aria-label="上移">↑</button><button class="icon-button dark" type="button" data-lora-down title="下移" aria-label="下移">↓</button><button class="icon-button dark" type="button" data-lora-remove title="移除" aria-label="移除">×</button></div>`;
      row.querySelector(".lora-toggle").addEventListener("change", (event) => {
        item.enabled = event.target.checked;
        reconcileLoraTriggers(item.enabled ? [item] : []);
        renderLoras();
        schedulePersist();
      });
      row.querySelector(".lora-strength").addEventListener("change", (event) => {
        const value = Number(event.target.value);
        if (!Number.isFinite(value) || value < -100 || value > 100) {
          toast("LoRA strength 必须在 -100 到 100 之间");
          renderLoras();
          return;
        }
        item.strength = Math.round(value * 100) / 100;
        renderLoras();
        schedulePersist();
      });
      row.querySelector("[data-lora-up]").disabled = index === 0;
      row.querySelector("[data-lora-down]").disabled = index === loras.length - 1;
      row.querySelector("[data-lora-up]").addEventListener("click", () => moveLora(index, -1));
      row.querySelector("[data-lora-down]").addEventListener("click", () => moveLora(index, 1));
      row.querySelector("[data-lora-triggers]")?.addEventListener("click", () => openLoraTriggerDialog(item));
      row.querySelector("[data-lora-remove]").addEventListener("click", () => {
        draft.loras.splice(index, 1);
        reconcileLoraTriggers();
        renderLoras();
        renderLoraCatalog();
        schedulePersist();
      });
      return row;
    }),
  );
  updateGenerationAvailability();
}
function moveLora(index, delta) {
  const next = index + delta;
  if (next < 0 || next >= draft.loras.length) return;
  [draft.loras[index], draft.loras[next]] = [draft.loras[next], draft.loras[index]];
  renderLoras();
  schedulePersist();
}
async function loadLoraInventory({ refresh = false } = {}) {
  const requestedProfile = currentProfile();
  const requestId = ++loraInventoryRequest;
  loraInventoryLoaded = false;
  loraInventoryError = "";
  if (loraInventoryProfile !== requestedProfile) {
    loraInventoryProfile = requestedProfile;
    loraInventory = [];
    loraClassificationItems = [];
  }
  renderLoras();
  renderLoraCatalog();
  try {
    const data = await request(`/api/loras?model_profile=${encodeURIComponent(requestedProfile)}${refresh ? "&refresh=1" : ""}`);
    if (requestId !== loraInventoryRequest || requestedProfile !== currentProfile()) return;
    loraInventory = data.items || [];
    loraClassificationItems = Array.isArray(data.classification_items) ? data.classification_items : loraInventory;
    loraInventoryLoaded = true;
    loraInventoryError = "";
    reconcileLoraTriggers(draft.loras.filter((item) => item.enabled));
    renderLoras();
    renderLoraCatalog();
  } catch (error) {
    if (requestId !== loraInventoryRequest || requestedProfile !== currentProfile()) return;
    loraInventory = [];
    loraClassificationItems = [];
    loraInventoryLoaded = false;
    loraInventoryError = error.message;
    renderLoras();
    renderLoraCatalog();
  }
  updateRepairControls();
  if (typeof singleWorkbenchResources === "function") singleWorkbenchResources();
}
function renderLoraCatalog() {
  if (!ui.loraCatalog) return;
  const query = String(ui.loraSearch.value || "")
    .trim()
    .toLowerCase();
  const configured = new Set(
    draft.loras.map((item) => normalizedPath(loraItem(item.filename)?.filename || item.filename)),
  );
  const items = loraInventory.filter(
    (item) =>
      !query || `${item.normalized_path || item.filename} ${item.display_name || ""} ${(item.trigger_words || []).join(" ")}`.toLowerCase().includes(query),
  );
  ui.loraCatalogEmpty.hidden = Boolean(loraInventoryError) || items.length > 0;
  ui.loraCatalog.replaceChildren(
    ...items.map((item) => {
      const configuredItem = configured.has(normalizedPath(item.filename));
      const display = loraPresentation(item, item.filename);
      const button = document.createElement("button");
      button.type = "button";
      button.className = "lora-catalog-item";
      button.disabled = configuredItem;
      const triggers = normalizeTriggerWords(item.trigger_words || []);
      const preview = item.preview
        ? `<img loading="lazy" decoding="async" src="${escapeHtml(item.preview)}" alt="" data-lora-preview>`
        : "";
      button.innerHTML = `<span class="lora-catalog-preview">${preview}<span class="lora-preview-placeholder" aria-hidden="true">LoRA</span></span><span class="lora-catalog-copy"><strong title="${escapeHtml(item.display_name || display.path)}">${escapeHtml(item.display_name || display.basename)}</strong><small title="${escapeHtml(display.path)}">${escapeHtml(display.folder)} / ${escapeHtml(display.basename)}${configuredItem ? " · 已配置" : ""}</small><span class="lora-catalog-triggers">${item.trigger_override ? "已覆盖 · " : ""}${triggers.length ? `触发词：${escapeHtml(triggers.join(", "))}` : item.trigger_override ? "已禁用触发词" : "无触发词"}</span></span><span class="lora-catalog-add" aria-hidden="true">${configuredItem ? "✓" : "+"}</span>`;
      button.querySelector("[data-lora-preview]")?.addEventListener("error", (event) => {
        event.currentTarget.remove();
      });
      button.addEventListener("click", () => {
        const lora = { filename: item.filename, enabled: true, strength: 1 };
        draft.loras.push(lora);
        reconcileLoraTriggers([lora]);
        renderLoras();
        renderLoraCatalog();
        schedulePersist();
      });
      return button;
    }),
  );
  ui.loraCatalogMeta.textContent = loraInventoryError || `${loraInventory.length} 个本地 LoRA`;
}
async function openLoraDialog() {
  ui.loraSearch.value = "";
  ui.loraDialog.showModal();
  await loadLoraInventory();
  renderLoraCatalog();
}
function parseLoraTriggerInput(value) {
  return normalizeTriggerWords(
    String(value || "")
      .replaceAll("\r", ",")
      .replaceAll("\n", ",")
      .replaceAll("，", ",")
      .split(","),
  );
}
function replaceLoraInventoryItem(value) {
  const identity = normalizedPath(value.filename);
  loraClassificationItems = loraClassificationItems.filter((item) => normalizedPath(item.name || item.filename) !== identity);
  loraClassificationItems.push(value);
  loraInventory = loraInventory.filter((item) => normalizedPath(item.filename) !== identity);
  if (value.model_profile === currentProfile() && value.classification_status === "recognized") loraInventory.push(value);
}
function openLoraTriggerDialog(configuredItem) {
  const inventoryItem = loraItem(configuredItem.filename);
  if (!inventoryItem) return;
  editingLoraTrigger = configuredItem;
  const display = loraPresentation(inventoryItem, configuredItem.filename);
  ui.loraTriggerTitle.textContent = `编辑 ${inventoryItem.display_name || display.basename}`;
  ui.loraTriggerFilename.value = inventoryItem.filename;
  ui.loraTriggerWords.value = normalizeTriggerWords(inventoryItem.trigger_words || []).join("\n");
  const source = normalizeTriggerWords(inventoryItem.source_trigger_words || []);
  ui.loraSourceTriggerWords.textContent = source.length ? source.join(", ") : "无";
  ui.resetLoraTriggers.disabled = !inventoryItem.trigger_override;
  ui.loraTriggerDialog.showModal();
}
async function saveLoraTriggers(event) {
  event.preventDefault();
  if (!editingLoraTrigger) return;
  try {
    const value = await request("/api/loras/triggers", {
      method: "PUT",
      body: JSON.stringify({
        filename: ui.loraTriggerFilename.value,
        model_profile: currentProfile(),
        triggerWords: parseLoraTriggerInput(ui.loraTriggerWords.value),
      }),
    });
    replaceLoraInventoryItem(value);
    reconcileLoraTriggers(editingLoraTrigger.enabled ? [editingLoraTrigger] : []);
    renderLoras();
    renderLoraCatalog();
    schedulePersist();
    ui.loraTriggerDialog.close();
    toast(value.trigger_words?.length ? "LoRA 触发词已保存" : "已禁用该 LoRA 的自动触发词");
  } catch (error) {
    toast(error.message);
  }
}
async function resetLoraTriggers() {
  if (!editingLoraTrigger) return;
  try {
    const filename = encodeURIComponent(ui.loraTriggerFilename.value);
    const value = await request(`/api/loras/triggers?filename=${filename}&model_profile=${encodeURIComponent(currentProfile())}`, { method: "DELETE" });
    replaceLoraInventoryItem(value);
    reconcileLoraTriggers(editingLoraTrigger.enabled ? [editingLoraTrigger] : []);
    renderLoras();
    renderLoraCatalog();
    schedulePersist();
    ui.loraTriggerDialog.close();
    toast("已恢复 LoRA Manager 原始触发词");
  } catch (error) {
    toast(error.message);
  }
}

let currentQueue = [];
let lastPreviewId = 0;
let submitInFlight = false;
function renderBatchPayload(payload) {
  renderBatch(payload.batch, payload.queue || []);
}
let queuePanelCloseTimer = null;
function setQueuePanelOpen(open) {
  if (!ui.queuePanel) return;
  clearTimeout(queuePanelCloseTimer);
  if (open) {
    ui.queuePanel.hidden = false;
    // Force a frame so the transition plays from the collapsed style.
    ui.queuePanel.getBoundingClientRect();
    ui.queuePanel.classList.add("open");
  } else {
    ui.queuePanel.classList.remove("open");
    queuePanelCloseTimer = setTimeout(() => { ui.queuePanel.hidden = true; }, 320);
  }
}

function renderQueue(queue) {
  currentQueue = queue;
  if (!ui.queueBadge || !ui.queuePanel) return;
  ui.queueBadge.hidden = queue.length === 0;
  ui.queueBadge.textContent = `队列 ${queue.length}`;
  if (queue.length === 0) {
    setQueuePanelOpen(false);
  }
  ui.queuePanel.replaceChildren(
    ...queue.map((entry) => {
      const row = document.createElement("div");
      row.className = "queue-row";
      const label = document.createElement("span");
      label.textContent = `${entry.position}. ${entry.count} 张 · ${entry.model_name}`;
      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "icon-button dark";
      remove.title = "移出队列";
      remove.setAttribute("aria-label", "移出队列");
      remove.textContent = "×";
      remove.addEventListener("click", async () => {
        try {
          const payload = await request(`/api/batches/queue/${entry.queue_id}`, { method: "DELETE" });
          renderQueue(payload.queue || []);
        } catch (error) {
          toast(error.message);
        }
      });
      row.append(label, remove);
      return row;
    }),
  );
}
function renderBatch(batch, queue = currentQueue) {
  currentBatch = batch;
  if (typeof singleWorkbenchBatch === "function") singleWorkbenchBatch(batch);
  renderQueue(queue);
  const active = Boolean(batch && ["running", "stopping"].includes(batch.status));
  const completed = batch?.completed || 0;
  const total = batch?.total || 0;
  const labels = {
    running: "正在生成",
    stopping: "完成当前张后停止",
    completed: "批次完成",
    stopped: "已停止",
    error: "执行失败",
  };
  ui.batchState.textContent = batch ? labels[batch.status] || batch.status : "等待任务";
  ui.progressCount.textContent = `${completed} / ${total}`;
  ui.progressFill.style.width = total ? `${Math.min(100, (completed / total) * 100)}%` : "0%";
  // 单张采样进度(来自 ComfyUI websocket,尽力而为;旧版页面缺控件时跳过)
  if (ui.sampleFill && ui.sampleProgress) {
    const progress = active ? batch.progress : null;
    const sampleTrack = ui.sampleFill.parentElement;
    if (progress && progress.max > 0) {
      ui.sampleProgress.hidden = false;
      ui.sampleProgress.textContent = `采样 ${progress.value}/${progress.max}`;
      sampleTrack.hidden = false;
      ui.sampleFill.style.width = `${Math.min(100, (progress.value / progress.max) * 100)}%`;
    } else {
      ui.sampleProgress.hidden = true;
      sampleTrack.hidden = true;
      ui.sampleFill.style.width = "0%";
    }
  }
  // 实时预览帧:preview_id 变化时刷新缩略图
  if (ui.livePreview) {
    if (active && batch.preview_id && batch.preview_id !== lastPreviewId) {
      lastPreviewId = batch.preview_id;
      ui.livePreview.src = `/api/batches/current/preview?v=${batch.preview_id}`;
      ui.livePreview.hidden = false;
    } else if (!active) {
      ui.livePreview.hidden = true;
      lastPreviewId = 0;
    }
  }
  ui.stopButton.disabled = !active || batch.status === "stopping";
  // 运行中仍可加入队列，但分类有效性与连接状态共用同一守卫。
  updateGenerationAvailability();
  ui.startButton.textContent = active ? "加入队列" : "开始生成";
  ui.batchError.hidden = !batch?.error;
  ui.batchError.textContent = batch?.error || "";
  if (batch && !active && lastTerminalBatch !== `${batch.id}:${batch.status}:${completed}`) {
    lastTerminalBatch = `${batch.id}:${batch.status}:${completed}`;
    if (batch.settings?.workbench === "single") {
      historyCache.clear();
      if (typeof singleWorkbenchActive === "function" && singleWorkbenchActive()) historyDirty = true;
      else loadHistory(historyPage);
    } else loadHistory(1);
  }
}
async function refreshConnection() {
  if (document.hidden) return;
  try {
    const status = await request("/api/status");
    ui.connection.className = `status-badge ${status.online ? "online" : "offline"}`;
    ui.connection.querySelector("span").textContent = status.online ? `ComfyUI ${status.version}` : "ComfyUI 离线";
    updateGenerationAvailability();
  } catch {
    ui.connection.className = "status-badge offline";
    ui.connection.querySelector("span").textContent = "ComfyUI 离线";
    updateGenerationAvailability();
  }
}
let pollErrorShown = false;
let pollIdleTicks = 0;
let pollBatchPending = false;
let pollBatchLast = 0;
async function pollBatch() {
  if (document.hidden || pollBatchPending) return;
  const batchActive = Boolean(currentBatch && ["running", "stopping"].includes(currentBatch.status));
  const singleActive = batchActive && currentBatch.settings?.workbench === "single" && typeof singleWorkbenchActive === "function" && singleWorkbenchActive();
  const interval = singleActive ? 400 : batchActive ? 1200 : 4800;
  if (pollIdleTicks && Date.now() - pollBatchLast < interval) return;
  pollIdleTicks++; pollBatchLast = Date.now(); pollBatchPending = true;
  const batchAtStart = currentBatch;
  try {
    const payload = await request("/api/batches/current");
    // A submit/stop response can replace the batch while this read is in
    // flight. Its newer state takes precedence over this older snapshot.
    if (currentBatch !== batchAtStart) return;
    renderBatchPayload(payload);
    pollErrorShown = false;
  } catch (error) {
    // 服务端不可达时只提示一次,恢复后自动复位,不再每 1.2 秒刷屏。
    if (!pollErrorShown) {
      toast(error.message);
      pollErrorShown = true;
    }
  } finally {
    pollBatchPending = false;
  }
}
function card(record, index) {
  const article = document.createElement("article");
  article.className = "image-card is-loading";
  article.style.animationDelay = `${Math.min(index * 25, 250)}ms`;
  const button = document.createElement("button");
  button.type = "button";
  button.dataset.recordId = String(record.id);
  button.setAttribute("aria-label", `查看 ${record.filename || "生成结果"}`);
  button.innerHTML = `<img loading="lazy" decoding="async" fetchpriority="low" src="/api/images/${record.id}" alt="Anima 生成结果"><span class="image-failure" hidden>图片加载失败</span><div class="card-copy"><strong>${escapeHtml(record.filename)}</strong><span>#${record.sequence} · Seed ${record.sample_seed}</span></div>`;
  button.addEventListener("click", () => openDetail(record));
  const image = button.querySelector("img");
  const markLoaded = () => {
    article.classList.remove("is-loading");
    image.classList.add("image-loaded");
  };
  image.addEventListener("load", markLoaded, { once: true });
  const markFailed = (event) => {
    article.classList.remove("is-loading");
    event?.target?.classList.add("image-failed");
    button.querySelector(".image-failure").hidden = false;
  };
  image.addEventListener("error", markFailed, { once: true });
  if (image.complete) {
    if (image.naturalWidth) markLoaded();
    else markFailed({ target: image });
  }
  article.append(button);
  return article;
}
let historyRequest = 0;
function cacheHistoryPage(data) {
  if (!data || !Number.isFinite(Number(data.page))) return;
  historyCache.set(Number(data.page), data);
}

function renderHistoryPage(data) {
  historyDirty = false;
  historyPage = Number(data.page) || 1;
  historyPages = Number(data.pages) || 1;
  historyItems = Array.isArray(data.items) ? data.items : [];
  cacheHistoryPage(data);
  ui.gallery.replaceChildren(...historyItems.map(card));
  ui.emptyState.hidden = historyItems.length > 0;
  const historyUnavailable = config.storage_status?.history?.available === false;
  ui.emptyState.querySelector("strong").textContent = historyUnavailable ? "历史记录暂不可用" : "等待第一张作品";
  ui.emptyState.querySelector("small").textContent = historyUnavailable
    ? "原历史文件已受保护，生成已暂停。请按顶部提示修复文件后重启。"
    : "完成生成后会出现在这里";
  ui.pageLabel.textContent = `${historyPage} / ${historyPages}`;
  ui.prevPage.disabled = historyPage <= 1;
  ui.nextPage.disabled = historyPage >= historyPages;
  ui.historyCount.textContent = historyUnavailable ? "历史存储不可用" : `${data.total || 0} 张图片`;
  updatePreviewNavigation();
}

async function loadHistory(page = historyPage) {
  // 与 loadPool 相同的竞态守卫:快速翻页时丢弃乱序到达的旧响应。
  const requestId = ++historyRequest;
  try {
    const source = document.getElementById("historyWorkbenchFilter")?.value || "";
    const data = await request(`/api/history?page=${page}&limit=24${source ? `&workbench=${source}` : ""}`);
    if (requestId !== historyRequest) return;
    renderHistoryPage(data);
  } catch (error) {
    if (requestId !== historyRequest) return;
    toast(error.message);
  }
}

function renderDetail(record) {
  ui.detailImage.src = `/api/images/${record.id}`;
  ui.detailImage.alt = record.filename ? `生成结果：${record.filename}` : "生成结果";
  ui.detailMeta.textContent = `${record.created_at || ""} · ${record.filename || ""}`;
  ui.detailPositive.value = record.positive_prompt || record.resolved_prompt || "";
  ui.detailNegative.value = record.negative_prompt || "";
  if ((record.settings?.workbench || record.workbench) === "single" && typeof renderSingleDetail === "function") {
    renderSingleDetail(record);
    return;
  }
  if (typeof resetSingleDetail === "function") resetSingleDetail();
  const settings = normalizeSettings(record.settings || {});
  const stats = [
    ["尺寸", `${settings.width} × ${settings.height}`],
    ["采样", `${settings.sampler_name} / ${settings.scheduler} / ${settings.steps} steps / CFG ${settings.cfg}`],
    ["图像种子", record.sample_seed],
    ["提示词种子", record.prompt_seed],
    ["人物", `${settings.female_count || 0} 女 / ${settings.male_count || 0} 男`],
  ];
  ui.detailStats.innerHTML = stats
    .map(([key, value]) => `<div><dt>${escapeHtml(key)}</dt><dd>${escapeHtml(value)}</dd></div>`)
    .join("");
  const selected = record.resolved_selection || {};
  const activeLoras = (settings.loras || []).filter((item) => item.enabled);
  const loraText = activeLoras.length
    ? activeLoras.map((item) => `${escapeHtml(item.filename)} × ${escapeHtml(item.strength)}`).join("、")
    : "未使用";
  ui.detailSelection.innerHTML = `<span class="kicker">ACTUAL DRAW</span><div>${SECTIONS.map((section) => `<span><b>${SECTION_META[section].label}</b>${(selected[section] || []).map((item) => escapeHtml(item.title)).join("、") || "未使用"}</span>`).join("")}<span><b>LoRA</b>${loraText}</span></div>`;
}

function updateDetailRecord(record) {
  selectedRecord = record;
  ++regenerationRequest;
  regenerationOptions = null;
  closeRegenerationDrawers(false);
  renderDetail(record);
  if (detailContext?.origin === "single" && typeof singleGalleryState !== "undefined") singleGalleryState.viewerRecord = record;
}
function openDetail(record, { origin = "random", returnFocus = null } = {}) {
  if (!record) return;
  closeImagePreview({ immediate: true, restoreFocus: false });
  detailContext = { origin, returnFocus: returnFocus || document.activeElement };
  updateDetailRecord(record);
  updatePreviewNavigation();
  if (!ui.detailDialog.open) ui.detailDialog.showModal();
}
function suppressDetailReturnFocus() {
  if (detailContext) detailContext.returnFocus = null;
}
function finishDetailClose() {
  if (ui.detailDialog.open) return;
  const context = detailContext;
  closeImagePreview({ immediate: true, restoreFocus: false });
  ++regenerationRequest;
  regenerationOptions = null;
  detailContext = null;
  if (context?.origin === "single" && typeof singleGalleryDetailClosed === "function") singleGalleryDetailClosed();
  if (context?.returnFocus?.isConnected && !context.returnFocus.disabled) context.returnFocus.focus({ preventScroll: true });
  else if (context?.returnFocus && context.origin === "single") document.getElementById("singlePreview")?.focus({ preventScroll: true });
  else if (context?.returnFocus && context.origin === "random") {
    // Crossing random archive pages replaces the original thumbnail element.
    const target = ui.gallery.querySelector(`button[data-record-id="${selectedRecord?.id}"]`) || ui.gallery.querySelector("button") || document.getElementById("historyWorkbenchFilter");
    target?.focus({ preventScroll: true });
  }
}

const IMAGE_PREVIEW_MAX_ZOOM = 4;
const imagePreviewReducedMotion = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
const clampPreview = (value, min, max) => Math.min(max, Math.max(min, value));
const previewMotion = (name, fallback) => {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  const duration = Number.parseFloat(value);
  return Number.isFinite(duration) ? duration : fallback;
};

function currentPreviewRecord() {
  return imagePreview.context?.kind === "single" ? imagePreview.context.record : selectedRecord;
}
function setCurrentPreviewRecord(record) {
  if (imagePreview.context?.kind === "single") imagePreview.context.record = record;
  else { selectedRecord = record; if (record) renderDetail(record); }
}

function previewRecordIndex(record, items = historyItems) {
  if (!record || !Array.isArray(items)) return -1;
  return items.findIndex((item) => String(item.id) === String(record.id));
}

function selectedHistoryLocation() {
  if (imagePreview.context?.kind === "single") return imagePreview.context.history.location();
  const currentIndex = previewRecordIndex(selectedRecord);
  if (currentIndex >= 0) return { page: historyPage, items: historyItems, index: currentIndex };
  for (const [page, data] of historyCache) {
    const items = Array.isArray(data?.items) ? data.items : [];
    const index = previewRecordIndex(selectedRecord, items);
    if (index >= 0) return { page, items, index };
  }
  return { page: historyPage, items: historyItems, index: -1 };
}

function previewNeighborAvailable(direction) {
  if (imagePreview.context?.kind === "single") return imagePreview.context.history.available(direction);
  const location = selectedHistoryLocation();
  if (location.index >= 0 && location.index + direction >= 0 && location.index + direction < location.items.length) return true;
  const targetPage = location.page + (direction > 0 ? 1 : -1);
  return targetPage >= 1 && targetPage <= historyPages;
}

function updatePreviewNavigation() {
  const active = Boolean((currentPreviewRecord() || imagePreview.context?.kind === "single") && imagePreview.phase !== "closed");
  const locked = ["loading", "switching", "closing"].includes(imagePreview.phase);
  const previousAvailable = active && previewNeighborAvailable(-1);
  const nextAvailable = active && previewNeighborAvailable(1);
  ui.imagePreviewPrevious.disabled = locked || !previousAvailable;
  ui.imagePreviewNext.disabled = locked || !nextAvailable;
  ui.imagePreviewDialog.classList.toggle("has-navigation", active && (previousAvailable || nextAvailable));
}

function announcePreview(message) {
  ui.imagePreviewAnnouncement.textContent = message;
}

function previewPositionLabel(record = currentPreviewRecord()) {
  if (imagePreview.context?.kind === "single") return imagePreview.context.history.label(record);
  const location = previewRecordIndex(record) >= 0
    ? { page: historyPage, items: historyItems, index: previewRecordIndex(record) }
    : selectedHistoryLocation();
  if (location.index < 0) return record?.filename || "当前图片";
  const total = Number(ui.historyCount.textContent.match(/\d+/)?.[0] || 0);
  let position = (location.page - 1) * 24 + location.index + 1;
  let priorPagesAreCached = true;
  let cachedBefore = 0;
  for (let page = 1; page < location.page; page += 1) {
    const data = historyCache.get(page);
    if (!data || !Array.isArray(data.items)) {
      priorPagesAreCached = false;
      break;
    }
    cachedBefore += data.items.length;
  }
  if (priorPagesAreCached) position = cachedBefore + location.index + 1;
  return `${record?.filename || "当前图片"}，第 ${position} 张${total ? `，共 ${total} 张` : ""}`;
}

function clearPreviewCache() {
  imagePreview.cacheEpoch = (imagePreview.cacheEpoch || 0) + 1;
  for (const promise of imagePreview.preloaded.values()) promise.release?.();
  imagePreview.preloaded.clear();
}
function trimPreviewCache() {
  const current = String(currentPreviewRecord()?.id);
  for (const [key, promise] of imagePreview.preloaded) {
    if (imagePreview.preloaded.size <= 5) break;
    if (key === current) continue;
    imagePreview.preloaded.delete(key);
    promise.release?.();
  }
}
function preloadRecord(record) {
  if (!record) return Promise.resolve(null);
  const key = String(record.id);
  const cached = imagePreview.preloaded.get(key);
  if (cached) {
    imagePreview.preloaded.delete(key);
    imagePreview.preloaded.set(key, cached);
    return cached;
  }
  const image = new Image();
  const context = imagePreview.context, epoch = imagePreview.cacheEpoch || 0;
  let released = false;
  image.decoding = "async";
  image.src = `/api/images/${record.id}`;
  const promise = image.decode()
    .then(() => !released && context === imagePreview.context && epoch === (imagePreview.cacheEpoch || 0) && imagePreview.preloaded.get(key) === promise ? image : null)
    .catch(() => {
      if (imagePreview.preloaded.get(key) === promise) imagePreview.preloaded.delete(key);
      return null;
    });
  promise.release = () => { released = true; image.src = ""; };
  imagePreview.preloaded.set(key, promise);
  trimPreviewCache();
  return promise;
}

function preloadPreviewNeighbors() {
  const location = selectedHistoryLocation();
  if (location.index < 0) return;
  for (const direction of [-1, 1]) {
    const neighbor = location.items[location.index + direction];
    if (neighbor) preloadRecord(neighbor);
  }
}

async function loadHistoryPageForPreview(page) {
  const cached = historyCache.get(page);
  if (cached) return cached;
  const source = document.getElementById("historyWorkbenchFilter")?.value || "";
  const data = await request(`/api/history?page=${page}&limit=24${source ? `&workbench=${source}` : ""}`);
  cacheHistoryPage(data);
  return data;
}

function previewNeighbor(direction, location = selectedHistoryLocation()) {
  const index = location.index + direction;
  if (location.index >= 0 && index >= 0 && index < location.items.length) {
    return { record: location.items[index], page: location.page, data: null };
  }
  const targetPage = location.page + (direction > 0 ? 1 : -1);
  if (targetPage < 1 || targetPage > historyPages) return null;
  return { record: null, page: targetPage, data: null };
}

function playPreviewAnimation(from, to, duration) {
  cancelImagePreviewAnimation();
  const reduce = imagePreviewReducedMotion();
  const animation = ui.imagePreviewImage.animate(
    reduce ? [from, to] : [from, to],
    { duration: reduce ? 120 : duration, easing: getComputedStyle(document.documentElement).getPropertyValue("--spring").trim(), fill: "both" },
  );
  imagePreview.animation = animation;
  return animation.finished.then(() => {
    if (imagePreview.animation === animation) {
      animation.commitStyles();
      animation.cancel();
      imagePreview.animation = null;
    }
    return true;
  }).catch(() => false);
}

async function switchPreviewImage(record, requestId, { src = null, label = null } = {}) {
  const context = imagePreview.context;
  const current = () => requestId === imagePreview.navigationRequest && imagePreview.context === context;
  const previousRecord = currentPreviewRecord();
  const previousSrc = ui.imagePreviewImage.currentSrc || ui.imagePreviewImage.src;
  const image = src ? await new Promise(resolve => { const value = new Image(); value.src = src; value.decode().then(() => resolve(value)).catch(() => resolve(null)); }) : await preloadRecord(record);
  if (!image || !current() || imagePreview.phase !== "switching") return false;
  const fadeOut = await playPreviewAnimation(liveImagePreviewFrame(), { ...liveImagePreviewFrame(), opacity: 0 }, previewMotion("--motion-fast", 150));
  if (!fadeOut || !current() || imagePreview.phase !== "switching") return false;

  setCurrentPreviewRecord(record);
  ui.imagePreviewImage.src = src || `/api/images/${record.id}`;
  ui.imagePreviewImage.alt = `生成结果全尺寸预览：${label || record?.filename || "当前图片"}`;
  ui.imagePreviewImage.hidden = false;
  ui.imagePreviewImage.style.opacity = "0";
  imagePreview.zoom = 1;
  imagePreview.x = imagePreview.y = 0;
  let decoded = true;
  try {
    await ui.imagePreviewImage.decode();
  } catch {
    decoded = false;
  }
  if (!decoded && current() && imagePreview.phase === "switching") {
    setCurrentPreviewRecord(previousRecord);
    ui.imagePreviewImage.src = previousSrc;
    ui.imagePreviewImage.alt = `生成结果全尺寸预览：${previousRecord?.filename || "当前图片"}`;
    ui.imagePreviewImage.hidden = false;
    ui.imagePreviewImage.style.opacity = "1";
    imagePreview.zoom = 1;
    imagePreview.x = imagePreview.y = 0;
    await ui.imagePreviewImage.decode().catch(() => null);
    layoutImagePreview();
    ui.imagePreviewTitle.textContent = previousRecord?.filename || "全尺寸图片预览";
  }
  if (!current() || imagePreview.phase !== "switching") return false;
  // Commit the detail at the same time the decoded new bitmap is displayed.
  // Esc during its entry animation must return to this image's details.
  if (decoded) imagePreview.context?.onRecordCommit?.(record);
  layoutImagePreview();
  const target = imagePreviewFrame();
  const enter = imagePreviewReducedMotion() ? { ...target, opacity: 0 } : { ...target, opacity: 0, transform: `${target.transform} scale(.985)` };
  imagePreview.phase = "opening";
  const entered = await playPreviewAnimation(enter, target, previewMotion("--motion-medium", 240));
  if (!entered || !current() || imagePreview.phase !== "opening") return false;
  imagePreview.phase = "open";
  ui.imagePreviewTitle.textContent = decoded ? (label || record?.filename || "全尺寸图片预览") : previousRecord?.filename || "全尺寸图片预览";
  ui.imagePreviewStatus.hidden = true;
  announcePreview(previewPositionLabel(record));
  updatePreviewNavigation();
  ui.imagePreviewDialog.classList.remove("is-navigating");
  preloadPreviewNeighbors();
  return decoded;
}

async function navigatePreview(direction, focusTarget = null) {
  if (imagePreview.context?.kind === "single") return imagePreview.context.history.navigate(direction, focusTarget);
  const context = imagePreview.context;
  const restoreFocus = () => {
    if (!focusTarget?.isConnected) return;
    if (!focusTarget.disabled) focusTarget.focus({ preventScroll: true });
    else ui.imagePreviewImage.focus({ preventScroll: true });
  };
  if (imagePreview.phase !== "open") return;
  const requestId = ++imagePreview.navigationRequest;
  ui.imagePreviewDialog.classList.add("is-navigating");
  const location = selectedHistoryLocation();
  let target = previewNeighbor(direction, location);
  if (!target) {
    announcePreview(direction < 0 ? "已经是第一张图片" : "已经是最后一张图片");
    ui.imagePreviewDialog.classList.remove("is-navigating");
    updatePreviewNavigation();
    restoreFocus();
    return;
  }
  if (!target.record) {
    imagePreview.phase = "switching";
    announcePreview("正在加载相邻图片…");
    try {
      target.data = await loadHistoryPageForPreview(target.page);
      const items = Array.isArray(target.data.items) ? target.data.items : [];
      target.record = items[direction > 0 ? 0 : items.length - 1];
    } catch (error) {
      if (requestId !== imagePreview.navigationRequest || imagePreview.context !== context) return;
      imagePreview.phase = "open";
      ui.imagePreviewDialog.classList.remove("is-navigating");
      updatePreviewNavigation();
      announcePreview(error.message || "相邻图片加载失败");
      restoreFocus();
      return;
    }
    if (!target.record || requestId !== imagePreview.navigationRequest || imagePreview.context !== context) {
      if (requestId === imagePreview.navigationRequest && imagePreview.context === context) {
        imagePreview.phase = "open";
        ui.imagePreviewDialog.classList.remove("is-navigating");
        updatePreviewNavigation();
        restoreFocus();
      }
      return;
    }
    cacheHistoryPage(target.data);
  } else {
    imagePreview.phase = "switching";
  }
  updatePreviewNavigation();
  const switched = await switchPreviewImage(target.record, requestId);
  if (requestId !== imagePreview.navigationRequest || imagePreview.context !== context) return;
  if (switched && target.data) renderHistoryPage(target.data);
  if (switched) restoreFocus();
  if (!switched && requestId === imagePreview.navigationRequest && imagePreview.phase === "switching") {
    imagePreview.phase = "open";
    ui.imagePreviewDialog.classList.remove("is-navigating");
    announcePreview("图片加载失败，请重试");
    updatePreviewNavigation();
    restoreFocus();
  }
}

function imagePreviewFrame() {
  return { transform: ui.imagePreviewImage.style.transform || "none", clipPath: "inset(0px round 18px)", opacity: 1 };
}

function cancelImagePreviewAnimation() {
  imagePreview.animation?.cancel();
  imagePreview.animation = null;
}

function animateImagePreview(from, to, duration, onFinish = () => {}) {
  cancelImagePreviewAnimation();
  const reduce = imagePreviewReducedMotion();
  const animation = ui.imagePreviewImage.animate(
    reduce ? (imagePreview.phase === "closing" ? [from, { ...from, opacity: 0 }] : [{ ...to, opacity: 0 }, to]) : [from, to],
    { duration: reduce ? 120 : duration, easing: getComputedStyle(document.documentElement).getPropertyValue("--spring").trim(), fill: "both" },
  );
  imagePreview.animation = animation;
  animation.finished.then(() => {
    if (imagePreview.animation !== animation) return;
    cancelImagePreviewAnimation();
    onFinish();
  }).catch(() => {}); // Reversing/closing during an animation is expected.
}

// Fade the veil from its live opacity so interrupting a fade never jumps.
function animateImagePreviewVeil(toOpacity, duration, onFinish = () => {}) {
  const veil = ui.imagePreviewVeil;
  const from = getComputedStyle(veil).opacity;
  veil.getAnimations().forEach(animation => animation.cancel());
  const animation = veil.animate([{ opacity: from }, { opacity: toOpacity }], {
    duration: imagePreviewReducedMotion() ? 120 : duration, fill: "both",
  });
  animation.finished.then(onFinish).catch(() => {});
  return animation;
}

function liveImagePreviewFrame() {
  const style = getComputedStyle(ui.imagePreviewImage);
  return { transform: style.transform, clipPath: style.clipPath, opacity: style.opacity };
}

function clampImagePreviewOffset() {
  const maxX = Math.max(0, (imagePreview.width * imagePreview.zoom - ui.imagePreviewStage.clientWidth) / 2);
  const maxY = Math.max(0, (imagePreview.height * imagePreview.zoom - ui.imagePreviewStage.clientHeight) / 2);
  imagePreview.x = clampPreview(imagePreview.x, -maxX, maxX);
  imagePreview.y = clampPreview(imagePreview.y, -maxY, maxY);
}

function applyImagePreviewTransform() {
  clampImagePreviewOffset();
  ui.imagePreviewImage.style.transform = "translate3d(" + imagePreview.x + "px, " + imagePreview.y + "px, 0) scale(" + imagePreview.zoom + ")";
  ui.imagePreviewImage.classList.toggle("is-zoomed", imagePreview.zoom > 1);
}

function layoutImagePreview() {
  const image = ui.imagePreviewImage;
  const stage = ui.imagePreviewStage;
  const style = getComputedStyle(stage);
  const availableWidth = Math.min(stage.clientWidth * .92, stage.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight));
  const availableHeight = Math.min(stage.clientHeight * .92, stage.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom));
  const fit = Math.min(1, availableWidth / image.naturalWidth, availableHeight / image.naturalHeight);
  imagePreview.width = image.naturalWidth * fit;
  imagePreview.height = image.naturalHeight * fit;
  image.style.width = imagePreview.width + "px";
  image.style.height = imagePreview.height + "px";
  applyImagePreviewTransform();
}

// Reproduce object-fit:cover without stretching; reveal the cropped edges as it opens.
function imagePreviewSourceFrame() {
  const sourceElement = imagePreview.context?.sourceElement || ui.detailImage;
  const sourceContainer = imagePreview.context?.sourceContainer || ui.detailDialog;
  if (!sourceElement?.isConnected) return { ...imagePreviewFrame(), opacity: 0 };
  const source = sourceElement.getBoundingClientRect();
  const dialog = sourceContainer.getBoundingClientRect();
  const stage = ui.imagePreviewStage.getBoundingClientRect();
  const scale = Math.max(source.width / imagePreview.width, source.height / imagePreview.height);
  const left = Math.max(source.left, dialog.left + 1, stage.left);
  const top = Math.max(source.top, dialog.top + 1, stage.top);
  const right = Math.min(source.right, dialog.right - 1, stage.right);
  const bottom = Math.min(source.bottom, dialog.bottom - 1, stage.bottom);
  if (!scale || right <= left || bottom <= top) return { ...imagePreviewFrame(), opacity: 0 };
  const cx = source.left + source.width / 2;
  const cy = source.top + source.height / 2;
  const imageLeft = cx - imagePreview.width * scale / 2;
  const imageTop = cy - imagePreview.height * scale / 2;
  const insets = [
    (top - imageTop) / scale,
    (imageLeft + imagePreview.width * scale - right) / scale,
    (imageTop + imagePreview.height * scale - bottom) / scale,
    (left - imageLeft) / scale,
  ].map(value => Math.max(0, value) + "px").join(" ");
  return {
    transform: "translate3d(" + (cx - stage.left - stage.width / 2) + "px, " + (cy - stage.top - stage.height / 2) + "px, 0) scale(" + scale + ")",
    clipPath: "inset(" + insets + " round " + (18 / scale) + "px)",
    opacity: 1,
  };
}

async function openImagePreview(event, context = null) {
  event?.stopPropagation(); // Preserve any expanded replay/variant controls underneath.
  if (imagePreview.phase === "closed") clearPreviewCache();
  if (context && imagePreview.phase === "closed") imagePreview.context = context;
  if (!imagePreview.context && imagePreview.phase === "closed") imagePreview.context = detailContext?.origin === "single" && typeof singleGalleryContext === "function"
    ? singleGalleryContext()
    : { kind: "random", sourceElement: ui.detailImage, sourceContainer: ui.detailDialog, returnFocus: ui.detailImage, isValid: () => ui.detailDialog.open };
  const record = currentPreviewRecord();
  const source = imagePreview.context?.src || (record ? `/api/images/${record.id}` : "");
  if (!record || !source || !ui.detailDialog.open) return;

  // Reverse-reopen: if the exit animation is still in flight, bounce back from
  // the live frame instead of ignoring the click (previously felt like a hang).
  if (imagePreview.phase === "closing") {
    ++imagePreview.token; // Cancel the pending finishImagePreviewClose callback.
    if (!ui.imagePreviewDialog.open) {
      // The dialog got closed before we could intercept; restart cleanly.
      imagePreview.phase = "closed";
      return openImagePreview(event);
    }
    imagePreview.phase = "opening";
    const from = liveImagePreviewFrame();
    imagePreview.zoom = 1;
    imagePreview.x = imagePreview.y = 0;
    applyImagePreviewTransform();
    animateImagePreviewVeil(1, previewMotion("--motion-medium", 240));
    animateImagePreview(from, imagePreviewFrame(), previewMotion("--motion-medium", 240), () => {
      if (imagePreview.phase === "opening") {
        imagePreview.phase = "open";
        updatePreviewNavigation();
        imagePreview.context?.onReady?.();
      }
    });
    return;
  }
  if (imagePreview.phase !== "closed") return;

  const token = ++imagePreview.token;
  imagePreview.phase = "loading";
  imagePreview.zoom = 1;
  imagePreview.x = imagePreview.y = 0;
  imagePreview.tap = null;
  // The browser's HTTP cache can reuse the source; decoded preview resources
  // belong to the open viewer and are released when it closes.
  ui.imagePreviewImage.hidden = true;
  ui.imagePreviewStatus.hidden = false;
  ui.imagePreviewStatus.textContent = "正在加载原图…";
  if (source) ui.imagePreviewImage.src = source;
  ui.imagePreviewImage.alt = `生成结果全尺寸预览：${record?.filename || "当前图片"}`;
  ui.imagePreviewTitle.textContent = record?.filename || "全尺寸图片预览";
  ui.imagePreviewDialog.dataset.context = imagePreview.context.kind;
  document.documentElement.classList.add("image-preview-open");
  ui.imagePreviewDialog.showModal();
  imagePreview.context?.sourceElement?.setAttribute("aria-expanded", "true");
  ui.closeImagePreview.focus({ preventScroll: true });
  updatePreviewNavigation();
  announcePreview(`正在打开${previewPositionLabel(record)}`);
  imagePreview.context?.onOpen?.();
  animateImagePreviewVeil(1, previewMotion("--motion-slow", 360));
  try {
    await ui.imagePreviewImage.decode();
    if (token !== imagePreview.token || imagePreview.context?.isValid?.() === false) return;
    // If a close was requested while decoding, keep the image hidden and let
    // the in-flight close animation finish instead of starting a new entry.
    if (imagePreview.phase === "closing") return;
    ui.imagePreviewStatus.hidden = true;
    ui.imagePreviewImage.hidden = false;
    layoutImagePreview();
    imagePreview.phase = "opening";
    animateImagePreview(imagePreviewSourceFrame(), imagePreviewFrame(), previewMotion("--motion-slow", 360), () => {
      if (imagePreview.phase === "opening") {
        imagePreview.phase = "open";
        updatePreviewNavigation();
        announcePreview(previewPositionLabel(record));
        preloadPreviewNeighbors();
        imagePreview.context?.onReady?.();
      }
    });
  } catch {
    if (token !== imagePreview.token) return;
    imagePreview.phase = "error";
    ui.imagePreviewStatus.textContent = "图片加载失败，请关闭后重试。";
    imagePreview.context?.onError?.(record);
  }
}

function finishImagePreviewClose(restoreFocus = true) {
  const context = imagePreview.context;
  clearPreviewCache();
  imagePreview.pendingReturnFocus = restoreFocus ? context : null;
  ++imagePreview.token;
  imagePreview.phase = "closed";
  cancelImagePreviewAnimation();
  imagePreview.pointers.clear();
  imagePreview.gesture = imagePreview.tap = imagePreview.backdropPointer = imagePreview.swipe = null;
  imagePreview.navigationRequest++;
  ui.imagePreviewVeil.getAnimations().forEach(animation => animation.cancel());
  ui.imagePreviewImage.classList.remove("is-interacting", "is-zoomed");
  ui.imagePreviewImage.hidden = true;
  ui.imagePreviewImage.removeAttribute("src");
  ui.imagePreviewStatus.hidden = true;
  ui.imagePreviewAnnouncement.textContent = "";
  ui.imagePreviewDialog.classList.remove("has-navigation", "is-navigating");
  document.documentElement.classList.remove("image-preview-open");
  context?.sourceElement?.setAttribute("aria-expanded", "false");
  if (ui.imagePreviewDialog.open) ui.imagePreviewDialog.close();
  imagePreview.context = null;
  delete ui.imagePreviewDialog.dataset.context;
  context?.onClose?.();
}

function restoreImagePreviewCloseFocus() {
  const context = imagePreview.pendingReturnFocus;
  if (!context?.returnFocus) return;
  // Run after the native close event and a rendered frame: the closed modal
  // can retain focus until it has actually left the browser's top layer.
  requestAnimationFrame(() => requestAnimationFrame(() => {
    if (imagePreview.pendingReturnFocus !== context) return;
    imagePreview.pendingReturnFocus = null;
    if (imagePreview.phase === "closed" && context.isValid?.() !== false && context.returnFocus.isConnected)
      context.returnFocus.focus({ preventScroll: true });
  }));
}

function closeImagePreview({ immediate = false, restoreFocus = true } = {}) {
  if (imagePreview.phase === "closed") return;
  if (immediate) return finishImagePreviewClose(restoreFocus);
  if (imagePreview.phase === "closing") return;
  ++imagePreview.token; // Invalidate an in-flight decode before starting the exit.
  ++imagePreview.navigationRequest;
  const hasImage = !ui.imagePreviewImage.hidden;
  imagePreview.phase = "closing";
  if (hasImage) {
    animateImagePreviewVeil(0, previewMotion("--motion-medium", 240));
    animateImagePreview(liveImagePreviewFrame(), imagePreviewSourceFrame(), previewMotion("--motion-medium", 240), () => finishImagePreviewClose(restoreFocus));
  } else {
    animateImagePreviewVeil(0, previewMotion("--motion-medium", 240), () => finishImagePreviewClose(restoreFocus));
  }
}

// During manipulation, update transforms directly so the picture follows the pointer.
function setImagePreviewZoom(nextZoom, focalPoint = null, smooth = false) {
  if (imagePreview.phase !== "open") return;
  const from = liveImagePreviewFrame();
  const previousZoom = imagePreview.zoom;
  imagePreview.zoom = clampPreview(nextZoom, 1, IMAGE_PREVIEW_MAX_ZOOM);
  if (imagePreview.zoom === 1) {
    imagePreview.x = imagePreview.y = 0;
  } else if (focalPoint) {
    const stage = ui.imagePreviewStage.getBoundingClientRect();
    const ratio = imagePreview.zoom / previousZoom;
    imagePreview.x = (focalPoint.x - stage.left - stage.width / 2) * (1 - ratio) + imagePreview.x * ratio;
    imagePreview.y = (focalPoint.y - stage.top - stage.height / 2) * (1 - ratio) + imagePreview.y * ratio;
  }
  applyImagePreviewTransform();
  if (smooth) animateImagePreview(from, imagePreviewFrame(), 220);
  else cancelImagePreviewAnimation();
}

function resetImagePreviewTransform() {
  setImagePreviewZoom(1, null, true);
}

function beginImagePreviewGesture() {
  const points = [...imagePreview.pointers.values()];
  imagePreview.gesture = {
    points: points.map(point => ({ ...point })),
    zoom: imagePreview.zoom, x: imagePreview.x, y: imagePreview.y,
  };
}

function imagePreviewPointerDown(event) {
  if (imagePreview.phase === "closing" && (event.pointerType !== "mouse" || event.button === 0)) {
    openImagePreview(); // reverse-reopen from the live frame
    return;
  }
  // During the entry animation, let it settle: grabbing mid-entry would snap
  // the pan offset through clamp logic and look like a jump.
  if (imagePreview.phase !== "open" || (event.pointerType === "mouse" && event.button !== 0)) return;
  // Continue from the presentation transform if a double-click zoom was still settling.
  const current = new DOMMatrixReadOnly(getComputedStyle(ui.imagePreviewImage).transform);
  cancelImagePreviewAnimation();
  imagePreview.zoom = current.a;
  imagePreview.x = current.e;
  imagePreview.y = current.f;
  applyImagePreviewTransform();
  ui.imagePreviewImage.setPointerCapture(event.pointerId);
  imagePreview.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY, time: performance.now(), moved: false });
  imagePreview.swipe = event.pointerType === "mouse" ? null : { pointerId: event.pointerId, startX: event.clientX, startY: event.clientY };
  if (imagePreview.pointers.size > 1) {
    imagePreview.tap = null;
    imagePreview.pointers.forEach(point => { point.moved = true; });
  }
  ui.imagePreviewImage.classList.add("is-interacting");
  beginImagePreviewGesture();
  event.preventDefault();
}

function imagePreviewPointerMove(event) {
  const point = imagePreview.pointers.get(event.pointerId);
  if (!point || imagePreview.phase !== "open") return;
  Object.assign(point, { x: event.clientX, y: event.clientY });
  point.moved ||= Math.hypot(point.x - point.startX, point.y - point.startY) > 6;
  const points = [...imagePreview.pointers.values()];
  const gesture = imagePreview.gesture;
  if (points.length > 1) {
    const [a, b] = points;
    const [startA, startB] = gesture.points;
    const startDistance = Math.max(1, Math.hypot(startA.x - startB.x, startA.y - startB.y));
    imagePreview.zoom = clampPreview(gesture.zoom * Math.hypot(a.x - b.x, a.y - b.y) / startDistance, 1, IMAGE_PREVIEW_MAX_ZOOM);
    const stage = ui.imagePreviewStage.getBoundingClientRect();
    const ratio = imagePreview.zoom / gesture.zoom;
    imagePreview.x = (a.x + b.x) / 2 - stage.left - stage.width / 2 - ((startA.x + startB.x) / 2 - stage.left - stage.width / 2 - gesture.x) * ratio;
    imagePreview.y = (a.y + b.y) / 2 - stage.top - stage.height / 2 - ((startA.y + startB.y) / 2 - stage.top - stage.height / 2 - gesture.y) * ratio;
  } else if (point.moved) {
    imagePreview.x = gesture.x + point.x - gesture.points[0].x;
    imagePreview.y = gesture.y + point.y - gesture.points[0].y;
  }
  applyImagePreviewTransform();
  event.preventDefault();
}

function imagePreviewPointerUp(event) {
  const point = imagePreview.pointers.get(event.pointerId);
  if (!point) return;
  const deltaX = point.x - point.startX;
  const deltaY = point.y - point.startY;
  const isSwipe = event.type === "pointerup"
    && event.pointerType !== "mouse"
    && imagePreview.zoom <= 1.01
    && Math.abs(deltaX) >= 56
    && Math.abs(deltaX) > Math.abs(deltaY) * 1.25;
  imagePreview.pointers.delete(event.pointerId);
  if (isSwipe && imagePreview.pointers.size === 0) {
    imagePreview.tap = null;
    imagePreview.gesture = null;
    imagePreview.swipe = null;
    ui.imagePreviewImage.classList.remove("is-interacting");
    navigatePreview(deltaX < 0 ? 1 : -1);
    return;
  }
  if (event.type === "pointerup" && event.pointerType === "touch" && !point.moved && performance.now() - point.time < 300) {
    const previous = imagePreview.tap;
    if (previous && performance.now() - previous.time < 300 && Math.hypot(point.x - previous.x, point.y - previous.y) < 30) {
      setImagePreviewZoom(imagePreview.zoom < 1.01 ? 2 : 1, point, true);
      imagePreview.tap = null;
    } else imagePreview.tap = { x: point.x, y: point.y, time: performance.now() };
  }
  if (imagePreview.pointers.size) beginImagePreviewGesture();
  else {
    imagePreview.gesture = null;
    imagePreview.swipe = null;
    ui.imagePreviewImage.classList.remove("is-interacting");
  }
}

function imagePreviewWheel(event) {
  event.preventDefault();
  const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? ui.imagePreviewStage.clientHeight : 1);
  setImagePreviewZoom(imagePreview.zoom * Math.exp(-clampPreview(delta, -500, 500) * .0015), { x: event.clientX, y: event.clientY });
}

function collapseRegenerationDrawer(drawer) {
  if (drawer.hidden || drawer.classList.contains("closing")) return;
  drawer.classList.add("closing");
  setTimeout(() => {
    drawer.hidden = true;
    drawer.classList.remove("closing");
  }, 200);
}

function closeRegenerationDrawers(restoreFocus = true) {
  const hadOpen = !ui.replayDrawer.hidden || !ui.variantDrawer.hidden;
  collapseRegenerationDrawer(ui.replayDrawer);
  collapseRegenerationDrawer(ui.variantDrawer);
  ui.reproduceImage.setAttribute("aria-expanded", "false");
  ui.variantButton.setAttribute("aria-expanded", "false");
  if (restoreFocus && hadOpen && regenerationTrigger && document.contains(regenerationTrigger))
    regenerationTrigger.focus({ preventScroll: true });
  regenerationTrigger = null;
}

function regenerationReasons(mode) {
  return regenerationOptions?.modes?.[mode]?.reasons || [];
}

function renderRegenerationOptions() {
  if (!regenerationOptions || !selectedRecord) return;
  const issues = regenerationOptions.resourceIssues || [];
  const issueText = issues.length
    ? `<strong>无法生成</strong><span>${issues.map((item) => `${escapeHtml(item.label)}：${escapeHtml(item.name)}`).join("<br>")}</span>`
    : `<strong>资源检查通过</strong><span>模型、已启用 LoRA、高清模型与采样资源均可用。</span>`;
  ui.replayStatus.dataset.state = issues.length ? "error" : "ok";
  ui.replayStatus.innerHTML = issueText;
  ui.variantStatus.dataset.state = issues.length ? "error" : "ok";
  ui.variantStatus.innerHTML = issueText;
  const singleRecord = (selectedRecord.settings?.workbench || selectedRecord.workbench) === "single";
  ui.replaySeedSummary.innerHTML = `<span><b>图像种子</b>${escapeHtml(selectedRecord.sample_seed)}</span>${singleRecord ? "" : `<span><b>提示词种子</b>${escapeHtml(selectedRecord.prompt_seed)}</span>`}`;
  ui.confirmReplay.disabled = !regenerationOptions.modes?.replay?.available;
  for (const input of ui.redrawSections.querySelectorAll("input")) {
    const section = regenerationOptions.sections?.[input.value];
    input.disabled = !section?.canRedraw;
    input.checked = false;
    input.closest("label").hidden = !section?.canRedraw;
  }
  updateVariantDrawer();
}

async function loadRegenerationOptions() {
  if (!selectedRecord) return null;
  if (regenerationOptions) return regenerationOptions;
  const requestId = ++regenerationRequest;
  ui.replayStatus.dataset.state = "pending";
  ui.replayStatus.textContent = "正在检查历史记录与本地资源…";
  ui.variantStatus.dataset.state = "pending";
  ui.variantStatus.textContent = "正在检查历史记录与本地资源…";
  try {
    const value = await request(`/api/history/${selectedRecord.id}/regeneration-options`);
    if (requestId !== regenerationRequest || !selectedRecord) return null;
    regenerationOptions = value;
    renderRegenerationOptions();
    return value;
  } catch (error) {
    if (requestId !== regenerationRequest) return null;
    for (const status of [ui.replayStatus, ui.variantStatus]) {
      status.dataset.state = "error";
      status.textContent = error.message;
    }
    return null;
  }
}

async function openRegenerationDrawer(kind, trigger) {
  const drawer = kind === "replay" ? ui.replayDrawer : ui.variantDrawer;
  const wasOpen = !drawer.hidden || drawer.classList.contains("closing");
  closeRegenerationDrawers(false);
  if (wasOpen) {
    trigger.focus({ preventScroll: true });
    return;
  }
  regenerationTrigger = trigger;
  drawer.classList.remove("closing");
  drawer.hidden = false;
  trigger.setAttribute("aria-expanded", "true");
  await loadRegenerationOptions();
  const focusTarget = kind === "replay" ? ui.confirmReplay : ui.variantModes.querySelector("input:checked");
  focusTarget?.focus({ preventScroll: true });
}

function selectedRegenerationMode() {
  return ui.variantModes.querySelector('input[name="regenerationMode"]:checked')?.value || "prompt_variant";
}

function selectedRedrawSections() {
  return [...ui.redrawSections.querySelectorAll("input:checked")].map((input) => input.value);
}

let redrawSectionsAnim = null;
function setRedrawSectionsVisible(visible) {
  const el = ui.redrawSections;
  // No-op when the requested state already matches; importantly this avoids
  // cancelling an in-flight entry/exit animation on repeated redraws.
  if (visible === !el.hidden) return;
  redrawSectionsAnim?.cancel();
  redrawSectionsAnim = null;
  if (visible) {
    el.hidden = false;
    redrawSectionsAnim = el.animate(
      [{ opacity: 0, transform: "translateY(-6px)" }, { opacity: 1, transform: "none" }],
      { duration: imagePreviewReducedMotion() ? 60 : 200, easing: "cubic-bezier(.2,.8,.2,1)", fill: "both" },
    );
  } else {
    redrawSectionsAnim = el.animate(
      [{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(-6px)" }],
      { duration: imagePreviewReducedMotion() ? 60 : 160, easing: "cubic-bezier(.4,0,1,1)", fill: "both" },
    );
    redrawSectionsAnim.finished.then(() => { el.hidden = true; }).catch(() => {});
  }
}

function updateVariantDrawer() {
  const mode = selectedRegenerationMode();
  const sections = selectedRedrawSections();
  setRedrawSectionsVisible(mode === "content_redraw");
  const labels = {
    prompt_variant: "生成同提示词变体",
    content_redraw: "重新抽取所选内容",
    settings_reroll: "按此配置再抽",
  };
  ui.confirmVariant.textContent = labels[mode];
  const available = Boolean(regenerationOptions?.modes?.[mode]?.available);
  const reasons = [...regenerationReasons(mode)];
  if (mode === "content_redraw") {
    for (const [section, value] of Object.entries(regenerationOptions?.sections || {})) {
      if (value.random && !sections.includes(section) && value.canPreserve === false)
        reasons.push(value.preserveReason || `请同时重抽 ${section}：缺少可靠的历史快照`);
    }
  }
  ui.confirmVariant.disabled = !available || reasons.length > 0 || (mode === "content_redraw" && !sections.length);
  if (reasons.length && !(regenerationOptions?.resourceIssues || []).length) {
    ui.variantStatus.dataset.state = "error";
    ui.variantStatus.innerHTML = `<strong>当前方式不可用</strong><span>${reasons.map(escapeHtml).join("<br>")}</span>`;
  } else if (!(regenerationOptions?.resourceIssues || []).length) {
    ui.variantStatus.dataset.state = "ok";
    ui.variantStatus.textContent = "资源检查通过，所选方式可用。";
  }
}

async function submitRegeneration(mode, count = 1, sections = []) {
  if (!selectedRecord || submitInFlight) return false;
  submitInFlight = true;
  ui.confirmReplay.disabled = true;
  ui.confirmVariant.disabled = true;
  try {
    const body = mode === "replay" ? { mode } : { mode, count };
    if (mode === "content_redraw") body.sections = sections;
    const state = await request(`/api/history/${selectedRecord.id}/regenerate`, {
      method: "POST",
      body: JSON.stringify(body),
    });
    toast(state.status === "queued" ? `已加入队列,第 ${state.position} 位` : "批次已开始");
    closeRegenerationDrawers(false);
    suppressDetailReturnFocus();
    ui.detailDialog.close();
    return true;
  } catch (error) {
    toast(error.message);
    regenerationOptions = null;
    await loadRegenerationOptions();
    return false;
  } finally {
    submitInFlight = false;
    try {
      renderBatchPayload(await request("/api/batches/current"));
    } catch {
      updateGenerationAvailability();
    }
    if (regenerationOptions) renderRegenerationOptions();
  }
}

function selectionHas(section, id) {
  const selection = draft.pools[section];
  return selection.mode === "all" ? !selection.excluded_ids.includes(id) : selection.ids.includes(id);
}
function toggleSelection(section, id, checked) {
  const selection = draft.pools[section];
  if (selection.mode === "all")
    selection.excluded_ids = checked
      ? selection.excluded_ids.filter((value) => value !== id)
      : [...new Set([...selection.excluded_ids, id])];
  else if (checked) selection.ids = [...new Set([...selection.ids, id])];
  else selection.ids = selection.ids.filter((value) => value !== id);
  renderPoolItems();
  renderDimensions();
  updatePoolOverview();
  updatePoseConflict();
  updateFavoriteSelectionButton();
  schedulePersist();
}

function favoriteKey(item) {
  return String(item.favorite_key || "");
}
function favoriteMap() {
  const map = new Map();
  for (const item of favoritesData.items || []) {
    const key = activeSection === "character" ? String(item.name || item.id || "") : String(item.id || item.name || "");
    if (key) map.set(key, item);
  }
  return map;
}
function isFavorite(item) {
  return Boolean(favoriteMap().get(favoriteKey(item))?.groupIds?.length);
}

let favoritesRequest = 0;
async function loadFavorites(section = activeSection) {
  const requestId = ++favoritesRequest;
  try {
    const payload = await request(`/api/favorites/${section}`);
    if (requestId !== favoritesRequest || section !== activeSection) return;
    favoritesData = payload;
    favoritesAvailable = true;
  } catch (error) {
    if (requestId !== favoritesRequest || section !== activeSection) return;
    favoritesData = { groups: [], items: [], favorite_keys: [] };
    favoritesAvailable = false;
    if (currentView().filters.collection) currentView().filters.collection = "";
    if (currentView().sort === "favorite-first") currentView().sort = "";
    toast(`收藏不可用：${error.message}`);
  }
  updateFavoriteSelectionButton();
}
async function loadCustomGroups(section = activeSection) {
  const requestedProfile = currentProfile();
  const requestId = ++customGroupsRequest;
  try {
    const payload = await request(`/api/custom-groups/${section}?model_profile=${encodeURIComponent(requestedProfile)}`);
    if (requestId !== customGroupsRequest || requestedProfile !== currentProfile() || section !== activeSection) return;
    customGroups = payload.groups || [];
    if (!customTreeInitialized.has(section)) {
      collapsedCustomGroups[section] = new Set(
        customGroups.filter((group) => group.kind !== "group").map((group) => group.id),
      );
      customTreeInitialized.add(section);
      persistCustomTree();
    }
    if (currentView().filters.customGroup && !customGroup(currentView().filters.customGroup)) {
      currentView().filters.customGroup = "";
    }
    const activeGroup = customGroup(currentView().filters.customGroup);
    let ancestor = activeGroup?.parentId ? customGroup(activeGroup.parentId) : null;
    while (ancestor) {
      collapsedCustomGroups[section].delete(ancestor.id);
      ancestor = ancestor.parentId ? customGroup(ancestor.parentId) : null;
    }
  } catch (error) {
    if (requestId !== customGroupsRequest || requestedProfile !== currentProfile() || section !== activeSection) return;
    customGroups = [];
    toast(`自定义分组不可用：${error.message}`);
  }
}

let drawerTrigger = null;
async function openPool(section) {
  activeSection = section;
  poolPage = 1;
  const view = currentView();
  drawerTrigger = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  // 关闭态的抽屉带 inert(不进 Tab 序列);打开时移除并把焦点移入抽屉。
  ui.poolDrawer.removeAttribute("inert");
  ui.poolDrawer.classList.add("open");
  ui.poolDrawer.setAttribute("aria-hidden", "false");
  clearTimeout(backdropCloseTimer);
  ui.drawerBackdrop.classList.remove("closing");
  ui.drawerBackdrop.hidden = false;
  ui.poolTitle.textContent = `${SECTION_META[section].label}池`;
  ui.poolKicker.textContent = "PROMPT POOL";
  ui.poolSearch.value = view.query;
  ui.poolSort.value = view.sort;
  ui.poolLanguage.value = view.language;
  ui.selectedOnly.checked = view.selectedOnly;
  ui.poolSearch.focus({ preventScroll: true });
  await Promise.all([loadFavorites(section), loadCustomGroups(section)]);
  await loadPool();
  schedulePersist();
}
let backdropCloseTimer = null;
function hideDrawerBackdrop() {
  clearTimeout(backdropCloseTimer);
  ui.drawerBackdrop.classList.add("closing");
  backdropCloseTimer = setTimeout(() => {
    ui.drawerBackdrop.hidden = true;
    ui.drawerBackdrop.classList.remove("closing");
  }, 300);
}

function closePool() {
  currentView().scrollTop = ui.poolGrid.scrollTop;
  setPoolSidebarOpen(false);
  ui.poolDrawer.classList.remove("open");
  ui.poolDrawer.setAttribute("aria-hidden", "true");
  ui.poolDrawer.setAttribute("inert", "");
  hideDrawerBackdrop();
  if (drawerTrigger && document.contains(drawerTrigger)) drawerTrigger.focus({ preventScroll: true });
  drawerTrigger = null;
  schedulePersist();
}
function setPoolSidebarOpen(open) {
  ui.poolSidebar.classList.toggle("mobile-open", open);
  ui.togglePoolSidebar.setAttribute("aria-expanded", String(open));
  ui.togglePoolSidebar.title = open ? "关闭分类导航" : "打开分类导航";
  ui.togglePoolSidebar.setAttribute("aria-label", ui.togglePoolSidebar.title);
}

function readKreaPoolSelection() {
  return { ids: [...kreaPromptPoolSelection] };
}
function openKreaPromptPool() {
  if (currentProfile() !== "krea2") return;
  clearTimeout(kreaPoolBackdropTimer);
  kreaPoolBackdropTimer = null;
  ui.kreaPoolDrawer.removeAttribute("inert");
  ui.kreaPoolDrawer.classList.add("open");
  ui.kreaPoolDrawer.setAttribute("aria-hidden", "false");
  ui.kreaPoolBackdrop.classList.remove("closing");
  ui.kreaPoolBackdrop.hidden = false;
  ui.kreaPoolSearch.focus({ preventScroll: true });
  void loadKreaPromptPool();
}
function closeKreaPromptPool() {
  ui.kreaPoolDrawer.classList.remove("open");
  ui.kreaPoolDrawer.setAttribute("aria-hidden", "true");
  ui.kreaPoolDrawer.setAttribute("inert", "");
  clearTimeout(kreaPoolBackdropTimer);
  ui.kreaPoolBackdrop.classList.add("closing");
  kreaPoolBackdropTimer = setTimeout(() => {
    kreaPoolBackdropTimer = null;
    if (ui.kreaPoolDrawer.classList.contains("open")) return;
    ui.kreaPoolBackdrop.hidden = true;
    ui.kreaPoolBackdrop.classList.remove("closing");
  }, 220);
  ui.openKreaPromptPool.focus({ preventScroll: true });
}
function renderKreaPromptPool() {
  const enabled = currentProfile() === "krea2";
  ui.kreaPromptPoolControls.hidden = !enabled;
  ui.animaPromptPoolControls.hidden = enabled;
  const items = Array.isArray(kreaPromptPool.items) ? kreaPromptPool.items : [];
  ui.krea_prompt_pool_enabled.checked = enabled && ui.krea_prompt_pool_enabled.checked;
  ui.krea_prompt_pool_enabled.disabled = !enabled || Number(kreaPromptPool.count || 0) === 0 || kreaPromptPoolSelection.length === 0;
  const selected = new Set(readKreaPoolSelection().ids);
  const total = Number(kreaPromptPool.count || 0);
  const filteredCount = Number(kreaPoolQueryCount || 0);
  const pageCount = Math.max(1, Number(kreaPoolPages || 0));
  const visible = items;
  ui.kreaPromptPoolCount.textContent = `${total} 项 · 已选 ${selected.size}`;
  ui.kreaPoolMeta.textContent = `${filteredCount} 项匹配 · ${selected.size} 项已选`;
  ui.kreaPoolSelectionLabel.textContent = `已选择 ${selected.size} 项`;
  ui.kreaPoolPageLabel.textContent = `${Math.min(kreaPoolPage, pageCount)} / ${pageCount}`;
  ui.kreaPoolPrev.disabled = kreaPoolPage <= 1 || !kreaPoolPages;
  ui.kreaPoolNext.disabled = kreaPoolPage >= kreaPoolPages || !kreaPoolPages;
  ui.kreaPoolDeleteSelection.disabled = selected.size === 0;
  ui.kreaPromptPoolList.replaceChildren(...visible.map((item) => {
    const row = document.createElement("article");
    row.className = `krea-prompt-row${selected.has(String(item.id)) ? " selected" : ""}`;
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = selected.has(String(item.id));
    checkbox.setAttribute("aria-label", "选择自然语言提示词");
    checkbox.addEventListener("change", () => {
      const id = String(item.id);
      if (checkbox.checked) kreaPromptPoolSelection = [...new Set([...kreaPromptPoolSelection, id])];
      else kreaPromptPoolSelection = kreaPromptPoolSelection.filter((value) => value !== id);
      if (ui.kreaPoolSelectedOnly.checked) applyKreaSelectedViewPage();
      renderKreaPromptPool();
      schedulePersist();
    });
    const text = document.createElement("span");
    text.textContent = item.text || item.prompt || "";
    const tools = document.createElement("div");
    tools.className = "krea-prompt-tools";
    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "button ghost compact";
    edit.textContent = "编辑";
    edit.setAttribute("aria-label", "编辑自然语言提示词");
    edit.addEventListener("click", () => openKreaPromptItemDialog(item));
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "icon-button dark krea-prompt-remove";
    remove.title = "从词池删除";
    remove.setAttribute("aria-label", "从词池删除");
    remove.textContent = "×";
    remove.addEventListener("click", () => deleteKreaPromptPoolItem(item));
    tools.append(edit, remove);
    row.append(checkbox, text, tools);
    return row;
  }));
  if (!items.length) {
    const empty = document.createElement("div");
    empty.className = "inline-empty krea-prompt-empty";
    empty.textContent = total ? "没有匹配的提示词。" : "Krea 2 词池为空，请新增或导入自然语言提示词。";
    ui.kreaPromptPoolList.append(empty);
  }
  updatePoolOverview();
}
function kreaPoolParams({ page = 1, limit = 48, q = ui.kreaPoolSearch.value.trim(), selectedIds = [] } = {}) {
  const params = new URLSearchParams({ model_profile: "krea2", page: String(page), limit: String(limit), q });
  if (selectedIds.length) params.set("selected_ids", selectedIds.join(","));
  return params;
}
async function fetchAllKreaPromptPoolItems(query) {
  const first = await request(`/api/prompt-pools?${kreaPoolParams({ page: 1, limit: 500, q: query })}`);
  const pages = Math.max(1, Number(first.pages || 1));
  const items = [...(Array.isArray(first.items) ? first.items : [])];
  for (let start = 2; start <= pages; start += 6) {
    const batch = Array.from({ length: Math.min(6, pages - start + 1) }, (_, index) => start + index);
    const result = await Promise.all(batch.map((page) =>
      request(`/api/prompt-pools?${kreaPoolParams({ page, limit: 500, q: query })}`),
    ));
    for (const payload of result) items.push(...(Array.isArray(payload.items) ? payload.items : []));
  }
  let poolCount = Number(first.pool_count ?? first.total_count);
  if (!Number.isFinite(poolCount)) poolCount = query ? kreaPoolTotalCount : Number(first.count ?? first.total ?? items.length);
  if (!Number.isFinite(poolCount) || poolCount < 0) poolCount = items.length;
  if (query && !kreaPoolTotalCount && !Number.isFinite(Number(first.pool_count ?? first.total_count))) {
    const totalPayload = await request(`/api/prompt-pools?${kreaPoolParams({ page: 1, limit: 1, q: "" })}`);
    poolCount = Number(totalPayload.pool_count ?? totalPayload.total_count ?? totalPayload.count ?? totalPayload.total ?? poolCount);
  }
  return {
    items,
    count: Number(first.count ?? first.total ?? items.length),
    poolCount: Number.isFinite(poolCount) ? poolCount : items.length,
  };
}
function applyKreaSelectedViewPage() {
  if (!Array.isArray(kreaSelectedViewItems)) return;
  const selected = new Set(kreaPromptPoolSelection);
  const matches = kreaSelectedViewItems.filter((item) => selected.has(String(item.id)));
  kreaPoolQueryCount = matches.length;
  kreaPoolPages = Math.ceil(matches.length / 48);
  kreaPoolPage = Math.max(1, Math.min(kreaPoolPage, Math.max(kreaPoolPages, 1)));
  kreaPromptPool.items = matches.slice((kreaPoolPage - 1) * 48, kreaPoolPage * 48);
  kreaPromptPool.page = kreaPoolPage;
  kreaPromptPool.pages = kreaPoolPages;
}
async function deleteKreaPromptPoolItem(item) {
  return deleteKreaPromptPoolSelection([String(item.id)]);
}
async function loadKreaPromptPool({ page = kreaPoolPage, force = false } = {}) {
  if (currentProfile() !== "krea2") {
    renderKreaPromptPool();
    return;
  }
  const requestId = ++kreaPromptPoolRequest;
  const query = ui.kreaPoolSearch.value.trim();
  try {
    if (ui.kreaPoolSelectedOnly.checked) {
      if (force || kreaSelectedViewItems === null || kreaSelectedViewQuery !== query) {
        const result = await fetchAllKreaPromptPoolItems(query);
        if (requestId !== kreaPromptPoolRequest || currentProfile() !== "krea2") return;
        kreaSelectedViewItems = result.items;
        kreaSelectedViewQuery = query;
        kreaPoolTotalCount = result.poolCount;
        kreaPromptPool.count = result.poolCount;
      }
      kreaPoolPage = Math.max(1, Number(page) || 1);
      applyKreaSelectedViewPage();
    } else {
      const selectedIds = kreaPromptPoolSelection.slice(0, 48);
      const payload = await request(`/api/prompt-pools?${kreaPoolParams({ page, limit: 48, q: query, selectedIds })}`);
      if (requestId !== kreaPromptPoolRequest || currentProfile() !== "krea2") return;
      kreaPoolPage = Number(payload.page || page || 1);
      kreaPoolPages = Number(payload.pages || 0);
      kreaPoolQueryCount = Number(payload.count ?? payload.total ?? payload.items?.length ?? 0);
      const payloadPoolCount = Number(payload.pool_count ?? payload.total_count);
      if (Number.isFinite(payloadPoolCount)) kreaPoolTotalCount = payloadPoolCount;
      else if (!query) kreaPoolTotalCount = kreaPoolQueryCount;
      if (kreaPoolPages === 0) kreaPoolPage = 1;
      if (kreaPoolPages > 0 && kreaPoolPage > kreaPoolPages) {
        await loadKreaPromptPool({ page: kreaPoolPages });
        return;
      }
      kreaPromptPool = {
        model_profile: "krea2",
        items: Array.isArray(payload.items) ? payload.items : [],
        count: Number.isFinite(kreaPoolTotalCount) ? kreaPoolTotalCount : kreaPoolQueryCount,
        page: kreaPoolPage,
        pages: kreaPoolPages,
      };
    }
    if (requestId !== kreaPromptPoolRequest || currentProfile() !== "krea2") return;
    renderKreaPromptPool();
  } catch (error) {
    if (requestId !== kreaPromptPoolRequest || currentProfile() !== "krea2") return;
    kreaPromptPool = { model_profile: "krea2", items: [], count: 0, page: 1, pages: 0 };
    kreaPoolTotalCount = 0;
    kreaPoolQueryCount = 0;
    kreaPoolPages = 0;
    renderKreaPromptPool();
    toast(`Krea 2 提示词池读取失败：${error.message}`);
  }
}
function openKreaPromptItemDialog(item = null) {
  editingKreaPromptItem = item ? { id: String(item.id), text: String(item.text || item.prompt || "") } : null;
  ui.kreaPromptItemTitle.textContent = editingKreaPromptItem ? "编辑自然语言提示词" : "新增自然语言提示词";
  ui.kreaPromptItemText.value = editingKreaPromptItem?.text || "";
  ui.kreaPromptItemDelete.hidden = !editingKreaPromptItem;
  ui.kreaPromptItemDialog.showModal();
  ui.kreaPromptItemText.focus({ preventScroll: true });
}
async function saveKreaPromptItem(event) {
  event.preventDefault();
  const text = ui.kreaPromptItemText.value.trim();
  if (!text) return;
  const previousId = editingKreaPromptItem?.id || "";
  try {
    const payload = await request("/api/prompt-pools/items", {
      method: editingKreaPromptItem ? "PUT" : "POST",
      body: JSON.stringify({ model_profile: "krea2", ...(previousId ? { id: previousId } : {}), text }),
    });
    const item = payload.item || payload;
    if (Number.isFinite(Number(item.count))) kreaPoolTotalCount = Number(item.count);
    const id = String(item.id || "");
    if (previousId) kreaPromptPoolSelection = kreaPromptPoolSelection.filter((value) => value !== previousId);
    if (id) kreaPromptPoolSelection = [...new Set([...kreaPromptPoolSelection, id])];
    if (kreaPromptPoolSelection.length) ui.krea_prompt_pool_enabled.checked = true;
    ui.kreaPromptItemDialog.close();
    editingKreaPromptItem = null;
    kreaSelectedViewItems = null;
    await loadKreaPromptPool();
    schedulePersist();
    toast(previousId ? "提示词已更新并选中" : "提示词已新增并选中");
  } catch (error) {
    toast(error.message);
  }
}
async function deleteKreaPromptPoolSelection(ids = kreaPromptPoolSelection) {
  const targets = [...new Set(ids.map(String))];
  if (!targets.length) return;
  const question = targets.length === 1 ? "删除选中的 1 条 Krea 2 提示词？" : `删除选中的 ${targets.length} 条 Krea 2 提示词？`;
  if (!confirm(question)) return;
  try {
    let payload;
    if (targets.length === 1) {
      payload = await request(`/api/prompt-pools/items?model_profile=krea2&id=${encodeURIComponent(targets[0])}`, { method: "DELETE" });
    } else {
      payload = await request("/api/prompt-pools/items/delete", {
        method: "POST",
        body: JSON.stringify({ model_profile: "krea2", ids: targets }),
      });
    }
    if (Number.isFinite(Number(payload?.count))) kreaPoolTotalCount = Number(payload.count);
    kreaPromptPoolSelection = kreaPromptPoolSelection.filter((id) => !targets.includes(id));
    if (!kreaPromptPoolSelection.length) ui.krea_prompt_pool_enabled.checked = false;
    if (editingKreaPromptItem && targets.includes(editingKreaPromptItem.id)) ui.kreaPromptItemDialog.close();
    kreaSelectedViewItems = null;
    await loadKreaPromptPool();
    schedulePersist();
    toast(`已删除 ${targets.length} 条提示词`);
  } catch (error) {
    toast(error.message);
  }
}
function openKreaPromptImportDialog() {
  kreaImportRequest += 1;
  kreaImportPreview = null;
  ui.kreaPromptPoolFile.value = "";
  ui.kreaPromptPoolText.value = "";
  ui.kreaPromptImportFormat.value = "auto";
  ui.kreaPromptImportMode.value = "append";
  ui.kreaPromptImportHint.textContent = "选择文件或粘贴内容后，先校验预览，再确认写入。";
  ui.kreaPromptImportSummary.hidden = true;
  ui.kreaPromptImportRows.replaceChildren();
  ui.kreaPromptImportCommit.disabled = true;
  ui.kreaPromptImportCommit.textContent = "确认追加";
  ui.kreaPromptImportDialog.showModal();
  ui.kreaPromptPoolText.focus({ preventScroll: true });
}
function resolveKreaImportFormat(content) {
  const selected = ui.kreaPromptImportFormat.value;
  if (selected !== "auto") return selected;
  const text = String(content).replace(/^\uFEFF/, "").trim();
  if (/^[\[{]/.test(text)) return "json";
  const firstLine = text.split(/\r?\n/, 1)[0] || "";
  if (/(^|,)\s*"?(?:text|prompt|提示词|自然语言)"?\s*(,|$)/i.test(firstLine)) return "csv";
  return "text";
}
function invalidateKreaPromptImportPreview(message = "内容或格式已变化，请重新校验并预览。") {
  kreaImportRequest += 1;
  kreaImportPreview = null;
  ui.kreaPromptImportSummary.hidden = true;
  ui.kreaPromptImportRows.replaceChildren();
  ui.kreaPromptImportCommit.disabled = true;
  ui.kreaPromptImportCommit.textContent = ui.kreaPromptImportMode.value === "replace" ? "确认替换" : "确认追加";
  ui.kreaPromptImportHint.textContent = message;
}
async function previewKreaPromptImport() {
  const content = ui.kreaPromptPoolText.value;
  if (!content.trim()) {
    kreaImportPreview = null;
    ui.kreaPromptImportCommit.disabled = true;
    ui.kreaPromptImportHint.textContent = "请先选择文件或输入提示词内容。";
    return;
  }
  const requestId = ++kreaImportRequest;
  ui.kreaPromptImportCommit.disabled = true;
  ui.kreaPromptImportHint.textContent = "正在校验导入内容…";
  try {
    const payload = await request("/api/prompt-pools/import/preview", {
      method: "POST",
      body: JSON.stringify({
        model_profile: "krea2",
        mode: ui.kreaPromptImportMode.value,
        format: resolveKreaImportFormat(content),
        content,
      }),
    });
    if (requestId !== kreaImportRequest) return;
    kreaImportPreview = {
      ...payload,
      source: content,
      mode: ui.kreaPromptImportMode.value,
      format: resolveKreaImportFormat(content),
    };
    renderKreaPromptImportPreview();
  } catch (error) {
    if (requestId !== kreaImportRequest) return;
    kreaImportPreview = null;
    ui.kreaPromptImportSummary.hidden = true;
    ui.kreaPromptImportRows.replaceChildren();
    ui.kreaPromptImportCommit.disabled = true;
    ui.kreaPromptImportHint.textContent = `校验失败：${error.message}`;
  }
}
function renderKreaPromptImportPreview() {
  if (!kreaImportPreview) return;
  const summary = kreaImportPreview.summary || {};
  const items = Array.isArray(kreaImportPreview.items) ? kreaImportPreview.items : [];
  const getNumber = (...keys) => {
    for (const key of keys) if (Number.isFinite(Number(summary[key]))) return Number(summary[key]);
    return null;
  };
  const duplicateCount = getNumber("duplicates", "duplicate", "duplicate_count", "duplicates_count", "repeated", "repeat_count");
  const newCount = getNumber("new", "new_count", "added", "importable") ?? items.length;
  const existingCount = getNumber("existingDuplicates", "existing_duplicates", "existing", "existing_count", "already_exists");
  const inputCount = getNumber("input", "input_count", "total", "total_count") ?? items.length + (duplicateCount || 0);
  const summaryParts = [
    `${inputCount} 条输入`,
    `${newCount} 条新增`,
    ...(duplicateCount === null ? [] : [`${duplicateCount} 条输入重复`]),
    ...(existingCount === null ? [] : [`${existingCount} 条与词池重复`]),
  ];
  ui.kreaPromptImportSummary.hidden = false;
  ui.kreaPromptImportSummary.replaceChildren(...summaryParts.map((label) => {
    const tag = document.createElement("span");
    tag.textContent = label;
    return tag;
  }));
  const rows = items.slice(0, 100).map((value, index) => {
    const text = typeof value === "string" ? value : String(value?.text || value?.prompt || "");
    const row = document.createElement("div");
    row.className = "krea-import-row";
    const ordinal = document.createElement("small");
    ordinal.textContent = String(index + 1);
    const prompt = document.createElement("span");
    prompt.textContent = text;
    row.append(ordinal, prompt);
    return row;
  });
  if (items.length > 100) {
    const more = document.createElement("div");
    more.className = "krea-import-more";
    more.textContent = `其余 ${items.length - 100} 条已校验，不在预览中展开。`;
    rows.push(more);
  }
  ui.kreaPromptImportRows.replaceChildren(...rows);
  const mode = kreaImportPreview.mode;
  ui.kreaPromptImportHint.textContent = mode === "replace"
    ? "预览通过后，确认将用这些待导入词条替换整个 Krea 2 词池。"
    : "预览通过后，确认将待导入词条追加到 Krea 2 词池。重复项会按不区分大小写的规则去重。";
  ui.kreaPromptImportCommit.disabled = items.length === 0;
  ui.kreaPromptImportCommit.textContent = mode === "replace" ? `确认替换（${items.length} 项）` : `确认追加（${items.length} 项）`;
}
async function commitKreaPromptImport() {
  if (!kreaImportPreview || kreaImportPreview.source !== ui.kreaPromptPoolText.value || kreaImportPreview.mode !== ui.kreaPromptImportMode.value || kreaImportPreview.format !== resolveKreaImportFormat(ui.kreaPromptPoolText.value)) {
    await previewKreaPromptImport();
    return;
  }
  const mode = kreaImportPreview.mode;
  const items = Array.isArray(kreaImportPreview.items) ? kreaImportPreview.items : [];
  if (!items.length) return;
  ui.kreaPromptImportCommit.disabled = true;
  try {
    const payload = await request("/api/prompt-pools/import", {
      method: "POST",
      body: JSON.stringify({ model_profile: "krea2", mode, items }),
    });
    if (Number.isFinite(Number(payload?.count))) kreaPoolTotalCount = Number(payload.count);
    const importedIds = Array.isArray(payload.imported_ids)
      ? payload.imported_ids.map(String)
      : Array.isArray(payload.importedIds) ? payload.importedIds.map(String) : [];
    kreaPromptPoolSelection = mode === "replace"
      ? importedIds
      : [...new Set([...kreaPromptPoolSelection, ...importedIds])];
    ui.krea_prompt_pool_enabled.checked = kreaPromptPoolSelection.length > 0;
    ui.kreaPromptImportDialog.close();
    kreaImportPreview = null;
    kreaSelectedViewItems = null;
    await loadKreaPromptPool({ force: true });
    schedulePersist();
    toast(mode === "replace" ? "Krea 2 词池已替换" : `Krea 2 词池已追加 ${importedIds.length} 项`);
  } catch (error) {
    ui.kreaPromptImportCommit.disabled = false;
    toast(error.message);
  }
}

function poolQueryBody() {
  const view = currentView();
  return {
    model_profile: currentProfile(),
    page: poolPage,
    limit: 48,
    q: view.query,
    sort: view.sort,
    collection: view.filters.collection,
    custom_group: view.filters.customGroup,
    categories: view.filters.categories,
    traits: view.filters.traits,
    gender: view.filters.gender,
    hair: view.filters.hair,
    eye: view.filters.eye,
    series: view.filters.series,
  };
}
async function loadPool() {
  const requestedProfile = currentProfile();
  const requestId = ++poolRequest;
  if (requestedProfile === "krea2") {
    await loadKreaPromptPool();
    return;
  }
  const view = currentView();
  try {
    const query = poolQueryBody();
    let data;
    if (view.selectedOnly)
      data = await request(`/api/pools/${activeSection}/query`, {
        method: "POST",
        body: JSON.stringify({ ...query, selection: draft.pools[activeSection] }),
      });
    else {
      const params = new URLSearchParams({
        model_profile: query.model_profile,
        page: query.page,
        limit: query.limit,
        q: query.q,
        sort: query.sort,
        collection: query.collection,
        custom_group: query.custom_group,
        gender: query.gender,
        hair: query.hair,
        eye: query.eye,
        series: query.series,
      });
      query.categories.forEach((value) => params.append("category", value));
      query.traits.forEach((value) => params.append("trait", value));
      data = await request(`/api/pools/${activeSection}?${params}`);
    }
    if (requestId !== poolRequest || requestedProfile !== currentProfile()) return;
    poolItems = data.items || [];
    poolFacets = data.facets || {};
    poolPages = data.pages;
    poolPage = data.page;
    if (activeSection === "pose") poolItems.forEach((item) => poseItemCache.set(item.id, item));
    ui.poolPageLabel.textContent = `${data.page} / ${data.pages}`;
    ui.poolPrev.disabled = data.page <= 1;
    ui.poolNext.disabled = data.page >= data.pages;
    ui.poolMeta.textContent = `${data.total} 项可用 · ${selectedCount(activeSection)} 项已选${favoritesAvailable ? "" : " · 收藏离线"}`;
    renderPoolSidebar();
    renderPoolItems();
    updatePoseConflict();
    requestAnimationFrame(() => {
      if (view.scrollTop && poolPage === 1) {
        ui.poolGrid.scrollTop = view.scrollTop;
        view.scrollTop = 0;
      }
    });
  } catch (error) {
    if (requestId !== poolRequest) return;
    toast(error.message);
  }
}

function sidebarSection(title, items, kind, activeValues, multi = false) {
  if (!items?.length) return null;
  const section = document.createElement("section");
  section.className = "facet-section";
  section.innerHTML = `<h3>${escapeHtml(title)}</h3><div></div>`;
  const body = section.querySelector("div");
  const active = new Set(Array.isArray(activeValues) ? activeValues : [activeValues].filter(Boolean));
  for (const item of items) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = active.has(item.value) ? "active" : "";
    // 英文优先显示规范值;中文优先/双语显示服务端给出的中文标签(无中文时回退英文)
    const label = currentView().language === "en" ? item.label : item.label_zh || item.label;
    button.innerHTML = `<span>${escapeHtml(label)}</span><small>${item.count ?? ""}</small>`;
    button.addEventListener("click", () => {
      const filters = currentView().filters;
      if (multi) {
        const values = new Set(filters[kind] || []);
        values.has(item.value) ? values.delete(item.value) : values.add(item.value);
        filters[kind] = [...values];
      } else filters[kind] = filters[kind] === item.value ? "" : item.value;
      poolPage = 1;
      schedulePersist();
      loadPool();
    });
    body.append(button);
  }
  return section;
}

function favoriteChildren(parentId) {
  return (favoritesData.groups || []).filter((group) => (group.parentId || null) === (parentId || null));
}
function favoriteGroup(id) {
  return (favoritesData.groups || []).find((group) => group.id === id) || null;
}
function favoriteDescendantIds(groupId) {
  const ids = new Set();
  const pending = [groupId];
  while (pending.length) {
    const id = pending.pop();
    if (ids.has(id)) continue;
    ids.add(id);
    favoriteChildren(id).forEach((group) => pending.push(group.id));
  }
  return ids;
}
function favoriteGroupPath(group) {
  const names = [];
  const seen = new Set();
  let current = group;
  while (current && !seen.has(current.id)) {
    seen.add(current.id);
    names.unshift(current.id === "default" ? "我的收藏" : current.name);
    current = current.parentId ? favoriteGroup(current.parentId) : null;
  }
  return names.join(" / ");
}
function customChildren(parentId) {
  return customGroups.filter((group) => (group.parentId || null) === (parentId || null));
}
function customGroup(id) {
  return customGroups.find((group) => group.id === id) || null;
}
function customDescendantIds(groupId) {
  const ids = new Set();
  const pending = [groupId];
  while (pending.length) {
    const id = pending.pop();
    if (ids.has(id)) continue;
    ids.add(id);
    customChildren(id).forEach((group) => pending.push(group.id));
  }
  return ids;
}
function customGroupPath(group) {
  const names = [];
  const seen = new Set();
  let current = group;
  while (current && !seen.has(current.id)) {
    seen.add(current.id);
    names.unshift(current.name);
    current = current.parentId ? customGroup(current.parentId) : null;
  }
  return names.join(" / ");
}
function persistFavoriteTree() {
  const value = Object.fromEntries(SECTIONS.map((section) => [section, [...collapsedFavoriteGroups[section]]]));
  localStorage.setItem(GROUP_TREE_KEY, JSON.stringify(value));
}
function persistCustomTree() {
  const value = Object.fromEntries(SECTIONS.map((section) => [section, [...collapsedCustomGroups[section]]]));
  localStorage.setItem(CUSTOM_GROUP_TREE_KEY, JSON.stringify(value));
}
function focusFavoriteTreeRow(groupId) {
  queueMicrotask(() => {
    const row = ui.poolSidebar.querySelector(`.favorite-tree-row[data-group-id="${CSS.escape(groupId)}"]`);
    if (!row) return;
    ui.poolSidebar.querySelectorAll('.favorite-tree [role="treeitem"]').forEach((item) => {
      item.tabIndex = item === row ? 0 : -1;
    });
    row.focus();
  });
}
// Rows belonging to a group's descendant set, looked up pre-render.
function treeRowsForGroups(ids) {
  return [...ui.poolSidebar.querySelectorAll(".favorite-tree-row[data-group-id]")]
    .filter((row) => ids.has(row.dataset.groupId));
}
function animateTreeRowsIn(rows) {
  if (imagePreviewReducedMotion()) return;
  rows.forEach((row, index) => {
    row.animate(
      [{ opacity: 0, transform: "translateX(-8px)" }, { opacity: 1, transform: "none" }],
      { duration: 240, delay: Math.min(index * 30, 150), easing: "cubic-bezier(.32,.72,0,1)", fill: "both" },
    );
  });
}
function animateTreeRowsOut(rows, done) {
  if (!rows.length || imagePreviewReducedMotion()) return done();
  let remaining = rows.length;
  const finish = () => { if (--remaining <= 0) done(); };
  rows.forEach((row) => {
    row.animate(
      [{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateX(-8px)" }],
      { duration: 160, easing: "cubic-bezier(.4,0,1,1)", fill: "both" },
    ).finished.then(finish).catch(finish);
  });
}
// Re-render the sidebar and play an entry animation on rows that did not exist
// before the render (i.e. freshly expanded children).
function renderPoolSidebarAnimated(enteredIds = new Set()) {
  const before = new Set(
    [...ui.poolSidebar.querySelectorAll(".favorite-tree-row[data-group-id]")].map((row) => row.dataset.groupId),
  );
  renderPoolSidebar();
  const fresh = enteredIds.size
    ? treeRowsForGroups(enteredIds).filter((row) => !before.has(row.dataset.groupId))
    : [];
  animateTreeRowsIn(fresh);
}

function toggleFavoriteTreeGroup(groupId, restoreFocus = false) {
  const collapsed = collapsedFavoriteGroups[activeSection];
  const collapsing = !collapsed.has(groupId);
  const descendants = favoriteDescendantIds(groupId);
  if (collapsing) {
    collapsed.add(groupId);
    persistFavoriteTree();
    animateTreeRowsOut(treeRowsForGroups(descendants), () => {
      renderPoolSidebar();
      if (restoreFocus) focusFavoriteTreeRow(groupId);
    });
    return;
  }
  collapsed.delete(groupId);
  persistFavoriteTree();
  renderPoolSidebarAnimated(descendants);
  if (restoreFocus) focusFavoriteTreeRow(groupId);
}
function focusCustomTreeRow(groupId) {
  queueMicrotask(() => {
    const tree = ui.poolSidebar.querySelector(".custom-group-tree");
    const row = tree?.querySelector(`.favorite-tree-row[data-group-id="${CSS.escape(groupId)}"]`);
    if (!row) return;
    tree.querySelectorAll('[role="treeitem"]').forEach((item) => {
      item.tabIndex = item === row ? 0 : -1;
    });
    row.focus();
  });
}
function toggleCustomTreeGroup(groupId, restoreFocus = false) {
  const collapsed = collapsedCustomGroups[activeSection];
  const collapsing = !collapsed.has(groupId);
  const descendants = customDescendantIds(groupId);
  if (collapsing) {
    collapsed.add(groupId);
    persistCustomTree();
    animateTreeRowsOut(treeRowsForGroups(descendants), () => {
      renderPoolSidebar();
      if (restoreFocus) focusCustomTreeRow(groupId);
    });
    return;
  }
  collapsed.delete(groupId);
  persistCustomTree();
  renderPoolSidebarAnimated(descendants);
  if (restoreFocus) focusCustomTreeRow(groupId);
}
function renderFavoriteTree(container, view) {
  container.className = "favorite-tree";
  container.setAttribute("role", "tree");
  container.setAttribute("aria-label", "收藏分组树");
  const allRow = document.createElement("div");
  allRow.className = `favorite-tree-row favorite-all-row${view.filters.collection ? "" : " active"}`;
  allRow.setAttribute("role", "treeitem");
  allRow.setAttribute("aria-level", "1");
  allRow.tabIndex = view.filters.collection ? -1 : 0;
  allRow.innerHTML = `<span class="tree-spacer" aria-hidden="true"></span><button type="button" class="group-select"><span>全部${escapeHtml(SECTION_META[activeSection].label)}</span><small>${config.catalog?.counts?.[activeSection] || ""}</small></button><span></span>`;
  allRow.querySelector(".group-select").addEventListener("click", () => {
    view.filters.collection = "";
    poolPage = 1;
    loadPool();
    schedulePersist();
    renderPoolSidebar();
  });
  container.append(allRow);

  const collapsed = collapsedFavoriteGroups[activeSection];
  const appendGroup = (group, depth) => {
    const children = favoriteChildren(group.id);
    const isCollapsed = collapsed.has(group.id);
    const active = view.filters.collection === group.id;
    const row = document.createElement("div");
    row.className = `favorite-tree-row${active ? " active" : ""}`;
    row.dataset.groupId = group.id;
    row.dataset.parentId = group.parentId || "";
    row.style.setProperty("--tree-depth", String(Math.min(depth, 4)));
    row.title = favoriteGroupPath(group);
    row.setAttribute("role", "treeitem");
    row.setAttribute("aria-level", String(depth + 1));
    row.setAttribute("aria-selected", String(active));
    if (children.length) row.setAttribute("aria-expanded", String(!isCollapsed));
    row.tabIndex = active ? 0 : -1;
    const disclosure = document.createElement(children.length ? "button" : "span");
    disclosure.className = children.length ? "tree-disclosure" : "tree-spacer";
    if (children.length) {
      disclosure.type = "button";
      disclosure.tabIndex = -1;
      disclosure.title = isCollapsed ? "展开子分组" : "折叠子分组";
      disclosure.setAttribute("aria-label", disclosure.title);
      disclosure.textContent = "›";
      disclosure.addEventListener("click", (event) => {
        event.stopPropagation();
        toggleFavoriteTreeGroup(group.id, true);
      });
    }
    const button = document.createElement("button");
    button.type = "button";
    button.className = "group-select";
    button.innerHTML = `<span>${escapeHtml(group.id === "default" ? "我的收藏" : group.name)}</span><small>${group.totalCount ?? 0}</small>`;
    button.addEventListener("click", () => {
      view.filters.collection = group.id;
      poolPage = 1;
      loadPool();
      schedulePersist();
      renderPoolSidebar();
    });
    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "collection-edit";
    edit.title = group.isSystem ? "管理子分组" : "管理收藏分组";
    edit.setAttribute("aria-label", `${edit.title} ${group.id === "default" ? "我的收藏" : group.name}`);
    edit.textContent = "···";
    edit.addEventListener("click", (event) => {
      event.stopPropagation();
      openGroupDialog(group);
    });
    row.append(disclosure, button, edit);
    container.append(row);
    if (!isCollapsed) children.forEach((child) => appendGroup(child, depth + 1));
  };
  favoriteChildren(null).forEach((group) => appendGroup(group, 0));
}
function renderCustomTree(container, view) {
  container.className = "favorite-tree custom-group-tree";
  container.setAttribute("role", "tree");
  container.setAttribute("aria-label", "自定义分组树");
  const allRow = document.createElement("div");
  allRow.className = `favorite-tree-row favorite-all-row${view.filters.customGroup ? "" : " active"}`;
  allRow.dataset.groupId = "";
  allRow.setAttribute("role", "treeitem");
  allRow.setAttribute("aria-level", "1");
  allRow.tabIndex = view.filters.customGroup ? -1 : 0;
  allRow.innerHTML = '<span class="tree-spacer" aria-hidden="true"></span><button type="button" class="group-select"><span>不限分组</span><small></small></button><span></span>';
  allRow.querySelector(".group-select").addEventListener("click", async () => {
    view.filters.customGroup = "";
    poolPage = 1;
    schedulePersist();
    renderPoolSidebar();
    focusCustomTreeRow("");
    await loadPool();
    if (!view.filters.customGroup) focusCustomTreeRow("");
  });
  container.append(allRow);

  const collapsed = collapsedCustomGroups[activeSection];
  const appendGroup = (group, depth) => {
    const children = customChildren(group.id);
    const isCollapsed = collapsed.has(group.id);
    const active = view.filters.customGroup === group.id;
    const row = document.createElement("div");
    row.className = `favorite-tree-row${active ? " active" : ""}`;
    row.dataset.groupId = group.id;
    row.dataset.parentId = group.parentId || "";
    row.style.setProperty("--tree-depth", String(Math.min(depth, 4)));
    row.title = customGroupPath(group);
    row.setAttribute("role", "treeitem");
    row.setAttribute("aria-level", String(depth + 1));
    row.setAttribute("aria-selected", String(active));
    if (children.length) row.setAttribute("aria-expanded", String(!isCollapsed));
    row.tabIndex = active ? 0 : -1;
    const disclosure = document.createElement(children.length ? "button" : "span");
    disclosure.className = children.length ? "tree-disclosure" : "tree-spacer";
    if (children.length) {
      disclosure.type = "button";
      disclosure.tabIndex = -1;
      disclosure.title = isCollapsed ? "展开子分组" : "折叠子分组";
      disclosure.setAttribute("aria-label", disclosure.title);
      disclosure.textContent = "›";
      disclosure.addEventListener("click", (event) => {
        event.stopPropagation();
        toggleCustomTreeGroup(group.id, true);
      });
    }
    const button = document.createElement("button");
    button.type = "button";
    button.className = "group-select";
    button.innerHTML = `<span>${escapeHtml(group.name)}</span><small>${group.count || 0}</small>`;
    button.addEventListener("click", async () => {
      view.filters.customGroup = group.id;
      poolPage = 1;
      schedulePersist();
      renderPoolSidebar();
      focusCustomTreeRow(group.id);
      await loadPool();
      if (view.filters.customGroup === group.id) focusCustomTreeRow(group.id);
    });
    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "collection-edit";
    edit.title = group.kind === "group" ? "编辑自定义分组" : "编辑导入大项";
    edit.setAttribute("aria-label", `${edit.title} ${group.name}`);
    edit.textContent = "···";
    edit.addEventListener("click", (event) => {
      event.stopPropagation();
      openCustomGroupDialog(group);
    });
    row.append(disclosure, button, edit);
    container.append(row);
    if (!isCollapsed) children.forEach((child) => appendGroup(child, depth + 1));
  };
  customChildren(null).forEach((group) => appendGroup(group, 0));
}
function handleGroupTreeKeydown(event) {
  const row = event.target.closest('[role="treeitem"]');
  const tree = row?.closest(".favorite-tree");
  if (!row || !tree) return;
  const rows = [...tree.querySelectorAll('[role="treeitem"]')];
  const index = rows.indexOf(row);
  const groupId = row.dataset.groupId || "";
  const isCustom = tree.classList.contains("custom-group-tree");
  const group = groupId ? (isCustom ? customGroup(groupId) : favoriteGroup(groupId)) : null;
  const toggle = isCustom ? toggleCustomTreeGroup : toggleFavoriteTreeGroup;
  if (event.key === "ArrowDown" || event.key === "ArrowUp") {
    event.preventDefault();
    const next = rows[index + (event.key === "ArrowDown" ? 1 : -1)];
    if (next) {
      row.tabIndex = -1;
      next.tabIndex = 0;
      next.focus();
    }
  } else if (event.key === "ArrowRight" && group) {
    event.preventDefault();
    if (row.getAttribute("aria-expanded") === "false") toggle(groupId, true);
    else {
      const next = rows[index + 1];
      if (next) {
        row.tabIndex = -1;
        next.tabIndex = 0;
        next.focus();
      }
    }
  } else if (event.key === "ArrowLeft" && group) {
    event.preventDefault();
    if (row.getAttribute("aria-expanded") === "true") toggle(groupId, true);
    else if (group.parentId) {
      const parent = rows.find((item) => item.dataset.groupId === group.parentId);
      if (parent) {
        row.tabIndex = -1;
        parent.tabIndex = 0;
        parent.focus();
      }
    }
  } else if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    row.querySelector(".group-select")?.click();
  }
}

function renderPoolSidebar() {
  const view = currentView();
  const nav = document.createDocumentFragment();
  const collections = document.createElement("section");
  collections.className = "facet-section collections";
  collections.innerHTML = `<div class="facet-heading"><h3>收藏分组</h3><button type="button" title="新建收藏分组" aria-label="新建收藏分组">+</button></div><div></div>`;
  collections.querySelector(".facet-heading button").disabled = !favoritesAvailable;
  collections.querySelector(".facet-heading button").addEventListener("click", () => openGroupDialog());
  const collectionBody = collections.lastElementChild;
  renderFavoriteTree(collectionBody, view);
  nav.append(collections);
  const customSection = document.createElement("section");
  customSection.className = "facet-section custom-group-section";
  customSection.innerHTML = `<div class="facet-heading"><h3>自定义分组</h3><button type="button" title="新建自定义分组" aria-label="新建自定义分组">+</button></div><div></div>`;
  customSection.querySelector(".facet-heading button").addEventListener("click", () => openCustomGroupDialog());
  const customBody = customSection.lastElementChild;
  renderCustomTree(customBody, view);
  nav.append(customSection);
  if (activeSection === "character") {
    nav.append(sidebarSection("角色性别", poolFacets.gender, "gender", view.filters.gender));
    nav.append(sidebarSection("角色发色", poolFacets.hair, "hair", view.filters.hair));
    nav.append(sidebarSection("角色瞳色", poolFacets.eye, "eye", view.filters.eye));
    nav.append(sidebarSection("热门作品系列", poolFacets.series, "series", view.filters.series));
  } else {
    const labels = NATIVE_FACET_LABELS[activeSection];
    nav.append(sidebarSection(labels.categories, poolFacets.categories, "categories", view.filters.categories, true));
    nav.append(sidebarSection(labels.traits, poolFacets.traits, "traits", view.filters.traits, true));
  }
  ui.poolSidebar.replaceChildren(nav);
}

function displayTitle(item) {
  const lang = currentView().language;
  // 角色条目:title 是罗马字原名,title_zh 来自内置译名表(未收录时为空)。
  if (item.section === "character") {
    const zh = item.title_zh || "";
    if (lang === "en" || !zh) return item.title;
    if (lang === "bilingual") return `${zh} / ${item.title}`;
    return zh;
  }
  if (lang === "en") return item.subtitle || item.title;
  if (lang === "bilingual" && item.subtitle && item.subtitle !== item.title) return `${item.title} / ${item.subtitle}`;
  return item.title;
}
function seriesDisplay(item) {
  if (!item.copyright) return "";
  if (currentView().language === "en") return item.copyright;
  return item.subtitle_zh || item.copyright;
}
function categoryDisplay(value) {
  // 双语分类「中文 (English)」按语言偏好取半边;纯中文/纯英文原样。
  const match = String(value ?? "").match(/^(.*?)\s*\(([^()]*)\)\s*$/);
  if (!match || !match[1].trim()) return String(value ?? "");
  return currentView().language === "en" ? match[2].trim() : match[1].trim();
}
function itemBadges(item) {
  const values = [];
  if (activeSection === "character") {
    values.push(item.gender === "1girl" ? "女性" : item.gender === "1boy" ? "男性" : "未标注");
    if (item.hair) values.push(`${item.hair} hair`);
    if (item.eye) values.push(`${item.eye} eyes`);
  } else {
    values.push(...(item.categories || []).slice(0, 1).map(categoryDisplay));
    values.push(...(item.traits || []).slice(0, 2));
  }
  return values.filter(Boolean);
}
function renderPoolItems() {
  const favMap = favoriteMap();
  ui.poolEmpty.hidden = poolItems.length > 0;
  ui.poolGrid.replaceChildren(
    ...poolItems.map((item) => {
      const article = document.createElement("article");
      const selected = selectionHas(activeSection, item.id);
      const savedFavorite = favMap.get(favoriteKey(item));
      const favorite = Boolean(savedFavorite?.groupIds?.length);
      article.className = `pool-item ${selected ? "selected" : ""}${favorite ? " favorite" : ""}`;
      article.innerHTML = `<label><input type="checkbox" ${selected ? "checked" : ""}><span class="pool-thumb">${item.preview ? `<img loading="lazy" src="${escapeHtml(item.preview)}" alt="">` : "<span>+</span>"}</span><span class="pool-item-copy"><strong title="${escapeHtml(displayTitle(item))}">${escapeHtml(displayTitle(item))}</strong><span class="item-badges">${itemBadges(
        item,
      )
        .map((value) => `<em>${escapeHtml(value)}</em>`)
        .join(
          "",
        )}</span><small>${escapeHtml(savedFavorite?.nickname || seriesDisplay(item) || item.prompt || "自定义项")}</small></span></label><div class="item-tools"><button class="favorite-toggle ${favorite ? "active" : ""}" type="button" title="${favorite ? "取消收藏" : "加入收藏"}" aria-label="${favorite ? "取消收藏" : "加入收藏"}">${favorite ? "♥" : "♡"}</button><button class="favorite-settings" type="button" title="收藏设置" aria-label="收藏设置" ${favorite ? "" : "hidden"}>⋮</button><button class="item-edit" type="button" title="编辑自定义项" aria-label="编辑自定义项" ${item.builtin ? "hidden" : ""}>✎</button></div>`;
      article
        .querySelector("label input")
        .addEventListener("change", (event) => toggleSelection(activeSection, item.id, event.target.checked));
      article.querySelector("img")?.addEventListener("error", (event) => (event.target.style.display = "none"));
      article.querySelector(".favorite-toggle").disabled = !favoritesAvailable;
      article.querySelector(".favorite-toggle").addEventListener("click", (event) => {
        event.stopPropagation();
        toggleFavorite(item, !favorite);
      });
      article.querySelector(".favorite-settings").addEventListener("click", (event) => {
        event.stopPropagation();
        openFavoriteDialog(item);
      });
      article.querySelector(".item-edit").addEventListener("click", (event) => {
        event.stopPropagation();
        openCustom(item);
      });
      return article;
    }),
  );
  ui.poolSelectionLabel.textContent = `已选择 ${selectedCount(activeSection)} 项`;
  updateFavoriteSelectionButton();
}

function updatePoseConflict() {
  if (activeSection !== "pose") {
    ui.poseConflict.hidden = true;
    return;
  }
  const selection = draft.pools.pose;
  if (selection.mode === "all" || Number(draft.counts.pose || 1) <= 1) {
    ui.poseConflict.hidden = true;
    return;
  }
  const selected = selection.ids.map((id) => poseItemCache.get(id)).filter(Boolean);
  const groups = new Map();
  for (const item of selected)
    for (const slot of item.conflict_slots || []) {
      if (!groups.has(slot)) groups.set(slot, []);
      groups.get(slot).push(item.title);
    }
  const conflicts = [...groups.entries()].filter(([, names]) => names.length > 1);
  if (!conflicts.length) {
    ui.poseConflict.hidden = true;
    return;
  }
  const labels = { hand_action: "手部动作", body_pose: "整体姿态", interaction: "双人互动" };
  ui.poseConflict.hidden = false;
  ui.poseConflict.innerHTML = `<strong>存在潜在姿势冲突</strong><span>${conflicts.map(([slot, names]) => `${escapeHtml(labels[slot] || slot)}：${names.map(escapeHtml).join("、")}`).join("；")}</span>`;
}

function reconcileFavoriteCollection() {
  const view = currentView();
  if (view.filters.collection && !favoriteGroup(view.filters.collection)) {
    view.filters.collection = "";
    schedulePersist();
    return true;
  }
  return false;
}
async function toggleFavorite(item, favorite) {
  try {
    favoritesData = await requestFavorites(`/api/favorites/${activeSection}/item`, {
      method: "PUT",
      body: JSON.stringify({ id: item.id, favorite }),
    });
    favoritesAvailable = true;
    const resetCollection = reconcileFavoriteCollection();
    if (resetCollection || (!favorite && currentView().filters.collection)) await loadPool();
    else {
      renderPoolItems();
      renderPoolSidebar();
    }
    toast(favorite ? "已加入 Anima 收藏" : "已取消收藏");
  } catch (error) {
    toast(error.message);
  }
}
function orderedFavoriteGroups() {
  const result = [];
  const append = (group, depth) => {
    result.push({ group, depth });
    favoriteChildren(group.id).forEach((child) => append(child, depth + 1));
  };
  favoriteChildren(null).forEach((group) => append(group, 0));
  return result;
}
function openFavoriteDialog(item) {
  editingFavoriteItem = item;
  const saved = favoriteMap().get(favoriteKey(item)) || {};
  ui.favoriteTitle.textContent = displayTitle(item);
  ui.favoriteNickname.value = saved.nickname || "";
  ui.favoriteGroups.replaceChildren(
    ...orderedFavoriteGroups().map(({ group, depth }) => {
      const label = document.createElement("label");
      label.className = "favorite-group-check tree-check";
      label.style.setProperty("--tree-depth", String(Math.min(depth, 4)));
      label.title = favoriteGroupPath(group);
      label.innerHTML = `<input type="checkbox" value="${escapeHtml(group.id)}" ${(saved.groupIds || []).includes(group.id) ? "checked" : ""}><span>${escapeHtml(group.id === "default" ? "我的收藏" : group.name)}</span><small>${group.totalCount ?? 0}</small>`;
      return label;
    }),
  );
  ui.favoriteDialog.showModal();
}
function favoriteSelectionDefaultGroups() {
  const currentCollection = currentView().filters.collection;
  if (currentCollection && favoriteGroup(currentCollection)) return [currentCollection];
  return favoriteGroup("default") ? ["default"] : [];
}
function renderFavoriteSelectionGroups(selectedIds = favoriteSelectionDefaultGroups()) {
  const selected = new Set(selectedIds);
  ui.favoriteSelectionGroups.replaceChildren(
    ...orderedFavoriteGroups().map(({ group, depth }) => {
      const label = document.createElement("label");
      label.className = "favorite-group-check tree-check";
      label.style.setProperty("--tree-depth", String(Math.min(depth, 4)));
      label.title = favoriteGroupPath(group);
      label.innerHTML = `<input type="checkbox" value="${escapeHtml(group.id)}" ${selected.has(group.id) ? "checked" : ""}><span>${escapeHtml(group.id === "default" ? "我的收藏" : group.name)}</span><small>${group.totalCount ?? 0}</small>`;
      return label;
    }),
  );
}
function openFavoriteSelectionDialog() {
  if (!favoritesAvailable || !selectedCount(activeSection)) return;
  ui.favoriteSelectionTitle.textContent = `收藏${SECTION_META[activeSection].label}池本次选择`;
  ui.favoriteSelectionSummary.textContent = `将当前${SECTION_META[activeSection].label}池已选择的 ${selectedCount(activeSection)} 项加入收藏分组；已有收藏只追加分组，不覆盖备注。`;
  ui.favoriteSelectionGroupName.value = "";
  ui.favoriteSelectionError.hidden = true;
  ui.favoriteSelectionError.textContent = "";
  ui.confirmFavoriteSelection.disabled = false;
  ui.confirmFavoriteSelection.textContent = "收藏";
  renderFavoriteSelectionGroups();
  ui.favoriteSelectionDialog.showModal();
}
async function createFavoriteSelectionGroup() {
  const name = ui.favoriteSelectionGroupName.value.trim();
  if (!name) {
    ui.favoriteSelectionError.textContent = "请输入收藏分组名称";
    ui.favoriteSelectionError.hidden = false;
    return;
  }
  ui.createFavoriteSelectionGroup.disabled = true;
  try {
    favoritesData = await requestFavorites(`/api/favorites/${activeSection}/groups`, {
      method: "POST",
      body: JSON.stringify({ name }),
    });
    const selectedIds = [...ui.favoriteSelectionGroups.querySelectorAll("input:checked")].map((input) => input.value);
    const newGroup = (favoritesData.groups || []).find((group) => group.name === name && !group.isSystem);
    if (newGroup) selectedIds.push(newGroup.id);
    renderFavoriteSelectionGroups([...new Set(selectedIds)]);
    ui.favoriteSelectionGroupName.value = "";
    ui.favoriteSelectionError.hidden = true;
    renderPoolSidebar();
  } catch (error) {
    ui.favoriteSelectionError.textContent = error.message;
    ui.favoriteSelectionError.hidden = false;
  } finally {
    ui.createFavoriteSelectionGroup.disabled = false;
  }
}
async function saveFavoriteSelection(event) {
  event.preventDefault();
  const groupIds = [...ui.favoriteSelectionGroups.querySelectorAll("input:checked")].map((input) => input.value);
  if (!groupIds.length) {
    ui.favoriteSelectionError.textContent = "至少选择一个收藏分组";
    ui.favoriteSelectionError.hidden = false;
    return;
  }
  ui.confirmFavoriteSelection.disabled = true;
  ui.confirmFavoriteSelection.textContent = "收藏中…";
  try {
    const payload = await request(`/api/favorites/${activeSection}/selection`, {
      method: "POST",
      body: JSON.stringify({ selection: draft.pools[activeSection], groupIds }),
    });
    favoritesData = payload;
    ui.favoriteSelectionDialog.close();
    renderPoolItems();
    renderPoolSidebar();
    toast(`已收藏 ${payload.selectedCount || 0} 项：新增 ${payload.createdCount || 0} 项，更新 ${payload.updatedCount || 0} 项`);
  } catch (error) {
    ui.favoriteSelectionError.textContent = error.message;
    ui.favoriteSelectionError.hidden = false;
  } finally {
    ui.confirmFavoriteSelection.disabled = false;
    ui.confirmFavoriteSelection.textContent = "收藏";
  }
}
async function saveFavorite(event) {
  event.preventDefault();
  if (!editingFavoriteItem) return;
  const groupIds = [...ui.favoriteGroups.querySelectorAll("input:checked")].map((input) => input.value);
  try {
    favoritesData = await requestFavorites(`/api/favorites/${activeSection}/item`, {
      method: "PUT",
      body: JSON.stringify({
        id: editingFavoriteItem.id,
        favorite: true,
        groupIds,
        nickname: ui.favoriteNickname.value,
      }),
    });
    ui.favoriteDialog.close();
    if (reconcileFavoriteCollection()) await loadPool();
    else {
      renderPoolItems();
      renderPoolSidebar();
    }
    toast("收藏设置已保存");
  } catch (error) {
    toast(error.message);
  }
}
async function removeFavorite() {
  if (!editingFavoriteItem) return;
  try {
    favoritesData = await requestFavorites(`/api/favorites/${activeSection}/item`, {
      method: "PUT",
      body: JSON.stringify({ id: editingFavoriteItem.id, favorite: false }),
    });
    ui.favoriteDialog.close();
    reconcileFavoriteCollection();
    await loadPool();
    toast("已取消收藏");
  } catch (error) {
    toast(error.message);
  }
}

function openGroupDialog(group = null) {
  editingGroup = group;
  ui.groupId.value = group?.id || "";
  ui.groupName.value = group?.name || "";
  ui.groupDialogTitle.textContent = !group ? "新建收藏分组" : group.isSystem ? "管理我的收藏" : "编辑收藏分组";
  ui.groupNameField.hidden = Boolean(group?.isSystem);
  ui.groupName.required = !group?.isSystem;
  ui.deleteGroup.hidden = !group || group.isSystem;
  ui.importChildGroup.hidden = !group;
  ui.saveGroupButton.hidden = Boolean(group?.isSystem);
  ui.groupDialog.showModal();
}
async function saveGroup(event) {
  event.preventDefault();
  const id = ui.groupId.value;
  try {
    favoritesData = await requestFavorites(
      id
        ? `/api/favorites/${activeSection}/groups/${encodeURIComponent(id)}`
        : `/api/favorites/${activeSection}/groups`,
      { method: id ? "PUT" : "POST", body: JSON.stringify({ name: ui.groupName.value }) },
    );
    ui.groupDialog.close();
    renderPoolSidebar();
    toast(id ? "收藏分组已更新" : "收藏分组已创建");
  } catch (error) {
    toast(error.message);
  }
}
function setChildGroupError(message = "") {
  ui.childGroupError.textContent = message;
  ui.childGroupError.hidden = !message;
}
function renderChildGroupSources() {
  const query = ui.childGroupSearch.value.trim().toLocaleLowerCase();
  const siblings = favoriteChildren(childGroupParent?.id);
  const available = customGroups.filter((group) => !query || group.name.toLocaleLowerCase().includes(query));
  ui.childGroupList.replaceChildren(
    ...available.map((group) => {
      const duplicate = siblings.some(
        (item) =>
          item.sourceCustomGroupId === group.id ||
          item.name.trim().toLocaleLowerCase() === group.name.trim().toLocaleLowerCase(),
      );
      const disabled = !group.count || duplicate;
      const label = document.createElement("label");
      label.className = `child-group-option${disabled ? " disabled" : ""}`;
      label.title = duplicate ? "当前父组下已经导入" : !group.count ? "空分组不能导入" : `导入 ${group.count} 项`;
      label.innerHTML = `<input type="radio" name="childGroupSource" value="${escapeHtml(group.id)}" ${selectedChildSource === group.id ? "checked" : ""} ${disabled ? "disabled" : ""}><span><strong>${escapeHtml(customGroupPath(group))}</strong><small>${duplicate ? "已导入" : !group.count ? "空分组" : `${group.count} 项 · 一次性快照`}</small></span>`;
      label.querySelector("input").addEventListener("change", () => {
        selectedChildSource = group.id;
        ui.confirmChildGroup.disabled = false;
        setChildGroupError();
      });
      return label;
    }),
  );
  ui.childGroupEmpty.textContent = customGroups.length ? "没有匹配的自定义分组。" : "当前分类还没有自定义分组。";
  ui.childGroupEmpty.hidden = available.length > 0;
}
function openChildGroupDialog(group = editingGroup) {
  if (!group) return;
  childGroupParent = group;
  selectedChildSource = "";
  ui.groupDialog.close();
  ui.childGroupTitle.textContent = `导入到“${group.id === "default" ? "我的收藏" : group.name}”`;
  ui.childGroupHint.textContent = "从当前提示词池的自定义分组创建一次性收藏快照，后续两边独立修改。";
  ui.childGroupSearch.value = "";
  ui.confirmChildGroup.disabled = true;
  setChildGroupError();
  renderChildGroupSources();
  ui.childGroupDialog.showModal();
}
async function importFavoriteChild(event) {
  event.preventDefault();
  if (!childGroupParent || !selectedChildSource || ui.confirmChildGroup.disabled) return;
  const label = ui.confirmChildGroup.textContent;
  ui.confirmChildGroup.disabled = true;
  ui.confirmChildGroup.textContent = "导入中…";
  try {
    favoritesData = await requestFavorites(
      `/api/favorites/${activeSection}/groups/${encodeURIComponent(childGroupParent.id)}/children/import`,
      { method: "POST", body: JSON.stringify({ customGroupId: selectedChildSource }) },
    );
    collapsedFavoriteGroups[activeSection].delete(childGroupParent.id);
    persistFavoriteTree();
    ui.childGroupDialog.close();
    renderPoolSidebar();
    if (currentView().filters.collection === childGroupParent.id) await loadPool();
    toast("收藏子分组已导入");
  } catch (error) {
    setChildGroupError(error.message);
  } finally {
    ui.confirmChildGroup.disabled = !selectedChildSource;
    ui.confirmChildGroup.textContent = label;
  }
}

function deleteStat(label, value) {
  return `<div><strong>${Number(value) || 0}</strong><span>${escapeHtml(label)}</span></div>`;
}
function openDeleteGroupDialog(kind, group) {
  pendingGroupDelete = { kind, group };
  const favorite = kind === "favorite";
  const subtree = favorite ? favoriteDescendantIds(group.id) : customDescendantIds(group.id);
  const directCount = Number(group.directCount || 0);
  const affectedFavorites = favorite
    ? (favoritesData.items || []).filter((item) => (item.groupIds || []).some((id) => subtree.has(id)))
    : [];
  const exclusiveCount = favorite
    ? affectedFavorites.filter((item) => !(item.groupIds || []).some((id) => !subtree.has(id))).length
    : Number(group.exclusiveCount || 0);
  const totalCount = favorite ? affectedFavorites.length : Number(group.count || 0);
  const sharedCount = Math.max(0, totalCount - exclusiveCount);
  const childCount = Number(group.childCount || 0);
  const canDeleteItems = favorite ? Boolean(group.parentId && !childCount && directCount) : totalCount > 0;
  ui.groupDialog.close();
  ui.customGroupDialog.close();
  ui.deleteGroupTitle.textContent = `删除“${group.id === "default" ? "我的收藏" : group.name}”`;
  ui.deleteGroupSummary.textContent =
    subtree.size > 1
      ? `将删除当前父组及 ${subtree.size - 1} 个后代分组，${favorite ? "收藏" : "自定义提示词"}默认保留。`
      : "默认只删除分组结构，条目保持不变。";
  ui.deleteGroupStats.innerHTML = favorite
    ? `${deleteStat("分组", subtree.size)}${deleteStat("聚合收藏", totalCount)}${deleteStat("独占收藏", exclusiveCount)}${deleteStat("共享收藏", sharedCount)}`
    : `${deleteStat("分组", subtree.size)}${deleteStat("聚合条目", totalCount)}${deleteStat("可删除独占项", exclusiveCount)}${deleteStat("保留共享项", sharedCount)}`;
  ui.deleteGroupKeep.checked = true;
  ui.deleteGroupKeepHint.textContent = favorite
    ? `${exclusiveCount} 项失去最后分组的收藏会移入“我的收藏”，其余收藏保留现有分组。`
    : "所有自定义提示词都会保留，仅解除当前分组关联。";
  ui.deleteGroupItemsOption.hidden = !canDeleteItems;
  ui.deleteGroupItemsTitle.textContent = favorite
    ? `同时删除 ${exclusiveCount} 项独占收藏`
    : `同时删除 ${exclusiveCount} 项独占提示词`;
  ui.deleteGroupItemsHint.textContent = sharedCount
    ? `${sharedCount} 项共享条目只会解除当前分组关联。`
    : "没有共享条目。";
  ui.deleteGroupAllOption.hidden = favorite;
  ui.deleteGroupAllHint.textContent = sharedCount
    ? `不可撤销：将永久删除全部 ${totalCount} 项内容，其中 ${sharedCount} 项也属于其他分组。`
    : `不可撤销：将永久删除该分组子树中的全部 ${totalCount} 项内容。`;
  ui.deleteGroupError.hidden = true;
  ui.deleteGroupError.textContent = "";
  ui.confirmDeleteGroup.disabled = false;
  ui.confirmDeleteGroup.dataset.storageWrite = favorite ? "" : "custom_prompts";
  ui.confirmDeleteGroup.textContent = "删除";
  ui.deleteGroupDialog.showModal();
}
async function confirmGroupDelete(event) {
  event.preventDefault();
  if (!pendingGroupDelete || ui.confirmDeleteGroup.disabled) return;
  const { kind, group } = pendingGroupDelete;
  const deleteItems = !ui.deleteGroupItemsOption.hidden && ui.deleteGroupItems.checked;
  const deleteMode = ui.deleteGroupAll.checked ? "all" : deleteItems ? "exclusive" : "keep";
  ui.confirmDeleteGroup.disabled = true;
  ui.confirmDeleteGroup.textContent = "删除中…";
  ui.deleteGroupError.hidden = true;
  try {
    if (kind === "favorite") {
      const payload = await request(
        `/api/favorites/${activeSection}/groups/${encodeURIComponent(group.id)}?deleteItems=${deleteItems}`,
        { method: "DELETE" },
      );
      favoritesData = payload;
      if ((payload.deletedGroupIds || []).includes(currentView().filters.collection))
        currentView().filters.collection = "";
      collapsedFavoriteGroups[activeSection] = new Set(
        [...collapsedFavoriteGroups[activeSection]].filter((id) => !(payload.deletedGroupIds || []).includes(id)),
      );
      persistFavoriteTree();
      toast(
        deleteItems
          ? `已删除分组和 ${payload.deletedFavoriteCount || 0} 项独占收藏`
          : `已删除 ${payload.deletedGroupCount || 1} 个分组，条目已保留`,
      );
    } else {
      const payload = await request(
        `/api/custom-groups/${activeSection}/${encodeURIComponent(group.id)}?deleteMode=${deleteMode}&model_profile=${encodeURIComponent(currentProfile())}`,
        { method: "DELETE" },
      );
      customGroups = payload.groups || [];
      const deletedIds = new Set(payload.deletedItemIds || []);
      const selection = draft.pools[activeSection];
      selection.ids = selection.ids.filter((id) => !deletedIds.has(id));
      selection.excluded_ids = selection.excluded_ids.filter((id) => !deletedIds.has(id));
      deletedIds.forEach((id) => poseItemCache.delete(id));
      const removedGroups = new Set(payload.deletedGroupIds || [group.id]);
      if (removedGroups.has(currentView().filters.customGroup)) currentView().filters.customGroup = "";
      collapsedCustomGroups[activeSection] = new Set(
        [...collapsedCustomGroups[activeSection]].filter((id) => !removedGroups.has(id)),
      );
      persistCustomTree();
      schedulePersist();
      toast(
        payload.deleteMode === "all"
          ? `已彻底删除分组和 ${payload.deletedItemCount || 0} 项关联内容`
          : payload.deleteMode === "exclusive"
            ? `已删除分组和 ${payload.deletedItemCount || 0} 项独占提示词`
            : "自定义分组已删除，条目已保留",
      );
    }
    ui.deleteGroupDialog.close();
    pendingGroupDelete = null;
    await loadPool();
    renderPoolSidebar();
    renderDimensions();
    updatePoolOverview();
  } catch (error) {
    ui.deleteGroupError.textContent = error.message;
    ui.deleteGroupError.hidden = false;
  } finally {
    ui.confirmDeleteGroup.disabled = false;
    ui.confirmDeleteGroup.textContent = "删除";
  }
}
function deleteFavoriteGroup() {
  if (editingGroup) openDeleteGroupDialog("favorite", editingGroup);
}

function selectCurrentPage(checked) {
  for (const item of poolItems) {
    const selection = draft.pools[activeSection];
    if (selection.mode === "all")
      selection.excluded_ids = checked
        ? selection.excluded_ids.filter((value) => value !== item.id)
        : [...new Set([...selection.excluded_ids, item.id])];
    else if (checked) selection.ids = [...new Set([...selection.ids, item.id])];
    else selection.ids = selection.ids.filter((value) => value !== item.id);
  }
  renderPoolItems();
  renderDimensions();
  updatePoolOverview();
  updatePoseConflict();
  updateFavoriteSelectionButton();
  schedulePersist();
}

function openCustom(item = null) {
  invalidatePromptNormalization();
  editingCustom = item;
  ui.customDialogTitle.textContent = item ? "编辑自定义项" : "新增自定义项";
  ui.customId.value = item?.id || "";
  ui.customTitle.value = item?.title || "";
  ui.customSubtitle.value = item?.subtitle || "";
  ui.customGender.value = item?.gender === "1girl" ? "female" : item?.gender === "1boy" ? "male" : "unknown";
  ui.customHair.value = item?.hair || "";
  ui.customEye.value = item?.eye || "";
  ui.customSeries.value = item?.copyright || "";
  ui.customCategory.value = item?.categories?.[0] || "";
  ui.customTraits.value = (item?.traits || []).join(", ");
  ui.customPrompt.value = item?.prompt || "";
  ui.customGenderField.hidden = activeSection !== "character";
  ui.customCharacterMeta.hidden = activeSection !== "character";
  ui.customPoseMeta.hidden = activeSection !== "pose";
  const selectedGroups = new Set(item?.group_ids || item?.groupIds || []);
  const leafGroups = customGroups.filter((group) => group.kind === "group");
  ui.customGroupChecks.replaceChildren(
    ...leafGroups.map((group) => {
      const label = document.createElement("label");
      label.className = "favorite-group-check";
      label.title = customGroupPath(group);
      label.innerHTML = `<input type="checkbox" value="${escapeHtml(group.id)}" ${selectedGroups.has(group.id) ? "checked" : ""}><span>${escapeHtml(customGroupPath(group))}</span>`;
      return label;
    }),
  );
  if (!leafGroups.length)
    ui.customGroupChecks.innerHTML = `<div class="inline-empty">尚未创建自定义分组，可在随机池侧栏新建。</div>`;
  ui.deleteCustom.hidden = !item;
  ui.customDialog.showModal();
}
async function saveCustom(event) {
  event.preventDefault();
  try {
    await normalizePromptFields({ custom_prompt: ui.customPrompt.value });
    const payload = {
      section: activeSection,
      model_profile: currentProfile(),
      title: ui.customTitle.value,
      subtitle: ui.customSubtitle.value,
      prompt: ui.customPrompt.value,
      gender: ui.customGender.value,
      hair: ui.customHair.value,
      eye: ui.customEye.value,
      copyright: ui.customSeries.value,
      categories: ui.customCategory.value ? [ui.customCategory.value] : [],
      traits: ui.customTraits.value
        .split(",")
        .map((value) => value.trim())
        .filter(Boolean),
      groupIds: [...ui.customGroupChecks.querySelectorAll("input:checked")].map((input) => input.value),
    };
    const item = editingCustom
      ? await request(`/api/custom-prompts/${editingCustom.id}?model_profile=${encodeURIComponent(currentProfile())}`, { method: "PUT", body: JSON.stringify(payload) })
      : await request("/api/custom-prompts", { method: "POST", body: JSON.stringify(payload) });
    if (!selectionHas(activeSection, item.id)) toggleSelection(activeSection, item.id, true);
    ui.customDialog.close();
    await loadCustomGroups();
    await loadPool();
    toast(editingCustom ? "自定义项已更新" : "自定义项已加入随机池");
  } catch (error) {
    toast(error.message);
  }
}
async function deleteCustomItem() {
  if (!editingCustom || !confirm("删除这个自定义项？")) return;
  try {
    await request(`/api/custom-prompts/${editingCustom.id}?model_profile=${encodeURIComponent(currentProfile())}`, { method: "DELETE" });
    const selection = draft.pools[activeSection];
    selection.ids = selection.ids.filter((id) => id !== editingCustom.id);
    selection.excluded_ids = selection.excluded_ids.filter((id) => id !== editingCustom.id);
    ui.customDialog.close();
    await loadPool();
    renderDimensions();
    updatePoolOverview();
    schedulePersist();
    toast("自定义项已删除");
  } catch (error) {
    toast(error.message);
  }
}

function openCustomGroupDialog(group = null) {
  editingCustomGroup = group;
  ui.customGroupId.value = group?.id || "";
  ui.customGroupName.value = group?.name || "";
  ui.customGroupDialogTitle.textContent = group
    ? group.kind === "group"
      ? "编辑自定义分组"
      : "编辑导入大项"
    : "新建自定义分组";
  ui.deleteCustomGroup.hidden = !group;
  ui.customGroupDialog.showModal();
}
async function saveCustomGroup(event) {
  event.preventDefault();
  const id = ui.customGroupId.value;
  try {
    const payload = await request(
      id ? `/api/custom-groups/${activeSection}/${encodeURIComponent(id)}?model_profile=${encodeURIComponent(currentProfile())}` : `/api/custom-groups/${activeSection}?model_profile=${encodeURIComponent(currentProfile())}`,
      { method: id ? "PUT" : "POST", body: JSON.stringify({ name: ui.customGroupName.value, model_profile: currentProfile() }) },
    );
    customGroups = payload.groups || [];
    ui.customGroupDialog.close();
    renderPoolSidebar();
    toast(id ? "自定义分组已更新" : "自定义分组已创建");
  } catch (error) {
    toast(error.message);
  }
}
function removeCustomGroup() {
  if (editingCustomGroup) openDeleteGroupDialog("custom", editingCustomGroup);
}

function selectedImportGroupIds() {
  return [...ui.importTargetGroups.querySelectorAll("input:checked")].map((input) => input.value);
}
function selectedImportGroupNames() {
  const selected = new Set(selectedImportGroupIds());
  return customGroups.filter((group) => selected.has(group.id)).map((group) => customGroupPath(group));
}
function renderImportTargetGroups() {
  const leafGroups = customGroups.filter((group) => group.kind === "group");
  if (!leafGroups.length) {
    ui.importTargetGroups.innerHTML = '<div class="inline-empty">当前池暂无自定义分组，请先在池侧栏新建。</div>';
    return;
  }
  ui.importTargetGroups.replaceChildren(
    ...leafGroups.map((group) => {
      const label = document.createElement("label");
      label.className = "favorite-group-check";
      label.title = customGroupPath(group);
      label.innerHTML = `<input type="checkbox" value="${escapeHtml(group.id)}"><span>${escapeHtml(customGroupPath(group))}</span><small>${group.count || 0} 项</small>`;
      label.querySelector("input").addEventListener("change", () => {
        if (importPreview) renderImportPreview();
      });
      return label;
    }),
  );
}
function openImportDialog() {
  importPreview = null;
  const label = SECTION_META[activeSection].label;
  ui.importDialogTitle.textContent = `${label}池批量导入`;
  ui.importJsonTemplate.href = `/api/custom-prompts/templates/${activeSection}/json?model_profile=${encodeURIComponent(currentProfile())}`;
  ui.importJsonTemplate.textContent = `${label} JSON 模板`;
  ui.importCsvTemplate.href = `/api/custom-prompts/templates/${activeSection}/csv?model_profile=${encodeURIComponent(currentProfile())}`;
  ui.importCsvTemplate.textContent = `${label} CSV 模板`;
  ui.importFile.value = "";
  ui.importBundleName.value = "";
  ui.importHint.textContent = `仅导入${label}池条目；选择文件后会先校验，不会立即写入。`;
  ui.importSummary.hidden = true;
  ui.importRows.replaceChildren();
  ui.commitImport.disabled = true;
  renderImportTargetGroups();
  ui.importDialog.showModal();
}
async function previewImportFile() {
  const file = ui.importFile.files?.[0];
  if (!file) return;
  const format = file.name.toLowerCase().endsWith(".csv")
    ? "csv"
    : file.name.toLowerCase().endsWith(".json")
      ? "json"
      : "";
  if (!format) {
    toast("只支持 JSON 或 CSV 文件");
    return;
  }
  ui.importBundleName.value = file.name.replace(/\.[^.]+$/, "").trim();
  ui.importHint.textContent = `正在校验 ${file.name}`;
  try {
    importPreview = await request("/api/custom-prompts/import/preview", {
      method: "POST",
      body: JSON.stringify({
        format,
        content: await file.text(),
        section: activeSection,
        model_profile: currentProfile(),
        bundleName: ui.importBundleName.value,
      }),
    });
    importPreview.filename = file.name;
    renderImportPreview();
  } catch (error) {
    importPreview = null;
    ui.commitImport.disabled = true;
    ui.importHint.textContent = error.message;
    toast(error.message);
  }
}
function renderImportPreview() {
  const summary = importPreview.summary || {};
  ui.importHint.textContent = `${importPreview.filename} · 已完成校验`;
  ui.importSummary.hidden = false;
  ui.importSummary.innerHTML = `<span>新增 ${summary.new || 0}</span><span>冲突 ${summary.conflict || 0}</span><span>错误 ${summary.error || 0}</span>`;
  const labels = { new: "新增", conflict: "冲突", error: "错误" };
  ui.importRows.replaceChildren(
    ...importPreview.rows.map((row, index) => {
      const element = document.createElement("div");
      element.className = "import-row";
      element.dataset.status = row.status;
      const title = row.item?.title || "无法读取";
      const embeddedGroups = row.groups?.length ? row.groups : ["未分组"];
      const bundleName = ui.importBundleName.value.trim() || "未命名大项";
      const importPaths = embeddedGroups.map((name) => `${bundleName} / ${name}`);
      const targetGroups = selectedImportGroupNames();
      const detail = row.error || (row.action === "skip" ? "不导入" : [...importPaths, ...targetGroups].join("、"));
      element.innerHTML = `<span>#${row.row}</span><span class="import-status">${labels[row.status]}</span><strong title="${escapeHtml(title)}">${escapeHtml(title)}</strong><small title="${escapeHtml(detail)}">${escapeHtml(detail)}</small><select aria-label="导入操作" ${row.status === "error" ? "disabled" : ""}>${row.status === "new" ? '<option value="create">导入</option><option value="skip">跳过</option>' : row.status === "conflict" ? '<option value="skip">跳过</option><option value="overwrite">覆盖</option>' : '<option value="skip">跳过</option>'}</select>`;
      const select = element.querySelector("select");
      select.value = row.action;
      select.addEventListener("change", () => {
        importPreview.rows[index].action = select.value;
        renderImportPreview();
      });
      return element;
    }),
  );
  ui.commitImport.disabled =
    !ui.importBundleName.value.trim() ||
    !importPreview.rows.some((row) => row.status !== "error" && row.action !== "skip");
}
async function commitCustomImport() {
  if (!importPreview) return;
  ui.commitImport.disabled = true;
  try {
    const result = await request("/api/custom-prompts/import", {
      method: "POST",
      body: JSON.stringify({
        rows: importPreview.rows,
        section: activeSection,
        model_profile: currentProfile(),
        targetGroupIds: selectedImportGroupIds(),
        bundleName: ui.importBundleName.value,
      }),
    });
    ui.importDialog.close();
    await loadCustomGroups();
    await loadPool();
    renderDimensions();
    toast(`${result.bundleName}：新增 ${result.imported}，更新 ${result.updated}，跳过 ${result.skipped}`);
  } catch (error) {
    ui.commitImport.disabled = false;
    toast(error.message);
  }
}

function ensureSelectValue(select, value) {
  if (!select || !value) return;
  if (![...select.options].some((option) => option.value === value)) {
    const missing = select.dataset?.nodeChoices && !JSON.parse(select.dataset.nodeChoices).includes(value);
    const option = new Option(missing ? `当前不可用 · ${value}` : value, value);
    if (missing) option.dataset.missing = "true";
    select.add(option);
  }
  select.value = value;
}
function populateSelect(select, values, selected) {
  select.replaceChildren(...values.map((value) => new Option(value, value)));
  ensureSelectValue(select, selected);
}
function populateResourceSelect(select, values, selected, emptyText) {
  const options = values.map((value) => new Option(value, value));
  if (selected && !values.includes(selected)) {
    const missing = new Option(`当前缺失 · ${selected}`, selected);
    missing.dataset.missing = "true";
    options.unshift(missing);
  }
  if (!options.length) options.push(new Option(emptyText, ""));
  select.replaceChildren(...options);
  select.value = selected && [...select.options].some((option) => option.value === selected) ? selected : "";
}
function applyNodeInputControl(element, spec) {
  if (!element || !Array.isArray(spec)) return;
  const options = spec[1] && typeof spec[1] === "object" ? spec[1] : {};
  const choices = Array.isArray(spec[0]) ? spec[0] : Array.isArray(options.options) && options.options.every(value => typeof value === "string") ? options.options : null;
  if (choices && element.tagName === "SELECT") {
    element.dataset.nodeChoices = JSON.stringify(choices);
    populateResourceSelect(element, choices, element.value, "节点没有可用选项");
    for (const option of element.options) if (option.dataset.missing) option.textContent = `当前不可用 · ${option.value}`;
  }
  if (element.type === "number") for (const key of ["min", "max", "step"]) if (typeof options[key] === "number") element[key] = String(options[key]);
}
function applyNodeResourceControls() {
  const nodeInputs = resources.node_inputs || {};
  const face = nodeInputs.ForbiddenVisionFaceProcessorIntegrated || {};
  for (const [key, id] of Object.entries(UPSCALER_CONTROL_IDS.face_refine)) applyNodeInputControl(ui[id], face[key]);
  const nvidia = nodeInputs.RTXVideoSuperResolution || {};
  applyNodeInputControl(ui.nvidia_quality, nvidia.quality);
  const scale = nvidia.resize_type?.[1]?.options?.find(option => option.key === "scale by multiplier")?.inputs?.required?.scale;
  applyNodeInputControl(ui.nvidia_scale, scale);
}
function resolveInventoryPath(selected, values) {
  const requested = String(selected || "").trim().replaceAll("\\", "/");
  if (!requested) return "";
  const lower = requested.toLocaleLowerCase();
  const exact = values.find((value) => String(value).replaceAll("\\", "/").toLocaleLowerCase() === lower);
  if (exact) return exact;
  const basename = requested.split("/").pop().toLocaleLowerCase();
  const matches = values.filter((value) => String(value).replaceAll("\\", "/").split("/").pop().toLocaleLowerCase() === basename);
  return matches.length === 1 ? matches[0] : requested;
}
function populateModelSelect(select, values, selected, missingReason = "当前分类不可用") {
  const groups = new Map();
  for (const raw of values) {
    const value = String(raw || "").replaceAll("\\", "/");
    if (!value) continue;
    const slash = value.lastIndexOf("/");
    const folder = slash >= 0 ? value.slice(0, slash) : "根目录";
    if (!groups.has(folder)) groups.set(folder, []);
    groups.get(folder).push({ value, label: slash >= 0 ? value.slice(slash + 1) : value });
  }
  const options = [];
  for (const [folder, items] of groups) {
    const optgroup = document.createElement("optgroup");
    optgroup.label = folder;
    for (const item of items) optgroup.append(new Option(item.label, item.value));
    options.push(optgroup);
  }
  if (selected && !values.includes(selected)) {
    const missing = new Option(`不可用 · ${selected} · ${missingReason}`, selected);
    missing.dataset.missing = "true";
    missing.disabled = true;
    missing.title = missingReason;
    options.unshift(missing);
  }
  if (!options.length) options.push(new Option("没有识别为当前档案的主模型", ""));
  select.replaceChildren(...options);
  select.value = selected && [...select.options].some((option) => option.value === selected) ? selected : "";
}
function refreshCurrentModelSelection() {
  const selected = ui.model_name.value || profileDrafts[currentProfile()]?.model_name || "";
  const values = resourcesLoaded && resourceInventoryProfile === currentProfile() ? resources.models : [];
  // Resolve legacy basenames against the complete inventory before filtering by profile.
  // An incompatible exact path must never be replaced by a similarly named compatible file.
  const allNames = resourcesLoaded && resourceInventoryProfile === currentProfile()
    ? (resources.model_items || []).map((item) => item.name || item.filename).filter(Boolean) : [];
  const exact = allNames.find((value) => normalizedPath(value) === normalizedPath(selected));
  const matches = allNames.filter((value) => normalizedPath(value).split("/").pop() === normalizedPath(selected));
  const resolved = exact || (!normalizedPath(selected).includes("/") && matches.length === 1 ? matches[0] : selected);
  populateModelSelect(ui.model_name, values, resolved, modelSelectionIssue(resolved));
  ui.model_name.disabled = values.length === 0;
}
function modelLabel(value) {
  const filename = String(value || "未选择模型")
    .replaceAll("\\", "/")
    .split("/")
    .pop();
  return filename.replace(/\.(safetensors|ckpt|pth)$/i, "");
}
function profileEntries() {
  const source = resources.model_profiles || resources.profiles || {};
  return Object.entries(source).filter(([id, value]) => value && typeof value === "object").map(([id, value]) => ({ id: normalizeProfileId(id), ...value }));
}
function updateProfileResources() {
  const profile = profileConfig(currentProfile());
  const available = profile.available || {};
  const missing = [
    ...(available.clip === false ? [profile.clip_name || "CLIP"] : []),
    ...(available.vae === false ? [profile.vae_name || "VAE"] : []),
  ];
  ui.modelProfileHint.textContent = missing.length
    ? `${profileLabel(currentProfile())}：缺少 ${missing.join("、")}`
    : `${profileLabel(currentProfile())} 会自动切换绑定模型、编码方式与采样参数。`;
  const boundSeed = resolveInventoryPath(profile.seedvr2_model || "", resources.seedvr2_models || []);
  if (boundSeed && resources.seedvr2_models?.includes(boundSeed) && !ui.seedvr2_model_name.value) ensureSelectValue(ui.seedvr2_model_name, boundSeed);
  ui.seedvr2_model_name.title = profile.seedvr2_model ? `档案默认：${profile.seedvr2_model}` : "请选择 SeedVR2 模型";
  ui.modelProfileHint.dataset.profileReady = String(!missing.length && available.model !== false);
  ui.model_name.title = modelSelectionIssue() || `${profileLabel(currentProfile())} 主模型`;
}
function profileOptionsFromResources() {
  const entries = profileEntries();
  if (entries.length) return entries;
  return [
    { id: "anima", label: "Anima" },
    { id: "krea2", label: "Krea 2" },
  ];
}
async function loadResources({ refresh = false } = {}) {
  const requestId = ++resourceRequest;
  const selectedProfile = currentProfile();
  const selectedSeed = ui.seedvr2_model_name.value || profileConfig(selectedProfile).seedvr2_model || "";
  const selectedSeedVae = ui.seedvr2_vae_name.value || profileConfig(selectedProfile).seedvr2_vae || "";
  const selectedSampler = ui.sampler_name.value || profileDefaults(selectedProfile).sampler_name || defaults?.sampler_name || "";
  const selectedScheduler = ui.scheduler.value || profileDefaults(selectedProfile).scheduler || defaults?.scheduler || "";
  resourcesLoaded = false;
  resourceInventoryProfile = selectedProfile;
  resourceError = "";
  refreshCurrentModelSelection();
  updateGenerationAvailability();
  ui.model_profile.disabled = true;
  ui.sampler_name.disabled = true;
  ui.scheduler.disabled = true;
  ui.seedvr2_model_name.disabled = true;
  ui.seedvr2_vae_name.disabled = true;
  for (const id of ["latent_second_sampler_name", "latent_second_scheduler", "seedvr2_sampler_name", "seedvr2_scheduler", "face_sampler", "face_scheduler"]) ui[id].disabled = true;
  // Retain selected values while disabled: autosave and overlapping reads must
  // not capture placeholder values and silently replace them with defaults.
  try {
    const payload = await request(`/api/resources?model_profile=${encodeURIComponent(selectedProfile)}${refresh ? "&refresh=1" : ""}`);
    if (requestId !== resourceRequest || selectedProfile !== currentProfile()) return;
    const modelProfiles = payload.model_profiles || payload.profiles || {};
    resources = {
      models: Array.isArray(payload.models) ? payload.models : [],
      model_items: Array.isArray(payload.model_items) ? payload.model_items : [],
      profile_models: payload.profile_models && typeof payload.profile_models === "object" ? payload.profile_models : {},
      model_assignments: payload.model_assignments || {},
      seedvr2_models: Array.isArray(payload.seedvr2_models) ? payload.seedvr2_models : [],
      seedvr2_vaes: Array.isArray(payload.seedvr2_vaes) ? payload.seedvr2_vaes : [],
      upscale_models: Array.isArray(payload.upscale_models) ? payload.upscale_models : [],
      clips: Array.isArray(payload.clips) ? payload.clips : [],
      vaes: Array.isArray(payload.vaes) ? payload.vaes : [],
      clip_types: Array.isArray(payload.clip_types) ? payload.clip_types : [],
      samplers: Array.isArray(payload.samplers) ? payload.samplers : [],
      schedulers: Array.isArray(payload.schedulers) ? payload.schedulers : [],
      model_profiles: modelProfiles,
      profiles: modelProfiles,
      nodes: payload.nodes && typeof payload.nodes === "object" ? payload.nodes : {},
      plugins: payload.plugins && typeof payload.plugins === "object" ? payload.plugins : {},
      prompt_processing: payload.prompt_processing || {},
      node_inputs: payload.node_inputs || {},
    };
    profileResources = modelProfiles;
    const profileOptions = profileOptionsFromResources();
    ui.model_profile.replaceChildren(...profileOptions.map((item) => new Option(item.label || item.name || item.id, item.id)));
    ensureSelectValue(ui.model_profile, selectedProfile);
    activeProfileId = currentProfile();
    const modelValues = resources.models.length ? resources.models : (resources.profile_models[selectedProfile] || []);
    populateResourceSelect(ui.sampler_name, resources.samplers, ui.sampler_name.value || selectedSampler, "没有可用 Sampler");
    populateResourceSelect(ui.scheduler, resources.schedulers, ui.scheduler.value || selectedScheduler, "没有可用 Scheduler");
    const seedValues = resources.seedvr2_models;
    populateResourceSelect(ui.seedvr2_model_name, seedValues, resolveInventoryPath(ui.seedvr2_model_name.value || selectedSeed, resources.seedvr2_models), "没有可用 SeedVR2 模型");
    const seedVaeValues = resources.seedvr2_vaes;
    populateResourceSelect(ui.seedvr2_vae_name, seedVaeValues, resolveInventoryPath(ui.seedvr2_vae_name.value || selectedSeedVae, resources.seedvr2_vaes), "没有可用 SeedVR2 VAE");
    for (const id of ["latent_second_sampler_name", "seedvr2_sampler_name", "face_sampler"]) populateResourceSelect(ui[id], resources.samplers, controlValue(id), "没有可用 Sampler");
    for (const id of ["latent_second_scheduler", "seedvr2_scheduler", "face_scheduler"]) populateResourceSelect(ui[id], resources.schedulers, controlValue(id), "没有可用 Scheduler");
    resourcesLoaded = true;
    ui.model_profile.disabled = profileOptions.length < 2;
    ui.model_name.disabled = modelValues.length === 0;
    ui.sampler_name.disabled = resources.samplers.length === 0;
    ui.scheduler.disabled = resources.schedulers.length === 0;
    ui.seedvr2_vae_name.disabled = resources.seedvr2_vaes.length === 0;
    for (const id of ["latent_second_sampler_name", "seedvr2_sampler_name", "face_sampler"]) ui[id].disabled = resources.samplers.length === 0;
    for (const id of ["latent_second_scheduler", "seedvr2_scheduler", "face_scheduler"]) ui[id].disabled = resources.schedulers.length === 0;
    applyNodeResourceControls();
  } catch (error) {
    if (requestId !== resourceRequest || selectedProfile !== currentProfile()) return;
    resources = { models: [], model_items: [], profile_models: {}, model_assignments: {}, seedvr2_models: [], seedvr2_vaes: [], upscale_models: [], samplers: [], schedulers: [], model_profiles: {}, profiles: {}, nodes: {}, plugins: {} };
    profileResources = {};
    resourceError = `模型资源读取失败：${error.message}`;
    ui.model_profile.disabled = false;
    populateResourceSelect(ui.sampler_name, [], ui.sampler_name.value || selectedSampler, "Sampler 资源离线");
    populateResourceSelect(ui.scheduler, [], ui.scheduler.value || selectedScheduler, "Scheduler 资源离线");
    populateResourceSelect(ui.seedvr2_model_name, [], ui.seedvr2_model_name.value || selectedSeed, "SeedVR2 资源离线");
    populateResourceSelect(ui.seedvr2_vae_name, [], ui.seedvr2_vae_name.value || selectedSeedVae, "SeedVR2 VAE 资源离线");
    populateResourceSelect(ui.face_upscaler_model, [], ui.face_upscaler_model.value, "脸部放大模型资源离线");
  }
  updateProfileResources();
  updateRepairControls();
  if (typeof singleWorkbenchResources === "function") singleWorkbenchResources();
}
function updateRepairControls() {
  refreshCurrentModelSelection();
  const upscalers = {
    latent: ui.latent_upscale_enabled.checked,
    seedvr2: ui.seedvr2_enabled.checked,
    nvidia: ui.nvidia_upscale_enabled.checked,
    face: ui.face_refine_enabled.checked,
  };
  const activeCount = Object.values(upscalers).filter(Boolean).length;
  const baseLabel = ui.base_mode.value === "dual_sampler" ? "底图－双采样器" : "普通底图";
  ui.repairSummary.textContent = `${profileLabel(currentProfile())} · ${baseLabel} · ${activeCount} 项后处理`;
  ui.latentUpscaleFields.classList.toggle("disabled", !upscalers.latent);
  ui.seedvr2Fields.classList.toggle("disabled", !upscalers.seedvr2);
  ui.nvidiaUpscaleFields.classList.toggle("disabled", !upscalers.nvidia);
  ui.faceRefineFields.classList.toggle("disabled", !upscalers.face);
  ui.latentSecondSamplerFields.classList.toggle("disabled", !upscalers.latent || !ui.latent_second_sampler_enabled.checked);
  ui.latent_upscale_scale.disabled = !upscalers.latent;
  ui.latent_upscale_method.disabled = !upscalers.latent;
  ui.latent_second_sampler_enabled.disabled = !upscalers.latent;
  for (const id of Object.values(POST_SAMPLER_IDS)) ui[id].disabled = !upscalers.latent || !ui.latent_second_sampler_enabled.checked;
  ui.seedvr2_model_name.disabled = !upscalers.seedvr2 || !resourcesLoaded;
  ui.seedvr2_vae_name.disabled = !upscalers.seedvr2 || !resourcesLoaded;
  for (const id of ["seedvr2_weight_dtype", "seedvr2_scale_by", "seedvr2_color_correction_method", ...Object.values(SEED_SAMPLER_IDS), ...Object.values(SEED_TILE_IDS.encode), ...Object.values(SEED_TILE_IDS.decode)]) ui[id].disabled = !upscalers.seedvr2;
  for (const id of ["nvidia_resize_type", "nvidia_scale", "nvidia_quality"]) ui[id].disabled = !upscalers.nvidia;
  for (const id of Object.values(UPSCALER_CONTROL_IDS.face_refine).filter((id) => id !== "face_refine_enabled")) ui[id].disabled = !upscalers.face;
  if (typeof sharedParameterVisibility === "function") sharedParameterVisibility();
  if (typeof singleWorkbenchLayout === "function" && typeof singleWorkbenchState !== "undefined" && singleWorkbenchState.ready) singleWorkbenchLayout();
  const settings = readSettings();
  const profile = profileConfig(currentProfile());
  const warnings = [];
  if (resourceError) warnings.push(resourceError);
  if (resourcesLoaded && profile.missing?.length) warnings.push(`${profileLabel(currentProfile())} 缺少资源：${profile.missing.join("、")}`);
  warnings.push(...generationClassificationIssues(settings));
  if (resourcesLoaded && upscalers.seedvr2 && settings.upscalers.seedvr2.model_name && resources.seedvr2_models.length && !resources.seedvr2_models.includes(settings.upscalers.seedvr2.model_name)) warnings.push(`SeedVR2 模型不可用：${settings.upscalers.seedvr2.model_name}`);
  if (resourcesLoaded && upscalers.face && settings.upscalers.face_refine.upscaler_model && !resources.upscale_models.includes(settings.upscalers.face_refine.upscaler_model)) warnings.push(`脸部放大模型不可用：${settings.upscalers.face_refine.upscaler_model}`);
  const advertisedAvailability = (inventory, names) => {
    if (inventory == null) return null;
    if (Array.isArray(inventory)) {
      const available = new Set(inventory.map((item) => String(item).toLowerCase()));
      return names.some((name) => available.has(name.toLowerCase()));
    }
    if (typeof inventory !== "object") return false;
    const advertised = names.filter((name) => Object.prototype.hasOwnProperty.call(inventory, name));
    if (!advertised.length) return null;
    return advertised.some((name) => inventory[name] === true);
  };
  if (resourcesLoaded && upscalers.nvidia) {
    const nvidiaNode = advertisedAvailability(resources.nodes, ["NvidiaUpscale", "RTXVideoSuperResolution"]);
    const nvidiaPlugin = advertisedAvailability(resources.plugins, ["nvidia_upscale"]);
    if ((nvidiaNode !== null || nvidiaPlugin !== null) && nvidiaNode !== true && nvidiaPlugin !== true) {
      warnings.push("N 卡放大节点不可用：RTXVideoSuperResolution");
    }
  }
  if (resourcesLoaded && !resources.samplers.length) warnings.push("ComfyUI 当前没有可用 Sampler");
  if (resourcesLoaded && !resources.schedulers.length) warnings.push("ComfyUI 当前没有可用 Scheduler");
  if (resourcesLoaded && !resources.samplers.includes(settings.sampler_name)) warnings.push(`Sampler 不可用：${settings.sampler_name}`);
  if (resourcesLoaded && !resources.schedulers.includes(settings.scheduler)) warnings.push(`Scheduler 不可用：${settings.scheduler}`);
  ui.resourceWarning.hidden = warnings.length === 0;
  ui.resourceWarning.textContent = warnings.join("；");
  updateProfileResources();
  updateGenerationAvailability();
}

let ownershipItems = [];
let ownershipRequest = 0;
function safeLoraManagerUrl(value) {
  try {
    const url = new URL(value);
    if (url.protocol !== "http:" || !["localhost", "127.0.0.1", "[::1]"].includes(url.hostname) || url.username || url.password) return "";
    return url.href;
  } catch { return ""; }
}
function setLoraManagerUrl(value) {
  const url = safeLoraManagerUrl(value);
  if (!ui.openLoraManager || !url) return;
  ui.openLoraManager.href = url;
  ui.openLoraManager.hidden = false;
}
function renderResourceOwnership() {
  const query = ui.ownershipSearch.value.trim().toLocaleLowerCase();
  const filtered = ownershipItems.filter((item) => `${item.name || ""} ${item.folder || ""} ${item.basename || ""} ${item.base_model || ""} ${item.classification_reason || ""}`.toLocaleLowerCase().includes(query));
  ui.ownershipSummary.textContent = `${ownershipItems.length} 项资源 · ${ownershipItems.filter((item) => !item.model_profile).length} 项未识别或不支持 · 分类来自 LoRA Manager`;
  ui.ownershipList.replaceChildren(...filtered.map((item) => {
    const row = document.createElement("article");
    row.className = "ownership-row";
    const copy = document.createElement("div");
    copy.className = "ownership-row-copy";
    const name = document.createElement("strong");
    name.textContent = item.basename || item.name;
    const folder = document.createElement("small");
    folder.textContent = item.name;
    folder.title = item.name;
    const reason = document.createElement("small");
    reason.className = "ownership-reason";
    reason.textContent = item.classification_reason || (item.model_profile ? "已识别" : "请在 LoRA Manager 中补全 base_model");
    copy.append(name, folder, reason);
    const metadata = document.createElement("div");
    metadata.className = "ownership-metadata";
    const baseModel = document.createElement("span");
    baseModel.textContent = `base_model：${item.base_model || "未提供"}`;
    const mode = document.createElement("strong");
    mode.textContent = `模式：${item.model_profile ? profileLabel(item.model_profile) : "不可用"}`;
    metadata.append(baseModel, mode);
    row.append(copy, metadata);
    return row;
  }));
  if (!filtered.length) ui.ownershipList.innerHTML = '<div class="inline-empty">没有匹配的资源。</div>';
}
async function loadResourceOwnership({ refresh = false } = {}) {
  const kind = ui.ownershipKind.value;
  const requestId = ++ownershipRequest;
  ownershipItems = [];
  ui.openLoraManager.hidden = true;
  ui.openLoraManager.removeAttribute("href");
  setLoraManagerUrl(`${String(config.comfy_url || "").replace(/\/+$/, "")}/${kind === "models" ? "checkpoints" : "loras"}`);
  ui.ownershipError.hidden = true;
  ui.ownershipSummary.textContent = "正在读取资源清单…";
  ui.ownershipList.replaceChildren();
  try {
    const payload = await request(`/api/resource-ownership?kind=${encodeURIComponent(kind)}${refresh ? "&refresh=1" : ""}`);
    if (requestId !== ownershipRequest || kind !== ui.ownershipKind.value) return;
    ownershipItems = Array.isArray(payload.items) ? payload.items : [];
    setLoraManagerUrl(payload.manager_url);
    renderResourceOwnership();
  } catch (error) {
    if (requestId !== ownershipRequest || kind !== ui.ownershipKind.value) return;
    ownershipItems = [];
    ui.ownershipSummary.textContent = "资源清单读取失败";
    ui.ownershipError.textContent = error.message;
    ui.ownershipError.hidden = false;
  }
}
async function refreshResourceClassification() {
  ui.refreshResourceClassification.disabled = true;
  try {
    await Promise.allSettled([
      loadResourceOwnership({ refresh: true }),
      loadResources({ refresh: true }),
      loadLoraInventory({ refresh: true }),
    ]);
    regenerationOptions = null;
    if (selectedRecord && (!ui.replayDrawer.hidden || !ui.variantDrawer.hidden)) await loadRegenerationOptions();
  } finally {
    ui.refreshResourceClassification.disabled = false;
  }
}

const PRESET_SETTING_KEYS = [
  "workbench", "generation_mode", "single", "clip_name", "vae_name", "clip_type",
  "model_profile",
  "model_name",
  "krea_prompt_pool",
  "base_mode",
  "dual",
  "upscalers",
  "loras",
  "lora_managed_triggers",
  "manual_artist",
  "quality_prompt",
  "extra_prompt",
  "negative_prompt",
  "width",
  "height",
  "steps",
  "cfg",
  "sampler_name",
  "scheduler",
];
function presetSnapshot() {
  const settings = readSettings();
  return Object.fromEntries(PRESET_SETTING_KEYS.filter(key => settings[key] !== undefined).map((key) => [key, clone(settings[key])]));
}
async function loadStylePresets() {
  const requestedProfile = currentProfile();
  const requestId = ++stylePresetRequest;
  if (stylePresetProfile && stylePresetProfile !== requestedProfile) {
    stylePresets = [];
    renderStylePresets();
  }
  stylePresetProfile = requestedProfile;
  try {
    const data = await request(`/api/style-presets?model_profile=${encodeURIComponent(requestedProfile)}`);
    if (requestId !== stylePresetRequest || requestedProfile !== currentProfile()) return;
    stylePresets = data.items || [];
    renderStylePresets();
  } catch (error) {
    if (requestId !== stylePresetRequest || requestedProfile !== currentProfile()) return;
    toast(error.message);
  }
}
function setPresetFeedback(message = "", type = "error") {
  ui.presetError.textContent = message;
  ui.presetError.hidden = !message;
  ui.presetError.dataset.type = type;
  ui.presetName.setAttribute("aria-invalid", String(Boolean(message) && type === "error"));
}
function renderStylePresets() {
  ui.presetSummary.textContent = `${stylePresets.length} 个预设`;
  ui.presetEmpty.hidden = stylePresets.length > 0;
  ui.presetList.replaceChildren(
    ...stylePresets.map((item) => {
      const row = document.createElement("div");
      row.className = `preset-row${item.favorite ? " favorite" : ""}`;
      const activeLoras = (item.settings?.loras || []).filter((lora) => lora.enabled).length;
      const settings = item.settings || {};
      const modelShort = profileLabel(settings.model_profile || "anima");
      const metaParts = [
        modelShort,
        `${settings.width || "?"}×${settings.height || "?"}`,
        `${settings.steps || "?"} 步`,
        settings.base_mode === "dual_sampler" ? "双采样底图" : "普通底图",
        settings.upscalers?.latent?.enabled ? "潜空间放大" : "无潜空间放大",
      ];
      if (activeLoras) metaParts.push(`${activeLoras} LoRA`);
      row.innerHTML = `<button class="preset-apply" type="button" title="${escapeHtml(settings.model_profile || "anima")}"><strong>${escapeHtml(item.name)}</strong><small>${escapeHtml(metaParts.join(" · "))}</small></button><div class="preset-actions"><button type="button" data-preset-favorite title="${item.favorite ? "取消收藏" : "收藏"}" aria-label="${item.favorite ? "取消收藏" : "收藏"}">${item.favorite ? "★" : "☆"}</button><button type="button" data-preset-update title="用当前设置覆盖" aria-label="用当前设置覆盖">↻</button><button type="button" data-preset-rename title="重命名" aria-label="重命名">✎</button><button type="button" data-preset-delete title="删除" aria-label="删除">×</button></div>`;
      row.querySelector(".preset-apply").addEventListener("click", () => {
        if (typeof singleWorkbenchApplyPreset === "function") singleWorkbenchApplyPreset(item);
        else applySettings({ ...readSettings(), ...item.settings });
        schedulePersist();
        toast(`已应用预设：${item.name}`);
      });
      row
        .querySelector("[data-preset-favorite]")
        .addEventListener("click", () => updateStylePreset(item, { favorite: !item.favorite }));
      row.querySelector("[data-preset-update]").addEventListener("click", () => {
        if (confirm(`用当前设置覆盖“${item.name}”？`)) updateStylePreset(item, { settings: presetSnapshot() });
      });
      row.querySelector("[data-preset-rename]").addEventListener("click", () => {
        const name = prompt("新的预设名称", item.name);
        if (name?.trim()) updateStylePreset(item, { name: name.trim() });
      });
      row.querySelector("[data-preset-delete]").addEventListener("click", () => deleteStylePreset(item));
      return row;
    }),
  );
}
async function createStylePreset() {
  if (ui.savePreset.disabled) return;
  const name = ui.presetName.value.trim();
  if (!name) {
    setPresetFeedback("请输入预设名称");
    ui.presetName.focus();
    return;
  }
  if (stylePresets.some((item) => item.name.trim().toLocaleLowerCase() === name.toLocaleLowerCase())) {
    setPresetFeedback("已有同名预设，请改名或使用覆盖按钮");
    ui.presetName.focus();
    return;
  }
  const originalLabel = ui.savePreset.textContent;
  ui.savePreset.disabled = true;
  ui.savePreset.textContent = "保存中…";
  setPresetFeedback("正在写入本地预设…", "pending");
  try {
    const created = await request("/api/style-presets", {
      method: "POST",
      body: JSON.stringify({ name, favorite: false, model_profile: currentProfile(), settings: presetSnapshot() }),
    });
    const favoriteCount = stylePresets.findIndex((item) => !item.favorite);
    stylePresets.splice(favoriteCount < 0 ? stylePresets.length : favoriteCount, 0, created);
    ui.presetName.value = "";
    renderStylePresets();
    setPresetFeedback(`已保存“${created.name}”`, "success");
    toast("风格预设已保存");
  } catch (error) {
    setPresetFeedback(error.message);
    toast(error.message);
  } finally {
    ui.savePreset.disabled = false;
    ui.savePreset.textContent = originalLabel;
  }
}
async function updateStylePreset(item, changes) {
  try {
    await request(`/api/style-presets/${encodeURIComponent(item.id)}`, {
      method: "PUT",
      body: JSON.stringify({ ...changes, model_profile: currentProfile() }),
    });
    await loadStylePresets();
  } catch (error) {
    toast(error.message);
  }
}
async function deleteStylePreset(item) {
  if (!confirm(`删除预设“${item.name}”？`)) return;
  try {
    await request(`/api/style-presets/${encodeURIComponent(item.id)}?model_profile=${encodeURIComponent(currentProfile())}`, { method: "DELETE" });
    await loadStylePresets();
    toast("风格预设已删除");
  } catch (error) {
    toast(error.message);
  }
}
function setPromptRuleError(message = "") {
  ui.promptRuleError.textContent = message;
  ui.promptRuleError.hidden = !message;
}
function clearPromptRuleEditor() {
  editingPromptRule = null;
  ui.promptRuleId.value = "";
  ui.promptRuleFrom.value = "";
  ui.promptRuleTo.value = "";
  ui.promptRulePositive.checked = true;
  ui.promptRuleNegative.checked = true;
  ui.promptRuleLora.checked = false;
  ui.deletePromptRule.hidden = true;
  setPromptRuleError();
}
function editPromptRule(item) {
  if (item.kind !== "custom") return;
  editingPromptRule = item;
  ui.promptRuleId.value = item.id;
  ui.promptRuleFrom.value = item.from || "";
  ui.promptRuleTo.value = item.to || "";
  ui.promptRulePositive.checked = item.scopes?.includes("positive");
  ui.promptRuleNegative.checked = item.scopes?.includes("negative");
  ui.promptRuleLora.checked = item.scopes?.includes("lora");
  ui.deletePromptRule.hidden = false;
  setPromptRuleError();
  ui.promptRuleFrom.focus();
}
function renderPromptRules() {
  ui.promptRuleList.replaceChildren(
    ...promptRules.map((item) => {
      const row = document.createElement("div");
      row.className = `prompt-rule-row${item.enabled ? "" : " disabled"}`;
      const description =
        item.kind === "builtin"
          ? item.description
          : `${item.from} → ${item.to} · ${(item.scopes || []).join(" / ")}`;
      const ruleName = item.kind === "builtin" ? item.name : item.from;
      row.innerHTML = `<div class="prompt-rule-copy"><strong>${escapeHtml(ruleName)}</strong><small title="${escapeHtml(description)}">${escapeHtml(description)}</small></div><div class="prompt-rule-actions"><label class="compact-check"><input type="checkbox" ${item.enabled ? "checked" : ""} aria-label="${escapeHtml(ruleName)}：${item.enabled ? "停用" : "启用"}规则"><span>${item.enabled ? "启用" : "停用"}</span></label>${item.kind === "custom" ? '<button class="button ghost compact" type="button">编辑</button>' : ""}</div>`;
      row.querySelector('input[type="checkbox"]').addEventListener("change", async (event) => {
        try {
          await request(`/api/prompt-rules/${encodeURIComponent(item.id)}`, {
            method: "PUT",
            body: JSON.stringify({ enabled: event.target.checked }),
          });
          await loadPromptRules();
        } catch (error) {
          toast(error.message);
          await loadPromptRules();
        }
      });
      row.querySelector("button")?.addEventListener("click", () => editPromptRule(item));
      return row;
    }),
  );
}
async function loadPromptRules() {
  const data = await request("/api/prompt-rules");
  promptRules = data.items || [];
  renderPromptRules();
}
async function openPromptRuleDialog() {
  clearPromptRuleEditor();
  ui.promptRuleDialog.showModal();
  try {
    await loadPromptRules();
  } catch (error) {
    setPromptRuleError(error.message);
  }
}
function promptRuleScopes() {
  return [
    ui.promptRulePositive.checked ? "positive" : "",
    ui.promptRuleNegative.checked ? "negative" : "",
    ui.promptRuleLora.checked ? "lora" : "",
  ].filter(Boolean);
}
async function savePromptRule(event) {
  event.preventDefault();
  const scopes = promptRuleScopes();
  if (!scopes.length) {
    setPromptRuleError("至少选择一个应用范围");
    return;
  }
  const payload = {
    from: ui.promptRuleFrom.value,
    to: ui.promptRuleTo.value,
    scopes,
    enabled: true,
  };
  try {
    const path = editingPromptRule
      ? `/api/prompt-rules/${encodeURIComponent(editingPromptRule.id)}`
      : "/api/prompt-rules";
    await request(path, {
      method: editingPromptRule ? "PUT" : "POST",
      body: JSON.stringify({ ...payload, model_profile: currentProfile() }),
    });
    clearPromptRuleEditor();
    await loadPromptRules();
    toast("提示词替换规则已保存");
  } catch (error) {
    setPromptRuleError(error.message);
  }
}
async function deletePromptRule() {
  if (!editingPromptRule) return;
  try {
    await request(`/api/prompt-rules/${encodeURIComponent(editingPromptRule.id)}`, {
      method: "DELETE",
    });
    clearPromptRuleEditor();
    await loadPromptRules();
    toast("提示词替换规则已删除");
  } catch (error) {
    setPromptRuleError(error.message);
  }
}

function artistName(value) {
  let name = String(value || "").trim();
  if (name.startsWith("@")) name = name.slice(1).trim();
  else if (name.toLowerCase().startsWith("by ")) name = name.slice(3).trim();
  return name;
}
function artistTokensFrom(value) {
  const values = String(value || "")
    .replaceAll("\r", ",")
    .replaceAll("\n", ",")
    .split(/,|(?=@)/)
    .map(artistName)
    .filter(Boolean);
  const seen = new Set();
  return values.filter((name) => {
    const key = name.toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}
function canonicalArtists(value) {
  return artistTokensFrom(value)
    .map((name) => `@${name}`)
    .join(", ");
}
function artistTokens() {
  return artistTokensFrom(ui.manual_artist.value);
}
function appendArtist(name) {
  const values = artistTokens();
  const keys = new Set(values.map((value) => value.toLowerCase()));
  const clean = artistName(name);
  if (clean && !keys.has(clean.toLowerCase())) values.push(clean);
  ui.manual_artist.value = values.map((value) => `@${value}`).join(", ");
  schedulePersist();
}
function artistEditingValue(value) {
  return String(value || "")
    .replaceAll("\r", ",")
    .replaceAll("\n", ",")
    .split(",")
    .map((part) => {
      const leading = part.match(/^\s*/)?.[0] || "";
      let name = part.slice(leading.length);
      if (/^by\s+/i.test(name)) name = name.replace(/^by\s+/i, "@");
      else if (name && !name.startsWith("@")) name = `@${name}`;
      return name;
    })
    .join(", ");
}
function normalizeArtistField(finalize = false) {
  const input = ui.manual_artist;
  const raw = input.value;
  const caret = input.selectionStart ?? raw.length;
  const transform = finalize ? canonicalArtists : artistEditingValue;
  const next = transform(raw);
  if (next === raw) return;
  const nextCaret = transform(raw.slice(0, caret)).length;
  input.value = next;
  if (document.activeElement === input) input.setSelectionRange(nextCaret, nextCaret);
}
async function loadArtistFavorites() {
  try {
    artistFavoritesData = await request("/api/favorites/artist");
  } catch {
    artistFavoritesData = { groups: [], items: [] };
  }
  renderArtistFavorites();
}
function renderArtistFavorites() {
  const items = (artistFavoritesData.items || []).filter((item) => item.groupIds?.length);
  ui.artistFavoriteCount.textContent = `${items.length} 位`;
  ui.artistFavorites.replaceChildren(
    ...items.slice(0, 12).map((item) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "artist-chip";
      button.title = item.nickname ? `追加 @${item.name} · ${item.nickname}` : `追加 @${item.name}`;
      button.innerHTML = `<strong>@${escapeHtml(item.name)}</strong>${item.nickname ? `<small>${escapeHtml(item.nickname)}</small>` : ""}`;
      button.addEventListener("click", () => appendArtist(item.name));
      return button;
    }),
  );
  ui.artistFavoriteList.replaceChildren(
    ...(items.length
      ? items.map((item) => {
          const row = document.createElement("div");
          row.className = "artist-favorite-row";
          row.innerHTML = `<button class="artist-favorite-name" type="button" title="追加到固定画师"><strong>@${escapeHtml(item.name)}</strong>${item.nickname ? `<small>${escapeHtml(item.nickname)}</small>` : ""}</button><button class="button ghost compact" type="button">追加</button><button class="icon-button dark" type="button" title="取消收藏" aria-label="取消收藏">×</button>`;
          row.children[0].addEventListener("click", () => appendArtist(item.name));
          row.children[1].addEventListener("click", () => appendArtist(item.name));
          row.children[2].addEventListener("click", () => removeArtistFavorite(item.name));
          return row;
        })
      : [
          Object.assign(document.createElement("div"), {
            className: "artist-favorite-empty",
            textContent: "还没有收藏画师",
          }),
        ]),
  );
}
async function openArtistDialog() {
  await loadArtistFavorites();
  ui.artistDialog.showModal();
}
async function saveCurrentArtistFavorites() {
  const values = artistTokens();
  if (!values.length) {
    toast("请先输入画师名称");
    return;
  }
  try {
    for (const name of values)
      artistFavoritesData = await request("/api/favorites/artist/item", {
        method: "PUT",
        body: JSON.stringify({ name, favorite: true }),
      });
    renderArtistFavorites();
    toast(`已收藏 ${values.length} 位画师`);
  } catch (error) {
    toast(error.message);
  }
}
async function removeArtistFavorite(name) {
  try {
    artistFavoritesData = await request("/api/favorites/artist/item", {
      method: "PUT",
      body: JSON.stringify({ name, favorite: false }),
    });
    renderArtistFavorites();
    toast("已取消收藏画师");
  } catch (error) {
    toast(error.message);
  }
}

async function submitBatch(settings, seeds = null) {
  // 统一的批次提交入口:主表单、复现、变体共用;in-flight 守卫防双击重复入队。
  if (submitInFlight) return false;
  settings = clone(settings);
  seeds = seeds ? clone(seeds) : null;
  const classificationIssues = generationClassificationIssues(settings);
  if (classificationIssues.length) {
    updateGenerationAvailability();
    toast(classificationIssues.join("；"));
    return false;
  }
  submitInFlight = true;
  ui.startButton.disabled = true;
  try {
    if (settings.workbench !== "single") settings = await normalizeSettingsPayload(settings);
    const body = seeds ? { ...settings, seeds } : settings;
    const state = await request("/api/batches", { method: "POST", body: JSON.stringify(body) });
    if (state.status === "queued") toast(`已加入队列,第 ${state.position} 位`);
    else toast("批次已开始");
    return true;
  } catch (error) {
    toast(error.message);
    await refreshConnection();
    return false;
  } finally {
    try {
      renderBatchPayload(await request("/api/batches/current"));
    } catch {
      // The submission outcome remains valid if its follow-up status read fails.
    } finally {
      submitInFlight = false;
      updateGenerationAvailability();
    }
  }
}
ui.settingsForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  if (submitInFlight) return;
  try {
    const single = typeof singleWorkbenchActive === "function" && singleWorkbenchActive();
    if (single && singleWorkbenchInputBlocked()) throw new Error("参考图正在读取或不可用，请等待、重新添加或移除");
    // Capture the workbench, profile and seeds before the first await. The
    // submit entry owns normalization and its lock for the entire request.
    const settings = readSettings();
    const seeds = single ? singleWorkbenchSeeds() : null;
    persistNow();
    await submitBatch(settings, seeds);
  } catch (error) {
    toast(error.message);
  }
});
ui.settingsForm.addEventListener("input", (event) => {
  if (event.target.matches("input, textarea, select")) schedulePersist();
});
ui.settingsForm.addEventListener("change", schedulePersist);
document.addEventListener("input", (event) => {
  if (promptFieldForElement(event.target) || event.target.matches?.("[data-single-prompt]")) invalidatePromptNormalization();
});
ui.stopButton.addEventListener("click", async () => {
  if (!currentBatch) return;
  try {
    // 停止 = 中断当前批次并清空队列(队列条目可单独用 × 移除)
    renderBatchPayload(await request(`/api/batches/${currentBatch.id}/stop`, { method: "POST" }));
  } catch (error) {
    toast(error.message);
  }
});
// Animated collapse for <details> sections: intercept the toggle, play the
// reverse of the `disclose` entry animation, then flip `open` off.
for (const details of document.querySelectorAll("details.advanced-section, details.artist-favorites-section")) {
  const summary = details.querySelector("summary");
  const content = details.querySelector(".details-content, .artist-favorites-content, summary + *");
  if (!summary || !content) continue;
  summary.addEventListener("click", (event) => {
    if (details.classList.contains("single-fixed-open") || !details.open || details.dataset.collapsing) return;
    event.preventDefault();
    details.dataset.collapsing = "1";
    const animation = content.animate(
      [{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(-6px)" }],
      { duration: imagePreviewReducedMotion() ? 60 : 180, easing: "cubic-bezier(.4,0,1,1)", fill: "both" },
    );
    animation.finished.then(() => {
      details.open = false;
      delete details.dataset.collapsing;
      animation.cancel(); // release the fill so CSS takes over cleanly
    }).catch(() => {
      details.open = false;
      delete details.dataset.collapsing;
    });
  });
}

ui.queueBadge?.addEventListener("click", () => {
  const open = !ui.queuePanel.classList.contains("open") && currentQueue.length > 0;
  setQueuePanelOpen(open);
  ui.queueBadge.setAttribute("aria-expanded", String(open));
});
ui.reproduceImage?.addEventListener("click", () => openRegenerationDrawer("replay", ui.reproduceImage));
ui.variantButton?.addEventListener("click", () => openRegenerationDrawer("variant", ui.variantButton));
ui.closeReplayDrawer.addEventListener("click", () => closeRegenerationDrawers());
ui.closeVariantDrawer.addEventListener("click", () => closeRegenerationDrawers());
ui.confirmReplay.addEventListener("click", () => submitRegeneration("replay"));
ui.confirmVariant.addEventListener("click", () => {
  const mode = selectedRegenerationMode();
  const count = Math.max(1, Math.min(100, Number(ui.variantDrawerCount.value) || 4));
  submitRegeneration(mode, count, selectedRedrawSections());
});
ui.variantModes.addEventListener("change", updateVariantDrawer);
ui.redrawSections.addEventListener("change", updateVariantDrawer);
for (const input of [ui.variantCount, ui.variantDrawerCount])
  input.addEventListener("input", (event) => {
    const count = Math.max(1, Math.min(100, Number(event.target.value) || 4));
    ui.variantCount.value = count;
    ui.variantDrawerCount.value = count;
  });
ui.prevPage.addEventListener("click", () => loadHistory(historyPage - 1));
ui.nextPage.addEventListener("click", () => loadHistory(historyPage + 1));
ui.detailImage.addEventListener("click", openImagePreview);
ui.detailImage.addEventListener("keydown", (event) => {
  if (event.key !== "Enter" && event.key !== " ") return;
  event.preventDefault();
  openImagePreview();
});
ui.closeImagePreview.addEventListener("click", () => closeImagePreview());
ui.imagePreviewPrevious.addEventListener("click", (event) => navigatePreview(-1, event.currentTarget));
ui.imagePreviewNext.addEventListener("click", (event) => navigatePreview(1, event.currentTarget));
ui.imagePreviewImage.addEventListener("pointerdown", imagePreviewPointerDown);
ui.imagePreviewImage.addEventListener("pointermove", imagePreviewPointerMove);
ui.imagePreviewImage.addEventListener("pointerup", imagePreviewPointerUp);
ui.imagePreviewImage.addEventListener("pointercancel", imagePreviewPointerUp);
ui.imagePreviewImage.addEventListener("lostpointercapture", imagePreviewPointerUp);
ui.imagePreviewStage.addEventListener("wheel", imagePreviewWheel, { passive: false });
ui.imagePreviewImage.addEventListener("dblclick", (event) => {
  event.preventDefault();
  setImagePreviewZoom(imagePreview.zoom < 1.01 ? 2 : 1, event, true);
});
ui.imagePreviewVeil.addEventListener("pointerdown", (event) => {
  if (event.button === 0) imagePreview.backdropPointer = { id: event.pointerId, x: event.clientX, y: event.clientY };
});
ui.imagePreviewVeil.addEventListener("pointerup", (event) => {
  const start = imagePreview.backdropPointer;
  imagePreview.backdropPointer = null;
  if (start?.id === event.pointerId && Math.hypot(event.clientX - start.x, event.clientY - start.y) < 6) closeImagePreview();
});
ui.imagePreviewDialog.addEventListener("keydown", (event) => {
  if (event.key === "Escape") {
    event.preventDefault();
    event.stopPropagation();
    closeImagePreview();
    return;
  }
  if (event.key === "+" || event.key === "=") {
    event.preventDefault();
    setImagePreviewZoom(imagePreview.zoom + .25, null, true);
  } else if (event.key === "-") {
    event.preventDefault();
    setImagePreviewZoom(imagePreview.zoom - .25, null, true);
  } else if (event.key === "0") {
    event.preventDefault();
    resetImagePreviewTransform();
  } else if (event.key === "ArrowLeft" || event.key === "ArrowRight" || event.key === "ArrowUp" || event.key === "ArrowDown") {
    event.preventDefault();
    if (imagePreview.zoom <= 1.01 && (event.key === "ArrowLeft" || event.key === "ArrowRight")) {
      const navigationButton = event.target instanceof Element ? event.target.closest(".image-preview-nav") : null;
      navigatePreview(event.key === "ArrowLeft" ? -1 : 1, navigationButton);
      return;
    }
    cancelImagePreviewAnimation();
    const amount = event.shiftKey ? 80 : 24;
    imagePreview.x += event.key === "ArrowLeft" ? amount * -1 : event.key === "ArrowRight" ? amount : 0;
    imagePreview.y += event.key === "ArrowUp" ? amount * -1 : event.key === "ArrowDown" ? amount : 0;
    applyImagePreviewTransform();
  }
});
ui.imagePreviewDialog.addEventListener("cancel", (event) => {
  event.preventDefault();
  closeImagePreview();
});
ui.imagePreviewDialog.addEventListener("close", () => {
  if (ui.imagePreviewDialog.open) return;
  // External close (e.g. detail dialog closing) while opening/open: reset now
  // rather than letting a half-finished entry animation keep the veil alive.
  if (imagePreview.phase !== "closed") finishImagePreviewClose(false);
  restoreImagePreviewCloseFocus();
});
ui.detailDialog.addEventListener("close", finishDetailClose);
new ResizeObserver(() => {
  if (imagePreview.phase === "open") {
    cancelImagePreviewAnimation();
    imagePreview.pointers.clear();
    imagePreview.gesture = imagePreview.tap = null;
    ui.imagePreviewImage.classList.remove("is-interacting");
    layoutImagePreview();
  } else if (imagePreview.phase === "closing") finishImagePreviewClose();
}).observe(ui.imagePreviewStage);
ui.closeDialog.addEventListener("click", () => {
  closeImagePreview({ immediate: true, restoreFocus: false });
  closeRegenerationDrawers(false);
  ui.detailDialog.close();
});
ui.detailDialog.addEventListener("click", (event) => {
  if (event.target === ui.detailDialog) {
    closeImagePreview({ immediate: true, restoreFocus: false });
    closeRegenerationDrawers(false);
    ui.detailDialog.close();
    return;
  }
  if (
    (!ui.replayDrawer.hidden || !ui.variantDrawer.hidden) &&
    !event.target.closest(".regeneration-drawer, .reproduce-actions")
  )
    closeRegenerationDrawers();
});
ui.detailDialog.addEventListener("cancel", (event) => {
  if (!ui.replayDrawer.hidden || !ui.variantDrawer.hidden) {
    event.preventDefault();
    closeRegenerationDrawers();
  }
});
ui.restoreSettings.addEventListener("click", () => {
  if (!selectedRecord) return;
  suppressDetailReturnFocus();
  closeImagePreview({ immediate: true, restoreFocus: false });
  if (typeof singleWorkbenchRestoreHistory === "function") singleWorkbenchRestoreHistory(selectedRecord);
  else applySettings(selectedRecord.settings);
  ui.detailDialog.close();
  schedulePersist();
  toast("已载入历史设置");
});
async function deleteSelectedHistoryRecord() {
  if (!selectedRecord || !confirm("只删除 WebUI 历史记录，图片文件会保留。继续吗？")) return;
  const deletedId = selectedRecord.id;
  const owner = detailContext;
  const fromSingleViewer = owner?.origin === "single";
  ui.deleteRecord.disabled = true;
  try {
    await request(`/api/history/${deletedId}`, { method: "DELETE" });
    if (detailContext === owner && String(selectedRecord?.id) === String(deletedId)) closeImagePreview({ immediate: true, restoreFocus: false });
    const neighbor = typeof singleGalleryDeleted === "function" ? await singleGalleryDeleted(deletedId) : null;
    if (detailContext === owner && String(selectedRecord?.id) === String(deletedId)) {
      if (fromSingleViewer && neighbor && ui.detailDialog.open) {
        updateDetailRecord(neighbor);
        ui.detailImage.focus({ preventScroll: true });
      } else { ui.detailDialog.close(); selectedRecord = null; }
    }
    historyCache.clear();
    if (fromSingleViewer) historyDirty = true;
    else await loadHistory(historyPage);
  } catch (error) {
    toast(error.message);
  } finally {
    ui.deleteRecord.disabled = false;
  }
}
ui.deleteRecord.addEventListener("click", deleteSelectedHistoryRecord);
ui.closePool.addEventListener("click", closePool);
ui.confirmPool.addEventListener("click", closePool);
ui.drawerBackdrop.addEventListener("click", closePool);
ui.togglePoolSidebar.addEventListener("click", () =>
  setPoolSidebarOpen(!ui.poolSidebar.classList.contains("mobile-open")),
);
ui.poolSidebar.addEventListener("click", (event) => {
  if (
    window.innerWidth <= 620 &&
    event.target.closest("button") &&
    !event.target.closest(".facet-heading, .collection-edit, .tree-disclosure")
  )
    setPoolSidebarOpen(false);
});
ui.poolSidebar.addEventListener("keydown", handleGroupTreeKeydown);
ui.poolSearch.addEventListener("input", () => {
  currentView().query = ui.poolSearch.value;
  poolPage = 1;
  clearTimeout(ui.poolSearch._timer);
  ui.poolSearch._timer = setTimeout(loadPool, 220);
  schedulePersist();
});
ui.poolSort.addEventListener("change", () => {
  currentView().sort = ui.poolSort.value;
  poolPage = 1;
  loadPool();
  schedulePersist();
});
ui.poolLanguage.addEventListener("change", () => {
  currentView().language = ui.poolLanguage.value;
  renderPoolItems();
  renderPoolSidebar(); // 侧栏分类标签同步切换语言
  schedulePersist();
});
ui.selectedOnly.addEventListener("change", () => {
  currentView().selectedOnly = ui.selectedOnly.checked;
  poolPage = 1;
  loadPool();
  schedulePersist();
});
ui.clearFilters.addEventListener("click", () => {
  const language = currentView().language;
  poolViews[activeSection] = { ...defaultView(), language };
  ui.poolSearch.value = "";
  ui.poolSort.value = "";
  ui.selectedOnly.checked = false;
  poolPage = 1;
  loadPool();
  schedulePersist();
});
ui.poolPrev.addEventListener("click", () => {
  poolPage -= 1;
  loadPool();
});
ui.poolNext.addEventListener("click", () => {
  poolPage += 1;
  loadPool();
});
ui.selectPage.addEventListener("click", () => selectCurrentPage(true));
ui.selectAllPool.addEventListener("click", () => {
  draft.pools[activeSection] = { mode: "all", ids: [], excluded_ids: [] };
  renderPoolItems();
  renderDimensions();
  updatePoolOverview();
  updatePoseConflict();
  updateFavoriteSelectionButton();
  schedulePersist();
});
ui.clearPool.addEventListener("click", () => {
  draft.pools[activeSection] = { mode: "include", ids: [], excluded_ids: [] };
  renderPoolItems();
  renderDimensions();
  updatePoolOverview();
  updatePoseConflict();
  updateFavoriteSelectionButton();
  schedulePersist();
});
ui.favoriteSelection.addEventListener("click", openFavoriteSelectionDialog);
ui.favoriteSelectionForm.addEventListener("submit", saveFavoriteSelection);
ui.createFavoriteSelectionGroup.addEventListener("click", createFavoriteSelectionGroup);
ui.closeFavoriteSelection.addEventListener("click", () => ui.favoriteSelectionDialog.close());
ui.cancelFavoriteSelection.addEventListener("click", () => ui.favoriteSelectionDialog.close());
ui.addCustom.addEventListener("click", () => openCustom());
ui.importCustom.addEventListener("click", openImportDialog);
ui.customForm.addEventListener("submit", saveCustom);
ui.cancelCustom.addEventListener("click", () => ui.customDialog.close());
ui.closeCustom.addEventListener("click", () => ui.customDialog.close());
ui.deleteCustom.addEventListener("click", deleteCustomItem);
ui.customGroupForm.addEventListener("submit", saveCustomGroup);
ui.deleteCustomGroup.addEventListener("click", removeCustomGroup);
ui.closeCustomGroup.addEventListener("click", () => ui.customGroupDialog.close());
ui.cancelCustomGroup.addEventListener("click", () => ui.customGroupDialog.close());
ui.importFile.addEventListener("change", previewImportFile);
ui.importBundleName.addEventListener("input", () => {
  if (importPreview) renderImportPreview();
});
ui.commitImport.addEventListener("click", commitCustomImport);
ui.closeImport.addEventListener("click", () => ui.importDialog.close());
ui.cancelImport.addEventListener("click", () => ui.importDialog.close());
ui.manageArtists.addEventListener("click", openArtistDialog);
ui.saveCurrentArtists.addEventListener("click", saveCurrentArtistFavorites);
ui.closeArtists.addEventListener("click", () => ui.artistDialog.close());
ui.cancelArtists.addEventListener("click", () => ui.artistDialog.close());
ui.savePreset.addEventListener("click", createStylePreset);
ui.presetName.addEventListener("keydown", (event) => {
  if (event.key === "Enter") {
    event.preventDefault();
    createStylePreset();
  }
});
ui.presetName.addEventListener("input", () => setPresetFeedback());
ui.manual_artist.addEventListener("input", () => {
  normalizeArtistField(false);
  schedulePersist();
});
ui.manual_artist.addEventListener("paste", () =>
  queueMicrotask(() => {
    normalizeArtistField(true);
    schedulePersist();
  }),
);
ui.manual_artist.addEventListener("keydown", (event) => {
  if (event.key === "Enter") {
    event.preventDefault();
    normalizeArtistField(true);
    schedulePersist();
  }
});
ui.manual_artist.addEventListener("blur", () => {
  normalizeArtistField(true);
  schedulePersist();
  normalizePromptElement(ui.manual_artist);
});
for (const input of [ui.quality_prompt, ui.extra_prompt, ui.negative_prompt, ui.customPrompt])
  input.addEventListener("blur", () => normalizePromptElement(input));
ui.undoPromptNormalization.addEventListener("click", () => {
  if (!normalizationUndo) return;
  invalidatePromptNormalization();
  for (const [field, value] of Object.entries(normalizationUndo)) applyPromptField(field, value);
  clearNormalizationUndo("已撤销上次提示词规范。");
  schedulePersist();
});
ui.managePromptRules.addEventListener("click", openPromptRuleDialog);
ui.promptRuleForm.addEventListener("submit", savePromptRule);
ui.clearPromptRule.addEventListener("click", clearPromptRuleEditor);
ui.deletePromptRule.addEventListener("click", deletePromptRule);
ui.closePromptRules.addEventListener("click", () => ui.promptRuleDialog.close());
ui.cancelPromptRules.addEventListener("click", () => ui.promptRuleDialog.close());
ui.model_profile.addEventListener("change", () => {
  const next = normalizeProfileId(ui.model_profile.value);
  if (typeof singleWorkbenchState !== "undefined" && singleWorkbenchState.ready) { singleWorkbenchSwitch(singleWorkbenchState.mode, next); return; }
  switchProfileDraft(next);
  updateProfileResources();
  updateRepairControls();
  schedulePersist();
  void refreshProfileData();
});
ui.model_name.addEventListener("change", () => {
  saveCurrentProfileDraft();
  updateRepairControls();
  schedulePersist();
});
ui.manageResourceOwnership.addEventListener("click", async () => {
  ui.resourceOwnershipDialog.showModal();
  await loadResourceOwnership();
});
ui.closeResourceOwnership.addEventListener("click", () => ui.resourceOwnershipDialog.close());
ui.closeResourceOwnershipFooter.addEventListener("click", () => ui.resourceOwnershipDialog.close());
ui.ownershipKind.addEventListener("change", loadResourceOwnership);
ui.ownershipSearch.addEventListener("input", renderResourceOwnership);
ui.refreshResourceClassification.addEventListener("click", refreshResourceClassification);
ui.openKreaPromptPool.addEventListener("click", openKreaPromptPool);
ui.closeKreaPool.addEventListener("click", closeKreaPromptPool);
ui.confirmKreaPool.addEventListener("click", closeKreaPromptPool);
ui.kreaPoolBackdrop.addEventListener("click", closeKreaPromptPool);
ui.kreaPoolSearch.addEventListener("input", () => {
  clearTimeout(kreaPoolSearchTimer);
  kreaPoolSearchTimer = setTimeout(() => {
    kreaPoolPage = 1;
    kreaSelectedViewItems = null;
    void loadKreaPromptPool({ page: 1, force: true });
  }, 220);
});
ui.kreaPoolSelectedOnly.addEventListener("change", () => {
  kreaPoolPage = 1;
  kreaSelectedViewItems = null;
  void loadKreaPromptPool({ page: 1, force: true });
});
ui.kreaPoolSelectPage.addEventListener("click", () => {
  const pageIds = kreaPromptPool.items.map((item) => String(item.id));
  kreaPromptPoolSelection = [...new Set([...kreaPromptPoolSelection, ...pageIds])];
  if (pageIds.length) ui.krea_prompt_pool_enabled.checked = true;
  renderKreaPromptPool();
  schedulePersist();
});
ui.kreaPoolSelectAll.addEventListener("click", async () => {
  const query = ui.kreaPoolSearch.value.trim();
  const previous = ui.kreaPoolSelectAll.disabled;
  ui.kreaPoolSelectAll.disabled = true;
  try {
    const result = await fetchAllKreaPromptPoolItems(query);
    const selected = new Set(kreaPromptPoolSelection);
    const matchingItems = ui.kreaPoolSelectedOnly.checked
      ? result.items.filter((item) => selected.has(String(item.id)))
      : result.items;
    const ids = matchingItems.map((item) => String(item.id));
    kreaPromptPoolSelection = [...new Set([...kreaPromptPoolSelection, ...ids])];
    if (ids.length) ui.krea_prompt_pool_enabled.checked = true;
    kreaSelectedViewItems = null;
    await loadKreaPromptPool({ page: kreaPoolPage, force: true });
    schedulePersist();
    toast(ids.length ? `已选中当前筛选结果 ${ids.length} 项` : "当前筛选结果为空");
  } catch (error) {
    toast(error.message);
  } finally {
    ui.kreaPoolSelectAll.disabled = previous;
  }
});
ui.clearKreaPoolSelection.addEventListener("click", () => {
  kreaPromptPoolSelection = [];
  ui.krea_prompt_pool_enabled.checked = false;
  if (ui.kreaPoolSelectedOnly.checked) {
    applyKreaSelectedViewPage();
  }
  renderKreaPromptPool();
  schedulePersist();
});
ui.krea_prompt_pool_enabled.addEventListener("change", () => {
  if (currentProfile() !== "krea2") ui.krea_prompt_pool_enabled.checked = false;
  schedulePersist();
});
ui.kreaPromptPoolFile.addEventListener("change", async () => {
  const file = ui.kreaPromptPoolFile.files?.[0];
  if (!file) return;
  ui.kreaPromptPoolText.value = await file.text();
  const name = file.name.toLowerCase();
  ui.kreaPromptImportFormat.value = name.endsWith(".csv") ? "csv" : name.endsWith(".json") ? "json" : "auto";
  await previewKreaPromptImport();
});
ui.kreaPoolPrev.addEventListener("click", () => {
  if (kreaPoolPage <= 1) return;
  kreaPoolPage -= 1;
  if (ui.kreaPoolSelectedOnly.checked) applyKreaSelectedViewPage();
  else void loadKreaPromptPool({ page: kreaPoolPage });
  renderKreaPromptPool();
});
ui.kreaPoolNext.addEventListener("click", () => {
  if (kreaPoolPage >= kreaPoolPages) return;
  kreaPoolPage += 1;
  if (ui.kreaPoolSelectedOnly.checked) applyKreaSelectedViewPage();
  else void loadKreaPromptPool({ page: kreaPoolPage });
  renderKreaPromptPool();
});
ui.kreaPoolDeleteSelection.addEventListener("click", () => deleteKreaPromptPoolSelection());
ui.kreaPoolAddItem.addEventListener("click", () => openKreaPromptItemDialog());
ui.kreaPoolOpenImport.addEventListener("click", openKreaPromptImportDialog);
ui.kreaPromptItemForm.addEventListener("submit", saveKreaPromptItem);
ui.kreaPromptItemClose.addEventListener("click", () => ui.kreaPromptItemDialog.close());
ui.kreaPromptItemCancel.addEventListener("click", () => ui.kreaPromptItemDialog.close());
ui.kreaPromptItemDelete.addEventListener("click", () => {
  if (editingKreaPromptItem) void deleteKreaPromptPoolSelection([editingKreaPromptItem.id]);
});
ui.kreaPromptImportClose.addEventListener("click", () => ui.kreaPromptImportDialog.close());
ui.kreaPromptImportCancel.addEventListener("click", () => ui.kreaPromptImportDialog.close());
ui.kreaPromptImportPreviewButton.addEventListener("click", previewKreaPromptImport);
ui.kreaPromptImportCommit.addEventListener("click", commitKreaPromptImport);
ui.kreaPromptPoolText.addEventListener("input", () => {
  invalidateKreaPromptImportPreview("内容已变化，请重新校验并预览。");
});
ui.kreaPromptImportMode.addEventListener("change", () => {
  invalidateKreaPromptImportPreview("导入方式已变化，请重新校验并预览。");
});
ui.kreaPromptImportFormat.addEventListener("change", () => {
  invalidateKreaPromptImportPreview("导入格式已变化，请重新校验并预览。");
});
for (const control of [
  ui.latent_second_sampler_enabled,
  ui.base_mode,
  ui.latent_upscale_enabled,
  ui.latent_upscale_scale,
  ui.latent_upscale_method,
  ...Object.values(POST_SAMPLER_IDS).map((id) => ui[id]),
  ui.seedvr2_enabled,
  ui.seedvr2_model_name,
  ui.seedvr2_vae_name,
  ui.seedvr2_weight_dtype,
  ui.seedvr2_scale_by,
  ui.seedvr2_color_correction_method,
  ...Object.values(SEED_SAMPLER_IDS).map((id) => ui[id]),
  ...Object.values(SEED_TILE_IDS.encode).map((id) => ui[id]),
  ...Object.values(SEED_TILE_IDS.decode).map((id) => ui[id]),
  ui.nvidia_upscale_enabled,
  ui.nvidia_resize_type,
  ui.nvidia_scale,
  ui.nvidia_quality,
  ui.face_refine_enabled,
  ...Object.values(UPSCALER_CONTROL_IDS.face_refine).map((id) => ui[id]),
]) {
  control.addEventListener("change", () => {
    updateRepairControls();
    schedulePersist();
  });
}
ui.themeToggle.addEventListener("click", () =>
  applyTheme(document.documentElement.dataset.theme === "dark" ? "light" : "dark", true),
);
ui.favoriteForm.addEventListener("submit", saveFavorite);
ui.removeFavorite.addEventListener("click", removeFavorite);
ui.closeFavorite.addEventListener("click", () => ui.favoriteDialog.close());
ui.cancelFavorite.addEventListener("click", () => ui.favoriteDialog.close());
ui.groupForm.addEventListener("submit", saveGroup);
ui.deleteGroup.addEventListener("click", deleteFavoriteGroup);
ui.closeGroup.addEventListener("click", () => ui.groupDialog.close());
ui.cancelGroup.addEventListener("click", () => ui.groupDialog.close());
ui.importChildGroup.addEventListener("click", () => openChildGroupDialog());
ui.childGroupForm.addEventListener("submit", importFavoriteChild);
ui.childGroupSearch.addEventListener("input", () => {
  // 与池搜索一致的去抖,避免每次击键全量重建列表。
  clearTimeout(ui.childGroupSearch._timer);
  ui.childGroupSearch._timer = setTimeout(renderChildGroupSources, 220);
});
ui.closeChildGroup.addEventListener("click", () => ui.childGroupDialog.close());
ui.cancelChildGroup.addEventListener("click", () => ui.childGroupDialog.close());
ui.deleteGroupForm.addEventListener("submit", confirmGroupDelete);
ui.closeDeleteGroup.addEventListener("click", () => ui.deleteGroupDialog.close());
ui.cancelDeleteGroup.addEventListener("click", () => ui.deleteGroupDialog.close());
ui.addLora.addEventListener("click", openLoraDialog);
ui.resetLoras.addEventListener("click", () => {
  draft.loras = defaultLorasForProfile(currentProfile());
  reconcileLoraTriggers(draft.loras.filter((item) => item.enabled));
  renderLoras();
  schedulePersist();
  toast("已恢复模板默认 LoRA");
});
ui.closeLora.addEventListener("click", () => ui.loraDialog.close());
ui.cancelLora.addEventListener("click", () => ui.loraDialog.close());
ui.loraTriggerForm.addEventListener("submit", saveLoraTriggers);
ui.resetLoraTriggers.addEventListener("click", resetLoraTriggers);
ui.closeLoraTriggers.addEventListener("click", () => ui.loraTriggerDialog.close());
ui.cancelLoraTriggers.addEventListener("click", () => ui.loraTriggerDialog.close());
ui.loraSearch.addEventListener("input", () => {
  clearTimeout(ui.loraSearch._timer);
  ui.loraSearch._timer = setTimeout(renderLoraCatalog, 220);
});
ui.extra_prompt.addEventListener("input", forgetRemovedManagedTriggers);
ui.female_count.addEventListener("input", updatePeopleTotal);
ui.male_count.addEventListener("input", updatePeopleTotal);
ui.resetSettings.addEventListener("click", () => {
  if (!confirm("恢复全部模板默认设置？")) return;
  const profile = currentProfile();
  if (typeof singleWorkbenchActive === "function" && singleWorkbenchActive()) applySettings(singleWorkbenchDefaultSettings(profile));
  else applySettings(typeof randomWorkbenchDefaultSettings === "function" ? randomWorkbenchDefaultSettings(profile) : { ...profileSettingsDefaults(profile), model_profile: profile });
  schedulePersist();
  toast("已恢复当前模型档案默认设置");
});
ui.clearDraft.addEventListener("click", () => {
  if (!confirm("清除本地草稿并恢复默认设置？收藏不会被删除。")) return;
  localStorage.removeItem(DRAFT_KEY);
  if (typeof singleWorkbenchState !== "undefined" && singleWorkbenchState.ready) { localStorage.removeItem(WORKBENCH_DRAFT_KEY); singleWorkbenchState.drafts = { random: {}, single: {} }; singleWorkbenchState.mode = "random"; }
  for (const key of LEGACY_DRAFT_KEYS) localStorage.removeItem(key);
  localStorage.removeItem(VIEW_KEY);
  poolViews = Object.fromEntries(SECTIONS.map((section) => [section, defaultView()]));
  applySettings(typeof randomWorkbenchDefaultSettings === "function" ? randomWorkbenchDefaultSettings(defaults.model_profile) : defaults);
  ui.draftStatus.textContent = "本地草稿已清除";
  toast("已清除本地草稿");
});
document.querySelectorAll(".mobile-tabs button[data-mobile-view]").forEach((button) =>
  button.addEventListener("click", () => {
    const view = button.dataset.mobileView;
    document
      .querySelectorAll(".mobile-tabs button")
      .forEach((item) => item.classList.toggle("active", item === button));
    document.querySelector(".workspace").dataset.mobileView = view;
  }),
);
document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") return;
  if (ui.kreaPoolDrawer.classList.contains("open")) closeKreaPromptPool();
  else if (ui.poolDrawer.classList.contains("open")) closePool();
});

async function initialize() {
  applyTheme(initialTheme());
  try {
    const payload = await request("/api/config");
    config = payload;
    defaults = payload.defaults;
    renderStorageStatus();
    // 启动期数据文件恢复等服务端警告(如坏文件已备份重建)逐条提示。
    for (const warning of payload.warnings || []) toast(warning);
    loadStoredView();
    const restored = loadStoredDraft();
    if (!restored) {
      applySettings(typeof randomWorkbenchDefaultSettings === "function" ? randomWorkbenchDefaultSettings(defaults.model_profile) : defaults);
      ui.draftStatus.textContent = "使用模板默认设置";
    }
    if (typeof initializeSingleWorkbench === "function") initializeSingleWorkbench();
    initialized = true;
  } catch (error) {
    const message = `界面初始化失败：${error.message}`;
    ui.appError.textContent = message;
    ui.appError.hidden = false;
    toast(message);
    return;
  }
  await Promise.allSettled([
    loadResources(),
    loadKreaPromptPool(),
    refreshConnection(),
    pollBatch(),
    loadHistory(1),
    loadLoraInventory(),
    loadArtistFavorites(),
    loadStylePresets(),
  ]);
  setInterval(pollBatch, 400);
  setInterval(refreshConnection, 5000);
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) {
      pollIdleTicks = 0;
      pollBatch();
      refreshConnection();
    }
  });
}
initialize();
