const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const read = file => fs.readFileSync(path.join(__dirname, '../static', file), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
const tick = () => new Promise(setImmediate);
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const metadata = (overrides = {}) => ({ source: 'metadata', format: 'A1111', text: 'a quiet forest', negative: 'blur', parameters: { Seed: '123' }, candidates: [], warnings: [], ...overrides });
const asset = (id = 'image-a', details = metadata()) => ({ id, filename: `${id}.png`, width: 640, height: 480, size: 100, image_url: `/api/prompt-images/${id}/image`, metadata: details });
const file = name => ({ name: name || 'test.png', type: 'image/png', size: 100 });
const response = (value, ok = true) => ({ ok, json: async () => value });
function target(id = '') {
  const handlers = {}, attributes = {}, classes = new Set();
  return { id, value: '', hidden: false, disabled: false, files: [], textContent: '', listeners: handlers,
    classList: { toggle(name, on) { if (on) classes.add(name); else classes.delete(name); }, contains: name => classes.has(name) },
    setAttribute(name, value) { attributes[name] = String(value); }, getAttribute(name) { return name === 'src' ? this.src : attributes[name]; },
    removeAttribute(name) { delete attributes[name]; if (name === 'src') delete this.src; },
    replaceChildren(...options) { this.options = options; }, focus() { this.focused = true; },
    append(...children) { this.children = [...(this.children || []), ...children]; },
    addEventListener(name, fn) { (handlers[name] ||= []).push(fn); },
    emit(name, data = {}) { const event = { type: name, target: this, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }, ...data }; for (const fn of handlers[name] || []) fn(event); return event; },
    dispatchEvent(event) { this.emit(event.type); },
  };
}
function setup({ saved = null, request, fetch } = {}) {
  const elements = {}, element = id => elements[id] ||= target(id), calls = [], uploads = [], storage = new Map(), timers = [];
  if (saved) storage.set(`anima-studio:prompt-reverse:v${saved.version || 1}`, JSON.stringify(saved));
  element('single_positive_prompt').value = 'current body'; element('singleNegative').value = 'current negative';
  const options = { available: true, services: [{ id: 'service', name: 'Service', models: [{ name: 'vision' }] }], templates: ['anima', 'krea2'].map(profile => ({ id: `builtin:reverse:${profile}`, name: profile })), defaults: { anima: 'builtin:reverse:anima', krea2: 'builtin:reverse:krea2' }, methods: { metadata: { available: true }, vision: { available: true }, tagger: { available: true, models: [{ name: 'pixai-tagger-v0.9-timm', state: 'ready' }] } } };
  const document = Object.assign(target(), { getElementById: element, createElement: tag => ({ ...target(), tagName: tag.toUpperCase() }), hidden: false, querySelector: () => null });
  const ctx = vm.createContext({ console, document, window: target(), AbortController, Event: function(type) { this.type = type; },
    profile: 'anima', currentProfile: () => ctx.profile, singleWorkbenchActive: () => true, imagePreview: { phase: 'closed' },
    singleWorkbenchState: { mode: 'single', single: { input_image_id: 'reference-untouched' }, generationMode: 'img2img' },
    localStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) },
    setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout() {}, schedulePersist() {},
    Option: function(name, id) { this.text = name; this.value = id; }, FormData: class { append(name, value) { this.name = name; this.file = value; } },
    navigator: { clipboard: { writeText: async text => { ctx.copied = text; } } },
    request: async (url, init) => { calls.push({ url, init }); if (request) return request(url, init); if (url.includes('/options')) return plain(options); if (url.startsWith('/api/prompt-images/')) return asset(url.split('/').at(-1)); return { id: 'job', status: 'completed', result: metadata() }; },
    fetch: async (url, init) => { uploads.push({ url, init }); return fetch ? fetch(url, init) : response(asset()); },
  });
  for (const name of ['single-input.js', 'prompt-reverse.js', 'prompt-assistant.js']) vm.runInContext(read(name), ctx);
  ctx.initializePromptReverse();
  const state = vm.runInContext('promptReverseState', ctx);
  ctx.promptReverseSetMode('reverse');
  const configure = (image = asset(), profile = ctx.profile) => { const draft = ctx.promptReverseDraft(profile); Object.assign(draft, { imageId: image.id, image, method: image.metadata?.source === 'none' ? 'vision' : 'metadata', service: 'service', model: 'vision', template: `builtin:reverse:${profile}` }); return draft; };
  return { ctx, elements, element, state, configure, calls, uploads, storage, timers, options, document };
}

test('metadata upload extracts automatically without AI configuration and keeps generation untouched', async () => {
  const { ctx, calls, uploads, elements } = setup({ request: async (url) => url.includes('/options') ? { available: false, services: [], templates: [], reason: 'not configured' } : { id: 'metadata-job', status: 'completed', result: metadata() } });
  await tick(); assert.equal(await ctx.promptReverseAcceptInput([file()]), true);
  const draft = ctx.promptReverseDraft();
  assert.equal(draft.result, 'a quiet forest'); assert.equal(draft.negative, 'blur'); assert.equal(draft.source, 'metadata');
  assert.equal(uploads[0].url, '/api/prompt-images');
  const posts = calls.filter(item => item.init?.method === 'POST'); assert.equal(posts.length, 1);
  assert.deepEqual(JSON.parse(posts[0].init.body), { kind: 'reverse', method: 'metadata', model_profile: 'anima', image_id: 'image-a' });
  assert.equal(elements.single_positive_prompt.value, 'current body'); assert.equal(elements.singleNegative.value, 'current negative');
  assert.equal(ctx.singleWorkbenchState.single.input_image_id, 'reference-untouched'); assert.equal(ctx.singleWorkbenchState.generationMode, 'img2img');
});

test('image without metadata waits for explicit method and start, then polls exactly one model job', async () => {
  const { ctx, calls, timers } = setup({ fetch: async () => response(asset('plain', { source: 'none' })) });
  await tick(); let reads = 0;
  ctx.request = async (url, init) => { calls.push({ url, init }); if (init && JSON.parse(init.body).method === 'metadata') return { id: 'metadata-job', status: 'completed', result: { source: 'none', outcome: 'no_metadata' } }; return init ? { id: 'ai-job', status: 'queued' } : ++reads === 1 ? { status: 'running' } : { status: 'completed', result: metadata({ source: 'ai', format: 'AI', text: 'visual description' }) }; };
  await ctx.promptReverseAcceptInput([file()]);
  assert.equal(ctx.promptReverseDraft().busy, false); assert.equal(timers.length, 0);
  assert.equal(ctx.promptReverseDraft().method, 'metadata'); assert.match(ctx.promptReverseDraft().status, /开始识别/);
  ctx.promptReverseSetMethod('vision'); assert.equal(calls.filter(item => item.init?.method === 'POST').length, 1);
  await ctx.promptReverseStart();
  assert.equal(ctx.promptReverseDraft().busy, true); assert.equal(timers.length, 1);
  await timers[0]();
  assert.equal(ctx.promptReverseDraft().result, 'visual description'); assert.equal(ctx.promptReverseDraft().source, 'ai');
  const posts = calls.filter(item => item.init?.method === 'POST').map(item => JSON.parse(item.init.body));
  assert.deepEqual(posts.map(item => item.method), ['metadata', 'vision']);
  assert.equal(posts[1].seed, 'random');
});

test('unconfigured AI retains the image and gives an actionable message without a job', async () => {
  const { ctx, calls } = setup({ fetch: async () => response(asset('plain', { source: 'none' })), request: async url => url.includes('/options') ? { services: [], templates: [], reason: '请配置视觉服务' } : { id: 'metadata-job', status: 'completed', result: { source: 'none', outcome: 'no_metadata' } } });
  await tick(); await ctx.promptReverseAcceptInput([file()]);
  ctx.promptReverseSetMethod('vision'); await ctx.promptReverseStart();
  assert.equal(ctx.promptReverseDraft().imageId, 'plain'); assert.match(ctx.promptReverseDraft().status, /配置视觉服务/);
  assert.equal(calls.filter(item => item.init).length, 1);
});

test('unavailable vision bridge blocks AI with its reason while metadata remains readable', async () => {
  const { ctx, configure, state, options, elements, calls } = setup(); await tick();
  const draft = configure(asset('plain', { source: 'none' }));
  state.options = { ...options, methods: { ...options.methods, vision: { available: false, reason: '视觉节点不兼容，请更新插件' } } };
  ctx.promptReverseRender(); assert.equal(elements.reverseStart.disabled, true);
  assert.equal(ctx.promptReverseIssue(draft), '视觉节点不兼容，请更新插件');
  await ctx.promptReverseStart(); assert.equal(calls.filter(item => item.init).length, 0);
  draft.image = asset(); draft.imageId = draft.image.id;
  ctx.promptReverseRender(); assert.equal(elements.reverseStart.disabled, false);
  await ctx.promptReverseStart(); assert.equal(draft.result, 'a quiet forest');
});

test('metadata upload does not wait for pending model choices and never submits a model job', async () => {
  const choices = deferred(), requests = [];
  const { ctx, options } = setup({ fetch: async () => response(asset('plain', { source: 'none' })), request: (url, init) => { requests.push({ url, init }); return url.includes('/options') ? choices.promise : Promise.resolve({ id: 'metadata-job', status: 'completed', result: { source: 'none', outcome: 'no_metadata' } }); } });
  await ctx.promptReverseAcceptInput([file()]);
  assert.equal(ctx.promptReverseRuntime().uploading, false); assert.equal(ctx.promptReverseDraft().busy, false);
  assert.equal(requests.filter(item => item.init).length, 1); assert.equal(ctx.promptReverseDraft().imageId, 'plain');
  assert.match(ctx.promptReverseDraft().status, /选择/); choices.resolve(options); await tick();
  assert.equal(requests.filter(item => item.init).length, 1);
});

test('multiple metadata candidates require explicit choice; positive and negative import undo independently', async () => {
  const { ctx, configure, elements } = setup(); await tick(); const draft = configure();
  ctx.promptReverseApplyResult(draft, metadata({ text: '', candidates: [{ id: 'a', label: 'A', text: 'first', negative: 'bad first', parameters: { Seed: 1 } }, { id: 'b', label: 'B', text: 'second', negative: 'bad second', parameters: { Seed: 2 } }] }));
  ctx.promptReverseRender(); assert.equal(elements.reverseReplace.disabled, true); assert.equal(draft.result, '');
  ctx.promptReverseAdopt('replace'); assert.equal(elements.single_positive_prompt.value, 'current body');
  ctx.promptReverseSelectCandidate('b'); assert.equal(draft.result, 'second');
  ctx.promptReverseAdopt('append'); ctx.promptReverseAdopt('replace', true);
  assert.equal(elements.single_positive_prompt.value, 'current body\n\nsecond'); assert.equal(elements.singleNegative.value, 'bad second');
  ctx.promptReverseUndo(true); assert.equal(elements.singleNegative.value, 'current negative');
  ctx.promptReverseUndo(); assert.equal(elements.single_positive_prompt.value, 'current body');
  ctx.promptReverseAdopt('replace'); elements.single_positive_prompt.value = 'new work'; ctx.promptReverseUndo(); assert.equal(elements.single_positive_prompt.value, 'new work');
  await ctx.promptReverseCopy(true); assert.equal(ctx.copied, 'bad second');
});

test('parameter table uses Chinese labels, deduplicates equal aliases and keeps unsafe text and large seeds literal', async () => {
  const { ctx, elements } = setup(); await tick();
  const seed = '18446744073709551615', injection = '<img src=x onerror=alert(1)>', long = 'detail '.repeat(80);
  ctx.promptReverseRenderParameters({ Seed: seed, sample_seed: seed, seed: '2', Steps: 24, model_name: injection, custom: { message: injection }, long, unknown: 'kept' });
  const rows = elements.reverseParameters.options;
  assert.equal(rows.length, 7);
  assert.equal(rows[0].children[0].textContent, '图像种子'); assert.equal(rows[0].children[1].textContent, seed);
  assert.equal(rows[1].children[0].textContent, '图像种子（seed）'); assert.equal(rows[1].children[1].textContent, '2');
  assert.equal(rows[2].children[0].textContent, '采样步数');
  assert.equal(rows[3].children[0].textContent, '模型'); assert.equal(rows[3].children[1].textContent, injection);
  assert.equal(rows[3].children[1].innerHTML, undefined);
  const details = rows[4].children[1].children[0];
  assert.equal(details.tagName, 'DETAILS'); assert.equal(details.open, undefined);
  assert.equal(details.children[1].textContent, JSON.stringify({ message: injection }, null, 2));
  assert.equal(rows[5].children[1].children[0].children[1].textContent, long);
  assert.equal(rows[6].children[0].textContent, 'unknown'); assert.equal(rows[6].children[1].textContent, 'kept');
});

test('oversized positive, negative and appended results cannot corrupt generation fields', async () => {
  const { ctx, configure, elements } = setup(); await tick(); const draft = configure();
  ctx.promptReverseApplyResult(draft, metadata({ text: 'x'.repeat(20001), negative: 'y'.repeat(20001) }));
  ctx.promptReverseAdopt('replace'); ctx.promptReverseAdopt('replace', true);
  assert.equal(elements.single_positive_prompt.value, 'current body'); assert.equal(elements.singleNegative.value, 'current negative');
  assert.match(draft.status, /20,000/); assert.equal(draft.undo, null); assert.equal(draft.negativeUndo, null);
  ctx.promptReverseEdit('result', 'z'); elements.single_positive_prompt.value = 'a'.repeat(20000); ctx.promptReverseAdopt('append');
  assert.equal(elements.single_positive_prompt.value.length, 20000);
  ctx.promptReverseEdit('result', '😀'.repeat(20000)); ctx.promptReverseAdopt('replace');
  assert.equal(Array.from(elements.single_positive_prompt.value).length, 20000);
});

test('late upload cannot replace the newest image and removing during upload invalidates response', async () => {
  const first = deferred(), second = deferred(); let index = 0;
  const { ctx, uploads } = setup({ fetch: () => ++index === 1 ? first.promise : second.promise }); await tick();
  const old = ctx.promptReverseAcceptInput([file('old.png')]), fresh = ctx.promptReverseAcceptInput([file('new.png')]);
  second.resolve(response(asset('new'))); await fresh;
  first.resolve(response(asset('old'))); await old;
  assert.equal(ctx.promptReverseDraft().imageId, 'new'); assert.equal(uploads[0].init.signal.aborted, true);
  const pending = deferred(); ctx.fetch = () => pending.promise;
  const removing = ctx.promptReverseAcceptInput([file()]); ctx.promptReverseRemove(); pending.resolve(response(asset('late'))); await removing;
  assert.equal(ctx.promptReverseDraft().imageId, '');
});

test('late completion remains with its profile and editing the preview prevents overwrite', async () => {
  const { ctx, configure, elements } = setup(); await tick(); const draft = configure(asset('plain', { source: 'none' })), gate = deferred();
  ctx.request = async (_url, init) => init ? { id: 'ai-job', status: 'queued' } : gate.promise;
  const pending = ctx.promptReverseStart(); await tick(); ctx.profile = 'krea2'; const other = configure(asset('other'), 'krea2'); other.result = 'Krea preview'; ctx.promptReverseRender();
  gate.resolve({ status: 'completed', result: metadata({ source: 'ai' }) }); await pending;
  assert.equal(draft.result, 'a quiet forest'); assert.equal(elements.reverseResult.value, 'Krea preview');
  ctx.profile = 'anima'; const gate2 = deferred(); ctx.request = async (_url, init) => init ? { id: 'ai-job-2', status: 'queued' } : gate2.promise;
  const second = ctx.promptReverseStart(); await tick(); ctx.promptReverseEdit('result', 'manually edited'); gate2.resolve({ status: 'completed', result: metadata({ source: 'ai', text: 'obsolete' }) }); await second;
  assert.equal(draft.result, 'manually edited'); assert.equal(draft.busy, false);
});

test('cancel during job creation cancels returned id and discards completed output', async () => {
  const { ctx, configure, calls } = setup(); await tick(); const draft = configure(), gate = deferred();
  ctx.request = (url, init) => { calls.push({ url, init }); return url === '/api/prompt-jobs' ? gate.promise : Promise.resolve({}); };
  const pending = ctx.promptReverseStart(); await ctx.promptReverseCancel();
  gate.resolve({ id: 'late-job', status: 'completed', result: metadata() }); await pending;
  assert.ok(calls.some(item => item.url === '/api/prompt-jobs/late-job/cancel')); assert.equal(draft.result, ''); assert.equal(draft.busy, false);
});

test('replacement during pending job create prevents that old task from taking ownership', async () => {
  const { ctx, configure } = setup(); await tick(); const draft = configure(), gate = deferred();
  ctx.request = () => gate.promise;
  const pending = ctx.promptReverseStart(); ctx.promptReverseRemove();
  gate.resolve({ id: 'old-job', status: 'completed', result: metadata() }); await pending;
  assert.equal(draft.imageId, ''); assert.equal(draft.jobId, ''); assert.equal(draft.result, '');
});

test('persisted desk/profile drafts restore with GET only and keep rewrite v1 data unchanged', async () => {
  const imageId = 'saved-image', fingerprint = JSON.stringify([imageId, 'service', 'vision', 'builtin:reverse:krea2']);
  const saved = { version: 1, mode: 'reverse', drafts: { krea2: { imageId, service: 'service', model: 'vision', template: 'builtin:reverse:krea2', jobId: 'saved-job', jobRevision: 4, revision: 4, jobFingerprint: fingerprint, busy: true } } };
  const { ctx, calls, elements, storage, state } = setup({ saved, request: async url => url.includes('/options') ? { services: [], templates: [] } : url.startsWith('/api/prompt-images/') ? asset(imageId, { source: 'none' }) : { status: 'completed', result: metadata({ source: 'ai', text: 'recovered AI' }) } });
  storage.set('anima-studio:prompt-assistant:v1', 'legacy rewrite data'); await tick(); await tick();
  assert.equal(ctx.promptReverseDraft('krea2').result, 'recovered AI'); assert.equal(ctx.promptReverseDraft('krea2').busy, false);
  assert.equal(elements.reverseResult.value, ''); assert.equal(calls.filter(item => item.init?.method === 'POST').length, 0);
  assert.equal(state.mode, 'reverse'); assert.equal(storage.get('anima-studio:prompt-assistant:v1'), 'legacy rewrite data');
  assert.equal(JSON.parse(storage.get('anima-studio:prompt-reverse:v2')).drafts.krea2.results.vision.result, 'recovered AI');
});

test('missing saved image keeps preview and cannot trigger a new AI request on restore', async () => {
  const { ctx, configure, calls } = setup(); await tick(); const draft = configure();
  ctx.promptReverseApplyResult(draft, metadata());
  ctx.request = async (url, init) => { calls.push({ url, init }); throw new Error('图片不存在'); };
  await ctx.promptReverseRestore('anima');
  assert.equal(draft.result, 'a quiet forest'); assert.equal(draft.image, null); assert.equal(draft.imageId, 'image-a');
  assert.match(draft.status, /图片不存在/); assert.equal(calls.filter(item => item.init).length, 0);
});

test('options edits invalidate AI previews but cannot invalidate extracted metadata', async () => {
  const { ctx, configure, elements } = setup(); await tick(); const draft = configure();
  ctx.promptReverseApplyResult(draft, metadata()); ctx.promptReverseEdit('model', 'other'); assert.equal(elements.reverseReplace.disabled, false);
  ctx.promptReverseApplyResult(draft, metadata({ source: 'ai' })); ctx.promptReverseEdit('model', 'third'); assert.equal(elements.reverseReplace.disabled, true);
});

test('image validation, independent mode controls and keyboard navigation never route to generation', async () => {
  const { ctx, elements, uploads } = setup(); await tick();
  await ctx.promptReverseAcceptInput([file(), file()]); await ctx.promptReverseAcceptInput([{ ...file(), size: 0 }]);
  assert.equal(uploads.length, 0);
  elements.promptDeskReverse.emit('keydown', { key: 'ArrowLeft' });
  assert.equal(elements.promptRewritePanel.hidden, false); assert.equal(elements.promptReversePanel.hidden, true);
  assert.equal(elements.promptDeskRewrite.focused, true);
  const drop = elements.reverseInputDropZone.emit('drop', { dataTransfer: { types: ['Files'], files: [file()] } });
  assert.equal(drop.prevented, undefined); assert.equal(uploads.length, 0);
  elements.promptDeskRewrite.emit('keydown', { key: 'End' });
  assert.equal(elements.promptReversePanel.hidden, false); assert.equal(elements.promptDeskReverse.getAttribute('aria-selected'), 'true');
});

test('transient polling errors retry reads only, and clearing drafts invalidates in-flight requests', async () => {
  const { ctx, configure, timers } = setup(); await tick(); const draft = configure(asset('plain', { source: 'none' }));
  let reads = 0, posts = 0;
  ctx.request = async (_url, init) => { if (init) { posts++; return { id: 'job', status: 'queued' }; } if (++reads === 1) throw new Error('offline'); return { status: 'completed', result: metadata({ source: 'ai' }) }; };
  await ctx.promptReverseStart(); assert.equal(draft.busy, true); await timers[0](); assert.equal(draft.result, 'a quiet forest'); assert.equal(posts, 1);
  const gate = deferred(); ctx.request = () => gate.promise;
  const pending = ctx.promptReverseStart(); ctx.promptReverseClear(); gate.resolve({ id: 'obsolete', status: 'completed', result: metadata() }); await pending;
  assert.equal(ctx.promptReverseDraft().result, ''); assert.equal(ctx.promptReverseDraft().busy, false);
});

test('methods preserve independent edited results and controls without submitting jobs', async () => {
  const { ctx, configure, calls, elements } = setup(); await tick(); const draft = configure(asset('plain', { source: 'none' }));
  ctx.promptReverseEditSeed('18446744073709551615');
  ctx.promptReverseApplyResult(draft, metadata({ source: 'ai', text: 'AI description', negative: '', parameters: {}, recognition: { seed: '18446744073709551615' } }));
  ctx.promptReverseEdit('result', 'edited AI description');
  ctx.promptReverseSetMethod('tagger');
  assert.equal(draft.result, ''); ctx.promptReverseEditTagger('general_threshold', '0.42');
  ctx.promptReverseApplyResult(draft, metadata({ source: 'tagger', text: 'tag, tag 2', negative: '', parameters: {}, recognition: { model: 'pixai-tagger-v0.9-timm', general_threshold: 0.42 } }));
  ctx.promptReverseSetMethod('metadata'); assert.equal(draft.result, '');
  ctx.promptReverseSetMethod('vision');
  assert.equal(draft.result, 'edited AI description'); assert.equal(draft.seed, '18446744073709551615');
  assert.equal(elements.reverseRecognitionDetails.hidden, false);
  assert.equal(elements.reverseRecognition.options[0].children[0].textContent, '反推种子');
  assert.equal(elements.reverseRecognition.options[0].children[1].textContent, '18446744073709551615');
  ctx.promptReverseSetMethod('tagger'); assert.equal(draft.result, 'tag, tag 2'); assert.equal(draft.tagger.general_threshold, 0.42);
  assert.equal(elements.reverseDetails.hidden, true); assert.equal(elements.reverseRecognitionDetails.hidden, false);
  assert.equal(calls.filter(item => item.init?.method === 'POST').length, 0);
});

test('tagger runs without vision configuration using a frozen exact local payload', async () => {
  const { ctx, state, configure, calls, options } = setup(); await tick(); const draft = configure(asset('plain', { source: 'none' }));
  state.options = { ...options, available: false, services: [], methods: { ...options.methods, vision: { available: false } } };
  ctx.promptReverseSetMethod('tagger'); ctx.promptReverseEditTagger('exclude_tags', 'text, watermark');
  const gate = deferred();
  ctx.request = async (url, init) => { calls.push({ url, init }); return gate.promise; };
  const pending = ctx.promptReverseStart();
  const payload = JSON.parse(calls.at(-1).init.body);
  assert.deepEqual(payload, { kind: 'reverse', method: 'tagger', model_profile: 'anima', image_id: 'plain', tagger: { model: 'pixai-tagger-v0.9-timm', general_threshold: 0.35, character_threshold: 0.75, exclude_tags: 'text, watermark' } });
  ctx.promptReverseEditTagger('general_threshold', '0.5');
  assert.equal(payload.tagger.general_threshold, 0.35);
  gate.resolve({ id: 'local-job', status: 'completed', result: metadata({ source: 'tagger', text: 'obsolete tags' }) }); await pending;
  assert.equal(draft.result, ''); assert.match(draft.status, /未覆盖/);
});

test('only ready local models can execute, metadata bypasses missing models', async () => {
  const { ctx, state, configure, elements, calls } = setup(); await tick(); const draft = configure(asset('plain', { source: 'none' }));
  ctx.promptReverseSetMethod('tagger');
  for (const status of ['missing', 'incomplete', 'invalid', 'unverifiable']) {
    state.options.methods.tagger.models[0] = { name: draft.tagger.model, state: status, reason: `repair ${status}` };
    ctx.promptReverseRender(); assert.equal(elements.reverseStart.disabled, true); assert.match(elements.reverseTaggerState.textContent, new RegExp(status));
    await ctx.promptReverseStart();
  }
  assert.equal(calls.filter(item => item.init).length, 0);
  draft.image = asset(); draft.imageId = draft.image.id;
  await ctx.promptReverseStart(); assert.equal(draft.method, 'metadata'); assert.equal(draft.source, 'metadata');
  assert.equal(JSON.parse(calls.at(-1).init.body).method, 'tagger');
});

test('seed strings and local thresholds reject invalid values without model calls', async () => {
  const { ctx, configure, elements, calls } = setup(); await tick(); const draft = configure(asset('plain', { source: 'none' }));
  for (const seed of ['', '-1', '0.1', '18446744073709551616', '999999999999999999999', '1e3']) {
    ctx.promptReverseEditSeed(seed); assert.equal(elements.reverseStart.disabled, true); await ctx.promptReverseStart();
  }
  for (const seed of ['0', '18446744073709551615']) { ctx.promptReverseEditSeed(seed); assert.equal(elements.reverseStart.disabled, false); assert.equal(draft.seed, seed); }
  ctx.promptReverseEditSeed('random', true); assert.equal(draft.seed, 'random');
  ctx.promptReverseEditSeed('fixed', true); assert.equal(draft.seed, '18446744073709551615');
  ctx.promptReverseSetMethod('tagger');
  for (const threshold of ['', '-0.1', '1.1', 'NaN']) { ctx.promptReverseEditTagger('general_threshold', threshold); assert.equal(elements.reverseStart.disabled, true); await ctx.promptReverseStart(); }
  for (const threshold of ['0', '1']) { ctx.promptReverseEditTagger('general_threshold', threshold); assert.equal(elements.reverseStart.disabled, false); }
  assert.equal(calls.filter(item => item.init).length, 0);
});

test('one task per profile and switched-away completion fills only its original method', async () => {
  const { ctx, configure, calls, elements } = setup(); await tick(); const draft = configure(asset('plain', { source: 'none' }));
  const gate = deferred(); ctx.request = async (url, init) => { calls.push({ url, init }); return init ? { id: 'vision-job', status: 'queued' } : gate.promise; };
  const pending = ctx.promptReverseStart(); await tick();
  ctx.promptReverseSetMethod('tagger'); ctx.promptReverseEdit('result', 'my local draft');
  assert.equal(elements.reverseStart.disabled, true); await ctx.promptReverseStart();
  assert.equal(calls.filter(item => item.init).length, 1);
  gate.resolve({ status: 'completed', result: metadata({ source: 'ai', text: 'finished vision' }) }); await pending;
  assert.equal(draft.result, 'my local draft'); assert.equal(draft.method, 'tagger'); assert.equal(draft.busy, false);
  ctx.promptReverseSetMethod('vision'); assert.equal(draft.result, 'finished vision');
});

test('editing a method then switching away protects its result from late completion', async () => {
  const { ctx, configure } = setup(); await tick(); const draft = configure(asset('plain', { source: 'none' }));
  const gate = deferred(); ctx.request = async (_url, init) => init ? { id: 'vision-job', status: 'queued' } : gate.promise;
  const pending = ctx.promptReverseStart(); await tick();
  ctx.promptReverseEdit('result', 'keep my changes'); ctx.promptReverseSetMethod('metadata');
  gate.resolve({ status: 'completed', result: metadata({ source: 'ai', text: 'late AI' }) }); await pending;
  ctx.promptReverseSetMethod('vision'); assert.equal(draft.result, 'keep my changes'); assert.match(draft.status, /未覆盖/);
});

test('v2 persisted active job resumes into its own method with GET only', async () => {
  const initial = setup(); await tick(); const draft = initial.configure(asset('plain', { source: 'none' }));
  initial.ctx.promptReverseApplyResult(draft, metadata({ source: 'ai', text: 'previous vision' }));
  draft.busy = true; draft.jobId = 'existing-job'; draft.jobRevision = 7; draft.revision = 7;
  draft.jobMethod = 'vision'; draft.jobImageId = 'plain'; draft.jobViewRevision = draft.viewRevision;
  draft.jobFingerprint = initial.ctx.promptReverseFingerprint(draft, 'vision');
  initial.ctx.promptReverseSetMethod('tagger'); initial.ctx.promptReverseEdit('result', 'local result');
  const saved = JSON.parse(initial.storage.get('anima-studio:prompt-reverse:v2'));
  const restored = setup({ saved, request: async url => url.includes('/options') ? initial.options : url.startsWith('/api/prompt-images/') ? asset('plain', { source: 'none' }) : { status: 'completed', result: metadata({ source: 'ai', text: 'restored vision' }) } });
  await tick(); await tick();
  assert.equal(restored.ctx.promptReverseDraft().result, 'local result'); assert.equal(restored.ctx.promptReverseDraft().method, 'tagger');
  restored.ctx.promptReverseSetMethod('vision'); assert.equal(restored.ctx.promptReverseDraft().result, 'restored vision');
  assert.equal(restored.calls.filter(item => item.init).length, 0);
});

test('replacing an image resets results to metadata while retaining model preferences', async () => {
  const { ctx, configure } = setup({ fetch: async () => response(asset('replacement', { source: 'none' })), request: async url => url.includes('/options') ? {} : { id: 'metadata-job', status: 'completed', result: { source: 'none', outcome: 'no_metadata' } } });
  await tick(); const draft = configure(asset('plain', { source: 'none' }));
  ctx.promptReverseEditSeed('42'); ctx.promptReverseApplyResult(draft, metadata({ source: 'ai', text: 'previous image' }));
  ctx.promptReverseSetMethod('tagger'); ctx.promptReverseEditTagger('character_threshold', '0.8'); ctx.promptReverseEdit('result', 'old tags');
  await ctx.promptReverseAcceptInput([file()]);
  assert.equal(draft.method, 'metadata'); assert.equal(draft.imageId, 'replacement'); assert.equal(draft.result, '');
  ctx.promptReverseSetMethod('vision'); assert.equal(draft.result, ''); assert.equal(draft.seed, '42');
  ctx.promptReverseSetMethod('tagger'); assert.equal(draft.result, ''); assert.equal(draft.tagger.character_threshold, 0.8);
});

test('method tabs support arrow, home, end and profile isolation', async () => {
  const { ctx, configure, elements, calls } = setup(); await tick(); configure(asset('plain', { source: 'none' }));
  ctx.promptReverseSetMethod('metadata'); elements['reverseMethod-metadata'].emit('keydown', { key: 'ArrowRight' });
  assert.equal(ctx.promptReverseDraft().method, 'vision'); assert.equal(elements['reverseMethod-vision'].focused, true);
  assert.equal(elements.reverseVisionOptions.hidden, false); assert.equal(elements.reverseTaggerOptions.hidden, true);
  elements['reverseMethod-vision'].emit('keydown', { key: 'End' }); assert.equal(ctx.promptReverseDraft().method, 'tagger');
  ctx.promptReverseEditTagger('exclude_tags', 'anima tags'); ctx.profile = 'krea2'; ctx.promptReverseRender();
  assert.equal(ctx.promptReverseDraft().method, 'metadata'); assert.notEqual(ctx.promptReverseDraft().tagger.exclude_tags, 'anima tags');
  ctx.profile = 'anima'; ctx.promptReverseRender(); assert.equal(ctx.promptReverseDraft().method, 'tagger'); assert.equal(elements.reverseExcludeTags.value, 'anima tags');
  elements['reverseMethod-tagger'].emit('keydown', { key: 'Home' }); assert.equal(ctx.promptReverseDraft().method, 'metadata');
  assert.equal(calls.filter(item => item.init).length, 0);
});

test('server workflow defaults apply once to new and legacy drafts but preserve edits', async () => {
  const defaults = { model: 'pixai-tagger-v0.9-timm', general_threshold: 0.35, character_threshold: 0.75, exclude_tags: 'watermark, username, artist name, lower text, blurry' };
  const choiceGate = deferred();
  const ui = setup({ request: async () => choiceGate.promise });
  ui.ctx.promptReverseEditTagger('exclude_tags', 'my edited exclusions');
  choiceGate.resolve({ ...ui.options, methods: { ...ui.options.methods, tagger: { ...ui.options.methods.tagger, defaults } } }); await tick();
  assert.equal(ui.ctx.promptReverseDraft().tagger.exclude_tags, 'my edited exclusions');
  assert.equal(ui.ctx.promptReverseDraft('krea2').tagger.exclude_tags, defaults.exclude_tags);
  const legacy = setup({ saved: { version: 1, mode: 'reverse', drafts: { anima: { source: 'ai', result: 'old vision' } } }, request: async () => ({ ...ui.options, methods: { ...ui.options.methods, tagger: { ...ui.options.methods.tagger, defaults } } }) });
  await tick(); assert.equal(legacy.ctx.promptReverseDraft().result, 'old vision'); assert.equal(legacy.ctx.promptReverseDraft().tagger.exclude_tags, defaults.exclude_tags);
  const saved = JSON.parse(ui.storage.get('anima-studio:prompt-reverse:v2'));
  const restored = setup({ saved, request: async () => ({ ...ui.options, methods: { tagger: { defaults: { ...defaults, exclude_tags: 'changed upstream default' } } } }) });
  await tick(); assert.equal(restored.ctx.promptReverseDraft().tagger.exclude_tags, 'my edited exclusions');
  assert.equal(restored.ctx.promptReverseDraft('krea2').tagger.exclude_tags, defaults.exclude_tags);
});

test('legacy stale AI output stays stale through draft migration', async () => {
  const saved = { version: 1, drafts: { anima: { imageId: 'plain', source: 'ai', result: 'old result', model: 'new model', service: 'service', template: 'builtin:reverse:anima', resultFor: JSON.stringify(['plain', 'service', 'old model', 'builtin:reverse:anima']) } } };
  const ui = setup({ saved }); await tick(); assert.equal(ui.ctx.promptReverseStale(ui.ctx.promptReverseDraft()), true);
});
