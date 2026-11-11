/* Style library. Preset content, editor sessions and committed writes have separate owners. */
(() => {
  "use strict";
  const INCLUDE = Object.freeze({ size: false, repair: false, img2img: false, affixes: false });
  const includeLabels = { size: "尺寸", repair: "放大修复", img2img: "图生图去噪与裁切", affixes: "单图前后缀" };
  const copy = value => JSON.parse(JSON.stringify(value));
  const uniqueWords = value => {
    const seen = new Set();
    return String(value || "").split(/[,，\n]/).map(word => word.trim()).filter(word => {
      const key = word.toLocaleLowerCase();
      if (!word || seen.has(key)) return false;
      seen.add(key); return true;
    });
  };
  const normalizedInclude = value => Object.fromEntries(Object.keys(INCLUDE).map(key => [key, value?.[key] === true]));
  function editorDraft(mode, item) {
    return { name: item?.name || "", favorite: !!item?.favorite, groups: [...item?.groups || []], tags: (item?.tags || []).join(", "), cover_id: item?.cover_id || null,
      include: mode === "create" ? { ...INCLUDE } : normalizedInclude(item?.include) };
  }
  function filterItems(items, view, groups = []) {
    const terms = String(view.query || "").trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
    const selectedTags = (view.tags || []).map(tag => tag.toLocaleLowerCase());
    const names = new Map(groups.map(group => [group.id, group.name]));
    const filtered = items.filter(item => {
      if (view.collection === "favorite" && !item.favorite) return false;
      if (view.collection === "ungrouped" && item.groups?.length) return false;
      if (view.collection?.startsWith("group:") && (!item.favorite || !item.groups?.includes(view.collection.slice(6)))) return false;
      const tags = (item.tags || []).map(tag => tag.toLocaleLowerCase());
      if (!selectedTags.every(tag => tags.includes(tag))) return false;
      const settings = item.settings || {};
      const haystack = [item.name, ...item.tags || [], ...(item.groups || []).map(id => names.get(id) || ""),
        settings.model_name, settings.clip_name, settings.vae_name, settings.model_profile, settings.sampler_name, settings.scheduler,
        ...[...(settings.loras || []), ...(settings.single?.loras_secondary || [])].map(lora => lora.filename || lora.name || lora.lora_name || "")].join(" ").toLocaleLowerCase();
      return terms.every(term => haystack.includes(term));
    });
    return filtered.sort((a, b) => {
      if (view.sort === "name") return a.name.localeCompare(b.name, "zh-CN", { numeric: true });
      if (view.sort === "created") return String(b.created_at).localeCompare(String(a.created_at));
      if (view.sort === "favorite" && !!a.favorite !== !!b.favorite) return Number(b.favorite) - Number(a.favorite);
      return String(b.updated_at).localeCompare(String(a.updated_at)) || a.name.localeCompare(b.name, "zh-CN");
    });
  }
  function editorPayload(session) {
    const draft = session.draft;
    const payload = { model_profile: session.profile, name: draft.name.trim(), favorite: !!draft.favorite,
      groups: [...draft.groups], tags: uniqueWords(draft.tags), cover_id: draft.cover_id || null };
    if (session.mode !== "edit") {
      payload.settings = copy(session.snapshot);
      delete payload.settings.resource_replacements;
      payload.include = normalizedInclude(draft.include);
    }
    return payload;
  }
  function cropRectangle(width, height, zoom = 1, cx = .5, cy = .5) {
    const ratio = width / height, target = 4 / 3, safeZoom = Math.max(1, Math.min(4, zoom));
    const w = Math.min(1, target / ratio) / safeZoom, h = Math.min(1, ratio / target) / safeZoom;
    const x = Math.max(0, Math.min(1 - w, cx - w / 2));
    const y = Math.max(0, Math.min(1 - h, cy - h / 2));
    return { x, y, width: w, height: h };
  }
  // Uploads are preparation: a dismissed editor must not start a later preset write.
  // Once write() starts, it is never aborted by navigation, and committed() always runs.
  async function saveSession(session, services) {
    if (session.busy) return { ignored: true };
    session.busy = true;
    const payload = editorPayload(session), file = session.file, crop = copy(session.crop);
    try {
      if (file) {
        const cover = await services.upload(file, crop);
        if (!services.current(session)) return { abandoned: true };
        payload.cover_id = cover.id;
      }
      if (!services.current(session)) return { abandoned: true };
      const result = await services.write(payload);
      if (services.current(session)) services.success(result);
      await services.committed(result);
      return { committed: true, result };
    } catch (error) {
      if (services.current(session)) services.failure(error);
      else services.detachedFailure?.(error);
      return { error };
    } finally { session.busy = false; services.settled?.(session); }
  }
  const helpers = { INCLUDE, uniqueWords, normalizedInclude, editorDraft, filterItems, editorPayload, cropRectangle, saveSession };
  if (typeof module !== "undefined" && module.exports) module.exports = helpers;
  if (typeof document === "undefined") return;

  const state = { epoch: 0, profile: "", items: [], groups: [], query: "", collection: "all", tags: [], sort: "favorite",
    selected: new Set(), selecting: false, read: 0, abort: null, busy: new Set(), batchWrites: new Map(), editor: null, small: null, confirmation: null, loading: false, filtersOpen: false, managerVisible: false };
  const ui = {};
  const profile = () => typeof currentProfile === "function" ? currentProfile() : "anima";
  const profileText = value => value === "krea2" ? "Krea 2" : "Anima";
  const notify = message => { if (typeof toast === "function") toast(message); };
  const writable = () => !(typeof config !== "undefined" && config.storage_status?.style_presets?.read_only);
  const h = (tag, className, text) => { const node = document.createElement(tag); if (className) node.className = className; if (text !== undefined) node.textContent = text; return node; };
  const button = (text, className, action, label) => { const node = h("button", className, text); node.type = "button"; if (label) node.setAttribute("aria-label", label); if (action) node.addEventListener("click", action); return node; };
  const icon = (name) => {
    const paths = { close: '<path d="m6 6 12 12M18 6 6 18"/>', star: '<path d="m12 3 2.8 5.8 6.4.9-4.6 4.5 1.1 6.3-5.7-3-5.7 3 1.1-6.3-4.6-4.5 6.4-.9Z"/>',
      image: '<rect x="3" y="3" width="18" height="18" rx="4"/><circle cx="8" cy="8" r="1.5"/><path d="m3 17 5-5 4 4 4-6 5 7"/>', plus: '<path d="M12 5v14M5 12h14"/>', edit: '<path d="m15 4 5 5M4 20l5-1L21 7a2 2 0 0 0-4-4L5 15Z"/>', search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 5 5"/>' };
    const node = h("span", "spm-icon"); node.setAttribute("aria-hidden", "true");
    node.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">${paths[name] || paths.image}</svg>`;
    return node;
  };
  function show(dialog, open, options = {}) {
    if (globalThis.StudioMotion) return StudioMotion.dialog(dialog, open, { response: .32, damping: 1, ...options });
    if (open && !dialog.open) dialog.showModal(); else if (!open && dialog.open) dialog.close();
  }
  function makeDialog(id, className, title, onClose) {
    const dialog = h("dialog", `spm-dialog ${className}`); dialog.id = id;
    dialog.setAttribute("aria-labelledby", `${id}Title`);
    const heading = h("header", "spm-heading"), text = h("div", "spm-heading-copy");
    const name = h("h2", "", title); name.id = `${id}Title`; text.append(name);
    const close = button("", "spm-icon-button", onClose, `关闭${title}`); close.append(icon("close"));
    heading.append(text, close); dialog.append(heading); document.body.append(dialog);
    let outsideStart = false;
    const outside = event => { const rect = dialog.getBoundingClientRect(); return event.target === dialog && (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom); };
    dialog.addEventListener("cancel", event => { event.preventDefault(); event.stopImmediatePropagation(); onClose(); }, true);
    dialog.addEventListener("pointerdown", event => { outsideStart = outside(event); }, true);
    dialog.addEventListener("click", event => { if (outsideStart && outside(event)) { event.preventDefault(); event.stopImmediatePropagation(); onClose(); } outsideStart = false; }, true);
    return { dialog, heading, title: name, copy: text };
  }
  function error(node, message = "") { node.textContent = message; node.hidden = !message; }
  function ensure() {
    if (ui.manager) return;
    const shell = makeDialog("stylePresetManager", "spm-manager", "风格预设", closeManager);
    ui.manager = shell.dialog; ui.managerHeading = shell.heading;
    ui.subtitle = h("p", "spm-subtitle"); shell.copy.append(ui.subtitle);
    const createButton = button("新建预设", "button primary compact", () => create()); createButton.id = "stylePresetNew"; ui.newButton = createButton;
    shell.heading.insertBefore(createButton, shell.heading.lastChild);
    const toolbar = h("div", "spm-toolbar"), search = h("label", "spm-search");
    ui.search = h("input"); ui.search.id = "stylePresetSearch"; ui.search.type = "search"; ui.search.placeholder = "查找名称、标签、模型或 LoRA"; ui.search.setAttribute("aria-label", "查找风格预设");
    ui.search.addEventListener("input", () => { state.query = ui.search.value; renderGrid(); }); search.append(icon("search"), ui.search);
    ui.sort = h("select"); ui.sort.id = "stylePresetSort"; ui.sort.setAttribute("aria-label", "预设排序");
    for (const [value, text] of [["favorite", "收藏优先"], ["updated", "最近更新"], ["created", "最近创建"], ["name", "名称"]]) ui.sort.append(new Option(text, value));
    ui.sort.addEventListener("change", () => { state.sort = ui.sort.value; renderGrid(); });
    ui.selectToggle = button("选择", "button ghost compact", () => { state.selecting = !state.selecting; state.selected.clear(); renderGrid(); }); ui.selectToggle.id = "stylePresetSelect";
    ui.filterToggle = button("筛选", "button ghost compact spm-mobile-filters", () => { state.filtersOpen = !state.filtersOpen; syncMobileFilters(); });
    ui.filterToggle.id = "stylePresetFiltersToggle"; ui.filterToggle.setAttribute("aria-controls", "stylePresetFilters"); ui.filterToggle.setAttribute("aria-expanded", "false");
    toolbar.append(search, ui.sort, ui.filterToggle, ui.selectToggle); ui.manager.append(toolbar);
    const body = h("div", "spm-library"); ui.sidebar = h("nav", "spm-sidebar"); ui.sidebar.id = "stylePresetFilters"; ui.sidebar.setAttribute("aria-label", "预设收藏与标签");
    const content = h("section", "spm-content"); ui.notice = h("div", "spm-error"); ui.notice.setAttribute("role", "status"); ui.notice.hidden = true;
    ui.results = h("p", "spm-results"); ui.results.setAttribute("aria-live", "polite");
    ui.grid = h("div", "spm-grid"); ui.grid.id = "stylePresetGrid";
    ui.scroll = h("div", "spm-scroll"); ui.scroll.append(ui.grid); content.append(ui.notice, ui.results, ui.scroll); body.append(ui.sidebar, content); ui.manager.append(body);
    ui.batchbar = h("footer", "spm-batchbar"); ui.batchbar.hidden = true; ui.manager.append(ui.batchbar);
    const editor = makeDialog("stylePresetEditor", "spm-editor", "新建风格预设", () => void closeEditor());
    ui.editor = editor.dialog; ui.editorTitle = editor.title; ui.editorBody = h("div", "spm-editor-body"); ui.editor.append(ui.editorBody);
    ui.editorError = h("p", "spm-error"); ui.editorError.setAttribute("role", "status"); ui.editorError.hidden = true;
    ui.editorSave = button("保存预设", "button primary", () => void submitEditor()); ui.editorSave.id = "stylePresetSave";
    const editorFooter = h("footer", "spm-footer"); editorFooter.append(ui.editorError, button("取消", "button ghost", () => void closeEditor()), ui.editorSave); ui.editor.append(editorFooter);
    ui.editor.addEventListener("keydown", event => { if ((event.ctrlKey || event.metaKey) && event.key === "Enter") { event.preventDefault(); void submitEditor(); } });
    const small = makeDialog("stylePresetOrganize", "spm-small", "整理预设", () => void closeSmall()); ui.small = small.dialog; ui.smallTitle = small.title;
    ui.smallBody = h("div", "spm-small-body"); ui.smallError = h("p", "spm-error"); ui.smallError.setAttribute("role", "status"); ui.smallError.hidden = true;
    ui.smallSave = button("保存", "button primary", () => void submitSmall()); ui.smallSave.id = "stylePresetOrganizeSave";
    const smallFooter = h("footer", "spm-footer"); smallFooter.append(ui.smallError, button("取消", "button ghost", () => void closeSmall()), ui.smallSave); ui.small.append(ui.smallBody, smallFooter);
    const confirmShell = makeDialog("stylePresetConfirm", "spm-confirm", "确认操作", () => settleConfirm(false)); ui.confirm = confirmShell.dialog; ui.confirmTitle = confirmShell.title;
    ui.confirmMessage = h("p", "spm-confirm-message"); ui.confirmAccept = button("确认", "button primary", () => settleConfirm(true)); ui.confirmAccept.id = "stylePresetConfirmAccept";
    ui.confirmCancel = button("取消", "button ghost", () => settleConfirm(false)); const confirmFooter = h("footer", "spm-footer"); confirmFooter.append(ui.confirmCancel, ui.confirmAccept); ui.confirm.append(ui.confirmMessage, confirmFooter);
    ui.mobileQuery = matchMedia("(max-width: 720px)"); ui.mobileQuery.addEventListener("change", () => syncMobileFilters(true)); syncMobileFilters(true);
    ui.manager.addEventListener("pointerdown", event => {
      if (ui.mobileQuery.matches && state.filtersOpen && !ui.sidebar.contains(event.target) && !ui.filterToggle.contains(event.target)) { state.filtersOpen = false; syncMobileFilters(); }
    });
  }
  function syncMobileFilters(immediate = false) {
    if (!ui.sidebar) return;
    const visible = !ui.mobileQuery.matches || state.filtersOpen;
    ui.filterToggle.setAttribute("aria-expanded", String(ui.mobileQuery.matches && state.filtersOpen));
    if (globalThis.StudioMotion) StudioMotion.presence(ui.sidebar, visible, { from: { opacity: 0, y: -6, scale: .985 }, response: .28, damping: 1, immediate: immediate || !ui.mobileQuery.matches });
    else { ui.sidebar.hidden = !visible; ui.sidebar.inert = !visible; }
  }
  function confirmAction({ title = "确认操作", message = "", confirmLabel = "确认", danger = false } = {}) {
    ensure(); if (state.confirmation) settleConfirm(false, true);
    ui.confirmTitle.textContent = title; ui.confirmMessage.textContent = message; ui.confirmAccept.textContent = confirmLabel;
    ui.confirmAccept.className = `button ${danger ? "danger" : "primary"}`;
    return new Promise(resolve => { state.confirmation = { resolve }; show(ui.confirm, true); ui.confirmCancel.focus({ preventScroll: true }); });
  }
  function settleConfirm(value, immediate = false) { const current = state.confirmation; state.confirmation = null; if (ui.confirm) show(ui.confirm, false, { immediate }); current?.resolve(value); }
  function closeManager() { if (!ui.manager) return; state.managerVisible = false; state.read++; state.abort?.abort(); show(ui.manager, false); }
  function currentSession(session) { return state.editor === session && state.epoch === session.epoch && profile() === session.profile; }
  function currentSmall(session) { return state.small === session && state.epoch === session.epoch && profile() === session.profile; }
  function contextChanged() {
    state.epoch++; state.read++; state.abort?.abort(); state.profile = profile(); state.managerVisible = false;
    state.items = []; state.groups = []; state.selected.clear(); state.selecting = false; state.collection = "all"; state.tags = []; state.query = ""; state.filtersOpen = false;
    discardEditor(true); discardSmall(true); settleConfirm(false, true);
    if (ui.manager) { show(ui.manager, false, { immediate: true, restoreFocus: false }); ui.search.value = ""; syncMobileFilters(true); render(); }
  }
  async function refresh() {
    if (!ui.manager || (!ui.manager.open && !ui.editor.open && !ui.small.open)) return;
    const requestedProfile = profile();
    if (state.profile && state.profile !== requestedProfile) { contextChanged(); return; }
    state.profile = requestedProfile; const epoch = state.epoch, serial = ++state.read;
    state.abort?.abort(); state.abort = new AbortController(); state.loading = true;
    if (!state.items.length) renderGrid(); error(ui.notice);
    try {
      const data = await request(`/api/style-presets?model_profile=${encodeURIComponent(requestedProfile)}`, { signal: state.abort.signal });
      if (serial !== state.read || epoch !== state.epoch || requestedProfile !== profile()) return;
      state.items = data.items || []; state.groups = data.groups || []; state.loading = false;
      const ids = new Set(state.items.map(item => item.id)); state.selected = new Set([...state.selected].filter(id => ids.has(id)));
      if (state.collection.startsWith("group:") && !state.groups.some(group => group.id === state.collection.slice(6))) state.collection = "favorite";
      render();
      if (state.editor) { state.editor.draft.groups = state.editor.draft.groups.filter(id => state.groups.some(group => group.id === id)); renderEditorGroups(state.editor); }
    } catch (failure) {
      if (serial !== state.read || epoch !== state.epoch || failure.name === "AbortError") return;
      state.loading = false; error(ui.notice, failure.message); renderGrid();
    }
  }
  async function open() {
    ensure();
    if (state.editor) { await closeEditor(); if (state.editor) return; }
    if (state.small) { await closeSmall(); if (state.small) return; }
    if (state.profile !== profile()) contextChanged(); state.profile = profile();
    if (!ui.manager.open || globalThis.StudioMotion?.target(ui.manager) === false) state.epoch++;
    state.managerVisible = true; show(ui.manager, true); ui.search.focus({ preventScroll: true }); await refresh();
  }
  function render() { renderSidebar(); renderGrid(); renderProtection(); }
  function renderProtection() {
    if (!ui.manager) return;
    const protectedStore = !writable(); ui.newButton.disabled = protectedStore;
    if (protectedStore) error(ui.notice, `风格预设目前为只读，可继续查看和应用。${typeof config !== "undefined" ? config.storage_status?.style_presets?.reason || "" : ""}`);
    ui.sidebar.querySelectorAll(".spm-sidebar-heading button, .spm-group-edit").forEach(node => { node.disabled = protectedStore; });
    if (state.editor) setEditorBusy(state.editor);
  }
  function renderSidebar() {
    if (!ui.sidebar) return; const focusKey = ui.sidebar.contains(document.activeElement) ? document.activeElement.dataset.focusKey : null; ui.sidebar.replaceChildren();
    const addCollection = (id, name, count) => {
      const node = button("", "spm-nav", () => { state.collection = id; render(); }); node.dataset.collection = id; node.dataset.focusKey = `collection:${id}`;
      node.setAttribute("aria-current", state.collection === id ? "page" : "false"); node.append(h("span", "", name), h("small", "", String(count))); ui.sidebar.append(node); return node;
    };
    addCollection("all", "全部预设", state.items.length); addCollection("favorite", "已收藏", state.items.filter(item => item.favorite).length);
    addCollection("ungrouped", "未分组", filterItems(state.items, { collection: "ungrouped" }).length);
    const groupsHeading = h("div", "spm-sidebar-heading"), createGroup = button("＋", "spm-text-button", () => openGroup(), "新建收藏分组"); createGroup.dataset.focusKey = "group:new"; groupsHeading.append(h("strong", "", "收藏分组"), createGroup); ui.sidebar.append(groupsHeading);
    for (const group of state.groups) {
      const row = h("div", "spm-group-row"), nav = button(group.name, "spm-nav", () => { state.collection = `group:${group.id}`; render(); });
      nav.setAttribute("aria-current", state.collection === `group:${group.id}` ? "page" : "false"); nav.title = group.name; nav.dataset.focusKey = `group:view:${group.id}`;
      const edit = button("", "spm-icon-button spm-group-edit", () => openGroup(group), `编辑分组 ${group.name}`); edit.dataset.focusKey = `group:edit:${group.id}`; edit.append(icon("edit")); row.append(nav, edit); ui.sidebar.append(row);
    }
    if (!state.groups.length) ui.sidebar.append(h("p", "spm-muted", "创建分组，让常用风格各就其位。"));
    const tags = [...new Set(state.items.flatMap(item => item.tags || []))].sort((a, b) => a.localeCompare(b, "zh-CN"));
    if (tags.length) {
      const heading = h("div", "spm-sidebar-heading"); heading.append(h("strong", "", "标签"));
      if (state.tags.length) heading.append(button("清除", "spm-text-button", () => { state.tags = []; render(); }));
      const list = h("div", "spm-tags");
      for (const tag of tags) { const node = button(tag, "spm-tag", () => { state.tags = state.tags.includes(tag) ? state.tags.filter(value => value !== tag) : [...state.tags, tag]; render(); }); node.dataset.focusKey = `tag:${tag}`; node.setAttribute("aria-pressed", String(state.tags.includes(tag))); list.append(node); }
      ui.sidebar.append(heading, list);
    }
    if (focusKey) [...ui.sidebar.querySelectorAll("[data-focus-key]")].find(node => node.dataset.focusKey === focusKey)?.focus({ preventScroll: true });
  }
  function summary(item) {
    const settings = item.settings || {}, loras = (settings.loras || []).filter(lora => lora.enabled !== false);
    const parts = [settings.steps ? `${settings.steps} 步` : "", settings.cfg !== undefined ? `CFG ${settings.cfg}` : "", loras.length ? `${loras.length} LoRA` : ""].filter(Boolean);
    return parts.join(" · ") || "风格参数";
  }
  function imageNode(item, className) {
    const wrap = h("div", className);
    if (item.cover_url && /^\/api\/style-preset-covers\//.test(item.cover_url)) {
      const image = h("img"); image.src = item.cover_url; image.alt = ""; image.loading = "lazy"; image.decoding = "async";
      image.addEventListener("error", () => { image.remove(); wrap.append(icon("image")); }, { once: true }); wrap.append(image);
    } else wrap.append(icon("image"));
    return wrap;
  }
  function renderGrid() {
    if (!ui.grid) return;
    const items = filterItems(state.items, state, state.groups), focused = document.activeElement;
    const focusId = focused?.dataset?.focusKey; ui.grid.replaceChildren();
    ui.subtitle.textContent = `${profileText(state.profile || profile())} · ${state.items.length} 个预设`;
    ui.results.textContent = state.loading && !state.items.length ? "正在读取预设…" : `${items.length} 个预设${state.tags.length > 1 ? " · 同时匹配所选标签" : ""}`;
    ui.selectToggle.textContent = state.selecting ? "完成选择" : "选择"; ui.selectToggle.setAttribute("aria-pressed", String(state.selecting));
    if (!items.length) {
      const empty = h("div", "spm-empty"); empty.append(icon("image"), h("h3", "", state.loading ? "正在读取预设" : state.items.length ? "没有匹配的预设" : "保存你的第一种风格"),
        h("p", "", state.loading ? "" : state.items.length ? "试试其他关键词，或清除筛选。" : "保留风格参数，应用时继续使用当前画面描述。"));
      if (!state.loading) empty.append(button(state.items.length ? "清除筛选" : "新建预设", "button secondary compact", () => {
        if (!state.items.length) { create(); return; } state.query = ""; state.tags = []; state.collection = "all"; ui.search.value = ""; render();
      })); ui.grid.append(empty);
    }
    for (const item of items) {
      const card = h("article", `spm-card${state.selected.has(item.id) ? " is-selected" : ""}`); card.dataset.presetId = item.id;
      const primary = button("", "spm-card-open", () => {
        if (state.selecting) { state.selected.has(item.id) ? state.selected.delete(item.id) : state.selected.add(item.id); renderGrid(); }
        else edit(item);
      }, `${state.selecting ? "选择" : "查看"} ${item.name}`); primary.dataset.focusKey = `open:${item.id}`;
      primary.append(imageNode(item, "spm-card-cover"), h("strong", "spm-card-name", item.name), h("span", "spm-card-summary", summary(item)));
      const labels = Object.entries(normalizedInclude(item.include)).filter(([, yes]) => yes).map(([key]) => includeLabels[key]);
      primary.append(h("small", "spm-card-includes", labels.length ? labels.join("、") : "仅风格 · 其余使用当前设置")); card.append(primary);
      if (state.selecting) { const selected = h("span", "spm-selection-indicator", state.selected.has(item.id) ? "✓" : ""); selected.setAttribute("aria-hidden", "true"); card.append(selected); }
      else {
        const actions = h("div", "spm-card-actions"), star = button("", "spm-icon-button spm-star", () => void toggleFavorite(item), `${item.favorite ? "取消收藏" : "收藏"} ${item.name}`);
        star.append(icon("star")); star.setAttribute("aria-pressed", String(!!item.favorite)); star.dataset.focusKey = `star:${item.id}`; star.disabled = state.busy.has(item.id) || !writable();
        const apply = button("应用", "spm-apply", async () => {
          const epoch = state.epoch, forProfile = state.profile;
          const isCurrent = () => state.managerVisible && state.epoch === epoch && forProfile === profile();
          if (typeof applyStylePreset === "function" && await applyStylePreset(item, { isCurrent }) !== false && isCurrent()) closeManager();
        }); apply.dataset.focusKey = `apply:${item.id}`;
        actions.append(star, apply); card.append(actions);
      }
      ui.grid.append(card);
    }
    if (focusId) [...ui.grid.querySelectorAll("[data-focus-key]")].find(node => node.dataset.focusKey === focusId)?.focus({ preventScroll: true });
    renderBatchbar(items);
  }
  function renderBatchbar(items) {
    ui.batchbar.hidden = !state.selecting; ui.batchbar.replaceChildren(); if (!state.selecting) return;
    ui.batchbar.append(h("strong", "", `已选择 ${state.selected.size} 项`), button("选择当前结果", "spm-text-button", () => { items.forEach(item => state.selected.add(item.id)); renderGrid(); }), button("清空", "spm-text-button", () => { state.selected.clear(); renderGrid(); }));
    for (const [action, label] of [["favorite", "收藏"], ["unfavorite", "取消收藏"], ["groups", "整理分组"], ["tags", "编辑标签"]]) {
      const node = button(label, "button ghost compact", () => openBatch(action)); node.disabled = !state.selected.size || !writable() || state.batchWrites.has(profile()); node.dataset.batchAction = action; ui.batchbar.append(node);
    }
  }
  async function changed(forProfile, message) {
    notify(`${profileText(forProfile)}：${message}`);
    if (forProfile !== profile()) return;
    try { if (typeof loadStylePresets === "function") await loadStylePresets(); await refresh(); }
    catch (failure) { if (ui.notice) error(ui.notice, `更改已保存，列表刷新失败：${failure.message}`); }
  }
  async function toggleFavorite(item) {
    if (state.busy.has(item.id) || !writable()) return; const forProfile = state.profile, epoch = state.epoch, old = !!item.favorite;
    state.read++; state.abort?.abort(); state.loading = false;
    state.busy.add(item.id); item.favorite = !old; render();
    try { await request(`/api/style-presets/${encodeURIComponent(item.id)}`, { method: "PUT", body: JSON.stringify({ model_profile: forProfile, favorite: !old }) }); await changed(forProfile, !old ? "已加入收藏" : "已取消收藏"); }
    catch (failure) { if (epoch === state.epoch && forProfile === profile()) { item.favorite = old; error(ui.notice, failure.message); } else notify(`${profileText(forProfile)}：${failure.message}`); }
    finally { state.busy.delete(item.id); if (epoch === state.epoch) render(); }
  }
  function field(label, control, hint) { const node = h("label", "spm-field"); node.append(h("span", "", label), control); if (hint) node.append(h("small", "spm-muted", hint)); return node; }
  function textInput(value, update, id, maxLength = 100) { const input = h("input"); input.type = "text"; input.value = value || ""; input.maxLength = maxLength; if (id) input.id = id; input.addEventListener("input", () => update(input.value)); return input; }
  function editorDirty(session) { return JSON.stringify(session.draft) !== session.initial || !!session.file || !!session.fileLoading; }
  function revoke(session) { if (session?.objectUrl) URL.revokeObjectURL(session.objectUrl); session?.resize?.disconnect(); }
  function discardEditor(immediate = false) { const old = state.editor; state.editor = null; revoke(old); if (ui.editor) show(ui.editor, false, { immediate, restoreFocus: !immediate }); }
  async function closeEditor() {
    const session = state.editor; if (!session) return;
    if (!session.busy && editorDirty(session) && !await confirmAction({ title: "放弃未保存的更改？", message: "名称、封面和整理信息尚未保存。", confirmLabel: "放弃更改", danger: true })) return;
    if (state.editor === session) discardEditor();
  }
  async function startEditor(mode, item) {
    ensure();
    if (state.editor && !await replaceEditor()) return;
    if (state.profile !== profile()) contextChanged(); state.profile = profile();
    if (item && (item.settings?.model_profile || item.model_profile || "anima") !== state.profile) { notify("请切换到这个预设所属的模型档案后再编辑"); return; }
    const snapshot = mode === "edit" ? null : copy(readSettings());
    const session = { mode, item: item ? copy(item) : null, profile: state.profile, epoch: state.epoch, snapshot, busy: false, file: null, objectUrl: "", fileRead: 0,
      imageWidth: 0, imageHeight: 0, crop: { x: 0, y: 0, width: 1, height: 1 }, zoom: 1, cx: .5, cy: .5,
      draft: editorDraft(mode, item) };
    session.initial = JSON.stringify(session.draft); state.editor = session; renderEditor(session); show(ui.editor, true);
    ui.editorBody.querySelector("#stylePresetName")?.focus({ preventScroll: true }); await refresh();
  }
  async function replaceEditor() { const previous = state.editor; await closeEditor(); return !state.editor || state.editor !== previous; }
  const create = () => startEditor("create");
  const edit = item => startEditor("edit", item);
  const overwrite = item => startEditor("overwrite", item);
  function renderEditor(session) {
    ui.editorBody.replaceChildren(); error(ui.editorError); ui.editorSave.textContent = session.mode === "overwrite" ? "更新风格参数" : "保存预设"; ui.editorSave.disabled = false; ui.editor.setAttribute("aria-busy", "false");
    ui.editorTitle.textContent = session.mode === "create" ? "新建风格预设" : session.mode === "overwrite" ? "用当前参数更新预设" : "编辑风格预设";
    const intro = h("p", "spm-editor-intro", session.mode === "edit" ? "整理预设信息，不改变已保存的风格参数。" : "已锁定此刻的风格参数。正文、提示词池、AI 处理和设备设置继续使用当前选择。");
    ui.editorBody.append(intro, h("p", "spm-capture-label", `${profileText(session.profile)} · ${(session.snapshot?.workbench || session.item?.source?.workbench) === "single" ? "单图工作台" : "随机工作台"}${session.mode !== "edit" ? " · 使用打开弹窗时的参数" : ""}`));
    const layout = h("div", "spm-editor-columns"), cover = h("section", "spm-cover-panel"), fields = h("div", "spm-editor-fields");
    fields.append(field("预设名称", textInput(session.draft.name, value => { session.draft.name = value; }, "stylePresetName")));
    const star = h("label", "spm-check-line"), checkbox = h("input"); checkbox.type = "checkbox"; checkbox.checked = session.draft.favorite; checkbox.id = "stylePresetFavorite";
    checkbox.addEventListener("change", () => { session.draft.favorite = checkbox.checked; }); star.append(checkbox, h("span", "", "加入收藏")); fields.append(star);
    const tags = textInput(session.draft.tags, value => { session.draft.tags = value; }, "stylePresetTags", 4000); tags.placeholder = "例如：柔光、人像、胶片";
    fields.append(field("标签", tags, "使用逗号分隔；查找时可以组合多个标签。"));
    const groups = h("fieldset", "spm-group-checks"); groups.id = "stylePresetGroups"; groups.append(h("legend", "", "收藏分组")); session.groupsNode = groups;
    fields.append(groups); layout.append(cover, fields); ui.editorBody.append(layout); renderEditorGroups(session); buildCoverEditor(session, cover);
    if (session.mode !== "edit") {
      const optional = h("section", "spm-optional"); optional.append(h("h3", "", "可选携带参数"), h("p", "spm-muted", "未勾选的项目在应用时保留当前值。双采样中间放大始终属于基础风格。"));
      const descriptions = { size: `${session.snapshot.width || "—"} × ${session.snapshot.height || "—"}`, repair: "生成后的放大修复与后处理", img2img: "去噪强度与画面裁切方式，不携带参考图", affixes: "单图提示词前缀和后缀，不携带正文" };
      const checks = h("div", "spm-include-grid");
      for (const key of Object.keys(INCLUDE)) {
        const label = h("label", "spm-include"), control = h("input"); control.type = "checkbox"; control.checked = session.draft.include[key]; control.dataset.include = key; control.id = `stylePresetInclude_${key}`;
        const unavailable = session.snapshot.workbench !== "single" && ["img2img", "affixes"].includes(key);
        control.disabled = unavailable; if (unavailable) control.dataset.unavailable = "true";
        control.addEventListener("change", () => { session.draft.include[key] = control.checked; }); const text = h("span");
        text.append(h("strong", "", includeLabels[key]), h("small", "", unavailable ? session.draft.include[key] ? "保留原预设的单图参数；切换到单图工作台可更新" : "请在单图工作台保存此项" : descriptions[key])); label.append(control, text); checks.append(label);
      } optional.append(checks); ui.editorBody.append(optional);
    }
    const details = h("details", "spm-settings-summary"), settings = session.snapshot || session.item?.settings || {};
    details.append(h("summary", "", "查看风格参数摘要")); const list = h("dl");
    const info = [["模型", settings.model_name || profileText(session.profile)], ["采样", `${settings.sampler_name || "—"} · ${settings.scheduler || "—"}`], ["步数 / CFG", `${settings.steps ?? "—"} / ${settings.cfg ?? "—"}`], ["底图方式", settings.base_mode === "dual_sampler" ? "双采样" : "普通采样"], ["LoRA", (settings.loras || []).filter(lora => lora.enabled !== false).map(lora => lora.filename || lora.name || lora.lora_name).filter(Boolean).join("、") || "无"], ["来源", (session.item?.source?.workbench || settings.workbench) === "single" ? "单图工作台" : "随机工作台"]];
    for (const [name, value] of info) { const row = h("div"); row.append(h("dt", "", name), h("dd", "", value)); list.append(row); } details.append(list); ui.editorBody.append(details);
    if (session.mode === "edit") {
      const actions = h("div", "spm-editor-secondary"); actions.append(button("用当前参数更新…", "button secondary compact", async () => { const item = session.item; if (await replaceEditor()) overwrite(item); }), button("删除预设…", "spm-text-button spm-danger", () => void deleteItem(session))); ui.editorBody.append(actions);
    }
    setEditorBusy(session);
  }
  function renderEditorGroups(session) {
    if (!currentSession(session) || !session.groupsNode) return;
    session.groupsNode.replaceChildren(h("legend", "", "收藏分组"));
    if (!state.groups.length) session.groupsNode.append(h("p", "spm-muted", "尚未创建分组。"));
    for (const group of state.groups) {
      const label = h("label", "spm-group-check"), control = h("input"); control.type = "checkbox"; control.value = group.id; control.checked = session.draft.groups.includes(group.id);
      control.addEventListener("change", () => { session.draft.groups = control.checked ? [...new Set([...session.draft.groups, group.id])] : session.draft.groups.filter(id => id !== group.id); if (control.checked) { session.draft.favorite = true; ui.editorBody.querySelector("#stylePresetFavorite").checked = true; } }); label.append(control, h("span", "", group.name)); session.groupsNode.append(label);
    }
    session.groupsNode.append(button("新建分组", "spm-text-button", () => openGroup()));
    setEditorBusy(session);
  }
  function buildCoverEditor(session, container) {
    container.append(h("h3", "", "预设封面"));
    const drop = h("div", "spm-cover-drop"); drop.id = "stylePresetCoverDrop"; drop.tabIndex = 0; drop.setAttribute("role", "group"); drop.setAttribute("aria-label", "导入封面，拖入图片或使用下方按钮");
    const stage = h("div", "spm-crop-stage"); stage.id = "stylePresetCropStage"; stage.append(imageNode(session.item || {}, "spm-existing-cover"));
    const hint = h("p", "spm-muted", "拖入图片，或选择本地图片。封面比例为 4:3。"), file = h("input"); file.type = "file"; file.accept = "image/png,image/jpeg,image/webp"; file.hidden = true; file.id = "stylePresetCoverFile";
    file.addEventListener("change", () => { if (file.files[0]) void readCover(session, file.files[0]); file.value = ""; });
    const actions = h("div", "spm-cover-actions"); actions.append(button("选择图片", "button ghost compact", () => file.click()), button("移除", "spm-text-button", () => {
      if (!currentSession(session) || session.busy || !writable()) return; session.fileRead++; if (session.objectUrl) URL.revokeObjectURL(session.objectUrl); session.objectUrl = ""; session.file = null; session.fileLoading = false; session.draft.cover_id = null;
      stage.replaceChildren(imageNode({}, "spm-existing-cover")); controls.hidden = true; reset.hidden = true; hint.textContent = "保存后将移除封面。";
      setEditorBusy(session);
    }));
    const reset = button("重置裁切", "spm-text-button", () => {
      if (!currentSession(session) || session.busy || !session.file || !writable()) return;
      session.zoom = 1; session.cx = .5; session.cy = .5; session.cover.zoom.value = "1"; paintCrop(session);
    }); reset.id = "stylePresetCoverReset"; reset.hidden = true; actions.append(reset);
    const controls = h("label", "spm-crop-controls"); controls.hidden = true; const zoom = h("input"); zoom.type = "range"; zoom.min = "1"; zoom.max = "4"; zoom.step = ".01"; zoom.value = "1"; zoom.id = "stylePresetCoverZoom";
    zoom.addEventListener("input", () => { session.zoom = Number(zoom.value); paintCrop(session); }); controls.append(h("span", "", "缩放"), zoom);
    drop.append(stage, hint); container.append(drop, file, actions, controls); session.cover = { stage, hint, zoom, controls, reset };
    drop.addEventListener("dragover", event => { event.preventDefault(); if (!session.busy) drop.classList.add("is-dragover"); });
    drop.addEventListener("dragleave", event => { if (!drop.contains(event.relatedTarget)) drop.classList.remove("is-dragover"); });
    drop.addEventListener("drop", event => { event.preventDefault(); drop.classList.remove("is-dragover"); if (event.dataTransfer.files[0]) void readCover(session, event.dataTransfer.files[0]); });
    let drag = null;
    stage.addEventListener("pointerdown", event => { if (!session.file || session.busy || !writable() || event.button !== 0) return; event.preventDefault(); stage.setPointerCapture(event.pointerId); drag = { x: event.clientX, y: event.clientY, cx: session.cx, cy: session.cy }; });
    stage.addEventListener("pointermove", event => { if (!drag || !currentSession(session)) return; const bounds = stage.getBoundingClientRect(); session.cx = drag.cx - (event.clientX - drag.x) * session.crop.width / bounds.width; session.cy = drag.cy - (event.clientY - drag.y) * session.crop.height / bounds.height; paintCrop(session); });
    for (const type of ["pointerup", "pointercancel", "lostpointercapture"]) stage.addEventListener(type, () => { drag = null; });
    stage.addEventListener("keydown", event => { const directions = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }; if (!session.file || session.busy || !writable() || !directions[event.key]) return; event.preventDefault(); const [dx, dy] = directions[event.key]; session.cx += dx * .015; session.cy += dy * .015; paintCrop(session); });
    session.resize = new ResizeObserver(() => { if (session.file) paintCrop(session); }); session.resize.observe(stage);
  }
  async function readCover(session, file) {
    if (!currentSession(session) || session.busy || !writable()) return;
    if (!["image/png", "image/jpeg", "image/webp"].includes(file.type)) { error(ui.editorError, "请选择 PNG、JPEG 或 WebP 图片。"); return; }
    if (file.size > 20 * 1024 * 1024) { error(ui.editorError, "封面图片不能超过 20 MB。"); return; }
    const serial = ++session.fileRead, url = URL.createObjectURL(file), image = new Image(); session.fileLoading = true; setEditorBusy(session);
    try {
      image.src = url; await image.decode();
      if (!currentSession(session) || serial !== session.fileRead) { URL.revokeObjectURL(url); return; }
      if (!image.naturalWidth || !image.naturalHeight) throw new Error("图片尺寸无效");
      if (session.objectUrl) URL.revokeObjectURL(session.objectUrl);
      session.objectUrl = url; session.file = file; session.imageWidth = image.naturalWidth; session.imageHeight = image.naturalHeight; session.zoom = 1; session.cx = .5; session.cy = .5;
      session.cover.stage.replaceChildren(image); session.cover.stage.tabIndex = 0; session.cover.stage.setAttribute("aria-label", "拖动调整封面裁切，或使用方向键微调"); session.cover.zoom.value = "1"; session.cover.controls.hidden = false; session.cover.reset.hidden = false;
      session.cover.hint.textContent = "拖动调整位置，使用滑块缩放。"; error(ui.editorError); paintCrop(session);
    } catch (failure) { URL.revokeObjectURL(url); if (currentSession(session) && serial === session.fileRead) error(ui.editorError, `无法读取图片：${failure.message}`); }
    finally { if (serial === session.fileRead) { session.fileLoading = false; setEditorBusy(session); } }
  }
  function paintCrop(session) {
    if (!currentSession(session) || !session.file) return;
    const rect = cropRectangle(session.imageWidth, session.imageHeight, session.zoom, session.cx, session.cy); session.crop = rect; session.cx = rect.x + rect.width / 2; session.cy = rect.y + rect.height / 2;
    const image = session.cover.stage.querySelector("img"), bounds = session.cover.stage.getBoundingClientRect(); if (!image || !bounds.width) return;
    Object.assign(image.style, { width: `${bounds.width / rect.width}px`, height: `${bounds.height / rect.height}px`, left: `${-rect.x * bounds.width / rect.width}px`, top: `${-rect.y * bounds.height / rect.height}px` });
  }
  function setEditorBusy(session) {
    if (!currentSession(session)) return;
    ui.editorSave.disabled = session.busy || session.fileLoading || !writable(); ui.editorSave.textContent = session.busy ? "正在保存…" : session.fileLoading ? "正在读取图片…" : session.mode === "overwrite" ? "更新风格参数" : "保存预设";
    ui.editor.setAttribute("aria-busy", String(session.busy));
    ui.editorBody.querySelectorAll("input, button, textarea, select").forEach(node => { node.disabled = session.busy || !writable() || node.dataset.unavailable === "true"; });
    if (!writable()) error(ui.editorError, "风格预设目前为只读，恢复存储后可以继续编辑。");
  }
  async function submitEditor() {
    const session = state.editor; if (!session || session.busy || session.fileLoading || !currentSession(session) || !writable()) return;
    if (!session.draft.name.trim()) { error(ui.editorError, "请输入预设名称。"); ui.editorBody.querySelector("#stylePresetName")?.focus(); return; }
    if (state.items.some(item => item.id !== session.item?.id && item.name.trim().toLocaleLowerCase() === session.draft.name.trim().toLocaleLowerCase())) { error(ui.editorError, "已有同名预设，请使用其他名称。"); return; }
    error(ui.editorError);
    const saving = saveSession(session, {
      current: currentSession,
      upload: async (file, crop) => { const form = new FormData(); form.append("image", file); form.append("crop", JSON.stringify(crop)); return request("/api/style-preset-covers", { method: "POST", headers: {}, body: form }); },
      write: payload => request(session.mode === "create" ? "/api/style-presets" : `/api/style-presets/${encodeURIComponent(session.item.id)}`, { method: session.mode === "create" ? "POST" : "PUT", body: JSON.stringify(payload) }),
      success: () => discardEditor(),
      committed: result => changed(session.profile, `已保存“${result.name || session.draft.name}”`),
      failure: failure => error(ui.editorError, failure.message), detachedFailure: failure => notify(`${profileText(session.profile)}：保存失败，${failure.message}`), settled: setEditorBusy,
    }); setEditorBusy(session); await saving;
  }
  async function deleteItem(session) {
    if (!currentSession(session) || session.busy) return;
    if (!await confirmAction({ title: `删除“${session.item.name}”？`, message: "预设和封面将从风格库移除，当前生成设置不会改变。", confirmLabel: "删除预设", danger: true }) || !currentSession(session)) return;
    session.busy = true; setEditorBusy(session);
    try { await request(`/api/style-presets/${encodeURIComponent(session.item.id)}?model_profile=${encodeURIComponent(session.profile)}`, { method: "DELETE" }); if (currentSession(session)) discardEditor(); await changed(session.profile, "已删除预设"); }
    catch (failure) { if (currentSession(session)) error(ui.editorError, failure.message); else notify(failure.message); }
    finally { session.busy = false; setEditorBusy(session); }
  }
  function smallDirty(session) { return JSON.stringify(session.draft) !== session.initial; }
  function discardSmall(immediate = false) { state.small = null; if (ui.small) show(ui.small, false, { immediate, restoreFocus: !immediate }); }
  async function closeSmall() {
    const session = state.small; if (!session) return;
    if (!session.busy && smallDirty(session) && !await confirmAction({ title: "放弃未保存的更改？", message: "这次整理尚未保存。", confirmLabel: "放弃更改", danger: true })) return;
    if (state.small === session) discardSmall();
  }
  async function openGroup(group) {
    if (!writable()) return; ensure(); if (state.small) { await closeSmall(); if (state.small) return; }
    const session = { type: "group", profile: profile(), epoch: state.epoch, id: group?.id, draft: { name: group?.name || "" }, busy: false }; session.initial = JSON.stringify(session.draft); state.small = session;
    ui.smallTitle.textContent = group ? "编辑收藏分组" : "新建收藏分组"; ui.smallBody.replaceChildren(); error(ui.smallError); ui.smallSave.disabled = false; ui.smallSave.textContent = "保存分组";
    const input = textInput(session.draft.name, value => { session.draft.name = value; }, "stylePresetGroupName"); ui.smallBody.append(field("分组名称", input));
    if (group) ui.smallBody.append(button("删除分组…", "spm-text-button spm-danger", () => void deleteGroup(session)));
    show(ui.small, true); input.focus({ preventScroll: true });
  }
  async function deleteGroup(session) {
    if (session.busy || !currentSmall(session) || !writable()) return;
    if (!await confirmAction({ title: "删除收藏分组？", message: "只移除分组，预设、标签和收藏状态都会保留。", confirmLabel: "删除分组", danger: true }) || !currentSmall(session)) return;
    session.busy = true; ui.smallSave.disabled = true;
    try { await request(`/api/style-preset-groups/${encodeURIComponent(session.id)}?model_profile=${encodeURIComponent(session.profile)}`, { method: "DELETE" }); if (currentSmall(session)) discardSmall(); await changed(session.profile, "已删除分组，收藏已保留"); }
    catch (failure) { if (currentSmall(session)) error(ui.smallError, failure.message); else notify(failure.message); }
    finally { session.busy = false; if (currentSmall(session)) { ui.smallSave.disabled = !writable(); if (!writable()) ui.smallBody.querySelectorAll("input, button, select").forEach(node => { node.disabled = true; }); } }
  }
  async function openBatch(action) {
    if (!state.selected.size || !writable()) return;
    const ids = [...state.selected], forProfile = profile();
    if (action === "favorite" || action === "unfavorite") {
      if (state.batchWrites.has(forProfile)) return; const operation = {}; state.batchWrites.set(forProfile, operation); renderGrid(); const epoch = state.epoch;
      try { await request("/api/style-presets/batch", { method: "POST", body: JSON.stringify({ model_profile: forProfile, ids, action: "favorite", value: action === "favorite" }) }); await changed(forProfile, `已${action === "favorite" ? "收藏" : "取消收藏"} ${ids.length} 个预设`); }
      catch (failure) { if (epoch === state.epoch && forProfile === profile()) error(ui.notice, failure.message); else notify(`${profileText(forProfile)}：${failure.message}`); }
      finally { if (state.batchWrites.get(forProfile) === operation) state.batchWrites.delete(forProfile); if (epoch === state.epoch) renderGrid(); } return;
    }
    if (state.small) { await closeSmall(); if (state.small) return; }
    const session = { type: "batch", action, ids, profile: forProfile, epoch: state.epoch, draft: { mode: "add", values: [], tags: "" }, busy: false }; session.initial = JSON.stringify(session.draft); state.small = session;
    ui.smallTitle.textContent = action === "groups" ? "批量整理分组" : "批量编辑标签"; ui.smallBody.replaceChildren(h("p", "spm-muted", `将修改已选择的 ${ids.length} 个预设，之后的筛选变化不影响本次选择。`)); error(ui.smallError); ui.smallSave.disabled = false; ui.smallSave.textContent = "保存整理";
    const mode = h("select"); for (const [value, text] of [["add", "追加"], ["remove", "移除"], ["replace", "替换全部"]]) mode.append(new Option(text, value)); mode.addEventListener("change", () => { session.draft.mode = mode.value; }); ui.smallBody.append(field("操作方式", mode));
    if (action === "groups") {
      const groups = h("div", "spm-group-checks");
      for (const group of state.groups) { const label = h("label", "spm-group-check"), control = h("input"); control.type = "checkbox"; control.addEventListener("change", () => { session.draft.values = control.checked ? [...session.draft.values, group.id] : session.draft.values.filter(id => id !== group.id); }); label.append(control, h("span", "", group.name)); groups.append(label); }
      if (!state.groups.length) groups.append(h("p", "spm-muted", "请先在收藏分组中创建分组。")); ui.smallBody.append(groups, h("p", "spm-muted", "加入分组会同步收藏；移出分组保留星标。"));
    } else ui.smallBody.append(field("标签", textInput("", value => { session.draft.tags = value; }, "stylePresetBatchTags", 4000), "使用逗号分隔。"));
    show(ui.small, true); ui.smallBody.querySelector("input, select")?.focus({ preventScroll: true });
  }
  async function submitSmall() {
    const session = state.small; if (!session || session.busy || !currentSmall(session) || !writable()) return;
    if (session.type === "group" && !session.draft.name.trim()) { error(ui.smallError, "请输入分组名称。"); return; }
    const values = session.action === "tags" ? uniqueWords(session.draft.tags) : session.draft.values;
    if (session.type === "batch" && !values.length && session.draft.mode !== "replace") { error(ui.smallError, session.action === "tags" ? "请至少输入一个标签。" : "请选择至少一个分组。"); return; }
    const payload = session.type === "group" ? { model_profile: session.profile, name: session.draft.name.trim() } : { model_profile: session.profile, ids: session.ids, action: session.action, value: { mode: session.draft.mode, values } };
    session.busy = true; ui.smallSave.disabled = true; error(ui.smallError);
    try {
      await request(session.type === "group" ? `/api/style-preset-groups${session.id ? `/${encodeURIComponent(session.id)}` : ""}` : "/api/style-presets/batch", { method: session.type === "group" && session.id ? "PUT" : "POST", body: JSON.stringify(payload) });
      if (currentSmall(session)) discardSmall(); await changed(session.profile, session.type === "group" ? "分组已保存" : "预设整理已保存");
    } catch (failure) { if (currentSmall(session)) error(ui.smallError, failure.message); else notify(`${profileText(session.profile)}：${failure.message}`); }
    finally { session.busy = false; if (currentSmall(session)) { ui.smallSave.disabled = !writable(); if (!writable()) ui.smallBody.querySelectorAll("input, button, select").forEach(node => { node.disabled = true; }); } }
  }
  globalThis.StylePresetManager = Object.freeze({ open, create, edit, overwrite, confirm: confirmAction, refresh, contextChanged });
})();
