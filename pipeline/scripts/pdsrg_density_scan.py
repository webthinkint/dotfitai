"""PDSRG density scan (plan §6.1): rank all PDSRG PDFs by extraction difficulty.

Metrics per PDF: pages, chars/page, words/page, tables, table cells, and the
share of pages containing tables. Identifies the worst-case files the gate
test must deep-verify (plan names CreatineMonohydrate + Introduction docs;
we verify that assumption with data).
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import pdfplumber

from qa_pipeline.io_utils import configure_stdio


def scan(pdf_path: Path) -> dict:
    pages_chars = 0
    pages_words = 0
    n_tables = 0
    n_cells = 0
    pages_with_tables = 0
    n_pages = 0
    with pdfplumber.open(pdf_path) as pdf:
        for page in pdf.pages:
            n_pages += 1
            text = page.extract_text() or ""
            pages_chars += len(text)
            pages_words += len(text.split())
            tables = page.find_tables()
            if tables:
                pages_with_tables += 1
            n_tables += len(tables)
            for t in tables:
                data = t.extract()
                n_cells += sum(len(row) for row in data) if data else 0
    return {
        "file": pdf_path.name,
        "pages": n_pages,
        "chars_per_page": round(pages_chars / n_pages) if n_pages else 0,
        "words_per_page": round(pages_words / n_pages) if n_pages else 0,
        "tables": n_tables,
        "table_cells": n_cells,
        "pages_with_tables_pct": round(100 * pages_with_tables / n_pages) if n_pages else 0,
    }


def main(root: str) -> None:
    configure_stdio()
    rows = [scan(p) for p in sorted(Path(root).glob("*.pdf"))]
    # difficulty proxy: text density x table density
    rows.sort(key=lambda r: (r["chars_per_page"] * (1 + r["pages_with_tables_pct"] / 25)), reverse=True)
    print(f"{'file':60s} {'pages':>5} {'ch/pg':>6} {'tables':>6} {'cells':>6} {'tblpages%':>9}")
    for r in rows:
        print(f"{r['file'][:60]:60s} {r['pages']:>5} {r['chars_per_page']:>6} "
              f"{r['tables']:>6} {r['table_cells']:>6} {r['pages_with_tables_pct']:>9}")
    Path("out").mkdir(exist_ok=True)
    Path("out/pdsrg_density.json").write_text(json.dumps(rows, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main(sys.argv[1])
