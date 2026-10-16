/* One owned reference image drives the single workbench's generation mode. */
const SINGLE_INPUT_MAX_BYTES = 20 * 1024 * 1024;

function singleWorkbenchInputBlocked() {
  if (!singleWorkbenchActive()) return false;
  const state = singleWorkbenchState;
  const id = state.single?.input_image_id;
  return Boolean(state.inputBusy || (id && state.inputImage?.id !== id)
    || (state.generationMode === "img2img" && !id));
}

function singleWorkbenchHideDrop() {
  singleWorkbenchState.inputDragDepth = 0;
  const overlay = document.getElementById("singleDropOverlay");
  if (overlay) overlay.hidden = true;
}

function singleWorkbenchCanReceiveInput() {
  if (!singleWorkbenchActive() || document.hidden) return false;
  if (document.querySelector("dialog:modal")) return false;
  return typeof imagePreview === "undefined" || imagePreview.phase === "closed";
}

function singleWorkbenchCancelInput({ render = true } = {}) {
  const state = singleWorkbenchState;
  state.inputRequest = (state.inputRequest || 0) + 1;
  state.inputAbort?.abort();
  state.inputAbort = null;
  state.inputBusy = "";
  state.inputError = "";
  singleWorkbenchHideDrop();
  if (render) singleWorkbenchRenderInput();
}

function singleWorkbenchRenderInput() {
  const state = singleWorkbenchState;
  const id = state.single?.input_image_id || "";
  const value = id && state.inputImage?.id === id ? state.inputImage : null;
  const section = document.getElementById("singleInputSection");
  if (!section) return;
  section.hidden = false;
  section.setAttribute("aria-busy", String(Boolean(state.inputBusy)));
  section.classList.toggle("has-reference", Boolean(id));
  section.classList.toggle("input-unavailable", Boolean(id && !value && !state.inputBusy));
  const image = document.getElementById("singleInputPreview");
  image.hidden = !value;
  if (value) {
    const src = value.image_url || `/api/input-images/${encodeURIComponent(value.id)}/image`;
    if (image.getAttribute("src") !== src) image.src = src;
  } else image.removeAttribute("src");
  document.getElementById("singleInputRemove").hidden = !id && !state.inputBusy;
  document.getElementById("singleUseInputSize").hidden = !value;
  document.getElementById("singleInputFields").hidden = !id;
  const status = document.getElementById("singleInputStatus");
  status.textContent = state.inputBusy === "upload" ? "正在保存参考图…"
    : state.inputBusy === "restore" ? "正在读取参考图…"
      : state.inputError || (value ? `${value.filename || "参考图"} · ${value.width} × ${value.height}`
        : id ? "参考图不可用，请重新添加或移除后生成。"
          : "拖入或粘贴图片，自动切换为图生图。PNG / JPEG / WebP，最大 20 MiB。");
  if (!singleWorkbenchCanReceiveInput()) singleWorkbenchHideDrop();
  if (typeof updateGenerationAvailability === "function") updateGenerationAvailability();
}

function singleWorkbenchSetInput(value, { activate = true } = {}) {
  if (value && (typeof value.id !== "string" || !value.id || !Number.isFinite(value.width)
    || !Number.isFinite(value.height) || value.width <= 0 || value.height <= 0)) {
    throw new Error("参考图信息无效，请重新添加图片。");
  }
  singleWorkbenchCancelInput({ render: false });
  const state = singleWorkbenchState;
  state.inputImage = value;
  state.single.input_image_id = value?.id || "";
  if (value && activate) {
    state.generationMode = "img2img";
    ui.base_mode.value = "normal";
    updateRepairControls();
  } else if (!value) state.generationMode = "txt2img";
  singleWorkbenchRenderInput();
  singleWorkbenchRenderMode();
  schedulePersist();
}

function singleWorkbenchInputFileError(files) {
  if (files.length !== 1) return "一次只能添加 1 张参考图。";
  const file = files[0];
  const type = String(file.type || "").toLowerCase();
  const knownType = ["image/png", "image/jpeg", "image/webp"].includes(type);
  const unknownType = !type || type === "application/octet-stream";
  if (!knownType && !(unknownType && /\.(png|jpe?g|webp)$/i.test(file.name || ""))) {
    return "参考图仅支持 PNG、JPEG、WebP。";
  }
  if (!file.size || file.size > SINGLE_INPUT_MAX_BYTES) return "参考图必须非空且不超过 20 MiB。";
  return "";
}

async function singleWorkbenchAcceptInput(files) {
  if (!singleWorkbenchCanReceiveInput()) return false;
  const list = Array.from(files || []);
  const error = singleWorkbenchInputFileError(list);
  if (error) {
    singleWorkbenchState.inputError = error;
    singleWorkbenchRenderInput();
    toast(error);
    return false;
  }
  return singleWorkbenchUpload(list[0]);
}

async function singleWorkbenchUpload(file) {
  if (!file || !singleWorkbenchActive()) return false;
  const error = singleWorkbenchInputFileError([file]);
  if (error) {
    singleWorkbenchState.inputError = error;
    singleWorkbenchRenderInput();
    toast(error);
    return false;
  }
  singleWorkbenchCancelInput({ render: false });
  const state = singleWorkbenchState, token = state.inputRequest, profile = currentProfile();
  const controller = new AbortController();
  state.inputAbort = controller;
  state.inputBusy = "upload";
  singleWorkbenchRenderInput();
  const current = () => token === state.inputRequest && profile === currentProfile() && singleWorkbenchActive();
  try {
    const body = new FormData();
    body.append("image", file);
    const response = await fetch("/api/input-images", { method: "POST", body, signal: controller.signal });
    const result = await response.json().catch(() => ({}));
    if (!current()) return false;
    if (!response.ok) throw new Error(result.error || "参考图上传失败，请重试。");
    singleWorkbenchSetInput(result);
    return true;
  } catch (error) {
    if (!current()) return false;
    state.inputError = error.name === "AbortError" ? "参考图上传已取消。" : error.message;
    toast(state.inputError);
    return false;
  } finally {
    if (current()) {
      state.inputAbort = null;
      state.inputBusy = "";
      singleWorkbenchRenderInput();
    }
  }
}

async function singleWorkbenchRestoreInput() {
  singleWorkbenchCancelInput({ render: false });
  const state = singleWorkbenchState, id = state.single.input_image_id;
  if (!id) {
    singleWorkbenchSetInput(null);
    return;
  }
  // A missing saved image remains visible and blocks generation until replaced or removed.
  state.inputImage = null;
  state.inputBusy = "restore";
  state.generationMode = "img2img";
  const token = state.inputRequest, profile = currentProfile(), controller = new AbortController();
  state.inputAbort = controller;
  singleWorkbenchRenderInput();
  const current = () => token === state.inputRequest && id === state.single.input_image_id
    && profile === currentProfile() && singleWorkbenchActive();
  try {
    const value = await request(`/api/input-images/${encodeURIComponent(id)}`, { signal: controller.signal });
    if (!current()) return;
    if (value.id !== id) throw new Error("返回的参考图与保存记录不匹配。");
    singleWorkbenchSetInput(value);
  } catch (error) {
    if (!current()) return;
    state.inputError = `参考图不可用：${error.message}。请重新添加或移除后生成。`;
  } finally {
    if (current()) {
      state.inputAbort = null;
      state.inputBusy = "";
      singleWorkbenchRenderInput();
    }
  }
}

function singleWorkbenchDragHasFiles(event) {
  const data = event.dataTransfer;
  return Boolean(data && (Array.from(data.types || []).includes("Files")
    || Array.from(data.items || []).some(item => item.kind === "file")));
}

function initializeSingleInput() {
  const state = singleWorkbenchState;
  if (state.inputInitialized) return;
  state.inputInitialized = true;
  const overlay = document.createElement("div");
  overlay.id = "singleDropOverlay";
  overlay.className = "single-drop-overlay";
  overlay.hidden = true;
  overlay.setAttribute("aria-hidden", "true");
  overlay.innerHTML = `<div class="single-drop-content"><div class="single-drop-icon"><svg viewBox="0 0 128 108" aria-hidden="true"><rect x="14" y="15" width="47" height="57" rx="12" fill="#9de3e9" transform="rotate(-19 38 44)"/><path d="m31 29 9 26m-17-9 26-9" stroke="white" stroke-width="4"/><rect x="66" y="21" width="47" height="57" rx="10" fill="#689bff" transform="rotate(16 89 50)"/><path d="m79 36 24 7m-26 1 20 6m-22 2 13 4" stroke="white" stroke-width="3"/><rect x="43" y="52" width="51" height="47" rx="13" fill="#3860ff"/><circle cx="76" cy="66" r="5" fill="white"/><path d="m51 87 9-13 9 10 5-6 10 9" fill="none" stroke="white" stroke-width="3.5" stroke-linejoin="round" stroke-linecap="round"/></svg></div><strong class="single-drop-title">松开以添加参考图</strong><span class="single-drop-hint">一次一张，PNG / JPEG / WebP，最大 20 MiB</span></div>`;
  document.body.append(overlay);
  document.getElementById("singleInputUpload").addEventListener("change", event => {
    const files = Array.from(event.target.files || []);
    event.target.value = "";
    if (files.length) void singleWorkbenchAcceptInput(files);
  });
  document.getElementById("singleInputRemove").addEventListener("click", () => singleWorkbenchSetInput(null));
  document.getElementById("singleUseInputSize").addEventListener("click", () => {
    const image = state.inputImage;
    if (!image || image.id !== state.single.input_image_id) return;
    ui.width.value = Math.max(64, Math.min(4096, Math.round(image.width / 8) * 8));
    ui.height.value = Math.max(64, Math.min(4096, Math.round(image.height / 8) * 8));
    schedulePersist();
    toast("已按原图尺寸设置，取 8 的倍数");
  });
  document.addEventListener("paste", event => {
    if (!singleWorkbenchCanReceiveInput()) return;
    const files = Array.from(event.clipboardData?.items || [])
      .filter(item => item.kind === "file" && item.type.startsWith("image/"))
      .map(item => item.getAsFile()).filter(Boolean);
    if (!files.length) return;
    event.preventDefault();
    void singleWorkbenchAcceptInput(files);
  });
  document.addEventListener("dragenter", event => {
    if (!singleWorkbenchCanReceiveInput() || !singleWorkbenchDragHasFiles(event)) return;
    event.preventDefault();
    state.inputDragDepth = (state.inputDragDepth || 0) + 1;
    overlay.hidden = false;
  });
  document.addEventListener("dragover", event => {
    if (!singleWorkbenchCanReceiveInput()) { singleWorkbenchHideDrop(); return; }
    if (!singleWorkbenchDragHasFiles(event)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
    state.inputDragDepth = Math.max(1, state.inputDragDepth || 0);
    overlay.hidden = false;
  });
  document.addEventListener("dragleave", event => {
    if (!state.inputDragDepth) return;
    state.inputDragDepth = Math.max(0, state.inputDragDepth - 1);
    if (!state.inputDragDepth || (event.relatedTarget === null
      && (event.target === document.documentElement || event.target === document))) singleWorkbenchHideDrop();
  });
  document.addEventListener("drop", event => {
    const accept = singleWorkbenchCanReceiveInput() && singleWorkbenchDragHasFiles(event);
    singleWorkbenchHideDrop();
    if (!accept) return;
    event.preventDefault();
    void singleWorkbenchAcceptInput(event.dataTransfer.files);
  });
  document.addEventListener("dragend", singleWorkbenchHideDrop);
  document.addEventListener("keydown", event => { if (event.key === "Escape") singleWorkbenchHideDrop(); });
  document.addEventListener("visibilitychange", () => { if (document.hidden) singleWorkbenchHideDrop(); });
  window.addEventListener("blur", singleWorkbenchHideDrop);
  window.addEventListener("pagehide", () => singleWorkbenchCancelInput({ render: false }));
  singleWorkbenchRenderInput();
}
