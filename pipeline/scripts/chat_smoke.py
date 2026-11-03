"""Small-chat smoke test: a live call to the small deployment.

Proves endpoint + key + deployment name end-to-end *and* exercises the exact
contract Stage 2 relies on: strict JSON-schema structured output for an
extraction task, through the same client the stages use, so the deployment's
name picks the wire the same way.

Deliberately passes no ``temperature``: determinism comes from the strict schema
(plus the Stage 2 source-containment diff pass), not from temperature 0.

Prints endpoint, deployment and parsed JSON, never any key material. Exits
non-zero on any failure.

Usage (from pipeline/):
    uv run scripts/chat_smoke.py
"""

from __future__ import annotations

import argparse
import json
import sys

from qa_pipeline.azure_config import (
    AzureConfigError, REQUIRE_OPENAI_SMALL_CHAT, load_azure_config,
)
from qa_pipeline.stage2 import CHAT_API_VERSION, Extractor

SCHEMA = {
    "type": "object",
    "properties": {
        "question_canonical": {"type": "string"},
        "products": {"type": "array", "items": {"type": "string"}},
        "confidence": {"type": "number"},
    },
    "required": ["question_canonical", "products", "confidence"],
    "additionalProperties": False,
}

SYSTEM = ("You extract structure from customer nutrition questions. "
          "Return JSON only.")
USER = ("Customer asks: Can I take creatine monohydrate with caffeine "
        "before my workout? Products mentioned as plain strings.")


def main() -> int:
    parser = argparse.ArgumentParser(description="Live small-chat deployment smoke test")
    parser.add_argument("--api-version", default=CHAT_API_VERSION)
    args = parser.parse_args()

    try:
        cfg = load_azure_config(require=REQUIRE_OPENAI_SMALL_CHAT)
    except AzureConfigError as e:
        print(f"FAIL: {e}", file=sys.stderr)
        return 1
    endpoint = cfg.openai_endpoint
    api_key = cfg.openai_api_key
    deployment = cfg.small_chat_deployment

    print(f"endpoint        : {endpoint}")
    print(f"deployment      : {deployment}")
    print(f"api-version     : {args.api_version}")

    extractor = Extractor(endpoint, api_key, deployment, args.api_version,
                          schema=SCHEMA, schema_name="qa_extract")
    try:
        parsed = extractor([{"role": "system", "content": SYSTEM},
                            {"role": "user", "content": USER}])
    except Exception as e:  # noqa: BLE001 — printing the failure is the tool's job
        print(f"FAIL: {type(e).__name__}: {e}", file=sys.stderr)
        return 1

    missing = [k for k in SCHEMA["required"] if k not in parsed]
    if missing:
        print(f"FAIL: schema keys missing from response: {missing}", file=sys.stderr)
        return 1
    print(f"parsed          : {json.dumps(parsed, ensure_ascii=False)}")
    print("PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
