"""Stage 4 cluster calibration scan (plan §4 Stage 4 step 1).

One-off analysis (the pdsrg_density_scan.py precedent), NOT part of the CLI.
Answers two questions before the clustering threshold becomes a module
constant:

1. How many near-duplicate clusters form at each cosine threshold, within
   §4 buckets (pairs compared only when they share a part_no or a topic)?
2. Which pairs sit on the border — the lowest-similarity pairs that merge
   and the highest-similarity pairs that stay apart — so the locked
   threshold is a judged number, not a guess.

Embeds question_canonical via the production Embedder; vectors cache in
processed/qa/stage4/runs/embeddings.jsonl so the full stage4 run reuses
every API call this scan spends.
"""

from __future__ import annotations

import math
import sys
import time
from pathlib import Path

from qa_pipeline.azure_config import (
    REQUIRE_OPENAI_EMBEDDING, AzureConfigError, load_azure_config,
)
from qa_pipeline.embeddings import EMBEDDING_API_VERSION, Embedder
from qa_pipeline.io_utils import configure_stdio, read_jsonl

DEFAULT_DOCS = "../processed/qa/stage2/documents.jsonl"
DEFAULT_CACHE = "../processed/qa/stage4/runs/embeddings.jsonl"
THRESHOLDS = [round(0.80 + 0.02 * i, 2) for i in range(9)]  # 0.80..0.96
CANDIDATES = (0.88, 0.90, 0.92, 0.94)
N_SAMPLES = 12


def normalize(vec: list[float]) -> list[float]:
    norm = math.sqrt(math.sumprod(vec, vec))
    return [x / norm for x in vec]


def buckets_share(a: dict, b: dict) -> bool:
    """§4 bucket rule: same product or same topic (case-insensitive)."""
    if set(a["products"]) & set(b["products"]):
        return True
    return bool({t.casefold() for t in a["topics"]}
                & {t.casefold() for t in b["topics"]})


class UnionFind:
    def __init__(self, n: int) -> None:
        self.parent = list(range(n))

    def find(self, x: int) -> int:
        while self.parent[x] != x:
            self.parent[x] = self.parent[self.parent[x]]
            x = self.parent[x]
        return x

    def union(self, a: int, b: int) -> None:
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.parent[max(ra, rb)] = min(ra, rb)


def clusters_at(edges: list[tuple[int, int, float]], threshold: float,
                n: int) -> tuple[dict[int, list[int]], list[int]]:
    """(root -> member indices, root-of-each-record) at *threshold*."""
    uf = UnionFind(n)
    for i, j, sim in edges:
        if sim >= threshold:
            uf.union(i, j)
    roots = [uf.find(i) for i in range(n)]
    out: dict[int, list[int]] = {}
    for i, root in enumerate(roots):
        out.setdefault(root, []).append(i)
    return out, roots


def main() -> int:
    configure_stdio()
    docs_path = Path(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DOCS).resolve()
    cache_path = Path(sys.argv[2] if len(sys.argv) > 2 else DEFAULT_CACHE).resolve()
    records = read_jsonl(docs_path)
    q = [r for r in records if (r.get("question_canonical") or "").strip()]
    print(f"{len(records)} records, {len(q)} with question_canonical")

    try:
        cfg = load_azure_config(require=REQUIRE_OPENAI_EMBEDDING)
    except AzureConfigError as e:
        print(f"error: {e}", file=sys.stderr)
        return 2
    texts = sorted({r["question_canonical"] for r in q})
    embedder = Embedder(cfg.openai_endpoint, cfg.openai_api_key,
                        cfg.embedding_deployment, EMBEDDING_API_VERSION,
                        cache_path=cache_path)
    t0 = time.monotonic()
    vecs = dict(zip(texts, embedder.embed(texts)))
    embedder.save_cache()
    print(f"embedded {len(texts)} unique questions "
          f"({embedder.n_api_calls} API call(s), {embedder.n_cache_hits} cache "
          f"hit(s), {time.monotonic() - t0:.1f}s)")

    q.sort(key=lambda r: r["source_file"])  # stable pair/cluster ordering
    vectors = [normalize(vecs[r["question_canonical"]]) for r in q]
    t0 = time.monotonic()
    pairs_checked = 0
    edges: list[tuple[int, int, float]] = []
    for i in range(len(q)):
        for j in range(i + 1, len(q)):
            if not buckets_share(q[i], q[j]):
                continue
            pairs_checked += 1
            sim = math.sumprod(vectors[i], vectors[j])
            if sim >= THRESHOLDS[0]:
                edges.append((i, j, sim))
    edges.sort(key=lambda e: (-e[2], e[0], e[1]))
    print(f"{pairs_checked} bucketed pairs checked, {len(edges)} above "
          f"{THRESHOLDS[0]} ({time.monotonic() - t0:.1f}s pure-python sumprod)")

    from collections import Counter
    pn_counts = Counter(pn for r in q for pn in r["products"])
    top_faq = pn_counts.most_common(3)
    print(f"\ntop FAQ part_nos: {top_faq}")

    print(f"\n{'thr':>4} {'clusters':>8} {'records':>8} {'max':>4} {'≥5':>3}"
          f"  | family-fragmentation (top 3 part_nos)")
    for thr in THRESHOLDS:
        cl, roots = clusters_at(edges, thr, len(q))
        multi = {k: v for k, v in cl.items() if len(v) >= 2}
        sizes = sorted((len(v) for v in multi.values()), reverse=True)
        frag = []
        for pn, _n in top_faq:
            idxs = [i for i, r in enumerate(q) if pn in r["products"]]
            frag.append(len({roots[i] for i in idxs}))
        print(f"{thr:>4.2f} {len(multi):>8} {sum(sizes):>8} "
              f"{sizes[0] if sizes else 0:>4} {sum(1 for s in sizes if s >= 5):>3}"
              f"  | {frag}")

    for thr in CANDIDATES:
        cl, roots = clusters_at(edges, thr, len(q))
        # borderline: the weakest merged pairs and the strongest still-split ones
        merged = [e for e in edges if e[2] >= thr and roots[e[0]] == roots[e[1]]]
        merged.sort(key=lambda e: e[2])
        split = [e for e in edges if e[2] < thr and roots[e[0]] != roots[e[1]]]
        print(f"\n--- threshold {thr:.2f}: weakest merges ---")
        for i, j, sim in merged[:N_SAMPLES]:
            print(f"  {sim:.4f}  {_snip(q[i])}  ||  {_snip(q[j])}")
        print(f"--- threshold {thr:.2f}: strongest non-merges ---")
        for i, j, sim in split[:N_SAMPLES]:
            print(f"  {sim:.4f}  {_snip(q[i])}  ||  {_snip(q[j])}")
    return 0


def _snip(r: dict) -> str:
    s = r["question_canonical"].replace("\n", " ")
    return (s[:72] + "…") if len(s) > 72 else s


if __name__ == "__main__":
    raise SystemExit(main())
