"""Stage 2 — LLM-assisted structuring (plan §4, Stage 2). Scrubbed text only.

Consumes Stage 1 ``documents.jsonl`` (scrubbed, never raw) plus the §5 alias
table (built from ``products.json``) and produces one canonical record per
document:

- ``question_canonical`` — customer question, normalized to standalone
  phrasing (LLM transcription; null for expert notes with no question)
- ``question_original`` — verbatim scrubbed Stage 1 question (deterministic)
- ``answer`` — expert answer, cleaned (typos, formatting), content unchanged
- ``products[]`` — normalized to ``part_no`` (int) via the alias table:
  LLM mentions mapped deterministically, unioned with the blind text scan
  (``CURATED_ALIASES`` + legacy renames). ``CURATED_LLM_ONLY_ALIASES``
  resolve on the mention path only — the model supplies the context that
  tells ``Women's MV`` from ``women's health``. Context-only tokens (PP,
  MVM), replacements and discontinued names never tag — currency cues.
- ``topics[]`` — LLM labels + subfolder hint (+ ``multivitamin`` when an
  unresolved MVM mention leaves no product tag, per ``CONTEXT_ONLY_TOKENS``)
- ``audience_flags{}`` — the plan's five escalation booleans (LLM)
- ``currency_cues[]`` — deprecated/discontinued names found by tolerant
  deterministic scan (renames, replacements, discontinued)
- ``residual_pii_flag`` — Stage 1 flag OR LLM flag (the LLM redacts what it
  flags, to [NAME]; owner-confirmed staff names redact silently without
  flagging, public-figure surnames are quoted verbatim); ``confidence`` — LLM
  self-assessed extraction quality, below threshold goes to review

The LLM output is **transcription + structuring, never generation**: a
source-containment diff pass (word-recall of the answer against the source
sections) verifies no invented content; failures go to the review queue,
never silently dropped. Small Azure OpenAI deployment, strict JSON-schema
outputs, GPT-5-family default (no temperature knob — determinism comes from
the strict schema plus the containment pass, not temperature 0).

Determinism: records carry no timestamps and are sorted by ``source_file``;
reruns with the same LLM responses (same-machine cache hit or ``--no-llm``)
are byte-identical. Live LLM reruns may vary by wording — the gitignored
``runs/stage2_cache.jsonl`` (keyed deployment|api-version|prompt|source)
makes same-machine reruns free and stable. ``--no-llm`` runs the
deterministic rule-based fallback for every document (confidence 0, queued
for review) — CI, tests and shaping without Azure spend.
"""

from __future__ import annotations

import hashlib
import json
import re
import time
from collections import Counter
from pathlib import Path
from typing import Any, Protocol

from .alias import _tolerant_pattern as _alias_tolerant_pattern
from .alias import build_alias_table, norm as _norm
from .scrub import ACCEPTED_HONORIFIC_NAMES

# --- tunables (curation lives in constants — AGENTS.md) ----------------------

CHAT_API_VERSION = "2025-04-01-preview"  # verified live (scripts/chat_smoke.py)
PROMPT_VERSION = "1.1.0"  # part of the cache key: a prompt edit must miss cache
MIN_CONFIDENCE_DEFAULT = 0.7  # below -> Stage 3 review queue (plan §4 Stage 3)
CONTAINMENT_THRESHOLD = 0.85  # answer word-recall against source, below -> review
AUDIT_RATE = 0.05  # deterministic 5% high-confidence audit sample (plan §4 Stage 3)
MAX_SOURCE_CHARS = 80_000  # combined source sections; above -> fallback, never truncated
MAX_RETRIES = 6

AUDIENCE_KEYS = (
    "minor",
    "pregnancy_breastfeeding",
    "medical_condition",
    "drug_test_athlete",
    "weight_extreme",
)

# Staff names the owner confirmed (Stage 2 triage round 1, 2026-09-06):
# first names CC'd across program notes plus the surnames that complete the
# known staff full names (Neal Spruce, Mark Rowland, Kat Barefield — spelled
# as attested). The prompt redacts these to [NAME] SILENTLY (no flag): they
# recur in hundreds of answers and bulk-accepting them every run is not
# triage. NOTE the trade taken: a future customer sharing one of these first
# names is silently redacted too — harmless (bare first names carry no answer
# content), and anything fuller still flags. Veto here if that trade is ever
# wrong. Deliberately EXCLUDED (customer-side per the same triage): James,
# Paul, Steve, Neil, Matt, Kendra, Ferguson, Gay, Riley, Jessica.
SILENT_STAFF_NAMES = frozenset({
    "Berlinda", "Chad", "Kat", "Mark", "Jill", "Neal",
    "Spruce", "Rowland", "Barefield",
})

_RETRYABLE = {"RateLimitError", "APIConnectionError", "APITimeoutError",
              "InternalServerError"}


# --- LLM contract -------------------------------------------------------------

STAGE2_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "question_canonical": {
            "type": ["string", "null"],
            "description": "Customer question rephrased standalone, or null "
                           "when the document has no customer question.",
        },
        "answer": {
            "type": "string",
            "description": "Expert answer cleaned (typos, formatting only), "
                           "content unchanged, placeholders preserved.",
        },
        "products_mentioned": {
            "type": "array",
            "items": {"type": "string"},
            "description": "Supplement product names/abbreviations as written "
                           "in the source (AF, MVM, FirstString, LeanMR...). "
                           "Empty when none are mentioned. Never part numbers.",
        },
        "topics": {
            "type": "array",
            "items": {"type": "string"},
            "description": "1-5 short topic labels.",
        },
        "audience_flags": {
            "type": "object",
            "properties": {k: {"type": "boolean"} for k in AUDIENCE_KEYS},
            "required": list(AUDIENCE_KEYS),
            "additionalProperties": False,
        },
        "residual_pii_flag": {
            "type": "boolean",
            "description": "True when a real person name (or other PII) was "
                           "seen in the scrubbed source.",
        },
        "residual_pii_evidence": {
            "type": ["string", "null"],
            "description": "The offending span, or null when the flag is false.",
        },
        "confidence": {
            "type": "number",
            "description": "Self-assessed extraction quality, 0-1.",
        },
    },
    "required": ["question_canonical", "answer", "products_mentioned", "topics",
                 "audience_flags", "residual_pii_flag", "residual_pii_evidence",
                 "confidence"],
    "additionalProperties": False,
}

_PUBLIC_FIGURES = ", ".join(sorted(ACCEPTED_HONORIFIC_NAMES))
_STAFF_NAMES = ", ".join(sorted(SILENT_STAFF_NAMES))

SYSTEM_PROMPT = (
    "You structure scrubbed customer nutrition Q&A documents for retrieval. "
    "Return JSON only via the schema. Rules: transcription, never generation. "
    "question_canonical rephrases the customer's question as standalone phrasing; "
    "null when the document has no customer question (expert note). answer cleans "
    "the expert's reply (typos, formatting) without changing content, adding claims, "
    "or inventing advice — every answer sentence must be traceable to the source "
    "expert text. For expert notes with no customer question, transcribe the FULL "
    "expert text (cleaned only) — never summarize or shorten; a one-line summary "
    "is a failure. Preserve placeholders verbatim ([EMAIL] [PHONE] [CUSTOMER] [NAME] "
    "[SIGNATURE] [ADDRESS] [PROFILE-URL]); never restore names or contact details. "
    "Person names need care. (1) These dotFIT staff names appear routinely as CCs "
    "and referrals — redact them to [NAME] SILENTLY (residual_pii_flag stays "
    f"false, evidence null): {_STAFF_NAMES}. "
    "(2) These widely-cited public figures (study authors, clinicians quoted in "
    "pasted articles) — quote them verbatim, never flag, never redact: "
    f"{_PUBLIC_FIGURES}. "
    "(3) Any other real person name: replace it with [NAME] in the answer and set "
    "residual_pii_flag true, with ONLY the name span (no surrounding words) in "
    "residual_pii_evidence. "
    "products_mentioned lists supplement product names/abbreviations as written in the "
    "source (AF, SB, FS, WLLS, MVM, PP, FirstString, LeanMR...), every product mentioned "
    "in question or answer, empty when none — never part numbers. When the document "
    "discusses MVM generically with no clear audience, return 'MVM' as written. "
    "topics are 1-5 short labels. audience_flags is true when the document concerns that "
    "group (minor under 18; pregnancy/breastfeeding; managed medical condition or "
    "medication; drug-tested athlete; extreme weight/calorie target). confidence is 0-1 "
    "extraction quality — low when the source is contradictory, truncated, or ambiguous."
)


def build_messages(rec: dict[str, Any], families: list[str]) -> list[dict[str, str]]:
    """System + user messages for one Stage 1 record (deterministic)."""
    question = rec.get("question")
    familes_sorted = sorted(families)
    user = (
        f"Document type: {rec.get('doc_type')}\n"
        f"Year: {rec.get('year')}\n"
        f"Topic hint (subfolder): {rec.get('topic_subfolder') or '—'}\n"
        f"Filename (topic summary): {rec.get('filename')}\n"
        f"Known product families: {', '.join(familes_sorted)}\n"
        "Deterministic abbreviations (return as written): AF, SB, FS, WLLS. "
        "Context-only (return as written, do not resolve): PP, MVM — unless the "
        "audience is explicit (women's health: Women's MV; age 50+: Over 50 MV; "
        "general/athlete: Active MV).\n"
        f"Customer question (verbatim scrubbed, may be null): {question or '—'}\n"
        f"Customer section (scrubbed):\n{rec.get('customer_section') or '—'}\n"
        f"Expert answer (scrubbed, the source of truth):\n{rec.get('expert_section') or '—'}"
    )
    return [{"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": user}]


def validate_llm_output(obj: Any) -> dict[str, Any]:
    """Strict-check the parsed LLM JSON (no new deps). Raises ValueError."""
    if not isinstance(obj, dict):
        raise ValueError(f"LLM output must be an object, got {type(obj).__name__}")
    missing = [k for k in STAGE2_SCHEMA["required"] if k not in obj]
    if missing:
        raise ValueError(f"LLM output missing keys: {missing}")
    extra = [k for k in obj if k not in STAGE2_SCHEMA["required"]]
    if extra:
        raise ValueError(f"LLM output has extra keys: {extra}")
    qc = obj["question_canonical"]
    if qc is not None and (not isinstance(qc, str) or not qc.strip()):
        raise ValueError("question_canonical must be a non-empty string or null")
    if not isinstance(obj["answer"], str) or not obj["answer"].strip():
        raise ValueError("answer must be a non-empty string")
    if (not isinstance(obj["products_mentioned"], list)
            or not all(isinstance(p, str) for p in obj["products_mentioned"])):
        raise ValueError("products_mentioned must be a list of strings")
    if (not isinstance(obj["topics"], list)
            or not all(isinstance(t, str) and t.strip() for t in obj["topics"])):
        raise ValueError("topics must be a list of non-empty strings")
    af = obj["audience_flags"]
    if not isinstance(af, dict) or set(af) != set(AUDIENCE_KEYS):
        raise ValueError(f"audience_flags must have exactly keys {list(AUDIENCE_KEYS)}")
    if not all(isinstance(af[k], bool) for k in AUDIENCE_KEYS):
        raise ValueError("audience_flags values must be booleans")
    if not isinstance(obj["residual_pii_flag"], bool):
        raise ValueError("residual_pii_flag must be a boolean")
    ev = obj["residual_pii_evidence"]
    if ev is not None and not isinstance(ev, str):
        raise ValueError("residual_pii_evidence must be a string or null")
    cf = obj["confidence"]
    if not isinstance(cf, (int, float)) or isinstance(cf, bool):
        raise ValueError("confidence must be a number")
    if not 0.0 <= float(cf) <= 1.0:
        raise ValueError(f"confidence {cf!r} outside 0-1")
    return obj


# --- deterministic post-processing (no LLM) ------------------------------------

_WORD_RE = re.compile(r"[a-z0-9]+")
_BOUNDARY_RE = re.compile(r"(?<=[0-9])(?=[a-z])|(?<=[a-z])(?=[0-9])")


def word_tokens(text: str) -> list[str]:
    """Lowercase alphanumeric tokens (placeholders degrade to words).

    Digit↔letter boundaries split first, so dosage forms (``34mg``,
    ``1.01grams``, ``B12``) match their spaced writings (``34 mg``) — without
    this, a perfectly transcribed dosage scores as invented (triage round 2,
    2026-09-06: ``34 mg/serving`` vs source ``34mg/serving`` scored 0.33).
    """
    return _WORD_RE.findall(_BOUNDARY_RE.sub(" ", text.casefold()))


def containment_score(answer: str, source: str) -> float:
    """Multiset word-recall of *answer* against *source* (0-1).

    Transcription keeps wording, so a faithful clean scores ~1; invented
    sentences introduce novel tokens and drop the score. Empty answers score 0.
    """
    ans = word_tokens(answer)
    if not ans:
        return 0.0
    have = Counter(word_tokens(source))
    hit = sum(min(n, have.get(tok, 0)) for tok, n in Counter(ans).items())
    return hit / len(ans)


def build_product_lookup(alias_table: dict[str, Any]) -> dict[str, Any]:
    """Alias table -> ``{"tag": norm->part_nos, "context_only", "never_tag"}``.

    - ``tag``: families + deterministic aliases + LLM-only aliases + legacy
      renames (a rename is an identity mapping, safe to expand to the
      successor's part_nos). This lookup drives :func:`normalize_products`,
      which maps *LLM mention strings* — the model has already judged the
      mention to be a product in that document, so context-gated aliases
      (``Women's``) belong here. They must NOT reach
      :func:`deterministic_product_tags`, which scans raw text blind; that
      function reads ``deterministic_aliases`` directly and so never sees
      them.
    - ``context_only``: PP/MVM norms — resolved per document by the LLM, never
      tagged deterministically.
    - ``never_tag``: replacement + discontinued norms — currency cues only.
    """
    tag: dict[str, list[int]] = {}
    for fam in alias_table["families"]:
        tag.setdefault(_norm(fam["family"]), sorted(fam["part_nos"]))
    for entry in alias_table.get("deterministic_aliases", []):
        tag.setdefault(_norm(entry["token"]), sorted(entry["part_nos"]))
    for entry in alias_table.get("llm_only_aliases", []):
        tag.setdefault(_norm(entry["token"]), sorted(entry["part_nos"]))
    for ren in alias_table.get("legacy_renames", []):
        tag.setdefault(_norm(ren["deprecated"]), sorted(ren["part_nos"]))
    context_only = {_norm(tok) for tok in alias_table.get("context_only_tokens", {})}
    never_tag = {_norm(r["deprecated"]) for r in alias_table.get("replacements", [])}
    never_tag |= {_norm(d["name"]) for d in alias_table.get("discontinued", [])}
    # A token that is both deterministic and context-only must never tag:
    # the ambiguity ruling wins (the MVM lesson — progress 2026-09-01).
    for tok in context_only | never_tag:
        tag.pop(tok, None)
    return {"tag": tag, "context_only": context_only, "never_tag": never_tag}


def normalize_products(mentions: list[str], lookup: dict[str, Any],
                       families: list[str] | None = None) -> tuple[list[int], list[str]]:
    """LLM mention strings -> (sorted part_nos, unresolved mentions).

    Variant longnames (``AminoFormula - Blue Raspberry``) resolve by family
    prefix; context-only / replacement / discontinued mentions never tag and
    are reported unresolved for the review queue.
    """
    tag, context_only, never_tag = lookup["tag"], lookup["context_only"], lookup["never_tag"]
    fam_norms = sorted(((_norm(f), f) for f in (families or [])), key=lambda t: -len(t[0]))
    part_nos: set[int] = set()
    unresolved: list[str] = []
    for raw in mentions:
        m = (raw or "").strip()
        if not m:
            continue
        key = _norm(m)
        if not key:
            unresolved.append(m)
            continue
        if key in context_only or key in never_tag:
            unresolved.append(m)
            continue
        if key in tag:
            part_nos.update(tag[key])
            continue
        hit: list[int] | None = None  # longest family-prefix match (variant names)
        for fnorm, _fname in fam_norms:
            if key.startswith(fnorm):
                hit = tag.get(fnorm)
                break
        if hit:
            part_nos.update(hit)
        else:
            unresolved.append(m)
    return sorted(part_nos), sorted(set(unresolved))


def deterministic_product_tags(text: str, alias_table: dict[str, Any],
                               lookup: dict[str, Any]) -> list[int]:
    """High-precision scan for deterministic tags (aliases + legacy renames).

    Safety net for mentions the LLM missed: every hit here maps to one family
    by curation, so unioning with the LLM-normalized set is safe. Word
    boundaries for short uppercase tokens, tolerant matching for legacy names
    (spacing/punctuation/``and``-forms — the ``Recover&Build`` lesson).

    Reads ``deterministic_aliases`` only. ``llm_only_aliases`` are excluded by
    construction: they are ordinary English too (``Women's``), and this scan
    has no context with which to tell a product from a phrase.
    """
    tag = lookup["tag"]
    found: set[int] = set()
    for entry in alias_table.get("deterministic_aliases", []):
        if re.search(r"(?<![A-Za-z0-9])" + re.escape(entry["token"])
                     + r"(?![A-Za-z0-9])", text):
            key = _norm(entry["token"])
            if key in tag:  # context-only/never-tag tokens were evicted
                found.update(tag[key])
    for ren in alias_table.get("legacy_renames", []):
        if _alias_tolerant_pattern(ren["deprecated"]).search(text):
            key = _norm(ren["deprecated"])
            if key in tag:
                found.update(tag[key])
    return sorted(found)


def detect_currency_cues(text: str, alias_table: dict[str, Any]) -> list[str]:
    """Deprecated/discontinued names in *text* (tolerant scan, sorted).

    Renames, replacements and discontinued-without-successor alike: any of
    them makes the answer's currency suspect for Stage 4. Display forms as in
    the alias table (``LeanMR``, ``Recover&Build``, ``KidsMV``...).
    """
    cues: set[str] = set()
    for ren in alias_table.get("legacy_renames", []):
        if _alias_tolerant_pattern(ren["deprecated"]).search(text):
            cues.add(ren["deprecated"])
    for rep in alias_table.get("replacements", []):
        if _alias_tolerant_pattern(rep["deprecated"]).search(text):
            cues.add(rep["deprecated"])
    for disc in alias_table.get("discontinued", []):
        if _alias_tolerant_pattern(disc["name"]).search(text):
            cues.add(disc["name"])
    return sorted(cues)


def merge_topics(llm_topics: list[str], topic_subfolder: str | None,
                 unresolved_mvm: bool = False) -> list[str]:
    """LLM labels + subfolder hint, deduped case-insensitively, sorted.

    An unresolved MVM mention leaves no product tag, so the multivitamin
    topic is forced (``CONTEXT_ONLY_TOKENS`` guidance) — otherwise the
    document would be invisible to multivitamin queries entirely.
    """
    seen: dict[str, str] = {}
    for t in llm_topics:
        t = (t or "").strip()
        if t and t.casefold() not in seen:
            seen[t.casefold()] = t
    for part in (topic_subfolder or "").split("/"):
        part = part.strip()
        if part and part.casefold() not in seen:
            seen[part.casefold()] = part
    if unresolved_mvm and "multivitamin" not in seen:
        seen["multivitamin"] = "multivitamin"
    return sorted(seen.values(), key=str.casefold)


def is_audit_sample(doc_id: str, rate: float = AUDIT_RATE) -> bool:
    """Deterministic ~*rate* audit sample (hash-based, not random)."""
    if rate <= 0:
        return False
    digest = hashlib.sha256(doc_id.encode("utf-8")).digest()
    return digest[0] < int(rate * 256)


# --- record build ---------------------------------------------------------------

def _source_text(rec: dict[str, Any]) -> str:
    return "\n".join(t for t in (rec.get("question") or "",
                                 rec.get("customer_section") or "",
                                 rec.get("expert_section") or "") if t)


def build_record(rec: dict[str, Any], llm: dict[str, Any],
                 alias_table: dict[str, Any], lookup: dict[str, Any],
                 families: list[str], model: str,
                 min_confidence: float = MIN_CONFIDENCE_DEFAULT) -> dict[str, Any]:
    """One Stage 2 record from a Stage 1 record + validated LLM output."""
    source = _source_text(rec)
    llm_part_nos, unresolved = normalize_products(
        llm["products_mentioned"], lookup, families)
    det_part_nos = deterministic_product_tags(source, alias_table, lookup)
    part_nos = sorted(set(llm_part_nos) | set(det_part_nos))
    # unresolved = LLM mentions that tagged nothing (context-only, never-tag,
    # or unknown strings needing review) — deterministic-scan hits always tag.
    currency = detect_currency_cues(source, alias_table)
    unresolved_mvm = any(_norm(m) in lookup["context_only"] and _norm(m) == _norm("MVM")
                         for m in llm["products_mentioned"])
    topics = merge_topics(llm["topics"], rec.get("topic_subfolder"), unresolved_mvm)
    score = containment_score(llm["answer"], source)
    residual = bool(rec.get("residual_pii_flag")) or bool(llm["residual_pii_flag"])
    return {
        "id": rec["id"],
        "source_file": rec["source_file"],
        "year": rec.get("year"),
        "doc_type": rec.get("doc_type"),
        "thread_date": rec.get("thread_date"),
        "topic_subfolder": rec.get("topic_subfolder"),
        "filename": rec.get("filename"),
        "question_original": rec.get("question"),
        "question_canonical": llm["question_canonical"],
        "answer": llm["answer"],
        "products": part_nos,
        "products_unresolved": unresolved,
        "topics": topics,
        "audience_flags": {k: bool(llm["audience_flags"][k]) for k in AUDIENCE_KEYS},
        "currency_cues": currency,
        "residual_pii_flag": residual,
        "confidence": float(llm["confidence"]),
        "containment_score": round(score, 4),
        "needs_review": False,  # set by mark_review(); kept here for the audit trail
        "llm_error": None,
        "model": model,
    }


def fallback_record(rec: dict[str, Any], alias_table: dict[str, Any],
                    lookup: dict[str, Any], error: str,
                    model: str = "rule-based") -> dict[str, Any]:
    """Deterministic record when the LLM is unavailable or rejected.

    Answer stays verbatim (containment 1.0 by construction); products come
    from the deterministic scan only; confidence 0 forces review. ``--no-llm``
    runs this for every document.
    """
    source = _source_text(rec)
    part_nos = deterministic_product_tags(source, alias_table, lookup)
    currency = detect_currency_cues(source, alias_table)
    topics = merge_topics([], rec.get("topic_subfolder"))
    return {
        "id": rec["id"],
        "source_file": rec["source_file"],
        "year": rec.get("year"),
        "doc_type": rec.get("doc_type"),
        "thread_date": rec.get("thread_date"),
        "topic_subfolder": rec.get("topic_subfolder"),
        "filename": rec.get("filename"),
        "question_original": rec.get("question"),
        "question_canonical": (rec.get("question").strip()
                               if rec.get("question") else None),
        "answer": (rec.get("expert_section") or "").strip(),
        "products": part_nos,
        "products_unresolved": [],
        "topics": topics,
        "audience_flags": {k: False for k in AUDIENCE_KEYS},
        "currency_cues": currency,
        "residual_pii_flag": bool(rec.get("residual_pii_flag")),
        "confidence": 0.0,
        "containment_score": 1.0 if (rec.get("expert_section") or "").strip() else 0.0,
        "needs_review": True,
        "llm_error": error,
        "model": model,
    }


def review_reasons(rec2: dict[str, Any],
                   min_confidence: float = MIN_CONFIDENCE_DEFAULT) -> list[str]:
    """Why *rec2* is in the Stage 3 queue (plan §4 Stage 3), most specific first.

    ``products_unresolved`` is deliberately NOT a queue reason: the LLM names
    every brand it sees (Gatorade, dotFIT housekeeping, discontinued spellings),
    so queuing on it puts ~every document in review. Unresolved mentions are a
    *curation* signal — tallied in ``summarize()`` for the alias worksheet —
    while the queue stays on quality signals (plan: confidence + residual PII;
    plus containment and hard errors, which are confidence by another name).
    """
    reasons: list[str] = []
    if rec2.get("llm_error"):
        reasons.append("llm_error")
    if rec2.get("residual_pii_flag"):
        reasons.append("residual_pii_flag")
    if float(rec2.get("containment_score", 1.0)) < CONTAINMENT_THRESHOLD:
        reasons.append("containment_fail")
    if float(rec2.get("confidence", 0.0)) < min_confidence:
        reasons.append("low_confidence")
    if not reasons and is_audit_sample(rec2["id"]):
        reasons.append("audit_sample")
    return reasons


def mark_review(rec2: dict[str, Any],
                min_confidence: float = MIN_CONFIDENCE_DEFAULT) -> dict[str, Any]:
    """Set ``needs_review`` from :func:`review_reasons` (pure)."""
    rec2 = dict(rec2)
    rec2["needs_review"] = bool(review_reasons(rec2, min_confidence))
    return rec2


# --- cache -----------------------------------------------------------------------

def cache_key(deployment: str, api_version: str, rec: dict[str, Any]) -> str:
    """Stable key: deployment|api-version|prompt|source (input change misses)."""
    raw = "|".join([
        deployment, api_version, PROMPT_VERSION,
        rec.get("source_file") or "", rec.get("doc_type") or "",
        rec.get("question") or "", rec.get("customer_section") or "",
        rec.get("expert_section") or "",
    ]).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()


def load_cache(path: Path) -> dict[str, dict[str, Any]]:
    """Read the JSONL LLM cache (missing file -> empty, never an error)."""
    if not path.is_file():
        return {}
    out: dict[str, dict[str, Any]] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            entry = json.loads(line)
            out[entry["k"]] = entry["v"]
    return out


def append_cache(path: Path, key: str, value: dict[str, Any]) -> None:
    """Append one entry (per-document checkpoint — a crash keeps prior calls)."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps({"k": key, "v": value}, ensure_ascii=False) + "\n")


# --- Azure wiring ------------------------------------------------------------------

class _ChatAPI(Protocol):
    def create(self, *, model: str, messages: list[dict],
               response_format: dict[str, Any]) -> Any: ...


class Extractor:
    """Small-chat JSON-schema client with retries. Never echoes key material.

    *schema*/*schema_name* default to the Stage 2 contract; Stage 4's
    currency judge injects its own (same wire format, different schema).
    """

    def __init__(self, endpoint: str, api_key: str, deployment: str,
                 api_version: str, max_retries: int = MAX_RETRIES,
                 client: _ChatAPI | None = None,
                 schema: dict[str, Any] | None = None,
                 schema_name: str = "qa_stage2"):
        self.endpoint = endpoint
        self.api_key = api_key
        self.deployment = deployment
        self.api_version = api_version
        self.max_retries = max_retries
        self._client = client  # injectable for tests
        self.schema = schema if schema is not None else STAGE2_SCHEMA
        self.schema_name = schema_name
        self.n_api_calls = 0

    def __call__(self, messages: list[dict[str, str]]) -> dict[str, Any]:
        client = self._client if self._client is not None else self._make_client()
        delay = 1.0
        for attempt in range(self.max_retries + 1):
            try:
                resp = client.create(
                    model=self.deployment,
                    messages=messages,
                    response_format={
                        "type": "json_schema",
                        "json_schema": {"name": self.schema_name,
                                        "strict": True,
                                        "schema": self.schema},
                    },
                )
                self.n_api_calls += 1
                return json.loads(resp.choices[0].message.content)
            except Exception as e:  # noqa: BLE001 — classified below, re-raised if fatal
                if type(e).__name__ not in _RETRYABLE or attempt == self.max_retries:
                    raise
                time.sleep(delay)
                delay = min(delay * 2, 30.0)
        raise AssertionError("unreachable")

    def _make_client(self) -> _ChatAPI:
        from openai import AzureOpenAI
        return AzureOpenAI(  # type: ignore[return-value]
            azure_endpoint=self.endpoint, api_key=self.api_key,
            api_version=self.api_version,
        ).chat.completions


# --- driver (pure orchestration — CLI owns I/O, tests inject fakes) ------------------

def run_stage2(docs: list[dict[str, Any]], alias_table: dict[str, Any],
               llm_call: Any | None, model: str,
               min_confidence: float = MIN_CONFIDENCE_DEFAULT,
               cache: dict[str, dict[str, Any]] | None = None,
               cache_write: Any | None = None,
               cache_key_fn: Any | None = None) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    """Structure *docs* (sorted by source_file) -> (records, stats).

    *llm_call* maps messages -> parsed LLM JSON (None = rule-based fallback
    for every document). *cache* maps cache keys -> validated LLM outputs and
    *cache_write* persists new entries; *cache_key_fn* maps a Stage 1 record
    to its cache key (CLI wires the deployment-scoped key).
    """
    families = [f["family"] for f in alias_table["families"]]
    lookup = build_product_lookup(alias_table)
    cache = cache if cache is not None else {}
    records: list[dict[str, Any]] = []
    stats = {"n_llm_calls": 0, "n_cache_hits": 0, "n_fallback": 0, "n_llm_errors": 0}
    for rec in sorted(docs, key=lambda d: d["source_file"]):
        source_len = len(_source_text(rec))
        if source_len > MAX_SOURCE_CHARS:
            records.append(mark_review(
                fallback_record(rec, alias_table, lookup, "source_too_long"), min_confidence))
            stats["n_fallback"] += 1
            continue
        if llm_call is None:
            records.append(mark_review(
                fallback_record(rec, alias_table, lookup, "no_llm_mode"), min_confidence))
            stats["n_fallback"] += 1
            continue
        key = cache_key_fn(rec) if cache_key_fn else None
        cached = cache.get(key) if key else None
        if cached is not None:
            try:
                validated = validate_llm_output(cached)
            except ValueError as e:
                records.append(mark_review(
                    fallback_record(rec, alias_table, lookup,
                                    f"cached_output_invalid: {e}"), min_confidence))
                stats["n_fallback"] += 1
                stats["n_llm_errors"] += 1
                continue
            records.append(mark_review(
                build_record(rec, validated, alias_table, lookup, families,
                             model, min_confidence), min_confidence))
            stats["n_cache_hits"] += 1
            continue
        try:
            raw = llm_call(build_messages(rec, families))
            validated = validate_llm_output(raw)
        except Exception as e:  # noqa: BLE001 — one bad doc must not kill the run
            records.append(mark_review(
                fallback_record(rec, alias_table, lookup,
                                f"{type(e).__name__}: {e}"[:300]), min_confidence))
            stats["n_fallback"] += 1
            stats["n_llm_errors"] += 1
            continue
        if key and cache_write:
            cache[key] = validated
            cache_write(key, validated)
        records.append(mark_review(
            build_record(rec, validated, alias_table, lookup, families,
                         model, min_confidence), min_confidence))
        stats["n_llm_calls"] += 1
    records.sort(key=lambda r: r["source_file"])
    return records, stats


def summarize(records: list[dict[str, Any]],
              min_confidence: float = MIN_CONFIDENCE_DEFAULT) -> dict[str, Any]:
    """Counts for ``summary.json`` (deterministic from records)."""
    by_type: dict[str, int] = {}
    for r in records:
        by_type[str(r.get("doc_type"))] = by_type.get(str(r.get("doc_type")), 0) + 1
    reasons: dict[str, int] = {}
    for r in records:
        for reason in review_reasons(r, min_confidence):
            reasons[reason] = reasons.get(reason, 0) + 1
    unresolved: dict[str, int] = {}  # curation signal for the alias worksheet
    for r in records:
        for m in r.get("products_unresolved", []):
            unresolved[m] = unresolved.get(m, 0) + 1
    return {
        "n_documents": len(records),
        "by_doc_type": dict(sorted(by_type.items())),
        "n_review_queue": sum(1 for r in records if r["needs_review"]),
        "review_reasons": dict(sorted(reasons.items())),
        "with_products": sum(1 for r in records if r["products"]),
        "with_currency_cues": sum(1 for r in records if r["currency_cues"]),
        "products_indexed": sorted({pn for r in records for pn in r["products"]}),
        "unresolved_mentions": dict(sorted(unresolved.items(),
                                             key=lambda kv: (-kv[1], kv[0]))),
    }


def rebuild_alias_table(products_path: Path) -> dict[str, Any]:
    """products.json -> alias table (same derivation the ``aliases`` command uses)."""
    return build_alias_table(json.loads(products_path.read_text(encoding="utf-8")))
