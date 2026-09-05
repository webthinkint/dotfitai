"""AI Search connectivity check (plan §9) — read-only, creates/uploads nothing.

Loads the Search section of ``.env`` (``require=REQUIRE_SEARCH``) and calls
``get_service_statistics()`` + a ``get_index`` probe: proves the endpoint
shape, the admin key, the network path, and SDK↔tier compatibility *before*
the index build uploads anything. Single-resource calls only — the serverless
tier rejects index enumeration, so nothing here lists. Exit 0 on success.
Never echoes key material.

Usage (from pipeline/):
    uv run scripts/search_ping.py
"""

from __future__ import annotations

import sys

from qa_pipeline.azure_config import (
    AzureConfigError, REQUIRE_SEARCH, load_azure_config,
)


def main() -> int:
    try:
        cfg = load_azure_config(require=REQUIRE_SEARCH)
    except AzureConfigError as e:
        print(f"FAIL: {e}", file=sys.stderr)
        return 1

    try:
        from azure.core.credentials import AzureKeyCredential
        from azure.core.exceptions import ResourceNotFoundError
        from azure.search.documents.indexes import SearchIndexClient

        client = SearchIndexClient(
            cfg.search_endpoint, AzureKeyCredential(cfg.search_admin_key)
        )
        stats = client.get_service_statistics()
        try:
            client.get_index("kb-main")
            kb = "kb-main: present"
        except ResourceNotFoundError:
            kb = "kb-main: absent (will be created on upload)"
    except Exception as e:  # noqa: BLE001 — printing the failure is the tool's job
        print(f"FAIL: {type(e).__name__}: {e}", file=sys.stderr)
        if _is_auth_failure(e):
            print("hint: check AZURE_SEARCH_ADMIN_KEY (Keys blade) in .env",
                  file=sys.stderr)
        elif _is_dns_or_network(e):
            print("hint: check AZURE_SEARCH_ENDPOINT (Overview blade) — it must "
                  "be https://<name>.search.windows.net", file=sys.stderr)
        return 1

    counters = stats.get("counters", {}) if hasattr(stats, "get") else {}

    def _u(name: str):
        return (counters.get(name) or {}).get("usage", "?")

    def _q(name: str):
        return (counters.get(name) or {}).get("quota", "usage-based")

    print(f"endpoint : {cfg.search_endpoint}")
    print(f"index    : {kb}")
    print(f"docs     : {_u('document_counter')} (usage-based billing)")
    print(f"indexes  : {_u('index_counter')}/{_q('index_counter')}")
    print("PASS")
    return 0


def _is_auth_failure(e: Exception) -> bool:
    return getattr(e, "status_code", None) in (401, 403) or "401" in str(e)


def _is_dns_or_network(e: Exception) -> bool:
    name = type(e).__name__
    return name in {"ServiceRequestError", "ConnectError"} or "NameResolution" in str(e)


if __name__ == "__main__":
    raise SystemExit(main())
