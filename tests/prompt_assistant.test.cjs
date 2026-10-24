const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../static/prompt-assistant.js'), 'utf8');
const singleSource = fs.readFileSync(path.join(__dirname, '../static/single-workbench.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
const poolItem = (prompt = 'rewritten text') => ({ title: '林间少女', subtitle: '午后的柔和光线', prompt });
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
function setup(saved = null) {
  const elements = {}, calls = [], timers = [], storage = new Map();
  if (saved) storage.set('anima-studio:prompt-assistant:v1', JSON.stringify(saved));
  const element = id => elements[id] ||= {
    id, value: '', hidden: false, disabled: false, textContent: '', listeners: {},
    replaceChildren(...options) { this.options = options; },
    addEventListener(name, fn) { (this.listeners[name] ||= []).push(fn); },
    dispatchEvent(event) { for (const fn of this.listeners[event.type] || []) fn({ target: this }); },
    focus() { this.focused = true; },
  };
  element('single_positive_prompt').value = 'existing body';
  const options = { available: true, template_only: true, services: [{ id: 'service', name: 'Service', models: [{ name: 'model' }] }], templates: [{ id: 'template', name: 'Template', category: 'user' }] };
  const ctx = vm.createContext({
    currentProfile: () => ctx.profile, profile: 'anima',
    document: { getElementById: element }, Option: function(name, id) { this.text = name; this.value = id; },
    Event: function(type) { this.type = type; }, setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout() {},
    localStorage: { setItem: (key, value) => storage.set(key, value), getItem: key => storage.get(key), removeItem: key => storage.delete(key) },
    schedulePersist() {}, request: async (url, options) => { calls.push({ url, options }); return options ? { id: 'job' } : url.includes('/options') ? plain(ctx.options) : { status: 'completed', result: { text: 'rewritten text' } }; },
    options,
  });
  vm.runInContext(source, ctx);
  const state = vm.runInContext('promptAssistantState', ctx);
  ctx.initializePromptAssistant();
  state.options = plain(options);
  function configured(profile = ctx.profile) {
    const draft = ctx.promptAssistantDraft(profile);
    Object.assign(draft, { source: 'rewrite me', service: 'service', model: 'model', template: 'template' });
    return draft;
  }
  return { ctx, state, elements, element, calls, timers, storage, configured };
}
test('initial rewrite input is blank and independent from generation; reading remains local', async () => {
  const { ctx, calls, elements } = setup();
  await new Promise(setImmediate);
  assert.equal(ctx.promptAssistantDraft().source, '');
  assert.equal(elements.single_positive_prompt.value, 'existing body');
  assert.equal(calls.length, 1); assert.equal(calls[0].url, '/api/prompt-assistant/options');
  ctx.promptAssistantReadCurrent();
  assert.equal(ctx.promptAssistantDraft().source, 'existing body');
  ctx.promptAssistantEdit('source', 'separate edit');
  assert.equal(elements.single_positive_prompt.value, 'existing body');
  assert.equal(calls.length, 1);
});
test('rewrite request sends selected identifiers and independent source, never generation or credentials', async () => {
  const { ctx, configured, calls, elements } = setup();
  const draft = configured();
  await ctx.promptAssistantStart();
  const post = calls.find(item => item.options?.method === 'POST');
  assert.deepEqual(JSON.parse(post.options.body), { kind: 'rewrite', model_profile: 'anima', source_text: 'rewrite me', service_id: 'service', model_name: 'model', template_id: 'template' });
  assert.equal(draft.result, 'rewritten text');
  assert.equal(elements.single_positive_prompt.value, 'existing body');
  assert.equal(draft.busy, false);
});
test('append uses a blank line, replace targets only正文, and undo protects subsequent editing', () => {
  const { ctx, configured, elements } = setup();
  const draft = configured();
  ctx.promptAssistantEdit('result', 'new paragraph');
  ctx.promptAssistantAdopt('append');
  assert.equal(elements.single_positive_prompt.value, 'existing body\n\nnew paragraph');
  ctx.promptAssistantUndo();
  assert.equal(elements.single_positive_prompt.value, 'existing body');
  ctx.promptAssistantAdopt('replace');
  assert.equal(elements.single_positive_prompt.value, 'new paragraph');
  elements.single_positive_prompt.value = 'new user edit';
  ctx.promptAssistantUndo();
  assert.equal(elements.single_positive_prompt.value, 'new user edit');
  assert.equal(draft.undo, null);
});
test('model drafts and selected options persist independently without inheriting正文', () => {
  const { ctx, configured, storage } = setup();
  configured(); ctx.promptAssistantEdit('source', 'anima input'); ctx.promptAssistantEdit('result', 'anima preview');
  ctx.profile = 'krea2';
  assert.equal(ctx.promptAssistantDraft().source, '');
  configured(); ctx.promptAssistantEdit('source', 'krea input'); ctx.promptAssistantEdit('result', 'krea preview');
  const saved = JSON.parse(storage.get('anima-studio:prompt-assistant:v1'));
  const reopened = setup(saved);
  assert.equal(reopened.ctx.promptAssistantDraft('anima').source, 'anima input');
  assert.equal(reopened.ctx.promptAssistantDraft('krea2').result, 'krea preview');
  assert.equal(reopened.ctx.promptAssistantDraft('krea2').template, 'template');
  assert.equal(reopened.elements.single_positive_prompt.value, 'existing body');
});
test('unavailable saved services remain visible and cannot silently select another provider', () => {
  const { ctx, configured, state, elements } = setup();
  const draft = configured(); draft.service = 'deleted-provider';
  ctx.promptAssistantRender();
  assert.equal(elements.rewriteService.value, 'deleted-provider');
  assert.equal(elements.rewriteService.options.at(-1).disabled, true);
  assert.equal(elements.rewriteStart.disabled, true);
  assert.match(ctx.promptAssistantIssue(draft), /不可用/);
  state.options.available = false; state.options.reason = 'repair plugin';
  assert.equal(ctx.promptAssistantIssue(draft), 'repair plugin');
});
test('late completion updates its own model draft without writing current model or正文', async () => {
  const { ctx, configured, elements } = setup();
  const draft = configured(), gate = deferred();
  ctx.request = async (url, options) => options ? { id: 'old-job' } : gate.promise;
  const pending = ctx.promptAssistantStart();
  await new Promise(setImmediate);
  ctx.profile = 'krea2'; const other = configured(); other.source = 'different source'; other.result = 'new preview';
  ctx.promptAssistantRender();
  gate.resolve({ status: 'completed', result: { text: 'old completion' } });
  await pending;
  assert.equal(draft.result, 'old completion');
  assert.equal(other.result, 'new preview');
  assert.equal(elements.rewriteResult.value, 'new preview');
  assert.equal(elements.single_positive_prompt.value, 'existing body');
});
test('editing input or preview while a task runs prevents its result from replacing newer work', async () => {
  for (const key of ['source', 'result']) {
    const { ctx, configured } = setup();
    const draft = configured(), gate = deferred();
    ctx.request = async (url, options) => options ? { id: 'job' } : gate.promise;
    const pending = ctx.promptAssistantStart();
    await new Promise(setImmediate);
    ctx.promptAssistantEdit(key, 'newer text');
    gate.resolve({ status: 'completed', result: { text: 'obsolete result' } });
    await pending;
    assert.notEqual(draft.result, 'obsolete result');
    assert.equal(draft[key], 'newer text');
    assert.equal(draft.busy, false);
  }
});
test('cancel keeps the job pending until stopping finishes and never adopts output', async () => {
  const { ctx, configured, timers } = setup();
  const draft = configured(); draft.jobId = 'job'; draft.busy = true;
  const calls = [];
  ctx.request = async (url, options) => { calls.push(url); return options ? {} : { status: calls.length < 3 ? 'stopping' : 'cancelled' }; };
  await ctx.promptAssistantCancel();
  await ctx.promptAssistantPoll('anima', 'job', draft.revision, ctx.promptAssistantFingerprint(draft));
  assert.equal(draft.busy, true); assert.equal(timers.length, 1);
  await timers[0]();
  assert.equal(draft.busy, false); assert.equal(draft.jobId, ''); assert.equal(draft.result, '');
});
test('cancelling while create POST is pending cancels the returned job and discards even a completed response', async () => {
  const { ctx, configured, elements } = setup(), create = deferred(), calls = [];
  const draft = configured();
  ctx.request = (url, options) => {
    calls.push(url);
    if (url === '/api/prompt-jobs') return create.promise;
    if (url.endsWith('/cancel')) return Promise.resolve({});
    return Promise.resolve({ status: 'completed', result: { text: 'must not adopt' } });
  };
  const pending = ctx.promptAssistantStart();
  await ctx.promptAssistantCancel();
  assert.equal(draft.cancelRequested, true);
  assert.equal(draft.busy, true);
  create.resolve({ id: 'late-created-job' });
  await pending;
  assert.ok(calls.includes('/api/prompt-jobs/late-created-job/cancel'));
  assert.equal(draft.result, '');
  assert.equal(draft.busy, false);
  assert.equal(elements.single_positive_prompt.value, 'existing body');
});
test('refresh restores a persisted task using reads, preserving its fingerprint and result ownership', async () => {
  const persisted = { source: 'saved source', service: 'service', model: 'model', template: 'template',
    result: '', resultFor: '', jobId: 'saved-job', jobRevision: 7, revision: 7, busy: true,
    jobFingerprint: JSON.stringify(['saved source', 'service', 'model', 'template']) };
  const { ctx, calls, elements } = setup({ version: 1, drafts: { krea2: persisted } });
  await new Promise(setImmediate);
  assert.equal(ctx.promptAssistantDraft('krea2').result, 'rewritten text');
  assert.equal(ctx.promptAssistantDraft('krea2').jobId, '');
  assert.ok(calls.some(item => item.url === '/api/prompt-jobs/saved-job'));
  assert.equal(calls.filter(item => item.options?.method === 'POST').length, 0);
  assert.equal(elements.rewriteResult.value, '', 'inactive Krea result must not replace Anima preview');
  assert.equal(elements.single_positive_prompt.value, 'existing body');
});
test('temporary result-read errors retain the job and retry GET without submitting another rewrite', async () => {
  const { ctx, configured, timers } = setup();
  const draft = configured(); draft.jobId = 'job'; draft.busy = true;
  let reads = 0;
  ctx.request = async () => { if (++reads === 1) throw new Error('offline'); return { status: 'completed', result: { text: 'recovered' } }; };
  await ctx.promptAssistantPoll('anima', 'job', draft.revision, ctx.promptAssistantFingerprint(draft));
  assert.equal(draft.jobId, 'job'); assert.equal(draft.busy, true);
  await timers[0]();
  assert.equal(draft.result, 'recovered'); assert.equal(reads, 2);
});
test('legacy overrides and completed jobs migrate only to preview, never正文 or a new request', async () => {
  const { ctx, elements, calls } = setup();
  ctx.promptAssistantMigrateLegacy({ prompt_job_id: 'legacy', processed_prompt_override: 'legacy final' });
  assert.equal(ctx.promptAssistantDraft().result, 'legacy final');
  assert.equal(ctx.promptAssistantDraft().legacy, true);
  assert.equal(elements.single_positive_prompt.value, 'existing body');
  assert.equal(calls.filter(item => item.options).length, 0);
  ctx.profile = 'krea2';
  ctx.promptAssistantMigrateLegacy({ prompt_job_id: 'stored-job', processed_prompt_override: null });
  await new Promise(setImmediate);
  assert.equal(ctx.promptAssistantDraft().result, 'rewritten text');
  assert.equal(calls.filter(item => item.options).length, 0);
});
test('old job migration cannot replace a preview edited while its read was pending', async () => {
  const { ctx } = setup(), gate = deferred();
  ctx.request = () => gate.promise;
  ctx.promptAssistantMigrateLegacy({ prompt_job_id: 'old-job' });
  ctx.promptAssistantEdit('result', 'my preview');
  gate.resolve({ status: 'completed', result: { text: 'legacy final' } });
  await new Promise(setImmediate);
  assert.equal(ctx.promptAssistantDraft().result, 'my preview');
});
test('clearing drafts invalidates requests already in flight', async () => {
  const { ctx, configured } = setup(), gate = deferred();
  configured(); ctx.request = () => gate.promise;
  const pending = ctx.promptAssistantStart();
  ctx.promptAssistantClear();
  gate.resolve({ id: 'old-job' });
  await pending;
  assert.equal(ctx.promptAssistantDraft().result, '');
  assert.equal(ctx.promptAssistantDraft().busy, false);
});
test('ordinary single settings force legacy processing off without dropping authored text or stored custom rules', () => {
  const { ctx } = setup();
  vm.runInContext(singleSource, ctx);
  const state = vm.runInContext('singleWorkbenchState', ctx);
  Object.assign(state, { ready: true, mode: 'single', single: { positive_prompt: 'body', prefix: 'prefix', suffix: 'suffix', prompt_job_id: 'old-job', processed_prompt_override: 'old result', prompt_processing: { expand: { enabled: true, rule: 'custom' }, oc: { enabled: true, character: 'OC' } } } });
  ctx.document.querySelectorAll = () => [];
  ctx.singleWorkbenchResourceValues = () => ({});
  ctx.SECTIONS = [];
  const result = ctx.singleWorkbenchRead({ model_profile: 'anima' });
  assert.equal(result.single.positive_prompt, 'body');
  assert.equal(result.single.prompt_processing.expand.rule, 'custom');
  assert.equal(result.single.prompt_processing.oc.character, 'OC');
  assert.equal(result.single.prompt_processing.expand.enabled, false);
  assert.equal(result.single.prompt_processing.oc.enabled, false);
  assert.equal(result.single.prompt_job_id, '');
  assert.equal(result.single.processed_prompt_override, null);
});

test('structured output stays hidden while exact live preview and complete adoption carry metadata', async () => {
  const { ctx, configured, elements } = setup();
  configured();
  ctx.request = async (url, options) => options ? { id: 'structured' } : { status: 'completed', result: { text: 'rewritten text', pool_item: poolItem() } };
  await ctx.promptAssistantStart();
  assert.equal(elements.rewriteResult.value, 'rewritten text');
  assert.deepEqual(plain(ctx.promptPoolMetadataForText('anima', 'rewritten text', elements.rewriteResult)), poolItem());
  assert.equal(ctx.promptPoolMetadataForText('krea2', 'rewritten text', elements.rewriteResult), null);
  elements.rewriteResult.selectionStart = 0; elements.rewriteResult.selectionEnd = 3;
  assert.equal(ctx.promptPoolMetadataForText('anima', 'rewritten text', elements.rewriteResult), null);
  elements.rewriteResult.selectionEnd = 0;
  ctx.promptAssistantAdopt('append');
  assert.equal(ctx.promptPoolMetadataForText('anima', elements.single_positive_prompt.value, elements.single_positive_prompt), null);
  ctx.promptAssistantAdopt('replace');
  assert.deepEqual(plain(ctx.promptPoolMetadataForText('anima', 'rewritten text', elements.single_positive_prompt)), poolItem());
  elements.single_positive_prompt.value = 'manual edit'; elements.single_positive_prompt.dispatchEvent(new ctx.Event('input'));
  elements.single_positive_prompt.value = 'rewritten text'; elements.single_positive_prompt.dispatchEvent(new ctx.Event('input'));
  assert.equal(ctx.promptPoolMetadataForText('anima', 'rewritten text', elements.single_positive_prompt), null);
});

test('manual preview edits and changed-then-restored inputs revoke metadata permanently', async () => {
  const { ctx, configured, elements } = setup();
  const draft = configured();
  ctx.request = async (url, options) => options ? { id: 'structured' } : { status: 'completed', result: { text: 'rewritten text', pool_item: poolItem() } };
  await ctx.promptAssistantStart();
  ctx.promptAssistantEdit('result', 'manual'); ctx.promptAssistantEdit('result', 'rewritten text');
  assert.equal(ctx.promptPoolMetadataForText('anima', 'rewritten text', elements.rewriteResult), null);
  await ctx.promptAssistantStart();
  const oldSource = draft.source; ctx.promptAssistantEdit('source', 'changed'); ctx.promptAssistantEdit('source', oldSource);
  assert.equal(ctx.promptPoolMetadataForText('anima', 'rewritten text', elements.rewriteResult), null);
});

test('metadata persists by profile and a failed model result preserves usable prior text', async () => {
  const original = setup(); original.configured();
  original.ctx.request = async (url, options) => options ? { id: 'structured' } : { status: 'completed', result: { text: 'rewritten text', pool_item: poolItem() } };
  await original.ctx.promptAssistantStart(); original.ctx.promptAssistantAdopt('replace');
  const saved = JSON.parse(original.storage.get('anima-studio:prompt-assistant:v1'));
  const restored = setup(saved); await new Promise(setImmediate);
  assert.deepEqual(plain(restored.ctx.promptPoolMetadataForText('anima', 'rewritten text', restored.elements.rewriteResult)), poolItem());
  assert.equal(restored.ctx.promptPoolMetadataForText('anima', 'existing body', restored.elements.single_positive_prompt), null);
  restored.ctx.request = async (url, options) => options ? { id: 'bad-format' } : { status: 'error', error: 'AI 返回的随机池格式无效' };
  await restored.ctx.promptAssistantStart();
  assert.equal(restored.elements.rewriteResult.value, 'rewritten text');
  assert.match(restored.elements.rewriteStatus.textContent, /格式无效/);
});
