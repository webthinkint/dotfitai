"""Embedding smoke test (plan §10) — first live call to the deployed
`text-embedding-3-large` deployment.

One-off verification tool in the spirit of ``scripts/pdsrg_gate.py``: proves
endpoint + key + deployment name end-to-end before the index builder (§9)
relies on them.

Deliberately does NOT use ``load_azure_config()`` — that loader enforces the
full runtime contract, and the two chat deployments are still pending a quota
increase (open item 1). This tool reads only the OpenAI section it needs,
reusing the module's validators (imported private on purpose: single source
of validation logic).

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
    AzureConfigError, ENV_FILENAME, _endpoint, _required, read_env_file,
    repo_root,
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

    path = repo_root() / ENV_FILENAME
    if not path.is_file():
        print(f"FAIL: {ENV_FILENAME} not found at {path}", file=sys.stderr)
        return 1
    values = read_env_file(path)

    try:
        endpoint = _endpoint(values, "AZURE_OPENAI_ENDPOINT")
        api_key = _required(values, "AZURE_OPENAI_API_KEY")
        deployment = _required(values, "AZURE_OPENAI_EMBEDDING_DEPLOYMENT")
    except AzureConfigError as e:
        print(f"FAIL: {e}", file=sys.stderr)
        return 1

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
