"""PDSRG chunking pipeline (plan §6): PDF -> indexed chunk documents.

Input: the Practitioner Dietary Supplement Reference Guide PDF corpus
(39 files, four layout templates). Output: one JSONL record per chunk,
section-level chunked by heading with heading-path prefixes, tables kept
intact, plus a per-doc review outline and a run summary.

Extraction strategy is inherited from the validated gate test
(``scripts/pdsrg_gate.py``, human-verified PASS 2026-09-01):

- pdfplumber ``lines`` strategy; trivial (<=2 cell) and *prose false
  positive* detections dropped; collapse signature re-extracted with the
  ``text`` strategy (restores ultra-wide dosage grids).

The chunker adds the **prose false-positive filter** the gate demanded
(progress 2026-09-01: "the chunker must not index these as tables"):
ruled prose blocks extract as tables whose rows NEVER have >=2 non-empty
cells (each "row" is a wrapped text line) and whose cells are sentence
fragments. Real dosage grids always have multi-cell rows. Corpus-wide:
761 raw detections -> 49 real tables kept (422 trivial, 290 prose dropped,
2 text-strategy fallbacks).

Heading detection is font-based; the corpus uses four layout templates:

- **main** (Calibri body, Cambria headings): Cambria-Bold /
  Cambria-BoldItalic >= 9.5pt are headings. Body bold (Calibri-Bold,
  e.g. figure captions) is NOT a heading.
- **montserrat** (Alln1 SuperBlend "Nutrition Science Guide"): any line
  >= 13pt (the repeated 16pt running header is dropped by the
  repetition rule).
- **calibri-plain** (Electrolytes): Calibri >= 13pt.
- **slide deck** (Sleep Aid, Aptos fonts): no font headings — pages are
  sections, page title = first Aptos-Bold (>=13.5) or >=16pt line.

Levels are assigned by the doc's own heading-size ranking (title
fonts vary 13.5-18pt across docs), italic headings always deepest,
clamped to 3. Section paths drive the chunk content prefix (plan:
"SuperOmega-3 Fish Oils > Dosing & Evidence").

Chunk records align with the §9 index fields (``id``, ``source_type``,
``authority``, ``title``, ``content``, ``products``, ``locator``...).
References/bibliography sections are excluded from chunks by default
(retrieval noise; ``--keep-references`` includes them) and reported in
the summary. Deterministic: sorted iteration, no timestamps in chunk
files — byte-identical reruns.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import quote

import pdfplumber

from .alias import norm  # NFKC-casefold-alphanumeric match key

# --- table extraction settings (identical to the validated gate) -------------

LINES_SETTINGS = {"vertical_strategy": "lines", "horizontal_strategy": "lines"}
TEXT_SETTINGS = {
    "vertical_strategy": "text",
    "horizontal_strategy": "text",
    "snap_tolerance": 3,
    "join_tolerance": 3,
}

DOSAGE_RE = re.compile(
    r"\d+(?:[.,]\d+)?\s*(?:mg|mcg|[\u00b5\u03bc]g|ug|g\b|grams?\b|IU\b|international units?\b)",
    re.IGNORECASE,
)

# --- tunables -----------------------------------------------------------------

CAMBRIA_HEADING_MIN = 9.5   # Cambria-Bold(Italic) headings (main template)
SIZE_HEADING_MIN = 13.0     # any-font heading (montserrat / calibri-plain)
NOISE_MAX_SIZE = 7.5        # disclaimers (Cambria 6.x), page numbers (~7)
RUNNING_HEADER_MIN_PAGES = 3
RUNNING_HEADER_PAGE_FRACTION = 0.6
PROSE_MIN_MEDIAN_CELL = 40  # sentence-fragment cells -> framed prose
TARGET_TOKENS = 650
MAX_TOKENS = 800
CHARS_PER_TOKEN = 4
SLIDE_TITLE_BOLD_MIN = 13.5
SLIDE_TITLE_ANY_MIN = 16.0
BULLET_MARKERS = ("•", "◦", "▪", "·", "‣")

FORMERLY_RE = re.compile(r"\((formerly [^)]+)\)", re.IGNORECASE)

# --- curated stem metadata ----------------------------------------------------
# Stem (filename minus .pdf) -> product family / topic metadata. Resolution
# order: this table first, then norm() match against alias-table families.
# Dispositions (support lead, 2026-09-01 (5)):
# - renamed products point at their current family -> chunks tag the
#   current part_nos; deprecated names live in alias.py CURATED_LEGACY
# - "no_part_nos": doc stays indexable but tags no products facet values
#   (discontinued products — no successor SKU to tag)
# category per the four PDSRG Introduction sections: Health / Performance /
# WeightLoss / MVM; topic docs (no product) are General.
STEM_META: dict[str, dict] = {
    # -- product docs, part_nos known via alias table ----------------------
    "ActiveMV": {"family": "Active MV", "category": "MVM"},
    "Alln1 SuperBlend": {"family": "Alln1 SuperBlend", "category": "Health"},
    "AminoFormula": {"family": "AminoFormula", "category": "Performance"},
    "BestPlantProtein": {"family": "Plant Protein", "category": "Performance"},
    "CalciumComplex": {"family": "Calcium Complex", "category": "Health"},
    "CarbRepel": {"family": "CarbRepel", "category": "WeightLoss"},
    "CreatineMonohydrate": {"family": "Creatine Monohydrate", "category": "Performance"},
    "DigestiveEnzymes": {"family": "Digestive Enzymes", "category": "Health"},
    "Electrolytes": {"family": "Electrolytes", "category": "Performance"},
    "FirstString": {"family": "First String", "category": "Performance"},
    "GlutamineComplex (formerly MuscleDefender)": {"family": "GlutamineComplex", "category": "Performance"},
    "LeanMeal (Formerly LeanMR)": {"family": "LeanMeal Nutrition Shake", "category": "WeightLoss"},
    "NO7 Preworkout (formerly NO7 Rage)": {"family": "NO7 PreWorkout", "category": "Performance"},
    "Over50MV": {"family": "Over 50 MV", "category": "MVM"},
    "Pre&PostWorkoutShake": {"family": "Pre & Post Workout Formula", "category": "Performance"},
    "Sleep Aid": {"family": "SleepAid", "category": "Health"},
    "SuperOmega-3 Fish Oils": {"family": "Omega-3 Fish Oil", "category": "Health"},
    "ThermAccel": {"family": "ThermAccel", "category": "WeightLoss"},
    "Vitamin D-3": {"family": "Vitamin D-3", "category": "Health"},
    "WeightLoss&LiverSupport": {"family": "WeightLoss & LiverSupport", "category": "WeightLoss"},
    "WheySmooth": {"family": "WheySmooth", "category": "Performance"},
    "Women'sMV": {"family": "Women's MV", "category": "MVM"},
    "WorkoutExtreme": {"family": "Workout Extreme", "category": "Performance"},
    # -- product docs with dispositions (support lead, 2026-09-01 (5)) ----
    # renamed -> current family (chunks tag current part_nos); the
    # deprecated names live in alias.py CURATED_LEGACY for currency flags.
    "AdvancedBrainHealth": {"family": "Brain Health", "category": "Health"},
    "ExtremeCreatineXXXL": {"family": "Creatine Complex", "category": "Performance"},
    "JointSkinCollagen+": {"family": "CollagenComplex", "category": "Health"},
    "Reformulated with Careflow (2025)": {"family": "Creatine Complex", "category": "Performance"},
    "SuperiorAntioxidant": {"family": "Antioxidant", "category": "Health"},
    "UltraProbiotic": {"family": "Probiotics", "category": "Health"},
    # discontinued / replaced: doc stays indexable (science + "what
    # happened to X" answers) but never tags a successor's part_nos —
    # Recover&Build's replacement (AminoFormula) is a different formula.
    "KidsMV": {"family": "KidsMV", "category": "MVM", "no_part_nos": True,
               "status": "discontinued",
               "note": "discontinued — older kids/teens: Active MV; younger "
                       "kids: third-party ChildLife Nutrition, Children's "
                       "Multi Vitamin & Mineral"},
    "Recover&Build": {"family": "Recover & Build", "category": "Performance",
                      "no_part_nos": True, "status": "discontinued",
                      "note": "discontinued — replaced by AminoFormula "
                              "(legacy rename in the alias table)"},
    "VeganMV": {"family": "VeganMV", "category": "MVM", "no_part_nos": True,
                "status": "discontinued", "note": "discontinued"},
    # -- topic docs (no product): plan §6.4 --------------------------------
    "Introduction": {"family": None, "category": "General", "topics": ["company", "supplement-policy"]},
    "Introduction to Health Products": {"family": None, "category": "General", "topics": ["health", "multivitamin"]},
    "Introduction to Lifelong Complete Multivitamin & Mineral Supplementation": {"family": None, "category": "General", "topics": ["multivitamin"]},
    "Introduction to Performance Products": {"family": None, "category": "General", "topics": ["performance"]},
    "Introduction to Weight Loss Products": {"family": None, "category": "General", "topics": ["weight-loss"]},
    "Product Manufacturing & Third Party Testing": {"family": None, "category": "General", "topics": ["manufacturing", "quality"]},
    "dotFIT Multivitamin & Mineral Formulas Specialty Design Criteria": {"family": None, "category": "General", "topics": ["multivitamin", "formulation"]},
}


# --- table classification (gate logic + the new prose filter) ----------------

def _cell_norm(s: str | None) -> str:
    s = unicodedata.normalize("NFKC", s or "")
    return re.sub(r"\s+", " ", s).strip().lower()


def _nonempty(row: list[str | None]) -> int:
    return sum(1 for c in row if _cell_norm(c))


def is_trivial(data: list[list[str | None]]) -> bool:
    """Prose false positive on ruled text lines: <=2 non-empty cells total."""
    return sum(_nonempty(row) for row in data) <= 2


def is_prose_false_positive(data: list[list[str | None]]) -> bool:
    """Ruled prose block detected as a table (gate finding, 2026-09-01).

    Signature (verified on SuperiorAntioxidant p8 11x3, p11 4x2 / 8x1,
    Alln1 p9 40x6): almost NO row has >=2 non-empty cells (each "row" is
    one wrapped text line; a stray artifact row can split across a column
    rule) and the cells are sentence fragments. Real dosage grids have
    multi-cell rows on at least a third of their rows; short-cell
    exceptions (one-column number lists) fail the median-length guard.
    """
    n_rows = len(data)
    multi = sum(1 for row in data if _nonempty(row) >= 2)
    if n_rows and multi / n_rows >= 0.34:
        return False
    cells = sorted((_cell_norm(c) for row in data for c in row if _cell_norm(c)),
                   key=len)
    if not cells:
        return True
    return len(cells[len(cells) // 2]) >= PROSE_MIN_MEDIAN_CELL


def is_collapsed(data: list[list[str | None]]) -> bool:
    """Collapse signature: a row with one non-empty cell holding >=3 dosages."""
    return any(
        _nonempty(row) == 1 and len(DOSAGE_RE.findall(_cell_norm(row[0]))) >= 3
        for row in data
    )


def extract_tables_hybrid(page, stats: dict[str, int]) -> list[dict]:
    """Real tables on *page* with bboxes; trivial/prose detections dropped.

    Counts every raw detection in *stats* for the run summary.
    """
    out: list[dict] = []
    for t in page.find_tables(table_settings=LINES_SETTINGS):
        data = t.extract()
        stats["raw"] += 1
        if not data or is_trivial(data):
            stats["trivial"] += 1
            continue
        strategy = "lines"
        collapsed = is_collapsed(data)
        if collapsed:
            # collapsed wide grids have run-on single-cell rows — must be
            # re-extracted BEFORE the prose filter, whose signature
            # (single-cell rows, long cells) they share
            cropped = page.crop(t.bbox)
            retry = cropped.find_tables(table_settings=TEXT_SETTINGS)
            if retry:
                best = max(retry, key=lambda rt: len(rt.extract() or []))
                redata = best.extract()
                if redata and not is_collapsed(redata):
                    data, strategy = redata, "text(fallback)"
                    stats["text_fallback"] += 1
                    collapsed = False
        if collapsed:
            # Still collapsed after the retry: a dosage-dense grid, not
            # prose (prose never packs 3+ dosages into one cell). Keep it
            # atomic downstream via the oversize path instead of dropping.
            stats["kept"] += 1
            out.append({"data": data, "strategy": strategy + "(collapsed)",
                        "page": page.page_number, "bbox": t.bbox})
            continue
        if is_prose_false_positive(data):
            stats["prose_dropped"] += 1
            continue
        stats["kept"] += 1
        out.append({"data": data, "strategy": strategy, "page": page.page_number,
                    "bbox": t.bbox})
    return out


def render_table_markdown(data: list[list[str | None]]) -> str:
    """Rectangular markdown table; fully-empty columns removed."""
    n_cols = max(len(row) for row in data)
    rows = [[(c or "").replace("\n", " ").strip() for c in row]
            + [""] * (n_cols - len(row)) for row in data]
    keep = [j for j in range(n_cols) if any(r[j] for r in rows)]
    rows = [[r[j] for j in keep] for r in rows]
    if not keep:
        return ""
    lines = ["| " + " | ".join(rows[0]) + " |",
             "|" + "---|" * len(keep)]
    for r in rows[1:]:
        lines.append("| " + " | ".join(r) + " |")
    return "\n".join(lines)


# --- line model & heading classification --------------------------------------

@dataclass
class PageLine:
    text: str
    size: float          # mean char size
    bold: bool
    italic: bool
    fonts: frozenset     # subset prefixes stripped
    page: int
    top: float
    bottom: float
    x0: float = 0.0
    x1: float = 0.0
    kind: str = "body"   # noise | body | heading | marker
    level: int = 0       # heading depth 1..3


def _line_from_extracted(ln: dict, page_no: int) -> PageLine:
    chars = ln["chars"]
    fonts = frozenset(c["fontname"].split("+")[-1] for c in chars)
    sizes = [c["size"] for c in chars]
    names = " ".join(fonts)
    return PageLine(
        text=ln["text"].strip(),
        size=round(sum(sizes) / len(sizes), 1),
        bold="Bold" in names,
        italic="Italic" in names,
        fonts=fonts,
        page=page_no,
        top=ln["top"],
        bottom=ln["bottom"],
        x0=ln["x0"],
        x1=ln["x1"],
    )


def is_slide_deck(lines: list[PageLine]) -> bool:
    return any("Aptos" in f for ln in lines for f in ln.fonts)


def running_header_texts(lines: list[PageLine], n_pages: int) -> set[str]:
    """Texts appearing as a line near the top/bottom of most pages.

    Must NOT catch repeated *content* (Electrolytes repeats the subhead
    "Recommended & Typical Intakes" on most pages, mid-page): require the
    page-position signature of a running header/footer — among the first
    two or last two lines (by vertical order) on every page it occurs.
    """
    if n_pages < RUNNING_HEADER_MIN_PAGES:
        return set()
    by_key: dict[str, list[PageLine]] = {}
    for ln in lines:
        if ln.text:
            by_key.setdefault(norm(ln.text), []).append(ln)

    # page-edge lookup, built once (it does not depend on the candidate).
    # Identity, not equality: PageLine is a dataclass, so two structurally
    # identical lines on a page compare equal and a mid-page repeat could
    # borrow an edge line's verdict.
    page_order: dict[int, list[PageLine]] = {}
    for ln in lines:
        page_order.setdefault(ln.page, []).append(ln)
    edges: dict[int, set[int]] = {}
    for page, page_lines in page_order.items():
        page_lines.sort(key=lambda l: l.top)
        edges[page] = {id(l) for l in page_lines[:2] + page_lines[-2:]}

    min_pages = max(2, int(n_pages * RUNNING_HEADER_PAGE_FRACTION))
    headers: set[str] = set()
    for key, lns in by_key.items():
        if len({ln.page for ln in lns}) < min_pages:
            continue
        if all(id(ln) in edges[ln.page] for ln in lns):
            headers.add(key)
    return headers


def classify_lines(lines: list[PageLine], slide: bool,
                   headers: set[str]) -> list[PageLine]:
    """Set ``kind`` (noise/body/marker/heading) on every line."""
    for ln in lines:
        if not ln.text:
            ln.kind = "noise"
            continue
        if any(f.startswith("Georgia") for f in ln.fonts):
            ln.kind = "noise"          # running header/footer (all templates)
            continue
        if ln.size < NOISE_MAX_SIZE:
            ln.kind = "noise"          # disclaimers, page numbers, footnotes
            continue
        if norm(ln.text) in headers:
            ln.kind = "noise"          # repeated running header (any template)
            continue
        if ln.text in BULLET_MARKERS:
            ln.kind = "marker"         # standalone bullet -> list item start
            continue
        if slide:
            continue                   # body; page titles handled separately
        if any(f.startswith("Cambria-Bold") for f in ln.fonts) \
                and ln.size >= CAMBRIA_HEADING_MIN:
            ln.kind = "heading"
            continue
        if ln.size >= SIZE_HEADING_MIN:
            ln.kind = "heading"
            continue
        ln.kind = "body"
    return lines


def assign_heading_levels(lines: list[PageLine]) -> None:
    """Rank the doc's own heading sizes: largest=L1 ... clamp 3; italic deepest.

    Title fonts vary 13.5-18pt across docs, so absolute sizes cannot assign
    levels; the per-doc size distribution can (Goal/Rationale ~13 under a
    ~14 title; ~11 subheads; italic sub-subheads).
    """
    plain_sizes = sorted({ln.size for ln in lines
                          if ln.kind == "heading" and not ln.italic},
                         reverse=True)
    n_plain = len(plain_sizes)
    for ln in lines:
        if ln.kind != "heading":
            continue
        if ln.italic:
            ln.level = min(3, n_plain + 1)
        else:
            ln.level = min(3, plain_sizes.index(ln.size) + 1)


def _clean_heading_text(text: str) -> str:
    """Cosmetic trim: bold-italic lead-ins that swallow the first sentence
    ("Lysine (1669.5 mg): L-lysine is an indispensable...") keep only the
    label; cap runaway lengths."""
    text = re.sub(r"\s+", " ", text).strip()
    if ": " in text:
        label, rest = text.split(": ", 1)
        # only amount-lead-ins ("Lysine (1669.5 mg): …"), never headings
        # like "Summary: Intake and Recommendation of EPA & DHA"
        if re.search(r"\d", label) and len(label) <= 48 and len(rest) > 24:
            text = label
    if len(text) > 120:
        text = text[:117].rstrip() + "…"
    return text


def merge_wrapped_headings(items: list) -> list:
    """Join adjacent same-style heading lines (PDF line wrap), drop empties.

    ``items`` are flow items: ("line", PageLine) | ("table", dict). Two
    heading lines merge when consecutive, same size (±0.3) and italicity.
    """
    out: list = []
    for kind, obj in items:
        if kind == "line" and obj.kind == "heading" and out:
            pkind, prev = out[-1]
            if pkind == "line" and prev.kind == "heading" \
                    and prev.page == obj.page \
                    and abs(prev.size - obj.size) <= 0.3 \
                    and prev.italic == obj.italic:
                prev.text = _clean_heading_text(
                    f"{prev.text} {obj.text}")
                prev.bottom = obj.bottom
                continue
        if kind == "line" and obj.kind == "heading":
            obj.text = _clean_heading_text(obj.text)
        out.append((kind, obj))
    return out


# --- section & chunk assembly --------------------------------------------------

@dataclass
class Section:
    path: list[str]
    blocks: list[tuple[str, str, int]] = field(default_factory=list)  # (kind, text, page)


def build_sections(items: list, doc_title: str) -> list[Section]:
    """Heading-delimited sections; tables and markers become blocks."""
    sections: list[Section] = []
    stack: list[tuple[int, str]] = []           # (level, title)
    para: list[str] = []
    para_page = 0

    def flush_para() -> None:
        nonlocal para, para_page
        if para:
            sections[-1].blocks.append(("para", " ".join(para), para_page))
        para, para_page = [], 0

    def close_section() -> None:
        flush_para()

    def open_section() -> Section:
        path = [t for _, t in stack]
        sec = Section(path=path)
        sections.append(sec)
        return sec

    open_section()  # content before the first heading
    for kind, obj in items:
        if kind == "line":
            ln = obj
            if ln.kind == "heading":
                close_section()
                while stack and stack[-1][0] >= ln.level:
                    stack.pop()
                stack.append((ln.level, ln.text))
                open_section()
            elif ln.kind == "marker":
                flush_para()
                para, para_page = [ln.text], ln.page
            elif ln.kind == "body":
                if not para:
                    para_page = ln.page
                if para and para[-1].endswith("-") \
                        and ln.text[:1].islower():
                    # wrapped at an existing hyphen — join keeping it
                    para[-1] = para[-1] + ln.text
                else:
                    para.append(ln.text)
        else:  # table
            flush_para()
            md = render_table_markdown(obj["data"])
            if md:
                sections[-1].blocks.append(("table", md, obj["page"]))
    close_section()
    return [s for s in sections if s.blocks]


def _split_oversize_para(text: str, page: int,
                         budget_tokens: int) -> list[tuple[str, str, int]]:
    """Word-split a paragraph that cannot fit a chunk on its own.

    *budget_tokens* is TARGET minus the heading prefix, which is prepended to
    every chunk's content and therefore consumes part of the same budget.
    """
    out: list[tuple[str, str, int]] = []
    cur: list[str] = []
    cur_chars = 0
    for w in text.split(" "):
        add = len(w) + (1 if cur else 0)
        if cur and (cur_chars + add) // CHARS_PER_TOKEN > budget_tokens:
            out.append(("para", " ".join(cur), page))
            cur, cur_chars = [], 0
            add = len(w)
        cur.append(w)
        cur_chars += add
    if cur:
        out.append(("para", " ".join(cur), page))
    return out


def chunk_section(section: Section, doc_title: str,
                  prefix_path: list[str]) -> list[dict]:
    """Greedy assembly: fill to ~TARGET, never exceed MAX; tables atomic.

    The heading-path prefix is part of the indexed ``content``, so it counts
    against the budget and against ``n_tokens`` — measuring only the body
    enforced the cap on a string that is not what gets embedded.
    """
    prefix = " > ".join([doc_title] + prefix_path) if prefix_path else doc_title
    prefix_chars = len(prefix) + 2          # + the blank line after the prefix
    body_budget = max(1, TARGET_TOKENS - prefix_chars // CHARS_PER_TOKEN)

    blocks: list[tuple[str, str, int]] = []
    for kind, text, page in section.blocks:
        if kind == "para" and (prefix_chars + len(text)) // CHARS_PER_TOKEN > MAX_TOKENS:
            blocks.extend(_split_oversize_para(text, page, body_budget))
        else:
            blocks.append((kind, text, page))

    chunks: list[dict] = []
    cur: list[tuple[str, str, int]] = []
    cur_chars = 0                           # exact char count of the body so
                                            # far, including block separators:
                                            # summing per-block floored token
                                            # counts drifts below the real
                                            # total and lets chunks past MAX

    def flush() -> None:
        nonlocal cur, cur_chars
        if not cur:
            return
        body = "\n\n".join(t for _, t, _ in cur)
        pages = sorted(p for _, _, p in cur)
        content = f"{prefix}\n\n{body}"
        rec = {
            "section_path": list(prefix_path),
            "content": content,
            "pages": [pages[0], pages[-1]],
            "n_tokens": len(content) // CHARS_PER_TOKEN,
            "flags": [],
        }
        if rec["n_tokens"] > MAX_TOKENS:
            rec["flags"].append("oversize")  # atomic table / long single block
        chunks.append(rec)
        cur, cur_chars = [], 0

    for kind, text, page in blocks:
        add = len(text) + (2 if cur else 0)     # "\n\n" between blocks
        if cur and (prefix_chars + cur_chars + add) // CHARS_PER_TOKEN > MAX_TOKENS:
            flush()
            add = len(text)
        cur.append((kind, text, page))
        cur_chars += add
    flush()
    return chunks


def is_references_section(section: Section) -> bool:
    """Bibliography sections: any path element named References (they can
    nest, e.g. AminoFormula's lives under 'Supplement Facts Panel')."""
    return any(norm(p) == "references" for p in section.path)


# --- document metadata ---------------------------------------------------------

def slugify(stem: str) -> str:
    s = unicodedata.normalize("NFKC", stem).casefold()
    s = re.sub(r"[^a-z0-9]+", "-", s).strip("-")
    return re.sub(r"-{2,}", "-", s) or "doc"


def resolve_stem(stem: str, alias_families: dict[str, dict]) -> dict:
    """Stem -> {family, part_nos, category, topics, status, note}.

    Curated STEM_META first, then norm() match against alias families
    (spelling variants are free). Unknown stems raise — an unattested
    mapping must never silently produce untagged chunks (alias lesson,
    2026-09-01: 0-doc rows are the tell).
    """
    meta = STEM_META.get(stem)
    if meta is None:
        base = FORMERLY_RE.sub("", stem).strip()
        fam = alias_families.get(norm(base))
        if fam is None:
            raise ValueError(
                f"PDSRG stem {stem!r} matches no curated entry and no alias "
                f"family — add it to STEM_META in qa_pipeline/pdsrg.py")
        return {"family": fam["family"], "part_nos": fam["part_nos"],
                "category": None, "topics": [], "status": None,
                "note": None, "needs_category": True}
    fam = meta["family"]
    part_nos: list[int] | None = None
    if fam is not None and not meta.get("no_part_nos"):
        af = alias_families.get(norm(fam))
        if af is None:
            raise ValueError(
                f"STEM_META[{stem!r}] family {fam!r} not in the alias table — "
                f"rebuild aliases or fix the curated entry")
        part_nos = af["part_nos"]
    return {"family": fam, "part_nos": part_nos,
            "category": meta["category"], "topics": meta.get("topics", []),
            "status": meta.get("status"), "note": meta.get("note"),
            "needs_category": False}


# --- slide-deck sectioning ------------------------------------------------------

def slide_sections(lines: list[PageLine], tables: list[dict]) -> list[Section]:
    """Sleep Aid-style decks: pages are sections titled by their first
    Aptos-Bold (>=13.5pt) or >=16pt line; untitled/tiny pages continue the
    previous section."""
    sections: list[Section] = []
    for page_no in sorted({ln.page for ln in lines}):
        plines = [ln for ln in lines if ln.page == page_no
                  and ln.kind in ("body", "marker")]
        title = None
        if plines:
            first = plines[0]
            if (first.bold and first.size >= SLIDE_TITLE_BOLD_MIN) \
                    or first.size >= SLIDE_TITLE_ANY_MIN:
                title = first
        page_tables = [t for t in tables if t["page"] == page_no]
        n_content = len(plines) + len(page_tables)
        if title is None and sections and n_content <= 4:
            target = sections[-1]          # spillover page -> previous slide
        else:
            target = Section(path=[title.text] if title else [f"Page {page_no}"])
            sections.append(target)
        para: list[str] = []
        for ln in plines:
            if ln is title:
                continue
            if ln.kind == "marker":
                if para:
                    target.blocks.append(("para", " ".join(para), page_no))
                para = [ln.text]
            else:
                para.append(ln.text)
        if para:
            target.blocks.append(("para", " ".join(para), page_no))
        for t in page_tables:
            md = render_table_markdown(t["data"])
            if md:
                target.blocks.append(("table", md, page_no))
    return [s for s in sections if s.blocks]


# --- per-PDF driver ---------------------------------------------------------------

def _inside_any_table(ln: PageLine, tables: list[dict]) -> bool:
    """Line text lives inside a kept table (re-rendered there) -> drop."""
    mid = (ln.top + ln.bottom) / 2
    for t in tables:
        x0, top, x1, bottom = t["bbox"]
        if top <= mid <= bottom and ln.x0 >= x0 - 2 and ln.x1 <= x1 + 2:
            return True
    return False


def chunk_pdf(path: Path, alias_families: dict[str, dict],
              keep_references: bool = False) -> dict:
    """One PDF -> {doc meta, chunks[], table stats, review outline}."""
    stem = path.stem
    resolved = resolve_stem(stem, alias_families)
    doc_title = resolved["family"] or stem
    table_stats = {"raw": 0, "trivial": 0, "prose_dropped": 0,
                   "text_fallback": 0, "kept": 0}

    lines: list[PageLine] = []
    all_tables: list[dict] = []
    n_pages = 0
    with pdfplumber.open(path) as pdf:
        n_pages = len(pdf.pages)
        for page in pdf.pages:
            lines.extend(_line_from_extracted(ln, page.page_number)
                         for ln in page.extract_text_lines())
            all_tables.extend(extract_tables_hybrid(page, table_stats))

        slide = is_slide_deck(lines)
        headers = running_header_texts(lines, n_pages)
        lines = classify_lines(lines, slide, headers)

        # drop text lines re-rendered inside kept tables
        tables_by_page: dict[int, list[dict]] = {}
        for t in all_tables:
            tables_by_page.setdefault(t["page"], []).append(t)
        lines = [ln for ln in lines
                 if ln.kind == "noise"
                 or not _inside_any_table(ln, tables_by_page.get(ln.page, []))]

        if slide:
            sections = slide_sections(lines, all_tables)
            items: list = []
        else:
            flow: list = [("line", ln) for ln in lines if ln.kind != "noise"] \
                + [("table", t) for t in all_tables]
            flow.sort(key=lambda it: (it[1].page if it[0] == "line" else it[1]["page"],
                                      it[1].top if it[0] == "line" else it[1]["bbox"][1]))
            items = merge_wrapped_headings(flow)
            assign_heading_levels([o for k, o in items if k == "line"])
            sections = build_sections(items, doc_title)

    references: list[Section] = []
    content_sections: list[Section] = []
    for s in sections:
        if not keep_references and is_references_section(s):
            references.append(s)
        else:
            content_sections.append(s)

    chunks: list[dict] = []
    for s in content_sections:
        prefix_path = [p for p in s.path if norm(p) != norm(doc_title)]
        chunks.extend(chunk_section(s, doc_title, prefix_path))
    for i, c in enumerate(chunks, 1):
        c["id"] = f"pdsrg:{slugify(stem)}:{i:03d}"

    ref_pages = [p for s in references for b in s.blocks for p in (b[2],)]
    doc = {
        "file": path.name,
        "stem": stem,
        "slug": slugify(stem),
        "doc_title": doc_title,
        "n_pages": n_pages,
        "slide_deck": slide,
        "family": resolved["family"],
        "part_nos": resolved["part_nos"] or [],
        "needs_category": resolved["needs_category"],
        "category": resolved["category"],
        "topics": resolved["topics"],
        "tables": table_stats,
        "n_sections": len(content_sections),
        "n_chunks": len(chunks),
        "n_tokens": sum(c["n_tokens"] for c in chunks),
        "status": resolved["status"],
        "note": resolved["note"],
        "references_excluded": {
            "n_sections": len(references),
            "pages": [min(ref_pages), max(ref_pages)] if ref_pages else [],
        } if references else None,
    }
    return {"doc": doc, "chunks": chunks,
            "sections": content_sections + references}


def chunk_records(doc: dict, chunks: list[dict],
                  citation_base: str = "pdsrg/") -> list[dict]:
    """Final JSONL records with shared per-doc metadata attached.

    Every §9 index field is stamped here, including ``is_current``: the
    default query filter is ``is_current eq true`` and Azure AI Search does
    not match null against it, so an unstamped chunk would be invisible to
    every query. PDSRG chunks are current by definition — a *discontinued
    product* is a separate axis, carried by ``product_status``, because those
    docs must stay retrievable to answer "what happened to X".
    """
    out = []
    for c in chunks:
        title = c["section_path"][-1] if c["section_path"] else doc["doc_title"]
        pages = c["pages"]
        loc = f"p. {pages[0]}" if pages[0] == pages[1] else f"pp. {pages[0]}-{pages[1]}"
        rec = {
            "id": c["id"],
            "source_type": "pdsrg",
            "source_file": doc["source_file"],
            "authority": 2,
            "doc_title": doc["doc_title"],
            "title": title,
            "section_path": c["section_path"],
            "content": c["content"],
            "products": doc["part_nos"],
            "product_family": doc["family"],
            "category": doc["category"],
            "topics": doc["topics"],
            "pages": pages,
            "locator": f"{doc['doc_title']} — {title} ({loc})",
            # source_file carries raw corpus names (spaces, &, apostrophes)
            # that break rendered links — quote the path (slashes stay) so
            # the URL works wherever the deployment serves the PDFs from.
            "citation_url": f"{citation_base}{quote(doc['source_file'])}"
                            f"#page={pages[0]}",
            "date": None,               # §9: nullable for non-QA sources
            "is_current": True,
            "n_tokens": c["n_tokens"],
        }
        if c["flags"]:
            rec["flags"] = c["flags"]
        if doc.get("status"):
            rec["product_status"] = doc["status"]
        if doc.get("note"):
            rec["product_note"] = doc["note"]
        out.append(rec)
    return out


def review_outline(doc: dict, sections: list[Section]) -> str:
    """Human spot-check artifact: headings, section sizes, chunk counts."""
    lines = [f"# {doc['doc_title']} — chunking outline",
             "",
             f"- file: `{doc['file']}` ({doc['n_pages']} pages"
             + (", slide deck" if doc["slide_deck"] else "") + ")",
             f"- family: {doc['family'] or '—'} part_nos={doc['part_nos'] or '—'}"
             + (f" — {doc['status']}" if doc.get("status") else "")
             + (f" ({doc['note']})" if doc.get("note") else ""),
             f"- category: {doc['category'] or 'MISSING'}"
             f" topics={doc['topics'] or '—'}",
             f"- tables: {doc['tables']}",
             f"- sections: {doc['n_sections']}, chunks: {doc['n_chunks']}, "
             f"tokens: ~{doc['n_tokens']}",
             f"- references excluded: {doc['references_excluded']}",
             "",
             "| section path | blocks | ~tokens |",
             "|---|---|---|"]
    for s in sections:
        tokens = sum(max(1, len(t) // CHARS_PER_TOKEN) for _, t, _ in s.blocks)
        path = " > ".join(s.path) or "(front matter)"
        lines.append(f"| {path} | {len(s.blocks)} | {tokens} |")
    return "\n".join(lines) + "\n"
