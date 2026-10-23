const createFrontendContext = require('./helpers/frontend-context.cjs');
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const source = fs.readFileSync(path.join(__dirname, "../static/shared-favorites.js"), "utf8");
const app = fs.readFileSync(path.join(__dirname, "../static/app.js"), "utf8");
const plain = value => JSON.parse(JSON.stringify(value));
function context(extra = {}) {
  let profile = "anima", persists = 0;
  const elements = {}, messages = [];
  const sandbox = createFrontendContext({
    console, URLSearchParams, setTimeout, clearTimeout, structuredClone,
    SECTIONS: ["character", "clothing", "pose", "background", "expression"],
    SECTION_META: { character: { label: "角色" }, pose: { label: "姿势" } },
    currentProfile: () => profile, schedulePersist: () => persists++, toast: message => messages.push(message),
    Event: class { constructor(type, options) { this.type = type; Object.assign(this, options); } },
    document: { getElementById: id => elements[id] ||= { value: "", textContent: "", hidden: false }, querySelectorAll: () => [], contains: () => true },
    localStorage: { setItem() {} }, ...extra,
  });
  vm.runInContext(source, sandbox);
  return { ctx: sandbox, elements, messages, persists: () => persists, profile: value => profile = value, run: code => vm.runInContext(code, sandbox) };
}
function appFunction(name) {
  let start = app.indexOf(`function ${name}(`);
  if (app.slice(start - 6, start) === "async ") start -= 6;
  return app.slice(start, app.indexOf("\n}", start) + 2);
}
test("selected text wins over full field and whitespace-only content is rejected", () => {
  const { ctx } = context();
  assert.equal(ctx.sharedFavoriteSource({ value: "red, blue sky, green", selectionStart: 5, selectionEnd: 13 }), "blue sky");
  assert.equal(ctx.sharedFavoriteSource({ value: "  full prompt  ", selectionStart: 2, selectionEnd: 2 }), "full prompt");
  assert.equal(ctx.sharedFavoriteSource({ value: "  ", selectionStart: 0, selectionEnd: 0 }), "");
});
test("manual insert replaces selection at latest field and dispatches input for draft saving", () => {
  const { ctx, run, profile, persists } = context();
  const target = { value: "start end", selectionStart: 6, selectionEnd: 9, events: [], closest: () => null,
    setRangeText(text, start, end) { this.value = this.value.slice(0, start) + text + this.value.slice(end); },
    focus() { this.focused = true; }, dispatchEvent(event) { this.events.push(event.type); } };
  ctx.target = target; run('sharedFavoritesState.targets.single = target');
  ctx.sharedFavoriteInsert("sunset", "single");
  assert.equal(target.value, "start sunset"); assert.deepEqual(target.events, ["input"]); assert.equal(persists(), 1);
  profile("krea2"); target.value = "A scene"; target.selectionStart = target.selectionEnd = 7;
  ctx.sharedFavoriteInsert("Golden light.", "single");
  assert.equal(target.value, "A scene\nGolden light.");
});
test("unvisited workbenches default to main single prompt or random extra prompt", () => {
  const { ctx, elements } = context();
  elements.single_positive_prompt = { value: "single" }; elements.extra_prompt = { value: "random" };
  assert.equal(ctx.sharedFavoriteTarget("single"), elements.single_positive_prompt);
  assert.equal(ctx.sharedFavoriteTarget("random"), elements.extra_prompt);
});
test("favorites URLs always carry the explicit model including group deletion queries", () => {
  const { ctx } = context();
  assert.equal(ctx.sharedFavoriteUrl("prompt", "/text", "krea2"), "/api/favorites/prompt/text?model_profile=krea2");
  assert.equal(ctx.sharedFavoriteUrl("pose", "/groups/id?deleteItems=false", "anima"), "/api/favorites/pose/groups/id?deleteItems=false&model_profile=anima");
});
test("late favorites responses cannot populate a different model or section", async () => {
  const pending = [];
  const { ctx, run, profile } = context({ request: url => new Promise(resolve => pending.push({ url, resolve })) });
  run('sharedFavoritesState.ready=true; sharedFavoritesState.profile="anima"; sharedFavoritesStatus=()=>{}; sharedFavoritesRender=()=>{}');
  const old = ctx.sharedFavoritesLoad();
  profile("krea2"); run('sharedFavoritesState.profile="krea2"; sharedFavoritesState.epoch++');
  const fresh = ctx.sharedFavoritesLoad();
  pending[1].resolve({ items: [{ id: "k", text: "Krea scene", groupIds: ["default"] }], groups: [{ id: "default" }] });
  await fresh;
  pending[0].resolve({ items: [{ id: "anima", groupIds: ["default"] }] }); await old;
  assert.equal(run("sharedFavoritesState.items[0].id"), "k");
  profile("anima"); run('sharedFavoritesState.profile="anima"; sharedFavoritesState.epoch++; sharedFavoritesView().section="pose"');
  const pose = ctx.sharedFavoritesLoad();
  run('sharedFavoritesView().section="expression"');
  pending[2].resolve({ items: [{ id: "stale" }], groups: [] }); await pose;
  assert.equal(run("sharedFavoritesState.items[0].id"), "k");
  assert.equal(pending.length, 3, "stale favorites must not start stale pool reads");
});
test("per-model group choices survive loading renders and are validated only after data arrives", () => {
  const { ctx, run, profile } = context();
  ctx.document.getElementById = () => null;
  run('sharedFavoritesStatus=()=>{}; sharedFavoritesView().groups.character="anima-group"');
  ctx.sharedFavoritesRender(); assert.equal(run("sharedFavoritesView().groups.character"), "anima-group");
  profile("krea2"); run('sharedFavoritesView().groups.prompt="krea-group"');
  ctx.sharedFavoritesRender(); assert.equal(run("sharedFavoritesView().groups.prompt"), "krea-group");
  profile("anima"); assert.equal(run("sharedFavoritesView().groups.character"), "anima-group");
  run('sharedFavoritesState.data={groups:[{id:"default"}],items:[]}');
  ctx.sharedFavoritesRender(); assert.equal(run("sharedFavoritesView().groups.character"), "__all__");
});
test("group filtering includes descendants without looping on malformed cycles", () => {
  const { ctx } = context();
  assert.deepEqual([...ctx.sharedFavoriteGroupIds({ groups: [{ id: "b", parentId: "a" }, { id: "c", parentId: "b" }, { id: "a", parentId: "c" }] }, "a")], ["a", "b", "c"]);
});
test("saving a favorite sends only text and groups and never selects a random candidate", async () => {
  const calls = [];
  const { ctx, elements, run } = context({ request: async (url, options) => calls.push({ url, body: JSON.parse(options.body) }) });
  Object.assign(elements, {
    sharedFavoriteText: { value: "  blue sky  " }, sharedFavoriteName: { value: "Sky" },
    sharedFavoriteDialog: { close() {} },
  });
  ctx.document.querySelectorAll = () => [{ value: "default" }];
  run('sharedFavoritesState.editor={profile:"anima",section:"background",data:{groups:[]},busy:false}; sharedFavoriteValidate=()=>{}');
  await ctx.sharedFavoriteSave({ preventDefault() {} });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, "/api/favorites/background/text?model_profile=anima");
  assert.deepEqual(calls[0].body, { title: "Sky", text: "blue sky", groupIds: ["default"] });
  assert.equal(run("sharedFavoritesState.editor"), null);
});
test("Anima join uses include/all selection contract in random live state and dormant random draft", async () => {
  const { ctx, run } = context({
    singleWorkbenchActive: () => false, activeSection: "pose", renderDimensions() {}, updatePoolOverview() {}, renderPoolItems() {},
    draft: { pools: { pose: { mode: "include", ids: ["old"], excluded_ids: [] } }, modes: {} },
  });
  await ctx.sharedFavoriteAddToRandom({ id: "new" }, "anima", "pose");
  assert.deepEqual(plain(ctx.draft.pools.pose), { mode: "include", ids: ["old", "new"], excluded_ids: [] });
  assert.equal(ctx.draft.modes.pose, "pool");
  ctx.singleWorkbenchActive = () => true; ctx.singleWorkbenchPersist = () => {};
  ctx.singleWorkbenchState = { drafts: { random: { anima: { settings: { pools: { pose: { mode: "all", excluded_ids: ["new", "x"] } } }, profileDrafts: {} } } } };
  await ctx.sharedFavoriteAddToRandom({ id: "new" }, "anima", "pose");
  assert.deepEqual(plain(ctx.singleWorkbenchState.drafts.random.anima.settings.pools.pose), { mode: "all", excluded_ids: ["x"] });
  assert.equal(ctx.singleWorkbenchState.drafts.random.anima.settings.random_pose, true);
  assert.equal(run('sharedFavoritesState.profile'), "");
});
test("Krea join reuses ensured item and enables dormant random draft without altering single controls", async () => {
  const calls = [];
  const { ctx, profile } = context({
    request: async (url, options) => { calls.push({ url, body: JSON.parse(options.body) }); return { id: "ensured" }; },
    singleWorkbenchActive: () => true, singleWorkbenchPersist() {},
    singleWorkbenchState: { drafts: { random: { krea2: { settings: { krea_prompt_pool: { enabled: false, ids: ["existing"] } }, profileDrafts: { krea2: {} } } } } },
  });
  profile("krea2");
  await ctx.sharedFavoriteAddToRandom({ text: "An old snapshot", id: "favorite-id" }, "krea2", "prompt");
  const state = ctx.singleWorkbenchState.drafts.random.krea2;
  assert.deepEqual(plain(state.settings.krea_prompt_pool.ids), ["existing", "ensured"]);
  assert.equal(state.settings.krea_prompt_pool.enabled, true);
  assert.equal(state.profileDrafts.krea2.kreaPromptPoolEnabled, true);
  assert.deepEqual(calls, [{ url: "/api/prompt-pools/items/ensure", body: { model_profile: "krea2", text: "An old snapshot" } }]);
});
test("a Krea ensure response after model switch cannot alter either workbench selection", async () => {
  let resolve;
  const { ctx, profile } = context({ request: () => new Promise(done => resolve = done), singleWorkbenchActive: () => { throw new Error("must not write draft"); } });
  profile("krea2"); const pending = ctx.sharedFavoriteAddToRandom({ text: "snapshot" }, "krea2", "prompt");
  profile("anima"); resolve({ id: "new" }); await pending;
});
test("request wrapper scopes legacy favorites writes and broadcasts original model/section", async () => {
  const calls = [], notices = [];
  const { ctx, profile, run } = context({
    fetch: async (url, options) => { calls.push({ url, options }); return { ok: true, json: async () => ({ model_profile: "krea2" }) }; },
    singleWorkbenchFavoritesChanged: message => notices.push(plain(message)),
  });
  const start = app.indexOf("async function request(path, options = {})");
  const end = app.indexOf("\n}", start) + 2;
  run(app.slice(start, end)); profile("krea2");
  await ctx.request("/api/favorites/prompt/item", { method: "PUT", body: "{}" });
  assert.equal(calls[0].url, "/api/favorites/prompt/item?model_profile=krea2");
  assert.deepEqual(notices, [{ model_profile: "krea2", section: "prompt" }]);
});
test("editor group requests reject responses from its previous category", async () => {
  const pending = [], rendered = [];
  const { ctx, elements, run } = context({ request: url => new Promise(resolve => pending.push({ url, resolve })) });
  elements.sharedFavoriteGroups = { replaceChildren() {} };
  ctx.rendered = rendered;
  run('sharedFavoritesState.editor={profile:"anima",section:"pose",request:0}; sharedFavoriteRenderChecks=(groups)=>rendered.push(groups); sharedFavoriteValidate=()=>{}');
  const editor = run("sharedFavoritesState.editor");
  const pose = ctx.sharedFavoriteEditorGroups(editor); editor.section = "expression";
  const expression = ctx.sharedFavoriteEditorGroups(editor);
  pending[1].resolve({ groups: [{ id: "current" }] }); await expression;
  pending[0].resolve({ groups: [{ id: "stale" }] }); await pose;
  assert.deepEqual(plain(rendered), [[{ id: "current" }]]);
});
test("partial save failures leave editor and text available for idempotent retry", async () => {
  const { ctx, elements, run } = context({ request: async () => { throw new Error("提示词已保存，收藏待重试"); } });
  elements.sharedFavoriteText = { value: "sunset" }; elements.sharedFavoriteName = { value: "Sunset" };
  ctx.document.querySelectorAll = () => [{ value: "default" }];
  run('sharedFavoritesState.editor={profile:"anima",section:"pose",data:{groups:[]},busy:false}; sharedFavoriteValidate=()=>{}');
  await ctx.sharedFavoriteSave({ preventDefault() {} });
  assert.equal(elements.sharedFavoriteError.textContent, "提示词已保存，收藏待重试");
  assert.equal(elements.sharedFavoriteText.value, "sunset");
  assert.equal(run("sharedFavoritesState.editor.busy"), false);
});
test("completed mutation after switching profiles only broadcasts to the original model", async () => {
  let resolve; const notices = [];
  const { ctx, profile, run } = context({
    fetch: () => new Promise(done => resolve = done),
    singleWorkbenchFavoritesChanged: message => notices.push(plain(message)),
  });
  run(appFunction("request"));
  const pending = ctx.request("/api/favorites/pose/item", { method: "PUT", body: "{}" });
  profile("krea2"); resolve({ ok: true, json: async () => ({ items: ["anima"] }) });
  await assert.rejects(pending, /原模型完成/);
  assert.deepEqual(notices, [{ model_profile: "anima", section: "pose" }]);
});
test("a slow artist read cannot reopen its dialog in Krea", async () => {
  let resolve, opens = 0;
  const { ctx, run, profile } = context({ ui: { artistDialog: { showModal: () => opens++ } }, loadArtistFavorites: () => new Promise(done => resolve = done) });
  run(appFunction("openArtistDialog"));
  const pending = ctx.openArtistDialog(); profile("krea2"); resolve(); await pending;
  assert.equal(opens, 0);
});
test("bulk favorite completion for an old category does not replace the active category data", async () => {
  let resolve;
  const { ctx, run } = context({
    ui: { favoriteSelectionGroups: { querySelectorAll: () => [{ value: "default" }] }, confirmFavoriteSelection: {} },
    activeSection: "pose", draft: { pools: { pose: { mode: "include", ids: ["p"] } } },
    favoritesData: { items: ["current"] }, request: () => new Promise(done => resolve = done),
  });
  run(appFunction("saveFavoriteSelection"));
  const pending = ctx.saveFavoriteSelection({ preventDefault() {} });
  ctx.activeSection = "expression"; resolve({ items: ["stale"] }); await pending;
  assert.deepEqual(ctx.favoritesData.items, ["current"]);
});
