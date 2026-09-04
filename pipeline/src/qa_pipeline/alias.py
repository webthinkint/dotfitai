"""Alias table builder (plan §5): products.json -> versioned alias table.

Two layers, both versioned in code (the emitted JSON is a derived artifact):

1. **Deterministic derivation** from ``products.json``:
   - family name = longname with trailing `` - <variant>`` segments stripped
     (``AminoFormula - Blue Raspberry`` -> ``AminoFormula``)
   - legacy names from ``(formerly X)`` markers (``LeanMR``, ``Muscle
     Defender L-Glutamine``) — these drive the Stage 4 currency rules
2. **Curated overlay** (decisions confirmed 2026-09-01, see docs/progress.md):
   - WheySmooth universe (incl. All Natural + BULK lines) = ONE family
   - dotBARs = one family (flavor variations); dotWAFER separate
   - Creatine Monohydrate drink mix + unflavored = one family;
     Creatine Complex separate
   - MVs (Active / Women's / Over 50) = three separate products
   - shaker bottles + SportMixer = gear, excluded from the alias table
   - ``NO7 Rage -> NO7 PreWorkout`` legacy rename (not marked in products.json)
   - replacements (Recover&Build -> AminoFormula) are kept OUT of
     ``legacy_renames``: a rename is an identity mapping, a replacement is a
     different formula, and only the first may expand to the successor's
     part_nos
   - the planned "Reformulated with Careflow (2025)" currency cue was DROPPED
     (absent from LeanMeal product copy; confirmed 2026-09-01)

Name matching downstream is normalization-based (``norm``), so spacing/casing
variants — ``First String`` vs ``FirstString``, ``Active MV`` vs ``ActiveMV``,
``Whey Smooth`` vs ``WheySmooth`` — need no explicit alias entries. Only true
abbreviations (AF, SB, FS, WLLS, MVM, …) require curation; those are harvested
as *candidates* into a worksheet (open item #3) and enter the curated overlay
only after the support-lead session confirms them.

**Application policy** (binding for every consumer of this table): aliases
drive (a) index metadata tags (``products`` field), (b) query-side expansion,
(c) Stage 4 currency flags. **Corpus text is never rewritten** — a find-and-
replace pass would corrupt dual-usage tokens (e.g. PP = "protein powders" in
most docs) and break Stage 2's source-containment/traceability contract.
Ambiguous tokens get no deterministic rule; they are resolved contextually by
the Stage 2 LLM (unsure cases → Stage 3 review queue).

Outputs (deterministic, no timestamps — byte-identical reruns):
    processed/aliases/alias_table.json       authoritative table
    processed/aliases/curation_worksheet.md  candidate abbreviations + evidence
"""

from __future__ import annotations

import json
import re
import unicodedata
from pathlib import Path

from .io_utils import write_json, write_text

ALIAS_TABLE_VERSION = "1.2.0"

# --- curated overlay ---------------------------------------------------------

# part_no -> family name (overrides the strip-suffix derivation)
CURATED_FAMILIES: dict[int, str] = {
    # WheySmooth universe: core + All Natural + BULK lines = one family
    1369: "WheySmooth", 1370: "WheySmooth", 1399: "WheySmooth",
    1374: "WheySmooth", 1375: "WheySmooth",
    1391: "WheySmooth", 1392: "WheySmooth",
    # dotBARs: one family of flavor variations
    1456: "dotBAR", 1457: "dotBAR", 1462: "dotBAR",
    1480: "dotBAR", 1482: "dotBAR",
}

# family names that must come out of base derivation (asserted, not overridden)
EXPECTED_FAMILIES: dict[str, list[int]] = {
    "Creatine Monohydrate": [1200, 1227],      # drink mix + unflavored
    "Active MV": [1005],                        # MVs are separate products
    "Women's MV": [1007],
    "Over 50 MV": [1009],
}

# legacy renames not derivable from products.json (deprecated -> family).
# 2026-09-01 (4): six PDSRG-product dispositions from the support lead
# (SuperiorAntioxidant -> Antioxidant resolved once sku 1000 was added to
# products.json the same day).
CURATED_LEGACY: list[dict] = [
    {"deprecated": "NO7 Rage", "family": "NO7 PreWorkout",
     "source": "curated (confirmed 2026-09-01)"},
    {"deprecated": "SuperiorAntioxidant", "family": "Antioxidant",
     "source": "support lead (PDSRG dispositions, 2026-09-01): renamed"},
    {"deprecated": "UltraProbiotic", "family": "Probiotics",
     "source": "support lead (PDSRG dispositions, 2026-09-01): renamed"},
    {"deprecated": "ExtremeCreatineXXXL", "family": "Creatine Complex",
     "source": "support lead (PDSRG dispositions, 2026-09-01): renamed"},
    {"deprecated": "CreatineXXL", "family": "Creatine Complex",
     "source": "formerly-marker in the PDSRG title (ExtremeCreatineXXXL "
               "(formerly CreatineXXL)); corpus-attested 'creatine XXL' "
               "(5 docs) — same rename chain, same target"},
    {"deprecated": "JointSkinCollagen+", "family": "CollagenComplex",
     "source": "support lead (PDSRG dispositions, 2026-09-01): renamed"},
    {"deprecated": "JointFlexPlus", "family": "CollagenComplex",
     "source": "formerly-marker in the PDSRG title (JointSkinCollagen+ "
               "(formerly JointFlexPlus)); corpus-attested (4 docs) — "
               "same rename chain, same target"},
    {"deprecated": "AdvancedBrainHealth", "family": "Brain Health",
     "source": "support lead (PDSRG dispositions, 2026-09-01): renamed"},
]

# Replacements are NOT renames. A rename is an identity mapping — the old
# name and the new name are the same product, so expanding the alias to the
# current part_nos is correct. A replacement is a different formula that took
# over the slot: expanding it would answer a question about the old product
# with the new product's claims. They live in their own section (rather than
# behind a flag on legacy_renames) so no consumer can treat the two alike by
# reading part_nos without checking a discriminator.
CURATED_REPLACEMENTS: list[dict] = [
    {"deprecated": "Recover&Build", "successor_family": "AminoFormula",
     "note": "discontinued and replaced by AminoFormula — a DIFFERENT "
             "formula. Currency cue and query-side redirect only; never a "
             "product tag (its PDSRG chunks tag no part_nos).",
     "source": "support lead (PDSRG dispositions, 2026-09-01)"},
]

# products with no successor: names the runtime must recognize as gone
# (query rewrite / "what happened to X" answers). No family link — these
# never resolve to part_nos.
DISCONTINUED_PRODUCTS: list[dict] = [
    {"name": "KidsMV",
     "note": "discontinued — older kids/teens: Active MV; younger kids: "
             "third-party ChildLife Nutrition, Children's Multi Vitamin & "
             "Mineral"},
    {"name": "VeganMV", "note": "discontinued"},
]

# The "(formerly X)" marker text is not always the form the corpus uses.
# Map derived marker text -> corpus-attested core used for matching/display.
# (Verified 2026-09-01: 19 docs write MuscleDefender / muscle defender /
# Muscle Defender — never the full marker form — so the unmodified alias
# would have matched nothing.)
LEGACY_NAME_OVERRIDES: dict[str, str] = {
    "Muscle Defender L-Glutamine": "MuscleDefender",
}

# gear part_nos: excluded from the alias table entirely
GEAR_PART_NOS: list[int] = [1611, 1612, 1630, 1631, 1646]

# candidate abbreviations for the curation worksheet (open item #3).
# norm() covers spelling variants; these are true abbreviations that need
# human confirmation before they join CURATED_ALIASES.
# recommendation: "tag" (deterministic auto-tag safe — every hit maps here),
# "context-only" (dual/generic usage — Stage 2 LLM resolves per doc),
# "no mapping" (drop the token).
SEED_ABBREVIATIONS: dict[str, dict] = {
    "AF": {"target": "AminoFormula", "recommendation": "tag"},
    "SB": {"target": "Alln1 SuperBlend", "recommendation": "tag"},
    "FS": {"target": "First String", "recommendation": "tag"},
    "WLLS": {"target": "WeightLoss & LiverSupport", "recommendation": "tag"},
    "MVM": {"target": "resolve by audience context (Women's MV / Over 50 MV / "
                      "Active MV); unclear -> topic-level only",
            "recommendation": "context-only"},  # tag reconsidered & rejected
    "PP": {"target": "Pre & Post Workout Formula OR 'protein powders' (generic — "
                      "evidence favors the latter)",
            "recommendation": "context-only"},
}

# Confirmed deterministic aliases (curation session 2026-09-01; open item #3).
# "tag" outcomes: deterministic metadata tagging approved. Matching rule is
# the same uppercase word-boundary regex the harvest used (corpus attests
# capitalized usage). Targets resolve to family part_nos at build time.
CURATED_ALIASES: dict[str, dict] = {
    "AF": {"family": "AminoFormula"},
    "SB": {"family": "Alln1 SuperBlend"},
    "FS": {"family": "First String"},
    "WLLS": {"family": "WeightLoss & LiverSupport"},
}

# context-only outcomes stay OUT of the deterministic table: Stage 2 LLM
# resolves them per document (unsure cases -> Stage 3 review queue).
CONTEXT_ONLY_TOKENS: dict[str, str] = {
    "PP": "'protein powders' (generic) vs Pre & Post Workout Formula — "
          "dual usage proven in corpus evidence",
    "MVM": "generic 'multivitamin and mineral formula'. MVs are distinct "
           "products chosen by audience — resolve contextually: women's "
           "health -> Women's MV, age 50+ -> Over 50 MV, general/athlete "
           "-> Active MV, kids/youth -> KidsMV is DISCONTINUED (teens: Active "
           "MV; younger kids: third-party guidance in the alias table's "
           "discontinued section — tag nothing); unclear -> topics=[multivitamin] "
           "with NO product tag (never all three part_nos)",
}

FORMERLY_RE = re.compile(r"\((formerly [^)]+)\)", re.IGNORECASE)


# --- normalization -----------------------------------------------------------

def norm(s: str) -> str:
    """Canonical match key: NFKC, casefold, strip non-alphanumerics."""
    s = unicodedata.normalize("NFKC", s).casefold()
    return re.sub(r"[^a-z0-9]+", "", s)


def strip_variant_suffix(longname: str) -> str:
    """``Family - Variant`` -> ``Family`` (repeatedly, for multi-segment names)."""
    name = longname.strip()
    # drop a trailing "(formerly ...)" marker first; its content is captured
    # separately as a legacy name
    name = FORMERLY_RE.sub("", name).strip()
    while " - " in name:
        name = name.rsplit(" - ", 1)[0].strip()
    return name


# --- table build -------------------------------------------------------------

def _part_no(p: dict) -> int:
    """products.json stores part_no as a string; canonicalize to int."""
    return int(p["part_no"])


def build_alias_table(products: list[dict]) -> dict:
    if not products:
        raise ValueError("products list is empty")

    by_part_no = {}
    for p in products:
        pn = _part_no(p)
        if pn in by_part_no:
            raise ValueError(f"duplicate part_no {pn}")
        by_part_no[pn] = p

    gear = set(GEAR_PART_NOS)
    present = set(by_part_no)
    families: dict[str, dict] = {}

    for p in sorted(products, key=_part_no):
        pn, longname = _part_no(p), p["longname"]
        if pn in gear:
            continue

        family = CURATED_FAMILIES.get(pn) or strip_variant_suffix(longname)
        fam = families.setdefault(
            family,
            {"family": family, "part_nos": [], "aliases": set(),
             "legacy_names": {}, "first_part_no": pn},
        )
        if pn < fam["first_part_no"]:
            fam["first_part_no"] = pn
        fam["part_nos"].append(pn)

        for legacy in FORMERLY_RE.findall(longname):
            # "(formerly LeanMR)" -> "LeanMR"
            deprecated = legacy[len("formerly "):].strip()
            source = "products.json (formerly marker)"
            if deprecated in LEGACY_NAME_OVERRIDES:
                deprecated = LEGACY_NAME_OVERRIDES[deprecated]
                source += "; corpus-attested form"
            fam["legacy_names"][norm(deprecated)] = deprecated
            fam["legacy_sources"] = fam.get("legacy_sources", {})
            fam["legacy_sources"][norm(deprecated)] = source

    # curated legacy renames resolve against derived family names
    legacy_renames = []
    for fam in sorted(families.values(), key=lambda f: f["family"]):
        for key in sorted(fam["legacy_names"]):
            legacy_renames.append({
                "deprecated": fam["legacy_names"][key],
                "current_family": fam["family"],
                "part_nos": fam["part_nos"],
                "source": fam.get("legacy_sources", {}).get(
                    key, "products.json (formerly marker)"),
            })
    for entry in CURATED_LEGACY:
        fam = families.get(entry["family"])
        if fam is None:
            raise ValueError(
                f"curated legacy rename targets unknown family "
                f"{entry['family']!r}")
        legacy_renames.append({
            "deprecated": entry["deprecated"],
            "current_family": entry["family"],
            "part_nos": fam["part_nos"],
            "source": entry["source"],
        })

    replacements = []
    for entry in CURATED_REPLACEMENTS:
        fam = families.get(entry["successor_family"])
        if fam is None:
            raise ValueError(
                f"curated replacement targets unknown family "
                f"{entry['successor_family']!r}")
        replacements.append({
            "deprecated": entry["deprecated"],
            "successor_family": entry["successor_family"],
            "successor_part_nos": sorted(fam["part_nos"]),
            "note": entry["note"],
            "source": entry["source"],
        })

    # assert the base derivation where no override should be needed
    for expected_family, pns in EXPECTED_FAMILIES.items():
        for pn in pns:
            actual = CURATED_FAMILIES.get(pn) or strip_variant_suffix(
                by_part_no[pn]["longname"])
            if actual != expected_family:
                raise ValueError(
                    f"expected part_no {pn} to derive family "
                    f"{expected_family!r}, got {actual!r} — update "
                    f"CURATED_FAMILIES or the derivation rule")

    family_records = [
        {
            "family": fam["family"],
            "canonical_part_no": fam["first_part_no"],
            "part_nos": sorted(fam["part_nos"]),
            "n_variants": len(fam["part_nos"]),
        }
        for fam in sorted(families.values(), key=lambda f: f["family"])
    ]

    # confirmed abbreviation aliases -> flat list of tag rules for downstream
    deterministic_aliases = []
    for token, cfg in sorted(CURATED_ALIASES.items()):
        if "family" in cfg:
            fam = families.get(cfg["family"])
            if fam is None:
                raise ValueError(
                    f"CURATED_ALIASES[{token!r}] targets unknown family "
                    f"{cfg['family']!r}")
            deterministic_aliases.append({
                "token": token, "family": cfg["family"],
                "part_nos": sorted(fam["part_nos"]),
                "source": "curation session 2026-09-01",
            })
        else:
            unknown = sorted(set(cfg["part_nos"]) - present - gear)
            if unknown:
                raise ValueError(
                    f"CURATED_ALIASES[{token!r}] part_nos not in (non-gear) "
                    f"products: {unknown}")
            entry = {
                "token": token,
                "part_nos": sorted(cfg["part_nos"]),
                "source": "curation session 2026-09-01",
            }
            if cfg.get("note"):
                entry["note"] = cfg["note"]
            deterministic_aliases.append(entry)

    return {
        "version": ALIAS_TABLE_VERSION,
        "source": "data/Product Data/products.json",
        "normalization": "NFKC, casefold, strip non-alphanumeric",
        "n_products": len(products),
        # gear is excluded from families, so the ratio that matters for
        # retrieval is n_products_indexed -> n_families
        "n_products_indexed": len(products) - len(gear & present),
        "n_families": len(family_records),
        "families": family_records,
        "deterministic_aliases": deterministic_aliases,
        "context_only_tokens": dict(sorted(CONTEXT_ONLY_TOKENS.items())),
        "legacy_renames": legacy_renames,
        "replacements": replacements,
        "discontinued": [dict(d) for d in DISCONTINUED_PRODUCTS],
        "excluded": {
            "gear": sorted(gear & present),
            "note": "shaker bottles / SportMixer — not indexed as supplement products",
        },
    }


# --- QA-corpus harvest (candidates only — worksheet, not the table) ----------

def _tolerant_pattern(name: str) -> re.Pattern:
    """Case/spacing/punctuation-tolerant regex matching *name* in raw text."""
    parts = [re.escape(ch) for ch in name if ch.isalnum()]
    return re.compile(r"[^A-Za-z0-9]*".join(parts), re.IGNORECASE)


def harvest_candidates(alias_table: dict, docs_path: Path) -> list[dict]:
    """Count seed-abbreviation + legacy-name hits in the Stage 1 corpus.

    Scans question/customer_section/expert_section/filename of every document
    (all post-Stage-0, i.e. PII-scrubbed). Case-sensitive whole-token match
    for short uppercase abbreviations; normalized substring match for legacy
    names (spacing variants must count).
    """
    fields = ("question", "customer_section", "expert_section", "filename")
    docs = [json.loads(line)
            for line in docs_path.read_text(encoding="utf-8").splitlines() if line]

    def combined_texts():
        """One combined text per document (counts are per-document)."""
        for d in docs:
            yield d, " \n ".join(str(d.get(f) or "") for f in fields)

    candidates = []
    for token, seed in sorted(SEED_ABBREVIATIONS.items()):
        pattern = re.compile(r"(?<![A-Za-z0-9])" + re.escape(token)
                             + r"(?![A-Za-z0-9])")
        n_docs = 0
        snippets: list[str] = []
        for d, text in combined_texts():
            m = pattern.search(text)
            if m:
                n_docs += 1
                if len(snippets) < 2:
                    lo, hi = max(0, m.start() - 40), min(len(text), m.end() + 40)
                    snippets.append(
                        f"{d['source_file']}: …{text[lo:hi].strip()}…")
        candidates.append({
            "token": token, "candidate_target": seed["target"],
            "recommendation": seed["recommendation"],
            "n_docs": n_docs, "evidence": snippets,
        })

    for ren in alias_table["legacy_renames"]:
        key = norm(ren["deprecated"])
        pattern = _tolerant_pattern(ren["deprecated"])
        n_docs = 0
        snippets: list[str] = []
        for d, text in combined_texts():
            m = pattern.search(text)
            if m:
                n_docs += 1
                if not snippets:
                    lo, hi = max(0, m.start() - 40), min(len(text), m.end() + 40)
                    snippets.append(
                        f"{d['source_file']}: …{text[lo:hi].strip()}…")
        candidates.append({
            "token": ren["deprecated"],
            "candidate_target": f"{ren['current_family']} (legacy rename)",
            "recommendation": "tag",  # renames are unambiguous by definition
            "n_docs": n_docs, "evidence": snippets,
        })

    for rep in alias_table.get("replacements", []):
        pattern = _tolerant_pattern(rep["deprecated"])
        n_docs = 0
        snippets: list[str] = []
        for d, text in combined_texts():
            m = pattern.search(text)
            if m:
                n_docs += 1
                if not snippets:
                    lo, hi = max(0, m.start() - 40), min(len(text), m.end() + 40)
                    snippets.append(
                        f"{d['source_file']}: …{text[lo:hi].strip()}…")
        candidates.append({
            "token": rep["deprecated"],
            "candidate_target": f"{rep['successor_family']} (REPLACEMENT — "
                                "different formula; currency cue and query "
                                "redirect only, never a product tag)",
            "recommendation": "context-only",
            "n_docs": n_docs, "evidence": snippets,
        })

    # discontinued names: currency cues without a successor — corpus mentions
    # of a discontinued product must flag the answer superseded too
    for disc in alias_table.get("discontinued", []):
        pattern = _tolerant_pattern(disc["name"])
        n_docs = 0
        snippets: list[str] = []
        for d, text in combined_texts():
            m = pattern.search(text)
            if m:
                n_docs += 1
                if not snippets:
                    lo, hi = max(0, m.start() - 40), min(len(text), m.end() + 40)
                    snippets.append(
                        f"{d['source_file']}: …{text[lo:hi].strip()}…")
        candidates.append({
            "token": disc["name"],
            "candidate_target": "discontinued (no successor) — currency cue "
                                "only, never a product tag",
            "recommendation": "tag",
            "n_docs": n_docs, "evidence": snippets,
        })

    return candidates


def write_curation_worksheet(path: Path, candidates: list[dict]) -> None:
    lines = [
        "# Alias curation worksheet (plan open item #3)",
        "",
        "Confirm or correct each row with the support lead. Confirmed entries "
        "move into the curated overlay in ``qa_pipeline/alias.py`` and the "
        "table is rebuilt.",
        "",
        "**Application policy — corpus text is NEVER rewritten.** Aliases drive "
        "only: (a) index metadata tags (``products`` field), (b) query-side "
        "expansion, (c) Stage 4 currency flags. Ambiguous tokens are resolved "
        "contextually by the Stage 2 LLM, never by find-and-replace.",
        "",
        "Spelling variants need no curation: matching is normalization-based "
        "(First String ≡ FirstString, Active MV ≡ ActiveMV, WheySmooth ≡ "
        "Whey Smooth). Only true abbreviations and legacy names are listed.",
        "",
        "**Outcome legend** (edit the *Recommended* cell if the session "
        "disagrees):",
        "",
        "- **tag** — deterministic auto-tagging is safe: every corpus hit maps "
        "to this family",
        "- **context-only** — no deterministic rule (dual/generic usage); the "
        "Stage 2 LLM tags per document, unsure cases → review queue",
        "- **no mapping** — drop the token entirely",
        "",
        "**Session record 2026-09-01:** all rows confirmed per recommendation.",
        "MVM was briefly flipped to tag, then reconsidered: the MVs are distinct",
        "products chosen by audience (women / 50+ / general), so a deterministic",
        "all-MV tag would blur the distinction — context-only stands, with",
        "audience guidance recorded in ``CONTEXT_ONLY_TOKENS``. Outcomes live in",
        "``CURATED_ALIASES`` / ``CONTEXT_ONLY_TOKENS`` in ``qa_pipeline/alias.py``;",
        "this file is the session record; regenerate with ``qa-pipeline aliases``.",
        "",
        "**PDSRG dispositions 2026-09-01 (4), support lead:** the legacy-rename",
        "rows below (SuperiorAntioxidant, UltraProbiotic, ExtremeCreatineXXXL,",
        "JointSkinCollagen+, AdvancedBrainHealth) come from that",
        "session — confirmed by the product owner (sku 1000 Antioxidant was",
        "added to products.json to complete the SuperiorAntioxidant rename).",
        "**Recover&Build is a *replacement*, not a rename** (different formula),",
        "so it lives in the table's `replacements` section: currency cue and",
        "query redirect only, never a product tag.",
        "Two chain names (CreatineXXL, JointFlexPlus) come from formerly-markers",
        "in the PDSRG titles, corpus-attested. Discontinued rows (KidsMV, VeganMV)",
        "are currency cues only: mentions flag the answer superseded; they never",
        "tag a product.",
        "",
        "| Confirm | Token | Outcome | Mapping | Docs | Evidence |",
        "|---|---|---|---|---|---|",
    ]
    for c in candidates:
        ev = ("<br>".join(
            re.sub(r"\s+", " ", e).replace("|", "\\|") for e in c["evidence"])
            or "—")
        lines.append(
            f"| [x] | `{c['token']}` | **{c['recommendation']}** | "
            f"{c['candidate_target']} | {c['n_docs']} | {ev} |")
    lines.append("")
    write_text(path, "\n".join(lines))
