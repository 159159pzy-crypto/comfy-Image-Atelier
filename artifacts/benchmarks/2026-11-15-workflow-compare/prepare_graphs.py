"""Prepare standalone benchmark graphs without submitting GPU jobs."""
from __future__ import annotations

import collections
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
from anima_webui.workflow import WorkflowTemplates, default_settings_for_profile

OUT = Path(__file__).resolve().parent
ATTACHMENT = Path(r"D:\loud\workflow (2).json")
POSITIVE = "masterpiece, best quality, scenery, a small wooden cabin beside a clear mountain lake, pine forest, distant snow capped mountains, morning sunlight, peaceful atmosphere, detailed environment, no humans"
NEGATIVE = "worst quality, low quality, blurry, jpeg artifacts, text, watermark"
SEED = 20261115
WIDTH, HEIGHT = 768, 1024


def write(name, value):
    (OUT / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def node(kind, **inputs):
    return {"class_type": kind, "inputs": inputs}


def main():
    source = json.loads(ATTACHMENT.read_text(encoding="utf-8-sig"))
    root_nodes = {n["id"]: n for n in source["nodes"]}
    resources = root_nodes[167]["widgets_values_named"]
    sampling = root_nodes[691]["widgets_values_named"]
    pipeline = root_nodes[299]["widgets_values_named"]
    assert root_nodes[691]["mode"] == 0 and root_nodes[701]["mode"] == 2
    assert sampling["switch"] is False and sampling["switch_3"] is False
    assert pipeline["switch"] is True and pipeline["value"] is False
    assert not root_nodes[72]["widgets_values"][-1]
    assert not root_nodes[243]["widgets_values"][-1]
    settings = default_settings_for_profile("anima", workbench="single")
    settings.update(width=WIDTH, height=HEIGHT, count=1, negative_prompt=NEGATIVE)
    settings["single"]["positive_prompt"] = POSITIVE
    settings["single"]["vae_decode"] = {"tiled": True, "tile_size": 512, "overlap": 64, "temporal_size": 64, "temporal_overlap": 8}
    a = {
        "167:56": node("UNETLoader", unet_name=settings["model_name"], weight_dtype="default"),
        "167:57": node("CLIPLoader", clip_name=resources["clip_name"], type=resources["type"], device="default"),
        "167:58": node("VAELoader", vae_name=resources["vae_name"]),
        "299:52": node("CLIPTextEncode", clip=["167:57", 0], text=POSITIVE),
        "299:295": node("CLIPTextEncode", clip=["167:57", 0], text=NEGATIVE),
        "691:53": node("EmptyLatentImage", width=WIDTH, height=HEIGHT, batch_size=1),
        "691:1240": node("KSampler", model=["167:56", 0], positive=["299:52", 0], negative=["299:295", 0], latent_image=["691:53", 0], seed=SEED, steps=settings["steps"], cfg=settings["cfg"], sampler_name=settings["sampler_name"], scheduler=settings["scheduler"], denoise=settings["base_denoise"]),
        "691:239": node("VAEDecodeTiled", samples=["691:1240", 0], vae=["167:58", 0], tile_size=512, overlap=64, temporal_size=64, temporal_overlap=8),
        "691:1413": node("VRAM_Debug", image_pass=["691:239", 0], empty_cache=True, gc_collect=True, unload_all_models=False),
        "benchmark:save": node("SaveImage", images=["691:1413", 1], filename_prefix="benchmarks/2026-11-15-workflow-compare/a_attachment_matched"),
    }
    a["benchmark:save"]["_meta"] = {
        "title": "Attachment core benchmark (reconstructed, not the original full workflow)",
        "benchmark_note": "Core enabled image-generation graph reconstructed from workflow (2).json. Missing Silvermoon Turbo replaced with Anima base and both arms matched to 30 steps / CFG 4 / normal sampling / negative zeroing disabled. Prompt, seed, dimensions and output saver standardized. Manual confirmation, notifications and inactive branches omitted. VRAM cleanup retained before SaveImage. This does not test the original Turbo configuration.",
    }
    templates = WorkflowTemplates.load(ROOT / "templates")
    b = templates.submission(
        settings, SEED, SEED,
        "benchmarks/2026-11-15-workflow-compare/b_project_matched",
        "codex-workflow-comparison-20261115", 1,
        frozen_positive_prompt=POSITIVE,
        frozen_negative_prompt=NEGATIVE,
        execution_nonce=2026111501,
    )
    for graph in (a, b["prompt"]):
        types = {n["class_type"] for n in graph.values()}
        assert not types.intersection({"PromptExpand", "ImageCaptionNode", "PatchTritonVAE", "AaaliceImagePicker"})
        texts = {n["inputs"]["text"] for n in graph.values() if n["class_type"] == "CLIPTextEncode"}
        assert texts == {POSITIVE, NEGATIVE}, texts
    write("a_attachment_matched.json", a)
    write("b_project_matched.json", b["prompt"])
    write("b_project_matched_submission.json", b)
    write("parameters.json", {
        "purpose": "Matched-settings comparison between the reconstructed attachment core and the current project single-image builder. Original Silvermoon Turbo model is unavailable, so this does not validate its saved Turbo configuration. Neither arm uses Triton. Cache/warmup conditions must be reported with timings.",
        "source_attachment": str(ATTACHMENT),
        "source_attachment_sha256": hashlib.sha256(ATTACHMENT.read_bytes()).hexdigest(),
        "shared": {"width": WIDTH, "height": HEIGHT, "batch_size": 1, "sample_seed": SEED, "prompt_seed": SEED, "positive": POSITIVE, "negative": NEGATIVE, "model": settings["model_name"], "weight_dtype": "default", "steps": settings["steps"], "cfg": settings["cfg"], "sampler": settings["sampler_name"], "scheduler": settings["scheduler"], "denoise": settings["base_denoise"], "base_mode": "normal", "zero_negative": False, "loras": [], "postprocessing": [], "vae": resources["vae_name"], "clip": resources["clip_name"], "clip_type": resources["type"], "vae_decode": {"tiled": True, "tile_size": 512, "overlap": 64, "temporal_size": 64, "temporal_overlap": 8}},
        "a": {"description": "Reconstruction of the attachment's enabled base-generation core, with matched test settings", "original_saved_parameters": {"model": resources["unet_name"], "steps": sampling["steps"], "cfg": sampling["cfg"], "sampler": sampling["sampler_name"], "scheduler": sampling["scheduler"], "base_mode": "normal", "zero_negative": True}, "deviations": ["Silvermoon Turbo unavailable: use anima-base-v1.0 for both arms", "Match 30 steps / CFG 4 / negative zeroing disabled to current project single-image defaults", "Shared test prompt, seed and dimensions replace saved values", "Resolve Get/Set nodes and switches to their selected active inputs", "Omit manual image confirmation, notifications and inactive modules", "Use ordinary SaveImage after VRAM_Debug instead of ConditionalSaveImage before VRAM_Debug"]},
        "b": {"description": "Current real project builder with a new Anima single-workbench draft", "pipeline_version": settings["pipeline_version"], "builder": "WorkflowTemplates.submission(default_settings_for_profile('anima', workbench='single'))", "execution_nonce": 2026111501, "deviations": ["Shared test prompt, seed and dimensions replace defaults", "One image instead of a batch", "Explicitly freeze the same positive and negative prompt strings"]},
        "node_counts": {"a": len(a), "b": len(b["prompt"])},
        "class_counts": {"a": dict(collections.Counter(n["class_type"] for n in a.values())), "b": dict(collections.Counter(n["class_type"] for n in b["prompt"].values()))},
    })
    print(json.dumps({"directory": str(OUT), "a_nodes": len(a), "b_nodes": len(b["prompt"]), "b_samplers": {k:n for k,n in b["prompt"].items() if n["class_type"] in ("KSampler", "KSamplerAdvanced", "SesquiLatentUpscale")}}, ensure_ascii=False))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
