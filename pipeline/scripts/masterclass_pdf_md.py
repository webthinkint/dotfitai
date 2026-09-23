"""dotFIT Masterclass PDFs -> markdown (data/Masterclass/pdfs).

"Like we do the PDSRG": extraction is the validated §6 machinery from
``qa_pipeline.pdsrg``, reused piece-for-piece — the hybrid lines/text table
strategy with trivial and prose-false-positive filters, font-based heading
classification with running-header suppression, slide-deck sectioning (pages
are sections, titled by their first big bold line), wrapped-heading merge,
level assignment. The driver in ``pdsrg.chunk_pdf`` itself cannot be called:
its ``resolve_stem`` deliberately raises on stems that map to no curated
PDSRG entry or alias family, and every Masterclass filename is such a stem.
This script therefore replicates that driver's ~30 extraction lines (the
package pieces are imported, not copied) and replaces only the
PDSRG-specific parts:

- doc title: NFKC-folded filename stem (no product-family resolution —
  Masterclass docs get tagged at a future indexing step, if ever).
- references sections are KEPT: this is a source conversion, not retrieval
  chunking; the sleep-aid probe showed the reference links are the PDF's
  most valuable asset. Sections that ``is_references_section`` flags are
  still listed in the doc's meta block so a later chunker can drop them.
- per-page hyperlink annotations are collected and appended as a link
  appendix (the deck's live PubMed/PMC citations are not text-extractable).

Output: processed/masterclass/md/<slug>.md (slug = pdsrg-style slugify of
the stem) + a deterministic summary.json (sorted, no timestamps — reruns
are byte-identical, the pipeline determinism rule).

Usage (from pipeline/): uv run scripts/masterclass_pdf_md.py
"""

from __future__ import annotations

import json
import re
import sys
import unicodedata
from pathlib import Path

import pdfplumber

from qa_pipeline.pdsrg import (  # noqa: E402
    NOISE_MAX_SIZE, _inside_any_table, _line_from_extracted,
    assign_heading_levels, build_sections, classify_lines,
    extract_tables_hybrid, is_references_section, is_slide_deck,
    merge_wrapped_headings, running_header_texts, slide_sections, slugify,
)

TABLE_STATS = ("raw", "trivial", "prose_dropped", "text_fallback", "kept")


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def pdf_dir() -> Path:
    return repo_root() / "data" / "Masterclass" / "pdfs"


def out_dir() -> Path:
    return repo_root() / "processed" / "masterclass" / "md"


def clean_title(stem: str) -> str:
    """NFKC (full-width chars fold), collapse whitespace; else verbatim."""
    return re.sub(r"\s+", " ", unicodedata.normalize("NFKC", stem)).strip()


def looks_like_presentation(lines: list) -> bool:
    """Masterclass extension of the §6 slide-deck gate.

    ``is_slide_deck`` fires only on the Aptos template (Sleep Aid-era
    decks). The older Masterclass decks are Calibri/ArialMT with ALL text
    at 20pt+, so the flowing-document classifier marks every line a
    heading (>= SIZE_HEADING_MIN) and every section ends up bodyless and
    dropped — observed: 20-page deck -> 0 sections. Median non-noise line
    size >= 14pt separates the two worlds cleanly: PDSRG-style flowing
    docs run ~9-11pt body text, presentations 20pt+.
    """
    sizes = sorted(ln.size for ln in lines
                   if ln.text and ln.size >= NOISE_MAX_SIZE)
    if not sizes:
        return False
    return sizes[len(sizes) // 2] >= 14.0


def convert(path: Path) -> dict:
    """One PDF -> {slug, title, n_pages, slide, stats, sections, links}."""
    stem = path.stem
    title = clean_title(stem)
    stats = {k: 0 for k in TABLE_STATS}
    lines: list = []
    all_tables: list[dict] = []
    links: list[tuple[int, float, str]] = []
    with pdfplumber.open(path) as pdf:
        n_pages = len(pdf.pages)
        for page in pdf.pages:
            lines.extend(_line_from_extracted(ln, page.page_number)
                         for ln in page.extract_text_lines())
            all_tables.extend(extract_tables_hybrid(page, stats))
            for h in page.hyperlinks:
                uri = h.get("uri")
                if uri and uri.startswith("http"):
                    links.append((page.page_number, h.get("top", 0.0), uri))

    slide = is_slide_deck(lines) or looks_like_presentation(lines)
    headers = running_header_texts(lines, n_pages)
    lines = classify_lines(lines, slide, headers)

    tables_by_page: dict[int, list[dict]] = {}
    for t in all_tables:
        tables_by_page.setdefault(t["page"], []).append(t)
    lines = [ln for ln in lines
             if ln.kind == "noise"
             or not _inside_any_table(ln, tables_by_page.get(ln.page, []))]

    if slide:
        sections = slide_sections(lines, all_tables)
    else:
        flow = [("line", ln) for ln in lines if ln.kind != "noise"] \
            + [("table", t) for t in all_tables]
        flow.sort(key=lambda it: (it[1].page if it[0] == "line" else it[1]["page"],
                                  it[1].top if it[0] == "line" else it[1]["bbox"][1]))
        items = merge_wrapped_headings(flow)
        assign_heading_levels([o for k, o in items if k == "line"])
        sections = build_sections(items, title)

    links.sort(key=lambda t: (t[0], t[1], t[2]))
    return {"slug": slugify(stem), "title": title, "n_pages": n_pages,
            "slide": slide, "stats": stats, "sections": sections,
            "links": links}


def page_span(section) -> tuple[int, int]:
    pages = [p for _, _, p in section.blocks]
    return (min(pages), max(pages))


def render_markdown(doc: dict, rel_file: str) -> str:
    out = [f"# {doc['title']}", "",
           f"- file: `{rel_file}` ({doc['n_pages']} pages"
           + (", slide deck" if doc["slide"] else "") + ")",
           f"- tables kept: {doc['stats']['kept']}"
           f" (trivial dropped {doc['stats']['trivial']},"
           f" prose dropped {doc['stats']['prose_dropped']},"
           f" text fallback {doc['stats']['text_fallback']})",
           f"- external links: {len(doc['links'])}", ""]
    ref_paths = []
    for s in doc["sections"]:
        path = " > ".join(s.path)
        if is_references_section(s):
            ref_paths.append(path or "(untitled)")
        a, b = page_span(s)
        loc = f"p. {a}" if a == b else f"pp. {a}-{b}"
        if path:
            out.append(f"{'#' * min(len(s.path) + 1, 6)} {path} ({loc})")
        else:
            out.append(f"## (front matter) ({loc})")
        out.append("")
        for kind, text, _page in s.blocks:
            out.append(text)
            out.append("")
    if ref_paths:
        out.append(f"- reference sections kept (drop at chunking time): "
                   f"{'; '.join(ref_paths)}")
        out.append("")
    if doc["links"]:
        out.append("## Links")
        out.append("")
        seen: set[tuple[int, str]] = set()
        for page, _top, uri in doc["links"]:
            key = (page, uri)
            if key in seen:  # same page+uri from overlapping rects
                continue
            seen.add(key)
            out.append(f"- p.{page}: {uri}")
        out.append("")
    return "\n".join(out).rstrip() + "\n"


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    files = sorted(pdf_dir().glob("*.pdf"))
    if not files:
        print(f"FAIL: no PDFs under {pdf_dir()}", file=sys.stderr)
        return 2
    od = out_dir()
    od.mkdir(parents=True, exist_ok=True)

    docs_meta: list[dict] = []
    errors: list[str] = []
    for i, path in enumerate(files, 1):
        try:
            doc = convert(path)
        except Exception as exc:  # noqa: BLE001 — one bad PDF must not kill the run
            errors.append(f"{path.name}: {type(exc).__name__}: {exc}")
            print(f"  ERROR {path.name}: {exc}", file=sys.stderr)
            continue
        md = render_markdown(doc, path.name)
        (od / f"{doc['slug']}.md").write_text(md, encoding="utf-8")
        n_blocks = sum(len(s.blocks) for s in doc["sections"])
        docs_meta.append({
            "file": path.name, "slug": doc["slug"], "title": doc["title"],
            "n_pages": doc["n_pages"], "slide_deck": doc["slide"],
            "n_sections": len(doc["sections"]), "n_blocks": n_blocks,
            "n_links": len(doc["links"]), "tables": doc["stats"],
        })
        print(f"  [{i}/{len(files)}] {doc['title']}: {doc['n_pages']} pages, "
              f"{len(doc['sections'])} sections, {n_blocks} blocks, "
              f"tables kept={doc['stats']['kept']}, links={len(doc['links'])}"
              + (" [slide deck]" if doc["slide"] else ""))

    docs_meta.sort(key=lambda d: d["slug"])
    summary = {"n_docs": len(docs_meta), "errors": errors, "docs": docs_meta}
    (od / "summary.json").write_text(
        json.dumps(summary, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8")
    print(f"done: {len(docs_meta)}/{len(files)} converted, "
          f"{len(errors)} errors -> {od}")
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
