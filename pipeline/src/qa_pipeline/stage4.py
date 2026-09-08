"""Stage 4 — deduplication & currency filter (plan §4, Stage 4).

Consumes Stage 2 ``documents.jsonl`` (canonical records) plus the §5 alias
table and stamps every record with its retrieval currency:

- ``cluster_id`` — near-duplicate canonical questions (cosine ≥
  :data:`SIMILARITY_THRESHOLD` on ``question_canonical`` embeddings), compared
  only within §4 buckets (records sharing a part_no or a topic); identical
  question strings merge regardless of bucket. ``null`` for singletons and
  for records without a canonical question (expert notes — there is no
  question to dedup on).
- ``stage4_status`` — ``current`` / ``superseded_dup`` (a newer canonical
  answer exists; ``superseded_by`` names it) / ``superseded_currency`` (the
  answer depends on a product that is gone or replaced). ``is_current`` is
  the §9 filterable boolean derived from it (conflicted-cluster members stay
  ``current`` pending disposition — nothing leaves the index on a proxy).
- ``currency_judgment`` — ``null`` (no cues, or rename-only cues), or the
  gpt-5-mini formulation-independence call for replacement/discontinued cues:
  ``independent`` / ``dependent`` / ``low_confidence`` / ``no_llm`` / error.

Owner rulings baked in (2026-09-07, docs/decisions.md):

- **Renames never supersede.** A rename (LeanMR→LeanMeal and the other
  legacy entries) is an identity mapping — same product, same formula — so a
  rename cue is a dated-*name* signal, not a currency risk. Stage 2 already
  expanded renames to successor part_nos; the cue stays on the record for the
  runtime ("LeanMR, now LeanMeal"). Only replacement cues (a different
  formula took the slot: Recover&Build) and discontinued cues (no successor:
  KidsMV, VeganMV) can supersede, and only when the answer's guidance is
  formulation-*dependent* — an incidental mention in an otherwise general
  answer stays current (LLM-judged, conservative default superseded).
- **Threshold 0.88 is scan-locked, not guessed** (scripts/stage4_cluster_scan.py,
  2026-09-07): at 0.88 every sampled merge is a true duplicate; below 0.86
  distinct questions fuse ("replace" vs "combine" Alln1+ActiveMV at 0.8436),
  and a wrong merge silently removes a distinct answer from the index while a
  wrong miss only leaves a harmless duplicate retrievable.

Conflict routing (§4 step 3): a cluster whose members' part_no sets are
non-nested "materially disagree" by proxy — deterministic code cannot judge
prose — so the cluster is routed to the review queue *instead of auto-picking*
(no member is dedup-superseded pending disposition; a member's own currency
supersession still stands). Once the owner rules on a queued cluster the ruling
lands in :data:`CURATED_CLUSTER_DISPOSITIONS` and the cluster stops being
queued; ``split`` (owner ruling 2026-09-08, the corpus's only conflict) means
the members are distinct questions, so no member ever supersedes another and
both stay retrievable. The ruling pins its exact membership: if the cluster
reshapes or stops forming, Stage 4 raises rather than re-applying a ruling
nobody made for it (only a whole-corpus run can prove a ruling stale, so the
CLI passes ``strict_dispositions`` off for ``--include`` / ``--limit`` runs,
and it is off by default for library callers). A deterministic 5% audit sample
of auto-resolved clusters joins the queue (§4 Stage 3 audit precedent), and
every multi-member cluster lands in the committed ``clusters.jsonl``
worksheet — the session record for owner review, the alias-worksheet
precedent.

Determinism: outputs carry no timestamps and no vectors (vectors are API
results cached in the gitignored ``runs/embeddings.jsonl``; judgments cache in
``runs/stage4_cache.jsonl`` keyed deployment|api-version|prompt|record).
Records sort by ``source_file``; clusters and worksheet sort by ``cluster_id``
(= min member record id — stable, never a counter). Reruns on the same caches
are byte-identical. Embeddings use ``question_canonical`` alone — a different
surface than the index's ``title + content``, so the two caches never alias.
"""

from __future__ import annotations

import hashlib
import math
from typing import Any, Callable

from .stage2 import is_audit_sample

# --- tunables (curation lives in constants — AGENTS.md) ----------------------

SIMILARITY_THRESHOLD = 0.88  # scan-locked 2026-09-07 (see module docstring)
MIN_JUDGE_CONFIDENCE_DEFAULT = 0.7  # below -> superseded (default) + review
AUDIT_RATE = 0.05  # deterministic audit sample of auto-resolved clusters
CURRENCY_PROMPT_VERSION = "1.0.0"  # part of the cache key
STATUS_CURRENT = "current"
STATUS_SUPERSEDED_DUP = "superseded_dup"
STATUS_SUPERSEDED_CURRENCY = "superseded_currency"


# --- currency cue classification ----------------------------------------------

def classify_cues(cues: list[str], alias_table: dict[str, Any]) -> dict[str, list[str]]:
    """Split *cues* by alias-table section: renames vs gone-or-replaced.

    A cue the alias table does not know raises: currency cues are emitted by
    ``detect_currency_cues`` from the same table, so an unknown name means the
    two builds disagree and must not be silently resolved (AGENTS.md).
    """
    renames = {r["deprecated"] for r in alias_table.get("legacy_renames", [])}
    gone = ({r["deprecated"] for r in alias_table.get("replacements", [])}
            | {d["name"] for d in alias_table.get("discontinued", [])})
    out: dict[str, list[str]] = {"rename": [], "gone_or_replaced": []}
    for cue in cues:
        if cue in renames:
            out["rename"].append(cue)
        elif cue in gone:
            out["gone_or_replaced"].append(cue)
        else:
            raise ValueError(
                f"unknown currency cue {cue!r} — not in the alias table's "
                "rename/replacement/discontinued sections; rebuild with the "
                "same alias table that produced the Stage 2 records")
    return out


def cue_descriptions(cues: list[str], alias_table: dict[str, Any]) -> list[str]:
    """One-line human description per gone-or-replaced cue, for the judge."""
    out: list[str] = []
    for rep in alias_table.get("replacements", []):
        if rep["deprecated"] in cues:
            line = f"{rep['deprecated']} — replaced by {rep['successor_family']}, a DIFFERENT formula"
            out.append(line + (f" ({rep['note']})" if rep.get("note") else ""))
    for disc in alias_table.get("discontinued", []):
        if disc["name"] in cues:
            line = f"{disc['name']} — discontinued, no successor"
            out.append(line + (f" ({disc['note']})" if disc.get("note") else ""))
    return sorted(out)


# --- LLM contract ---------------------------------------------------------------

CURRENCY_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "formulation_dependent": {
            "type": "boolean",
            "description": "True when the ANSWER's guidance depends on the "
                           "gone/replaced product — it recommends, doses, or "
                           "describes that product's formulation/effects, or "
                           "its availability/what replaced it in product-"
                           "specific terms. False when the mention is "
                           "incidental and the guidance stands on its own.",
        },
        "evidence": {
            "type": "string",
            "description": "Short quote or precise reference from the answer "
                           "supporting the decision.",
        },
        "confidence": {
            "type": "number",
            "description": "Self-assessed certainty, 0-1.",
        },
    },
    "required": ["formulation_dependent", "evidence", "confidence"],
    "additionalProperties": False,
}

CURRENCY_SYSTEM_PROMPT = (
    "You judge whether an archived expert answer is still safe to serve as "
    "current guidance. The customer thread mentions a product that is gone: "
    "either discontinued with no successor, or replaced by a DIFFERENT "
    "formula. Renamed products never reach you (same product, new name). "
    "Judge the ANSWER text only, not the question. formulation_dependent is "
    "true when the answer's guidance depends on that specific product — it "
    "recommends taking it, doses it, describes its formula/macros/effects/"
    "flavors, says where to get it, or explains in product-specific terms "
    "what to use instead. It is false when the mention is incidental (the "
    "customer name-drops it; the expert answers the general question with "
    "guidance that stands without the product — diet, training, timing, "
    "third-party options). evidence quotes or precisely references the "
    "decisive answer span. When genuinely unclear, still decide and set a "
    "low confidence — a human reviews those."
)


def currency_messages(rec: dict[str, Any], cues: list[str],
                      alias_table: dict[str, Any]) -> list[dict[str, str]]:
    """System + user messages for one formulation-independence judgment."""
    gone = classify_cues(cues, alias_table)["gone_or_replaced"]
    lines = "\n".join(f"- {d}" for d in cue_descriptions(gone, alias_table))
    user = (
        "GONE/REPLACED PRODUCT(S) MENTIONED IN THIS THREAD:\n"
        f"{lines}\n\n"
        f"QUESTION (may be empty for expert notes):\n"
        f"{rec.get('question_canonical') or '—'}\n\n"
        f"ANSWER (scrubbed expert text — judge this only):\n{rec.get('answer') or '—'}"
    )
    return [{"role": "system", "content": CURRENCY_SYSTEM_PROMPT},
            {"role": "user", "content": user}]


def validate_judgment(raw: dict[str, Any]) -> dict[str, Any]:
    """Coerce/validate an LLM (or cached) judgment; ValueError when unusable."""
    if not isinstance(raw, dict):
        raise ValueError("judgment is not an object")
    dep = raw.get("formulation_dependent")
    if not isinstance(dep, bool):
        raise ValueError("formulation_dependent is not a boolean")
    evidence = raw.get("evidence")
    if not isinstance(evidence, str) or not evidence.strip():
        raise ValueError("evidence is missing/empty")
    conf = raw.get("confidence")
    if isinstance(conf, bool) or not isinstance(conf, (int, float)):
        raise ValueError("confidence is not a number")
    conf = float(conf)
    if not 0.0 <= conf <= 1.0:
        raise ValueError("confidence out of range")
    return {"formulation_dependent": dep,
            "evidence": evidence.strip(),
            "confidence": round(conf, 4)}


# --- clustering -------------------------------------------------------------------

def normalize_vector(vec: list[float]) -> list[float]:
    norm = math.sqrt(math.sumprod(vec, vec))
    return [x / norm for x in vec]


def share_bucket(a: dict[str, Any], b: dict[str, Any]) -> bool:
    """§4 bucket rule: same product or same topic (case-insensitive)."""
    if set(a.get("products") or []) & set(b.get("products") or []):
        return True
    return bool({t.casefold() for t in (a.get("topics") or [])}
                & {t.casefold() for t in (b.get("topics") or [])})


def cluster_questions(records: list[dict[str, Any]],
                      vectors: dict[str, list[float]],
                      threshold: float = SIMILARITY_THRESHOLD,
                      ) -> list[list[dict[str, Any]]]:
    """Near-duplicate clusters (≥2 members) from question embeddings.

    *records* must be the question-bearing records in ``source_file`` order;
    *vectors* maps ``question_canonical`` -> raw vector. Deterministic:
    union-find always merges into the smaller root, ``cluster_id`` is the min
    member record id, members sort by (thread_date, source_file).
    """
    order = sorted(records, key=lambda r: r["source_file"])
    norm = {q: normalize_vector(v) for q, v in vectors.items()}
    parent = {r["id"]: r["id"] for r in order}

    def find(x: str) -> str:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for i, a in enumerate(order):
        for b in order[i + 1:]:
            same_text = a["question_canonical"] == b["question_canonical"]
            if not same_text and not share_bucket(a, b):
                continue
            sim = math.sumprod(norm[a["question_canonical"]],
                               norm[b["question_canonical"]])
            if sim >= threshold:
                ra, rb = find(a["id"]), find(b["id"])
                if ra != rb:
                    parent[max(ra, rb)] = min(ra, rb)

    groups: dict[str, list[dict[str, Any]]] = {}
    for r in order:
        groups.setdefault(find(r["id"]), []).append(r)
    clusters = []
    for members in groups.values():
        if len(members) >= 2:
            members.sort(key=lambda r: (r.get("thread_date") or "",
                                        r["source_file"]))
            clusters.append(members)
    clusters.sort(key=lambda ms: min(m["id"] for m in ms))
    return clusters


def pick_canonical(members: list[dict[str, Any]]) -> dict[str, Any]:
    """§4 step 2: newest wins; null dates lose (empty key); ties break on
    source_file (lexicographic max — deterministic, documented)."""
    return max(members, key=lambda r: (r.get("thread_date") or "",
                                       r["source_file"]))


def products_conflict(members: list[dict[str, Any]]) -> bool:
    """§4 step 3 proxy for 'materially disagree': non-nested part_no sets.

    Empty product sets nest with anything (an untagged dup is still a dup).
    Prose contradiction is not deterministically checkable — that is what the
    audit sample and the worksheet are for.
    """
    sets = [set(m.get("products") or []) for m in members]
    for i, a in enumerate(sets):
        for b in sets[i + 1:]:
            if not (a <= b or b <= a):
                return True
    return False


DISPOSITION_SPLIT = "split"

# Owner dispositions for queued clusters (§4 step 3; docs/decisions.md). Keyed
# by ``cluster_id`` (= min member id); ``members`` pins the exact membership
# the ruling was made on, so a corpus change that reshapes the cluster raises
# rather than silently re-applying a ruling nobody made for it.
CURATED_CLUSTER_DISPOSITIONS: dict[str, dict[str, Any]] = {
    # 2026-09-08, open item 7 — the corpus's only cluster_conflict. The two
    # records are consecutive turns of ONE email thread: 8be45e86 is the
    # webform enquiry (why FirstString, 1 g protein per lb LBM, whether the
    # pre-workout serving is mandatory), and 79c66301 is the same customer's
    # follow-up ("what else with FirstString") answered with creatine + the
    # Level 1 plan, quoting the whole prior reply beneath it. Stage 1 keeps
    # only the *new* expert reply as the answer, so the newer record is a
    # delta, not a superset: superseding the older one would drop the only
    # direct answer to the pre-workout half while leaving that clause standing
    # in the newer record's canonical question. The non-nested part_nos are an
    # artifact of the two boilerplate blocks (the older enumerates MVs by
    # demographic, hence 1007 Women's MV; the newer names ActiveMV) — the
    # answers never contradict each other. Split: both stay retrievable,
    # neither supersedes the other.
    "79c663016afc2345": {
        "disposition": DISPOSITION_SPLIT,
        "members": ["79c663016afc2345", "8be45e86eb76c09f"],
        "source": "owner ruling 2026-09-08 (Stage 4 queue, open item 7)",
    },
}


def disposition_for(cluster_id: str,
                    members: list[dict[str, Any]]) -> str | None:
    """Curated owner disposition for *cluster_id*, or None if unruled.

    Raises if the cluster still forms but with different members than the
    ruling was made on — corpus attestation, the alias-table rule: a curated
    entry that no longer describes reality must fail loudly.
    """
    entry = CURATED_CLUSTER_DISPOSITIONS.get(cluster_id)
    if entry is None:
        return None
    ruled = list(entry["members"])
    actual = sorted(m["id"] for m in members)
    if ruled != actual:
        raise ValueError(
            f"Stage 4 cluster {cluster_id} was dispositioned for members "
            f"{ruled} but now clusters {actual}; the owner ruling no longer "
            "covers it — re-read the members and re-rule.")
    return str(entry["disposition"])


# --- record stamping ---------------------------------------------------------------

def _stamp(rec: dict[str, Any], **fields: Any) -> dict[str, Any]:
    out = dict(rec)
    out.update(fields)
    return out


def run_stage4(records: list[dict[str, Any]], alias_table: dict[str, Any],
               embed_call: Callable[[list[str]], list[list[float]]],
               judge_call: Any | None, judge_model: str,
               threshold: float = SIMILARITY_THRESHOLD,
               min_judge_confidence: float = MIN_JUDGE_CONFIDENCE_DEFAULT,
               audit_rate: float = AUDIT_RATE,
               strict_dispositions: bool = False,
               cache: dict[str, dict[str, Any]] | None = None,
               cache_write: Any | None = None,
               cache_key_fn: Any | None = None,
               ) -> tuple[list[dict[str, Any]], list[dict[str, Any]],
                          list[dict[str, Any]], dict[str, Any]]:
    """Stamp every record; -> (records, cluster worksheet, review, stats).

    *embed_call* maps unique question texts -> vectors (CLI wires the cached
    ``Embedder``; tests inject fakes). *judge_call* maps messages -> parsed
    judgment JSON (None = ``--no-llm`` conservative fallback: every
    gone-or-replaced cue superscedes + queues). The CLI owns all I/O.
    """
    records = sorted(records, key=lambda r: r["source_file"])
    cache = cache if cache is not None else {}
    stats = {"n_judge_calls": 0, "n_judge_cache_hits": 0,
             "n_judge_errors": 0, "n_judged": 0, "n_no_llm": 0}

    # -- currency pass (per record, before clustering: canonical pick needs it)
    stamped: list[dict[str, Any]] = []
    review: dict[str, list[str]] = {}
    for rec in records:
        cues = rec.get("currency_cues") or []
        classes = classify_cues(cues, alias_table)
        judgment: str | None = None
        superseded = False
        evidence: str | None = None
        error: str | None = None
        if classes["gone_or_replaced"]:
            key = cache_key_fn(rec) if cache_key_fn else None
            raw: dict[str, Any] | None = None
            cached = cache.get(key) if key else None
            if cached is not None:
                try:
                    raw = validate_judgment(cached)
                    stats["n_judge_cache_hits"] += 1
                except ValueError:
                    # corrupt cache entry: re-judge live, then overwrite it
                    stats["n_judge_errors"] += 1
            if raw is None and judge_call is not None:
                try:
                    raw = validate_judgment(
                        judge_call(currency_messages(rec, cues, alias_table)))
                    stats["n_judge_calls"] += 1
                    if key and cache_write:
                        cache[key] = raw
                        cache_write(key, raw)
                except Exception as e:  # noqa: BLE001 — one bad doc must not kill the run
                    stats["n_judge_errors"] += 1
                    error = f"{type(e).__name__}: {e}"[:300]
            if raw is not None:
                stats["n_judged"] += 1
                evidence = raw["evidence"]
                if raw["confidence"] < min_judge_confidence:
                    judgment, superseded = "low_confidence", True
                    review.setdefault(rec["id"], []).append(
                        "currency_low_confidence")
                elif raw["formulation_dependent"]:
                    judgment, superseded = "dependent", True
                else:
                    judgment, superseded = "independent", False
            else:
                # no usable judgment (no-llm mode or hard error): the §4
                # conservative default — superseded unless proven independent
                # — plus a queue reason so a disposition can flip it.
                superseded = True
                if judge_call is None:
                    judgment = "no_llm"
                    review.setdefault(rec["id"], []).append("currency_no_llm")
                    stats["n_no_llm"] += 1
                else:
                    judgment = "error"
                    review.setdefault(rec["id"], []).append("currency_error")
        stamped.append(_stamp(
            rec, cluster_id=None, stage4_status=(
                STATUS_SUPERSEDED_CURRENCY if superseded else STATUS_CURRENT),
            superseded_by=None, currency_judgment=judgment,
            currency_evidence=evidence, currency_error=error,
            stage4_needs_review=False, stage4_review_reasons=[]))

    # -- clustering (question-bearing records only)
    q_records = [r for r in stamped
                 if (r.get("question_canonical") or "").strip()]
    texts = sorted({r["question_canonical"] for r in q_records})
    vectors = dict(zip(texts, embed_call(texts)))
    stats["n_unique_questions"] = len(texts)
    clusters = cluster_questions(q_records, vectors, threshold)

    by_id = {r["id"]: r for r in stamped}
    worksheet: list[dict[str, Any]] = []
    ruled_clusters: set[str] = set()
    for members in clusters:
        cluster_id = min(m["id"] for m in members)
        conflict = products_conflict(members)
        disposition = disposition_for(cluster_id, members)
        if disposition is not None:
            ruled_clusters.add(cluster_id)
        audit = (is_audit_sample(cluster_id, audit_rate)
                 and not conflict and disposition is None)
        for m in members:
            m["cluster_id"] = cluster_id
        canonical = None
        if not conflict and disposition != DISPOSITION_SPLIT:
            candidates = [m for m in members
                          if m["stage4_status"] == STATUS_CURRENT]
            if candidates:
                canonical = pick_canonical(candidates)
                for m in candidates:
                    if m["id"] != canonical["id"]:
                        m["stage4_status"] = STATUS_SUPERSEDED_DUP
                        m["superseded_by"] = canonical["id"]
        reasons = (["cluster_conflict"]
                   if conflict and disposition is None else []) + \
                  (["cluster_audit"] if audit else [])
        if reasons:
            for m in members:
                m["stage4_review_reasons"] = sorted(
                    set(m.get("stage4_review_reasons", [])) | set(reasons))
        worksheet.append({
            "cluster_id": cluster_id,
            "size": len(members),
            "conflict": conflict,
            "audit": audit,
            "disposition": disposition,
            "canonical": ({"id": canonical["id"],
                           "source_file": canonical["source_file"],
                           "thread_date": canonical.get("thread_date")}
                          if canonical else None),
            "members": [{"id": m["id"], "source_file": m["source_file"],
                         "thread_date": m.get("thread_date"),
                         "stage4_status": m["stage4_status"],
                         "superseded_by": m.get("superseded_by"),
                         "products": m.get("products") or []}
                        for m in members],
        })
    if strict_dispositions:
        stale = sorted(set(CURATED_CLUSTER_DISPOSITIONS) - ruled_clusters)
        if stale:
            raise ValueError(
                "stale Stage 4 cluster disposition(s) — no such cluster in "
                f"this run: {', '.join(stale)}. The ruled cluster no longer "
                "forms; re-read the records and re-rule rather than deleting "
                "the entry silently.")
    stats["n_clusters"] = len(clusters)
    stats["n_conflict_clusters"] = sum(1 for w in worksheet if w["conflict"])
    stats["n_audit_clusters"] = sum(1 for w in worksheet if w["audit"])
    stats["n_dispositioned_clusters"] = sum(
        1 for w in worksheet if w["disposition"] is not None)

    # -- finalize review flags + the §9 contract boolean
    for r in stamped:
        reasons = sorted(set(r.get("stage4_review_reasons", []))
                         | set(review.get(r["id"], [])))
        r["stage4_review_reasons"] = reasons
        r["stage4_needs_review"] = bool(reasons)
        r["is_current"] = r["stage4_status"] == STATUS_CURRENT
    review_rows = [{"id": r["id"], "source_file": r["source_file"],
                    "cluster_id": r.get("cluster_id"),
                    "reasons": r["stage4_review_reasons"]}
                   for r in stamped if r["stage4_needs_review"]]
    review_rows.sort(key=lambda x: x["source_file"])
    worksheet.sort(key=lambda w: w["cluster_id"])
    stamped.sort(key=lambda r: r["source_file"])
    return stamped, worksheet, review_rows, stats


def summarize(records: list[dict[str, Any]],
              worksheet: list[dict[str, Any]]) -> dict[str, Any]:
    """Counts for ``summary.json`` (deterministic from outputs)."""
    statuses: dict[str, int] = {}
    judgments: dict[str, int] = {}
    reasons: dict[str, int] = {}
    for r in records:
        s = str(r.get("stage4_status"))
        statuses[s] = statuses.get(s, 0) + 1
        j = r.get("currency_judgment")
        if j is not None:
            judgments[j] = judgments.get(j, 0) + 1
        for reason in r.get("stage4_review_reasons", []):
            reasons[reason] = reasons.get(reason, 0) + 1
    return {
        "n_documents": len(records),
        "by_stage4_status": dict(sorted(statuses.items())),
        "n_current": statuses.get(STATUS_CURRENT, 0),
        "by_currency_judgment": dict(sorted(judgments.items())),
        "n_clusters": len(worksheet),
        "n_conflict_clusters": sum(1 for w in worksheet if w["conflict"]),
        "n_dispositioned_clusters": sum(
            1 for w in worksheet if w.get("disposition") is not None),
        "n_audit_clusters": sum(1 for w in worksheet if w["audit"]),
        "n_review_queue": sum(1 for r in records if r["stage4_needs_review"]),
        "review_reasons": dict(sorted(reasons.items())),
    }


def currency_cache_key(deployment: str, api_version: str,
                       rec: dict[str, Any]) -> str:
    """Stable key: deployment|api-version|prompt|record (input change misses)."""
    raw = "|".join([
        deployment, api_version, CURRENCY_PROMPT_VERSION,
        rec["id"], rec.get("question_canonical") or "",
        rec.get("answer") or "",
        ",".join(sorted(rec.get("currency_cues") or [])),
    ]).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()
