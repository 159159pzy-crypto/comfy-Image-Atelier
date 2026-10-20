from __future__ import annotations

import io
import json
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch

from PIL import Image

from anima_webui.input_images import InputImageStore
from anima_webui.workflow import WorkflowError


def png() -> bytes:
    stream = io.BytesIO()
    with Image.new("RGBA", (32, 16), (255, 0, 0, 0)) as image:
        image.save(stream, format="PNG")
    return stream.getvalue()


class InputImageStorageTests(unittest.TestCase):
    def test_parallel_upload_and_prepare_do_not_decode_simultaneously(self):
        with tempfile.TemporaryDirectory() as directory:
            store = InputImageStore(directory)
            body = png()
            asset = store.add(body)
            first_entered = threading.Event()
            second_started = threading.Event()
            concurrent_decode = threading.Event()
            release = threading.Event()
            real_open = Image.open
            calls_lock = threading.Lock()
            calls = 0

            def blocked_open(*args, **kwargs):
                nonlocal calls
                with calls_lock:
                    calls += 1
                    first = calls == 1
                if first:
                    first_entered.set()
                    if not release.wait(5):
                        raise TimeoutError("test did not release decoder")
                elif not release.is_set():
                    concurrent_decode.set()
                return real_open(*args, **kwargs)

            def prepare():
                second_started.set()
                return store.prepare(asset["id"], 16, 16, "pad")

            with patch("anima_webui.input_images.Image.open", side_effect=blocked_open):
                with ThreadPoolExecutor(max_workers=2) as executor:
                    upload = executor.submit(store.add, body)
                    try:
                        self.assertTrue(first_entered.wait(2))
                        prepared = executor.submit(prepare)
                        self.assertTrue(second_started.wait(2))
                        self.assertFalse(concurrent_decode.wait(0.1))
                    finally:
                        release.set()
                    self.assertEqual(upload.result(timeout=2)["id"], asset["id"])
                    with real_open(io.BytesIO(prepared.result(timeout=2))) as result:
                        self.assertEqual(result.size, (16, 16))
                        self.assertEqual(result.getpixel((0, 0)), (255, 255, 255))

    def test_corrupt_metadata_has_an_actionable_error_and_keeps_original_files(self):
        with tempfile.TemporaryDirectory() as directory:
            store = InputImageStore(directory)
            asset = store.add(png())
            metadata_path = Path(directory) / f'{asset["id"]}.json'
            image_path = Path(directory) / f'{asset["id"]}.png'
            image_bytes = image_path.read_bytes()
            for invalid in (b"{partial", b"[]", b'{"id":"wrong"}', b"\xff\xfe", b" " * 65537):
                with self.subTest(invalid=invalid[:32]):
                    metadata_path.write_bytes(invalid)
                    with self.assertRaisesRegex(WorkflowError, "重新上传"):
                        store.get(asset["id"])
                    self.assertEqual(metadata_path.read_bytes(), invalid)
                    self.assertEqual(image_path.read_bytes(), image_bytes)

    def test_corrupt_saved_pixels_are_reported_without_decoding_large_targets(self):
        with tempfile.TemporaryDirectory() as directory:
            store = InputImageStore(directory)
            asset = store.add(png())
            metadata_path = Path(directory) / f'{asset["id"]}.json'
            image_path = Path(directory) / f'{asset["id"]}.png'
            image_path.write_bytes(b"broken PNG")
            # Even if the stored size is consistent, prepare must translate
            # decoder failures into a useful domain error.
            asset["size"] = image_path.stat().st_size
            metadata_path.write_text(json.dumps(asset), encoding="utf-8")
            with self.assertRaisesRegex(WorkflowError, "重新上传"):
                store.prepare(asset["id"], 16, 16, "crop")
            for width, height in ((0, 1), (1, -1), (True, 16), (16, 1.5), (100000, 100000)):
                with self.subTest(size=(width, height)):
                    with patch("anima_webui.input_images.Image.open") as decoder:
                        with self.assertRaises(WorkflowError):
                            store.prepare(asset["id"], width, height, "pad")
                        decoder.assert_not_called()
