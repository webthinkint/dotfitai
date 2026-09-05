"""Small-chat smoke test (plan §10) — first live call to the deployed
``gpt-5-mini`` (or equivalent) small-model deployment.

One-off verification tool in the spirit of ``scripts/embedding_smoke.py``:
proves endpoint + key + deployment name end-to-end *and* exercises the
exact contract Stage 2 (§4) will rely on — strict JSON-schema structured
output for an extraction task.

Deliberately passes no ``temperature``: GPT-5-family deployments only accept
the default; determinism comes from the strict schema (plus the Stage 2
source-containment diff pass), not from temperature 0. If this probe ever
needs a temperature knob, the plan §4 note must be revisited, not worked
around here.

Prints endpoint, deployment, returned model id, parsed JSON and token usage
— never any key material. Exits non-zero on any failure.

Usage (from pipeline/):
    uv run scripts/chat_smoke.py
"""

from __future__ import annotations

import argparse
import json
import sys

from openai import AzureOpenAI

from qa_pipeline.azure_config import (
    AzureConfigError, REQUIRE_OPENAI_SMALL_CHAT, load_azure_config,
)

# Same Foundry v2 route rule as the embedding probe: current api-versions
# only (2024-10-21 404s on v2 resources).
DEFAULT_API_VERSION = "2025-04-01-preview"

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
    parser.add_argument("--api-version", default=DEFAULT_API_VERSION)
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

    client = AzureOpenAI(
        azure_endpoint=endpoint, api_key=api_key, api_version=args.api_version
    )
    try:
        resp = client.chat.completions.create(
            model=deployment,
            messages=[{"role": "system", "content": SYSTEM},
                      {"role": "user", "content": USER}],
            response_format={"type": "json_schema",
                             "json_schema": {"name": "qa_extract",
                                             "strict": True,
                                             "schema": SCHEMA}},
        )
    except Exception as e:  # noqa: BLE001 — printing the failure is the tool's job
        print(f"FAIL: {type(e).__name__}: {e}", file=sys.stderr)
        return 1

    print(f"model (returned): {resp.model}")
    usage = resp.usage
    print(f"tokens used     : {usage.total_tokens} "
          f"(prompt={usage.prompt_tokens}, completion={usage.completion_tokens})")

    try:
        parsed = json.loads(resp.choices[0].message.content)
    except (json.JSONDecodeError, IndexError, AttributeError) as e:
        print(f"FAIL: response was not parseable JSON: {e}", file=sys.stderr)
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
