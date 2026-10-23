const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
function context() {
  const ctx = vm.createContext({ escapeHtml: value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('"', '&quot;') });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../static/single-details.js'), 'utf8'), ctx);
  return ctx;
}
function record() {
  return { sample_seed: 0, positive_prompt: 'saved final', negative_prompt: 'negative',
    settings: { workbench: 'single', generation_mode: 'txt2img', base_mode: 'normal', model_profile: 'anima', model_name: 'old-model', width: 512, height: 768, steps: 20, cfg: 4, sampler_name: 'euler', scheduler: 'simple',
      loras: [{ filename: 'lora', enabled: true, strength: 0.75 }], single: { normal_pipeline: 'primary', positive_prompt: 'source', prefix: '', suffix: '', zero_negative: false, latent: { pipeline: 'primary', upscale_method: 'sesqui' } },
      upscalers: { latent: { enabled: true, scale_by: 1.5, second_sampler: { enabled: false } } } },
    bound_resources: { unet: 'actual/model', loras: ['actual/lora'] }, single_runtime: { positive: 'processed', effective_positive: { primary: 'effective main', secondary: 'effective other' }, negative: 'actual negative' } };
}
test('details use historical resource and prompt snapshots, keeping zero seeds and actual size separate', () => {
  const data = context().singleDetailData(record());
  const basic = new Map(data.basic);
  assert.equal(basic.get('主模型'), 'actual/model');
  assert.equal(basic.get('图像种子'), 0);
  assert.equal(basic.get('底图尺寸'), '512 × 768');
  assert.equal(basic.get('最终图片尺寸'), '读取中…');
  assert.equal(data.positive, 'effective main'); assert.equal(data.negative, 'actual negative');
  assert.equal(data.loras[0].filename, 'actual/lora'); assert.equal(data.loras[0].strength, .75);
  assert.equal(data.stages.length, 1);
  assert.equal(data.repairs[0].rows.find(row => row[0] === '缩放方法')[1], undefined);
});
test('dual snapshots show independent second parameters, LoRA stages and different effective prompts', () => {
  const saved = record(); saved.settings.base_mode = 'dual_sampler';
  saved.settings.single.dual = { first_pipeline: 'primary', second_pipeline: 'secondary', switch_step: 12, scale: 1.25, upscale_method: 'bicubic', second_cfg: 2, second_sampler_name: 'heun', second_scheduler: 'beta' };
  saved.settings.single.loras_secondary = [{ filename: 'other', enabled: true, strength: .6 }];
  saved.settings.single.secondary_zero_negative = true;
  const data = context().singleDetailData(saved);
  assert.equal(data.positive, 'effective other'); assert.equal(data.negative, 'actual negative'); assert.equal(data.zeroNegative, true);
  assert.equal(new Map(data.stages[1].rows).get('CFG'), 2);
  assert.equal(new Map(data.stages[1].rows).get('起始步'), 12);
  assert.equal(new Map(data.stages[1].rows).get('结束步'), 20);
  assert.equal(data.loras[0].stages, '第一段采样'); assert.equal(data.loras[1].stages, '第二段采样');
  assert.ok(data.promptSources.some(row => row[0] === '第一段采样 · 正向' && row[1] === 'effective main'));
  assert.ok(data.promptSources.some(row => row[0] === '第一段采样 · 负向（使用文本）' && row[1] === 'actual negative'));
});
test('img2img details use the recorded asset, denoise and fit and sparse old records never invent defaults', () => {
  const saved = record(); saved.settings.generation_mode = 'img2img';
  Object.assign(saved.settings.single, { input_image_id: 'old', denoise: .65, resize_mode: 'pad' }); saved.single_runtime.input_image_id = 'actual';
  const ctx = context(), data = ctx.singleDetailData(saved);
  assert.equal(new Map(data.basic).get('参考图'), 'actual');
  assert.equal(new Map(data.basic).get('尺寸适配'), '适应并留边');
  assert.equal(new Map(data.stages[0].rows).get('降噪幅度'), .65);
  const old = ctx.singleDetailData({ settings: { workbench: 'single' }, positive_prompt: '' });
  assert.equal(old.positive, ''); assert.equal(new Map(old.basic).get('主模型'), undefined);
  assert.equal(old.repairs[0].enabled, undefined);
  assert.equal(old.lorasKnown, false);
  assert.match(ctx.singleDetailRows(old.basic), /未记录/);
});
test('historical text is escaped in generated detail markup', () => {
  assert.equal(context().singleDetailRows([['模型', '<img onerror="bad">']]).includes('<img'), false);
});
test('face prompt overrides are identified as node processing, not misreported as final pipeline text', () => {
  const saved = record(); saved.settings.single.face_pipeline = 'secondary';
  saved.settings.upscalers.face_refine = { enabled: true, face_positive_prompt: 'custom face', replace_positive_prompt: true, exclusions: 'smile' };
  const data = context().singleDetailData(saved);
  assert.equal(data.lorasKnown, false, 'missing secondary LoRA snapshot is unknown');
  assert.ok(data.promptSources.some(row => row[0] === '脸部细化 · 传入正向' && row[1] === 'effective other'));
  assert.ok(data.promptSources.some(row => row[0] === '脸部节点 · 替换正向文本' && row[1] === 'custom face'));
  assert.match(new Map(data.repairs.find(stage => stage.title === '脸部细化').rows).get('提示词处理'), /重新编码/);
});

test('dual noise and actual Sesqui and VAE settings are retained in collapsed historical groups', () => {
  const saved = record(); saved.settings.base_mode = 'dual_sampler';
  saved.settings.single.dual = { first_pipeline: 'primary', second_pipeline: 'primary', upscale_method: 'sesqui',
    first_add_noise: false, first_leftover_noise: true, second_add_noise: true, second_leftover_noise: false,
    model_format: 'Flux2', half_precision: false };
  saved.settings.single.vae_decode = { tiled: true, tile_size: 768, overlap: 32, temporal_size: 20, temporal_overlap: 4 };
  const before = JSON.stringify(saved), ctx = context(), data = ctx.singleDetailData(saved);
  const first = new Map(data.stages[0].otherGroups.find(group => group.title === '噪声处理').rows);
  assert.equal(first.get('添加噪声'), false); assert.equal(first.get('保留残余噪声'), true);
  const sesqui = new Map(data.stages[1].otherGroups.find(group => group.title === 'Sesqui 潜空间模型').rows);
  assert.equal(sesqui.get('模型格式'), 'Flux2'); assert.equal(sesqui.get('半精度'), false);
  const vae = new Map(data.stages[1].otherGroups.find(group => group.title === '底图 VAE 解码').rows);
  assert.equal(vae.get('空间分块大小'), 768); assert.equal(vae.get('时间重叠'), 4);
  const markup = ctx.singleDetailOtherParameters(data.stages[1].otherGroups);
  assert.match(markup, /<details class="single-detail-other"><summary>其他历史参数/);
  assert.doesNotMatch(markup, /<details[^>]*\bopen\b/);
  assert.equal(JSON.stringify(saved), before);
});

test('enabled repair snapshots expose saved low-frequency parameters in execution order', () => {
  const saved = record();
  saved.settings.single.vae_encode = { tiled: false, tile_size: 1024 };
  saved.settings.single.latent = { pipeline: 'primary', upscale_method: 'sesqui', model_format: 'Wan 2.1', half_precision: false };
  saved.settings.single.dlss = { enabled: true, local_tone: 0, local_struct: .3, output_view: '左右对比', gpu_gen: 'RTX 40' };
  saved.settings.upscalers.seedvr2 = { enabled: true, weight_dtype: 'fp8_e4m3fn',
    encode: { tile_size: 640, overlap: 80, temporal_size: 32, temporal_overlap: 4 },
    decode: { tile_size: 896, overlap: 96, temporal_size: 128, temporal_overlap: 16 } };
  saved.settings.upscalers.face_refine = { enabled: true, face_selection: 0, detection_confidence: .81, crop_padding: 2.2,
    enable_pre_upscale: true, upscaler_model: 'saved-model', blend_softness: 6, mask_expansion: 3, sampling_mask_blur_size: 13,
    sampling_mask_blur_strength: 0, enable_segmentation: false, offload_models_to_cpu: true };
  const data = context().singleDetailData(saved);
  assert.deepEqual(Array.from(data.repairs, stage => stage.title), ['潜空间放大', '脸部细化', 'SeedVR2', 'N 卡放大']);
  const latent = data.repairs[0].otherGroups.find(group => group.title === '潜空间 VAE 编码');
  assert.equal(latent.rows.length, 1, 'disabled tiling must not present unused saved tile sizes as effective');
  assert.equal(latent.rows[0][1], false);
  const seed = data.repairs[2];
  assert.equal(new Map(seed.otherGroups.find(group => group.title === 'SeedVR2 VAE 编码').rows).get('空间分块大小'), 640);
  assert.equal(new Map(seed.otherGroups.find(group => group.title === 'SeedVR2 VAE 解码').rows).get('空间分块大小'), 896);
  const dlss = new Map([...data.repairs[3].rows, ...data.repairs[3].otherGroups[0].rows]);
  assert.equal(dlss.get('局部光影'), 0); assert.equal(dlss.get('输出视图'), '左右对比');
  const faceGroups = data.repairs[1].otherGroups;
  assert.equal(new Map(faceGroups.find(group => group.title === '检测与裁切').rows).get('脸部选择'), 0);
  assert.equal(new Map(faceGroups.find(group => group.title === '预放大').rows).get('预放大模型'), 'saved-model');
  assert.equal(new Map(faceGroups.find(group => group.title === '蒙版与融合').rows).get('采样蒙版模糊强度'), 0);
  assert.equal(new Map(faceGroups.find(group => group.title === '修复选项').rows).get('图像分割'), false);
});

test('missing extra parameters remain unknown and unused encoders are omitted', () => {
  const ctx = context(), saved = record(); saved.settings.upscalers.latent.enabled = false;
  const data = ctx.singleDetailData(saved);
  assert.equal(data.stages[0].otherGroups.some(group => group.title === '参考图 VAE 编码'), false);
  const vae = data.stages[0].otherGroups.find(group => group.title === '底图 VAE 解码');
  assert.ok(vae.rows.every(row => row[1] === undefined));
  assert.match(ctx.singleDetailOtherParameters([vae]), /未记录/);
  saved.settings.generation_mode = 'img2img';
  saved.settings.single.vae_encode = { tiled: true, tile_size: 256 };
  saved.settings.single.krea_variance = { enabled: true, auto_strength_factor: 0, noise_insert: 'disabled' };
  saved.prompt_seed = 0;
  const img = ctx.singleDetailData(saved);
  assert.equal(new Map(img.basic).get('变化种子'), 0);
  assert.equal(new Map(img.stages[0].otherGroups.find(group => group.title === '参考图 VAE 编码').rows).get('空间分块大小'), 256);
  assert.equal(new Map(img.stages[0].otherGroups.find(group => group.title === 'Krea 变化控制').rows).get('自动强度系数'), 0);
  assert.equal(ctx.singleDetailOtherParameters(), '');
});

test('extra markup and saved reference URLs escape historical text without metadata lookups', () => {
  const ctx = context();
  const markup = ctx.singleDetailOtherParameters([{ title: '<script>', rows: [['旧模型', '<img onerror="bad">']] }]);
  assert.doesNotMatch(markup, /<script>|<img/);
  const id = 'a'.repeat(64), reference = ctx.singleDetailReferenceMarkup(id);
  assert.match(reference, new RegExp(`/api/input-images/${id}/image`));
  assert.match(reference, new RegExp(`title="${id}"`));
  assert.match(reference, /aaaaaaaaaaaa…<\/code>/);
  assert.match(ctx.singleDetailReferenceMarkup('asset/"name'), /asset%2F%22name/);
  assert.doesNotMatch(ctx.singleDetailReferenceMarkup(undefined), /<img/);
});

test('reference load failure only changes the thumbnail status and late image events cannot affect another record', () => {
  const ctx = context(), saved = record(); saved.id = 7; saved.settings.generation_mode = 'img2img';
  saved.single_runtime.input_image_id = 'a'.repeat(64);
  const status = { hidden: true }, thumbnail = { complete: false, naturalWidth: 0, naturalHeight: 0, hidden: false };
  const classes = new Set();
  const reference = { isConnected: true, querySelector: selector => selector === 'img' ? thumbnail : status,
    classList: { toggle(name, value) { value ? classes.add(name) : classes.delete(name); } } };
  const label = { firstChild: { textContent: '' } }, promptHost = { append() {} }, outputSize = {};
  ctx.selectedRecord = saved;
  ctx.ui = { detailDialog: { classList: { toggle() {}, remove() {} } }, detailStats: { innerHTML: '', querySelector: () => outputSize },
    detailMain: { append() {} }, detailScroll: { append() {} },
    detailPositive: { closest: () => label }, detailNegative: { closest: () => label },
    detailSelection: { innerHTML: '', querySelector: selector => selector === '[data-single-prompts]' ? promptHost : reference, querySelectorAll: () => [] },
    detailImage: { complete: false, naturalWidth: 1024, naturalHeight: 1536, getAttribute: () => '/api/images/7' }, variantButton: {}, variantModes: { querySelectorAll: () => [] },
    replayDrawer: { querySelector: () => ({}) } };
  ctx.renderSingleDetail(saved);
  assert.doesNotMatch(ctx.ui.detailStats.innerHTML, /a{64}/);
  const params = ctx.ui.detailStats.innerHTML;
  thumbnail.onerror();
  assert.equal(thumbnail.hidden, true); assert.equal(status.hidden, false);
  assert.equal(ctx.ui.detailStats.innerHTML, params);
  ctx.ui.detailImage.onload();
  assert.equal(outputSize.textContent, '1024 × 1536');
  ctx.selectedRecord = { id: 8 };
  thumbnail.naturalWidth = thumbnail.naturalHeight = 100; thumbnail.onload();
  assert.equal(status.hidden, false, 'stale thumbnail event is ignored');
  ctx.ui.detailImage.naturalWidth = 888;
  ctx.ui.detailImage.onload();
  assert.equal(outputSize.textContent, '1024 × 1536', 'stale image dimensions are ignored');
});

test('the compact summary keeps historical model, zero seed and independent image dimensions', () => {
  const ctx = context(), saved = record(), before = JSON.stringify(saved);
  const rows = ctx.singleDetailSummary(ctx.singleDetailData(saved)), summary = new Map(rows);
  assert.deepEqual([...summary.keys()], ['主模型', '生成／采样', '图像种子', '底图尺寸', '最终图片尺寸']);
  assert.deepEqual(Array.from(rows, row => row[2]), ['model', 'generation-sampling', 'sample-seed', 'base-size', 'output-size']);
  assert.equal(summary.get('主模型'), 'actual/model');
  assert.equal(summary.get('图像种子'), 0);
  assert.equal(summary.get('生成／采样'), '文生图 · 普通采样');
  assert.equal(summary.get('底图尺寸'), '512 × 768');
  assert.equal(summary.get('最终图片尺寸'), '读取中…');
  assert.equal(JSON.stringify(saved), before);
});

test('summary combines recorded generation and sampling modes without inventing legacy defaults', () => {
  const ctx = context(), saved = record();
  saved.settings.generation_mode = 'img2img'; saved.settings.base_mode = 'dual_sampler';
  assert.equal(new Map(ctx.singleDetailSummary(ctx.singleDetailData(saved))).get('生成／采样'), '图生图 · 双采样');
  delete saved.settings.base_mode;
  assert.equal(new Map(ctx.singleDetailSummary(ctx.singleDetailData(saved))).get('生成／采样'), '图生图 · 未记录');
  delete saved.settings.generation_mode;
  assert.equal(new Map(ctx.singleDetailSummary(ctx.singleDetailData(saved))).get('生成／采样'), undefined);
  saved.settings.base_mode = 'normal';
  assert.equal(new Map(ctx.singleDetailSummary(ctx.singleDetailData(saved))).get('生成／采样'), '未记录 · 普通采样');
  const legacy = ctx.singleDetailSummary(ctx.singleDetailData({}));
  assert.equal(new Map(legacy).get('图像种子'), undefined);
  assert.match(ctx.singleDetailRows(legacy), /data-detail-value="generation-sampling"><dt>生成／采样<\/dt><dd>未记录<\/dd>/);
});

test('summary model hover text is complete and escaped, and seeds remain complete including zero', () => {
  const ctx = context(), saved = record();
  saved.bound_resources.unet = 'folder/"<model>' + ' long-model'.repeat(30) + '.safetensors';
  saved.sample_seed = '18446744073709551615';
  let markup = ctx.singleDetailRows(ctx.singleDetailSummary(ctx.singleDetailData(saved)));
  assert.match(markup, /data-detail-value="model"/);
  assert.ok(markup.includes(`title="${ctx.escapeHtml(saved.bound_resources.unet)}"`));
  assert.doesNotMatch(markup, /<model>/);
  assert.match(markup, /data-detail-value="sample-seed"><dt>图像种子<\/dt><dd>18446744073709551615<\/dd>/);
  saved.sample_seed = 0;
  markup = ctx.singleDetailRows(ctx.singleDetailSummary(ctx.singleDetailData(saved)));
  assert.match(markup, /data-detail-value="sample-seed"><dt>图像种子<\/dt><dd>0<\/dd>/);
});

test('all six groups start closed and retain model, prompt, LoRA, repair and reference snapshots', () => {
  const ctx = context(), saved = record();
  saved.settings.generation_mode = 'img2img';
  saved.settings.single.input_image_id = 'saved-reference';
  saved.settings.single.resize_mode = 'pad';
  saved.settings.single.weight_dtype = 'fp8_e4m3fn';
  saved.settings.single.prefix = '<script>saved prefix</script>';
  saved.settings.single.positive_prompt = 'long saved prompt '.repeat(300);
  const markup = ctx.singleDetailGroups(ctx.singleDetailData(saved));
  assert.deepEqual([...markup.matchAll(/data-single-detail-group="([^"]+)"/g)].map(match => match[1]), ['models', 'prompts', 'sampling', 'loras', 'repairs', 'reference']);
  assert.doesNotMatch(markup, /<details[^>]*\bopen\b/);
  assert.match(markup, /fp8_e4m3fn/);
  assert.match(markup, /actual\/lora/);
  assert.match(markup, /long saved prompt /);
  assert.match(markup, /&lt;script>saved prefix/);
  assert.match(markup, /saved-reference\/image/);
  assert.match(markup, /适应并留边/);
  assert.match(markup, /1 项启用/);
  assert.match(markup, /Sesqui 潜空间模型/);
});

test('sparse legacy records show unknown statuses while txt2img reference is explicitly inapplicable', () => {
  const ctx = context();
  const old = ctx.singleDetailGroups(ctx.singleDetailData({ settings: { workbench: 'single' } }));
  assert.match(old, /采样阶段<\/span><small>未记录/);
  assert.match(old, /参考图<\/span><small>未记录/);
  assert.doesNotMatch(old, /<small>1 段|未使用<\/p>/);
  assert.match(ctx.singleDetailGroups(ctx.singleDetailData(record())), /文生图未使用参考图/);
});

test('opening one top-level historical group closes its siblings and leaves nested details independent', () => {
  const ctx = context();
  const groups = Array.from({ length: 6 }, () => ({ open: false, addEventListener(name, handler) { this.toggle = handler; } }));
  ctx.bindSingleDetailGroups({ querySelectorAll: () => groups });
  groups[0].open = true; groups[0].toggle();
  groups[2].open = true; groups[2].toggle();
  assert.deepEqual(groups.map(group => group.open), [false, false, true, false, false, false]);
  groups[2].open = false; groups[2].toggle();
  assert.ok(groups.every(group => !group.open));
});

test('switching workbenches preserves shared prompt nodes and returns drawers to their original home', () => {
  const ctx = context(), positive = {}, negative = {}, classes = new Set();
  const mount = () => ({ append(...children) { this.children = children; }, scrollTop: 500 });
  ctx.ui = { detailMain: mount(), detailScroll: mount(), detailDrawerHome: mount(), replayDrawer: {}, variantDrawer: {},
    detailPositive: { closest: () => positive }, detailNegative: { closest: () => negative },
    detailDialog: { classList: { toggle(name, value) { value ? classes.add(name) : classes.delete(name); }, remove(name) { classes.delete(name); } } } };
  ctx.singleDetailLayout(true);
  assert.deepEqual(ctx.ui.detailMain.children, [positive, negative]);
  assert.deepEqual(ctx.ui.detailScroll.children, [ctx.ui.replayDrawer, ctx.ui.variantDrawer]);
  assert.equal(ctx.ui.detailScroll.scrollTop, 0);
  assert.equal(classes.has('is-single-detail'), true);
  classes.add('is-regenerating');
  ctx.singleDetailLayout(false);
  assert.deepEqual(ctx.ui.detailDrawerHome.children, [ctx.ui.replayDrawer, ctx.ui.variantDrawer]);
  assert.equal(classes.size, 0);
});
