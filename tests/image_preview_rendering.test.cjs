const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
const flush = () => new Promise(resolve => setImmediate(resolve));

function classList() {
  const values = new Set();
  return {
    add: (...names) => names.forEach(name => values.add(name)),
    remove: (...names) => names.forEach(name => values.delete(name)),
    contains: name => values.has(name),
    toggle(name, enabled = !values.has(name)) { enabled ? values.add(name) : values.delete(name); return enabled; },
  };
}

function element() {
  const attrs = new Map(), listeners = new Map(), writes = [], animations = [];
  const properties = { removeProperty(name) { delete this[name]; } };
  return {
    attrs, listeners, writes, animations, classList: classList(), dataset: {}, hidden: false,
    style: new Proxy(properties, { set(target, name, value) { writes.push([name, value]); target[name] = value; return true; } }),
    addEventListener: (name, callback) => listeners.set(name, callback),
    setAttribute: (name, value) => attrs.set(name, String(value)),
    getAttribute: name => attrs.get(name) ?? null,
    removeAttribute: name => attrs.delete(name),
    set src(value) { attrs.set('src', value); }, get src() { return attrs.get('src') || ''; },
    get currentSrc() { return this.src; },
    setPointerCapture() {}, releasePointerCapture() {}, focus() {},
    getAnimations: () => animations.filter(animation => !animation.cancelled),
    animate(frames, options) {
      let finish;
      const finished = new Promise(resolve => { finish = resolve; });
      const animation = { frames, options, finished, finish, cancelled: false,
        cancel() { this.cancelled = true; }, commitStyles() {} };
      animations.push(animation);
      return animation;
    },
    open: true, close() { this.open = false; }, showModal() { this.open = true; },
  };
}

function setup({ width = 4000, height = 3000, reducedMotion = false, devicePixelRatio = 1 } = {}) {
  const ui = Object.fromEntries(['imagePreviewImage', 'imagePreviewStage', 'imagePreviewDialog',
    'imagePreviewVeil', 'imagePreviewStatus', 'imagePreviewAnnouncement', 'imagePreviewPrevious',
    'imagePreviewNext', 'imagePreviewTitle', 'closeImagePreview', 'detailImage', 'detailDialog', 'historyCount'].map(name => [name, element()]));
  const stage = ui.imagePreviewStage, image = ui.imagePreviewImage;
  Object.assign(stage, { clientWidth: 1000, clientHeight: 800 });
  stage.getBoundingClientRect = () => ({ left: 20, top: 30, width: stage.clientWidth, height: stage.clientHeight,
    right: 20 + stage.clientWidth, bottom: 30 + stage.clientHeight });
  Object.assign(image, { naturalWidth: width, naturalHeight: height, decode: async () => {} });
  image.src = '/api/images/1';
  const frames = new Map();
  let now = 0, serial = 0, resize;
  const root = element();
  const ctx = vm.createContext({
    console, ui, devicePixelRatio, document: { documentElement: root },
    performance: { now: () => now },
    requestAnimationFrame(callback) { frames.set(++serial, callback); return serial; },
    cancelAnimationFrame(id) { frames.delete(id); },
    ResizeObserver: class { constructor(callback) { resize = callback; } observe() {} },
    matchMedia: () => ({ matches: reducedMotion }),
    getComputedStyle: target => ({ paddingLeft: '0', paddingRight: '0', paddingTop: '0', paddingBottom: '0',
      transform: target.style?.transform || 'none', clipPath: target.style?.clipPath || 'none',
      opacity: target.style?.opacity || '1', width: target.style?.width, height: target.style?.height,
      getPropertyValue: name => name === '--spring' ? 'ease-out' : '' }),
    selectedRecord: { id: 1, filename: 'original.png' }, historyItems: [], historyCache: new Map(), historyPage: 1, historyPages: 1,
    Image: class { decode() { return Promise.resolve(); } }, Element: class {},
  });
  vm.runInContext(source.slice(source.indexOf('const imagePreview ='), source.indexOf('let regenerationOptions =')), ctx);
  vm.runInContext(source.slice(source.indexOf('const IMAGE_PREVIEW_MAX_ZOOM'), source.indexOf('function collapseRegenerationDrawer')), ctx);
  const doubleClickStart = source.indexOf('ui.imagePreviewImage.addEventListener("dblclick"');
  const keyboard = source.indexOf('ui.imagePreviewDialog.addEventListener("keydown"');
  vm.runInContext(source.slice(doubleClickStart, source.indexOf('ui.imagePreviewVeil.addEventListener', doubleClickStart)), ctx);
  vm.runInContext(source.slice(keyboard, source.indexOf('ui.imagePreviewDialog.addEventListener("cancel"', keyboard)), ctx);
  const resizeStart = source.indexOf('new ResizeObserver(() => {');
  const resizeEnd = source.indexOf('}).observe(ui.imagePreviewStage);', resizeStart) + '}).observe(ui.imagePreviewStage);'.length;
  vm.runInContext(source.slice(resizeStart, resizeEnd), ctx);
  const state = vm.runInContext('imagePreview', ctx);
  Object.assign(state, { phase: 'open', context: { kind: 'random' } });
  function frame(ms = 16) {
    now += ms;
    const pending = [...frames.values()];
    frames.clear();
    pending.forEach(callback => callback(now));
  }
  function settle() {
    for (let index = 0; frames.size && index < 120; index++) frame();
    assert.equal(frames.size, 0, 'rendering or zoom animation must settle');
  }
  function wheel(deltaY, extra = {}) {
    ctx.imagePreviewWheel({ deltaY, deltaMode: 0, clientX: 520, clientY: 430, preventDefault() {}, ...extra });
  }
  function doubleClick(extra = {}) {
    image.listeners.get('dblclick')({ clientX: 520, clientY: 430, x: 520, y: 430, preventDefault() {}, ...extra });
  }
  function pointer(type, id, x, y, extra = {}) {
    ctx[`imagePreviewPointer${type}`]({ type: `pointer${type.toLowerCase()}`, pointerId: id, pointerType: 'touch',
      button: 0, clientX: x, clientY: y, preventDefault() {}, ...extra });
  }
  function key(key, extra = {}) {
    ui.imagePreviewDialog.listeners.get('keydown')({ key, preventDefault() {}, stopPropagation() {}, ...extra });
  }
  ctx.layoutImagePreview();
  settle();
  image.writes.length = 0;
  return { ctx, state, ui, image, stage, frames, frame, settle, wheel, doubleClick, pointer, key, resize };
}

function near(actual, expected, message) {
  assert.ok(Math.abs(actual - expected) < 1e-7, `${message || 'values should match'}: ${actual} !== ${expected}`);
}

test('fit uses natural dimensions and never enlarges small originals', () => {
  const large = setup();
  near(large.state.fit, .23);
  near(parseFloat(large.image.style.width), 920);
  near(parseFloat(large.image.style.height), 690);
  const small = setup({ width: 240, height: 160 });
  assert.equal(small.state.fit, 1);
  assert.equal(small.image.style.width, '240px');
  assert.equal(small.image.style.height, '160px');
});

for (const devicePixelRatio of [1, 2]) {
  test(`double-click renders original pixels as CSS dimensions at DPR ${devicePixelRatio}`, () => {
    const h = setup({ devicePixelRatio });
    h.doubleClick(); h.settle();
    near(parseFloat(h.image.style.width), 4000);
    near(parseFloat(h.image.style.height), 3000);
    near(h.state.fit * h.state.zoom, 1);
    assert.doesNotMatch(h.image.style.transform, /scale\(/, 'zoom must alter rendered dimensions rather than scaling a fitted bitmap');
    h.doubleClick(); h.settle();
    assert.equal(h.state.zoom, 1);
    near(parseFloat(h.image.style.width), 920);
  });
}

test('double-click on an original that already fits keeps its original size', () => {
  const h = setup({ width: 240, height: 160 });
  h.doubleClick(); h.settle();
  assert.equal(h.state.zoom, 1);
  assert.equal(h.image.style.width, '240px');
  assert.equal(h.image.style.height, '160px');
});

test('zoom bounds are fit through 400 percent of original dimensions', () => {
  const h = setup();
  h.ctx.setImagePreviewZoom(1000); h.settle();
  near(h.state.zoom * h.state.fit, 4);
  near(parseFloat(h.image.style.width), 16000);
  near(parseFloat(h.image.style.height), 12000);
  h.ctx.setImagePreviewZoom(-100); h.settle();
  assert.equal(h.state.zoom, 1);
  assert.equal(h.state.x, 0); assert.equal(h.state.y, 0);
});

test('wheel zoom retains the image point under the pointer while clamping pan to visible edges', () => {
  const h = setup();
  h.ctx.setImagePreviewZoom(2, { x: 620, y: 490 }); h.settle();
  near(h.state.x, -100); near(h.state.y, -60);
  const before = { x: (100 - h.state.x) / h.state.zoom, y: (60 - h.state.y) / h.state.zoom };
  h.wheel(-120, { clientX: 620, clientY: 490 }); h.settle();
  near((100 - h.state.x) / h.state.zoom, before.x);
  near((60 - h.state.y) / h.state.zoom, before.y);
  h.state.x = 100000; h.state.y = -100000;
  h.ctx.applyImagePreviewTransform(); h.settle();
  near(h.state.x, (parseFloat(h.image.style.width) - h.stage.clientWidth) / 2);
  near(h.state.y, -(parseFloat(h.image.style.height) - h.stage.clientHeight) / 2);
});

test('a burst of wheel events combines its zoom changes into one rendered frame', () => {
  const h = setup();
  h.wheel(-80); h.wheel(-80); h.wheel(-80);
  assert.equal(h.frames.size, 1);
  assert.equal(h.image.writes.length, 0, 'input events should not each force a bitmap resize');
  h.frame();
  near(h.state.zoom, Math.exp(.36));
  near(parseFloat(h.image.style.width), 920 * Math.exp(.36));
  assert.equal(h.image.writes.filter(([name]) => name === 'width').length, 1);
});

test('smooth zoom renders intermediate pixel dimensions and wheel interruption continues from the visible size', () => {
  const h = setup();
  h.doubleClick(); h.frame(16); h.frame(55);
  const displayedWidth = parseFloat(h.image.style.width);
  assert.ok(displayedWidth > 920 && displayedWidth < 4000);
  near(displayedWidth, h.state.width * h.state.zoom);
  h.wheel(-80); h.settle();
  near(parseFloat(h.image.style.width), displayedWidth * Math.exp(.12));
  assert.doesNotMatch(h.image.style.transform, /scale\(/);
});

test('drag interrupts a smooth zoom at its visible size without changing zoom', () => {
  const h = setup();
  h.doubleClick(); h.frame(16); h.frame(55);
  const displayedWidth = parseFloat(h.image.style.width), displayedZoom = h.state.zoom;
  h.pointer('Down', 1, 520, 430, { pointerType: 'mouse' });
  h.pointer('Move', 1, 555, 455, { pointerType: 'mouse' });
  h.pointer('Up', 1, 555, 455, { pointerType: 'mouse' });
  h.settle();
  near(parseFloat(h.image.style.width), displayedWidth);
  near(h.state.zoom, displayedZoom);
  near(h.state.x, 35); near(h.state.y, 25);
});

test('pinch and touch double-tap share the original-size bounds', () => {
  const h = setup();
  h.pointer('Down', 1, 470, 430); h.pointer('Down', 2, 570, 430);
  h.pointer('Move', 2, 1070, 430); h.settle();
  near(h.state.zoom, 6);
  near(parseFloat(h.image.style.width), 5520);
  h.pointer('Move', 2, 100000, 430); h.settle();
  near(h.state.zoom * h.state.fit, 4);
  h.pointer('Up', 1, 470, 430); h.pointer('Up', 2, 100000, 430);
  h.ctx.resetImagePreviewTransform(); h.settle();
  h.pointer('Down', 3, 520, 430); h.pointer('Up', 3, 520, 430);
  h.frame(50);
  h.pointer('Down', 4, 520, 430); h.pointer('Up', 4, 520, 430); h.settle();
  near(parseFloat(h.image.style.width), 4000);
});

test('keyboard zoom and pan use the same dimensions and reset to fit', () => {
  const h = setup();
  h.key('+'); h.settle();
  near(h.state.zoom, 1.25); near(parseFloat(h.image.style.width), 1150);
  h.key('ArrowRight'); h.key('ArrowDown'); h.settle();
  assert.equal(h.state.x, 24); assert.equal(h.state.y, 24);
  h.key('0'); h.settle();
  assert.equal(h.state.zoom, 1); assert.equal(h.state.x, 0); assert.equal(h.state.y, 0);
  near(parseFloat(h.image.style.width), 920);
});

test('reduced motion changes dimensions in one frame without a zoom tween', () => {
  const h = setup({ reducedMotion: true });
  h.doubleClick();
  assert.equal(h.frames.size, 1);
  h.frame();
  near(parseFloat(h.image.style.width), 4000);
  assert.equal(h.frames.size, 0);
  assert.equal(h.image.animations.length, 0);
});

test('resizing preserves fit mode or the actual original scale and interrupts an in-flight zoom', () => {
  const fit = setup();
  fit.stage.clientWidth = 500; fit.stage.clientHeight = 400;
  fit.resize(); fit.settle();
  assert.equal(fit.state.zoom, 1); near(parseFloat(fit.image.style.width), 460);
  const original = setup();
  original.doubleClick(); original.settle();
  original.stage.clientWidth = 500; original.stage.clientHeight = 400;
  original.resize(); original.settle();
  near(parseFloat(original.image.style.width), 4000);
  near(original.state.zoom * original.state.fit, 1);
  const animated = setup();
  animated.doubleClick(); animated.frame(60);
  const width = parseFloat(animated.image.style.width);
  animated.stage.clientWidth = 500; animated.stage.clientHeight = 400;
  animated.resize(); animated.settle();
  near(parseFloat(animated.image.style.width), width);
  assert.equal(animated.state.gesture, null); assert.equal(animated.state.pointers.size, 0);
});

test('entry and exit release temporary scaled animations while retaining an original source', async () => {
  const h = setup();
  h.state.phase = 'closed'; h.state.context = null;
  Object.assign(h.ui.detailImage, { isConnected: true,
    getBoundingClientRect: () => ({ left: 100, top: 100, right: 340, bottom: 280, width: 240, height: 180 }) });
  h.ui.detailDialog.getBoundingClientRect = () => ({ left: 40, top: 50, right: 900, bottom: 750, width: 860, height: 700 });
  await h.ctx.openImagePreview(null, { kind: 'random', sourceElement: h.ui.detailImage, sourceContainer: h.ui.detailDialog });
  assert.equal(h.state.phase, 'opening');
  assert.equal(h.image.src, '/api/images/1');
  assert.match(h.image.animations.at(-1).frames[0].transform, /scale\(/);
  h.image.animations.at(-1).finish(); await flush();
  assert.equal(h.state.phase, 'open');
  assert.equal(h.image.getAnimations().length, 0);
  assert.doesNotMatch(h.image.style.transform, /scale\(/);
  h.ctx.closeImagePreview();
  assert.equal(h.state.phase, 'closing');
  h.image.animations.at(-1).finish(); await flush();
  assert.equal(h.state.phase, 'closed');
  assert.equal(h.image.getAnimations().length, 0);
  assert.equal(h.image.getAttribute('src'), null);
});

test('a decode completing after close cannot reveal the hidden original or restart entry', async () => {
  const h = setup();
  let resolve;
  h.image.decode = () => new Promise(done => { resolve = done; });
  h.state.phase = 'closed'; h.state.context = null;
  const opening = h.ctx.openImagePreview(null, { kind: 'random' });
  assert.equal(h.state.phase, 'loading');
  h.ctx.closeImagePreview({ immediate: true });
  resolve(); await opening;
  assert.equal(h.state.phase, 'closed');
  assert.equal(h.image.hidden, true);
  assert.equal(h.image.getAttribute('src'), null);
  assert.equal(h.image.animations.length, 0);
});

test('closing cancels queued renders and animation work and releases decoded preloads', async () => {
  const h = setup();
  let released = 0;
  h.state.preloaded.set('1', { release: () => { released++; } });
  h.doubleClick(); h.frame(20); h.frame(30);
  h.ctx.finishImagePreviewClose();
  assert.equal(h.state.phase, 'closed');
  assert.equal(h.frames.size, 0);
  assert.equal(released, 1);
  assert.equal(h.state.preloaded.size, 0);
  assert.equal(h.image.getAttribute('src'), null);
  assert.equal(h.image.hidden, true);
  const writes = h.image.writes.length;
  h.frame(500); await flush();
  assert.equal(h.image.writes.length, writes, 'late animation callbacks cannot redraw a closed viewer');
});
