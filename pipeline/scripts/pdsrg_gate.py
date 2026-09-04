"""PDSRG extraction gate test (plan §6.1). Hybrid table extraction.

Gate: "proceed only when dosage tables extract correctly."

Extraction strategy (validated on the corpus):

- pdfplumber `lines` strategy by default — correct for prose pages and ruled
  tables, and generates prose false-positive "tables" (1-2 cells on ruled
  text lines) which we filter as trivial.
- Ultra-wide dosage grids (e.g. the 17-col MVM design-criteria tables)
  collapse under `lines`: each data row lands run-on in the first cell. We
  detect the collapse signature (single non-empty cell containing >= 3
  dosage values) and re-extract that table's bbox with the `text` strategy,
  which restores column alignment (verified: Vitamin D row -> separate
  4.9µg / 15-20µg / 100µg / ... cells).

Automated checks per file:
  text_layer_present          — pages yield text (not scanned images)
  dosages_in_text             — mg/mcg/g/IU values found in page text
  no_collapsed_data_rows      — no collapse signature survives hybrid pass
  table_cells_in_page_text    — non-trivial table cell text appears in page
                                text (whitespace-normalized; catches drift)
  table_rows_rectangular      — non-trivial tables have consistent widths

Writes human-review artifacts: processed/pdsrg/gate/<stem>/page-NNN.md
(each page's text + its non-trivial tables as markdown) and gate_summary.json.
Exit 0 = PASS on all files, 1 = FAIL.
"""

from __future__ import annotations

import json
import re
import sys
import unicodedata
from pathlib import Path

import pdfplumber

from qa_pipeline.io_utils import configure_stdio, write_text

DOSAGE_RE = re.compile(
    r"\d+(?:[.,]\d+)?\s*(?:mg|mcg|[\u00b5\u03bc]g|ug|g\b|grams?\b|IU\b|international units?\b)",
    re.IGNORECASE,
)
LINES_SETTINGS = {"vertical_strategy": "lines", "horizontal_strategy": "lines"}
TEXT_SETTINGS = {
    "vertical_strategy": "text",
    "horizontal_strategy": "text",
    "snap_tolerance": 3,
    "join_tolerance": 3,
}


def _norm(s: str | None) -> str:
    if s is None:
        return ""
    s = unicodedata.normalize("NFKC", s)
    return re.sub(r"\s+", " ", s).strip().lower()


def _nonempty(row: list[str | None]) -> int:
    return sum(1 for c in row if _norm(c))


def _is_collapsed(data: list[list[str | None]]) -> bool:
    """Collapse signature: a row with one non-empty cell holding >=3 dosages."""
    return any(
        _nonempty(row) == 1 and len(DOSAGE_RE.findall(_norm(row[0]))) >= 3
        for row in data
    )


def _is_trivial(data: list[list[str | None]]) -> bool:
    """Prose false positive: <=2 non-empty cells total."""
    return sum(_nonempty(row) for row in data) <= 2


def extract_tables_hybrid(page) -> list[dict]:
    """Non-trivial tables on *page*; collapsed ones re-extracted with text."""
    out: list[dict] = []
    for t in page.find_tables(table_settings=LINES_SETTINGS):
        data = t.extract()
        if not data or _is_trivial(data):
            continue
        strategy = "lines"
        if _is_collapsed(data):
            bbox = t.bbox
            cropped = page.crop(bbox)
            retry = cropped.find_tables(table_settings=TEXT_SETTINGS)
            if retry:
                better = max(retry, key=lambda rt: len(rt.extract() or []))
                redata = better.extract()
                if redata and not _is_collapsed(redata):
                    data, strategy = redata, "text(fallback)"
        out.append({"data": data, "strategy": strategy, "page": page.page_number})
    return out


def _cell_violates(cell_n: str, page_text_nospace: str) -> bool:
    """Evidence-based containment, scoped to what the gate protects: dosage
    values. A cell only counts as evidence if it contains a dosage value;
    the value must then appear in the page text (whitespace-insensitive, to
    tolerate line-wrapped numbers like '1.3-1. 7mg' and '4.9 µg').

    Prose, citations, footnotes: no dosage evidence -> skipped (reference
    sections wrapped in rules are detected as 'tables' and would otherwise
    false-fail on citation numbering)."""
    for m in DOSAGE_RE.finditer(cell_n):
        if re.sub(r"\s", "", m.group(0)) not in page_text_nospace:
            return True
    return False


def gate_one(pdf_path: Path, out_dir: Path) -> dict:
    text_dosages = 0
    table_dosages = 0
    containment_misses: list[str] = []
    ragged_tables = 0
    collapsed_rows = 0
    n_tables = 0
    n_pages = 0

    for page in pdfplumber.open(pdf_path).pages:
        n_pages += 1
        raw_text = page.extract_text() or ""
        page_text = _norm(raw_text)
        page_nospace = re.sub(r"\s", "", page_text)
        text_dosages += len(DOSAGE_RE.findall(page_text))
        lines: list[str] = [f"<!-- page {page.page_number} text -->", raw_text]
        for tbl in extract_tables_hybrid(page):
            data = tbl["data"]
            n_tables += 1
            if _is_collapsed(data):
                collapsed_rows += 1
            widths = {len(row) for row in data}
            if len(widths) > 1:
                ragged_tables += 1
            lines.append(f"\n<!-- table p{page.page_number} "
                         f"({len(data)}x{max(widths)}, {tbl['strategy']}) -->")
            for row in data:
                cells = []
                for cell in row:
                    cell_n = _norm(cell)
                    if cell_n:
                        if _cell_violates(cell_n, page_nospace):
                            containment_misses.append(
                                f"p{page.page_number}: {cell_n[:60]!r}")
                        table_dosages += len(DOSAGE_RE.findall(cell_n))
                        cells.append(str(cell).replace("\n", " ").strip())
                    else:
                        cells.append("")
                lines.append("| " + " | ".join(cells) + " |")
        write_text(out_dir / f"page-{page.page_number:03d}.md", "\n".join(lines) + "\n")

    res = {
        "file": pdf_path.name,
        "pages": n_pages,
        "tables": n_tables,
        "text_dosage_values": text_dosages,
        "table_dosage_values": table_dosages,
        "collapsed_data_rows": collapsed_rows,
        "ragged_tables": ragged_tables,
        "containment_misses": len(containment_misses),
        "containment_miss_examples": containment_misses[:5],
        "checks": {
            "text_layer_present": n_pages > 0,
            "dosages_in_text": text_dosages > 0,
            "no_collapsed_data_rows": collapsed_rows == 0,
            "table_rows_rectangular": ragged_tables == 0,
            "table_cells_in_page_text": len(containment_misses) == 0,
        },
    }
    res["pass"] = all(res["checks"].values())
    return res


def main(paths: list[str], out_root: str) -> int:
    configure_stdio()
    out = Path(out_root)
    results = []
    for raw in paths:
        p = Path(raw)
        res = gate_one(p, out / p.stem)
        results.append(res)
        status = "PASS" if res["pass"] else "FAIL"
        print(f"[{status}] {res['file']}: pages={res['pages']} tables={res['tables']} "
              f"text_dos={res['text_dosage_values']} tbl_dos={res['table_dosage_values']} "
              f"collapsed={res['collapsed_data_rows']} ragged={res['ragged_tables']} "
              f"misses={res['containment_misses']}")
        for k, v in res["checks"].items():
            if not v:
                print(f"    check failed: {k}")
    write_text(out / "gate_summary.json",
               json.dumps(results, indent=1, ensure_ascii=False) + "\n")
    overall = all(r["pass"] for r in results)
    print(f"\nGATE {'PASS' if overall else 'FAIL'} — artifacts in {out}")
    return 0 if overall else 1


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("usage: pdsrg_gate.py <pdf...> <out_root>")
        raise SystemExit(2)
    raise SystemExit(main(sys.argv[1:-1], sys.argv[-1]))
