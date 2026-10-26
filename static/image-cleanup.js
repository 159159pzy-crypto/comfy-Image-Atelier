/* A tab leases all saved reference images, including inactive model drafts. */
const imageCleanupState = { ready: false, viewReady: false, session: null, pending: Promise.resolve(), timer: null, busy: false, loading: false, status: null, leaseError: "" };
const imageCleanupElement = id => document.getElementById(id);
function imageCleanupSetText(id, text) { const element = imageCleanupElement(id); if (element) element.textContent = text; }
function imageCleanupControls() {
  const disabled = imageCleanupState.busy || imageCleanupState.loading || !imageCleanupState.status || Boolean(imageCleanupState.status.read_only);
  for (const id of ["imageCleanupEnabled", "imageCleanupDays", "imageCleanupSave", "imageCleanupRun"]) {
    const control = imageCleanupElement(id); if (control) control.disabled = disabled;
  }
}
function imageCleanupReadStored(key) { try { return JSON.parse(localStorage.getItem(key) || "null"); } catch { return null; } }
function imageCleanupCollectReferences() {
  const inputs = new Set(), prompts = new Set(), valid = id => typeof id === "string" && /^[a-f0-9]{64}$/.test(id);
  const workbench = typeof singleWorkbenchState !== "undefined" && singleWorkbenchState.ready ? singleWorkbenchState : imageCleanupReadStored("anima-studio:workbenches:v1");
  for (const profiles of Object.values(workbench?.drafts || {})) for (const draft of Object.values(profiles || {})) {
    const id = draft?.settings?.single?.input_image_id; if (valid(id)) inputs.add(id);
  }
  if (workbench?.mode !== "random" && valid(workbench?.single?.input_image_id)) inputs.add(workbench.single.input_image_id);
  const reverse = typeof promptReverseState !== "undefined" && promptReverseState.ready ? promptReverseState : imageCleanupReadStored("anima-studio:prompt-reverse:v2") || imageCleanupReadStored("anima-studio:prompt-reverse:v1");
  for (const draft of Object.values(reverse?.drafts || {})) {
    if (valid(draft?.imageId)) prompts.add(draft.imageId);
    if (draft?.busy && valid(draft.jobImageId)) prompts.add(draft.jobImageId);
  }
  return { input_image_ids: [...inputs].sort(), prompt_image_ids: [...prompts].sort() };
}
function imageCleanupSyncLeases() {
  clearTimeout(imageCleanupState.timer);
  imageCleanupState.session ||= globalThis.crypto?.randomUUID?.() || `tab-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  // Serialize complete replacement requests; a delayed old response must never
  // replace a newer tab reference set on the server.
  const send = async () => {
    try {
      const result = await request("/api/image-cleanup/leases", { method: "POST", body: JSON.stringify({ session_id: imageCleanupState.session, ...imageCleanupCollectReferences() }) });
      imageCleanupState.leaseError = "";
      return result;
    } catch (error) {
      // Background protection must not expose housekeeping UI on the homepage.
      imageCleanupState.leaseError = `草稿图片保护同步失败：${error.message}。连接恢复后会重试。`;
      return null;
    }
  };
  imageCleanupState.pending = imageCleanupState.pending.then(send, send);
  return imageCleanupState.pending;
}
function imageCleanupScheduleLeases() { clearTimeout(imageCleanupState.timer); imageCleanupState.timer = setTimeout(imageCleanupSyncLeases, 150); }
function imageCleanupFormatBytes(value) {
  const bytes = Number(value) || 0;
  return bytes >= 1024 * 1024 * 1024 ? `${(bytes / (1024 ** 3)).toFixed(2)} GiB` : `${(bytes / (1024 ** 2)).toFixed(1)} MiB`;
}
function imageCleanupRenderResult(result) {
  if (!result) return "尚未执行清理。";
  const finished = typeof result.finished_at === "number" ? new Date(result.finished_at * 1000).toLocaleString() : result.finished_at || "刚刚";
  return `最近清理：${finished}\n删除 ${result.deleted_files || 0} 个副本文件，释放 ${imageCleanupFormatBytes(result.freed_bytes)}。`;
}
function imageCleanupRenderDetails(result) {
  if (!result) return "";
  return [result.skipped_files ? `跳过 ${result.skipped_files} 个副本文件。` : "", result.comfy_status || "", ...(result.errors || [])].filter(Boolean).join("\n");
}
function imageCleanupSetResult(result) {
  imageCleanupSetText("imageCleanupResult", imageCleanupRenderResult(result));
  const text = imageCleanupRenderDetails(result), details = imageCleanupElement("imageCleanupDetails");
  imageCleanupSetText("imageCleanupDetailsText", text);
  if (details) { details.hidden = !text; details.open = false; }
}
function imageCleanupRender(status, { settings = true } = {}) {
  imageCleanupState.status = status;
  if (settings) {
    const enabled = imageCleanupElement("imageCleanupEnabled"), days = imageCleanupElement("imageCleanupDays");
    if (enabled) enabled.checked = status.enabled;
    if (days) days.value = status.retention_days;
  }
  const counts = status.counts || {}, bytes = status.bytes || {};
  const total = (Number(counts.input) || 0) + (Number(counts.prompt) || 0) + (Number(counts.comfy_input) || 0);
  imageCleanupSetText("imageCleanupStats", `已登记 ${total} 个副本，占用 ${imageCleanupFormatBytes(bytes.total)}。`);
  imageCleanupSetResult(status.last_run);
  imageCleanupSetText("imageCleanupError", status.read_only ? status.reason || "历史存储不可写，清理已暂停。" : imageCleanupState.leaseError);
  imageCleanupControls();
}
async function imageCleanupOpen() {
  if (imageCleanupState.loading || imageCleanupState.busy) return;
  imageCleanupState.loading = true;
  imageCleanupSetText("imageCleanupError", "");
  imageCleanupControls();
  try { imageCleanupRender(await request("/api/image-cleanup")); }
  catch (error) { imageCleanupSetText("imageCleanupError", error.message); }
  finally { imageCleanupState.loading = false; imageCleanupControls(); }
}
async function imageCleanupSave(event) {
  event.preventDefault(); if (imageCleanupState.busy || imageCleanupState.loading || imageCleanupState.status?.read_only) return;
  const enabled = imageCleanupElement("imageCleanupEnabled").checked, retention_days = Number(imageCleanupElement("imageCleanupDays").value);
  if (!Number.isInteger(retention_days) || retention_days < 1 || retention_days > 365) { imageCleanupSetText("imageCleanupError", "保留天数必须为 1–365 的整数"); return; }
  imageCleanupState.busy = true;
  imageCleanupControls();
  try { imageCleanupRender(await request("/api/image-cleanup", { method: "PUT", body: JSON.stringify({ enabled, retention_days }) })); imageCleanupSetText("imageCleanupResult", `设置已保存。\n${imageCleanupRenderResult(imageCleanupState.status.last_run)}`); }
  catch (error) { imageCleanupSetText("imageCleanupError", error.message); }
  finally { imageCleanupState.busy = false; imageCleanupControls(); }
}
async function imageCleanupRun() {
  if (imageCleanupState.busy || imageCleanupState.loading || imageCleanupState.status?.read_only) return;
  imageCleanupState.busy = true;
  imageCleanupControls();
  imageCleanupSetText("imageCleanupError", "");
  imageCleanupSetText("imageCleanupResult", "正在清理到期的闲置副本…");
  try {
    if (!await imageCleanupSyncLeases()) throw new Error("无法确认当前草稿图片的保护状态，未执行清理；请恢复连接后重试");
    const result = await request("/api/image-cleanup/run", { method: "POST", body: "{}" });
    imageCleanupSetResult(result);
    try { imageCleanupRender(await request("/api/image-cleanup")); }
    catch (error) { imageCleanupSetText("imageCleanupError", `清理已完成，占用统计刷新失败：${error.message}`); }
    imageCleanupSetResult(result);
  } catch (error) { imageCleanupSetResult(imageCleanupState.status?.last_run); imageCleanupSetText("imageCleanupError", error.message); }
  finally { imageCleanupState.busy = false; imageCleanupControls(); }
}
function initializeImageCleanupProtection() {
  if (imageCleanupState.ready) return;
  imageCleanupState.ready = true;
  setInterval(imageCleanupSyncLeases, 60000);
  for (const event of ["focus", "pageshow"]) window.addEventListener(event, imageCleanupSyncLeases);
  window.addEventListener("storage", event => { if (event.key?.startsWith("anima-studio:")) imageCleanupScheduleLeases(); });
  document.addEventListener("visibilitychange", () => { if (!document.hidden) void imageCleanupSyncLeases(); });
  void imageCleanupSyncLeases();
}
function initializeImageCleanupView() {
  if (imageCleanupState.viewReady) return;
  const section = imageCleanupElement("imageCleanupSection"), form = imageCleanupElement("imageCleanupForm"), run = imageCleanupElement("imageCleanupRun");
  if (!section || !form || !run) return;
  imageCleanupState.viewReady = true;
  // The artwork shell loads status when its cleanup page is entered. Keeping
  // that separate from background leases avoids reads while browsing images.
  form.addEventListener("submit", imageCleanupSave);
  run.addEventListener("click", imageCleanupRun);
  imageCleanupControls();
}
function initializeImageCleanup() {
  initializeImageCleanupProtection();
  initializeImageCleanupView();
}
