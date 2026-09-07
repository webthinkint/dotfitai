"""Triage the Stage 2 review queue into bulk-dispositionable groups (item 13).

`processed/qa/stage2/review_queue.jsonl` is 222 rows of ``source_file`` +
``reasons`` and nothing else, so working it means opening 222 records. The
dispositions are the owner's; what this does is make the pass a sign-off
instead of a 222-item read, the same way the alias worksheet did for §5.

Reading it right depends on one thing (`docs/decisions.md`, Stage 2 triage
shape): **this queue does not gate.** Stage 2 redacts what it flags, so a
flagged record is already scrubbed and already indexed — the queue is an audit
trail to work, not a hold. That is why the report leads with how many flagged
records are live in the index: that number is the actual exposure, and it is
not zero.

Groups, in the order an owner should work them:

1. **``residual_pii_flag`` with no placeholder in the record** — the sharp end.
   The LLM said it saw a name but the committed text carries no ``[NAME]`` /
   ``[CUSTOMER]`` / ``[EMAIL]`` marker, so either the redaction did not land or
   the flag was spurious. These need reading. Everything else can be sampled.
2. **``residual_pii_flag`` with a placeholder** — the flag fired and the
   redaction is visible in the text. Bulk-confirmable.
3. **``containment_fail``** — the answer diverged from its source beyond the
   containment threshold; a transcription-not-generation check, not a PII one.
4. **``low_confidence``** — the model's own self-assessment; read alongside
   ``confidence``.
5. **``audit_sample``** — the deterministic 5% sample. Nothing is wrong with
   these by construction; they are the spot-check.

Deliberately absent: the residual-PII **evidence spans**. Those live only in
the gitignored Stage 2 cache and never in a committed artifact
(`docs/decisions.md`), so this report shows the placeholder census and the
record's own fields, not the offending text. It writes nothing and prints no
answer text for the same reason.

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

# The Stage 0 placeholder vocabulary that stands for a redacted person
# (AGENTS.md: these tokens are parsed by Stage 1 and by human reviewers).
PERSON_PLACEHOLDERS = ("[NAME]", "[CUSTOMER]", "[EMAIL]", "[PHONE]",
                       "[SIGNATURE]", "[PROFILE-URL]")

# Worked in this order: the first group is the one that cannot be sampled.
GROUP_ORDER = ("residual_pii_no_placeholder", "residual_pii_placeholder",
               "containment_fail", "low_confidence", "audit_sample")

GROUP_NOTES = {
    "residual_pii_no_placeholder":
        "READ EACH. Flagged as residual PII but the committed text carries no "
        "placeholder — either the redaction did not land or the flag was "
        "spurious. Not bulk-dispositionable.",
    "residual_pii_placeholder":
        "Bulk-confirmable. Flag fired and a placeholder is visible in the "
        "committed text, so the redaction did land.",
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
    text = " ".join(str(record.get(field) or "")
                    for field in ("question_canonical", "question_original",
                                  "answer"))
    return [token for token in PERSON_PLACEHOLDERS if token in text]


def group_of(row: dict, record: dict) -> str:
    reasons = set(row.get("reasons") or [])
    if "residual_pii_flag" in reasons:
        return ("residual_pii_placeholder" if placeholders_in(record)
                else "residual_pii_no_placeholder")
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
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()
    configure_stdio()

    stage2 = Path(args.stage2).resolve()
    queue = read_jsonl(stage2 / "review_queue.jsonl")
    records = {r["source_file"]: r for r in read_jsonl(stage2 / "documents.jsonl")}

    missing = [row["source_file"] for row in queue
               if row["source_file"] not in records]
    if missing:
        raise SystemExit(
            f"{len(missing)} queue row(s) name a document Stage 2 did not "
            f"emit — the two artifacts are from different runs: {missing[:3]}")

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
        groups[group_of(row, record)].append({
            "source_file": row["source_file"],
            "reasons": row.get("reasons") or [],
            "confidence": record.get("confidence"),
            "containment_score": record.get("containment_score"),
            "placeholders": placeholders_in(record),
            "is_current": row["source_file"] in current,
            "doc_type": record.get("doc_type"),
            "year": record.get("year"),
        })

    n_current = sum(1 for rows in groups.values() for r in rows if r["is_current"])
    report = {
        "n_queue": len(queue),
        "n_indexed_and_flagged": n_current,
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
          "withheld (docs/decisions.md).")
    print()
    for name in (*GROUP_ORDER, "other"):
        rows = groups.get(name) or []
        if not rows:
            continue
        print(f"## {name} — {len(rows)} "
              f"({sum(1 for r in rows if r['is_current'])} indexed)")
        print(f"   {GROUP_NOTES.get(name, '')}")
        show = rows if name == "residual_pii_no_placeholder" else rows[:5]
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
