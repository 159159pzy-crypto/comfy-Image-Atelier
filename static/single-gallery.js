/* Single-image browsing owns its cursor and selection. Random archive state is
 * deliberately never read or changed here. Detail and fullscreen engines are shared. */
const singleGalleryState = {
  ready: false, active: false, loaded: false, items: [], total: 0,
  nextBefore: null, hasMore: false, loading: false, error: "", failedMore: false,
  epoch: 0, request: 0, pending: null,
  lastCompleted: null, live: null, busy: false, waitingBatch: null, terminalKey: "",
  viewerRecord: null, viewerOpen: false, selectionRequest: 0, navigationIntent: 0, navigationCursor: null,
  imageError: null, canvasError: "", canvasSource: "", failedImages: new Set(), retryDirection: null,
  generationKey: "", frameEpoch: 0, frameRequest: 0, framePending: false, requestedFrame: "", batchSnapshot: null,
  finishingBatch: null, finishingOptional: false, finishRequest: 0, finishingPromise: null,
};
const singleGalleryElement = id => document.getElementById(id);
function singleGalleryMergeRecords(existing, incoming) {
  const records = new Map(existing.map(record => [String(record.id), record]));
  for (const record of incoming || []) if (record?.settings?.workbench === "single") records.set(String(record.id), record);
  return [...records.values()].sort((a, b) => Number(b.id) - Number(a.id));
}
function singleGalleryInvalidate({ viewer = true } = {}) {
  const state = singleGalleryState;
  state.epoch++; state.request++; state.loading = false; state.pending = null;
  if (viewer) { state.selectionRequest++; state.navigationIntent++; state.navigationCursor = null; }
}
function singleGalleryMode(active) {
  const state = singleGalleryState;
  if (!state.ready || state.active === active) return;
  state.active = active;
  if (active) {
    if (state.finishingBatch) void singleGalleryFinishBatch(state.finishingBatch);
    else void singleGalleryLoad({ refresh: state.loaded });
    if (state.batchSnapshot && state.busy) singleGalleryBatch(state.batchSnapshot);
    singleGalleryCanvas();
  }
  else {
    singleGalleryInvalidate();
    singleGalleryClearLive();
    ++state.finishRequest; state.finishingPromise = null;
    if (imagePreview.context?.kind === "single") closeImagePreview({ immediate: true, restoreFocus: false });
    singleGalleryCanvas();
  }
}
async function singleGalleryLoad({ more = false, refresh = false } = {}) {
  const state = singleGalleryState;
  if (!state.ready || !state.active) return null;
  if (state.loading) {
    if (refresh) {
      const epoch = state.epoch;
      return state.pending?.then(() => epoch === state.epoch && state.active ? singleGalleryLoad({ refresh: true }) : null);
    }
    return state.pending;
  }
  if (!refresh && !more && state.loaded) return null;
  if (more && (!state.hasMore || !state.nextBefore)) return null;
  const before = more ? state.nextBefore : null, epoch = state.epoch, requestId = ++state.request;
  state.loading = true; state.error = ""; state.failedMore = more;
  singleGalleryRender();
  const pending = (async () => {
    try {
      const data = await request(`/api/history?workbench=single&limit=24${before ? `&before_id=${before}` : ""}`);
      if (epoch !== state.epoch || requestId !== state.request || !state.active) return null;
      if (typeof data.has_more !== "boolean" || !Object.prototype.hasOwnProperty.call(data, "next_before_id")) throw new Error("后台尚未启用历史游标，请重启后台服务后重试。");
      const hadItems = state.items.length > 0;
      const incoming = singleGalleryMergeRecords([], data.items);
      const priorIds = new Set(state.items.map(record => String(record.id)));
      // Never join disjoint cursor windows: unseen middle records must remain reachable.
      // A detail opened before this refresh keeps its own record until navigation.
      const resetWindow = !more && hadItems && incoming.length > 0 && !incoming.some(record => priorIds.has(String(record.id))) && Number(incoming.at(-1).id) > Number(state.items[0].id);
      state.items = resetWindow || Number(data.total) === 0 ? incoming : singleGalleryMergeRecords(state.items, incoming);
      state.total = Number(data.total) || 0;
      if (more || !hadItems || resetWindow || state.total === 0) {
        state.nextBefore = data.next_before_id || null;
        state.hasMore = Boolean(data.has_more);
      }
      state.loaded = true;
      const newest = state.items[0] || null;
      // The final canvas changes only after the finishing image has decoded.
      if (!state.busy && !state.finishingBatch && (!state.total || !state.lastCompleted || (newest && Number(newest.id) >= Number(state.lastCompleted.id)))) state.lastCompleted = newest;
      singleGalleryCanvas();
      return data;
    } catch (error) {
      if (epoch === state.epoch && requestId === state.request) state.error = error.message || "历史加载失败";
      return null;
    } finally {
      if (epoch === state.epoch && requestId === state.request) {
        state.loading = false; state.pending = null;
        singleGalleryCanvas(); singleGalleryRender();
      }
    }
  })();
  state.pending = pending;
  return pending;
}
function singleGalleryRefresh() { return singleGalleryLoad({ refresh: true }); }
function singleGalleryCanvas() {
  const state = singleGalleryState;
  if (!state.ready) return;
  const record = state.lastCompleted, source = state.active ? state.live?.src || (!state.busy && !state.finishingBatch && record ? `/api/images/${record.id}` : "") : "";
  const image = singleGalleryElement("singlePreviewImage"), open = singleGalleryElement("singlePreviewOpen");
  if (source !== state.canvasSource) { state.canvasSource = source; state.canvasError = ""; }
  if (source && image.getAttribute("src") !== source) image.src = source;
  if (!source) image.removeAttribute("src");
  image.hidden = !source;
  singleGalleryElement("singlePreviewEmpty").hidden = Boolean(source);
  open.disabled = state.busy || Boolean(state.finishingBatch) || !record;
  open.setAttribute("aria-label", state.busy || state.finishingBatch ? "正在生成，完成后可查看详情" : "查看单图生成详情");
  singleGalleryElement("singlePreviewStatus").textContent = state.busy
    ? `${singleGalleryProgressLabel(state.batchSnapshot)} · ${state.live ? "实时降噪预览" : "等待采样预览"}`
    : state.finishingBatch ? (state.error ? `最终图片加载失败：${state.error}` : "生成完成 · 正在载入最终图片…")
    : state.canvasError ? "图片加载失败，可重试或查看详情。"
    : state.error ? `历史加载失败：${state.error}`
    : record ? `${record.filename} · Seed ${record.sample_seed}`
    : state.loading ? "正在加载最近作品…" : "下一张作品，从这里开始";
  const retry = singleGalleryElement("singlePreviewRetry");
  retry.hidden = state.busy || (!state.error && !state.canvasError);
  retry.disabled = state.loading;
}
function singleGalleryLocation() {
  const state = singleGalleryState;
  return { page: 1, items: state.items, index: state.items.findIndex(record => String(record.id) === String(state.viewerRecord?.id)) };
}
function singleGalleryAvailable(direction) {
  const state = singleGalleryState, location = singleGalleryLocation();
  if (location.index < 0 && state.viewerRecord) return state.hasMore || state.items.some(record => direction > 0 ? Number(record.id) < Number(state.viewerRecord.id) : Number(record.id) > Number(state.viewerRecord.id));
  if (direction < 0) return location.index > 0;
  return location.index >= 0 && (location.index < state.items.length - 1 || state.hasMore);
}
function singleGalleryLabel(record) {
  const index = singleGalleryState.items.findIndex(item => String(item.id) === String(record?.id));
  return record ? `${record.filename || "单图作品"}${index >= 0 ? `，第 ${index + 1} 张，共 ${singleGalleryState.total} 张` : ""}` : "还没有单图作品";
}
function singleGalleryRender() {
  const state = singleGalleryState;
  if (!state.ready) return;
  const visible = imagePreview.context?.kind === "single" && imagePreview.phase !== "closed";
  const feedback = singleGalleryElement("singleViewerFeedback");
  const message = state.imageError ? "图片加载失败，可重试或切换另一张。" : state.error ? `历史加载失败：${state.error}` : state.loading ? "正在加载历史…" : "";
  feedback.hidden = !visible || !message;
  singleGalleryElement("singleViewerStatus").textContent = message;
  const retry = singleGalleryElement("singleViewerRetry");
  retry.hidden = !state.error && !state.imageError;
  retry.disabled = state.loading || ["loading", "opening", "switching", "closing"].includes(imagePreview.phase);
  if (visible) {
    if (state.imageError) ui.imagePreviewStatus.hidden = true;
    updatePreviewNavigation();
  }
}
function singleGalleryContext() {
  const state = singleGalleryState, owner = detailContext;
  return {
    kind: "single", record: state.viewerRecord,
    src: state.viewerRecord ? `/api/images/${state.viewerRecord.id}` : "",
    sourceElement: ui.detailImage, sourceContainer: ui.detailDialog, returnFocus: ui.detailImage,
    isValid: () => ui.detailDialog.open && detailContext === owner,
    history: { location: singleGalleryLocation, available: singleGalleryAvailable, label: singleGalleryLabel, navigate: singleGalleryNavigate },
    onRecordCommit: record => {
      if (detailContext !== owner || !ui.detailDialog.open) return;
      state.viewerRecord = record;
      imagePreview.context.src = `/api/images/${record.id}`;
      updateDetailRecord(record);
    },
    onOpen: () => { state.viewerOpen = true; state.imageError = null; state.retryDirection = null; singleGalleryRender(); void singleGalleryLoad(); },
    onReady: singleGalleryRender,
    onError: record => { state.imageError = record; if (record) state.failedImages.add(String(record.id)); singleGalleryRender(); },
    // Let a shared history read finish for the canvas, but cancel any pending navigation.
    onClose: () => { state.viewerOpen = false; state.selectionRequest++; state.navigationIntent++; state.navigationCursor = null; state.imageError = null; state.retryDirection = null; singleGalleryRender(); },
  };
}
function singleGalleryOpenDetail() {
  const state = singleGalleryState;
  if (!state.ready || !state.active || state.busy || state.finishingBatch || !state.lastCompleted) return;
  state.imageError = null;
  openDetail(state.lastCompleted, { origin: "single", returnFocus: singleGalleryElement("singlePreviewOpen") });
}
async function singleGallerySelect(record, { focus = false, navigation = false } = {}) {
  const state = singleGalleryState, context = imagePreview.context;
  if (!record || !state.viewerOpen || context?.kind !== "single" || imagePreview.phase === "closing") return false;
  const requestId = ++imagePreview.navigationRequest, selection = ++state.selectionRequest;
  if (!navigation) { ++state.navigationIntent; state.navigationCursor = null; }
  ++imagePreview.token;
  cancelImagePreviewAnimation();
  imagePreview.phase = "switching"; ui.imagePreviewDialog.classList.add("is-navigating");
  state.imageError = null; state.retryDirection = null;
  singleGalleryRender();
  const switched = await switchPreviewImage(record, requestId);
  if (selection !== state.selectionRequest || requestId !== imagePreview.navigationRequest || imagePreview.context !== context || !state.viewerOpen || imagePreview.phase === "closing") return false;
  if (switched) {
    state.viewerRecord = record;
    context.src = `/api/images/${record.id}`;
    state.failedImages.delete(String(record.id));
    if (selectedRecord !== record) updateDetailRecord(record);
    announcePreview(singleGalleryLabel(record));
  } else {
    if (typeof restorePreviewCommittedImage === "function") await restorePreviewCommittedImage(requestId, context);
    if (selection !== state.selectionRequest || requestId !== imagePreview.navigationRequest || imagePreview.context !== context || !state.viewerOpen || imagePreview.phase === "closing") return false;
    state.navigationCursor = null;
    state.imageError = record;
    state.failedImages.add(String(record.id));
    imagePreview.phase = "open";
    ui.imagePreviewDialog.classList.remove("is-navigating");
    announcePreview("图片加载失败，可重试或切换另一张");
  }
  singleGalleryRender();
  if (focus) ui.imagePreviewImage.focus({ preventScroll: true });
  return switched;
}
async function singleGalleryNavigate(direction, focusTarget = null) {
  const state = singleGalleryState;
  if (!["open", "opening", "switching", "error"].includes(imagePreview.phase) || !state.viewerOpen) return;
  const context = imagePreview.context;
  const epoch = state.epoch, intent = ++state.navigationIntent;
  const requestId = ++imagePreview.navigationRequest;
  const valid = () => epoch === state.epoch && state.viewerOpen && imagePreview.context === context && imagePreview.phase !== "closing" && imagePreview.phase !== "closed";
  const current = () => valid() && intent === state.navigationIntent;
  state.retryDirection = direction;
  imagePreview.phase = "switching";
  ui.imagePreviewDialog.classList.add("is-navigating");
  updatePreviewNavigation();
  const previous = state.navigationCursor?.context === context && state.navigationCursor.epoch === epoch
    ? state.navigationCursor.promise : Promise.resolve(state.viewerRecord);
  const cursor = { context, epoch, promise: previous.then(async from => {
    let position = () => state.items.findIndex(record => String(record.id) === String(from?.id));
    while (from && position() < 0 && state.hasMore && Number(state.items.at(-1)?.id) > Number(from.id)) {
      const count = state.items.length;
      await singleGalleryLoad({ more: true });
      if (!valid() || state.error || state.items.length === count) return from;
    }
    let index = position() + direction;
    if (position() < 0 && from) index = direction > 0 ? state.items.findIndex(record => Number(record.id) < Number(from.id)) : state.items.findLastIndex(record => Number(record.id) > Number(from.id));
    while (index >= 0 && valid()) {
      if (index >= state.items.length) {
        if (!state.hasMore) break;
        const count = state.items.length;
        await singleGalleryLoad({ more: true });
        if (!valid() || state.error || state.items.length === count) return from;
      }
      const record = state.items[index];
      if (!record) break;
      if (state.failedImages.has(String(record.id))) { index += direction; continue; }
      return record;
    }
    return from;
  }) };
  state.navigationCursor = cursor;
  const record = await cursor.promise;
  if (!current()) return;
  if (record && !state.error && String(record.id) === String(state.viewerRecord?.id)) {
    if (typeof restorePreviewCommittedImage === "function") await restorePreviewCommittedImage(requestId, context);
    if (!current()) return;
    imagePreview.phase = "open";
    ui.imagePreviewDialog.classList.remove("is-navigating");
    state.retryDirection = null;
    announcePreview(direction < 0 ? "已经是第一张图片" : "已经是最后一张图片");
    updatePreviewNavigation(); return;
  }
  if (record && !state.error) {
    await singleGallerySelect(record, { navigation: true });
    if (!current() || !["open", "error"].includes(imagePreview.phase)) return;
    if (focusTarget?.isConnected && !focusTarget.disabled) focusTarget.focus({ preventScroll: true });
    else ui.imagePreviewImage.focus({ preventScroll: true });
    return;
  }
  state.navigationCursor = null;
  if (typeof restorePreviewCommittedImage === "function") await restorePreviewCommittedImage(requestId, context);
  if (!current()) return;
  imagePreview.phase = "open";
  ui.imagePreviewDialog.classList.remove("is-navigating");
  if (!state.error) state.retryDirection = null;
  announcePreview(state.error || (direction < 0 ? "已经是第一张图片" : "已经是最后一张图片"));
  updatePreviewNavigation();
}
function singleGalleryProgressLabel(batch) {
  const node = batch?.progress_node || batch?.preview_node || "";
  const stage = node === "701:1245" ? "第一段采样" : node === "701:1246" ? "第二段采样"
    : node === "704:1248" ? "潜空间二次采样" : node.startsWith("712:") ? "SeedVR2"
    : node.startsWith("1253:") ? "脸部细化" : node.startsWith("1432:") ? "NVIDIA 处理"
    : node.includes("decode") || ["691:239", "701:539", "704:359"].includes(node) ? "图像解码" : "底图采样";
  const progress = batch?.progress;
  return progress?.max > 0 ? `${stage} ${progress.value}/${progress.max}` : batch?.stage === "prompt_processing" ? "正在处理提示词" : `正在生成 · ${stage}`;
}
function singleGalleryClearLive() {
  const state = singleGalleryState;
  ++state.frameRequest; state.framePending = false; state.requestedFrame = "";
  if (state.live?.src?.startsWith("blob:")) URL.revokeObjectURL(state.live.src);
  state.live = null;
}
async function singleGalleryDecode(source) {
  const image = new Image();
  image.src = source;
  await image.decode();
  return image;
}
async function singleGalleryFetchFrame(batch) {
  const state = singleGalleryState, identity = `${batch.id}:${batch.prompt_id || ""}`, epoch = Number(batch.preview_epoch || 0), frameKey = `${identity}:${epoch}:${batch.preview_id}`;
  if (!state.active || state.framePending || frameKey === state.requestedFrame || !batch.preview_id || !batch.prompt_id) return;
  if (state.live?.batchId === batch.id && state.live.promptId === batch.prompt_id && state.live.epoch === epoch && Number(batch.preview_id) <= state.live.previewId) return;
  state.framePending = true; state.requestedFrame = frameKey;
  const requestId = ++state.frameRequest;
  const valid = () => requestId === state.frameRequest && state.generationKey === identity && state.frameEpoch === epoch && state.busy && state.active;
  let source = "", committed = false;
  try {
    const query = new URLSearchParams({ batch_id: batch.id, prompt_id: batch.prompt_id, v: String(batch.preview_id) });
    if (batch.preview_epoch != null) query.set("epoch", String(epoch));
    if (batch.preview_node) query.set("node_id", batch.preview_node);
    const response = await fetch(`/api/batches/current/preview?${query}`, { cache: "no-store" });
    if (!response.ok || response.status === 204 || !valid()) { if (valid()) state.requestedFrame = ""; return; }
    const returnedPrompt = response.headers.get("X-Preview-Prompt-Id"), returnedBatch = response.headers.get("X-Preview-Batch-Id");
    if (returnedPrompt && returnedPrompt !== String(batch.prompt_id) || returnedBatch && returnedBatch !== String(batch.id)) return;
    const returnedNode = response.headers.get("X-Preview-Node-Id");
    if (returnedNode && batch.preview_node && returnedNode !== String(batch.preview_node)) return;
    const returnedEpoch = response.headers.get("X-Preview-Epoch");
    if (batch.preview_epoch != null && returnedEpoch !== String(epoch)) return;
    const previewId = Number(response.headers.get("X-Preview-Id")) || batch.preview_id;
    if (previewId < Number(batch.preview_id) || (state.live?.promptId === batch.prompt_id && state.live.epoch === epoch && previewId <= state.live.previewId)) return;
    const body = await response.blob();
    if (!body.type.startsWith("image/") || !valid()) return;
    source = URL.createObjectURL(body);
    await singleGalleryDecode(source);
    if (!valid()) return;
    const previous = state.live?.src;
    state.live = { batchId: batch.id, promptId: batch.prompt_id, epoch, previewId, src: source };
    state.requestedFrame = `${identity}:${epoch}:${previewId}`;
    source = ""; committed = true;
    singleGalleryCanvas();
    if (previous?.startsWith("blob:")) URL.revokeObjectURL(previous);
  } catch {
    if (valid()) state.requestedFrame = "";
    // A missed intermediate frame must never fail the actual generation.
  } finally {
    if (source) URL.revokeObjectURL(source);
    if (!committed && valid()) state.requestedFrame = "";
    if (requestId === state.frameRequest) state.framePending = false;
  }
}
async function singleGalleryFinishBatch(batchId) {
  const state = singleGalleryState;
  if (!state.active || state.finishingPromise) return state.finishingPromise;
  state.finishingBatch = batchId;
  const requestId = ++state.finishRequest;
  const valid = () => requestId === state.finishRequest && state.finishingBatch === batchId && !state.busy && state.active;
  const pending = (async () => {
    try {
      await singleGalleryRefresh();
      if (!valid()) return;
      const record = state.items.find(item => String(item.batch_id) === String(batchId));
      if (!record) {
        if (state.finishingOptional && !state.error) {
          singleGalleryClearLive(); state.finishingBatch = null; state.lastCompleted = state.items[0] || state.lastCompleted;
        } else if (!state.error) state.error = "尚未读取到本次作品，请重试";
        return;
      }
      await singleGalleryDecode(`/api/images/${record.id}`);
      if (!valid()) return;
      state.lastCompleted = record;
      singleGalleryClearLive(); state.finishingBatch = null; state.error = "";
    } catch (error) {
      if (valid()) state.error = error.message || "最终图片读取失败";
    } finally {
      if (requestId === state.finishRequest) { state.finishingPromise = null; singleGalleryCanvas(); singleGalleryRender(); }
    }
  })();
  state.finishingPromise = pending;
  return pending;
}
function singleGalleryBatch(batch) {
  const state = singleGalleryState;
  if (!state.ready) return;
  if (batch?.kind === "prompt_preview" || batch?.settings?.workbench !== "single") {
    const wasBusy = state.busy;
    const previous = state.waitingBatch;
    state.busy = false; state.waitingBatch = null;
    state.generationKey = ""; ++state.frameRequest; state.framePending = false;
    if (wasBusy && previous) { state.finishingBatch = previous; state.finishingOptional = true; void singleGalleryFinishBatch(previous); }
    singleGalleryCanvas();
    return;
  }
  if (["running", "stopping"].includes(batch.status)) {
    const key = `${batch.id}:${batch.prompt_id || ""}`;
    if (state.generationKey !== key) {
      singleGalleryClearLive(); ++state.finishRequest; state.finishingBatch = null; state.finishingOptional = false; state.finishingPromise = null;
      state.generationKey = key;
    }
    const epoch = Number(batch.preview_epoch || 0);
    if (state.frameEpoch !== epoch) {
      ++state.frameRequest; state.framePending = false; state.requestedFrame = "";
      state.frameEpoch = epoch;
    }
    state.batchSnapshot = batch;
    state.busy = true; state.waitingBatch = batch.id;
    void singleGalleryFetchFrame(batch);
    singleGalleryCanvas();
    return;
  }
  if (batch.status === "queued") return;
  const terminalKey = `${batch.id}:${batch.status}:${batch.completed || 0}`;
  if (state.terminalKey === terminalKey) return;
  state.terminalKey = terminalKey; state.waitingBatch = null; state.busy = false;
  state.generationKey = ""; ++state.frameRequest; state.framePending = false;
  if (batch.status === "completed") {
    state.finishingBatch = batch.id; state.finishingOptional = false;
    void singleGalleryFinishBatch(batch.id);
  } else {
    ++state.finishRequest; state.finishingBatch = null; state.finishingPromise = null;
    singleGalleryClearLive(); void singleGalleryRefresh();
  }
  singleGalleryCanvas(); singleGalleryRender();
}
function singleGalleryDetailClosed() {
  const state = singleGalleryState;
  state.viewerOpen = false; state.viewerRecord = null; state.imageError = null;
  state.selectionRequest++; state.navigationIntent++; state.navigationCursor = null; state.retryDirection = null;
  singleGalleryRender();
}
async function singleGalleryDeleted(id) {
  const state = singleGalleryState;
  // Invalidate list reads without cancelling a different image opened while DELETE waited.
  singleGalleryInvalidate({ viewer: false });
  let index = state.items.findIndex(record => String(record.id) === String(id));
  let totalRefreshed = false;
  while (index < 0 && state.hasMore && state.active && Number(state.items.at(-1)?.id) > Number(id)) {
    const count = state.items.length;
    totalRefreshed = Boolean(await singleGalleryLoad({ more: true })) || totalRefreshed;
    index = state.items.findIndex(record => String(record.id) === String(id));
    if (state.error || state.items.length === count) break;
  }
  // At a loaded boundary, resolve the older page before falling back to a newer image.
  if (index >= 0 && index === state.items.length - 1 && state.hasMore && state.active) {
    totalRefreshed = Boolean(await singleGalleryLoad({ more: true })) || totalRefreshed;
    index = state.items.findIndex(record => String(record.id) === String(id));
  }
  const neighbor = index >= 0 ? state.items[index + 1] || state.items[index - 1] || null : state.items.find(record => Number(record.id) < Number(id)) || state.items.findLast(record => Number(record.id) > Number(id)) || null;
  state.items = state.items.filter(record => String(record.id) !== String(id));
  if (index >= 0 && !totalRefreshed) state.total = Math.max(0, state.total - 1);
  state.failedImages.delete(String(id)); imagePreview.preloaded.delete(String(id));
  if (String(state.lastCompleted?.id) === String(id)) state.lastCompleted = state.items[0] || null;
  if (String(state.viewerRecord?.id) === String(id)) state.viewerRecord = neighbor;
  if (state.imageError && String(state.imageError.id) === String(id)) state.imageError = null;
  singleGalleryCanvas(); singleGalleryRender();
  if (!state.items.length && state.hasMore && state.active) void singleGalleryLoad({ more: true });
  return neighbor;
}
async function singleGalleryDeletedMany(ids) {
  const state = singleGalleryState, removed = new Set(ids.map(String));
  singleGalleryInvalidate({ viewer: false });
  const previous = state.viewerRecord || state.lastCompleted, before = state.items;
  const index = before.findIndex(record => String(record.id) === String(previous?.id));
  const older = index < 0 ? [] : before.slice(index + 1), newer = index < 0 ? before : before.slice(0, index).reverse();
  let neighbor = [...older, ...newer].find(record => !removed.has(String(record.id))) || null;
  state.items = before.filter(record => !removed.has(String(record.id)));
  state.total = Math.max(0, state.total - (before.length - state.items.length));
  for (const id of removed) { state.failedImages.delete(id); imagePreview.preloaded.delete(id); }
  if (removed.has(String(state.lastCompleted?.id))) state.lastCompleted = state.items[0] || null;
  if (removed.has(String(state.viewerRecord?.id))) state.viewerRecord = neighbor;
  if (removed.has(String(state.imageError?.id))) state.imageError = null;
  if (state.active) {
    await singleGalleryLoad({ refresh: true });
    neighbor ||= state.items.find(record => !removed.has(String(record.id))) || null;
    if (!state.viewerRecord && state.viewerOpen) state.viewerRecord = neighbor;
  } else state.loaded = false;
  singleGalleryCanvas(); singleGalleryRender();
  return neighbor;
}
async function singleGalleryRetryViewer() {
  const state = singleGalleryState;
  if (state.imageError) {
    const record = state.imageError;
    imagePreview.preloaded.delete(String(record.id));
    return singleGallerySelect(record, { focus: true });
  }
  const direction = state.retryDirection, epoch = state.epoch, selection = state.selectionRequest;
  await singleGalleryLoad({ more: state.failedMore, refresh: !state.failedMore });
  if (!state.error && direction && epoch === state.epoch && selection === state.selectionRequest && state.viewerOpen) return singleGalleryNavigate(direction);
}
function initializeSingleGallery() {
  const state = singleGalleryState;
  if (state.ready) return;
  state.ready = true;
  singleGalleryElement("singlePreviewOpen").addEventListener("click", singleGalleryOpenDetail);
  singleGalleryElement("singlePreviewRetry").addEventListener("click", () => {
    if (state.busy) return;
    if (state.finishingBatch) { state.error = ""; void singleGalleryFinishBatch(state.finishingBatch); return; }
    if (state.canvasError) { state.canvasError = ""; singleGalleryElement("singlePreviewImage").removeAttribute("src"); singleGalleryCanvas(); }
    if (state.error) void singleGalleryRefresh();
  });
  singleGalleryElement("singleViewerRetry").addEventListener("click", singleGalleryRetryViewer);
  singleGalleryElement("singlePreviewImage").addEventListener("error", () => { state.canvasError = state.canvasSource; singleGalleryCanvas(); });
  singleGalleryElement("singlePreviewImage").addEventListener("load", () => { state.canvasError = ""; singleGalleryCanvas(); });
  singleGalleryMode(singleWorkbenchActive());
  if (typeof currentBatch !== "undefined" && currentBatch) singleGalleryBatch(currentBatch);
}
