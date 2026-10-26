/* Krea's structured pool shares the Anima browser's layout and selection rules. */
const kreaPoolView = {
  collection: "", customGroup: "", groups: [], favorites: { groups: [], items: [] },
  collapsed: new Set(), editor: null, favoriteIds: [], busy: false, itemSession: 0,
  selectionRevision: 0, selectionOperation: null, selectionSummary: null, resultContext: null,
};
function kreaElement(tag, className = "", text = null) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== null) element.textContent = text;
  return element;
}
function kreaButton(text, action, className = "button ghost compact") {
  const button = kreaElement("button", className, text); button.type = "button";
  button.addEventListener("click", action); return button;
}
function kreaFavoriteUrl(suffix = "") { return `/api/favorites/prompt${suffix}?model_profile=krea2`; }
function kreaEpoch() { return typeof sharedFavoritesState === "undefined" ? 0 : sharedFavoritesState.epoch; }
function kreaCurrent(epoch) { return currentProfile() === "krea2" && epoch === kreaEpoch(); }
function kreaSelectionSignature() { return JSON.stringify(kreaPromptPoolSelection); }
function kreaSelectionContext() {
  return { epoch: kreaEpoch(), revision: kreaPoolView.selectionRevision,
    globalRevision: typeof poolContextRevision === "undefined" ? 0 : poolContextRevision,
    query: JSON.stringify(kreaQuery()) };
}
function kreaDraftContextCurrent(context) {
  return kreaCurrent(context.epoch) &&
    context.globalRevision === (typeof poolContextRevision === "undefined" ? 0 : poolContextRevision);
}
function kreaSelectionContextCurrent(context) {
  return kreaDraftContextCurrent(context) && context.revision === kreaPoolView.selectionRevision &&
    context.query === JSON.stringify(kreaQuery());
}
function kreaSelectedCount() {
  const summary = kreaPoolView.selectionSummary;
  return summary?.signature === kreaSelectionSignature() ? summary.total : new Set(kreaPromptPoolSelection).size;
}
function kreaPoolResultsCurrent() {
  return Boolean(kreaPoolView.resultContext && kreaSelectionContextCurrent(kreaPoolView.resultContext));
}
function updateKreaSelectionButtons() {
  if (!ui.kreaPoolSelectAll) return;
  const resultsCurrent = kreaPoolResultsCurrent();
  ui.kreaPoolSelectAll.disabled = kreaPoolView.busy || (resultsCurrent && !kreaPoolQueryCount);
  ui.kreaPoolSelectPage.disabled = kreaPoolView.busy || !resultsCurrent || !kreaPromptPool.items?.length;
  ui.kreaPromptPoolList?.querySelectorAll('input[type="checkbox"]').forEach(input => { input.disabled = !resultsCurrent; });
}
function invalidateKreaPoolSelection() {
  ++kreaPoolView.selectionRevision; ++kreaPromptPoolRequest;
  kreaPoolView.selectionOperation = null; kreaPoolView.busy = false; kreaPoolView.selectionSummary = null;
  updateKreaSelectionButtons();
}
function beginKreaSelection() {
  if (currentProfile() !== "krea2") return null;
  if (kreaPoolView.selectionOperation && kreaSelectionContextCurrent(kreaPoolView.selectionOperation)) return null;
  const operation = kreaSelectionContext();
  kreaPoolView.selectionOperation = operation; kreaPoolView.busy = true;
  updateKreaSelectionButtons();
  return operation;
}
function finishKreaSelection(operation) {
  if (kreaPoolView.selectionOperation !== operation) return;
  kreaPoolView.selectionOperation = null; kreaPoolView.busy = false;
  updateKreaSelectionButtons();
}
function resetKreaPoolContext() {
  invalidateKreaPoolSelection();
  ++kreaPromptPoolRequest; ++kreaImportRequest; ++kreaPoolView.itemSession;
  kreaPoolView.editor = null; editingKreaPromptItem = null; kreaImportPreview = null;
  for (const id of ["kreaPromptItemDialog", "kreaPromptImportDialog", "kreaGroupDialog", "kreaFavoriteSelectionDialog"]) {
    if (ui[id]?.open) motionDialog(ui[id], false);
  }
}
function kreaQuery({ page = 1, limit = 48, q = ui.kreaPoolSearch.value.trim(), selectedOnly = ui.kreaPoolSelectedOnly.checked, all = false } = {}) {
  return { model_profile: "krea2", page, limit, q: all ? "" : q,
    selected_ids: [...kreaPromptPoolSelection], selected_only: all ? false : selectedOnly,
    collection: all ? "" : kreaPoolView.collection, custom_group: all ? "" : kreaPoolView.customGroup,
    sort: all ? "" : ui.kreaPoolSort.value };
}
async function queryKreaPool(body) {
  return request("/api/prompt-pools/query", { method: "POST", body: JSON.stringify(body) });
}
async function fetchAllKreaPromptPoolItems(query = ui.kreaPoolSearch.value.trim(), options = {}, current = () => true) {
  const body = kreaQuery({ ...options, q: query, limit: 500 });
  const first = await queryKreaPool(body), items = [...(first.items || [])];
  if (!current()) return null;
  for (let start = 2; start <= first.pages; start += 6) {
    const pages = Array.from({ length: Math.min(6, first.pages - start + 1) }, (_, index) => start + index);
    const results = await Promise.all(pages.map(page => queryKreaPool({ ...body, page })));
    if (!current()) return null;
    results.forEach(result => items.push(...(result.items || [])));
  }
  return { items, count: first.count, poolCount: first.pool_count };
}
function openKreaPromptPool() {
  if (currentProfile() !== "krea2") return;
  ui.kreaPoolDrawer.removeAttribute("inert"); ui.kreaPoolDrawer.classList.add("open");
  ui.kreaPoolDrawer.setAttribute("aria-hidden", "false");
  ui.kreaPoolBackdrop.classList.remove("closing");
  animatePoolSurface(ui.kreaPoolDrawer, ui.kreaPoolBackdrop, true);
  setKreaSidebarOpen(false); ui.kreaPoolSearch.focus({ preventScroll: true });
  void loadKreaPromptPool();
}
function closeKreaPromptPool() {
  invalidateKreaPoolSelection();
  setKreaSidebarOpen(false); ui.kreaPoolDrawer.classList.remove("open");
  ui.kreaPoolDrawer.setAttribute("inert", "");
  ui.openKreaPromptPool.focus({ preventScroll: true });
  ui.kreaPoolDrawer.setAttribute("aria-hidden", "true");
  animatePoolSurface(ui.kreaPoolDrawer, ui.kreaPoolBackdrop, false);
}
function setKreaSidebarOpen(open) {
  ui.kreaPoolSidebar.classList.toggle("mobile-open", open);
  ui.kreaPoolSidebar.inert = matchMedia("(max-width: 620px)").matches && !open;
  ui.toggleKreaPoolSidebar.setAttribute("aria-expanded", String(open));
}
async function loadKreaPromptPool({ page = kreaPoolPage } = {}) {
  if (currentProfile() !== "krea2") { renderKreaPromptPool(); return; }
  const requestId = ++kreaPromptPoolRequest, context = kreaSelectionContext();
  kreaPoolView.resultContext = null; updateKreaSelectionButtons();
  try {
    const [payload, groups, favorites] = await Promise.all([
      queryKreaPool(kreaQuery({ page })),
      request("/api/prompt-pools/groups?model_profile=krea2"), request(kreaFavoriteUrl()),
    ]);
    if (requestId !== kreaPromptPoolRequest || !kreaSelectionContextCurrent(context)) return;
    kreaPoolView.groups = groups.groups || []; kreaPoolView.favorites = favorites;
    if ((kreaPoolView.collection && kreaPoolView.collection !== "__all__" && !favorites.groups.some(g => g.id === kreaPoolView.collection)) ||
        (kreaPoolView.customGroup && !kreaPoolView.groups.some(g => g.id === kreaPoolView.customGroup))) {
      kreaPoolView.collection = ""; kreaPoolView.customGroup = "";
      return loadKreaPromptPool({ page: 1 });
    }
    kreaPoolPages = Number(payload.pages || 0); kreaPoolPage = Math.max(1, Number(payload.page || page));
    if (kreaPoolPage > Math.max(1, kreaPoolPages)) return loadKreaPromptPool({ page: Math.max(1, kreaPoolPages) });
    kreaPoolQueryCount = Number(payload.count || 0); kreaPoolTotalCount = Number(payload.pool_count || 0);
    kreaPromptPool = { ...payload, count: kreaPoolTotalCount };
    kreaPoolView.resultContext = context;
    kreaPoolView.selectionSummary = Number.isFinite(payload.selection_total)
      ? { signature: kreaSelectionSignature(), total: Math.max(0, payload.selection_total) } : null;
    renderKreaPromptPool(); renderKreaPoolSidebar();
  } catch (error) {
    if (requestId === kreaPromptPoolRequest && kreaSelectionContextCurrent(context)) toast(`Krea 2 提示词池读取失败：${error.message}`);
  }
}
function renderKreaPromptPool() {
  const enabled = currentProfile() === "krea2", selected = new Set(kreaPromptPoolSelection), selectedCount = kreaSelectedCount();
  ui.kreaPromptPoolControls.hidden = !enabled; ui.animaPromptPoolControls.hidden = enabled;
  ui.krea_prompt_pool_enabled.disabled = !enabled || !Number(kreaPromptPool.count || 0) || !selectedCount;
  ui.kreaPromptPoolCount.textContent = `${kreaPromptPool.count || 0} 项 · 已选 ${selectedCount}`;
  ui.kreaPoolMeta.textContent = `${kreaPoolQueryCount} 项匹配 · ${selectedCount} 项已选`;
  ui.kreaPoolSelectionLabel.textContent = `已选择 ${selectedCount} 项`;
  ui.kreaPoolPageLabel.textContent = `${kreaPoolPage} / ${Math.max(1, kreaPoolPages)}`;
  ui.kreaPoolPrev.disabled = kreaPoolPage <= 1; ui.kreaPoolNext.disabled = kreaPoolPage >= kreaPoolPages;
  ui.kreaPoolDeleteSelection.disabled = !selectedCount;
  ui.kreaPoolFavoriteSelection.disabled = !selectedCount;
  updateKreaSelectionButtons();
  ui.kreaPromptPoolList.replaceChildren(...(kreaPromptPool.items || []).map(item => {
    const row = kreaElement("article", `krea-prompt-row${selected.has(item.id) ? " selected" : ""}${item.favorite ? " favorite" : ""}`);
    const checkbox = kreaElement("input"); checkbox.type = "checkbox"; checkbox.checked = selected.has(item.id);
    checkbox.disabled = !kreaPoolResultsCurrent();
    checkbox.setAttribute("aria-label", `选择 ${item.title || "自然语言提示词"}`);
    checkbox.addEventListener("change", async () => {
      if (!kreaPoolResultsCurrent()) return;
      if (checkbox.checked) await selectKreaItems([item]);
      else { invalidateKreaPoolSelection(); kreaPromptPoolSelection = kreaPromptPoolSelection.filter(id => id !== item.id); schedulePersist(); await loadKreaPromptPool(); }
    });
    const copy = kreaElement("div", "krea-prompt-copy"); copy.title = item.text;
    copy.append(kreaElement("strong", "", item.title || item.text), kreaElement("small", "", item.subtitle || ""), kreaElement("span", "krea-prompt-body", item.text));
    if (item.in_pool === false) copy.append(kreaElement("em", "", "仅收藏 · 选用时加入词池"));
    copy.addEventListener("click", () => checkbox.click());
    const controls = kreaElement("div", "krea-prompt-tools");
    const favorite = kreaButton(item.favorite ? "♥" : "♡", async () => {
      if (item.favorite && item.favorite_id) {
        try { await request(kreaFavoriteUrl("/item"), { method: "PUT", body: JSON.stringify({ id: item.favorite_id, favorite: false }) }); notifyKreaFavorites(); await loadKreaPromptPool(); }
        catch (error) { toast(error.message); }
      } else sharedFavoriteOpen("single", { section: "prompt", text: item.text, metadata: { title: item.title, subtitle: item.subtitle, prompt: item.text } });
    }, `button ghost compact favorite-toggle${item.favorite ? " active" : ""}`);
    favorite.title = item.favorite ? "取消收藏" : "收藏提示词"; favorite.setAttribute("aria-label", favorite.title);
    controls.append(favorite);
    if (item.in_pool !== false) {
      const edit = kreaButton("✎", () => openKreaPromptItemDialog(item)); edit.title = "编辑提示词"; edit.setAttribute("aria-label", edit.title); controls.append(edit);
      const remove = kreaButton("×", () => deleteKreaPromptPoolSelection([item.id]), "icon-button dark krea-prompt-remove");
      remove.title = "从词池删除"; remove.setAttribute("aria-label", remove.title); controls.append(remove);
    }
    row.append(checkbox, copy, controls); return row;
  }));
  if (!kreaPromptPool.items?.length) ui.kreaPromptPoolList.append(kreaElement("div", "inline-empty krea-prompt-empty", kreaPoolTotalCount ? "没有匹配的提示词。" : "Krea 2 词池为空，请新增或导入自然语言提示词。"));
  updatePoolOverview();
  if (typeof protectStorageControls === "function") protectStorageControls();
}
async function applyKreaSelection(items, operation) {
  if (!items.length || !kreaSelectionContextCurrent(operation)) return;
  const missing = items.filter(item => item.in_pool === false);
  if (missing.length) await request("/api/prompt-pools/items/ensure-many", { method: "POST", body: JSON.stringify({ model_profile: "krea2", items: missing.map(({ title, subtitle, text }) => ({ title, subtitle, text })) }) });
  if (!kreaSelectionContextCurrent(operation)) return;
  invalidateKreaPoolSelection();
  kreaPromptPoolSelection = [...new Set([...kreaPromptPoolSelection, ...items.map(item => item.id)])];
  ui.krea_prompt_pool_enabled.checked = true;
  schedulePersist(); await loadKreaPromptPool();
}
async function selectKreaItems(items) {
  const operation = beginKreaSelection(); if (!operation) return;
  try {
    await applyKreaSelection(items, operation);
  } catch (error) { if (kreaSelectionContextCurrent(operation)) { toast(error.message); renderKreaPromptPool(); } }
  finally { finishKreaSelection(operation); }
}
async function selectAllKreaFilteredItems() {
  const operation = beginKreaSelection(); if (!operation) return;
  try {
    const result = await fetchAllKreaPromptPoolItems(undefined, {}, () => kreaSelectionContextCurrent(operation));
    if (result && kreaSelectionContextCurrent(operation)) await applyKreaSelection(result.items, operation);
  } catch (error) { if (kreaSelectionContextCurrent(operation)) toast(error.message); }
  finally { finishKreaSelection(operation); }
}
function kreaDescendants(groups, id) {
  const ids = new Set([id]);
  for (let changed = true; changed;) { changed = false; for (const group of groups) if (ids.has(group.parentId) && !ids.has(group.id)) { ids.add(group.id); changed = true; } }
  return ids;
}
function kreaGroupPath(group, groups = kreaPoolView.groups) {
  const labels = [group.name], seen = new Set([group.id]);
  for (let parent = groups.find(item => item.id === group.parentId); parent && !seen.has(parent.id); parent = groups.find(item => item.id === parent.parentId)) {
    labels.unshift(parent.name); seen.add(parent.id);
  }
  return labels.join(" / ");
}
function renderKreaPoolSidebar() {
  const nav = document.createDocumentFragment();
  for (const kind of ["favorite", "custom"]) {
    const favorite = kind === "favorite", groups = favorite ? kreaPoolView.favorites.groups || [] : kreaPoolView.groups;
    const section = kreaElement("section", `facet-section${favorite ? " collections" : " custom-group-section"}`);
    const heading = kreaElement("div", "facet-heading"); heading.append(kreaElement("h3", "", favorite ? "收藏分组" : "自定义分组"));
    const add = kreaButton("+", () => openKreaGroupDialog(kind), ""); add.title = favorite ? "新建收藏分组" : "新建自定义分组"; add.setAttribute("aria-label", add.title); heading.append(add); section.append(heading);
    const tree = kreaElement("div", "favorite-tree");
    const filter = id => { invalidateKreaPoolSelection(); if (favorite) kreaPoolView.collection = id; else kreaPoolView.customGroup = id; setKreaSidebarOpen(false); void loadKreaPromptPool({ page: 1 }); };
    const active = favorite ? kreaPoolView.collection : kreaPoolView.customGroup;
    const all = kreaButton(favorite ? "全部提示词" : "不限分组", () => filter(""), active === "" ? "active" : ""); tree.append(all);
    if (favorite) tree.append(kreaButton(`全部收藏  ${kreaPoolView.favorites.items?.length || 0}`, () => filter("__all__"), active === "__all__" ? "active" : ""));
    const walk = (parent = null, depth = 0, visited = new Set()) => {
      for (const group of groups.filter(g => (g.parentId || null) === parent)) {
        if (visited.has(group.id)) continue; visited.add(group.id);
        const children = groups.some(g => g.parentId === group.id), key = `${kind}:${group.id}`, collapsed = kreaPoolView.collapsed.has(key);
        const row = kreaElement("div", `favorite-tree-row${active === group.id ? " active" : ""}`);
        row.style.setProperty("--tree-depth", depth); row.dataset.groupId = group.id; row.dataset.parentId = group.parentId || "";
        if (children) {
          row.setAttribute("aria-expanded", String(!collapsed));
          const toggle = kreaButton("›", () => { if (collapsed) kreaPoolView.collapsed.delete(key); else kreaPoolView.collapsed.add(key); renderKreaPoolSidebar(); }, "tree-disclosure");
          toggle.setAttribute("aria-label", `${collapsed ? "展开" : "收起"}${group.name}`); row.append(toggle);
        } else row.append(kreaElement("span", "tree-spacer"));
        const select = kreaButton("", () => filter(group.id), "group-select");
        const descendants = kreaDescendants(groups, group.id);
        const count = favorite ? (kreaPoolView.favorites.items || []).filter(item => item.groupIds?.some(id => descendants.has(id))).length : group.count || 0;
        select.append(kreaElement("span", "", group.id === "default" ? "我的收藏" : group.name), kreaElement("small", "", String(count)));
        const edit = kreaButton("⋯", () => openKreaGroupDialog(kind, group), "collection-edit"); edit.setAttribute("aria-label", `管理${group.name}`);
        row.append(select, edit); tree.append(row); if (!collapsed) walk(group.id, depth + 1, visited);
      }
    };
    walk(); section.append(tree); nav.append(section);
  }
  ui.kreaPoolSidebar.replaceChildren(nav);
}
function renderKreaGroupChecks(container, groups, selected = []) {
  container.replaceChildren(...groups.map(group => {
    const label = kreaElement("label", "favorite-group-check"), checkbox = kreaElement("input");
    checkbox.type = "checkbox"; checkbox.value = group.id; checkbox.checked = selected.includes(group.id);
    label.append(checkbox, kreaElement("span", "", kreaGroupPath(group, groups))); return label;
  }));
  if (!groups.length) container.append(kreaElement("span", "field-hint", "暂无分组，可在左侧新建。"));
}
function kreaChecked(container) { return [...container.querySelectorAll("input:checked")].map(input => input.value); }
function openKreaPromptItemDialog(item = null) {
  ++kreaPoolView.itemSession;
  editingKreaPromptItem = item ? { ...item } : null;
  ui.kreaPromptItemTitle.textContent = item ? "编辑自然语言提示词" : "新增自然语言提示词";
  ui.kreaPromptItemName.value = item?.title || ""; ui.kreaPromptItemSubtitle.value = item?.subtitle || "";
  ui.kreaPromptItemText.value = item?.text || ""; ui.kreaPromptItemDelete.hidden = !item;
  renderKreaGroupChecks(ui.kreaPromptItemGroups, kreaPoolView.groups, item?.groupIds || (kreaPoolView.customGroup ? [kreaPoolView.customGroup] : []));
  motionDialog(ui.kreaPromptItemDialog, true); ui.kreaPromptItemName.focus({ preventScroll: true });
}
async function saveKreaPromptItem(event) {
  event.preventDefault(); const previousId = editingKreaPromptItem?.id, context = kreaSelectionContext();
  const session = kreaPoolView.itemSession;
  const body = { model_profile: "krea2", title: ui.kreaPromptItemName.value.trim(), subtitle: ui.kreaPromptItemSubtitle.value.trim(), text: ui.kreaPromptItemText.value.trim(), groupIds: kreaChecked(ui.kreaPromptItemGroups), ...(previousId ? { id: previousId } : {}) };
  if (!body.title || !body.text || currentProfile() !== "krea2") return;
  try {
    const item = await request("/api/prompt-pools/items", { method: previousId ? "PUT" : "POST", body: JSON.stringify(body) });
    if (!kreaDraftContextCurrent(context)) return;
    invalidateKreaPoolSelection();
    kreaPromptPoolSelection = [...new Set(kreaPromptPoolSelection.map(id => id === previousId ? item.id : id))];
    if (session === kreaPoolView.itemSession) { motionDialog(ui.kreaPromptItemDialog, false); editingKreaPromptItem = null; }
    await loadKreaPromptPool(); schedulePersist(); toast(previousId ? "提示词已更新" : "提示词已新增");
  } catch (error) { toast(error.message); }
}
async function deleteKreaPromptPoolSelection(ids = kreaPromptPoolSelection) {
  const context = kreaSelectionContext();
  const targets = [...new Set(ids.map(String))];
  if (!targets.length || !confirm(`从 Krea 2 词池删除 ${targets.length} 条提示词？已保存的收藏仍会保留。`)) return;
  try {
    await request("/api/prompt-pools/items/delete", { method: "POST", body: JSON.stringify({ model_profile: "krea2", ids: targets }) });
    if (!kreaDraftContextCurrent(context)) return;
    invalidateKreaPoolSelection();
    kreaPromptPoolSelection = kreaPromptPoolSelection.filter(id => !targets.includes(id));
    if (!kreaPromptPoolSelection.length) ui.krea_prompt_pool_enabled.checked = false;
    if (editingKreaPromptItem && targets.includes(editingKreaPromptItem.id)) motionDialog(ui.kreaPromptItemDialog, false);
    await loadKreaPromptPool(); schedulePersist(); toast(`已删除 ${targets.length} 条提示词`);
  } catch (error) { toast(error.message); }
}
function openKreaGroupDialog(kind, group = null) {
  kreaPoolView.editor = { kind, group, busy: false }; const favorite = kind === "favorite";
  const groups = favorite ? kreaPoolView.favorites.groups || [] : kreaPoolView.groups;
  ui.kreaGroupTitle.textContent = `${group ? "管理" : "新建"}${favorite ? "收藏" : "自定义"}分组`;
  ui.kreaGroupName.value = group?.name || ""; ui.kreaGroupName.disabled = group?.id === "default";
  ui.kreaGroupParent.replaceChildren(new Option("顶层分组", ""), ...groups.filter(g => g.id !== group?.id).map(g => new Option(kreaGroupPath(g, groups), g.id)));
  ui.kreaGroupParent.value = group?.parentId || ""; ui.kreaGroupParent.disabled = Boolean(group) || favorite;
  ui.kreaGroupSave.hidden = group?.id === "default";
  ui.kreaGroupDelete.hidden = !group || group.id === "default";
  ui.kreaGroupDeleteModeField.hidden = favorite || !group; ui.kreaGroupDeleteMode.value = "keep";
  ui.kreaGroupImportField.hidden = !favorite || !group;
  ui.kreaGroupImportSource.replaceChildren(...kreaPoolView.groups.map(g => new Option(kreaGroupPath(g), g.id)));
  ui.kreaGroupImport.disabled = !kreaPoolView.groups.length; ui.kreaGroupError.textContent = "";
  for (const control of [ui.kreaGroupSave, ui.kreaGroupDelete]) {
    control.toggleAttribute("data-favorite-extension-write", favorite);
    control.dataset.storageWrite = favorite ? "" : "prompt_pools";
  }
  ui.kreaGroupImport.setAttribute("data-favorite-extension-write", "");
  motionDialog(ui.kreaGroupDialog, true);
}
async function saveKreaGroup(event) {
  event.preventDefault(); const editor = kreaPoolView.editor, epoch = kreaEpoch();
  if (!editor || editor.busy || currentProfile() !== "krea2") return;
  editor.busy = true;
  try {
    const { kind, group } = editor, name = ui.kreaGroupName.value.trim();
    const url = kind === "favorite" ? kreaFavoriteUrl(`/groups${group ? `/${encodeURIComponent(group.id)}` : ""}`) : `/api/prompt-pools/groups${group ? `/${encodeURIComponent(group.id)}` : ""}`;
    const body = kind === "favorite" ? { name } : { model_profile: "krea2", name, parentId: ui.kreaGroupParent.value || null };
    await request(url, { method: group ? "PUT" : "POST", body: JSON.stringify(body) });
    if (editor !== kreaPoolView.editor || !kreaCurrent(epoch)) return;
    motionDialog(ui.kreaGroupDialog, false); notifyKreaFavorites(); await loadKreaPromptPool();
  } catch (error) { ui.kreaGroupError.textContent = error.message; }
  finally { editor.busy = false; }
}
async function deleteKreaGroup() {
  const editor = kreaPoolView.editor, epoch = kreaEpoch(); if (!editor?.group || editor.busy) return;
  if (!confirm(`删除分组“${editor.group.name}”及其子分组？${editor.kind === "favorite" ? "收藏正文仍会保留。" : ui.kreaGroupDeleteMode.selectedOptions[0].textContent}`)) return;
  editor.busy = true;
  try {
    const url = editor.kind === "favorite" ? kreaFavoriteUrl(`/groups/${encodeURIComponent(editor.group.id)}`) : `/api/prompt-pools/groups/${encodeURIComponent(editor.group.id)}?model_profile=krea2&deleteMode=${ui.kreaGroupDeleteMode.value}`;
    await request(url, { method: "DELETE" });
    if (!kreaCurrent(epoch)) return;
    if (editor.kind === "custom" && ui.kreaGroupDeleteMode.value !== "keep") await reconcileKreaSelection();
    motionDialog(ui.kreaGroupDialog, false); notifyKreaFavorites(); await loadKreaPromptPool({ page: 1 });
  } catch (error) { ui.kreaGroupError.textContent = error.message; }
  finally { editor.busy = false; }
}
async function importKreaFavoriteChild() {
  const editor = kreaPoolView.editor, epoch = kreaEpoch(); if (!editor?.group || editor.busy) return;
  editor.busy = true;
  try {
    await request(kreaFavoriteUrl(`/groups/${encodeURIComponent(editor.group.id)}/children/import`), { method: "POST", body: JSON.stringify({ customGroupId: ui.kreaGroupImportSource.value }) });
    if (!kreaCurrent(epoch)) return;
    motionDialog(ui.kreaGroupDialog, false); notifyKreaFavorites(); await loadKreaPromptPool();
  } catch (error) { ui.kreaGroupError.textContent = error.message; }
  finally { editor.busy = false; }
}
function notifyKreaFavorites() { if (typeof sharedFavoritesChanged === "function") sharedFavoritesChanged({ model_profile: "krea2", section: "prompt" }); }
async function reconcileKreaSelection(validIds = null) {
  const context = kreaSelectionContext();
  const ids = new Set(validIds || (await fetchAllKreaPromptPoolItems("", { all: true })).items.map(item => item.id));
  if (!kreaSelectionContextCurrent(context)) return;
  invalidateKreaPoolSelection();
  kreaPromptPoolSelection = kreaPromptPoolSelection.filter(id => ids.has(id));
  if (!kreaPromptPoolSelection.length) ui.krea_prompt_pool_enabled.checked = false;
  schedulePersist();
}
function openKreaFavoriteSelection() {
  kreaPoolView.favoriteIds = [...kreaPromptPoolSelection];
  ui.kreaFavoriteSelectionHint.textContent = `将 ${kreaPoolView.favoriteIds.length} 条已选提示词加入收藏分组。`;
  renderKreaGroupChecks(ui.kreaFavoriteSelectionGroups, kreaPoolView.favorites.groups || [], [kreaPoolView.collection && kreaPoolView.collection !== "__all__" ? kreaPoolView.collection : "default"]);
  ui.kreaFavoriteSelectionName.value = ""; ui.kreaFavoriteSelectionError.textContent = "";
  ui.kreaFavoriteSelectionSave.setAttribute("data-favorite-extension-write", "");
  ui.kreaFavoriteSelectionCreate.setAttribute("data-favorite-extension-write", "");
  motionDialog(ui.kreaFavoriteSelectionDialog, true);
}
async function saveKreaFavoriteSelection(event) {
  event.preventDefault(); const groups = kreaChecked(ui.kreaFavoriteSelectionGroups), epoch = kreaEpoch();
  if (!groups.length) { ui.kreaFavoriteSelectionError.textContent = "请选择至少一个收藏分组"; return; }
  ui.kreaFavoriteSelectionSave.disabled = true;
  try {
    await request(kreaFavoriteUrl("/selection"), { method: "POST", body: JSON.stringify({ selection: { mode: "include", ids: kreaPoolView.favoriteIds }, groupIds: groups }) });
    if (!kreaCurrent(epoch)) return;
    motionDialog(ui.kreaFavoriteSelectionDialog, false); notifyKreaFavorites(); await loadKreaPromptPool(); toast("已收藏本次选择");
  } catch (error) { ui.kreaFavoriteSelectionError.textContent = error.message; }
  finally { ui.kreaFavoriteSelectionSave.disabled = false; }
}
async function createKreaSelectionGroup() {
  const name = ui.kreaFavoriteSelectionName.value.trim(), epoch = kreaEpoch(); if (!name) return;
  try {
    const selected = kreaChecked(ui.kreaFavoriteSelectionGroups), before = new Set(kreaPoolView.favorites.groups.map(g => g.id));
    const result = await request(kreaFavoriteUrl("/groups"), { method: "POST", body: JSON.stringify({ name }) });
    if (!kreaCurrent(epoch)) return;
    kreaPoolView.favorites = result; selected.push(...result.groups.filter(g => !before.has(g.id)).map(g => g.id));
    renderKreaGroupChecks(ui.kreaFavoriteSelectionGroups, result.groups, selected); ui.kreaFavoriteSelectionName.value = ""; notifyKreaFavorites();
  } catch (error) { ui.kreaFavoriteSelectionError.textContent = error.message; }
}
function resolveKreaImportFormat(content) {
  if (ui.kreaPromptImportFormat.value !== "auto") return ui.kreaPromptImportFormat.value;
  const text = String(content).replace(/^\uFEFF/, "").trim(); if (/^[\[{]/.test(text)) return "json";
  return /(^|,)\s*"?(?:text|prompt|提示词|自然语言)"?\s*(,|$)/i.test(text.split(/\r?\n/, 1)[0] || "") ? "csv" : "text";
}
function openKreaPromptImportDialog() {
  kreaImportRequest++; kreaImportPreview = null; ui.kreaPromptPoolFile.value = ""; ui.kreaPromptPoolText.value = "";
  ui.kreaPromptImportFormat.value = "auto"; ui.kreaPromptImportMode.value = "append"; ui.kreaPromptImportBundle.value = "";
  document.getElementById("kreaPromptImportSource").open = true;
  renderKreaGroupChecks(ui.kreaPromptImportGroups, kreaPoolView.groups, kreaPoolView.customGroup ? [kreaPoolView.customGroup] : []);
  ui.kreaPromptImportGroups.closest("fieldset").disabled = false;
  invalidateKreaPromptImportPreview("选择文件或粘贴内容后，先校验预览，再确认写入。"); motionDialog(ui.kreaPromptImportDialog, true);
}
function kreaImportOptions() {
  const mode = ui.kreaPromptImportMode.value;
  return { model_profile: "krea2", mode, format: resolveKreaImportFormat(ui.kreaPromptPoolText.value), content: ui.kreaPromptPoolText.value,
    bundleName: ui.kreaPromptImportBundle.value.trim(), targetGroupIds: mode === "replace" ? [] : kreaChecked(ui.kreaPromptImportGroups) };
}
function invalidateKreaPromptImportPreview(message = "内容或选项已变化，请重新校验并预览。") {
  kreaImportRequest++; kreaImportPreview = null; ui.kreaPromptImportSummary.hidden = true; ui.kreaPromptImportRows.replaceChildren();
  ui.kreaPromptImportCommit.disabled = true; ui.kreaPromptImportCommit.textContent = "确认导入"; ui.kreaPromptImportHint.textContent = message;
}
async function previewKreaPromptImport() {
  const options = kreaImportOptions(), id = ++kreaImportRequest, epoch = kreaEpoch();
  if (!options.content.trim() || !options.bundleName) { invalidateKreaPromptImportPreview("请填写导入大项名称，并选择文件或输入内容。"); return; }
  ui.kreaPromptImportCommit.disabled = true; ui.kreaPromptImportHint.textContent = "正在校验导入内容…";
  try {
    const payload = await request("/api/prompt-pools/import/preview", { method: "POST", body: JSON.stringify(options) });
    if (id !== kreaImportRequest || !kreaCurrent(epoch)) return;
    kreaImportPreview = { ...payload, options, fingerprint: JSON.stringify(options) }; renderKreaPromptImportPreview();
    const source = document.getElementById("kreaPromptImportSource");
    if (source && !payload.summary?.error) {
      if (typeof StudioMotion !== "undefined") StudioMotion.disclosure(source, false);
      else source.open = false;
    }
  } catch (error) { if (id === kreaImportRequest) invalidateKreaPromptImportPreview(`校验失败：${error.message}`); }
}
function renderKreaPromptImportPreview() {
  const preview = kreaImportPreview; if (!preview) return;
  const rows = preview.rows || [], summary = preview.summary || {};
  ui.kreaPromptImportSummary.hidden = false;
  ui.kreaPromptImportSummary.replaceChildren(...[`${rows.length} 条输入`, `${summary.new || 0} 条新增`, `${summary.conflict || 0} 条冲突`, `${summary.error || 0} 条错误`].map(text => kreaElement("span", "", text)));
  ui.kreaPromptImportRows.replaceChildren(...rows.map((item, index) => {
    const row = kreaElement("div", "krea-import-row structured"), copy = kreaElement("div", "krea-import-copy");
    copy.append(kreaElement("strong", "", item.item?.title || `第 ${index + 1} 条`), kreaElement("small", "", item.item?.subtitle || ""), kreaElement("span", "", item.error || item.item?.prompt || item.item?.text || ""));
    if (item.groups?.length) copy.append(kreaElement("small", "", `分组：${item.groups.join(" / ")}`));
    const action = kreaElement("select"); action.setAttribute("aria-label", `第 ${index + 1} 条导入操作`);
    if (item.status === "error") { action.append(new Option("需修正", "skip")); action.disabled = true; }
    else if (item.status === "conflict") action.append(new Option("跳过", "skip"), new Option("覆盖信息", "overwrite"));
    else action.append(new Option("导入", "create"), new Option("跳过", "skip"));
    action.value = item.action || (item.status === "new" ? "create" : "skip");
    action.addEventListener("change", () => { item.action = action.value; ui.kreaPromptImportCommit.disabled = rows.some(r => r.status === "error") || rows.every(r => r.action === "skip"); });
    row.append(kreaElement("small", "", String(index + 1)), copy, action); return row;
  }));
  ui.kreaPromptImportHint.textContent = summary.error ? "请修正错误后重新预览，尚未写入任何内容。" : preview.options.mode === "replace" ? "将替换全部 Krea 词条与自定义分组，收藏快照保留。" : "冲突默认跳过；覆盖仅更新同正文词条的名称、说明和分组。";
  ui.kreaPromptImportCommit.disabled = !rows.length || Boolean(summary.error) || rows.every(r => r.action === "skip");
  ui.kreaPromptImportCommit.textContent = preview.options.mode === "replace" ? "确认替换整个词池" : "确认导入";
}
async function commitKreaPromptImport() {
  const preview = kreaImportPreview, context = kreaSelectionContext();
  if (!preview || preview.fingerprint !== JSON.stringify(kreaImportOptions())) { await previewKreaPromptImport(); return; }
  if (preview.saving) return;
  if (preview.options.mode === "replace" && !confirm("替换整个 Krea 2 词池及自定义分组？现有收藏快照仍会保留。")) return;
  preview.saving = true;
  ui.kreaPromptImportCommit.disabled = true;
  try {
    const payload = await request("/api/prompt-pools/import", { method: "POST", body: JSON.stringify({ model_profile: "krea2", mode: preview.options.mode, rows: preview.rows, bundleName: preview.options.bundleName, targetGroupIds: preview.options.targetGroupIds }) });
    if (!kreaDraftContextCurrent(context)) return;
    if (preview.options.mode === "replace") await reconcileKreaSelection(preview === kreaImportPreview ? payload.valid_ids : null);
    if (!kreaDraftContextCurrent(context)) return;
    if (preview === kreaImportPreview) { motionDialog(ui.kreaPromptImportDialog, false); kreaImportPreview = null; }
    await loadKreaPromptPool({ page: 1 });
    schedulePersist(); toast(`导入完成：新增 ${payload.imported || 0} 条，更新 ${payload.updated || 0} 条`);
  } catch (error) { if (preview === kreaImportPreview && kreaDraftContextCurrent(context)) { ui.kreaPromptImportHint.textContent = `导入失败：${error.message}`; ui.kreaPromptImportCommit.disabled = false; } }
  finally { preview.saving = false; }
}
function initializeKreaPromptPool() {
  for (const id of ["kreaPoolSort", "toggleKreaPoolSidebar", "kreaPoolSidebar", "clearKreaPoolFilters", "kreaPoolFavoriteSelection", "kreaPromptItemName", "kreaPromptItemSubtitle", "kreaPromptItemGroups", "kreaPromptImportBundle", "kreaPromptImportGroups", "kreaGroupDialog", "kreaGroupForm", "kreaGroupTitle", "kreaGroupClose", "kreaGroupCancel", "kreaGroupName", "kreaGroupParent", "kreaGroupDelete", "kreaGroupDeleteModeField", "kreaGroupDeleteMode", "kreaGroupSave", "kreaGroupImportField", "kreaGroupImportSource", "kreaGroupImport", "kreaGroupError", "kreaFavoriteSelectionDialog", "kreaFavoriteSelectionForm", "kreaFavoriteSelectionHint", "kreaFavoriteSelectionGroups", "kreaFavoriteSelectionName", "kreaFavoriteSelectionCreate", "kreaFavoriteSelectionSave", "kreaFavoriteSelectionError", "kreaFavoriteSelectionClose", "kreaFavoriteSelectionCancel"]) ui[id] = document.getElementById(id);
  const on = (id, event, fn) => ui[id].addEventListener(event, fn);
  on("openKreaPromptPool", "click", openKreaPromptPool);
  for (const id of ["closeKreaPool", "confirmKreaPool", "kreaPoolBackdrop"]) on(id, "click", closeKreaPromptPool);
  on("toggleKreaPoolSidebar", "click", () => setKreaSidebarOpen(!ui.kreaPoolSidebar.classList.contains("mobile-open")));
  matchMedia("(max-width: 620px)").addEventListener("change", () => setKreaSidebarOpen(false));
  on("kreaPoolSearch", "input", () => { invalidateKreaPoolSelection(); clearTimeout(kreaPoolSearchTimer); kreaPoolSearchTimer = setTimeout(() => void loadKreaPromptPool({ page: 1 }), 220); });
  for (const id of ["kreaPoolSort", "kreaPoolSelectedOnly"]) on(id, "change", () => { invalidateKreaPoolSelection(); return loadKreaPromptPool({ page: 1 }); });
  on("clearKreaPoolFilters", "click", () => { invalidateKreaPoolSelection(); kreaPoolView.collection = ""; kreaPoolView.customGroup = ""; ui.kreaPoolSearch.value = ""; ui.kreaPoolSort.value = ""; ui.kreaPoolSelectedOnly.checked = false; return loadKreaPromptPool({ page: 1 }); });
  on("kreaPoolSelectPage", "click", () => kreaPoolResultsCurrent() ? selectKreaItems(kreaPromptPool.items || []) : undefined);
  on("kreaPoolSelectAll", "click", selectAllKreaFilteredItems);
  on("clearKreaPoolSelection", "click", () => { invalidateKreaPoolSelection(); kreaPromptPoolSelection = []; ui.krea_prompt_pool_enabled.checked = false; schedulePersist(); return loadKreaPromptPool(); });
  on("krea_prompt_pool_enabled", "change", () => { invalidateKreaPoolSelection(); schedulePersist(); return loadKreaPromptPool(); });
  on("kreaPoolPrev", "click", () => void loadKreaPromptPool({ page: Math.max(1, kreaPoolPage - 1) }));
  on("kreaPoolNext", "click", () => void loadKreaPromptPool({ page: Math.min(kreaPoolPages, kreaPoolPage + 1) }));
  on("kreaPoolDeleteSelection", "click", () => void deleteKreaPromptPoolSelection());
  on("kreaPoolAddItem", "click", () => openKreaPromptItemDialog()); on("kreaPromptItemForm", "submit", saveKreaPromptItem);
  for (const id of ["kreaPromptItemClose", "kreaPromptItemCancel"]) on(id, "click", () => motionDialog(ui.kreaPromptItemDialog, false));
  on("kreaPromptItemDelete", "click", () => editingKreaPromptItem && void deleteKreaPromptPoolSelection([editingKreaPromptItem.id]));
  on("kreaPoolFavoriteSelection", "click", openKreaFavoriteSelection); on("kreaFavoriteSelectionForm", "submit", saveKreaFavoriteSelection);
  on("kreaFavoriteSelectionCreate", "click", createKreaSelectionGroup);
  for (const id of ["kreaFavoriteSelectionClose", "kreaFavoriteSelectionCancel"]) on(id, "click", () => motionDialog(ui.kreaFavoriteSelectionDialog, false));
  on("kreaGroupForm", "submit", saveKreaGroup); on("kreaGroupDelete", "click", deleteKreaGroup); on("kreaGroupImport", "click", importKreaFavoriteChild);
  for (const id of ["kreaGroupClose", "kreaGroupCancel"]) on(id, "click", () => motionDialog(ui.kreaGroupDialog, false));
  on("kreaPoolOpenImport", "click", openKreaPromptImportDialog);
  for (const id of ["kreaPromptImportClose", "kreaPromptImportCancel"]) on(id, "click", () => motionDialog(ui.kreaPromptImportDialog, false));
  on("kreaPromptImportForm", "submit", event => event.preventDefault());
  on("kreaPromptImportPreviewButton", "click", previewKreaPromptImport); on("kreaPromptImportCommit", "click", commitKreaPromptImport);
  for (const id of ["kreaPromptPoolText", "kreaPromptImportBundle"]) on(id, "input", () => invalidateKreaPromptImportPreview());
  for (const id of ["kreaPromptImportFormat", "kreaPromptImportGroups"]) on(id, "change", () => invalidateKreaPromptImportPreview());
  on("kreaPromptImportMode", "change", () => { ui.kreaPromptImportGroups.closest("fieldset").disabled = ui.kreaPromptImportMode.value === "replace"; invalidateKreaPromptImportPreview(); });
  on("kreaPromptPoolFile", "change", async () => {
    const file = ui.kreaPromptPoolFile.files?.[0]; if (!file) return;
    invalidateKreaPromptImportPreview("正在读取文件…"); const id = kreaImportRequest, epoch = kreaEpoch();
    try {
      const text = await file.text(); if (id !== kreaImportRequest || !kreaCurrent(epoch)) return;
      ui.kreaPromptPoolText.value = text; ui.kreaPromptImportBundle.value = file.name.replace(/\.[^.]+$/, "").slice(0, 100);
      ui.kreaPromptImportFormat.value = file.name.toLowerCase().endsWith(".csv") ? "csv" : "auto"; await previewKreaPromptImport();
    } catch (error) { if (id === kreaImportRequest && kreaCurrent(epoch)) invalidateKreaPromptImportPreview(`读取文件失败：${error.message}`); }
  });
}
