/* Reuse shared controls and restore their exact positions when leaving single mode. */
const singleLayoutState = {
  initialized: false, active: null, category: "common", moves: [], shared: [],
  hiddenFields: [], constraints: new Map(), invalidPending: false,
  categoryLayouts: [], categoryPlacement: "original",
};

function initializeSingleLayout() {
  const state = singleLayoutState;
  if (state.initialized) return;
  const controls = document.getElementById("singleWorkbenchControls");
  if (!controls?.children.length) return;
  state.initialized = true;
  state.singleFields = [...controls.querySelectorAll("input, select, textarea, button")];
  state.singleDisabled = new Map(state.singleFields.map(field => [field, field.disabled]));
  const cardFor = id => document.getElementById(id)?.closest(".control-section");
  const mark = (element, name, order, group = "", wide = false) => {
    if (!element) return;
    element.dataset.singleCard = name;
    if (group) element.dataset.singleGroup = group;
    element.style.setProperty("--single-order", order);
    element.classList.toggle("single-card-wide", wide);
  };
  const makeMove = node => {
    if (!node) return null;
    const anchor = document.createComment("Original workbench position");
    node.before(anchor);
    return { node, anchor };
  };
  mark(controls.querySelector(".single-prompt-section"), "prompt", 1, "", true);
  mark(document.getElementById("singleInputSection"), "reference", 2, "", true);
  mark(document.getElementById("singleParameterTabs"), "categories", 3, "", true);
  for (const [id, name, order, group, wide] of [
    ["model_profile", "model", 10, "common", false],
    ["width", "sampling", 11, "common", false],
    ["addLora", "lora", 12, "common", true],
    ["savePreset", "presets", 40, "more", true],
  ]) {
    const node = cardFor(id);
    if (!node) continue;
    mark(node, name, order, group, wide);
    const summary = node.querySelector(":scope > summary");
    state.shared.push({ node, summary });
    summary?.addEventListener("click", event => {
      if (singleWorkbenchActive() && node.classList.contains("single-fixed-open")) event.preventDefault();
    });
    node.addEventListener("toggle", () => {
      if (singleWorkbenchActive() && node.classList.contains("single-fixed-open") && !node.open) node.open = true;
    });
  }
  let order = 15;
  for (const card of controls.querySelectorAll(":scope > [data-single-group]")) {
    if (!card.dataset.singleCard) mark(card, `parameters-${order}`, order++, card.dataset.singleGroup, card.classList.contains("single-card-wide"));
  }
  mark(document.getElementById("singleProcessedSection"), "processed", 20, "prompt", true);
  mark(document.getElementById("singleDlssSection"), "dlss", 28, "repair");
  mark(document.getElementById("singleCompatibilitySection"), "compatibility", 41, "more", true);
  for (const name of ["prefix", "positive_prompt", "suffix"]) {
    document.getElementById(`single_${name}`)?.closest("label")?.classList.add(`single-prompt-${name}`);
  }
  document.getElementById("singleNegative")?.closest("label")?.classList.add("single-prompt-negative");

  for (const [name, enabledId] of [
    ["latent", "latent_upscale_enabled"], ["seedvr2", "seedvr2_enabled"],
    ["nvidia", "nvidia_upscale_enabled"], ["face", "face_refine_enabled"],
  ]) {
    const node = document.querySelector(`.sampling-stage-${name}`);
    if (!node) continue;
    const wrapper = document.createElement("section");
    wrapper.className = "control-section single-postprocess-card";
    wrapper.hidden = true;
    controls.append(wrapper);
    mark(wrapper, `post-${name}`, 24 + state.moves.length, "repair");
    state.moves.push({ ...makeMove(node), wrapper, enabled: document.getElementById(enabledId) });
  }
  state.seed = makeMove(document.getElementById("singleSeedControls"));
  state.seedSection = document.getElementById("singleSeedSection");
  state.seedTarget = cardFor("width")?.querySelector(".details-content");
  state.dual = makeMove(document.getElementById("singleDualSection"));
  state.latentMethod = makeMove(document.getElementById("singleLatentMethodField"));
  state.latentTarget = document.getElementById("latent_upscale_scale")?.closest("label");

  const hide = node => {
    if (node && !state.hiddenFields.some(item => item.node === node)) state.hiddenFields.push({ node });
  };
  hide(document.getElementById("seedvr2_weight_dtype")?.closest("label"));
  document.querySelectorAll(".sampling-stage-seedvr2 .sampling-fieldset").forEach(hide);
  hide(document.getElementById("nvidia_resize_type")?.closest("label"));
  const faceKeep = new Set(["face_steps", "face_cfg_scale", "face_sampler", "face_scheduler", "face_denoise_strength", "face_processing_resolution"]);
  for (const node of document.querySelectorAll("#faceRefineFields > label")) {
    if (!faceKeep.has(node.querySelector("input, select, textarea")?.id)) hide(node);
  }
  hide(document.querySelector("#faceRefineFields > .switch-list"));
  const postCard = name => state.moves.find(move => move.wrapper.dataset.singleCard === `post-${name}`)?.wrapper;
  const processingCard = stage => document.getElementById(stage === "expand" ? "singleExpandSection" : "singleOcSection")
    || document.querySelector(`[data-single-path="prompt_processing.${stage}.enabled"]`)?.closest(".control-section");
  for (const [category, leftCards, rightCards, tailCards] of [
    ["prompt", [processingCard("expand")], [processingCard("oc")], [document.getElementById("singleProcessedSection")]],
    ["repair", [postCard("latent"), postCard("nvidia"), document.getElementById("singleDlssSection")], [postCard("seedvr2"), postCard("face")], []],
    ["more", [], [document.getElementById("singleKreaSection")], [cardFor("savePreset"), document.getElementById("singleCompatibilitySection")]],
  ]) {
    const container = document.createElement("div");
    container.className = "single-category-layout";
    container.dataset.singleCategoryLayout = category;
    container.hidden = true;
    mark(container, `category-${category}`, 15, "", true);
    const columns = document.createElement("div");
    columns.className = "single-category-columns";
    const left = document.createElement("div"), right = document.createElement("div"), tail = document.createElement("div");
    left.className = right.className = "single-category-column";
    left.dataset.singleColumn = "left";
    right.dataset.singleColumn = "right";
    tail.className = "single-category-tail";
    columns.append(left, right);
    container.append(columns, tail);
    controls.append(container);
    const cards = [];
    for (const [target, nodes] of [[left, leftCards], [right, rightCards], [tail, tailCards]]) {
      for (const node of nodes.filter(Boolean)) cards.push({ ...makeMove(node), target });
    }
    cards.sort((a, b) => Number(a.node.style.getPropertyValue("--single-order")) - Number(b.node.style.getPropertyValue("--single-order")));
    state.categoryLayouts.push({ category, container, columns, left, right, tail, cards });
  }
  state.columnsMobile = matchMedia("(max-width: 700px)");
  state.columnsMobile.addEventListener("change", singleWorkbenchLayout);
  state.command = makeMove(document.querySelector(".command-deck"));
  state.queue = makeMove(document.getElementById("queuePanel"));
  state.mobile = matchMedia("(max-width: 1100px)");
  state.mobile.addEventListener("change", singleWorkbenchLayout);

  const tabs = document.getElementById("singleParameterTabs");
  tabs?.addEventListener("click", event => {
    const button = event.target.closest("[data-single-category]");
    if (button) singleWorkbenchSelectCategory(button.dataset.singleCategory);
  });
  tabs?.addEventListener("keydown", event => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    const buttons = [...tabs.querySelectorAll("[data-single-category]")];
    const current = buttons.indexOf(event.target.closest("[data-single-category]"));
    if (current < 0) return;
    event.preventDefault();
    const next = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1
      : (current + (event.key === "ArrowRight" ? 1 : -1) + buttons.length) % buttons.length;
    singleWorkbenchSelectCategory(buttons[next].dataset.singleCategory, { focus: true });
  });
  if (tabs && typeof ResizeObserver !== "undefined") {
    state.tabObserver = new ResizeObserver(singleWorkbenchPositionIndicator);
    state.tabObserver.observe(tabs);
  }
  document.getElementById("settingsForm")?.addEventListener("invalid", event => {
    if (!singleWorkbenchActive()) return;
    if (state.invalidPending) { event.preventDefault(); return; }
    state.invalidPending = true;
    singleWorkbenchRevealField(event.target);
    queueMicrotask(() => { state.invalidPending = false; });
  }, true);
}

function singleWorkbenchCaptureLayoutFocus() {
  const focused = document.activeElement;
  const snapshot = { focused, selection: null, scroll: [], x: window.scrollX, y: window.scrollY };
  if (focused instanceof HTMLInputElement || focused instanceof HTMLTextAreaElement) {
    if (typeof focused.selectionStart === "number") snapshot.selection = [focused.selectionStart, focused.selectionEnd, focused.selectionDirection];
  }
  for (let node = focused instanceof Element ? focused : null; node; node = node.parentElement) {
    if (node.scrollTop || node.scrollLeft || node.scrollHeight > node.clientHeight || node.scrollWidth > node.clientWidth) {
      snapshot.scroll.push([node, node.scrollLeft, node.scrollTop]);
    }
  }
  return snapshot;
}

function singleWorkbenchRestoreLayoutFocus(snapshot) {
  if (!snapshot) return;
  const { focused, selection } = snapshot;
  if (focused instanceof HTMLElement && focused.isConnected && !focused.disabled && focused.getClientRects().length) {
    if (document.activeElement !== focused) focused.focus({ preventScroll: true });
    if (selection && typeof focused.setSelectionRange === "function") focused.setSelectionRange(...selection);
  }
  for (const [node, left, top] of snapshot.scroll) {
    if (node.isConnected) { node.scrollLeft = left; node.scrollTop = top; }
  }
  if (window.scrollX !== snapshot.x || window.scrollY !== snapshot.y) window.scrollTo(snapshot.x, snapshot.y);
}

function singleWorkbenchPlaceCategoryCards(active) {
  const state = singleLayoutState;
  const placement = active ? (state.columnsMobile.matches ? "single" : "columns") : "original";
  if (placement !== state.categoryPlacement) {
    for (const layout of state.categoryLayouts) for (const card of layout.cards) {
      if (placement === "original") card.anchor.after(card.node);
      else (placement === "single" ? layout.container : card.target).append(card.node);
    }
    state.categoryPlacement = placement;
  }
  for (const layout of state.categoryLayouts) {
    layout.container.hidden = !active || layout.category !== state.category;
    layout.columns.hidden = placement !== "columns" || !layout.cards.some(card => card.target !== layout.tail && !card.node.hidden);
    layout.tail.hidden = placement !== "columns" || !layout.cards.some(card => card.target === layout.tail && !card.node.hidden);
  }
}

function singleWorkbenchPositionIndicator() {
  if (!singleWorkbenchActive()) return;
  const button = document.querySelector(`#singleParameterTabs [data-single-category="${singleLayoutState.category}"]`);
  const indicator = document.getElementById("singleTabIndicator");
  if (!button || !indicator) return;
  indicator.style.width = `${button.offsetWidth}px`;
  indicator.style.transform = `translateX(${button.offsetLeft}px)`;
}

function singleWorkbenchSelectCategory(category, { focus = false } = {}) {
  if (!["common", "prompt", "repair", "more"].includes(category)) return;
  singleLayoutState.category = category;
  singleWorkbenchLayout();
  const button = document.querySelector(`#singleParameterTabs [data-single-category="${category}"]`);
  if (!button) return;
  if (focus) button.focus();
  const tabs = button.parentElement;
  const left = button.offsetLeft, right = left + button.offsetWidth;
  if (left < tabs.scrollLeft) tabs.scrollLeft = Math.max(0, left - 4);
  else if (right > tabs.scrollLeft + tabs.clientWidth) tabs.scrollLeft = right - tabs.clientWidth + 4;
}

function singleWorkbenchRevealField(element) {
  if (!singleWorkbenchActive() || !(element instanceof Element)) return;
  const category = element.closest("[data-single-group]")?.dataset.singleGroup;
  if (category) singleWorkbenchSelectCategory(category);
  for (let parent = element.parentElement; parent && parent.id !== "settingsForm"; parent = parent.parentElement) {
    if (parent.tagName === "DETAILS") parent.open = true;
  }
  document.querySelector('.mobile-tabs [data-mobile-view="settings"]')?.click();
}

function singleWorkbenchCompactFields(active) {
  const state = singleLayoutState;
  for (const item of state.hiddenFields) {
    if (active) {
      item.node.hidden = true;
      for (const field of item.node.querySelectorAll("input, select, textarea")) {
        let saved = state.constraints.get(field);
        if (!saved) {
          saved = new Map();
          for (const name of ["min", "max", "step", "required", "pattern"]) saved.set(name, field.getAttribute(name));
          state.constraints.set(field, saved);
        } else {
          // Live node schemas can refresh constraints while single mode is open.
          for (const name of saved.keys()) {
            const value = field.getAttribute(name);
            if (value !== null && !(name === "step" && value === "any")) saved.set(name, value);
          }
        }
        for (const name of saved.keys()) field.removeAttribute(name);
        if (field.type === "number") field.setAttribute("step", "any");
      }
    } else if (item.originalHidden !== undefined) item.node.hidden = item.originalHidden;
  }
  if (!active) {
    for (const [field, attrs] of state.constraints) for (const [name, value] of attrs) {
      if (value === null) field.removeAttribute(name); else field.setAttribute(name, value);
    }
    state.constraints.clear();
  }
}

function singleWorkbenchLayout() {
  initializeSingleLayout();
  const state = singleLayoutState;
  if (!state.initialized) return;
  const active = singleWorkbenchActive();
  const nextPlacement = active ? (state.columnsMobile.matches ? "single" : "columns") : "original";
  const commandTarget = active && !state.mobile.matches ? document.getElementById("singleCommandSlot") : state.command?.anchor.parentElement;
  const focusSnapshot = active !== state.active || nextPlacement !== state.categoryPlacement || state.command?.node.parentElement !== commandTarget
    ? singleWorkbenchCaptureLayoutFocus() : null;
  if (active !== state.active) {
    if (active) {
      for (const [field, disabled] of state.singleDisabled) field.disabled = disabled;
      for (const record of state.shared) {
        record.hidden = record.node.hidden;
        record.open = record.node.open;
        record.tabIndex = record.summary?.getAttribute("tabindex");
        record.node.classList.add("single-fixed-open");
        record.node.open = true;
        record.summary?.setAttribute("tabindex", "-1");
      }
      for (const item of state.hiddenFields) item.originalHidden = item.node.hidden;
      for (const move of state.moves) move.wrapper.append(move.node);
      if (state.dual && state.seedTarget) state.seedTarget.append(state.dual.node);
      if (state.seed && state.seedTarget) state.seedTarget.append(state.seed.node);
      if (state.seedSection) { state.seedSectionHidden = state.seedSection.hidden; state.seedSection.hidden = true; }
      if (state.latentMethod && state.latentTarget) state.latentTarget.after(state.latentMethod.node);
    } else {
      if (state.active === true) for (const field of state.singleFields) state.singleDisabled.set(field, field.disabled);
      for (const move of state.moves) { move.anchor.after(move.node); move.wrapper.hidden = true; }
      if (state.dual) state.dual.anchor.after(state.dual.node);
      if (state.seed) state.seed.anchor.after(state.seed.node);
      if (state.seedSection && state.seedSectionHidden !== undefined) state.seedSection.hidden = state.seedSectionHidden;
      if (state.latentMethod) state.latentMethod.anchor.after(state.latentMethod.node);
      for (const record of state.shared) {
        record.node.classList.remove("single-fixed-open");
        if (record.open !== undefined) record.node.open = record.open;
        if (record.hidden !== undefined) record.node.hidden = record.hidden;
        if (record.summary) {
          if (record.tabIndex == null) record.summary.removeAttribute("tabindex");
          else record.summary.setAttribute("tabindex", record.tabIndex);
        }
      }
    }
    state.active = active;
  }
  // Both workbenches expose the same concise post-processing controls.
  // Values remain in the shared controls for history and preset compatibility.
  singleWorkbenchCompactFields(true);
  if (active) {
    document.getElementById("settingsForm").dataset.singleCategory = state.category;
    const tabs = [...document.querySelectorAll("#singleParameterTabs [data-single-category]")];
    for (const button of tabs) {
      const selected = button.dataset.singleCategory === state.category;
      button.setAttribute("role", "tab");
      button.id ||= `single-tab-${button.dataset.singleCategory}`;
      button.setAttribute("aria-selected", String(selected));
      button.tabIndex = selected ? 0 : -1;
    }
    const panels = [...document.querySelectorAll("#settingsForm [data-single-group]")];
    for (const card of panels) {
      const when = card.dataset.singleWhen;
      const available = !when || when === "dual" && ui.base_mode.value === "dual_sampler" && singleWorkbenchState.generationMode !== "img2img"
        || when === "krea2" && currentProfile() === "krea2" || when === "latent" && ui.latent_upscale_enabled.checked;
      card.hidden = card.dataset.singleGroup !== state.category || !available || card === state.seedSection;
      card.dataset.singleUnavailable = String(!available);
      if (when) for (const field of card.querySelectorAll("input, select, textarea, button")) field.disabled = !available;
      card.id ||= `single-panel-${card.dataset.singleCard}`;
      card.setAttribute("role", "tabpanel");
      const tab = tabs.find(button => button.dataset.singleCategory === card.dataset.singleGroup);
      if (tab) card.setAttribute("aria-labelledby", tab.id);
    }
    for (const panel of document.querySelectorAll("[data-single-enabled]")) {
      const toggle = document.querySelector(`[data-single-path="${panel.dataset.singleEnabled}"]`);
      const enabled = Boolean(toggle?.checked);
      panel.hidden = !enabled;
      const unavailable = panel.closest("[data-single-group]")?.dataset.singleUnavailable === "true";
      for (const field of panel.querySelectorAll("input, select, textarea, button")) field.disabled = !enabled || unavailable;
    }
    for (const button of tabs) button.setAttribute("aria-controls", panels.filter(card => card.dataset.singleGroup === button.dataset.singleCategory).map(card => card.id).join(" "));
    const method = document.getElementById("single_latent_upscale_method");
    if (method) method.disabled = !ui.latent_upscale_enabled.checked;
    const seed = document.getElementById("singleSampleSeed");
    if (seed) seed.disabled = document.getElementById("singleSeedMode")?.value !== "fixed";
    singleWorkbenchPositionIndicator();
  } else {
    for (const field of state.singleFields) field.disabled = true;
    for (const record of state.shared) { record.node.removeAttribute("role"); record.node.removeAttribute("aria-labelledby"); }
  }
  singleWorkbenchPlaceCategoryCards(active);
  document.getElementById("randomArchive").hidden = active;
  document.querySelector('[data-mobile-view="gallery"]').textContent = active ? "预览" : "作品库";
  if (active && !state.mobile.matches) {
    const commandSlot = document.getElementById("singleCommandSlot"), previewRail = document.getElementById("singlePreviewRail");
    if (state.command && state.command.node.parentElement !== commandSlot) commandSlot.append(state.command.node);
    if (state.queue && state.queue.node.parentElement !== previewRail) previewRail.append(state.queue.node);
  } else {
    for (const move of [state.command, state.queue]) if (move && move.anchor.nextSibling !== move.node) move.anchor.after(move.node);
  }
  singleWorkbenchRestoreLayoutFocus(focusSnapshot);
}
