const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
function setup() {
  const element = () => ({ hidden: false, textContent: '', style: {}, parentElement: { hidden: true } });
  const ui = Object.fromEntries(['batchState', 'progressCount', 'progressFill', 'sampleFill', 'sampleProgress', 'stopButton', 'startButton', 'batchError'].map(key => [key, element()]));
  const ctx = vm.createContext({ ui, currentBatch: null, currentQueue: [], lastTerminalBatch: '',
    renderQueue() {}, renderRandomPreview() {}, updateGenerationAvailability() {}, loadHistory() {} });
  for (const name of ['batchProgressLabel', 'renderBatch']) {
    const start = source.indexOf(`function ${name}(`);
    vm.runInContext(source.slice(start, source.indexOf('\n}', start) + 2), ctx);
  }
  return { ctx, ui };
}
function batch(overrides = {}) {
  return { id: 'krea-batch', status: 'running', stage: 'generating', total: 40, completed: 0,
    prompt_id: 'prompt-1', settings: { workbench: 'random', model_profile: 'krea2' }, ...overrides };
}

test('Krea model loading and first-step wait remain visible without fabricated progress', () => {
  const { ctx, ui } = setup();
  for (const [node, label] of [['167:56', '正在准备模型'], ['1026:1011', '正在准备提示词'], ['701:1245', '第一段采样 · 等待首步']]) {
    ctx.renderBatch(batch({ progress_node: node }));
    assert.equal(ui.sampleProgress.hidden, false);
    assert.equal(ui.sampleProgress.textContent, label);
    assert.equal(ui.sampleFill.parentElement.hidden, true);
    assert.equal(ui.sampleFill.style.width, '0%');
    assert.equal(ui.progressCount.textContent, '0 / 40');
  }
});

test('both profiles show real steps, reset on second sampler and show decode before completion', () => {
  for (const model_profile of ['anima', 'krea2']) {
    const { ctx, ui } = setup();
    const current = batch({ settings: { workbench: 'random', model_profile } });
    ctx.renderBatch({ ...current, progress_node: '701:1245', progress: { value: 12, max: 30 } });
    assert.equal(ui.sampleProgress.textContent, '第一段采样 12/30');
    assert.equal(ui.sampleFill.style.width, '40%');
    assert.equal(ui.sampleFill.parentElement.hidden, false);
    ctx.renderBatch({ ...current, progress_node: '701:1246' });
    assert.equal(ui.sampleProgress.textContent, '第二段采样 · 等待首步');
    assert.equal(ui.sampleFill.parentElement.hidden, true);
    ctx.renderBatch({ ...current, progress_node: '701:1246', progress: { value: 3, max: 15 } });
    assert.equal(ui.sampleProgress.textContent, '第二段采样 3/15');
    assert.equal(ui.sampleFill.style.width, '20%');
    ctx.renderBatch({ ...current, progress_node: '701:539' });
    assert.equal(ui.sampleProgress.textContent, '正在解码图像');
    assert.equal(ui.sampleFill.parentElement.hidden, true);
    ctx.renderBatch({ ...current, status: 'completed', completed: 40 });
    assert.equal(ui.sampleProgress.hidden, true);
    assert.equal(ui.progressFill.style.width, '100%');
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
  assert.equal(ui.batchState.textContent, '正在处理提示词');
  assert.equal(ui.sampleProgress.textContent, '正在处理提示词');
  ctx.renderBatch({ ...rewrite, status: 'completed', completed: 1 });
  assert.equal(ui.batchState.textContent, '提示词处理完成');
  assert.equal(ui.stopButton.disabled, true);
  assert.equal(ui.startButton.textContent, '开始生成');
  assert.equal(ctx.lastTerminalBatch, 'last-image:completed:1');
  assert.equal(historyReads.length, 0);
  assert.equal(forwarded.length, 2);
});
