const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const app = fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8');
const single = fs.readFileSync(path.join(__dirname, '../static/single-gallery.js'), 'utf8');
const flush = () => new Promise(resolve => setImmediate(resolve));
const record = id => ({ id, filename: `${id}.png`, settings: { workbench: 'single' } });
function deferred() { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function install(ctx, source, name) {
  let start = source.indexOf(`function ${name}(`);
  if (source.slice(start - 6, start) === 'async ') start -= 6;
  vm.runInContext(source.slice(start, source.indexOf('\n}', start) + 2), ctx);
}
function setup() {
  const displayed = [], notices = [], pages = [];
  const image = { src: '/api/images/1', currentSrc: '/api/images/1', hidden: false, style: {}, decode: async () => {}, getAttribute() { return this.src; }, focus() {} };
  const ctx = vm.createContext({
    imagePreview: { phase: 'open', context: { kind: 'random' }, navigationRequest: 0, navigationCursor: null, token: 0, committedSrc: '/api/images/1' },
    selectedRecord: record(1), historyItems: [1, 2, 3, 4].map(record), historyPage: 1, historyPages: 2,
    ui: { imagePreviewImage: image, imagePreviewTitle: {}, imagePreviewStatus: {}, imagePreviewDialog: { classList: { add() {}, remove() {} } } },
    selectedHistoryLocation: () => ({ page: 1, items: ctx.historyItems, index: ctx.historyItems.findIndex(value => value.id === ctx.selectedRecord.id) }),
    loadHistoryPageForPreview: async page => { pages.push(page); return { page, items: [5, 6, 7].map(record) }; },
    switchPreviewImage: async value => { displayed.push(value.id); ctx.selectedRecord = value; ctx.imagePreview.phase = 'open'; return true; },
    renderHistoryPage() {}, updatePreviewNavigation() {}, announcePreview: value => notices.push(value),
    cancelImagePreviewAnimation() {}, layoutImagePreview() {},
  });
  for (const name of ['previewNeighbor', 'navigatePreview']) install(ctx, app, name);
  return { ctx, displayed, notices, pages, image };
}
test('three rapid random navigation intents reach the third neighbor without decoding intermediate images', async () => {
  const h = setup();
  await Promise.all([h.ctx.navigatePreview(1), h.ctx.navigatePreview(1), h.ctx.navigatePreview(1)]);
  assert.deepEqual(h.displayed, [4]);
});
test('reversing navigation while a page is loading accumulates from the intended cursor', async () => {
  const h = setup(), pending = deferred(); h.ctx.selectedRecord = record(4);
  h.ctx.loadHistoryPageForPreview = page => { h.pages.push(page); return pending.promise; };
  const first = h.ctx.navigatePreview(1), second = h.ctx.navigatePreview(1), third = h.ctx.navigatePreview(-1);
  pending.resolve({ page: 2, items: [5, 6, 7].map(record) });
  await Promise.all([first, second, third]);
  assert.deepEqual(h.pages, [2]); assert.deepEqual(h.displayed, [5]);
});
test('a pending cursor cannot repaint after its dialog owner changes', async () => {
  const h = setup(), pending = deferred(); h.ctx.selectedRecord = record(4);
  h.ctx.loadHistoryPageForPreview = () => pending.promise;
  const operation = h.ctx.navigatePreview(1); await flush();
  h.ctx.imagePreview.context = { kind: 'single' }; h.ctx.imagePreview.navigationRequest++;
  pending.resolve({ page: 2, items: [5].map(record) }); await operation;
  assert.deepEqual(h.displayed, []);
});
test('failed latest decode resets intent and restores the last committed bitmap and detail together', async () => {
  const h = setup();
  for (const name of ['currentPreviewRecord', 'restorePreviewCommittedImage']) install(h.ctx, app, name);
  h.ctx.switchPreviewImage = async () => { h.image.src = '/api/images/2'; h.image.style.opacity = '0'; return false; };
  await h.ctx.navigatePreview(1);
  assert.equal(h.ctx.selectedRecord.id, 1); assert.equal(h.image.src, '/api/images/1');
  assert.equal(h.image.style.opacity, '1'); assert.equal(h.ctx.imagePreview.navigationCursor, null);
});
test('single cursor accumulates three rapid directions across one older-history read', async () => {
  const h = setup(), state = { viewerOpen: true, viewerRecord: record(4), items: [4, 3].map(record), epoch: 1, navigationIntent: 0, hasMore: true, failedImages: new Set() };
  h.ctx.imagePreview.context = { kind: 'single' }; h.ctx.singleGalleryState = state;
  h.ctx.singleGalleryLoad = async () => { h.pages.push(1); state.items.push(record(2), record(1)); state.hasMore = false; };
  h.ctx.singleGallerySelect = async value => { h.displayed.push(value.id); state.viewerRecord = value; h.ctx.imagePreview.phase = 'open'; return true; };
  install(h.ctx, single, 'singleGalleryNavigate');
  await Promise.all([h.ctx.singleGalleryNavigate(1), h.ctx.singleGalleryNavigate(1), h.ctx.singleGalleryNavigate(1)]);
  assert.deepEqual(h.pages, [1]); assert.deepEqual(h.displayed, [1]);
});
test('a superseded element decode cannot commit a record or overwrite the newest source', async () => {
  const h = setup(), old = deferred();
  h.ctx.preloadRecord = async () => ({}); h.ctx.playPreviewAnimation = async () => true;
  h.ctx.liveImagePreviewFrame = () => ({}); h.ctx.previewMotion = (_, value) => value;
  h.ctx.imagePreviewFrame = () => ({ transform: 'none' }); h.ctx.imagePreviewReducedMotion = () => true;
  h.ctx.previewPositionLabel = () => ''; h.ctx.preloadPreviewNeighbors = () => {}; h.ctx.renderDetail = () => {};
  h.image.decode = () => h.image.src.endsWith('/2') ? old.promise : Promise.resolve();
  for (const name of ['currentPreviewRecord', 'setCurrentPreviewRecord', 'switchPreviewImage']) install(h.ctx, app, name);
  h.ctx.imagePreview.navigationRequest = 1; h.ctx.imagePreview.phase = 'switching';
  const first = h.ctx.switchPreviewImage(record(2), 1); await flush();
  assert.equal(h.ctx.selectedRecord.id, 1, 'preparing a bitmap does not commit its detail');
  h.ctx.imagePreview.navigationRequest = 2;
  await h.ctx.switchPreviewImage(record(3), 2);
  old.resolve(); assert.equal(await first, false);
  assert.equal(h.ctx.selectedRecord.id, 3); assert.equal(h.image.src, '/api/images/3');
  assert.equal(h.ctx.imagePreview.committedSrc, '/api/images/3');
});
