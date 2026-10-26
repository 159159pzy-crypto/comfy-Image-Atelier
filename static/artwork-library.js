/* Browsing, details and confirmations share one artwork surface. */
const artworkLibraryState = {
  ready: false, folders: [], selected: new Map(), busy: false, folderRequest: 0, membershipRequest: 0,
  mount: null, previous: null, opener: null, view: "detail", panelReturn: "library",
  homeQuery: { source: "", folder: "" }, selecting: false,
  get query() { return this.mount?.query || this.homeQuery; },
  set query(value) { if (this.mount) this.mount.query = value; else this.homeQuery = value; },
  editing: null, folderEditing: false, pendingFolderDelete: null,
  deleteRecords: [], membershipRecords: [], libraryScroll: 0, hostScroll: 0,
  viewRevision: 0, viewFocus: {}, viewFocusFallback: {}, viewOffsets: new Map(), folderNodes: new Map(),
};
const artworkElement = id => document.getElementById(id);
const ARTWORK_VIEWS = { library: "artworkLibraryView", detail: "artworkDetailView", membership: "artworkMembershipPanel", delete: "artworkDeletePanel", cleanup: "artworkCleanupPanel" };
const ARTWORK_SOURCE_LABELS = { "": "全部工作台", single: "单图工作台", random: "随机工作台" };
function artworkFocus(element) {
  if (!element || element.isConnected === false || element.disabled || element.hidden || element.inert || element.closest?.("[hidden], [inert]")) return false;
  if (element.getClientRects && !element.getClientRects().length) return false;
  element.focus({ preventScroll: true }); return true;
}
function artworkPresence(element, visible, { immediate = false, x = 0, y = 6, onComplete } = {}) {
  if (!element) return;
  if (typeof StudioMotion === "undefined") {
    element.hidden = !visible; element.inert = !visible; onComplete?.(); return;
  }
  return StudioMotion.presence(element, visible, { from: { opacity: 0, x, y }, response: .3, damping: 1, immediate, onComplete });
}
function artworkRenderSource({ immediate = false } = {}) {
  const state = artworkLibraryState, group = artworkElement("historyWorkbenchFilter");
  const buttons = [...(group?.querySelectorAll?.("[data-artwork-source]") || [])];
  for (const button of buttons) {
    const checked = button.dataset.artworkSource === state.homeQuery.source;
    button.setAttribute("aria-checked", String(checked)); button.tabIndex = checked ? 0 : -1; button.disabled = state.busy;
  }
  const summary = artworkElement("artworkSourceSummary");
  if (summary) summary.textContent = `范围：${ARTWORK_SOURCE_LABELS[state.query.source] || ARTWORK_SOURCE_LABELS[""]}`;
  const indicator = group?.querySelector?.(".artwork-source-thumb"), selected = buttons.find(button => button.dataset.artworkSource === state.homeQuery.source);
  if (indicator && selected && typeof animateStudioIndicator === "function") {
    animateStudioIndicator(indicator, { x: selected.offsetLeft - indicator.offsetLeft, width: selected.offsetWidth }, { immediate });
  }
}
function artworkActiveHistoryView() { return artworkLibraryState.mount || historyViewState(); }
function artworkHistoryViews() { return [historyViewState(), artworkLibraryState.mount].filter(Boolean); }
function artworkHistoryQuery(view = artworkActiveHistoryView()) {
  const filter = view.query.folder;
  return filter === "__all__" ? "&favorites_only=true" : filter ? `&folder_id=${encodeURIComponent(filter)}` : "";
}
function artworkHistoryParams(view = artworkActiveHistoryView()) {
  const source = view.query.source;
  return `${source ? `&workbench=${encodeURIComponent(source)}` : ""}${artworkHistoryQuery(view)}`;
}
function artworkHistoryKey(view = artworkActiveHistoryView()) { return `${view.kind}:${view.queryEpoch}:${artworkHistoryParams(view)}`; }
function artworkInvalidateHistory(view = artworkActiveHistoryView()) {
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
function artworkSelectionToggle(record, selected) {
  const key = String(record.id);
  if (selected && !artworkLibraryState.selected.has(key) && artworkLibraryState.selected.size >= 200) return false;
  if (selected) artworkLibraryState.selected.set(key, record); else artworkLibraryState.selected.delete(key);
  return true;
}
function artworkFilterChanged() {
  artworkLibraryState.selected.clear(); artworkInvalidateHistory(); artworkRenderSelection();
}
function artworkSetFilter(patch) {
  const state = artworkLibraryState;
  if (state.busy) return;
  const next = state.mount
    ? { source: state.query.source, folder: typeof patch.folder === "string" ? patch.folder : state.query.folder }
    : { source: Object.hasOwn(ARTWORK_SOURCE_LABELS, patch.source) ? patch.source : state.query.source, folder: "" };
  if (next.source === state.query.source && next.folder === state.query.folder) return;
  state.query = next;
  artworkFilterChanged(); artworkRenderFolders(); void loadHistory(1, artworkActiveHistoryView());
}
function artworkWritable() { return config?.storage_status?.history?.available !== false && !config?.storage_status?.history?.read_only; }
function artworkSelecting() { return Boolean(artworkLibraryState.mount && artworkLibraryState.selecting); }
function artworkRenderFolderControls() {
  const busy = artworkLibraryState.busy, blocked = busy || !artworkWritable();
  for (const id of ["artworkFolderEdit", "artworkFolderSave", "artworkFolderNew", "artworkFolderCancel", "artworkFolderName", "artworkFolderDeleteSubmit", "artworkFolderDeleteCancel"]) {
    const element = artworkElement(id); if (element) element.disabled = blocked;
  }
  for (const button of artworkElement("artworkFolderList")?.querySelectorAll?.("button") || []) button.disabled = button.classList.contains("artwork-folder-link") ? busy : blocked;
}
function artworkRenderSelection() {
  if (!artworkLibraryState.ready) return;
  artworkRenderFolderControls();
  const state = artworkLibraryState, count = state.selected.size, busy = state.busy || !artworkWritable(), selecting = artworkSelecting();
  const view = artworkActiveHistoryView(), items = view.items, elements = view.elements;
  const toolbar = artworkElement("artworkBatchToolbar");
  if (!selecting && toolbar?.contains?.(document.activeElement)) artworkFocus(artworkElement("artworkSelectionToggle"));
  if (!selecting && document.activeElement?.closest?.(".artwork-check")) artworkFocus(artworkElement("artworkSelectionToggle"));
  if (selecting && !count && artworkElement("artworkBatchActions")?.contains?.(document.activeElement)) artworkFocus(artworkElement("artworkSelectPage")) || artworkFocus(artworkElement("artworkSelectionToggle"));
  artworkPresence(toolbar, selecting, { immediate: !artworkElement("detailDialog")?.open });
  artworkPresence(artworkElement("artworkBatchActions"), selecting && !!count, { immediate: !artworkElement("detailDialog")?.open });
  artworkElement("artworkSelectionToggle").textContent = selecting ? "完成" : "选择";
  artworkElement("artworkSelectionToggle").setAttribute("aria-pressed", String(selecting));
  artworkElement("artworkSelectionToggle").disabled = busy;
  artworkElement("artworkSelectionCount").textContent = `已选 ${count} / 200 张`;
  for (const id of ["artworkBatchFavorite", "artworkBatchDelete", "artworkSelectionClear"]) artworkElement(id).disabled = !count || busy;
  const remove = artworkElement("artworkBatchRemove");
  remove.hidden = !state.query.folder; remove.disabled = !count || busy || !state.query.folder;
  remove.textContent = state.query.folder === "__all__" ? "取消收藏" : "移出此夹";
  artworkElement("artworkSelectPage").disabled = busy || !items.length;
  artworkElement("artworkSelectPage").checked = Boolean(items.length) && items.every(record => state.selected.has(String(record.id)));
  artworkElement("artworkSelectPage").indeterminate = !artworkElement("artworkSelectPage").checked && items.some(record => state.selected.has(String(record.id)));
  artworkRenderSource();
  artworkElement("artworkFolderFilter").disabled = state.busy;
  for (const list of artworkHistoryViews()) for (const article of list.elements.gallery.children) artworkDecorateCard(article);
  if (!items.length && state.query.folder && artworkWritable()) {
    elements.emptyState.querySelector("strong").textContent = "这个收藏夹还没有作品";
    elements.emptyState.querySelector("small").textContent = "点击作品右下角的星号收藏，或选择作品后加入收藏夹。";
  }
}
function artworkCardActivate(record, historyView = null) {
  const view = historyViewState(historyView);
  if (!artworkSelecting() || view !== artworkLibraryState.mount) { artworkLibraryState.detailReturnId = record.id; openDetail(record, { historyView: view }); return; }
  if (artworkLibraryState.busy || !artworkWritable()) return;
  if (!artworkSelectionToggle(record, !artworkLibraryState.selected.has(String(record.id)))) toast("每批最多选择 200 张作品");
  artworkRenderSelection();
}
function artworkDecorateCard(article) {
  if (!artworkLibraryState.ready || !article.record) return;
  let controls = article.querySelector(".artwork-card-tools");
  if (!controls) {
    controls = document.createElement("div"); controls.className = "artwork-card-tools";
    controls.innerHTML = '<button class="artwork-star" type="button"></button><label class="artwork-check" hidden><input type="checkbox"><span class="sr-only">选择作品</span></label>';
    controls.querySelector("input").addEventListener("change", event => {
      if (artworkLibraryState.busy || !artworkWritable()) { artworkRenderSelection(); return; }
      if (!artworkSelectionToggle(article.record, event.target.checked)) toast("每批最多选择 200 张作品");
      artworkRenderSelection();
    });
    controls.querySelector(".artwork-star").addEventListener("click", () => void artworkToggleFavorite(article.record));
    article.append(controls);
  }
  const record = article.record, favorite = Boolean(record.folder_ids?.length), selecting = artworkSelecting() && article.historyView === artworkLibraryState.mount;
  const checkbox = controls.querySelector("input"), star = controls.querySelector(".artwork-star");
  checkbox.checked = artworkLibraryState.selected.has(String(record.id));
  checkbox.setAttribute("aria-label", `选择 ${record.filename || "作品"}`);
  controls.querySelector(".artwork-check").hidden = !selecting; star.hidden = selecting;
  star.textContent = favorite ? "★" : "☆";
  star.setAttribute("aria-label", favorite ? "取消作品收藏" : "收藏作品");
  star.setAttribute("aria-pressed", String(favorite)); star.title = favorite ? "取消收藏" : "收藏到我的收藏";
  for (const control of controls.querySelectorAll("input,button")) control.disabled = artworkLibraryState.busy || !artworkWritable();
  article.classList.toggle("artwork-selected", selecting && checkbox.checked);
  article.classList.toggle("artwork-selectable", selecting);
  const open = article.querySelector("button[data-record-id]");
  if (open) {
    open.setAttribute("aria-label", `${selecting ? "选择" : "查看"} ${record.filename || "作品"}`);
    if (selecting) open.setAttribute("aria-pressed", String(checkbox.checked)); else open.removeAttribute("aria-pressed");
  }
}
function artworkRenderDetail(record) {
  if (!artworkLibraryState.ready) return;
  const favorite = Boolean(record?.folder_ids?.length), button = artworkElement("detailArtworkFavorite");
  button.textContent = favorite ? "★" : "☆"; button.setAttribute("aria-pressed", String(favorite));
  button.setAttribute("aria-label", favorite ? "取消作品收藏" : "收藏作品"); button.title = favorite ? "取消收藏" : "收藏到我的收藏";
  button.disabled = !record || artworkLibraryState.busy || !artworkWritable();
}
function artworkRenderFolders() {
  const state = artworkLibraryState, select = artworkElement("artworkFolderFilter");
  const signature = JSON.stringify(state.folders.map(folder => [folder.id, folder.name, folder.count]));
  if (select.artworkOptions !== signature) {
    select.replaceChildren(new Option("全部作品", ""), new Option("全部收藏", "__all__"), ...state.folders.map(folder => new Option(`${folder.name} (${folder.count || 0})`, folder.id)));
    select.artworkOptions = signature;
  }
  select.value = state.query.folder; artworkRenderSource();
  const list = artworkElement("artworkFolderList"), retained = new Set();
  const folders = [{ id: "", name: "全部作品" }, { id: "__all__", name: "全部收藏" }, ...state.folders];
  folders.forEach((folder, index) => {
    let row = state.folderNodes.get(folder.id);
    if (!row) {
      row = document.createElement("div"); row.className = "artwork-folder-row";
      const link = document.createElement("button"); link.type = "button"; link.className = "artwork-folder-link";
      const name = document.createElement("span"), count = document.createElement("small"); link.append(name, count);
      link.addEventListener("click", () => artworkSetFilter({ folder: row.artworkFolder.id }));
      const actions = document.createElement("span"); actions.className = "artwork-folder-actions";
      const edit = document.createElement("button"); edit.type = "button"; edit.className = "artwork-text-button"; edit.textContent = "改名";
      edit.addEventListener("click", () => { state.editing = row.artworkFolder.id; artworkElement("artworkFolderName").value = row.artworkFolder.name; artworkElement("artworkFolderSave").textContent = "保存"; artworkFocus(artworkElement("artworkFolderName")); });
      const remove = document.createElement("button"); remove.type = "button"; remove.className = "artwork-text-button"; remove.textContent = "删除";
      remove.addEventListener("click", () => artworkAskDeleteFolder(row.artworkFolder));
      actions.append(edit, remove); row.append(link, actions);
      row.artworkParts = { link, name, count, actions, edit, remove }; state.folderNodes.set(folder.id, row);
    }
    row.artworkFolder = folder;
    const { link, name, count, actions, edit, remove } = row.artworkParts;
    name.textContent = folder.name; count.textContent = typeof folder.count === "number" ? String(folder.count) : ""; count.hidden = typeof folder.count !== "number";
    link.setAttribute("aria-current", folder.id === state.query.folder ? "page" : "false"); link.disabled = state.busy;
    const hideActions = !state.folderEditing || !folder.id || ["default", "__all__"].includes(folder.id);
    if (hideActions && actions.contains?.(document.activeElement)) artworkFocus(artworkElement("artworkFolderEdit"));
    actions.hidden = hideActions;
    edit.setAttribute("aria-label", `重命名${folder.name}`); remove.setAttribute("aria-label", `删除收藏夹${folder.name}`);
    edit.disabled = remove.disabled = state.busy || !artworkWritable();
    retained.add(folder.id);
    if (list.children[index] !== row) list.insertBefore(row, list.children[index] || null);
  });
  for (const [id, row] of state.folderNodes) if (!retained.has(id)) {
    if (row.contains?.(document.activeElement)) artworkFocus(artworkElement("artworkFolderEdit"));
    row.remove(); state.folderNodes.delete(id);
  }
  const form = artworkElement("artworkFolderForm");
  if (!state.folderEditing && form?.contains?.(document.activeElement)) artworkFocus(artworkElement("artworkFolderEdit"));
  artworkPresence(form, state.folderEditing, { immediate: !artworkElement("detailDialog")?.open });
  artworkElement("artworkFolderEdit").textContent = state.folderEditing ? "完成" : "编辑";
  artworkElement("artworkFolderEdit").setAttribute("aria-expanded", String(state.folderEditing));
  artworkElement("artworkFolderNew").textContent = state.editing ? "新建收藏夹" : "清空";
  artworkRenderFolderControls();
  artworkRenderSelection();
}
async function artworkLoadFolders() {
  const token = ++artworkLibraryState.folderRequest, data = await request("/api/artwork-folders");
  if (token !== artworkLibraryState.folderRequest) return;
  artworkLibraryState.folders = data.folders || []; artworkRenderFolders();
}
function artworkApplyMemberships(items) {
  const updates = new Map((items || []).map(item => [String(item.id), item.folder_ids || []]));
  const update = record => { if (record && updates.has(String(record.id))) record.folder_ids = [...updates.get(String(record.id))]; };
  for (const view of artworkHistoryViews()) {
    view.items.forEach(update);
    for (const page of view.cache.values()) (page.items || []).forEach(update);
  }
  for (const record of artworkLibraryState.selected.values()) update(record);
  if (typeof singleGalleryState !== "undefined") { singleGalleryState.items.forEach(update); update(singleGalleryState.lastCompleted); update(singleGalleryState.viewerRecord); }
  update(selectedRecord); if (selectedRecord) artworkRenderDetail(selectedRecord);
  for (const view of artworkHistoryViews()) {
    const kept = view.items.filter(record => artworkRecordMatches(record, view.query));
    if (kept.length !== view.items.length) artworkRemoveViewRecords(view, new Set(view.items.filter(record => !kept.includes(record)).map(record => String(record.id))));
    for (const article of view.elements.gallery?.children || []) artworkDecorateCard(article);
  }
}
function artworkRefreshHistories() {
  for (const view of artworkHistoryViews()) {
    artworkInvalidateHistory(view);
    void Promise.resolve(loadHistory(view.page, view)).then(data => {
      if (!view.closed && data && !view.items.length && view.page > 1) void loadHistory(Math.max(1, Math.min(view.page - 1, view.pages)), view);
    }).catch(error => { if (!view.closed) toast(`作品列表刷新失败：${error.message}`); });
  }
}
async function artworkMutateMembership(records, folderIds, action) {
  const result = await request("/api/artwork-folders/members", { method: "POST", body: JSON.stringify({ image_ids: records.map(record => record.id), folder_ids: folderIds, action }) });
  artworkApplyMemberships(result.items);
  if (result.folders) { ++artworkLibraryState.folderRequest; artworkLibraryState.folders = result.folders; artworkRenderFolders(); }
  artworkRefreshHistories();
  return result;
}
async function artworkToggleFavorite(record) {
  if (artworkLibraryState.busy || !artworkWritable()) return;
  artworkLibraryState.busy = true; artworkRenderSelection(); artworkRenderDetail(selectedRecord);
  let saved = false;
  try { await artworkMutateMembership([record], record.folder_ids?.length ? [] : ["default"], record.folder_ids?.length ? "clear" : "add"); saved = true; }
  catch (error) { toast(error.message); }
  finally { artworkLibraryState.busy = false; artworkRenderSelection(); artworkRenderDetail(selectedRecord); artworkRenderFolders(); }
  if (saved && typeof StudioMotion !== "undefined") {
    const stars = artworkHistoryViews().map(view => view.elements.gallery?.querySelector(`[data-record-id="${record.id}"] .artwork-star`));
    const buttons = [...stars, selectedRecord?.id === record.id ? artworkElement("detailArtworkFavorite") : null];
    for (const button of buttons) if (button && !button.hidden && button.isConnected) {
      StudioMotion.spring(button, "favorite", { scale: 1 }, { initial: { scale: .88 }, response: .24, damping: 1,
        onUpdate: value => { button.style.scale = String(value.scale); }, onComplete: () => button.style.removeProperty("scale") });
    }
  }
}
function artworkSetView(view, { immediate = false, returning = false, focus = true, focusTarget = null } = {}) {
  const state = artworkLibraryState, dialog = artworkElement("detailDialog");
  if (!Object.hasOwn(ARTWORK_VIEWS, view)) return;
  const previous = state.view, changed = previous !== view, revision = ++state.viewRevision;
  immediate ||= !dialog?.open;
  if (changed) {
    state.viewFocus[previous] = document.activeElement;
    const actionPanel = document.activeElement?.closest?.("[data-detail-panel]");
    state.viewFocusFallback[previous] = actionPanel ? document.querySelector?.(`[data-detail-action="${actionPanel.dataset.detailPanel}"]`) : null;
  }
  if (previous === "detail" && changed) {
    if (typeof invalidateHistoryTransfer === "function") invalidateHistoryTransfer();
    if (typeof closeDetailActionPanels === "function") closeDetailActionPanels({ immediate: true });
    if (typeof closeRegenerationDrawers === "function") closeRegenerationDrawers(false, true);
  }
  if (previous === "library" && changed) { state.libraryScroll = artworkElement("artworkLibraryBody")?.scrollTop || 0; state.hostScroll = dialog?.scrollTop || 0; }
  state.view = view;
  if (dialog) {
    dialog.classList.add("artwork-host"); dialog.classList.toggle("is-artwork-library", view === "library"); dialog.classList.toggle("is-artwork-panel", ["membership", "delete", "cleanup"].includes(view));
    dialog.classList.toggle("is-artwork-session", Boolean(state.mount));
    dialog.dataset.artworkView = view;
    const title = ({ library: "整理作品", detail: "作品详情", membership: "加入收藏夹", delete: "删除作品", cleanup: "图片清理" })[view];
    dialog.setAttribute("aria-label", title);
    if (artworkElement("artworkViewTitle")) artworkElement("artworkViewTitle").textContent = title;
    if (view !== "detail") dialog.classList.remove("is-single-detail", "is-regenerating");
    if (changed) dialog.scrollTop = view === "library" ? state.hostScroll : 0;
  }
  const back = artworkElement("artworkBack");
  if (back) { back.hidden = view === "library" || (view === "detail" && !state.mount); back.textContent = view === "detail" ? "返回作品" : "返回"; }
  for (const [name, id] of Object.entries(ARTWORK_VIEWS)) {
    const node = artworkElement(id); if (!node) continue;
    const visible = view === name, running = typeof StudioMotion !== "undefined" && StudioMotion.snapshot(node, "presence")?.active;
    // A reversal keeps the existing spatial path as well as its value/velocity.
    // Changing the path while a spring is active would itself create a jump.
    if (!running) state.viewOffsets.set(name, (returning ? -1 : 1) * (visible ? 12 : -12));
    node.classList.toggle("artwork-view-exiting", !visible && !node.hidden && !immediate);
    artworkPresence(node, visible, { immediate: immediate || (!changed && !running), x: state.viewOffsets.get(name), y: 0,
      onComplete() { if (revision === state.viewRevision) node.classList.remove("artwork-view-exiting"); },
    });
  }
  if (view === "library" && artworkElement("artworkLibraryBody")) artworkElement("artworkLibraryBody").scrollTop = state.libraryScroll;
  if (changed && focus && dialog?.open) {
    const target = focusTarget || (returning ? state.viewFocus[view] : null);
    if (!artworkFocus(target) && !(returning && artworkFocus(state.viewFocusFallback[view]))) artworkFocus(artworkElement("artworkViewTitle")) || artworkFocus(back);
  }
}
function artworkShowDetail() { artworkSetView("detail"); }
function artworkDetailVisible() { return artworkLibraryState.view === "detail" && Boolean(artworkElement("detailDialog")?.open); }
function artworkReturnToLibrary() {
  if (typeof releaseDetailForLibrary === "function") releaseDetailForLibrary();
  const card = artworkActiveHistoryView().elements.gallery.querySelector(`button[data-record-id="${artworkLibraryState.detailReturnId || ""}"]`);
  const previous = artworkLibraryState.view;
  artworkSetView("library", { returning: true, focusTarget: (previous === "detail" ? card : null) || artworkLibraryState.viewFocus.library || artworkElement("artworkSelectionToggle") }); artworkRenderSelection();
}
function artworkBack() {
  const state = artworkLibraryState;
  if (state.busy) return true;
  if (["membership", "delete", "cleanup"].includes(state.view)) {
    if (state.panelReturn === "detail" && selectedRecord) { if (typeof renderDetail === "function") renderDetail(selectedRecord); artworkSetView("detail", { returning: true }); }
    else if (state.mount) artworkReturnToLibrary(); else return false;
    return true;
  }
  if (state.view === "detail" && state.mount) { artworkReturnToLibrary(); return true; }
  if (state.view === "library" && state.pendingFolderDelete) { artworkCancelDeleteFolder(); return true; }
  if (state.view === "library" && state.folderEditing) { state.folderEditing = false; artworkRenderFolders(); return true; }
  if (state.view === "library" && state.selecting) { state.selecting = false; state.selected.clear(); artworkRenderSelection(); return true; }
  return false;
}
function artworkOpenCleanup() {
  const state = artworkLibraryState;
  if (state.busy || !state.mount || state.view !== "library") return;
  state.panelReturn = "library"; artworkSetView("cleanup");
  if (typeof imageCleanupOpen === "function") void imageCleanupOpen();
}
async function artworkOpenMembership(records) {
  if (!records.length || artworkLibraryState.busy || !artworkWritable()) return;
  const state = artworkLibraryState, origin = state.view, epoch = artworkHistoryKey(), revision = state.viewRevision, requestId = ++state.membershipRequest;
  const current = () => requestId === state.membershipRequest && revision === state.viewRevision && epoch === artworkHistoryKey()
    && state.view === origin && !state.busy && Boolean(artworkElement("detailDialog")?.open);
  try { await artworkLoadFolders(); } catch (error) { if (current()) toast(error.message); return; }
  if (!current()) return;
  state.membershipRecords = records;
  artworkElement("artworkMembershipHeading").textContent = `加入收藏夹 · ${records.length} 张`;
  artworkElement("artworkMembershipHint").textContent = "选择收藏夹，作品原有的其他分类会保留。";
  artworkElement("artworkMembershipError").textContent = "";
  const container = artworkElement("artworkMembershipChoices"); container.replaceChildren();
  for (const folder of state.folders) {
    const label = document.createElement("label"); label.className = "favorite-group-check";
    const checkbox = document.createElement("input"); checkbox.type = "checkbox"; checkbox.value = folder.id;
    label.append(checkbox, document.createTextNode(folder.name)); container.append(label);
  }
  state.panelReturn = origin; artworkSetView("membership", { focusTarget: container.querySelector("input") });
}
async function artworkSaveMembership(event) {
  event.preventDefault(); if (artworkLibraryState.busy || !artworkWritable()) return;
  const records = artworkLibraryState.membershipRecords, folderIds = [...artworkElement("artworkMembershipChoices").querySelectorAll("input:checked")].map(input => input.value);
  if (!folderIds.length) { artworkElement("artworkMembershipError").textContent = "请至少选择一个收藏夹"; return; }
  artworkLibraryState.busy = true; artworkElement("artworkMembershipSave").disabled = true;
  let saved = false;
  try { await artworkMutateMembership(records, folderIds, "add"); saved = true; toast("作品收藏已更新"); }
  catch (error) { artworkElement("artworkMembershipError").textContent = error.message; }
  finally { artworkLibraryState.busy = false; artworkElement("artworkMembershipSave").disabled = !artworkWritable(); artworkRenderSelection(); artworkRenderDetail(selectedRecord); }
  if (saved) artworkBack();
}
async function artworkRemoveSelection() {
  const filter = artworkLibraryState.query.folder;
  if (!filter || artworkLibraryState.busy || !artworkWritable() || !artworkLibraryState.selected.size) return;
  artworkLibraryState.busy = true;
  try { await artworkMutateMembership([...artworkLibraryState.selected.values()], filter === "__all__" ? [] : [filter], filter === "__all__" ? "clear" : "remove"); artworkLibraryState.selected.clear(); }
  catch (error) { toast(error.message); } finally { artworkLibraryState.busy = false; artworkRenderFolders(); }
}
async function artworkSaveFolder(event) {
  event.preventDefault(); const state = artworkLibraryState, name = artworkElement("artworkFolderName").value.trim(), id = state.editing;
  if (!name || state.busy || !artworkWritable()) return;
  state.busy = true; artworkRenderFolders();
  try {
    const result = await request(`/api/artwork-folders${id ? `/${encodeURIComponent(id)}` : ""}`, { method: id ? "PATCH" : "POST", body: JSON.stringify({ name }) });
    ++state.folderRequest; state.folders = result.folders; artworkNewFolder();
  } catch (error) { artworkElement("artworkFolderError").textContent = error.message; }
  finally { state.busy = false; artworkRenderFolders(); }
}
function artworkNewFolder() {
  artworkLibraryState.editing = null; artworkElement("artworkFolderName").value = "";
  artworkElement("artworkFolderSave").textContent = "新建"; artworkElement("artworkFolderError").textContent = "";
}
function artworkAskDeleteFolder(folder) {
  if (artworkLibraryState.busy || !artworkWritable()) return;
  artworkLibraryState.pendingFolderDelete = folder;
  artworkLibraryState.folderDeleteFocus = document.activeElement;
  artworkElement("artworkFolderDeleteSummary").textContent = `删除收藏夹“${folder.name}”？`;
  artworkPresence(artworkElement("artworkFolderDeleteConfirm"), true); artworkFocus(artworkElement("artworkFolderDeleteCancel"));
}
function artworkCancelDeleteFolder() {
  const state = artworkLibraryState, panel = artworkElement("artworkFolderDeleteConfirm");
  state.pendingFolderDelete = null;
  if (panel?.contains?.(document.activeElement)) artworkFocus(state.folderDeleteFocus) || artworkFocus(artworkElement("artworkFolderEdit"));
  artworkPresence(panel, false, { immediate: !artworkElement("detailDialog")?.open });
}
async function artworkDeleteFolder() {
  const state = artworkLibraryState, folder = state.pendingFolderDelete;
  if (!folder || state.busy || !artworkWritable()) return;
  state.busy = true; artworkRenderFolders();
  try {
    const result = await request(`/api/artwork-folders/${encodeURIComponent(folder.id)}`, { method: "DELETE" });
    ++state.folderRequest; state.folders = result.folders;
    const cached = [...artworkHistoryViews().flatMap(view => [...view.items, ...[...view.cache.values()].flatMap(page => page.items || [])]), ...state.selected.values(), ...(typeof singleGalleryState !== "undefined" ? singleGalleryState.items : []), selectedRecord].filter(Boolean);
    artworkApplyMemberships(cached.filter(record => record.folder_ids?.includes(folder.id)).map(record => ({ id: record.id, folder_ids: record.folder_ids.filter(id => id !== folder.id).length ? record.folder_ids.filter(id => id !== folder.id) : ["default"] })));
    if (state.query.folder === folder.id) state.query.folder = "__all__";
    artworkFilterChanged(); artworkRefreshHistories(); artworkCancelDeleteFolder(); artworkNewFolder();
  } catch (error) { artworkElement("artworkFolderError").textContent = error.message; }
  finally { state.busy = false; artworkRenderFolders(); }
}
function artworkOpenDelete(records) {
  if (!records.length || artworkLibraryState.busy || !artworkWritable()) return;
  artworkLibraryState.deleteRecords = [...records]; artworkLibraryState.panelReturn = artworkLibraryState.view;
  artworkElement("artworkDeleteMode").value = "record"; artworkElement("artworkDeleteResults").textContent = "";
  artworkRenderDelete(); artworkSetView("delete", { focusTarget: artworkElement("artworkDeleteMode") });
  const revision = artworkLibraryState.viewRevision;
  void artworkLoadFolders().then(() => { if (revision === artworkLibraryState.viewRevision) artworkRenderDelete(); }).catch(error => {
    if (revision === artworkLibraryState.viewRevision) artworkElement("artworkDeleteResults").textContent = `收藏夹名称读取失败：${error.message}`;
  });
}
function artworkRenderDelete() {
  const records = artworkLibraryState.deleteRecords, folders = new Set(records.flatMap(record => record.folder_ids || []));
  const names = [...folders].map(id => artworkLibraryState.folders.find(folder => folder.id === id)?.name || id), permanent = artworkElement("artworkDeleteMode").value === "file";
  artworkElement("artworkDeleteSummary").textContent = `共 ${records.length} 张作品。${names.length ? `同时移出收藏夹：${names.join("、")}。` : ""}`;
  artworkElement("artworkDeleteWarning").textContent = permanent ? "原图将永久删除，不进入回收站。文件无法访问或被其他未选中的记录引用时，该项会保留并说明原因。" : "仅删除作品记录、收藏关系和缩略图；原图文件保留。";
  artworkElement("artworkDeleteSubmit").textContent = permanent ? "永久删除原图及记录" : "只删除记录";
  artworkElement("artworkDeleteSubmit").disabled = !records.length || artworkLibraryState.busy || !artworkWritable();
}
function artworkDeletionOutcome(results) {
  const completed = [], failed = [];
  for (const result of results || []) (result.record_deleted || result.code === "already_missing" ? completed : failed).push(result);
  return { completed, failed };
}
function artworkRecordMatches(record, query) {
  return (!query.source || (record.workbench || record.settings?.workbench || "random") === query.source)
    && (!query.folder || (query.folder === "__all__" ? record.folder_ids?.length : record.folder_ids?.includes(query.folder)));
}
function artworkRemoveViewRecords(view, keys, knownRecords = []) {
  // Apply the committed deletion before any refresh. An offline refresh must
  // never leave an already-deleted image visible or selected in another view.
  const cached = [...view.cache.values()], elements = view.elements;
  const known = new Map([...view.items, ...cached.flatMap(page => page.items || []), ...knownRecords].filter(Boolean).map(record => [String(record.id), record]));
  const removedCount = [...keys].filter(id => view.items.some(record => String(record.id) === id) || (known.has(id) && artworkRecordMatches(known.get(id), view.query))).length;
  const label = elements?.historyCount;
  const cachedTotal = view.cache.get(view.page)?.total;
  const total = cachedTotal === undefined ? Number(label?.textContent?.match(/^\d+/)?.[0]) : Number(cachedTotal);
  view.items = view.items.filter(record => !keys.has(String(record.id)));
  for (const page of cached) page.items = (page.items || []).filter(record => !keys.has(String(record.id)));
  if (elements) {
    for (const node of [...(elements.gallery?.children || [])]) if (keys.has(node.dataset.recordId)) {
      if (typeof card !== "undefined") card.visibilityObserver?.unobserve(node);
      node.remove();
    }
    if (Number.isFinite(total)) {
      const remaining = Math.max(0, total - removedCount);
      if (label) label.textContent = `${remaining} 张图片`;
      view.total = remaining; view.pages = Math.max(1, Math.ceil(remaining / 24));
      view.page = Math.min(view.page, view.pages);
      if (elements.pageLabel) elements.pageLabel.textContent = `${view.page} / ${view.pages}`;
      if (elements.prevPage) elements.prevPage.disabled = view.page <= 1;
      if (elements.nextPage) elements.nextPage.disabled = view.page >= view.pages;
    }
    if (elements.emptyState) elements.emptyState.hidden = view.items.length > 0;
  }
}
function artworkRemoveLocalHistory(keys) {
  const known = [...artworkLibraryState.selected.values(), selectedRecord, ...artworkHistoryViews().flatMap(view => [...view.items, ...[...view.cache.values()].flatMap(page => page.items || [])])].filter(Boolean);
  keys.forEach(id => artworkLibraryState.selected.delete(id));
  for (const view of artworkHistoryViews()) { artworkRemoveViewRecords(view, keys, known); artworkInvalidateHistory(view); }
}
async function artworkApplyDeleted(ids) {
  const keys = new Set(ids.map(String)); artworkRemoveLocalHistory(keys);
  const owned = selectedRecord && keys.has(String(selectedRecord.id)), owner = detailContext;
  if (owned) closeImagePreview({ immediate: true, restoreFocus: false });
  let neighbor = null; const errors = [];
  try { if (typeof singleGalleryDeletedMany === "function") neighbor = await singleGalleryDeletedMany(ids); }
  catch (error) {
    errors.push(error.message);
    if (typeof singleGalleryState !== "undefined") neighbor = singleGalleryState.items.find(record => !keys.has(String(record.id))) || null;
  }
  if (owned && owner === detailContext && keys.has(String(selectedRecord?.id))) {
    if (owner?.origin === "single" && neighbor && ui.detailDialog.open) {
      updateDetailRecord(neighbor);
      if (artworkLibraryState.view !== "detail") artworkSetView(artworkLibraryState.view);
    } else selectedRecord = null;
  }
  historyDirty = true;
  artworkRefreshHistories();
  artworkRenderSelection();
  if (errors.length) throw new Error(errors.join("；"));
}
async function artworkConfirmDelete(event) {
  event.preventDefault(); if (artworkLibraryState.busy || !artworkWritable()) return;
  const records = artworkLibraryState.deleteRecords, mode = artworkElement("artworkDeleteMode").value;
  artworkLibraryState.busy = true; artworkRenderDelete(); artworkElement("artworkDeleteMode").disabled = true;
  let finished = false;
  try {
    const response = await request("/api/history/delete", { method: "POST", body: JSON.stringify({ image_ids: records.map(record => record.id), mode }) });
    const outcome = artworkDeletionOutcome(response.results), successfulIds = outcome.completed.map(item => item.id ?? item.image_id), successful = new Set(successfulIds.map(String));
    artworkLibraryState.deleteRecords = records.filter(record => !successful.has(String(record.id)));
    const errors = outcome.failed.map(item => `#${item.id ?? item.image_id}：${item.error || item.code || "删除失败"}${item.file_deleted ? "（原文件已删除，记录未删除；可重试完成记录清理）" : ""}`);
    artworkElement("artworkDeleteResults").textContent = `已完成 ${successfulIds.length} 张，${artworkLibraryState.deleteRecords.length} 张未完成。${errors.length ? `\n${errors.join("\n")}` : ""}`;
    const warnings = [];
    try { if (successfulIds.length) await artworkApplyDeleted(successfulIds); } catch (error) { warnings.push(`作品列表刷新失败：${error.message}；删除结果已保留。`); }
    try { await artworkLoadFolders(); } catch (error) { warnings.push(`收藏夹数量刷新失败：${error.message}；删除结果已保留。`); }
    if (warnings.length) artworkElement("artworkDeleteResults").textContent += `\n${warnings.join("\n")}`;
    finished = !artworkLibraryState.deleteRecords.length;
    if (finished) toast(`已删除 ${successfulIds.length} 张作品${mode === "file" ? "及对应原文件" : "记录，原文件保留"}${warnings.length ? `。${warnings.join(" ")}` : ""}`);
  } catch (error) { artworkElement("artworkDeleteResults").textContent = `${error.message}。保留选择，可重试。`; }
  finally { artworkLibraryState.busy = false; artworkElement("artworkDeleteMode").disabled = false; artworkRenderDelete(); artworkRenderSelection(); artworkRenderDetail(selectedRecord); }
  if (finished && !artworkBack()) motionDialog(artworkElement("detailDialog"), false);
}
async function artworkOpenLibrary(event) {
  const state = artworkLibraryState, dialog = artworkElement("detailDialog");
  if (state.busy) return;
  if (state.mount) { motionDialog(dialog, true); return; }
  state.opener = event?.currentTarget || document.activeElement;
  if (typeof releaseDetailForLibrary === "function") releaseDetailForLibrary();
  const view = state.mount = {
    kind: "library", query: { ...state.homeQuery, folder: "" }, queryEpoch: 0,
    page: 1, pages: 1, total: 0, items: [], cache: new Map(), requestId: 0, dirty: false, closed: false,
    elements: { gallery: artworkElement("artworkGallery"), emptyState: artworkElement("artworkEmptyState"), historyCount: artworkElement("artworkHistoryCount"),
      pageLabel: artworkElement("artworkPageLabel"), prevPage: artworkElement("artworkPrevPage"), nextPage: artworkElement("artworkNextPage") },
  };
  state.selecting = false; state.folderEditing = false; state.libraryScroll = state.hostScroll = 0;
  state.viewFocus = {}; state.viewFocusFallback = {};
  artworkCancelDeleteFolder(); artworkNewFolder(); artworkFilterChanged(); artworkSetView("library", { immediate: true, focus: false });
  const cached = historyViewState().cache.get(1);
  renderHistoryPage(cached ? { ...cached } : { page: 1, pages: 1, total: 0, items: [] }, view);
  for (const node of artworkElement("artworkLibraryView").querySelectorAll("details[open]")) node.open = false;
  motionDialog(dialog, true); artworkElement("artworkSelectionToggle").focus({ preventScroll: true });
  const results = await Promise.allSettled([artworkLoadFolders(), loadHistory(1, view)]);
  if (state.mount === view && !view.closed) for (const result of results) if (result.status === "rejected") toast(result.reason.message);
}
function artworkCloseLibrary({ restoreFocus = true } = {}) {
  const state = artworkLibraryState, dialog = artworkElement("detailDialog");
  if (dialog?.open) return;
  const mounted = state.mount, opener = state.opener;
  state.previous = state.opener = null;
  if (mounted) {
    mounted.closed = true; artworkInvalidateHistory(mounted); state.mount = null;
    artworkRenderSource({ immediate: true });
  }
  state.selecting = false; state.selected.clear(); state.folderEditing = false;
  artworkSetView("detail", { immediate: true, focus: false });
  for (const id of ["artworkBatchToolbar", "artworkBatchActions", "artworkFolderForm", "artworkFolderDeleteConfirm"]) artworkPresence(artworkElement(id), false, { immediate: true });
  state.viewFocus = {}; state.viewFocusFallback = {};
  if (mounted && restoreFocus && opener?.isConnected && !opener.disabled) opener.focus({ preventScroll: true });
}
function artworkRevealWorkbench() {
  const dialog = artworkElement("detailDialog");
  if (dialog?.open) motionDialog(dialog, false, { immediate: true, restoreFocus: false });
  artworkCloseLibrary({ restoreFocus: false });
}
function initializeArtworkLibrary() {
  if (artworkLibraryState.ready) return;
  artworkLibraryState.ready = true;
  for (const button of document.querySelectorAll("[data-artwork-open]")) button.addEventListener("click", artworkOpenLibrary);
  artworkElement("artworkPrevPage").addEventListener("click", () => { const view = artworkLibraryState.mount; if (view) void loadHistory(view.page - 1, view); });
  artworkElement("artworkNextPage").addEventListener("click", () => { const view = artworkLibraryState.mount; if (view) void loadHistory(view.page + 1, view); });
  artworkElement("artworkBack").addEventListener("click", () => { if (!artworkBack()) motionDialog(artworkElement("detailDialog"), false); });
  artworkElement("artworkFolderFilter").addEventListener("change", event => artworkSetFilter({ folder: event.target.value }));
  const sourceGroup = artworkElement("historyWorkbenchFilter"), sources = [...sourceGroup.querySelectorAll("[data-artwork-source]")];
  for (const button of sources) button.addEventListener("click", () => artworkSetFilter({ source: button.dataset.artworkSource }));
  sourceGroup.addEventListener("keydown", event => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key) || artworkLibraryState.busy || artworkLibraryState.mount) return;
    event.preventDefault();
    const index = sources.findIndex(button => button.dataset.artworkSource === artworkLibraryState.query.source);
    const next = event.key === "Home" ? 0 : event.key === "End" ? sources.length - 1 : (index + (event.key === "ArrowRight" ? 1 : -1) + sources.length) % sources.length;
    artworkSetFilter({ source: sources[next].dataset.artworkSource }); artworkFocus(sources[next]);
  });
  if (typeof ResizeObserver !== "undefined") new ResizeObserver(() => artworkRenderSource({ immediate: true })).observe(sourceGroup);
  artworkElement("artworkCleanupOpen").addEventListener("click", artworkOpenCleanup);
  artworkElement("artworkFolderEdit").addEventListener("click", () => { artworkLibraryState.folderEditing = !artworkLibraryState.folderEditing; artworkCancelDeleteFolder(); artworkNewFolder(); artworkRenderFolders(); });
  artworkElement("artworkFolderForm").addEventListener("submit", artworkSaveFolder);
  artworkElement("artworkFolderNew").addEventListener("click", () => { artworkNewFolder(); artworkElement("artworkFolderName").focus(); });
  artworkElement("artworkFolderCancel").addEventListener("click", () => { artworkLibraryState.folderEditing = false; artworkCancelDeleteFolder(); artworkRenderFolders(); });
  artworkElement("artworkFolderDeleteSubmit").addEventListener("click", artworkDeleteFolder);
  artworkElement("artworkFolderDeleteCancel").addEventListener("click", artworkCancelDeleteFolder);
  artworkElement("artworkMembershipForm").addEventListener("submit", artworkSaveMembership);
  artworkElement("artworkSelectionToggle").addEventListener("click", () => { artworkLibraryState.selecting = !artworkLibraryState.selecting; artworkLibraryState.selected.clear(); artworkRenderSelection(); });
  artworkElement("artworkSelectPage").addEventListener("change", event => { if (artworkLibraryState.busy || !artworkWritable()) return; const select = event.target.checked; for (const record of artworkActiveHistoryView().items) if (!artworkSelectionToggle(record, select)) { toast("每批最多选择 200 张作品"); break; } artworkRenderSelection(); });
  artworkElement("artworkSelectionClear").addEventListener("click", () => { artworkLibraryState.selected.clear(); artworkRenderSelection(); });
  artworkElement("artworkBatchFavorite").addEventListener("click", () => void artworkOpenMembership([...artworkLibraryState.selected.values()]));
  artworkElement("artworkBatchRemove").addEventListener("click", artworkRemoveSelection);
  artworkElement("artworkBatchDelete").addEventListener("click", () => artworkOpenDelete([...artworkLibraryState.selected.values()]));
  artworkElement("detailArtworkFavorite").addEventListener("click", () => { if (selectedRecord) void artworkToggleFavorite(selectedRecord); });
  artworkElement("artworkDeleteForm").addEventListener("submit", artworkConfirmDelete);
  artworkElement("artworkDeleteMode").addEventListener("change", artworkRenderDelete);
  for (const button of document.querySelectorAll("[data-artwork-back]")) button.addEventListener("click", () => { if (!artworkBack()) motionDialog(artworkElement("detailDialog"), false); });
  void artworkLoadFolders().catch(error => toast(`作品收藏夹读取失败：${error.message}`)); artworkRenderSelection(); artworkRenderSource({ immediate: true });
}
