import unittest

from anima_webui.prompt_strategies import purpose_templates


class PurposeTemplatesTests(unittest.TestCase):
    def test_stable_ids_and_profile_specific_rendering(self):
        purposes = ("cinematic", "fashion-poster", "editorial-illustration", "environment")
        anima = purpose_templates("anima")
        krea = purpose_templates("krea2")

        for profile, templates in (("anima", anima), ("krea2", krea)):
            self.assertEqual([item["id"] for item in templates],
                             [f"builtin:rewrite:{profile}:{purpose}" for purpose in purposes])
            self.assertEqual(len({item["name"] for item in templates}), len(purposes))
            self.assertEqual(len({item["content"] for item in templates}), len(purposes))

        for anima_item, krea_item in zip(anima, krea):
            self.assertEqual(anima_item["name"], krea_item["name"])
            self.assertIn("Booru-style tags", anima_item["content"])
            wording = "cohesive English image caption" if krea_item["id"].endswith(":fashion-poster") else "natural English prose"
            self.assertIn(wording, krea_item["content"])
            self.assertNotIn("Booru-style tags", krea_item["content"])

    def test_results_can_be_updated_without_changing_defaults(self):
        templates = purpose_templates("Anima")
        templates[0]["name"] = "修改"
        self.assertEqual(purpose_templates("anima")[0]["name"], "电影分镜")
