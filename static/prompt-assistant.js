/* The rewrite desk owns text drafts, never generation settings or credentials. */
const PROMPT_ASSISTANT_KEY = "anima-studio:prompt-assistant:v1";
const promptAssistantState = { ready: false, options: null, optionsRequest: 0, drafts: {}, timers: {}, epoch: 0 };
function promptAssistantDraft(profile = currentProfile()) {
  return promptAssistantState.drafts[profile] ||= {
    source: "", service: "", model: "", template: "", result: "", resultFor: "", poolItem: null, bodyPoolItem: null,
    revision: 0, jobId: "", jobRevision: 0, jobFingerprint: "", cancelRequested: false,
    busy: false, status: "", legacy: false, undo: null, migrated: [],
  };
}
function promptPoolReadMetadata(value, text) {
  if (!value || typeof value !== "object" || typeof text !== "string") return null;
  if (!["title", "subtitle", "prompt"].every(key => typeof value[key] === "string")) return null;
  const item = Object.fromEntries(["title", "subtitle", "prompt"].map(key => [key, value[key].trim()]));
  if (!item.title || !item.subtitle || !item.prompt || item.prompt !== text.trim()
      || Array.from(item.title).length > 200 || Array.from(item.subtitle).length > 300 || Array.from(item.prompt).length > 20000) return null;
  return item;
}
// Saving needs a live source, not a historical text lookup: manual edits revoke metadata.
function promptPoolMetadataForText(profile, text, source = null) {
  if (profile !== currentProfile() || typeof text !== "string" || !source) return null;
  const target = typeof source === "string" ? document.getElementById(source) : source;
  if (!target || target.value?.trim() !== text.trim()) return null;
  const start = target.selectionStart ?? 0, end = target.selectionEnd ?? start;
  if (end > start && target.value.slice(start, end).trim() !== target.value.trim()) return null;
  const draft = promptAssistantState.drafts[profile];
  if (target.id === "single_positive_prompt") return promptPoolReadMetadata(draft?.bodyPoolItem, text);
  if (target.id === "rewriteResult") {
    if (!draft || draft.busy || draft.legacy || draft.resultFor !== promptAssistantFingerprint(draft)) return null;
    return promptPoolReadMetadata(draft.poolItem, text);
  }
  if (target.id === "reverseResult" && typeof promptReverseState !== "undefined") {
    const reverse = promptReverseState.drafts[profile];
    if (!reverse || reverse.busy || reverse.source !== "ai" || promptReverseStale(reverse)) return null;
    return promptPoolReadMetadata(reverse.poolItem, text);
  }
  return null;
}
function promptPoolSetBodyMetadata(item, text, profile = currentProfile()) {
  promptAssistantDraft(profile).bodyPoolItem = promptPoolReadMetadata(item, text);
  promptAssistantPersist();
}
function promptAssistantMarkup() {
  return `<section id="singleProcessedSection" class="control-section prompt-assistant" data-single-group="prompt">
    <div class="section-heading prompt-desk-heading"><h2>提示词工作台</h2><div id="promptDeskTabs" class="prompt-desk-switch" role="tablist" aria-label="提示词工作台"><span class="prompt-desk-thumb" aria-hidden="true"></span><button id="promptDeskRewrite" type="button" role="tab" aria-controls="promptRewritePanel" aria-selected="true">重写台</button><button id="promptDeskReverse" type="button" role="tab" aria-controls="promptReversePanel" aria-selected="false" tabindex="-1">反推台</button></div></div>
    <div id="promptRewritePanel" class="prompt-desk-panel" role="tabpanel" aria-labelledby="promptDeskRewrite">
    <div class="single-inline-actions"><button id="rewriteRefresh" class="button ghost compact" type="button">刷新选项</button></div>
    <p class="field-hint">输入与生图正文独立保存。重写完成后，可编辑预览，再手动追加或替换正文。系统提示词和第三方服务在启动器中管理。</p>
    <div class="prompt-assistant-selectors">
      <label class="field"><span>服务</span><select id="rewriteService" aria-describedby="rewriteStatus"></select></label>
      <label class="field"><span>模型</span><select id="rewriteModel"></select></label>
      <label class="field"><span>系统提示词</span><select id="rewriteTemplate"></select></label>
    </div>
    <label class="field"><span>重写输入</span><textarea id="rewriteSource" rows="5" maxlength="20000" placeholder="在这里粘贴或编写待处理文本，不会改变当前画面描述。"></textarea></label>
    <div class="single-inline-actions"><button id="rewriteRead" class="button ghost compact" type="button">读取当前正文</button><button id="rewriteStart" class="button primary compact" type="button">开始重写</button><button id="rewriteCancel" class="button ghost compact" type="button" hidden>取消重写</button></div>
    <p id="rewriteStatus" class="field-hint" role="status" aria-live="polite"></p>
    <label class="field"><span>结果预览 · 可编辑</span><textarea id="rewriteResult" rows="6" maxlength="20000" placeholder="重写结果会显示在这里；生图只使用上方画面描述中的文本。"></textarea></label>
    <div class="single-inline-actions"><button id="rewriteAppend" class="button ghost compact" type="button">追加到正文</button><button id="rewriteReplace" class="button primary compact" type="button">替换正文</button><button id="rewriteUndo" class="button ghost compact" type="button" hidden>撤销导入</button></div>
    </div>${typeof promptReverseMarkup === "function" ? promptReverseMarkup() : ""}
  </section>`;
}
function promptAssistantFingerprint(draft) {
  return JSON.stringify([draft.source, draft.service, draft.model, draft.template]);
}
function promptAssistantPersist() {
  try {
    localStorage.setItem(PROMPT_ASSISTANT_KEY, JSON.stringify({ version: 1, drafts: promptAssistantState.drafts }));
  } catch { /* The generation draft reports browser storage availability separately. */ }
}
function promptAssistantSelect(id, entries, selected, placeholder) {
  const select = document.getElementById(id);
  if (!select) return;
  const values = entries.map(item => new Option(item.name, item.id));
  if (selected && !entries.some(item => item.id === selected)) {
    const missing = new Option(`${selected}（当前不可用）`, selected); missing.disabled = true; values.push(missing);
  }
  select.replaceChildren(new Option(placeholder, ""), ...values);
  select.value = selected || "";
}
function promptAssistantIssue(draft) {
  const options = promptAssistantState.options;
  if (!options) return "正在读取服务与系统提示词…";
  if (!options.available) return options.reason || "请先在启动器中配置并保存服务与系统提示词，再刷新选项。";
  const service = (options.services || []).find(item => item.id === draft.service);
  if (!service) return draft.service ? "所选服务已不可用，请重新选择。" : "请选择服务、模型与系统提示词。";
  if (!(service.models || []).some(item => item.name === draft.model)) return "请选择可用模型。";
  if (!(options.templates || []).some(item => item.id === draft.template)) return "请选择系统提示词。";
  if (!draft.source.trim()) return "请输入待重写文本，或读取当前正文。";
  return "";
}
function promptAssistantRender() {
  if (typeof promptReverseRender === "function") promptReverseRender();
  if (!promptAssistantState.ready || !document.getElementById("rewriteSource")) return;
  const draft = promptAssistantDraft(), options = promptAssistantState.options || {};
  const body = document.getElementById("single_positive_prompt");
  if (draft.bodyPoolItem && body && (typeof singleWorkbenchActive !== "function" || singleWorkbenchActive())
      && (typeof singleWorkbenchState === "undefined" || !singleWorkbenchState.restoring)
      && !promptPoolReadMetadata(draft.bodyPoolItem, body.value)) {
    draft.bodyPoolItem = null; promptAssistantPersist();
  }
  promptAssistantSelect("rewriteService", options.services || [], draft.service, "选择服务");
  const service = (options.services || []).find(item => item.id === draft.service);
  promptAssistantSelect("rewriteModel", (service?.models || []).map(item => ({ id: item.name, name: item.name })), draft.model, "选择模型");
  promptAssistantSelect("rewriteTemplate", options.templates || [], draft.template, "选择系统提示词");
  document.getElementById("rewriteSource").value = draft.source;
  document.getElementById("rewriteResult").value = draft.result;
  const issue = promptAssistantIssue(draft);
  const stale = Boolean(draft.result && !draft.legacy && draft.resultFor !== promptAssistantFingerprint(draft));
  document.getElementById("rewriteStart").disabled = draft.busy || Boolean(issue);
  document.getElementById("rewriteCancel").hidden = !draft.busy;
  for (const id of ["rewriteAppend", "rewriteReplace"]) document.getElementById(id).disabled = !draft.result.trim() || draft.busy || stale;
  document.getElementById("rewriteUndo").hidden = !draft.undo;
  document.getElementById("rewriteStatus").textContent = draft.status || (stale ? "输入或选项已变化，请重新重写；旧预览保留供参考。" : issue || "准备就绪。开始重写后才会调用所选服务。");
}
async function promptAssistantLoadOptions() {
  const token = ++promptAssistantState.optionsRequest;
  try {
    const options = await request("/api/prompt-assistant/options");
    if (token !== promptAssistantState.optionsRequest) return;
    promptAssistantState.options = options;
  } catch (error) {
    if (token !== promptAssistantState.optionsRequest) return;
    promptAssistantState.options = { available: false, reason: error.message, services: [], templates: [] };
  }
  promptAssistantRender();
}
function promptAssistantEdit(key, value) {
  const draft = promptAssistantDraft();
  draft[key] = value;
  draft.poolItem = null;
  draft.revision++;
  if (key !== "result") {
    draft.status = "";
    if (key === "service") draft.model = "";
  } else {
    draft.resultFor = promptAssistantFingerprint(draft); draft.legacy = false;
    draft.status = "预览已编辑，检查后可手动导入正文。";
  }
  promptAssistantPersist();
  promptAssistantRender();
}
function promptAssistantReadCurrent() {
  promptAssistantEdit("source", document.getElementById("single_positive_prompt").value);
  document.getElementById("rewriteSource").focus();
}
async function promptAssistantStart() {
  const profile = currentProfile(), draft = promptAssistantDraft(profile);
  if (draft.busy || promptAssistantIssue(draft)) return;
  const revision = ++draft.revision, epoch = promptAssistantState.epoch;
  const fingerprint = promptAssistantFingerprint(draft);
  draft.busy = true; draft.status = "正在创建重写任务…"; draft.jobId = "";
  draft.jobRevision = revision; draft.jobFingerprint = fingerprint; draft.cancelRequested = false;
  promptAssistantPersist(); promptAssistantRender();
  try {
    const job = await request("/api/prompt-jobs", { method: "POST", body: JSON.stringify({
      kind: "rewrite", model_profile: profile, source_text: draft.source,
      service_id: draft.service, model_name: draft.model, template_id: draft.template,
    }) });
    if (epoch !== promptAssistantState.epoch || promptAssistantState.drafts[profile] !== draft) return;
    draft.jobId = job.id;
    draft.status = "重写任务已排队…";
    promptAssistantPersist();
    if (draft.cancelRequested) await promptAssistantCancelJob(profile, draft, job.id);
    await promptAssistantPoll(profile, job.id, revision, fingerprint, epoch);
  } catch (error) {
    if (epoch !== promptAssistantState.epoch || promptAssistantState.drafts[profile] !== draft) return;
    draft.busy = false; draft.jobId = ""; draft.status = `重写失败：${error.message}`;
    promptAssistantPersist();
    if (profile === currentProfile()) promptAssistantRender();
  }
}
async function promptAssistantPoll(profile, id, revision, fingerprint, epoch = promptAssistantState.epoch) {
  const draft = promptAssistantState.drafts[profile];
  if (!draft || draft.jobId !== id || epoch !== promptAssistantState.epoch) return;
  try {
    const job = await request(`/api/prompt-jobs/${encodeURIComponent(id)}`);
    if (draft !== promptAssistantState.drafts[profile] || draft.jobId !== id || epoch !== promptAssistantState.epoch) return;
    const pending = ["queued", "running", "stopping"].includes(job.status);
    draft.busy = pending;
    draft.status = job.error || ({ queued: "重写任务排队中…", running: "正在重写…", stopping: "正在取消重写…", cancelled: "重写已取消，正文保持原样。", interrupted: "重写已中断，可重新尝试。", error: "重写失败，可重新尝试。" }[job.status] || "");
    if (pending && draft.cancelRequested) draft.status = "已请求取消，等待任务停止…";
    if (pending) promptAssistantState.timers[profile] = setTimeout(() => promptAssistantPoll(profile, id, revision, fingerprint, epoch), 1200);
    else {
      draft.jobId = "";
      if (job.status === "completed") {
        if (draft.cancelRequested) draft.status = "任务已结束；你已请求取消，结果未写入预览或正文。";
        else if (revision === draft.revision && fingerprint === promptAssistantFingerprint(draft)) {
          draft.result = job.result?.text || ""; draft.resultFor = fingerprint; draft.legacy = false;
          draft.poolItem = promptPoolReadMetadata(job.result?.pool_item, draft.result);
          draft.status = draft.result ? "重写完成。检查预览后，手动追加或替换正文。" : "服务未返回可用文本，请重试。";
        } else draft.status = "任务已完成，但输入或选项已修改；未覆盖现有预览。";
      }
    }
    promptAssistantPersist();
    if (profile === currentProfile()) promptAssistantRender();
  } catch (error) {
    if (draft.jobId !== id || epoch !== promptAssistantState.epoch) return;
    if (error.status === 404) {
      draft.busy = false; draft.jobId = ""; draft.status = "原重写任务已不存在，正文与现有预览保持原样。";
    } else {
      draft.busy = true; draft.status = `读取重写结果失败：${error.message}；正在重试读取，不会重新调用 AI。`;
      promptAssistantState.timers[profile] = setTimeout(() => promptAssistantPoll(profile, id, revision, fingerprint, epoch), 2500);
    }
    promptAssistantPersist();
    if (profile === currentProfile()) promptAssistantRender();
  }
}
async function promptAssistantCancelJob(profile, draft, id) {
  try {
    await request(`/api/prompt-jobs/${encodeURIComponent(id)}/cancel`, { method: "POST" });
    if (draft.jobId !== id) return;
    draft.status = "正在取消重写…";
  } catch (error) {
    if (draft.jobId === id) draft.status = `取消失败：${error.message}`;
  }
  promptAssistantPersist();
  if (profile === currentProfile()) promptAssistantRender();
}
async function promptAssistantCancel() {
  const profile = currentProfile(), draft = promptAssistantDraft(profile);
  if (!draft.busy) return;
  draft.cancelRequested = true;
  draft.status = draft.jobId ? "正在请求取消重写…" : "已请求取消，等待任务创建完成…";
  promptAssistantPersist(); promptAssistantRender();
  if (draft.jobId) await promptAssistantCancelJob(profile, draft, draft.jobId);
}
async function promptAssistantResume(profile) {
  const draft = promptAssistantDraft(profile), id = draft.jobId;
  if (!id) return;
  draft.busy = true;
  if (draft.cancelRequested) await promptAssistantCancelJob(profile, draft, id);
  await promptAssistantPoll(profile, id, draft.jobRevision, draft.jobFingerprint);
}
function promptAssistantAdopt(mode) {
  const draft = promptAssistantDraft();
  if (draft.busy || !draft.result.trim() || (!draft.legacy && draft.resultFor !== promptAssistantFingerprint(draft))) return;
  const target = document.getElementById("single_positive_prompt"), before = target.value;
  const text = draft.result.trim();
  const after = mode === "append" && before.trim() ? `${before.replace(/\s+$/, "")}\n\n${text}` : text;
  const metadata = mode !== "append" || !before.trim() ? promptPoolReadMetadata(draft.poolItem, text) : null;
  if (before === after) return;
  target.value = after;
  target.dispatchEvent(new Event("input", { bubbles: true }));
  promptPoolSetBodyMetadata(metadata, after);
  draft.undo = { before, after };
  draft.status = mode === "append" ? "已追加到画面描述，可撤销。" : "已替换画面描述，可撤销。";
  promptAssistantPersist(); promptAssistantRender(); schedulePersist();
}
function promptAssistantUndo() {
  const draft = promptAssistantDraft(), target = document.getElementById("single_positive_prompt");
  if (!draft.undo) return;
  if (target.value !== draft.undo.after) {
    draft.undo = null; draft.status = "正文已继续编辑，已保留你的新内容。";
  } else {
    target.value = draft.undo.before; draft.undo = null;
    target.dispatchEvent(new Event("input", { bubbles: true }));
    draft.status = "已撤销上次导入。";
  }
  promptAssistantPersist(); promptAssistantRender(); schedulePersist();
}
function promptAssistantMigrateLegacy(single, profile = currentProfile()) {
  const draft = promptAssistantDraft(profile);
  const jobId = single.prompt_job_id || "", override = single.processed_prompt_override;
  const key = typeof override === "string" ? `text:${override}` : jobId ? `job:${jobId}` : "";
  if (!key && Object.values(single.prompt_processing || {}).some(stage => stage.enabled) && !draft.status) draft.status = "旧扩写与 OC 自动执行已停用，正文保持原样；需要改写时请在这里手动开始。";
  if (!key || draft.migrated.includes(key)) return;
  draft.migrated.push(key);
  draft.migrated = draft.migrated.slice(-16);
  if (draft.result) { promptAssistantPersist(); return; }
  const revision = draft.revision, epoch = promptAssistantState.epoch;
  const apply = text => {
    if (typeof text !== "string" || !text.trim() || draft.revision !== revision || draft.result || epoch !== promptAssistantState.epoch) return;
    draft.result = text; draft.legacy = true;
    draft.status = "已将旧处理结果保留为预览，未写入正文；旧扩写与 OC 自动执行已停用。";
    promptAssistantPersist();
    if (profile === currentProfile()) promptAssistantRender();
  };
  if (typeof override === "string") apply(override);
  else void request(`/api/prompt-jobs/${encodeURIComponent(jobId)}`).then(job => {
    if (job.status === "completed") apply(job.result?.text);
  }).catch(() => { /* Unavailable old jobs never trigger another model request. */ });
  promptAssistantPersist();
}
function promptAssistantClear() {
  if (typeof promptReverseClear === "function") promptReverseClear();
  promptAssistantState.epoch++;
  Object.values(promptAssistantState.timers).forEach(clearTimeout);
  promptAssistantState.drafts = {}; promptAssistantState.timers = {};
  localStorage.removeItem(PROMPT_ASSISTANT_KEY);
  promptAssistantRender();
}
function initializePromptAssistant() {
  if (promptAssistantState.ready) return;
  try {
    const saved = JSON.parse(localStorage.getItem(PROMPT_ASSISTANT_KEY) || "null");
    if (saved?.version === 1) for (const profile of ["anima", "krea2"]) {
      const value = saved.drafts?.[profile];
      if (!value || typeof value !== "object") continue;
      const draft = promptAssistantDraft(profile);
      for (const key of ["source", "service", "model", "template", "result", "resultFor", "jobId", "jobFingerprint"]) if (typeof value[key] === "string") draft[key] = value[key];
      draft.revision = Number.isSafeInteger(value.revision) ? value.revision : 0;
      draft.jobRevision = Number.isSafeInteger(value.jobRevision) ? value.jobRevision : draft.revision;
      draft.cancelRequested = Boolean(value.cancelRequested);
      draft.busy = Boolean(draft.jobId);
      draft.legacy = Boolean(value.legacy);
      draft.poolItem = promptPoolReadMetadata(value.poolItem, draft.result);
      draft.bodyPoolItem = promptPoolReadMetadata(value.bodyPoolItem, value.bodyPoolItem?.prompt);
      draft.migrated = Array.isArray(value.migrated) ? value.migrated.filter(item => typeof item === "string") : [];
      if (draft.jobId) draft.status = "正在恢复上次重写任务…";
      else if (value.busy) draft.status = "页面在创建任务时刷新，未收到任务编号。请先检查当前队列；正文保持原样。";
    }
  } catch { /* Keep the original local data until the next explicit edit. */ }
  promptAssistantState.ready = true;
  if (typeof initializePromptReverse === "function") initializePromptReverse();
  for (const [id, key] of [["rewriteSource", "source"], ["rewriteResult", "result"], ["rewriteService", "service"], ["rewriteModel", "model"], ["rewriteTemplate", "template"]]) {
    document.getElementById(id).addEventListener(id === "rewriteSource" || id === "rewriteResult" ? "input" : "change", event => promptAssistantEdit(key, event.target.value));
  }
  for (const [id, callback] of [["rewriteRead", promptAssistantReadCurrent], ["rewriteStart", promptAssistantStart], ["rewriteCancel", promptAssistantCancel], ["rewriteRefresh", promptAssistantLoadOptions], ["rewriteAppend", () => promptAssistantAdopt("append")], ["rewriteReplace", () => promptAssistantAdopt("replace")], ["rewriteUndo", promptAssistantUndo]]) document.getElementById(id).addEventListener("click", callback);
  document.getElementById("single_positive_prompt").addEventListener("input", () => {
    const draft = promptAssistantDraft();
    draft.bodyPoolItem = null; promptAssistantPersist();
    if (draft.undo && draft.undo.after !== document.getElementById("single_positive_prompt").value) { draft.undo = null; promptAssistantRender(); }
  });
  promptAssistantRender();
  void promptAssistantLoadOptions();
  for (const profile of ["anima", "krea2"]) if (promptAssistantDraft(profile).jobId) void promptAssistantResume(profile);
}
