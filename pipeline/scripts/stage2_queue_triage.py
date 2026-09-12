"""Triage the Stage 2 review queue into bulk-dispositionable groups (item 13).

`processed/qa/stage2/review_queue.jsonl` is rows of ``source_file`` +
``reasons`` and nothing else, so working it means opening every record. The
dispositions are the owner's; what this does is make the pass a sign-off
instead of a 219-item read, the same way the alias worksheet did for §5.

Reading it right depends on one thing (`docs/v1/decisions.md`, Stage 2 triage
shape): **this queue does not gate.** A flagged record is already redacted and
already indexed — the queue is an audit trail to work, not a hold. That is why
the report leads with how many flagged records are live in the index: that
number is the actual exposure, and it is not zero.

**Grouping is by evidence-span survival, not by placeholder census** (triage
round 2, 2026-09-08). The first cut of this script asked "does the record
contain a ``[NAME]``/``[EMAIL]`` anywhere?" and called that "the redaction
landed". It does not follow: a record can carry ``[EMAIL]`` in its header
block and a full customer name in the body, and five such records sat in the
bulk-sign-off pile while one sat in the read pile. What is actually being
asked is whether *this* flag's span survived, so that is what is now tested —
the LLM's own ``residual_pii_evidence`` string, looked up in the committed
record and in the committed Stage 0 text.

Groups, in the order an owner should work them:

1. **``residual_pii_unresolved``** — the flagged span is still present in the
   committed Stage 2/4 record. The sharp end; these need reading, and after
   the round-2 scrub rules the residue is prose mentions (a third party named
   inside a sentence), which no deterministic rule can reach.
2. **``residual_pii_text_residue``** — the record is clean but the span is
   still in the committed ``stage0/text``. Not served (``index_build`` ships
   ``question_canonical``), so this is a committed-tree question, not an index
   one — but the tree is in git.
3. **``residual_pii_resolved``** — span gone from both. Bulk-confirmable.
4. **``residual_pii_unverifiable``** — flagged with no evidence span (a Stage
   1-origin flag, which carries no span by construction), or the Stage 2 cache
   is not on this machine. Falls back to the placeholder census.
5. **``containment_fail``** — the answer diverged from its source beyond the
   containment threshold; a transcription-not-generation check, not a PII one.
6. **``low_confidence``** — the model's own self-assessment; read alongside
   ``confidence``.
7. **``audit_sample``** — the deterministic 5% sample. Nothing is wrong with
   these by construction; they are the spot-check.

Deliberately absent, still: the **evidence spans themselves**. They live only
in the gitignored Stage 2 cache and never in a committed artifact
(`docs/v1/decisions.md`). This report reads them to decide a group and then
reports the group — never the string. It writes nothing and prints no answer
text for the same reason.

The cache is joined to the queue on the answer text (the record copies the
LLM's ``answer`` verbatim), which needs no key recomputation and so no Azure
config. Two records with a byte-identical answer would collide and the first
span wins; the corpus has exactly one such pair and neither side is PII-
flagged, so the join is 1:1 where it matters. Without the cache the report
still runs, degraded, and says so.

    uv run python scripts/stage2_queue_triage.py
    uv run python scripts/stage2_queue_triage.py --json
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path

from qa_pipeline.io_utils import configure_stdio, read_jsonl

DEFAULT_STAGE2 = Path("../processed/qa/stage2")
DEFAULT_STAGE4 = Path("../processed/qa/stage4/documents.jsonl")
DEFAULT_STAGE0_TEXT = Path("../processed/qa/stage0/text")
DEFAULT_CACHE = Path("../processed/qa/stage2/runs/stage2_cache.jsonl")

# The Stage 0 placeholder vocabulary that stands for a redacted person
# (AGENTS.md: these tokens are parsed by Stage 1 and by human reviewers). Only
# used for the degraded path now — see the module docstring on why a
# placeholder census does not answer "did this flag get resolved".
PERSON_PLACEHOLDERS = ("[NAME]", "[CUSTOMER]", "[EMAIL]", "[PHONE]",
                       "[SIGNATURE]", "[PROFILE-URL]")

RECORD_FIELDS = ("question_canonical", "question_original", "answer")

# Worked in this order: the first group is the one that cannot be sampled.
GROUP_ORDER = ("residual_pii_unresolved", "residual_pii_text_residue",
               "residual_pii_resolved", "residual_pii_unverifiable",
               "containment_fail", "low_confidence", "audit_sample")

GROUP_NOTES = {
    "residual_pii_unresolved":
        "READ EACH. The span the LLM flagged is still in the committed "
        "record. Not bulk-dispositionable.",
    "residual_pii_text_residue":
        "Record is clean, committed stage0/text still carries the span. Not "
        "served by the index — a committed-tree ruling, not a per-record read.",
    "residual_pii_resolved":
        "Bulk-confirmable. The flagged span is gone from the record and from "
        "the committed Stage 0 text.",
    "residual_pii_unverifiable":
        "Flagged with no evidence span to check (Stage 1-origin flag). Falls "
        "back to the placeholder census shown per row.",
    "containment_fail":
        "Transcription check, not PII: the answer diverged from its source "
        "beyond the containment threshold.",
    "low_confidence":
        "The model's own self-assessment fell below the threshold; read with "
        "the confidence value.",
    "audit_sample":
        "The deterministic 5% spot-check. Nothing is known to be wrong with "
        "these — that is the point.",
}


def placeholders_in(record: dict) -> list[str]:
    text = " ".join(str(record.get(field) or "") for field in RECORD_FIELDS)
    return [token for token in PERSON_PLACEHOLDERS if token in text]


def load_evidence(cache_path: Path, records: dict[str, dict]) -> dict[str, str]:
    """``source_file`` -> the LLM's evidence span, joined on answer text.

    Missing cache -> empty mapping (the report degrades, it does not fail):
    the cache is gitignored, so a fresh clone legitimately has none.
    """
    if not cache_path.is_file():
        return {}
    by_answer: dict[str, str] = {}
    for row in read_jsonl(cache_path):
        value = row.get("v") or {}
        span = value.get("residual_pii_evidence")
        if value.get("answer") is not None and span:
            by_answer.setdefault(value["answer"], span)
    return {source_file: by_answer[record["answer"]]
            for source_file, record in records.items()
            if record.get("answer") in by_answer}


def span_survives(span: str, record: dict, stage0_text: Path,
                  source_file: str) -> tuple[bool, bool]:
    """(still in the committed record, still in the committed Stage 0 text)."""
    in_record = any(span in str(record.get(f) or "") for f in RECORD_FIELDS)
    text_file = stage0_text / source_file.replace(".docx", ".txt")
    in_text = (span in text_file.read_text(encoding="utf-8")
               if text_file.is_file() else False)
    return in_record, in_text


def group_of(row: dict, record: dict, span: str | None,
             stage0_text: Path) -> str:
    reasons = set(row.get("reasons") or [])
    if "residual_pii_flag" in reasons:
        if span is None:
            return "residual_pii_unverifiable"
        in_record, in_text = span_survives(span, record, stage0_text,
                                           row["source_file"])
        if in_record:
            return "residual_pii_unresolved"
        return ("residual_pii_text_residue" if in_text
                else "residual_pii_resolved")
    for reason in ("containment_fail", "low_confidence", "audit_sample"):
        if reason in reasons:
            return reason
    return "other"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage2", default=str(DEFAULT_STAGE2),
                        help=f"Stage 2 output dir (default: {DEFAULT_STAGE2})")
    parser.add_argument("--stage4", default=str(DEFAULT_STAGE4),
                        help="Stage 4 documents.jsonl, for the indexed count")
    parser.add_argument("--stage0-text", default=str(DEFAULT_STAGE0_TEXT),
                        help="committed Stage 0 text tree, for the residue check")
    parser.add_argument("--cache", default=str(DEFAULT_CACHE),
                        help="Stage 2 LLM cache (gitignored; absent -> "
                             "degraded report)")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()
    configure_stdio()

    stage2 = Path(args.stage2).resolve()
    stage0_text = Path(args.stage0_text).resolve()
    queue = read_jsonl(stage2 / "review_queue.jsonl")
    records = {r["source_file"]: r for r in read_jsonl(stage2 / "documents.jsonl")}

    missing = [row["source_file"] for row in queue
               if row["source_file"] not in records]
    if missing:
        raise SystemExit(
            f"{len(missing)} queue row(s) name a document Stage 2 did not "
            f"emit — the two artifacts are from different runs: {missing[:3]}")

    evidence = load_evidence(Path(args.cache).resolve(), records)

    # A flagged record that Stage 4 still calls current is live in the index.
    # That is the exposure this queue is about, so it is counted, not implied.
    stage4_path = Path(args.stage4).resolve()
    current: set[str] = set()
    if stage4_path.is_file():
        current = {r["source_file"] for r in read_jsonl(stage4_path)
                   if r.get("is_current")}

    groups: dict[str, list[dict]] = {name: [] for name in GROUP_ORDER}
    groups["other"] = []
    for row in queue:
        record = records[row["source_file"]]
        span = evidence.get(row["source_file"])
        groups[group_of(row, record, span, stage0_text)].append({
            "source_file": row["source_file"],
            "reasons": row.get("reasons") or [],
            "confidence": record.get("confidence"),
            "containment_score": record.get("containment_score"),
            "placeholders": placeholders_in(record),
            "has_evidence_span": span is not None,
            "is_current": row["source_file"] in current,
            "doc_type": record.get("doc_type"),
            "year": record.get("year"),
        })

    n_current = sum(1 for rows in groups.values() for r in rows if r["is_current"])
    report = {
        "n_queue": len(queue),
        "n_indexed_and_flagged": n_current,
        "n_with_evidence_span": len(evidence),
        "cache_available": bool(evidence),
        "reason_counts": dict(Counter(
            reason for row in queue for reason in (row.get("reasons") or []))),
        "groups": {name: {"n": len(rows),
                          "n_indexed": sum(1 for r in rows if r["is_current"]),
                          "note": GROUP_NOTES.get(name, ""),
                          "rows": rows}
                   for name, rows in groups.items() if rows},
    }

    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=1))
        return 0

    print(f"Stage 2 review queue: {len(queue)} rows "
          f"({n_current} are is_current and therefore live in the index)")
    print("This queue does not gate — flagged records are redacted, not "
          "withheld (docs/v1/decisions.md).")
    if not evidence:
        print("!! Stage 2 cache not found — no evidence spans, so residual-PII "
              "rows land in 'unverifiable'. Run on the machine that has "
              "processed/qa/stage2/runs/stage2_cache.jsonl for the real split.")
    print()
    for name in (*GROUP_ORDER, "other"):
        rows = groups.get(name) or []
        if not rows:
            continue
        print(f"## {name} — {len(rows)} "
              f"({sum(1 for r in rows if r['is_current'])} indexed)")
        print(f"   {GROUP_NOTES.get(name, '')}")
        show = rows if name == "residual_pii_unresolved" else rows[:5]
        for row in show:
            extra = (f" conf={row['confidence']}"
                     if row.get("confidence") is not None else "")
            print(f"   - {row['source_file']}{extra}")
        if len(rows) > len(show):
            print(f"   … and {len(rows) - len(show)} more (--json for all)")
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
