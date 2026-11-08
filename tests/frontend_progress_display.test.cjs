const createFrontendContext = require('./helpers/frontend-context.cjs');
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
function setup() {
  const element = () => ({ hidden: false, textContent: '', style: {}, parentElement: { hidden: true } });
  const ui = Object.fromEntries(['batchState', 'progressCount', 'progressFill', 'sampleFill', 'sampleProgress', 'stopButton', 'startButton', 'batchError'].map(key => [key, element()]));
  const ctx = createFrontendContext({ ui, currentBatch: null, currentQueue: [], lastTerminalBatch: '', stopRequestId: '',
    renderQueue() {}, updateGenerationAvailability() {}, loadHistory() {} });
  for (const name of ['batchProgressLabel', 'loraRuntimeSummary', 'renderBatch']) {
    const start = source.indexOf(`function ${name}(`);
    vm.runInContext(source.slice(start, source.indexOf('\n}', start) + 2), ctx);
  }
  return { ctx, ui };
}
function batch(overrides = {}) {
  return { id: 'krea-batch', status: 'running', stage: 'generating', total: 40, completed: 0,
    prompt_id: 'prompt-1', settings: { workbench: 'random', model_profile: 'krea2' }, ...overrides };
}

test('LoRA warnings remain visible and clear when the next task has only successful bindings or is idle', () => {
  const { ctx } = setup();
  const notice = {hidden: true, textContent: ''};
  ctx.document = {getElementById: id => id === 'loraBatchNotice' ? notice : null};
  const applied = [{character_name: 'A', filename: 'bound.safetensors', strength: .8}];
  const warning = batch({id: 'warning-task', lora_runtime: {version: 1, applied,
    skipped: [{filename: 'missing.safetensors', message: '文件不存在'}]}, lora_warning_count: 2});

  ctx.renderBatch(warning);
  assert.equal(notice.hidden, false);
  assert.match(notice.textContent, /文件不存在/);
  assert.match(notice.textContent, /累计跳过 2/);
  assert.doesNotMatch(notice.textContent, /自动启用/);

  for (const count of [1, 3]) {
    ctx.renderBatch(batch({id: `successful-task-${count}`, lora_runtime: {version: 1,
      applied: Array.from({length: count}, () => applied[0]), skipped: []}, lora_warning_count: 0}));
    assert.equal(notice.hidden, true);
    assert.equal(notice.textContent, '');
  }

  ctx.renderBatch(warning);
  ctx.renderBatch(null);
  assert.equal(notice.hidden, true);
  assert.equal(notice.textContent, '');
});

test('server stage labels describe v3 progress and identify a failed stage', () => {
  const { ctx, ui } = setup();
  ctx.renderBatch(batch({ progress_stage: 'post_sampler', progress_stage_label: '整图后采样', progress_node: 'v3:post', progress: { value: 3, max: 16 } }));
  assert.equal(ui.sampleProgress.textContent, '整图后采样 3/16');
  ctx.renderBatch(batch({ status: 'error', error: '显存不足', failed_stage_label: '像素高清' }));
  assert.match(ui.batchError.textContent, /像素高清失败：显存不足/);
  ctx.renderBatch(batch({ status: 'error', error: '像素高清失败：显存不足', failed_stage_label: '像素高清' }));
  assert.equal(ui.batchError.textContent, '像素高清失败：显存不足');
});

test('preview-bearing batches update controls and reach the single workbench without an extra image request', async () => {
  for (const workbench of ['random', 'single']) {
    const { ctx, ui } = setup();
    const forwarded = [], requests = [];
    ctx.singleWorkbenchBatch = value => forwarded.push(value);
    ctx.fetch = async url => { requests.push(url); return { ok: false, status: 204 }; };
    const current = batch({ preview_id: 4, preview_epoch: 1, preview_node: '701:1245',
      progress_node: '701:1245', progress: { value: 3, max: 30 },
      settings: { workbench, model_profile: 'krea2' } });

    ctx.renderBatch(current);
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(ctx.currentBatch, current);
    assert.deepEqual(forwarded, [current]);
    assert.equal(ui.batchState.textContent, '正在生成');
    assert.equal(ui.progressCount.textContent, '0 / 40');
    assert.equal(ui.sampleProgress.textContent, '第一段采样 3/30');
    assert.equal(ui.sampleFill.style.transform, 'scaleX(0.1)');
    assert.equal(ui.stopButton.disabled, false);
    assert.equal(ui.startButton.textContent, '加入队列');
    assert.deepEqual(requests, []);
  }
});

test('Krea model loading and first-step wait remain visible without fabricated progress', () => {
  const { ctx, ui } = setup();
  for (const [node, label] of [['167:56', '正在准备模型'], ['1026:1011', '正在准备提示词'], ['701:1245', '第一段采样 · 等待首步']]) {
    ctx.renderBatch(batch({ progress_node: node }));
    assert.equal(ui.sampleProgress.hidden, false);
    assert.equal(ui.sampleProgress.textContent, label);
    assert.equal(ui.sampleFill.parentElement.hidden, true);
    assert.equal(ui.sampleFill.style.transform, 'scaleX(0)');
    assert.equal(ui.progressCount.textContent, '0 / 40');
  }
});

test('both profiles show real steps, reset on second sampler and show decode before completion', () => {
  for (const model_profile of ['anima', 'krea2']) {
    const { ctx, ui } = setup();
    const current = batch({ settings: { workbench: 'random', model_profile } });
    ctx.renderBatch({ ...current, progress_node: '701:1245', progress: { value: 12, max: 30 } });
    assert.equal(ui.sampleProgress.textContent, '第一段采样 12/30');
    assert.equal(ui.sampleFill.style.transform, 'scaleX(0.4)');
    assert.equal(ui.sampleFill.parentElement.hidden, false);
    ctx.renderBatch({ ...current, progress_node: '701:1246' });
    assert.equal(ui.sampleProgress.textContent, '第二段采样 · 等待首步');
    assert.equal(ui.sampleFill.parentElement.hidden, true);
    ctx.renderBatch({ ...current, progress_node: '701:1246', progress: { value: 3, max: 15 } });
    assert.equal(ui.sampleProgress.textContent, '第二段采样 3/15');
    assert.equal(ui.sampleFill.style.transform, 'scaleX(0.2)');
    ctx.renderBatch({ ...current, progress_node: '701:539' });
    assert.equal(ui.sampleProgress.textContent, '正在解码图像');
    assert.equal(ui.sampleFill.parentElement.hidden, true);
    ctx.renderBatch({ ...current, status: 'completed', completed: 40 });
    assert.equal(ui.sampleProgress.hidden, true);
    assert.equal(ui.progressFill.style.transform, 'scaleX(1)');
  }
});

test('submission, queued execution, disconnect and completed execution have distinct states', () => {
  const { ctx } = setup();
  assert.equal(ctx.batchProgressLabel(batch({ prompt_id: '' })), '正在提交任务');
  assert.equal(ctx.batchProgressLabel(batch()), '等待生成进度');
  assert.equal(ctx.batchProgressLabel(batch({ progress_node: '701:1245', preview_status: 'disconnected' })), '进度连接已断开，正在重连');
  assert.equal(ctx.batchProgressLabel(batch({ progress_node: '' })), '正在收取生成结果');
  assert.equal(ctx.batchProgressLabel(batch({ progress_node: 'v2:output' })), '正在保存图片');
  assert.equal(ctx.batchProgressLabel(batch({ stage: 'prompt_processing' })), '正在处理提示词');
});

test('normal sampling and postprocessing use their own measured step totals', () => {
  const { ctx } = setup();
  for (const [node, label] of [['691:1240', '底图采样'], ['704:1248', '潜空间二次采样'], ['712:1208', 'SeedVR2 采样']]) {
    assert.equal(ctx.batchProgressLabel(batch({ progress_node: node, progress: { value: 2, max: 8 } })), `${label} 2/8`);
  }
});

test('rewrite tasks retain queue activity but never refresh image history or claim image completion', () => {
  const { ctx, ui } = setup();
  const historyReads = [], forwarded = [];
  ctx.loadHistory = (...args) => historyReads.push(args);
  ctx.singleWorkbenchBatch = value => forwarded.push(value);
  ctx.lastTerminalBatch = 'last-image:completed:1';
  const rewrite = batch({ id: 'rewrite-job', kind: 'prompt_preview', stage: 'prompt_processing',
    total: 1, settings: { kind: 'rewrite', workbench: 'single', model_profile: 'krea2' } });
  ctx.renderBatch(rewrite);
  assert.equal(ctx.currentBatch.id, 'rewrite-job');
  assert.equal(ui.stopButton.disabled, false);
  assert.equal(ui.startButton.textContent, '加入队列');
  assert.equal(ui.batchState.textContent, '正在重写提示词');
  assert.equal(ui.sampleProgress.textContent, '正在处理提示词');
  ctx.renderBatch({ ...rewrite, status: 'completed', completed: 1 });
  assert.equal(ui.batchState.textContent, '提示词重写完成');
  assert.equal(ui.stopButton.disabled, true);
  assert.equal(ui.startButton.textContent, '开始生成');
  assert.equal(ctx.lastTerminalBatch, 'last-image:completed:1');
  assert.equal(historyReads.length, 0);
  assert.equal(forwarded.length, 2);
});

test('reverse jobs show image analysis labels without claiming image generation or refreshing history', () => {
  const { ctx, ui } = setup();
  const reads = []; ctx.loadHistory = (...args) => reads.push(args);
  const reverse = batch({ id: 'reverse-job', kind: 'prompt_preview', stage: 'prompt_processing', total: 1,
    settings: { kind: 'reverse', workbench: 'single', model_profile: 'anima' } });
  ctx.renderBatch(reverse);
  assert.equal(ui.batchState.textContent, '正在反推图像提示词');
  assert.equal(ui.sampleProgress.textContent, '正在反推图像提示词');
  ctx.renderBatch({ ...reverse, status: 'completed', completed: 1 });
  assert.equal(ui.batchState.textContent, '图像反推完成'); assert.deepEqual(reads, []);
});

test('a local stop exposes unconfirmed remote cancellation and clears it for the next job', () => {
  const { ctx, ui } = setup();
  const warning = '旧版 ComfyUI 无法安全取消正在执行的任务，远端任务可能继续执行';
  ctx.renderBatch(batch({ status: 'stopped', cancel_warning: warning }));
  assert.equal(ui.batchState.textContent, '本地已停止，ComfyUI 尚未确认');
  assert.equal(ui.batchError.hidden, false);
  assert.equal(ui.batchError.textContent, warning);
  assert.equal(ui.stopButton.disabled, true);
  ctx.renderBatch(batch({ id: 'next-batch' }));
  assert.equal(ui.batchError.hidden, true);
  assert.equal(ui.batchError.textContent, '');
});

test('stop confirmation has accurate local labels and never claims remote termination', () => {
  const { ctx, ui } = setup();
  ctx.renderBatch(batch({ status: 'stopping', cancel_state: 'pending', progress: { value: 3, max: 12 } }));
  assert.equal(ui.batchState.textContent, '正在取消当前任务');
  assert.match(ui.sampleProgress.textContent, /正在取消/);
  assert.equal(ui.sampleFill.parentElement.hidden, true);
  assert.equal(ui.stopButton.disabled, true);
  for (const cancel_state of ['confirmed', 'not_requested', undefined]) {
    ctx.renderBatch(batch({ status: 'stopped', cancel_state }));
    assert.equal(ui.batchState.textContent, '本地批次已停止');
    assert.equal(ui.batchError.hidden, true);
  }
  ctx.renderBatch(batch({ status: 'stopped', cancel_state: 'unconfirmed' }));
  assert.equal(ui.batchState.textContent, '本地已停止，ComfyUI 尚未确认');
  assert.match(ui.batchError.textContent, /远端任务可能继续/);
  ctx.renderBatch(batch({ kind: 'prompt_preview', status: 'stopped', cancel_state: 'unconfirmed', settings: { kind: 'reverse' } }));
  assert.equal(ui.batchState.textContent, '本地已停止，ComfyUI 尚未确认');
});

function stopSetup() {
  const result = setup(), { ctx } = result;
  const start = source.indexOf('async function stopCurrentBatch(');
  vm.runInContext(source.slice(start, source.indexOf('\n}', start) + 2), ctx);
  ctx.renderBatchPayload = payload => ctx.renderBatch(payload.batch, payload.queue || []);
  ctx.toast = () => {};
  ctx.renderBatch(batch());
  return result;
}

test('stop disables immediately, submits once, and keeps the real status until the response', async () => {
  const { ctx, ui } = stopSetup();
  const calls = []; let release;
  ctx.request = (url, options) => { calls.push([url, options]); return new Promise(resolve => { release = resolve; }); };
  const pending = ctx.stopCurrentBatch();
  await ctx.stopCurrentBatch();
  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], '/api/batches/krea-batch/stop');
  assert.equal(calls[0][1].method, 'POST');
  assert.equal(ctx.currentBatch.status, 'running');
  assert.equal(ui.batchState.textContent, '正在请求停止…');
  assert.equal(ui.stopButton.disabled, true);
  release({ batch: batch({ status: 'stopped', cancel_state: 'confirmed' }), queue: [] });
  await pending;
  assert.equal(ctx.stopRequestId, '');
  assert.equal(ui.batchState.textContent, '本地批次已停止');
  assert.equal(ui.stopButton.disabled, true);
});

test('a failed stop refreshes actual server status and permits retry only when still running', async () => {
  const { ctx, ui } = stopSetup();
  const calls = [], notices = [];
  ctx.toast = text => notices.push(text);
  ctx.request = async (url, options) => {
    calls.push(url);
    if (options?.method) throw new Error('network unavailable');
    return { batch: batch(), queue: [] };
  };
  await ctx.stopCurrentBatch();
  assert.deepEqual(calls, ['/api/batches/krea-batch/stop', '/api/batches/current']);
  assert.match(notices[0], /停止请求失败/);
  assert.equal(ctx.currentBatch.status, 'running');
  assert.equal(ui.stopButton.disabled, false);
  assert.equal(ctx.stopRequestId, '');
});

test('late stop responses do not replace a new batch or downgrade completion', async () => {
  for (const next of [batch({ id: 'next-batch' }), batch({ status: 'completed', completed: 40 })]) {
    const { ctx, ui } = stopSetup(); let release;
    ctx.request = () => new Promise(resolve => { release = resolve; });
    const pending = ctx.stopCurrentBatch();
    ctx.renderBatch(next);
    release({ batch: batch({ status: 'stopping', cancel_state: 'pending' }), queue: [] });
    await pending;
    assert.equal(ctx.currentBatch, next);
    assert.equal(ui.batchState.textContent, next.status === 'completed' ? '批次完成' : '正在生成');
  }
});

test('a delayed old stop does not block or clear a newer batch stop request', async () => {
  const { ctx, ui } = stopSetup();
  const pendingResponses = [];
  ctx.request = () => new Promise(resolve => pendingResponses.push(resolve));
  const first = ctx.stopCurrentBatch();
  ctx.renderBatch(batch({ id: 'next-batch' }));
  const second = ctx.stopCurrentBatch();
  assert.equal(pendingResponses.length, 2);
  assert.equal(ctx.stopRequestId, 'next-batch');
  pendingResponses[0]({ batch: batch({ status: 'stopped' }), queue: [] });
  await first;
  assert.equal(ctx.stopRequestId, 'next-batch');
  assert.equal(ui.stopButton.disabled, true);
  pendingResponses[1]({ batch: batch({ id: 'next-batch', status: 'stopped', cancel_state: 'confirmed' }), queue: [] });
  await second;
  assert.equal(ctx.stopRequestId, '');
  assert.equal(ctx.currentBatch.id, 'next-batch');
});

test('completed generation preserves the current page while selecting or browsing a collection', () => {
  for (const mode of ['selection', 'collection', 'ordinary']) {
    const { ctx } = setup(), reads = [];
    ctx.historyPage = 4;
    ctx.artworkSelectionActive = () => mode === 'selection';
    ctx.historyDetailState = { homeQuery: { source: '', folderId: mode === 'collection' ? 'portrait' : '' } };
    ctx.loadHistory = page => reads.push(page);
    ctx.renderBatch(batch({ status: 'completed', completed: 40 }));
    assert.deepEqual(reads, [mode === 'ordinary' ? 1 : 4]);
  }
});

test('new completed output invalidates stale page and fullscreen reads once per batch', () => {
  const { ctx } = setup(); let invalidations = 0;
  ctx.invalidateHistoryView = () => invalidations++;
  const completed = batch({ status: 'completed', completed: 40 });
  ctx.renderBatch(completed); ctx.renderBatch(completed);
  assert.equal(invalidations, 1);
  ctx.renderBatch({ ...completed, id: 'next' });
  assert.equal(invalidations, 2);
});
