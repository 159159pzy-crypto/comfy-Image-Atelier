const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require('node:path').join(__dirname, '../static/app.js'), 'utf8');
function fn(name) { let start = source.indexOf(`function ${name}(`); if (source.slice(start - 6, start) === 'async ') start -= 6; return source.slice(start, source.indexOf('\n}', start) + 2); }
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
function setup() {
  const elements = {}, calls = [];
  const field = id => elements[id] ||= { value: '', textContent: '', hidden: false, checked: false, focus() {} };
  const ui = Object.fromEntries(['promptRuleFrom', 'promptRuleTo', 'promptRuleId', 'promptRulePositive', 'promptRuleNegative', 'promptRuleLora', 'deletePromptRule', 'promptRuleError', 'undoPromptNormalization', 'promptNormalizationStatus'].map(id => [id, field(id)]));
  ui.promptRulePositive.checked = true;
  const ctx = vm.createContext({ ui, document: { getElementById: field }, currentProfile: () => ctx.profile, profile: 'anima', promptRulesProfile: 'anima', promptRulesRequest: 0, promptRules: [], editingPromptRule: null, promptRuleEditorRevision: 0,
    renderPromptRules() {}, request: async (url, options) => { calls.push({ url, options }); return { items: [] }; }, toast() {},
    normalizationUndo: null, singleWorkbenchState: { ready: true, mode: 'single' }, invalidatePromptNormalization() {}, singleWorkbenchInvalidatePrompt() {}, schedulePersist() {},
    promptElementForField: id => field(id), applyPromptField: (id, value) => { field(id).value = value; },
    PROMPT_FIELD_LABELS: {},
  });
  for (const name of ['loadPromptRules', 'clearPromptRuleEditor', 'editPromptRule', 'setPromptRuleError', 'promptRuleScopes', 'savePromptRule', 'deletePromptRule', 'setNormalizationUndo', 'clearNormalizationUndo', 'undoPromptNormalization']) vm.runInContext(fn(name), ctx);
  return { ctx, elements, field, calls };
}
test('rule reads use model query and discard responses from an older model', async () => {
  const { ctx, elements } = setup(), requests = [];
  ctx.request = (url, options) => { const gate = deferred(); requests.push({ url, gate }); return gate.promise; };
  const old = ctx.loadPromptRules();
  ctx.profile = 'krea2';
  const fresh = ctx.loadPromptRules();
  assert.match(requests[1].url, /model_profile=krea2/);
  requests[1].gate.resolve({ items: [{ id: 'natural-whitespace' }] }); await fresh;
  requests[0].gate.resolve({ items: [{ id: 'lowercase-tags' }] }); await old;
  assert.equal(ctx.promptRules[0].id, 'natural-whitespace');
  assert.equal(ctx.promptRulesProfile, 'krea2');
  assert.match(elements.promptRuleTitle.textContent, /Krea 2/);
  assert.match(elements.promptRuleHint.textContent, /区分大小写/);
});
test('rule create, update and delete all carry the captured model', async () => {
  const { ctx, calls } = setup();
  ctx.profile = ctx.promptRulesProfile = 'krea2';
  ctx.ui.promptRuleFrom.value = 'Line one,\nLine two';
  ctx.ui.promptRuleTo.value = 'Replacement';
  await ctx.savePromptRule({ preventDefault() {} });
  assert.equal(JSON.parse(calls[0].options.body).model_profile, 'krea2');
  assert.equal(JSON.parse(calls[0].options.body).from, 'Line one,\nLine two');
  ctx.editPromptRule({ id: 'rule', kind: 'custom', from: 'old', to: 'new', scopes: ['positive'] });
  await ctx.savePromptRule({ preventDefault() {} });
  assert.equal(calls.find(item => item.options?.method === 'PUT').url, '/api/prompt-rules/rule');
  assert.equal(JSON.parse(calls.find(item => item.options?.method === 'PUT').options.body).model_profile, 'krea2');
  ctx.editPromptRule({ id: 'rule', kind: 'custom', from: 'old', to: 'new', scopes: ['positive'] });
  await ctx.deletePromptRule();
  assert.equal(calls.find(item => item.options?.method === 'DELETE').url, '/api/prompt-rules/rule?model_profile=krea2');
});
test('a completed save cannot clear edits made after its request or another model editor', async () => {
  for (const edit of ['new input', 'profile']) {
    const { ctx } = setup(), gate = deferred();
    ctx.ui.promptRuleFrom.value = 'original'; ctx.ui.promptRuleTo.value = 'replacement';
    ctx.request = () => gate.promise;
    const pending = ctx.savePromptRule({ preventDefault() {} });
    ctx.ui.promptRuleFrom.value = 'new work'; ctx.promptRuleEditorRevision++;
    if (edit === 'profile') ctx.profile = 'krea2';
    gate.resolve({});
    await pending;
    assert.equal(ctx.ui.promptRuleFrom.value, 'new work');
  }
});
test('single normalization undo restores captured field only and preserves subsequent edits', () => {
  const { ctx, field } = setup();
  const target = field('single_positive_prompt'); target.value = 'normalized';
  ctx.setNormalizationUndo([{ field: target.id, before: 'original', after: 'normalized' }], { [target.id]: target });
  assert.equal(field('singleUndoNormalization').hidden, false);
  ctx.undoPromptNormalization();
  assert.equal(target.value, 'original');
  target.value = 'normalized';
  ctx.setNormalizationUndo([{ field: target.id, before: 'original', after: 'normalized' }], { [target.id]: target });
  target.value = 'continued edit';
  ctx.undoPromptNormalization();
  assert.equal(target.value, 'continued edit');
});
test('normalization undo cannot write a different model or workbench draft', () => {
  for (const change of ['profile', 'workbench']) {
    const { ctx, field } = setup();
    const target = field('single_positive_prompt'); target.value = 'normalized';
    ctx.setNormalizationUndo([{ field: target.id, before: 'original', after: 'normalized' }], { [target.id]: target });
    if (change === 'profile') ctx.profile = 'krea2'; else ctx.singleWorkbenchState.mode = 'random';
    ctx.undoPromptNormalization();
    assert.equal(target.value, 'normalized');
  }
});
