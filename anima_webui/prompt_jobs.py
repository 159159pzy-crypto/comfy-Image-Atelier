"""Durable text-processing jobs, separate from generated artwork history."""
from __future__ import annotations

import json
import uuid
from typing import Any

from .history import _now


class PromptJobStore:
    def __init__(self, history: Any):
        self.history = history

    async def create(self, settings: dict[str, Any], fingerprint: str) -> dict[str, Any]:
        return await self.history._call(self._create, settings, fingerprint)

    def _create(self, settings: dict[str, Any], fingerprint: str) -> dict[str, Any]:
        self.history.ensure_writable()
        job_id = uuid.uuid4().hex
        now = _now()
        self.history.connection.execute(
            "INSERT INTO prompt_jobs(id,status,input_fingerprint,settings_json,created_at,updated_at) VALUES(?,?,?,?,?,?)",
            (job_id, "queued", fingerprint, json.dumps(settings, ensure_ascii=False), now, now),
        )
        self.history.connection.commit()
        return self._get(job_id)

    async def get(self, job_id: str) -> dict[str, Any]:
        return await self.history._call(self._get, job_id)

    def _get(self, job_id: str) -> dict[str, Any]:
        row = self.history.connection.execute("SELECT * FROM prompt_jobs WHERE id=?", (job_id,)).fetchone()
        if row is None:
            raise KeyError(job_id)
        result = dict(row)
        result["settings"] = json.loads(result.pop("settings_json"))
        result["result"] = json.loads(result.pop("result_json"))
        result["kind"] = result["settings"].get("kind", "legacy_preview")
        return result

    async def update(self, job_id: str, *, status: str | None = None, prompt_id: str | None = None,
                     result: dict[str, Any] | None = None, error: str | None = None) -> dict[str, Any]:
        return await self.history._call(self._update, job_id, status, prompt_id, result, error)

    def _update(self, job_id: str, status: str | None, prompt_id: str | None,
                result: dict[str, Any] | None, error: str | None) -> dict[str, Any]:
        self.history.ensure_writable()
        current = self._get(job_id)
        self.history.connection.execute(
            "UPDATE prompt_jobs SET status=?,prompt_id=?,result_json=?,error=?,updated_at=? WHERE id=?",
            (status if status is not None else current["status"],
             prompt_id if prompt_id is not None else current["prompt_id"],
             json.dumps(result if result is not None else current["result"], ensure_ascii=False),
             error if error is not None else current["error"], _now(), job_id),
        )
        self.history.connection.commit()
        return self._get(job_id)
