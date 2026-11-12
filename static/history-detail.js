/* History filtering and the ordinary image detail/delete surface. */
const historyDetailState = {
  ready: false, busy: false, record: null, owner: null, session: 0, fileOutcome: null,
  homeQuery: { source: "", folderId: "" },
};
const historyDetailElement = id => document.getElementById(id);
const HISTORY_SOURCES = new Set(["", "single", "random"]);
function historyDetailFocus(element) {
  if (!element || element.isConnected === false || element.disabled || element.hidden || element.inert || element.closest?.("[hidden], [inert]")) return false;
  if (element.getClientRects && !element.getClientRects().length) return false;
  element.focus({ preventScroll: true }); return true;
}
function renderHistorySource({ immediate = false } = {}) {
  const group = historyDetailElement("historyWorkbenchFilter");
  const buttons = [...(group?.querySelectorAll?.("[data-history-source]") || [])];
  for (const button of buttons) {
    const checked = button.dataset.historySource === historyDetailState.homeQuery.source;
    button.setAttribute("aria-checked", String(checked)); button.tabIndex = checked ? 0 : -1;
  }
  const indicator = group?.querySelector?.(".history-source-thumb"), selected = buttons.find(button => button.dataset.historySource === historyDetailState.homeQuery.source);
  if (indicator && selected && typeof animateStudioIndicator === "function") animateStudioIndicator(indicator, { x: selected.offsetLeft - indicator.offsetLeft, width: selected.offsetWidth }, { immediate });
}
function historyQueryParams(view = historyViewState()) {
  return (view.query.source ? `&workbench=${encodeURIComponent(view.query.source)}` : "")
    + (view.query.folderId ? `&folder_id=${encodeURIComponent(view.query.folderId)}` : "");
}
function historyQueryKey(view = historyViewState()) { return `${view.kind}:${view.queryEpoch}:${historyQueryParams(view)}`; }
function setHistorySource(source) {
  if (!HISTORY_SOURCES.has(source) || source === historyDetailState.homeQuery.source) return;
  historyDetailState.homeQuery = { ...historyDetailState.homeQuery, source };
  invalidateHistoryView(); renderHistorySource(); void loadHistory(1);
}
function historyDetailWritable() { return config?.storage_status?.history?.available !== false && !config?.storage_status?.history?.read_only; }
function invalidateHistoryView(view = historyViewState()) {
  ++view.queryEpoch; view.cache.clear(); ++view.requestId; view.dirty = true;
  if (typeof imagePreview !== "undefined" && (!imagePreview.context || (imagePreview.context.kind !== "single" && previewHistoryView() === view))) {
    const context = imagePreview.context, navigating = imagePreview.navigationCursor && ["switching", "opening"].includes(imagePreview.phase);
    const requestId = ++imagePreview.navigationRequest;
    imagePreview.navigationCursor = null;
    if (typeof clearPreviewCache === "function") clearPreviewCache();
    // A completed batch can change page boundaries while a neighbor is decoding.
    // Restore its last committed bitmap; an obsolete cursor must neither repaint
    // a cached page nor leave a cancelled fade invisible or stuck in switching.
    if (navigating && typeof restorePreviewCommittedImage === "function") {
      void Promise.resolve(restorePreviewCommittedImage(requestId, context)).then(() => {
        if (requestId !== imagePreview.navigationRequest || context !== imagePreview.context || ["closing", "closed"].includes(imagePreview.phase)) return;
        imagePreview.phase = "open";
        ui.imagePreviewDialog.classList.remove("is-navigating");
        updatePreviewNavigation();
      });
    }
  }
}
function showHistoryDetail() { resetHistoryDetail(); }
function historyDetailVisible() {
  return Boolean(historyDetailElement("detailDialog")?.open) && !historyDetailElement("historyDeleteDialog")?.open;
}
function closeHistoryDelete({ restoreFocus = true, immediate = false, force = false } = {}) {
  const state = historyDetailState, dialog = historyDetailElement("historyDeleteDialog");
  if (!force && (state.busy || !state.record)) return false;
  const record = state.record, owner = state.owner, session = ++state.session;
  state.record = state.owner = state.fileOutcome = null;
  renderHistoryDelete();
  if (!dialog?.open) return false;
  const onComplete = () => {
    if (!restoreFocus || session !== state.session || dialog.open || !historyDetailElement("detailDialog")?.open
      || detailContext !== owner || String(selectedRecord?.id) !== String(record?.id)) return;
    historyDetailFocus(document.querySelector('[data-detail-action="more"]')) || historyDetailFocus(historyDetailElement("historyDetailTitle"));
  };
  motionDialog(dialog, false, { immediate, restoreFocus: false, onComplete });
  if (typeof StudioMotion === "undefined") onComplete();
  return true;
}
function resetHistoryDetail() { closeHistoryDelete({ immediate: true, restoreFocus: false, force: true }); }
function revealHistoryWorkbench() {
  const dialog = historyDetailElement("detailDialog");
  if (dialog?.open) motionDialog(dialog, false, { immediate: true, restoreFocus: false });
  resetHistoryDetail();
}
function openHistoryDelete(record) {
  if (!record || historyDetailState.busy || !historyDetailWritable()) return;
  const state = historyDetailState;
  ++state.session; state.record = record; state.owner = detailContext; state.fileOutcome = null;
  if (typeof invalidateHistoryTransfer === "function") invalidateHistoryTransfer();
  if (typeof closeArtworkPopovers === "function") closeArtworkPopovers({ restoreFocus: false, immediate: true });
  if (typeof closeDetailActionPanels === "function") closeDetailActionPanels({ immediate: true });
  if (typeof closeRegenerationDrawers === "function") closeRegenerationDrawers(false, true);
  const results = historyDetailElement("historyDeleteResults");
  results.textContent = ""; results.classList.remove("is-error");
  renderHistoryDelete();
  motionDialog(historyDetailElement("historyDeleteDialog"), true, { restoreFocus: false });
  historyDetailFocus(historyDetailElement("cancelHistoryDelete"));
}
function renderHistoryDelete() {
  const state = historyDetailState;
  // Keep the filename visible while a closing animation finishes.
  if (state.record) historyDetailElement("historyDeleteSummary").textContent = state.record.filename || `作品 #${state.record.id}`;
  for (const id of ["historyDeleteRecord", "historyDeleteFile"]) {
    historyDetailElement(id).disabled = !state.record || state.busy || !historyDetailWritable();
  }
  for (const id of ["closeHistoryDelete", "cancelHistoryDelete"]) historyDetailElement(id).disabled = state.busy;
  historyDetailElement("historyDeleteDialog").setAttribute("aria-busy", String(state.busy));
}
function containHistoryDeleteFocus(event) {
  const dialog = historyDetailElement("historyDeleteDialog");
  if (event.defaultPrevented || event.key !== "Tab" || !dialog.open) return;
  const buttons = [...dialog.querySelectorAll("button")].filter(button => !button.disabled && button.getClientRects().length);
  const index = buttons.indexOf(document.activeElement);
  if (!buttons.length) { event.preventDefault(); historyDetailFocus(historyDetailElement("historyDeleteResults")); }
  else if (index < 0 || (event.shiftKey ? index === 0 : index === buttons.length - 1)) {
    event.preventDefault(); historyDetailFocus(event.shiftKey ? buttons.at(-1) : buttons[0]);
  }
}
function removeHistoryViewRecord(view, record) {
  const key = String(record.id), cached = [...view.cache.values()], elements = view.elements;
  const matches = (!view.query.source || (record.workbench || record.settings?.workbench || "random") === view.query.source)
    && (!view.query.folderId || (record.folder_ids || []).includes(view.query.folderId));
  const present = view.items.some(item => String(item.id) === key);
  const total = Number(view.cache.get(view.page)?.total ?? view.total);
  view.items = view.items.filter(item => String(item.id) !== key);
  for (const page of cached) page.items = (page.items || []).filter(item => String(item.id) !== key);
  for (const node of [...(elements?.gallery?.children || [])]) if (node.dataset.recordId === key) {
    if (typeof ArtworkThumbnails !== "undefined") ArtworkThumbnails.remove(node);
    node.remove();
  }
  if (Number.isFinite(total) && (present || matches)) {
    view.total = Math.max(0, total - 1); view.pages = Math.max(1, Math.ceil(view.total / 24)); view.page = Math.min(view.page, view.pages);
    if (elements?.historyCount) elements.historyCount.textContent = `${view.total} 张图片`;
    if (elements?.pageLabel) elements.pageLabel.textContent = `${view.page} / ${view.pages}`;
    if (elements?.prevPage) elements.prevPage.disabled = view.page <= 1;
    if (elements?.nextPage) elements.nextPage.disabled = view.page >= view.pages;
  }
  if (elements?.emptyState) elements.emptyState.hidden = view.items.length > 0;
  invalidateHistoryView(view);
}
async function applyHistoryDeleted(record, owner, { beforeDetailUpdate } = {}) {
  const id = String(record.id), view = historyViewState(), warnings = [];
  removeHistoryViewRecord(view, record);
  if (typeof artworkRecordRemoved === "function") artworkRecordRemoved(record.id);
  const ownsDetail = () => detailContext === owner && String(selectedRecord?.id) === id;
  if (ownsDetail()) closeImagePreview({ immediate: true, restoreFocus: false });
  let neighbor = null;
  try { if (typeof singleGalleryDeleted === "function") neighbor = await singleGalleryDeleted(record.id); }
  catch (error) { warnings.push(`单图列表刷新失败：${error.message}`); }
  // Keep the choice modal blocking stale detail actions while the next image loads.
  beforeDetailUpdate?.();
  if (ownsDetail()) {
    if (owner?.origin === "single" && neighbor && ui.detailDialog.open) {
      updateDetailRecord(neighbor); historyDetailFocus(ui.detailImage);
    } else { selectedRecord = null; motionDialog(ui.detailDialog, false); }
  }
  historyDirty = true;
  // A failed refresh cannot restore the committed deletion from old caches.
  if (!(typeof singleWorkbenchActive === "function" && singleWorkbenchActive())) {
    try {
      const data = await loadHistory(view.page, view);
      if (!data) warnings.push("作品列表刷新失败，删除已完成；可稍后刷新");
    } catch (error) { warnings.push(`作品列表刷新失败：${error.message}；删除已完成`); }
  }
  return warnings;
}
function historyDeleteSuccessMessage(mode, result = {}) {
  if (result.code === "already_missing") return "作品记录已不存在，列表已更新";
  if (result.file_deleted) return "记录及原图已删除";
  if (result.file_missing) return "记录已删除，原图原已不存在";
  if (mode === "record") return "记录已删除，原图保留";
  return "记录已删除";
}
function historyDeleteFailureMessage(error) {
  const result = error.payload?.results?.[0];
  let message = String(result?.error || error.message || "删除未完成").replace(/[。；\s]+$/, "");
  if (result?.file_deleted && !result.record_deleted && !/原(?:图|文件)已删除/.test(message)) {
    message += "。原图已删除，但记录仍保留；可重试完成记录删除";
  }
  if (!message.includes("重试")) message += "。可重试";
  return `${message}。`;
}
async function confirmHistoryDelete(mode) {
  const state = historyDetailState, record = state.record, owner = state.owner, session = state.session;
  if (!["record", "file"].includes(mode) || !record || state.busy || !historyDetailWritable()) return;
  if (detailContext !== owner || String(selectedRecord?.id) !== String(record.id)) {
    closeHistoryDelete({ immediate: true, restoreFocus: false }); return;
  }
  const current = () => state.session === session && state.record === record && state.owner === owner;
  const results = historyDetailElement("historyDeleteResults"), button = historyDetailElement(mode === "file" ? "historyDeleteFile" : "historyDeleteRecord");
  results.textContent = "正在删除…"; results.classList.remove("is-error");
  state.busy = true; button.setAttribute("aria-busy", "true"); renderHistoryDelete();
  historyDetailFocus(results);
  try {
    let response;
    try { response = await request(`/api/history/${encodeURIComponent(record.id)}?mode=${mode}`, { method: "DELETE" }); }
    catch (error) {
      if (error.status === 404) response = { deleted: true, results: [{ ...error.payload?.results?.[0], code: "already_missing" }] };
      else throw error;
    }
    if (!response.deleted) {
      const error = new Error("删除未完成"); error.payload = response; throw error;
    }
    const result = { ...response.results?.[0] };
    // A record-only retry must not claim to preserve a file removed on the prior attempt.
    if (state.fileOutcome === "deleted" && current()) result.file_deleted = true;
    else if (state.fileOutcome === "missing" && current()) result.file_missing = true;
    const warnings = result.warning ? [result.warning] : [];
    const closeCommitted = () => { if (current()) closeHistoryDelete({ immediate: true, restoreFocus: false, force: true }); };
    if (current()) results.textContent = "删除已完成，正在更新作品…";
    try { warnings.push(...await applyHistoryDeleted(record, owner, { beforeDetailUpdate: closeCommitted })); }
    catch (error) { warnings.push(`删除已完成，但界面刷新失败：${error.message}`); }
    finally { closeCommitted(); }
    toast([historyDeleteSuccessMessage(mode, result), ...new Set(warnings)].join("。"));
  } catch (error) {
    const message = historyDeleteFailureMessage(error);
    if (current()) {
      const failure = error.payload?.results?.[0];
      if (failure?.file_deleted) state.fileOutcome = "deleted";
      else if (failure?.file_missing && state.fileOutcome !== "deleted") state.fileOutcome = "missing";
      results.textContent = message; results.classList.add("is-error");
    }
    else toast(message);
  } finally {
    state.busy = false; button.removeAttribute("aria-busy"); renderHistoryDelete();
    if (current()) historyDetailFocus(button);
  }
}
function initializeHistoryDetail() {
  if (historyDetailState.ready) return;
  historyDetailState.ready = true;
  const dialog = historyDetailElement("historyDeleteDialog");
  for (const button of dialog.querySelectorAll("[data-history-delete-mode]")) {
    button.addEventListener("click", () => { void confirmHistoryDelete(button.dataset.historyDeleteMode); });
  }
  for (const id of ["closeHistoryDelete", "cancelHistoryDelete"]) historyDetailElement(id).addEventListener("click", () => closeHistoryDelete());
  dialog.addEventListener("cancel", event => { event.preventDefault(); closeHistoryDelete(); });
  dialog.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      // Repeated native CloseWatcher cancel events can become non-cancelable.
      // Own the key before its default action so a pending deletion stays modal.
      event.preventDefault(); event.stopPropagation(); closeHistoryDelete();
    } else containHistoryDeleteFocus(event);
  });
  let outsidePointer = false;
  const outside = event => {
    if (event.target !== dialog) return false;
    const rect = dialog.getBoundingClientRect();
    return event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom;
  };
  dialog.addEventListener("pointerdown", event => { outsidePointer = outside(event); });
  dialog.addEventListener("click", event => {
    if (event.target === dialog) {
      // Consume generic dismissal, even during a pending delete or a drag from inside.
      event.preventDefault();
      if (outsidePointer && outside(event)) closeHistoryDelete();
    }
    outsidePointer = false;
  });
  dialog.addEventListener("close", () => {
    if (dialog.open || !historyDetailState.record) return;
    ++historyDetailState.session;
    historyDetailState.record = historyDetailState.owner = historyDetailState.fileOutcome = null;
    renderHistoryDelete();
  });
  const group = historyDetailElement("historyWorkbenchFilter"), sources = [...group.querySelectorAll("[data-history-source]")];
  for (const button of sources) button.addEventListener("click", () => setHistorySource(button.dataset.historySource));
  group.addEventListener("keydown", event => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const index = sources.findIndex(button => button.dataset.historySource === historyDetailState.homeQuery.source);
    const next = event.key === "Home" ? 0 : event.key === "End" ? sources.length - 1 : (index + (event.key === "ArrowRight" ? 1 : -1) + sources.length) % sources.length;
    setHistorySource(sources[next].dataset.historySource); historyDetailFocus(sources[next]);
  });
  if (typeof ResizeObserver !== "undefined") new ResizeObserver(() => renderHistorySource({ immediate: true })).observe(group);
  renderHistorySource({ immediate: true });
}
