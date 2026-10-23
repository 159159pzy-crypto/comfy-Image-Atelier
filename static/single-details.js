/* Historical details deliberately do not normalize against today's defaults. */
function singleDetailValue(value) {
  return value === undefined || value === null ? "未记录" : typeof value === "boolean" ? (value ? "开启" : "关闭") : String(value);
}
function singleDetailRows(rows) {
  return rows.map(([label, value, key]) => `<div${key ? ` data-detail-value="${escapeHtml(key)}"` : ""}><dt>${escapeHtml(label)}</dt><dd${key === "model" ? ` title="${escapeHtml(singleDetailValue(value))}"` : ""}>${escapeHtml(singleDetailValue(value))}</dd></div>`).join("");
}
function singleDetailOtherParameters(groups = []) {
  const content = groups.filter(group => group.rows?.length).map(group => `<div class="single-detail-parameter-group"><h4>${escapeHtml(group.title)}</h4><dl>${singleDetailRows(group.rows)}</dl></div>`).join("");
  return content ? `<details class="single-detail-other"><summary>其他历史参数</summary>${content}</details>` : "";
}
function singleDetailReferenceMarkup(id) {
  if (typeof id !== "string" || !id) return '<section class="single-detail-reference"><p>参考图未记录</p></section>';
  const shortId = id.length > 16 ? `${id.slice(0, 12)}…` : id;
  return `<section class="single-detail-reference" data-single-reference><img src="/api/input-images/${escapeHtml(encodeURIComponent(id))}/image" alt="生成时保存的参考图"><div><strong>已保存参考图</strong><code title="${escapeHtml(id)}">${escapeHtml(shortId)}</code><p data-reference-status role="status" hidden>参考图不可用</p></div></section>`;
}
function singleDetailData(record) {
  const settings = record.settings || {}, single = settings.single || {}, runtime = record.single_runtime || {}, bound = record.bound_resources || {};
  const mode = settings.generation_mode, dual = (settings.workbench === "single" ? single.dual : settings.dual) || {}, post = settings.upscalers || {};
  const variance = settings.krea_variance || single.krea_variance;
  const dlss = post.nvidia?.dlss || single.dlss;
  const primaryNegative = runtime.negative ?? record.negative_prompt ?? settings.negative_prompt;
  const pipelinePrompt = name => runtime.effective_positive?.[name];
  const zeroFor = name => name === "primary" ? single.zero_negative : name === "secondary" ? single.secondary_zero_negative : undefined;
  const stages = [];
  const face = post.face_refine || {};
  const faceReencodes = Boolean(face.face_positive_prompt?.trim() || face.face_negative_prompt?.trim() || face.exclusions?.trim());
  const fields = (value, definitions) => definitions.map(([key, label]) => [label, value?.[key]]);
  const tiles = (title, value, toggle = true) => ({ title, rows: [
    ...(toggle ? [["分块处理", value?.tiled]] : []),
    ...(toggle && value?.tiled === false ? [] : fields(value, [["tile_size", "空间分块大小"], ["overlap", "空间重叠"], ["temporal_size", "时间分块大小"], ["temporal_overlap", "时间重叠"]])),
  ] });
  const sesqui = value => value?.upscale_method === "sesqui" ? [{ title: "Sesqui 潜空间模型", rows: fields(value, [["model_format", "模型格式"], ["half_precision", "半精度"]]) }] : [];
  const baseGroups = [{ title: "模型与文本编码", rows: [["模型权重精度", single.weight_dtype], ["CLIP 类型", settings.clip_type]] }];
  if (variance?.enabled) baseGroups.push({ title: "Krea 变化控制", rows: fields(variance, [
    ["auto_strength_factor", "自动强度系数"], ["noise_insert", "噪声注入阶段"], ["steps_switchover_percent", "步数切换比例"],
    ["mask_starts_at", "遮罩起点"], ["mask_percent", "遮罩比例"], ["noise_distribution", "噪声分布"], ["granularity", "变化粒度"],
  ]) });
  const samplerRows = (source, second = false) => [
    ["步数", settings.steps], ["CFG", second ? source.second_cfg : settings.cfg],
    ["采样器", second ? source.second_sampler_name : settings.sampler_name],
    ["调度器", second ? source.second_scheduler : settings.scheduler],
  ];
  if (settings.base_mode === "dual_sampler") {
    stages.push({ title: "第一段采样", pipeline: dual.first_pipeline, rows: [...samplerRows(settings), ["起始步", 0], ["结束步", settings.steps]], otherGroups: [...baseGroups, { title: "噪声处理", rows: [["添加噪声", dual.first_add_noise], ["保留残余噪声", dual.first_leftover_noise]] }] });
    stages.push({ title: "第二段采样", pipeline: dual.second_pipeline, rows: [...samplerRows(dual, true), ["起始步", dual.switch_step], ["结束步", settings.steps], ["中间放大", dual.scale == null ? undefined : `${dual.scale} ×`], ["放大算法", dual.upscale_method]], otherGroups: [{ title: "噪声处理", rows: [["添加噪声", dual.second_add_noise], ["保留残余噪声", dual.second_leftover_noise]] }, ...sesqui(dual), tiles("底图 VAE 解码", single.vae_decode)] });
  } else {
    stages.push({ title: "底图采样", pipeline: single.normal_pipeline, rows: [...samplerRows(settings), ["降噪幅度", mode === "img2img" ? single.denoise : settings.base_denoise]], otherGroups: [...baseGroups, ...(mode === "img2img" ? [tiles("参考图 VAE 编码", single.vae_encode)] : []), tiles("底图 VAE 解码", single.vae_decode)] });
  }
  const basePipeline = settings.base_mode === "dual_sampler" ? dual.second_pipeline : single.normal_pipeline;
  const positive = pipelinePrompt(basePipeline) ?? record.positive_prompt ?? record.resolved_prompt ?? runtime.positive;
  const negative = primaryNegative, zeroNegative = zeroFor(basePipeline);
  if (post.latent?.enabled && post.latent.second_sampler?.enabled) stages.push({ title: "潜空间二次采样", pipeline: single.latent?.pipeline, rows: [] });
  if (post.face_refine?.enabled) stages.push({ title: "脸部细化", pipeline: single.face_pipeline, rows: [] });
  for (const stage of stages) {
    stage.positive = pipelinePrompt(stage.pipeline);
    stage.negative = primaryNegative; stage.zeroNegative = zeroFor(stage.pipeline);
    stage.pipelineLabel = stage.pipeline === "primary" ? "主 LoRA 列表" : stage.pipeline === "secondary" ? "旧副管线 LoRA" : "未记录";
  }
  const loras = ["primary", "secondary"].flatMap(name => {
    const active = (name === "primary" ? settings.loras : single.loras_secondary)?.filter(item => item.enabled);
    const paths = name === "primary" ? bound.loras : bound.loras_secondary;
    const used = stages.filter(stage => stage.pipeline === name).map(stage => stage.title);
    if (!used.length) return [];
    return (active || []).map((item, index) => ({ filename: paths?.[index] ?? item.filename, strength: item.strength, stages: used.join("、") }));
  });
  const sampler = value => [["步数", value?.steps], ["CFG", value?.cfg], ["采样器", value?.sampler_name], ["调度器", value?.scheduler], ["降噪", value?.denoise]];
  const repairs = [
    { title: "潜空间放大", enabled: post.latent?.enabled, rows: [["倍率", post.latent?.scale_by], ["缩放方法", single.latent?.model_format ?? post.latent?.model_format], ["二次采样", post.latent?.second_sampler?.enabled], ...(post.latent?.second_sampler?.enabled ? sampler(post.latent.second_sampler) : [])], otherGroups: [...sesqui(single.latent), tiles("潜空间 VAE 编码", single.vae_encode), tiles("潜空间 VAE 解码", single.vae_decode)] },
    { title: "SeedVR2", enabled: post.seedvr2?.enabled, rows: [["模型", bound.seedvr2_model ?? post.seedvr2?.model_name], ["VAE", bound.seedvr2_vae ?? post.seedvr2?.vae_name], ["颜色匹配方法", post.seedvr2?.color_correction_method], ["倍率", post.seedvr2?.scale_by]], otherGroups: [{ title: "采样", rows: sampler(post.seedvr2?.sampler) }, { title: "模型", rows: [["模型权重精度", post.seedvr2?.weight_dtype]] }, tiles("SeedVR2 VAE 编码", post.seedvr2?.encode, false), tiles("SeedVR2 VAE 解码", post.seedvr2?.decode, false)] },
    { title: "N 卡放大", enabled: post.nvidia?.enabled, rows: [["倍率", post.nvidia?.scale], ...fields(dlss, [["style", "风格"], ["intensity", "强度"], ["local_tone", "局部光影"], ["local_struct", "局部细节"], ["output_mix", "输出混合"]])], otherGroups: [{ title: "放大与输出", rows: [["质量", post.nvidia?.quality], ["缩放方式", post.nvidia?.resize_type], ...fields(dlss, [["output_view", "输出视图"], ["gpu_gen", "显卡代际"]])] }] },
    { title: "脸部细化", enabled: face.enabled, rows: [["步数", face.steps], ["CFG", face.cfg_scale], ["采样器", face.sampler], ["调度器", face.scheduler], ["降噪", face.denoise_strength], ["提示词处理", faceReencodes ? "节点内重新编码，见下方提示词明细" : "使用传入条件"], ...(faceReencodes ? [["替换正向", face.replace_positive_prompt], ["替换负向", face.replace_negative_prompt], ["排除词", face.exclusions]] : [])], otherGroups: [
      { title: "检测与裁切", rows: fields(face, [["processing_resolution", "处理分辨率"], ["face_selection", "脸部选择"], ["detection_confidence", "检测置信度"], ["manual_rotation", "手动旋转"], ["crop_padding", "裁切边距"]]) },
      { title: "预放大", rows: [["启用预放大", face.enable_pre_upscale], ...(face.enable_pre_upscale ? [["预放大模型", face.upscaler_model]] : [])] },
      { title: "蒙版与融合", rows: fields(face, [["blend_softness", "融合柔和度"], ["mask_expansion", "蒙版扩张"], ["sampling_mask_blur_size", "采样蒙版模糊大小"], ["sampling_mask_blur_strength", "采样蒙版模糊强度"]]) },
      { title: "修复选项", rows: fields(face, [["enable_color_correction", "色彩校正"], ["enable_segmentation", "图像分割"], ["enable_differential_diffusion", "差分扩散"], ["enable_lightness_rescue", "亮度修复"], ["enable_final_refinement", "最终细化"], ["offload_models_to_cpu", "模型卸载到处理器"]]) },
    ] },
  ];
  const repairOrder = ["潜空间放大", "脸部细化", "SeedVR2", "N 卡放大"];
  repairs.sort((a, b) => repairOrder.indexOf(a.title) - repairOrder.indexOf(b.title));
  const profile = bound.model_profile ?? settings.model_profile;
  const basic = [["模型档案", profile === "anima" ? "Anima" : profile === "krea2" ? "Krea 2" : profile], ["主模型", bound.unet ?? settings.model_name],
    ["生成方式", mode === "img2img" ? "图生图" : mode === "txt2img" ? "文生图" : undefined],
    ["底图模式", settings.base_mode === "dual_sampler" ? "双采样" : settings.base_mode === "normal" ? "普通采样" : undefined],
    ["图像种子", record.sample_seed], ["底图尺寸", settings.width != null && settings.height != null ? `${settings.width} × ${settings.height}` : undefined],
    ["最终图片尺寸", "读取中…", "output-size"], ["CLIP", bound.clip ?? settings.clip_name], ["VAE", bound.vae ?? settings.vae_name]];
  if (mode === "img2img") basic.push(["参考图", runtime.input_image_id ?? single.input_image_id], ["尺寸适配", ({ crop: "填满并裁切", pad: "适应并留边", stretch: "拉伸" })[single.resize_mode] ?? single.resize_mode]);
  if (variance?.enabled) basic.push(["Krea 变化控制", "开启"], ["变化种子", record.prompt_seed], ["随机比例", variance.randomize_percent], ["变化强度", variance.strength]);
  const promptSources = [];
  for (const [key, label] of [["prefix", "原始前缀"], ["positive_prompt", "原始画面描述"], ["suffix", "原始后缀"]]) {
    if (Object.hasOwn(single, key)) promptSources.push([label, single[key]]);
  }
  if (runtime.positive !== undefined) promptSources.push(["处理后主体提示词", runtime.positive]);
  if (single.processed_prompt_override != null && single.processed_prompt_override !== runtime.positive) promptSources.push(["手动编辑的处理结果", single.processed_prompt_override]);
  for (const stage of stages) {
    const incoming = stage.title === "脸部细化" && faceReencodes;
    if (stage.positive !== undefined && (incoming || stage.positive !== positive)) promptSources.push([`${stage.title} · ${incoming ? "传入" : ""}正向`, stage.positive]);
    if (stage.negative !== undefined && (incoming || stage.negative !== negative || stage.zeroNegative !== zeroNegative)) promptSources.push([`${stage.title} · ${incoming ? "传入" : ""}负向${stage.zeroNegative ? "（条件置零）" : "（使用文本）"}`, stage.negative]);
  }
  if (face.enabled && faceReencodes) {
    promptSources.push([face.replace_positive_prompt && face.face_positive_prompt ? "脸部节点 · 替换正向文本" : "脸部节点 · 附加正向文本", face.face_positive_prompt ?? ""],
      [face.replace_negative_prompt && face.face_negative_prompt ? "脸部节点 · 替换负向文本" : "脸部节点 · 附加负向文本", face.face_negative_prompt ?? ""],
      ["脸部节点 · 排除词", face.exclusions ?? ""], ["脸部节点处理说明", "节点按排除词、替换开关及各张脸的通配符结果重新编码。此处保留传入文本和处理设置，未记录逐张脸展开后的最终文本。"]);
  }
  const lorasKnown = stages.every(stage => stage.pipeline === "primary" ? Array.isArray(settings.loras) : stage.pipeline === "secondary" && Array.isArray(single.loras_secondary));
  return { basic, stages, loras, lorasKnown, repairs, positive, negative, zeroNegative, promptSources };
}
function singleDetailSummary(data) {
  const basic = new Map(data.basic), generation = basic.get("生成方式"), sampling = basic.get("底图模式");
  const mode = generation == null && sampling == null ? undefined : `${singleDetailValue(generation)} · ${singleDetailValue(sampling)}`;
  return [
    ["主模型", basic.get("主模型"), "model"],
    ["生成／采样", mode, "generation-sampling"],
    ["图像种子", basic.get("图像种子"), "sample-seed"],
    ["底图尺寸", basic.get("底图尺寸"), "base-size"],
    ["最终图片尺寸", basic.get("最终图片尺寸"), "output-size"],
  ];
}
function singleDetailGroups(data) {
  const group = (id, title, status, content) => `<details class="single-detail-section" data-single-detail-group="${id}" name="single-detail-groups"><summary><span>${title}</span><small>${escapeHtml(status)}</small></summary><div class="single-detail-group-content">${content}</div></details>`;
  const textArea = ([label, value]) => `<label>${escapeHtml(label)}<textarea rows="3" readonly>${escapeHtml(singleDetailValue(value))}</textarea></label>`;
  const referenceRow = data.basic.find(([label]) => label === "参考图");
  const models = data.basic.filter(([label]) => !["生成方式", "底图模式", "图像种子", "底图尺寸", "最终图片尺寸", "参考图", "尺寸适配"].includes(label));
  const modelExtras = data.stages[0]?.otherGroups?.filter(item => ["模型与文本编码", "Krea 变化控制"].includes(item.title)) || [];
  const samplers = data.stages.filter(stage => stage.rows.length);
  const repairsEnabled = data.repairs.filter(stage => stage.enabled).length;
  const repairStatus = repairsEnabled ? `${repairsEnabled} 项启用` : data.repairs.every(stage => stage.enabled === false) ? "未启用" : "未记录完整状态";
  const sources = data.promptSources.length ? `<details class="single-detail-prompt-sources"><summary>原始输入与阶段提示词 · ${data.promptSources.length}</summary>${data.promptSources.map(textArea).join("")}</details>` : "";
  return group("models", "模型与编码", singleDetailValue(new Map(data.basic).get("模型档案")), `<dl>${singleDetailRows(models)}</dl>${singleDetailOtherParameters(modelExtras)}`)
    + group("prompts", "提示词", data.zeroNegative === true ? "负向条件置零" : data.zeroNegative === false ? "正向 / 负向" : "负向条件未记录", `<div data-single-prompts></div>${sources}`)
    + group("sampling", "采样阶段", new Map(data.basic).get("底图模式") ? `${samplers.length} 段` : "未记录", samplers.map(stage => `<section class="single-detail-stage"><h3>${escapeHtml(stage.title)}</h3><dl>${singleDetailRows([...stage.rows, ["LoRA 来源", stage.pipelineLabel], ["负向条件", stage.zeroNegative === true ? "置零" : stage.zeroNegative === false ? "使用负面提示词" : undefined]])}</dl>${singleDetailOtherParameters(stage.otherGroups)}</section>`).join(""))
    + group("loras", "实际使用的 LoRA", data.loras.length ? `${data.loras.length} 项` : data.lorasKnown ? "未使用" : "未记录", data.loras.length ? data.loras.map(item => `<div class="single-detail-lora"><strong>${escapeHtml(singleDetailValue(item.filename))}</strong><span>强度 ${escapeHtml(singleDetailValue(item.strength))}</span><small>${escapeHtml(item.stages)}</small></div>`).join("") : `<p>${data.lorasKnown ? "未使用" : "未记录"}</p>`)
    + group("repairs", "放大与修复", repairStatus, data.repairs.map(stage => stage.enabled ? `<section class="single-detail-stage"><h3>${escapeHtml(stage.title)}<small>已启用</small></h3><dl>${singleDetailRows(stage.rows)}</dl>${singleDetailOtherParameters(stage.otherGroups)}</section>` : `<div class="single-detail-off"><span>${escapeHtml(stage.title)}</span><span>${stage.enabled === false ? "未启用" : "未记录"}</span></div>`).join(""))
    + group("reference", "参考图", referenceRow ? referenceRow[1] ? "已保存" : "未记录" : new Map(data.basic).get("生成方式") === "文生图" ? "不适用" : "未记录", referenceRow ? singleDetailReferenceMarkup(referenceRow[1]) + `<dl>${singleDetailRows(data.basic.filter(([label]) => label === "尺寸适配"))}</dl>` : `<p>${new Map(data.basic).get("生成方式") === "文生图" ? "文生图未使用参考图" : "参考图未记录"}</p>`);
}
function bindSingleDetailGroups(container) {
  if (typeof StudioMotion !== "undefined") {
    StudioMotion.bindDisclosures(container);
    return;
  }
  const groups = [...container.querySelectorAll("[data-single-detail-group]")];
  for (const group of groups) group.addEventListener("toggle", () => {
    if (group.open) for (const other of groups) if (other !== group) other.open = false;
  });
}
function singleDetailLayout(single) {
  // Keep the shared prompt nodes alive while rebuilding groups or switching workbenches.
  ui.detailMain.append(ui.detailPositive.closest("label"), ui.detailNegative.closest("label"));
  const target = single ? ui.detailScroll : ui.detailDrawerHome;
  target.append(ui.replayDrawer, ui.variantDrawer);
  ui.detailScroll.scrollTop = 0;
  ui.detailDialog.classList.toggle("is-single-detail", single);
  ui.detailDialog.classList.remove("is-regenerating");
}
function renderSingleDetail(record) {
  const data = singleDetailData(record);
  singleDetailLayout(true);
  ui.detailStats.innerHTML = singleDetailRows(singleDetailSummary(data));
  ui.detailPositive.value = data.positive ?? "未记录";
  ui.detailNegative.value = data.negative ?? "未记录";
  ui.detailSelection.innerHTML = singleDetailGroups(data);
  ui.detailSelection.querySelector("[data-single-prompts]").append(ui.detailPositive.closest("label"), ui.detailNegative.closest("label"));
  bindSingleDetailGroups(ui.detailSelection);
  const reference = ui.detailSelection.querySelector("[data-single-reference]");
  if (reference) {
    const thumbnail = reference.querySelector("img"), status = reference.querySelector("[data-reference-status]");
    const updateReference = () => {
      if (selectedRecord !== record || !reference.isConnected) return;
      const available = thumbnail.naturalWidth > 0 && thumbnail.naturalHeight > 0;
      thumbnail.hidden = !available; status.hidden = available;
      reference.classList.toggle("is-unavailable", !available);
    };
    thumbnail.onload = thumbnail.onerror = updateReference;
    if (thumbnail.complete) updateReference();
  }
  const image = ui.detailImage, expected = `/api/images/${record.id}`;
  const setSize = () => {
    if (selectedRecord !== record || image.getAttribute("src") !== expected) return;
    const target = ui.detailStats.querySelector('[data-detail-value="output-size"] dd');
    if (target) target.textContent = image.naturalWidth && image.naturalHeight ? `${image.naturalWidth} × ${image.naturalHeight}` : "图片不可用";
  };
  image.onload = setSize; image.onerror = setSize;
  if (image.complete) setSize();
  ui.variantButton.textContent = "同提示词换种子";
  singleDetailActions(true);
  ui.detailPositive.closest("label").firstChild.textContent = "底图最终有效提示词";
  ui.detailNegative.closest("label").firstChild.textContent = data.zeroNegative ? "底图负面提示词 · 编码后条件置零" : "底图负面提示词";
}
function resetSingleDetail() {
  singleDetailLayout(false);
  ui.detailImage.onload = ui.detailImage.onerror = null;
  ui.variantButton.textContent = "再抽变体";
  singleDetailActions(false);
  ui.detailPositive.closest("label").firstChild.textContent = "完整提示词";
  ui.detailNegative.closest("label").firstChild.textContent = "负面提示词";
}
function singleDetailActions(single) {
  for (const input of ui.variantModes.querySelectorAll('input[name="regenerationMode"]')) {
    input.closest("label").hidden = single && input.value !== "prompt_variant";
    if (single) input.checked = input.value === "prompt_variant";
  }
  const description = ui.replayDrawer.querySelector("p");
  description.textContent = single
    ? "使用已保存的最终提示词、图像种子和生成设置重跑；图生图沿用原参考图。资源文件发生变化时可能影响结果。"
    : "冻结历史最终提示词，沿用原设置与双种子。资源文件内容被覆盖时无法识别，不承诺像素级一致。";
}
