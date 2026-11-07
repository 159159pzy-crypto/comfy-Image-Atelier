"""History regeneration contracts across migration and dispatch boundaries."""
import copy
import asyncio
import unittest

import test_single_backend as fixtures
from anima_webui.server import HISTORY_KEY
from anima_webui.workflow import WorkflowTemplates, migrate_settings, validate_settings


class RegenerationAuditTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.fixture = fixtures.SingleBackendTests()
        await self.fixture.asyncSetUp()
        self.client = self.fixture.client
        self.manager = self.fixture.manager
        self.history = self.fixture.app[HISTORY_KEY]

    async def asyncTearDown(self):
        if self.fixture.comfy.block:
            self.fixture.comfy.block.set()
        await self.fixture.asyncTearDown()

    async def original(self):
        response = await self.client.post('/api/batches', json=self.fixture.settings())
        self.assertEqual(response.status, 201, await response.text())
        await self.manager.wait()
        return (await self.history.list_images())['items'][0]

    async def test_single_history_variant_honors_count_and_saves_each_result(self):
        original = await self.original()
        before = len(self.fixture.comfy.submissions)
        response = await self.client.post(f"/api/history/{original['id']}/regenerate",
                                          json={'mode': 'prompt_variant', 'count': 4})
        self.assertEqual(response.status, 201, await response.text())
        batch = await response.json()
        self.assertEqual(batch['total'], 4)
        await self.manager.wait()
        self.assertEqual(len(self.fixture.comfy.submissions) - before, 4)
        saved = await self.history.get_batch(batch['id'])
        self.assertEqual((saved['total'], saved['completed']), (4, 4))
        results = [item for item in (await self.history.list_images())['items'] if item['batch_id'] == batch['id']]
        self.assertEqual(len(results), 4)
        self.assertTrue(all(item['settings']['count'] == 4 for item in results))
        for payload in self.fixture.comfy.submissions[before:]:
            self.assertEqual(payload['extra_data']['extra_pnginfo']['anima_random_webui']['settings']['count'], 4)

    async def test_direct_single_still_generates_one(self):
        settings = self.fixture.settings()
        settings['count'] = 4
        response = await self.client.post('/api/batches', json=settings)
        self.assertEqual((await response.json())['total'], 1)
        await self.manager.wait()

    async def test_queued_single_variant_preserves_count_and_rejects_over_limit(self):
        original = await self.original()
        self.fixture.comfy.block = asyncio.Event()
        await self.manager.start(self.fixture.settings())
        response = await self.client.post(f"/api/history/{original['id']}/regenerate",
                                          json={'mode': 'prompt_variant', 'count': 4})
        queued = await response.json()
        self.assertEqual((queued['status'], queued['count']), ('queued', 4))
        response = await self.client.post(f"/api/history/{original['id']}/regenerate",
                                          json={'mode': 'prompt_variant', 'count': 101})
        self.assertEqual(response.status, 400)
        self.fixture.comfy.block.set()
        await self.manager.wait()
        self.assertEqual(self.manager.snapshot()['completed'], 4)
        original = (await self.history.list_images())['items'][0]
        response = await self.client.post(f"/api/history/{original['id']}/regenerate", json={'mode': 'replay'})
        self.assertEqual((await response.json())['total'], 1)
        await self.manager.wait()

    async def test_deleted_krea_selection_only_blocks_current_sampling(self):
        original_inventory = self.fixture.comfy.resource_inventory

        async def inventory():
            result = await original_inventory()
            result['models'].append('krea2_turbo_int8_convrot.safetensors')
            return result

        self.fixture.comfy.resource_inventory = inventory
        settings = validate_settings({'model_profile': 'krea2', 'count': 1,
                                      'krea_prompt_pool': {'enabled': True, 'ids': ['prompt_deleted']}})
        await self.history.create_batch('old', 1, settings)
        image = await self.history.add_image(batch_id='old', sequence=1, prompt_id='old',
            image={'filename': 'old.png', 'type': 'output'}, positive_prompt='A saved complete scene.',
            negative_prompt='', sample_seed=1, prompt_seed=2, settings=settings)
        options = await (await self.client.get(f"/api/history/{image['id']}/regeneration-options")).json()
        self.assertFalse(options['modes']['settings_reroll']['available'])
        for mode in ('replay', 'prompt_variant'):
            self.assertTrue(options['modes'][mode]['available'])
            response = await self.client.post(f"/api/history/{image['id']}/regenerate",
                                              json={'mode': mode, **({'count': 1} if mode != 'replay' else {})})
            self.assertEqual(response.status, 201, await response.text())
            await self.manager.wait()
        response = await self.client.post(f"/api/history/{image['id']}/regenerate", json={'mode': 'settings_reroll'})
        self.assertEqual(response.status, 400)
        # A nonempty library is not enough if every explicitly selected id
        # was removed. Frozen execution remains independent of that selection.
        await self.manager.prompt_pools.import_items('krea2', ['An unrelated scene.'])
        options = await (await self.client.get(f"/api/history/{image['id']}/regeneration-options")).json()
        self.assertFalse(options['modes']['settings_reroll']['available'])
        self.assertTrue(options['modes']['replay']['available'])

        async def missing_model():
            result = await original_inventory()
            result['models'] = []
            return result

        self.fixture.comfy.resource_inventory = missing_model
        options = await (await self.client.get(f"/api/history/{image['id']}/regeneration-options")).json()
        self.assertFalse(options['modes']['replay']['available'])
        response = await self.client.post(f"/api/history/{image['id']}/regenerate", json={'mode': 'replay'})
        self.assertEqual(response.status, 400)


def test_explicit_single_count_survives_repeated_migration_and_rendering():
    settings = {'workbench': 'single', 'count': 4}
    for _ in range(3):
        settings = validate_settings(migrate_settings(settings))
    assert settings['count'] == 4
    payload = WorkflowTemplates.load(fixtures.APP_DIR / 'templates').submission(
        copy.deepcopy(settings), 1, 2, 'test', 'client', 1)
    assert payload['extra_data']['extra_pnginfo']['anima_random_webui']['settings']['count'] == 4
    assert validate_settings({'workbench': 'single'})['count'] == 1
    assert validate_settings({'count': 1000})['count'] == 1000
