const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const app = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
const single = fs.readFileSync(path.join(__dirname, '../static/single-workbench.js'), 'utf8');
const clone = value => JSON.parse(JSON.stringify(value));
function install(ctx, name) {
  let start = app.indexOf(`function ${name}(`);
  if (app.slice(start - 6, start) === 'async ') start -= 6;
  vm.runInContext(app.slice(start, app.indexOf('\n}', start) + 2), ctx);
}
function startup(stored = null) {
  const storage = new Map(stored == null ? [] : [['anima-studio:workbenches:v1', JSON.stringify(stored)]]);
  const nodes = new Map(), writes = [], captures = [];
  const source = { pipeline_version: 5, model_profile: 'anima', workbench: 'random', count: 10, width: 832, height: 1216, steps: 30, dual: null };
  const node = id => { if (!nodes.has(id)) nodes.set(id, { value: '0', addEventListener() {}, focus() {} }); return nodes.get(id); };
  const ctx = vm.createContext({ clone, pipelineContractState: { version: 5, source, persistenceBlocked: false }, profileDrafts: {},
    currentProfile: () => 'anima', normalizeProfileId: value => value, SECTIONS: [],
    document: { getElementById: node, querySelectorAll: () => [] }, ui: { settingsForm: node('form'), model_name: node('model'), draftStatus: {} },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem(key, value) { writes.push(key); storage.set(key, value); }, removeItem: key => storage.delete(key) },
    window: { addEventListener() {} }, initializeSingleInput() {}, initializeSharedFavorites() {}, openPromptRuleDialog() {}, undoPromptNormalization() {},
    singleWorkbenchResetCompatibility() {},
    canonicalizeWorkflowSettings: async settings => clone(settings),
  });
  vm.runInContext(single, ctx);
  const state = vm.runInContext('singleWorkbenchState', ctx);
  ctx.singleWorkbenchBuild = () => {};
  ctx.singleWorkbenchRenderMode = ctx.singleWorkbenchResources = () => {};
  ctx.singleWorkbenchCapture = () => { captures.push(true); return { settings: { count: 0, width: 0, height: 0, steps: 0 }, profileDrafts: {} }; };
  return { ctx, state, source, storage, writes, captures };
}

test('workflow normalization posts a pure settings envelope without mutating its source', async () => {
  const calls = [], source = { pipeline_version: 2, dual: null, upscalers: { latent: { scale_by: 3.2 } } };
  const ctx = vm.createContext({ request: async (url, options) => { calls.push([url, JSON.parse(options.body)]); return { settings: { ...source, steps: 30 } }; } });
  install(ctx, 'canonicalizeWorkflowSettings');
  const before = clone(source), result = await ctx.canonicalizeWorkflowSettings(source);
  assert.deepEqual(calls, [['/api/settings/normalize', { settings: source }]]);
  assert.equal(result.pipeline_version, 2); assert.equal(result.dual, null); assert.deepEqual(source, before);
});

test('startup bootstraps its random draft from applied settings and cannot save or switch during validation', async () => {
  const h = startup(); let resolve; const checked = [];
  h.ctx.canonicalizeWorkflowSettings = settings => { checked.push(clone(settings)); return new Promise(yes => { resolve = () => yes(settings); }); };
  const initializing = h.ctx.initializeSingleWorkbench();
  assert.equal(h.state.initializing, true);
  assert.equal(h.ctx.singleWorkbenchPersist(), true);
  assert.equal(h.ctx.singleWorkbenchSwitch('single'), false);
  assert.equal(h.captures.length, 0); assert.equal(h.writes.length, 0);
  assert.deepEqual(checked, [h.source]);
  resolve(); await initializing;
  assert.equal(h.state.initializing, false);
  assert.equal(h.state.drafts.random.anima.settings.count, 10);
  assert.equal(h.state.drafts.random.anima.settings.width, 832);
  assert.equal(h.state.drafts.random.anima.settings.pipeline_version, 5);
});

test('invalid inactive workbench drafts stay intact and block pagehide autosave', async () => {
  const saved = { version: 1, mode: 'single', profile: 'anima', drafts: { random: { anima: { settings: { count: 0 } } }, single: { anima: { settings: { pipeline_version: 4, count: 1 } } } } };
  const h = startup(saved), before = h.storage.get('anima-studio:workbenches:v1');
  h.ctx.canonicalizeWorkflowSettings = async settings => { if (settings.count === 0) throw new Error('生成数量无效'); return settings; };
  await assert.rejects(h.ctx.initializeSingleWorkbench(), /原内容已保留.*生成数量无效/);
  assert.equal(h.ctx.pipelineContractState.persistenceBlocked, true);
  h.ctx.singleWorkbenchPersist();
  assert.equal(h.storage.get('anima-studio:workbenches:v1'), before);
  assert.equal(h.writes.length, 0);
});

test('a rejected legacy draft is neither deleted nor replaced', async () => {
  for (const value of ['{invalid-json', JSON.stringify({ version: 4, settings: { pipeline_version: 2, count: 0 } })]) {
    const storage = new Map([['draft', value]]), writes = [];
    const ctx = vm.createContext({ DRAFT_KEY: 'draft', LEGACY_DRAFT_KEYS: ['old'], clone,
      pipelineContractState: { persistenceBlocked: false }, localStorage: { getItem: key => storage.get(key), removeItem: key => { writes.push(key); storage.delete(key); } },
      canonicalizeWorkflowSettings: async () => { throw new Error('invalid count'); },
    });
    install(ctx, 'loadStoredDraft');
    await assert.rejects(ctx.loadStoredDraft(), /原内容已保留/);
    assert.equal(storage.get('draft'), value); assert.equal(writes.length, 0);
    assert.equal(ctx.pipelineContractState.persistenceBlocked, true);
  }
});

test('explicit clear after failed startup reloads without pagehide recreating an empty draft', () => {
  const h = startup({ version: 1, drafts: { random: {}, single: {} } });
  h.ctx.pipelineContractState.persistenceBlocked = true; h.state.ready = true;
  let handler, reloaded = false;
  Object.assign(h.ctx, { confirm: () => true, DRAFT_KEY: 'old-draft', LEGACY_DRAFT_KEYS: [], VIEW_KEY: 'view' });
  h.ctx.ui.clearDraft = { addEventListener: (_event, callback) => { handler = callback; } };
  h.ctx.window.location = { reload() { reloaded = true; h.ctx.singleWorkbenchPersist(); } };
  const start = app.indexOf('ui.clearDraft.addEventListener(');
  vm.runInContext(app.slice(start, app.indexOf('\n});', start) + 4), h.ctx);
  handler();
  assert.equal(reloaded, true); assert.equal(h.state.ready, false);
  assert.equal(h.storage.has('anima-studio:workbenches:v1'), false); assert.equal(h.writes.length, 0);
});
