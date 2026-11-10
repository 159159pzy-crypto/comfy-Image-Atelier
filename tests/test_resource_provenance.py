from anima_webui.resource_resolution import merge_resource_replacements, resources_changed, resolve_resources
from anima_webui.workflow import validate_settings
from test_resource_resolution import catalog_inventory


def test_rechecking_merges_original_selection_into_one_final_record():
    first = {"module": "base", "field": "model_name", "from": "A", "to": "B", "reason": "missing_model"}
    second = {**first, "from": "B", "to": "C"}
    result = merge_resource_replacements([first], [second])
    assert result == [{**first, "to": "C"}]
    assert resources_changed(result)
    assert merge_resource_replacements(result, [{**first, "from": "C", "to": "A"}]) == []


def test_unique_basename_or_case_normalization_keeps_physical_resource_identity():
    settings = validate_settings({"base_mode": "normal"})
    inventory = catalog_inventory(settings)
    canonical = "Models/" + settings["model_name"]
    inventory["models"] = [canonical]
    inventory["resource_catalog"][0]["name"] = canonical
    for current in (settings["model_name"], canonical.upper().replace("/", "\\")):
        settings["model_name"] = current
        result = resolve_resources(settings, inventory)
        assert result["ready"]
        assert result["effective_settings"]["model_name"] == canonical
        assert result["replacements"][0]["reason"] == "path_normalized"
        assert not resources_changed(result["replacements"])


def test_normalizing_a_previous_replacement_does_not_erase_its_reason():
    first = {"module": "base", "field": "model_name", "from": "A", "to": "B", "reason": "missing_model"}
    normalized = {**first, "from": "B", "to": "Models/B", "reason": "path_normalized"}
    result = merge_resource_replacements([first], [normalized])
    assert result == [{**first, "to": "Models/B"}]
    assert resources_changed(result)
