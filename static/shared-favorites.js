/* Model-owned favorites, displayed as compact insert/remove chips in the single workbench. */
const SHARED_FAVORITES_KEY = "anima-studio:favorites-updated";
const SHARED_FAVORITES_VIEW_KEY = "anima-studio:favorite-views:v1";
const sharedFavoritesState = {
  ready: false, profile: "", epoch: 0, request: 0, data: null, items: [],
  views: {}, targets: {}, saveTargets: {}, editor: null, groupEditor: null, refreshTimer: null,
  changeRevision: 0, reconciled: new Map(), refreshFailed: false,
};

// Source changes own cache invalidation; retrying this refresh never repeats a write.
function poolSourceChanged(change = {}, broadcast = true) {
  sharedFavoritesChanged({ ...change, sourceChange: true }, broadcast);
}
function prunePoolSourceSelections(profile, section, ids) {
  const deleted = new Set(ids || []);
  if (!deleted.size) return;
  const prune = state => {
    if (!state) return;
    if (profile === "anima") {
      const selection = state.pools?.[section];
      if (selection) for (const key of ["ids", "excluded_ids"]) selection[key] = (selection[key] || []).filter(id => !deleted.has(id));
    } else {
      if (state.kreaPromptPoolSelection) state.kreaPromptPoolSelection = state.kreaPromptPoolSelection.filter(id => !deleted.has(id));
      if (state.krea_prompt_pool?.ids) state.krea_prompt_pool.ids = state.krea_prompt_pool.ids.filter(id => !deleted.has(id));
      if (state.kreaPromptPoolSelection?.length === 0) state.kreaPromptPoolEnabled = false;
      if (state.krea_prompt_pool?.ids?.length === 0) state.krea_prompt_pool.enabled = false;
    }
  };
  if (typeof profileDrafts !== "undefined") prune(profileDrafts[profile]);
  if (typeof singleWorkbenchState !== "undefined") for (const workbench of Object.values(singleWorkbenchState.drafts || {})) {
    for (const [savedProfile, saved] of Object.entries(workbench || {})) {
      if (savedProfile === profile) prune(saved?.settings);
      prune(saved?.profileDrafts?.[profile]);
    }
  }
  if (profile === currentProfile()) {
    if (profile === "anima" && typeof draft !== "undefined") prune(draft);
    if (profile === "krea2" && typeof kreaPromptPoolSelection !== "undefined") {
      kreaPromptPoolSelection = kreaPromptPoolSelection.filter(id => !deleted.has(id));
      if (!kreaPromptPoolSelection.length && ui.krea_prompt_pool_enabled) ui.krea_prompt_pool_enabled.checked = false;
    }
  }
  if (typeof schedulePersist === "function") schedulePersist();
}
function poolMutationError(error, context = {}) {
  const payload = error.payload || {};
  if (payload.source_committed === true) {
    poolSourceChanged({ ...context, ...payload });
    return `源词条已提交，关联同步未完成：${error.message}。请先刷新状态，不要重复提交。${payload.recovery_required ? "需要恢复存储后重试同步。" : ""}`;
  }
  return `${payload.source_committed === false ? "更改未提交，编辑内容已保留。" : ""}${error.message}${payload.recovery_required ? " 请恢复存储后重试，编辑内容已保留。" : ""}`;
}
async function ensurePoolReconciled(profile = currentProfile(), section = sharedFavoritesView(profile).section) {
  if (section === "artist") return;
  const key = `${profile}:${section}`, state = sharedFavoritesState;
  if (!state.reconciled.has(key)) {
    const pending = request(sharedFavoriteUrl(section, "/reconcile", profile), { method: "POST", body: "{}" })
      .then(result => {
        state.reconciled.set(key, true);
        if (result.deletedItemIds?.length || result.updatedItemIds?.length || result.favoritesSync?.updatedCount || result.favoritesSync?.deletedCount) sharedFavoritesChanged({ ...result, model_profile: profile, section, refresh: false }, false);
        return result;
      })
      .catch(error => { state.reconciled.delete(key); throw error; });
    state.reconciled.set(key, pending);
  }
  return state.reconciled.get(key);
}
function showPoolRefreshFailure(failed, message = "内容已保存，列表刷新失败。请重新刷新，操作不会重复提交。") {
  sharedFavoritesState.refreshFailed = failed;
  const notice = document.getElementById("poolRefreshNotice");
  if (!notice) return;
  notice.hidden = !failed;
  const text = document.getElementById("poolRefreshMessage");
  if (text) text.textContent = message;
}
async function refreshPoolLifecycle() {
  const state = sharedFavoritesState, profile = currentProfile();
  const revision = state.changeRevision;
  const tasks = [];
  if (state.ready) tasks.push(sharedFavoritesLoad({ throwOnError: true, reconcile: false }));
  if (profile === "anima") {
    if (typeof refreshPoolSelectionTotals === "function") tasks.push(refreshPoolSelectionTotals({ throwOnError: true }));
    if (typeof activeSection !== "undefined" && ui.poolDrawer?.classList.contains("open")) {
      const section = activeSection;
      tasks.push(Promise.all([loadFavorites(section, { throwOnError: true }), loadCustomGroups(section, { throwOnError: true })]).then(() => {
        if (profile === currentProfile() && section === activeSection && revision === state.changeRevision) return loadPool({ throwOnError: true });
      }));
    }
    if (typeof loadArtistFavorites === "function" && sharedFavoritesView().section === "artist") tasks.push(loadArtistFavorites());
  } else if (typeof loadKreaPromptPool === "function") tasks.push(loadKreaPromptPool({ throwOnError: true }));
  const results = await Promise.allSettled(tasks);
  if (revision !== state.changeRevision || profile !== currentProfile()) return;
  showPoolRefreshFailure(results.some(result => result.status === "rejected"));
}

function sharedFavoriteUrl(section, suffix = "", profile = currentProfile()) {
  return `/api/favorites/${section}${suffix}${suffix.includes("?") ? "&" : "?"}model_profile=${encodeURIComponent(profile)}`;
}
function sharedFavoritesView(profile = currentProfile()) {
  const view = sharedFavoritesState.views[profile] ||= { section: profile === "krea2" ? "prompt" : "character", groups: {} };
  if (profile === "krea2") view.section = "prompt";
  else if (![...SECTIONS, "artist"].includes(view.section)) view.section = "character";
  return view;
}
function sharedFavoritesRememberView() {
  try { localStorage.setItem(SHARED_FAVORITES_VIEW_KEY, JSON.stringify(sharedFavoritesState.views)); } catch { /* Optional UI preference. */ }
}
function sharedFavoritesPanelMarkup(prefix) {
  return `<div id="${prefix}FavoritePanel" class="shared-favorite-panel" hidden>
    <div class="single-favorite-tabs" data-favorite-tabs></div>
    <label class="field"><span>收藏分组</span><select id="${prefix}FavoriteGroup"><option value="__all__">全部收藏</option></select></label>
    <div class="single-inline-actions"><button id="${prefix}FavoriteManage" type="button" class="button ghost compact">管理分组</button><button id="${prefix}FavoriteRefresh" type="button" class="button ghost compact">刷新</button></div>
    <p class="field-hint">点击名称插入最近编辑位置，点击爱心确认后取消收藏。</p>
    <div id="${prefix}FavoriteList" class="single-favorite-list"></div>
    <p id="${prefix}FavoriteStatus" class="field-hint" role="status"></p></div>`;
}
function sharedFavoriteTarget(workbench) {
  const remembered = sharedFavoritesState.targets[workbench];
  if (remembered && document.contains(remembered)) return remembered;
  return document.getElementById(workbench === "single" ? "single_positive_prompt" : "extra_prompt");
}
function sharedFavoriteSource(target) {
  if (!target) return "";
  const start = target.selectionStart ?? 0, end = target.selectionEnd ?? start;
  return (end > start ? target.value.slice(start, end) : target.value).trim();
}
function sharedFavoriteCapturedSource(workbench) {
  const target = sharedFavoritesState.saveTargets[workbench] || sharedFavoriteTarget(workbench);
  const text = sharedFavoriteSource(target);
  const start = target?.selectionStart ?? 0, end = target?.selectionEnd ?? start;
  const whole = end === start || (start === 0 && end === target.value.length);
  const metadata = whole && typeof promptPoolMetadataForText === "function"
    ? promptPoolMetadataForText(currentProfile(), text, target) : null;
  return { text, metadata };
}
function sharedFavoriteInsert(text, workbench) {
  const target = sharedFavoriteTarget(workbench);
  if (!target) return;
  const start = target.selectionStart ?? target.value.length, end = target.selectionEnd ?? start;
  const before = target.value.slice(0, start), after = target.value.slice(end);
  const separator = currentProfile() === "krea2" ? "\n" : ", ";
  const prefix = before && !/[\s,，]$/.test(before) ? separator : "";
  const suffix = after && !/^[\s,，]/.test(after) ? separator : "";
  target.setRangeText(`${prefix}${text}${suffix}`, start, end, "end");
  const details = target.closest("details");
  if (details) {
    if (typeof StudioMotion !== "undefined") StudioMotion.disclosure(details, true, { immediate: true });
    else details.open = true;
  }
  target.focus();
  target.dispatchEvent(new Event("input", { bubbles: true }));
  schedulePersist();
}
function sharedFavoriteGroupIds(data, selected) {
  const ids = new Set([selected]);
  for (let changed = true; changed;) {
    changed = false;
    for (const group of data?.groups || []) {
      if (ids.has(group.parentId) && !ids.has(group.id)) { ids.add(group.id); changed = true; }
    }
  }
  return ids;
}
function sharedFavoriteKey(item, section) {
  return String(section === "character" || section === "artist" ? item.name || item.id : item.id || item.name);
}
function sharedFavoritesCurrent(profile, section, epoch = sharedFavoritesState.epoch) {
  return currentProfile() === profile && sharedFavoritesView(profile).section === section && epoch === sharedFavoritesState.epoch;
}
function sharedFavoritesStatus(message) {
  for (const prefix of ["single"]) {
    const element = document.getElementById(`${prefix}FavoriteStatus`);
    if (element) element.textContent = message;
  }
}
function sharedFavoritesResetModel() {
  const state = sharedFavoritesState;
  if (!state.ready || state.profile === currentProfile()) return;
  state.profile = currentProfile();
  state.epoch++; state.request++; state.data = null; state.items = [];
  if (typeof resetKreaPoolContext === "function") resetKreaPoolContext();
  state.targets = {}; state.saveTargets = {}; state.editor = null; state.groupEditor = null;
  for (const id of ["sharedFavoriteDialog", "sharedFavoriteGroupDialog", "favoriteDialog", "favoriteSelectionDialog", "groupDialog", "childGroupDialog", "deleteGroupDialog", "artistDialog"]) {
    const dialog = document.getElementById(id);
    if (dialog?.open) motionDialog(dialog, false);
  }
  if (typeof favoritesData !== "undefined") { favoritesData = { groups: [], items: [], favorite_keys: [] }; favoritesAvailable = false; ++favoritesRequest; }
  if (typeof artistFavoritesData !== "undefined") { artistFavoritesData = { groups: [], items: [] }; renderArtistFavorites(); }
  if (ui.poolDrawer.classList.contains("open")) closePool();
  if (ui.kreaPoolDrawer.classList.contains("open")) closeKreaPromptPool();
  sharedFavoritesRender();
  sharedFavoritesStatus("正在读取当前模型收藏…");
  void sharedFavoritesLoad();
  if (currentProfile() === "anima") void loadArtistFavorites();
}
async function sharedFavoritesLoad({ throwOnError = false, reconcile = true } = {}) {
  const state = sharedFavoritesState;
  if (!state.ready) return;
  if (state.profile !== currentProfile()) { if (reconcile) sharedFavoritesResetModel(); return; }
  const profile = currentProfile(), section = sharedFavoritesView(profile).section, epoch = state.epoch;
  try { if (reconcile && state.reconciled.get(`${profile}:${section}`) !== true) await ensurePoolReconciled(profile, section); }
  catch (error) { sharedFavoritesStatus(poolMutationError(error, { model_profile: profile, section })); if (throwOnError) throw error; return; }
  if (!sharedFavoritesCurrent(profile, section, epoch)) return;
  const token = ++state.request;
  const valid = () => token === state.request && sharedFavoritesCurrent(profile, section, epoch);
  sharedFavoritesStatus("正在读取共享收藏…");
  try {
    const data = await request(sharedFavoriteUrl(section, "", profile));
    if (!valid()) return;
    let items;
    if (section === "prompt") {
      items = (data.items || []).filter(item => item.groupIds?.length).map(item => ({
        ...item, text: item.text || item.customContent || "", title: item.title || item.nickname || item.text || item.customContent,
        favorite_key: String(item.id),
      }));
    } else if (section === "artist") {
      items = (data.items || []).filter(item => item.groupIds?.length).map(item => ({ ...item, text: `@${item.name}`, title: item.nickname || `@${item.name}`, favorite_key: item.name }));
    } else {
      items = [];
      for (let page = 1, pages = 1; page <= pages; page++) {
        const result = await request(`/api/pools/${section}?collection=__all__&limit=200&page=${page}&model_profile=${profile}`);
        if (!valid()) return;
        items.push(...(result.items || []));
        pages = Number(result.pages || 1);
      }
      const saved = new Map((data.items || []).map(item => [sharedFavoriteKey(item, section), item]));
      items = items.map(item => ({
        ...item, title: saved.get(String(item.favorite_key))?.nickname || item.title || item.name,
        text: item.prompt || (item.trigger || []).join(", ") || item.name || item.title,
      }));
    }
    if (!valid()) return;
    state.data = data; state.items = items;
    sharedFavoritesRender();
  } catch (error) {
    if (!valid()) return;
    state.data = null; state.items = []; sharedFavoritesRender();
    sharedFavoritesStatus(`收藏暂不可用：${error.message}`);
    if (throwOnError) throw error;
  }
}
function sharedFavoritesRender() {
  const state = sharedFavoritesState, profile = currentProfile(), view = sharedFavoritesView(profile), section = view.section;
  const data = state.data, groups = data?.groups || [];
  if (data && !groups.some(group => group.id === view.groups[section])) view.groups[section] = "__all__";
  const group = view.groups[section] || "__all__", groupIds = sharedFavoriteGroupIds(data, group);
  const saved = new Map((data?.items || []).map(item => [sharedFavoriteKey(item, section), item]));
  const items = state.items.filter(item => {
    const entry = saved.get(String(item.favorite_key));
    return group === "__all__" ? entry?.groupIds?.length : entry?.groupIds?.some(id => groupIds.has(id));
  });
  for (const prefix of ["single"]) {
    const panel = document.getElementById(`${prefix}FavoritePanel`);
    if (!panel) continue;
    panel.querySelector("[data-favorite-tabs]").replaceChildren(...(profile === "krea2" ? ["prompt"] : [...SECTIONS, "artist"]).map(value => {
      const button = document.createElement("button");
      button.type = "button"; button.className = "button ghost compact";
      button.textContent = value === "prompt" ? "Krea 2 自然语言" : SECTION_META[value]?.label || "画师";
      button.dataset.singleFavoriteSection = value;
      button.setAttribute("aria-pressed", String(value === section));
      button.addEventListener("click", () => {
        if (value === view.section) return;
        view.section = value; state.request++; state.data = null; state.items = [];
        sharedFavoritesRememberView(); sharedFavoritesRender(); void sharedFavoritesLoad();
      });
      return button;
    }));
    const select = document.getElementById(`${prefix}FavoriteGroup`);
    select.replaceChildren(new Option("全部收藏", "__all__"), ...groups.map(item => new Option(sharedFavoriteGroupName(item, groups), item.id)));
    select.value = group;
    document.getElementById(`${prefix}FavoriteManage`).disabled = !data;
    document.getElementById(`${prefix}FavoriteList`).replaceChildren(...items.map(item => {
      const row = document.createElement("span"); row.className = "single-favorite-chip";
      const insert = sharedFavoriteButton(item.title || item.name, () => sharedFavoriteInsert(item.text, prefix));
      insert.title = item.text; insert.className = "shared-favorite-insert";
      const remove = sharedFavoriteButton("♥", async () => {
        if (remove.disabled || !confirm(`确定取消收藏“${item.title || item.name}”吗？词池中的词条会保留。`)) return;
        remove.disabled = true;
        try { await request(sharedFavoriteUrl(section, "/item", profile), { method: "PUT", body: JSON.stringify({ ...sharedFavoriteItemIdentity(item, section), favorite: false }) }); }
        catch (error) { toast(error.message); remove.disabled = false; }
      });
      remove.setAttribute("aria-label", `取消收藏 ${item.title || item.name}`);
      sharedFavoriteProtect(remove, section);
      row.append(insert, remove); return row;
    }));
  }
  sharedFavoritesStatus(data ? `${items.length} 项收藏 · ${profile === "krea2" ? "Krea 2" : "Anima"}` : "暂无收藏数据。");
}
function sharedFavoriteButton(label, action) {
  const button = document.createElement("button"); button.type = "button"; button.textContent = label;
  button.addEventListener("click", action); return button;
}
function sharedFavoriteProtect(control, section, custom = false) {
  control.toggleAttribute("data-favorite-extension-write", section === "prompt" || section === "expression");
  control.toggleAttribute("data-favorite-custom-write", custom && section !== "prompt" && section !== "artist");
}
function sharedFavoriteItemIdentity(item, section) {
  return section === "artist" ? { name: item.name } : { id: item.id };
}
function sharedFavoriteGroupName(group, groups) {
  const names = [group.id === "default" ? "我的收藏" : group.name], seen = new Set([group.id]);
  for (let parent = groups.find(item => item.id === group.parentId); parent && !seen.has(parent.id); parent = groups.find(item => item.id === parent.parentId)) {
    seen.add(parent.id); names.unshift(parent.id === "default" ? "我的收藏" : parent.name);
  }
  return names.join(" / ");
}
function sharedFavoriteRenderChecks(groups, selected = ["default"]) {
  document.getElementById("sharedFavoriteGroups").replaceChildren(...groups.map(group => {
    const label = document.createElement("label"); label.className = "favorite-group-check";
    const checkbox = document.createElement("input"); checkbox.type = "checkbox"; checkbox.value = group.id; checkbox.checked = selected.includes(group.id);
    const name = document.createElement("span"); name.textContent = sharedFavoriteGroupName(group, groups);
    label.append(checkbox, name); return label;
  }));
}
function sharedFavoriteEditorError(message = "") {
  const error = document.getElementById("sharedFavoriteError");
  error.textContent = message; error.hidden = !message;
}
async function sharedFavoriteEditorGroups(editor, selected) {
  const token = ++editor.request, section = editor.section;
  document.getElementById("sharedFavoriteSave").disabled = true;
  document.getElementById("sharedFavoriteCreateGroup").disabled = true;
  document.getElementById("sharedFavoriteGroups").replaceChildren();
  try {
    if (sharedFavoritesState.reconciled.get(`${editor.profile}:${section}`) !== true) await ensurePoolReconciled(editor.profile, section);
    if (sharedFavoritesState.editor !== editor || editor.profile !== currentProfile() || token !== editor.request || section !== editor.section) return;
    const data = await request(sharedFavoriteUrl(section, "", editor.profile));
    if (sharedFavoritesState.editor !== editor || editor.profile !== currentProfile() || token !== editor.request || section !== editor.section) return;
    editor.data = data;
    sharedFavoriteRenderChecks(data.groups || [], selected);
    sharedFavoriteEditorError();
    sharedFavoriteValidate();
    document.getElementById("sharedFavoriteCreateGroup").disabled = false;
  } catch (error) { if (sharedFavoritesState.editor === editor && token === editor.request) sharedFavoriteEditorError(error.message); }
}
function sharedFavoriteValidate() {
  const editor = sharedFavoritesState.editor;
  const save = document.getElementById("sharedFavoriteSave");
  save.disabled = !editor || !editor.data || editor.busy || !document.getElementById("sharedFavoriteText").value.trim();
  if (editor) {
    sharedFavoriteProtect(save, editor.section, true);
    if (editor.section === "prompt") save.setAttribute("data-storage-write", "prompt_pools");
    else save.removeAttribute("data-storage-write");
    sharedFavoriteProtect(document.getElementById("sharedFavoriteCreateGroup"), editor.section);
    document.getElementById("sharedFavoriteSection").disabled = editor.busy || editor.profile === "krea2";
  }
  if (typeof protectStorageControls === "function") protectStorageControls();
}
function sharedFavoriteOpen(workbench, { section = null, text = null, metadata = null } = {}) {
  const profile = currentProfile(), view = sharedFavoritesView(profile);
  section ||= profile === "krea2" ? "prompt" : view.section === "artist" ? "character" : view.section;
  const editor = { profile, section, workbench, request: 0, data: null, busy: false };
  sharedFavoritesState.editor = editor;
  document.getElementById("sharedFavoriteHeading").textContent = `收藏提示词 · ${profile === "krea2" ? "Krea 2" : "Anima"}`;
  const captured = text === null ? sharedFavoriteCapturedSource(workbench) : { text, metadata };
  const content = captured.text;
  document.getElementById("sharedFavoriteName").value = captured.metadata?.title || content.slice(0, 80);
  document.getElementById("sharedFavoriteSubtitle").value = captured.metadata?.subtitle || "";
  const textarea = document.getElementById("sharedFavoriteText"); textarea.value = content; textarea.readOnly = false;
  document.getElementById("sharedFavoriteTextHint").hidden = false;
  const category = document.getElementById("sharedFavoriteSection");
  category.replaceChildren(...(profile === "krea2" ? ["prompt"] : SECTIONS).map(value => new Option(value === "prompt" ? "自然语言" : SECTION_META[value]?.label || value, value)));
  category.value = section; category.disabled = profile === "krea2";
  document.getElementById("sharedFavoriteNewGroup").value = "";
  sharedFavoriteEditorError(); sharedFavoriteValidate();
  motionDialog(document.getElementById("sharedFavoriteDialog"), true);
  void sharedFavoriteEditorGroups(editor, view.groups[section] && view.groups[section] !== "__all__" ? [view.groups[section]] : ["default"]);
}
async function sharedFavoriteSave(event) {
  event.preventDefault();
  const editor = sharedFavoritesState.editor;
  if (!editor || editor.profile !== currentProfile() || editor.busy || !editor.data) return;
  const text = document.getElementById("sharedFavoriteText").value.trim(), title = document.getElementById("sharedFavoriteName").value.trim();
  const subtitle = document.getElementById("sharedFavoriteSubtitle").value.trim();
  const groupIds = [...document.querySelectorAll("#sharedFavoriteGroups input:checked")].map(input => input.value);
  if (!title || !text || !groupIds.length) { sharedFavoriteEditorError(!title ? "主标题不能为空" : !text ? "提示词内容不能为空" : "请选择至少一个收藏分组"); return; }
  editor.busy = true; sharedFavoriteValidate(); sharedFavoriteEditorError();
  try {
    const body = { title, subtitle, text, groupIds };
    await request(sharedFavoriteUrl(editor.section, "/text", editor.profile), { method: "POST", body: JSON.stringify(body) });
    if (sharedFavoritesState.editor !== editor || editor.profile !== currentProfile()) return;
    motionDialog(document.getElementById("sharedFavoriteDialog"), false); sharedFavoritesState.editor = null;
    toast("已收藏并加入词池");
  } catch (error) { if (sharedFavoritesState.editor === editor) sharedFavoriteEditorError(poolMutationError(error, { model_profile: editor.profile, section: editor.section })); }
  finally { editor.busy = false; if (sharedFavoritesState.editor === editor) sharedFavoriteValidate(); }
}
async function sharedFavoriteCreateGroup() {
  const editor = sharedFavoritesState.editor, name = document.getElementById("sharedFavoriteNewGroup").value.trim();
  if (!editor || !name || editor.profile !== currentProfile()) return;
  const section = editor.section, token = editor.request;
  document.getElementById("sharedFavoriteCreateGroup").disabled = true;
  try {
    const before = new Set((editor.data?.groups || []).map(group => group.id));
    const data = await request(sharedFavoriteUrl(section, "/groups", editor.profile), { method: "POST", body: JSON.stringify({ name }) });
    if (sharedFavoritesState.editor !== editor || editor.section !== section || editor.request !== token) return;
    const selected = [...document.querySelectorAll("#sharedFavoriteGroups input:checked")].map(input => input.value);
    selected.push(...(data.groups || []).filter(group => !before.has(group.id)).map(group => group.id));
    editor.data = data; sharedFavoriteRenderChecks(data.groups || [], selected); document.getElementById("sharedFavoriteNewGroup").value = ""; sharedFavoriteEditorError();
  } catch (error) { if (sharedFavoritesState.editor === editor && editor.section === section && editor.request === token) sharedFavoriteEditorError(error.message); }
  finally { if (sharedFavoritesState.editor === editor && editor.section === section && editor.request === token) document.getElementById("sharedFavoriteCreateGroup").disabled = false; }
}
function sharedFavoritesChanged(change = {}, broadcast = true) {
  const { model_profile = currentProfile(), section = sharedFavoritesView(model_profile).section, deletedItemIds = [], updatedItemIds = [], refresh = true } = change;
  const state = sharedFavoritesState;
  prunePoolSourceSelections(model_profile, section, deletedItemIds);
  if (broadcast) {
    try { localStorage.setItem(SHARED_FAVORITES_KEY, JSON.stringify({ model_profile, section, deletedItemIds, updatedItemIds, sourceChange: Boolean(change.sourceChange), nonce: `${Date.now()}-${Math.random()}` })); } catch { /* Same-page refresh remains available. */ }
  }
  if (model_profile !== currentProfile()) return;
  state.changeRevision++; state.request++;
  if (section === sharedFavoritesView(model_profile).section) { state.data = null; state.items = []; if (state.ready) sharedFavoritesRender(); }
  if (typeof favoritesRequest !== "undefined") favoritesRequest++;
  if (typeof customGroupsRequest !== "undefined") customGroupsRequest++;
  if (typeof poolRequest !== "undefined") poolRequest++;
  if (typeof poolSelectionOperation !== "undefined") poolSelectionOperation = null;
  if (typeof poolResultContext !== "undefined") poolResultContext = "";
  if (typeof ui !== "undefined" && ui.poolGrid) ui.poolGrid.inert = true;
  if (typeof favoritesData !== "undefined" && typeof activeSection !== "undefined" && activeSection === section) {
    favoritesData = { groups: [], items: [], favorite_keys: [] }; favoritesAvailable = false;
  }
  if (typeof poolSelectionStats !== "undefined") poolSelectionStats.delete(section);
  if (typeof poseItemCache !== "undefined") for (const id of [...deletedItemIds, ...updatedItemIds]) poseItemCache.delete(id);
  if (typeof invalidateKreaPoolSelection === "function") invalidateKreaPoolSelection();
  if (typeof kreaPoolView !== "undefined") { kreaPoolView.selectionSummary = null; kreaPoolView.resultContext = null; }
  if (!state.ready || !refresh) return;
  clearTimeout(state.refreshTimer);
  state.refreshTimer = setTimeout(() => { if (model_profile === currentProfile()) void refreshPoolLifecycle(); }, 0);
}
function sharedFavoriteManageGroups() {
  if (!sharedFavoritesState.data) return;
  sharedFavoritesState.groupEditor = { profile: currentProfile(), section: sharedFavoritesView().section, data: sharedFavoritesState.data, id: "", busy: false };
  sharedFavoriteRenderGroupManager();
  motionDialog(document.getElementById("sharedFavoriteGroupDialog"), true);
}
function sharedFavoriteRenderGroupManager() {
  const editor = sharedFavoritesState.groupEditor;
  if (!editor) return;
  const groups = editor.data.groups || [];
  document.getElementById("sharedFavoriteGroupList").replaceChildren(...groups.map(group => {
    const row = document.createElement("div"); row.className = "shared-favorite-group-row";
    const label = document.createElement("span"); label.textContent = sharedFavoriteGroupName(group, groups);
    const edit = sharedFavoriteButton("重命名", () => { editor.id = group.id; document.getElementById("sharedFavoriteGroupName").value = group.name; });
    const remove = sharedFavoriteButton("删除分组", () => void sharedFavoriteMutateGroup(group));
    sharedFavoriteProtect(remove, editor.section);
    edit.disabled = remove.disabled = Boolean(group.isSystem) || editor.busy;
    row.append(label, edit, remove); return row;
  }));
  document.getElementById("sharedFavoriteGroupName").value = "";
  document.getElementById("sharedFavoriteGroupError").textContent = "";
  document.getElementById("sharedFavoriteGroupSave").disabled = editor.busy;
  sharedFavoriteProtect(document.getElementById("sharedFavoriteGroupSave"), editor.section);
}
async function sharedFavoriteMutateGroup(remove = null) {
  const editor = sharedFavoritesState.groupEditor;
  if (!editor || editor.busy || editor.profile !== currentProfile()) return;
  const name = document.getElementById("sharedFavoriteGroupName").value.trim();
  if (!remove && !name) { document.getElementById("sharedFavoriteGroupError").textContent = "请输入分组名称"; return; }
  editor.busy = true;
  const id = remove?.id || editor.id;
  const suffix = id ? `/groups/${encodeURIComponent(id)}${remove ? "?deleteItems=false" : ""}` : "/groups";
  try {
    const data = await request(sharedFavoriteUrl(editor.section, suffix, editor.profile), { method: remove ? "DELETE" : id ? "PUT" : "POST", ...(!remove ? { body: JSON.stringify({ name }) } : {}) });
    if (sharedFavoritesState.groupEditor !== editor || editor.profile !== currentProfile()) return;
    editor.data = data; editor.id = ""; editor.busy = false; sharedFavoriteRenderGroupManager();
  } catch (error) { if (sharedFavoritesState.groupEditor === editor) document.getElementById("sharedFavoriteGroupError").textContent = error.message; }
  finally { editor.busy = false; if (sharedFavoritesState.groupEditor === editor) document.getElementById("sharedFavoriteGroupSave").disabled = false; }
}
function initializeSharedFavorites() {
  const state = sharedFavoritesState;
  if (state.ready) return;
  document.getElementById("poolRefreshRetry")?.addEventListener("click", () => void refreshPoolLifecycle());
  try { state.views = JSON.parse(localStorage.getItem(SHARED_FAVORITES_VIEW_KEY) || "{}") || {}; } catch { state.views = {}; }
  for (const prefix of ["single"]) {
    document.getElementById(`${prefix}FavoriteOpen`).addEventListener("click", () => { const panel = document.getElementById(`${prefix}FavoritePanel`); panel.hidden = !panel.hidden; if (!panel.hidden) void sharedFavoritesLoad(); });
    const save = document.getElementById(`${prefix}FavoriteSave`);
    let selectedSource = null;
    save.addEventListener("pointerdown", () => { selectedSource = sharedFavoriteCapturedSource(prefix); });
    save.addEventListener("pointercancel", () => { selectedSource = null; });
    save.addEventListener("pointerleave", () => { selectedSource = null; });
    save.addEventListener("click", () => { sharedFavoriteOpen(prefix, selectedSource || sharedFavoriteCapturedSource(prefix)); selectedSource = null; });
    document.getElementById(`${prefix}FavoriteRefresh`).addEventListener("click", sharedFavoritesLoad);
    document.getElementById(`${prefix}FavoriteManage`).addEventListener("click", sharedFavoriteManageGroups);
    document.getElementById(`${prefix}FavoriteGroup`).addEventListener("change", event => { const view = sharedFavoritesView(); view.groups[view.section] = event.target.value; sharedFavoritesRememberView(); sharedFavoritesRender(); });
  }
  for (const target of document.querySelectorAll("[data-single-prompt], #singleProcessedPrompt")) {
    for (const type of ["focus", "input", "select"]) target.addEventListener(type, () => {
      state.targets.single = target; state.saveTargets.single = target;
    });
  }
  for (const target of document.querySelectorAll("#rewriteResult, #reverseResult")) {
    for (const type of ["focus", "input", "select"]) target.addEventListener(type, () => { state.saveTargets.single = target; });
  }
  document.getElementById("sharedFavoriteForm").addEventListener("submit", sharedFavoriteSave);
  document.getElementById("sharedFavoriteText").addEventListener("input", sharedFavoriteValidate);
  document.getElementById("sharedFavoriteSection").addEventListener("change", event => {
    const editor = state.editor;
    if (!editor || editor.busy) return;
    editor.section = event.target.value; editor.data = null; void sharedFavoriteEditorGroups(editor);
  });
  document.getElementById("sharedFavoriteCreateGroup").addEventListener("click", sharedFavoriteCreateGroup);
  document.getElementById("sharedFavoriteGroupForm").addEventListener("submit", event => { event.preventDefault(); void sharedFavoriteMutateGroup(); });
  document.getElementById("sharedFavoriteGroupNew").addEventListener("click", () => { if (state.groupEditor) state.groupEditor.id = ""; document.getElementById("sharedFavoriteGroupName").value = ""; });
  for (const button of document.querySelectorAll("[data-close-shared-favorite]")) button.addEventListener("click", () => motionDialog(document.getElementById(button.dataset.closeSharedFavorite), false));
  document.getElementById("sharedFavoriteDialog").addEventListener("close", event => { if (!event.currentTarget.open) state.editor = null; });
  document.getElementById("sharedFavoriteGroupDialog").addEventListener("close", event => { if (!event.currentTarget.open) state.groupEditor = null; });
  window.addEventListener("storage", event => {
    if (event.key !== SHARED_FAVORITES_KEY || !event.newValue) return;
    try { const message = JSON.parse(event.newValue); if (message.model_profile && message.section) sharedFavoritesChanged(message, false); } catch { /* Ignore legacy/non-JSON notifications. */ }
  });
  window.addEventListener("focus", () => sharedFavoritesChanged({ model_profile: currentProfile() }, false));
  state.ready = true; sharedFavoritesResetModel();
}
