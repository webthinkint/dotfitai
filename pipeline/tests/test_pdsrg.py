"""Unit tests for the PDSRG chunker (plan §6) — synthetic fixtures only.

The pdfplumber-dependent extraction is exercised on the real corpus by the
CLI run; these tests lock the pure logic: table classification (gate
findings), heading rules per template, level ranking, section paths,
chunk sizing, references exclusion, and stem resolution.
"""

from __future__ import annotations

import pytest

from qa_pipeline.alias import norm
from qa_pipeline.pdsrg import (
    MAX_TOKENS, PageLine, assign_heading_levels, build_sections,
    chunk_records, chunk_section, classify_lines, is_collapsed,
    is_prose_false_positive, is_references_section, is_trivial,
    merge_wrapped_headings, render_table_markdown, resolve_stem,
    running_header_texts, slugify,
)


# --- table classification -----------------------------------------------------

def test_trivial_filter_catches_one_cell_prose():
    assert is_trivial([["Some text", None], [None, "more"]])


def test_prose_false_positive_signature():
    # gate finding: SuperiorAntioxidant p8 — 11x3, every row one cell,
    # cells are sentence fragments
    data = [["Beyond Prevention", "", ""],
            ["Supplementation (although not equivocally proven in all", "", ""],
            ["ocular disorders, assuming the disorder is related to", "", ""]]
    assert is_prose_false_positive(data)


def test_prose_filter_keeps_real_grids():
    # MVM p5 style: multi-cell rows (the binding worst case)
    data = [["Nutrient", "Active MV", "Women's MV", "Over 50 MV"],
            ["Vitamin D", "4.9 µg", "15-20 µg", "100 µg"],
            ["Vitamin B12", "100 mcg", "100 mcg", "500 mcg"]]
    assert not is_prose_false_positive(data)
    assert not is_trivial(data)


def test_prose_filter_short_cells_not_prose():
    # one-column but short cells (e.g. a number list) -> not prose
    data = [["100"], ["200"], ["300"]]
    assert not is_prose_false_positive(data)


def test_collapse_signature():
    assert is_collapsed([["4.9µg 15-20µg 100µg N-250µg", None, None, None]])


def test_render_table_markdown_drops_empty_columns():
    data = [["Nutrient", "Amount", ""],
            ["Vitamin D", "4.9 µg", ""]]
    md = render_table_markdown(data)
    assert md.splitlines()[0] == "| Nutrient | Amount |"
    assert "---|---" in md.splitlines()[1]
    assert md.count("\n") == 2  # header + separator + one row


def test_render_table_markdown_flattens_cell_newlines():
    md = render_table_markdown([["A\nB", "C"], ["1", "2"]])
    assert "| A B | C |" in md


# --- line classification --------------------------------------------------------

def _line(text, size=11.0, fonts=("Calibri",), page=1, top=100.0,
          bottom=112.0, kind="body", level=0):
    fonts = frozenset(fonts)
    return PageLine(text=text, size=size, bold="Bold" in " ".join(fonts),
                    italic="Italic" in " ".join(fonts), fonts=fonts, page=page,
                    top=top, bottom=bottom, kind=kind, level=level)


def test_classify_noise_fonts_sizes_headers():
    lines = [
        _line("Practitioner Dietary Supplement Reference – 4th Edition",
              size=11.9, fonts=("Georgia-Bold",)),          # running header
        _line("www.dotFIT.com", size=10.0, fonts=("Georgia",)),  # footer
        _line("This information is educational material…", size=6.1,
              fonts=("Cambria",)),                          # disclaimer
        _line("10", size=7.0),                              # page number
        _line("Background", size=11.0, fonts=("Cambria-Bold",)),   # heading
        _line("Figure 1 — caption", size=11.0, fonts=("Calibri-Bold",)),
        _line("Body text here.", size=11.0),
        _line("•", size=11.0),                              # list marker
    ]
    out = classify_lines(lines, slide=False, headers=set())
    kinds = [ln.kind for ln in out]
    assert kinds == ["noise", "noise", "noise", "noise",
                     "heading", "body", "body", "marker"]


def test_classify_size_headings_for_non_cambria_templates():
    # Alln1 (Montserrat) / Electrolytes (Calibri) templates
    lines = [
        _line("Purpose & Rationale", size=14.0, fonts=("Montserrat-Regular",)),
        _line("Why don't Americans get enough nutrients?", size=13.0,
              fonts=("Montserrat-Regular",)),
        _line("body", size=12.0, fonts=("Montserrat-Regular",)),
        _line("Roles of Electrolytes in the Body", size=14.0),
    ]
    out = classify_lines(lines, slide=False, headers=set())
    assert [ln.kind for ln in out] == ["heading", "heading", "body", "heading"]


def test_running_header_detection_needs_page_edge_position():
    pages = 10
    lines = []
    for p in range(1, pages + 1):
        lines.append(_line("Nutrition Science Guide", size=16.0, page=p, top=40))
        lines.append(_line("header follow-up", size=11.0, page=p, top=60))
        lines.append(_line("Recommended & Typical Intakes", size=11.0,
                           fonts=("Calibri-Italic",), page=p, top=400))
        for j in range(6):  # dense page: subhead is mid-page, not page-edge
            lines.append(_line(f"body {p} line {j}", size=11.0,
                               page=p, top=430 + 10 * j))
    headers = running_header_texts(lines, pages)
    assert norm("Nutrition Science Guide") in headers
    assert norm("header follow-up") in headers          # second line on page
    # repeated mid-page subhead must NOT be dropped (Electrolytes case)
    assert norm("Recommended & Typical Intakes") not in headers


def test_running_header_not_detected_for_short_docs():
    lines = [_line("Same title", size=16.0, page=p) for p in (1, 2)]
    assert running_header_texts(lines, 2) == set()


def test_heading_levels_ranked_per_doc():
    # WeightLoss&LiverSupport-style: 16 title, 14 sections, 13 subs, 11
    lines = [_line(t, size=s, fonts=("Cambria-Bold",), kind="heading")
             for t, s in [("Weight Loss & Liver Support", 16.0),
                          ("Goal", 14.0), ("Choline", 13.0), ("TMAO", 11.0)]]
    lines.append(_line("Italic sub", size=11.0,
                       fonts=("Cambria-BoldItalic",), kind="heading"))
    assign_heading_levels(lines)
    assert [ln.level for ln in lines] == [1, 2, 3, 3, 3]


def test_heading_levels_clamped_to_three():
    lines = [_line(t, size=s, fonts=("Cambria-Bold",), kind="heading")
             for t, s in [("A", 18.0), ("B", 16.0), ("C", 14.0), ("D", 13.0),
                          ("E", 11.0)]]
    assign_heading_levels(lines)
    assert [ln.level for ln in lines] == [1, 2, 3, 3, 3]


def test_merge_wrapped_headings():
    items = [
        ("line", _line("Exercise, Protein and EAAs to Maximize and Prolong a "
                       "Positive Muscle P", size=11.0, fonts=("Cambria-Bold",),
                       kind="heading")),
        ("line", _line("a Lifetime", size=11.0, fonts=("Cambria-Bold",),
                       kind="heading", top=112.5)),
        ("line", _line("body between", size=11.0)),
        ("line", _line("Next Heading", size=13.0, fonts=("Cambria-Bold",),
                       kind="heading")),
    ]
    merged = merge_wrapped_headings(items)
    headings = [o for k, o in merged if k == "line" and o.kind == "heading"]
    assert len(headings) == 2
    assert headings[0].text.endswith("Muscle P a Lifetime")


# --- sections --------------------------------------------------------------------

def test_build_sections_path_stack_and_prefix_dedupe():
    doc_title = "Weight Loss & Liver Support"
    items = [
        ("line", _line(doc_title, size=16.0, fonts=("Cambria-Bold",),
                       kind="heading", level=1)),
        ("line", _line("intro text", size=11.0)),
        ("line", _line("Goal", size=14.0, fonts=("Cambria-Bold",),
                       kind="heading", level=2)),
        ("line", _line("goal body", size=11.0)),
        ("line", _line("Choline", size=13.0, fonts=("Cambria-Bold",),
                       kind="heading", level=3)),
        ("line", _line("choline body", size=11.0)),
    ]
    sections = build_sections(items, doc_title)
    paths = [s.path for s in sections]
    assert paths == [[doc_title], [doc_title, "Goal"],
                     [doc_title, "Goal", "Choline"]]
    # chunk prefix dedupes doc_title == path[0]
    chunks = chunk_section(sections[1], doc_title, ["Goal"])
    assert chunks[0]["content"].startswith(f"{doc_title} > Goal\n\n")


def test_build_sections_bullet_markers_split_blocks():
    items = [
        ("line", _line("List intro", size=11.0)),
        ("line", _line("•", size=11.0, kind="marker")),
        ("line", _line("first item", size=11.0)),
        ("line", _line("•", size=11.0, kind="marker")),
        ("line", _line("second item", size=11.0)),
    ]
    sections = build_sections(items, "Doc")
    assert len(sections[0].blocks) == 3


def test_build_sections_hyphen_wrap_joins():
    items = [
        ("line", _line("well-", size=11.0)),
        ("line", _line("known compound", size=11.0)),
    ]
    sections = build_sections(items, "Doc")
    assert sections[0].blocks[0][1] == "well-known compound"


def test_references_section_detection():
    sec = build_sections(
        [("line", _line("References", size=13.0, fonts=("Cambria-Bold",),
                        kind="heading", level=1)),
         ("line", _line("1 Smith J. …", size=10.0))], "Doc")
    assert is_references_section(sec[0])
    goal = build_sections(
        [("line", _line("Goal", size=13.0, fonts=("Cambria-Bold",),
                        kind="heading", level=1)),
         ("line", _line("body", size=11.0))], "Doc")
    assert not is_references_section(goal[0])


# --- chunk sizing ------------------------------------------------------------------

def _para(tokens: int, page: int = 2) -> tuple[str, str, int]:
    text = "word " * (tokens * 4 // 5)  # ~tokens tokens at 4 chars/token
    return ("para", text.strip(), page)


def test_chunk_assembly_respects_max_and_targets():
    from qa_pipeline.pdsrg import Section
    section = Section(path=["Big"])
    section.blocks = [_para(300), _para(300), _para(300), _para(300)]
    chunks = chunk_section(section, "Doc", ["Big"])
    assert len(chunks) >= 2
    # n_tokens now measures the indexed content, prefix included
    assert all(c["n_tokens"] <= MAX_TOKENS for c in chunks)
    # pages tracked
    assert all(c["pages"] == [2, 2] for c in chunks)


def test_oversize_table_is_atomic():
    from qa_pipeline.pdsrg import Section
    section = Section(path=["Grid"])
    big_table = "| a | " + " | ".join(str(i) for i in range(2500)) + " |"
    section.blocks = [("table", big_table, 5), ("para", "tail", 5)]
    chunks = chunk_section(section, "Doc", ["Grid"])
    assert len(chunks) == 2  # table alone, then para
    assert "| a |" in chunks[0]["content"]
    assert chunks[0]["pages"] == [5, 5]


def test_chunk_records_carry_discontinued_status():
    from qa_pipeline.pdsrg import Section
    section = Section(path=["Goal"])
    section.blocks = [("para", "Support for kids' multivitamin use.", 2)]
    chunks = chunk_section(section, "KidsMV", ["Goal"])
    chunks[0]["id"] = "pdsrg:kidsmv:001"
    doc = {"doc_title": "KidsMV", "family": "KidsMV", "part_nos": [],
           "category": "MVM", "topics": [], "source_file": "data/x.pdf",
           "slug": "kidsmv", "status": "discontinued",
           "note": "discontinued — older kids/teens: Active MV"}
    rec = chunk_records(doc, chunks)[0]
    assert rec["products"] == []
    assert rec["product_status"] == "discontinued"
    assert "Active MV" in rec["product_note"]


def test_chunk_records_shape():
    section = build_sections(
        [("line", _line("Goal", size=13.0, fonts=("Cambria-Bold",),
                        kind="heading", level=1)),
         ("line", _line("To accelerate weight loss.", size=11.0, page=3))],
        "Doc")
    chunks = chunk_section(section[0], "ThermAccel", ["Goal"])
    chunks[0]["id"] = "pdsrg:thermaccel:001"
    doc = {"doc_title": "ThermAccel", "family": "ThermAccel",
           "part_nos": [1102], "category": "WeightLoss", "topics": [],
           "source_file": "data/x.pdf", "slug": "thermaccel"}
    rec = chunk_records(doc, chunks)[0]
    assert rec["id"] == "pdsrg:thermaccel:001"
    assert rec["source_type"] == "pdsrg"
    assert rec["authority"] == 2
    assert rec["products"] == [1102]
    assert rec["title"] == "Goal"
    assert rec["locator"] == "ThermAccel — Goal (p. 3)"
    assert rec["content"].startswith("ThermAccel > Goal\n\n")


def test_chunk_records_stamp_the_index_contract_fields():
    """§9: the default query filter is ``is_current eq true`` and Azure AI
    Search does not match null, so an unstamped chunk is invisible to every
    query — including the authority-1/2 claims sources."""
    from qa_pipeline.pdsrg import Section
    section = Section(path=["Goal"])
    section.blocks = [("para", "Body text.", 4)]
    chunks = chunk_section(section, "ThermAccel", ["Goal"])
    chunks[0]["id"] = "pdsrg:thermaccel:001"
    doc = {"doc_title": "ThermAccel", "family": "ThermAccel",
           "part_nos": [1102], "category": "WeightLoss", "topics": [],
           "source_file": "ThermAccel.pdf", "slug": "thermaccel"}
    rec = chunk_records(doc, chunks)[0]
    assert rec["is_current"] is True
    assert rec["date"] is None
    assert rec["citation_url"] == "pdsrg/ThermAccel.pdf#page=4"


def test_discontinued_products_stay_current():
    """product_status is the SKU lifecycle; is_current is answer currency.
    A discontinued doc must remain retrievable for "what happened to X"."""
    from qa_pipeline.pdsrg import Section
    section = Section(path=["Goal"])
    section.blocks = [("para", "Body text.", 2)]
    chunks = chunk_section(section, "KidsMV", ["Goal"])
    chunks[0]["id"] = "pdsrg:kidsmv:001"
    doc = {"doc_title": "KidsMV", "family": "KidsMV", "part_nos": [],
           "category": "MVM", "topics": [], "source_file": "KidsMV.pdf",
           "slug": "kidsmv", "status": "discontinued", "note": "discontinued"}
    rec = chunk_records(doc, chunks)[0]
    assert rec["product_status"] == "discontinued"
    assert rec["is_current"] is True


def test_citation_base_is_configurable():
    from qa_pipeline.pdsrg import Section
    section = Section(path=["Goal"])
    section.blocks = [("para", "Body text.", 7)]
    chunks = chunk_section(section, "Doc", ["Goal"])
    chunks[0]["id"] = "pdsrg:doc:001"
    doc = {"doc_title": "Doc", "family": "Doc", "part_nos": [],
           "category": "General", "topics": [], "source_file": "Doc.pdf",
           "slug": "doc"}
    rec = chunk_records(doc, chunks, citation_base="https://cdn.example/kb/")[0]
    assert rec["citation_url"] == "https://cdn.example/kb/Doc.pdf#page=7"


# --- stem resolution ------------------------------------------------------------------

@pytest.fixture
def alias_families():
    return {norm("AminoFormula"): {"family": "AminoFormula",
                                   "part_nos": [1213, 1216, 1220]},
            norm("First String"): {"family": "First String",
                                   "part_nos": [1371, 1372]},
            norm("Creatine Complex"): {"family": "Creatine Complex",
                                       "part_nos": [1207]}}


def test_resolve_stem_curated_overrides_norm(alias_families):
    r = resolve_stem("AminoFormula", alias_families)
    assert r["part_nos"] == [1213, 1216, 1220]
    assert r["category"] == "Performance"


def test_resolve_stem_norm_fallback(alias_families):
    # "CreatineComplex" is not in STEM_META but norm-matches a family
    r = resolve_stem("CreatineComplex", alias_families)
    assert r["family"] == "Creatine Complex"
    assert r["part_nos"] == [1207]
    assert r["needs_category"]  # flagged: needs a curated category


def test_resolve_stem_renamed_resolves_current_part_nos(alias_families):
    # PDSRG dispositions 2026-09-01 (5): UltraProbiotic renamed to Probiotics
    fams = dict(alias_families)
    fams[norm("Probiotics")] = {"family": "Probiotics", "part_nos": [1017]}
    fams[norm("Antioxidant")] = {"family": "Antioxidant", "part_nos": [1000]}
    r = resolve_stem("UltraProbiotic", fams)
    assert r["part_nos"] == [1017]
    assert r["status"] is None  # current product — no status marker
    # SuperiorAntioxidant resolved once sku 1000 landed in products.json
    r = resolve_stem("SuperiorAntioxidant", fams)
    assert r["part_nos"] == [1000]
    assert r["status"] is None


def test_resolve_stem_discontinued_has_no_part_nos(alias_families):
    r = resolve_stem("KidsMV", alias_families)
    assert r["family"] == "KidsMV"
    assert r["part_nos"] is None
    assert r["status"] == "discontinued"
    assert "Active MV" in r["note"]  # successor guidance travels with it


def test_resolve_stem_replaced_product_keeps_own_label(alias_families):
    # Recover&Build is replaced BY AminoFormula — its doc keeps the legacy
    # label and tags no part_nos (different formula); the mapping lives in
    # the alias table's legacy_renames for currency flags / query rewrite
    r = resolve_stem("Recover&Build", alias_families)
    assert r["family"] == "Recover & Build"
    assert r["part_nos"] is None
    assert r["status"] == "discontinued"


def test_resolve_stem_unknown_raises(alias_families):
    with pytest.raises(ValueError, match="STEM_META"):
        resolve_stem("Brand New Product", alias_families)


def test_slugify_stable_and_posix():
    assert slugify("WeightLoss&LiverSupport") == "weightloss-liversupport"
    assert slugify("GlutamineComplex (formerly MuscleDefender)") == \
        "glutaminecomplex-formerly-muscledefender"
    assert slugify("NO7 Preworkout (formerly NO7 Rage)") == \
        "no7-preworkout-formerly-no7-rage"
