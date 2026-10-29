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
  vm.runInContext('for (const section of SECTIONS) sharedFavoritesState.reconciled.set(`anima:${section}`, true); sharedFavoritesState.reconciled.set("krea2:prompt", true);', sandbox);
  return { ctx: sandbox, elements, messages, persists: () => persists, profile: value => profile = value, run: code => vm.runInContext(code, sandbox) };
}
function appFunction(name) {
  let start = app.indexOf(`function ${name}(`);
  if (app.slice(start - 6, start) === "async ") start -= 6;
  return app.slice(start, app.indexOf("\n}", start) + 2);
}
function favoriteElement() {
  return { value: "", disabled: false, listeners: {}, children: [], dataset: {},
    addEventListener(type, listener) { this.listeners[type] = listener; },
    replaceChildren(...children) { this.children = children; }, append(...children) { this.children.push(...children); },
    setAttribute() {}, toggleAttribute() {}, querySelector() { return this.tabs ||= favoriteElement(); } };
}
function renderedFavorite(model, section, extra) {
  const nodes = {};
  const fixture = context({
    document: { getElementById: id => nodes[id] ||= favoriteElement(), createElement: favoriteElement, querySelectorAll: () => [] },
    Option: function(label, value) { this.text = label; this.value = value; }, ...extra,
  });
  fixture.profile(model);
  const item = { id: "favorite-one", name: "artist-or-character-name", title: "雨夜收藏", text: "rain at night", groupIds: ["default"] };
  item.favorite_key = ["character", "artist"].includes(section) ? item.name : item.id;
  fixture.ctx.favoriteItem = item;
  fixture.ctx.promptTarget = { value: "Keep my prompt unchanged", selectionStart: 5, selectionEnd: 14,
    setRangeText(text, start, end) { this.value = this.value.slice(0, start) + text + this.value.slice(end); },
    closest: () => null, focus() {}, dispatchEvent() {} };
  fixture.run(`sharedFavoritesView().section=${JSON.stringify(section)}; sharedFavoritesState.data={groups:[{id:"default",name:"Default"}],items:[favoriteItem]}; sharedFavoritesState.items=[favoriteItem]; sharedFavoritesState.targets.single=promptTarget`);
  fixture.ctx.sharedFavoritesRender();
  return { ...fixture, item, remove: nodes.singleFavoriteList.children[0].children[1] };
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
test("saving a favorite sends title subtitle text and groups without selecting a random candidate", async () => {
  const calls = [];
  const { ctx, elements, run } = context({ request: async (url, options) => calls.push({ url, body: JSON.parse(options.body) }) });
  Object.assign(elements, {
    sharedFavoriteText: { value: "  blue sky  " }, sharedFavoriteName: { value: "Sky" }, sharedFavoriteSubtitle: { value: "  Clear weather  " },
    sharedFavoriteDialog: { close() {} },
  });
  ctx.document.querySelectorAll = () => [{ value: "default" }];
  run('sharedFavoritesState.editor={profile:"anima",section:"background",data:{groups:[]},busy:false}; sharedFavoriteValidate=()=>{}');
  await ctx.sharedFavoriteSave({ preventDefault() {} });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, "/api/favorites/background/text?model_profile=anima");
  assert.deepEqual(calls[0].body, { title: "Sky", subtitle: "Clear weather", text: "blue sky", groupIds: ["default"] });
  assert.equal(run("sharedFavoritesState.editor"), null);
});
test("AI metadata follows full source text but never a partial selection", () => {
  const calls = [];
  const target = { id: "rewriteResult", value: "Golden sunset", selectionStart: 0, selectionEnd: 0 };
  const { ctx, run } = context({ promptPoolMetadataForText: (...args) => { calls.push(args); return { title: "Sunset", subtitle: "Warm sky", prompt: target.value }; } });
  ctx.target = target; run('sharedFavoritesState.saveTargets.single = target');
  assert.equal(ctx.sharedFavoriteCapturedSource("single").metadata.title, "Sunset");
  target.selectionEnd = 6;
  assert.equal(ctx.sharedFavoriteCapturedSource("single").text, "Golden");
  assert.equal(ctx.sharedFavoriteCapturedSource("single").metadata, null);
  assert.equal(calls.length, 1);
  target.selectionEnd = target.value.length;
  assert.equal(ctx.sharedFavoriteCapturedSource("single").metadata.title, "Sunset");
  assert.equal(calls[1][2], target);
});

test("Krea saving is protected by both pool and favorite storage, then releases the pool guard for Anima", () => {
  let checks = 0;
  const { ctx, elements, run } = context({ protectStorageControls: () => checks++ });
  function guarded() {
    return { attrs: {}, toggleAttribute(name, enabled) { if (enabled) this.attrs[name] = ""; else delete this.attrs[name]; },
      setAttribute(name, value) { this.attrs[name] = value; }, removeAttribute(name) { delete this.attrs[name]; } };
  }
  elements.sharedFavoriteSave = guarded(); elements.sharedFavoriteCreateGroup = guarded();
  elements.sharedFavoriteText = { value: "Scene" };
  run('sharedFavoritesState.editor={profile:"krea2",section:"prompt",data:{groups:[]},busy:false}');
  ctx.sharedFavoriteValidate();
  assert.equal(elements.sharedFavoriteSave.attrs["data-storage-write"], "prompt_pools");
  assert.ok("data-favorite-extension-write" in elements.sharedFavoriteSave.attrs);
  run('sharedFavoritesState.editor.profile="anima"; sharedFavoritesState.editor.section="pose"');
  ctx.sharedFavoriteValidate();
  assert.ok(!("data-storage-write" in elements.sharedFavoriteSave.attrs));
  assert.ok("data-favorite-custom-write" in elements.sharedFavoriteSave.attrs);
  assert.equal(checks, 2);
});

test("single-only initialization and compact chips keep insert and unfavorite actions separate", async () => {
  const nodes = {}, calls = [], inserted = [];
  const { ctx, run } = context({
    document: { getElementById(id) { assert.ok(!id.startsWith("random"), "removed random favorite nodes must never be accessed"); return nodes[id] ||= favoriteElement(); },
      createElement: favoriteElement, querySelectorAll: () => [] },
    window: { addEventListener() {} }, localStorage: { getItem() { return null; }, setItem() {} },
    Option: function(label, value) { this.text = label; this.value = value; },
    confirm: () => true,
    request: async (url, options) => { calls.push({ url, body: JSON.parse(options.body) }); },
  });
  ctx.inserted = inserted;
  run('sharedFavoritesResetModel=()=>{}; sharedFavoriteInsert=(text)=>inserted.push(text)');
  ctx.initializeSharedFavorites();
  run('sharedFavoritesState.data={groups:[{id:"default",name:"Default"}],items:[{id:"pose1",groupIds:["default"]}]}; sharedFavoritesView().section="pose"; sharedFavoritesState.items=[{id:"pose1",favorite_key:"pose1",title:"Standing",text:"standing in rain"}]');
  ctx.sharedFavoritesRender();
  const chip = nodes.singleFavoriteList.children[0];
  assert.deepEqual(chip.children.map(button => button.textContent), ["Standing", "♥"]);
  chip.children[0].listeners.click();
  assert.deepEqual(inserted, ["standing in rain"]);
  await chip.children[1].listeners.click();
  assert.deepEqual(inserted, ["standing in rain"]);
  assert.deepEqual(calls[0].body, { id: "pose1", favorite: false });
});

for (const [model, section] of [
  ...["character", "clothing", "pose", "background", "expression", "artist"].map(section => ["anima", section]),
  ["krea2", "prompt"],
]) {
  test(`${model}/${section} unfavorite confirms first, prevents duplicate writes and allows retry after failure`, async () => {
    const prompts = [], pending = [];
    let accepted = false, remove;
    const fixture = renderedFavorite(model, section, {
      confirm: message => { prompts.push({ message, disabled: remove.disabled }); return accepted; },
      request: (url, options) => new Promise((resolve, reject) => pending.push({ url, options, resolve, reject })),
    });
    remove = fixture.remove;
    const unchanged = () => {
      assert.equal(fixture.ctx.promptTarget.value, "Keep my prompt unchanged");
      assert.equal(fixture.ctx.promptTarget.selectionStart, 5);
      assert.equal(fixture.ctx.promptTarget.selectionEnd, 14);
      assert.equal(fixture.persists(), 0);
      assert.equal(fixture.run("sharedFavoritesState.items.length"), 1);
    };

    await remove.listeners.click();
    assert.equal(prompts.length, 1);
    assert.match(prompts[0].message, /取消收藏.*雨夜收藏/);
    assert.equal(prompts[0].disabled, false, "confirmation must happen before disabling the chip");
    assert.equal(remove.disabled, false);
    assert.equal(pending.length, 0, "cancel must not make a mutation request");
    assert.deepEqual(fixture.messages, []);
    unchanged();

    accepted = true;
    const first = remove.listeners.click();
    assert.equal(prompts.length, 2);
    assert.equal(prompts[1].disabled, false);
    assert.equal(remove.disabled, true);
    assert.equal(pending.length, 1);
    assert.equal(pending[0].url, `/api/favorites/${section}/item?model_profile=${model}`);
    assert.equal(pending[0].options.method, "PUT");
    assert.deepEqual(JSON.parse(pending[0].options.body), {
      ...(section === "artist" ? { name: fixture.item.name } : { id: fixture.item.id }), favorite: false,
    });
    await remove.listeners.click();
    assert.equal(prompts.length, 2, "a pending write must not ask again");
    assert.equal(pending.length, 1, "a pending write must not send duplicate requests");
    unchanged();

    pending[0].reject(new Error("收藏服务暂时不可用"));
    await first;
    assert.deepEqual(fixture.messages, ["收藏服务暂时不可用"]);
    assert.equal(remove.disabled, false, "failure must leave the chip available for retry");
    unchanged();

    const retry = remove.listeners.click();
    assert.equal(prompts.length, 3);
    assert.equal(pending.length, 2);
    assert.equal(remove.disabled, true);
    assert.equal(pending[1].url, pending[0].url);
    assert.deepEqual(pending[1].options, pending[0].options);
    pending[1].resolve({ items: [] });
    await retry;
    assert.equal(fixture.messages.length, 1);
    unchanged();
  });
}

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
  elements.sharedFavoriteText = { value: "sunset" }; elements.sharedFavoriteName = { value: "Sunset" }; elements.sharedFavoriteSubtitle = { value: "Evening" };
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

test("source deletion invalidates pending readers and prunes every saved draft only in its owning model", () => {
  const notices = [], timers = [];
  const pool = () => ({ mode: "all", ids: ["gone", "keep"], excluded_ids: ["gone", "other"] });
  const { ctx, run, persists } = context({
    ui: { poolGrid: {} }, activeSection: "pose", draft: { pools: { pose: pool() }, fixed: { pose: "unchanged" }, modes: { pose: "fixed" } },
    profileDrafts: { anima: { pools: { pose: pool() } }, krea2: { kreaPromptPoolSelection: ["gone"] } },
    singleWorkbenchState: { drafts: { random: { anima: { settings: { pools: { pose: pool() } }, profileDrafts: { anima: { pools: { pose: pool() } } } } }, single: {} } },
    poolRequest: 1, favoritesRequest: 1, customGroupsRequest: 1, poolSelectionOperation: {}, poolResultContext: "old",
    favoritesData: { items: ["old"] }, favoritesAvailable: true, poolSelectionStats: new Map([["pose", {}]]), poseItemCache: new Map([["gone", {}], ["keep", {}]]),
    setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout() {},
    localStorage: { setItem: (key, value) => notices.push(JSON.parse(value)) },
  });
  run('sharedFavoritesView().section="pose"; sharedFavoritesState.ready=true; sharedFavoritesRender=()=>{}; sharedFavoritesState.items=[{id:"gone"}]');
  ctx.poolSourceChanged({ model_profile: "anima", section: "pose", deletedItemIds: ["gone"], updatedItemIds: ["keep"] });
  assert.deepEqual(plain(ctx.draft.pools.pose), { mode: "all", ids: ["keep"], excluded_ids: ["other"] });
  assert.deepEqual(plain(ctx.profileDrafts.anima.pools.pose.ids), ["keep"]);
  assert.deepEqual(plain(ctx.singleWorkbenchState.drafts.random.anima.settings.pools.pose.ids), ["keep"]);
  assert.deepEqual(plain(ctx.singleWorkbenchState.drafts.random.anima.profileDrafts.anima.pools.pose.ids), ["keep"]);
  assert.deepEqual(plain(ctx.profileDrafts.krea2.kreaPromptPoolSelection), ["gone"]);
  assert.equal(ctx.draft.fixed.pose, "unchanged"); assert.equal(ctx.draft.modes.pose, "fixed");
  assert.equal(ctx.poolRequest, 2); assert.equal(ctx.favoritesRequest, 2); assert.equal(ctx.customGroupsRequest, 2);
  assert.equal(ctx.poolSelectionOperation, null); assert.equal(ctx.poolResultContext, ""); assert.equal(ctx.ui.poolGrid.inert, true);
  assert.equal(ctx.poseItemCache.size, 0); assert.equal(ctx.poolSelectionStats.size, 0);
  assert.deepEqual(plain(run("sharedFavoritesState.items")), []);
  assert.equal(notices.length, 1); assert.deepEqual(notices[0].deletedItemIds, ["gone"]); assert.equal(timers.length, 1); assert.equal(persists(), 1);
});

test("cross-tab changes cancel old favorite reads without rebroadcasting or mutating current prompt text", async () => {
  let resolve; const writes = [];
  const { ctx, run } = context({
    request: () => new Promise(done => resolve = done), setTimeout: () => 1, clearTimeout() {},
    localStorage: { setItem: (...args) => writes.push(args) }, ui: { poolGrid: {} },
  });
  run('sharedFavoritesState.ready=true; sharedFavoritesState.profile="anima"; sharedFavoritesView().section="pose"; sharedFavoritesRender=()=>{}; sharedFavoritesStatus=()=>{}');
  const pending = ctx.sharedFavoritesLoad();
  ctx.sharedFavoritesChanged({ model_profile: "anima", section: "pose", deletedItemIds: ["deleted"] }, false);
  resolve({ items: [{ id: "deleted", groupIds: ["default"] }], groups: [] }); await pending;
  assert.deepEqual(plain(run("sharedFavoritesState.items")), []); assert.equal(writes.length, 0);
});

test("first-open reconciliation runs once per model and section and never emits a broadcast loop", async () => {
  const calls = [], notices = [];
  const { ctx, run } = context({ request: async (url, options) => { calls.push({ url, options }); return { model_profile: "anima", section: "pose", updatedItemIds: ["p"], deletedItemIds: [], favoritesSync: { state: "complete", updatedCount: 1, deletedCount: 0 } }; }, localStorage: { setItem: (...args) => notices.push(args) } });
  run("sharedFavoritesState.reconciled.clear()");
  await Promise.all([ctx.ensurePoolReconciled("anima", "pose"), ctx.ensurePoolReconciled("anima", "pose")]);
  await ctx.ensurePoolReconciled("anima", "pose");
  assert.equal(calls.length, 1); assert.equal(calls[0].url, "/api/favorites/pose/reconcile?model_profile=anima"); assert.equal(calls[0].options.method, "POST");
  assert.equal(notices.length, 0);
});

test("read refresh failures expose a read-only retry without replaying a mutation", async () => {
  let reads = 0, writes = 0;
  const { ctx, elements, run, profile } = context({ request: async () => { writes++; }, loadKreaPromptPool: async () => { reads++; } });
  profile("krea2");
  run('sharedFavoritesState.ready=true; sharedFavoritesState.profile="krea2"; sharedFavoritesLoad=async()=>{ throw new Error("offline"); }');
  await ctx.refreshPoolLifecycle();
  assert.equal(run("sharedFavoritesState.refreshFailed"), true); assert.equal(elements.poolRefreshNotice.hidden, false);
  run("sharedFavoritesLoad=async()=>{}"); await ctx.refreshPoolLifecycle();
  assert.equal(elements.poolRefreshNotice.hidden, true); assert.equal(reads, 2); assert.equal(writes, 0);
});

test("lifecycle retry stays read-only before reconciliation while an explicit first open still reconciles", async () => {
  const calls = [];
  const { ctx, run, profile } = context({
    request: async (url, options) => { calls.push({ url, method: options?.method || "GET" }); return { items: [], groups: [] }; },
    loadKreaPromptPool: async () => {},
  });
  profile("krea2");
  run('sharedFavoritesState.ready=true; sharedFavoritesState.profile="krea2"; sharedFavoritesState.reconciled.clear(); sharedFavoritesRender=()=>{}; sharedFavoritesStatus=()=>{}');
  await ctx.refreshPoolLifecycle(); await ctx.refreshPoolLifecycle();
  assert.deepEqual(calls.map(call => call.method), ["GET", "GET"]);
  assert.equal(run('sharedFavoritesState.reconciled.has("krea2:prompt")'), false);
  await ctx.sharedFavoritesLoad();
  assert.deepEqual(calls.map(call => call.method), ["GET", "GET", "POST", "GET"]);
  assert.equal(calls[2].url, "/api/favorites/prompt/reconcile?model_profile=krea2");
  assert.equal(run('sharedFavoritesState.reconciled.get("krea2:prompt")'), true);
});

test("partly committed saves preserve editor text and report recovery without automatically repeating writes", async () => {
  let writes = 0;
  const error = Object.assign(new Error("收藏存储不可写"), { payload: { source_committed: true, recovery_required: true, updatedItemIds: ["source"] } });
  const { ctx, elements, run } = context({ request: async () => { writes++; throw error; } });
  elements.sharedFavoriteText = { value: "sunset" }; elements.sharedFavoriteName = { value: "Sunset" }; elements.sharedFavoriteSubtitle = { value: "Evening" };
  ctx.document.querySelectorAll = () => [{ value: "default" }];
  run('sharedFavoritesState.editor={profile:"anima",section:"pose",data:{groups:[]},busy:false}; sharedFavoriteValidate=()=>{}');
  await ctx.sharedFavoriteSave({ preventDefault() {} });
  assert.match(elements.sharedFavoriteError.textContent, /源词条已提交/); assert.match(elements.sharedFavoriteError.textContent, /不要重复提交/); assert.match(elements.sharedFavoriteError.textContent, /恢复存储/);
  assert.equal(elements.sharedFavoriteText.value, "sunset"); assert.ok(run("sharedFavoritesState.editor")); assert.equal(writes, 1);
});

test("the shared Krea panel removes one favorite ID while preserving another alias of the same source", async () => {
  const calls = [], fixture = renderedFavorite("krea2", "prompt", { confirm: () => true, request: async (url, options) => calls.push({ url, body: JSON.parse(options.body) }) });
  const first = { ...fixture.item, source_item_id: "stable-source" };
  const second = { ...first, id: "favorite-two", favorite_key: "favorite-two", title: "另一个别名" };
  fixture.ctx.sameSourceFavorites = [first, second];
  fixture.run('sharedFavoritesState.data.items=sameSourceFavorites; sharedFavoritesState.items=sameSourceFavorites');
  fixture.ctx.sharedFavoritesRender();
  const list = fixture.ctx.document.getElementById("singleFavoriteList");
  assert.equal(list.children.length, 2);
  await list.children[0].children[1].listeners.click();
  assert.equal(calls.length, 1); assert.deepEqual(calls[0].body, { id: first.id, favorite: false });
  fixture.ctx.remainingFavorites = [second];
  fixture.run('sharedFavoritesState.data.items=remainingFavorites; sharedFavoritesState.items=remainingFavorites');
  fixture.ctx.sharedFavoritesRender();
  assert.equal(list.children.length, 1); assert.equal(list.children[0].children[0].textContent, "另一个别名");
});

test("Anima source cards cancel all linked favorites by source ID while adding remains item-ID based", async () => {
  for (const section of ["character", "clothing", "pose", "background", "expression"]) {
    const calls = [];
    const { ctx, run } = context({
      activeSection: section, favoritesData: {}, favoritesAvailable: true,
      requestFavorites: async (url, options) => { calls.push({ url, body: JSON.parse(options.body) }); return { items: [], groups: [] }; },
      reconcileFavoriteCollection: () => false, currentView: () => ({ filters: { collection: "" } }),
      renderPoolItems() {}, renderPoolSidebar() {}, loadPool: async () => {},
    });
    run(appFunction("toggleFavorite"));
    await ctx.toggleFavorite({ id: "source", favorite_ids: ["alias-one", "alias-two"] }, false);
    await ctx.toggleFavorite({ id: "source" }, true);
    assert.equal(calls.length, 2); assert.equal(calls[0].url, `/api/favorites/${section}/item`);
    assert.deepEqual(calls[0].body, { source_item_id: "source", favorite: false });
    assert.deepEqual(calls[1].body, { id: "source", favorite: true });
  }
});

test("the favorite editor opened from an Anima source card removes all source associations in one request", async () => {
  const calls = [];
  const { ctx, run } = context({
    activeSection: "pose", editingFavoriteItem: { id: "source", favorite_ids: ["alias-one", "alias-two"] }, favoritesData: {},
    ui: { favoriteDialog: {} }, requestFavorites: async (url, options) => { calls.push({ url, body: JSON.parse(options.body) }); return { items: [], groups: [] }; },
    reconcileFavoriteCollection: () => false, loadPool: async () => {},
  });
  ctx.motionDialog = () => {}; run(appFunction("removeFavorite")); await ctx.removeFavorite();
  assert.equal(calls.length, 1); assert.deepEqual(calls[0].body, { source_item_id: "source", favorite: false });
});

test("Anima item deletion sends one write, preserves a newer editor, and refreshes through the lifecycle only", async () => {
  let resolve; const calls = [], notices = [], dialogs = [];
  const { ctx, run } = context({
    activeSection: "pose", editingCustom: { id: "source" }, customEditorSession: 1, customWriteBusy: false,
    confirm: () => true, request: (url, options) => { calls.push({ url, options }); return new Promise(done => resolve = done); },
    ui: { customDialog: {} }, poolSourceChanged: value => notices.push(plain(value)),
  });
  ctx.poolSourceChanged = value => notices.push(plain(value));
  ctx.motionDialog = (...args) => dialogs.push(args); run(appFunction("deleteCustomItem"));
  const pending = ctx.deleteCustomItem(); await ctx.deleteCustomItem();
  assert.equal(calls.length, 1); assert.match(calls[0].url, /source\?model_profile=anima/);
  ctx.editingCustom = { id: "new-editor" }; ctx.customEditorSession++;
  resolve({ deleted: true, deletedItemIds: ["source"], favoritesSync: { state: "complete", deletedCount: 1 } }); await pending;
  assert.equal(notices.length, 1); assert.deepEqual(notices[0].deletedItemIds, ["source"]);
  assert.equal(dialogs.length, 0); assert.equal(ctx.customWriteBusy, false); assert.equal(ctx.editingCustom.id, "new-editor");
});

test("Anima failed item deletion keeps the editor and does not prune or broadcast success", async () => {
  const notices = [], dialogs = [];
  const { ctx, elements, run } = context({
    activeSection: "pose", editingCustom: { id: "source" }, customEditorSession: 1, customWriteBusy: false,
    confirm: () => true, request: async () => { throw Object.assign(new Error("收藏不可写"), { payload: { source_committed: false, recovery_required: true } }); },
    ui: { customDialog: {} }, poolSourceChanged: value => notices.push(value),
  });
  ctx.poolSourceChanged = value => notices.push(plain(value));
  ctx.motionDialog = (...args) => dialogs.push(args); run(appFunction("deleteCustomItem")); await ctx.deleteCustomItem();
  assert.equal(notices.length, 0); assert.equal(dialogs.length, 0); assert.equal(ctx.customWriteBusy, false);
  assert.match(elements.customMutationError.textContent, /更改未提交/); assert.match(elements.customMutationError.textContent, /恢复存储/);
});

test("Anima import overwrite broadcasts its source result without replaying writes for a list refresh", async () => {
  const calls = [], notices = [];
  const preview = { rows: [{ action: "overwrite", item: { id: "source" } }] };
  const { ctx, run } = context({
    activeSection: "pose", importPreview: preview, ui: { commitImport: {}, importBundleName: { value: "Bundle" }, importDialog: {} },
    selectedImportGroupIds: () => ["group"], request: async (url, options) => { calls.push({ url, body: JSON.parse(options.body) }); return { updatedItemIds: ["source"], deletedItemIds: [], bundleName: "Bundle", imported: 0, updated: 1, skipped: 0 }; },
    poolSourceChanged: value => notices.push(plain(value)),
  });
  ctx.poolSourceChanged = value => notices.push(plain(value));
  ctx.motionDialog = () => {}; run(appFunction("commitCustomImport")); await ctx.commitCustomImport();
  assert.equal(calls.length, 1); assert.equal(calls[0].url, "/api/custom-prompts/import"); assert.equal(calls[0].body.section, "pose");
  assert.deepEqual(notices[0].updatedItemIds, ["source"]); assert.equal(notices[0].model_profile, "anima");
});
