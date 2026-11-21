const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../static/detail-image-intent.js'), 'utf8');
function events() {
  const listeners = new Map();
  return {
    addEventListener(name, handler) { listeners.set(name, handler); },
    fire(name, extra = {}) { listeners.get(name)?.(extra); },
  };
}
function setup() {
  const document = events(), navigator = { onLine: true, connection: {} }, images = [], timers = new Map();
  let now = 0, nextTimer = 0;
  const ctx = vm.createContext({
    document, navigator,
    setTimeout(fn, delay) { timers.set(++nextTimer, { fn, time: now + delay }); return nextTimer; },
    clearTimeout(id) { timers.delete(id); },
    Image: class {
      constructor() { images.push(this); }
      decode() { return new Promise((resolve, reject) => { this.succeed = resolve; this.fail = reject; }); }
      removeAttribute(name) { if (name === 'src') this.src = ''; }
    },
  });
  const intent = vm.runInContext(source + '\nDetailImageIntent', ctx);
  function add(id, shouldWarm) {
    const button = Object.assign(events(), { isConnected: true, matches: () => true });
    intent.bind(button, id, shouldWarm);
    return button;
  }
  function move(button, clientX, clientY = 10) {
    document.fire('pointermove', { pointerType: 'mouse', buttons: 0, movementX: 0, movementY: 0,
      clientX, clientY, target: { closest: () => button } });
  }
  function wait(ms) {
    const end = now + ms;
    while (true) {
      const next = [...timers.entries()].filter(([, timer]) => timer.time <= end).sort((a, b) => a[1].time - b[1].time)[0];
      if (!next) break;
      timers.delete(next[0]); now = next[1].time; next[1].fn();
    }
    now = end;
  }
  return { intent, document, navigator, images, timers, add, move, wait };
}
const mouse = { pointerType: 'mouse', button: 0 };
const touch = { pointerType: 'touch', button: 0 };

test('hover dwell skips flyovers and another card cancels unclaimed original work', async () => {
  const h = setup(), a = h.add(1), b = h.add(2);
  h.move(a, 20); h.wait(109);
  a.fire('pointerleave', mouse); h.wait(20);
  assert.equal(h.images.length, 0);
  h.move(a, 22); h.wait(110);
  const older = h.images[0];
  assert.equal(older.src, '/api/images/1');
  h.move(b, 52);
  assert.equal(older.src, '');
  h.wait(110); older.fail(); h.images[1].succeed(); await Promise.resolve();
  const latest = h.intent.take('2');
  assert.equal(latest, h.images[1]);
  assert.equal(latest.decoding, 'async');
});

test('primary press starts immediately and transfers an in-flight decode exactly once', async () => {
  const h = setup(), card = h.add(7);
  h.move(card, 13); h.wait(20);
  card.fire('pointerdown', mouse); card.fire('pointerdown', mouse);
  assert.equal(h.images.length, 1);
  const image = h.images[0];
  image.complete = true;
  assert.equal(h.intent.take(7), null, 'complete alone does not guarantee decoded pixels');
  h.intent.clear(); card.fire('blur'); card.fire('pointerleave', mouse); h.wait(60000);
  assert.equal(image.src, '/api/images/7');
  image.succeed(); await Promise.resolve();
  assert.equal(h.intent.take(7), null);
  assert.equal(h.images.length, 1);
});

test('decoded images are ready at handoff and failed images fall back to the normal click path', async () => {
  const h = setup(), card = h.add(9);
  card.fire('pointerdown', mouse); h.images[0].fail(); await Promise.resolve();
  assert.equal(h.intent.take(9), null);
  card.fire('pointerdown', mouse);
  assert.equal(h.intent.take(9), null);
  h.images[1].fail(); await Promise.resolve();
  assert.equal(h.images[1].src, '/api/images/9', 'gallery failure handling cannot cancel the handed-off request');
  card.fire('pointerdown', mouse); h.images[2].succeed(); await Promise.resolve();
  assert.equal(h.intent.take(9), h.images[2]);
  assert.equal(h.intent.take(9), null);
});

test('touch prepares on primary press and a cancelled gesture releases the original', async () => {
  const h = setup(), card = h.add(8);
  card.fire('pointerenter', touch); h.wait(200);
  card.fire('pointerdown', { ...touch, isPrimary: false }); card.fire('pointerdown', { ...mouse, button: 2 });
  assert.equal(h.images.length, 0);
  card.fire('pointerdown', touch); card.fire('pointercancel', touch);
  assert.equal(h.images[0].src, '');
  card.fire('pointerdown', touch); card.fire('pointerleave', touch);
  h.images[1].succeed(); await Promise.resolve();
  assert.equal(h.intent.take(8), h.images[1], 'the native touch leave can precede click');
  card.fire('pointerdown', touch); card.fire('pointerleave', touch); h.wait(400);
  assert.equal(h.intent.take(8), null, 'a release without a click drops the unclaimed original');
});

test('keyboard focus dwells, blur cancels and restored focus or stationary hover cannot fetch again', async () => {
  const h = setup(), card = h.add(3);
  h.move(null, 18);
  h.document.fire('keydown', { key: 'Tab' }); card.fire('focus'); h.wait(20); card.fire('blur'); h.wait(110);
  assert.equal(h.images.length, 0);
  h.document.fire('keydown', { key: 'Tab' }); card.fire('focus'); h.wait(110);
  h.images[0].succeed(); await Promise.resolve();
  assert.equal(h.intent.take(3), h.images[0]); card.fire('blur');
  card.fire('focus'); card.fire('pointerenter', mouse); h.move(card, 18); h.wait(120);
  assert.equal(h.images.length, 1);
  h.move(card, 19); h.wait(110);
  assert.equal(h.images.length, 2);
});

test('eligibility is checked again at dwell time; cancellation and expiry release pending images', () => {
  const h = setup(); let allowed = true;
  const card = h.add(4, () => allowed);
  h.move(card, 20); allowed = false; h.wait(120);
  allowed = true; h.navigator.connection.saveData = true; card.fire('pointerdown', mouse);
  h.navigator.connection.saveData = false; h.move(card, 21); h.intent.clear(); h.wait(120);
  assert.equal(h.images.length, 0);
  card.fire('pointerdown', mouse); h.wait(60000);
  assert.equal(h.images[0].src, ''); assert.equal(h.intent.take(4), null);
  card.fire('pointerdown', mouse);
  assert.equal(h.intent.take(5), null); assert.equal(h.images[1].src, '');
});

test('scrolling a card under a stationary mouse cannot prefetch, and real movement starts only one dwell timer', () => {
  const h = setup(), card = h.add(6);
  h.move(null, 40);
  card.fire('pointerenter', mouse); h.move(card, 40); h.wait(150);
  assert.equal(h.images.length, 0);
  h.move(card, 41); h.wait(60);
  for (let x = 42; x < 82; x++) h.move(card, x);
  assert.equal(h.timers.size, 1);
  h.wait(50);
  assert.equal(h.images.length, 1, 'subsequent movements within the card do not restart dwell');
});

test('going to the background releases unclaimed work and offline localhost remains eligible', () => {
  const h = setup(), card = h.add(7);
  h.navigator.onLine = false;
  card.fire('pointerdown', mouse); assert.equal(h.images[0].src, '/api/images/7');
  h.document.hidden = true; h.document.fire('visibilitychange');
  assert.equal(h.images[0].src, '');
  card.fire('pointerdown', mouse); assert.equal(h.images.length, 1);
  h.document.hidden = false; card.fire('pointerdown', mouse);
  assert.equal(h.intent.take(7), null);
  h.document.hidden = true; h.document.fire('visibilitychange');
  assert.equal(h.images[1].src, '/api/images/7', 'the foreground detail took ownership before the tab was hidden');
});
