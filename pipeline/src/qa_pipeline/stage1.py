"""Stage 1 — parse & classify (plan §4, Stage 1). Deterministic, no LLM.

Consumes Stage 0 scrubbed text (placeholders are part of the contract) plus the
Stage 0 reports (for flags) and produces one record per document:

- section split: expert answer vs. quoted customer message (header markers)
- classification: qa_email | expert_note | other (+ needs_review flag for the
  LLM-confirm edge cases of the plan)
- metadata: year (folder), topic subfolder, filename, answer date from the
  ``Sent:`` header, extracted question text

Outputs: documents.jsonl (parsed docs), review_queue.jsonl (errors, residual
PII, edge cases) and summary.json. All JSONL sorted by source_file.
"""

from __future__ import annotations

import re
from datetime import datetime
from pathlib import Path

from .io_utils import doc_id, read_json
from .structure import (
    QA_QUESTION_LABELS,
    find_customer_header,
    find_forwarded_split,
    find_qa_split,
    find_question_label,
    has_email_markers,
    label_of,
)

# Date parsing is deliberately regex-based, NOT strptime with %A/%B/%p:
# those directives resolve through LC_TIME, so a prod host with a non-English
# locale would silently parse every ``Sent:`` header as None. Only the date is
# needed, so the time (and its locale-bound AM/PM) is never parsed at all.
MONTHS = {
    "january": 1, "february": 2, "march": 3, "april": 4, "may": 5,
    "june": 6, "july": 7, "august": 8, "september": 9, "october": 10,
    "november": 11, "december": 12,
    "jan": 1, "feb": 2, "mar": 3, "apr": 4, "jun": 6, "jul": 7, "aug": 8,
    "sep": 9, "sept": 9, "oct": 10, "nov": 11, "dec": 12,
}
WEEKDAY_PREFIX_RE = re.compile(
    r"^\s*(?:mon|tues?|wed(?:nes)?|thur?s?|fri|sat(?:ur)?|sun)[a-z]*\s*,?\s*",
    re.IGNORECASE,
)
TEXT_DATE_RE = re.compile(r"\b([A-Za-z]{3,9})\s+(\d{1,2})\s*,?\s+(\d{4})\b")
SLASH_DATE_RE = re.compile(r"\b(\d{1,2})/(\d{1,2})/(\d{2,4})\b")
ISO_DATE_RE = re.compile(r"\b(\d{4})-(\d{2})-(\d{2})\b")

QUESTION_LABELS = {"question", "message", "comments"}


def parse_sent_date(value: str) -> str | None:
    """'Sunday, February 26, 2023 5:08 AM' -> '2023-02-26' (ISO date, or None).

    Locale-independent by construction (see MONTHS above).
    """
    value = WEEKDAY_PREFIX_RE.sub("", value.strip())

    m = TEXT_DATE_RE.search(value)
    if m:
        month = MONTHS.get(m.group(1).lower())
        if month:
            got = _iso(int(m.group(3)), month, int(m.group(2)))
            if got:
                return got

    m = ISO_DATE_RE.search(value)
    if m:
        got = _iso(int(m.group(1)), int(m.group(2)), int(m.group(3)))
        if got:
            return got

    m = SLASH_DATE_RE.search(value)
    if m:
        year = int(m.group(3))
        if year < 100:                      # 2-digit years, strptime's rule
            year += 2000 if year < 69 else 1900
        got = _iso(year, int(m.group(1)), int(m.group(2)))
        if got:
            return got
    return None


def _iso(year: int, month: int, day: int) -> str | None:
    try:
        return datetime(year, month, day).strftime("%Y-%m-%d")
    except ValueError:
        return None


def _extract_question(customer_lines: list[str]) -> str | None:
    """Text of the Question:/Message: labeled line + continuation lines."""
    for i, line in enumerate(customer_lines):
        lab = label_of(line)
        if lab and lab[0] in QUESTION_LABELS:
            parts = [lab[1]]
            for j in range(i + 1, len(customer_lines)):
                if label_of(customer_lines[j]) is not None:
                    break
                if not customer_lines[j].strip():
                    break   # a blank line ends the question; what follows is
                            # the reply, not more question text
                parts.append(customer_lines[j].strip())
            text = " ".join(p for p in parts if p).strip()
            return text or None
    return None


def classify_and_parse(
    lines: list[str],
    source_file: str,
    scrub_report: dict | None = None,
) -> dict:
    """One Stage 1 record from scrubbed lines (see module docstring)."""
    rel_parts = source_file.split("/")
    year = int(rel_parts[0]) if len(rel_parts) > 1 and rel_parts[0].isdigit() else None
    topic = "/".join(rel_parts[1:-1]) if len(rel_parts) > 2 else ""
    filename = rel_parts[-1].removesuffix(".docx")

    header_idx = find_customer_header(lines)
    if header_idx == 0 and (fwd := find_forwarded_split(lines)) is not None:
        # the document opens with its own thread header, so the expert's
        # reply is written BELOW the quoted contact block, not above it
        _, a_idx = fwd
        expert_lines = [l for l in lines[a_idx:] if l.strip()]
        customer_lines = lines[:a_idx]
        doc_type = "qa_email"
        needs_review = False
    elif header_idx is not None:
        expert_lines = [l for l in lines[:header_idx] if l.strip()]
        customer_lines = lines[header_idx:]
        doc_type = "qa_email"
        needs_review = False
    elif (qa_split := find_qa_split(lines)) is not None:
        # free-form Question:/Answer: note — same shape as a QA email, no thread
        q_idx, a_idx = qa_split
        expert_lines = [l for l in lines[a_idx:] if l.strip()]
        customer_lines = lines[q_idx:a_idx]
        doc_type = "qa_email"
        needs_review = False
    elif (q_idx := find_question_label(lines)) is not None and all(
        (lab := label_of(l)) is None or lab[0] in QA_QUESTION_LABELS
        for l in lines
    ):
        # message-only Q&A note: one labeled question, answer inline after it
        expert_lines = [l for i, l in enumerate(lines) if l.strip() and i != q_idx]
        customer_lines = [lines[q_idx]]
        doc_type = "qa_email"
        needs_review = False
    elif has_email_markers(lines):
        expert_lines = [l for l in lines if l.strip()]
        customer_lines = []
        doc_type = "other"          # markers present but no quotable structure
        needs_review = True         # -> LLM-confirm / human review per plan
    else:
        expert_lines = [l for l in lines if l.strip()]
        customer_lines = []
        doc_type = "expert_note"
        needs_review = False

    # NB: this is the *thread's* Sent: header — the enquiry's send date, not
    # the expert's reply date (which the corpus rarely records). Stage 4 uses
    # it as a lower bound for currency ordering.
    thread_date = None
    for line in customer_lines:
        lab = label_of(line)
        if lab and lab[0] == "sent":
            thread_date = parse_sent_date(lab[1])
            if thread_date:
                break
    if thread_date is None:
        for line in lines:  # Sent: without From: variants
            lab = label_of(line)
            if lab and lab[0] == "sent":
                thread_date = parse_sent_date(lab[1])
                if thread_date:
                    break

    flags = list((scrub_report or {}).get("flags", []))
    residual_pii = bool((scrub_report or {}).get("residual_pii_flag", False))
    if residual_pii:
        needs_review = True
    # a document with no answer/body text is not indexable — the CLI excludes
    # it from documents.jsonl (owner disposition 2026-09-02); the flag stays
    # true in the record so the exclusion is auditable per document
    if not expert_lines:
        needs_review = True

    return {
        "id": doc_id(source_file),
        "source_file": source_file,
        "year": year,
        "topic_subfolder": topic,
        "filename": filename,
        "doc_type": doc_type,
        "needs_review": needs_review,
        "residual_pii_flag": residual_pii,
        "thread_date": thread_date,
        "question": _extract_question(customer_lines),
        "expert_section": "\n".join(expert_lines),
        "customer_section": "\n".join(l for l in customer_lines if l.strip()),
        "scrub_flags": flags,
        "n_lines": len(lines),
    }


def review_reasons(rec: dict) -> list[str]:
    """Why *rec* is in the Stage 3 queue (plan §4 Stage 3), most specific first.

    Documents with no expert-answer text never get here: the CLI excludes
    them from documents.jsonl and tallies them in summary.json instead
    (owner disposition 2026-09-02 — only answerable docs are indexed, and
    there is nothing left to decide about a stub)."""
    reasons: list[str] = []
    if rec["residual_pii_flag"]:
        reasons.append("residual_pii_flag")
    if rec["doc_type"] == "other":
        reasons.append("no_quotable_structure")
    return reasons + rec["scrub_flags"]


def load_scrub_report(stage0_reports_dir: Path, source_file: str) -> dict | None:
    """Reports mirror the input tree: <source_file-without-.docx>.json."""
    p = stage0_reports_dir / Path(source_file).with_suffix(".json")
    if p.is_file():
        try:
            return read_json(p)
        except Exception:
            return None
    return None
