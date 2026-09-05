"""Embedding smoke test (plan §10) — first live call to the deployed
`text-embedding-3-large` deployment.

One-off verification tool in the spirit of ``scripts/pdsrg_gate.py``: proves
endpoint + key + deployment name end-to-end before the index builder (§9)
relies on them.

Deliberately uses ``load_azure_config(require=REQUIRE_OPENAI_EMBEDDING)`` —
the full contract isn't needed and the two chat deployments are still
pending a quota increase (open item 1).

Prints endpoint, deployment, returned model id, dims and token usage — never
any key material. Exits non-zero on any failure.

Usage (from pipeline/):
    uv run scripts/embedding_smoke.py
"""

from __future__ import annotations

import argparse
import sys

from openai import AzureOpenAI

from qa_pipeline.azure_config import (
    AzureConfigError, REQUIRE_OPENAI_EMBEDDING, load_azure_config,
)

# Verified against the Foundry v2 resource (services.ai.azure.com): the classic
# /openai/deployments route works, but only on current api-versions — the
# older 2024-10-21 returns 404 "Resource not found" on v2 resources.
DEFAULT_API_VERSION = "2025-04-01-preview"
EXPECTED_DIMS = 3072  # text-embedding-3-large — the §9 index contract


def main() -> int:
    parser = argparse.ArgumentParser(description="Live embedding deployment smoke test")
    parser.add_argument("--api-version", default=DEFAULT_API_VERSION)
    parser.add_argument("--input", default="dotFIT embedding smoke test")
    args = parser.parse_args()

    try:
        cfg = load_azure_config(require=REQUIRE_OPENAI_EMBEDDING)
    except AzureConfigError as e:
        print(f"FAIL: {e}", file=sys.stderr)
        return 1
    endpoint = cfg.openai_endpoint
    api_key = cfg.openai_api_key
    deployment = cfg.embedding_deployment

    print(f"endpoint        : {endpoint}")
    print(f"deployment      : {deployment}")
    print(f"api-version     : {args.api_version}")

    client = AzureOpenAI(
        azure_endpoint=endpoint, api_key=api_key, api_version=args.api_version
    )
    try:
        resp = client.embeddings.create(model=deployment, input=args.input)
    except Exception as e:  # noqa: BLE001 — printing the failure is the tool's job
        print(f"FAIL: {type(e).__name__}: {e}", file=sys.stderr)
        return 1

    dims = len(resp.data[0].embedding)
    print(f"model (returned): {resp.model}")
    print(f"vector dims     : {dims}")
    print(f"tokens used     : {resp.usage.total_tokens}")

    if dims != EXPECTED_DIMS:
        print(
            f"FAIL: expected {EXPECTED_DIMS} dims (text-embedding-3-large), "
            f"got {dims} — check which model the deployment points at",
            file=sys.stderr,
        )
        return 1
    print("PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
