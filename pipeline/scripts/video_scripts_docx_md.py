"""dotFIT 2026 website product-video scripts -> one markdown file per product.

The document (original-data/Product Video Scripts) is the product-page copy
that replaces the current product information: per product an overview
("takeaway") and five fixed questions — what it is, what it does, who it is
for, how to use it (dosing), what makes it different. Each file closes with
the product's Supplement or Nutrition Facts panels from the website export
(original-data/Product Data/products.json), one per flavor.

The text is read with python-docx, never model-written. The document's
heading styles cannot be trusted, so they are ignored:

- some product titles are Heading 1, others plain or bold body text, and
  some body paragraphs are styled Heading 1 (which is why the document's
  navigation pane shows sections outside their product);
- a section label is either its own paragraph ("What is it?", or a variant
  such as "What Is the Product") or a prefix of the body paragraph ("What is
  it? DigestiveEnzymes is ...", "(Takeaway) ...", "Overview (repositioned
  Takeaway — top of product page) ...").

So the split is the hand-curated ``PRODUCTS`` list below (document order):
a paragraph whose text is exactly a listed title starts that product, and a
section label from ``SECTION_LABELS`` starts a section. The run fails if a
title is missing or repeated, if a non-empty paragraph falls outside a
section and is not in ``DROPPED``, if a product lacks a section not listed
in ``MISSING``, or if a product tag is not an alias-table family.

What changes the document's wording, and nothing else:

- ``CORRECTIONS``: the owners' approved typo and grammar fixes, each an exact
  string that must occur exactly once in its product's text;
- the protein bars' placeholder protein minimum ("[12]", "[12-15]") is
  filled with the lowest protein per serving among the bars in the export;
- a paragraph broken mid-sentence (the next one starts in lower case after
  the previous ended without punctuation) is rejoined, runs of spaces
  collapse, and Word list paragraphs become markdown bullets.

Every currency amount is masked to ``[price]``. Sections are written in the
fixed order with the takeaway first, as "Overview" — the document's own
direction for the product page.

The facts panels keep the export's serving lines, the table (columns empty
in every row dropped) and its Daily Value and symbol footnotes; label
directions, ingredients and warnings are not part of the panel. A part with
no facts table fails the run.

Output: pipeline-output/video-scripts/md/NN-<slug>.md + summary.json (sorted,
no timestamps — reruns are byte-identical, the pipeline determinism rule).

Usage (from pipeline/): uv run scripts/video_scripts_docx_md.py
"""

from __future__ import annotations

import html
import json
import re
import sys
from pathlib import Path

import docx
from docx.oxml.ns import qn

from summaries_pptx_md import PRICE, mask_prices

DOC_NAME = "2026 Website Product Videos.docx"
EXPORT_NAME = "products.json"

# (key, heading written to the md). Output order.
SECTIONS: list[tuple[str, str]] = [
    ("overview", "Overview"),
    ("what", "What is it?"),
    ("does", "What does it do?"),
    ("who", "Who is it for?"),
    ("use", "How do you use it?"),
    ("different", "What makes it different?"),
]

# Every label the document uses, as a whole paragraph or a paragraph prefix.
# Longest first, so "Overview (repositioned ...)" wins over a shorter label.
SECTION_LABELS: list[tuple[str, str]] = sorted([
    ("Overview (repositioned Takeaway — top of product page)", "overview"),
    ("Overview (repositioned Takeaway — top of category/product pages)", "overview"),
    ("(Takeaway)", "overview"),
    ("(Take away)", "overview"),
    ("Takeaway", "overview"),
    ("What is it?", "what"),
    ("What Is the Product", "what"),
    ("What does it do?", "does"),
    ("What It Does", "does"),
    ("Who is it for?", "who"),
    ("Who Would Use It", "who"),
    ("How do you use it?", "use"),
    ("How to Use It", "use"),
    ("What makes ours different?", "different"),
    ("What makes it different?", "different"),
], key=lambda kv: -len(kv[0]))

# (slug, title as the md writes it, title paragraph exactly as in the
# document, alias-table families, part numbers). Part numbers default to
# every part of the families; listing them narrows to the variants the
# script describes. Document order.
PRODUCTS: list[tuple[str, str, str, list[str], list[int] | None]] = [
    ("over50mv", "Over50MV", "Over50MV", ["Over 50 MV"], None),
    ("vitamin-d3", "Vitamin D3", "Vitamin D3", ["Vitamin D-3"], None),
    ("womensmv", "Women'sMV", "Women'sMV", ["Women's MV"], None),
    ("leanmeal", "LeanMeal", "LeanMeal", ["LeanMeal Nutrition Shake"], None),
    ("digestive-enzymes", "DigestiveEnzymes", "Digestive Enzymes", ["Digestive Enzymes"], None),
    ("probiotics", "Probiotics", "Probiotics", ["Probiotics"], None),
    ("pre-post-workout", "Pre & Post Workout Formula", "Pre/Post Workout",
     ["Pre & Post Workout Formula"], None),
    ("thermaccel", "ThermAccel", "ThermAccel", ["ThermAccel"], None),
    ("alln1-superblend", "Alln1 SuperBlend", "Alln1 SuperBlend", ["Alln1 SuperBlend"], None),
    ("antioxidant", "Antioxidant", "Antioxidant", ["Antioxidant"], None),
    ("calcium-complex", "CalciumComplex", "CalciumComplex", ["Calcium Complex"], None),
    ("plant-protein", "PlantProtein", "PlantProtein", ["Plant Protein"], None),
    ("glutamine-complex", "GlutamineComplex", "GlutamineComplex", ["GlutamineComplex"], None),
    ("electrolytes", "Electrolytes", "Electrolytes", ["Electrolytes"], None),
    ("carbrepel", "CarbRepel", "CarbRepel", ["CarbRepel"], None),
    ("wlls", "WeightLoss & LiverSupport", "WeightLoss & LiverSupport",
     ["WeightLoss & LiverSupport"], None),
    ("leanpak90", "LeanPak90", "LeanPak90", ["LeanPak90"], None),
    ("protein-bars", "dotBARs & dotWAFERs", "Protein Bars", ["dotBAR", "Vanilla dotWAFER"], None),
    ("activemv", "ActiveMV", "ActiveMV — Multivitamin & Mineral Formula", ["Active MV"], None),
    ("brain-health", "Advanced Brain Health", "Advanced Brain Health", ["Brain Health"], None),
    ("collagen-complex", "CollagenComplex", "CollagenComplex", ["CollagenComplex"], None),
    ("omega-3", "Omega3", "Omega3", ["Omega-3 Fish Oil"], None),
    ("sleep-aid", "Sleep Aid", "Sleep Aid (Takeaway only or All if not in blue longsleeve shi",
     ["SleepAid"], None),
    ("all-natural-wheysmooth", "All Natural WheySmooth", "All Natural WheySmooth",
     ["WheySmooth"], [1374, 1375]),
    ("wheysmooth", "WheySmooth", "WheySmooth", ["WheySmooth"], [1369, 1370, 1391, 1392, 1399]),
    ("firststring", "FirstString", "FirstString", ["First String"], None),
    ("creatine-monohydrate", "Creatine Monohydrate", "Creatine Monohydrate",
     ["Creatine Monohydrate"], None),
    ("creatine-complex", "CreatineComplex", "CreatineComplex", ["Creatine Complex"], None),
    ("aminoformula", "AminoFormula", "AminoFormula", ["AminoFormula"], None),
    ("no7-preworkout", "NO7 Preworkout", "NO7 Preworkout", ["NO7 PreWorkout"], None),
]

# Products in the document that are not written out, by title paragraph.
SKIPPED: dict[str, str] = {
    "Energy Aid": "not on the website yet",
}

# Paragraphs that are not product copy, exactly as in the document.
DROPPED: dict[str, str] = {
    "Total Remaining Videos to Film": "production status line",
    "WheySmooth": "repeated product title under the WheySmooth heading",
}

# Sections a product has no text for.
MISSING: dict[str, list[str]] = {
    "sleep-aid": ["overview"],  # not written yet
}

# A kit whose facts are its components' panels, not its own part's.
FACTS_PARTS: dict[str, list[int]] = {
    "leanpak90": [1100, 1102, 1101],
}

# The protein-bars script leaves the protein minimum as placeholders; each
# is filled with the lowest protein per serving among the bars' facts.
PROTEIN_PLACEHOLDERS: dict[str, int] = {"[12]": 2, "[12-15]": 1}

# (wrong, right) per product, applied to the document's text before the
# whitespace collapse. Each wrong string must occur exactly once.
CORRECTIONS: dict[str, list[tuple[str, str]]] = {
    "over50mv": [
        ("Splitting your 2-tabs, 1 with an AM Meal and 1 with a PM meal for greater",
         "Splitting your 2 tabs, 1 with an AM meal and 1 with a PM meal, allows for greater"),
    ],
    "vitamin-d3": [
        ('start right now." And always', "start right now. And always"),
        ("Moreover, older adults, bodies make", "Moreover, older adults' bodies make"),
        ("vitamin D. in people with", "vitamin D. In people with"),
        ("is candidate for", "is a candidate for"),
        ("And most all athletes", "And almost all athletes"),
    ],
    "leanmeal": [
        ("Meal replacements, such as LeanMeal incorporated into daily meal planning has been",
         "Meal replacements such as LeanMeal, incorporated into daily meal planning, have been"),
    ],
    "pre-post-workout": [
        ("goal. because it is", "goal. Because it is"),
    ],
    "alln1-superblend": [
        ("hundreds of dollars piecing together.", "hundreds of dollars piecing together.”"),
        ("in a single daily solution", "in a single daily solution."),
        ("long-term health", "long-term health."),
        ("adapt to optimal nutrition", "adapt to optimal nutrition."),
        ("AllN1 SuperBlend", "Alln1 SuperBlend"),
    ],
    "antioxidant": [
        ("3rd- party tested", "3rd-party tested"),
    ],
    "calcium-complex": [
        ("or not consuming calcium fortified foods", "or aren't consuming calcium-fortified foods"),
    ],
    "plant-protein": [
        ("built in  -and - with no artificial", "built in, and with no artificial"),
        ("fall short of on the amino acids", "fall short on the amino acids"),
    ],
    "glutamine-complex": [
        ("fall to up to 50% during stresses", "fall by up to 50% during stress"),
        ("compete or breakdown", "compete or break down"),
        ("Additionally supplementing supports", "Additionally, supplementing supports"),
        ("using it. it’s good for anyone", "using it. It’s good for anyone"),
    ],
    "carbrepel": [
        ("inhibits the enzymes that digests starch", "inhibits the enzyme that digests starch"),
    ],
    "leanpak90": [
        ("The LeanPac90 system", "The LeanPak90 system"),
    ],
    "activemv": [
        ("We don’t’- so you shouldn’t either- rely on store bought guesses - Choose",
         "We don’t — so you shouldn’t either — rely on store-bought guesses. Choose"),
        ("ready for anything", "ready for anything."),
    ],
    "omega-3": [
        ("exercise. most importantly", "exercise. Most importantly"),
    ],
    "all-natural-wheysmooth": [
        ("premium control release whey", "premium controlled-release whey"),
        ("and no, gluten,", "and no gluten,"),
        ("convenient shake.”", "convenient shake."),
    ],
    "wheysmooth": [
        ("Through it’s controlled release proteins It delivers",
         "Through its controlled-release proteins, it delivers"),
        ("convenient shake.”", "convenient shake."),
    ],
    "firststring": [
        ("shaker cup. -“Fuel", "shaker cup. “Fuel"),
    ],
    "creatine-monohydrate": [
        ("less than 175LBS, you can cut that maintenance dose in half – in other works, 1-scoop",
         "less than 175 lbs, you can cut that maintenance dose in half – in other words, 1 scoop"),
        ("inside and out. - Now you can Power", "inside and out. Now you can power"),
    ],
    "creatine-complex": [
        ("premier multi-Ingredient Pre/Post Workout Supplement, that takes creatine to the to the "
         "next level, with Beta-alanine, Glutamine and the premium vasodilator with Careflow",
         "premier multi-ingredient pre/post workout supplement that takes creatine to the next "
         "level with beta-alanine, glutamine and the premium vasodilator Careflow"),
        ("beta-alanine further delay fatigue, deliver greater force production",
         "beta-alanine to further delay fatigue and deliver greater force production"),
    ],
    "aminoformula": [
        ("Within extreme low calories", "Within extremely low calories"),
        ("within extreme low calories", "within extremely low calories"),
        ("Addtionally, non exercisers", "Additionally, non-exercisers"),
        ("watered-down ESSENTIAL AMINO ACID formula", "watered-down essential amino acid formula"),
        ("height dose leucine", "high-dose leucine"),
        ("formulas on the market, contain", "formulas on the market contain"),
        ("found with the Amino Formula.", "found with AminoFormula."),
        ("level to maximizes recovery", "level to maximize recovery"),
        ("start ingesting 10min before", "start ingesting 10 min before"),
    ],
    "no7-preworkout": [
        ("NO7 Preworkout the premier", "NO7 Preworkout is the premier"),
        ("Using dotFIT’s “NO7 PRE WORKOUT is like", "Using dotFIT’s NO7 Preworkout is like"),
        ("this is your edge.”", "this is your edge."),
    ],
}

_TERMINAL = tuple(".!?\"'”’)")


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def doc_path() -> Path:
    return repo_root() / "original-data" / "Product Video Scripts" / DOC_NAME


def export_path() -> Path:
    return repo_root() / "original-data" / "Product Data" / EXPORT_NAME


def out_dir() -> Path:
    return repo_root() / "pipeline-output" / "video-scripts"


def alias_path() -> Path:
    return repo_root() / "pipeline-output" / "aliases" / "alias_table.json"


def read_paragraphs(path: Path) -> list[dict]:
    """Body paragraphs in order (the document has no tables), whitespace-trimmed."""
    out = []
    for p in docx.Document(str(path)).paragraphs:
        text = p.text.replace("\xa0", " ").strip()
        if text:
            out.append({"text": text, "list": p._p.find(f".//{qn('w:numPr')}") is not None})
    return out


def split_label(text: str) -> tuple[str | None, str]:
    """(section key, remaining body) when the paragraph opens with a label."""
    for label, key in SECTION_LABELS:
        if text == label:
            return key, ""
        if text.startswith(label) and (label.endswith(")") or text[len(label)] in " \n"):
            return key, text[len(label):].strip()
    return None, text


def assign(paras: list[dict]) -> tuple[dict[str, dict[str, list[dict]]], list[str], int]:
    """Products -> sections -> paragraphs; errors; count of rejoined paragraphs."""
    titles = {doc_title: slug for slug, _t, doc_title, _f, _p in PRODUCTS}
    products: dict[str, dict[str, list[dict]]] = {}
    errors: list[str] = []
    joined = 0
    slug: str | None = None
    section: str | None = None
    skipping = False
    for i, para in enumerate(paras):
        text = para["text"]
        if text in titles and titles[text] not in products:
            slug, section, skipping = titles[text], None, False
            products[slug] = {}
            continue
        if text in SKIPPED:
            slug, section, skipping = None, None, True
            continue
        if skipping or text in DROPPED:
            continue
        key, body = split_label(text)
        if key is not None:
            if slug is None:
                errors.append(f"paragraph {i}: section label before any product")
                continue
            if key in products[slug]:
                errors.append(f"{slug}: section {key!r} appears twice")
            section = key
            products[slug][key] = []
            if not body:
                continue
            text = body
        if slug is None or section is None:
            errors.append(f"paragraph {i} outside any section: {text[:60]!r}")
            continue
        current = products[slug][section]
        if current and not current[-1]["text"].endswith(_TERMINAL) and text[0].islower():
            current[-1]["text"] += " " + text
            joined += 1
            continue
        current.append({"text": text, "list": para["list"]})
    return products, errors, joined


def replace_once(paras: list[dict], old: str, new: str, count: int = 1) -> bool:
    """Replace ``old`` across the paragraphs when it occurs exactly ``count`` times."""
    if sum(p["text"].count(old) for p in paras) != count:
        return False
    for p in paras:
        p["text"] = p["text"].replace(old, new)
    return True


def edit(products: dict[str, dict[str, list[dict]]], min_protein: str) -> list[str]:
    """Corrections, the protein fill, whitespace collapse and price masking."""
    errors: list[str] = []
    for slug, sections in products.items():
        paras = [p for ps in sections.values() for p in ps]
        for old, new in CORRECTIONS.get(slug, []):
            if not replace_once(paras, old, new):
                errors.append(f"{slug}: correction {old[:50]!r} does not occur exactly once")
        if slug == "protein-bars":
            for old, count in PROTEIN_PLACEHOLDERS.items():
                if not replace_once(paras, old, min_protein, count):
                    errors.append(f"{slug}: placeholder {old!r} does not occur {count} times")
        for p in paras:
            p["text"] = mask_prices(re.sub(r" {2,}", " ", p["text"]))
    return errors


def _cells(line: str) -> list[str]:
    return [c.strip() for c in line.strip().strip("|").split("|")]


def facts_panel(entry: dict) -> dict:
    """The export's facts panel for one part: title, serving lines, table, footnotes."""
    text = html.unescape(entry["searchcontent"].replace("\r\n", "\n"))
    m = re.search(r"^### Specifications[ \t]*\n(.*?)(?=^### |\Z)", text, re.M | re.S)
    if not m:
        raise ValueError(f"{entry['part_no']}: no Specifications section")
    lines = [ln.rstrip() for ln in m.group(1).split("\n")]
    tables: list[list[str]] = []
    in_table = False
    for ln in lines:
        if ln.startswith("|"):
            if not in_table:
                tables.append([])
            tables[-1].append(ln)
        in_table = ln.startswith("|")
    if len(tables) != 1:
        raise ValueError(f"{entry['part_no']}: {len(tables)} facts tables, expected 1")
    rows = [_cells(ln) for ln in tables[0] if not re.fullmatch(r"[|\s:-]+", ln)]
    width = max(len(r) for r in rows)
    rows = [r + [""] * (width - len(r)) for r in rows]
    keep = [i for i in range(width) if any(r[i] for r in rows)]
    rows = [[r[i] for i in keep] for r in rows]
    heading = re.search(r"^##[ \t]+(.*Facts)[ \t]*$", m.group(1), re.M)
    if heading:
        title = heading.group(1)
    else:
        title = "Nutrition Facts" if any(r[0] == "Calories" for r in rows) else "Supplement Facts"
    serving = [ln.split("**")[0].strip() for ln in lines
               if re.search(r"serving size|servings per container", ln, re.I)]
    raw = m.group(1).split("\n")
    notes = []
    for i, ln in enumerate(lines):
        if ln.startswith(("‡", "†")) or (ln.startswith("\\*") and "daily value" in ln.lower()):
            # a markdown hard break ("  ") continues the footnote on the next line
            while raw[i].endswith("  ") and i + 1 < len(lines) and lines[i + 1]:
                i += 1
                ln += " " + lines[i].strip()
            notes.append(ln.strip())
    return {"title": title, "serving": serving, "rows": rows, "notes": notes}


def protein_grams(panel: dict) -> float:
    for row in panel["rows"]:
        if row[0] == "Protein":
            m = re.match(r"([\d.]+)\s*g\b", row[1])
            if m:
                return float(m.group(1))
    raise ValueError("no protein row")


def panel_md(panel: dict) -> list[str]:
    out = ["  \n".join(panel["serving"]), ""] if panel["serving"] else []
    rows = [[c.replace("|", "\\|") for c in r] for r in panel["rows"]]
    out.append("| " + " | ".join(rows[0]) + " |")
    out.append("|" + "---|" * len(rows[0]))
    out += ["| " + " | ".join(r) + " |" for r in rows[1:]]
    out.append("")
    for note in panel["notes"]:
        out += [note, ""]
    return [mask_prices(ln) for ln in out]


def facts_md(parts: list[int], export: dict[int, dict]) -> list[str]:
    """One subsection per distinct panel, named by every part that carries it."""
    groups: list[tuple[list[str], dict]] = []
    for no in parts:
        panel = facts_panel(export[no])
        for names, seen in groups:
            if seen == panel:
                names.append(export[no]["longname"])
                break
        else:
            groups.append(([export[no]["longname"]], panel))
    nutrition = all(panel["title"] == "Nutrition Facts" for _n, panel in groups)
    out = [f"## {'Nutrition Facts' if nutrition else 'Supplement Facts'}", ""]
    for names, panel in groups:
        sub = " / ".join(re.sub(r"\s+", " ", n).strip() for n in names)
        out += [f"### {sub}", ""] + panel_md(panel)
    return out


def validate(products: dict[str, dict[str, list[dict]]],
             families: dict[str, list[int]], export: dict[int, dict]) -> list[str]:
    errors: list[str] = []
    slugs = [p[0] for p in PRODUCTS]
    if len(set(slugs)) != len(slugs):
        errors.append("duplicate product slug")
    for slug, _title, doc_title, fams, part_nos in PRODUCTS:
        if slug not in products:
            errors.append(f"{slug}: title paragraph {doc_title!r} not found")
            continue
        want = {k for k, _ in SECTIONS} - set(MISSING.get(slug, []))
        have = {k for k, paras in products[slug].items() if paras}
        if want - have:
            errors.append(f"{slug}: no text for {sorted(want - have)}")
        if have - want:
            errors.append(f"{slug}: has text for {sorted(have - want)}, listed as missing")
        unknown = [f for f in fams if f not in families]
        errors.extend(f"{slug}: product {f!r} is not an alias-table family" for f in unknown)
        if not fams:
            errors.append(f"{slug}: no product tag")
        if part_nos and not unknown:
            stray = set(part_nos) - {n for f in fams for n in families[f]}
            if stray:
                errors.append(f"{slug}: part numbers {sorted(stray)} not in its families")
        if not unknown:
            for no in facts_parts(slug, fams, part_nos, families):
                if no not in export:
                    errors.append(f"{slug}: part {no} not in {EXPORT_NAME}")
                    continue
                try:
                    facts_panel(export[no])
                except ValueError as e:
                    errors.append(f"{slug}: {e}")
    errors.extend(f"MISSING/CORRECTIONS/FACTS_PARTS key {k!r} is not a product slug"
                  for k in list(MISSING) + list(CORRECTIONS) + list(FACTS_PARTS) if k not in slugs)
    return errors


def part_numbers(fams: list[str], part_nos: list[int] | None,
                 families: dict[str, list[int]]) -> list[int]:
    return sorted(part_nos or {n for f in fams for n in families[f]})


def facts_parts(slug: str, fams: list[str], part_nos: list[int] | None,
                families: dict[str, list[int]]) -> list[int]:
    return FACTS_PARTS.get(slug) or part_numbers(fams, part_nos, families)


def render(product, sections: dict[str, list[dict]], families: dict[str, list[int]],
           export: dict[int, dict], notes: list[str]) -> str:
    slug, title, doc_title, fams, part_nos = product
    nos = ", ".join(map(str, part_numbers(fams, part_nos, families)))
    out = [f"# {title}", "",
           f"- source: `{DOC_NAME}`, \"{doc_title}\"; facts: `{EXPORT_NAME}`",
           f"- products: {', '.join(fams)}",
           f"- part numbers: {nos}", ""]
    for note in notes:
        out += [f"_Note (annotation, not script text): {note}_", ""]
    for key, heading in SECTIONS:
        paras = sections.get(key)
        if not paras:
            continue
        out += [f"## {heading}", ""]
        for i, p in enumerate(paras):
            out.append(("- " if p["list"] else "") + p["text"])
            nxt = paras[i + 1] if i + 1 < len(paras) else None
            if not (p["list"] and nxt and nxt["list"]):
                out.append("")
    out += facts_md(facts_parts(slug, fams, part_nos, families), export)
    return "\n".join(out).rstrip() + "\n"


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    path = doc_path()
    if not path.exists():
        print(f"FAIL: document not found: {path}", file=sys.stderr)
        return 2
    table = json.loads(alias_path().read_text(encoding="utf-8"))
    families = {f["family"]: f["part_nos"] for f in table["families"]}
    export = {int(e["part_no"]): e
              for e in json.loads(export_path().read_text(encoding="utf-8"))}

    products, errors, joined = assign(read_paragraphs(path))
    errors += validate(products, families, export)
    if errors:
        for e in errors:
            print(f"FAIL: {e}", file=sys.stderr)
        return 1

    bar_parts = part_numbers(["dotBAR", "Vanilla dotWAFER"], None, families)
    grams = sorted(protein_grams(facts_panel(export[no])) for no in bar_parts)
    fmt = lambda g: f"{g:g}"  # noqa: E731
    errors = edit(products, fmt(grams[0]))
    if errors:
        for e in errors:
            print(f"FAIL: {e}", file=sys.stderr)
        return 1
    notes = {"protein-bars": [
        f"The script's protein minimum is filled from the bars' Nutrition Facts: the lowest "
        f"protein per serving among the flavors ({fmt(grams[0])} g; the range is "
        f"{fmt(grams[0])}–{fmt(grams[-1])} g)."]}

    od = out_dir()
    md_dir = od / "md"
    md_dir.mkdir(parents=True, exist_ok=True)
    for stale in md_dir.glob("*.md"):
        stale.unlink()
    meta = []
    masked = 0
    for i, product in enumerate(PRODUCTS, 1):
        slug, title, _doc_title, fams, part_nos = product
        name = f"{i:02d}-{slug}.md"
        text = render(product, products[slug], families, export, notes.get(slug, []))
        masked += text.count(PRICE)
        (md_dir / name).write_text(text, encoding="utf-8", newline="\n")
        meta.append({"file": name, "slug": slug, "title": title, "products": fams,
                     "part_nos": part_numbers(fams, part_nos, families),
                     "facts_parts": facts_parts(slug, fams, part_nos, families),
                     "missing": MISSING.get(slug, []),
                     "corrections": len(CORRECTIONS.get(slug, [])),
                     "chars": sum(len(p["text"]) for ps in products[slug].values() for p in ps)})
    summary = {"document": DOC_NAME, "export": EXPORT_NAME, "n_products": len(meta),
               "paragraphs_rejoined": joined,
               "corrections": sum(len(v) for v in CORRECTIONS.values()),
               "prices_masked": masked, "skipped": SKIPPED, "dropped": DROPPED,
               "products": meta}
    (od / "summary.json").write_text(
        json.dumps(summary, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")

    print(f"done: {len(meta)} products, {summary['corrections']} corrections, "
          f"{joined} paragraphs rejoined, {masked} prices masked -> {md_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
