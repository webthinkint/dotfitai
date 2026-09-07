"""CLI entry points: ``stage0``, ``stage1``, ``stage2``, ``stage4``, ``run``
(stages 0+1), ``aliases``, ``pdsrg``, ``podcast``, ``index``, ``golden``.
Per-subcommand contracts live in the module each one drives; what follows is
the QA-stage layout, which this module owns.

Designed for identical behavior on Windows (dev) and Linux (production):

    uv run qa-pipeline run --input data/QAs --out processed/qa

Output layout (mirrors the input tree so files are traceable by path):

    <out>/stage0/text/<year>/<subdirs>/<name>.txt     scrubbed text
    <out>/stage0/reports/<year>/<subdirs>/<name>.json per-file scrub report
    <out>/stage1/documents.jsonl                      one record per parsed doc
    <out>/stage1/review_queue.jsonl                   errors + residual PII + edge cases
    <out>/stage1/summary.json
    <out>/runs/*.json                                 run manifests (audit trail)

Stage 2 (``stage2``) reads Stage 1 output + products.json and writes into its
own out dir (default ``processed/qa/stage2/``): ``documents.jsonl`` (one
canonical record per doc), ``review_queue.jsonl``, ``summary.json`` and
``runs/`` (manifests + the gitignored ``stage2_cache.jsonl`` LLM cache).

Per-file outputs are deterministic (no timestamps inside them); run manifests
carry the timestamps/versioning for the audit trail.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import platform
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import docx as docx_pkg

from . import __version__
from .alias import build_alias_table, harvest_candidates, norm as _norm, \
    write_curation_worksheet
from .azure_config import (
    AzureConfigError, REQUIRE_INDEX, REQUIRE_OPENAI_EMBEDDING,
    REQUIRE_OPENAI_SMALL_CHAT, load_azure_config,
)
from .embeddings import EMBEDDING_API_VERSION, Embedder
from .extract import extract_docx
from .golden import (
    ADVERSARIAL_SIZE, GOLDEN_VERSION, run_golden, write_adversarial_worksheet,
    write_worksheet,
)
from .index_build import (
    INDEX_NAME, build_documents, delete_documents, embed_text, ensure_index,
    list_index_ids, read_menu_rows, upload_documents,
)
from .io_utils import (
    configure_stdio, doc_id, iter_docx, read_jsonl, rel_posix, sha256_file,
    write_json, write_text,
)
from .pdsrg import chunk_pdf, chunk_records, review_outline
from .podcast import (
    clean_title, resolve_titles, segment_episode, summarize as summarize_podcast,
)
from .scrub import scrub_docx
from .stage1 import classify_and_parse, load_scrub_report, review_reasons
from .stage2 import (
    CHAT_API_VERSION, MIN_CONFIDENCE_DEFAULT, PROMPT_VERSION, Extractor,
    append_cache, cache_key, load_cache, review_reasons as stage2_reasons,
    run_stage2, summarize as summarize_stage2,
)
from .stage4 import (
    CURRENCY_PROMPT_VERSION, CURRENCY_SCHEMA, MIN_JUDGE_CONFIDENCE_DEFAULT,
    SIMILARITY_THRESHOLD, currency_cache_key, run_stage4,
    summarize as summarize_stage4,
)


def _utcnow() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _manifest(out_dir: Path, stage: str, args: argparse.Namespace,
              files: list[Path], input_root: Path, extra: dict) -> Path:
    runs = out_dir / "runs"
    runs.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    manifest = {
        "pipeline_version": __version__,
        "stage": stage,
        "utc": _utcnow(),
        "args": {k: v for k, v in vars(args).items() if k != "func"},
        "python": platform.python_version(),
        "platform": platform.platform(),
        "python_docx": getattr(docx_pkg, "__version__", "unknown"),
        "input_root": str(input_root),
        "n_files": len(files),
        "inputs": [
            {"path": rel_posix(p, input_root), "sha256": sha256_file(p),
             "bytes": p.stat().st_size}
            for p in files
        ],
        **extra,
    }
    path = runs / f"{stage}-{stamp}.json"
    write_json(path, manifest)
    return path


def _select_files(input_root: Path, include: list[str] | None, limit: int | None) -> list[Path]:
    files = iter_docx(input_root)
    if include:
        patterns = include
        files = [f for f in files
                 if any(fnmatch.fnmatch(rel_posix(f, input_root), pat) for pat in patterns)]
    if limit is not None:
        files = files[:limit]
    return files


def _errors_path(out_root: Path) -> Path:
    return out_root / "stage0" / "errors.json"


def _read_stage0_errors(out_root: Path) -> dict:
    """Parse errors recorded by the last stage0 run.

    Deliberately a committed artifact rather than a ``runs/`` manifest: the
    manifests are gitignored, so a fresh clone or the prod host would
    otherwise rebuild an empty review queue and silently lose the parse
    errors it is supposed to route to the data owner.
    """
    path = _errors_path(out_root)
    if not path.is_file():
        return {"filtered": False, "files": []}
    return json.loads(path.read_text(encoding="utf-8"))


def _prune_orphans(root: Path, expected: set[Path]) -> int:
    """Delete outputs whose input no longer exists (see AGENTS.md: outputs are
    regenerated, and a byte-identical rerun cannot detect a stale file)."""
    if not root.is_dir():
        return 0
    removed = 0
    for path in sorted(root.rglob("*")):
        if path.is_file() and path not in expected:
            path.unlink()
            removed += 1
    for directory in sorted((d for d in root.rglob("*") if d.is_dir()),
                            reverse=True):
        try:
            directory.rmdir()          # only succeeds when empty
        except OSError:
            pass
    return removed


def cmd_stage0(args: argparse.Namespace) -> int:
    input_root = Path(args.input).resolve()
    out_root = Path(args.out).resolve()
    if not input_root.is_dir():
        print(f"error: input dir not found: {input_root}", file=sys.stderr)
        return 2
    files = _select_files(input_root, args.include, args.limit)

    text_dir = out_root / "stage0" / "text"
    reports_dir = out_root / "stage0" / "reports"
    errors: list[dict] = []
    totals: dict[str, int] = {}
    n_ok = n_err = 0
    filtered = bool(args.include or args.limit is not None)
    expected_text: set[Path] = set()
    expected_reports: set[Path] = set()

    for i, path in enumerate(files, 1):
        rel = rel_posix(path, input_root)
        text_path = text_dir / Path(rel).with_suffix(".txt")
        report_path = reports_dir / Path(rel).with_suffix(".json")

        extraction = extract_docx(path)
        scrubbed, rep = scrub_docx(extraction)

        record = {"source_file": rel, "id": doc_id(rel), **rep.to_dict()}
        write_json(report_path, record)
        expected_reports.add(report_path)

        if rep.ok:
            write_text(text_path, scrubbed + "\n" if scrubbed is not None else "")
            expected_text.add(text_path)
            n_ok += 1
            for k, v in rep.redactions.items():
                totals[f"redactions.{k}"] = totals.get(f"redactions.{k}", 0) + v
            for k, v in rep.dropped.items():
                totals[f"dropped.{k}"] = totals.get(f"dropped.{k}", 0) + v
            if rep.residual_pii_flag:
                totals["residual_pii_files"] = totals.get("residual_pii_files", 0) + 1
        else:
            n_err += 1
            errors.append(record)

        if not args.quiet and i % 100 == 0:
            print(f"  stage0: {i}/{len(files)}", flush=True)

    write_json(_errors_path(out_root), {
        "pipeline_version": __version__,
        "filtered": filtered,
        "n_errors": n_err,
        "files": sorted(({"source_file": e["source_file"], "error": e["error"]}
                         for e in errors), key=lambda e: e["source_file"]),
    })

    n_pruned = 0
    if filtered:
        print("  note: filtered run (--include/--limit) — stale outputs kept "
              "and stage1 will still process the whole tree")
    elif not args.no_prune:
        n_pruned = (_prune_orphans(text_dir, expected_text)
                    + _prune_orphans(reports_dir, expected_reports))
        if n_pruned:
            print(f"  pruned {n_pruned} output file(s) with no matching input")

    manifest_extra = {
        "ok": n_ok, "errors": n_err, "totals": totals, "filtered": filtered,
        "pruned": n_pruned,
        "error_files": [e["source_file"] for e in errors],
    }
    mpath = _manifest(out_root, "stage0", args, files, input_root, manifest_extra)
    print(f"stage0 done: {n_ok} scrubbed, {n_err} errors (manual queue), "
          f"manifest: {mpath}")
    for e in errors:
        print(f"  ERROR {e['source_file']}: {e['error']}")
    return 0 if n_err == 0 or not args.fail_on_error else 1


def cmd_stage1(args: argparse.Namespace) -> int:
    out_root = Path(args.out).resolve()
    text_dir = out_root / "stage0" / "text"
    reports_dir = out_root / "stage0" / "reports"
    if not text_dir.is_dir():
        print(f"error: stage0 output not found (run stage0 first): {text_dir}",
              file=sys.stderr)
        return 2

    stage1_dir = out_root / "stage1"
    stage1_dir.mkdir(parents=True, exist_ok=True)

    texts = sorted(text_dir.rglob("*.txt"))
    documents: list[dict] = []
    review: list[dict] = []
    excluded: list[dict] = []
    by_type: dict[str, int] = {}
    by_year: dict[str, int] = {}

    for i, text_path in enumerate(texts, 1):
        rel = text_path.relative_to(text_dir).as_posix()
        rel = rel[: -len(".txt")] + ".docx" if rel.endswith(".txt") else rel

        lines = text_path.read_text(encoding="utf-8").split("\n")
        rep = load_scrub_report(reports_dir, rel)
        rec = classify_and_parse(lines, rel, scrub_report=rep)

        # owner disposition 2026-09-02: documents with no expert-answer text
        # (stubs, image-only exports, question-only forwards) are not
        # indexed — excluded from documents.jsonl, tallied in summary.json,
        # and NOT queued (there is nothing left to decide about them)
        if not rec["expert_section"].strip():
            reason = ("no_expert_answer" if rec["doc_type"] == "qa_email"
                      else "empty_document")
            excluded.append({"source_file": rec["source_file"],
                             "reason": reason})
            continue

        documents.append(rec)

        if rec["doc_type"] == "qa_email":
            by_type["qa_email"] = by_type.get("qa_email", 0) + 1
        elif rec["doc_type"] == "expert_note":
            by_type["expert_note"] = by_type.get("expert_note", 0) + 1
        else:
            by_type["other"] = by_type.get("other", 0) + 1
        by_year[str(rec["year"])] = by_year.get(str(rec["year"]), 0) + 1

        if rec["needs_review"]:
            review.append({
                "source_file": rec["source_file"],
                "reasons": review_reasons(rec),
            })

        if not args.quiet and i % 100 == 0:
            print(f"  stage1: {i}/{len(texts)}", flush=True)

    # parse errors recorded by the last stage0 run join the manual queue
    stage0_errors = _read_stage0_errors(out_root)
    if stage0_errors.get("filtered"):
        print("  warning: stage0 last ran with --include/--limit; this stage1 "
              "covers the whole tree, so counts mix runs", file=sys.stderr)
    for err in stage0_errors.get("files", []):
        review.append({"source_file": err["source_file"],
                       "reasons": ["parse_error"]})

    docs_path = stage1_dir / "documents.jsonl"
    review_path = stage1_dir / "review_queue.jsonl"
    documents.sort(key=lambda r: r["source_file"])
    with docs_path.open("w", encoding="utf-8", newline="\n") as f:
        for rec in documents:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    with review_path.open("w", encoding="utf-8", newline="\n") as f:
        for rec in sorted(review, key=lambda r: r["source_file"]):
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")

    summary = {
        "pipeline_version": __version__,
        "n_documents": len(documents),
        "n_excluded": {
            "no_expert_answer": sum(1 for e in excluded
                                    if e["reason"] == "no_expert_answer"),
            "empty_document": sum(1 for e in excluded
                                  if e["reason"] == "empty_document"),
        },
        "excluded_files": sorted(excluded, key=lambda e: e["source_file"]),
        "by_doc_type": by_type,
        "by_year": by_year,
        "n_review_queue": sum(1 for _ in open(review_path, encoding="utf-8")),
        "with_question_extracted": sum(1 for r in documents if r["question"]),
        "with_thread_date": sum(1 for r in documents if r["thread_date"]),
    }
    write_json(stage1_dir / "summary.json", summary)
    print(f"stage1 done: {summary['n_documents']} documents, "
          f"{summary['n_review_queue']} in review queue")
    print(f"  by type: {by_type}")
    print(f"  by year: {dict(sorted(by_year.items()))}")
    return 0


def cmd_stage2(args: argparse.Namespace) -> int:
    qa_docs_path = Path(args.qa_docs).resolve()
    products_path = Path(args.products).resolve()
    if not qa_docs_path.is_file():
        print(f"error: --qa-docs not found: {qa_docs_path}", file=sys.stderr)
        return 2
    if not products_path.is_file():
        print(f"error: --products not found: {products_path}", file=sys.stderr)
        return 2
    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    docs = read_jsonl(qa_docs_path)
    if args.include:
        docs = [d for d in docs
                if any(fnmatch.fnmatch(d.get("source_file", ""), pat)
                       for pat in args.include)]
    docs.sort(key=lambda d: d.get("source_file", ""))
    if args.limit is not None:
        docs = docs[: args.limit]

    products = json.loads(products_path.read_text(encoding="utf-8"))
    alias_table = build_alias_table(products)

    min_conf = float(args.min_confidence)
    api_version = args.api_version
    cache_path = out_dir / "runs" / "stage2_cache.jsonl"
    use_cache = not args.no_cache
    cache = load_cache(cache_path) if use_cache else {}

    llm_call = None
    deployment = "rule-based"
    if not args.no_llm:
        try:
            cfg = load_azure_config(require=REQUIRE_OPENAI_SMALL_CHAT)
        except AzureConfigError as e:
            print(f"error: {e}", file=sys.stderr)
            return 2
        deployment = cfg.small_chat_deployment
        extractor = Extractor(cfg.openai_endpoint, cfg.openai_api_key,
                              deployment, api_version)

        def llm_call(messages: list, _ex: Extractor = extractor) -> dict:  # type: ignore[misc]
            return _ex(messages)

    def key_fn(rec: dict) -> str:
        return cache_key(deployment, api_version, rec)

    def write_fn(key: str, value: dict) -> None:
        append_cache(cache_path, key, value)

    records, stats = run_stage2(
        docs, alias_table, llm_call, deployment, min_conf,
        cache=cache, cache_write=(write_fn if use_cache else None),
        cache_key_fn=key_fn)

    docs_path = out_dir / "documents.jsonl"
    with docs_path.open("w", encoding="utf-8", newline="\n") as f:
        for rec in records:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    review = [{"source_file": r["source_file"],
               "reasons": stage2_reasons(r, min_conf)}
              for r in records if r["needs_review"]]
    review.sort(key=lambda r: r["source_file"])
    with (out_dir / "review_queue.jsonl").open("w", encoding="utf-8",
                                                   newline="\n") as f:
        for rec in review:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")

    summary: dict[str, Any] = {
        "pipeline_version": __version__,
        "options": {
            "min_confidence": min_conf,
            "api_version": api_version,
            "prompt_version": PROMPT_VERSION,
            "deployment": deployment,
            "no_llm": bool(args.no_llm),
            "no_cache": bool(args.no_cache),
            "include": args.include,
            "limit": args.limit,
        },
        **summarize_stage2(records, min_conf),
        "llm": {**stats, "deployment": deployment,
                  "api_version": api_version,
                  "prompt_version": PROMPT_VERSION},
    }
    write_json(out_dir / "summary.json", summary)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    write_json(out_dir / "runs" / f"stage2-{stamp}.json", {
        "pipeline_version": __version__, "stage": "stage2", "utc": _utcnow(),
        "args": {k: v for k, v in vars(args).items() if k != "func"},
        "inputs": {"qa_docs": sha256_file(qa_docs_path),
                   "products": sha256_file(products_path)},
        "n_documents": len(records), "n_review": len(review),
        "stats": stats,
    })
    if not args.quiet:
        for r in records[:3]:
            print(f"  {r['source_file']}: products={r['products']} "
                  f"conf={r['confidence']} containment={r['containment_score']} "
                  f"review={r['needs_review']}")
    print(f"stage2 done: {len(records)} documents, {len(review)} in review "
          f"queue ({stats['n_llm_calls']} LLM call(s), "
          f"{stats['n_cache_hits']} cache hit(s), {stats['n_fallback']} fallback)")
    return 1 if stats["n_llm_errors"] and args.fail_on_error else 0


def cmd_stage4(args: argparse.Namespace) -> int:
    qa_docs_path = Path(args.qa_docs).resolve()
    products_path = Path(args.products).resolve()
    if not qa_docs_path.is_file():
        print(f"error: --qa-docs not found: {qa_docs_path}", file=sys.stderr)
        return 2
    if not products_path.is_file():
        print(f"error: --products not found: {products_path}", file=sys.stderr)
        return 2
    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    docs = read_jsonl(qa_docs_path)
    if args.include:
        docs = [d for d in docs
                if any(fnmatch.fnmatch(d.get("source_file", ""), pat)
                       for pat in args.include)]
    docs.sort(key=lambda d: d.get("source_file", ""))
    if args.limit is not None:
        docs = docs[: args.limit]

    alias_table = build_alias_table(
        json.loads(products_path.read_text(encoding="utf-8")))

    require = REQUIRE_OPENAI_EMBEDDING if args.no_llm else (
        REQUIRE_OPENAI_EMBEDDING + REQUIRE_OPENAI_SMALL_CHAT)
    try:
        cfg = load_azure_config(require=require)
    except AzureConfigError as e:
        print(f"error: {e}", file=sys.stderr)
        return 2

    api_version = args.api_version
    embedder = Embedder(
        cfg.openai_endpoint, cfg.openai_api_key, cfg.embedding_deployment,
        EMBEDDING_API_VERSION, cache_path=out_dir / "runs" / "embeddings.jsonl",
    )

    def embed_call(texts: list[str]) -> list[list[float]]:
        return embedder.embed(texts)

    judge_call = None
    deployment = "rule-based"
    if not args.no_llm:
        deployment = cfg.small_chat_deployment
        judge = Extractor(cfg.openai_endpoint, cfg.openai_api_key,
                          deployment, api_version,
                          schema=CURRENCY_SCHEMA,
                          schema_name="qa_stage4_currency")

        def judge_call(messages: list, _j: Extractor = judge) -> dict:  # type: ignore[misc]
            return _j(messages)

    cache_path = out_dir / "runs" / "stage4_cache.jsonl"
    use_cache = not args.no_cache
    cache = load_cache(cache_path) if use_cache else {}

    def key_fn(rec: dict) -> str:
        return currency_cache_key(deployment, api_version, rec)

    def write_fn(key: str, value: dict) -> None:
        append_cache(cache_path, key, value)

    min_judge = float(args.min_judge_confidence)
    records, worksheet, review, stats = run_stage4(
        docs, alias_table, embed_call, judge_call, deployment,
        threshold=SIMILARITY_THRESHOLD, min_judge_confidence=min_judge,
        cache=cache, cache_write=(write_fn if use_cache else None),
        cache_key_fn=key_fn)
    embedder.save_cache()

    docs_path = out_dir / "documents.jsonl"
    with docs_path.open("w", encoding="utf-8", newline="\n") as f:
        for rec in records:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    with (out_dir / "clusters.jsonl").open("w", encoding="utf-8",
                                             newline="\n") as f:
        for row in worksheet:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")
    with (out_dir / "review_queue.jsonl").open("w", encoding="utf-8",
                                                 newline="\n") as f:
        for rec in review:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")

    summary: dict[str, Any] = {
        "pipeline_version": __version__,
        "options": {
            "similarity_threshold": SIMILARITY_THRESHOLD,
            "min_judge_confidence": min_judge,
            "api_version": api_version,
            "currency_prompt_version": CURRENCY_PROMPT_VERSION,
            "deployment": deployment,
            "no_llm": bool(args.no_llm),
            "no_cache": bool(args.no_cache),
            "include": args.include,
            "limit": args.limit,
        },
        **summarize_stage4(records, worksheet),
        "embedding": {
            "deployment": cfg.embedding_deployment,
            "api_version": EMBEDDING_API_VERSION,
            "n_cache_hits": embedder.n_cache_hits,
            "n_api_calls": embedder.n_api_calls,
        },
        "judge": {**stats, "deployment": deployment,
                  "api_version": api_version,
                  "currency_prompt_version": CURRENCY_PROMPT_VERSION},
    }
    write_json(out_dir / "summary.json", summary)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    write_json(out_dir / "runs" / f"stage4-{stamp}.json", {
        "pipeline_version": __version__, "stage": "stage4", "utc": _utcnow(),
        "args": {k: v for k, v in vars(args).items() if k != "func"},
        "inputs": {"qa_docs": sha256_file(qa_docs_path),
                   "products": sha256_file(products_path)},
        "n_documents": len(records), "n_clusters": len(worksheet),
        "n_review": len(review), "stats": stats,
    })
    if not args.quiet:
        for w in worksheet[:3]:
            canon = w["canonical"]["source_file"] if w["canonical"] else "(none)"
            print(f"  cluster {w['cluster_id']}: {w['size']} members, "
                  f"canonical {canon}, conflict={w['conflict']}")
    print(f"stage4 done: {len(records)} records, "
          f"{stats['n_clusters']} cluster(s) ({stats['n_conflict_clusters']} "
          f"conflict, {stats['n_audit_clusters']} audit), "
          f"{summarize_stage4(records, worksheet)['n_current']} current, "
          f"{len(review)} in review queue "
          f"({stats['n_judge_calls']} judge call(s), "
          f"{stats['n_judge_cache_hits']} judge cache hit(s))")
    return 1 if stats["n_judge_errors"] and args.fail_on_error else 0


def cmd_aliases(args: argparse.Namespace) -> int:
    products_path = Path(args.products).resolve()
    if not products_path.is_file():
        print(f"error: products.json not found: {products_path}", file=sys.stderr)
        return 2
    products = json.loads(products_path.read_text(encoding="utf-8"))

    table = build_alias_table(products)
    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    table_path = out_dir / "alias_table.json"
    write_json(table_path, table)
    print(f"alias table: {table['n_families']} families from "
          f"{table['n_products_indexed']} indexed products "
          f"({table['n_products']} SKUs incl. gear) -> {table_path}")
    for ren in table["legacy_renames"]:
        print(f"  legacy: {ren['deprecated']} -> {ren['current_family']} "
              f"({ren['source']})")

    docs_path = Path(args.qa_docs).resolve() if args.qa_docs else None
    if docs_path and docs_path.is_file():
        candidates = harvest_candidates(table, docs_path)
        ws_path = out_dir / "curation_worksheet.md"
        write_curation_worksheet(ws_path, candidates)
        hits = [c for c in candidates if c["n_docs"]]
        print(f"curation worksheet: {len(hits)}/{len(candidates)} candidates "
              f"found in corpus -> {ws_path}")
    return 0


def _iter_pdfs(input_root: Path, include: list[str] | None,
               limit: int | None) -> list[Path]:
    files = sorted(p for p in input_root.rglob("*.pdf") if p.is_file())
    if include:
        files = [f for f in files
                 if any(fnmatch.fnmatch(rel_posix(f, input_root), pat)
                        for pat in include)]
    if limit is not None:
        files = files[:limit]
    return files


def cmd_pdsrg(args: argparse.Namespace) -> int:
    input_root = Path(args.input).resolve()
    out_root = Path(args.out).resolve()
    if not input_root.is_dir():
        print(f"error: input dir not found: {input_root}", file=sys.stderr)
        return 2
    products_path = Path(args.products).resolve()
    if not products_path.is_file():
        print(f"error: products.json not found: {products_path}", file=sys.stderr)
        return 2

    alias_families = {_norm(f["family"]): f for f in build_alias_table(
        json.loads(products_path.read_text(encoding="utf-8")))["families"]}

    files = _iter_pdfs(input_root, args.include, args.limit)
    chunks_dir = out_root / "chunks"
    review_dir = out_root / "review"
    chunks_dir.mkdir(parents=True, exist_ok=True)
    review_dir.mkdir(parents=True, exist_ok=True)

    all_records: list[dict] = []
    docs: list[dict] = []
    errors: list[str] = []
    for i, path in enumerate(files, 1):
        try:
            result = chunk_pdf(path, alias_families,
                               keep_references=args.keep_references)
        except Exception as exc:  # noqa: BLE001 — one bad PDF must not kill the run
            errors.append(f"{path.name}: {type(exc).__name__}: {exc}")
            print(f"  ERROR {path.name}: {type(exc).__name__}: {exc}", file=sys.stderr)
            continue
        doc = result["doc"]
        # relative to the corpus root, never the working directory: the old
        # cwd-relative form emitted absolute dev paths (C:/Dev/...) whenever
        # the CLI ran from pipeline/, so identical inputs produced different
        # bytes depending on where the command was invoked
        doc["source_file"] = rel_posix(path, input_root)
        records = chunk_records(doc, result["chunks"],
                                citation_base=args.citation_base)
        all_records.extend(records)
        docs.append(doc)
        write_text(review_dir / f"{doc['slug']}.md",
                   review_outline(doc, result["sections"]))
        if not args.quiet:
            print(f"  [{i}/{len(files)}] {doc['doc_title']}: "
                  f"{doc['n_chunks']} chunks, {doc['n_sections']} sections, "
                  f"tables kept={doc['tables']['kept']} "
                  f"prose_dropped={doc['tables']['prose_dropped']}")

    records_path = chunks_dir / "chunks.jsonl"
    with records_path.open("w", encoding="utf-8", newline="\n") as f:
        for rec in all_records:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")

    totals_tables: dict[str, int] = {}
    for d in docs:
        for k, v in d["tables"].items():
            totals_tables[k] = totals_tables.get(k, 0) + v
    summary = {
        "pipeline_version": __version__,
        "options": {"keep_references": bool(args.keep_references),
                    "citation_base": args.citation_base,
                    "target_tokens": 650, "max_tokens": 800},
        "n_docs": len(docs),
        "n_chunks": len(all_records),
        "n_tokens": sum(r["n_tokens"] for r in all_records),
        "tables": totals_tables,
        "by_category": _count_by(docs, "category"),
        "n_product_docs": sum(1 for d in docs if d["family"]),
        "n_topic_docs": sum(1 for d in docs if not d["family"]),
        "review_flags": {
            "discontinued_products": sorted(
                {f"{d['family']} ({d['status']})" for d in docs
                 if d.get("status")}),
            "needs_category": sorted(d["stem"] for d in docs
                                      if d["needs_category"]),
            "errors": errors,
        },
        "docs": docs,
    }
    write_json(chunks_dir / "summary.json", summary)
    mpath = _manifest(out_root, "pdsrg", args, files, input_root, {
        "ok": len(docs), "errors": len(errors), "n_chunks": len(all_records),
    })
    print(f"pdsrg done: {len(docs)} docs -> {len(all_records)} chunks "
          f"(~{summary['n_tokens']} tokens); tables {totals_tables}; "
          f"manifest: {mpath}")
    if errors:
        return 1 if args.fail_on_error else 0
    return 0


def cmd_podcast(args: argparse.Namespace) -> int:
    transcripts_dir = Path(args.transcripts).resolve()
    audio_dir = Path(args.audio).resolve()
    out_root = Path(args.out).resolve()
    if not transcripts_dir.is_dir():
        print(f"error: transcripts dir not found: {transcripts_dir}",
              file=sys.stderr)
        return 2
    if not audio_dir.is_dir():
        print(f"error: audio dir not found: {audio_dir}", file=sys.stderr)
        return 2

    try:
        titles = resolve_titles(audio_dir)
    except ValueError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    files = sorted(transcripts_dir.glob("*.json"))
    if args.include:
        files = [f for f in files
                 if any(fnmatch.fnmatch(rel_posix(f, transcripts_dir), pat)
                        for pat in args.include)]
    if args.limit is not None:
        files = files[: args.limit]

    segments_dir = out_root / "segments"
    segments_dir.mkdir(parents=True, exist_ok=True)
    all_chunks: list[dict] = []
    errors: list[str] = []
    for i, path in enumerate(files, 1):
        slug = path.stem
        if slug not in titles:
            errors.append(f"{path.name}: no .mp3 twin in {audio_dir} "
                          f"(slug {slug!r} unresolved)")
            continue
        payload = json.loads(path.read_text(encoding="utf-8"))
        source_file = titles[slug]
        title = clean_title(Path(source_file).name)
        chunks = segment_episode(slug, title, source_file, payload)
        all_chunks.extend(chunks)
        if not args.quiet:
            print(f"  [{i}/{len(files)}] {title}: {len(chunks)} segments")

    all_chunks.sort(key=lambda c: c["id"])
    with (segments_dir / "segments.jsonl").open("w", encoding="utf-8",
                                                   newline="\n") as f:
        for rec in all_chunks:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    summary = {"pipeline_version": __version__,
               "options": {"target_words": 200, "max_words": 250,
                             "target_seconds": 90.0, "max_seconds": 120.0},
               "errors": errors,
               **summarize_podcast(all_chunks)}
    write_json(segments_dir / "summary.json", summary)
    mpath = _manifest(out_root, "podcast", args, files, transcripts_dir, {
        "ok": len(files) - len(errors), "errors": len(errors),
        "n_segments": len(all_chunks),
    })
    print(f"podcast done: {len(files) - len(errors)}/{len(files)} episodes -> "
          f"{len(all_chunks)} segments; manifest: {mpath}")
    for e in errors:
        print(f"  ERROR {e}", file=sys.stderr)
    return 1 if errors and args.fail_on_error else 0


def cmd_golden(args: argparse.Namespace) -> int:
    qa_path = Path(args.qa_docs).resolve()
    if not qa_path.is_file():
        print(f"error: Stage 4 documents.jsonl not found: {qa_path}",
              file=sys.stderr)
        return 2
    products_path = Path(args.products).resolve()
    if not products_path.is_file():
        print(f"error: products.json not found: {products_path}", file=sys.stderr)
        return 2

    records = read_jsonl(qa_path)
    alias_table = build_alias_table(
        json.loads(products_path.read_text(encoding="utf-8")))
    items, swaps, summary = run_golden(records, alias_table)

    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    sample_path = out_dir / "sample.jsonl"
    with sample_path.open("w", encoding="utf-8", newline="\n") as f:
        for item in items:
            f.write(json.dumps(item, ensure_ascii=False) + "\n")
    write_worksheet(out_dir / "worksheet.md", items)
    write_adversarial_worksheet(out_dir / "adversarial.md")
    write_json(out_dir / "summary.json", summary)

    mpath = _manifest(out_dir, "golden", args, [qa_path], qa_path.parent, {
        "n_items": len(items),
        "products_sha256": sha256_file(products_path),
        "alias_table_version": alias_table["version"],
        "golden_version": GOLDEN_VERSION,
    })
    years = ", ".join(
        f"{row['year']}:{row['sampled']}" for row in summary["year_allocation"])
    print(f"golden set: {summary['pool']['n_pool']} current QA pairs -> "
          f"{len(items)} items ({years}); splits "
          f"{summary['splits']['sampled']['dev']}/"
          f"{summary['splits']['sampled']['test']} dev/test + "
          f"{ADVERSARIAL_SIZE} adversarial; "
          f"{len(swaps)} coverage swap(s) -> {out_dir}")
    if summary["unmet"]:
        for u in summary["unmet"]:
            print(f"  WARNING unmet coverage: {u}", file=sys.stderr)
        return 1
    print(f"manifest: {mpath}")
    return 0


def cmd_index(args: argparse.Namespace) -> int:
    chunks_path = Path(args.chunks).resolve()
    products_path = Path(args.products).resolve()
    menus_path = Path(args.menus).resolve()
    for path, flag in ((chunks_path, "--chunks"), (products_path, "--products"),
                       (menus_path, "--menus")):
        if not path.is_file():
            print(f"error: {flag} not found: {path}", file=sys.stderr)
            return 2
    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    chunks = read_jsonl(chunks_path)
    products = json.loads(products_path.read_text(encoding="utf-8"))
    families = build_alias_table(products)["families"]
    menu_rows = read_menu_rows(menus_path)
    segments_path = Path(args.podcast_segments).resolve()
    if segments_path.is_file():
        podcast_segments = read_jsonl(segments_path)
    else:
        podcast_segments = []
        print(f"  note: podcast segments not found ({segments_path}) — "
              f"shaping without the podcast source")
    qa_docs_path = Path(args.qa_docs).resolve()
    if qa_docs_path.is_file():
        qa_records = read_jsonl(qa_docs_path)
    else:
        qa_records = []
        print(f"  note: QA canonical docs not found ({qa_docs_path}) — "
              f"shaping without the QA source")
    docs = build_documents(chunks, products, families, menu_rows,
                           podcast_segments, qa_records)

    docs_path = out_dir / "documents.jsonl"
    with docs_path.open("w", encoding="utf-8", newline="\n") as f:
        for rec in docs:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")

    summary: dict[str, Any] = {
        "pipeline_version": __version__,
        "options": {
            "index_name": args.index_name, "limit": args.limit,
            "no_embed": bool(args.no_embed), "no_upload": bool(args.no_upload),
            "reset": bool(args.reset),
        },
        "n_documents": len(docs),
        "by_source_type": _count_by(docs, "source_type"),
        "products_indexed": sorted({pn for d in docs for pn in d["products"]}),
        "embedding": None,
        "upload": None,
    }

    if args.no_embed:
        write_json(out_dir / "summary.json", summary)
        print(f"index shaped: {len(docs)} documents -> {docs_path} (no embedding)")
        print(f"  by source: {summary['by_source_type']}")
        return 0

    try:
        cfg = load_azure_config(require=REQUIRE_INDEX)
    except AzureConfigError as e:
        print(f"error: {e}", file=sys.stderr)
        return 2

    selected = docs if args.limit is None else docs[: args.limit]
    embedder = Embedder(
        cfg.openai_endpoint, cfg.openai_api_key, cfg.embedding_deployment,
        EMBEDDING_API_VERSION, cache_path=out_dir / "runs" / "embeddings.jsonl",
    )
    if not args.quiet:
        print(f"  embedding {len(selected)} document(s) "
              f"({cfg.embedding_deployment} @ {EMBEDDING_API_VERSION})")
    vectors = embedder.embed([embed_text(d) for d in selected])
    embedder.save_cache()
    summary["embedding"] = {
        "deployment": cfg.embedding_deployment,
        "api_version": EMBEDDING_API_VERSION,
        "n_docs": len(selected),
        "n_cache_hits": embedder.n_cache_hits,
        "n_api_calls": embedder.n_api_calls,
        "dims": len(vectors[0]) if vectors else 0,
    }

    if args.no_upload:
        write_json(out_dir / "summary.json", summary)
        print(f"index done (dry-run): shaped {len(docs)}, embedded "
              f"{len(selected)} ({embedder.n_api_calls} API call(s), "
              f"{embedder.n_cache_hits} cache hit(s)) — upload skipped")
        for d in selected[:2]:
            print(f"  sample {d['id']}: {d['title']} | {d['content'][:70]}")
        return 0

    try:
        index_status = ensure_index(cfg.search_endpoint, cfg.search_admin_key,
                                    args.index_name, reset=args.reset)
        n_ok, errors = upload_documents(cfg.search_endpoint,
                                        cfg.search_admin_key,
                                        args.index_name, selected, vectors)
    except Exception as e:  # noqa: BLE001 — report, don't traceback
        print(f"error: upload failed: {type(e).__name__}: {e}", file=sys.stderr)
        return 1

    summary["upload"] = {
        "index": args.index_name, "index_status": index_status,
        "n_uploaded": n_ok, "n_selected": len(selected), "errors": errors[:20],
    }
    # prune: the index mirrors documents.jsonl (the corpus-mirror convention).
    # Skipped under --limit — a partial upload must not delete the rest.
    if not args.no_prune and args.limit is None:
        present = list_index_ids(cfg.search_endpoint, cfg.search_admin_key,
                                 args.index_name)
        doomed = sorted(set(present) - {d["id"] for d in selected})
        if doomed:
            n_del, del_errors = delete_documents(
                cfg.search_endpoint, cfg.search_admin_key, args.index_name,
                doomed)
            summary["prune"] = {"n_present": len(present),
                                "n_deleted": n_del,
                                "errors": del_errors[:20]}
            errors.extend(del_errors)
        else:
            summary["prune"] = {"n_present": len(present), "n_deleted": 0,
                                "errors": []}
    write_json(out_dir / "summary.json", summary)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    write_json(out_dir / "runs" / f"index-{stamp}.json", {
        "pipeline_version": __version__, "stage": "index", "utc": _utcnow(),
        "args": {k: v for k, v in vars(args).items() if k != "func"},
        "inputs": {"chunks": sha256_file(chunks_path),
                   "products": sha256_file(products_path),
                   "menus": sha256_file(menus_path),
                   "podcast_segments": (sha256_file(segments_path)
                                          if segments_path.is_file()
                                          else None),
                   "qa_docs": (sha256_file(qa_docs_path)
                                if qa_docs_path.is_file()
                                else None)},
        "n_documents": len(docs), "n_embedded": len(selected),
        "n_uploaded": n_ok, "errors": errors,
    })
    print(f"index done: {len(docs)} shaped, {n_ok}/{len(selected)} uploaded to "
          f"{args.index_name} ({index_status}); errors: {len(errors)}")
    if summary.get("prune"):
        print(f"  prune: {summary['prune']['n_deleted']} deleted, "
              f"{summary['prune']['n_present']} were present")
    for e in errors[:10]:
        print(f"  ERROR {e['key']}: {e['error']}")
    return 1 if errors and args.fail_on_error else 0


def _count_by(docs: list[dict], key: str) -> dict[str, int]:
    out: dict[str, int] = {}
    for d in docs:
        k = str(d.get(key) or "none")
        out[k] = out.get(k, 0) + 1
    return dict(sorted(out.items()))


def cmd_run(args: argparse.Namespace) -> int:
    rc = cmd_stage0(args)
    if rc != 0 and args.fail_on_error:
        return rc
    return cmd_stage1(args)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="qa-pipeline",
        description="QA corpus pipeline (plan §4): Stage 0 (PII scrub), Stage 1 "
        "(parse & classify), Stage 2 (LLM structuring)",
    )
    p.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    sub = p.add_subparsers(dest="command", required=True)

    def common(sp: argparse.ArgumentParser) -> None:
        sp.add_argument("--input", default="data/QAs",
                        help="root of the QA corpus (default: data/QAs)")
        sp.add_argument("--out", default="processed/qa",
                        help="output root (default: processed/qa)")
        sp.add_argument("--include", action="append", metavar="GLOB",
                        help="only process files whose input-relative path matches "
                             "this glob (repeatable)")
        sp.add_argument("--limit", type=int, default=None,
                        help="process at most N files (for pilots)")
        sp.add_argument("--quiet", action="store_true")
        sp.add_argument("--fail-on-error", action="store_true",
                        help="non-zero exit code if any file fails to parse")
        sp.add_argument("--no-prune", action="store_true",
                        help="keep outputs whose input file no longer exists "
                             "(default: delete them so the tree matches the "
                             "corpus)")

    s0 = sub.add_parser("stage0", help="PII scrub: .docx -> scrubbed text + reports")
    common(s0)
    s0.set_defaults(func=cmd_stage0)

    s1 = sub.add_parser("stage1", help="parse & classify scrubbed text -> documents.jsonl")
    common(s1)
    s1.set_defaults(func=cmd_stage1)

    s2 = sub.add_parser(
        "stage2",
        help="LLM-assisted structuring: stage1 docs -> canonical Q&A "
             "(plan §4 Stage 2)")
    s2.add_argument("--qa-docs", default="processed/qa/stage1/documents.jsonl",
                    help="Stage 1 documents.jsonl (scrubbed input)")
    s2.add_argument("--products", default="data/Product Data/products.json",
                    help="path to products.json (alias table source)")
    s2.add_argument("--out", default="processed/qa/stage2",
                    help="output dir (default: processed/qa/stage2)")
    s2.add_argument("--include", action="append", metavar="GLOB",
                    help="only process docs whose source_file matches this "
                         "glob (repeatable)")
    s2.add_argument("--limit", type=int, default=None,
                    help="process at most N docs, source_file order (for pilots)")
    s2.add_argument("--min-confidence", type=float,
                    default=MIN_CONFIDENCE_DEFAULT,
                    help="review threshold (default: "
                         f"{MIN_CONFIDENCE_DEFAULT})")
    s2.add_argument("--api-version", default=CHAT_API_VERSION,
                    help="Azure OpenAI api-version (default: "
                         f"{CHAT_API_VERSION})")
    s2.add_argument("--no-llm", action="store_true",
                    help="rule-based fallback for every document (no Azure calls)")
    s2.add_argument("--no-cache", action="store_true",
                    help="skip the runs/stage2_cache.jsonl LLM cache")
    s2.add_argument("--quiet", action="store_true")
    s2.add_argument("--fail-on-error", action="store_true",
                    help="non-zero exit if any document's LLM call fails")
    s2.set_defaults(func=cmd_stage2)

    s4 = sub.add_parser(
        "stage4",
        help="deduplication + currency filter: stage2 canonicals -> "
             "is_current stamps (plan §4 Stage 4)")
    s4.add_argument("--qa-docs", default="processed/qa/stage2/documents.jsonl",
                    help="Stage 2 documents.jsonl (canonical input)")
    s4.add_argument("--products", default="data/Product Data/products.json",
                    help="path to products.json (alias table source)")
    s4.add_argument("--out", default="processed/qa/stage4",
                    help="output dir (default: processed/qa/stage4)")
    s4.add_argument("--include", action="append", metavar="GLOB",
                    help="only process docs whose source_file matches this "
                         "glob (repeatable)")
    s4.add_argument("--limit", type=int, default=None,
                    help="process at most N docs, source_file order (for pilots)")
    s4.add_argument("--min-judge-confidence", type=float,
                    default=MIN_JUDGE_CONFIDENCE_DEFAULT,
                    help="currency-judgment review threshold (default: "
                         f"{MIN_JUDGE_CONFIDENCE_DEFAULT})")
    s4.add_argument("--api-version", default=CHAT_API_VERSION,
                    help="Azure OpenAI api-version (default: "
                         f"{CHAT_API_VERSION})")
    s4.add_argument("--no-llm", action="store_true",
                    help="conservative currency fallback: every "
                         "gone-or-replaced cue superscedes + queues (no "
                         "chat calls; clustering still embeds)")
    s4.add_argument("--no-cache", action="store_true",
                    help="skip the runs/stage4_cache.jsonl judgment cache")
    s4.add_argument("--quiet", action="store_true")
    s4.add_argument("--fail-on-error", action="store_true",
                    help="non-zero exit if any currency judgment fails")
    s4.set_defaults(func=cmd_stage4)

    al = sub.add_parser("aliases", help="build alias table from products.json")
    al.add_argument("--products", default="data/Product Data/products.json",
                    help="path to products.json")
    al.add_argument("--out", default="processed/aliases",
                    help="output dir (default: processed/aliases)")
    al.add_argument("--qa-docs", default="processed/qa/stage1/documents.jsonl",
                    help="Stage 1 documents.jsonl for the candidate harvest "
                         "(empty string / missing file skips the worksheet)")
    al.set_defaults(func=cmd_aliases)

    pd = sub.add_parser("pdsrg", help="chunk the PDSRG PDF corpus (plan §6)")
    pd.add_argument("--input", default="data/Practitioner Dietary Supplement Reference Guide",
                    help="root of the PDSRG PDF corpus")
    pd.add_argument("--out", default="processed/pdsrg",
                    help="output root (default: processed/pdsrg)")
    pd.add_argument("--products", default="data/Product Data/products.json",
                    help="path to products.json (alias table source)")
    pd.add_argument("--include", action="append", metavar="GLOB",
                    help="only process files whose input-relative path matches "
                         "this glob (repeatable)")
    pd.add_argument("--limit", type=int, default=None,
                    help="process at most N files (for pilots)")
    pd.add_argument("--citation-base", default="pdsrg/",
                    help="prefix for the citation_url of each chunk "
                         "(default: pdsrg/) — the deployment decides where "
                         "the PDFs are served from")
    pd.add_argument("--keep-references", action="store_true",
                    help="index References/bibliography sections "
                         "(excluded by default)")
    pd.add_argument("--quiet", action="store_true")
    pd.add_argument("--fail-on-error", action="store_true")
    pd.set_defaults(func=cmd_pdsrg)

    ix = sub.add_parser(
        "index",
        help="shape + embed + upload the §9 kb-main index "
             "(pdsrg chunks + products.json + menu descriptions)")
    ix.add_argument("--chunks", default="processed/pdsrg/chunks/chunks.jsonl",
                    help="§6 chunks.jsonl")
    ix.add_argument("--products", default="data/Product Data/products.json",
                    help="products.json (§5 section-split source)")
    ix.add_argument("--menus",
                    default="data/Reference Menus/All Reference Menus Export.csv",
                    help="menu CSV (§8 description docs)")
    ix.add_argument("--podcast-segments",
                    default="processed/podcasts/segments/segments.jsonl",
                    help="§7 segments.jsonl (missing file shapes without "
                         "the podcast source)")
    ix.add_argument("--qa-docs",
                    default="processed/qa/stage4/documents.jsonl",
                    help="Stage 4 documents.jsonl (missing file shapes "
                         "without the QA source)")
    ix.add_argument("--out", default="processed/index",
                    help="output dir (default: processed/index)")
    ix.add_argument("--index-name", default=INDEX_NAME,
                    help=f"AI Search index name (default: {INDEX_NAME})")
    ix.add_argument("--limit", type=int, default=None,
                    help="embed/upload only the first N documents, id order "
                         "(documents.jsonl still covers everything)")
    ix.add_argument("--no-embed", action="store_true",
                    help="shape documents.jsonl only (no Azure calls)")
    ix.add_argument("--no-upload", action="store_true",
                    help="embed (cached) but skip AI Search upload (dry-run)")
    ix.add_argument("--reset", action="store_true",
                    help="drop + recreate the index before upload")
    ix.add_argument("--no-prune", action="store_true",
                    help="keep index documents that are no longer in "
                         "documents.jsonl (default: delete them so the "
                         "index mirrors the build; skipped under --limit)")
    ix.add_argument("--quiet", action="store_true")
    ix.add_argument("--fail-on-error", action="store_true")
    ix.set_defaults(func=cmd_index)

    pc = sub.add_parser(
        "podcast",
        help="segment podcast transcripts into topic chunks (plan §7 step 3)")
    pc.add_argument("--transcripts",
                    default="processed/podcasts/transcripts",
                    help="dir of fast-transcription .json files")
    pc.add_argument("--audio", default="data/Suppbeast Podcast",
                    help="dir of the .mp3 files (episode-title source)")
    pc.add_argument("--out", default="processed/podcasts",
                    help="output root (default: processed/podcasts)")
    pc.add_argument("--include", action="append", metavar="GLOB",
                    help="only process transcripts whose dir-relative path "
                         "matches this glob (repeatable)")
    pc.add_argument("--limit", type=int, default=None,
                    help="process at most N transcripts (for pilots)")
    pc.add_argument("--quiet", action="store_true")
    pc.add_argument("--fail-on-error", action="store_true")
    pc.set_defaults(func=cmd_podcast)

    g = sub.add_parser(
        "golden",
        help="stratified golden-set sample + labeling worksheets (plan §12)")
    g.add_argument("--qa-docs", default="processed/qa/stage4/documents.jsonl",
                   help="Stage 4 documents.jsonl (the sampling pool)")
    g.add_argument("--products", default="data/Product Data/products.json",
                   help="path to products.json (alias table source)")
    g.add_argument("--out", default="processed/golden",
                   help="output dir (default: processed/golden)")
    g.set_defaults(func=cmd_golden)

    r = sub.add_parser("run", help="stage0 followed by stage1")
    common(r)
    r.set_defaults(func=cmd_run)

    return p


def main(argv: list[str] | None = None) -> int:
    configure_stdio()
    args = build_parser().parse_args(argv)
    return args.func(args)
