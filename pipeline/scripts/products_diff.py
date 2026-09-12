"""Diff two ``products.json`` exports (open item 2, the freshness cadence).

`products.json` is the **legal-approved claims corpus** (§3): the assistant
quotes it verbatim and never paraphrases it, so a re-export that silently
rewords a claim changes what the assistant is allowed to say. The monthly diff
this supports is not a data-hygiene chore — it is the review that catches that.

What it reports, in the order that matters:

1. **Claim-bearing section changes** — a section whose text moved, per SKU. The
   §5 section split (`index_build.split_sections`) is reused so the unit is the
   same one the index builds documents from: a changed ``description`` is a
   changed claim document, and its old embedding is now stale.
2. **Added / removed SKUs** — a removed SKU with corpus mentions becomes an
   alias-table currency question (§5, `docs/v1/decisions.md`: discontinued with no
   successor stays indexable and tags nothing).
3. **Renames** — ``longname`` changed on the same ``part_no``. A rename is an
   identity mapping and never supersedes (AGENTS.md), so it belongs in the
   alias table's rename section, not its replacement section. This tool cannot
   tell a rename from a reformulation that kept the part_no; it reports the
   change and says so.
4. **URL changes** — every product document's ``citation_url``.

Naming the owner and setting the cadence is still item 2 and still a decision;
this is only the instrument. It writes nothing.

    uv run python scripts/products_diff.py OLD.json NEW.json
    uv run python scripts/products_diff.py OLD.json NEW.json --json
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from qa_pipeline.index_build import split_sections
from qa_pipeline.io_utils import configure_stdio


def load(path: Path) -> dict[str, dict]:
    products = json.loads(path.read_text(encoding="utf-8"))
    return {str(p["part_no"]): p for p in products}


def sections_of(product: dict) -> dict[str, str]:
    return dict(split_sections(product.get("searchcontent") or ""))


def diff(old: dict[str, dict], new: dict[str, dict]) -> dict:
    added = sorted(set(new) - set(old))
    removed = sorted(set(old) - set(new))
    both = sorted(set(old) & set(new))

    renames, url_changes, section_changes = [], [], []
    for part_no in both:
        before, after = old[part_no], new[part_no]
        if before.get("longname") != after.get("longname"):
            renames.append({"part_no": part_no,
                            "from": before.get("longname"),
                            "to": after.get("longname")})
        if before.get("URL") != after.get("URL"):
            url_changes.append({"part_no": part_no, "from": before.get("URL"),
                                "to": after.get("URL")})

        old_sections, new_sections = sections_of(before), sections_of(after)
        changed = sorted(s for s in set(old_sections) & set(new_sections)
                         if old_sections[s] != new_sections[s])
        gained = sorted(set(new_sections) - set(old_sections))
        lost = sorted(set(old_sections) - set(new_sections))
        if changed or gained or lost:
            section_changes.append({
                "part_no": part_no,
                "longname": after.get("longname"),
                "changed": changed, "added": gained, "removed": lost,
            })

    return {
        "n_old": len(old), "n_new": len(new),
        "added_skus": [{"part_no": p, "longname": new[p].get("longname")}
                       for p in added],
        "removed_skus": [{"part_no": p, "longname": old[p].get("longname")}
                         for p in removed],
        "renames": renames,
        "url_changes": url_changes,
        "section_changes": section_changes,
        "n_documents_to_re_embed": sum(
            len(c["changed"]) + len(c["added"]) for c in section_changes),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("old", help="previous products.json")
    parser.add_argument("new", help="new products.json export")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()
    configure_stdio()

    report = diff(load(Path(args.old).resolve()), load(Path(args.new).resolve()))
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=1))
        return 0

    print(f"products.json: {report['n_old']} SKUs -> {report['n_new']}")
    print()

    if report["section_changes"]:
        print(f"## claim-bearing section changes "
              f"({len(report['section_changes'])} SKUs)")
        print("   Approved copy moved. Every changed section is a changed §9 "
              "document whose embedding is now stale.")
        for change in report["section_changes"]:
            parts = []
            if change["changed"]:
                parts.append("changed: " + ", ".join(change["changed"]))
            if change["added"]:
                parts.append("new: " + ", ".join(change["added"]))
            if change["removed"]:
                parts.append("gone: " + ", ".join(change["removed"]))
            print(f"   - {change['part_no']} {change['longname']} "
                  f"— {'; '.join(parts)}")
        print(f"   -> {report['n_documents_to_re_embed']} document(s) to "
              "re-embed on the next index build")
        print()

    for key, title, note in (
        ("added_skus", "added SKUs",
         "New claim documents; check the alias table covers the new names (§5)."),
        ("removed_skus", "removed SKUs",
         "A removed SKU still mentioned in the corpus is a currency question, "
         "not a deletion — see docs/v1/decisions.md."),
    ):
        if report[key]:
            print(f"## {title} ({len(report[key])})")
            print(f"   {note}")
            for row in report[key]:
                print(f"   - {row['part_no']} {row['longname']}")
            print()

    if report["renames"]:
        print(f"## longname changes ({len(report['renames'])})")
        print("   A rename is an identity mapping and never supersedes "
              "(AGENTS.md). This cannot distinguish a rename from a "
              "reformulation that kept its part_no — confirm before filing it "
              "in the alias table's rename section.")
        for row in report["renames"]:
            print(f"   - {row['part_no']}: {row['from']!r} -> {row['to']!r}")
        print()

    if report["url_changes"]:
        print(f"## URL changes ({len(report['url_changes'])})")
        print("   Each is a product document's citation_url.")
        for row in report["url_changes"]:
            print(f"   - {row['part_no']}: {row['from']} -> {row['to']}")
        print()

    if not any(report[k] for k in ("added_skus", "removed_skus", "renames",
                                   "url_changes", "section_changes")):
        print("no differences")
    return 0


if __name__ == "__main__":
    sys.exit(main())
