/* Image analysis has its own assets, drafts and jobs; it never changes generation settings. */
const PROMPT_REVERSE_KEY = "anima-studio:prompt-reverse:v2";
const PROMPT_REVERSE_LEGACY_KEY = "anima-studio:prompt-reverse:v1";
const PROMPT_REVERSE_METHODS = ["metadata", "vision", "tagger"];
const PROMPT_REVERSE_LABELS = { metadata: "元数据提取", vision: "视觉 AI", tagger: "本地标签" };
const PROMPT_REVERSE_LEASE_INTERVAL = 60_000;
const PROMPT_REVERSE_VIEW_KEYS = ["result", "negative", "parameters", "recognition", "candidates", "candidate", "source", "format", "warnings", "resultFor", "status", "viewRevision", "poolItem"];
function promptReverseEmptyView() {
  return { result: "", negative: "", parameters: {}, recognition: {}, candidates: [], candidate: "", source: "", format: "", warnings: [], resultFor: "", status: "", viewRevision: 0, poolItem: null };
}
const promptReverseState = { ready: false, mode: "rewrite", options: null, optionsRequest: 0, drafts: {}, runtime: {}, epoch: 0, lease: null };
function promptReverseDraft(profile = currentProfile()) {
  return promptReverseState.drafts[profile] ||= {
    imageId: "", image: null, method: "metadata", service: "", model: "", template: `builtin:reverse:${profile}`, seed: "random", fixedSeed: "0",
    tagger: { model: "pixai-tagger-v0.9-timm", general_threshold: 0.35, character_threshold: 0.75, exclude_tags: "" }, taggerConfigured: false,
    ...promptReverseEmptyView(), results: {},
    revision: 0, resultFor: "", jobId: "", jobRevision: 0, jobFingerprint: "", cancelRequested: false,
    jobMethod: "metadata", jobImageId: "", jobViewRevision: 0, jobStatus: "", busy: false, undo: null, negativeUndo: null,
  };
}
function promptReverseSaveView(draft) {
  draft.results ||= {};
  draft.results[draft.method] = Object.fromEntries(PROMPT_REVERSE_VIEW_KEYS.map(key => [key, draft[key]]));
}
function promptReverseSetMethod(method, focus = false, profile = currentProfile()) {
  if (!PROMPT_REVERSE_METHODS.includes(method)) return;
  const draft = promptReverseDraft(profile);
  if (method !== draft.method) {
    promptReverseSaveView(draft); draft.method = method;
    Object.assign(draft, promptReverseEmptyView(), draft.results[method] || {});
  }
  promptReversePersist(); if (profile === currentProfile()) promptReverseRender();
  if (focus) document.getElementById(`reverseMethod-${method}`).focus();
}
function promptReverseRuntime(profile = currentProfile()) {
  return promptReverseState.runtime[profile] ||= { request: 0, restoreRequest: 0, abort: null, uploading: false, timer: null };
}
function promptReverseLease() {
  if (!promptReverseState.lease) {
    // A fresh identity per document keeps independent tabs and refreshes from
    // releasing one another's images. Never persist this in shared storage.
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
    const hex = Array.from(bytes, value => value.toString(16).padStart(2, "0")).join("");
    promptReverseState.lease = { clientId: `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`, revision: 0, timer: null };
  }
  return promptReverseState.lease;
}
async function promptReverseRenewLease({ restore = false } = {}) {
  if (!promptReverseState.ready) return;
  const lease = promptReverseLease(), epoch = promptReverseState.epoch;
  clearTimeout(lease.timer);
  // Schedule before awaiting the network so a stalled request cannot stop
  // future renewals. The server rejects older revisions arriving out of order.
  lease.timer = setTimeout(() => { void promptReverseRenewLease(); }, PROMPT_REVERSE_LEASE_INTERVAL);
  const snapshots = Object.entries(promptReverseState.drafts).filter(([, draft]) => draft.imageId).map(([profile, draft]) => ({ profile, draft, imageId: draft.imageId, token: promptReverseRuntime(profile).request }));
  const imageIds = [...new Set(snapshots.map(item => item.imageId))].sort();
  if (!imageIds.length && !lease.revision) return;
  const revision = ++lease.revision;
  try {
    const result = await request(`/api/prompt-images/leases/${encodeURIComponent(lease.clientId)}`, {
      method: "PUT", body: JSON.stringify({ revision, image_ids: imageIds }),
      signal: typeof AbortSignal !== "undefined" && AbortSignal.timeout ? AbortSignal.timeout(10_000) : undefined,
    });
    if (epoch !== promptReverseState.epoch || revision !== lease.revision || result.revision !== revision) return;
    const missing = new Set(Array.isArray(result.missing_image_ids) ? result.missing_image_ids : []), restores = [];
    let changed = false;
    for (const { profile, draft, imageId, token } of snapshots) {
      if (draft !== promptReverseState.drafts[profile] || draft.imageId !== imageId || promptReverseRuntime(profile).request !== token) continue;
      if (missing.has(imageId)) {
        promptReverseRuntime(profile).restoreRequest++;
        draft.image = null; draft.status = "原图片不可用，请重新添加；已保存的结果仍可查看。"; changed = true;
      } else if (restore) restores.push(promptReverseRestore(profile, { poll: false }));
    }
    if (changed) { promptReversePersist(); promptReverseRender(); }
    await Promise.all(restores);
  } catch { /* A later heartbeat retries without disturbing prompts or jobs. */ }
}
function promptReverseMarkup() {
  return `<div id="promptReversePanel" class="prompt-desk-panel" role="tabpanel" aria-labelledby="promptDeskReverse" hidden>
    <p class="field-hint">添加图片后先读取元数据。没有提示词时，可选择视觉 AI 或本地标签，再开始识别。</p>
    <div id="reverseInputDropZone" class="image-input-zone reverse-input-zone" tabindex="0" role="group" aria-label="提示词反推识别框" aria-describedby="reverseImageStatus">
      <div class="single-reference-heading"><label class="button ghost compact single-upload-button">＋ 添加反推图片<input id="reverseInputUpload" class="sr-only" type="file" accept="image/png,image/jpeg,image/webp"></label><span id="reverseImageStatus" class="field-hint">拖入图片，或点击此框后粘贴。PNG / JPEG / WebP，最大 20 MiB。</span></div>
      <img id="reverseInputPreview" alt="用于提示词反推的图片" hidden>
    </div>
    <div class="single-inline-actions"><button id="reverseRemove" class="button ghost compact" type="button" hidden>移除图片</button><button id="reverseRefresh" class="button ghost compact" type="button">刷新选项</button></div>
    <div class="reverse-method-switch" role="tablist" aria-label="反推方式">${PROMPT_REVERSE_METHODS.map(method => `<button id="reverseMethod-${method}" type="button" role="tab" aria-controls="reverseMethodPanel" aria-selected="${method === "metadata"}" tabindex="${method === "metadata" ? 0 : -1}">${PROMPT_REVERSE_LABELS[method]}</button>`).join("")}</div>
    <div id="reverseMethodPanel" class="reverse-method-panel" role="tabpanel" aria-labelledby="reverseMethod-metadata">
    <p id="reverseMetadataHint" class="field-hint">直接读取原图保存的提示词与参数，无需模型或联网。</p>
    <div id="reverseVisionOptions" class="reverse-method-options" hidden><div class="prompt-assistant-selectors">
      <label class="field"><span>AI 服务</span><select id="reverseService"></select></label>
      <label class="field"><span>视觉模型</span><select id="reverseModel"></select></label>
      <label class="field"><span>反推系统提示词</span><select id="reverseTemplate"></select></label>
    </div>
    <div class="reverse-seed-fields"><label class="field"><span>反推种子</span><select id="reverseSeedMode"><option value="random">每次随机</option><option value="fixed">固定种子</option></select></label><label id="reverseSeedField" class="field" hidden><span>固定种子</span><input id="reverseSeed" type="text" inputmode="numeric" pattern="[0-9]+" maxlength="20" value="0"></label></div>
    <p class="field-hint">视觉模型根据图片生成描述。反推种子只用于本次识别，不修改生图种子。</p></div>
    <div id="reverseTaggerOptions" class="reverse-method-options" hidden>
      <label class="field"><span>本地标签模型</span><select id="reverseTaggerModel"></select></label><p id="reverseTaggerState" class="field-hint" role="status"></p>
      <details class="reverse-details reverse-tagger-settings"><summary>高级识别设置</summary><div class="reverse-threshold-fields"><label class="field"><span>普通标签阈值</span><input id="reverseGeneralThreshold" type="number" min="0" max="1" step="0.01" value="0.35"></label><label class="field"><span>角色标签阈值</span><input id="reverseCharacterThreshold" type="number" min="0" max="1" step="0.01" value="0.75"></label></div><label class="field"><span>排除标签</span><textarea id="reverseExcludeTags" rows="2" maxlength="20000" placeholder="用逗号分隔不需要的标签"></textarea></label></details>
      <p class="field-hint">使用本机模型识别标签。模型缺失时，请在启动器的「本地反推模型」中下载，再刷新选项。</p>
    </div></div>
    <div class="single-inline-actions"><button id="reverseStart" class="button primary compact" type="button">提取元数据</button><button id="reverseCancel" class="button ghost compact" type="button" hidden>取消识别</button><span id="reverseSource" class="reverse-source" hidden></span></div>
    <p id="reverseStatus" class="field-hint" role="status" aria-live="polite"></p>
    <label id="reverseCandidateField" class="field" hidden><span>发现多组提示词，请选择</span><select id="reverseCandidate"></select></label>
    <label class="field"><span>正面提示词 · 可编辑</span><textarea id="reverseResult" rows="6" maxlength="20000" placeholder="提取或反推的提示词会显示在这里。"></textarea></label>
    <div class="single-inline-actions"><button id="reverseCopy" class="button ghost compact" type="button">复制提示词</button><button id="reverseAppend" class="button ghost compact" type="button">追加到正文</button><button id="reverseReplace" class="button primary compact" type="button">替换正文</button><button id="reverseUndo" class="button ghost compact" type="button" hidden>撤销正文导入</button></div>
    <label class="field"><span>负面提示词 · 可编辑</span><textarea id="reverseNegative" rows="3" maxlength="20000" placeholder="图片包含负面提示词时会显示在这里。"></textarea></label>
    <div class="single-inline-actions"><button id="reverseNegativeCopy" class="button ghost compact" type="button">复制负面词</button><button id="reverseNegativeReplace" class="button ghost compact" type="button">替换负面词</button><button id="reverseNegativeUndo" class="button ghost compact" type="button" hidden>撤销负面词导入</button></div>
    <details id="reverseDetails" class="reverse-details" hidden><summary>图片参数与解析说明（仅供参考）</summary><table class="reverse-parameter-table" aria-label="图片生成参数（只读）"><tbody id="reverseParameters"></tbody></table><p id="reverseWarnings" class="field-hint"></p></details>
    <details id="reverseRecognitionDetails" class="reverse-details" hidden><summary>本次识别设置（仅供参考）</summary><table class="reverse-parameter-table" aria-label="反推识别设置（只读）"><tbody id="reverseRecognition"></tbody></table></details>
  </div>`;
}
function promptReversePersist() {
  for (const draft of Object.values(promptReverseState.drafts)) promptReverseSaveView(draft);
  try { localStorage.setItem(PROMPT_REVERSE_KEY, JSON.stringify({ version: 2, mode: promptReverseState.mode, drafts: promptReverseState.drafts })); } catch { /* Keep the active draft usable. */ }
}
function promptReverseFingerprint(draft, method = draft.method) {
  if (method === "metadata" || method === "none") return JSON.stringify([draft.imageId]);
  if (method === "tagger") return JSON.stringify([draft.imageId, "tagger", draft.tagger]);
  return JSON.stringify([draft.imageId, draft.service, draft.model, draft.template, draft.seed]);
}
function promptReverseHasMetadata(draft) {
  const metadata = draft.image?.metadata;
  return metadata?.source === "metadata" && Boolean(metadata.text?.trim() || metadata.candidates?.some(candidate => candidate.text?.trim()));
}
function promptReverseIssue(draft, method = draft.method) {
  if (!draft.imageId || !draft.image) return "请先添加一张图片。";
  if (promptReverseHasMetadata(draft) || method === "metadata") return "";
  const options = promptReverseState.options;
  if (!options) return "正在读取识别选项；图片已保留。";
  if (method === "tagger") {
    const settings = options.methods?.tagger;
    if (!settings?.available) return settings?.reason || "本地标签节点不可用，请在启动器中安装后刷新选项。";
    const model = settings.models?.find(item => item.name === draft.tagger.model);
    if (!model) return "请选择本地标签模型。";
    if (model.state !== "ready") return model.reason || "本地模型尚未就绪，请在启动器中准备模型后刷新选项。";
    if (![draft.tagger.general_threshold, draft.tagger.character_threshold].every(value => typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 1)) return "识别阈值需在 0 到 1 之间。";
    return "";
  }
  const vision = options.methods?.vision || options;
  if (vision.available === false) return vision.reason || "AI 反推服务当前不可用，请检查启动器配置后刷新选项。";
  const service = (options.services || []).find(item => item.id === draft.service);
  if (!service) return options.reason || "图片没有可用提示词，请选择支持图片输入的 AI 服务。";
  if (!(service.models || []).some(item => item.name === draft.model)) return "请选择支持图片输入的模型。";
  if (!(options.templates || []).some(item => item.id === draft.template)) return "请选择反推系统提示词。";
  if (draft.seed !== "random" && (!/^\d{1,20}$/.test(draft.seed) || BigInt(draft.seed) > 18446744073709551615n)) return "固定种子需为 0 到 18446744073709551615 之间的整数。";
  return "";
}
function promptReverseStale(draft) {
  return Boolean((draft.result || draft.negative) && draft.resultFor !== promptReverseFingerprint(draft, draft.source || draft.method));
}
function promptReverseParameterLabel(key) {
  const labels = { seed: "图像种子", sampleseed: "图像种子", promptseed: "提示词种子", steps: "采样步数",
    cfg: "CFG", cfgscale: "CFG", sampler: "采样器", samplername: "采样器", scheduler: "调度器",
    model: "模型", modelname: "模型", modelhash: "模型哈希", size: "尺寸", imagesize: "尺寸", width: "宽度", height: "高度",
    denoise: "重绘强度", denoisingstrength: "重绘强度", clipskip: "CLIP 跳过层数", vae: "VAE", vaename: "VAE",
    loras: "LoRA", modelprofile: "模型类型", generationmode: "生成方式", sequence: "序号", pipelineprompts: "各阶段提示词",
    method: "识别方式", generalthreshold: "普通标签阈值", characterthreshold: "角色标签阈值", excludetags: "排除标签", templateid: "反推提示词", serviceid: "服务", dtype: "精度" };
  return labels[String(key).toLowerCase().replace(/[^a-z0-9]/g, "")] || key;
}
function promptReverseRenderParameters(parameters, targetId = "reverseParameters") {
  const rows = [], seen = new Set(), labels = new Set();
  for (const [key, value] of Object.entries(parameters || {})) {
    const label = targetId === "reverseRecognition" && key.toLowerCase() === "seed" ? "反推种子" : promptReverseParameterLabel(key), encoded = JSON.stringify(value);
    const signature = JSON.stringify([label, encoded]);
    if (seen.has(signature)) continue;
    seen.add(signature);
    const row = document.createElement("tr"), heading = document.createElement("th"), cell = document.createElement("td");
    heading.setAttribute("scope", "row"); heading.textContent = labels.has(label) ? `${label}（${key}）` : label; labels.add(label);
    const complex = value !== null && typeof value === "object", text = complex ? JSON.stringify(value, null, 2) : String(value ?? "");
    if (complex || text.length > 160 || text.split("\n").length > 3) {
      const details = document.createElement("details"), summary = document.createElement("summary"), pre = document.createElement("pre");
      summary.textContent = complex ? "展开结构化参数" : `展开完整内容（${text.length} 字符）`;
      pre.textContent = text; details.append(summary, pre); cell.append(details);
    } else cell.textContent = text;
    row.append(heading, cell); rows.push(row);
  }
  document.getElementById(targetId).replaceChildren(...rows);
}
function promptDeskPositionIndicator({ immediate = false } = {}) {
  if (typeof animateStudioIndicator !== "function") return;
  const tabs = document.getElementById("promptDeskTabs");
  const thumb = tabs?.querySelector(".prompt-desk-thumb");
  const button = document.getElementById(promptReverseState.mode === "reverse" ? "promptDeskReverse" : "promptDeskRewrite");
  if (!thumb || !button) return;
  animateStudioIndicator(thumb, { x: button.offsetLeft - thumb.offsetLeft, width: button.offsetWidth }, { immediate });
}
function promptReverseRender() {
  if (!promptReverseState.ready) return;
  const reverse = promptReverseState.mode === "reverse", draft = promptReverseDraft(), runtime = promptReverseRuntime();
  const get = id => document.getElementById(id);
  get("promptRewritePanel").hidden = reverse; get("promptReversePanel").hidden = !reverse;
  get("promptDeskTabs").classList.toggle("is-reverse", reverse);
  for (const [id, selected] of [["promptDeskRewrite", !reverse], ["promptDeskReverse", reverse]]) {
    get(id).setAttribute("aria-selected", String(selected)); get(id).tabIndex = selected ? 0 : -1;
  }
  promptDeskPositionIndicator();
  const options = promptReverseState.options || {}, service = (options.services || []).find(item => item.id === draft.service);
  for (const method of PROMPT_REVERSE_METHODS) {
    const selected = draft.method === method;
    get(`reverseMethod-${method}`).setAttribute("aria-selected", String(selected)); get(`reverseMethod-${method}`).tabIndex = selected ? 0 : -1;
  }
  get("reverseMethodPanel").setAttribute("aria-labelledby", `reverseMethod-${draft.method}`);
  get("reverseMetadataHint").hidden = draft.method !== "metadata";
  get("reverseVisionOptions").hidden = draft.method !== "vision";
  get("reverseTaggerOptions").hidden = draft.method !== "tagger";
  promptAssistantSelect("reverseService", options.services || [], draft.service, "选择服务");
  promptAssistantSelect("reverseModel", (service?.models || []).map(item => ({ id: item.name, name: item.name })), draft.model, "选择视觉模型");
  promptAssistantSelect("reverseTemplate", options.templates || [], draft.template, "选择反推提示词");
  get("reverseSeedMode").value = draft.seed === "random" ? "random" : "fixed";
  get("reverseSeedField").hidden = draft.seed === "random"; get("reverseSeed").value = draft.fixedSeed;
  const tagger = options.methods?.tagger, modelStates = { ready: "已就绪", missing: "待下载", incomplete: "需补全", invalid: "需修复", unverifiable: "目录待确认" };
  promptAssistantSelect("reverseTaggerModel", (tagger?.models || []).map(item => ({ id: item.name, name: `${item.name} · ${modelStates[item.state] || "未就绪"}` })), draft.tagger.model, "选择本地模型");
  const taggerModel = tagger?.models?.find(item => item.name === draft.tagger.model);
  get("reverseTaggerState").textContent = taggerModel?.state === "ready" ? "模型已就绪，识别将在本机运行。" : taggerModel?.reason || tagger?.reason || "正在读取本地模型状态…";
  get("reverseGeneralThreshold").value = draft.tagger.general_threshold;
  get("reverseCharacterThreshold").value = draft.tagger.character_threshold;
  get("reverseExcludeTags").value = draft.tagger.exclude_tags;
  const image = get("reverseInputPreview"); image.hidden = !draft.image;
  if (draft.image) {
    const src = `/api/prompt-images/${encodeURIComponent(draft.imageId)}/image`;
    if (image.getAttribute("src") !== src) image.src = src;
  } else image.removeAttribute("src");
  get("reverseImageStatus").textContent = runtime.uploading ? "正在上传图片并读取元数据…" : draft.image ? `${draft.image.filename} · ${draft.image.width} × ${draft.image.height}` : draft.imageId ? "原图片不可用，请重新添加；已保存的结果仍可查看。" : "拖入图片，或点击此框后粘贴。PNG / JPEG / WebP，最大 20 MiB。";
  get("reverseInputDropZone").setAttribute("aria-busy", String(runtime.uploading || draft.busy));
  get("reverseRemove").hidden = !draft.imageId && !runtime.uploading;
  get("reverseStart").disabled = runtime.uploading || draft.busy || Boolean(promptReverseIssue(draft));
  get("reverseStart").textContent = draft.method === "metadata" ? "提取元数据" : "开始识别";
  get("reverseCancel").hidden = !draft.busy && !runtime.uploading;
  const stale = promptReverseStale(draft), blocked = runtime.uploading || draft.busy || stale || (draft.candidates.length > 1 && !draft.candidate);
  get("reverseStatus").textContent = draft.busy ? draft.jobStatus || "识别任务处理中…" : draft.status || (stale ? "图片或识别选项已变化，请重新识别；旧结果保留供参考。" : promptReverseIssue(draft) || (draft.method === "metadata" ? "图片已就绪，可提取元数据。" : "图片已就绪，点击「开始识别」运行所选模型。"));
  get("reverseResult").value = draft.result; get("reverseNegative").value = draft.negative;
  for (const id of ["reverseAppend", "reverseReplace"]) get(id).disabled = blocked || !draft.result.trim();
  get("reverseCopy").disabled = !draft.result.trim();
  get("reverseNegativeReplace").disabled = blocked || !draft.negative.trim();
  get("reverseNegativeCopy").disabled = !draft.negative.trim();
  get("reverseUndo").hidden = !draft.undo; get("reverseNegativeUndo").hidden = !draft.negativeUndo;
  get("reverseCandidateField").hidden = draft.candidates.length < 2;
  promptAssistantSelect("reverseCandidate", draft.candidates.map(item => ({ id: item.id, name: item.label })), draft.candidate, "请选择一组提示词");
  get("reverseSource").hidden = !draft.source;
  get("reverseSource").textContent = draft.source === "metadata" ? `元数据提取 · ${draft.format || "图片"}` : draft.source === "ai" ? "视觉 AI 反推" : draft.source === "tagger" ? "本地标签" : "未发现元数据提示词";
  const parameters = Object.entries(draft.parameters || {});
  get("reverseDetails").hidden = !parameters.length && !draft.warnings.length;
  promptReverseRenderParameters(draft.parameters);
  get("reverseWarnings").textContent = draft.warnings.join("\n");
  get("reverseRecognitionDetails").hidden = !Object.keys(draft.recognition || {}).length;
  promptReverseRenderParameters(draft.recognition, "reverseRecognition");
}
function promptReverseSetMode(mode, focus = false) {
  if (!["rewrite", "reverse"].includes(mode)) return;
  const changed = promptReverseState.mode !== mode;
  if (changed && typeof StudioMotion !== "undefined") {
    StudioMotion.presence(document.getElementById(mode === "reverse" ? "promptRewritePanel" : "promptReversePanel"), false, { immediate: true });
  }
  promptReverseState.mode = mode;
  document.getElementById("reverseInputDropZone").classList.toggle("is-dragging", false);
  promptReversePersist(); promptReverseRender();
  if (changed && typeof StudioMotion !== "undefined") {
    StudioMotion.presence(document.getElementById(mode === "reverse" ? "promptReversePanel" : "promptRewritePanel"), true, {
      from: { opacity: 0, y: 4 }, response: .26, damping: 1,
    });
  }
  if (focus) document.getElementById(mode === "reverse" ? "promptDeskReverse" : "promptDeskRewrite").focus({ preventScroll: true });
}
async function promptReverseLoadOptions() {
  const token = ++promptReverseState.optionsRequest;
  try {
    const options = await request("/api/prompt-assistant/options?kind=reverse");
    if (token !== promptReverseState.optionsRequest) return;
    promptReverseState.options = options;
    for (const profile of ["anima", "krea2"]) {
      const draft = promptReverseDraft(profile);
      const defaults = options.methods?.tagger?.defaults;
      if (!draft.taggerConfigured && defaults) {
        for (const key of Object.keys(draft.tagger)) if (typeof defaults[key] === typeof draft.tagger[key]) draft.tagger[key] = defaults[key];
        draft.taggerConfigured = true;
      }
      if (!draft.template) draft.template = options.defaults?.[profile] || `builtin:reverse:${profile}`;
      if (!draft.service && options.services?.length === 1) draft.service = options.services[0].id;
      const service = options.services?.find(item => item.id === draft.service);
      if (!draft.model && service?.models?.length === 1) draft.model = service.models[0].name;
    }
  } catch (error) {
    if (token !== promptReverseState.optionsRequest) return;
    promptReverseState.options = { services: [], templates: [], reason: error.message };
  }
  promptReversePersist(); promptReverseRender();
}
function promptReverseEdit(key, value) {
  const draft = promptReverseDraft(); draft[key] = value; draft.viewRevision++; draft.status = "";
  if (key !== "negative") {
    draft.poolItem = null;
    draft.candidates = draft.candidates.map(({ pool_item, ...candidate }) => candidate);
  }
  if (key === "service") draft.model = "";
  if (key === "result" || key === "negative") {
    draft.resultFor = promptReverseFingerprint(draft, draft.source || draft.method);
    draft.status = "预览已编辑，检查后可手动导入。";
  }
  promptReversePersist(); promptReverseRender();
}
function promptReverseEditTagger(key, value) {
  const draft = promptReverseDraft();
  draft.tagger = { ...draft.tagger, [key]: key.endsWith("threshold") ? value === "" ? null : Number(value) : value };
  draft.taggerConfigured = true;
  draft.viewRevision++; draft.status = ""; promptReversePersist(); promptReverseRender();
}
function promptReverseEditSeed(value, mode = false) {
  const draft = promptReverseDraft();
  draft.poolItem = null;
  draft.candidates = draft.candidates.map(({ pool_item, ...candidate }) => candidate);
  if (mode) draft.seed = value === "random" ? "random" : draft.fixedSeed;
  else { draft.fixedSeed = value; draft.seed = value; }
  draft.viewRevision++; draft.status = ""; promptReversePersist(); promptReverseRender();
}
function promptReverseSelectCandidate(id) {
  const draft = promptReverseDraft(), candidate = draft.candidates.find(item => item.id === id);
  draft.viewRevision++; draft.candidate = candidate?.id || "";
  draft.result = candidate?.text || ""; draft.negative = candidate?.negative || ""; draft.parameters = candidate?.parameters || {};
  draft.poolItem = draft.source === "ai" ? promptPoolReadMetadata(candidate?.pool_item, draft.result) : null;
  draft.resultFor = promptReverseFingerprint(draft, draft.source || draft.method);
  draft.status = candidate ? "已选择这组提示词。检查后可手动导入。" : "请选择一组提示词后再导入。";
  promptReversePersist(); promptReverseRender();
}
function promptReverseApplyResult(draft, result) {
  draft.source = result.source || "metadata"; draft.format = result.format || "";
  draft.warnings = Array.isArray(result.warnings) ? result.warnings.filter(item => typeof item === "string") : [];
  draft.candidates = Array.isArray(result.candidates) ? result.candidates : [];
  const chosen = draft.candidates.length === 1 ? draft.candidates[0] : draft.candidates.length > 1 ? null : result;
  draft.candidate = chosen?.id || "";
  draft.result = chosen?.text || ""; draft.negative = chosen?.negative || ""; draft.parameters = chosen?.parameters || {};
  draft.poolItem = draft.source === "ai" ? promptPoolReadMetadata(chosen?.pool_item || result.pool_item, draft.result) : null;
  draft.recognition = result.recognition && typeof result.recognition === "object" ? result.recognition : {};
  draft.resultFor = promptReverseFingerprint(draft, draft.source);
  draft.status = draft.candidates.length > 1 ? "发现多组提示词，请明确选择一组后导入。" : draft.result ? `${draft.source === "ai" ? "视觉 AI 反推" : draft.source === "tagger" ? "本地标签识别" : "元数据提取"}完成。检查结果后可手动导入。` : result.outcome === "no_metadata" || draft.source === "none" ? "图片没有可用提示词。选择「视觉 AI」或「本地标签」，再点击「开始识别」。" : "未返回可用提示词，请重新识别。";
}
function promptReverseInvalidate(profile, { cancel = true } = {}) {
  const draft = promptReverseDraft(profile), runtime = promptReverseRuntime(profile);
  runtime.request++; runtime.abort?.abort(); runtime.abort = null; runtime.uploading = false; clearTimeout(runtime.timer);
  if (cancel && draft.jobId) void request(`/api/prompt-jobs/${encodeURIComponent(draft.jobId)}/cancel`, { method: "POST" }).catch(() => {});
  draft.revision++; draft.jobRevision = draft.revision; draft.jobId = ""; draft.busy = false; draft.cancelRequested = false;
}
async function promptReverseAcceptInput(files) {
  if (!singleWorkbenchCanReceiveInput() || promptReverseState.mode !== "reverse") return false;
  const list = Array.from(files || []), error = singleWorkbenchInputFileError(list).replaceAll("参考图", "反推图片");
  const profile = currentProfile(), draft = promptReverseDraft(profile);
  if (error) { draft.status = error; promptReverseRender(); return false; }
  promptReverseInvalidate(profile);
  const runtime = promptReverseRuntime(profile), token = runtime.request, epoch = promptReverseState.epoch, controller = new AbortController();
  runtime.abort = controller; runtime.uploading = true; draft.status = "正在读取图片元数据…";
  const current = () => token === runtime.request && epoch === promptReverseState.epoch && promptReverseState.drafts[profile] === draft;
  promptReverseRender();
  try {
    const body = new FormData(); body.append("image", list[0]);
    const response = await fetch("/api/prompt-images", { method: "POST", body, signal: controller.signal });
    const asset = await response.json().catch(() => ({}));
    if (!current()) return false;
    if (!response.ok) throw new Error(asset.error || "图片上传失败，请重试。");
    if (!asset.id || !asset.width || !asset.height) throw new Error("图片信息无效，请重新添加。");
    draft.imageId = asset.id; draft.image = asset; draft.results = {}; draft.method = "metadata";
    Object.assign(draft, promptReverseEmptyView());
    runtime.abort = null;
    promptReversePersist();
    void promptReverseRenewLease();
    if (!current()) return false;
    runtime.uploading = false;
    await promptReverseStart(profile);
    return true;
  } catch (error) {
    if (!current()) return false;
    draft.status = error.name === "AbortError" ? "图片上传已取消。" : `图片读取失败：${error.message}`;
    return false;
  } finally {
    if (current()) { runtime.uploading = false; runtime.abort = null; promptReversePersist(); if (profile === currentProfile()) promptReverseRender(); }
  }
}
async function promptReverseStart(profile = currentProfile()) {
  const draft = promptReverseDraft(profile), runtime = promptReverseRuntime(profile), issue = promptReverseIssue(draft);
  if (draft.busy || runtime.uploading) return;
  if (issue) { draft.status = issue; promptReversePersist(); promptReverseRender(); return; }
  const requestedMethod = draft.method, method = promptReverseHasMetadata(draft) ? "metadata" : requestedMethod;
  if (method !== draft.method) promptReverseSetMethod(method, false, profile);
  const revision = ++draft.revision, fingerprint = promptReverseFingerprint(draft, method), epoch = promptReverseState.epoch;
  draft.busy = true; draft.jobId = ""; draft.jobRevision = revision; draft.jobFingerprint = fingerprint; draft.cancelRequested = false;
  draft.jobMethod = method; draft.jobImageId = draft.imageId; draft.jobViewRevision = draft.viewRevision;
  draft.jobStatus = method === "metadata" ? "正在提取图片提示词…" : `正在创建${PROMPT_REVERSE_LABELS[method]}任务…`;
  promptReversePersist(); promptReverseRender();
  try {
    const payload = { kind: "reverse", method: requestedMethod, model_profile: profile, image_id: draft.imageId };
    if (requestedMethod === "vision") Object.assign(payload, { service_id: draft.service, model_name: draft.model, template_id: draft.template, seed: draft.seed });
    if (requestedMethod === "tagger") payload.tagger = { ...draft.tagger };
    const job = await request("/api/prompt-jobs", { method: "POST", body: JSON.stringify(payload) });
    if (epoch !== promptReverseState.epoch || draft !== promptReverseState.drafts[profile] || revision !== draft.jobRevision) {
      if (job.id) void request(`/api/prompt-jobs/${encodeURIComponent(job.id)}/cancel`, { method: "POST" }).catch(() => {});
      return;
    }
    draft.jobId = job.id;
    promptReversePersist();
    if (draft.cancelRequested) await promptReverseCancelJob(profile, draft, job.id);
    if (job.status && !["queued", "running", "stopping"].includes(job.status)) promptReverseReceive(profile, job, revision, fingerprint, epoch);
    else await promptReversePoll(profile, job.id, revision, fingerprint, epoch);
  } catch (error) {
    if (epoch !== promptReverseState.epoch || draft !== promptReverseState.drafts[profile] || revision !== draft.jobRevision) return;
    draft.busy = false; draft.jobId = "";
    const status = `识别失败：${error.message}`;
    if (draft.method === draft.jobMethod) draft.status = status;
    else { draft.results[draft.jobMethod] ||= promptReverseEmptyView(); draft.results[draft.jobMethod].status = status; }
    promptReversePersist(); if (profile === currentProfile()) promptReverseRender();
  }
}
function promptReverseReceive(profile, job, revision, fingerprint, epoch) {
  const draft = promptReverseState.drafts[profile];
  if (!draft || epoch !== promptReverseState.epoch || revision !== draft.jobRevision) return;
  const pending = ["queued", "running", "stopping"].includes(job.status);
  draft.busy = pending;
  draft.jobStatus = job.error || ({ queued: `${PROMPT_REVERSE_LABELS[draft.jobMethod]}任务排队中…`, running: `${PROMPT_REVERSE_LABELS[draft.jobMethod]}正在识别图片…`, stopping: "正在取消识别…", cancelled: "识别已取消。", interrupted: "识别已中断，可重新尝试。", error: "识别失败，可重新尝试。" }[job.status] || "");
  if (pending) {
    if (draft.cancelRequested) draft.jobStatus = "已请求取消，等待任务停止…";
    promptReverseRuntime(profile).timer = setTimeout(() => promptReversePoll(profile, draft.jobId, revision, fingerprint, epoch), 1200);
  } else {
    draft.jobId = "";
    const target = draft.method === draft.jobMethod ? draft : { ...draft, ...promptReverseEmptyView(), ...(draft.results[draft.jobMethod] || {}), method: draft.jobMethod };
    target.status = draft.jobStatus;
    if (job.status === "completed") {
      if (draft.cancelRequested) target.status = "任务已结束；已按取消请求保留原预览。";
      else if (target.viewRevision === draft.jobViewRevision && draft.imageId === draft.jobImageId && promptReverseFingerprint(draft, draft.jobMethod) === fingerprint) promptReverseApplyResult(target, job.result || {});
      else target.status = "任务已完成，但图片、选项或预览已变化，未覆盖新内容。";
    }
    if (target !== draft) draft.results[draft.jobMethod] = Object.fromEntries(PROMPT_REVERSE_VIEW_KEYS.map(key => [key, target[key]]));
  }
  promptReversePersist(); if (profile === currentProfile()) promptReverseRender();
}
async function promptReversePoll(profile, id, revision, fingerprint, epoch = promptReverseState.epoch) {
  const draft = promptReverseState.drafts[profile];
  if (!draft || !id || draft.jobId !== id || epoch !== promptReverseState.epoch) return;
  try {
    const job = await request(`/api/prompt-jobs/${encodeURIComponent(id)}`);
    if (draft !== promptReverseState.drafts[profile] || draft.jobId !== id || epoch !== promptReverseState.epoch) return;
    promptReverseReceive(profile, job, revision, fingerprint, epoch);
  } catch (error) {
    if (draft.jobId !== id || epoch !== promptReverseState.epoch) return;
    if (error.status === 404) { draft.busy = false; draft.jobId = ""; draft.status = "原识别任务已不存在，请重新识别。"; }
    else { draft.jobStatus = `读取结果失败：${error.message}；正在重试读取，不会重复提交识别。`; promptReverseRuntime(profile).timer = setTimeout(() => promptReversePoll(profile, id, revision, fingerprint, epoch), 2500); }
    promptReversePersist(); if (profile === currentProfile()) promptReverseRender();
  }
}
async function promptReverseCancelJob(profile, draft, id) {
  try { await request(`/api/prompt-jobs/${encodeURIComponent(id)}/cancel`, { method: "POST" }); }
  catch (error) { if (draft.jobId === id) draft.jobStatus = `取消请求失败：${error.message}；返回结果不会覆盖预览。`; }
  promptReversePersist(); if (profile === currentProfile()) promptReverseRender();
}
async function promptReverseCancel() {
  const profile = currentProfile(), draft = promptReverseDraft(profile), runtime = promptReverseRuntime(profile);
  if (runtime.uploading) { runtime.request++; runtime.abort?.abort(); runtime.abort = null; runtime.uploading = false; draft.status = "图片上传已取消。"; }
  if (draft.busy) { draft.cancelRequested = true; draft.jobStatus = "正在取消识别…"; if (draft.jobId) await promptReverseCancelJob(profile, draft, draft.jobId); }
  promptReversePersist(); promptReverseRender();
}
function promptReverseRemove() {
  const profile = currentProfile(), draft = promptReverseDraft(profile);
  promptReverseInvalidate(profile);
  draft.imageId = ""; draft.image = null; draft.status = "已移除图片，现有预览保留供参考。";
  promptReversePersist(); promptReverseRender();
  void promptReverseRenewLease();
}
function promptReverseAdopt(mode, negative = false) {
  const draft = promptReverseDraft(), text = (negative ? draft.negative : draft.result).trim();
  if (!text || draft.busy || promptReverseRuntime().uploading || promptReverseStale(draft) || (draft.candidates.length > 1 && !draft.candidate)) return;
  const target = document.getElementById(negative ? "singleNegative" : "single_positive_prompt"), before = target.value;
  const after = mode === "append" && before.trim() ? `${before.replace(/\s+$/, "")}\n\n${text}` : text;
  if (Array.from(after).length > 20000) {
    draft.status = `${negative ? "负面提示词" : "画面描述"}最多支持 20,000 个字符，请精简结果后导入。`;
    promptReversePersist(); promptReverseRender(); return;
  }
  if (before === after) return;
  target.value = after; target.dispatchEvent(new Event("input", { bubbles: true }));
  if (!negative) promptPoolSetBodyMetadata(mode !== "append" || !before.trim() ? draft.poolItem : null, after);
  draft[negative ? "negativeUndo" : "undo"] = { before, after };
  draft.status = negative ? "已替换负面提示词，可撤销。" : mode === "append" ? "已追加到画面描述，可撤销。" : "已替换画面描述，可撤销。";
  promptReversePersist(); promptReverseRender(); schedulePersist();
}
function promptReverseUndo(negative = false) {
  const draft = promptReverseDraft(), key = negative ? "negativeUndo" : "undo", undo = draft[key];
  if (!undo) return;
  const target = document.getElementById(negative ? "singleNegative" : "single_positive_prompt");
  if (target.value === undo.after) { target.value = undo.before; target.dispatchEvent(new Event("input", { bubbles: true })); if (!negative) promptPoolSetBodyMetadata(null, undo.before); draft.status = "已撤销上次导入。"; }
  else draft.status = "内容已继续编辑，已保留你的新内容。";
  draft[key] = null; promptReversePersist(); promptReverseRender(); schedulePersist();
}
async function promptReverseCopy(negative = false) {
  const draft = promptReverseDraft();
  try { await navigator.clipboard.writeText(negative ? draft.negative : draft.result); draft.status = "已复制。"; }
  catch { draft.status = "浏览器未允许复制，请在结果框中选中文本后复制。"; }
  promptReverseRender();
}
async function promptReverseRestore(profile, { poll = true } = {}) {
  const draft = promptReverseDraft(profile), runtime = promptReverseRuntime(profile), token = runtime.request, epoch = promptReverseState.epoch, imageId = draft.imageId;
  const restoreToken = ++runtime.restoreRequest;
  const job = { id: draft.jobId, revision: draft.jobRevision, fingerprint: draft.jobFingerprint };
  const sameDraft = () => epoch === promptReverseState.epoch && draft === promptReverseState.drafts[profile] && imageId === draft.imageId && token === runtime.request;
  const current = () => sameDraft() && restoreToken === runtime.restoreRequest;
  if (imageId) {
    try {
      const asset = await request(`/api/prompt-images/${encodeURIComponent(imageId)}`);
      if (current()) {
        if (asset.id !== imageId) throw new Error("图片记录不匹配");
        draft.image = asset;
      }
    } catch (error) {
      if (current()) { draft.image = null; draft.status = `原图片不可用：${error.message}；请重新添加图片。`; }
    }
    if (current()) { promptReversePersist(); if (profile === currentProfile()) promptReverseRender(); }
  }
  // A newer visibility check may supersede only the image read; it must not
  // swallow the initial recovery of an existing recognition job.
  if (poll && sameDraft() && job.id && draft.jobId === job.id && draft.jobRevision === job.revision) {
    if (draft.cancelRequested) await promptReverseCancelJob(profile, draft, job.id);
    await promptReversePoll(profile, job.id, job.revision, job.fingerprint, epoch);
  }
}
function promptReverseClear() {
  promptReverseState.epoch++;
  for (const runtime of Object.values(promptReverseState.runtime)) { runtime.abort?.abort(); clearTimeout(runtime.timer); }
  promptReverseState.drafts = {}; promptReverseState.runtime = {}; promptReverseState.mode = "rewrite";
  localStorage.removeItem(PROMPT_REVERSE_KEY); localStorage.removeItem(PROMPT_REVERSE_LEGACY_KEY); promptReverseRender();
  void promptReverseRenewLease();
}
function promptReverseReadView(value) {
  const view = promptReverseEmptyView();
  if (!value || typeof value !== "object") return view;
  for (const key of ["result", "negative", "candidate", "source", "format", "resultFor", "status"]) if (typeof value[key] === "string") view[key] = value[key];
  view.viewRevision = Number.isSafeInteger(value.viewRevision) ? value.viewRevision : 0;
  view.candidates = Array.isArray(value.candidates) ? value.candidates.filter(item => item && typeof item.id === "string" && typeof item.text === "string") : [];
  view.warnings = Array.isArray(value.warnings) ? value.warnings.filter(item => typeof item === "string") : [];
  for (const key of ["parameters", "recognition"]) if (value[key] && typeof value[key] === "object" && !Array.isArray(value[key])) view[key] = value[key];
  view.poolItem = view.source === "ai" ? promptPoolReadMetadata(value.poolItem, view.result) : null;
  return view;
}
function initializePromptReverse() {
  if (promptReverseState.ready) return;
  try {
    const saved = JSON.parse(localStorage.getItem(PROMPT_REVERSE_KEY) || localStorage.getItem(PROMPT_REVERSE_LEGACY_KEY) || "null");
    if ([1, 2].includes(saved?.version)) {
      promptReverseState.mode = saved.mode === "reverse" ? "reverse" : "rewrite";
      for (const profile of ["anima", "krea2"]) {
        const value = saved.drafts?.[profile]; if (!value || typeof value !== "object") continue;
        const draft = promptReverseDraft(profile);
        for (const key of ["imageId", "service", "model", "template", "seed", "fixedSeed", "jobId", "jobFingerprint", "jobImageId"]) if (typeof value[key] === "string") draft[key] = value[key];
        draft.method = saved.version === 2 && PROMPT_REVERSE_METHODS.includes(value.method) ? value.method : value.source === "ai" || value.jobId ? "vision" : "metadata";
        if (value.tagger && typeof value.tagger === "object") for (const key of Object.keys(draft.tagger)) if (typeof value.tagger[key] === typeof draft.tagger[key]) draft.tagger[key] = value.tagger[key];
        draft.taggerConfigured = typeof value.taggerConfigured === "boolean" ? value.taggerConfigured : Boolean(value.tagger);
        for (const method of PROMPT_REVERSE_METHODS) if (value.results?.[method]) draft.results[method] = promptReverseReadView(value.results[method]);
        Object.assign(draft, promptReverseReadView(value));
        draft.revision = Number.isSafeInteger(value.revision) ? value.revision : 0;
        draft.jobRevision = Number.isSafeInteger(value.jobRevision) ? value.jobRevision : draft.revision;
        draft.jobMethod = PROMPT_REVERSE_METHODS.includes(value.jobMethod) ? value.jobMethod : draft.method;
        draft.jobImageId ||= draft.imageId;
        draft.jobViewRevision = Number.isSafeInteger(value.jobViewRevision) ? value.jobViewRevision : draft.viewRevision;
        if (saved.version === 1) {
          const legacyFingerprint = draft.source === "metadata" ? JSON.stringify([draft.imageId]) : JSON.stringify([draft.imageId, draft.service, draft.model, draft.template]);
          if (draft.source && draft.resultFor === legacyFingerprint) draft.resultFor = promptReverseFingerprint(draft, draft.source);
          draft.jobFingerprint = promptReverseFingerprint(draft, draft.jobMethod);
          if (draft.jobRevision !== draft.revision) draft.viewRevision++;
        }
        draft.cancelRequested = Boolean(value.cancelRequested); draft.busy = Boolean(draft.jobId);
        if (draft.busy) draft.jobStatus = "正在恢复上次识别任务…";
        else if (value.busy) draft.status = "页面在创建任务时刷新，未取得任务编号；请重新识别。";
      }
    }
  } catch { /* Existing rewrite drafts are independent. */ }
  promptReverseState.ready = true;
  const get = id => document.getElementById(id);
  if (typeof ResizeObserver !== "undefined") {
    promptReverseState.tabObserver = new ResizeObserver(() => promptDeskPositionIndicator({ immediate: true }));
    promptReverseState.tabObserver.observe(get("promptDeskTabs"));
  }
  for (const [id, mode] of [["promptDeskRewrite", "rewrite"], ["promptDeskReverse", "reverse"]]) {
    get(id).addEventListener("click", () => promptReverseSetMode(mode));
    get(id).addEventListener("keydown", event => {
      if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
      event.preventDefault(); promptReverseSetMode(event.key === "Home" ? "rewrite" : event.key === "End" ? "reverse" : mode === "rewrite" ? "reverse" : "rewrite", true);
    });
  }
  for (const [index, method] of PROMPT_REVERSE_METHODS.entries()) {
    const tab = get(`reverseMethod-${method}`);
    tab.addEventListener("click", () => promptReverseSetMethod(method));
    tab.addEventListener("keydown", event => {
      if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
      event.preventDefault();
      const next = event.key === "Home" ? 0 : event.key === "End" ? 2 : (index + (event.key === "ArrowRight" ? 1 : 2)) % 3;
      promptReverseSetMethod(PROMPT_REVERSE_METHODS[next], true);
    });
  }
  for (const [id, key] of [["reverseResult", "result"], ["reverseNegative", "negative"], ["reverseService", "service"], ["reverseModel", "model"], ["reverseTemplate", "template"]]) get(id).addEventListener(["result", "negative"].includes(key) ? "input" : "change", event => promptReverseEdit(key, event.target.value));
  get("reverseCandidate").addEventListener("change", event => promptReverseSelectCandidate(event.target.value));
  for (const [id, key] of [["reverseTaggerModel", "model"], ["reverseGeneralThreshold", "general_threshold"], ["reverseCharacterThreshold", "character_threshold"], ["reverseExcludeTags", "exclude_tags"]]) get(id).addEventListener("change", event => promptReverseEditTagger(key, event.target.value));
  get("reverseSeedMode").addEventListener("change", event => promptReverseEditSeed(event.target.value, true));
  get("reverseSeed").addEventListener("input", event => promptReverseEditSeed(event.target.value));
  for (const [id, callback] of [["reverseRefresh", () => { promptReverseState.optionsPromise = promptReverseLoadOptions(); }], ["reverseStart", () => promptReverseStart()], ["reverseCancel", promptReverseCancel], ["reverseRemove", promptReverseRemove], ["reverseCopy", () => promptReverseCopy()], ["reverseAppend", () => promptReverseAdopt("append")], ["reverseReplace", () => promptReverseAdopt("replace")], ["reverseUndo", () => promptReverseUndo()], ["reverseNegativeCopy", () => promptReverseCopy(true)], ["reverseNegativeReplace", () => promptReverseAdopt("replace", true)], ["reverseNegativeUndo", () => promptReverseUndo(true)]]) get(id).addEventListener("click", callback);
  get("reverseInputUpload").addEventListener("change", event => { const files = Array.from(event.target.files || []); event.target.value = ""; if (files.length) void promptReverseAcceptInput(files); });
  bindImageInputZone(get("reverseInputDropZone"), { canReceive: () => singleWorkbenchCanReceiveInput() && promptReverseState.mode === "reverse", accept: promptReverseAcceptInput });
  for (const [id, key] of [["single_positive_prompt", "undo"], ["singleNegative", "negativeUndo"]]) get(id).addEventListener("input", () => {
    const draft = promptReverseDraft(); if (draft[key] && draft[key].after !== get(id).value) { draft[key] = null; promptReverseRender(); }
  });
  const resumeImages = () => { void promptReverseRenewLease({ restore: true }); };
  window.addEventListener("online", resumeImages);
  window.addEventListener("pageshow", resumeImages);
  document.addEventListener("visibilitychange", () => { if (!document.hidden) resumeImages(); });
  promptReverseRender(); promptReverseState.optionsPromise = promptReverseLoadOptions();
  const epoch = promptReverseState.epoch;
  const savedDrafts = ["anima", "krea2"].map(profile => ({ profile, draft: promptReverseDraft(profile), token: promptReverseRuntime(profile).request }));
  // Protect persisted references before reading them; startup cleanup may run
  // concurrently. The server gives new uploads a separate short grace period.
  void promptReverseRenewLease().then(() => {
    if (epoch !== promptReverseState.epoch) return;
    for (const { profile, draft, token } of savedDrafts) {
      if (draft === promptReverseState.drafts[profile] && token === promptReverseRuntime(profile).request && (draft.imageId || draft.jobId)) void promptReverseRestore(profile);
    }
  });
}
