/* Model-owned favorites; both workbenches use the same records and controls. */
const SHARED_FAVORITES_KEY = "anima-studio:favorites-updated";
const SHARED_FAVORITES_VIEW_KEY = "anima-studio:favorite-views:v1";
const sharedFavoritesState = {
  ready: false, profile: "", epoch: 0, request: 0, data: null, items: [],
  views: {}, targets: {}, editor: null, groupEditor: null, refreshTimer: null,
};

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
    <p class="field-hint">点击名称插入最近编辑位置；加入随机池后可在随机工作台使用。</p>
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
  if (details) details.open = true;
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
  for (const prefix of ["single", "random"]) {
    const element = document.getElementById(`${prefix}FavoriteStatus`);
    if (element) element.textContent = message;
  }
}
function sharedFavoritesResetModel() {
  const state = sharedFavoritesState;
  if (!state.ready || state.profile === currentProfile()) return;
  state.profile = currentProfile();
  state.epoch++; state.request++; state.data = null; state.items = [];
  state.targets = {}; state.editor = null; state.groupEditor = null;
  for (const id of ["sharedFavoriteDialog", "sharedFavoriteGroupDialog", "favoriteDialog", "favoriteSelectionDialog", "groupDialog", "childGroupDialog", "deleteGroupDialog", "artistDialog"]) {
    const dialog = document.getElementById(id);
    if (dialog?.open) dialog.close();
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
async function sharedFavoritesLoad() {
  const state = sharedFavoritesState;
  if (!state.ready) return;
  if (state.profile !== currentProfile()) { sharedFavoritesResetModel(); return; }
  const profile = currentProfile(), section = sharedFavoritesView(profile).section, epoch = state.epoch, token = ++state.request;
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
  for (const prefix of ["single", "random"]) {
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
      const edit = sharedFavoriteButton("编辑", () => sharedFavoriteOpen(prefix, { item, saved: saved.get(String(item.favorite_key)), section }));
      edit.setAttribute("aria-label", `编辑收藏 ${item.title || item.name}`);
      const pool = sharedFavoriteButton("加入随机池", () => void sharedFavoriteAddToRandom(item, profile, section));
      pool.hidden = section === "artist";
      const remove = sharedFavoriteButton("♥", async () => {
        remove.disabled = true;
        try { await request(sharedFavoriteUrl(section, "/item", profile), { method: "PUT", body: JSON.stringify({ ...sharedFavoriteItemIdentity(item, section), favorite: false }) }); }
        catch (error) { toast(error.message); remove.disabled = false; }
      });
      remove.setAttribute("aria-label", `取消收藏 ${item.title || item.name}`);
      sharedFavoriteProtect(remove, section);
      row.append(insert, edit, pool, remove); return row;
    }));
  }
  sharedFavoritesStatus(data ? `${items.length} 项收藏 · ${profile === "krea2" ? "Krea 2" : "Anima"} 单图与随机工作台共享` : "暂无收藏数据。");
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
    sharedFavoriteProtect(save, editor.section, !editor.item);
    sharedFavoriteProtect(document.getElementById("sharedFavoriteCreateGroup"), editor.section);
    document.getElementById("sharedFavoriteSection").disabled = editor.busy || Boolean(editor.item) || editor.profile === "krea2";
  }
}
function sharedFavoriteOpen(workbench, { item = null, saved = null, section = null, text = null } = {}) {
  const profile = currentProfile(), view = sharedFavoritesView(profile);
  section ||= profile === "krea2" ? "prompt" : view.section === "artist" ? "character" : view.section;
  const editor = { profile, section, workbench, item, saved, request: 0, data: null, busy: false };
  sharedFavoritesState.editor = editor;
  document.getElementById("sharedFavoriteHeading").textContent = item ? "编辑收藏" : `收藏提示词 · ${profile === "krea2" ? "Krea 2" : "Anima"}`;
  const content = text ?? (item ? item.text : sharedFavoriteSource(sharedFavoriteTarget(workbench)));
  document.getElementById("sharedFavoriteName").value = item ? saved?.nickname || saved?.title || item.title || "" : content.slice(0, 80);
  const textarea = document.getElementById("sharedFavoriteText"); textarea.value = content; textarea.readOnly = Boolean(item);
  document.getElementById("sharedFavoriteTextHint").hidden = !item;
  const category = document.getElementById("sharedFavoriteSection");
  category.replaceChildren(...(profile === "krea2" ? ["prompt"] : item?.text && section === "artist" ? ["artist"] : SECTIONS).map(value => new Option(value === "prompt" ? "自然语言" : SECTION_META[value]?.label || "画师", value)));
  category.value = section; category.disabled = Boolean(item) || profile === "krea2";
  document.getElementById("sharedFavoriteNewGroup").value = "";
  sharedFavoriteEditorError(); sharedFavoriteValidate();
  document.getElementById("sharedFavoriteDialog").showModal();
  void sharedFavoriteEditorGroups(editor, saved?.groupIds || (view.groups[section] && view.groups[section] !== "__all__" ? [view.groups[section]] : ["default"]));
}
async function sharedFavoriteSave(event) {
  event.preventDefault();
  const editor = sharedFavoritesState.editor;
  if (!editor || editor.profile !== currentProfile() || editor.busy || !editor.data) return;
  const text = document.getElementById("sharedFavoriteText").value.trim(), title = document.getElementById("sharedFavoriteName").value.trim();
  const groupIds = [...document.querySelectorAll("#sharedFavoriteGroups input:checked")].map(input => input.value);
  if (!text || !groupIds.length) { sharedFavoriteEditorError(!text ? "提示词内容不能为空" : "请选择至少一个收藏分组"); return; }
  editor.busy = true; sharedFavoriteValidate(); sharedFavoriteEditorError();
  try {
    const suffix = editor.item ? "/item" : "/text";
    const body = editor.item ? { ...sharedFavoriteItemIdentity(editor.item, editor.section), favorite: true, nickname: title, groupIds } : { title, text, groupIds };
    await request(sharedFavoriteUrl(editor.section, suffix, editor.profile), { method: editor.item ? "PUT" : "POST", body: JSON.stringify(body) });
    if (sharedFavoritesState.editor !== editor || editor.profile !== currentProfile()) return;
    document.getElementById("sharedFavoriteDialog").close(); sharedFavoritesState.editor = null;
    toast(editor.item ? "收藏已更新" : "提示词已收藏");
  } catch (error) { if (sharedFavoritesState.editor === editor) sharedFavoriteEditorError(error.message); }
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
async function sharedFavoriteAddToRandom(item, profile, section) {
  if (currentProfile() !== profile) return;
  const epoch = sharedFavoritesState.epoch;
  try {
    let id = item.id;
    if (profile === "krea2") {
      const result = await request("/api/prompt-pools/items/ensure", { method: "POST", body: JSON.stringify({ model_profile: profile, text: item.text }) });
      if (currentProfile() !== profile || epoch !== sharedFavoritesState.epoch) return;
      id = result.id;
    }
    const single = typeof singleWorkbenchActive === "function" && singleWorkbenchActive();
    if (single) {
      const state = singleWorkbenchState;
      const snapshot = state.drafts.random[profile] ||= { settings: randomWorkbenchDefaultSettings(profile), profileDrafts: {} };
      const settings = snapshot.settings;
      if (profile === "krea2") {
        settings.krea_prompt_pool = { enabled: true, ids: [...new Set([...(settings.krea_prompt_pool?.ids || []), id])] };
        if (snapshot.profileDrafts?.[profile]) Object.assign(snapshot.profileDrafts[profile], { kreaPromptPoolEnabled: true, kreaPromptPoolSelection: settings.krea_prompt_pool.ids });
      } else {
        settings.pools ||= {}; settings.pools[section] = sharedFavoritePoolSelection(settings.pools[section], id);
        settings[`random_${section}`] = true;
        if (snapshot.profileDrafts?.[profile]) { snapshot.profileDrafts[profile].pools = structuredClone(settings.pools); snapshot.profileDrafts[profile].modes[section] = "pool"; }
      }
      singleWorkbenchPersist();
    } else if (profile === "krea2") {
      kreaPromptPoolSelection = [...new Set([...kreaPromptPoolSelection, id])];
      ui.krea_prompt_pool_enabled.checked = true;
      await loadKreaPromptPool({ force: true }); schedulePersist();
    } else {
      draft.pools[section] = sharedFavoritePoolSelection(draft.pools[section], id); draft.modes[section] = "pool";
      renderDimensions(); updatePoolOverview(); if (section === activeSection) renderPoolItems(); schedulePersist();
    }
    if (currentProfile() === profile && epoch === sharedFavoritesState.epoch) toast("已加入随机工作台的随机池");
  } catch (error) { if (currentProfile() === profile && epoch === sharedFavoritesState.epoch) toast(error.message); }
}
function sharedFavoritePoolSelection(selection = {}, id) {
  return selection.mode === "all" ? { ...selection, excluded_ids: (selection.excluded_ids || []).filter(value => value !== id) }
    : { ...selection, mode: "include", ids: [...new Set([...(selection.ids || []), id])], excluded_ids: selection.excluded_ids || [] };
}
function sharedFavoritesChanged({ model_profile = currentProfile(), section = sharedFavoritesView(model_profile).section } = {}, broadcast = true) {
  if (!sharedFavoritesState.ready) return;
  if (broadcast) {
    try { localStorage.setItem(SHARED_FAVORITES_KEY, JSON.stringify({ model_profile, section, nonce: `${Date.now()}-${Math.random()}` })); } catch { /* Same-page refresh remains available. */ }
  }
  if (model_profile !== currentProfile()) return;
  clearTimeout(sharedFavoritesState.refreshTimer);
  sharedFavoritesState.refreshTimer = setTimeout(() => {
    if (model_profile !== currentProfile()) return;
    void sharedFavoritesLoad();
    if (model_profile === "anima") {
      void loadArtistFavorites();
      if (ui.poolDrawer.classList.contains("open")) void loadFavorites(activeSection).then(() => {
        if (currentProfile() === model_profile) { renderPoolSidebar(); void loadPool(); }
      });
    }
  }, 0);
}
function sharedFavoriteManageGroups() {
  if (!sharedFavoritesState.data) return;
  sharedFavoritesState.groupEditor = { profile: currentProfile(), section: sharedFavoritesView().section, data: sharedFavoritesState.data, id: "", busy: false };
  sharedFavoriteRenderGroupManager();
  document.getElementById("sharedFavoriteGroupDialog").showModal();
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
  try { state.views = JSON.parse(localStorage.getItem(SHARED_FAVORITES_VIEW_KEY) || "{}") || {}; } catch { state.views = {}; }
  const random = document.getElementById("randomSharedFavorites");
  random.innerHTML = '<div class="single-prompt-tools"><button id="randomFavoriteOpen" type="button" class="button ghost compact">♡ 共享收藏</button><button id="randomFavoriteSave" type="button" class="button ghost compact">收藏提示词</button></div>' + sharedFavoritesPanelMarkup("random");
  for (const prefix of ["single", "random"]) {
    document.getElementById(`${prefix}FavoriteOpen`).addEventListener("click", () => { const panel = document.getElementById(`${prefix}FavoritePanel`); panel.hidden = !panel.hidden; if (!panel.hidden) void sharedFavoritesLoad(); });
    const save = document.getElementById(`${prefix}FavoriteSave`);
    let selectedSource = null;
    save.addEventListener("pointerdown", () => { selectedSource = sharedFavoriteSource(sharedFavoriteTarget(prefix)); });
    save.addEventListener("pointercancel", () => { selectedSource = null; });
    save.addEventListener("pointerleave", () => { selectedSource = null; });
    save.addEventListener("click", () => { sharedFavoriteOpen(prefix, { text: selectedSource }); selectedSource = null; });
    document.getElementById(`${prefix}FavoriteRefresh`).addEventListener("click", sharedFavoritesLoad);
    document.getElementById(`${prefix}FavoriteManage`).addEventListener("click", sharedFavoriteManageGroups);
    document.getElementById(`${prefix}FavoriteGroup`).addEventListener("change", event => { const view = sharedFavoritesView(); view.groups[view.section] = event.target.value; sharedFavoritesRememberView(); sharedFavoritesRender(); });
  }
  for (const target of document.querySelectorAll("[data-single-prompt], #singleProcessedPrompt, #quality_prompt, #extra_prompt, #negative_prompt, #manual_artist")) {
    const workbench = target.id.startsWith("single") ? "single" : "random";
    for (const type of ["focus", "input", "select"]) target.addEventListener(type, () => state.targets[workbench] = target);
  }
  document.addEventListener("focusin", event => { if (event.target.matches?.("#dimensionList [data-fixed]")) state.targets.random = event.target; });
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
  for (const button of document.querySelectorAll("[data-close-shared-favorite]")) button.addEventListener("click", () => document.getElementById(button.dataset.closeSharedFavorite).close());
  document.getElementById("sharedFavoriteDialog").addEventListener("close", () => { state.editor = null; });
  document.getElementById("sharedFavoriteGroupDialog").addEventListener("close", () => { state.groupEditor = null; });
  window.addEventListener("storage", event => {
    if (event.key !== SHARED_FAVORITES_KEY || !event.newValue) return;
    try { const message = JSON.parse(event.newValue); if (message.model_profile && message.section) sharedFavoritesChanged(message, false); } catch { /* Ignore legacy/non-JSON notifications. */ }
  });
  window.addEventListener("focus", () => sharedFavoritesChanged({ model_profile: currentProfile() }, false));
  state.ready = true; sharedFavoritesResetModel();
}
