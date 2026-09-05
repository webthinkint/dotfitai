"""Cached embedding client for the plan §9 ``content_vector`` field.

Vectors are API results — not byte-deterministic across reruns — so they are
cached locally (gitignored ``runs/embeddings.jsonl``) keyed by
``sha256(deployment|api_version|text)``: reruns re-embed only what changed.
The cache lives outside the committed outputs on purpose; regenerating it
costs API calls, so it is *not* pruned with them.

The embedding input is ``title + "\\n\\n" + content`` (decided 2026-09-05:
product sections don't always name their product in body text, so the title
rides along for context; PDSRG chunk content already carries its heading
path, which merely duplicates harmlessly).

The Azure OpenAI resource is Foundry v2 shape: the classic SDK route works
but only on current api-versions (see scripts/embedding_smoke.py notes).
"""

from __future__ import annotations

import hashlib
import json
import time
from pathlib import Path
from typing import Any, Protocol

EMBEDDING_API_VERSION = "2025-04-01-preview"  # verified live (2026-09-05)
_RETRYABLE = {"RateLimitError", "APIConnectionError", "APITimeoutError",
              "InternalServerError"}


class _EmbeddingsAPI(Protocol):
    def create(self, *, model: str, input: list[str]) -> Any: ...


class Embedder:
    """Batch embedding with a local JSONL cache. Never echoes key material."""

    MAX_INPUT_CHARS = 28_000   # ~7K tokens < the 8191-token input limit, with headroom

    def __init__(self, endpoint: str, api_key: str, deployment: str,
                 api_version: str, cache_path: Path | None = None,
                 batch_size: int = 16, max_retries: int = 6,
                 client: _EmbeddingsAPI | None = None):
        self.endpoint = endpoint
        self.api_key = api_key
        self.deployment = deployment
        self.api_version = api_version
        self.cache_path = cache_path
        self.batch_size = batch_size
        self.max_retries = max_retries
        self._client = client          # injectable for tests
        self.n_cache_hits = 0
        self.n_api_calls = 0
        self._cache: dict[str, list[float]] = {}
        self._dirty: list[str] = []
        if cache_path is not None and cache_path.is_file():
            with cache_path.open("r", encoding="utf-8") as f:
                for line in f:
                    if line.strip():
                        rec = json.loads(line)
                        self._cache[rec["k"]] = rec["v"]

    @staticmethod
    def cache_key(deployment: str, api_version: str, text: str) -> str:
        raw = f"{deployment}|{api_version}|{text}".encode("utf-8")
        return hashlib.sha256(raw).hexdigest()

    def _key(self, text: str) -> str:
        return self.cache_key(self.deployment, self.api_version, text)

    def embed(self, texts: list[str]) -> list[list[float]]:
        """Vectors aligned 1:1 with *texts*. Fails on oversize input *before*
        any API call is spent (loud, never truncated silently)."""
        for t in texts:
            if len(t) > self.MAX_INPUT_CHARS:
                raise ValueError(
                    f"embedding input of {len(t)} chars exceeds the "
                    f"{self.MAX_INPUT_CHARS}-char cap (~8191-token model "
                    f"limit) — split the source document instead"
                )
        out: list[list[float] | None] = [None] * len(texts)
        missing: list[int] = []
        for i, t in enumerate(texts):
            vec = self._cache.get(self._key(t))
            if vec is not None:
                out[i] = vec
                self.n_cache_hits += 1
            else:
                missing.append(i)
        for start in range(0, len(missing), self.batch_size):
            idxs = missing[start:start + self.batch_size]
            batch = [texts[i] for i in idxs]
            for i, vec in zip(idxs, self._embed_batch(batch)):
                out[i] = vec
                k = self._key(texts[i])
                self._cache[k] = vec
                self._dirty.append(k)
        return out  # type: ignore[return-value]

    def _embed_batch(self, texts: list[str]) -> list[list[float]]:
        client = self._client if self._client is not None else self._make_client()
        delay = 1.0
        for attempt in range(self.max_retries + 1):
            try:
                resp = client.create(model=self.deployment, input=list(texts))
                self.n_api_calls += 1
                return [d.embedding for d in sorted(resp.data, key=lambda d: d.index)]
            except Exception as e:  # noqa: BLE001 — classified below, re-raised if not retryable
                if type(e).__name__ not in _RETRYABLE or attempt == self.max_retries:
                    raise
                time.sleep(delay)
                delay = min(delay * 2, 30.0)
        raise AssertionError("unreachable")

    def _make_client(self) -> _EmbeddingsAPI:
        from openai import AzureOpenAI
        return AzureOpenAI(  # type: ignore[return-value]
            azure_endpoint=self.endpoint, api_key=self.api_key,
            api_version=self.api_version,
        ).embeddings

    def save_cache(self) -> None:
        """Append vectors embedded since construction/load to the cache file."""
        if self.cache_path is None or not self._dirty:
            return
        self.cache_path.parent.mkdir(parents=True, exist_ok=True)
        with self.cache_path.open("a", encoding="utf-8", newline="\n") as f:
            for k in self._dirty:
                f.write(json.dumps({"k": k, "v": self._cache[k]}) + "\n")
        self._dirty.clear()
