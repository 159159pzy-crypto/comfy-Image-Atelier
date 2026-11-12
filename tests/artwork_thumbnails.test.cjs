const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../static/artwork-thumbnails.js'), 'utf8');
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
async function ticks() { for (let i = 0; i < 8; i++) await Promise.resolve(); }
function harness({ observer = true } = {}) {
  const frames = [], observers = [], requests = [], fetches = [], revoked = [], created = [];
  const windowEvents = new Map(), documentEvents = new Map();
  const document = { hidden: false, activeElement: null, addEventListener: (name, callback) => documentEvents.set(name, callback) };
  let order = 0;
  function node() {
    const classes = new Set(), events = new Map();
    return {
      dataset: {}, hidden: false, disabled: false,
      classList: { toggle(name, on) { if (on) classes.add(name); else classes.delete(name); }, contains: name => classes.has(name) },
      setAttribute(name, value) { this[name] = value; },
      addEventListener(name, callback) { if (!events.has(name)) events.set(name, new Set()); events.get(name).add(callback); },
      removeEventListener(name, callback) { events.get(name)?.delete(callback); },
      emit(name) { for (const callback of [...(events.get(name) || [])]) callback({ target: this, stopPropagation() {} }); },
      focus() { document.activeElement = this; },
    };
  }
  const ctx = vm.createContext({ document, window: { innerHeight: 800, innerWidth: 1200, addEventListener: (name, callback) => windowEvents.set(name, callback) },
    requestAnimationFrame: callback => { frames.push(callback); return frames.length; },
    fetch: (url, options) => { const gate = deferred(); fetches.push({ url, options, ...gate }); return gate.promise; },
    URL: { createObjectURL(body) { const value = `blob:test-${created.length + 1}`; created.push({ body, value }); return value; }, revokeObjectURL: url => revoked.push(url) },
    ...(observer ? { IntersectionObserver: class {
      constructor(callback, options) { this.callback = callback; this.options = options; this.targets = new Set(); observers.push(this); }
      observe(article) { this.targets.add(article); }
      unobserve(article) { this.targets.delete(article); }
    } } : {}),
  });
  vm.runInContext(source, ctx);
  const loader = vm.runInContext('ArtworkThumbnails', ctx);
  function add(id, top = 100) {
    const article = node(), image = node(), retry = node(), failure = node(), main = node();
    article.order = ++order;
    article.isConnected = true;
    article.rect = { top, bottom: top + 200, left: 0, right: 200 };
    article.closest = () => article.ancestorHidden ? {} : null;
    article.getClientRects = () => article.isConnected && !article.ancestorHidden ? [article.rect] : [];
    article.getBoundingClientRect = () => article.rect;
    article.compareDocumentPosition = other => article.order < other.order ? 4 : 2;
    article.querySelector = selector => selector === '.image-failure' ? failure : selector === '.image-card-open' ? main : null;
    image.dataset.thumbnailUrl = `/api/images/${id}/thumbnail`;
    image.complete = false; image.naturalWidth = 0;
    image.decode = () => Promise.resolve();
    image.removeAttribute = name => { if (name === 'src') image.currentSource = ''; };
    Object.defineProperty(image, 'src', { get: () => image.currentSource || '', set(value) {
      image.currentSource = value; image.complete = false; image.naturalWidth = 0;
      requests.push({ id, url: value, priority: image.fetchPriority, loading: image.loading });
    } });
    image.succeed = () => { image.complete = true; image.naturalWidth = 200; image.emit('load'); };
    image.fail = () => { image.complete = true; image.naturalWidth = 0; image.emit('error'); };
    loader.attach(article, image, retry);
    return { article, image, retry, failure, main, id };
  }
  function see(item, visible = true, nearby = true) {
    observers[0]?.callback([{ target: item.article, isIntersecting: visible }]);
    observers[1]?.callback([{ target: item.article, isIntersecting: nearby }]);
  }
  function remove(item) { loader.remove(item.article); item.article.isConnected = false; }
  function frame() { for (const callback of frames.splice(0)) callback(); }
  return { loader, add, see, remove, frame, observers, requests, fetches, created, revoked, document, windowEvents, documentEvents };
}

test('visible thumbnails precede nearby work, never exceed four, and clipped/far cards wait', async () => {
  const h = harness(), near = h.add(1, 900), clipped = h.add(2), far = h.add(3, 1400);
  h.see(near, false, true); h.see(clipped, false, false); h.see(far, false, false);
  const visible = [4, 5, 6, 7, 8].map(id => h.add(id)); visible.forEach(item => h.see(item));
  assert.deepEqual(h.observers.map(item => item.options.rootMargin), ['0px', '400px 0px']);
  h.frame();
  assert.deepEqual(h.requests.map(item => item.id), [4, 5, 6, 7]);
  assert.ok(h.requests.every(item => item.priority === 'high' && item.loading === 'eager'));
  visible[0].image.succeed(); await ticks(); h.frame();
  assert.equal(h.requests.at(-1).id, 8);
  visible[1].image.succeed(); await ticks(); h.frame();
  assert.deepEqual(h.requests.at(-1), { id: 1, url: '/api/images/1/thumbnail', priority: 'low', loading: 'eager' });
  assert.equal(clipped.image.src, ''); assert.equal(far.image.src, '');
});

test('rapid navigation retains old active slots but drops queued pages before the latest page loads', async () => {
  const h = harness(), old = Array.from({ length: 8 }, (_, i) => h.add(i + 1)); old.forEach(item => h.see(item)); h.frame();
  old.forEach(h.remove);
  const middle = [20, 21, 22, 23].map(id => h.add(id)); middle.forEach(item => h.see(item)); h.frame();
  middle.forEach(h.remove);
  const latest = [30, 31, 32, 33].map(id => h.add(id)); latest.forEach(item => h.see(item)); h.frame();
  assert.deepEqual(h.requests.map(item => item.id), [1, 2, 3, 4]);
  assert.equal(old[0].image.src, '/api/images/1/thumbnail', 'detaching does not cancel an active request');
  old[0].image.succeed(); old[1].image.fail(); await ticks(); h.frame();
  assert.deepEqual(h.requests.map(item => item.id), [1, 2, 3, 4, 30, 31]);
  assert.equal(old[0].image.src, '');
  assert.ok(h.observers.every(observer => observer.targets.size === 4));
  old[2].image.succeed(); old[3].image.succeed(); await ticks(); h.frame();
  assert.deepEqual(h.requests.map(item => item.id), [1, 2, 3, 4, 30, 31, 32, 33]);
});

test('hidden workbench and hidden document pause queued work without dropping active slots', async () => {
  const h = harness(), items = [1, 2, 3, 4, 5, 6].map(id => h.add(id)); items.forEach(item => h.see(item));
  h.loader.setActive(false); h.frame(); assert.equal(h.requests.length, 0);
  h.loader.setActive(true); h.frame(); assert.equal(h.requests.length, 4);
  h.loader.setActive(false); items[0].image.succeed(); await ticks(); h.frame(); assert.equal(h.requests.length, 4);
  h.document.hidden = true; h.loader.setActive(true); h.frame(); assert.equal(h.requests.length, 4);
  h.document.hidden = false; h.documentEvents.get('visibilitychange')(); h.frame(); assert.equal(h.requests.length, 5);
});

test('manual retry bypasses cached failure, preserves focus, holds its slot through decode and revokes its blob', async () => {
  const h = harness(), items = [1, 2, 3, 4].map(id => h.add(id)); items.forEach(item => h.see(item)); h.frame();
  const item = items[0]; item.image.fail(); await ticks(); h.frame();
  assert.equal(item.retry.hidden, false); assert.equal(h.fetches.length, 0);
  h.document.activeElement = item.retry; item.retry.emit('click'); h.frame();
  assert.equal(h.document.activeElement, item.main);
  assert.equal(h.fetches.length, 1); assert.equal(h.fetches[0].url, '/api/images/1/thumbnail');
  assert.equal(h.fetches[0].options.cache, 'reload');
  const fifth = h.add(5); h.see(fifth); h.frame(); assert.equal(fifth.image.src, '');
  const decoding = deferred(); item.image.decode = () => decoding.promise;
  h.fetches[0].resolve({ ok: true, blob: async () => ({ type: 'image/webp' }) }); await ticks();
  assert.equal(item.image.src, 'blob:test-1'); item.image.succeed(); await ticks(); h.frame();
  assert.equal(fifth.image.src, '', 'the retry occupies one of four slots through decoding');
  decoding.resolve(); await ticks(); h.frame();
  assert.equal(item.article.dataset.thumbnailState, 'loaded'); assert.equal(item.failure.hidden, true);
  assert.equal(fifth.image.src, '/api/images/5/thumbnail');
  h.remove(item); assert.deepEqual(h.revoked, ['blob:test-1']);
});

test('retry HTTP failures stay retryable without automatic fetches or original-image fallbacks', async () => {
  const h = harness(), item = h.add(1); h.see(item); h.frame(); item.image.fail();
  item.retry.emit('click'); h.frame();
  h.fetches[0].resolve({ ok: false, status: 404, blob() { throw new Error('must not decode an error response'); } });
  await ticks(); h.frame(); h.frame();
  assert.equal(item.article.dataset.thumbnailState, 'error'); assert.equal(item.retry.hidden, false);
  assert.equal(h.fetches.length, 1); assert.equal(h.created.length, 0);
  item.retry.emit('click'); h.frame();
  assert.equal(h.fetches[1].url, '/api/images/1/thumbnail');
});

test('detached retry fetch remains counted until its body finishes and never creates an unused blob URL', async () => {
  const h = harness(), items = [1, 2, 3, 4].map(id => h.add(id)); items.forEach(item => h.see(item)); h.frame();
  const item = items[0]; item.image.fail(); item.retry.emit('click'); h.frame(); h.remove(item);
  const fifth = h.add(5); h.see(fifth); h.frame(); assert.equal(fifth.image.src, '');
  const body = deferred(); h.fetches[0].resolve({ ok: true, blob: () => body.promise }); await ticks(); h.frame();
  assert.equal(fifth.image.src, ''); body.resolve({}); await ticks(); h.frame();
  assert.equal(fifth.image.src, '/api/images/5/thumbnail'); assert.equal(h.created.length, 0);
});

test('an old decode completion cannot settle twice or overwrite a newer retry', async () => {
  const h = harness(), items = [1, 2, 3, 4].map(id => h.add(id)); items.forEach(item => h.see(item)); h.frame();
  const item = items[0], decoding = deferred(); item.image.decode = () => decoding.promise;
  item.image.succeed(); await ticks(); item.image.fail();
  item.retry.emit('click'); h.frame();
  const queued = [5, 6].map(id => h.add(id)); queued.forEach(item => h.see(item)); h.frame();
  decoding.resolve(); await ticks(); h.frame();
  assert.equal(item.article.dataset.thumbnailState, 'loading'); assert.equal(h.requests.length, 4);
  h.fetches[0].reject(new Error('offline')); await ticks(); h.frame();
  assert.equal(h.requests.length, 5, 'exactly one slot was released');
  assert.equal(item.article.dataset.thumbnailState, 'error');
});

test('the no-observer fallback responds to scrolling and skips hidden ancestors', () => {
  const h = harness({ observer: false }), item = h.add(1, 1400), hidden = h.add(2);
  hidden.article.ancestorHidden = true; h.frame(); assert.equal(h.requests.length, 0);
  item.article.rect.top = 100; item.article.rect.bottom = 300; h.windowEvents.get('scroll')(); h.frame();
  assert.deepEqual(h.requests.map(item => item.id), [1]);
  hidden.article.ancestorHidden = false; h.windowEvents.get('resize')(); h.frame();
  assert.deepEqual(h.requests.map(item => item.id), [1, 2]);
});
