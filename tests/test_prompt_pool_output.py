import json

import pytest

from anima_webui.prompt_assistant import parse_pool_output, pool_output_template
from anima_webui.workflow import WorkflowError


def test_pool_output_roundtrips_multiline_prose_and_optional_json_fence():
    item = {"title": "雨中的街道", "subtitle": "暖色霓虹与潮湿路面", "prompt": "A rainy street, with warm neon.\nReflections cover the road."}
    encoded = json.dumps(item, ensure_ascii=False)
    assert parse_pool_output(encoded) == item
    assert parse_pool_output("```json\n" + encoded + "\n```") == item


@pytest.mark.parametrize("item", [
    [], {"title": "名称", "prompt": "body"},
    {"title": "名称", "subtitle": "说明", "prompt": "body", "section": "character"},
    {"title": "名称", "subtitle": "", "prompt": "body"},
    {"title": "名称", "subtitle": "说明", "prompt": 1},
    {"title": "名" * 201, "subtitle": "说明", "prompt": "body"},
    {"title": "名称", "subtitle": "说" * 301, "prompt": "body"},
    {"title": "名称", "subtitle": "说明", "prompt": "x" * 20001},
])
def test_pool_output_rejects_non_importable_shapes(item):
    with pytest.raises(WorkflowError, match="随机池格式无效"):
        parse_pool_output(json.dumps(item))


def test_model_contract_preserves_user_content_and_defers_pool_choice():
    for profile, expected in (("anima", "comma-separated English"), ("krea2", "natural English")):
        template = pool_output_template("Use a watercolor style.", profile)
        assert template.startswith("Use a watercolor style.\n\n")
        assert expected in template
        assert "Chinese supplementary description" in template
        assert "user chooses the destination pool" in template
